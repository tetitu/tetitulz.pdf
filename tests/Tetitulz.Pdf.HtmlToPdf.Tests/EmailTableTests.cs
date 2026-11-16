// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// The shapes HTML e-mail is made of, found against Chrome: a 600px layout table inside
    /// <c>&lt;center&gt;</c>, an <c>align="center"</c> button table, a table with no width as wide as
    /// its content, and <c>border="1"</c> ruling every cell 1px in the table's border colour. Each
    /// placement is asserted against its own left-aligned control, so no test depends on the band.
    /// </summary>
    public class EmailTableTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        private const string Narrow = "<table width='200' cellspacing='0'{0}><tr><td>Cell</td></tr></table>";

        private static double CellX(string wrapperOpen, string tableAttrs = "", string wrapperClose = "")
            => XOf(Content(wrapperOpen + string.Format(CultureInfo.InvariantCulture, Narrow, tableAttrs) + wrapperClose), "Cell");

        [Fact]
        public void AnAlignedTableIsCentredOrAtTheRight()
        {
            double left = CellX("");
            double centre = CellX("", " align='center'");
            double right = CellX("", " align='right'");
            Assert.True(centre > left + 50, "centred table still at the left");
            // Centred has half the spare room on its left; at the right, all of it.
            Assert.Equal(right - left, 2 * (centre - left), 1);
            Assert.Equal(left, CellX("", " align='left'"), 1);
        }

        [Fact]
        public void AutoSideMarginsCentreItAndALeftOneAloneRightAlignsIt()
        {
            double left = CellX("");
            double centre = CellX("", " align='center'");
            double right = CellX("", " align='right'");
            Assert.Equal(centre, CellX("", " style='margin:0 auto'"), 1);
            Assert.Equal(centre, CellX("", " style='margin-left:auto;margin-right:auto'"), 1);
            Assert.Equal(right, CellX("", " style='margin-left:auto'"), 1);
            // The control: a fixed margin is not auto.
            Assert.Equal(left, CellX("", " style='margin:0 0'"), 1);
        }

        [Fact]
        public void ACenterElementCentresTheTableInsideIt()
        {
            double left = CellX("");
            double centre = CellX("", " align='center'");
            Assert.True(centre > left + 50);
            Assert.Equal(centre, CellX("<center>", "", "</center>"), 1);
            Assert.Equal(centre, CellX("<div align='center'>", "", "</div>"), 1);
            Assert.Equal(centre, CellX("<div style='text-align:-webkit-center'>", "", "</div>"), 1);
        }

        /// <summary>A table's own <c>align="left"</c> wins over the <c>&lt;center&gt;</c> round it.</summary>
        [Fact]
        public void ATablesOwnLeftAlignWinsOverItsCenter()
        {
            double left = CellX("");
            Assert.Equal(left, CellX("<center>", " align='left'", "</center>"), 1);
            // The control: without its own align the same table is centred.
            Assert.True(CellX("<center>", "", "</center>") > left + 50);
        }

        /// <summary>
        /// The nearest ancestor that aligns anything decides: an inner CSS <c>text-align: left</c>
        /// stops the search before the outer <c>align="center"</c>, as it overrides the inherited
        /// <c>-webkit-center</c> in a browser.
        /// </summary>
        [Fact]
        public void TheNearestAligningAncestorDecides()
        {
            double left = CellX("");
            Assert.Equal(left, CellX("<div align='center'><div style='text-align:left'>", "", "</div></div>"), 1);
            // The control: with no inner text-align the outer align centres it.
            Assert.True(CellX("<div align='center'><div>", "", "</div></div>") > left + 50);
        }

        /// <summary><c>text-align: -webkit-center</c> centres text, as <c>center</c> does.</summary>
        [Fact]
        public void WebkitCenterCentresText()
        {
            string c = Content("<div style='text-align:-webkit-center'>Mid1</div><div style='text-align:center'>Mid2</div><div>Lft</div>");
            Assert.Equal(XOf(c, "Mid2"), XOf(c, "Mid1"), 1);
            Assert.True(XOf(c, "Mid1") > XOf(c, "Lft") + 50);
        }

        /// <summary>CSS <c>text-align: center</c> centres an ancestor's text, not its tables.</summary>
        [Fact]
        public void ACssTextAlignCentresTextButNotATable()
        {
            double left = CellX("");
            Assert.Equal(left, CellX("<div style='text-align:center'>", "", "</div>"), 1);
            // The control: the same ancestor does centre its own text.
            string c = Content("<div style='text-align:center'>Mid</div><div>Lft</div>");
            Assert.True(XOf(c, "Mid") > XOf(c, "Lft") + 50);
        }

        /// <summary>
        /// A <c>&lt;center&gt;</c> is a block: what precedes it ends its line, and its text is centred
        /// like a <c>text-align: center</c> block's. Walked as inline it flattened a whole message into
        /// one line.
        /// </summary>
        [Fact]
        public void ACenterElementIsABlockWhoseTextIsCentred()
        {
            // Mid1 and Mid2 are one width (digits share an advance), so centred they start together.
            string c = Content("Before<center>Mid1</center><div style='text-align:center'>Mid2</div>");
            Assert.True(YOf(c, "Mid1") < YOf(c, "Before") - 1, "the center element shared the line before it");
            Assert.True(XOf(c, "Mid1") > XOf(c, "Before") + 50, "its text is not centred");
            Assert.Equal(XOf(c, "Mid2"), XOf(c, "Mid1"), 1);
        }

        /// <summary>A table with no width is as wide as its content; a stated <c>width: 100%</c> (the control) fills the band.</summary>
        [Fact]
        public void ATableWithNoWidthIsAsWideAsItsContent()
        {
            static double FillWidth(string style)
            {
                string c = Content("<table cellspacing='0'" + style + "><tr><td style='background:#0000ff;padding:0'>Word</td></tr></table>");
                var m = Regex.Match(c, @"[-\d.]+ [-\d.]+ ([-\d.]+) [-\d.]+ re\s+f");
                Assert.True(m.Success, "no fill");
                return double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            double full = FillWidth(" style='width:100%'");
            double own = FillWidth("");
            Assert.True(full > 300, "the control did not fill the band");
            Assert.InRange(own, 20, 45);
        }

        private static double[] LineWidths(string content)
            => Regex.Matches(content, @"(?<![\w.])([\d.]+) w\b")
                .Select(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).Distinct().ToArray();

        /// <summary><c>border="4"</c> rules each cell 1px (0.75pt), not 4pt; <c>border="0"</c> (the control) rules nothing.</summary>
        [Fact]
        public void ABorderAttributeRulesEveryCellOnePixel()
        {
            string ruled = Content("<table border='4' cellspacing='0'><tr><td>A</td></tr></table>");
            Assert.Contains(0.75, LineWidths(ruled));
            Assert.DoesNotContain(4.0, LineWidths(ruled));
            Assert.NotEmpty(StrokedSegments(ruled));
            Assert.Empty(StrokedSegments(Content("<table border='0' cellspacing='0'><tr><td>A</td></tr></table>")));
        }

        /// <summary>The rules are the table's <c>border-color</c>, grey when it sets none — never black.</summary>
        [Fact]
        public void ABorderAttributesRulesTakeTheTablesBorderColour()
        {
            string grey = Content("<table border='1' cellspacing='0'><tr><td>A</td></tr></table>");
            string red = Content("<table border='1' cellspacing='0' style='border-color:#ff0000'><tr><td>A</td></tr></table>");
            Assert.Matches(@"0\.50196\d* 0\.50196\d* 0\.50196\d* RG", grey);
            Assert.DoesNotMatch(@"(?<![\d.])0 0 0 RG", grey);
            Assert.Matches(@"(?<![\d.])1 0 0 RG", red);
        }
    }
}
