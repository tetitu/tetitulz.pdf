// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// One paint call's bound, tagged with the content-stream operation index it came from — the
    /// display list built by
    /// <see cref="ContentStreamInterpreter.BuildDisplayList"/>.
    /// </summary>
    /// <remarks>
    /// <b>A culling aid over the operators it covers, not a complete inventory.</b> Only some
    /// painting operators report a bound as of this increment (see
    /// <see cref="ContentStreamInterpreter.BuildDisplayList"/>'s remarks for which); an op index
    /// with no <see cref="DisplayListEntry"/> must be treated by any consumer as unconditionally
    /// paintable in every band or tile a spatial index is asked to serve — never as absent.
    /// </remarks>
    internal readonly struct DisplayListEntry
    {
        /// <summary>Index into the operation list this entry's bound came from.</summary>
        public int OpIndex { get; }

        /// <summary>
        /// The paint call's bound in <b>page-point space, Y-down</b> — conservative (may be wider
        /// than the operator's exact ink), never narrower, and independent of
        /// <see cref="PdfRenderContext.DpiScale"/>.
        /// </summary>
        /// <remarks>
        /// <b>Not device pixels, on purpose.</b> This is the same space <c>TransformPoint</c>
        /// produces and <c>_currentPath</c>/<c>canvasPath</c> already sit in — CTM and the PDF
        /// Y-up-to-screen-Y-down flip applied, DPI scaling not. <see cref="PdfRenderSession"/>'s
        /// band and tile geometry (<c>RenderPageBands</c>'s <c>clipY</c>/<c>clipH</c>) is in the
        /// same points-space, which is what a consumer needs to compare against without a DPI
        /// conversion of its own. <see cref="PdfRenderContext.DrawBitmap"/> and the text-blob site
        /// build an absolute screen matrix that bakes DPI in (it has to, to paint correctly), so
        /// both divide it back out (<c>PdfRenderContext.UnscaleByDpi</c>) before reporting here —
        /// every other site never multiplied by DPI in the first place.
        /// </remarks>
        public SKRect PageBounds { get; }

        public DisplayListEntry(int opIndex, SKRect pageBounds)
        {
            OpIndex = opIndex;
            PageBounds = pageBounds;
        }
    }
}
