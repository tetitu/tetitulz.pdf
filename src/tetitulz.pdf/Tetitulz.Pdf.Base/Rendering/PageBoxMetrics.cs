// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// A page box read as an origin and an extent: §7.9.5's rectangle normalised, with the
    /// "no usable page box" fallback both renderers take.
    /// </summary>
    /// <remarks>
    /// <para><b>Three copies of this existed, and two of them said so.</b> The box <i>choice</i>
    /// was already shared (<c>PdfPageRenderer.ResolveRenderBox</c>) and the rotation was already
    /// shared (<c>Core.Document.PdfPageRotation</c>); only this last step was left open-coded — in
    /// <c>MeasurePage</c>, in <c>RenderPageTileToBitmap</c> under a comment reading
    /// "RenderPageToBitmap does exactly this", and in <c>PdfSvgRenderer</c> under one reading
    /// "so both paths agree". By CLAUDE.md's rule a sentence asserting that two implementations
    /// agree is a duplication report, not documentation.</para>
    ///
    /// <para><b>What the normalisation is for.</b> §7.9.5 permits a rectangle written from either
    /// corner pair, so subtracting blindly gives a <b>negative</b> width for an inverted box. Each
    /// copy had learned that separately and recorded its own symptom: the raster path turned it
    /// into a 1×1-pixel page through <c>Math.Max(1, Ceiling(negative))</c>, and the SVG path
    /// emitted a negative <c>width</c> and <c>viewBox</c> extent that no viewer will render.</para>
    ///
    /// <para><b>The two counts are asymmetric on purpose and are preserved exactly.</b> The
    /// horizontal pair needs three elements and the vertical pair four, so a three-element
    /// rectangle — malformed, but real — still yields an x and a width while falling back for y
    /// and height. All three copies agreed on this; it is behaviour, not an oversight.</para>
    ///
    /// <para><b>This is defence in depth, and only half of it is reachable.</b>
    /// <c>PdfPageRenderer.ClipToMedia</c> normalises <i>both</i> rectangles before intersecting
    /// them and emits a canonical <c>[x0 y0 x1 y1]</c>, so every four-element box arriving through
    /// <c>ResolveRenderBox</c> — which is every render entry point, raster and SVG alike — is
    /// <b>already upright</b>. The one input that bypasses it is a box with fewer than four
    /// elements, which <c>ClipToMedia</c> hands back untouched.</para>
    ///
    /// <para>That makes the split exact, and it is worth stating rather than re-deriving: the
    /// <b>horizontal</b> <c>Math.Min</c> and <c>Math.Abs</c> are reachable, through a three-element
    /// box written from the far corner, and <c>InvertedPageBoxTests</c> covers them. The
    /// <b>vertical</b> pair is <b>structurally unreachable</b> — a box short enough to bypass
    /// <c>ClipToMedia</c> has no fourth element to invert, so <c>vertical</c> is false whenever the
    /// input could have been inverted. A mutation sweep found both vertical arms surviving, and
    /// this is why: not coverage debt, and not worth a contrived fixture.</para>
    ///
    /// <para>A leaf by construction: no static fields, so no type initializer. That matters here
    /// because the SVG renderer reads it, and CLAUDE.md records a sideways static reference
    /// crashing the Base test host mid-run.</para>
    /// </remarks>
    internal readonly struct PageBoxMetrics
    {
        private PageBoxMetrics(float x, float y, float width, float height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>Lower-left x of the normalised rectangle, or 0 when there is no usable box.</summary>
        internal float X { get; }

        /// <summary>Lower-left y of the normalised rectangle, or 0 when there is no usable box.</summary>
        internal float Y { get; }

        /// <summary>Width in PDF points, or <see cref="PdfPageSizes.DefaultWidth"/>.</summary>
        internal float Width { get; }

        /// <summary>Height in PDF points, or <see cref="PdfPageSizes.DefaultHeight"/>.</summary>
        internal float Height { get; }

        /// <summary>
        /// Reads <paramref name="box"/> — a <c>/MediaBox</c>, <c>/CropBox</c> or any other boundary
        /// rectangle — as an origin and an extent.
        /// </summary>
        /// <param name="box">The rectangle, or <c>null</c> when the page declared none; one holding a
        /// number that is not finite as a <c>float</c> reads as none.</param>
        internal static PageBoxMetrics Of(PdfArray box)
        {
            // A box with a number past float's range is no usable box: the default page, in both
            // renderers, rather than an infinite `width` in the SVG and a blank raster.
            if (MatrixGuard.HasNonFiniteNumber(box, null)) box = null;
            bool horizontal = box != null && box.Count >= 3;
            bool vertical = box != null && box.Count >= 4;

            return new PageBoxMetrics(
                horizontal ? Math.Min(Element(box, 0), Element(box, 2)) : 0f,
                vertical ? Math.Min(Element(box, 1), Element(box, 3)) : 0f,
                horizontal ? Math.Abs(Element(box, 2) - Element(box, 0)) : PdfPageSizes.DefaultWidth,
                vertical ? Math.Abs(Element(box, 3) - Element(box, 1)) : PdfPageSizes.DefaultHeight);
        }

        /// <summary>
        /// One element, through the shared operand coercion. Both renderers' private
        /// <c>GetBoxFloat</c> aliases forwarded here already, so the element read was never the
        /// part that differed.
        /// </summary>
        private static float Element(PdfArray box, int index) => ContentOperand.ArrayFloat(box, index);
    }
}
