// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>float</c> on an inline element — Bootstrap's <c>.pull-right</c>, a list-group item's
    /// <c>.badge</c>: floated to its edge of the line, with the line's text beside it.
    /// </summary>
    /// <remarks>Ignored, the floated element would print in the text where it was written.</remarks>
    public class InlineFloatTests
    {
        private static string Content(string html) => DecodeStreams(ToPdf(html));

        private static (string Content, List<string> Reports) WithReports(string html)
        {
            var reports = new List<string>();
            var options = Managed();
            options.OnDiagnostic = d => { lock (reports) reports.Add(d.Detail ?? ""); };
            return (DecodeStreams(ToPdf(html, options)), reports);
        }

        private static double Margin() => XOf(Content("<p>Margin</p>"), "Margin");

        /// <summary>Where a word starts when it ends at the right margin: the same word, right-aligned alone.</summary>
        private static double RightAligned(string word) => XOf(Content($"<p style='text-align:right'>{word}</p>"), word);

        /// <summary>
        /// What a shrink-to-fit float is wider than its content: the Builder's half point
        /// (<c>PdfExporter.ShrinkToFitSlackPt</c>), so a line measured to a rounded width cannot
        /// wrap. Its text sits in that box at its own alignment, so a right float's text ends half a
        /// point short of the margin, and the text beside a left float starts half a point later.
        /// </summary>
        private const double Slack = 0.5;

        [Fact]
        public void ARightFloatSitsAtTheRightEdge_BesideTheLine()
        {
            string c = Content("<p>Cras justo <span style='float:right'>Badge</span></p>");

            Assert.Equal(RightAligned("Badge") - Slack, XOf(c, "Badge"), 2);
            Assert.Equal(YOf(c, "Cras"), YOf(c, "Badge"), 1);
            Assert.Equal(Margin(), XOf(c, "Cras"), 1);
        }

        /// <summary>
        /// The float's first line is level with its paragraph's first line, whatever space the
        /// paragraph has above it or however tall its lines are.
        /// </summary>
        [Theory]
        [InlineData("margin-top:20px")]
        [InlineData("line-height:30px")]
        public void AFloatIsLevelWithItsLine(string style)
        {
            string c = Content($"<p>Above</p><p style='{style}'>Cras justo <span style='float:right'>Badge</span></p>");

            Assert.Equal(YOf(c, "Cras"), YOf(c, "Badge"), 1);
        }

        /// <summary>
        /// A space the floated element starts with starts no line: a left float's text is at the
        /// margin. Written against the word before it, so the space is not collapsed into one the
        /// line already has — and at the paragraph's start the collector drops it anyway.
        /// </summary>
        [Fact]
        public void AFloatsOwnLeadingSpaceIsDropped()
        {
            string c = Content("<p>Text<span style='float:left'> Icon </span>more</p>");

            Assert.Equal(Margin(), XOf(c, "Icon"), 2);
        }

        /// <summary>
        /// The space before a float that ended its line now ends the line, and a line's last space
        /// takes no room: a right-aligned paragraph's text still ends at the margin.
        /// </summary>
        [Fact]
        public void TheSpaceBeforeAFloatAtTheLinesEndTakesNoRoom()
        {
            string c = Content("<p style='text-align:right'>Text <span style='float:left'>Tag</span></p>");

            Assert.Equal(RightAligned("Text"), XOf(c, "Text"), 2);
            // The float inherits the paragraph's alignment, as text-align inherits: right in its box.
            Assert.Equal(Margin() + Slack, XOf(c, "Tag"), 2);
        }

        /// <summary>
        /// The float holds only its own lines: the paragraph's next line, below it, is back at the
        /// full measure.
        /// </summary>
        [Fact]
        public void TheLineBelowAFloatIsAtTheFullMeasure()
        {
            string c = Content("<p><span style='float:left'>Icon</span>First<br>Second</p>");

            Assert.True(XOf(c, "First") > Margin() + 10, "the first line is not beside the float");
            Assert.Equal(Margin(), XOf(c, "Second"), 1);
        }

        /// <summary>
        /// A floated element with nothing in it floats nothing and reports nothing; one with nothing
        /// to measure — a space — goes back into its line as it was.
        /// </summary>
        [Fact]
        public void AFloatWithNothingToFloat_LeavesItsLineAsItWas()
        {
            var empty = WithReports("<p>Text <span style='float:right'></span>more</p>");
            string spaced = Content("<p>A<span style='float:right'> </span>B</p>");
            string plain = Content("<p>A<span> </span>B</p>");

            Assert.Equal(Margin(), XOf(empty.Content, "Text"), 1);
            Assert.DoesNotContain(empty.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal));
            Assert.Equal(XOf(plain, "B"), XOf(spaced, "B"), 2);
        }

        /// <summary>
        /// A float inside a floated block is lifted inside that block: the block goes to the band,
        /// whose cell the float lifts in, so the tag sits at the block's right edge — within its
        /// 150px, well right of its text — level with its line, and no float is reported as inline.
        /// </summary>
        [Fact]
        public void AFloatInsideAFloatedBlock_IsLiftedInsideIt()
        {
            var boxed = WithReports("<div style='float:left;width:150px'>Box <span style='float:right'>Tag</span></div><p>Beside</p>");

            Assert.InRange(XOf(boxed.Content, "Tag"), Margin() + 60, Margin() + 112.5);
            Assert.Equal(YOf(boxed.Content, "Box"), YOf(boxed.Content, "Tag"), 1);
            Assert.DoesNotContain(boxed.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal) && r.Contains("floated inline", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// A float recorded earlier, in the paragraph before a floated box of two paragraphs, still
        /// lifts to the right edge: the box's own mapping keeps its records to itself. (Before batch
        /// 125 the box was abandoned for the band, and the test was that abandoning took only its
        /// own records.)
        /// </summary>
        [Fact]
        public void AFloatedBoxKeepsTheEarlierRecords()
        {
            var c = WithReports("<p>Text <span style='float:right'>One</span></p><div style='float:left;width:150px'><p>A</p><p>B</p></div><p>Beside</p>");

            Assert.Equal(RightAligned("One") - Slack, XOf(c.Content, "One"), 2);
            Assert.DoesNotContain(c.Reports, r => r.Contains("floated inline", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// A floated block of two paragraphs is a floated box since batch 125, and an inline float in
        /// its second paragraph lifts inside the box, to the box's right edge (150px is 112.5pt) on the
        /// paragraph's line; nothing is reported as left in its line. Before, the block fell back to the
        /// band, whose cell lifted the tag the same way.
        /// </summary>
        [Fact]
        public void AFloatedBoxLiftsTheInlineFloatInsideIt()
        {
            var box = WithReports("<div style='float:left;width:150px'><p>One</p><p>Two <span style='float:right'>Tag</span></p></div><p>Beside</p>");

            Assert.InRange(XOf(box.Content, "Tag"), Margin() + 60, Margin() + 112.5);
            Assert.Equal(YOf(box.Content, "Two"), YOf(box.Content, "Tag"), 1);
            Assert.DoesNotContain(box.Reports, r => r.Contains("floated inline", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// A float the model cannot float — no width, and a table with a column of tables nested past
        /// the layout's cap — is abandoned for the band, and the inline float recorded against a
        /// paragraph in its scratch mapping goes with it: the band maps the element again and lifts the
        /// tag in its cell, and nothing is reported as left in its line. A stale record would name a
        /// paragraph no longer in the document, and be counted as left inline.
        /// </summary>
        [Fact]
        public void AnUnmeasurableFloatAbandoned_LeavesNoInlineFloatBehind()
        {
            var band = WithReports("<div style='float:left'><table><tr><td><table><tr><td>Logo <span style='float:right'>Tag</span></td></tr></table></td><td>" +
                                   ManagedTestSupport.TablesPastTheNestingCap("") + "</td></tr></table></div><p>Beside</p>");

            Assert.Contains(band.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal) && r.Contains("fixed column", System.StringComparison.Ordinal));
            Assert.DoesNotContain(band.Reports, r => r.Contains("floated inline", System.StringComparison.Ordinal));
            // Lifted in the nested cell — too narrow for a line beside it, so above its line — and drawn.
            Assert.True(YOf(band.Content, "Tag") >= YOf(band.Content, "Logo"), "the tag was not lifted before its paragraph");
        }

        /// <summary>
        /// A floated element whose whole content is another float: the inner one, recorded first,
        /// is lifted, and the outer — its runs gone with it — is left, reported.
        /// </summary>
        [Fact]
        public void AFloatWhoseContentIsAnotherFloat_LiftsTheInnerOne()
        {
            var nested = WithReports("<p>Text <span style='float:right'><span style='float:left'>Both</span></span></p>");

            Assert.Equal(Margin(), XOf(nested.Content, "Both"), 1);
            Assert.Contains(nested.Reports, r => r.StartsWith("float: 1 floated inline", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// A left float starts the line, and the text starts exactly where it ends — its width shrunk
        /// to its content, and no space between.
        /// </summary>
        [Fact]
        public void ALeftFloatStartsTheLine_AndTheTextFollowsIt()
        {
            string spaced = Content("<p><span style='float:left'>Icon</span> Text</p>");
            string tight = Content("<p><span style='float:left'>Icon</span>Text</p>");
            // Where text after "Icon" starts with nothing floated: its own run, so it can be found.
            string inline = Content("<p>Icon<b>Text</b></p>");

            Assert.Equal(Margin(), XOf(spaced, "Icon"), 1);
            Assert.Equal(YOf(spaced, "Icon"), YOf(spaced, "Text"), 1);
            Assert.Equal(XOf(inline, "Text") + Slack, XOf(tight, "Text"), 2);
            // The space after the float starts the line beside it, where CSS removes it.
            Assert.Equal(XOf(tight, "Text"), XOf(spaced, "Text"), 1);
        }

        /// <summary><c>float:left</c> is physical: in a right-to-left paragraph too it goes on the left.</summary>
        [Fact]
        public void AFloatsSideIsPhysical_InARightToLeftParagraph()
        {
            string c = Content("<p dir='rtl'>שלום <span style='float:left'>Badge</span></p>");

            // Right-to-left, its text aligns to the right of its box: the slack is on its left.
            Assert.Equal(Margin() + Slack, XOf(c, "Badge"), 2);
        }

        /// <summary>
        /// A declared width is reserved: the float's text starts that far in from the right edge, so
        /// 100px more width starts it 75pt further left.
        /// </summary>
        [Fact]
        public void ADeclaredWidthIsReserved()
        {
            string narrow = Content("<p>Text <span style='float:right;width:100px'>Wide</span></p>");
            string wide = Content("<p>Text <span style='float:right;width:200px'>Wide</span></p>");

            Assert.Equal(XOf(narrow, "Wide") - 75, XOf(wide, "Wide"), 1);
            Assert.True(XOf(narrow, "Wide") < RightAligned("Wide") - 10, "the declared width was not reserved");
        }

        /// <summary>
        /// A float after a line break is on a later line: the paragraph is split at the break and
        /// the float lifts before the tail, at the edge and level with its own line, below the
        /// first. It stayed in its line, reported.
        /// </summary>
        [Fact]
        public void AFloatAfterALineBreak_IsLiftedOnItsOwnLine()
        {
            var afterBreak = WithReports("<p>First<br>Then <span style='float:right'>Late</span></p>");

            Assert.Equal(RightAligned("Late") - Slack, XOf(afterBreak.Content, "Late"), 2);
            Assert.Equal(YOf(afterBreak.Content, "Then"), YOf(afterBreak.Content, "Late"), 1);
            Assert.True(YOf(afterBreak.Content, "First") > YOf(afterBreak.Content, "Then") + 5, "the first line is not above the second");
            Assert.Equal(Margin(), XOf(afterBreak.Content, "Then"), 1);
            Assert.DoesNotContain(afterBreak.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// A float on each of two lines: each is lifted on its own line, at the edge, since a new
        /// line frees both sides again.
        /// </summary>
        [Fact]
        public void AFloatOnEachLine_BothLift()
        {
            var two = WithReports("<p>Alpha <span style='float:right'>One</span><br>Beta <span style='float:right'>Two</span></p>");

            Assert.Equal(RightAligned("One") - Slack, XOf(two.Content, "One"), 2);
            Assert.Equal(RightAligned("Two") - Slack, XOf(two.Content, "Two"), 2);
            Assert.Equal(YOf(two.Content, "Alpha"), YOf(two.Content, "One"), 1);
            Assert.Equal(YOf(two.Content, "Beta"), YOf(two.Content, "Two"), 1);
            Assert.DoesNotContain(two.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// The split keeps the paragraph's shape: the tail takes the spacing after and none before —
        /// a margin above the paragraph is not repeated at the break — and the head keeps no spacing
        /// after, so the second line and the paragraph after it sit where they did before the split.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData("margin-top:20px")]
        public void TheSplitParagraphKeepsItsSpacing(string style)
        {
            string split = Content($"<p style='{style}'>First<br>Then <span style='float:right'>Late</span></p><p>Next</p>");
            string whole = Content($"<p style='{style}'>First<br>Then Late</p><p>Next</p>");

            Assert.Equal(YOf(whole, "Next"), YOf(split, "Next"), 1);
            Assert.Equal(YOf(whole, "Then"), YOf(split, "Then"), 1);
        }

        /// <summary>
        /// A float in a table cell is lifted to the cell's edge, level with its line, since the
        /// cell's layout takes floats (Builder <c>TableCellFloatTests</c>). It stayed in its line,
        /// reported.
        /// </summary>
        [Fact]
        public void AFloatInATableCell_IsLiftedToTheCellsEdge()
        {
            var inCell = WithReports("<table style='width:400px'><tr><td>Cell <span style='float:right'>Tag</span></td></tr></table>");

            Assert.True(XOf(inCell.Content, "Tag") > XOf(inCell.Content, "Cell") + 200, "the float in a cell did not reach the cell's edge");
            Assert.Equal(YOf(inCell.Content, "Cell"), YOf(inCell.Content, "Tag"), 1);
            Assert.DoesNotContain(inCell.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// A float after a break in a boxed paragraph is lifted too: the box moves to a BoxStart round
        /// both pieces, so the border is drawn once — as many strokes as the same paragraph without the
        /// float — the float sits at the right, level with its line, and nothing is reported.
        /// </summary>
        [Fact]
        public void AFloatAfterABreakInABoxedParagraph_IsLiftedInsideOneBox()
        {
            var boxed = WithReports("<p style='border:1px solid black'>First<br>Then <span style='float:right'>Late</span></p>");
            string control = Content("<p style='border:1px solid black'>First<br>Then Late</p>");

            Assert.True(XOf(boxed.Content, "Late") > XOf(boxed.Content, "Then") + 200, $"the late float stayed at x={XOf(boxed.Content, "Late"):F1}");
            Assert.Equal(YOf(boxed.Content, "Then"), YOf(boxed.Content, "Late"), 1);
            Assert.True(YOf(boxed.Content, "Then") < YOf(boxed.Content, "First") - 5, "the tail is not below the head");
            Assert.Equal(Strokes(control), Strokes(boxed.Content));
            Assert.DoesNotContain(boxed.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// The box's padding stays inside the border and the float inside the padding: with 10px of
        /// padding the text starts past the margin and the float ends short of it; the paragraph
        /// after the box is back at the margin, so the box closed where the paragraph ended.
        /// </summary>
        [Fact]
        public void ASplitBoxKeepsItsPaddingAndCloses()
        {
            string c = Content("<p style='border:1px solid black; padding:10px'>First<br>Then <span style='float:right'>Late</span></p><p>After</p>");
            string control = Content("<p style='border:1px solid black; padding:10px'>First<br>Then Late</p><p>After</p>");

            Assert.InRange(XOf(c, "Then"), Margin() + 6, Margin() + 12);
            Assert.InRange(XOf(c, "First"), Margin() + 6, Margin() + 12);
            Assert.InRange(XOf(c, "Late"), RightAligned("Late") - 12, RightAligned("Late") - 6);
            Assert.Equal(Margin(), XOf(c, "After"), 1);
            // The padding is laid out once, above as at the sides: the head sits where the unsplit paragraph's does.
            Assert.InRange(YOf(c, "First"), YOf(control, "First") - 1.5, YOf(control, "First") + 1.5);
            Assert.InRange(YOf(c, "After"), YOf(control, "After") - 1.5, YOf(control, "After") + 1.5);
        }

        /// <summary>
        /// The paragraph's margin becomes the box's: with margin-top 20px the head sits where the same
        /// boxed paragraph without a float does, within the border's width (a BoxStart's text starts
        /// below its top border, a boxed paragraph's on it — 0.75 pt, and the two box models' own).
        /// </summary>
        [Fact]
        public void ASplitBoxKeepsItsMarginOutsideTheBorder()
        {
            string boxed = Content("<p>Above</p><p style='border:1px solid black; margin-top:20px'>First<br>Then <span style='float:right'>Late</span></p>");
            string control = Content("<p>Above</p><p style='border:1px solid black; margin-top:20px'>First<br>Then Late</p>");

            Assert.InRange(YOf(boxed, "First"), YOf(control, "First") - 1.5, YOf(control, "First") + 1.5);
            Assert.InRange(YOf(boxed, "Then"), YOf(control, "Then") - 1.5, YOf(control, "Then") + 1.5);
        }

        /// <summary>A background moves with the box: the split paragraph is filled as the unsplit one is, once.</summary>
        [Fact]
        public void ASplitBoxKeepsItsBackground()
        {
            string boxed = Content("<p style='background:#ff0000'>First<br>Then <span style='float:right'>Late</span></p>");
            string control = Content("<p style='background:#ff0000'>First<br>Then Late</p>");

            Assert.True(Fills(control) > 0, "the control paints no background");
            Assert.Equal(Fills(control), Fills(boxed));
            Assert.True(XOf(boxed, "Late") > XOf(boxed, "Then") + 200, "the late float in the shaded paragraph did not move");
        }

        /// <summary>
        /// The shadow moves with the box (batch 76): a float after a break in a shadowed paragraph lifts,
        /// and the shadow is drawn once — under the box round the pieces, as the unsplit paragraph's is
        /// under it — with nothing reported as left in its line. A hard shadow is a grey fill; a blurred
        /// one (batch 88) is an image, drawn once likewise.
        /// </summary>
        [Fact]
        public void AFloatAfterABreakInAShadowedParagraph_IsLiftedAndTheShadowDrawnOnce()
        {
            var shadowed = WithReports("<p style='box-shadow: 2px 2px gray'>First<br>Then <span style='float:right'>Late</span></p>");
            string control = Content("<p style='box-shadow: 2px 2px gray'>First<br>Then Late</p>");
            var blurred = WithReports("<p style='box-shadow: 2px 2px 3px gray'>First<br>Then <span style='float:right'>Late</span></p>");

            Assert.True(XOf(shadowed.Content, "Late") > XOf(shadowed.Content, "Then") + 200, "the late float in a shadowed paragraph did not move");
            Assert.Equal(1, GreyFills(control));
            Assert.Equal(1, GreyFills(shadowed.Content));
            Assert.DoesNotContain(shadowed.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal));
            Assert.True(XOf(blurred.Content, "Late") > XOf(blurred.Content, "Then") + 200, "the late float in a blur-shadowed paragraph did not move");
            Assert.Equal(1, blurred.Content.Split('\n').Count(l => l.Trim().EndsWith(" Do", System.StringComparison.Ordinal)));
            Assert.Equal(0, GreyFills(blurred.Content));
        }

        /// <summary>
        /// The transform moves with the box (batch 84): a float after a break in a transformed
        /// paragraph lifts, nothing is reported, and the one <c>cm</c> — the box's, the pieces carry
        /// none — is set before the border's stroke, the float and the pieces, and closed after them,
        /// so the whole turns as the unsplit paragraph did (one <c>cm</c> there too).
        /// </summary>
        [Fact]
        public void AFloatAfterABreakInATransformedParagraph_IsLiftedInsideTheTransform()
        {
            var turned = WithReports("<p style='transform: translate(10px, 0); border: 1px solid black'>First<br>Then <span style='float:right'>Late</span></p><p>After</p>");
            string control = Content("<p style='transform: translate(10px, 0); border: 1px solid black'>First<br>Then Late</p><p>After</p>");
            string[] lines = turned.Content.Split('\n').Select(l => l.Trim()).ToArray();

            Assert.True(XOf(turned.Content, "Late") > XOf(turned.Content, "Then") + 200, "the late float in a transformed paragraph did not move");
            Assert.DoesNotContain(turned.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal));
            int[] cms = lines.Select((l, i) => (l, i)).Where(p => p.l.EndsWith(" cm", System.StringComparison.Ordinal)).Select(p => p.i).ToArray();
            int cm = Assert.Single(cms);
            Assert.Equal(1, control.Split('\n').Count(l => l.TrimEnd().EndsWith(" cm", System.StringComparison.Ordinal)));
            Assert.Equal("q", lines[cm - 1]);
            int close = -1, depth = 1;   // the Q closing the q before the cm, past the pairs inside
            for (int i = cm + 1; i < lines.Length && close < 0; i++)
                if (lines[i] == "q") depth++; else if (lines[i] == "Q" && --depth == 0) close = i;
            Assert.True(close > cm, "the transform's q is never closed");
            int stroke = System.Array.FindIndex(lines, l => l is "S" or "s");
            int late = System.Array.FindIndex(lines, l => l.Contains("(Late)", System.StringComparison.Ordinal));
            int then = System.Array.FindIndex(lines, l => l.Contains("(Then", System.StringComparison.Ordinal));
            int after = System.Array.FindIndex(lines, l => l.Contains("(After)", System.StringComparison.Ordinal));
            Assert.True(cm < stroke && stroke < close, $"the border ({stroke}) must be stroked inside the transform ({cm}–{close})");
            Assert.True(cm < late && late < close && cm < then && then < close, $"the float ({late}) and the tail ({then}) must be inside the transform ({cm}–{close})");
            Assert.True(after > close, "the paragraph after the box is inside its transform");
            Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
        }

        /// <summary>Fills set in CSS <c>gray</c> (#808080, 0.502 a channel): the shadow's colour, and nothing else's here.</summary>
        private static int GreyFills(string content)
            => content.Split('\n').Count(l =>
            {
                string[] parts = l.Trim().Split(' ');
                return parts.Length == 4 && parts[3] == "rg"
                    && parts.Take(3).All(p => double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v > 0.49 && v < 0.51);
            });

        /// <summary>Stroke operators in the content: a border is drawn as strokes, one box as many as one border.</summary>
        private static int Strokes(string content) => content.Split('\n').Count(l => l.Trim() is "S" or "s");

        /// <summary>Fill operators in the content: a background is one filled rectangle.</summary>
        private static int Fills(string content) => content.Split('\n').Count(l => l.Trim() is "f" or "f*");

        /// <summary>
        /// A second float on the same side sits inward of the first, on the same line, not over it
        /// and not in the text: "Two" ends where "One" begins, both level with the line; a third
        /// follows inward again. Nothing is reported.
        /// </summary>
        [Fact]
        public void ASecondFloatOnTheSameSide_SitsInwardOfTheFirst()
        {
            var two = WithReports("<p>Text <span style='float:right'>One</span> <span style='float:right'>Two</span> <span style='float:right'>Three</span></p>");
            // "Two" is about 22 pt wide at the default size: it ends where "One" begins, give or take the slack.
            double twoWidth = RightAligned("One") - RightAligned("TwoOne");

            Assert.Equal(RightAligned("One") - Slack, XOf(two.Content, "One"), 2);
            Assert.Equal(XOf(two.Content, "One") - twoWidth - 2 * Slack, XOf(two.Content, "Two"), 1.5);
            Assert.True(XOf(two.Content, "Three") < XOf(two.Content, "Two") - 20, "the third float is not inward of the second");
            Assert.Equal(YOf(two.Content, "Text"), YOf(two.Content, "One"), 1);
            Assert.Equal(YOf(two.Content, "Text"), YOf(two.Content, "Two"), 1);
            Assert.Equal(YOf(two.Content, "Text"), YOf(two.Content, "Three"), 1);
            Assert.Equal(Margin(), XOf(two.Content, "Text"), 1);
            Assert.DoesNotContain(two.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal));
        }

        /// <summary>
        /// A left float beside a left float: the second starts where the first ends, and the text
        /// starts after both.
        /// </summary>
        [Fact]
        public void ASecondLeftFloat_SitsAfterTheFirst()
        {
            string c = Content("<p><span style='float:left'>One</span><span style='float:left'>Two</span>Text</p>");

            Assert.Equal(Margin(), XOf(c, "One"), 1);
            Assert.True(XOf(c, "Two") > XOf(c, "One") + 10 && XOf(c, "Two") < XOf(c, "One") + 40, $"the second left float is at {XOf(c, "Two"):F1}");
            Assert.True(XOf(c, "Text") > XOf(c, "Two") + 10, "the text does not start after both floats");
            Assert.Equal(YOf(c, "One"), YOf(c, "Text"), 1);
        }

        /// <summary>
        /// A lifted float keeps with its line: where the paragraph's orphans send it to the next
        /// page, the float goes with it instead of staying at the foot of this one.
        /// </summary>
        [Fact]
        public void ALiftedFloatGoesWhereItsLineGoes()
        {
            static string Html(double px, string orphans) =>
                "<p style='margin:0'>TOPX</p>" +
                $"<p style='margin:{px.ToString(System.Globalization.CultureInfo.InvariantCulture)}px 0 0 0'>PUSHX</p>" +
                $"<p style='{orphans}'>LINE1X <span style='float:right'>FLOATX</span><br>LINE2X<br>LINE3X</p>";

            // The push at which the unconstrained paragraph keeps one line on the push's page.
            double found = -1;
            for (double px = 600; px < 1100 && found < 0; px += 2)
            {
                string c = Content(Html(px, "orphans:1"));
                if (YOf(c, "PUSHX") < YOf(c, "TOPX") && YOf(c, "LINE1X") < YOf(c, "PUSHX") && YOf(c, "LINE2X") > YOf(c, "LINE1X"))
                    found = px;
            }
            Assert.True(found > 0, "no push left one line on the page; the test proves nothing");

            string moved = Content(Html(found, ""));
            Assert.True(YOf(moved, "LINE1X") > YOf(moved, "PUSHX"), "the paragraph did not move");
            Assert.Equal(YOf(moved, "LINE1X"), YOf(moved, "FLOATX"), 1);
        }

        /// <summary>A float inside a float travels with it.</summary>
        [Fact]
        public void AFloatInsideAFloat_TravelsWithIt()
        {
            var nested = WithReports("<p>Text <span style='float:right'>Out <span style='float:left'>In</span></span></p>");

            Assert.Equal(YOf(nested.Content, "Text"), YOf(nested.Content, "In"), 1);
            Assert.True(XOf(nested.Content, "Out") > XOf(nested.Content, "Text") + 100, "the outer float was not lifted");
            Assert.DoesNotContain(nested.Reports, r => r.StartsWith("float:", System.StringComparison.Ordinal));
        }
    }
}
