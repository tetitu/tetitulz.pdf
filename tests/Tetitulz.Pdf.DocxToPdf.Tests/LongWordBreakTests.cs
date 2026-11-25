// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A Latin word wider than its whole line is broken between characters, in a body paragraph and in
    /// a table cell alike (<c>ParagraphRenderer.BreakWord</c>, the one implementation both call): each
    /// line holds the largest piece that fits, and the pieces read back as the word.
    /// </summary>
    /// <remarks>
    /// Nothing tested either call when the two copies were merged — keeping the word whole, or
    /// measuring it as weightless, passed every suite. A shaped run is the other rule: it is not split
    /// (<c>ShapedRunWrapTests</c>).
    /// </remarks>
    public class LongWordBreakTests
    {
        [Fact]
        public void AWordWiderThanTheBodyIsBrokenIntoEqualPiecesThatFit()
        {
            string word = new string('x', 300);
            var lines = LiteralLines(Convert(new Body(new Paragraph(Run(word)))));

            AssertBrokenInto(word, lines);
        }

        [Fact]
        public void AWordWiderThanItsCellIsBrokenIntoEqualPiecesThatFit()
        {
            string word = new string('x', 60);
            var lines = LiteralLines(ConvertCell(Run(word), innerPoints: 100));

            AssertBrokenInto(word, lines);
        }

        /// <summary>The control: a word that fits its cell is one line, not broken.</summary>
        [Fact]
        public void AWordThatFitsItsCellIsOneLine()
        {
            var lines = LiteralLines(ConvertCell(Run("xxxx"), innerPoints: 100));

            Assert.Equal(new[] { "xxxx" }, lines);
        }

        /// <summary>
        /// Breaking a very long word measures each character once, not the whole growing chunk on every
        /// character. <c>width(cur.ToString())</c> built a fresh prefix string and re-measured it per
        /// character — O(n²) glyph lookups and allocations (≈40 GB for a 20 M-character word, minutes).
        /// With an additive width the chunks are identical; the allocation is the work signal, so no wall
        /// clock is needed to see the quadratic cost.
        /// </summary>
        [Fact]
        public void BreakingAHugeWord_DoesNotAllocateOrMeasureQuadratically()
        {
            const int n = 100_000;
            string word = new string('i', n);

            // An additive width of one unit per character, maxWidth huge so the whole word is one chunk —
            // a quadratic cost would be purely re-measuring and re-allocating the growing prefix per character.
            // Per-thread, not GC.GetTotalAllocatedBytes: BreakWord runs on this thread, and a process-wide
            // counter would also catch other xUnit classes allocating in parallel.
            long before = GC.GetAllocatedBytesForCurrentThread();
            List<string> chunks = ParagraphRenderer.BreakWord(word, double.MaxValue, s => s.Length);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(word, string.Concat(chunks));   // the word reads back whole
            Assert.True(allocated < 50_000_000,
                $"breaking a {n:N0}-character word allocated {allocated:N0} bytes — the chunk is re-measured per character");
        }

        /// <summary>
        /// Several lines, the word read back whole, and every piece but the last the same length — the
        /// largest that fits, since every character is the same width.
        /// </summary>
        private static void AssertBrokenInto(string word, List<string> lines)
        {
            Assert.True(lines.Count >= 3, $"a word far wider than its line made {lines.Count} line(s)");
            Assert.Equal(word, string.Concat(lines));
            Assert.Single(lines.Take(lines.Count - 1).Select(l => l.Length).Distinct());
            Assert.True(lines.Last().Length <= lines[0].Length);
        }

        private static Run Run(string text)
            => new Run(new RunProperties(new FontSize { Val = "24" }),
                new Text(text) { Space = SpaceProcessingModeValues.Preserve });

        /// <summary>A one-cell table whose text area is <paramref name="innerPoints"/> wide, its margins zero.</summary>
        private static string ConvertCell(Run run, double innerPoints)
        {
            string twips = ((int)Math.Round(innerPoints * 20)).ToString(CultureInfo.InvariantCulture);
            var table = new Table(
                new TableProperties(
                    new TableWidth { Width = twips, Type = TableWidthUnitValues.Dxa },
                    new TableCellMarginDefault(
                        new TableCellLeftMargin { Width = 0, Type = TableWidthValues.Dxa },
                        new TableCellRightMargin { Width = 0, Type = TableWidthValues.Dxa })),
                new TableGrid(new GridColumn { Width = twips }),
                new TableRow(new TableCell(new Paragraph(run))));
            return Convert(new Body(table));
        }

        /// <summary>The literal text each text object draws, top of the page first, one entry per line.</summary>
        private static List<string> LiteralLines(string pdf)
            => Regex.Matches(pdf, @"BT\n(-?[\d.]+) (-?[\d.]+) Td\n(.*?)ET\n", RegexOptions.Singleline)
                .Select(m => (
                    Y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    Text: string.Concat(Regex.Matches(m.Groups[3].Value, @"\(([^)]*)\) Tj").Select(t => t.Groups[1].Value))))
                .Where(t => t.Text.Length > 0)
                .OrderByDescending(t => t.Y)
                .Select(t => t.Text)
                .ToList();

        private static string Convert(Body body)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(body);
                main.Document.Save();
            }
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(ms.ToArray(),
                new DocxToPdfOptions { CompressStreams = false });
            return Encoding.Latin1.GetString(pdf);
        }
    }
}
