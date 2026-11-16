// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Fonts;

/// <summary>
/// A font whose only Unicode cmap is <b>format 12</b> can be subsetted. Until 2026-08-15 it could
/// not: the subsetter read only format 4, so such a font produced a structurally valid subset
/// containing nothing but <c>.notdef</c> and every glyph in the document rendered as a blank box.
/// </summary>
/// <remarks>
/// <para>Format 12 is not exotic — it is what any font with astral coverage uses (Noto CJK, emoji
/// and symbol faces), and it is also where a font puts its Unicode mapping when it has more than
/// 65 536 mappable code points.</para>
/// <para><b>Two defects, and the second was invisible behind the first.</b> The subtable chooser
/// scored platform 3 / encoding 10 — Windows UCS-4, where format 12 lives — at <c>-1</c>, so a font
/// carrying <i>only</i> (3,10) matched nothing and reported "no Windows or Unicode subtable"
/// instead of reaching the format check at all. Fixing the reader without fixing the chooser would
/// have left those fonts exactly as broken.</para>
/// <para>The fixtures are built here, and carry <b>no format-4 subtable</b> — a font with both
/// would fall back to format 4 and prove nothing.</para>
/// </remarks>
public class CmapFormat12SubsetTests
{
    [Fact]
    public void Subset_AFontWhoseOnlyCmapIsFormat12_MapsCharactersInsteadOfProducingNotdefOnly()
    {
        // 'A'..'C' → gids 1..3, declared through a format-12 subtable under (3,10).
        byte[] font = FontWithFormat12Cmap(platformId: 3, encodingId: 10);

        byte[] subset = PdfFontSubsetter.Subset(font, "AB");

        Assert.Null(PdfFontSubsetter.LastSubsetError);
        Assert.NotNull(subset);
        // A subset that mapped nothing is the defect's signature: it still parses, it is just empty
        // of everything but .notdef. Asserting the glyphs survived is what separates the two.
        Assert.True(subset.Length > font.Length / 4,
            $"subset is {subset.Length} bytes from a {font.Length}-byte font, which is the " +
            "notdef-only shape this test exists to catch");
    }

    [Fact]
    public void Subset_AFontWithOnlyAWindowsUcs4Subtable_FindsItAtAll()
    {
        // The chooser bug in isolation: (3,10) scored -1, so bestOffset stayed 0 and the reader was
        // never reached. The reported error named the wrong cause entirely.
        byte[] font = FontWithFormat12Cmap(platformId: 3, encodingId: 10);

        PdfFontSubsetter.Subset(font, "A");

        Assert.Null(PdfFontSubsetter.LastSubsetError);
    }

    [Fact]
    public void Subset_AFormat12CmapUnderPlatformZero_IsAlsoRead()
    {
        // (0,4) is the other place format 12 legitimately lives.
        byte[] font = FontWithFormat12Cmap(platformId: 0, encodingId: 4);

        PdfFontSubsetter.Subset(font, "A");

        Assert.Null(PdfFontSubsetter.LastSubsetError);
    }

