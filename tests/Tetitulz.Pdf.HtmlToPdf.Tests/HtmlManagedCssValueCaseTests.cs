// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Two declaration-level defects the 2026 assembly scan raised as LOW, plus the entry-point
    /// asymmetry in <see cref="HtmlToPdfOptions.BasePath"/>'s contract.
    ///
    /// <para>Both CSS ones share a shape worth naming: the engine normalises the property
    /// <b>name</b> and then compares the <b>value</b> without normalising it, or it sets a flag and
    /// never provides a way to clear it. A half-normalised declaration and a one-way flag both look
    /// correct in the code that writes them; only the reset case shows the hole.</para>
    /// </summary>
    public class HtmlManagedCssValueCaseTests
    {
        /// <summary>
        /// Underline and strike are drawn as <b>filled rectangles</b>, not strokes
        /// (<c>PdfExporter.FillRule</c>) — a stroke count sees none of them and passes against every
        /// implementation.
        /// </summary>
        private static int RuleFillCount(string content) =>
            Regex.Matches(content, @"[-\d.]+\s+[-\d.]+\s+[-\d.]+\s+[-\d.]+\s+re\s*\n?f").Count;

        /// <summary>
        /// Whitespace-only drawn pieces of at least <paramref name="minSpaces"/> characters. Layout
        /// emits a space run as its own <c>Tj</c>, so "did the spaces collapse?" is a question about
        /// the pieces, not about one string.
        /// </summary>
        private static int SpaceRuns(string content, int minSpaces) =>
            DrawnTexts(content).Count(d => d.Text.Length >= minSpaces && d.Text.Trim().Length == 0);

        // ------------------------------------------------------ keyword values are case-insensitive

        /// <summary>
        /// CSS keyword values are ASCII case-insensitive. <c>DeclarationKey</c> lower-cases the
        /// property name, so <c>TEXT-DECORATION</c> reached the switch — and then
        /// <c>string.Contains</c>, which is ordinal, matched nothing.
        /// </summary>
        [Theory]
        [InlineData("UNDERLINE")]
        [InlineData("Underline")]
        [InlineData("underline")]
        public void TextDecorationUnderline_IsMatchedWhateverItsCase(string value)
        {
            string plain = DecodeStreams(ToPdf("<p>Hi</p>"));
            string dec = DecodeStreams(ToPdf($"<p style=\"text-decoration:{value}\">Hi</p>"));

            Assert.Equal(0, RuleFillCount(plain));      // the control draws no rule at all
            Assert.True(RuleFillCount(dec) > 0, "the underline must be drawn");
        }

        [Theory]
        [InlineData("LINE-THROUGH")]
        [InlineData("line-through")]
        public void TextDecorationLineThrough_IsMatchedWhateverItsCase(string value)
        {
            Assert.True(RuleFillCount(DecodeStreams(ToPdf($"<p style=\"text-decoration:{value}\">Hi</p>"))) > 0);
        }

        /// <summary>
        /// The case the finding called out as the worst of them: an upper-case <c>NONE</c> failed to
        /// clear the browser-default link underline, so the link stayed underlined against the
        /// author's explicit instruction — a wrong result rather than a missing one.
        /// </summary>
        [Fact]
        public void UpperCaseTextDecorationNone_ClearsTheLinkUnderline()
        {
            string underlined = DecodeStreams(ToPdf("<p><a href=\"https://x.example\">Hi</a></p>"));
            string cleared = DecodeStreams(ToPdf(
                "<p><a href=\"https://x.example\" style=\"TEXT-DECORATION: NONE\">Hi</a></p>"));

            Assert.True(RuleFillCount(underlined) > 0, "the UA default underlines a link");
            Assert.Equal(0, RuleFillCount(cleared));
        }

        /// <summary>
        /// <c>font-variant</c> takes the same treatment. Small caps are observable because the
        /// exporter upper-cases the token (<c>ApplyCaps</c>), so the drawn text differs from the
        /// source text — which is also why the control has no <c>text-transform</c>, the other
        /// property that would produce the same glyphs.
        /// </summary>
        [Theory]
        [InlineData("SMALL-CAPS")]
        [InlineData("small-caps")]
        public void FontVariantSmallCaps_IsMatchedWhateverItsCase(string value)
        {
            string plain = DecodeStreams(ToPdf("<p>Hi there</p>"));
            string dec = DecodeStreams(ToPdf($"<p style=\"font-variant:{value}\">Hi there</p>"));

            Assert.Contains("(there)", plain);          // the control draws it as authored
            Assert.Contains("(THERE)", dec);
        }

        /// <summary>
        /// The reset arm the property never had. Without it a stylesheet could turn small caps on
        /// and nothing downstream could turn them off — the same one-way-flag shape as
        /// <c>white-space</c> below.
        /// </summary>
        [Fact]
        public void FontVariantNormal_TurnsSmallCapsBackOff()
        {
            string dec = DecodeStreams(ToPdf(
                "<div style=\"font-variant:small-caps\"><p style=\"font-variant:normal\">Hi there</p></div>"));

            Assert.Contains("(there)", dec);
        }

        // ------------------------------------------------------ white-space can be switched back off

        /// <summary>
        /// <c>Style.Pre</c> inherits through <c>DeriveStyle</c>'s <c>MemberwiseClone</c> and
        /// <c>MapBlock</c> forces it on for <c>&lt;pre&gt;</c>, while the <c>white-space</c> arm only
        /// ever <i>set</i> it — so once any ancestor turned preformatting on, no descendant could
        /// turn it off.
        /// </summary>
        [Fact]
        public void WhiteSpaceNormal_TurnsPreformattingBackOff()
        {
            string inheritedPre = DecodeStreams(ToPdf("<pre>fixed  text<span>   kept   here</span></pre>"));
            string switchedOff = DecodeStreams(ToPdf(
                "<pre>fixed  text<span style=\"white-space:normal\">   collapsed   here</span></pre>"));

            Assert.Contains("(collapsed)", switchedOff);
            // Three-space runs come only from the span; the <pre> prefix contributes two, so this
            // threshold isolates the part under test from the part that must NOT change.
            Assert.True(SpaceRuns(inheritedPre, 3) > 0,
                "the control must keep the span's space runs — otherwise nothing is being switched off");
            Assert.Equal(0, SpaceRuns(switchedOff, 3));
        }

        /// <summary>
        /// <c>nowrap</c> collapses whitespace exactly as <c>normal</c> does; only the wrapping
        /// differs, and this engine has no line-box model to express that in. Mapping it onto
        /// <c>pre</c> would keep spaces CSS requires it to collapse.
        /// </summary>
        [Fact]
        public void WhiteSpaceNowrap_AlsoCollapsesSpaces()
        {
            string dec = DecodeStreams(ToPdf(
                "<pre>fixed  text<span style=\"white-space:nowrap\">   collapsed   here</span></pre>"));

            Assert.Contains("(collapsed)", dec);
            Assert.Equal(0, SpaceRuns(dec, 3));
        }

        /// <summary>
        /// <c>pre-line</c> is "keep the newlines, collapse the spaces" — it was mapped onto full
        /// <c>pre</c> and kept both. Asserting both halves is what separates it from <c>normal</c>
        /// (which would drop the break) and from <c>pre</c> (which would keep the spaces); either
        /// assertion alone passes against one of the two wrong answers.
        /// </summary>
        [Fact]
        public void WhiteSpacePreLine_KeepsNewlinesAndCollapsesSpaces()
        {
            string dec = DecodeStreams(ToPdf(
                "<div style=\"white-space:pre-line\">alpha    beta\ngamma</div>"));

            Assert.Equal(0, SpaceRuns(dec, 2));                    // the spaces collapsed…
            double alpha = YOf(dec, "alpha"), gamma = YOf(dec, "gamma");
            Assert.False(double.IsNaN(alpha) || double.IsNaN(gamma), "both words must be drawn");
            Assert.True(gamma < alpha, "…and the newline survived as a line break");
        }

        /// <summary>
        /// The control for the one above: under <c>normal</c> the newline is whitespace like any
        /// other, so both words share a baseline. Without this, "gamma is lower" would also pass
        /// against an implementation that simply wrapped the line.
        /// </summary>
        [Fact]
        public void WhiteSpaceNormal_DropsTheNewline()
        {
            string dec = DecodeStreams(ToPdf(
                "<div style=\"white-space:normal\">alpha    beta\ngamma</div>"));

            Assert.Equal(YOf(dec, "alpha"), YOf(dec, "gamma"), 2);
        }

        /// <summary>
        /// The regression counterpart: plain <c>&lt;pre&gt;</c> must still preserve its spaces. A
        /// reset arm written one keyword too wide would silently turn every preformatted block into
        /// flowing text.
        /// </summary>
        [Fact]
        public void PlainPre_StillPreservesItsSpaces()
        {
            Assert.True(SpaceRuns(DecodeStreams(ToPdf("<pre>fixed    text</pre>")), 4) > 0);
        }

        // ------------------------------------------------------ BasePath's documented contract

        /// <summary>
        /// <see cref="HtmlToPdfOptions.BasePath"/>'s doc used to say a <c>null</c> value meant the
        /// managed engine "does no file I/O". That is true for <c>ConvertHtml*</c> and false for
        /// <c>ConvertFile*</c>, which supplies the input file's own directory as an implicit base —
        /// so a caller who read the doc and left it null still got local subresources embedded.
        ///
        /// <para>The doc now states both halves. This test is what keeps the two from drifting apart
        /// again: it asserts the behaviour the amended wording describes, from both entry points, in
        /// one place.</para>
        /// </summary>
        [Fact]
        public void NullBasePath_ReadsLocalImagesFromConvertFileButNotFromConvertHtml()
        {
            using var dir = NewScratchDir();
            dir.Write("logo.png", MakeRgbPng(6, 6));
            const string Html = "<img src=\"logo.png\" width=\"20\"><p>Invoice</p>";
            string htmlPath = dir.Write("doc.html", Html);

            byte[] viaFile = HtmlToPdfConverter.ConvertFileToBytes(htmlPath, Managed());
            byte[] viaString = HtmlToPdfConverter.ConvertHtmlToBytes(Html, Managed());

            Assert.Contains("/Width 6", Raw(viaFile));
            Assert.DoesNotContain("/Subtype /Image", Raw(viaString));
        }

        /// <summary>
        /// The security property the amended doc still promises: traversal <i>above</i> the implicit
        /// base is refused, so the implicit base widens the reachable set to the input file's own
        /// directory and no further.
        /// </summary>
        [Fact]
        public void ImplicitBasePath_StillRefusesTraversalAboveTheInputDirectory()
        {
            using var dir = NewScratchDir();
            File.WriteAllBytes(Path.Combine(dir.Path, "outside.png"), MakeRgbPng(9, 9));
            string sub = Path.Combine(dir.Path, "docs");
            Directory.CreateDirectory(sub);
            string htmlPath = Path.Combine(sub, "doc.html");
            File.WriteAllText(htmlPath, "<img src=\"../outside.png\" width=\"20\"><p>Invoice</p>");

            byte[] pdf = HtmlToPdfConverter.ConvertFileToBytes(htmlPath, Managed());

            Assert.Contains("Invoice", DecodeStreams(pdf));
            Assert.DoesNotContain("/Width 9", Raw(pdf));
        }
    }
}
