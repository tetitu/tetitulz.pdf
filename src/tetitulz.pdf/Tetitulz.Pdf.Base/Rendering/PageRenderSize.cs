// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The size a page will render at, as reported by
    /// <see cref="PdfPageRenderer.GetRenderSize(System.IO.Stream,int,int,PdfRenderOptions)"/>.
    /// </summary>
    /// <remarks>
    /// <para>Answering this needed a render before: the geometry lived inside the rasteriser, so a
    /// caller sizing a control or allocating a buffer either rendered the page and measured the
    /// result, or re-derived it from <c>/MediaBox</c> and got one of <c>/CropBox</c> selection,
    /// <c>/UserUnit</c>, <c>/Rotate</c> or the resolution clamp wrong.</para>
    /// <para><b><see cref="WidthPixels"/> and <see cref="HeightPixels"/> already have
    /// <c>/Rotate</c> applied</b>, so they are the dimensions of the image the renderer returns —
    /// not of the page box, which is what <see cref="WidthPoints"/> and <see cref="HeightPoints"/>
    /// describe. For a 90°-rotated page the two pairs are transposed relative to each other, and
    /// that is the point of reporting both.</para>
    /// </remarks>
    public sealed class PageRenderSize
    {
        /// <summary>Width of the rendered image in pixels, with <c>/Rotate</c> applied.</summary>
        public int WidthPixels { get; }

        /// <summary>Height of the rendered image in pixels, with <c>/Rotate</c> applied.</summary>
        public int HeightPixels { get; }

        /// <summary>Width of the selected page box in PDF points, before rotation.</summary>
        public float WidthPoints { get; }

        /// <summary>Height of the selected page box in PDF points, before rotation.</summary>
        public float HeightPoints { get; }

        /// <summary>The page's <c>/Rotate</c>, normalised to 0, 90, 180 or 270.</summary>
        public int Rotation { get; }

        /// <summary>
        /// The resolution the page will actually be rendered at.
        /// </summary>
        /// <remarks>
        /// <b>This can be lower than the DPI asked for.</b> A page whose pixel extent would exceed
        /// the per-axis or total-area safety limits is scaled down, and that reduction is reported
        /// at render time through <see cref="RenderDiagnosticKind.RenderResolutionReduced"/>. Here
        /// it is available before the render, which is the only way a caller can size a buffer
        /// correctly for such a page.
        /// </remarks>
        public float EffectiveDpi { get; }

        /// <summary>
        /// The page's <c>/UserUnit</c> as the renderer applies it: points per default user-space
        /// unit (§14.11.1), 1 for a page that sets none or an unusable one.
        /// </summary>
        /// <remarks>
        /// <see cref="WidthPoints"/> and <see cref="HeightPoints"/> are in user-space units, the
        /// space text and annotation positions are in; the page's extent in points — what the
        /// rendered image is at <see cref="EffectiveDpi"/> — is them times this. A viewer that laid
        /// a page out at <see cref="WidthPoints"/> drew a <c>/UserUnit 10</c> page at a tenth of the
        /// size it rendered.
        /// </remarks>
        public float UserUnit { get; init; } = 1f;

        /// <summary>Creates a render-size report.</summary>
        public PageRenderSize(int widthPixels, int heightPixels, float widthPoints,
                              float heightPoints, int rotation, float effectiveDpi)
        {
            WidthPixels  = widthPixels;
            HeightPixels = heightPixels;
            WidthPoints  = widthPoints;
            HeightPoints = heightPoints;
            Rotation     = rotation;
            EffectiveDpi = effectiveDpi;
        }
    }
}
