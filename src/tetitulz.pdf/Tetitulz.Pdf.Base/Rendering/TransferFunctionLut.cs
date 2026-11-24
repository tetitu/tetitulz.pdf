// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using SkiaSharp;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Functions;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The graphics-state transfer function (<c>/TR</c>, <c>/TR2</c> — §10.4) as a per-channel
    /// lookup table, and its application to a paint colour. <b>One implementation, both
    /// renderers.</b>
    ///
    /// <para><b>It was raster-only, and that is a divergence rather than an omission.</b> Both
    /// writers read <c>/TR2 ?? /TR</c> off the ExtGState and both applied it to <i>images</i>; only
    /// the raster path also applied it to fill and stroke colours (<c>PdfRenderContext.ApplyAlpha</c>).
    /// So a page setting a `1 − t` transfer function and filling a red rectangle rendered cyan and
    /// exported red — structurally identical output, opposite colours, no diagnostic.</para>
    ///
    /// <para><b>A LUT is exact here, not an approximation.</b> The input is one 8-bit channel, so
    /// 256 entries cover every possible value; this is memoisation of a per-pixel evaluation, and
    /// no colour moves because of it. An all-identity result collapses to <c>null</c> so the common
    /// case costs nothing at paint time.</para>
    ///
    /// <para><b>No static fields</b> — two <c>const</c>s and pure functions, so the type has no
    /// initializer. That is deliberate: pointing one renderer at the other's static is what once
    /// crashed the Base test host part-way through a parallel run, and a leaf shared by both must
    /// not reintroduce a type-initialization edge (see <c>SvgSyntax</c>, held to the same rule).</para>
    /// </summary>
    internal static class TransferFunctionLut
    {
        /// <summary>Distinct values of one 8-bit channel — the LUT's length.</summary>
        internal const int ChannelValueCount = 256;

        private const int MaxChannelValue = ChannelValueCount - 1;
        private const float MaxChannelValueF = MaxChannelValue;

        /// <summary>
        /// Whether a <c>/TR</c> or <c>/TR2</c> entry names the identity, in which case any active
        /// transfer function is cleared rather than replaced.
        /// </summary>
        /// <remarks>
        /// <c>/Default</c> is included with <c>/Identity</c> because neither renderer installs a
        /// device default to revert to — §10.4's default transfer function <i>is</i> the identity
        /// here, so treating the two alike states what actually happens.
        /// </remarks>
        internal static bool IsIdentityName(PdfObject tr) =>
            tr is PdfName n && (n.Value == "Identity" || n.Value == "Default");

        /// <summary>
        /// Builds the R/G/B lookup tables for a <c>/TR</c> or <c>/TR2</c> entry, or <c>null</c> when
        /// the entry is absent, names the identity, or evaluates to the identity on all three
        /// channels.
        /// </summary>
        /// <remarks>
        /// A single function object maps all three channels; an array of four maps
        /// [R/C, G/M, B/Y, Gray/K] and only the first three are used, because both renderers
        /// composite in sRGB.
        /// </remarks>
        internal static byte[][] Build(PdfObject tr, PdfDocument doc)
        {
            if (tr == null || IsIdentityName(tr)) return null;

            var resolved = doc?.ResolveObject(tr) ?? tr;
            PdfDictionary fnR, fnG, fnB;
            if (resolved is PdfArray arr)
            {
                // §10.4 gives the array form exactly four functions, [R/C, G/M, B/Y, Gray/K], and
                // only the first three reach an sRGB composite. A SHORTER array is malformed; it is
                // tolerated by remapping the channels it does carry, rather than refusing the whole
                // entry, in the raster image path and here alike.
                // Any element may be /Identity, which resolves to null and leaves its channel
                // alone — the same as a missing element.
                fnR = ElementAt(arr, 0, doc);
                fnG = ElementAt(arr, 1, doc);
                fnB = ElementAt(arr, 2, doc);
            }
            else
            {
                fnR = fnG = fnB = resolved as PdfDictionary;
            }

            byte[] lutR = BuildChannel(fnR, doc);
            byte[] lutG = BuildChannel(fnG, doc);
            byte[] lutB = BuildChannel(fnB, doc);
            if (lutR == null && lutG == null && lutB == null) return null;
            return new[] { lutR, lutG, lutB };
        }

        /// <summary>
        /// One channel's table, or <c>null</c> for "identity, nothing to remap" — which is also the
        /// answer for a null function, so a three-element <c>/TR</c> array leaves the fourth channel
        /// alone rather than blackening it.
        /// </summary>
        internal static byte[] BuildChannel(PdfDictionary fn, PdfDocument doc)
        {
            if (fn == null) return null;

            // A function of no type §7.10 defines leaves its channel as it was. The evaluator
            // answers such a function with {0}, not null — its convention for "no usable colour" —
            // so without this check the identity fallback below would never fire and a broken /TR
            // would drive the channel to 0: the page painted black where the function was meant to
            // adjust it.
            long type = PdfResolve.Number(doc, fn, PdfName.FunctionType) is PdfNumber t && t.IsInteger ? t.LongValue : -1;
            if (type != 0 && type != 2 && type != 3 && type != 4) return null;

            var lut = new byte[ChannelValueCount];
            // doc may legitimately be null (a form rendered without a document context). The
            // resolver is optional to the evaluator — it exists so a Type 3 stitching function can
            // reach indirect sub-functions — so passing null degrades exactly those rather than
            // throwing.
            var resolver = doc != null ? (Func<PdfObject, PdfObject>)doc.ResolveObject : null;
            for (int v = 0; v < ChannelValueCount; v++)
            {
                float[] r = PdfFunctionEvaluator.Evaluate(fn, new[] { v / MaxChannelValueF }, resolver);
                float o = r != null && r.Length > 0 ? r[0] : v / MaxChannelValueF;
                // PdfColor.Channel is the one quantisation rule; this was a hand-copy of it. It
                // introduces no type-initializer edge on this class's "no static fields" rule —
                // PdfColor holds only consts.
                lut[v] = PdfColor.Channel(o);
            }
            return lut;
        }

        private static PdfDictionary ElementAt(PdfArray arr, int i, PdfDocument doc)
            => i < arr.Count ? (doc?.ResolveObject(arr[i]) ?? arr[i]) as PdfDictionary : null;

        /// <summary>
        /// Remaps every pixel of <paramref name="bitmap"/> through <paramref name="lut"/>, leaving
        /// alpha alone. A null table is a no-op.
        /// </summary>
        /// <remarks>
        /// <para><b>Both renderers apply <c>/TR</c> to images, and only one of them did it in
        /// bounded time.</b> The raster path built this 256-entry table; the SVG writer called
        /// <c>PdfFunctionEvaluator.Evaluate</c> <i>per channel per pixel</i> — 3·w·h evaluations,
        /// each allocating its argument array and, for a Type 4 calculator function, re-tokenising
        /// the whole program. The raster comment measured the same shape at 12 million calls for a
        /// 2000×2000 image against 768 for the table. The table is not an approximation: a channel
        /// is one byte, so 256 entries cover the function's whole input domain exactly.</para>
        /// <para>The SVG copy also evaluated with <b>no resolver</b>, so a Type 3 stitching function
        /// whose sub-functions are indirect degraded there and not in the raster path — a difference
        /// in the pixels, not only in the speed.</para>
        /// </remarks>
        internal static void ApplyToBitmap(byte[][] lut, SKBitmap bitmap)
        {
            if (lut == null || bitmap == null) return;

            Images.BitmapPixels.Map(bitmap, c => new SKColor(
                lut[0] != null ? lut[0][c.Red]   : c.Red,
                lut[1] != null ? lut[1][c.Green] : c.Green,
                lut[2] != null ? lut[2][c.Blue]  : c.Blue,
                c.Alpha));
        }

        /// <summary>Remaps a colour's three channels; a null table leaves it untouched.</summary>
        internal static SKColor Apply(byte[][] lut, SKColor c)
        {
            if (lut == null) return c;
            return new SKColor(
                lut[0] != null ? lut[0][c.Red]   : c.Red,
                lut[1] != null ? lut[1][c.Green] : c.Green,
                lut[2] != null ? lut[2][c.Blue]  : c.Blue,
                c.Alpha);
        }

        /// <summary>
        /// Remaps an SVG paint value. Anything that is not an <c>rgb(r,g,b)</c> triple — a
        /// <c>url(#…)</c> reference to a gradient or pattern, <c>none</c> — is returned unchanged,
        /// which is correct rather than merely safe: §10.4 transfers colour <i>components</i>, and a
        /// gradient's stops have already been transferred where they were built.
        /// </summary>
        /// <remarks>
        /// The parse is <see cref="PdfColor.TryParseCssColor"/>, the same one the SVG image-mask
        /// path uses to recover a stencil's ink colour — that path already round-tripped a string
        /// this assembly wrote, so this is not a new liberty, and there is one parser rather than
        /// two.
        /// </remarks>
        internal static string ApplyToCssColor(byte[][] lut, string cssColor)
        {
            if (lut == null || cssColor == null) return cssColor;
            if (!PdfColor.TryParseCssColor(cssColor, out SKColor c)) return cssColor;
            return PdfColor.ToCssRgb(Apply(lut, c));
        }
    }
}
