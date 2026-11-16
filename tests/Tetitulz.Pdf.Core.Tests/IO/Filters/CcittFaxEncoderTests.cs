// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Tests for the CCITT T.6 (Group 4) <b>encoder</b>.
///
/// The acceptance test is the round trip: every raster is packed, encoded, and then
/// decoded again through the real <see cref="CcittFaxDecodeFilter"/> — the same code
/// path a PDF consumer takes — and the result is compared to the packed input
/// <b>byte for byte</b>.  A round trip is falsifiable in a way a "did it return
/// bytes" check is not: the filter's failure mode is to hand back the raw compressed
/// input, which never equals the raster.
///
/// Padding: the decoder writes the bits past /Columns in each row's last byte as the
/// "white" value for the chosen /BlackIs1 convention, so <see cref="Pack"/> does the
/// same and byte-exact comparison stays meaningful for non-multiple-of-8 widths.
/// </summary>
public class CcittFaxEncoderTests
{
    private readonly ITestOutputHelper _output;

    public CcittFaxEncoderTests(ITestOutputHelper output) => _output = output;

    // ==================================================================
    //  Helpers
    // ==================================================================

    /// <summary>
    /// Packs a raster of "true = black" flags into the 1-bpp MSB-first layout the
    /// decoder produces.  Bits past <paramref name="columns"/> are set to the white
    /// value, matching the decoder.
    /// </summary>
    private static byte[] Pack(bool[][] black, int columns, bool blackIs1)
    {
        int rows = black.Length;
        int stride = (columns + 7) / 8;
        var packed = new byte[stride * rows];

        // /BlackIs1 false (the PDF default) → 1 is white; true → 0 is white.
        byte whiteFill = blackIs1 ? (byte)0x00 : (byte)0xFF;
        for (int i = 0; i < packed.Length; i++) packed[i] = whiteFill;

        for (int y = 0; y < rows; y++)
        {
            int off = y * stride;
            for (int x = 0; x < columns; x++)
            {
                if (!black[y][x]) continue;
                int mask = 0x80 >> (x & 7);
                if (blackIs1) packed[off + (x >> 3)] |= (byte)mask;
                else          packed[off + (x >> 3)] &= (byte)~mask;
            }
        }
        return packed;
    }

    private static PdfDictionary G4Parms(int columns, int? rows, bool blackIs1)
    {
        var d = new PdfDictionary();
        d[PdfName.K] = new PdfNumber(-1);
        d[PdfName.Columns] = new PdfNumber(columns);
        if (rows.HasValue) d[PdfName.Rows] = new PdfNumber(rows.Value);
        d[PdfName.BlackIs1] = blackIs1 ? PdfBoolean.True : PdfBoolean.False;
        d[PdfName.EndOfBlock] = PdfBoolean.True;
        return d;
    }

    /// <summary>
    /// Packs, encodes, decodes and asserts byte-exact equality.  Returns the encoded
    /// codestream so size-sensitive tests can measure it.
    /// </summary>
    private static byte[] AssertRoundTrip(bool[][] black, int columns,
        bool blackIs1 = false, bool passRows = true)
    {
        int rows = black.Length;
        byte[] packed = Pack(black, columns, blackIs1);

        byte[] encoded = CcittFaxEncoder.Encode(packed, columns, rows, blackIs1);

        byte[] decoded = new CcittFaxDecodeFilter()
            .Decode(encoded, G4Parms(columns, passRows ? rows : (int?)null, blackIs1));

        Assert.Equal(packed, decoded);
        return encoded;
    }

    private static bool[][] Raster(int columns, int rows, Func<int, int, bool> isBlack)
    {
        var r = new bool[rows][];
        for (int y = 0; y < rows; y++)
        {
            r[y] = new bool[columns];
            for (int x = 0; x < columns; x++) r[y][x] = isBlack(x, y);
        }
        return r;
    }

    // ==================================================================
    //  1. Round trip — the shapes that break each coding mode differently
    // ==================================================================

    [Fact]
    public void Encode_AllWhiteRaster_RoundTripsExactly()
    {
        // Nothing but V(0) against an all-white reference line — one bit per row.
        byte[] encoded = AssertRoundTrip(Raster(64, 40, (x, y) => false), columns: 64);
        Assert.True(encoded.Length <= 10,
            $"40 all-white rows should cost ~1 bit each plus EOFB; got {encoded.Length} bytes.");
    }

