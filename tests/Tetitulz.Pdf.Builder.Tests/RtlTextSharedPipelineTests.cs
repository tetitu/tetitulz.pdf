// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <c>Builder.Export.RtlText</c>, an adapter onto <c>Core.Text.TextRunComposer</c>'s
/// shape → bidi → glyph-id walk, shared with <c>DocxToPdf.Rendering.UnicodeText</c>.
///
/// <para><b>What these exist to catch.</b> The shared walk carries both the caller-supplied base
/// direction and the <b>code-point</b> walk; either lost produces a perfectly valid PDF that simply
/// draws the wrong thing. Each is therefore asserted here separately, on emitted glyph ids rather
/// than on a call having returned something.</para>
///
/// <para><b>Why the fixtures are mixed-script.</b> Pure Hebrew reads correctly under either base
/// direction — it is one run that reverses as a unit — so a single-script fixture passes with the
/// direction argument deleted. Only text that carries both a Latin and a Hebrew run can see it.</para>
/// </summary>
public class RtlTextSharedPipelineTests
{
    private const string Shalom = "שלום";

    // ── supplementary-plane characters: the code-point walk ──────────────────────────────────

    /// <summary>
    /// 𝑥 U+1D465 is <b>one</b> character, so it emits <b>one</b> glyph — the font's if it has one,
    /// the space glyph if not.
    ///
    /// <para>The walk this replaced iterated UTF-16 code <i>units</i>, so it emitted <b>two</b>
    /// glyphs for it: no cmap maps a lone surrogate, so both halves fell to the space substitute and
    /// every astral character drew as a double space of the wrong width. Asserting the glyph
    /// <i>identity</i> as well as the count is what makes this independent of whether this host owns
    /// a font covering the mathematical alphanumerics.</para>
    /// </summary>
    [HebrewCapableFontFact]
    public void AstralCharacter_EmitsOneGlyph_NotOneForEachSurrogateHalf()
    {
        var font = ProbeFont.Program;

        var run = RtlText.Shape("\U0001D465", font, 12, null!, BaseDirection.LeftToRight);

        Assert.Equal(new[] { ExpectedGlyph(font, 0x1D465) }, run.Gids);
    }

    /// <summary>
    /// The same defect at the other end of the pipeline. <c>CollectCodePoints</c> feeds
    /// <c>Ctx.GetUniFont</c>'s coverage loop, so collecting U+D835 and U+DD38 rather than U+1D538
    /// asked every candidate face to cover two values that are not characters — which no font can
    /// do, so resolution declined and the run fell through to the standard-14 path as '?'. Fixing
    /// only the drawing half leaves the character undrawable for a different reason.
    /// </summary>
    [Fact]
    public void CollectCodePoints_AstralCharacter_YieldsTheCharacter_NotItsSurrogateHalves()
    {
        var set = new HashSet<int>();

        RtlText.CollectCodePoints("a\U0001D538b", set);

        // 'a' and 'b' are below U+0100 and are deliberately not collected: the question this answers
        // is "what can the standard-14 fonts NOT draw".
        Assert.Equal(new[] { 0x1D538 }, set.OrderBy(cp => cp).ToArray());
    }

    /// <summary>
    /// The named-family variant asks a different question — the candidate face has to cover the
    /// run's Latin too, so nothing is filtered out — but it is the <b>same</b> walk, which is the
    /// whole point: an inlined <c>foreach (char c in text) cps.Add(c)</c> beside it would make a
    /// named-family run carrying an astral character demand coverage of two lone surrogates.
    /// </summary>
    [Fact]
    public void CollectAllCodePoints_KeepsLatin_AndStillPairsSurrogates()
    {
        var set = new HashSet<int>();

        RtlText.CollectAllCodePoints("a\U0001D538b", set);

        Assert.Equal(new[] { (int)'a', (int)'b', 0x1D538 }, set.OrderBy(cp => cp).ToArray());
    }

    /// <summary>
    /// An unpaired high surrogate is malformed input. It is passed through under its own value
    /// rather than swallowed, so the collection still terminates and the caller's diagnostic still
    /// names something the document actually contains.
    /// </summary>
    [Fact]
    public void CollectCodePoints_LoneSurrogate_IsPassedThroughUnderItsOwnValue()
    {
        var set = new HashSet<int>();

        RtlText.CollectCodePoints("a\uD835b", set);

        Assert.Equal(new[] { 0xD835 }, set.OrderBy(cp => cp).ToArray());
    }

    // ── the caller-supplied base direction ────────────────────────────────────────────────────

    /// <summary>
    /// The paragraph's base direction, not the token's own first strong character, decides where a
    /// Latin word inside a Hebrew paragraph is drawn. Under auto-detection <c>"Tel שלום"</c> starts
    /// with a strong L, so P2/P3 make it an <i>LTR paragraph</i> and the Latin word is drawn first;
    /// under the paragraph's real RTL direction the Latin run belongs on the right-hand end.
    ///
    /// <para>This is the invoice defect the <c>BaseDirection</c> overload exists for.</para>
    /// </summary>
    [HebrewCapableFontFact]
    public void MixedLatinAndHebrew_DrawsTheLatinRunLast_UnderAForcedRtlParagraph()
    {
        var font = ProbeFont.Program;
        string value = $"Tel {Shalom}";
        ushort[] tel = "Tel".Select(c => ExpectedGlyph(font, c)).ToArray();

        var auto = RtlText.Shape(value, font, 12, null!, BaseDirection.Auto);
        var rtl = RtlText.Shape(value, font, 12, null!, BaseDirection.RightToLeft);

        // Auto: first strong character is 'T', so the paragraph resolves LTR and "Tel" is leftmost.
        Assert.Equal(tel, auto.Gids.Take(3));
        // Forced RTL: "Tel" is a level-2 island inside a level-1 paragraph, so it lands at the
        // right-hand end — still spelled forwards, because L2 reverses it twice.
        Assert.Equal(tel, rtl.Gids.Skip(rtl.Gids.Length - 3));
        Assert.NotEqual(auto.Gids, rtl.Gids);
    }