    [Fact]
    public void Subset_AFormat12CmapDeclaringMoreGroupsThanFit_IsBoundedAndReported()
    {
        // numGroups is file-controlled. A declared count the file cannot hold must be clamped
        // before any array is sized, and said out loud — a silently truncated cmap is a font that
        // renders some characters as blanks with no explanation.
        byte[] font = FontWithFormat12Cmap(3, 10, declaredGroupsOverride: 100_000);

        PdfFontSubsetter.Subset(font, "A");

        Assert.NotNull(PdfFontSubsetter.LastSubsetError);
        Assert.Contains("groups", PdfFontSubsetter.LastSubsetError!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A font whose only <c>cmap</c> is format 0 is now <b>subsetted</b>, not refused.
    /// </summary>
    /// <remarks>
    /// <para>This replaces <c>Subset_AFormat0Cmap_IsStillRefusedByName</c>, which asserted the
    /// opposite and was correct when it was written. Format 0 is now read, so the old test is not
    /// edited into agreement — it is <b>renamed to its new claim</b>, per CLAUDE.md: a test whose
    /// name and body disagree is falsifiable and therefore invisible to the guards, which is how a
    /// defect keeps green coverage for its whole life.</para>
    ///
    /// <para>What it was protecting — that an unread format is named rather than passed over —
    /// survives in <see cref="Subset_AFormat2Cmap_IsStillRefusedByName"/>.</para>
    ///
    /// <para>The defect: an older symbol or icon face whose only <c>cmap</c> is <c>(1,0)</c>
    /// Macintosh Roman format 0 was selected by the priority scan and then rejected by the format
    /// test, producing a subset containing <c>.notdef</c> alone — a blank page where the text
    /// was.</para>
    /// </remarks>
    [Fact]
    public void Subset_AFormat0Cmap_IsMappedRatherThanRefused()
    {
        byte[] font = FontWithFormat0Cmap();

        byte[] subset = PdfFontSubsetter.Subset(font, "A");

        Assert.Null(PdfFontSubsetter.LastSubsetError);
        Assert.NotNull(subset);
        Assert.True(subset.Length > font.Length / 4,
            $"subset is {subset.Length} bytes from a {font.Length}-byte font, which is the "
          + "notdef-only shape this test exists to catch");
    }

    /// <summary>
    /// The honest-reporting half, on a format that genuinely remains unread: formats this subsetter
    /// does not handle are still named rather than passed over silently.
    /// </summary>
    [Fact]
    public void Subset_AFormat2Cmap_IsStillRefusedByName()
    {
        byte[] font = FontWithFormat0Cmap(formatOverride: 2);

        PdfFontSubsetter.Subset(font, "A");

        Assert.NotNull(PdfFontSubsetter.LastSubsetError);
        Assert.Contains("format 2", PdfFontSubsetter.LastSubsetError!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  Fixtures — minimal but real TrueType fonts
    // ------------------------------------------------------------------

    private static byte[] FontWithFormat12Cmap(
        int platformId, int encodingId, int declaredGroupsOverride = -1)
    {
        // One group: 'A'(0x41)..'C'(0x43) -> gid 1..3.
        var sub = new List<byte>();
        W16(sub, 12);                 // format
        W16(sub, 0);                  // reserved
        W32(sub, 28);                 // length
        W32(sub, 0);                  // language
        W32(sub, declaredGroupsOverride >= 0 ? (uint)declaredGroupsOverride : 1u);
        W32(sub, 0x41); W32(sub, 0x43); W32(sub, 1);

        return Font(CmapTable(platformId, encodingId, sub.ToArray()));
    }

    /// <param name="formatOverride">
    /// Written into the format field instead of 0, so the same body can stand in for a format this
    /// subsetter still does not read — which is what keeps the refusal path tested now that
    /// format 0 is handled.
    /// </param>
    private static byte[] FontWithFormat0Cmap(int formatOverride = 0)
    {
        var sub = new List<byte>();
        W16(sub, (ushort)formatOverride);
        W16(sub, 262);    // length
        W16(sub, 0);      // language
        for (int i = 0; i < 256; i++) sub.Add((byte)(i == 0x41 ? 1 : 0));
        return Font(CmapTable(3, 1, sub.ToArray()));
    }

    private static byte[] CmapTable(int platformId, int encodingId, byte[] subtable)
    {
        var t = new List<byte>();
        W16(t, 0);   // version
        W16(t, 1);   // numTables
        W16(t, (ushort)platformId);
        W16(t, (ushort)encodingId);
        W32(t, 12);  // offset to the subtable
        t.AddRange(subtable);
        return t.ToArray();
    }

    /// <summary>A minimal TrueType font: four glyphs, empty outlines, plus the supplied cmap.</summary>
    private static byte[] Font(byte[] cmap)
    {
        const int glyphCount = 4;

        var head = new byte[54];
        head[50] = 0; head[51] = 1;                  // indexToLocFormat = 1 (long)
        head[18] = 0x03; head[19] = 0xE8;            // unitsPerEm = 1000

        var maxp = new byte[32];
        maxp[0] = 0x00; maxp[1] = 0x01; maxp[2] = 0x00; maxp[3] = 0x00;
        maxp[4] = 0x00; maxp[5] = (byte)glyphCount;

        var hhea = new byte[36];
        hhea[34] = 0x00; hhea[35] = (byte)glyphCount;   // numberOfHMetrics

        var hmtx = new byte[glyphCount * 4];
        for (int i = 0; i < glyphCount; i++) { hmtx[i * 4] = 0x02; hmtx[i * 4 + 1] = 0x00; }

        var loca = new byte[(glyphCount + 1) * 4];      // all zero: every glyph empty
        var glyf = new byte[4];

        return Tetitulz.Pdf.Core.Fonts.SfntBuilder.Build(
            Tetitulz.Pdf.Core.Fonts.SfntBuilder.VersionTrueType,
            new List<(string, byte[])>
            {
                ("cmap", cmap), ("glyf", glyf), ("head", head), ("hhea", hhea),
                ("hmtx", hmtx), ("loca", loca), ("maxp", maxp),
            });
    }

    private static void W16(List<byte> to, ushort v) { to.Add((byte)(v >> 8)); to.Add((byte)v); }

    private static void W32(List<byte> to, uint v)
    {
        to.Add((byte)(v >> 24)); to.Add((byte)(v >> 16));
        to.Add((byte)(v >> 8)); to.Add((byte)v);
    }
}
