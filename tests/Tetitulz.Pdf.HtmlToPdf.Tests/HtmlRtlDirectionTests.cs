// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// Writing direction in the managed engine: <c>dir</c> inherits, <c>ltr</c> overrides,
    /// <c>auto</c> detects, and the resolved direction reaches lists and tables.
    ///
    /// <para>Assertions read the <c>Tm</c> coordinates the renderer emitted — never extracted
    /// text, which is re-bidi'd on the way out and would therefore report the right string for a
    /// document drawn in the wrong order.</para>
    ///
    /// <para>Host-independent: the covering font is injected through
    /// <see cref="HtmlToPdfOptions.FontProvider"/> with host substitution off, so these run
    /// identically on a container with no Hebrew font.</para>
    /// </summary>
    public class HtmlRtlDirectionTests
    {
        private const string Shalom = "שלום";
        private const string Olam = "עולם";
        private const string Glyphs = Shalom + Olam + "JohnSmithabcdefg .";

        private static HtmlToPdfOptions Options()
        {
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => MinimalTrueType.Build(Glyphs);
            return o;
        }

        private static string Content(string html) =>
            DecodeStreams(ToPdf(html, Options()));

        private static readonly Lazy<Dictionary<int, int>> Cmap =
            new(() => MinimalTrueType.ReadCmap(MinimalTrueType.Build(Glyphs)));

        /// <summary>x of the glyph run drawing <paramref name="word"/>, identified by its glyph ids.</summary>
        private static double XOfHebrew(string content, string word)
        {
            var want = word.Select(ch => (ushort)Cmap.Value[ch]).OrderBy(g => g).ToArray();
            foreach (var run in DrawnGlyphRuns(content))
                if (run.Gids.Length == want.Length && run.Gids.OrderBy(g => g).SequenceEqual(want))
                    return run.X;
            Assert.Fail($"'{word}' was never drawn as a glyph run");
            return double.NaN;
        }

        private static double XOfLatin(string content, string word)
        {
            double x = XOf(content, word);
            Assert.False(double.IsNaN(x), $"'{word}' was never drawn");
            return x;
        }

        // ------------------------------------------------------------------ inheritance

        /// <summary>
        /// The pattern almost every real Hebrew page uses: a wrapper element carries
        /// <c>dir="rtl"</c> and the paragraphs inside inherit it.
        ///
        /// <para>Read off the paragraph element alone, the direction would render this left-to-right —
        /// the single most impactful RTL defect, because it makes the feature look broken on markup
        /// that is correct.</para>
        /// </summary>
        [Fact]
        public void DirRtl_OnAnAncestorDiv_IsInherited()
        {
            // Asserts the MARGIN, on LATIN content — the two things that took a mutation run to
            // get right. Hebrew cannot detect inheritance, because DefaultTextDirection = Auto
            // resolves it right-to-left from its own content whether or not anything was
            // inherited. And word order cannot detect it either: Latin inside an RTL paragraph is
            // a single level-2 run with no odd level present, so UAX #9 L2 correctly leaves the
            // words alone. What the direction genuinely decides here is which margin the line
            // aligns to — which is also the symptom a reader actually sees on a mis-rendered page.
            string c = Content("<div dir=\"rtl\"><p>abc def</p></div>");

            Assert.True(XOfLatin(c, "abc") > 300,
                "the paragraph must inherit dir=\"rtl\" from its wrapper and align right");
            Assert.True(XOfLatin(c, "abc") < XOfLatin(c, "def"),
                "a pure-Latin run keeps its word order in either direction");
        }

        [Fact]
        public void DirRtl_ThroughSeveralLevelsOfNesting_IsStillInherited()
        {
            string c = Content(
                "<div dir=\"rtl\"><section><article><p>abc def</p></article></section></div>");

            Assert.True(XOfLatin(c, "abc") > 300);
        }

        [Fact]
        public void CssDirectionRtl_OnAnAncestor_IsAlsoInherited()
        {
            string c = Content("<div style=\"direction:rtl\"><p>abc def</p></div>");

            Assert.True(XOfLatin(c, "abc") > 300);
        }

        /// <summary>
        /// <c>dir="ltr"</c> must be able to override an RTL ancestor. It could not before: the
        /// old predicate matched the literal string <c>"rtl"</c> and returned a bool, so there
        /// was no way to express "explicitly left-to-right".
        ///
        /// <para><b>The fixture is mixed on purpose.</b> Pure Hebrew cannot detect this, and
        /// finding that out took a run: under an LTR base the Hebrew is a single right-to-left
        /// run that L2 reverses as a unit, so its words come out in the same visual order either
        /// way. Only where a Latin run sits alongside it does the base direction decide which of
        /// the two goes on the left.</para>
        /// </summary>
        [Fact]
        public void DirLtr_OverridesAnRtlAncestor()
        {
            string rtl = Content($"<div dir=\"rtl\"><p>{Shalom} John</p></div>");
            string ltr = Content($"<div dir=\"rtl\"><p dir=\"ltr\">{Shalom} John</p></div>");

            Assert.True(XOfLatin(rtl, "John") < XOfHebrew(rtl, Shalom),
                "under RTL the embedded Latin sits left of the Hebrew that precedes it");
            Assert.True(XOfHebrew(ltr, Shalom) < XOfLatin(ltr, "John"),
                "dir=\"ltr\" must override the inherited direction, putting the Hebrew first");
        }

        [Fact]
        public void DirLtr_OverridesTheAlignmentToo()
        {
            string c = Content($"<div dir=\"rtl\"><p dir=\"ltr\">{Shalom}</p></div>");

            Assert.True(XOfHebrew(c, Shalom) < 100,
                "an explicitly left-to-right paragraph aligns to the left margin");
        }

        [Fact]
        public void DirRtl_OnTheParagraphItself_StillWorks()
        {
            // The case that always worked; pinned so the inheritance rewrite cannot lose it.
            string c = Content("<p dir=\"rtl\">abc def</p>");

            Assert.True(XOfLatin(c, "abc") > 300);
        }

        // ------------------------------------------------------------------ auto

        [Fact]
        public void DirAuto_OnHebrewContent_ResolvesRightToLeft()
        {
            string c = Content($"<div dir=\"ltr\"><p dir=\"auto\">{Shalom} {Olam}</p></div>");

            Assert.True(XOfHebrew(c, Shalom) > XOfHebrew(c, Olam),
                "dir=\"auto\" must detect Hebrew as right-to-left even under an LTR ancestor");
        }

        [Fact]
        public void DirAuto_OnLatinContent_ResolvesLeftToRight()
        {
            string c = Content("<div dir=\"rtl\"><p dir=\"auto\">abc def</p></div>");

            Assert.True(XOfLatin(c, "abc") < 100, "auto-detected Latin aligns to the left margin");
        }

        [Fact]
        public void NoDirectionAnywhere_HebrewStillReadsRightToLeft()
        {
            // HtmlToPdfOptions.DefaultTextDirection defaults to Auto, so undeclared Hebrew is
            // detected from its content rather than laid out as if it were English.
            string c = Content($"<p>{Shalom} {Olam}</p>");

            Assert.True(XOfHebrew(c, Shalom) > XOfHebrew(c, Olam));
        }

        [Fact]
        public void DefaultTextDirectionLtr_GivesBrowserParity()
        {
            var o = Options();
            o.DefaultTextDirection = Tetitulz.Pdf.Builder.Model.TextDirection.LeftToRight;
            string c = DecodeStreams(ToPdf($"<p>{Shalom} {Olam}</p>", o));

            // Word ORDER is still correct under an LTR base — the Hebrew is one right-to-left run
            // that reverses as a unit. What changes is the margin it aligns to.
            Assert.True(XOfHebrew(c, Shalom) < 100, "an LTR base left-aligns the paragraph");
        }

        // ------------------------------------------------------------------ mixed content

        /// <summary>
        /// The end-to-end version of the headline defect, driven from HTML: a multi-word Latin
        /// phrase inside a Hebrew paragraph keeps its reading order.
        /// </summary>
        [Fact]
        public void InheritedRtl_MultiWordLatinRun_KeepsItsWordsInReadingOrder()
        {
            string c = Content($"<div dir=\"rtl\"><p>{Shalom} John Smith</p></div>");

            Assert.True(XOfLatin(c, "John") < XOfLatin(c, "Smith"),
                "the embedded Latin run must not be word-reversed");
        }

        // ------------------------------------------------------------------ alignment

        [Fact]
        public void InheritedRtl_DefaultsToTheRightMargin()
        {
            string c = Content($"<div dir=\"rtl\"><p>{Shalom}</p></div>");

            Assert.True(XOfHebrew(c, Shalom) > 300, "RTL text defaults to the right edge");
        }

        [Fact]
        public void ExplicitTextAlignLeft_UnderRtl_IsHonoured()
        {
            string c = Content($"<div dir=\"rtl\"><p style=\"text-align:left\">{Shalom}</p></div>");

            Assert.True(XOfHebrew(c, Shalom) < 100,
                "an explicit text-align:left must not be overridden by the direction");
        }

        [Fact]
        public void TextAlignStart_UnderRtl_IsTheRightEdge()
        {
            string c = Content($"<div dir=\"rtl\"><p style=\"text-align:start\">{Shalom}</p></div>");
            Assert.True(XOfHebrew(c, Shalom) > 300);
        }

        [Fact]
        public void TextAlignEnd_UnderRtl_IsTheLeftEdge()
        {
            string c = Content($"<div dir=\"rtl\"><p style=\"text-align:end\">{Shalom}</p></div>");
            Assert.True(XOfHebrew(c, Shalom) < 100);
        }

        // ------------------------------------------------------------------ lists and tables

        [Fact]
        public void InheritedRtl_ReachesListItems()
        {
            string c = Content("<div dir=\"rtl\"><ul><li>abc def</li></ul></div>");

            Assert.True(XOfLatin(c, "abc") > 300,
                "a list item must inherit the wrapper's direction");
        }

        [Fact]
        public void InheritedRtl_MirrorsTableColumns()
        {
            // A table inside dir="rtl" mirrors its columns even though the document as a whole is
            // LTR: taken from the SECTION alone, RtlColumns would do nothing here.
            string rtl = Content(
                "<div dir=\"rtl\"><table><tr><td>abc</td><td>def</td></tr></table></div>");
            string ltr = Content("<table><tr><td>abc</td><td>def</td></tr></table>");

            Assert.True(XOfLatin(ltr, "abc") < XOfLatin(ltr, "def"), "LTR: first cell on the left");
            Assert.True(XOfLatin(rtl, "abc") > XOfLatin(rtl, "def"), "RTL: first cell on the right");
        }
    }
}
