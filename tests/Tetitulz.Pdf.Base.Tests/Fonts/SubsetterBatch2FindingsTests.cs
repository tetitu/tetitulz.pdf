// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Fonts;

/// <summary>
/// The Fonts sweep, batch 2: what reading <see cref="PdfFontSubsetter"/>'s TrueType engine found.
/// Astral characters never survived it, and five shapes of hostile font threw out of it instead of
/// being refused or reported.
/// </summary>
public class SubsetterBatch2FindingsTests
{
    private const string Emoji = "\U0001F600";

    // ---------------------------------------------------------------- astral characters

    /// <summary>
    /// Text with an emoji, through the <c>string</c> overload. Each UTF-16 unit was taken as a code
    /// point, so the emoji arrived as two surrogate halves no cmap holds, and was reported missing.
    /// The subset is read back through Core's own TrueType reader, an independent implementation.
    /// </summary>
    [Fact]
    public void Subset_TextWithAnAstralCharacter_KeepsIt()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.Build("A" + Emoji), "A" + Emoji);

        Assert.Null(PdfFontSubsetter.LastSubsetError);
        var program = TrueTypeFontProgram.Parse(subset);
        Assert.True(program.TryGetGlyph(0x1F600, out ushort g) && g != 0);
        Assert.True(program.TryGetGlyph('A', out ushort a) && a != 0);
    }

    /// <summary>The <c>IEnumerable&lt;char&gt;</c> overload pairs surrogates the same way.</summary>
    [Fact]
    public void Subset_CharactersWithASurrogatePair_KeepTheCharacter()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.Build(Emoji), Emoji.ToCharArray());

        Assert.Null(PdfFontSubsetter.LastSubsetError);
        Assert.True(TrueTypeFontProgram.Parse(subset).TryGetGlyph(0x1F600, out ushort g) && g != 0);
    }

    /// <summary>
    /// Code points given directly reached the glyph, but the rebuilt cmap was format 4 alone, whose
    /// 16-bit casts wrote U+1F600 as U+F600: the emoji unmapped, and a private-use code mapped to
    /// its glyph. A (3,10) format-12 subtable carries it now.
    /// </summary>
    [Fact]
    public void Subset_AnAstralCodePoint_IsWrittenAsItselfNotItsLow16Bits()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.Build("A" + Emoji), new[] { (int)'A', 0x1F600 });

        var program = TrueTypeFontProgram.Parse(subset);
        Assert.True(program.TryGetGlyph(0x1F600, out ushort g) && g != 0);
        Assert.False(program.TryGetGlyph(0xF600, out ushort pua) && pua != 0);
        Assert.True(program.TryGetGlyph('A', out ushort a) && a != 0);
    }

    /// <summary>The control: BMP-only text keeps the single (3,1) subtable it always had.</summary>
    [Fact]
    public void Subset_BmpOnlyText_WritesOneCmapSubtable()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.Build("AB"), "AB");
        var (cmap, _) = Table(subset, "cmap");
        Assert.Equal(1, U16(subset, cmap + 2));
    }

    /// <summary>
    /// Read from the raw bytes, because a lenient parser does not need the field: the format-4
    /// subtable's declared length is its true length, 16 + 8 × segCount — seven header words, four
    /// arrays and the <c>reservedPad</c>. It said 14 + 8 × segCount, two bytes short in every subset,
    /// and the format-12 subtable placed after it by that length began two bytes early.
    /// </summary>
    [Fact]
    public void Subset_TheFormat4LengthCountsTheReservedPad()
    {
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.Build("A" + Emoji), "A" + Emoji);
        var (cmap, _) = Table(subset, "cmap");

        int format4 = cmap + (int)U32(subset, cmap + 4 + 4);
        Assert.Equal(4, U16(subset, format4));
        int segCount = U16(subset, format4 + 6) / 2;
        int declared = U16(subset, format4 + 2);
        Assert.Equal(16 + 8 * segCount, declared);

        // And the next subtable starts exactly where the declared length ends.
        int format12 = cmap + (int)U32(subset, cmap + 12 + 4);
        Assert.Equal(format4 + declared, format12);
        Assert.Equal(12, U16(subset, format12));
    }

    // ---------------------------------------------------------------- hostile fonts

    /// <summary>
    /// Malformed: maxp declares two glyphs, and the cmap maps B and C to glyphs 2 and 3. Those ids
    /// reached the glyph list and indexed loca out of range. They are unmapped and reported now.
    /// </summary>
    [Fact]
    public void Subset_Malformed_ACmapGlyphPastNumGlyphs_IsUnmappedNotThrown()
    {
        byte[] font = MinimalTrueType.Build("ABC");
        var (maxp, _) = Table(font, "maxp");
        SetU16(font, maxp + 4, 2);

        byte[] subset = PdfFontSubsetter.Subset(font, "ABC");

        Assert.Contains("map to a glyph it does not have", PdfFontSubsetter.LastSubsetError);
        Assert.True(TrueTypeFontProgram.Parse(subset).TryGetGlyph('A', out ushort a) && a != 0);
    }

    /// <summary>
    /// Malformed: glyph 1 is a composite naming glyph 500 in a four-glyph font. The component is
    /// dropped and reported, rather than indexing loca out of range.
    /// </summary>
    [Fact]
    public void Subset_Malformed_ACompositeComponentPastNumGlyphs_IsDropped()
    {
        byte[] font = MinimalTrueType.Build("ABC");
        var (glyf, _) = Table(font, "glyf");
        // numberOfContours -1, a bounding box, then one component: flags ARG_1_AND_2_ARE_WORDS,
        // glyph 500, offsets 0 and 0.
        int at = glyf;
        foreach (int word in new[] { 0xFFFF, 0, 0, 500, 700, 0x0001, 500, 0, 0 }) { SetU16(font, at, word); at += 2; }

        byte[] subset = PdfFontSubsetter.Subset(font, "A");

        Assert.Contains("composite glyph component", PdfFontSubsetter.LastSubsetError);
        Assert.Equal(2, MinimalTrueType.ReadNumGlyphs(subset));   // .notdef and the composite
    }

    /// <summary>
    /// Malformed: a 'glyf' the directory says is 4 bytes, under loca offsets of 20 and more. The
    /// glyph copy read past the array and threw; the glyphs are emptied and reported now.
    /// </summary>
    [Fact]
    public void Subset_Malformed_LocaPastGlyf_EmptiesTheGlyphsNotThrows()
    {
        byte[] font = MinimalTrueType.Build("AB");
        SetDirectoryLength(font, "glyf", 4);

        byte[] subset = PdfFontSubsetter.Subset(font, "AB");

        Assert.Contains("point past the 4-byte 'glyf' table", PdfFontSubsetter.LastSubsetError);
        Assert.Equal(3, MinimalTrueType.ReadNumGlyphs(subset));
    }

    /// <summary>
    /// Truncated head, hhea or maxp: the subset writes a field past their end. Refused by name,
    /// where it threw an index exception.
    /// </summary>
    [Theory]
    [InlineData("head", 53)]
    [InlineData("hhea", 35)]
    [InlineData("maxp", 5)]
    public void Subset_Truncated_ARequiredTableShorterThanItsFields_IsRefusedByName(string tag, int length)
    {
        byte[] font = MinimalTrueType.Build("A");
        SetDirectoryLength(font, tag, length);

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => PdfFontSubsetter.Subset(font, "A"));
        Assert.Contains($"'{tag}' table is {length} bytes", ex.Message);
    }

    /// <summary>
    /// Truncated loca, too short to locate a single glyph: refused, where the metrics loop indexed
    /// an empty array.
    /// </summary>
    [Fact]
    public void Subset_Truncated_ALocaWithNoGlyph_IsRefused()
    {
        byte[] font = MinimalTrueType.Build("A");
        SetDirectoryLength(font, "loca", 2);

        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => PdfFontSubsetter.Subset(font, "A"));
        Assert.Contains("no glyph", ex.Message);
    }

    // ---------------------------------------------------------------- sfnt patching

    private static (int Offset, int Record) Table(byte[] font, string tag)
    {
        int count = U16(font, 4);
        for (int i = 0; i < count; i++)
        {
            int record = 12 + i * 16;
            if (Encoding.ASCII.GetString(font, record, 4) == tag)
                return ((int)U32(font, record + 8), record);
        }
        throw new InvalidOperationException("no " + tag);
    }

    private static void SetDirectoryLength(byte[] font, string tag, int length)
    {
        var (_, record) = Table(font, tag);
        font[record + 12] = (byte)(length >> 24); font[record + 13] = (byte)(length >> 16);
        font[record + 14] = (byte)(length >> 8);  font[record + 15] = (byte)length;
    }

    private static int U16(byte[] b, int p) => (b[p] << 8) | b[p + 1];
    private static uint U32(byte[] b, int p) => ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];
    private static void SetU16(byte[] b, int p, int v) { b[p] = (byte)(v >> 8); b[p + 1] = (byte)v; }
}