    /// <summary>
    /// Reordering is a permutation: the same glyphs come back in a different order, never more or
    /// fewer. A direction-only assertion cannot see a reorder that truncates.
    /// </summary>
    [HebrewCapableFontFact]
    public void ForcingADirection_PermutesTheGlyphs_WithoutAddingOrDroppingAny()
    {
        var font = ProbeFont.Program;
        string value = $"Tel {Shalom}";

        var auto = RtlText.Shape(value, font, 12, null!, BaseDirection.Auto);
        var rtl = RtlText.Shape(value, font, 12, null!, BaseDirection.RightToLeft);

        Assert.Equal(auto.Gids.OrderBy(g => g), rtl.Gids.OrderBy(g => g));
        Assert.Equal(auto.WidthPt, rtl.WidthPt, 6);
    }

    // ── the width the exporter lays out with ─────────────────────────────────────────────────

    /// <summary>
    /// One glyph per <b>character</b>, and a width to match: the run's advance is the sum over the
    /// source's own code points, computed here from the font's advance table without consulting what
    /// <c>Shape</c> emitted. Neither Hebrew nor 𝑥 is substituted by shaping, and reordering only
    /// permutes, so that sum is the width the run must have.
    ///
    /// <para><b>Deriving the expectation from the source rather than from <c>run.Gids</c> is the
    /// point.</b> Summing the emitted glyphs' advances is true under any walk — it restates the
    /// call instead of checking it — and a first version of this test did exactly that and survived
    /// the pre-merge code-unit walk being restored. Under that walk 𝑥 becomes two space glyphs, so
    /// both the count and the width move away from the source's.</para>
    /// </summary>
    [HebrewCapableFontFact]
    public void WidthAndGlyphCountFollowTheSourcesCodePoints_NotItsCodeUnits()
    {
        var font = ProbeFont.Program;
        int[] sourceCodePoints = { 'ש', 'ל', 'ו', 'ם', ' ', 0x1D465 };

        var run = RtlText.Shape($"{Shalom} \U0001D465", font, 12, null!, BaseDirection.RightToLeft);

        double expected = sourceCodePoints.Sum(cp => (double)font.GetAdvance(ExpectedGlyph(font, cp)))
                          / 1000d * 12d;
        Assert.Equal(sourceCodePoints.Length, run.Gids.Length);
        Assert.Equal(expected, run.WidthPt, 6);
        Assert.True(run.WidthPt > 0, "a Hebrew word plus a maths italic x cannot be zero points wide");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The glyph this font draws for <paramref name="codePoint"/> — its own if it covers
    /// it, otherwise the space glyph the pipeline substitutes.</summary>
    private static ushort ExpectedGlyph(TrueTypeFontProgram font, int codePoint)
    {
        if (font.TryGetGlyph(codePoint, out ushort gid)) return gid;
        Assert.True(font.TryGetGlyph(' ', out ushort space),
            "the probe font has no space glyph, so the substitution under test is unobservable");
        return space;
    }
}

/// <summary>
/// Reports <b>Skipped</b> when no font on this host covers Hebrew, rather than passing while
/// executing nothing (CLAUDE.md banned pattern 2). Modelled on <c>TestHelpers</c>'
/// <c>HebrewFontFact</c>, and not a copy of it: these tests also need Latin letters and a space
/// glyph in the same face, which a font covering Hebrew alone does not promise.
/// </summary>
public sealed class HebrewCapableFontFactAttribute : FactAttribute
{
    public HebrewCapableFontFactAttribute()
    {
        if (!ProbeFont.Available)
            Skip = "No font on this host covers Hebrew (U+05D0–U+05EA) and Latin.";
    }
}

/// <summary>
/// A host font covering Hebrew, Latin and the space glyph — resolved through the production
/// <see cref="SystemFontResolver"/>, and by real cmap <b>coverage</b> rather than by family name:
/// "is a font called David installed" and "can anything here draw א" are different questions, and
/// only the second decides whether the behaviour under test is observable.
/// </summary>
internal static class ProbeFont
{
    private static readonly object Gate = new();
    private static FontResolution? _resolution;
    private static bool _probed;

    internal static bool Available => Resolve() != null;

    internal static TrueTypeFontProgram Program =>
        Resolve()?.Program
        ?? throw new InvalidOperationException(
            "No Hebrew-capable font on this host; the test should have been skipped.");

    private static FontResolution? Resolve()
    {
        lock (Gate)
        {
            if (_probed) return _resolution;
            _probed = true;

            var required = new List<int> { ' ', 'T', 'e', 'l' };
            foreach (char c in "שלום") required.Add(c);

            var result = SystemFontResolver.Resolve(
                new FontResolutionRequest
                {
                    FamilyHint = "Hebrew",
                    RequiredCodePoints = required,
                    AllowSystemFontSubstitution = true,
                },
                fontProvider: null);

            _resolution = result.Success ? result : null;
            return _resolution;
        }
    }
}