    [Fact]
    public void Encode_AllBlackRaster_RoundTripsExactly()
    {
        AssertRoundTrip(Raster(64, 40, (x, y) => true), columns: 64);
    }

    [Fact]
    public void Encode_VerticalStripes_RoundTripsExactly()
    {
        // Every row identical → row 0 costs horizontal/vertical codes, every later row
        // is a run of V(0)s.  Exercises the reference-line tracking hardest.
        AssertRoundTrip(Raster(64, 40, (x, y) => (x / 3) % 2 == 0), columns: 64);
    }

    [Fact]
    public void Encode_SingleColumnWideStripes_RoundTripsExactly()
    {
        // 1-pixel-wide alternating columns: the reference line's changing elements are
        // adjacent, which is where pass mode and V(±1) compete.
        AssertRoundTrip(Raster(64, 20, (x, y) => x % 2 == 0), columns: 64);
    }

    [Fact]
    public void Encode_HorizontalStripes_RoundTripsExactly()
    {
        // Alternating solid-black and solid-white rows: every row disagrees completely
        // with its reference, forcing horizontal mode on each.
        AssertRoundTrip(Raster(64, 40, (x, y) => (y / 2) % 2 == 0), columns: 64);
    }

    [Fact]
    public void Encode_Diagonal_RoundTripsExactly()
    {
        // A one-pixel diagonal is the V(+1) case, plus a vanishing/appearing run at the
        // ends of the raster.
        AssertRoundTrip(Raster(48, 48, (x, y) => x == y), columns: 48);
    }

    [Fact]
    public void Encode_ThickDiagonal_RoundTripsExactly()
    {
        AssertRoundTrip(Raster(50, 60, (x, y) => x >= y / 2 && x < y / 2 + 7), columns: 50);
    }

    [Fact]
    public void Encode_Checkerboard_RoundTripsExactly()
    {
        // Worst case for G4: every pixel is a changing element and every row is the
        // inverse of its reference.  It must still round trip (it will not compress).
        AssertRoundTrip(Raster(32, 32, (x, y) => (x + y) % 2 == 0), columns: 32);
    }

    [Fact]
    public void Encode_SingleBlackPixel_RoundTripsExactly()
    {
        AssertRoundTrip(Raster(40, 30, (x, y) => x == 17 && y == 11), columns: 40);
    }

    [Fact]
    public void Encode_SingleBlackPixelAtOrigin_RoundTripsExactly()
    {
        // A changing element at x = 0 is the a0 = -1 boundary case.
        AssertRoundTrip(Raster(24, 5, (x, y) => x == 0 && y == 0), columns: 24);
    }

    [Fact]
    public void Encode_SingleBlackPixelAtLastColumn_RoundTripsExactly()
    {
        AssertRoundTrip(Raster(24, 5, (x, y) => x == 23 && y == 4), columns: 24);
    }

    [Theory]
    [InlineData(12345, 64, 40, 50)]
    [InlineData(12345, 33, 17, 10)]
    [InlineData(12345, 129, 71, 25)]
    [InlineData(999, 7, 5, 90)]
    [InlineData(2468, 256, 64, 3)]
    [InlineData(1357, 100, 100, 97)]
    public void Encode_PseudoRandomRaster_RoundTripsExactly(int seed, int columns, int rows, int blackPercent)
    {
        // Fixed seed → the same raster on every machine and every run.
        var rng = new Random(seed);
        AssertRoundTrip(Raster(columns, rows, (x, y) => rng.Next(100) < blackPercent), columns);
    }

    [Fact]
    public void Encode_PseudoRandomRunsRaster_RoundTripsExactly()
    {
        // Random *runs* rather than random pixels: closer to real scanned content and
        // the only generator here that reliably produces long make-up-coded runs.
        var rng = new Random(24680);
        var raster = new bool[80][];
        for (int y = 0; y < 80; y++)
        {
            raster[y] = new bool[300];
            int x = 0;
            bool black = false;
            while (x < 300)
            {
                int run = 1 + rng.Next(90);
                for (int i = 0; i < run && x < 300; i++, x++) raster[y][x] = black;
                black = !black;
            }
        }
        AssertRoundTrip(raster, columns: 300);
    }

    // ==================================================================
    //  Exact codestream — pins pass mode, horizontal mode and EOFB
    // ==================================================================

