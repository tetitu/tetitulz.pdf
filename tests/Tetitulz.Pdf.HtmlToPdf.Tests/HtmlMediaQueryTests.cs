// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.HtmlToPdf;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Media query lists evaluated for print, against Media Queries Level 4 §2–3
    /// (w3.org/TR/mediaqueries-4, read for this — not recalled).
    /// </summary>
    /// <remarks>
    /// <para><b>Keywords and media types are exact. Without a viewport (the first theory) no media
    /// feature is evaluated</b>; with one (the second) width, height and orientation are. An
    /// unevaluated feature is <i>unknown</i>, and §3.1's three-valued logic lets a known part decide: that is why
    /// <c>screen and (color)</c> is false and <c>not screen and (color)</c> true below. A result
    /// still unknown at the end applies — the engine's standing approximation for feature-only
    /// queries, where the spec would say false.</para>
    ///
    /// <para>Several rows are the spec's own examples: <c>unknown</c> is false and <c>not unknown</c>
    /// true (§3.2), <c>or and (color)</c> is <c>not all</c> (§3.2), and <c>&amp;test, speech</c>
    /// matches only speech (§3.2).</para>
    /// </remarks>
    public class HtmlMediaQueryTests
    {
        [Theory]
        // §2.1: an empty list is true; a list is true if any query is.
        [InlineData("", true)]
        [InlineData("   ", true)]
        [InlineData("screen, print", true)]
        [InlineData("screen, speech", false)]
        [InlineData("print,", true)]                          // the empty entry is `not all`
        [InlineData("not screen and (width, height)", true)]  // a comma inside parentheses does not split
        // §2.3: all and print match; screen, deprecated and unknown types match nothing.
        [InlineData("all", true)]
        [InlineData("print", true)]
        [InlineData("PRINT", true)]                           // §3: ASCII case-insensitive
        [InlineData("screen", false)]
        [InlineData("speech", false)]
        [InlineData("tv", false)]
        [InlineData("unknown", false)]
        [InlineData("&test, speech", false)]
        // §2.2: not negates the whole query; only changes nothing.
        [InlineData("not print", false)]
        [InlineData("not screen", true)]
        [InlineData("not unknown", true)]
        [InlineData("only print", true)]
        [InlineData("only screen", false)]
        // §3.1: a known false decides an `and` whatever the unknown feature is.
        [InlineData("screen and (color)", false)]
        [InlineData("not screen and (color)", true)]
        [InlineData("not all and (color)", true)]              // not (true AND unknown) = unknown
        [InlineData("print and (orientation: landscape)", true)]
        [InlineData("(min-width: 600px)", true)]
        [InlineData("not (min-width: 600px)", true)]
        [InlineData("(color) and (monochrome)", true)]
        [InlineData("(color) or (monochrome)", true)]
        [InlineData("screen and (min-width: 1px), print", true)]
        // §3 / §3.2: a grammar mismatch is `not all`.
        [InlineData("or and (color)", false)]
        [InlineData("only", false)]
        [InlineData("not", false)]
        [InlineData("print and", false)]
        [InlineData("print (color)", false)]
        [InlineData("print or (color)", false)]
        [InlineData("not only print", false)]
        [InlineData("not and", false)]                        // §3: and/or are not media types, so
        [InlineData("not or", false)]                         // this is `not all`, not a negated type
        [InlineData("print and (color) or (monochrome)", false)]   // no `or` after a media type
        [InlineData("(color) and (monochrome) or (hover)", false)] // and/or cannot mix
        [InlineData("not (color) and (hover)", false)]
        [InlineData("print and (color", false)]                    // unclosed
        public void AMediaQueryListIsEvaluatedForPrint(string list, bool applies)
            => Assert.Equal(applies, HtmlToBuilderMapper.MediaApplies(list));

        /// <summary>
        /// A stray <c>)</c> in one query does not join the rest of the list to it. The list's own
        /// splitter let its depth go below zero there, and no comma after it split the list, so the
        /// <c>print</c> query was never evaluated (the dedup pass of 2026-10-21 gave the list the
        /// one top-level splitter, which passes over a <c>)</c> with nothing open). The control is
        /// the same list without its <c>print</c> query.
        /// </summary>
        [Fact]
        public void AStrayClosingParenDoesNotSwallowTheQueriesAfterIt()
        {
            Assert.True(HtmlToBuilderMapper.MediaApplies("screen and (min-width: 1px)), print"));
            Assert.False(HtmlToBuilderMapper.MediaApplies("screen and (min-width: 1px))"));
        }

        /// <summary>
        /// Width, height and orientation evaluated against a viewport — the page's content area,
        /// 739 × 979 CSS px for US Letter portrait with the default margins. A feature this does
        /// not answer stays unknown — <c>scan</c> — so the rows combining a known width with it are
        /// the §3.1 three-valued logic at work.
        /// </summary>
        [Theory]
        // Bootstrap 3's breakpoints at Letter portrait: the xs tier.
        [InlineData("(min-width: 768px)", 739, 979, false)]
        [InlineData("(max-width: 767px)", 739, 979, true)]
        [InlineData("(min-width: 768px) and (max-width: 991px)", 739, 979, false)]
        [InlineData("(min-width: 768px) and (max-width: 991px)", 800, 979, true)]
        [InlineData("(min-width: 768px) and (max-width: 991px)", 1000, 979, false)]
        [InlineData("(min-width: 1200px)", 1200, 979, true)]            // min- is inclusive
        [InlineData("(max-width: 767px)", 767, 979, true)]              // and so is max-
        [InlineData("print and (min-width: 768px)", 739, 979, false)]
        [InlineData("screen and (min-width: 1px), print", 739, 979, true)]
        [InlineData("not (min-width: 600px)", 500, 979, true)]
        [InlineData("not (min-width: 600px)", 700, 979, false)]
        [InlineData("(width: 739px)", 739, 979, true)]
        [InlineData("(max-device-width: 480px) and (orientation: landscape)", 739, 979, false)]
        [InlineData("(min-height: 900px)", 739, 979, true)]
        [InlineData("(max-height: 900px)", 739, 979, false)]
        // Units: em and rem are 16px in a media query, whatever the document says.
        [InlineData("(min-width: 48em)", 739, 979, false)]
        [InlineData("(min-width: 48em)", 800, 979, true)]
        [InlineData("(max-width: 8in)", 739, 979, true)]                 // 768px
        [InlineData("(min-width: 0)", 739, 979, true)]
        // Level 4 range syntax, either way round, and a two-sided range.
        [InlineData("(width >= 600px)", 700, 979, true)]
        [InlineData("(width >= 600px)", 500, 979, false)]
        [InlineData("(600px <= width)", 700, 979, true)]
        [InlineData("(600px > width)", 700, 979, false)]
        [InlineData("(400px < width < 700px)", 500, 979, true)]
        [InlineData("(400px < width < 700px)", 800, 979, false)]
        // Orientation and the boolean context.
        [InlineData("(orientation: portrait)", 739, 979, true)]
        [InlineData("(orientation: landscape)", 739, 979, false)]
        [InlineData("(orientation: landscape)", 979, 739, true)]
        [InlineData("(width)", 739, 979, true)]
        [InlineData("not (width)", 739, 979, false)]                     // known true, so its negation is false, not unknown
        // A feature it does not answer stays unknown, and unknown applies; a known false still decides an `and`.
        [InlineData("(scan: interlace)", 739, 979, true)]
        [InlineData("(min-width: 768px) and (scan: interlace)", 739, 979, false)]
        [InlineData("(min-width: 768px) or (scan: interlace)", 739, 979, true)]
        [InlineData("(min-width: 700px) or (scan: interlace)", 739, 979, true)]
        [InlineData("(min-width: foo)", 739, 979, true)]                  // an unreadable value is unknown
        // Nested conditions.
        [InlineData("((min-width: 700px) or (max-width: 100px))", 739, 979, true)]
        [InlineData("((min-width: 800px) or (max-width: 100px))", 739, 979, false)]
        [InlineData("(not (min-width: 800px))", 739, 979, true)]
        public void AMediaQueryIsEvaluatedAgainstTheViewport(string list, double width, double height, bool applies)
            => Assert.Equal(applies, HtmlToBuilderMapper.MediaApplies(
                list, new HtmlToBuilderMapper.MediaViewport(width, height)));

        /// <summary>
        /// The screen medium (<see cref="HtmlToPdfOptions.MediaType"/>): the media type keywords the
        /// other way round, and everything else as for print.
        /// </summary>
        [Theory]
        [InlineData("screen", true)]
        [InlineData("print", false)]
        [InlineData("all", true)]
        [InlineData("only screen", true)]
        [InlineData("not print", true)]
        [InlineData("not screen", false)]
        [InlineData("screen and (max-width: 767px)", true)]
        [InlineData("screen and (min-width: 768px)", false)]
        [InlineData("print, screen", true)]
        public void AMediaQueryListIsEvaluatedForScreen(string list, bool applies)
            => Assert.Equal(applies, HtmlToBuilderMapper.MediaApplies(
                list, new HtmlToBuilderMapper.MediaViewport(739, 979, HtmlMediaType.Screen)));

        /// <summary>
        /// The features a static page answers (Media Queries 4 §4–7, 5 §6, §11), on the 739 × 979
        /// Letter viewport: 8-bit colour, nothing monochrome, paletted or grid; nothing hovers or
        /// points; no preference and a light scheme; an sRGB gamut; a resolution of 1dppx; the
        /// page's aspect ratio; paged overflow in print, scrolling on a screen. They were unknown,
        /// and an unknown feature applies — so <c>(hover: hover)</c> rules and a dark scheme did.
        /// </summary>
        [Theory]
        [InlineData("(color)", "print", true)]
        [InlineData("(color: 8)", "print", true)]
        [InlineData("(min-color: 8)", "print", true)]
        [InlineData("(min-color: 9)", "print", false)]
        [InlineData("(monochrome)", "print", false)]
        [InlineData("(monochrome: 0)", "print", true)]
        [InlineData("(min-monochrome: 1)", "print", false)]
        [InlineData("(color-index)", "print", false)]
        [InlineData("(grid)", "print", false)]
        [InlineData("(grid: 0)", "print", true)]
        [InlineData("(hover: hover)", "print", false)]
        [InlineData("(hover: none)", "print", true)]
        [InlineData("(hover)", "print", false)]
        [InlineData("not (hover: hover)", "print", true)]
        [InlineData("(any-hover: hover)", "screen", false)]
        [InlineData("(pointer: fine)", "screen", false)]
        [InlineData("(pointer: coarse)", "print", false)]
        [InlineData("(any-pointer: none)", "print", true)]
        [InlineData("(prefers-color-scheme: dark)", "print", false)]
        [InlineData("(prefers-color-scheme: light)", "screen", true)]
        [InlineData("(prefers-reduced-motion: reduce)", "print", false)]
        [InlineData("(prefers-reduced-motion: no-preference)", "print", true)]
        [InlineData("(prefers-reduced-motion)", "print", false)]
        [InlineData("(prefers-contrast: more)", "print", false)]
        [InlineData("(prefers-reduced-transparency: reduce)", "print", false)]
        [InlineData("(forced-colors: active)", "print", false)]
        [InlineData("(inverted-colors: inverted)", "print", false)]
        [InlineData("(color-gamut: srgb)", "print", true)]
        [InlineData("(color-gamut: p3)", "print", false)]
        [InlineData("(dynamic-range: high)", "print", false)]
        [InlineData("(display-mode: standalone)", "screen", false)]
        [InlineData("(display-mode: browser)", "screen", true)]
        [InlineData("(overflow-block: paged)", "print", true)]
        [InlineData("(overflow-block: scroll)", "print", false)]
        [InlineData("(overflow-block: scroll)", "screen", true)]
        [InlineData("(overflow-inline)", "print", false)]
        [InlineData("(overflow-inline: scroll)", "screen", true)]
        [InlineData("(update)", "print", false)]
        [InlineData("(update: fast)", "screen", true)]
        [InlineData("(PREFERS-COLOR-SCHEME: DARK)", "print", false)]          // ASCII case-insensitive
        // The aspect ratio: 739/979 is about 0.755.
        [InlineData("(min-aspect-ratio: 1/1)", "print", false)]
        [InlineData("(max-aspect-ratio: 1/1)", "print", true)]
        [InlineData("(aspect-ratio: 739/979)", "print", true)]
        [InlineData("(aspect-ratio > 3/4)", "print", true)]
        [InlineData("(aspect-ratio < 3 / 4)", "print", false)]
        [InlineData("(min-aspect-ratio: 0.7)", "print", true)]
        [InlineData("(max-aspect-ratio: 0.7)", "print", false)]              // one number is a ratio too, read
        [InlineData("(min-device-aspect-ratio: 16/9)", "print", false)]
        // Resolution, 1dppx: 96dpi, about 37.8dpcm.
        [InlineData("(min-resolution: 2dppx)", "print", false)]
        [InlineData("(min-resolution: 192dpi)", "print", false)]
        [InlineData("(resolution: 96dpi)", "print", true)]
        [InlineData("(resolution: 1x)", "print", true)]
        [InlineData("(max-resolution: 1dppx)", "print", true)]
        [InlineData("(min-resolution: 38dpcm)", "print", false)]
        [InlineData("(min-resolution: 37dpcm)", "print", true)]
        [InlineData("(resolution >= 2dppx)", "print", false)]
        [InlineData("(-webkit-min-device-pixel-ratio: 2)", "screen", false)]
        [InlineData("(-webkit-min-device-pixel-ratio: 1)", "screen", true)]
        [InlineData("(-webkit-device-pixel-ratio: 1)", "screen", true)]
        // The retina idiom: neither query applies.
        [InlineData("only screen and (-webkit-min-device-pixel-ratio: 2), only screen and (min-resolution: 192dpi)", "screen", false)]
        // A value these cannot read is unknown, and applies.
        [InlineData("(hover: 3)", "print", true)]
        [InlineData("(min-resolution: foo)", "print", true)]
        [InlineData("(aspect-ratio: 1/0)", "print", true)]
        [InlineData("(max-color: -1)", "print", true)]                     // a negative depth is no value
        public void AStaticPagesFeaturesAreAnswered(string list, string medium, bool applies)
            => Assert.Equal(applies, HtmlToBuilderMapper.MediaApplies(
                list, new HtmlToBuilderMapper.MediaViewport(739, 979, medium == "screen" ? HtmlMediaType.Screen : HtmlMediaType.Print)));

        /// <summary>
        /// End to end: a stylesheet's dark-scheme rule does not apply and its light one does — the
        /// dark rule would hide the first paragraph, the light one hides the second.
        /// </summary>
        [Fact]
        public void ADarkSchemeRuleDoesNotApply()
        {
            string c = DecodeStreams(ToPdf("<html><head><style>" +
                "@media (prefers-color-scheme: dark) { .a { display: none } } " +
                "@media (prefers-color-scheme: light) { .b { display: none } }" +
                "</style></head><body><p class='a'>AAA</p><p class='b'>BBB</p></body></html>"));
            Assert.Contains(DrawnTexts(c), t => t.Text.Contains("AAA"));
            Assert.DoesNotContain(DrawnTexts(c), t => t.Text.Contains("BBB"));
        }
    }
}
