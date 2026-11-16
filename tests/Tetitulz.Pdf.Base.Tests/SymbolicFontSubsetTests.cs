// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// A symbolic TrueType font — one whose only usable cmap is Windows Symbol <c>(3,0)</c>, keyed in
/// the PUA at <c>0xF000 + code</c> — must subset like any other.
/// </summary>
/// <remarks>
/// <b>It subsetted to <c>.notdef</c> alone.</b> The subtable scoring gave <c>(3,0)</c> nothing, so
/// the <c>(1,0)</c> format-0 table these fonts also carry won at priority 1 and the font was
/// refused by the format gate. Measured before the fix: <c>symbol.ttf</c> and <c>wingding.ttf</c>,
/// 192 glyphs in and 1 out.
///
/// <para><b>The backlog row named the wrong gate, and that matters for the fix.</b> It said the
/// refusal message was "no Windows or Unicode subtable"; the message these fonts actually got was
/// "the font's only Unicode cmap is format 0". Scoring <c>(3,0)</c> alone would therefore have
/// changed nothing visible except the wording — the lookup still asks for U+0041 in a table keyed
/// at 0xF041 — which is why the fold is part of the same change rather than a follow-up.</para>
///
/// <para><b>No format-0 reader was needed, contrary to the row.</b> Measured on the shipping fonts:
/// symbol, wingdings and marlett all carry a <c>(3,0)</c> <b>format 4</b> table (starting at
/// 0xF020, 0xF020 and 0xF030). The format-0 table is the one that was winning, not the one that was
/// needed.</para>
/// </remarks>
public class SymbolicFontSubsetTests
{
    private const string Available = "HeloZ";

    [Fact]
    public void ASymbolicFontSubsetsToRealGlyphsRatherThanNotdefAlone()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.BuildSymbolic(Available), "Hel");

        Assert.NotNull(subset);
        Assert.Null(PdfFontSubsetter.LastSubsetError);
        Assert.Equal(4, GlyphCount(subset!));   // .notdef + H, e, l
    }

    /// <summary>
    /// The subset stays SYMBOLIC: its cmap declares encoding 0, and its keys are the PUA codes the
    /// source used.
    /// </summary>
    /// <remarks>
    /// A subset that advertised <c>(3,1)</c> would be claiming Unicode BMP for PUA code points, and
    /// a reader applying §9.6.6.4 looks for a <c>(3,0)</c> table first — finding a Unicode one, it
    /// takes the non-symbolic path and resolves the glyphs through an encoding instead. The subset
    /// must be the same KIND of font as the original.
    /// </remarks>
    [Fact]
    public void TheSubsetOfASymbolicFontIsItselfSymbolic()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.BuildSymbolic(Available), "Hel");

        Assert.Equal(0, CmapEncodingId(subset!));

        // The mapped codes are 0xF048/0xF065/0xF06C, not 0x48/0x65/0x6C.
        List<int> codes = MappedCodes(subset!);
        Assert.All(codes, c => Assert.InRange(c, 0xF000, 0xF0FF));
        Assert.Equal(3, codes.Count);
    }

    /// <summary>
    /// The control: an ordinary <c>(3,1)</c> font is untouched by any of this and still declares
    /// encoding 1 with plain code points.
    /// </summary>
    [Fact]
    public void ANonSymbolicFontSubsetIsUnchanged()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.Build(Available), "Hel");

        Assert.NotNull(subset);
        Assert.Equal(1, CmapEncodingId(subset!));
        Assert.All(MappedCodes(subset!), c => Assert.InRange(c, 0x20, 0x7E));
    }

    /// <summary>
    /// A character the symbolic font does not map is still reported, so the fold has not turned the
    /// refusal path off.
    /// </summary>
    /// <remarks>
    /// The floor: without it, a lookup that returned some glyph for everything would satisfy every
    /// case above and the diagnostic would silently stop working.
    /// </remarks>
    [Fact]
    public void AnUnmappedCharacterIsStillReported()
    {
        PdfFontSubsetter.Subset(MinimalTrueType.BuildSymbolic(Available), "HQ");

        Assert.NotNull(PdfFontSubsetter.LastSubsetError);
        Assert.Contains("1 of 2", PdfFontSubsetter.LastSubsetError!);
    }

    // ------------------------------------------------------------------

    private static int GlyphCount(byte[] sfnt) => ReadU16(sfnt, TableOffset(sfnt, "maxp") + 4);

    private static int CmapEncodingId(byte[] sfnt) => ReadU16(sfnt, TableOffset(sfnt, "cmap") + 6);

    /// <summary>Every code the emitted format-4 subtable maps, read out of its endCode array.</summary>
    private static List<int> MappedCodes(byte[] sfnt)
    {
        int cmap = TableOffset(sfnt, "cmap");
        int sub = cmap + (int)ReadU32(sfnt, cmap + 8);

        Assert.Equal(4, ReadU16(sfnt, sub));                 // format
        int segCount = ReadU16(sfnt, sub + 6) / 2;

        var codes = new List<int>();
        for (int seg = 0; seg < segCount; seg++)
        {
            int end = ReadU16(sfnt, sub + 14 + seg * 2);
            int start = ReadU16(sfnt, sub + 16 + segCount * 2 + seg * 2);
            if (start == 0xFFFF) continue;                   // the mandatory terminator
            for (int c = start; c <= end; c++) codes.Add(c);
        }
        return codes;
    }

    private static int TableOffset(byte[] sfnt, string tag)
    {
        int n = ReadU16(sfnt, 4);
        for (int i = 0; i < n; i++)
        {
            int dir = 12 + i * 16;
            if (sfnt[dir] == (byte)tag[0] && sfnt[dir + 1] == (byte)tag[1]
                && sfnt[dir + 2] == (byte)tag[2] && sfnt[dir + 3] == (byte)tag[3])
            {
                return (int)ReadU32(sfnt, dir + 8);
            }
        }

        Assert.Fail($"the subset has no {tag} table");
        return -1;
    }

    private static int ReadU16(byte[] d, int at) => (d[at] << 8) | d[at + 1];

    private static uint ReadU32(byte[] d, int at)
        => (uint)((d[at] << 24) | (d[at + 1] << 16) | (d[at + 2] << 8) | d[at + 3]);
}