    [Fact]
    public void Encode_PassMode_ProducesExactSpecifiedBits()
    {
        // 8 x 2.  Row 0 = W W B B B B W W, row 1 = all white.
        //
        // Row 0 (reference = imaginary all-white line):
        //   a0 = -1, a1 = 2, b1 = b2 = 8  →  |a1 - b1| = 6 > 3  →  horizontal mode
        //     "001" + white-run-2 "0111" + black-run-4 "011"
        //   a0 = 6, a1 = b1 = 8            →  V(0) "1"
        // Row 1 (reference = row 0):
        //   a0 = -1, a1 = 8, b1 = 2, b2 = 6, b2 < a1  →  pass mode "0001"
        //   a0 = 6,  a1 = b1 = 8           →  V(0) "1"
        // EOFB: "000000000001" x 2.
        //
        // 001 0111 011 1 | 0001 1 | EOFB  =  40 bits exactly, no fill needed.
        var raster = Raster(8, 2, (x, y) => y == 0 && x >= 2 && x < 6);
        byte[] packed = Pack(raster, columns: 8, blackIs1: true);
        Assert.Equal(new byte[] { 0x3C, 0x00 }, packed);   // sanity: the fixture is the raster we meant

        byte[] encoded = CcittFaxEncoder.Encode(packed, columns: 8, rows: 2, blackIs1: true);

        Assert.Equal(new byte[] { 0x2E, 0xE3, 0x00, 0x10, 0x01 }, encoded);
    }

    [Fact]
    public void Encode_AllWhiteSingleRow_ProducesExactSpecifiedBits()
    {
        // One V(0) then EOFB: "1" + "000000000001" + "000000000001" = 25 bits → 4 bytes,
        // the last holding a single set bit followed by seven fill zeros.
        //   1000 0000 | 0000 1000 | 0000 0000 | 1000 0000
        byte[] packed = Pack(Raster(8, 1, (x, y) => false), columns: 8, blackIs1: true);
        byte[] encoded = CcittFaxEncoder.Encode(packed, columns: 8, rows: 1, blackIs1: true);

        Assert.Equal(new byte[] { 0x80, 0x08, 0x00, 0x80 }, encoded);
    }

    // ==================================================================
    //  2. Compression actually happens
    // ==================================================================

    [Fact]
    public void Encode_MostlyWhiteScannedPage_BeatsFlateOnTheSameRaster()
    {
        // A 1728 x 2200 bitonal page (US Letter at 200 dpi) of text-like spans whose
        // edges jitter by a pixel from row to row — the structure a real scan has, and
        // the structure G4's vertical modes are for.  Deflate cannot exploit it because
        // a one-pixel horizontal shift changes every byte of the row.
        const int columns = 1728, rows = 2200;
        var rng = new Random(20260812);
        var raster = new bool[rows][];

        int[] spanStart = new int[46];
        int[] spanEnd = new int[46];
        int spans = 0;

        for (int y = 0; y < rows; y++)
        {
            if (y % 26 == 0)
            {
                // Start a new "line of text": fresh spans, or a blank inter-line gap.
                spans = (y / 26) % 6 == 5 ? 0 : 30 + rng.Next(16);
                int x = 120;
                for (int s = 0; s < spans; s++)
                {
                    x += 4 + rng.Next(20);
                    int w = 6 + rng.Next(34);
                    if (x + w >= columns - 120) { spans = s; break; }
                    spanStart[s] = x;
                    spanEnd[s] = x + w;
                    x += w;
                }
            }
            else
            {
                for (int s = 0; s < spans; s++)
                {
                    spanStart[s] += rng.Next(3) - 1;
                    spanEnd[s] += rng.Next(3) - 1;
                    if (spanEnd[s] <= spanStart[s]) spanEnd[s] = spanStart[s] + 1;
                }
            }

            var row = new bool[columns];
            for (int s = 0; s < spans; s++)
                for (int x = Math.Max(0, spanStart[s]); x < Math.Min(columns, spanEnd[s]); x++)
                    row[x] = true;
            raster[y] = row;
        }

        byte[] packed = Pack(raster, columns, blackIs1: false);
        byte[] g4 = AssertRoundTrip(raster, columns);          // correctness first
        byte[] flate = FlateEncodeFilter.Encode(packed);

        _output.WriteLine($"packed={packed.Length}  g4={g4.Length}  flate={flate.Length}  " +
                          $"g4:packed={(double)packed.Length / g4.Length:F1}x  " +
                          $"flate:g4={(double)flate.Length / g4.Length:F1}x");

        // Real thresholds, set well inside the measured margins (see the report): the
        // point is that G4 is categorically better here, not that it squeaks past.
        Assert.True(g4.Length * 6 < packed.Length,
            $"G4 should compress this page at least 6x; packed={packed.Length} g4={g4.Length}.");
        Assert.True(g4.Length * 2 < flate.Length,
            $"G4 should be at least 2x smaller than Flate on the same raster; " +
            $"g4={g4.Length} flate={flate.Length}.");
    }

