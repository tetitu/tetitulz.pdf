// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// What <c>PdfExporter.AdvanceWidth</c> does with a code point the Standard-14 AFM table does not
/// carry — the fallback that keeps such a glyph from measuring <b>zero</b>.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep over the PDF exporter replaced
/// <c>units += w &gt; 0 ? w : UnknownGlyphWidthUnits</c> with <c>units += w</c> — so every
/// character outside the table contributed nothing to the measured width — and <b>all 438 Builder
/// tests stayed green</b>. <see cref="Standard14AdvanceWidthTests"/> covers the table being
/// selected at all; nothing covered what happens to a character the selected table has no entry
/// for.</para>
///
/// <para><b>What a zero advance costs.</b> Width feeds wrapping and every <c>Tm</c> the exporter
/// emits, so a run of such characters measures as an empty string: the line never wraps, the pen
/// never moves, and every glyph is painted at the same x — a legible word collapses into one
/// smudged column. It is silent, and it is the normal case for any non-Latin text drawn through
/// the Standard-14 path, which is what a caller gets before a covering font is resolved.</para>
///
/// <para><b>The two families answer differently on purpose, which is why both are here.</b>
/// Helvetica and Times are proportional and their AFM tables report a missing width of <b>0</b>,
/// so the fallback is what stands between an unmapped glyph and a zero advance. Courier is
/// monospaced and reports a real 600, so the fallback is never reached there — that arm is the
/// acceptance control this file is paired with, and it must keep passing under the mutation above.
/// Asserting only the refusals would leave "everything advances by the unknown width" looking
/// correct.</para>
/// </remarks>
public class MissingGlyphAdvanceTests
{
    private const double Size = 12.0;

    /// <summary>Hebrew aleph — outside every Latin-text AFM table in the Standard-14 set.</summary>
    private const string Unmapped = "א";

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Proportional_AGlyphOutsideTheTable_StillAdvances(bool bold, bool italic)
    {
        double one = PdfExporter.AdvanceWidth(Unmapped, bold, italic, Size);

        Assert.True(one > 0,
            $"a code point absent from the AFM table measured {one}; a zero advance paints every " +
            "following glyph on top of this one.");
    }

    /// <summary>
    /// The advance accumulates: two unmapped code points are wider than one. A single-character
    /// assertion alone would pass on an implementation that returned a constant per string.
    /// </summary>
    [Fact]
    public void Proportional_UnmappedGlyphsAccumulate()
    {
        double one = PdfExporter.AdvanceWidth(Unmapped, bold: false, italic: false, Size);
        double two = PdfExporter.AdvanceWidth(Unmapped + Unmapped, bold: false, italic: false, Size);

        Assert.True(two > one, $"two unmapped glyphs ({two}) must measure wider than one ({one}).");
    }

    /// <summary>
    /// A mapped and an unmapped character both advance, so a mixed string is wider than either
    /// alone — the shape a wrapped line of transliterated text actually has.
    /// </summary>
    [Fact]
    public void Proportional_AMixedStringIsWiderThanEitherPart()
    {
        double mapped = PdfExporter.AdvanceWidth("W", bold: false, italic: false, Size);
        double unmapped = PdfExporter.AdvanceWidth(Unmapped, bold: false, italic: false, Size);
        double mixed = PdfExporter.AdvanceWidth("W" + Unmapped, bold: false, italic: false, Size);

        Assert.True(mixed > mapped, $"'W{Unmapped}' ({mixed}) must be wider than 'W' ({mapped}).");
        Assert.True(mixed > unmapped, $"'W{Unmapped}' ({mixed}) must be wider than the aleph alone ({unmapped}).");
    }

    // ── the acceptance control ────────────────────────────────────────────────────────
    // Courier's table reports a real missing width, so an unmapped glyph never reaches the
    // fallback. This arm is unaffected by the mutation the refusals above are written for, and it
    // is what shows they are pinning the fallback rather than "every glyph is 500 wide".

    /// <summary>
    /// Courier is monospaced: an unmapped code point advances by exactly the same amount as a
    /// mapped one, because its AFM missing width <i>is</i> the family's single advance.
    /// </summary>
    [Fact]
    public void Monospaced_AnUnmappedGlyphAdvancesLikeAMappedOne()
    {
        double mapped = PdfExporter.AdvanceWidth(
            "W", bold: false, italic: false, Size, PdfExporter.StdFamily.Mono);
        double unmapped = PdfExporter.AdvanceWidth(
            Unmapped, bold: false, italic: false, Size, PdfExporter.StdFamily.Mono);

        Assert.Equal(mapped, unmapped, precision: 6);
    }

    /// <summary>
    /// And the two families disagree about the unmapped glyph, which is the evidence that each is
    /// reading its own table's missing width rather than one shared constant.
    /// </summary>
    [Fact]
    public void TheTwoFamiliesGiveTheUnmappedGlyphDifferentAdvances()
    {
        double sans = PdfExporter.AdvanceWidth(
            Unmapped, bold: false, italic: false, Size, PdfExporter.StdFamily.Sans);
        double mono = PdfExporter.AdvanceWidth(
            Unmapped, bold: false, italic: false, Size, PdfExporter.StdFamily.Mono);

        Assert.NotEqual(sans, mono, precision: 6);
    }
}
