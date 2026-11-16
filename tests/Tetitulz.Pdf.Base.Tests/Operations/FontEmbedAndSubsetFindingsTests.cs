// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Font findings from the assembly scan: WinAnsi width lookup, hostile/truncated font files, the
/// subset cmap's uint16 length field, and a format-12-only cmap subsetting to nothing.
/// </summary>
public class FontEmbedAndSubsetFindingsTests
{
    // ==================================================================
    //  PdfFontEmbedder — /Widths must be indexed by WinAnsi code, not Latin-1
    // ==================================================================

    /// <summary>WinAnsi 0x92 is U+2019 (right single quotation mark), not U+0092.</summary>
    private const int WinAnsiRightSingleQuote = 0x92;

    [Fact]
    public void EmbedFont_WidthsAreIndexedByWinAnsiCode_NotByLatin1CodePoint()
    {
        // The font dictionary declares /WinAnsiEncoding, so /Widths entry i is the advance for
        // WinAnsi code 32+i. CP1252 puts typographic punctuation in 0x80-0x9F where Latin-1 has
        // C1 controls, so looking the code up as a Unicode code point found nothing and fell back
        // to the .notdef advance: every line with a smart quote was mis-spaced.
        byte[] font = MinimalTrueType.BuildWithDistinctWidths("\u2019");
        var metrics = PdfFontEmbedder.GetFontMetrics(font);

        // The builder's units-per-em is 1000, so glyph-space advances need no scaling.
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance, metrics.GetWidth('\u2019'));
        Assert.Equal(MinimalTrueType.NotdefAdvance, metrics.GetWidth(0x92));

