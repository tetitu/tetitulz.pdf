// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// A line made of several runs is laid out as one line: in the paragraph's direction, its runs in
    /// bidi order, each run's own spaces kept — on the legacy path as on the Builder path.
    /// </summary>
    /// <remarks>
    /// <para>Shaping (and so reordering) each run on its own and then drawing the runs left to right
    /// in logical order would read a Hebrew paragraph whose words fall in several runs backwards, put
    /// a list marker on the left of a right-to-left item and a cell's text on the cell's left in any
    /// alignment, and drop the spaces opening a run at every wrap — "Hello" + " World" drawn
    /// "HelloWorld". A table of a <c>w:bidi</c> section is not mirrored: ECMA-376-1 §17.4.1 and
    /// §17.6.1 rule that out.</para>
    /// <para>Order is read back through the text extractor, which restores a right-to-left line's
    /// logical order from the positions it was drawn at: a line drawn in the wrong order reads back
    /// wrong. Positions are read from the content stream.</para>
    /// </remarks>
    public class RtlLineLayoutTests
    {
        private const string Shalom = "\u05E9\u05DC\u05D5\u05DD";
        private const string Olam = "\u05E2\u05D5\u05DC\u05DD";
        private const string Gadol = "\u05D2\u05D3\u05D5\u05DC";
        private const string Item = "\u05E4\u05E8\u05D9\u05D8";

        // ------------------------------------------------------------------ order

        /// <summary>Hebrew, a Latin run, Hebrew: read in logical order, a space between each, on both paths.</summary>
        [HebrewCapableDocxFontFact]
        public void ARightToLeftLineOfSeveralRuns_ReadsInLogicalOrder_OnBothPaths()
        {
            byte[] docx = Docx(Rtl(Heb(Shalom + " "), Latin("ABC DEF"), Heb(" " + Olam)));

            foreach (bool legacy in new[] { true, false })
                Assert.Contains(Shalom + " ABC DEF " + Olam, TextOf(docx, legacy), StringComparison.Ordinal);
        }

        /// <summary>Three Hebrew runs — a bold word between two plain ones — keep their order.</summary>
        [HebrewCapableDocxFontFact]
        public void AFormattedWordInAHebrewLine_KeepsItsPlace_OnBothPaths()
        {
            byte[] docx = Docx(Rtl(Heb(Shalom + " "), Heb(Gadol, bold: true), Heb(" " + Olam)));

            foreach (bool legacy in new[] { true, false })
                Assert.Contains(Shalom + " " + Gadol + " " + Olam, TextOf(docx, legacy), StringComparison.Ordinal);
        }

        /// <summary>
        /// A right-to-left list item's marker is on its right, and reads first: the marker, then the item —
        /// not on the left of the item, touching it.
        /// </summary>
        /// <remarks>
        /// The Builder draws a marker as its own text object after its item, at the item's right; the plain
        /// extractor orders a right-to-left line's strings by where they were drawn, so it reads the marker
        /// first.
        /// </remarks>
        [HebrewCapableDocxFontFact]
        public void ARightToLeftListMarker_IsOnTheRightOfItsItem_OnBothPaths()
        {
            byte[] docx = Docx(numbered: true, Rtl(Heb(Item)));

            foreach (bool legacy in new[] { true, false })
                Assert.Contains("\u05D0. " + Item, TextOf(docx, legacy), StringComparison.Ordinal);
        }

        /// <summary>
        /// A Latin run ending in a space, between Hebrew runs: the space is at the paragraph's level,
        /// not the Latin's, so the run is cut there and the space lands between the Latin and the
        /// Hebrew that follows it — not doubled on one side and missing on the other.
        /// </summary>
        [HebrewCapableDocxFontFact]
        public void ALatinRunsClosingSpace_StaysBetweenItAndTheNextWord_OnTheLegacyPath()
            => Assert.Contains(Shalom + " ABC " + Olam,
                TextOf(Docx(Rtl(Heb(Shalom + " "), Latin("ABC "), Heb(Olam))), legacy: true), StringComparison.Ordinal);

        /// <summary>
        /// Brackets at the paragraph's right-to-left level are mirrored (UAX #9 L4) — in a
        /// standard-14 run, "(ABC)", and inside a Hebrew run, where the closing bracket is a piece of
        /// its own that holds no strong character and so has to be given the line's direction.
        /// </summary>
        [HebrewCapableDocxFontFact]
        public void BracketsAroundLatinInAHebrewLine_AreMirrored_OnTheLegacyPath()
        {
            // Hebrew at both ends: the extractor takes a line's direction from its first strong
            // character as drawn, so a line drawn with Latin at its left would read back left to right.
            Assert.Contains(Shalom + " (ABC) " + Olam,
                TextOf(Docx(Rtl(Heb(Shalom + " "), Latin("(ABC)"), Heb(" " + Olam))), legacy: true), StringComparison.Ordinal);
            Assert.Contains(Shalom + " (ABC) " + Olam,
                TextOf(Docx(Rtl(Heb(Shalom + " (ABC) " + Olam))), legacy: true), StringComparison.Ordinal);
            // A cell's line, through its own copy of the piece function.
            Assert.Contains(Shalom + " (ABC) " + Olam,
                TextOf(Docx(Table(Rtl(Heb(Shalom + " (ABC) " + Olam)))), legacy: true), StringComparison.Ordinal);
        }

        /// <summary>
        /// A bracket pair a line break splits is still a pair (UAX #9 N0, resolved over the paragraph):
        /// in a right-to-left paragraph "abc (DEF⏎GHI) עולם", the pair holds Latin and follows Latin,
        /// so the closing bracket is Latin too and is drawn after GHI, as written. Resolved a line at a
        /// time, it would stand between GHI and the Hebrew, take the paragraph's direction, and be drawn
        /// mirrored on GHI's left. In a body paragraph and in a cell.
        /// </summary>
        [Fact]
        public void ABracketPairSplitByALineBreak_IsStillAPair_OnTheLegacyPath()
        {
            Paragraph Split() => Rtl(new Run(new Text("abc (DEF") { Space = SpaceProcessingModeValues.Preserve },
                new Break(), new Text("GHI) ") { Space = SpaceProcessingModeValues.Preserve }), Heb(Olam));

            foreach (byte[] docx in new[] { Docx(Split()), Docx(Table(Split())) })
            {
                string content = PdfContentProbe.DecodeStreams(Pdf(docx, legacy: true));
                Assert.Contains(@"(GHI\)) Tj", content, StringComparison.Ordinal);
                Assert.DoesNotContain(@"(\() Tj", content, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// A <c>w:br</c> in a cell ends the line, as in a body paragraph: "One⏎Two" draws Two on a line
        /// below One, and "One⏎⏎Two" a line further down, the empty line taking its height — not
        /// "OneTwo", as a cell reading its <c>w:t</c> elements alone would draw.
        /// </summary>
        [Fact]
        public void ALineBreakInACell_EndsTheLine_OnTheLegacyPath()
        {
            Paragraph Broken(int breaks)
            {
                var run = new Run(new Text("One"));
                for (int i = 0; i < breaks; i++) run.Append(new Break());
                run.Append(new Text("Two"));
                return new Paragraph(run);
            }

            double Drop(int breaks)
            {
                string content = PdfContentProbe.DecodeStreams(Pdf(Docx(Table(Broken(breaks))), legacy: true));
                Assert.DoesNotContain("(OneTwo) Tj", content, StringComparison.Ordinal);
                return LineY(content, "One") - LineY(content, "Two");
            }

            double one = Drop(1), two = Drop(2);
            Assert.True(one > 8, $"Two {one} below One");
            Assert.Equal(2 * one, two, 1);
        }

        /// <summary>
        /// A page or a column break in a cell is passed over, as Word 16 draws it (printed):
        /// "One", the break, "Two" draw "OneTwo" on one line — only a text-wrapping break ends a cell's line.
        /// </summary>
        [Theory]
        [InlineData("page")]
        [InlineData("column")]
        public void APageOrColumnBreakInACell_IsPassedOver_AsWordDoes(string type)
        {
            var run = new Run(new Text("One"),
                new Break { Type = type == "page" ? BreakValues.Page : BreakValues.Column }, new Text("Two"));
            string content = PdfContentProbe.DecodeStreams(Pdf(Docx(Table(new Paragraph(run))), legacy: true));
            Assert.Contains("(OneTwo) Tj", content, StringComparison.Ordinal);
        }

        /// <summary>
        /// A line break adds nothing to the paragraph's bidi text, as Word 16 draws it (printed): in a
        /// right-to-left paragraph "1," then a <c>w:br</c> then "2 שלום", the comma
        /// keeps with its number and is drawn to the right of the 1 — "1,2" read through the break, its
        /// comma a separator between digits (W4). Taken as a separator of its own, the break would leave
        /// the comma a neutral in the paragraph's direction, drawn on the 1's left. In the body and in a cell.
        /// </summary>
        [Fact]
        public void ACommaBeforeALineBreak_KeepsWithItsNumber_AsWordDoes()
        {
            Paragraph Split() => Rtl(new Run(new Text("1,"), new Break(),
                new Text("2 ") { Space = SpaceProcessingModeValues.Preserve }), Heb(Shalom));

            foreach (byte[] docx in new[] { Docx(Split()), Docx(Table(Split())) })
            {
                string content = PdfContentProbe.DecodeStreams(Pdf(docx, legacy: true));
                Assert.Contains("(1,) Tj", content, StringComparison.Ordinal);
                Assert.DoesNotContain("(,) Tj", content, StringComparison.Ordinal);
            }
        }

        private static double LineY(string content, string literal)
        {
            int at = content.IndexOf("(" + literal + ") Tj", StringComparison.Ordinal);
            Assert.True(at >= 0, $"({literal}) Tj not drawn");
            Match line = null;
            foreach (Match m in LineStart.Matches(content.Substring(0, at))) line = m;
            Assert.NotNull(line);
            return double.Parse(line.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        /// <summary>A cell's line is ordered as a body paragraph's is.</summary>
        [HebrewCapableDocxFontFact]
        public void ARightToLeftCellOfSeveralRuns_ReadsInLogicalOrder_OnBothPaths()
        {
            byte[] docx = Docx(Table(Rtl(Heb(Shalom + " "), Latin("ABC"), Heb(" " + Olam))));

            foreach (bool legacy in new[] { true, false })
                Assert.Contains(Shalom + " ABC " + Olam, TextOf(docx, legacy), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ spaces

        /// <summary>
        /// A run's opening space, and a run of nothing but a space, are drawn: "Hello" + " World" +
        /// " " + "again" reads "Hello World again".
        /// </summary>
        [Fact]
        public void ARunsOpeningSpaceAndASpaceOnlyRun_AreDrawn_OnBothPaths()
        {
            byte[] docx = Docx(new Paragraph(Latin("Hello"), Latin(" World"), Latin(" "), Latin("again")));

            foreach (bool legacy in new[] { true, false })
                Assert.Contains("Hello World again", TextOf(docx, legacy), StringComparison.Ordinal);
        }

        /// <summary>The same in a cell, whose wrap had its own copy of the loop.</summary>
        [Fact]
        public void ACellRunsOpeningSpaceAndASpaceOnlyRun_AreDrawn_OnBothPaths()
        {
            byte[] docx = Docx(Table(new Paragraph(Latin("Hello"), Latin(" World"), Latin(" "), Latin("again"))));

            foreach (bool legacy in new[] { true, false })
                Assert.Contains("Hello World again", TextOf(docx, legacy), StringComparison.Ordinal);
        }

        // ------------------------------------------------------------------ alignment

        /// <summary>
        /// A centred cell paragraph is centred, and a <c>w:bidi</c> cell paragraph with no
        /// <c>w:jc</c> starts at the cell's right; the control, a plain one, starts at its left.
        /// </summary>
        [Fact]
        public void ACellLine_IsPlacedByItsParagraphsAlignment_OnTheLegacyPath()
        {
            double left = LegacyLineX(Docx(Table(new Paragraph(Latin("Cell")))), "Cell");
            double centred = LegacyLineX(Docx(Table(new Paragraph(
                new ParagraphProperties(new Justification { Val = JustificationValues.Center }), Latin("Cell")))), "Cell");
            double rtl = LegacyLineX(Docx(Table(Rtl(Latin("Cell")))), "Cell");

            Assert.True(centred > left + 100, $"centred at {centred}, left at {left}");
            Assert.True(rtl > centred + 100, $"right to left at {rtl}, centred at {centred}");
        }

        /// <summary>
        /// A <c>w:bidi</c> paragraph with no <c>w:jc</c> starts at the right margin on the Builder path,
        /// as on the legacy one; it hugged the left margin.
        /// </summary>
        [Fact]
        public void ARightToLeftParagraph_IsRightAligned_OnTheBuilderPath()
        {
            double rtl = PdfContentProbe.XOf(PdfContentProbe.DecodeStreams(Pdf(Docx(Rtl(Latin("Edge"))), legacy: false)), "Edge");
            double ltr = PdfContentProbe.XOf(PdfContentProbe.DecodeStreams(Pdf(Docx(new Paragraph(Latin("Edge"))), legacy: false)), "Edge");

            Assert.True(rtl > ltr + 300, $"right to left at {rtl}, left to right at {ltr}");
        }

        /// <summary>
        /// A table with no <c>w:bidiVisual</c> in a <c>w:bidi</c> section keeps its first column on
        /// the left, and a paragraph with no <c>w:bidi</c> there is left aligned (ECMA-376-1 §17.4.1,
        /// §17.6.1) — on the Builder path, which read the section's direction as its content's.
        /// </summary>
        [Fact]
        public void ARightToLeftSection_DoesNotMirrorItsTablesOrItsLeftToRightParagraphs_OnTheBuilderPath()
        {
            var table = new Table(
                new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }),
                new TableGrid(new GridColumn { Width = "4000" }, new GridColumn { Width = "4000" }),
                new TableRow(new TableCell(new Paragraph(Latin("One"))), new TableCell(new Paragraph(Latin("Two")))));
            string content = PdfContentProbe.DecodeStreams(Pdf(Docx(rtlSection: true, table, new Paragraph(Latin("Plain"))), legacy: false));

            double one = PdfContentProbe.XOf(content, "One"), two = PdfContentProbe.XOf(content, "Two");
            Assert.True(one < two, $"One at {one}, Two at {two}");
            Assert.True(PdfContentProbe.XOf(content, "Plain") < 150);
        }

        /// <summary>
        /// <c>w:bidiVisual</c> is an on/off element: present with no <c>w:val</c> it mirrors the table's columns,
        /// and <c>w:val="0"</c> leaves them left to right, as no element does — read by its presence alone, a
        /// table explicitly set left to right would be mirrored.
        /// </summary>
        [Theory]
        [InlineData("none", false)]
        [InlineData("on", true)]
        [InlineData("off", false)]
        public void ATablesBidiVisual_IsReadByItsValue_OnTheLegacyPath(string flag, bool mirrored)
            => AssertTablesBidiVisualIsReadByItsValue(flag, mirrored, legacy: true);

        /// <summary>The same three on the Builder path, whose mapper read the element's presence alone too.</summary>
        [Theory]
        [InlineData("none", false)]
        [InlineData("on", true)]
        [InlineData("off", false)]
        public void ATablesBidiVisual_IsReadByItsValue_OnTheBuilderPath(string flag, bool mirrored)
            => AssertTablesBidiVisualIsReadByItsValue(flag, mirrored, legacy: false);

        /// <summary>
        /// A mirrored table's outer borders are drawn on its sides: three columns, a 3-pt <c>w:left</c> and
        /// <c>w:right</c> and no inside vertical border. The borders were resolved for each cell's logical place
        /// and kept when the cell moved, so the 3-pt lines fell between the columns and none on the sides.
        /// </summary>
        [Fact]
        public void AMirroredTablesOuterBorders_AreDrawnOnItsSides_OnTheLegacyPath()
        {
            var tblPr = new TableProperties(
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct },
                new BiDiVisual(),
                new TableBorders(
                    new LeftBorder { Val = BorderValues.Single, Size = 24 },
                    new RightBorder { Val = BorderValues.Single, Size = 24 },
                    new InsideVerticalBorder { Val = BorderValues.Nil }));
            var table = new Table(tblPr,
                new TableGrid(new GridColumn { Width = "3000" }, new GridColumn { Width = "3000" }, new GridColumn { Width = "3000" }),
                new TableRow(new TableCell(new Paragraph(Latin("A"))), new TableCell(new Paragraph(Latin("B"))),
                             new TableCell(new Paragraph(Latin("C")))));
            string content = PdfContentProbe.DecodeStreams(Pdf(Docx(rtlSection: false, table, new Paragraph(Latin("Plain"))), legacy: true));

            // The x of every vertical 3-pt stroke ("3.00 w … x y m x y' l S").
            var thick = System.Text.RegularExpressions.Regex.Matches(content,
                    @"3\.00 w\n[^\n]* RG\n(-?[\d.]+) (-?[\d.]+) m\n(-?[\d.]+) (-?[\d.]+) l\nS")
                .Cast<System.Text.RegularExpressions.Match>()
                .Where(m => m.Groups[1].Value == m.Groups[3].Value)
                .Select(m => Math.Round(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)))
                .Distinct().OrderBy(x => x).ToArray();

            // The table fills the content box of a Letter page with one-inch margins: its sides are at 72 and
            // 540. Unswapped, the two 3-pt strokes fell on the interior edges, a third and two thirds across.
            Assert.Equal(new double[] { 72, 540 }, thick);
        }

        private void AssertTablesBidiVisualIsReadByItsValue(string flag, bool mirrored, bool legacy)
        {
            var tblPr = new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });
            if (flag == "on") tblPr.Append(new BiDiVisual());
            if (flag == "off") tblPr.Append(new BiDiVisual { Val = OnOffOnlyValues.Off });
            var table = new Table(tblPr,
                new TableGrid(new GridColumn { Width = "4000" }, new GridColumn { Width = "4000" }),
                new TableRow(new TableCell(new Paragraph(Latin("One"))), new TableCell(new Paragraph(Latin("Two")))));
            string content = PdfContentProbe.DecodeStreams(Pdf(Docx(rtlSection: false, table, new Paragraph(Latin("Plain"))), legacy));

            // A legacy cell line is "BT\n<x> <y> Td\n…(<text>) Tj": the x of the last Td before the word.
            // The Builder path's draws are read by the probe.
            double LineX(string word)
            {
                if (!legacy) return PdfContentProbe.XOf(content, word);
                int at = content.IndexOf("(" + word + ") Tj", StringComparison.Ordinal);
                Assert.True(at >= 0, $"'{word}' was never drawn");
                var td = System.Text.RegularExpressions.Regex.Matches(content.Substring(0, at), @"(-?[\d.]+) -?[\d.]+ Td");
                return double.Parse(td[td.Count - 1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            double one = LineX("One"), two = LineX("Two");
            Assert.True(mirrored ? one > two : one < two, $"{flag}: One at {one}, Two at {two}");
        }

        /// <summary>
        /// The section's <c>w:bidi</c> is kept as its layout, so a DOCX exported from the Builder model
        /// writes it back — and its plain paragraph stays left to right. The model had no place for it,
        /// and the re-export dropped it. The controls: a section without it gets none, nor does one whose
        /// <c>w:bidi</c> says <c>w:val="0"</c>.
        /// </summary>
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, false)]
        [InlineData(false, true)]
        public void ASectionsBidi_IsWrittenBack_ByTheBuilderPathsDocxExport(bool rtlSection, bool statedOff)
        {
            byte[] docx = Docx(rtlSection, new Paragraph(Latin("One")), new Paragraph(Latin("Plain")));
            if (statedOff)
                using (var edit = new MemoryStream())
                {
                    edit.Write(docx, 0, docx.Length);
                    using (var word = WordprocessingDocument.Open(edit, true))
                    {
                        var sectPr = word.MainDocumentPart.Document.Body.GetFirstChild<SectionProperties>();
                        sectPr.Append(new BiDi { Val = false });
                        word.MainDocumentPart.Document.Save();
                    }
                    docx = edit.ToArray();
                }
            Builder.Model.Document model;
            using (var word = WordprocessingDocument.Open(new MemoryStream(docx), false))
                model = DocxToBuilderMapper.Map(word, new DocxToPdfOptions());

            Assert.Equal(rtlSection, model.Sections[0].RightToLeftLayout);
            Assert.Equal(Builder.Model.TextDirection.LeftToRight, model.Sections[0].Direction);

            using var exported = WordprocessingDocument.Open(new MemoryStream(model.ToDocx()), false);
            Body body = exported.MainDocumentPart.Document.Body;
            Assert.Equal(rtlSection, body.GetFirstChild<SectionProperties>().GetFirstChild<BiDi>() != null);
            Assert.All(body.Elements<Paragraph>(), p => Assert.Null(p.ParagraphProperties?.BiDi));
        }

        // ------------------------------------------------------------------ tabs

        /// <summary>
        /// Right to left, a tab's stop is measured from the right margin: "A⇥B" and "AA⇥B" put B at
        /// the same place, left of the first default stop. Measured from the left, as before, B moved
        /// with A's width.
        /// </summary>
        [Fact]
        public void ARightToLeftTab_IsMeasuredFromTheRightMargin_OnTheLegacyPath()
        {
            Paragraph Tabbed(string before) => Rtl(new Run(new Text(before), new TabChar(), new Text("B")));

            byte[] one = Pdf(Docx(Tabbed("A")), legacy: true);
            byte[] two = Pdf(Docx(Tabbed("AA")), legacy: true);
            string content = PdfContentProbe.DecodeStreams(one);

            // Visual order: B first, then the tab, then A — B's line starts the text object.
            Assert.True(content.IndexOf("(B) Tj", StringComparison.Ordinal) < content.IndexOf("(A) Tj", StringComparison.Ordinal));
            Assert.Equal(LegacyLineX(one, "B"), LegacyLineX(two, "B"), 2);
            Assert.True(LegacyLineX(one, "B") > 400);
        }

        /// <summary>
        /// A right-to-left paragraph's tab stops, as Word 16 places them (measured from its own output):
        /// each the mirror image of a left-to-right stop, measured from the right margin
        /// (540 pt here), the stop 2 in in (396). "AAA⇥BBB" draws BBB left of the stop for a left stop, from
        /// it for a right one, centred on it for a centre one; "AAA⇥12.5" puts the period at it for a
        /// decimal one. With no stop, the default half inch: BBB ends at 504.
        /// </summary>
        [Theory]
        [InlineData(null, "BBB", 504 - 3 * 8.004)]
        [InlineData("left", "BBB", 396 - 3 * 8.004)]
        [InlineData("right", "BBB", 396)]
        [InlineData("center", "BBB", 396 - 3 * 8.004 / 2)]
        [InlineData("decimal", "12.5", 396 - 2 * 6.672)]
        public void ARightToLeftTab_AlignsAsWordDoes(string stop, string after, double expectedX)
        {
            var props = new ParagraphProperties(new BiDi());
            if (stop != null)
                props.Append(new Tabs(new TabStop
                {
                    Val = (TabStopValues)typeof(TabStopValues).GetProperty(char.ToUpperInvariant(stop[0]) + stop.Substring(1)).GetValue(null),
                    Position = 2880,
                }));
            var p = new Paragraph(props, Sized("AAA"), new Run(Size12(), new TabChar()), Sized(after));

            Assert.Equal(expectedX, LegacyLineX(Docx(p), after), 1);
        }

        /// <summary>
        /// A centre stop centres the text after it and a decimal stop puts its period at the stop, left to
        /// right too: both were read as a left and a right stop. The stop is 2 in from the left margin (216).
        /// </summary>
        [Theory]
        [InlineData("center", "BBB", 216 - 3 * 8.004 / 2)]
        [InlineData("decimal", "12.5", 216 - 2 * 6.672)]
        [InlineData("right", "BBB", 216 - 3 * 8.004)]   // control: unchanged
        [InlineData("left", "BBB", 216)]                // control: unchanged
        public void ALeftToRightCentreOrDecimalTab_Aligns(string stop, string after, double expectedX)
        {
            var val = (TabStopValues)typeof(TabStopValues).GetProperty(char.ToUpperInvariant(stop[0]) + stop.Substring(1)).GetValue(null);
            var p = new Paragraph(new ParagraphProperties(new Tabs(new TabStop { Val = val, Position = 2880 })),
                new Run(Size12(), new TabChar()), Sized(after));

            string content = PdfContentProbe.DecodeStreams(Pdf(Docx(p), legacy: true));
            int at = content.IndexOf("(" + after + ") Tj", StringComparison.Ordinal);
            Assert.True(at >= 0, after + " not drawn");
            double lineX = LegacyLineX(Pdf(Docx(p), legacy: true), after);   // the line starts with the tab
            var tab = Regex.Matches(content.Substring(0, at), @"\[(-?[\d.]+)\]\s*TJ");
            Assert.NotEmpty(tab);
            double tabWidth = -double.Parse(tab[tab.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture) / 1000 * 12;
            Assert.Equal(expectedX, lineX + tabWidth, 1);
        }

        private static RunProperties Size12() => new RunProperties(new FontSize { Val = "24" });

        private static Run Sized(string text) => new Run(Size12(), new Text(text) { Space = SpaceProcessingModeValues.Preserve });

        // ------------------------------------------------------------------ helpers

        private static Run Heb(string text, bool bold = false)
        {
            var props = new RunProperties(new RightToLeftText());
            if (bold) props.PrependChild(new Bold());
            return new Run(props, new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        }

        private static Run Latin(string text) => new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });

        private static Paragraph Rtl(params OpenXmlElement[] runs)
        {
            var p = new Paragraph(new ParagraphProperties(new BiDi()));
            p.Append(runs);
            return p;
        }

        private static Table Table(Paragraph cell)
            => new Table(
                new TableProperties(new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }),
                new TableGrid(new GridColumn { Width = "8000" }),
                new TableRow(new TableCell(cell)));

        private static byte[] Docx(params OpenXmlElement[] blocks) => Docx(false, false, blocks);

        private static byte[] Docx(bool numbered, params OpenXmlElement[] blocks) => Docx(numbered, false, blocks);

        private static byte[] Docx(bool rtlSection, OpenXmlElement table, OpenXmlElement after)
            => Docx(false, rtlSection, new[] { table, after });

        private static byte[] Docx(bool numbered, bool rtlSection, OpenXmlElement[] blocks)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                MainDocumentPart main = doc.AddMainDocumentPart();
                if (numbered)
                {
                    var numbering = main.AddNewPart<NumberingDefinitionsPart>();
                    numbering.Numbering = new Numbering(
                        new AbstractNum(new Level(
                            new StartNumberingValue { Val = 1 },
                            new NumberingFormat { Val = NumberFormatValues.Hebrew1 },
                            new LevelText { Val = "%1." }) { LevelIndex = 0 }) { AbstractNumberId = 0 },
                        new NumberingInstance(new AbstractNumId { Val = 0 }) { NumberID = 1 });
                    numbering.Numbering.Save();
                    foreach (var p in blocks)
                        if (p is Paragraph para)
                            (para.ParagraphProperties ??= new ParagraphProperties()).PrependChild(
                                new NumberingProperties(new NumberingLevelReference { Val = 0 }, new NumberingId { Val = 1 }));
                }
                var body = new Body();
                foreach (var b in blocks) body.Append(b.CloneNode(true));
                body.Append(rtlSection ? new SectionProperties(new BiDi()) : new SectionProperties());
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static byte[] Pdf(byte[] docx, bool legacy)
            => DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { UseLegacyRenderer = legacy });

        private static string TextOf(byte[] docx, bool legacy)
        {
            using var doc = PdfDocument.Open(new MemoryStream(Pdf(docx, legacy)));
            return PdfTextExtractor.ExtractText(doc);
        }

        private static readonly Regex LineStart = new Regex(@"BT\s+(-?[\d.]+)\s+(-?[\d.]+)\s+Td", RegexOptions.Compiled);

        /// <summary>
        /// Where the legacy renderer starts the text object that draws <paramref name="literal"/>
        /// first: a line's left end, its segments drawn from there in visual order.
        /// </summary>
        private static double LegacyLineX(byte[] pdfOrDocx, string literal)
        {
            byte[] pdf = pdfOrDocx.Length > 4 && pdfOrDocx[0] == (byte)'%' ? pdfOrDocx : Pdf(pdfOrDocx, legacy: true);
            string content = PdfContentProbe.DecodeStreams(pdf);
            int at = content.IndexOf("(" + literal + ") Tj", StringComparison.Ordinal);
            Assert.True(at >= 0, $"({literal}) Tj not drawn");
            Match line = null;
            foreach (Match m in LineStart.Matches(content.Substring(0, at))) line = m;
            Assert.NotNull(line);
            return double.Parse(line.Groups[1].Value, CultureInfo.InvariantCulture);
        }
    }
}
