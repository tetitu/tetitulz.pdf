// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Regression tests for <c>TrueTypeMetrics.Parse</c>'s <c>hhea</c> / <c>hmtx</c> handling.
///
/// <para>
/// <b>The bug.</b> <c>hhea</c> is 36 bytes and <c>numberOfHMetrics</c> is its last field, at
/// table offset 34. The parser skipped 30 bytes past <c>descender</c> (offset 8) and therefore
/// read at offset <b>38</b> — two bytes past the end of the table. On a font whose <c>hhea</c>
/// is the last table in the file that threw <see cref="IndexOutOfRangeException"/>; otherwise it
/// silently produced a garbage advance-width table. <c>PdfFontSubsetter.ParseHhea</c> has always
/// read offset 34 correctly, so the two disagreed about the same font.
/// </para>
///
/// <para>
/// Every font here is synthesised in memory — no font file is read from disk, so these run
/// identically on any host.
/// </para>
/// </summary>
public sealed class TrueTypeMetricsHheaTests
{
    // ------------------------------------------------------------------
    //  Tests
    // ------------------------------------------------------------------

    [Fact]
    public void Parse_ReadsNumberOfHMetricsAtOffset34_NotOffset38()
    {
        // hhea is padded to 40 bytes, and the two bytes at offset 38 — where the parser used to
        // read — are zero. A zero numberOfHMetrics leaves every advance width at 0, so the two
        // readings are told apart by the width alone.
        byte[] font = BuildFont(numberOfHMetrics: 3,
                                advanceWidths: new[] { 111, 222, 333 },
                                hheaPadTo: 40,
                                hheaLast: false);

        TrueTypeMetrics m = PdfFontEmbedder.GetFontMetrics(font);

        // GetWidth falls back to glyph 0's advance width for an unmapped code, and unitsPerEm is
        // 1000, so the value passes through unscaled.
        Assert.Equal(111, m.GetWidth('A'));
    }

    [Fact]
    public void Parse_HheaIsLastTableInFile_DoesNotThrow()
    {
        // Adversarial layout: hhea is exactly 36 bytes and ends at EOF, so a read at offset 38
        // runs off the end of the buffer. TtfReader indexes unchecked, so pre-fix this threw.
        byte[] font = BuildFont(numberOfHMetrics: 2,
                                advanceWidths: new[] { 500, 600 },
                                hheaPadTo: 36,
                                hheaLast: true);

        TrueTypeMetrics m = PdfFontEmbedder.GetFontMetrics(font);

        Assert.Equal(500, m.GetWidth('A'));
    }

    [Fact]
    public void Parse_NumberOfHMetricsLargerThanHmtxTable_DoesNotThrow()
    {
        // numberOfHMetrics comes straight out of the file, so a hostile font can claim far more
        // entries than hmtx holds. The read loop must clamp to what the table really supplies
        // rather than walking off the end of the buffer.
        byte[] font = BuildFont(numberOfHMetrics: 60000,
                                advanceWidths: new[] { 700, 800 },   // only 2 entries really exist
                                hheaPadTo: 36,
                                hheaLast: false);

        TrueTypeMetrics m = PdfFontEmbedder.GetFontMetrics(font);

        // The two real entries are still read; the claim beyond them is ignored, not faulted.
        Assert.Equal(700, m.GetWidth('A'));
    }

    [Fact]
    public void Parse_AgreesWithSubsetterAboutTheSameFont()
    {
        // The subsetter reads offset 34 and always has. Embedding a font and subsetting it must
        // not disagree about how many horizontal metrics it has: a font the subsetter accepts
        // must parse here too.
        byte[] font = BuildFont(numberOfHMetrics: 3,
                                advanceWidths: new[] { 250, 260, 270 },
                                hheaPadTo: 36,
                                hheaLast: false);

        TrueTypeMetrics m = PdfFontEmbedder.GetFontMetrics(font);

        Assert.Equal(250, m.GetWidth('A'));
        Assert.Equal(1000, m.UnitsPerEm);
        Assert.Equal(800, m.Ascent);
        Assert.Equal(-200, m.Descent);
    }

