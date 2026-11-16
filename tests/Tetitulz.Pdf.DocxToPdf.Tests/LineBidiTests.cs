// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// The legacy renderer's one bidi pass, read with plain strings for segments: a paragraph resolved
    /// once over its segments, each line then given back as pieces — its text, level and whether an
    /// override holds it.
    /// </summary>
    public class LineBidiTests
    {
        private const char Rlo = (char)0x202E, Lre = (char)0x202A, Pdf = (char)0x202C;
        private const string Shalom = "שלום";

        private static bool IsControl(string seg) => seg.Length == 1 && (seg[0] == Rlo || seg[0] == Lre || seg[0] == Pdf);

        private static LineBidi.Paragraph Paragraph(bool rtl, params List<string>[] lines)
            => LineBidi.Paragraph.Of(lines.SelectMany(l => l), rtl, seg => seg);

        private static List<string> Order(List<string> line, LineBidi.Paragraph paragraph, bool rtl)
            => LineBidi.InVisualOrder(line, paragraph, rtl,
                seg => seg,
                IsControl,
                (seg, text, level, overridden, whole) => $"{text}|{level}|{(overridden ? "O" : "-")}");

        /// <summary>A left-to-right paragraph with no right-to-left character and no control has no bidi, and its line is handed back as it is.</summary>
        [Fact]
        public void APlainLeftToRightParagraph_IsReturnedAsItIs()
        {
            var line = new List<string> { "ab", " cd" };
            LineBidi.Paragraph paragraph = Paragraph(false, line);

            Assert.Null(paragraph);
            Assert.Same(line, Order(line, paragraph, rtl: false));
        }

        /// <summary>
        /// An override opened on one line goes on into the next: its text there is still overridden,
        /// and the PDF closing it ends the override for what follows.
        /// </summary>
        [Fact]
        public void AnOverrideALineBreakSplits_GoesOnIntoTheNextLine_AndIsClosedThere()
        {
            var first = new List<string> { "x ", Rlo.ToString(), "ab" };
            var second = new List<string> { "cd", Pdf.ToString(), " ef" };
            var paragraph = Paragraph(false, first, second);

            var one = Order(first, paragraph, rtl: false);
            Assert.Contains("ab|1|O", one);
            Assert.Contains("x |0|-", one);

            var two = Order(second, paragraph, rtl: false);
            Assert.Contains("cd|1|O", two);
            Assert.Contains(" ef|0|-", two);
            // Visual order, the controls (no text) aside: the overridden piece first, the rest after it.
            Assert.Equal(new[] { "cd|1|O", " ef|0|-" }, two.Where(s => s.Contains('|')).ToArray());
        }

        /// <summary>An embedding holds no override; a PDF with nothing open is ignored.</summary>
        [Fact]
        public void AnEmbedding_DoesNotOverride_AndAStrayPdfIsIgnored()
        {
            var line = new List<string> { Pdf.ToString(), Lre.ToString(), "ab" };
            Assert.Contains("ab|2|-", Order(line, Paragraph(false, line), rtl: false));
        }

        /// <summary>
        /// A right-to-left line: a segment is cut where its level changes, and L2 puts the Hebrew
        /// rightmost — the Latin piece first in visual order.
        /// </summary>
        [Fact]
        public void ARightToLeftLine_IsCutAtLevelChanges_AndReversed()
        {
            var line = new List<string> { Shalom + " ABC" };
            Assert.Equal(new[] { "ABC|2|-", Shalom + " |1|-" }, Order(line, Paragraph(true, line), rtl: true));
        }

        /// <summary>
        /// A line lies where its text next appears after the spaces a break took, and the next line is
        /// looked for after it; a line that is not there is not found — no text is skipped to find it.
        /// </summary>
        [Fact]
        public void ALine_IsFoundAfterTheSpacesABreakTook_AndNothingElseIsSkipped()
        {
            var paragraph = LineBidi.Paragraph.Of(new[] { Shalom + "  ab cd" }, true, seg => seg);

            Assert.True(paragraph.TryLocate(Shalom, out int first));
            Assert.Equal(0, first);
            Assert.True(paragraph.TryLocate("ab", out int second));
            Assert.Equal(Shalom.Length + 2, second);
            Assert.False(paragraph.TryLocate("ef", out _));
            Assert.False(LineBidi.Paragraph.Of(new[] { Shalom + " ab" }, true, seg => seg).TryLocate("b", out _));
        }

        /// <summary>
        /// A line the paragraph does not hold is resolved on its own and reported, not misplaced: the
        /// Hebrew line "ab" never was is still cut and ordered by its own levels.
        /// </summary>
        [Fact]
        public void ALineNotInItsParagraph_IsResolvedAlone_AndReported()
        {
            DocxDiagnostics.Clear();
            var paragraph = Paragraph(true, new List<string> { Shalom });
            var stray = new List<string> { Shalom + " ABC" };

            Assert.Equal(new[] { "ABC|2|-", Shalom + " |1|-" }, Order(stray, paragraph, rtl: true));
            Assert.Contains("LineNotInParagraph", DocxDiagnostics.LastError, System.StringComparison.Ordinal);
        }
    }
}
