// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for the Huffman decoder framework, unknown-length segment handling,
/// and IAEX/aggregation-refinement support.
/// </summary>
public class Jbig2HuffmanAndMiscTests
{
    private static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        foreach (var p in parts) total += p?.Length ?? 0;
        var r = new byte[total];
        int off = 0;
        foreach (var p in parts)
        {
            if (p == null || p.Length == 0) continue;
            Buffer.BlockCopy(p, 0, r, off, p.Length);
            off += p.Length;
        }
        return r;
    }

    private static byte[] SegmentHeader(uint segmentNumber, int segmentType,
        uint pageAssociation, uint dataLength)
    {
        return new byte[]
        {
            (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),  (byte)(segmentNumber & 0xFF),
            (byte)(segmentType & 0x3F),
            0x00,
            (byte)(pageAssociation & 0xFF),
            (byte)(dataLength >> 24), (byte)(dataLength >> 16),
            (byte)(dataLength >> 8),  (byte)(dataLength & 0xFF),
        };
    }

    /// <summary>Header naming exactly one referred-to segment (§7.2.3/§7.2.5, short form —
    /// the referred segment number fits in 1 byte).</summary>
    private static byte[] ReferringSegmentHeader(uint segmentNumber, int segmentType,
        uint referredTo, uint pageAssociation, uint dataLength)
    {
        return new byte[]
        {
            (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16),
            (byte)(segmentNumber >> 8),  (byte)(segmentNumber & 0xFF),
            (byte)(segmentType & 0x3F),
            0x20,                              // count = 1 in the high 3 bits
            (byte)(referredTo & 0xFF),
            (byte)(pageAssociation & 0xFF),
            (byte)(dataLength >> 24), (byte)(dataLength >> 16),
            (byte)(dataLength >> 8),  (byte)(dataLength & 0xFF),
        };
    }

    private static byte[] PageInfoBody(uint width, uint height)
    {
        var body = new byte[19];
        body[0] = (byte)(width >> 24);  body[1] = (byte)(width >> 16);
        body[2] = (byte)(width >> 8);   body[3] = (byte)(width & 0xFF);
        body[4] = (byte)(height >> 24); body[5] = (byte)(height >> 16);
        body[6] = (byte)(height >> 8);  body[7] = (byte)(height & 0xFF);
        return body;
    }

    // ==================================================================
    //  Huffman bit reader
    // ==================================================================

    [Fact]
    public void HuffmanBitReader_ReadsBitsMsbFirst()
    {
        // 0xA5 = 10100101
        var r = new MsbBitStream(new byte[] { 0xA5 }, 0);
        Assert.Equal(1, r.ReadBit());
        Assert.Equal(0, r.ReadBit());
        Assert.Equal(1, r.ReadBit());
        Assert.Equal(0, r.ReadBit());
        Assert.Equal(0, r.ReadBit());
        Assert.Equal(1, r.ReadBit());
        Assert.Equal(0, r.ReadBit());
        Assert.Equal(1, r.ReadBit());
    }

    [Fact]
    public void HuffmanBitReader_ReadBits_CombinesMsbFirst()
    {
        // 0x12 0x34 → 00010010 00110100; reading 12 bits gives 0x123.
        var r = new MsbBitStream(new byte[] { 0x12, 0x34 }, 0);
        int v = r.ReadBits(12);
        Assert.Equal(0x123, v);
    }

    [Fact]
    public void HuffmanBitReader_PastEof_PadsWithZeros()
    {
        var r = new MsbBitStream(new byte[] { 0x80 }, 0);
        // First bit = 1, remaining 7 bits = 0, then past EOF = 0.
        Assert.Equal(1, r.ReadBit());
        for (int i = 0; i < 12; i++)
        {
            int b = r.ReadBit();
            Assert.True(b == 0 || b == -1);
        }
    }

    // ==================================================================
    //  Huffman table structural sanity
    // ==================================================================

    [Fact]
    /// <summary>
    /// Standard Table A is fixed by the JBIG2 spec (Annex B.1): four prefix codes covering
    /// 0-15, 16-271, 272-65807 and a 32-bit tail. Asserting those bounds is what pins the table
    /// as <i>Table A</i>; "has entries" would accept any table, including a copy of Table B.
    /// </summary>
    public void StandardTableA_MatchesTheSpecCodeLengthsAndRanges()
    {
        var table = Jbig2Decoder.StandardTableA;
        Assert.Equal(4, table.Entries.Length);

        Assert.Equal((1, 0, 0, 4),          Shape(table.Entries[0]));
        Assert.Equal((2, 2, 16, 8),         Shape(table.Entries[1]));
        Assert.Equal((3, 6, 272, 16),       Shape(table.Entries[2]));
        Assert.Equal((3, 7, 65808, 32),     Shape(table.Entries[3]));

        // ...and it decodes: an all-zero stream selects the first prefix, value 0.
        var reader = new MsbBitStream(new byte[] { 0x00, 0x00 }, 0);
        Assert.Equal(0, table.Decode(reader));
    }

    private static (int PrefixLen, int Prefix, int RangeLow, int RangeBits) Shape(
        Jbig2Decoder.Jbig2HuffmanEntry e)
        => (e.PrefixLen, e.Prefix, e.RangeLow, e.RangeBits);

    [Fact]
    public void StandardTableB_DecodesOnTrivialStream()
    {
        // Input 0x80 = 10000000.  Table B has "0" → value 0 as the first entry.
        var r = new MsbBitStream(new byte[] { 0x00 }, 0);
        int v = Jbig2Decoder.StandardTableB.Decode(r);
        Assert.Equal(0, v);
    }

    [Fact]
    public void HuffmanTable_UnresolvedPrefix_Throws()
    {
        // Construct a table with a single 1-bit prefix "1" and feed "0".
        var t = new Jbig2Decoder.Jbig2HuffmanTable(new[]
        {
            new Jbig2Decoder.Jbig2HuffmanEntry { PrefixLen = 1, Prefix = 1, RangeLow = 42, RangeBits = 0 }
        });
        var r = new MsbBitStream(new byte[] { 0x00, 0x00 }, 0);
        Assert.Throws<System.IO.InvalidDataException>(() => t.Decode(r));
    }

    // ==================================================================
    //  Unknown-length segment scanning
    // ==================================================================

    [Fact]
    public void UnknownLength_Segment_ScansToFfAcMarker()
    {
        // Use length 0xFFFFFFFF; body = 10 arbitrary bytes + FF AC end marker.
        uint unknownLen = 0xFFFFFFFF;
        byte[] body = new byte[10];
        byte[] endMarker = { 0xFF, 0xAC };

        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(16, 16),
            SegmentHeader(1, 62, 1, unknownLen), body, endMarker,  // extension with unknown length
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        // The parser should recognise 3 segments despite the middle one being unknown-length.
        Assert.Equal(3, diag.Count);
        Assert.Equal(48, diag[0].SegmentType);
        Assert.Equal(62, diag[1].SegmentType);
        Assert.Equal(51, diag[2].SegmentType);
    }

    // ==================================================================
    //  Diagnose / Assess must not endorse the Huffman paths
    // ==================================================================
    //
    //  All three of these previously asserted the opposite: that a Huffman
    //  symbol dictionary and a Huffman text region were "experimental" but
    //  decodable, and that a stream made of both was IsFullyDecodable. They
    //  encoded the defect in GAPS.md §2a — the symbol dictionary emitted blank
    //  placeholder bitmaps and reported success, so the one API a host calls to
    //  ask "will this decode?" answered YES for the path that returns blanks.

    [Fact]
    public void Diagnose_HuffmanTextRegion_NonRefined_IsReportedDecoded()
    {
        // SBFLAGS bit 0 set → SBHUFF=1, bit 1 clear → SBREFINE=0.
        // SBFLAGS is 16-bit big-endian and SBHUFF is bit 0, so the flag is in the SECOND byte.
        // A non-refined Huffman text region now decodes for real (GAPS.md §2a) — the
        // runcode-coded symbol-ID table is built and used, not approximated.
        var body = new byte[17 + 2 + 32];
        body[18] = 0x01;   // SBFLAGS low byte, bit 0 = SBHUFF
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 6, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
    }

    [Fact]
    public void Diagnose_ArithmeticTextRegion_IsStillReportedDecoded()
    {
        // The control that keeps the assertion above honest: with SBHUFF=0 the
        // same fixture must still classify as Decoded, so the test is measuring
        // the Huffman flag and not "text regions are unsupported".
        var body = new byte[17 + 2 + 32];   // sbFlags left zero → SBHUFF = 0
        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 6, 1, (uint)body.Length), body,
            SegmentHeader(2, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.Decoded, diag[1].Status);
    }

    [Fact]
    public void Diagnose_HuffmanSymbolDict_NonAggregated_IsSkippedBenign()
    {
        // SDFLAGS (2 bytes): high byte = 0, low byte bit 0 = SDHUFF, bit 1 = SDREFAGG (here 0).
        // A non-aggregated Huffman symbol dictionary decodes for real now — the collective bitmap
        // is parsed and split (GAPS.md §2a) — so, matching the arithmetic symbol dictionary's own
        // classification, this is SkippedBenign (no page content by itself), not Unsupported.
        var body = new byte[2 + 10];
        body[0] = 0x00; body[1] = 0x01;
        var stream = Concat(
            SegmentHeader(0, 0, 1, (uint)body.Length), body,
            SegmentHeader(1, 51, 0, 0));

        var diag = Jbig2Decoder.Diagnose(stream);
        Assert.Equal(Jbig2Decoder.Jbig2SegmentStatus.SkippedBenign, diag[0].Status);
    }

    [Fact]
    public void Assess_HuffmanStream_NonAggregatedNonRefined_IsNowReportedFullyDecodable()
    {
        // Mix: page info + non-aggregated Huffman symbol dict + non-refined Huffman text region +
        // end. Both halves of the Huffman path now decode for real (GAPS.md §2a), so — unlike
        // before either was fixed, when both were Unsupported — this static classification (which
        // reads flags, not an actual decode attempt) reports the stream as fully decodable.
        var symBody = new byte[2 + 10];
        symBody[1] = 0x01; // SDHUFF=1, SDREFAGG=0
        var txtBody = new byte[17 + 2 + 32];
        txtBody[18] = 0x01; // SBHUFF=1, SBREFINE=0 — bit 0/1 of the 16-bit BE SBFLAGS low byte

        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1,  0, 1, (uint)symBody.Length), symBody,
            SegmentHeader(2,  6, 1, (uint)txtBody.Length), txtBody,
            SegmentHeader(3, 51, 0, 0));

        var asm = Jbig2Decoder.Assess(stream);
        Assert.True(asm.IsFullyDecodable);
        Assert.Equal(0, asm.UnsupportedSegmentCount);
        Assert.Equal(string.Empty, asm.BlockerSummary);
    }

    [Fact]
    public void Assess_HuffmanTextRegion_Refined_IsReportedDecodable()
    {
        // A Huffman symbol dictionary and a Huffman text region with SBREFINE=1 both decode, so the
        // stream is fully decodable and names no blocker.
        var symBody = new byte[2 + 10];
        symBody[1] = 0x01; // SDHUFF=1, SDREFAGG=0
        var txtBody = new byte[17 + 2 + 32];
        txtBody[18] = 0x03; // SBHUFF=1, SBREFINE=1

        var stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1,  0, 1, (uint)symBody.Length), symBody,
            SegmentHeader(2,  6, 1, (uint)txtBody.Length), txtBody,
            SegmentHeader(3, 51, 0, 0));

        var asm = Jbig2Decoder.Assess(stream);
        Assert.True(asm.IsFullyDecodable);
        Assert.Equal(0, asm.UnsupportedSegmentCount);
        Assert.Equal(string.Empty, asm.BlockerSummary);
    }

    [Fact]
    public void HuffmanSymbolDictionary_RefinementAggregation_TruncatedStream_FallsBackAudibly()
    {
        // SDREFAGG=1 in the Huffman mode decodes now (GAPS.md §2a), so this stream is refused for
        // what it is: a dictionary body with no data after its flags, which runs out of bytes. The
        // filter's contract is that a non-null LastFallbackReason means "these are the raw
        // compressed bytes, not pixels".
        var symBody = new byte[2 + 10];
        symBody[1] = 0x03; // SDHUFF=1, SDREFAGG=1
        var txtBody = new byte[17 + 2 + 32];
        txtBody[18] = 0x01; // SBHUFF, irrelevant here since the dictionary fails first

        byte[] stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1,  0, 1, (uint)symBody.Length), symBody,
            SegmentHeader(2,  6, 1, (uint)txtBody.Length), txtBody,
            SegmentHeader(3, 51, 0, 0));

        byte[] result = new Jbig2DecodeFilter().Decode(stream, null);

        Assert.NotNull(Jbig2DecodeFilter.LastFallbackReason);
        // Passthrough returns the input verbatim — the signal that it is not a bitmap.
        Assert.Equal(stream, result);
    }

    /// <summary>
    /// The positive half of the fix: a non-aggregated Huffman symbol dictionary now decodes to
    /// real, non-blank pixels — not merely "does not throw". <see cref="Jbig2Decoder.DecodeSymbolDictionary"/>
    /// is called directly (made <c>internal</c> for exactly this reason) because the only path
    /// that would otherwise expose a symbol's pixels — a text region drawing it onto a page — is
    /// itself a Huffman text region, which still refuses; there is no way to reach the decoded
    /// bitmap through the public filter API today.
    /// </summary>
    [Fact]
    public void DecodeSymbolDictionary_Huffman_NonAggregated_DecodesRealNonBlankPixels()
    {
        // One height class, one symbol, 8x4, uncompressed (BMSIZE=0) collective bitmap holding a
        // hollow rectangle: rows 0xFF, 0x81, 0x81, 0xFF. SDHUFFDH=0 (Table B.4), SDHUFFDW=0
        // (Table B.2), SDHUFFBMSIZE=0 (Table B.1) — every selector at its standard-table default,
        // so no user-defined table is needed. Built bit-by-bit from the canonical codes
        // AssignCanonicalPrefixes computes for those three tables (verified independently by
        // Jbig2StandardHuffmanTableOracleTests), not guessed.
        var body = new byte[]
        {
            0x00, 0x01,             // SDFLAGS: SDHUFF=1, everything else (incl. SDREFAGG) 0
            0x00, 0x00, 0x00, 0x01, // SDNUMEXSYMS = 1
            0x00, 0x00, 0x00, 0x01, // SDNUMNEWSYMS = 1
            // Huffman body:
            //   HCDH=4  (Table B.4: "1110" + 3 bits of (4-4)=0            => 1110 000)
            //   DW=8    (Table B.2: "1110" + 3 bits of (8-3)=5            => 1110 101)
            //   DW OOB  (Table B.2: "111111")
            //   BMSIZE=0 (Table B.1: "0" + 4 bits of 0                    => 0 0000)
            //   padded to the next byte boundary
            0xE1, 0xD7, 0xF0, 0x00,
            // Collective bitmap: 8x4, 1 byte/row, uncompressed — hollow rectangle.
            0xFF, 0x81, 0x81, 0xFF,
            // Export flags (Table B.1): run=0 (exclude 0) then run=1 (include 1), padded.
            0x00, 0x40,
        };

        var symbols = Jbig2Decoder.DecodeSymbolDictionary(body, referredSymbols: null, budget: null);

        var symbol = Assert.Single(symbols);
        Assert.Equal(8, symbol.Width);
        Assert.Equal(4, symbol.Height);

        // The hollow-rectangle pattern, read back pixel by pixel — not just "non-empty bytes".
        int[] expectedRows = { 0xFF, 0x81, 0x81, 0xFF };
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
            {
                int expected = (expectedRows[y] >> (7 - x)) & 1;
                Assert.Equal(expected, symbol.GetPixel(x, y));
            }
    }

    /// <summary>
    /// The same hollow-rectangle fixture as
    /// <see cref="DecodeSymbolDictionary_Huffman_NonAggregated_DecodesRealNonBlankPixels"/>, but
    /// with SDHUFFDH=3 (a user-defined table) instead of the standard Table B.4 default — proving
    /// <c>customTables</c>/<c>referredSegmentNumbers</c> are actually consulted, through the real
    /// <c>ParseUserDefinedHuffmanTable</c> parser reading a hand-built Tables-segment body, not a
    /// table constructed directly in the test.
    /// </summary>
    [Fact]
    public void DecodeSymbolDictionary_Huffman_UserDefinedSDHUFFDH_IsConsultedAndDecodesTheSamePixels()
    {
        // Custom table: HTOOB=0, HTPS=2, HTRS=1, HTLOW=4, HTHIGH=5 — one regular row
        // (PREFLEN=1, RANGELEN=0, so it covers exactly the value 4) plus the two mandatory
        // lower-/upper-range lines (PREFLEN=2 each). Canonical codes: regular row "0",
        // lower-range "10", upper-range "11" — verified directly by
        // ParseUserDefinedHuffmanTable_DecodesAllFourRowKinds_WithTheCorrectedFlagsLayout's sibling
        // structure in Jbig2AggregationAndTablesTests, so decoding "0" here is expected to answer 4.
        byte[] tableBody =
        {
            0x02,                   // flags: HTOOB=0, HTPS-1=1(->2), HTRS-1=0(->1)
            0x00, 0x00, 0x00, 0x04, // HTLOW = 4
            0x00, 0x00, 0x00, 0x05, // HTHIGH = 5
            0x54,                   // rows: "010" (regular: PREFLEN=1,RANGELEN=0) + "10" (lower)
                                     //       + "10" (upper), padded
        };
        var customTable = Jbig2Decoder.ParseUserDefinedHuffmanTable(tableBody);
        Assert.NotNull(customTable);

        var customTables = new System.Collections.Generic.Dictionary<uint, Jbig2Decoder.Jbig2HuffmanTable>
        {
            [42] = customTable,
        };
        var referredSegmentNumbers = new uint[] { 42 };

        var body = new byte[]
        {
            0x00, 0x0D,             // SDFLAGS: SDHUFF=1, SDHUFFDH=3 (custom), SDHUFFDW=0, SDHUFFBMSIZE=0
            0x00, 0x00, 0x00, 0x01, // SDNUMEXSYMS = 1
            0x00, 0x00, 0x00, 0x01, // SDNUMNEWSYMS = 1
            // Huffman body:
            //   HCDH=4  (custom table: "0"                                   => 0)
            //   DW=8    (Table B.2: "1110" + 3 bits of (8-3)=5                => 1110 101)
            //   DW OOB  (Table B.2: "111111")
            //   BMSIZE=0 (Table B.1: "0" + 4 bits of 0                       => 0 0000)
            0x75, 0xFC, 0x00,
            // Collective bitmap: 8x4, 1 byte/row, uncompressed — the same hollow rectangle.
            0xFF, 0x81, 0x81, 0xFF,
            // Export flags (Table B.1): run=0 (exclude 0) then run=1 (include 1), padded.
            0x00, 0x40,
        };

        var symbols = Jbig2Decoder.DecodeSymbolDictionary(body, referredSymbols: null, budget: null,
            customTables: customTables, referredSegmentNumbers: referredSegmentNumbers);

        var symbol = Assert.Single(symbols);
        Assert.Equal(8, symbol.Width);
        Assert.Equal(4, symbol.Height);

        int[] expectedRows = { 0xFF, 0x81, 0x81, 0xFF };
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
            {
                int expected = (expectedRows[y] >> (7 - x)) & 1;
                Assert.Equal(expected, symbol.GetPixel(x, y));
            }
    }

    /// <summary>
    /// The symbol dictionary's own error when SDHUFFDH=3 but no Tables segment was actually
    /// referred to — a crafted or malformed stream, not the happy path above.
    /// </summary>
    [Fact]
    public void DecodeSymbolDictionary_Huffman_UserDefinedSDHUFFDH_WithNoReferredTable_ThrowsClearly()
    {
        var body = new byte[]
        {
            0x00, 0x0D,             // SDFLAGS: SDHUFF=1, SDHUFFDH=3 (custom), SDHUFFDW=0, SDHUFFBMSIZE=0
            0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x01,
        };

        var ex = Assert.Throws<System.IO.InvalidDataException>(() =>
            Jbig2Decoder.DecodeSymbolDictionary(body, referredSymbols: null, budget: null));
        Assert.Contains("SDHUFFDH", ex.Message);
    }

    /// <summary>
    /// The single-symbol test above cannot tell a correct split from one that always crops at
    /// column 0 — with only one symbol, its offset <c>x</c> is 0 either way. Two symbols in one
    /// height class, with deliberately opposite fills (all-black then all-white), makes a dropped
    /// <c>-x</c> offset produce the wrong symbol's pixels for the second glyph instead of merely
    /// passing by coincidence. (This mutation was tried and confirmed caught only by this test,
    /// not the single-symbol one, before trusting it.)
    /// </summary>
    [Fact]
    public void DecodeSymbolDictionary_Huffman_NonAggregated_SplitsMultipleSymbolsByAccumulatedWidth()
    {
        var body = new byte[]
        {
            0x00, 0x01,             // SDFLAGS: SDHUFF=1, SDREFAGG=0
            0x00, 0x00, 0x00, 0x02, // SDNUMEXSYMS = 2
            0x00, 0x00, 0x00, 0x02, // SDNUMNEWSYMS = 2
            // Huffman body:
            //   HCDH=4  (Table B.4: "1110" + 3 bits of (4-4)=0   => 1110 000)
            //   DW1=4   (Table B.2: "1110" + 3 bits of (4-3)=1   => 1110 001)
            //   DW2=0   (Table B.2: "0")                          — second symbol, same width (4)
            //   DW OOB  (Table B.2: "111111")
            //   BMSIZE=0 (Table B.1: "0" + 4 bits of 0            => 0 0000)
            //   padded to the next byte boundary
            0xE1, 0xC5, 0xF8, 0x00,
            // Collective bitmap: 8x4, 1 byte/row — left nibble (symbol 1, cols 0-3) all black,
            // right nibble (symbol 2, cols 4-7) all white.
            0xF0, 0xF0, 0xF0, 0xF0,
            // Export flags (Table B.1): run=0 (exclude 0) then run=2 (include both), padded.
            0x00, 0x80,
        };

        var symbols = Jbig2Decoder.DecodeSymbolDictionary(body, referredSymbols: null, budget: null);

        Assert.Equal(2, symbols.Length);
        foreach (var symbol in symbols)
        {
            Assert.Equal(4, symbol.Width);
            Assert.Equal(4, symbol.Height);
        }

        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                Assert.Equal(1, symbols[0].GetPixel(x, y)); // all black
                Assert.Equal(0, symbols[1].GetPixel(x, y)); // all white
            }
    }

    /// <summary>
    /// A height class whose collective bitmap is MMR-coded (<c>BMSIZE &gt; 0</c>) decodes to the pixels
    /// that were encoded, split by accumulated width.
    /// </summary>
    /// <remarks>
    /// <para><b>This branch was transcribed and never run</b> — GAPS.md §2a listed it as not
    /// fixture-verified, because every Huffman fixture above carries its collective bitmap
    /// uncompressed. The two places a transcription goes wrong without a fixture are <b>polarity</b>
    /// (a JBIG2 bitmap stores 1 as black, while PDF's <c>/CCITTFaxDecode</c> defaults to 0 as black)
    /// and the <b>byte count</b> that hands the reader back to the export flags.</para>
    ///
    /// <para><b>What this can vouch for and what it cannot.</b> The MMR bytes come from this codebase's
    /// own <see cref="CcittFaxEncoder"/>, which shares T.6's code tables with the decoder, so a wrong
    /// code table would round-trip unseen; those tables are the CCITT filter's to pin. What this pins
    /// is everything JBIG2 adds on top: exactly <c>BMSIZE</c> bytes consumed, the result read as
    /// 1-is-black, and the split by width. The Huffman codewords are the ones the uncompressed
    /// fixtures above already use; only <c>BMSIZE</c> is new, and it falls in Table B.1's first row,
    /// a "0" prefix and four bits.</para>
    ///
    /// <para><b>One symbol is exported, not both, and it has to be the second one to make the byte
    /// count visible.</b> The first version exported the whole dictionary, and a mutation reading one
    /// byte too many survived it: the export flags were then read from the wrong place, selected
    /// nothing, and the decoder's corrupt-stream fallback — export everything — produced exactly the
    /// answer the test expected. That fallback clamps to <c>SDNUMEXSYMS</c>, so with one symbol
    /// exported it hands back the <i>first</i>; only the case exporting symbol 1 can tell a correct
    /// read from the fallback, and it is the one that kills that mutation.</para>
    /// </remarks>
    [Theory]
    [InlineData(0)]   // export flags: exclude 0, include 1, exclude 1
    [InlineData(1)]   // export flags: exclude 1, include 1
    public void DecodeSymbolDictionary_Huffman_MmrCodedCollectiveBitmap_DecodesAndSplits(int exportedSymbol)
    {
        // Two 8×4 symbols side by side — a hollow rectangle, then a diagonal — so the coded rows
        // carry colour changes inside a row, not only full-width runs.
        byte[] collective =
        {
            0xFF, 0x80,
            0x81, 0x40,
            0x81, 0x20,
            0xFF, 0x10,
        };
        byte[] mmr = CcittFaxEncoder.Encode(collective, columns: 16, rows: 4, blackIs1: true);
        Assert.InRange(mmr.Length, 1, 15);

        var bits = new BitPacker();
        bits.Write("1110000");                                              // HCDH = 4  (Table B.4)
        bits.Write("1110101");                                              // DW = 8    (Table B.2)
        bits.Write("0");                                                    // DW = 0: the second symbol is 8 wide too
        bits.Write("111111");                                               // DW OOB    (Table B.2)
        bits.Write("0" + Convert.ToString(mmr.Length, 2).PadLeft(4, '0'));  // BMSIZE    (Table B.1)

        // Export flags, Table B.1 run lengths alternating exclude/include over the two new symbols.
        var export = new BitPacker();
        foreach (int run in exportedSymbol == 0 ? new[] { 0, 1, 1 } : new[] { 1, 1 })
            export.Write("0" + Convert.ToString(run, 2).PadLeft(4, '0'));

        byte[] body = Concat(
            new byte[] { 0x00, 0x01,      // SDFLAGS: SDHUFF = 1, SDREFAGG = 0
                         0, 0, 0, 1,      // SDNUMEXSYMS = 1
                         0, 0, 0, 2 },    // SDNUMNEWSYMS = 2
            bits.ToArray(),
            mmr,
            export.ToArray());

        var symbols = Jbig2Decoder.DecodeSymbolDictionary(body, referredSymbols: null, budget: null);

        var symbol = Assert.Single(symbols);
        Assert.Equal(8, symbol.Width);
        Assert.Equal(4, symbol.Height);
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
                Assert.Equal((collective[y * 2 + exportedSymbol] >> (7 - x)) & 1, symbol.GetPixel(x, y));
    }

    /// <summary>Packs a sequence of written codewords MSB-first, zero-padded to a whole byte.</summary>
    private sealed class BitPacker
    {
        private readonly System.Collections.Generic.List<bool> _bits = new System.Collections.Generic.List<bool>();

        internal void Write(string codeword)
        {
            foreach (char c in codeword) _bits.Add(c == '1');
        }

        internal void PadToByte()
        {
            while (_bits.Count % 8 != 0) _bits.Add(false);
        }

        internal byte[] ToArray()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (int i = 0; i < _bits.Count; i++)
                if (_bits[i]) bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            return bytes;
        }
    }

    /// <summary>
    /// The complete pipeline, end to end: a page + a Huffman symbol dictionary + a Huffman text
    /// region that places the one decoded symbol at a specific, non-trivial position — decoded
    /// through <see cref="Jbig2Decoder.Decode"/> itself, not a single method in isolation, and
    /// checked pixel-by-pixel against the exact expected rectangle on the final page bitmap.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the first real, non-synthetic region-placement test in this file for
    /// either coding mode.</b> <c>Jbig2TextRegionPlacementTests</c>'s own remarks explain why: this
    /// codebase has no MQ (arithmetic) <i>encoder</i>, so a real arithmetic-coded text region body
    /// cannot be synthesised, and every existing arithmetic-path text-region test either asserts
    /// the placement <i>function</i> in isolation or a refusal path that never reaches real
    /// decoding. Huffman has no such gap — every codeword can be hand-constructed from the same
    /// canonical tables the decoder itself builds — so this is the first test able to prove
    /// STRIPT/DT/DFS/regionX/regionY genuinely compose into a correct page-pixel position, for
    /// either coding mode.</para>
    /// <para>The bitstream was generated by a small script replicating this file's own
    /// <c>AssignCanonicalPrefixes</c> algorithm against the same table data (Tables B.6, B.8,
    /// B.11), not hand-derived — table B.6/B.8's codewords are irregular enough that hand
    /// arithmetic is exactly how the collective-bitmap fixture's first attempt went wrong. Chosen
    /// so nearly every bit is 0: a single symbol referenced via a 35-entry runcode preamble where
    /// only index 1 carries a real (1-bit) code, decoding STRIPT=1, DT=1 (net T=0), DFS=0,
    /// symbol ID 0 — all via each table's own shortest codeword.</para>
    /// </remarks>
    [Fact]
    public void Decode_HuffmanSymbolDictionaryAndTextRegion_PlacesTheSymbolAtTheExpectedPosition()
    {
        var symbolDictBody = new byte[]
        {
            0x00, 0x01,             // SDFLAGS: SDHUFF=1, SDREFAGG=0
            0x00, 0x00, 0x00, 0x01, // SDNUMEXSYMS = 1
            0x00, 0x00, 0x00, 0x01, // SDNUMNEWSYMS = 1
            0xE1, 0xD7, 0xF0, 0x00, // HCDH=4, DW=8, DW OOB, BMSIZE=0 (see the split-decode test above)
            0xFF, 0x81, 0x81, 0xFF, // collective bitmap: 8x4 hollow rectangle
            0x00, 0x40,             // export flags: exclude 0, include 1
        };

        const int regionX = 10, regionY = 15;
        var textRegionBody = Concat(
            // Region info (17 bytes): width=8, height=4, x=10, y=15, flags=0 (external combOp=OR).
            new byte[] { 0, 0, 0, 8, 0, 0, 0, 4, 0, 0, 0, regionX, 0, 0, 0, regionY, 0x00 },
            // SBFLAGS (2 bytes BE): SBHUFF=1, SBREFINE=0, LOGSBSTRIPS=0, REFCORNER=1 (TOPLEFT),
            // TRANSPOSED=0, SBCOMBOP=0, SBDEFPIXEL=0, SBDSOFFSET=0, SBRTEMPLATE=0.
            new byte[] { 0x00, 0x11 },
            // Huffman table selection flags (2 bytes BE): all 0 → default tables
            // (SBHUFFFS=B.6, SBHUFFDS=B.8, SBHUFFDT=B.11).
            new byte[] { 0x00, 0x00 },
            // SBNUMINSTANCES = 1 (4 bytes BE).
            new byte[] { 0, 0, 0, 1 },
            // Huffman body: 35x4-bit runcode preamble (only index 1 = length 1) + symbol-length
            // code "0" (decodes runcode value 1 -> length 1, range 1) + pad to byte + STRIPT=1
            // (Table B.11 "0") + DT=1 (Table B.11 "0") + DFS=0 (Table B.6 "00 0000000") + symbol
            // ID 0 (the 1-entry symbol table's own code, "0"). Generated, not hand-derived — see
            // the class remarks.
            new byte[]
            {
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            });

        byte[] stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1, 0, 1, (uint)symbolDictBody.Length), symbolDictBody,
            ReferringSegmentHeader(2, 6, referredTo: 1, pageAssociation: 1,
                dataLength: (uint)textRegionBody.Length), textRegionBody,
            SegmentHeader(3, 51, 0, 0));

        byte[] decoded = Jbig2Decoder.Decode(stream, null, out int pageWidth, out int pageHeight);

        Assert.Equal(64, pageWidth);
        Assert.Equal(64, pageHeight);
        var page = new Jbig2Decoder.Jbig2Bitmap(pageWidth, pageHeight, decoded);

        // The hollow-rectangle pattern at exactly (regionX, regionY), and nowhere else.
        int[] expectedRows = { 0xFF, 0x81, 0x81, 0xFF };
        for (int y = 0; y < pageHeight; y++)
            for (int x = 0; x < pageWidth; x++)
            {
                bool inSymbol = x >= regionX && x < regionX + 8 && y >= regionY && y < regionY + 4;
                int expected = inSymbol
                    ? (expectedRows[y - regionY] >> (7 - (x - regionX))) & 1
                    : 0;
                Assert.Equal(expected, page.GetPixel(x, y));
            }
    }

    /// <summary>
    /// A Huffman text region with SBDEFPIXEL=1 and SBCOMBOP=XOR: the region starts black, the symbol
    /// inverts what is under it, and the page receives the region with OR. The same fixture with
    /// SBCOMBOP=OR must come out all black, so the expected pattern cannot be produced by ignoring
    /// the operator.
    /// </summary>
    [Fact]
    public void Decode_HuffmanTextRegion_SbCombOpXor_InvertsTheSymbolOnABlackRegion()
    {
        var symbolDictBody = new byte[]
        {
            0x00, 0x01,             // SDFLAGS: SDHUFF=1, SDREFAGG=0
            0x00, 0x00, 0x00, 0x01, // SDNUMEXSYMS = 1
            0x00, 0x00, 0x00, 0x01, // SDNUMNEWSYMS = 1
            0xE1, 0xD7, 0xF0, 0x00, // HCDH=4, DW=8, DW OOB, BMSIZE=0
            0xFF, 0x81, 0x81, 0xFF, // collective bitmap: 8x4 hollow rectangle
            0x00, 0x40,             // export flags: exclude 0, include 1
        };

        const int regionX = 10, regionY = 15;
        byte[] Body(ushort sbFlags) => Concat(
            new byte[] { 0, 0, 0, 8, 0, 0, 0, 4, 0, 0, 0, regionX, 0, 0, 0, regionY, 0x00 },
            new byte[] { (byte)(sbFlags >> 8), (byte)sbFlags },
            new byte[] { 0x00, 0x00 },
            new byte[] { 0, 0, 0, 1 },
            new byte[]
            {
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            });

        // REFCORNER=1 (TOPLEFT) in bit 4; SBCOMBOP in bits 7-8; SBDEFPIXEL in bit 9.
        const ushort refCornerTopLeft = 0x0011, combOpXor = 0x0100, defPixelOne = 0x0200;
        byte[] Decode(ushort sbFlags)
        {
            byte[] stream = Concat(
                SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
                SegmentHeader(1, 0, 1, (uint)symbolDictBody.Length), symbolDictBody,
                ReferringSegmentHeader(2, 6, referredTo: 1, pageAssociation: 1,
                    dataLength: (uint)Body(sbFlags).Length), Body(sbFlags),
                SegmentHeader(3, 51, 0, 0));
            byte[] decoded = Jbig2Decoder.Decode(stream, null, out int w, out int h);
            return decoded;
        }

        byte[] xorPage = Decode((ushort)(refCornerTopLeft | combOpXor | defPixelOne));
        byte[] orPage = Decode((ushort)(refCornerTopLeft | defPixelOne));

        var xorBitmap = new Jbig2Decoder.Jbig2Bitmap(64, 64, xorPage);
        var orBitmap = new Jbig2Decoder.Jbig2Bitmap(64, 64, orPage);
        int[] expectedRows = { 0xFF, 0x81, 0x81, 0xFF };
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                // The region is 8x4 and the page is painted only inside it; outside it stays white.
                bool inRegion = x >= regionX && x < regionX + 8 && y >= regionY && y < regionY + 4;
                int symbolBit = inRegion ? (expectedRows[y - regionY] >> (7 - (x - regionX))) & 1 : 0;
                Assert.Equal(inRegion ? 1 - symbolBit : 0, xorBitmap.GetPixel(x, y));
                Assert.Equal(inRegion ? 1 : 0, orBitmap.GetPixel(x, y));
            }
    }

    /// <summary>
    /// The symbol-ID table's run codes — 32 (repeat the previous length), 33 and 34 (runs of zero
    /// lengths) — build the table they describe, so an instance names the symbol it means.
    /// </summary>
    /// <remarks>
    /// <para><b>These were transcribed and never run</b>: the end-to-end fixture above references one
    /// symbol, so its only length is signalled directly. The run-code semantics were checked
    /// against two independent implementations before this fixture was written, rather than from
    /// recollection — the two agree: 32 repeats the previous length 3 + (2 bits)
    /// times, 33 is 3 + (3 bits) zero lengths, 34 is 11 + (7 bits), and the reader skips to a byte
    /// boundary after the last length. (They differ on 32 immediately after a zero run — an independent decoder
    /// repeats the previous <i>symbol's</i> length, an independent implementation the last directly coded one — so this
    /// fixture never puts one there.)</para>
    ///
    /// <para><b>The symbols are told apart by width</b> — symbol <i>i</i> is <i>i</i> + 1 pixels wide —
    /// so a run code misread by one shifts the table and draws a line of the wrong length, which
    /// the pixel check sees. The code lengths: symbol 0 is 3 (direct), 1–4 repeat it (32, count 4),
    /// 5–9 are zero (33, count 5), 10–21 are zero (34, count 12), and 22 and 23 are 3 (direct).
    /// Every non-zero length is 3, and the run codes all have length 2, so both canonical code sets
    /// are simply consecutive in index order and need no derivation. Everything drawn from a
    /// standard table is encoded through the decoder's own externally verified rows.</para>
    /// </remarks>
    [Theory]
    [InlineData(4)]    // the last symbol the repeat run gave a length
    [InlineData(22)]   // the first symbol after both zero runs
    [InlineData(23)]   // the last symbol of the table
    public void HuffmanTextRegion_RunCodesBuildTheSymbolIdTable_AndTheInstanceDrawsTheNamedSymbol(int symbolId)
    {
        const int symbolCount = 24, regionX = 5, regionY = 7;

        // ---- symbol dictionary: 24 all-black symbols, one row high, widths 1..24 ----
        var sd = new BitPacker();
        EncodeValue(sd, Jbig2Decoder.StandardTableD, 1);                 // HCDH = 1   (B.4)
        for (int i = 0; i < symbolCount; i++)
            EncodeValue(sd, Jbig2Decoder.StandardTableB, 1);             // DW = +1    (B.2)
        EncodeOob(sd, Jbig2Decoder.StandardTableB);                      // DW OOB     (B.2)
        EncodeValue(sd, Jbig2Decoder.StandardTableA, 0);                 // BMSIZE = 0 (B.1)

        int totalWidth = symbolCount * (symbolCount + 1) / 2;            // 300
        var collective = new byte[(totalWidth + 7) / 8];
        for (int x = 0; x < totalWidth; x++) collective[x / 8] |= (byte)(0x80 >> (x % 8));

        var exportFlags = new BitPacker();
        EncodeValue(exportFlags, Jbig2Decoder.StandardTableA, 0);        // exclude 0
        EncodeValue(exportFlags, Jbig2Decoder.StandardTableA, symbolCount); // include all

        byte[] symbolDictBody = Concat(
            new byte[] { 0x00, 0x01, 0, 0, 0, symbolCount, 0, 0, 0, symbolCount },
            sd.ToArray(), collective, exportFlags.ToArray());

        // ---- text region: the run-coded symbol-ID table, then one instance ----
        var tr = new BitPacker();
        // 35 four-bit run-code lengths: 3, 32, 33 and 34 get length 2 (codes 00, 01, 10, 11).
        for (int runcode = 0; runcode < 35; runcode++)
            tr.Write(runcode is 3 or 32 or 33 or 34 ? "0010" : "0000");
        tr.Write("00");                  // symbol 0:      runcode 3  → length 3
        tr.Write("01" + "01");           // symbols 1-4:   runcode 32, count 3 + 1
        tr.Write("10" + "010");          // symbols 5-9:   runcode 33, count 3 + 2
        tr.Write("11" + "0000001");      // symbols 10-21: runcode 34, count 11 + 1
        tr.Write("00");                  // symbol 22:     runcode 3  → length 3
        tr.Write("00");                  // symbol 23:     runcode 3  → length 3
        tr.PadToByte();
        EncodeValue(tr, Jbig2Decoder.StandardTableK, 1);                 // STRIPT = 1 (B.11)
        EncodeValue(tr, Jbig2Decoder.StandardTableK, 1);                 // DT = 1     (B.11)
        EncodeValue(tr, Jbig2Decoder.StandardTableF, 0);                 // DFS = 0    (B.6)
        // Symbols with a non-zero length, in ID order: 0,1,2,3,4,22,23 → codes 000..110.
        int ordinal = System.Array.IndexOf(new[] { 0, 1, 2, 3, 4, 22, 23 }, symbolId);
        tr.Write(Convert.ToString(ordinal, 2).PadLeft(3, '0'));

        var textRegionBody = Concat(
            new byte[] { 0, 0, 0, 30, 0, 0, 0, 1, 0, 0, 0, regionX, 0, 0, 0, regionY, 0x00 },
            new byte[] { 0x00, 0x11 },   // SBFLAGS: SBHUFF = 1, REFCORNER = TOPLEFT
            new byte[] { 0x00, 0x00 },   // Huffman selectors: B.6 / B.8 / B.11
            new byte[] { 0, 0, 0, 1 },   // SBNUMINSTANCES = 1
            tr.ToArray());

        byte[] stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 16),
            SegmentHeader(1, 0, 1, (uint)symbolDictBody.Length), symbolDictBody,
            ReferringSegmentHeader(2, 6, referredTo: 1, pageAssociation: 1,
                dataLength: (uint)textRegionBody.Length), textRegionBody,
            SegmentHeader(3, 51, 0, 0));

        byte[] decoded = Jbig2Decoder.Decode(stream, null, out int pageWidth, out int pageHeight);
        var page = new Jbig2Decoder.Jbig2Bitmap(pageWidth, pageHeight, decoded);

        // A black run exactly symbolId + 1 pixels long at (regionX, regionY), and nothing else.
        for (int y = 0; y < pageHeight; y++)
            for (int x = 0; x < pageWidth; x++)
            {
                bool inSymbol = y == regionY && x >= regionX && x < regionX + symbolId + 1;
                Assert.True((inSymbol ? 1 : 0) == page.GetPixel(x, y),
                    $"pixel ({x},{y}) should be {(inSymbol ? "black" : "white")} for symbol {symbolId}");
            }
    }

    /// <summary>Writes <paramref name="value"/> through the row of a standard table that covers it.</summary>
    private static void EncodeValue(BitPacker bits, Jbig2Decoder.Jbig2HuffmanTable table, int value)
    {
        foreach (var e in table.Entries)
        {
            if (e.RangeBits < 0 || e.LowerSign || e.PrefixLen == 0) continue;
            long high = (long)e.RangeLow + (1L << e.RangeBits) - 1;
            if (value < e.RangeLow || value > high) continue;
            bits.Write(Convert.ToString(e.Prefix, 2).PadLeft(e.PrefixLen, '0'));
            if (e.RangeBits > 0)
                bits.Write(Convert.ToString(value - e.RangeLow, 2).PadLeft(e.RangeBits, '0'));
            return;
        }
        throw new InvalidOperationException($"no ordinary row of the table encodes {value}");
    }

    /// <summary>Writes a standard table's out-of-band codeword.</summary>
    private static void EncodeOob(BitPacker bits, Jbig2Decoder.Jbig2HuffmanTable table)
    {
        foreach (var e in table.Entries)
            if (e.RangeBits == -1)
            {
                bits.Write(Convert.ToString(e.Prefix, 2).PadLeft(e.PrefixLen, '0'));
                return;
            }
        throw new InvalidOperationException("the table has no OOB row");
    }

    /// <summary>
    /// A Huffman text region that references no symbol dictionary at all but claims a nonzero
    /// instance count must still refuse loudly, not silently draw nothing.
    /// </summary>
    /// <remarks>
    /// <para>Once non-refined Huffman text regions started decoding for real, the ORIGINAL form
    /// of this test — a fixture with <c>SBNUMINSTANCES = 0</c> and no referenced dictionary —
    /// stopped proving anything: zero instances is a legitimate, if degenerate, case that
    /// correctly succeeds by drawing nothing. Retargeted to <c>SBNUMINSTANCES = 1</c>: with no
    /// symbols available, the symbol-ID Huffman table built from zero referenced symbols has no
    /// entries, so decoding the one instance's symbol ID exhausts the bit-length search and
    /// throws — a loud failure, not a silently-empty page.</para>
    /// </remarks>
    [Fact]
    public void HuffmanTextRegion_WithNoReferencedSymbolsButNonzeroInstances_FallsBackAudibly()
    {
        var txtBody = new byte[17 + 2 + 32];
        txtBody[18] = 0x01;                // SBHUFF = 1, SBREFINE = 0, in SBFLAGS' low byte
        // huffman_flags (offset 19-20) left 0 → default tables (B.11/B.6/B.8), no selector throw.
        txtBody[24] = 0x01;                // SBNUMINSTANCES = 1 (4-byte BE, offset 21-24)

        // No symbol-dictionary segment: nothing is available for the one instance to reference.
        byte[] stream = Concat(
            SegmentHeader(0, 48, 1, 19), PageInfoBody(64, 64),
            SegmentHeader(1,  6, 1, (uint)txtBody.Length), txtBody,
            SegmentHeader(2, 51, 0, 0));

        byte[] result = new Jbig2DecodeFilter().Decode(stream, null);

        Assert.NotNull(Jbig2DecodeFilter.LastFallbackReason);
        Assert.Equal(stream, result);
    }
}
