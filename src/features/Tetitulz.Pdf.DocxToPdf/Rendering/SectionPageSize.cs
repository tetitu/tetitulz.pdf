// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// A section's <c>w:pgSz</c> as page width and height in points — the renderer's and the Builder mapper's
    /// one reading of it.
    /// </summary>
    /// <remarks>
    /// A dimension of zero describes no page and is treated as absent: the size the conversion would use without
    /// it stands, and the omission is reported. Read as it is, a zero width or height would write a page of no area
    /// for every line on one path (300 words, 3981 pages) and fail the whole conversion on the other.
    /// </remarks>
    internal static class SectionPageSize
    {
        /// <summary>
        /// Applies <paramref name="pageSize"/>'s width and height, where it gives a usable one, to
        /// <paramref name="width"/> and <paramref name="height"/>.
        /// </summary>
        internal static void Apply(PageSize pageSize, ref double width, ref double height)
        {
            if (pageSize == null) return;
            uint? w = pageSize.Width?.Value, h = pageSize.Height?.Value;
            if (w == 0 || h == 0)
                DocxDiagnostics.Report("SectionPageSize.Apply",
                    $"w:pgSz gives a page {w?.ToString() ?? "(unset)"} by {h?.ToString() ?? "(unset)"} twips; a dimension of zero "
                    + "is ignored and the page keeps its size in that dimension.");
            if (w > 0) width = PdfUnits.TwipsToPoints(w.Value);
            if (h > 0) height = PdfUnits.TwipsToPoints(h.Value);
        }

        /// <summary>
        /// The least content a section's page keeps on each axis, in points — the Builder's
        /// <see cref="Tetitulz.Pdf.Builder.Model.PageContentBox.MinContentPt"/>, the one floor.
        /// </summary>
        internal const double MinContentPt = Tetitulz.Pdf.Builder.Model.PageContentBox.MinContentPt;

        /// <summary>
        /// A section's page size and margins together, onto the geometry it starts from — the renderer's and the
        /// Builder mapper's one reading — keeping at least <see cref="MinContentPt"/> of content on each axis.
        /// </summary>
        /// <remarks>
        /// <para>A section's margins may leave no room on its page: a 1 KB document whose <c>w:pgMar</c> swallowed
        /// a Letter page laid out one character a line and one line a page — 20 000 words, 200 001 pages and
        /// 66 MB on one path, 20 000 pages on the other. On each axis, a page smaller than the floor keeps the size
        /// it starts from (as a zero one does, <see cref="Apply"/>); margins that leave less than the floor give way
        /// to the ones it starts from, and when those leave less too, to none. Each step is reported.</para>
        /// </remarks>
        /// <param name="pageSize">The section's <c>w:pgSz</c>, or <c>null</c>.</param>
        /// <param name="margin">The section's <c>w:pgMar</c>, or <c>null</c>.</param>
        /// <param name="useDocumentMargins">Whether the section's margins are read (<see cref="DocxToPdfOptions.UseDocumentMargins"/>).</param>
        /// <param name="width">The page width, in and out.</param>
        /// <param name="height">The page height, in and out.</param>
        /// <param name="top">The top margin, in and out.</param>
        /// <param name="bottom">The bottom margin, in and out.</param>
        /// <param name="left">The left margin, in and out.</param>
        /// <param name="right">The right margin, in and out.</param>
        internal static void Resolve(PageSize pageSize, PageMargin margin, bool useDocumentMargins,
            ref double width, ref double height, ref double top, ref double bottom, ref double left, ref double right)
        {
            double startWidth = width, startHeight = height;
            double startTop = top, startBottom = bottom, startLeft = left, startRight = right;

            if (useDocumentMargins && margin != null)
            {
                if (margin.Top?.Value != null) top = PdfUnits.TwipsToPoints(margin.Top.Value);
                if (margin.Bottom?.Value != null) bottom = PdfUnits.TwipsToPoints(margin.Bottom.Value);
                if (margin.Left?.Value != null) left = PdfUnits.TwipsToPoints(margin.Left.Value);
                if (margin.Right?.Value != null) right = PdfUnits.TwipsToPoints(margin.Right.Value);
            }
            Apply(pageSize, ref width, ref height);

            KeepRoom("width", ref width, startWidth, ref left, ref right, startLeft, startRight);
            KeepRoom("height", ref height, startHeight, ref top, ref bottom, startTop, startBottom);
        }

        private static void KeepRoom(string axis, ref double extent, double startExtent,
            ref double near, ref double far, double startNear, double startFar)
        {
            if (extent < MinContentPt && startExtent >= MinContentPt)
            {
                DocxDiagnostics.Report("SectionPageSize.Resolve",
                    $"the section's page {axis} of {extent:0.##} pt is under {MinContentPt} pt; the page keeps {startExtent:0.##} pt.");
                extent = startExtent;
            }
            if (extent - near - far >= MinContentPt) return;

            DocxDiagnostics.Report("SectionPageSize.Resolve",
                $"the section's margins leave {extent - near - far:0.##} pt of its {extent:0.##} pt page {axis}, under {MinContentPt} pt; "
                + "they are ignored for that axis.");
            near = startNear;
            far = startFar;
            // The last step is the Builder's rule, the one owner of the floor and the yield.
            (near, far) = Tetitulz.Pdf.Builder.Model.PageContentBox.Fit(extent, near, far, out _);
        }
    }
}
