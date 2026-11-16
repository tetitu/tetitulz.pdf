// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.CompilerServices;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Applies an ICC profile to colours and to decoded bitmaps, and owns the parse cache.
    ///
    /// <para><b>What this covers.</b> ICC colour management for vector content as well as images,
    /// evaluated here rather than through <c>SKColorSpace.CreateIcc</c>, which throws on a CMYK
    /// profile and declines any profile carrying a sampled tone curve — a silent no-op for a large
    /// share of the profiles that differ most from the device fallback.
    /// <c>PdfColor.ResolveSpace</c> collapsing <c>[/ICCBased s]</c> to the device space its
    /// <c>/N</c> stands for is the <i>fallback</i>, not the behaviour.</para>
    ///
    /// <para><b>What it deliberately does not do.</b> This is not a CMM. Only the matrix/TRC and
    /// grey-TRC models are evaluated (see <see cref="IccModel"/>); everything else — luts, CMYK,
    /// named colour, device link, a non-XYZ PCS — is reported through
    /// <see cref="RenderDiagnostics"/> and through
    /// <see cref="IccTransform.LastUnsupportedProfile"/>, and the caller keeps the device
    /// behaviour it had before. Rendering intents, black-point compensation and gamut mapping are
    /// out of scope entirely.</para>
    /// </summary>
    internal static class IccColorTransform
    {
        /// <summary>
        /// Parsed profiles, keyed by the decoded profile bytes. Weak so a closed document's
        /// profiles are collectable; keyed by the array <i>instance</i>, which is why
        /// <see cref="DecodedProfileBytes"/> exists — a fresh <c>GetDecodedData()</c> array on
        /// every <c>sc</c> operator would miss this cache every time.
        /// </summary>
        private static readonly ConditionalWeakTable<byte[], IccProfile> ProfileCache =
            new ConditionalWeakTable<byte[], IccProfile>();

        /// <summary>Decoded profile bytes, keyed by the ICC stream object they came from.</summary>
        private static readonly ConditionalWeakTable<PdfStream, byte[]> StreamBytesCache =
            new ConditionalWeakTable<PdfStream, byte[]>();

        // ==============================================================
        //  Profile acquisition
        // ==============================================================

        /// <summary>
        /// The parsed profile for these bytes, or <c>null</c> when there are none. Never throws.
        /// A profile that cannot be colour-managed comes back with
        /// <see cref="IccModel.Unsupported"/> and is reported once, on the parse that discovered it.
        /// </summary>
        internal static IccProfile GetProfile(byte[] profileBytes)
        {
            if (profileBytes == null || profileBytes.Length == 0)
            {
                IccTransform.NoteProfileConsulted(null);
                return null;
            }

            if (!ProfileCache.TryGetValue(profileBytes, out IccProfile profile))
            {
                profile = ParseSafely(profileBytes);
                // A concurrent parse of the same array is harmless — both results are equal, and
                // Add throws only because the other thread won.
                try { ProfileCache.Add(profileBytes, profile); }
                catch (ArgumentException) { }

                if (profile.Model == IccModel.Unsupported)
                    RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed,
                        "ICC profile not colour-managed, device fallback used: " + profile.UnsupportedReason);
            }

            IccTransform.NoteProfileConsulted(
                profile.Model == IccModel.Unsupported ? profile.UnsupportedReason : null);
            return profile;
        }

        private static IccProfile ParseSafely(byte[] profileBytes)
        {
            try
            {
                return IccProfile.Parse(profileBytes);
            }
            catch (Exception ex)
            {
                // Parse is written not to throw; if it ever does, that is a defect in this file and
                // must not take a render down with it.
                RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed,
                    "ICC profile parse threw", ex);
                return IccProfile.Unsupported("profile parse threw: " + ex.Message);
            }
        }

        // ==============================================================
        //  Colours (the vector path)
        // ==============================================================

        /// <summary>
        /// Converts one colour whose components are in an ICC profile's data space to sRGB.
        /// Returns <c>false</c> — leave the caller's device fallback alone — when the profile is
        /// one this evaluator does not cover, or when the component count does not match it.
        /// </summary>
        internal static bool TryTransform(byte[] profileBytes, float[] components, out SKColor color)
            => TryTransform(profileBytes, components, IccRenderingIntents.Default, out color);

        /// <summary>
        /// As <see cref="TryTransform(byte[], float[], out SKColor)"/>, reading the <c>A2B</c> table
        /// <paramref name="intent"/> selects (§8.6.5.8).
        /// </summary>
        internal static bool TryTransform(byte[] profileBytes, float[] components,
                                          IccRenderingIntent intent, out SKColor color)
        {
            color = default;
            IccProfile profile = GetProfile(profileBytes);
            if (profile == null || components == null || profile.Model == IccModel.Unsupported)
                return false;

            if (components.Length != profile.ComponentCount)
            {
                // The PDF's /N and the profile's data space disagree (§8.6.5.5 requires them to
                // match). Reported rather than coerced: guessing which one is right is how a
                // 4-component space gets painted as a 3-component one.
                RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed,
                    $"ICC profile is {profile.ComponentCount}-component ('{profile.DataColorSpace}') " +
                    $"but {components.Length} operands were supplied; device fallback used");
                IccTransform.NoteProfileConsulted(
                    $"component-count mismatch: profile is {profile.ComponentCount}, " +
                    $"operands were {components.Length}");
                return false;
            }

            return profile.TryEvaluate(components, intent, out color);
        }

        /// <summary>
        /// Converts <c>sc</c>/<c>scn</c> operands expressed in an <c>[/ICCBased stream]</c> space,
        /// routed through <see cref="IccTransform.Current"/> so a host that installed its own
        /// provider keeps control.
        ///
        /// <para>A provider that does not implement <see cref="IIccColorTransformProvider"/> —
        /// which every provider written before it existed does not — makes this a no-op, so a host
        /// that took over the ICC hook sees exactly the vector behaviour it saw before.</para>
        ///
        /// <para><b>Both of the ways that no-op happens are reported</b>, or a host whose provider implements the
        /// image seam and not this one gets colour-managed images beside device-space vector fills,
        /// in the same document, with nothing anywhere saying why — and the two are identical at
        /// the pixel level to a document whose profile simply is not managed.</para>
        /// </summary>
        internal static bool TryResolveIccBasedColor(PdfDocument doc, PdfArray csArray,
            float[] components, out SKColor color)
            => TryResolveIccBasedColor(doc, csArray, components, IccRenderingIntents.Default, out color);

        /// <summary>
        /// As <see cref="TryResolveIccBasedColor(PdfDocument, PdfArray, float[], out SKColor)"/>,
        /// honouring the graphics state's rendering intent (§8.6.5.8).
        /// </summary>
        /// <remarks>
        /// <para><b>The intent cannot cross the provider seam, and that is a fact about the seam
        /// rather than a gap here.</b> <see cref="IIccColorTransformProvider.TryTransformToSRgb"/>
        /// is public, shipped and takes no intent, and <c>Base</c> multi-targets netstandard2.0,
        /// which has no default interface members — so widening it is a break and adding a third
        /// interface beside it repeats the wart that made the second one necessary. Instead the
        /// built-in provider is recognised and called through directly: it is a one-line forwarder
        /// to <see cref="TryTransform(byte[], float[], IccRenderingIntent, out SKColor)"/>, so this
        /// is the same code with one more argument, not a bypass of anybody's provider.</para>
        /// <para>A host that registered its own provider owns colour management, intents included,
        /// and is told once per render that the intent it set is not being forwarded — otherwise
        /// "my CMM ignored /RI" and "this library never told it" look identical from outside.</para>
        /// </remarks>
        internal static bool TryResolveIccBasedColor(PdfDocument doc, PdfArray csArray,
            float[] components, IccRenderingIntent intent, out SKColor color)
        {
            color = default;
            if (csArray == null || csArray.Count < 2 || components == null || components.Length == 0)
                return false;
            if ((csArray[0] as PdfName)?.Value != PdfColorSpaceNames.ICCBased)
                return false;

            // A host provider that takes the intent gets it.
            if (!(IccTransform.Current is SkiaIccTransformProvider)
                && IccTransform.Current is IIccIntentColorTransformProvider withIntent)
            {
                byte[] bytes = DecodedProfileBytes(doc, csArray[1]);
                if (bytes == null) return false;
                try
                {
                    return withIntent.TryTransformToSRgb(bytes, components, IccRenderingIntents.ToPublic(intent),
                                                         IccRenderingIntents.Compensates(intent), out color);
                }
                catch (Exception ex)
                {
                    RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed,
                        "ICC component transform threw — device fallback used", ex);
                    return false;
                }
            }

            if (!(IccTransform.Current is IIccColorTransformProvider provider))
            {
                // Cannot fire on an ordinary render: the built-in SkiaIccTransformProvider
                // implements both seams, so this is reached only after a host has registered a
                // provider of its own that implements the image one alone. That host is exactly
                // who needs telling, and nothing else in the pipeline would tell them.
                const string PartialProvider =
                    "the registered ICC provider does not implement IIccColorTransformProvider, so "
                    + "vector colour is not managed; device fallback used";

                RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed, PartialProvider);
                IccTransform.NoteProfileConsulted(PartialProvider);
                return false;
            }

            byte[] profileBytes = DecodedProfileBytes(doc, csArray[1]);
            if (profileBytes == null)
            {
                const string Undecodable =
                    "the /ICCBased profile stream could not be decoded; device fallback used";

                RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed, Undecodable);
                IccTransform.NoteProfileConsulted(Undecodable);
                return false;
            }

            try
            {
                // The built-in provider forwards to the very method below, so calling it with the
                // intent is not a bypass — see the remarks.
                if (provider is SkiaIccTransformProvider)
                    return TryTransform(profileBytes, components, intent, out color);

                if (intent != IccRenderingIntents.Default) ReportIntentNotForwarded(intent);
                return provider.TryTransformToSRgb(profileBytes, components, out color);
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed,
                    "ICC component transform threw — device fallback used", ex);
                return false;
            }
        }

        /// <summary>
        /// Says once per registered provider, per thread, that the provider is not being told which
        /// rendering intent the file asked for.
        /// </summary>
        /// <remarks>
        /// <c>[ThreadStatic]</c> like every other diagnostic slot in this tree, and one-shot because
        /// a page sets its colour hundreds of times and a channel that repeats is a channel a caller
        /// filters. <b>Keyed to the provider instance</b>: a bare flag, never reset, said it once in a
        /// thread's lifetime — so a host that registered a second provider, or a render after the
        /// first on a pooled thread, was never told. It cannot fire on an ordinary render: the built-in
        /// provider takes the intent directly, and a provider implementing
        /// <see cref="IIccIntentColorTransformProvider"/> receives it.
        /// </remarks>
        internal static void ReportIntentNotForwarded(IccRenderingIntent intent)
        {
            object provider = IccTransform.Current;
            if (_intentNotForwardedFor != null && _intentNotForwardedFor.TryGetTarget(out object reported)
                && ReferenceEquals(reported, provider)) return;
            _intentNotForwardedFor = new WeakReference<object>(provider);

            RenderDiagnostics.Report(RenderDiagnosticKind.RenderParameterIgnored,
                $"Rendering intent /{IccRenderingIntents.Name(intent)} is not forwarded to the " +
                "registered ICC provider: it does not implement IIccIntentColorTransformProvider, " +
                "so the provider's own default applies.");
        }

        [ThreadStatic] private static WeakReference<object> _intentNotForwardedFor;   // weak: a slot must not keep a host provider alive

        /// <summary>
        /// Converts components that live in a <b>nested</b> colour space — a Separation/DeviceN
        /// <c>alternateSpace</c>, or an <c>/Indexed</c> base — consulting an <c>[/ICCBased …]</c>
        /// profile when that is what the space is, and otherwise converting through
        /// <paramref name="fallbackCs"/> exactly as before.
        /// </summary>
        /// <remarks>
        /// This exists because every nested site had independently collapsed its space to a device
        /// name via <c>ResolveAlternateCsName</c> / <c>ResolveSvgAltCsName</c> before any colour was
        /// computed, which threw the profile away. There are six such sites across the two
        /// renderers; routing them one at a time is how the pair drifts, so they all call this.
        /// <para>Declining is byte-identical to the previous behaviour and is reported through
        /// <c>IccTransform.LastUnsupportedProfile</c>.</para>
        /// </remarks>
        internal static SKColor ResolveAlternateColor(
            PdfDocument doc, PdfObject altObj, float[] components, string fallbackCs)
            => ResolveAlternateColor(doc, altObj, components, fallbackCs, IccRenderingIntents.Default);

        /// <summary>
        /// As <see cref="ResolveAlternateColor(PdfDocument, PdfObject, float[], string)"/>,
        /// honouring the graphics state's rendering intent.
        /// </summary>
        /// <remarks>
        /// The intent reaches here because a nested <c>[/ICCBased …]</c> is the <i>same</i> question
        /// as a top-level one — a Separation's alternate space, an <c>/Indexed</c> base and a
        /// shading's space are all evaluated through this method, and honouring <c>/RI</c> at the
        /// top level while dropping it here would be the partial consolidation CLAUDE.md warns is
        /// more dangerous than none: two colours in one document, from one profile, under one
        /// intent, resolved by two different tables.
        /// </remarks>
        internal static SKColor ResolveAlternateColor(
            PdfDocument doc, PdfObject altObj, float[] components, string fallbackCs,
            IccRenderingIntent intent)
        {
            PdfObject resolved = doc?.ResolveObject(altObj) ?? altObj;
            var arr = resolved as PdfArray;

            if (arr != null && arr.Count > 1
                && (arr[0] as PdfName)?.Value == PdfColorSpaceNames.ICCBased
                && TryResolveIccBasedColor(doc, arr, components, intent, out SKColor icc))
                return icc;

            // A CIE space's PARAMETER DICTIONARY is honoured here too, and it was not.
            //
            // ComponentsToColor below aliases CalGray onto DeviceGray and CalRGB onto DeviceRGB,
            // and routes Lab through the 3-argument D50-fused overload — so /Gamma, /Matrix,
            // /WhitePoint and /Range would be silently dropped by every caller that reaches this
            // fallback — an axial shading over [/CalGray << /Gamma 2.2 >>] sampled at 0.5 would render
            // rgb(128,128,128) while the SVG export of the same file gives rgb(55,55,55). The branches
            // live here, in the shared entry point, as in ContentStreamInterpreter.Color's sc/scn
            // ladder and the image path — routing them one at a time is how the pair drifts.
            if (arr != null && arr.Count > 1
                && doc?.ResolveObject(arr[1]) is PdfDictionary cieParams
                && components != null && components.Length > 0)
            {
                switch ((arr[0] as PdfName)?.Value)
                {
                    case PdfColorSpaceNames.CalGray:
                        return PdfColor.CalGrayToSRgb(components[0], cieParams, doc);

                    case PdfColorSpaceNames.CalRGB:
                        return PdfColor.CalRgbToSRgb(components, cieParams, doc);

                    case PdfColorSpaceNames.Lab:
                        return PdfColor.LabToSRgb(
                            components[0],
                            components.Length > 1 ? components[1] : 0f,
                            components.Length > 2 ? components[2] : 0f,
                            cieParams, doc);
                }
            }

            return PdfColor.ComponentsToColor(components, fallbackCs);
        }

        /// <summary>
        /// The decoded bytes of an ICC profile stream, memoised on the stream object so a page that
        /// sets the same colour space a thousand times decompresses and parses it once.
        /// </summary>
        internal static byte[] DecodedProfileBytes(PdfDocument doc, PdfObject streamObj)
        {
            var stream = (doc?.ResolveObject(streamObj) ?? streamObj) as PdfStream;
            if (stream == null)
            {
                IccTransform.NoteProfileConsulted(
                    "the /ICCBased array's second element is not a stream object");
                return null;
            }

            if (StreamBytesCache.TryGetValue(stream, out byte[] cached))
                return NonEmptyOrNoted(cached);

            byte[] bytes;
            try
            {
                bytes = stream.GetDecodedData() ?? Array.Empty<byte>();
            }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed,
                    "ICC profile stream decode failed", ex);
                bytes = Array.Empty<byte>();
            }

            try { StreamBytesCache.Add(stream, bytes); }
            catch (ArgumentException) { }
            return NonEmptyOrNoted(bytes);
        }

        /// <summary>
        /// A profile stream that decoded to nothing is still a profile the file asked for, so it
        /// gets a reason rather than reading as "no ICC profile present" — the whole point of
        /// <see cref="IccTransform.LastUnsupportedProfile"/> is telling those two apart.
        /// </summary>
        private static byte[] NonEmptyOrNoted(byte[] bytes)
        {
            if (bytes != null && bytes.Length > 0) return bytes;
            IccTransform.NoteProfileConsulted("the ICC profile stream decoded to zero bytes");
            return null;
        }

        // ==============================================================
        //  Image samples (the decode path)
        // ==============================================================

        /// <summary>
        /// Per-sample device→sRGB conversion for <b>one image</b>, bound to its ICC profile.
        /// </summary>
        /// <remarks>
        /// <para><b>Why the image path needs a different shape from the bitmap one.</b>
        /// <see cref="TryTransform(byte[], SKBitmap)"/> runs after the decode, on pixels that are
        /// already sRGB — fine for a 3-component profile, whose inputs survive as R,G,B, and
        /// useless for a CMYK one, whose four components were collapsed by
        /// <c>PdfColor.CmykToRgb</c> before it ever saw them. Recovering them by inverting that
        /// formula is exact only when min(c,m,y) = 0, so the conversion has to happen where the
        /// samples still are: inside the decode.</para>
        /// <para>The profile is resolved <b>once per image</b> rather than per pixel — a
        /// <c>ConditionalWeakTable</c> lookup per sample is not free — and the component scratch is
        /// an instance field, which is safe because one converter serves one decode loop and is
        /// never cached or shared.</para>
        /// </remarks>
        internal sealed class IccImageConverter
        {
            private readonly IccProfile _profile;
            private readonly float[] _components;

            /// <summary>
            /// The intent this image is drawn under — its own <c>/Intent</c> where it has one, the
            /// graphics state's otherwise. Bound once per image, like the profile: every sample of
            /// one image is converted under one intent.
            /// </summary>
            private readonly IccRenderingIntent _intent;

            private IccImageConverter(IccProfile profile, int components, IccRenderingIntent intent)
            {
                _profile = profile;
                _components = new float[components];
                _intent = intent;
            }

            /// <summary>
            /// A converter for <paramref name="imageDict"/>'s colour space, or <c>null</c> when it
            /// is not an <c>[/ICCBased …]</c> this evaluator can use with exactly
            /// <paramref name="components"/> components — in which case the caller keeps the device
            /// conversion it already had, byte for byte.
            /// </summary>
            /// <remarks>
            /// <c>graphicsStateIntent</c> is the rendering intent in force, which the image's own
            /// <c>/Intent</c> overrides (§8.9.5.2 Table 89). It is defaulted so that any caller
            /// reaching here without a graphics state keeps §8.6.5.8's default rather than
            /// inventing one.
            /// </remarks>
            internal static IccImageConverter TryCreate(PdfDocument doc, PdfDictionary imageDict,
                                                        int components,
                                                        IccRenderingIntent graphicsStateIntent
                                                            = IccRenderingIntents.Default)
            {
                if (doc == null || imageDict == null) return null;

                // /CS as well as /ColorSpace: an inline image abbreviates it (§8.9.7, Table 93),
                // and an inline image is exactly as entitled to an ICC space as an XObject.
                PdfObject csEntry = doc.ResolveObject(imageDict[PdfName.ColorSpace])
                                    ?? doc.ResolveObject(imageDict[PdfName.CS]);
                if (!(csEntry is PdfArray arr) || arr.Count < 2) return null;
                if ((arr[0] as PdfName)?.Value != PdfColorSpaceNames.ICCBased) return null;

                byte[] profileBytes = DecodedProfileBytes(doc, arr[1]);
                if (profileBytes == null) return null;

                return TryCreate(profileBytes, components,
                    IccRenderingIntents.ForImage(doc, imageDict, graphicsStateIntent));
            }

            /// <summary>
            /// A converter for a profile held as bytes — a JP2 file's <c>colr</c> box, which no
            /// dictionary names — or <c>null</c> when this evaluator cannot use it with exactly
            /// <paramref name="components"/> components. The dictionary overload resolves its
            /// stream and intent, then comes here.
            /// </summary>
            /// <param name="profileBytes">The raw ICC profile.</param>
            /// <param name="components">The samples' arity, which the profile's data colour space must match.</param>
            /// <param name="intent">The intent the image is drawn under, already resolved.</param>
            internal static IccImageConverter TryCreate(byte[] profileBytes, int components, IccRenderingIntent intent)
            {
                if (profileBytes == null || profileBytes.Length == 0) return null;
                IccProfile profile = GetProfile(profileBytes);
                if (profile == null || profile.Model == IccModel.Unsupported) return null;
                if (profile.ComponentCount != components) return null;
                return new IccImageConverter(profile, components, intent);
            }

            /// <summary>
            /// Converts one sample. Returns <c>false</c> — leaving the caller's device conversion
            /// in place — for anything the profile declines.
            /// </summary>
            internal bool TryConvert(float c0, float c1, float c2, float c3, out SKColor color)
            {
                _components[0] = c0; _components[1] = c1;
                _components[2] = c2; _components[3] = c3;
                return _profile.TryEvaluate(_components, _intent, out color);
            }
        }

        // ==============================================================
        //  Bitmaps (the image path)
        // ==============================================================

        /// <summary>
        /// Transforms a decoded image bitmap from an ICC profile's data space into sRGB, in place.
        /// Returns <c>false</c> without touching a pixel when the profile is not one of the two
        /// evaluated models.
        /// </summary>
        /// <remarks>
        /// <para>Grey runs through a 256-entry lookup table, so it costs one indexed read per
        /// channel. The three-component path evaluates the matrix per pixel, which is far slower —
        /// it is a <i>last resort</i> behind <c>SKColorSpace.CreateIcc</c>, which handles the
        /// ordinary matrix/TRC RGB profile in native code (see
        /// <see cref="SkiaIccTransformProvider.TransformToSRgb(byte[], SKBitmap)"/>). The pixel
        /// count is already bounded by <see cref="PdfLimits.MaxImagePixelArea"/> upstream.</para>
        /// <para>The pixels go through <c>BitmapPixels.Map</c>, which hands over <b>straight</b>
        /// colours and stores them back premultiplied where the bitmap is, which is what makes
        /// transforming the colour channels correct on a bitmap that already carries alpha. A profile
        /// that cannot evaluate is refused by a probe before any pixel is written, so the bitmap is
        /// never left half transformed.</para>
        /// </remarks>
        internal static bool TryTransform(byte[] profileBytes, SKBitmap bitmap)
        {
            if (bitmap == null) return false;
            IccProfile profile = GetProfile(profileBytes);
            if (profile == null || profile.Model == IccModel.Unsupported) return false;

            // A bitmap reaching here is already RGB, so a profile whose data space is neither
            // 3-component nor grey has nothing left to transform: its own inputs were consumed by
            // the decode. Declining SILENTLY is correct rather than lax — the decode applies such
            // a profile per sample, where the components still exist (see
            // TryCreateImageConverter), so this is not a fallback at all and reporting one would
            // be a false alarm on every colour-managed CMYK image.
            if (profile.ComponentCount != 3 && profile.Model != IccModel.GrayTrc) return false;

            BitmapPixels.Remap remap;
            if (profile.Model == IccModel.GrayTrc)
            {
                byte[] lut = BuildGrayLut(profile);
                if (lut == null) return false;
                remap = c => new SKColor(lut[c.Red], lut[c.Green], lut[c.Blue], c.Alpha);
            }
            else
            {
                var components = new float[3];
                if (!profile.TryEvaluate(components, out _)) return false;
                remap = c =>
                {
                    components[0] = c.Red   / 255f;
                    components[1] = c.Green / 255f;
                    components[2] = c.Blue  / 255f;
                    return profile.TryEvaluate(components, out SKColor mapped)
                        ? new SKColor(mapped.Red, mapped.Green, mapped.Blue, c.Alpha)
                        : c;
                };
            }

            try { BitmapPixels.Map(bitmap, remap); }
            catch (Exception ex)
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.IccTransformFailed,
                    "transforming bitmap pixels through the ICC profile failed", ex);
                return false;
            }
            return true;
        }

        /// <summary>Grey level → sRGB channel value, for all 256 possible sample values.</summary>
        private static byte[] BuildGrayLut(IccProfile profile)
        {
            var lut = new byte[256];
            var one = new float[1];
            for (int i = 0; i < 256; i++)
            {
                one[0] = i / 255f;
                if (!profile.TryEvaluate(one, out SKColor c)) return null;
                lut[i] = c.Red;
            }
            return lut;
        }
    }
}
