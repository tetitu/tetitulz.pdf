// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// Page layouts a caller built, made safe to walk.
    /// </summary>
    /// <remarks>
    /// <para><see cref="PdfPageTextLayout"/>, <see cref="PdfTextLine"/> and <see cref="PdfTextWord"/>
    /// are public and constructible, so a layout can hold a null page, a null line, a line whose
    /// <c>Words</c> is null, or a null word. The field and invoice extractors took such layouts
    /// through some walks and crashed on them in others, and so did the spatial queries and the
    /// table extractor.</para>
    /// <para><b>One rule for which lines and words a layout holds</b>: <see cref="LinesOf"/> and
    /// <see cref="WordsOf"/>, which every walk goes through — the spatial queries and the table
    /// extractor at each walk, the field and invoice extractors once, through
    /// <see cref="WithoutNulls(IReadOnlyList{PdfPageTextLayout})"/>, before theirs.</para>
    /// </remarks>
    internal static class TextLayouts
    {
        /// <summary>A page's lines, less null ones; none for a page without lines.</summary>
        internal static IEnumerable<PdfTextLine> LinesOf(PdfPageTextLayout page)
            => page?.Lines?.Where(l => l != null) ?? Enumerable.Empty<PdfTextLine>();

        /// <summary>A line's words, less null ones; none for a line without words.</summary>
        internal static IEnumerable<PdfTextWord> WordsOf(PdfTextLine line)
            => line?.Words?.Where(w => w != null) ?? Enumerable.Empty<PdfTextWord>();

        /// <summary>
        /// <paramref name="pages"/> with null pages dropped and each page made
        /// <see cref="WithoutNulls(PdfPageTextLayout)"/>.
        /// </summary>
        internal static List<PdfPageTextLayout> WithoutNulls(IReadOnlyList<PdfPageTextLayout> pages)
        {
            var clean = new List<PdfPageTextLayout>();
            if (pages == null) return clean;
            foreach (var page in pages)
                if (page != null) clean.Add(WithoutNulls(page));
            return clean;
        }

        /// <summary>
        /// <paramref name="page"/> with its null lines dropped, a line with null <c>Words</c>
        /// dropped, and null words taken out of a line. A page or line that needed no change is the
        /// same instance.
        /// </summary>
        internal static PdfPageTextLayout WithoutNulls(PdfPageTextLayout page)
        {
            if (page.Lines != null && page.Lines.All(l => l?.Words != null && l.Words.All(w => w != null)))
                return page;

            var lines = new List<PdfTextLine>();
            foreach (var line in LinesOf(page))
            {
                if (line.Words == null) continue;
                var words = WordsOf(line).ToList();
                lines.Add(words.Count == line.Words.Count
                    ? line
                    : new PdfTextLine(line.Text, line.X, line.Y, line.Width, line.Height, words, line.IsRtl)
                      { IsVertical = line.IsVertical });
            }
            return new PdfPageTextLayout(page.PageNumber, lines);
        }
    }
}
