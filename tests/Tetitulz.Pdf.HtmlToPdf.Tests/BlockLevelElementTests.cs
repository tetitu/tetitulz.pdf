// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// What CSS makes a block is laid out as one: a <c>&lt;span&gt;</c> or <c>&lt;label&gt;</c> with
    /// a block <c>display</c>, a float among blocks (a form row's label column), and a text control
    /// its stylesheet makes a block (Bootstrap's <c>.form-control</c>) — found by rendering a
    /// Bootstrap 3 horizontal form beside Chrome, where the label sat above a content-wide input.
    /// </summary>
    public class BlockLevelElementTests
    {
        private static string Content(string body) => DecodeStreams(ToPdf("<html><body>" + body + "</body></html>"));

        // ------------------------------------------------------------------ display and float

        /// <summary>
        /// A <c>display: block</c> span is a line of its own: the text round it goes above and below
        /// it. A plain span stays in the line (the control).
        /// </summary>
        [Fact]
        public void ABlockSpanIsALineOfItsOwn()
        {
            string block = Content("<div>A<span style='display:block'>B</span>C</div>");
            string inline = Content("<div>A<span>B</span>C</div>");

            Assert.True(YOf(block, "A") > YOf(block, "B") && YOf(block, "B") > YOf(block, "C"),
                "a block span did not start and end a line");
            Assert.False(double.IsNaN(YOf(inline, "A")) || double.IsNaN(YOf(inline, "B")), "the inline control drew nothing");
            Assert.Equal(YOf(inline, "A"), YOf(inline, "B"), 1);
        }

        /// <summary>
        /// A floated label beside a floated block is a column of the row, level with it — the form
        /// row of a horizontal form. It sat above the input as a line of text.
        /// </summary>
        [Fact]
        public void AFloatedLabelAmongBlocksIsAColumn()
        {
            string c = Content("<div><label style='float:left;width:25%'>Label</label>" +
                               "<div style='float:left;width:75%'>Value</div></div>");

            Assert.Equal(YOf(c, "Label"), YOf(c, "Value"), 1);
            Assert.True(XOf(c, "Value") > XOf(c, "Label") + 100, "the value is not in the column beside the label");
        }

        // ------------------------------------------------------------------ block form controls

        /// <summary>
        /// A text input its CSS makes a block fills its container: its bordered box is as wide as the
        /// page's content, where an inline one is only as wide as its text (the control).
        /// </summary>
        [Fact]
        public void ABlockInputFillsItsContainer()
        {
            static double Widest(string c) =>
                StrokedSegments(c).Where(s => s.IsHorizontal).Select(s => Math.Abs(s.X1 - s.X0)).DefaultIfEmpty(0).Max();

            string block = Content("<input value='x' style='display:block;border:1px solid #000'>");
            string inline = Content("<input value='x' style='border:1px solid #000'>");

            Assert.True(Widest(block) > 500, $"the block input's box is {Widest(block)}pt wide");
            Assert.True(Widest(inline) < 100, $"the inline input's box is {Widest(inline)}pt wide");
        }

        /// <summary>
        /// A block textarea is at least its <c>rows</c> lines tall: four rows of 15pt put the next
        /// block 60pt below the first line; one row, 15.
        /// </summary>
        [Theory]
        [InlineData(4, 60)]
        [InlineData(1, 15)]
        public void ABlockTextareaIsItsRowsTall(int rows, double gap)
        {
            string c = Content($"<textarea rows='{rows}' style='display:block;padding:0;border:0;margin:0;line-height:15pt'>A</textarea><div style='line-height:15pt'>B</div>");
            Assert.Equal(gap, YOf(c, "A") - YOf(c, "B"), 1);
        }

        /// <summary>A block textarea keeps its line breaks: each line of its text is a line of the box.</summary>
        [Fact]
        public void ABlockTextareaKeepsItsLines()
        {
            string c = Content("<textarea style='display:block;line-height:15pt'>A\nB</textarea>");
            Assert.Equal(15, YOf(c, "A") - YOf(c, "B"), 1);
        }

        /// <summary>
        /// A block password input shows bullets, never its value, as the inline one does; a text
        /// input shows its value (the control, so the password's absence is the mask's doing).
        /// </summary>
        [Fact]
        public void ABlockPasswordShowsNoValue()
        {
            string password = Content("<input type='password' value='secret' style='display:block'>");
            string text = Content("<input type='text' value='secret' style='display:block'>");
            Assert.DoesNotContain("secret", password, StringComparison.Ordinal);
            Assert.False(double.IsNaN(XOf(text, "secret")), "the text input did not show its value");
        }

        /// <summary>A block select shows its selected option, and only it.</summary>
        [Fact]
        public void ABlockSelectShowsItsSelectedOption()
        {
            string c = Content("<select style='display:block'><option>One</option><option selected>Two</option></select>");
            Assert.False(double.IsNaN(XOf(c, "Two")), "the selected option is not shown");
            Assert.True(double.IsNaN(XOf(c, "One")), "an unselected option was drawn");
        }

        /// <summary>
        /// A float in running text stays in its line — the inline float lift places it level with
        /// the text round it — however its container is walked: a block among inline siblings would
        /// fall below the line.
        /// </summary>
        [Fact]
        public void AFloatInRunningTextStaysInItsLine()
        {
            string c = Content("<div>Text <span style='float:right'>Badge</span> more</div>");
            Assert.Equal(YOf(c, "Text"), YOf(c, "Badge"), 1);
        }

        /// <summary>
        /// A block control inside a column is still a block — Bootstrap's input inside its
        /// <c>.col-*</c> div: the div's only child makes it a container of blocks.
        /// </summary>
        [Fact]
        public void ABlockInputInsideADivFillsIt()
        {
            string c = Content("<div><input value='x' style='display:block;border:1px solid #000'></div>");
            double widest = StrokedSegments(c).Where(s => s.IsHorizontal).Select(s => Math.Abs(s.X1 - s.X0)).DefaultIfEmpty(0).Max();
            Assert.True(widest > 500, $"the nested block input's box is {widest}pt wide");
        }

        /// <summary>
        /// In an interactive conversion a block control is still a form field, placed where its
        /// placeholder is — the static block path is for static output only.
        /// </summary>
        [Fact]
        public void AnInteractiveBlockInputIsStillAField()
        {
            var result = HtmlToPdfConverter.ConvertHtmlWithFormFields(
                "<html><body><input name='n' value='x' style='display:block'></body></html>",
                new HtmlToPdfOptions { RenderEngine = HtmlRenderEngine.Managed });
            Assert.Single(result.FormFields);
        }

        /// <summary>
        /// Bootstrap's horizontal form row as it is written: in a right-to-left page, a padded
        /// inline-block label and a column, both floated right with percentage widths and
        /// <c>position: relative</c>. The label is the row's
        /// right-hand column, level with the value — it sat above it.
        /// </summary>
        [Fact]
        public void ABootstrapFormRowPutsItsLabelBesideTheValue()
        {
            // Laid out as a page writes it, with the whitespace between the tags.
            string c = DecodeStreams(ToPdf("<html dir='rtl'><body><div>\n  " +
                "<label style='display:inline-block;max-width:100%;float:right;width:25%;padding:7px 15px 0;position:relative;min-height:1px'>Label</label>\n  " +
                "<div style='float:right;width:75%;padding:0 15px;position:relative;min-height:1px'>" +
                "<input value='Value' style='display:block;width:100%'></div>\n</div></body></html>"));

            Assert.True(Math.Abs(YOf(c, "Label") - YOf(c, "Value")) < 8, "the label is not beside the value");
            Assert.True(XOf(c, "Label") > XOf(c, "Value") + 100, "the label is not the right-hand column");
        }

        /// <summary>
        /// A float in running text beside a block sibling stays in its line: text, a floated badge,
        /// then a block — the badge is level with the text, as the inline float lift places it,
        /// not a block of its own below the text.
        /// </summary>
        [Fact]
        public void AFloatInRunningTextBesideABlockStaysInItsLine()
        {
            string c = Content("<div>Text <span style='float:right'>Badge</span><div>Block</div></div>");
            Assert.Equal(YOf(c, "Text"), YOf(c, "Badge"), 1);
            Assert.True(YOf(c, "Block") < YOf(c, "Text"), "the block is not below the text");
        }

        /// <summary>The same with the float before its text: it starts the text's line, level with it.</summary>
        [Fact]
        public void AFloatBeforeRunningTextBesideABlockStaysInItsLine()
        {
            string c = Content("<div><span style='float:right'>Badge</span> Text<div>Block</div></div>");
            Assert.Equal(YOf(c, "Text"), YOf(c, "Badge"), 1);
        }
    }
}
