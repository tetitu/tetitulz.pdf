// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;
using B = Tetitulz.Pdf.Core.Tests.IO.Filters.Jbig2TestEncoder.Bitmap;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Huffman-coded symbol dictionaries with refinement aggregation (SDHUFF=1, SDREFAGG=1, T.88
/// §6.5.8.2.2), built bit by bit from the standard tables. Their refinement bitmaps are arithmetic-coded
/// by <see cref="Jbig2TestEncoder"/>, so the refined pixels are checked against an oracle the decoder
/// did not write. Before these tests the Huffman refinement path was pinned only by the another reader corpus,
/// which the mutation harness does not run.
/// </summary>
public class Jbig2HuffmanRefinementTests
{
    private const int PageW = 40, PageH = 16;
    private static readonly B Sym0 = B.Random(6, 8, 11);
    private static readonly B Sym1 = B.Random(5, 8, 23);

    /// <summary>
    /// One new symbol, a refinement of referred symbol 1 (5x8) into a 6x8 bitmap with RDX 1 and RDY 0.
    /// The encoder writes the same fields the decoder reads: the symbol ID in SBSYMCODELEN bits (two,
    /// for two referred and one new symbol), RDX and RDY from Table B.15, BMSIZE from Table B.1, byte
    /// alignment, then the refinement bitmap over BMSIZE bytes.
    /// </summary>
    [Fact]
    public void HuffmanSymbolDictionary_RefinedSymbol_DecodesToTheRefinedPixels()
    {
        B refined = Sym1.Clone(6, 8);
        refined[0, 0] = 1 - refined[0, 0]; refined[4, 6] = 1 - refined[4, 6];

        var gr = new Jbig2TestEncoder();
        int grBlock = gr.Block(1 << 13);
        gr.Refinement(grBlock, refined, Sym1, dx: 1, dy: 0);
        byte[] refinement = gr.Finish();

        var bits = new HuffmanBits();
        bits.Value(Jbig2Decoder.StandardTableD, 8);          // HCDH: the height class is 8
        bits.Value(Jbig2Decoder.StandardTableB, 6);          // DW: the symbol is 6 wide
        bits.Value(Jbig2Decoder.StandardTableA, 1);          // REFAGGNINST: one instance, a refinement
        bits.Raw(1, 2);                                      // SBSYMCODELEN 2: referred symbol 1
        bits.Value(Jbig2Decoder.StandardTableO, 1);          // RDX
        bits.Value(Jbig2Decoder.StandardTableO, 0);          // RDY
        bits.Value(Jbig2Decoder.StandardTableA, refinement.Length); // BMSIZE
        bits.Align();
        bits.Bytes(refinement);
        bits.Value(Jbig2Decoder.StandardTableB, Jbig2Decoder.Jbig2HuffmanTable.OOB); // end of height class
        bits.Value(Jbig2Decoder.StandardTableA, 2);          // export: two referred symbols excluded
        bits.Value(Jbig2Decoder.StandardTableA, 1);          // export: the one new symbol

        byte[] dictionary = HuffmanDictionary(numExported: 1, numNew: 1, bits.ToArray());
        var referred = new List<Jbig2Decoder.Jbig2Bitmap>(Jbig2Decoder.DecodeSymbolDictionary(
            Jbig2TestEncoder.PlainDictionary(new[] { Sym0, Sym1 }), new List<Jbig2Decoder.Jbig2Bitmap>(), null));

        Jbig2Decoder.Jbig2Bitmap symbol = Assert.Single(Jbig2Decoder.DecodeSymbolDictionary(dictionary, referred, null));

        Assert.Equal(refined.W, symbol.Width);
        Assert.Equal(refined.H, symbol.Height);
        for (int y = 0; y < refined.H; y++)
            for (int x = 0; x < refined.W; x++)
                Assert.Equal(refined[x, y], symbol.GetPixel(x, y));
    }

    /// <summary>
    /// One new symbol of 13x8 aggregated from the two referred symbols (REFAGGNINST 2, §6.5.8.2.1): symbol
    /// 0 at S 0, then symbol 1 at S 7 (DS 2 after symbol 0's last column at 5). Each instance carries its
    /// symbol ID in SBSYMCODELEN bits and a refinement bit, which is 0 here, and each strip ends on an
    /// OOB IDS, the last one included. The strip and first-S fields use Tables B.11 and B.6, the gap
    /// Table B.8, as Table 17 gives them.
    /// </summary>
    [Fact]
    public void HuffmanSymbolDictionary_TwoInstanceAggregate_PlacesEachReferredSymbol()
    {
        var expected = new B(13, 8);
        Sym0.DrawOn(expected, 0, 0);
        Sym1.DrawOn(expected, 7, 0);

        var bits = new HuffmanBits();
        bits.Value(Jbig2Decoder.StandardTableD, 8);          // HCDH: the height class is 8
        bits.Value(Jbig2Decoder.StandardTableB, 13);         // DW: the symbol is 13 wide
        bits.Value(Jbig2Decoder.StandardTableA, 2);          // REFAGGNINST: two instances, a text region
        // Table B.11 has no line for 0: the strip deltas start at 1. The initial STRIPT is negated, so
        // -1 then +1 leaves the strip at T 0.
        bits.Value(Jbig2Decoder.StandardTableK, 1);          // the strip's initial STRIPT (-1)
        bits.Value(Jbig2Decoder.StandardTableK, 1);          // DT: the strip's T (+1)
        bits.Value(Jbig2Decoder.StandardTableF, 0);          // DFS: the first S is 0
        bits.Raw(0, 2);                                      // symbol 0 (SBSYMCODELEN 2)
        bits.Raw(0, 1);                                      // RI: not refined
        bits.Value(Jbig2Decoder.StandardTableH, 2);          // DS: symbol 1 starts at S 7
        bits.Raw(1, 2);                                      // symbol 1
        bits.Raw(0, 1);                                      // RI: not refined
        bits.Value(Jbig2Decoder.StandardTableH, Jbig2Decoder.Jbig2HuffmanTable.OOB); // end of strip
        bits.Value(Jbig2Decoder.StandardTableB, Jbig2Decoder.Jbig2HuffmanTable.OOB); // end of height class
        bits.Value(Jbig2Decoder.StandardTableA, 2);          // export: two referred symbols excluded
        bits.Value(Jbig2Decoder.StandardTableA, 1);          // export: the one new symbol

        byte[] dictionary = HuffmanDictionary(numExported: 1, numNew: 1, bits.ToArray());
        var referred = new List<Jbig2Decoder.Jbig2Bitmap>(Jbig2Decoder.DecodeSymbolDictionary(
            Jbig2TestEncoder.PlainDictionary(new[] { Sym0, Sym1 }), new List<Jbig2Decoder.Jbig2Bitmap>(), null));

        Jbig2Decoder.Jbig2Bitmap symbol = Assert.Single(Jbig2Decoder.DecodeSymbolDictionary(dictionary, referred, null));

        Assert.Equal(13, symbol.Width);
        Assert.Equal(8, symbol.Height);
        for (int y = 0; y < expected.H; y++)
            for (int x = 0; x < expected.W; x++)
                Assert.Equal(expected[x, y], symbol.GetPixel(x, y));
    }

    /// <summary>
    /// A Huffman text region (SBHUFF=1, SBREFINE=1) with one refined instance: symbol 1 drawn as a 6x8
    /// refinement at S 1, T 0. The region's own table selectors (RDW, RDH, RDX, RDY through Table B.14,
    /// RSIZE through Table B.1) are all at their defaults, and the symbol-ID table is built from a
    /// runcode preamble in which runcode 1 has length 1, so symbols 0 and 1 are the codes 0 and 1.
    /// </summary>
    [Fact]
    public void HuffmanTextRegion_RefinedInstance_DecodesTheRefinedPixels()
    {
        B refined = Sym1.Clone(6, 8);
        refined[0, 0] = 1 - refined[0, 0]; refined[4, 6] = 1 - refined[4, 6];

        var gr = new Jbig2TestEncoder();
        int grBlock = gr.Block(1 << 13);
        gr.Refinement(grBlock, refined, Sym1, dx: 1, dy: 0);
        byte[] refinement = gr.Finish();

        var bits = new HuffmanBits();
        for (int i = 0; i < 35; i++) bits.Raw(i == 1 ? 1 : 0, 4);   // runcode 1 has code length 1
        bits.Raw(0, 1);                                    // symbol 0's length, by runcode 1
        bits.Raw(0, 1);                                    // symbol 1's length, by runcode 1
        bits.Align();                                      // the symbol-ID table ends on a byte boundary
        bits.Value(Jbig2Decoder.StandardTableK, 1);        // the strip's initial STRIPT (-1)
        bits.Value(Jbig2Decoder.StandardTableK, 1);        // DT: the strip at T 0
        bits.Value(Jbig2Decoder.StandardTableF, 1);        // DFS: the first S is 1
        bits.Raw(1, 1);                                    // symbol 1 (its code is 1)
        bits.Raw(1, 1);                                    // RI: refined
        bits.Value(Jbig2Decoder.StandardTableN, 1);        // RDW: 6 wide
        bits.Value(Jbig2Decoder.StandardTableN, 0);        // RDH: 8 high
        bits.Value(Jbig2Decoder.StandardTableN, 1);        // RDX
        bits.Value(Jbig2Decoder.StandardTableN, 0);        // RDY
        bits.Value(Jbig2Decoder.StandardTableA, refinement.Length); // RSIZE
        bits.Bytes(refinement);
        bits.Value(Jbig2Decoder.StandardTableH, Jbig2Decoder.Jbig2HuffmanTable.OOB); // end of strip

        var region = new MemoryStream();
        Jbig2TestEncoder.U32(region, PageW); Jbig2TestEncoder.U32(region, PageH);
        Jbig2TestEncoder.U32(region, 0); Jbig2TestEncoder.U32(region, 0);
        region.WriteByte(0);                               // the region onto the page: OR
        Jbig2TestEncoder.U16(region, 0x0013);              // SBHUFF 1, SBREFINE 1, TOPLEFT
        Jbig2TestEncoder.U16(region, 0x0000);              // the Huffman selectors, all default
        region.Write(Jbig2TestEncoder.RefinementAt, 0, 4);
        Jbig2TestEncoder.U32(region, 1);                   // SBNUMINSTANCES
        byte[] coded = bits.ToArray();
        region.Write(coded, 0, coded.Length);

        var expected = new B(PageW, PageH);
        refined.DrawOn(expected, 1, 0);

        var s = new MemoryStream();
        Jbig2TestEncoder.Segment(s, 0, 48, null, Jbig2TestEncoder.PageInfo(PageW, PageH));
        Jbig2TestEncoder.Segment(s, 1, 0, null, Jbig2TestEncoder.PlainDictionary(new[] { Sym0, Sym1 }));
        Jbig2TestEncoder.Segment(s, 2, 6, new byte[] { 1 }, region.ToArray());
        Assert.Equal(Picture(expected), Picture(Jbig2Decoder.Decode(s.ToArray(), null, out _, out _), PageW, PageH));
    }

    /// <summary>A packed page as rows of '#' and '.', so a failure shows where the ink is.</summary>
    private static string Picture(byte[] packed, int w, int h)
    {
        int stride = (w + 7) / 8;
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
                sb.Append((packed[y * stride + (x >> 3)] & (0x80 >> (x & 7))) != 0 ? '#' : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string Picture(B bitmap)
    {
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < bitmap.H; y++)
        {
            for (int x = 0; x < bitmap.W; x++) sb.Append(bitmap[x, y] != 0 ? '#' : '.');
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The dictionary segment body: SDFLAGS with SDHUFF and SDREFAGG set and every Huffman selector at
    /// its standard default (no SDAT, since SDHUFF=1), SDRAT for template 0, then the counts. Built as
    /// the decoder reads it, so a header this test writes wrong fails here rather than in the decode.
    /// </summary>
    internal static byte[] HuffmanDictionary(int numExported, int numNew, byte[] huffmanData)
    {
        var s = new MemoryStream();
        Jbig2TestEncoder.U16(s, 0x0003);                     // SDHUFF 1, SDREFAGG 1, every selector 0, SDRTEMPLATE 0
        s.Write(Jbig2TestEncoder.RefinementAt, 0, 4);
        Jbig2TestEncoder.U32(s, numExported);
        Jbig2TestEncoder.U32(s, numNew);
        s.Write(huffmanData, 0, huffmanData.Length);
        return s.ToArray();
    }

    /// <summary>
    /// Writes Huffman codewords MSB first, from the same table entries the decoder reads. Each value is
    /// encoded by the one line whose range holds it: its prefix, then its offset in that line's range
    /// bits (counting down on a lower-range line, as §B.4 says).
    /// </summary>
    internal sealed class HuffmanBits
    {
        private readonly List<bool> _bits = new List<bool>();

        public void Raw(int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) != 0);
        }

        public void Value(Jbig2Decoder.Jbig2HuffmanTable table, int value)
        {
            if (value == Jbig2Decoder.Jbig2HuffmanTable.OOB)
            {
                foreach (var e in table.Entries)
                    if (e.RangeBits == -1) { Raw(e.Prefix, e.PrefixLen); return; }
                Assert.Fail("The table has no OOB line.");
                return;
            }

            foreach (var e in table.Entries)
            {
                if (e.RangeBits < 0) continue;
                long extra = e.LowerSign ? e.RangeLow - (long)value : value - (long)e.RangeLow;
                if (extra < 0 || extra >= 1L << e.RangeBits) continue;
                Raw(e.Prefix, e.PrefixLen);
                Raw((int)extra, e.RangeBits);
                return;
            }
            Assert.Fail($"No line of the table holds {value}.");
        }

        public void Align()
        {
            while (_bits.Count % 8 != 0) _bits.Add(false);
        }

        public void Bytes(byte[] data)
        {
            Align();
            foreach (byte b in data) Raw(b, 8);
        }

        public byte[] ToArray()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
                if (_bits[i]) bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
            return bytes;
        }
    }
}
