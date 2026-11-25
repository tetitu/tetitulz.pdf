// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Tests.TestInfrastructure;
using Xunit;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// <see cref="TextRunComposer"/> — the single shape → bidi-reorder → glyph pipeline that replaced
/// three separate copies (form appearances, the Builder exporter, the DOCX renderer).
///
/// <para>These assert on the <b>glyph ids actually emitted</b>, not on whether a call returned
/// something. A run that draws nothing, draws the wrong glyphs, or draws them backwards all produce
/// a perfectly valid non-null result, which is why the interesting assertions here compare gid
/// sequences against sequences derived independently from the font's own cmap.</para>
/// </summary>
public class TextRunComposerTests
{
    private const string Shalom = HebrewFontProbe.Shalom;

    private static ushort[] LogicalGids(TrueTypeFontProgram font, string text)
        => text.Select(c =>
        {
            Assert.True(font.TryGetGlyph(c, out ushort gid), $"font lacks a glyph for U+{(int)c:X4}");
            return gid;
        }).ToArray();

    // ── direction ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hebrew is stored logically and must be <b>drawn</b> right-to-left, so the emitted gids are
    /// the reverse of the logical sequence. Emitting them in logical order yields a page that reads
    /// backwards and looks entirely plausible to anyone who cannot read it.
    /// </summary>
    [HebrewFontFact]
    public void HebrewText_IsEmittedInVisualOrder()
    {
        var font = HebrewFontProbe.Program;

        var run = TextRunComposer.Compose(Shalom, font, null);

        Assert.Equal(LogicalGids(font, Shalom).Reverse(), run.GlyphIds);
    }

    /// <summary>
    /// The forced base direction has to reach the glyph output, not merely the bidi call. The
    /// fixture starts with a Latin word so first-strong detection says LTR — the invoice bug the
    /// overload exists for.
    /// </summary>
    [HebrewFontFact]
    public void MixedValue_ProducesADifferentGlyphOrderUnderForcedRtl()
    {
        var font = HebrewFontProbe.Program;
        string value = $"Tel {Shalom}";

        var auto = TextRunComposer.Compose(value, font,
            new TextRunOptions { BaseDirection = BaseDirection.Auto });
        var rtl = TextRunComposer.Compose(value, font,
            new TextRunOptions { BaseDirection = BaseDirection.RightToLeft });

        Assert.NotEqual(auto.GlyphIds, rtl.GlyphIds);
        Assert.False(auto.IsRightToLeft);
        Assert.True(rtl.IsRightToLeft);
    }

    /// <summary>
    /// Reordering permutes glyphs; it must never add or drop one. This is what catches a reorder
    /// that silently truncates, which a direction-only assertion would not.
    /// </summary>
    [HebrewFontFact]
    public void ReorderingPermutesTheGlyphs_WithoutAddingOrDroppingAny()
    {
        var font = HebrewFontProbe.Program;
        string value = $"Tel {Shalom}";

        var auto = TextRunComposer.Compose(value, font,
            new TextRunOptions { BaseDirection = BaseDirection.Auto });
        var rtl = TextRunComposer.Compose(value, font,
            new TextRunOptions { BaseDirection = BaseDirection.RightToLeft });

        Assert.Equal(auto.GlyphIds.OrderBy(g => g), rtl.GlyphIds.OrderBy(g => g));
    }

    /// <summary>Reordering does not change the total advance, which is what lets one composition
    /// serve both measuring and drawing.</summary>
    [HebrewFontFact]
    public void AdvanceIsIndependentOfDirection()
    {
        var font = HebrewFontProbe.Program;
        string value = $"Tel {Shalom}";

        Assert.Equal(
            TextRunComposer.Compose(value, font, new TextRunOptions { BaseDirection = BaseDirection.Auto })
                           .AdvanceGlyphSpaceUnits,
            TextRunComposer.Compose(value, font, new TextRunOptions { BaseDirection = BaseDirection.RightToLeft })
                           .AdvanceGlyphSpaceUnits);
    }

    /// <summary>
    /// The advance is in glyph space (1000/em), and <c>WidthAt</c> is the only place the font size
    /// is applied. Returning points from the composer instead would have forced every existing call
    /// site's arithmetic to change.
    /// </summary>
    [HebrewFontFact]
    public void WidthAtAppliesTheFontSizeToTheGlyphSpaceAdvance()
    {
        var run = TextRunComposer.Compose(Shalom, HebrewFontProbe.Program, null);

        Assert.Equal(run.AdvanceGlyphSpaceUnits / 1000d * 12d, run.WidthAt(12d), 6);
        Assert.Equal(2 * run.WidthAt(6d), run.WidthAt(12d), 6);
    }