        int[] widths = EmbedAndReadWidths(font);
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance, widths[WinAnsiRightSingleQuote - 32]);
    }

    [Fact]
    public void EmbedFont_LatinRangeWidthsAreUnchanged()
    {
        // The WinAnsi mapping must not disturb the range where WinAnsi and Latin-1 agree.
        byte[] font = MinimalTrueType.BuildWithDistinctWidths("A");
        int[] widths = EmbedAndReadWidths(font);

        Assert.Equal(MinimalTrueType.MappedGlyphAdvance, widths['A' - 32]);
        Assert.Equal(MinimalTrueType.NotdefAdvance, widths['B' - 32]);
    }

    /// <summary>
    /// The embedded font program is a real zlib stream holding the font, checked by an
    /// independent decoder. This library's own Flate filter hands back bytes it cannot inflate, so
    /// a <c>Compress</c> that returned the font untouched under <c>/Filter /FlateDecode</c> read back
    /// perfectly here and was garbage to every other reader — the mutation sweep of the zlib
    /// consolidation found no test that could tell.
    /// </summary>
    [Fact]
    public void EmbedFont_TheFontProgramIsAZlibStreamAnIndependentDecoderReadsBack()
    {
        byte[] font = MinimalTrueType.BuildWithDistinctWidths("A");
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        PdfFontEmbedder.EmbedFont(input, output, font, "F9");

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var fonts = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources?[PdfName.Font]));
        var fontDict = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(fonts[new PdfName("F9")]));
        var descriptor = Assert.IsAssignableFrom<PdfDictionary>(doc.ResolveObject(fontDict[PdfName.FontDescriptor]));
        var program = Assert.IsAssignableFrom<PdfStream>(doc.ResolveObject(descriptor[PdfName.FontFile2]));
        Assert.Equal(PdfName.FlateDecode, doc.ResolveObject(program[PdfName.Filter]) as PdfName);

        // RFC 1950: a two-byte header whose value is a multiple of 31, then raw deflate — inflated
        // here by System.IO.Compression, not by this library.
        byte[] raw = program.RawData;
        Assert.Equal(0x78, raw[0]);
        Assert.Equal(0, ((raw[0] << 8) | raw[1]) % 31);
        using var deflate = new System.IO.Compression.DeflateStream(
            new MemoryStream(raw, 2, raw.Length - 2), System.IO.Compression.CompressionMode.Decompress);
        using var inflated = new MemoryStream();
        deflate.CopyTo(inflated);
        Assert.Equal(font, inflated.ToArray());
    }

    /// <summary>Embeds <paramref name="font"/> into a one-page PDF and reads its /Widths back.</summary>
    private static int[] EmbedAndReadWidths(byte[] font)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        PdfFontEmbedder.EmbedFont(input, output, font, "F9");

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var fonts = doc.ResolveObject(doc.Pages[0].Resources?[PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);
        var fontDict = doc.ResolveObject(fonts[new PdfName("F9")]) as PdfDictionary;
        Assert.NotNull(fontDict);
        Assert.Equal("WinAnsiEncoding", (fontDict[PdfName.Encoding] as PdfName)?.Value);

        var widthsArr = doc.ResolveObject(fontDict[PdfName.Widths]) as PdfArray;
        Assert.NotNull(widthsArr);
        Assert.Equal(32, (int)((fontDict[PdfName.FirstChar] as PdfNumber).LongValue));

        var widths = new int[widthsArr.Count];
        for (int i = 0; i < widthsArr.Count; i++)
            widths[i] = (int)((widthsArr[i] as PdfNumber).LongValue);
        return widths;
    }

    // ==================================================================
    //  PdfFontEmbedder — hostile / truncated font files
    // ==================================================================

    [Fact]
    public void GetFontMetrics_TruncatedFontHeader_ReturnsDefaultsInsteadOfFaulting()
    {
        // 13 bytes with a non-zero numTables: the directory loop used to index straight past the
        // end and throw IndexOutOfRangeException out of a public API that documents no such thing.
        var truncated = new byte[13];
        truncated[4] = 0x00; truncated[5] = 0x64;   // numTables = 100

        var metrics = PdfFontEmbedder.GetFontMetrics(truncated);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.Ascent);       // nothing was readable, so nothing was claimed
        Assert.Equal(1000, metrics.UnitsPerEm);
    }

    [Fact]
    public void GetFontMetrics_TableLengthPointingOutsideTheFile_DoesNotAbandonTheOtherTables()
    {
        // A directory entry whose offset+length exceeds the file is clamped, not followed — and
        // the tables that ARE readable must still be parsed, so the caller gets real metrics
        // rather than defaults.
        byte[] hostile = (byte[])MinimalTrueType.Build("A").Clone();
        SetTableLength(hostile, "name", uint.MaxValue);

        var metrics = PdfFontEmbedder.GetFontMetrics(hostile);

        Assert.Equal(1000, metrics.UnitsPerEm);   // head
        Assert.Equal(800, metrics.Ascent);        // hhea
        Assert.Equal(-200, metrics.Descent);
    }

    [Fact]
    public void GetFontMetrics_TableStartingNearTheEndOfTheFile_ReadsZerosInsteadOfFaulting()
    {
        // The bounds check on the reader itself, isolated. Every other hostile-font test is
        // covered by the directory-loop guard, so removing the per-read check alone left them
        // green — a finding about those tests, not about the check. Here the 'head' entry is
        // rewritten to start four bytes before EOF with length 4, so offset+length is exactly
        // the file size and nothing is clamped: ParseHead then seeks there, skips 18 bytes and
        // reads unitsPerEm from past the end. Unchecked, that is IndexOutOfRangeException out
        // of a public API.
        byte[] hostile = (byte[])MinimalTrueType.Build("A").Clone();
        SetTableOffsetAndLength(hostile, "head", (uint)(hostile.Length - 4), 4);

        var metrics = PdfFontEmbedder.GetFontMetrics(hostile);

        // unitsPerEm read past the end yields 0, and TrueTypeMetrics keeps its 1000 default only
        // when head is absent — so 0 here is the evidence the read happened and was bounded.
        Assert.Equal(0, metrics.UnitsPerEm);
    }

    [Fact]
    public void GetFontMetrics_FourByteReadRunningPastTheEnd_IsBoundedNotFaulted()
    {
        // The 32-bit read path, isolated. 'post' is relocated two bytes before EOF, so
        // ParsePost seeks there, skips the 4-byte version and reads italicAngle — a Fixed
        // (16.16), i.e. a four-byte read starting past the end. Unchecked that is
        // IndexOutOfRangeException out of the public GetFontMetrics.
        //
        // Kept separate from the 16-bit case above because the two reads have separate checks:
        // a mutation removing only one of them left the other test green, which is a fact about
        // that test rather than about the guard.
        byte[] hostile = (byte[])MinimalTrueType.Build("A").Clone();
        SetTableOffsetAndLength(hostile, "post", (uint)(hostile.Length - 2), 2);

        var metrics = PdfFontEmbedder.GetFontMetrics(hostile);

        Assert.Equal(0.0, metrics.ItalicAngle);
        // The tables parsed before 'post' must still have been read, so this is not a
        // parse-abandoned-everything result.
        Assert.Equal(1000, metrics.UnitsPerEm);
        Assert.Equal(800, metrics.Ascent);
    }

    [Fact]
    public void GetFontMetrics_NameTableStringOffsetPastEnd_ReturnsEmptyNameInsteadOfFaulting()
    {
        // ReadBytes computed `_data.Length - _pos` as a NEGATIVE count once the cursor was past
        // the end, and Array.Copy throws on that. Truncating the file leaves the name records
        // pointing beyond it.
        byte[] font = MinimalTrueType.Build("A");
        byte[] truncated = font.Take(font.Length / 2).ToArray();

        var metrics = PdfFontEmbedder.GetFontMetrics(truncated);
        Assert.NotNull(metrics);
    }


    // ==================================================================
    //  PdfFontSubsetter — hostile input, format-12 cmap, cmap length overflow
    // ==================================================================

    [Fact]
    public void Subset_GlyfTableDeclaringAnAbsurdLength_IsClampedAndReported()
    {
        // `new byte[0xFFFFFFFF]` — OutOfMemoryException, or a negative-length Array.Copy after
        // the (int) cast. Either way the caller saw a fault rather than a bounded refusal.
        byte[] font = MinimalTrueType.Build("A");
        byte[] hostile = (byte[])font.Clone();
        SetTableLength(hostile, "glyf", uint.MaxValue);

        byte[] subset = PdfFontSubsetter.Subset(hostile, "A");

        Assert.NotNull(subset);
        Assert.NotNull(PdfFontSubsetter.LastSubsetError);
        Assert.Contains("glyf", PdfFontSubsetter.LastSubsetError, StringComparison.Ordinal);
        Assert.Contains("clamped", PdfFontSubsetter.LastSubsetError, StringComparison.Ordinal);
    }

    [Fact]
    public void Subset_DirectoryDeclaringMoreTablesThanTheFileHolds_IsBoundedAndStillSubsets()
    {
        // numTables = 1000 in a small file used to die with IndexOutOfRangeException in
        // ParseOffsetTable, reading 984 records that are not there. The loop now stops at the
        // buffer's end, so the ten real tables are still found and the subset is produced.
        byte[] font = MinimalTrueType.Build("A");
        byte[] hostile = (byte[])font.Clone();
        hostile[4] = 0x03; hostile[5] = 0xE8;   // numTables = 1000

        byte[] subset = PdfFontSubsetter.Subset(hostile, "A");

        Assert.Equal(2, MinimalTrueType.ReadNumGlyphs(subset));   // .notdef + A
        Assert.True(MinimalTrueType.ReadCmap(subset).ContainsKey('A'));
    }

    /// <summary>
    /// A cmap in a format the subsetter cannot read is <b>named</b>, rather than silently yielding
    /// a font containing only <c>.notdef</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Renamed and retargeted 2026-08-15, because the behaviour changed under it.</b> This
    /// was <c>Subset_CmapThatIsNotFormat4_…</c> and used format <b>12</b> as its example. Format 12
    /// is now read — it is the common case for any font with astral coverage, and refusing it made
    /// Noto CJK, emoji and symbol fonts unsubsettable — so "not format 4" no longer implies "cannot
    /// be read". Editing the assertion and keeping the name would have left a test whose name
    /// asserts a rule the code no longer follows.</para>
    /// <para>Format <b>2</b> stands in for the formats still unread. It used to be format 6, and
    /// before that the claim was simply "not format 4" — the stand-in has now moved twice as
    /// formats 12, then 0 and 6, became readable. The stand-in is an example, not the claim, so
    /// moving it is not the same as editing an assertion to match the code; the rule under test is
    /// unchanged and is the one that matters: the subsetter used to pick a subtable, see a format
    /// it did not read, and return <i>without a word</i> — a structurally valid font containing only
    /// <c>.notdef</c>, which <c>EmbedFont</c> then embedded happily so every glyph rendered as a
    /// blank box. The caller's only signal was that the output was small, which is also what a real
    /// tiny subset looks like.</para>
    /// <para><c>CmapFormat12SubsetTests</c> covers the other side — that format 12 is now read —
    /// including a real format-0 subtable rather than a relabelled one, and that format 0 is read.</para>
    /// </remarks>
    [Fact]
    public void Subset_CmapInAFormatTheSubsetterCannotRead_SaysSoInsteadOfReturningNotdefOnly()
    {
        byte[] font = MinimalTrueType.Build("A");
        SetCmapSubtableFormat(font, 2);

        byte[] subset = PdfFontSubsetter.Subset(font, "A");

        Assert.NotNull(PdfFontSubsetter.LastSubsetError);
        Assert.Contains("format 2", PdfFontSubsetter.LastSubsetError, StringComparison.Ordinal);
        // The evidence must match the claim: nothing was mapped, so only .notdef survives.
        Assert.Equal(1, MinimalTrueType.ReadNumGlyphs(subset));
    }

    [Fact]
    public void Subset_RequestedCharacterMissingFromTheCmap_IsReported()
    {
        // The font maps only 'A'; asking for 'Z' as well must not silently yield .notdef.
        byte[] subset = PdfFontSubsetter.Subset(MinimalTrueType.Build("A"), "AZ");

        Assert.NotNull(PdfFontSubsetter.LastSubsetError);
        Assert.Contains("1 of 2", PdfFontSubsetter.LastSubsetError, StringComparison.Ordinal);
        Assert.Equal(2, MinimalTrueType.ReadNumGlyphs(subset));   // .notdef + A
    }

    [Fact]
    public void Subset_SucceedingSubsetReportsNoError()
    {
        // The counterpart: the diagnostic must stay null when every requested character mapped,
        // or it says nothing.
        byte[] font = MinimalTrueType.Build("ABC");
        byte[] subset = PdfFontSubsetter.Subset(font, "AB");

        Assert.Null(PdfFontSubsetter.LastSubsetError);
        Assert.Equal(3, MinimalTrueType.ReadNumGlyphs(subset));   // .notdef + A + B
    }

    [Fact]
    public void Subset_LastSubsetError_IsClearedAtTheStartOfEveryCall()
    {
        byte[] oddCmap = MinimalTrueType.Build("A");
        SetCmapSubtableFormat(oddCmap, 12);
        PdfFontSubsetter.Subset(oddCmap, "A");
        Assert.NotNull(PdfFontSubsetter.LastSubsetError);

        PdfFontSubsetter.Subset(MinimalTrueType.Build("AB"), "AB");
        Assert.Null(PdfFontSubsetter.LastSubsetError);
    }

    /// <summary>Rewrites the cmap subtable's format word, leaving everything else in place.</summary>
    private static void SetCmapSubtableFormat(byte[] font, int format)
    {
        (int offset, _) = FindTable(font, "cmap");
        int subtableOffset = offset + (int)ReadUInt32(font, offset + 8);
        font[subtableOffset] = (byte)(format >> 8);
        font[subtableOffset + 1] = (byte)format;
    }

    [Fact]
    public void Subset_ManyScatteredCharacters_ProducesACmapWhoseDeclaredLengthIsTheRealLength()
    {
        // One format-4 segment per character overflows the subtable's uint16 length field at
        // 8190 segments: a 72 KB table declaring 6478 bytes, which a conformant rasteriser reads
        // as a truncated segment array and maps most characters to the wrong glyph. Coalescing
        // contiguous runs is what keeps the count down; the declared length must match the bytes
        // either way, which is what a decoder actually depends on.
        var chars = new List<char>();
        for (int i = 0; i < 4000; i++) chars.Add((char)(0x3400 + i * 2));   // deliberately non-contiguous

        byte[] font = MinimalTrueType.Build(new string(chars.ToArray()));
        byte[] subset = PdfFontSubsetter.Subset(font, chars);

        AssertCmapSubtableLengthMatchesItsBytes(subset);

        // And the mapping must survive: a sample of the requested characters resolves to a glyph.
        var map = MinimalTrueType.ReadCmap(subset);
        foreach (char c in new[] { chars[0], chars[1999], chars[3999] })
            Assert.True(map.ContainsKey(c), $"U+{(int)c:X4} is missing from the subset cmap");
    }

    [Fact]
    public void Subset_ContiguousCharacters_CoalesceIntoOneSegment()
    {
        // Subset glyph ids are assigned in code-point order, so a contiguous request is one run
        // with one idDelta — the case coalescing is for. Asserting the segment count pins that it
        // actually happens rather than merely fitting.
        const int count = 500;
        var chars = new List<char>();
        for (int i = 0; i < count; i++) chars.Add((char)(0x4E00 + i));

        byte[] subset = PdfFontSubsetter.Subset(
            MinimalTrueType.Build(new string(chars.ToArray())), chars);

        // One run plus the mandatory 0xFFFF terminator.
        Assert.Equal(2, ReadCmapSegmentCount(subset));

        var map = MinimalTrueType.ReadCmap(subset);
        Assert.Equal(count, map.Count);
    }

    private static void AssertCmapSubtableLengthMatchesItsBytes(byte[] font)
    {
        (int offset, int length) = FindTable(font, "cmap");
        int subtableOffset = offset + (int)ReadUInt32(font, offset + 8);
        int declared = ReadUInt16(font, subtableOffset + 2);
        int segCount = ReadUInt16(font, subtableOffset + 6) / 2;
        // Seven header words, four arrays of segCount words, and the reservedPad: 16 + 8n. This
        // said 14 + 8n, the writer's own miscount, so the two agreed and the assertion could not
        // see that every subset declared its cmap two bytes short (fixed 2026-10-08).
        int actual = 16 + 8 * segCount;

        Assert.Equal(actual, declared);
        Assert.True(subtableOffset + declared <= offset + length,
            "the declared subtable length runs past the end of the cmap table");
    }

    private static int ReadCmapSegmentCount(byte[] font)
    {
        (int offset, _) = FindTable(font, "cmap");
        int subtableOffset = offset + (int)ReadUInt32(font, offset + 8);
        return ReadUInt16(font, subtableOffset + 6) / 2;
    }

    private static (int offset, int length) FindTable(byte[] font, string tag)
    {
        int numTables = ReadUInt16(font, 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string t = new string(new[] { (char)font[rec], (char)font[rec + 1],
                                          (char)font[rec + 2], (char)font[rec + 3] });
            if (t == tag) return ((int)ReadUInt32(font, rec + 8), (int)ReadUInt32(font, rec + 12));
        }
        throw new InvalidOperationException($"no '{tag}' table in the subset font");
    }

    private static void SetTableOffsetAndLength(byte[] font, string tag, uint offset, uint length)
    {
        int numTables = ReadUInt16(font, 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string t = new string(new[] { (char)font[rec], (char)font[rec + 1],
                                          (char)font[rec + 2], (char)font[rec + 3] });
            if (t != tag) continue;
            WriteUInt32(font, rec + 8, offset);
            WriteUInt32(font, rec + 12, length);
            return;
        }
        throw new InvalidOperationException($"no '{tag}' table to relocate");
    }

    private static void WriteUInt32(byte[] b, int p, uint v)
    {
        b[p] = (byte)(v >> 24);
        b[p + 1] = (byte)(v >> 16);
        b[p + 2] = (byte)(v >> 8);
        b[p + 3] = (byte)v;
    }

    private static void SetTableLength(byte[] font, string tag, uint length)
    {
        int numTables = ReadUInt16(font, 4);
        for (int i = 0; i < numTables; i++)
        {
            int rec = 12 + i * 16;
            string t = new string(new[] { (char)font[rec], (char)font[rec + 1],
                                          (char)font[rec + 2], (char)font[rec + 3] });
            if (t != tag) continue;
            font[rec + 12] = (byte)(length >> 24);
            font[rec + 13] = (byte)(length >> 16);
            font[rec + 14] = (byte)(length >> 8);
            font[rec + 15] = (byte)length;
            return;
        }
        throw new InvalidOperationException($"no '{tag}' table to corrupt");
    }

    private static int ReadUInt16(byte[] b, int p) => (b[p] << 8) | b[p + 1];
    private static uint ReadUInt32(byte[] b, int p) =>
        ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3];
}
