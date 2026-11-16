// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Builder.Model
{
    /// <summary>
    /// The rule that keeps a page's content box usable: margins that would leave almost no room
    /// give way, so a document can never be laid out one word a line and one line a page.
    /// </summary>
    /// <remarks>
    /// <para>A section whose margins swallow its page — <c>MarginTop + MarginBottom &gt;= PageHeightPt</c>,
    /// or the same on the width — has a content box of zero or less on that axis. The exporter then
    /// draws one line per page (and, with a one-point-wide column, one character per line), so a 1 KB
    /// document explodes into hundreds of thousands of pages and tens of megabytes. The margins
    /// <b>give way</b>: when a page keeps less than <see cref="MinContentPt"/> on an axis, its two
    /// margins on that axis drop to zero and the content box becomes the whole page.</para>
    /// <para>This is the one owner of the rule. <see cref="Export.PdfExporter"/> applies it to every
    /// section it lays out (so the Builder API and the HTML <c>@page</c> path are both covered), and
    /// <c>DocxToPdf</c>'s <c>SectionPageSize</c> applies it as the final step of resolving a
    /// <c>w:pgMar</c> — the floor and the yield live here so the two cannot drift apart. The caller
    /// reports the give-way in its own diagnostics sink; this is pure arithmetic.</para>
    /// </remarks>
    public static class PageContentBox
    {
        /// <summary>The least content a page keeps on each axis, in points: half an inch.</summary>
        public const double MinContentPt = 36;

        /// <summary>
        /// The margins to use on one axis of a page <paramref name="extent"/> points long. When the
        /// two margins would leave less than <see cref="MinContentPt"/> of content between them, both
        /// give way to zero; otherwise they stand. <paramref name="gaveWay"/> is <see langword="true"/>
        /// when they were dropped, for the caller to report.
        /// </summary>
        /// <param name="extent">The page's size on this axis, in points (its width or its height).</param>
        /// <param name="near">The near margin (left or top), in points.</param>
        /// <param name="far">The far margin (right or bottom), in points.</param>
        /// <param name="gaveWay">Set <see langword="true"/> when the margins were dropped to zero.</param>
        /// <returns>The near and far margins to use on this axis.</returns>
        public static (double Near, double Far) Fit(double extent, double near, double far, out bool gaveWay)
        {
            // A non-finite or negative margin is a caller-validation concern (NumericArguments /
            // ModelMeasurements), not this rule's: it only decides whether a finite pair leaves room.
            if (extent - near - far >= MinContentPt)
            {
                gaveWay = false;
                return (near, far);
            }
            gaveWay = true;
            return (0, 0);
        }
    }
}
