// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>&lt;dl&gt;</c> and <c>&lt;dd&gt;</c> with the UA stylesheet's margins, which a page's CSS
    /// replaces (HTML §15.3.7): a <c>dl</c> walked its children with no box and no margins, so
    /// Bootstrap's 20px under it was lost — the blocks after it sat 15pt high against Chrome — and a
    /// <c>dd</c> had a fixed 24pt left indent whatever the page said, on the wrong side in a
    /// right-to-left block.
    /// </summary>
    public class DefinitionListTests
    {
        private const string Css = "body{font-size:10pt} p{margin:0} dt{font-weight:normal}";

        private static string Content(string css, string body) =>
            DecodeStreams(ToPdf("<html><head><style>" + Css + css + "</style></head><body>" + body + "</body></html>"));

        private const string List = "<dl><dt>TTT</dt><dd>DDD</dd></dl><p>AFTER</p>";

        private static double Gap(string css)
        {
            string c = Content(css, List);
            return YOf(c, "DDD") - YOf(c, "AFTER");
        }

        /// <summary>A <c>dl</c> is 1em (10pt) above what follows it; with no margin, nothing (the control).</summary>
        [Fact]
        public void ADefinitionListHasItsUaMargin()
            => Assert.Equal(Gap("dl{margin:0}") + 10, Gap(""), 2);

        /// <summary>A page's margin replaces it: Bootstrap's 20px is 15pt.</summary>
        [Fact]
        public void APagesMarginReplacesIt()
            => Assert.Equal(Gap("dl{margin:0}") + 15, Gap("dl{margin-top:0;margin-bottom:20px}"), 2);

        /// <summary>A <c>dd</c> is 40px (30pt) in from its start — the left in a left-to-right block.</summary>
        [Fact]
        public void ADefinitionIsFortyPixelsInFromItsStart()
        {
            string c = Content("", List);
            Assert.Equal(XOf(c, "TTT") + 30, XOf(c, "DDD"), 2);
        }

        /// <summary>
        /// In a right-to-left block the start is the right: the definition's right edge is 30pt in
        /// from the term's.
        /// </summary>
        [Fact]
        public void ARightToLeftDefinitionIsInFromTheRight()
        {
            // An anagram, so the two words are one width and their left edges tell their right ones.
            string c = Content("", "<div dir='rtl'><dl><dt>ABC</dt><dd>CBA</dd></dl></div>");
            Assert.Equal(XOf(c, "ABC") - 30, XOf(c, "CBA"), 2);
        }

        /// <summary>A page's <c>dd { margin-left: 0 }</c> — Bootstrap's — takes the indent away.</summary>
        [Fact]
        public void APagesMarginRemovesTheIndent()
        {
            string c = Content("dd{margin-left:0}", List);
            Assert.Equal(XOf(c, "TTT"), XOf(c, "DDD"), 2);
        }
    }
}