    // ── missing glyphs ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="MissingGlyphPolicy.Refuse"/> draws nothing for an uncovered code point and says
    /// which one. U+10FFFF is the last valid code point and no real font has it.
    /// </summary>
    [HebrewFontFact]
    public void UncoveredCodePoint_UnderRefuse_IsNotDrawnAndIsReported()
    {
        var font = HebrewFontProbe.Program;
        string value = "A􏿿B";   // 'A', U+10FFFF, 'B'

        var run = TextRunComposer.Compose(value, font,
            new TextRunOptions { MissingGlyphPolicy = MissingGlyphPolicy.Refuse });

        Assert.Equal(LogicalGids(font, "AB"), run.GlyphIds);
        Assert.Equal(0x10FFFF, run.MissingCodePoint);
        Assert.Equal(1, run.MissingCodePointCount);
        Assert.False(run.IsComplete);
        Assert.Contains("10FFFF", TextRunComposer.LastCoverageError);
    }

    /// <summary>
    /// The assertion that proves the space fallback is not silent: substituting a space and reporting
    /// nothing, a caller would let a Hebrew name lose a letter with no trace anywhere.
    /// </summary>
    [HebrewFontFact]
    public void UncoveredCodePoint_UnderSubstituteSpace_DrawsASpaceAndStillReportsIt()
    {
        var font = HebrewFontProbe.Program;
        string value = "A􏿿B";

        var run = TextRunComposer.Compose(value, font,
            new TextRunOptions { MissingGlyphPolicy = MissingGlyphPolicy.SubstituteSpace });

        Assert.Equal(LogicalGids(font, "A B"), run.GlyphIds);
        Assert.Equal(0x10FFFF, run.MissingCodePoint);
        Assert.False(run.IsComplete);
        Assert.NotNull(TextRunComposer.LastCoverageError);
    }

    /// <summary>
    /// A fully-covered run reports no failure — the control that stops the two tests above from
    /// passing against an implementation that always reports one.
    /// </summary>
    [HebrewFontFact]
    public void FullyCoveredText_ReportsNoMissingCodePointAndClearsTheDiagnostic()
    {
        var font = HebrewFontProbe.Program;

        // Make the slot dirty first, so this asserts clearing rather than never-having-been-set.
        TextRunComposer.Compose("􏿿", font, null);
        Assert.NotNull(TextRunComposer.LastCoverageError);

        var run = TextRunComposer.Compose(Shalom, font, null);

        Assert.True(run.IsComplete);
        Assert.Null(run.MissingCodePoint);
        Assert.Equal(0, run.MissingCodePointCount);
        Assert.Null(TextRunComposer.LastCoverageError);
    }

    // ── NeedsCompositeFont ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The threshold is Latin-1, not WinAnsi. U+0080–U+009F are control codes in Latin-1 but
    /// printable characters in WinAnsi, so writing the raw byte draws a different glyph — which is
    /// why they must route through a composite font even though they fit in one byte.
    /// </summary>
    [Theory]
    [InlineData("abc", false)]
    [InlineData("café", false)]          // U+00E9 is Latin-1
    [InlineData("שלום", true)]
    [InlineData("—", true)]         // em dash — WinAnsi 0x97, not Latin-1
    [InlineData("ab", true)]       // U+0092: Latin-1 control, WinAnsi curly quote
    [InlineData("", false)]
    [InlineData(null, false)]
    public void NeedsCompositeFont_KeysOnLatin1NotOnWinAnsi(string? text, bool expected)
    {
        Assert.Equal(expected, TextRunComposer.NeedsCompositeFont(text));
    }

    // ── /ToUnicode mapping ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every emitted glyph carries the code point it maps back to, and for unshaped scripts that is
    /// the character itself. Without this a document cannot be searched for the word visibly on
    /// its own page.
    /// </summary>
    [HebrewFontFact]
    public void EveryEmittedGlyphCarriesItsSourceCodePoint()
    {
        var font = HebrewFontProbe.Program;

        var run = TextRunComposer.Compose(Shalom, font, null);

        Assert.Equal(run.GlyphIds.Length, run.Glyphs.Count);
        var mapped = run.Glyphs.Select(g => g.CodePoint).OrderBy(c => c);
        var expected = Shalom.Select(c => (int)c).OrderBy(c => c);
        Assert.Equal(expected, mapped);

        for (int i = 0; i < run.GlyphIds.Length; i++)
            Assert.Equal(run.GlyphIds[i], run.Glyphs[i].GlyphId);
    }

    // ── degenerate input ────────────────────────────────────────────────────────────────────

    [HebrewFontFact]
    public void EmptyText_YieldsAnEmptyRunRatherThanThrowing()
    {
        var run = TextRunComposer.Compose("", HebrewFontProbe.Program, null);

        Assert.Empty(run.GlyphIds);
        Assert.Empty(run.Glyphs);
        Assert.Equal(0d, run.AdvanceGlyphSpaceUnits);
        Assert.True(run.IsComplete);
    }

    [Fact]
    public void NullFont_Throws()
    {
        Assert.Throws<System.ArgumentNullException>(
            () => TextRunComposer.Compose("abc", null, null));
    }
}
