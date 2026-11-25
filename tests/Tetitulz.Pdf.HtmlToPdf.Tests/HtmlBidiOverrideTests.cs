// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests
{
    /// <summary>
    /// <c>&lt;bdo&gt;</c>, <c>&lt;bdi&gt;</c> and CSS <c>unicode-bidi</c> — the explicit escape
    /// hatches from the bidi algorithm.
    ///
    /// <para>They are expressed as the Unicode control characters that mean them, which
    /// <c>UnicodeBidi.AssignEmbeddingLevels</c> already understands. The interesting half is that
    /// those characters must change the level resolution while <b>never</b> reaching the page or
    /// <c>/ToUnicode</c> — a font has no glyph for one, so an unstripped control both draws a
    /// substitute and puts invisible characters into every copy-paste.</para>
    /// </summary>
    public class HtmlBidiOverrideTests
    {
        private const string Glyphs = "abcdefghijklmnopqrstuvwxyz ";

        private static HtmlToPdfOptions Options()
        {
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => MinimalTrueType.Build(Glyphs);
            return o;
        }

        private static double XOfWord(string content, string word)
        {
            double x = XOf(content, word);
            Assert.False(double.IsNaN(x), $"'{word}' was never drawn");
            return x;
        }

        // ------------------------------------------------------------------ <bdo>

        /// <summary>
        /// <c>&lt;bdo dir="rtl"&gt;</c> overrides the algorithm outright: every character of its
        /// content is right-to-left, so <c>alpha beta</c> is displayed <c>ateb ahpla</c>, as a
        /// browser displays it. Latin text is the clearest fixture, because left to itself it would
        /// never reverse. (It reversed the words and not their letters until each standard-font
        /// word was arranged by its characters' levels.)
        /// </summary>
        [Fact]
        public void BdoRtl_ReversesLatinContentCharacterByCharacter()
        {
            string plain = DecodeStreams(ToPdf("<p>alpha beta</p>", Options()));
            string bdo = DecodeStreams(ToPdf("<p><bdo dir=\"rtl\">alpha beta</bdo></p>", Options()));

            Assert.True(XOfWord(plain, "alpha") < XOfWord(plain, "beta"),
                "the control must read left-to-right");
            Assert.True(XOfWord(bdo, "ateb") < XOfWord(bdo, "ahpla"),
                "bdo dir=rtl must lay its content out right-to-left");
            Assert.True(double.IsNaN(XOf(bdo, "alpha")), "a word inside the override was drawn in its logical order");
        }

        /// <summary>
        /// A <c>&lt;bdo&gt;</c> holds text drawn through an embedded font in its direction too: Hebrew under
        /// <c>dir="ltr"</c> is drawn alef, bet, gimel from the left, and "alef-bet cd" under <c>dir="rtl"</c>
        /// is drawn "dc" then the two letters. Shaped in its paragraph's direction, a token drawn through an
        /// embedded font would keep the order an override exists to undo.
        /// </summary>
        /// <remarks>
        /// Read back through the plain extractor, which takes a line's drawn order as visual and its
        /// direction from the first strong character so drawn: the left-to-right Hebrew reads back reversed.
        /// The control is each text with no override.
        /// </remarks>
        [Fact]
        public void Bdo_HoldsShapedTextInItsDirection()
        {
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => MinimalTrueType.Build(Glyphs + "\u05D0\u05D1\u05D2");
            string Read(string html)
            {
                using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(ToPdf(html, o)));
                return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc).Trim();
            }

            Assert.Equal("\u05D2\u05D1\u05D0", Read("<p><bdo dir=\"ltr\">\u05D0\u05D1\u05D2</bdo></p>"));
            Assert.Equal("\u05D0\u05D1\u05D2", Read("<p>\u05D0\u05D1\u05D2</p>"));
            Assert.Equal("dc \u05D0\u05D1", Read("<p><bdo dir=\"rtl\">\u05D0\u05D1 cd</bdo></p>"));
            Assert.Equal("cd \u05D0\u05D1", Read("<p>\u05D0\u05D1 cd</p>"));
        }

        [Fact]
        public void BdoLtr_LeavesLatinContentAlone()
        {
            string dec = DecodeStreams(ToPdf(
                "<div dir=\"rtl\"><p><bdo dir=\"ltr\">alpha beta</bdo></p></div>", Options()));

            Assert.True(XOfWord(dec, "alpha") < XOfWord(dec, "beta"));
        }

        [Fact]
        public void BdoWithNoDirAttribute_ChangesNothing()
        {
            // <bdo> without a dir has no defined override, so it must behave as a plain span
            // rather than picking a direction of its own.
            string plain = DecodeStreams(ToPdf("<p>alpha beta</p>", Options()));
            string bdo = DecodeStreams(ToPdf("<p><bdo>alpha beta</bdo></p>", Options()));

            Assert.Equal(XOfWord(plain, "alpha"), XOfWord(bdo, "alpha"), 1);
            Assert.Equal(XOfWord(plain, "beta"), XOfWord(bdo, "beta"), 1);
        }

        // ------------------------------------------------------------------ unicode-bidi

        [Fact]
        public void UnicodeBidiOverride_MatchesBdo()
        {
            string viaBdo = DecodeStreams(ToPdf("<p><bdo dir=\"rtl\">alpha beta</bdo></p>", Options()));
            string viaCss = DecodeStreams(ToPdf(
                "<p><span style=\"unicode-bidi:bidi-override;direction:rtl\">alpha beta</span></p>",
                Options()));

            // The two spellings mean the same thing and must produce the same layout.
            Assert.Equal(XOfWord(viaBdo, "ahpla"), XOfWord(viaCss, "ahpla"), 1);
            Assert.Equal(XOfWord(viaBdo, "ateb"), XOfWord(viaCss, "ateb"), 1);
        }

        [Fact]
        public void UnicodeBidiNormal_ChangesNothing()
        {
            string plain = DecodeStreams(ToPdf("<p>alpha beta</p>", Options()));
            string normal = DecodeStreams(ToPdf(
                "<p><span style=\"unicode-bidi:normal\">alpha beta</span></p>", Options()));

            Assert.Equal(XOfWord(plain, "alpha"), XOfWord(normal, "alpha"), 1);
        }

        // ------------------------------------------------------------------ the control characters

        /// <summary>
        /// The font here <b>covers the control characters</b>, and that is the entire point of the
        /// fixture. A Unicode-complete system font maps U+202E to a real (zero-width) glyph, so
        /// without the strip the control is drawn <i>and</i> recorded in <c>/ToUnicode</c> — which
        /// puts invisible characters into every copy-paste and makes a text search for the
        /// surrounding words fail.
        ///
        /// <para>With a font that does <i>not</i> cover them the unrenderable-text policy drops
        /// them anyway, so the strip is unobservable and a test built on that font passes with the
        /// strip deleted. That is how this test was written the first time.</para>
        /// </summary>
        private static HtmlToPdfOptions OptionsCoveringControls()
        {
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => MinimalTrueType.Build(Glyphs + "‪‫‬‭‮⁦⁧⁨⁩");
            return o;
        }

        [Fact]
        public void ControlCharacters_NeverReachTheExtractedText()
        {
            byte[] pdf = ToPdf("<p><bdo dir=\"rtl\">alpha beta</bdo></p>", OptionsCoveringControls());

            using var ms = new MemoryStream(pdf);
            using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms);
            string text = Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc);

            Assert.Contains("ahpla", text, StringComparison.Ordinal);   // the override's display order
            foreach (char c in text)
                Assert.False(Tetitulz.Pdf.Core.Text.UnicodeBidi.IsBidiControl(c),
                    $"a bidi control (U+{(int)c:X4}) reached the extracted text");
        }

        [Fact]
        public void ControlCharacters_AreNotCountedAsDrawnGlyphs()
        {
            // Count the glyphs actually emitted. Comparing the word's x against an unwrapped
            // control was tried first and does NOT work: the synthetic font gives the control a
            // zero advance, so nothing shifts and the test passes with the strip deleted. Which
            // glyph ids reached the page is the claim that can fail.
            var o = OptionsCoveringControls();
            byte[] pdf = ToPdf("<p><bdo dir=\"rtl\">alpha</bdo></p>", o);
            var cmap = MinimalTrueType.ReadCmap(
                MinimalTrueType.Build(Glyphs + "‪‫‬‭‮⁦⁧⁨⁩"));

            var controlGids = new[] { '‪', '‫', '‬', '‭', '‮',
                                      '⁦', '⁧', '⁨', '⁩' }
                .Where(c => cmap.ContainsKey(c)).Select(c => (ushort)cmap[c]).ToHashSet();
            Assert.NotEmpty(controlGids);   // the fixture really can observe the failure

            foreach (var run in DrawnGlyphRuns(DecodeStreams(pdf)))
                foreach (ushort gid in run.Gids)
                    Assert.DoesNotContain(gid, controlGids);
        }

        // ------------------------------------------------------------------ <bdi>

        /// <summary>
        /// What <c>&lt;bdi&gt;</c> is actually for: stopping a run of unknown direction from
        /// dragging the <b>neutral</b> text that follows it onto the wrong side.
        ///
        /// <para>The fixture has to be chosen with care. Pure Latin inside <c>dir="rtl"</c> — the
        /// obvious-looking test — resolves to a single level-2 run with no odd level, so L2
        /// correctly reorders nothing and the isolate is unobservable. Only a neutral sitting
        /// between a right-to-left run and a number discriminates: N1 treats a number as
        /// right-to-left, so without the isolate the neutral joins the Hebrew run and ends up on
        /// its far side.</para>
        /// </summary>
        [Fact]
        public void Bdi_KeepsFollowingNeutralsOutOfTheIsolatedRun()
        {
            const string Heb = "\u05E9\u05DC\u05D5\u05DD";
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => MinimalTrueType.Build(Heb + "abc-5 ");
            var cmap = MinimalTrueType.ReadCmap(MinimalTrueType.Build(Heb + "abc-5 "));

            double XOfHebrew(string content)
            {
                var want = Heb.Select(ch => (ushort)cmap[ch]).OrderBy(g => g).ToArray();
                foreach (var run in DrawnGlyphRuns(content))
                    if (run.Gids.Length == want.Length && run.Gids.OrderBy(g => g).SequenceEqual(want))
                        return run.X;
                Assert.Fail("the Hebrew was never drawn as a glyph run");
                return double.NaN;
            }

            string bare = DecodeStreams(ToPdf($"<p>abc {Heb} -- 5</p>", o));
            string isolated = DecodeStreams(ToPdf($"<p>abc <bdi>{Heb}</bdi> -- 5</p>", o));

            Assert.True(XOfWord(bare, "--") < XOfHebrew(bare),
                "without the isolate the neutral is pulled into the right-to-left run");
            Assert.True(XOfWord(isolated, "--") > XOfHebrew(isolated),
                "the isolate keeps the neutral in the paragraph's own direction");
        }

        [Fact]
        public void NestedOverrides_StayBalanced()
        {
            // An embedding or override is closed by U+202C and an isolate by U+2069; mixing them
            // leaves the stack unbalanced, so the wrong text stays overridden. Nesting the two
            // kinds is what catches a closer of the wrong kind.
            string dec = DecodeStreams(ToPdf(
                "<p><bdo dir=\"rtl\">one <bdi>two</bdi> three</bdo> four five</p>", Options()));

            // Inside the override every character is right-to-left: "one" and "three" are displayed
            // reversed, the isolated "two" is not.
            Assert.True(XOfWord(dec, "eerht") < XOfWord(dec, "eno"),
                "the override is active inside the bdo");
            Assert.True(XOfWord(dec, "four") < XOfWord(dec, "five"),
                "and closed after it — otherwise the trailing words are overridden too");
            Assert.True(XOfWord(dec, "eno") < XOfWord(dec, "four"),
                "the overridden group as a whole keeps its place in the line");
            XOfWord(dec, "two");   // the isolate inside is not overridden
        }

        // ------------------------------------------------------------------ dir on inline elements

        /// <summary>
        /// <c>dir</c> on an inline element isolates it, as HTML's own stylesheet says
        /// (<c>[dir] { unicode-bidi: isolate }</c>) — so a date keeps its written order inside
        /// right-to-left text.
        /// </summary>
        /// <remarks>
        /// <para>The real-world case: a Hebrew form printing its timestamp as
        /// <c>&lt;span dir="ltr"&gt;24/09/2026 07:28&lt;/span&gt;</c>. It rendered as
        /// "07:28 24/09/2026", because the engine only isolated when CSS <c>unicode-bidi</c> was
        /// also declared, and — once it did isolate — because Core's W7 did not start an isolate's
        /// digits from its own direction. This test needs both fixes; each alone leaves it red.</para>
        /// <para>Digits only, so the standard fonts draw it and each word is its own text object.
        /// The paragraph is right-to-left from the container, which is what reverses the two
        /// numbers when nothing isolates them.</para>
        /// </remarks>
        [Fact]
        public void DirOnAnInlineElement_KeepsItsNumbersInWrittenOrder()
        {
            string dec = DecodeStreams(ToPdf(
                "<div dir=\"rtl\"><p><span dir=\"ltr\">24/09/2026 07:28</span></p></div>"));

            Assert.True(XOfWord(dec, "24/09/2026") < XOfWord(dec, "07:28"),
                "the ltr span was not isolated: its two numbers were ordered right to left");
        }

        /// <summary>
        /// The control, and the rule's other half: an author's <c>unicode-bidi</c> overrides HTML's
        /// default, so <c>unicode-bidi:normal</c> on the same span takes the isolate away and the
        /// paragraph orders the numbers right to left again.
        /// </summary>
        [Fact]
        public void DirWithUnicodeBidiNormal_IsNotIsolated()
        {
            string dec = DecodeStreams(ToPdf(
                "<div dir=\"rtl\"><p><span dir=\"ltr\" style=\"unicode-bidi:normal\">24/09/2026 07:28</span></p></div>"));

            Assert.True(XOfWord(dec, "24/09/2026") > XOfWord(dec, "07:28"),
                "unicode-bidi:normal must override the isolate that dir implies");
        }

        /// <summary><c>dir="auto"</c> isolates too; digits alone resolve left to right.</summary>
        [Fact]
        public void DirAutoOnAnInlineElement_IsIsolated()
        {
            string dec = DecodeStreams(ToPdf(
                "<div dir=\"rtl\"><p><span dir=\"auto\">24/09/2026 07:28</span></p></div>"));

            Assert.True(XOfWord(dec, "24/09/2026") < XOfWord(dec, "07:28"),
                "dir=auto did not isolate its content");
        }

        /// <summary>
        /// <c>dir="auto"</c> takes its direction from its first strong character — a first-strong
        /// isolate, not a left-to-right one. Hebrew first makes it right-to-left even inside a
        /// left-to-right paragraph, which puts the Latin word that follows the Hebrew on its left.
        /// </summary>
        /// <remarks>
        /// Latin, not a number, follows the Hebrew: a number after Hebrew joins the right-to-left
        /// run in either direction (N1 counts it as R), so it lands on the left of the Hebrew in both
        /// and cannot tell the two isolates apart. A Latin word is strong left-to-right and does.
        /// </remarks>
        [Fact]
        public void DirAutoOnAnInlineElement_TakesItsDirectionFromItsFirstStrongCharacter()
        {
            const string Heb = "\u05E9\u05DC\u05D5\u05DD";
            var o = Managed();
            o.AllowSystemFontSubstitution = false;
            o.FontProvider = _ => MinimalTrueType.Build(Heb + "abc ");
            var cmap = MinimalTrueType.ReadCmap(MinimalTrueType.Build(Heb + "abc "));
            var want = Heb.Select(ch => (ushort)cmap[ch]).OrderBy(g => g).ToArray();

            double XOfHebrew(string content)
            {
                foreach (var run in DrawnGlyphRuns(content))
                    if (run.Gids.Length == want.Length && run.Gids.OrderBy(g => g).SequenceEqual(want))
                        return run.X;
                Assert.Fail("the Hebrew was never drawn as a glyph run");
                return double.NaN;
            }

            string auto = DecodeStreams(ToPdf($"<p dir=\"ltr\"><span dir=\"auto\">{Heb} abc</span></p>", o));
            string ltr = DecodeStreams(ToPdf($"<p dir=\"ltr\"><span dir=\"ltr\">{Heb} abc</span></p>", o));

            Assert.True(XOfWord(auto, "abc") < XOfHebrew(auto),
                "dir=auto with Hebrew first should resolve right to left, putting abc on the left");
            // The control: a left-to-right isolate keeps the Hebrew first.
            Assert.True(XOfWord(ltr, "abc") > XOfHebrew(ltr),
                "a dir=ltr span should keep the Hebrew before the Latin");
        }
    }
}
