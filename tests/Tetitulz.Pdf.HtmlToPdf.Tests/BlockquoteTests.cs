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
    /// A <c>&lt;blockquote&gt;</c> is laid out as a browser lays it out: the UA stylesheet's margins —
    /// 1em above and below, 40px each side — and whatever its CSS draws, and nothing more. It had a
    /// house style: a grey bar on the left and fixed indents, so a right-to-left page's bar (on the
    /// right, in Bootstrap-RTL) was never drawn and one appeared where CSS had none.
    /// </summary>
    public class BlockquoteTests
    {
        private static string Content(string body, string dir = "ltr") =>
            DecodeStreams(ToPdf($"<html dir='{dir}'><body>{body}</body></html>"));

        /// <summary>The left edge of a paragraph's text: where a plain paragraph starts.</summary>
        private static double Margin() => XOf(Content("<p>M</p>"), "M");

        /// <summary>A plain blockquote draws nothing but its text — no bar — 40px (30pt) in from the margin.</summary>
        [Fact]
        public void APlainBlockquoteIsIndentedAndDrawsNoBar()
        {
            string c = Content("<blockquote>Q</blockquote>");
            Assert.Empty(StrokedSegments(c));
            Assert.Equal(Margin() + 30, XOf(c, "Q"), 1);
        }

        /// <summary>A blockquote of paragraphs is indented the same, its box round them.</summary>
        [Fact]
        public void ABlockquoteOfParagraphsIsIndentedTheSame()
            => Assert.Equal(Margin() + 30, XOf(Content("<blockquote><p>Q</p></blockquote>"), "Q"), 1);

        /// <summary>A margin the page sets replaces the default indent.</summary>
        [Fact]
        public void ADeclaredMarginReplacesTheIndent()
            => Assert.Equal(Margin(), XOf(Content("<blockquote style='margin:0'>Q</blockquote>"), "Q"), 1);

        /// <summary>
        /// Bootstrap-RTL's bar is on the right: a right-to-left blockquote with <c>border-right</c>
        /// draws one vertical rule, to the right of its text.
        /// </summary>
        [Fact]
        public void ARightToLeftBarIsOnTheRight()
        {
            string c = Content("<blockquote style='margin:0;padding:0 20px;border-right:5px solid #eee'>Q</blockquote>", "rtl");
            var verticals = StrokedSegments(c).Where(s => s.IsVertical).ToList();
            Assert.Single(verticals);
            Assert.True(verticals[0].X0 > XOf(c, "Q"), "the bar is not to the right of the text");
        }

        /// <summary>A blockquote is 1em from its neighbours, as a paragraph is; a div is not (the control).</summary>
        [Fact]
        public void ABlockquoteIsOneEmFromItsNeighbours()
        {
            static double Gap(string c) => YOf(c, "A") - YOf(c, "B");
            double none = Gap(Content("<div>A</div><div>B</div>"));
            Assert.Equal(none + 12, Gap(Content("<div>A</div><blockquote>B</blockquote>")), 1);
        }

        /// <summary>A blockquote of paragraphs takes the 1em too, on its box: its paragraph's own margin is 0.</summary>
        [Fact]
        public void ABlockquoteOfParagraphsIsOneEmFromItsNeighbours()
        {
            static double Gap(string c) => YOf(c, "A") - YOf(c, "B");
            double none = Gap(Content("<div>A</div><div>B</div>"));
            Assert.Equal(none + 12, Gap(Content("<div>A</div><blockquote><p style='margin:0'>B</p></blockquote>")), 1);
        }
    }
}
