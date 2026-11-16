// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// The five managed-engine defects raised by the 2026 assembly scan (plan 10, MEDIUM).
    /// </summary>
    public class AssemblyScanMediumFixesTests
    {
        private const string Glyphs = "abcdefghijklmnopqrstuvwxyz0123456789 ";

        private static HtmlToPdfOptions Options(List<BuilderDiagnostic> sink = null)
        {
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => MinimalTrueType.Build(Glyphs);
            if (sink != null) o.OnDiagnostic = d => sink.Add(d);
            return o;
        }

        private static double XOfWord(string content, string word)
        {
            double x = XOf(content, word);
            Assert.False(double.IsNaN(x), $"'{word}' was never drawn");
            return x;
        }

        // =================================================================
        //  1. unicode-bidi reads the COMPUTED direction, which inherits
        // =================================================================

        /// <summary>
        /// <c>unicode-bidi:embed</c> on a span with no direction of its own takes the RTL direction
        /// it inherits, so its content is laid out right-to-left.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect.</b> CSS defines <c>unicode-bidi</c>'s embedding direction as the
        /// element's <i>computed</i> <c>direction</c>, which inherits. <c>ResolveBidiOverride</c>
        /// read only the element's own <c>dir</c> attribute and its own inline <c>direction</c>
        /// declaration — and the cascade fold copies stylesheet declarations onto matching elements,
        /// never onto their descendants — so an inherited RTL direction was invisible and the LTR
        /// control went out instead: U+202A rather than U+202B for <c>embed</c>, U+2066 rather than
        /// U+2067 for <c>isolate</c>.</para>
        /// <para><b>The content is MIXED-SCRIPT on purpose, and an earlier draft of this test that
        /// was not is why the note is here.</b> GAPS.md §4b: pure single-script content cannot detect
        /// a base-direction bug. Latin inside an RTL embedding still resolves to an even level and so
        /// still reads left to right — word order within the run is identical under U+202A and
        /// U+202B, and a Latin-only fixture passes with the fix deleted. What the embedding
        /// direction really decides is the order of the two <i>strong</i> runs relative to each
        /// other, so the fixture needs one run of each direction.</para>
        /// </remarks>
        [Fact]
        public void UnicodeBidiEmbed_UnderAnRtlAncestor_UsesTheInheritedDirection()
        {
            // LTR embedding (the old, wrong output): Latin first, Hebrew after it.
            // RTL embedding (correct):               Hebrew first, Latin after it.
            string embedded = DecodeStreams(ToPdf(
                "<div dir=\"rtl\"><span style=\"unicode-bidi:embed\">alpha " + Hebrew + "</span></div>",
                BidiOptions()));

            string control = DecodeStreams(ToPdf(
                "<div><span style=\"unicode-bidi:embed\">alpha " + Hebrew + "</span></div>",
                BidiOptions()));

            double controlLatin = XOfWord(control, "alpha");
            double controlHebrew = XOfHebrew(control);
            Assert.True(controlLatin < controlHebrew,
                "under an LTR embedding the Latin run comes first");

            double latin = XOfWord(embedded, "alpha");
            double hebrew = XOfHebrew(embedded);
            Assert.True(hebrew < latin,
                $"under an RTL embedding the Hebrew run must come first (latin={latin}, hebrew={hebrew})");
        }

        /// <summary>
        /// An explicit <c>dir</c> on the element still wins over what it inherits.
        /// </summary>
        /// <remarks>
        /// Without this the fix could have been "always use the inherited direction", which is the
        /// opposite error and would break every element-local override.
        /// </remarks>
        [Fact]
        public void UnicodeBidiEmbed_WithItsOwnLtrDir_OverridesTheRtlAncestor()
        {
            string content = DecodeStreams(ToPdf(
                "<div dir=\"rtl\"><span dir=\"ltr\" style=\"unicode-bidi:embed\">alpha " + Hebrew +
                "</span></div>", BidiOptions()));

            Assert.True(XOfWord(content, "alpha") < XOfHebrew(content),
                "an explicit dir=ltr must beat the inherited RTL direction");
        }

        /// <summary>A short Hebrew word — one strong RTL run for the mixed-script fixtures.</summary>
        private const string Hebrew = "שלום";

        private static HtmlToPdfOptions BidiOptions()
        {
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => MinimalTrueType.Build(Glyphs + Hebrew);
            return o;
        }

        /// <summary>
        /// The x of the Hebrew run. It is drawn as a glyph run rather than a literal string (the
        /// characters are outside WinAnsi), so it is located by position among the drawn runs rather
        /// than by text.
        /// </summary>
        private static double XOfHebrew(string content)
        {
            var runs = DrawnGlyphRuns(content);
            Assert.NotEmpty(runs);

            double latin = XOf(content, "alpha");
            // The Hebrew run is the drawn run that is not the Latin one.
            foreach (var (x, _, _) in runs)
                if (double.IsNaN(latin) || Math.Abs(x - latin) > 0.01) return x;

            Assert.Fail("no glyph run distinct from the Latin one was drawn");
            return double.NaN;
        }

        // =================================================================
        //  2. Truncation by node budget / depth cap is reported
        // =================================================================

        /// <summary>
        /// A document deeper than the mapper's nesting cap reports the truncation once.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect.</b> <c>MaxNodes</c> and <c>MaxDepth</c> were enforced by a bare
        /// <c>return</c>/<c>break</c> at six sites, so a document that exceeded either produced a
        /// partial PDF indistinguishable from a complete one: <c>OnDiagnostic</c> never fired and the
        /// return value was a well-formed PDF, so an automated pipeline would archive a truncated
        /// document as successful.</para>
        /// <para>The assertion is the guard's own evidence — the report naming the cap — and the
        /// second half asserts the content really was lost, so it cannot pass against an
        /// implementation that reports without truncating or truncates without reporting.</para>
        /// </remarks>
        [Fact]
        public void DepthCapExceeded_ReportsTheTruncationInsteadOfDroppingContentSilently()
        {
            var sink = new List<BuilderDiagnostic>();

            var html = new StringBuilder("<p>");
            for (int i = 0; i < 400; i++) html.Append("<span>");
            html.Append("buried");
            for (int i = 0; i < 400; i++) html.Append("</span>");
            html.Append("</p>");

            string content = DecodeStreams(ToPdf(html.ToString(), Options(sink)));

            Assert.DoesNotContain("buried", content, StringComparison.Ordinal);
            Assert.Contains(sink, d => d.Detail.IndexOf("nested deeper than", StringComparison.Ordinal) >= 0);
        }

        /// <summary>An ordinary document reports neither cap.</summary>
        [Fact]
        public void OrdinaryDocument_ReportsNoTruncation()
        {
            var sink = new List<BuilderDiagnostic>();
            ToPdf("<p>alpha</p><p>beta</p>", Options(sink));

            Assert.DoesNotContain(sink, d =>
                d.Detail.IndexOf("nested deeper than", StringComparison.Ordinal) >= 0 ||
                d.Detail.IndexOf("node mapping budget", StringComparison.Ordinal) >= 0);
        }

        // =================================================================
        //  3. Remote subresources are reported, not dropped in silence
        // =================================================================

        /// <summary>
        /// A remote image, a remote background image and a remote stylesheet each produce a
        /// diagnostic naming the resource.
        /// </summary>
        /// <remarks>
        /// The engine's "no network I/O" limit is a documented policy; dropping the resource is
        /// correct. Dropping it <i>silently</i> was the defect — a batch job could not tell a
        /// correctly-converted document from one that lost its branding and all its styling, and the
        /// <c>@font-face</c> path in this same assembly already reported the identical condition.
        /// </remarks>
        [Fact]
        public void RemoteSubresources_AreEachReportedAsDeclined()
        {
            var sink = new List<BuilderDiagnostic>();

            ToPdf("<link rel=\"stylesheet\" href=\"https://cdn.example/site.css\">" +
                  "<img src=\"https://cdn.example/logo.png\">" +
                  "<p style=\"background-image:url(https://cdn.example/bg.png)\">invoice</p>",
                  Options(sink));

            Assert.Contains(sink, d => d.Detail.IndexOf("The image", StringComparison.Ordinal) >= 0);
            Assert.Contains(sink, d => d.Detail.IndexOf("The background image", StringComparison.Ordinal) >= 0);
            Assert.Contains(sink, d => d.Detail.IndexOf("The stylesheet", StringComparison.Ordinal) >= 0);
        }

        /// <summary>A document with no external references reports nothing.</summary>
        [Fact]
        public void DocumentWithNoSubresources_ReportsNothingDeclined()
        {
            var sink = new List<BuilderDiagnostic>();
            ToPdf("<p>alpha</p>", Options(sink));

            Assert.DoesNotContain(sink, d => d.Detail.IndexOf("was not loaded", StringComparison.Ordinal) >= 0);
        }

        // =================================================================
        //  4. font-size percentages and keywords resolve
        // =================================================================

        /// <summary>
        /// <c>font-size:200%</c> doubles the parent size instead of leaving it unchanged.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect.</b> <c>ParseLength</c> maps <c>%</c> to its fallback, and the
        /// font-size call site passed the <i>current</i> size as that fallback — so every percentage
        /// resolved to "no change". Size keywords failed the numeric regex and took the same path.
        /// The very common <c>html{font-size:62.5%}</c> idiom therefore left every
        /// <c>rem</c>-derived size 1.6× wrong, with nothing reported.</para>
        /// <para>Asserted on the <c>Tf</c> operand in the content stream — the size the page really
        /// draws at — rather than on the intermediate model.</para>
        /// </remarks>
        [Theory]
        [InlineData("200%", 24)]
        [InlineData("50%", 6)]
        [InlineData("larger", 14.4)]
        [InlineData("smaller", 10)]
        [InlineData("x-large", 17.28)]
        public void FontSizePercentagesAndKeywords_Resolve(string declared, double expectedPt)
        {
            string content = DecodeStreams(ToPdf(
                "<p style=\"font-size:" + declared + "\">alpha</p>", Options()));

            Assert.Contains(FontSizes(content), s => Math.Abs(s - expectedPt) < 0.05);
        }

        /// <summary>An absolute length is untouched by the new keyword handling.</summary>
        [Fact]
        public void FontSizeInPoints_IsUnchanged()
        {
            string content = DecodeStreams(ToPdf("<p style=\"font-size:18pt\">alpha</p>", Options()));
            Assert.Contains(FontSizes(content), s => Math.Abs(s - 18) < 0.05);
        }

        /// <summary>Every <c>Tf</c> operand in a decoded content stream.</summary>
        private static List<double> FontSizes(string content)
        {
            var sizes = new List<double>();
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(content, @"/\w+\s+(-?[\d.]+)\s+Tf"))
                if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double v))
                    sizes.Add(v);
            return sizes;
        }

        // =================================================================
        //  5. border-<side>: none cancels a shorthand
        // =================================================================

        /// <summary>
        /// <c>border-top:none</c> beside <c>border:1px solid red</c> suppresses the top edge.
        /// </summary>
        /// <remarks>
        /// <para><b>The defect.</b> <c>ParseBorder</c> returned null both for "this declaration is
        /// absent" and for "this declaration explicitly says none", and the per-side call sites only
        /// assigned on non-null — so a per-side reset of a shorthand was a no-op. The extremely
        /// common table idiom <c>td{border:1px solid #ccc} td.first{border-left:none}</c> painted
        /// all four sides once the cascade fold put both declarations on one element.</para>
        /// <para>Asserted by counting the horizontal rules the exporter draws: a box with all four
        /// edges draws two horizontal lines, one with the top suppressed draws one. The control
        /// (all four sides) is asserted in the same test so the count means something.</para>
        /// </remarks>
        [Fact]
        public void BorderTopNone_CancelsTheShorthandInsteadOfBeingIgnored()
        {
            int allFour = HorizontalRules(
                "<div style=\"border:1px solid red\">alpha</div>");
            int topSuppressed = HorizontalRules(
                "<div style=\"border:1px solid red; border-top:none\">alpha</div>");

            Assert.True(allFour > topSuppressed,
                $"suppressing the top edge must draw fewer horizontal rules ({allFour} vs {topSuppressed})");
        }

        /// <summary>
        /// The three remaining edges are still drawn: the fix suppresses one side, not the border.
        /// </summary>
        [Fact]
        public void BorderTopNone_LeavesTheOtherThreeEdges()
        {
            int topSuppressed = HorizontalRules(
                "<div style=\"border:1px solid red; border-top:none\">alpha</div>");
            int noBorderAtAll = HorizontalRules("<div>alpha</div>");

            Assert.True(topSuppressed > noBorderAtAll,
                "the bottom edge must survive; only the named side is cancelled");
        }

        private static int HorizontalRules(string html)
        {
            var segments = StrokedSegments(DecodeStreams(ToPdf(html, Options())));
            return segments.Count(s => s.IsHorizontal && !s.IsVertical);
        }
    }
}
