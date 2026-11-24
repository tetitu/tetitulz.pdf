// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Pluggable ICC colour-transform provider.  Default implementation uses
    /// SkiaSharp's built-in `SKColorSpace.CreateIcc` when a profile stream is
    /// supplied; callers who need prepress-grade colour (a full CMM's gamut transform
    /// with rendering intents, black-point compensation, etc.) can register a
    /// custom provider via <see cref="IccTransform.Register"/>.
    ///
    /// The default provider is close to reference renderers for
    /// screen rendering; a native colour-management plugin is needed for
    /// byte-exact CMYK output on calibrated prepress systems.
    /// </summary>
    public interface IIccTransformProvider
    {
        /// <summary>
        /// Transforms a single raw-RGB / CMYK / Gray pixel from a source ICC
        /// profile's colour space into sRGB (byte-per-channel, alpha already
        /// set by the caller).  Returns the untransformed pixel when the
        /// provider cannot handle the requested transform.
        /// </summary>
        SKColor TransformToSRgb(byte[] iccProfileBytes, byte[] sourceComponents);

        /// <summary>
        /// Bulk-transform an RGBA8888 bitmap whose components are in the
        /// source ICC profile's space into sRGB, mutating the bitmap in
        /// place.  Returns true when the transform was performed.
        /// </summary>
        bool TransformToSRgb(byte[] iccProfileBytes, SKBitmap bitmap);
    }

    /// <summary>
    /// The colour-accurate companion to <see cref="IIccTransformProvider"/>, for
    /// <b>vector</b> content: a fill or stroke colour arrives as floating-point components in the
    /// profile's data space, not as bytes, and it can fail to convert.
    ///
    /// <para><b>Why this is a second interface rather than a member of the first.</b>
    /// <see cref="IIccTransformProvider"/> is public and already implemented outside this
    /// assembly, and <c>Tetitulz.Pdf.Base</c> multi-targets netstandard2.0, which has no default
    /// interface members — so adding a method there would break every existing implementation.
    /// Implementing this one is therefore <b>opt-in</b>: a host provider that predates it leaves
    /// vector colour exactly as it was, on the device space the <c>/N</c> stands for.</para>
    /// </summary>
    public interface IIccColorTransformProvider
    {
        /// <summary>
        /// Converts one colour, whose components are in <paramref name="iccProfileBytes"/>'
        /// data colour space and normalised to [0,1], into sRGB.
        /// </summary>
        /// <returns>
        /// <c>false</c> when this provider cannot handle the profile — the caller then keeps the
        /// device-space fallback rather than painting a guess.
        /// </returns>
        bool TryTransformToSRgb(byte[] iccProfileBytes, float[] components, out SKColor color);
    }

    /// <summary>A PDF rendering intent (ISO 32000-1 §8.6.5.8), as a host's colour management module receives it.</summary>
    public enum PdfRenderingIntent
    {
        /// <summary><c>/Perceptual</c> (ICC <c>A2B0</c>).</summary>
        Perceptual = 0,
        /// <summary><c>/RelativeColorimetric</c> (ICC <c>A2B1</c>), PDF's default.</summary>
        RelativeColorimetric = 1,
        /// <summary><c>/Saturation</c> (ICC <c>A2B2</c>).</summary>
        Saturation = 2,
        /// <summary><c>/AbsoluteColorimetric</c>: the relative table scaled by the media white.</summary>
        AbsoluteColorimetric = 3,
    }

    /// <summary>
    /// The intent-aware companion to <see cref="IIccTransformProvider"/> and
    /// <see cref="IIccColorTransformProvider"/>: a host binding a real colour management module
    /// receives the rendering intent the file asked for — the graphics state's <c>/RI</c>, an
    /// image's own <c>/Intent</c> — and whether black-point compensation applies (PDF 2.0's
    /// <c>/UseBlackPtComp</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>A third interface, opt-in, as the second was</b>: the two before it are public and
    /// implemented outside this assembly, and <c>Base</c> multi-targets netstandard2.0, which has no
    /// default interface members, so neither can grow an intent parameter without breaking every
    /// implementation. A provider that implements this one is called through it for vector colour and
    /// for images; one that does not is called as before, and told once (per provider, per thread) that the intent was
    /// not forwarded.</para>
    /// </remarks>
    public interface IIccIntentColorTransformProvider
    {
        /// <summary>
        /// Converts one colour, its components in <paramref name="iccProfileBytes"/>' data colour space
        /// and normalised to [0,1], into sRGB under <paramref name="intent"/>.
        /// </summary>
        /// <returns><c>false</c> when the provider cannot handle the profile; the device fallback applies.</returns>
        bool TryTransformToSRgb(byte[] iccProfileBytes, float[] components, PdfRenderingIntent intent,
                                bool blackPointCompensation, out SKColor color);

        /// <summary>
        /// Transforms an RGBA8888 bitmap whose components are in the profile's space into sRGB under
        /// <paramref name="intent"/>, in place; <c>true</c> when it did.
        /// </summary>
        bool TransformToSRgb(byte[] iccProfileBytes, SKBitmap bitmap, PdfRenderingIntent intent,
                             bool blackPointCompensation);
    }

    /// <summary>
    /// The default provider: a pure-managed matrix/TRC + grey-TRC evaluator
    /// (<see cref="IccColorTransform"/>) in front of <see cref="SKColorSpace.CreateIcc(byte[])"/>.
    ///
    /// <para><b>Which engine handles what, and why the order is what it is.</b> Measured against
    /// SkiaSharp 4.151.0: <see cref="SKColorSpace.CreateIcc(byte[])"/> <b>throws</b> on a CMYK profile
    /// (caught below, so it is a silent no-op), and <b>declines any profile whose tone curve is a
    /// sampled <c>curveType</c> table</b> — an <c>SKColorSpace</c> can only carry a parametric
    /// transfer function. It does accept a simple grey profile with a gamma curve, so the
    /// often-repeated "Skia cannot do grey" is only half true; what it cannot do is the table
    /// form, and that is a large share of real grey profiles.</para>
    ///
    /// <para>So grey goes <b>managed first</b>: a 256-entry lookup table, exact for every curve
    /// form, and the same answer whichever Skia build is present. Ordinary three-component
    /// matrix/TRC RGB goes to Skia first, because that is the case Skia already handles well in
    /// native code and per-pixel managed evaluation would be far slower for no gain; the managed
    /// matrix path runs only when Skia declines. A profile <b>neither</b> can evaluate — CMYK,
    /// lut-based, named colour, device link — is reported through
    /// <see cref="RenderDiagnostics"/> and through
    /// <see cref="IccTransform.LastUnsupportedProfile"/>, and the bitmap is left untouched, which
    /// is the alternate-space behaviour the spec prescribes.</para>
    ///
    /// <para>A host that needs prepress-grade colour — CMYK separations, rendering intents,
    /// black-point compensation — still needs a real CMM behind <see cref="IccTransform.Register"/>.
    /// </para>
    /// </summary>
    public sealed class SkiaIccTransformProvider : IIccTransformProvider, IIccColorTransformProvider
    {
        /// <inheritdoc/>
        public bool TryTransformToSRgb(byte[] iccProfileBytes, float[] components, out SKColor color)
            => IccColorTransform.TryTransform(iccProfileBytes, components, out color);

        public SKColor TransformToSRgb(byte[] iccProfileBytes, byte[] sourceComponents)
        {
            if (sourceComponents == null || sourceComponents.Length == 0)
                return SKColors.Black;

            // The managed evaluator when the profile is one it covers; otherwise the
            // component-count fallback below.
            var normalised = new float[sourceComponents.Length];
            for (int i = 0; i < normalised.Length; i++) normalised[i] = sourceComponents[i] / 255f;
            if (IccColorTransform.TryTransform(iccProfileBytes, normalised, out SKColor managed))
                return managed;

            return sourceComponents.Length switch
            {
                1 => new SKColor(sourceComponents[0], sourceComponents[0], sourceComponents[0]),
                3 => new SKColor(sourceComponents[0], sourceComponents[1], sourceComponents[2]),
                4 => CmykToSRgb(sourceComponents),
                _ => new SKColor(sourceComponents[0], sourceComponents[0], sourceComponents[0]),
            };
        }

        public bool TransformToSRgb(byte[] iccProfileBytes, SKBitmap bitmap)
        {
            if (iccProfileBytes == null || iccProfileBytes.Length == 0 || bitmap == null)
                return false;

            // Grey first — exact for every curve form, and independent of which Skia build is
            // present. See the class remarks for why the two models are ordered differently.
            var profile = IccColorTransform.GetProfile(iccProfileBytes);
            if (profile != null && profile.Model == IccModel.GrayTrc
                && IccColorTransform.TryTransform(iccProfileBytes, bitmap))
                return true;

            if (TransformWithSkia(iccProfileBytes, bitmap)) return true;

            // Skia declined a profile the managed matrix/TRC evaluator can still do.
            return IccColorTransform.TryTransform(iccProfileBytes, bitmap);
        }

        private static bool TransformWithSkia(byte[] iccProfileBytes, SKBitmap bitmap)
        {
            SKColorSpace sourceSpace = null;
            SKColorSpace sRgbSpace = null;
            SKImage srcImg = null;
            SKSurface dstSurface = null;
            SKImage dstImg = null;
            try
            {
                sourceSpace = SKColorSpace.CreateIcc(iccProfileBytes);
                if (sourceSpace == null) return false;

                // The memory is described as what it is: Skia reinterpreting a BGRA bitmap as RGBA
                // would run the profile over red and blue swapped.
                SKColorType colorType = bitmap.ColorType;
                if (colorType != SKColorType.Rgba8888 && colorType != SKColorType.Bgra8888) return false;
                SKAlphaType alphaType = bitmap.AlphaType;

                var srcPtr = bitmap.GetPixels();
                if (srcPtr == System.IntPtr.Zero) return false;

                // Map bitmap into an SKImage with the source colour space.
                var srcInfo = new SKImageInfo(bitmap.Width, bitmap.Height,
                    colorType, alphaType, sourceSpace);
                srcImg = SKImage.FromPixelCopy(srcInfo, srcPtr, bitmap.RowBytes);
                if (srcImg == null) return false;

                sRgbSpace = SKColorSpace.CreateSrgb();
                var dstInfo = new SKImageInfo(bitmap.Width, bitmap.Height,
                    colorType, SKAlphaType.Premul, sRgbSpace);
                dstSurface = SKSurface.Create(dstInfo);
                if (dstSurface == null) return false;

                dstSurface.Canvas.DrawImage(srcImg, 0, 0, SKSamplingOptions.Default);
                dstImg = dstSurface.Snapshot();
                if (dstImg == null) return false;

                // An opaque bitmap's bytes are its premultiplied ones (alpha 255); Skia refuses a read
                // into Opaque from a surface that is not.
                var readInfo = new SKImageInfo(bitmap.Width, bitmap.Height, colorType,
                    alphaType == SKAlphaType.Opaque ? SKAlphaType.Premul : alphaType);
                return dstImg.ReadPixels(readInfo, srcPtr, bitmap.RowBytes, 0, 0);
            }
            catch
            {
                return false;
            }
            finally
            {
                dstImg?.Dispose();
                dstSurface?.Dispose();
                srcImg?.Dispose();
                sRgbSpace?.Dispose();
                sourceSpace?.Dispose();
            }
        }

        /// <summary>
        /// 4-component fallback when no ICC transform is available. Delegates to the shared
        /// converter, never an inline formula with <see cref="Math.Round(double)"/>, which is banker's
        /// rounding and would disagree with every other CMYK site at exact half-levels.
        /// </summary>
        private static SKColor CmykToSRgb(byte[] cmyk)
            => PdfColor.CmykToRgb(cmyk[0] / 255f, cmyk[1] / 255f, cmyk[2] / 255f, cmyk[3] / 255f);
    }

    /// <summary>
    /// Global registration surface for ICC transform providers.  The default
    /// is <see cref="SkiaIccTransformProvider"/>; consumers that need a native
    /// colour engine can register their own implementation.
    /// </summary>
    public static class IccTransform
    {
        private static IIccTransformProvider _provider = new SkiaIccTransformProvider();
        private static readonly object _lock = new object();

        /// <summary>Replaces the active ICC provider.</summary>
        public static void Register(IIccTransformProvider provider)
        {
            if (provider == null) throw new ArgumentNullException(nameof(provider));
            lock (_lock) { _provider = provider; }
        }

        /// <summary>Reverts to the SkiaSharp-based default provider.</summary>
        public static void ResetToDefault()
        {
            lock (_lock) { _provider = new SkiaIccTransformProvider(); }
        }

        /// <summary>The currently-active provider.</summary>
        public static IIccTransformProvider Current
        {
            get { lock (_lock) { return _provider; } }
        }

        [ThreadStatic] private static string _lastUnsupportedProfile;

        /// <summary>
        /// Why the ICC profile most recently consulted <b>on this thread</b> was not colour-managed
        /// — <c>null</c> when it was, and <c>null</c> on a thread that has not consulted one.
        ///
        /// <para>This is what lets a caller tell <i>"this document has no ICC profile"</i> from
        /// <i>"it has one and we fell back to the device space"</i>, which are identical at the
        /// pixel level. The same reason is reported through
        /// <see cref="RenderDiagnostics"/> (<see cref="RenderDiagnosticKind.IccTransformFailed"/>),
        /// once per distinct profile; this slot is refreshed on <b>every</b> consultation, so
        /// reading it straight after an operation describes that operation.</para>
        ///
        /// <para><c>[ThreadStatic]</c> per the diagnostics rule in CLAUDE.md: read it on the thread
        /// that ran the render.</para>
        /// </summary>
        public static string LastUnsupportedProfile => _lastUnsupportedProfile;

        /// <summary>
        /// Records the outcome of consulting a profile. Called on every consultation — including
        /// the successful ones, which is what clears a previous document's reason out of the slot.
        /// </summary>
        internal static void NoteProfileConsulted(string unsupportedReason)
            => _lastUnsupportedProfile = unsupportedReason;
    }
}
