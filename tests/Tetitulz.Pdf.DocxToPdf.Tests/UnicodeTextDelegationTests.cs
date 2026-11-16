// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.DocxToPdf.Rendering;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// <c>UnicodeText</c> after it stopped carrying its own shape → bidi → glyph-id walk and became
    /// an adapter onto <c>Core.Text.TextRunComposer</c>.
    ///
    /// <para><b>What these exist to catch.</b> Two copies of that pipeline survived in the tree —
    /// this one and <c>Builder.Export.RtlText</c> — and each had acquired exactly the fix the other
    /// lacked: this one the <b>code-point</b> walk, that one the caller-supplied base direction.
    /// Merging by hand in either direction deletes one of them silently, because both defects
    /// produce a perfectly valid PDF that simply draws the wrong thing. This file pins the half this
    /// side owned, plus the auto base direction the DOCX renderer's layout depends on — the merge
    /// must not quietly force a direction here either.</para>
    ///
    /// <para><b>Why the fixtures are mixed-script.</b> Hebrew alone reads correctly under either
    /// base direction (one run reversing as a unit), so a single-script fixture passes with the
    /// direction argument deleted.</para>
    /// </summary>
    public class UnicodeTextDelegationTests
    {
        private const string Shalom = "שלום";

        // ── the fix THIS side owned: supplementary-plane characters ──────────────────────────

        /// <summary>
        /// 𝑥 U+1D465 — which Word's equation editor writes straight into <c>m:t</c> — is one
        /// character and emits one glyph. A UTF-16 code-<i>unit</i> walk emits two, because no cmap
        /// maps a lone surrogate and both halves fall to the space substitute.
        /// </summary>
        [HebrewCapableDocxFontFact]
        public void AstralCharacter_EmitsOneGlyph_NotOneForEachSurrogateHalf()
        {
            var font = DocxProbeFont.Program;

            var run = UnicodeText.Shape("\U0001D465", font, 12);

            Assert.Equal(new[] { ExpectedGlyph(font, 0x1D465) }, run.Gids);
            Assert.Equal(run.Gids.Length, run.Used.Count);
        }

        /// <summary>
        /// The measuring half of the same defect. A wrong width here does not draw anything wrong —
        /// it breaks the line in the wrong place, which is why it needs its own assertion.
        /// </summary>
        [HebrewCapableDocxFontFact]
        public void AstralCharacter_MeasuresOneAdvance_NotTwo()
        {
            var font = DocxProbeFont.Program;
            ushort gid = ExpectedGlyph(font, 0x1D465);

            double measured = UnicodeText.MeasureWidth("\U0001D465", font, 12);

            Assert.Equal(font.GetAdvance(gid) / 1000d * 12d, measured, 6);
        }

        // ── the base direction this renderer relies on ───────────────────────────────────────

        /// <summary>
        /// The DOCX renderer has no paragraph direction to hand down at these call sites, so it
        /// composes under auto-detection — P2/P3 — exactly as it always has. <c>"Tel שלום"</c>
        /// starts with a strong L, so the paragraph resolves left-to-right and the Latin word is
        /// drawn first, with the Hebrew reversed after it.
        ///
        /// <para>Asserted positionally rather than as "it differs from forced RTL": routing this
        /// adapter through the shared composer made a base-direction argument available for the
        /// first time, and silently defaulting it to anything else would re-lay-out every
        /// mixed-script DOCX in the world.</para>
        /// </summary>
        [HebrewCapableDocxFontFact]
        public void MixedLatinAndHebrew_ComposesUnderAutoDetection_DrawingTheLatinRunFirst()
        {
            var font = DocxProbeFont.Program;
            ushort[] tel = "Tel".Select(c => ExpectedGlyph(font, c)).ToArray();

            var run = UnicodeText.Shape($"Tel {Shalom}", font, 12);

            Assert.Equal(tel, run.Gids.Take(3));
            // …and the Hebrew that follows is reversed into visual order, which is the whole point
            // of composing rather than emitting the string's own order.
            Assert.Equal(Shalom.Reverse().Select(c => ExpectedGlyph(font, c)),
                         run.Gids.Skip(run.Gids.Length - Shalom.Length));
        }

        // ── /ToUnicode and the width the wrap loop uses ──────────────────────────────────────

        /// <summary>
        /// <c>Used</c> feeds <c>/ToUnicode</c>, so it must carry the <b>base</b> letter rather than
        /// the presentation form that was drawn: a reader searching for بيت has to find the word
        /// visibly on the page. Arabic is the only script that can catch this — Hebrew is reordered
        /// but never substituted, so it round-trips whether the inverse mapping exists or not.
        /// </summary>
        [ArabicCapableDocxFontFact]
        public void Used_MapsBackToBaseLetters_NotToThePresentationFormsDrawn()
        {
            const string Bayt = "بيت";
            var font = ArabicProbeFont.Program;

            var run = UnicodeText.Shape(Bayt, font, 12);

            var mapped = run.Used.Select(u => u.codepoint).ToHashSet();
            Assert.Equal(Bayt.Select(c => (int)c).ToHashSet(), mapped);
            Assert.DoesNotContain(mapped, cp => cp >= 0xFE70 && cp <= 0xFEFF);
        }

        /// <summary>
        /// The wrap loop accumulates a line from <c>Shape(...).WidthPts</c> and tests the next word
        /// with <c>MeasureWidth</c>. Those must be the same number for the same glyphs, and the
        /// number must be the advance of the glyphs that are drawn — recomputed here from the
        /// font's own advance table, which is a second opinion rather than a restatement.
        ///
        /// <para><b>The astral character in the fixture is load-bearing.</b> Without it, mutating
        /// <c>MeasureWidth</c> back to its own code-unit walk left this green: Latin and Hebrew
        /// are not substituted by shaping and reorder only permutes, so both walks reach the same
        /// sum. 𝑥 U+1D465 is the one input on which a code-unit walk measures a different number
        /// of glyphs from the number drawn.</para>
        /// </summary>
        [HebrewCapableDocxFontFact]
        public void MeasuredWidthIsTheAdvanceOfTheGlyphsThatAreDrawn()
        {
            var font = DocxProbeFont.Program;
            string value = $"Tel {Shalom} \U0001D465";

            var run = UnicodeText.Shape(value, font, 12);
            double expected = run.Gids.Sum(g => font.GetAdvance(g)) / 1000d * 12d;

            Assert.Equal(expected, run.WidthPts, 6);
            Assert.Equal(expected, UnicodeText.MeasureWidth(value, font, 12), 6);
            Assert.True(run.WidthPts > 0, "a Latin + Hebrew + maths phrase cannot be zero points wide");
        }

        /// <summary>
        /// <c>NeedsUnicode</c> stays local and stays a <b>CP1252</b> test, not the Latin-1 one the
        /// Builder side uses. They answer different questions — which encoding to write, versus
        /// which glyphs a standard-14 face has — and collapsing them would send every euro sign and
        /// smart quote down the embedded-font path.
        /// </summary>
        [Theory]
        [InlineData("plain", false)]
        [InlineData("café", false)]          // Latin-1, encodable
        [InlineData("€ 5", false)]           // euro: CP1252 0x80, WinAnsi can write it
        [InlineData("“quoted”", false)] // curly quotes: CP1252 0x93/0x94
        [InlineData("שלום", true)]
        public void NeedsUnicode_AsksWhetherWinAnsiCanEncodeIt_NotWhetherItIsAboveLatin1(
            string text, bool expected)
        {
            Assert.Equal(expected, UnicodeText.NeedsUnicode(text));
        }

        private static ushort ExpectedGlyph(TrueTypeFontProgram font, int codePoint)
        {
            if (font.TryGetGlyph(codePoint, out ushort gid)) return gid;
            Assert.True(font.TryGetGlyph(' ', out ushort space),
                "the probe font has no space glyph, so the substitution under test is unobservable");
            return space;
        }
    }

    /// <summary>Reports <b>Skipped</b> when nothing on this host covers Hebrew + Latin, rather than
    /// passing while executing nothing (CLAUDE.md banned pattern 2).</summary>
    public sealed class HebrewCapableDocxFontFactAttribute : FactAttribute
    {
        public HebrewCapableDocxFontFactAttribute()
        {
            if (DocxProbeFont.Resolve() == null)
                Skip = "No font on this host covers Hebrew (U+05D0–U+05EA) and Latin.";
        }
    }

    /// <summary>Skips when nothing on this host covers the Arabic presentation forms the shaper
    /// emits — which is the coverage the drawn run actually needs.</summary>
    public sealed class ArabicCapableDocxFontFactAttribute : FactAttribute
    {
        public ArabicCapableDocxFontFactAttribute()
        {
            if (ArabicProbeFont.Resolve() == null)
                Skip = "No font on this host covers shaped Arabic.";
        }
    }

    /// <summary>A host font covering Hebrew, Latin and space, resolved through the renderer's own
    /// <see cref="FontResolver"/> so the probe asks the same question the production path does.</summary>
    internal static class DocxProbeFont
    {
        private static readonly object Gate = new object();
        private static TrueTypeFontProgram? _program;
        private static bool _probed;

        internal static TrueTypeFontProgram Program =>
            Resolve() ?? throw new System.InvalidOperationException(
                "No Hebrew-capable font on this host; the test should have been skipped.");

        internal static TrueTypeFontProgram? Resolve()
        {
            lock (Gate)
            {
                if (_probed) return _program;
                _probed = true;
                var required = new List<int> { ' ', 'T', 'e', 'l' };
                foreach (char c in "שלום") required.Add(c);
                _program = FontResolver.Resolve("Arial", bold: false, italic: false, required);
                return _program;
            }
        }
    }

    /// <summary>A host font covering the <b>shaped</b> Arabic forms — the glyphs that actually get
    /// drawn, which is a stricter requirement than covering the base letters.</summary>
    internal static class ArabicProbeFont
    {
        private static readonly object Gate = new object();
        private static TrueTypeFontProgram? _program;
        private static bool _probed;

        internal static TrueTypeFontProgram Program =>
            Resolve() ?? throw new System.InvalidOperationException(
                "No Arabic-capable font on this host; the test should have been skipped.");

        internal static TrueTypeFontProgram? Resolve()
        {
            lock (Gate)
            {
                if (_probed) return _program;
                _probed = true;
                string shaped = OpenTypeShaper.Shape("بيت") ?? "بيت";
                var required = new List<int>();
                foreach (char c in "بيت") required.Add(c);
                foreach (char c in shaped) required.Add(c);
                _program = FontResolver.Resolve("Arial", bold: false, italic: false, required);
                return _program;
            }
        }
    }
}
