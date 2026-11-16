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
    /// A shaped (Hebrew/Arabic) run wraps, in a table cell as in a body paragraph — one shaped piece
    /// per line, measured at the width it is drawn.
    /// </summary>
    /// <remarks>
    /// <para><b>The cell kept a shaped run atomic</b>, so a run wider than its cell ran past the cell
    /// edge. It was left that way on purpose: split into a segment per word, the cell's left-to-right
    /// draw would put the first word leftmost and reverse every right-to-left line. The body paragraph
    /// already had the right shape — wrap the words, then shape each <i>line's</i> words as one unit so
    /// bidi orders the line — and the cell now uses the same helper,
    /// <see cref="UnicodeText.WrapWords"/>.</para>
    ///
    /// <para><b>Moving the loop into one place found a defect in the original.</b> The body path added
    /// each line's bare shaped width, so a <c>w:w</c>-scaled run wrapped at its unscaled width, and the
    /// piece it built carried no text, so its width skipped the <c>w:spacing</c> tracking the draw
    /// emits as <c>Tc</c> — a right-aligned line then sat off its margin by the tracking.</para>
    ///
    /// <para><b>Fonts come from the host</b>, as in <c>RtlBidiTests</c>: the run names Arial as its
    /// complex-script font, and every width a fixture depends on is <b>measured through the resolver
    /// the renderer itself uses</b>, never written as a literal — so the cell widths below sit where
    /// the test says they do on any font that covers Hebrew.</para>
    /// </remarks>
    public class ShapedRunWrapTests
    {
        private const string Family = "Arial";
        private const double Size = 16;     // w:sz 32

        // ------------------------------------------------------------------
        //  The shared helper
        // ------------------------------------------------------------------

        /// <summary>One unit per character, so every expectation below is arithmetic.</summary>
        private static double Chars(string s) => s.Length;

        [Fact]
        public void WordsThatFitStayOnePieceWithNoBreak()
        {
            var pieces = UnicodeText.WrapWords("ab cd ef", Chars, 0, false, 8);

            var only = Assert.Single(pieces);
            Assert.Equal("ab cd ef", only.Text);
            Assert.False(only.BreakBefore);
        }

        [Fact]
        public void WordsThatOverflowBecomeOnePiecePerLine()
        {
            // "ab cd" is 5, "ab cd ef" would be 8: the line of 6 takes two words.
            var pieces = UnicodeText.WrapWords("ab cd ef gh", Chars, 0, false, 6);

            Assert.Equal(new[] { ("ab cd", false), ("ef gh", true) }, pieces);
        }

        [Fact]
        public void ALineAlreadyFullBreaksBeforeTheFirstPiece()
        {
            // Five units of Latin already on the line leave no room for even the first word.
            var pieces = UnicodeText.WrapWords("ab cd", Chars, 5, true, 6);

            Assert.Equal(new[] { ("ab cd", true) }, pieces);
        }

        [Fact]
        public void ALineWithRoomContinuesWithoutABreak()
        {
            // The control for the test above: the same run after two units fits beside them.
            var pieces = UnicodeText.WrapWords("ab", Chars, 2, true, 6);

            Assert.Equal(new[] { ("ab", false) }, pieces);
        }

        [Fact]
        public void AWordWiderThanTheLineGetsALineOfItsOwnAndIsNotSplit()
        {
            // Breaking a joined script between characters changes its shaping, so it overflows.
            var pieces = UnicodeText.WrapWords("ab abcdefgh cd", Chars, 0, false, 5);

            Assert.Equal(new[] { ("ab", false), ("abcdefgh", true), ("cd", true) }, pieces);
        }

        /// <summary>
        /// Repeated spaces are kept as written where the line goes on, and are one break opportunity
        /// — taken whole, leaving no empty piece — where it breaks. They were collapsed to one until
        /// 2026-10-22; Word draws every one.
        /// </summary>
        [Fact]
        public void RepeatedSpacesAreKeptAndAreOneBreakOpportunity()
        {
            Assert.Equal(new[] { ("ab   cd", false) }, UnicodeText.WrapWords("ab   cd", Chars, 0, false, 10));

            Assert.Equal(new[] { ("ab", false), ("cd", true) }, UnicodeText.WrapWords("ab   cd", Chars, 0, false, 4));
        }

        /// <summary>
        /// A run's spaces at its edges are its own: before its first word when the line goes on,
        /// after its last, and a run of nothing but spaces. Each was dropped, so " עולם" after a Latin
        /// run touched it, and a run holding only the space between two others vanished.
        /// </summary>
        [Theory]
        [InlineData(" ab", 2, true, " ab")]      // after other text on the line
        [InlineData("ab ", 0, false, "ab ")]     // closing the run
        [InlineData(" ", 2, true, " ")]          // nothing but a space
        [InlineData("  ab  ", 0, false, "  ab  ")]
        public void ARunsOwnEdgeSpacesAreKept(string run, double lineWidth, bool lineHasContent, string expected)
            => Assert.Equal(new[] { (expected, false) }, UnicodeText.WrapWords(run, Chars, lineWidth, lineHasContent, 20));

        /// <summary>The spaces before a word the line breaks at go with the break: the next line starts at the word.</summary>
        [Fact]
        public void TheSpacesAtABreakGoWithIt()
            => Assert.Equal(new[] { ("ab", true) }, UnicodeText.WrapWords(" ab", Chars, 5, true, 6));

        /// <summary>The one tokenizer: each word with the spaces before it, and closing spaces last with no word.</summary>
        [Fact]
        public void SpacedWordsKeepsEverySpace()
        {
            Assert.Equal(new[] { (" ", "ab"), ("  ", "cd"), (" ", "") }, UnicodeText.SpacedWords(" ab  cd ").ToArray());
            Assert.Equal(new[] { ("", "ab") }, UnicodeText.SpacedWords("ab").ToArray());
            Assert.Empty(UnicodeText.SpacedWords(""));
        }

        // ------------------------------------------------------------------
        //  A table cell
        // ------------------------------------------------------------------

        [Fact]
        public void AHebrewRunWiderThanItsCellWrapsOneWordPerLineInLogicalOrder()
        {
            // Three words of three, four and five letters, and a cell wider than the longest word
            // but narrower than any two of them — so each word is a line of its own.
            string[] words = { "אבג", "דהוז", "חטיכל" };
            double longest = words.Max(Measure);
            double narrowestPair = Enumerable.Range(0, words.Length - 1)
                .Min(i => Measure(words[i] + " " + words[i + 1]));
            Assert.True(longest < narrowestPair, "the fixture needs a cell between those two widths");

            var lines = GlyphLines(ConvertCell(string.Join(" ", words), (longest + narrowestPair) / 2));

            // Top to bottom, the words in the order they were written: a line per word, each drawn
            // as one shaped piece. The atomic cell drew one line of fourteen glyphs here.
            Assert.Equal(new[] { 3, 4, 5 }, lines.Select(l => l.Glyphs));
            Assert.All(lines, l => Assert.Equal(1, l.GlyphTjs));
        }

        [Fact]
        public void ACellLineHoldingSeveralWordsIsShapedAsOneUnitNotAWordAtATime()
        {
            // Two three-letter words fit on a line and three do not. One Tj per line is what keeps a
            // right-to-left line in visual order: a segment per word is drawn first-word-leftmost.
            string[] words = { "אבג", "דהו", "זחט", "יכל" };
            double two = Measure(words[0] + " " + words[1]);
            double three = Measure(words[0] + " " + words[1] + " " + words[2]);

            var lines = GlyphLines(ConvertCell(string.Join(" ", words), (two + three) / 2));

            Assert.Equal(new[] { 7, 7 }, lines.Select(l => l.Glyphs));   // three letters, a space, three
            Assert.All(lines, l => Assert.Equal(1, l.GlyphTjs));
        }

        [Fact]
        public void AScaledHebrewRunInACellWrapsAtItsDrawnWidth()
        {
            // The cell of the test above holds two of these words at 100%. At 200% each word is
            // drawn twice as wide, so only one fits — measured unscaled, two would, and overrun.
            string[] words = { "אבג", "דהו", "זחט", "יכל" };
            double two = Measure(words[0] + " " + words[1]);
            double three = Measure(words[0] + " " + words[1] + " " + words[2]);

            var lines = GlyphLines(ConvertCell(string.Join(" ", words), (two + three) / 2, scale: 200));

            Assert.Equal(new[] { 3, 3, 3, 3 }, lines.Select(l => l.Glyphs));
        }

        [Fact]
        public void LatinAfterAScaledHebrewRunInACellIsChargedTheScaledWidth()
        {
            // The Latin words that fit beside a Hebrew word depend on how wide that word was
            // counted. At 300% it takes three times the room, so fewer fit.
            int beside100 = LatinWordsOnFirstLine(ConvertCellWithLatinAfter("אבגדה", 300, scale: 100));
            int beside300 = LatinWordsOnFirstLine(ConvertCellWithLatinAfter("אבגדה", 300, scale: 300));

            Assert.True(beside300 < beside100,
                $"a 300% Hebrew word must leave less room: {beside100} Latin words beside it at 100%, {beside300} at 300%");
        }

        [Fact]
        public void TrackedHebrewInACellChargesEachLineOnlyItsOwnTracking()
        {
            // A tracked run that wraps is charged the tracking of the characters on each line, not of
            // the whole run. So the Latin that fits after its LAST line is the same as after a run
            // made only of that line's words.
            // Five words at 5 pt of tracking a character, in a cell six UNtracked words wide: three
            // fit on the first line and two on the last, which leaves room for Latin after them —
            // a last line with no room left would compare zero with zero and prove nothing.
            const string word = "אבג";
            string five = string.Join(" ", Enumerable.Repeat(word, 5));
            double inner = Measure(string.Join(" ", Enumerable.Repeat(word, 6)));

            string wrapped = ConvertCellWithLatinAfter(five, inner, spacingTwips: 100);
            var hebrewLines = GlyphLines(wrapped);
            Assert.True(hebrewLines.Count >= 2, "the fixture must wrap the tracked run");
            int lastLineWords = (hebrewLines.Last().Glyphs + 1) / (word.Length + 1);
            Assert.True(LatinWordsOnLine(wrapped, hebrewLines.Count - 1) > 0,
                "the fixture must leave room for Latin after the last Hebrew line");

            string alone = ConvertCellWithLatinAfter(
                string.Join(" ", Enumerable.Repeat(word, lastLineWords)), inner, spacingTwips: 100);

            Assert.Equal(LatinWordsOnLine(alone, 0), LatinWordsOnLine(wrapped, hebrewLines.Count - 1));
        }

        [Fact]
        public void ARunThatFitsItsCellIsStillOneLine()
        {
            // The control: wrapping must not break a run that fits.
            const string text = "אבג דהו";
            var lines = GlyphLines(ConvertCell(text, Measure(text) + 20));

            var only = Assert.Single(lines);
            Assert.Equal(7, only.Glyphs);
        }

        // ------------------------------------------------------------------
        //  A body paragraph
        // ------------------------------------------------------------------

        [Fact]
        public void AScaledHebrewParagraphWrapsAtItsDrawnWidth()
        {
            // At w:w 200 every line is drawn twice as wide, so it must hold about half the words.
            // The loop used to add each line's bare shaped width, so both came out with the same line
            // count — and the scaled one ran a full column past the margin.
            string text = string.Join(" ", Enumerable.Repeat("אבג דהוז חטיכל", 30));

            int unscaled = GlyphLines(ConvertParagraph(text, scale: 100, spacingTwips: 0)).Count;
            int scaled = GlyphLines(ConvertParagraph(text, scale: 200, spacingTwips: 0)).Count;

            Assert.True(unscaled >= 3, $"the fixture needs several lines unscaled; it made {unscaled}");
            Assert.True(scaled >= unscaled * 1.6,
                $"a 200% run should need about twice the lines: {unscaled} unscaled, {scaled} scaled");
        }

        [Fact]
        public void LatinAfterAScaledHebrewRunInAParagraphIsChargedTheScaledWidth()
        {
            // The body's version of the cell test: the width a Hebrew line adds to the line is what
            // the next run is measured against.
            int beside100 = LatinWordsOnFirstLine(ConvertParagraphWithLatinAfter("אבגדה", scale: 100));
            int beside300 = LatinWordsOnFirstLine(ConvertParagraphWithLatinAfter("אבגדה", scale: 300));

            Assert.True(beside300 < beside100,
                $"a 300% Hebrew word must leave less room: {beside100} Latin words beside it at 100%, {beside300} at 300%");
        }

        [Fact]
        public void ATrackedRightToLeftLineStillEndsAtTheRightMargin()
        {
            // w:spacing 40 twips is 2 pt after every character. A right-aligned line starts that much
            // further left per character, or its end overshoots the margin — the piece used to carry
            // no text, so its width left the tracking out and the line did not move.
            const string text = "שלום עולם";
            double plain = GlyphLines(ConvertParagraph(text, scale: 100, spacingTwips: 0)).Single().X;
            double tracked = GlyphLines(ConvertParagraph(text, scale: 100, spacingTwips: 40)).Single().X;

            Assert.Equal(2.0 * text.Length, plain - tracked, 1);
        }

        // ------------------------------------------------------------------
        //  Fixtures
        // ------------------------------------------------------------------

        /// <summary>The width the renderer will measure: the same resolver, family and size.</summary>
        private static double Measure(string text)
        {
            var font = FontResolver.Resolve(Family, bold: false, italic: false, UnicodeText.CodePoints(text));
            Assert.True(font != null, $"no installed font covers the fixture's Hebrew as {Family}");
            return UnicodeText.MeasureWidth(text, font, Size);
        }

        private static RunProperties HebrewRun(int scale = 100, int spacingTwips = 0)
        {
            var rPr = new RunProperties(
                new RunFonts { ComplexScript = Family, Ascii = Family, HighAnsi = Family },
                new RightToLeftText(),
                new FontSize { Val = "32" },
                new FontSizeComplexScript { Val = "32" });
            if (scale != 100) rPr.Append(new CharacterScale { Val = scale });
            if (spacingTwips != 0) rPr.Append(new Spacing { Val = spacingTwips });
            return rPr;
        }

        /// <summary>
        /// A one-cell table whose text area is exactly <paramref name="innerPoints"/> wide: the
        /// table width and its one grid column agree, and the cell margins are zero.
        /// </summary>
        private static string ConvertCell(string text, double innerPoints, int scale = 100)
            => ConvertCellRuns(innerPoints,
                new Run(HebrewRun(scale), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));

        /// <summary>The Hebrew run, then a Latin run long enough to fill whatever room it leaves.</summary>
        private static string ConvertCellWithLatinAfter(string hebrew, double innerPoints,
            int scale = 100, int spacingTwips = 0)
            => ConvertCellRuns(innerPoints,
                new Run(HebrewRun(scale, spacingTwips), new Text(hebrew) { Space = SpaceProcessingModeValues.Preserve }),
                LatinRun());

        /// <summary>
        /// A one-cell table whose text area is exactly <paramref name="innerPoints"/> wide: the
        /// table width and its one grid column agree, and the cell margins are zero.
        /// </summary>
        private static string ConvertCellRuns(double innerPoints, params Run[] runs)
        {
            string twips = ((int)Math.Round(innerPoints * 20)).ToString(CultureInfo.InvariantCulture);
            var table = new Table(
                new TableProperties(
                    new TableWidth { Width = twips, Type = TableWidthUnitValues.Dxa },
                    new TableCellMarginDefault(
                        new TableCellLeftMargin { Width = 0, Type = TableWidthValues.Dxa },
                        new TableCellRightMargin { Width = 0, Type = TableWidthValues.Dxa })),
                new TableGrid(new GridColumn { Width = twips }),
                new TableRow(new TableCell(new Paragraph(runs))));
            return Convert(new Body(table));
        }

        private static string ConvertParagraph(string text, int scale, int spacingTwips)
        {
            var paragraph = new Paragraph(
                new ParagraphProperties(new BiDi()),
                new Run(HebrewRun(scale, spacingTwips), new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
            return Convert(new Body(paragraph));
        }

        private static string ConvertParagraphWithLatinAfter(string hebrew, int scale)
            => Convert(new Body(new Paragraph(
                new Run(HebrewRun(scale), new Text(hebrew) { Space = SpaceProcessingModeValues.Preserve }),
                LatinRun())));

        private static Run LatinRun()
            => new Run(new RunProperties(new FontSize { Val = "24" }),
                new Text(" " + string.Join(" ", Enumerable.Repeat("word", 120)))
                { Space = SpaceProcessingModeValues.Preserve });

        private static int LatinWordsOnFirstLine(string pdf) => LatinWordsOnLine(pdf, 0);

        /// <summary>
        /// The Latin words drawn on the <paramref name="index"/>-th text line holding glyphs, top of
        /// the page first — the line a Hebrew piece shares with the run after it.
        /// </summary>
        private static int LatinWordsOnLine(string pdf, int index)
        {
            var bt = TextObjects(pdf).Where(t => Regex.IsMatch(t.Body, "<[0-9A-F]+> Tj")).ElementAt(index);
            return Regex.Matches(bt.Body, @"\(([^)]*)\) Tj")
                .Sum(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        }

        /// <summary>Every text object, top of the page first.</summary>
        private static List<(double X, double Y, string Body)> TextObjects(string pdf)
            => Regex.Matches(pdf, @"BT\n(-?[\d.]+) (-?[\d.]+) Td\n(.*?)ET\n", RegexOptions.Singleline)
                .Select(m => (
                    double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    m.Groups[3].Value))
                .OrderByDescending(t => t.Item2)
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

        /// <summary>
        /// Every text object that draws glyphs by id, top of the page first: where it starts, how
        /// many glyph <c>Tj</c>s it holds, and how many glyphs they draw between them.
        /// </summary>
        private static List<(double X, double Y, int GlyphTjs, int Glyphs)> GlyphLines(string pdf)
        {
            var lines = new List<(double X, double Y, int GlyphTjs, int Glyphs)>();
            foreach (var (x, y, body) in TextObjects(pdf))
            {
                var tjs = Regex.Matches(body, @"<([0-9A-F]+)> Tj");
                if (tjs.Count == 0) continue;
                lines.Add((x, y, tjs.Count, tjs.Sum(m => m.Groups[1].Value.Length / 4)));
            }
            return lines;
        }
    }
}