    // ==================================================================
    //  3. /BlackIs1 both ways
    // ==================================================================

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Encode_BlackIs1_RoundTripsExactlyBothWays(bool blackIs1)
    {
        var rng = new Random(31415);
        AssertRoundTrip(Raster(70, 45, (x, y) => rng.Next(100) < 35), columns: 70, blackIs1: blackIs1);
    }

    [Fact]
    public void Encode_BlackIs1_InvertsWhichBitMeansBlack()
    {
        // The same picture under the two conventions must produce the *same*
        // codestream from *complementary* packed input.  If Encode ignored blackIs1
        // this would still pass on one of the two, so assert both directions.
        var raster = Raster(16, 4, (x, y) => (x + y) % 3 == 0);

        byte[] packedFalse = Pack(raster, 16, blackIs1: false);
        byte[] packedTrue = Pack(raster, 16, blackIs1: true);

        for (int i = 0; i < packedFalse.Length; i++)
            Assert.Equal((byte)~packedTrue[i], packedFalse[i]);

        Assert.Equal(
            CcittFaxEncoder.Encode(packedTrue, 16, 4, blackIs1: true),
            CcittFaxEncoder.Encode(packedFalse, 16, 4, blackIs1: false));
    }

    // ==================================================================
    //  4. Odd column counts (row padding)
    // ==================================================================

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(15)]
    [InlineData(17)]
    [InlineData(63)]
    [InlineData(65)]
    [InlineData(1000)]
    [InlineData(1729)]
    public void Encode_OddColumnCounts_RoundTripExactly(int columns)
    {
        var rng = new Random(4242 + columns);
        AssertRoundTrip(Raster(columns, 13, (x, y) => rng.Next(100) < 40), columns);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(9)]
    [InlineData(1000)]
    public void Encode_OddColumnCounts_BlackIs1_RoundTripExactly(int columns)
    {
        var rng = new Random(8484 + columns);
        AssertRoundTrip(Raster(columns, 11, (x, y) => rng.Next(100) < 60), columns, blackIs1: true);
    }

    [Fact]
    public void Encode_GarbagePaddingBits_Malformed_AreIgnoredNotEncoded()
    {
        // Padding bits past /Columns carry no pixel.  A caller that leaves them set to
        // the *black* value must still get the same codestream — and decoding it must
        // yield the canonical padding, not the caller's garbage.
        const int columns = 11;  // 5 padding bits in each 2-byte row
        var raster = Raster(columns, 6, (x, y) => (x * 3 + y) % 4 == 0);

        byte[] clean = Pack(raster, columns, blackIs1: false);
        byte[] dirty = (byte[])clean.Clone();
        for (int y = 0; y < 6; y++) dirty[y * 2 + 1] &= 0xE0;   // clear the 5 padding bits → "black"

        Assert.NotEqual(clean, dirty);   // the fixture really is different

        Assert.Equal(
            CcittFaxEncoder.Encode(clean, columns, 6),
            CcittFaxEncoder.Encode(dirty, columns, 6));

        byte[] decoded = new CcittFaxDecodeFilter()
            .Decode(CcittFaxEncoder.Encode(dirty, columns, 6), G4Parms(columns, 6, false));
        Assert.Equal(clean, decoded);
    }

    // ==================================================================
    //  Make-up codes and the EOFB terminator
    // ==================================================================

    [Fact]
    public void Encode_RunsLongerThanTheMakeupTable_RoundTripExactly()
    {
        // 6000 columns forces horizontal runs past 2560, so WriteRun must repeat the
        // 2560 make-up code before the final make-up + terminating pair.
        const int columns = 6000;
        var raster = Raster(columns, 3, (x, y) => y switch
        {
            0 => x >= 5000,                    // white 5000 (2560 + 2432 + 8), black 1000
            1 => x < 3000,                     // black 3000, white 3000
            _ => x >= 100 && x < 5900,
        });
        AssertRoundTrip(raster, columns);
    }

    [Fact]
    public void Encode_WithoutRowsParameter_EofbStopsTheDecoder()
    {
        // With no /Rows the decoder runs until it sees EOFB.  If Encode omitted the
        // terminator it would keep decoding into the zero fill and return extra rows.
        var rng = new Random(5150);
        AssertRoundTrip(Raster(37, 23, (x, y) => rng.Next(100) < 45), columns: 37, passRows: false);
    }

    [Fact]
    public void Encode_EndsWithEndOfFacsimileBlock()
    {
        byte[] packed = Pack(Raster(16, 3, (x, y) => x < 5), columns: 16, blackIs1: true);
        byte[] encoded = CcittFaxEncoder.Encode(packed, 16, 3, blackIs1: true);

        // Read the last 24 significant bits back out of the codestream and check they
        // are two EOL codes.  Total bit count is unknown, so search from the tail: the
        // final EOFB must end at or within 7 bits of the last byte boundary.
        Assert.True(ContainsEofbAtTail(encoded),
            "encoded G4 stream must end with EOFB (two 000000000001 codes) plus zero fill.");
    }

    private static bool ContainsEofbAtTail(byte[] data)
    {
        int totalBits = data.Length * 8;
        for (int fill = 0; fill < 8; fill++)
        {
            int end = totalBits - fill;
            int start = end - 24;
            if (start < 0) continue;
            long value = 0;
            for (int i = start; i < end; i++)
                // `& 1` leaves 0 or 1, so the int operand can never be negative and the long OR was
                // already correct; the uint cast keeps the compiler from sign-extending it (CS0675).
                value = (value << 1) | (uint)((data[i / 8] >> (7 - (i % 8))) & 1);
            if (value == 0x001001) return true;
        }
        return false;
    }

    // ==================================================================
    //  5. Adversarial input — typed refusals, never a hang or bad output
    // ==================================================================

    [Fact]
    public void Encode_NullBuffer_Adversarial_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() => CcittFaxEncoder.Encode(null!, 8, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Encode_ZeroOrNegativeColumns_Adversarial_Throws(int columns)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CcittFaxEncoder.Encode(new byte[64], columns, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Encode_ZeroOrNegativeRows_Adversarial_Throws(int rows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CcittFaxEncoder.Encode(new byte[64], 8, rows));
    }

    [Fact]
    public void Encode_TruncatedBuffer_Malformed_ThrowsInsteadOfReadingPastTheEnd()
    {
        // 9 columns → 2 bytes per row; 4 rows need 8 bytes.  Give it 7.
        var ex = Assert.Throws<ArgumentException>(
            () => CcittFaxEncoder.Encode(new byte[7], columns: 9, rows: 4));
        Assert.Equal("packedRows", ex.ParamName);
    }

    [Fact]
    public void Encode_EmptyBuffer_Adversarial_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => CcittFaxEncoder.Encode(Array.Empty<byte>(), columns: 8, rows: 1));
    }

    [Fact]
    public void Encode_OversizedColumns_Adversarial_ThrowsWithoutAllocating()
    {
        // Refused on the geometry alone — note the buffer is empty, so if the check ran
        // after the length test this would surface as the wrong exception type.
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => CcittFaxEncoder.Encode(Array.Empty<byte>(), columns: 2_000_000, rows: 1));
        Assert.Equal("columns", ex.ParamName);
    }

    [Fact]
    public void Encode_OversizedRows_Adversarial_ThrowsWithoutAllocating()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => CcittFaxEncoder.Encode(Array.Empty<byte>(), columns: 8, rows: 1_000_000));
        Assert.Equal("rows", ex.ParamName);
    }

    [Fact]
    public void Encode_OversizedPixelBudget_Adversarial_ThrowsWithoutAllocating()
    {
        // Each dimension is individually legal; the product is 20 billion pixels.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CcittFaxEncoder.Encode(Array.Empty<byte>(), columns: 1_000_000, rows: 200_000));
    }

    [Fact]
    public void Encode_LargeAllBlackRaster_Adversarial_TerminatesAndRoundTrips()
    {
        // Solid black at the decoder's own row cap shape: proves every coding loop makes
        // forward progress on degenerate content rather than spinning.
        AssertRoundTrip(Raster(1728, 400, (x, y) => true), columns: 1728);
    }
}
