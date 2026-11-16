// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The size an <b>authored</b> image may have — one every producer in the tree writes through,
    /// refused before the image is decoded or written: per side
    /// (<see cref="PdfLimits.MaxImageDimension"/>) and in area (<see cref="PdfLimits.MaxImagePixelArea"/>).
    /// </summary>
    /// <remarks>
    /// <para>It lived in Base (<c>Operations.Images</c>), where the inserters and the SVG converter
    /// asked it; the Builder's exporter and DocxToPdf's two image paths did not, so a JPEG whose
    /// frame header claimed 65 535 × 65 535 pixels went from a DOCX straight into the output, as it
    /// would from any producer — a document hostile to whatever opens it next, written by a library
    /// that refuses to open such a document itself. It is Core's now, and
    /// <see cref="PdfContentStreamBuilder.AddImage(byte[], int, int, string, string, int, double[], byte[])"/>
    /// asks it on every image, so no producer can write one past the rule; a producer that can skip an
    /// image and report asks first, as DocxToPdf and the Builder do, and a bare caller gets the
    /// exception.</para>
    /// </remarks>
    internal static class AuthoredImageSize
    {
        /// <summary>Why an image of this size is refused, or null when it is acceptable.</summary>
        /// <param name="widthPx">The image's width in pixels.</param>
        /// <param name="heightPx">The image's height in pixels.</param>
        internal static string Refusal(int widthPx, int heightPx)
        {
            if (widthPx > PdfLimits.MaxImageDimension || heightPx > PdfLimits.MaxImageDimension)
                return $"An image of {widthPx}x{heightPx} pixels exceeds the " +
                       $"{PdfLimits.MaxImageDimension}-pixel per-side limit, so writing it would " +
                       "produce a document that is hostile to whatever opens it next.";
            if ((long)widthPx * heightPx > PdfLimits.MaxImagePixelArea)
                return $"An image of {widthPx}x{heightPx} pixels is " +
                       $"{(long)widthPx * heightPx} in total, past the " +
                       $"{PdfLimits.MaxImagePixelArea}-pixel area limit. Two individually legal sides " +
                       "still multiply to an illegal area, which is why this check is separate.";
            return null;
        }

        /// <summary>
        /// The parameter an <see cref="System.ArgumentOutOfRangeException"/> for a refusal names:
        /// the height when it alone breaks the per-side limit, else the width.
        /// </summary>
        /// <param name="widthPx">The image's width in pixels.</param>
        /// <param name="heightPx">The image's height in pixels.</param>
        /// <param name="widthName">The caller's name for the width parameter.</param>
        /// <param name="heightName">The caller's name for the height parameter.</param>
        internal static string RefusedParameter(int widthPx, int heightPx, string widthName, string heightName)
            => heightPx > PdfLimits.MaxImageDimension && widthPx <= PdfLimits.MaxImageDimension
                ? heightName : widthName;
    }
}