    // ------------------------------------------------------------------
    //  Minimal sfnt builder
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a TrueType font containing only the tables <c>TrueTypeMetrics.Parse</c> reads for
    /// advance widths: <c>head</c>, <c>hhea</c>, <c>maxp</c>, <c>hmtx</c>.
    /// </summary>
    /// <param name="numberOfHMetrics">Value written into hhea's last field (offset 34).</param>
    /// <param name="advanceWidths">The advance widths actually stored in hmtx.</param>
    /// <param name="hheaPadTo">Total hhea length; 36 is the real size, more exercises the
    /// over-read landing on padding rather than off the end of the file.</param>
    /// <param name="hheaLast">Place hhea last so it ends at EOF.</param>
    private static byte[] BuildFont(
        int numberOfHMetrics, int[] advanceWidths, int hheaPadTo, bool hheaLast)
    {
        byte[] head = BuildHead(unitsPerEm: 1000);
        byte[] hhea = BuildHhea(ascender: 800, descender: -200, numberOfHMetrics, hheaPadTo);
        byte[] maxp = BuildMaxp(numGlyphs: advanceWidths.Length);
        byte[] hmtx = BuildHmtx(advanceWidths);

        var tables = new List<(string tag, byte[] data)>
        {
            ("head", head), ("maxp", maxp), ("hmtx", hmtx),
        };
        // The directory must stay sorted by tag, but physical order is free — which is what lets
        // hhea be placed last in the file.
        if (hheaLast) tables.Add(("hhea", hhea));
        else tables.Insert(0, ("hhea", hhea));

        int numTables = tables.Count;
        int directorySize = 12 + numTables * 16;

        // Assign physical offsets in list order (each table 4-byte aligned, except the last when
        // it must end exactly at EOF).
        var offsets = new Dictionary<string, uint>(StringComparer.Ordinal);
        int pos = directorySize;
        foreach (var (tag, data) in tables)
        {
            offsets[tag] = (uint)pos;
            pos += data.Length;
            pos = (pos + 3) & ~3;             // pad to 4-byte boundary
        }
        // Trim trailing padding so the final table really ends at EOF.
        var (lastTag, lastData) = tables[tables.Count - 1];
        int total = (int)offsets[lastTag] + lastData.Length;

        var font = new byte[Math.Max(pos, total)];
        if (hheaLast) font = new byte[total];

        // Offset table.
        WriteU32(font, 0, 0x00010000);
        WriteU16(font, 4, (ushort)numTables);
        WriteU16(font, 6, 0); WriteU16(font, 8, 0); WriteU16(font, 10, 0);

        // Table directory, sorted by tag as the format requires.
        var sorted = new List<(string tag, byte[] data)>(tables);
        sorted.Sort((a, b) => string.CompareOrdinal(a.tag, b.tag));
        int dir = 12;
        foreach (var (tag, data) in sorted)
        {
            Encoding.ASCII.GetBytes(tag).CopyTo(font, dir);
            WriteU32(font, dir + 4, 0);                       // checksum (unread)
            WriteU32(font, dir + 8, offsets[tag]);
            WriteU32(font, dir + 12, (uint)data.Length);
            dir += 16;
        }

        foreach (var (tag, data) in tables)
            data.CopyTo(font, (int)offsets[tag]);

        return font;
    }

    private static byte[] BuildHead(int unitsPerEm)
    {
        var t = new byte[54];
        WriteU32(t, 0, 0x00010000);      // version
        WriteU32(t, 12, 0x5F0F3CF5);     // magicNumber
        WriteU16(t, 18, (ushort)unitsPerEm);
        WriteI16(t, 36, -100);           // xMin
        WriteI16(t, 38, -200);           // yMin
        WriteI16(t, 40, 1000);           // xMax
        WriteI16(t, 42, 900);            // yMax
        return t;
    }

    private static byte[] BuildHhea(int ascender, int descender, int numberOfHMetrics, int padTo)
    {
        var t = new byte[Math.Max(36, padTo)];
        WriteU32(t, 0, 0x00010000);      // version
        WriteI16(t, 4, (short)ascender);
        WriteI16(t, 6, (short)descender);
        WriteI16(t, 8, 0);               // lineGap
        // offsets 10..33 are advanceWidthMax .. metricDataFormat, all left zero
        WriteU16(t, 34, (ushort)numberOfHMetrics);
        // Anything past 36 is padding and stays zero — which is exactly what the old code read.
        return t;
    }

    private static byte[] BuildMaxp(int numGlyphs)
    {
        var t = new byte[32];
        WriteU32(t, 0, 0x00010000);      // version
        WriteU16(t, 4, (ushort)numGlyphs);
        return t;
    }

    private static byte[] BuildHmtx(int[] advanceWidths)
    {
        var t = new byte[advanceWidths.Length * 4];
        for (int i = 0; i < advanceWidths.Length; i++)
        {
            WriteU16(t, i * 4, (ushort)advanceWidths[i]);   // advanceWidth
            WriteI16(t, i * 4 + 2, 0);                      // leftSideBearing
        }
        return t;
    }

    private static void WriteU16(byte[] b, int p, ushort v)
    { b[p] = (byte)(v >> 8); b[p + 1] = (byte)(v & 0xFF); }

    private static void WriteI16(byte[] b, int p, short v) => WriteU16(b, p, (ushort)v);

    private static void WriteU32(byte[] b, int p, uint v)
    {
        b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16);
        b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)(v & 0xFF);
    }
}
