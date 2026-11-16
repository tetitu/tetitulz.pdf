// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// ECC-200 geometry, and decoding a Data Matrix module grid.
/// </summary>
/// <remarks>
/// <para><b>A Data Matrix has two grids and the encoder was using the wrong one.</b> The
/// <i>symbol</i> is what gets printed; the <i>mapping matrix</i> is the symbol with every region's
/// two finder rows and two finder columns removed, and ISO/IEC 16022 Annex F's placement operates on
/// the mapping matrix. It was being run over the whole symbol, after which the finder patterns were
/// painted straight over the data — 34 of 98 placed modules destroyed on a 10×10.</para>
///
/// <para><b>Which grid is which is decidable from the size table alone</b>, and
/// <see cref="EveryMappingMatrixHoldsExactlyItsCodewords"/> decides it: for all twenty-one sizes the
/// mapping matrix holds precisely as many modules as the codewords need. That check does not depend
/// on owning the standard, on the encoder, or on the reader — which is what made it safe to change
/// the layout on the strength of it.</para>
/// </remarks>
public class DataMatrixReaderTests
{
    // ================================================================
    //  Geometry
    // ================================================================

    /// <summary>
    /// Every symbol size's mapping matrix holds exactly the modules its codewords need.
    /// </summary>
    /// <remarks>
    /// <para><b>The arithmetic that settles the mapping-matrix question.</b> Data plus
    /// error-correction codewords, times eight bits, must equal the mapping matrix's area — less the
    /// four modules ECC-200 spends on a fixed corner pattern when both mapping dimensions are 2 more
    /// than a multiple of 4, which is the one case Annex F's walk cannot reach.</para>
    ///
    /// <para>Compute the same thing from the <i>symbol's</i> dimensions instead and ten of the
    /// fifteen square sizes are wrong by a wide margin. So this is not a restatement of the table: it
    /// is the check that says the finder rows and columns must come out first. It covers the six
    /// rectangular sizes too, whose rows are 2 more than a multiple of 4 in three cases and whose
    /// corners the walk fills anyway.</para>
    /// </remarks>
    [Fact]
    public void EveryMappingMatrixHoldsExactlyItsCodewords()
    {
        for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
        {
            int area = DataMatrixLayout.MappingRows(symbol) * DataMatrixLayout.MappingColumns(symbol);
            int corner = DataMatrixLayout.HasCornerPattern(symbol)
                ? DataMatrixLayout.CornerModules
                : 0;

            int codewordBits =
                (DataMatrixLayout.DataCodewords(symbol) + DataMatrixLayout.EcCodewords(symbol)) * 8;

            Assert.Equal(codewordBits, area - corner);
        }
    }

    /// <summary>No mapping-matrix cell falls on a finder module.</summary>
    /// <remarks>
    /// <b>The defect, asserted directly.</b> Data and finders cannot share a module: whichever is
    /// painted second wins, and it was the finders. This walks every mapping cell of every size and
    /// checks where it lands.
    /// </remarks>
    [Fact]
    public void NoMappingCellLandsOnAFinderModule()
    {
        for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
        {
            int regionHeight = DataMatrixLayout.Rows(symbol) / DataMatrixLayout.RegionRows(symbol);
            int regionWidth = DataMatrixLayout.Columns(symbol) / DataMatrixLayout.RegionColumns(symbol);

            for (int y = 0; y < DataMatrixLayout.MappingRows(symbol); y++)
                for (int x = 0; x < DataMatrixLayout.MappingColumns(symbol); x++)
                {
                    DataMatrixLayout.ToSymbol(symbol, y, x, out int row, out int column);

                    int inRegionY = row % regionHeight, inRegionX = column % regionWidth;
                    bool onFinder = inRegionY == 0 || inRegionY == regionHeight - 1
                                    || inRegionX == 0 || inRegionX == regionWidth - 1;

                    Assert.False(onFinder,
                        $"size {DataMatrixLayout.Rows(symbol)}: mapping ({y},{x}) lands on the "
                        + $"finder at ({row},{column})");
                }
        }
    }

    /// <summary>Distinct mapping cells land on distinct symbol modules.</summary>
    /// <remarks>
    /// The other half of the previous test: a region transform that is off by one could keep every
    /// cell clear of the finders and still map two of them onto the same module, silently losing a
    /// bit.
    /// </remarks>
    [Fact]
    public void TheRegionTransformIsOneToOne()
    {
        for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
        {
            var taken = new bool[DataMatrixLayout.Rows(symbol), DataMatrixLayout.Columns(symbol)];

            for (int y = 0; y < DataMatrixLayout.MappingRows(symbol); y++)
                for (int x = 0; x < DataMatrixLayout.MappingColumns(symbol); x++)
                {
                    DataMatrixLayout.ToSymbol(symbol, y, x, out int row, out int column);

                    Assert.False(taken[row, column],
                        $"size {DataMatrixLayout.Rows(symbol)}: two mapping cells share ({row},{column})");
                    taken[row, column] = true;
                }
        }
    }

    /// <summary>Every region's solid bar runs the full width and height of that region.</summary>
    /// <remarks>
    /// <para><b>The L owns both corners it touches, and that is what makes it findable.</b> The
    /// clock track alternates, so on any even-sized symbol its light phase falls on the
    /// bottom-right corner — paint the track after the L and the solid bottom bar comes out one
    /// module short of the symbol's width.</para>
    ///
    /// <para>It cost a debugging session: the locator takes a symbol's width from its bottom bar,
    /// so one missing module made every sampled module position drift and the clock track stopped
    /// matching a third of the way across. Asserted here directly, over every size, because a
    /// round trip cannot see it — the reader would happily agree with a writer that was one module
    /// out.</para>
    /// </remarks>
    [Fact]
    public void TheSolidLRunsTheFullWidthAndHeightOfEveryRegion()
    {
        for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
        {
            var matrix = new bool[DataMatrixLayout.Rows(symbol), DataMatrixLayout.Columns(symbol)];
            DataMatrixLayout.PaintFinders(symbol, matrix);

            int regionHeight = DataMatrixLayout.Rows(symbol) / DataMatrixLayout.RegionRows(symbol);
            int regionWidth = DataMatrixLayout.Columns(symbol) / DataMatrixLayout.RegionColumns(symbol);

            for (int regionRow = 0; regionRow < DataMatrixLayout.RegionRows(symbol); regionRow++)
                for (int regionColumn = 0; regionColumn < DataMatrixLayout.RegionColumns(symbol); regionColumn++)
                {
                    int top = regionRow * regionHeight, left = regionColumn * regionWidth;

                    for (int x = 0; x < regionWidth; x++)
                        Assert.True(matrix[top + regionHeight - 1, left + x],
                            $"size {DataMatrixLayout.Rows(symbol)}: the bottom bar is light at "
                            + $"column {x} of its region");

                    for (int y = 0; y < regionHeight; y++)
                        Assert.True(matrix[top + y, left],
                            $"size {DataMatrixLayout.Rows(symbol)}: the left bar is light at "
                            + $"row {y} of its region");
                }
        }
    }

    /// <summary>The clock track alternates along the top edge.</summary>
    /// <remarks>
    /// The other half of the finder, and the property <see cref="DataMatrixLocator"/> uses to tell a
    /// symbol from a solid block of ink. Only the corner shared with the left bar is exempt.
    /// </remarks>
    [Fact]
    public void TheClockTrackAlternatesAlongTheTopEdge()
    {
        for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
        {
            var matrix = new bool[DataMatrixLayout.Rows(symbol), DataMatrixLayout.Columns(symbol)];
            DataMatrixLayout.PaintFinders(symbol, matrix);

            int regionWidth = DataMatrixLayout.Columns(symbol) / DataMatrixLayout.RegionColumns(symbol);
            int transitions = 0;

            for (int x = 1; x < regionWidth; x++)
                if (matrix[0, x] != matrix[0, x - 1]) transitions++;

            Assert.True(transitions >= regionWidth - 3,
                $"size {DataMatrixLayout.Rows(symbol)}: the top edge changed tone only "
                + $"{transitions} times across {regionWidth} modules");
        }
    }

    // ================================================================
    //  Round trips
    // ================================================================

    /// <summary>What the encoder writes, the reader reads.</summary>
    /// <remarks>
    /// <b>The payloads reach different symbol sizes and all three encodation paths</b>: plain ASCII,
    /// digit pairs (two digits to one codeword), and the upper shift that carries Latin-1 above 127.
    /// The accented row is there because an earlier version of it was spelled without accents — so
    /// the upper shift was never exercised, and deleting its whole branch from the reader left the
    /// suite green.
    /// </remarks>
    [Theory]
    [InlineData("A")]
    [InlineData("HELLO")]
    [InlineData("0123456789")]
    [InlineData("SHIP-4711/B")]
    [InlineData("caf\u00e9 na\u00efve \u00dcbung \u00b110\u00b0")]
    [InlineData("https://example.com/order/4711?ref=abcdefghijklmnop")]
    public void AnEncodedSymbolReadsBackAsWhatWentIn(string payload)
    {
        bool[,] grid = DataMatrixEncoder.Encode(payload);

        string decoded = DataMatrixGridReader.Decode(grid, out string refusal);

        Assert.Null(refusal);
        Assert.Equal(payload, decoded);
    }

    /// <summary>A payload big enough to need a four-region symbol still round-trips.</summary>
    /// <remarks>
    /// <b>Regions are where the mapping matrix stops being a simple inset.</b> Up to 26×26 a symbol
    /// is one region and a mapping row shifts by exactly one; from 32×32 it is four regions, and a
    /// mapping row shifts by two more for every region boundary it crosses. A transform that
    /// happened to work for the single-region sizes would fail here.
    /// </remarks>
    [Fact]
    public void AMultiRegionSymbolRoundTrips()
    {
        string payload = new string('X', 70);

        bool[,] grid = DataMatrixEncoder.Encode(payload);
        Assert.True(grid.GetLength(0) >= 32, "the payload did not reach a multi-region size");

        Assert.Equal(payload, DataMatrixGridReader.Decode(grid, out _));
    }

    /// <summary>Each rectangular size, filled to capacity, round-trips.</summary>
    /// <remarks>
    /// A payload of exactly a size's data capacity cannot land on a smaller one, so every row reaches
    /// its size — asserted, not assumed. The two-region rectangles are one region high and two wide,
    /// the case a square symbol never has.
    /// </remarks>
    [Theory]
    [InlineData(8, 18, 5)]
    [InlineData(8, 32, 10)]
    [InlineData(12, 26, 16)]
    [InlineData(12, 36, 22)]
    [InlineData(16, 36, 32)]
    [InlineData(16, 48, 49)]
    public void ARectangularSymbolRoundTrips(int rows, int columns, int capacity)
    {
        var payload = new System.Text.StringBuilder();
        for (int i = 0; i < capacity; i++) payload.Append((char)('A' + (i * 7) % 26));

        bool[,] grid = DataMatrixEncoder.Encode(payload.ToString(), rectangular: true);
        Assert.Equal(rows, grid.GetLength(0));
        Assert.Equal(columns, grid.GetLength(1));

        Assert.Equal(payload.ToString(), DataMatrixGridReader.Decode(grid, out string refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void APayloadBeyondTheLargestRectangleIsRefusedByName()
    {
        var error = Assert.Throws<ArgumentException>(() => DataMatrixEncoder.Encode(new string('X', 50), rectangular: true));
        Assert.Contains("rectangular", error.Message, StringComparison.Ordinal);

        // The same payload fits a square: 26×26 holds 44 data codewords, 32×32 holds 62.
        Assert.Equal(32, DataMatrixEncoder.Encode(new string('X', 50)).GetLength(0));
    }

    /// <summary>Damage within ECC-200's reach is repaired.</summary>
    [Fact]
    public void ADamagedSymbolIsStillRead()
    {
        const string payload = "SHIP-4711/B";
        bool[,] grid = DataMatrixEncoder.Encode(payload);

        // Two modules well inside the data area.
        grid[3, 3] = !grid[3, 3];
        grid[5, 6] = !grid[5, 6];

        Assert.Equal(payload, DataMatrixGridReader.Decode(grid, out string refusal));
        Assert.Null(refusal);
    }

    /// <summary>
    /// A 52×52 symbol's codewords are two interleaved Reed–Solomon blocks (ISO/IEC 16022 Table 7;
    /// an independent reader's Version 15, <c>ECBlocks(42, ECB(2, 102))</c>): block <i>b</i> is data codewords
    /// <i>b</i>, <i>b</i>+2, … and error-correction codewords 204 + <i>b</i>, 204 + <i>b</i> + 2, …,
    /// and each block's 42 error-correction codewords are that block's own code. De-interleaved here
    /// by that rule, not by the library's helper; the control is the single-block code the encoder
    /// used to write, which these codewords are not.
    /// </summary>
    [Fact]
    public void ALargestSquareSymbol_IsTwoInterleavedBlocks_EachItsOwnCode()
    {
        bool[,] grid = DataMatrixEncoder.Encode(new string('X', 200));
        Assert.Equal(52, grid.GetLength(0));
        int symbol = DataMatrixLayout.WithSize(52, 52);
        int[] raw = DataMatrixGridReader.ReadCodewords(symbol, grid);
        var field = new GaloisField256(GaloisField256.DataMatrixPrimitive, 84, GaloisField256.DataMatrixGeneratorBase);

        for (int b = 0; b < 2; b++)
        {
            var data = new int[102];
            var ec = new int[42];
            for (int i = 0; i < 102; i++) data[i] = raw[b + 2 * i];
            for (int i = 0; i < 42; i++) ec[i] = raw[204 + b + 2 * i];
            Assert.Equal(field.ErrorCorrection(data, 42), ec);
        }

        var allData = new int[204];
        Array.Copy(raw, allData, 204);
        Assert.NotEqual(field.ErrorCorrection(allData, 84), raw[204..]);
    }

    /// <summary>
    /// A 52×52 symbol reads back, and each block repairs up to its own 21 errors: 21 damaged codewords
    /// in each block (42 in all) are read; 22 in one block are refused, though the other is clean.
    /// </summary>
    [Fact]
    public void ALargestSquareSymbol_RepairsEachBlockToItsOwnLimit()
    {
        string payload = new string('X', 200);
        bool[,] clean = DataMatrixEncoder.Encode(payload);
        Assert.Equal(payload, DataMatrixGridReader.Decode(clean, out _));

        bool[,] each21 = (bool[,])clean.Clone();
        FlipFirstBitOf(each21, Enumerable.Range(0, 42));          // 0..41: 21 even, 21 odd
        Assert.Equal(payload, DataMatrixGridReader.Decode(each21, out string refusal));
        Assert.Null(refusal);

        bool[,] one22 = (bool[,])clean.Clone();
        FlipFirstBitOf(one22, Enumerable.Range(0, 22).Select(i => 2 * i));   // 22 in block 0
        Assert.Null(DataMatrixGridReader.Decode(one22, out refusal));
        Assert.Contains("error correction", refusal);
    }

    /// <summary>Inverts the most significant bit of each named codeword of a 52×52 symbol.</summary>
    private static void FlipFirstBitOf(bool[,] grid, IEnumerable<int> codewords)
    {
        int symbol = DataMatrixLayout.WithSize(52, 52);
        int rows = DataMatrixLayout.MappingRows(symbol), columns = DataMatrixLayout.MappingColumns(symbol);
        var positions = new (int Codeword, int Bit)?[rows, columns];
        Ecc200Placement.Trace(positions, rows, columns);
        var wanted = new HashSet<int>(codewords);

        for (int y = 0; y < rows; y++)
            for (int x = 0; x < columns; x++)
                if (positions[y, x] is { Bit: 1 } p && wanted.Contains(p.Codeword))
                {
                    DataMatrixLayout.ToSymbol(symbol, y, x, out int row, out int column);
                    grid[row, column] = !grid[row, column];
                }
    }

    /// <summary>The two modules Annex F cannot reach are filled, on the sizes that need it.</summary>
    /// <remarks>
    /// <para><b>They carry no data, which is exactly why nothing else notices.</b> When a mapping
    /// dimension is 2 more than a multiple of 4, the placement walk leaves a 2×2 hole at the
    /// bottom-right that ECC-200 closes with a fixed mark. Our reader never looks at those modules —
    /// they hold no codeword bit — so deleting the fill decoded every symbol perfectly well and left
    /// the whole suite green.</para>
    ///
    /// <para>It still has to be right: the modules are part of the symbol, and a reader that
    /// <i>does</i> check them would reject ours. Asserted structurally, because no round trip can
    /// reach it.</para>
    /// </remarks>
    [Fact]
    public void TheFixedCornerPatternIsFilledOnTheSizesThatNeedIt()
    {
        for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
        {
            int mapRows = DataMatrixLayout.MappingRows(symbol);
            int mapColumns = DataMatrixLayout.MappingColumns(symbol);

            // The layout's answer and the walk's must agree: the corner is a pattern exactly when
            // the walk leaves it unreached.
            var positions = new (int Codeword, int Bit)?[mapRows, mapColumns];
            Ecc200Placement.Trace(positions, mapRows, mapColumns);
            Assert.True(positions[mapRows - 1, mapColumns - 1].HasValue != DataMatrixLayout.HasCornerPattern(symbol),
                $"size {DataMatrixLayout.Rows(symbol)}×{DataMatrixLayout.Columns(symbol)}: the layout and "
                + "the placement walk disagree about the corner pattern");

            if (!DataMatrixLayout.HasCornerPattern(symbol)) continue;

            var mapping = new bool?[mapRows, mapColumns];
            var codewords = new System.Collections.Generic.List<int>();
            int total = DataMatrixLayout.DataCodewords(symbol) + DataMatrixLayout.EcCodewords(symbol);
            for (int i = 0; i < total; i++) codewords.Add(0);

            Ecc200Placement.Place(mapping, mapRows, mapColumns, codewords);

            Assert.True(mapping[mapRows - 1, mapColumns - 1].HasValue
                        && mapping[mapRows - 1, mapColumns - 1].Value,
                $"size {DataMatrixLayout.Rows(symbol)}: the corner module is unset");
            Assert.True(mapping[mapRows - 2, mapColumns - 2].HasValue
                        && mapping[mapRows - 2, mapColumns - 2].Value,
                $"size {DataMatrixLayout.Rows(symbol)}: the corner's second module is unset");
        }
    }

    /// <summary>An L whose two arms are different lengths is not a candidate.</summary>
    /// <remarks>
    /// <para><b>Built as raw pixels, because three attempts to provoke this through a rendered page
    /// all tripped a different filter first.</b> A full-width rule implies a square taller than the
    /// page; a short rule's square lands in whitespace and fails the clock-track test. The
    /// arm-length check is real but narrow, and the honest way to pin a narrow condition is to
    /// build exactly it rather than to keep drawing pages and hoping.</para>
    ///
    /// <para>The buffer below is precisely the case: a 100-pixel horizontal bar with a 10-pixel
    /// tick rising from its left end — an L by any loose reading — and a textured row exactly one
    /// square-height above, so the clock-track test would pass it. Only the arms disagreeing rules
    /// it out. A Data Matrix's two bars stand in the proportions of one of its sizes — equal for a
    /// square, from 1:4 to 4:9 for a rectangle — and 1:10 is none of them.</para>
    /// </remarks>
    [Fact]
    public void AnLWhoseArmsAreDifferentLengthsIsNotACandidate()
    {
        const int Width = 200, Height = 200, Bar = 100, Left = 20, Bottom = 150;
        int stride = Width * 4;
        var pixels = new byte[stride * Height];

        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;   // white paper
            pixels[i + 3] = 255;
        }

        void Ink(int x, int y)
        {
            int at = (y * stride) + (x * 4);
            pixels[at] = pixels[at + 1] = pixels[at + 2] = 0;
        }

        // The horizontal bar, and a tick a tenth of its length rising from the left end.
        for (int x = Left; x < Left + Bar; x++) Ink(x, Bottom);
        for (int y = Bottom - 9; y <= Bottom; y++) Ink(Left, y);

        // A textured row where the implied square's top edge would fall, so the clock-track
        // test cannot be the thing that rejects this.
        for (int x = Left; x < Left + Bar; x += 2) Ink(x, Bottom - Bar + 1);

        Assert.Empty(DataMatrixLocator.Locate(pixels, Width, Height, stride, 127));
    }

    /// <summary>A symbol stretched a little vertically is sampled by its own height.</summary>
    /// <remarks>
    /// <para><b>On a faithful render the module width and height are the same number</b>, so taking
    /// the row pitch from the bottom bar or from the left bar cannot be told apart there — a mutation
    /// swapping one for the other survived every rendered-page test. The proportion check admits an L
    /// a tenth off its size's shape, so a symbol scaled anisotropically by less than that is a
    /// candidate; sampled at the bottom bar's pitch, a 16-row symbol 6% taller than it should be
    /// drifts a whole module by its last row.</para>
    ///
    /// <para>Built as raw pixels, like the arm-length test, because the stretch is the whole
    /// point.</para>
    /// </remarks>
    [Fact]
    public void ASymbolStretchedVerticallyIsSampledByItsOwnHeight()
    {
        string payload = new string('Q', 20) + "STRETCHED-SYMBOL-4711";
        bool[,] symbol = DataMatrixEncoder.Encode(payload, rectangular: true);
        Assert.Equal(16, symbol.GetLength(0));
        Assert.Equal(48, symbol.GetLength(1));

        const int Margin = 20;
        const double ModuleWidth = 10.0, ModuleHeight = 10.6;
        int width = (2 * Margin) + (int)(48 * ModuleWidth);
        int height = (2 * Margin) + (int)Math.Ceiling(16 * ModuleHeight);
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int py = 0; py < height; py++)
            for (int px = 0; px < width; px++)
            {
                int row = (int)Math.Floor((py - Margin) / ModuleHeight);
                int column = (int)Math.Floor((px - Margin) / ModuleWidth);
                bool dark = py >= Margin && px >= Margin && row < 16 && column < 48 && symbol[row, column];

                int at = (py * stride) + (px * 4);
                pixels[at] = pixels[at + 1] = pixels[at + 2] = dark ? (byte)0 : (byte)255;
                pixels[at + 3] = 255;
            }

        var decoded = new System.Collections.Generic.List<string>();
        foreach (var candidate in DataMatrixLocator.Locate(pixels, width, height, stride, 127))
        {
            string text = DataMatrixGridReader.Decode(candidate.Grid, out _);
            if (text != null) decoded.Add(text);
        }

        Assert.Equal(payload, Assert.Single(decoded));
    }

    // ================================================================
    //  Refusals, with an acceptance control
    // ================================================================

    /// <summary>A grid whose finder patterns are wrong is passed over silently.</summary>
    /// <remarks>
    /// <b>The acceptance control for the refusal channel.</b> Data Matrix has no format field, so
    /// the L and the clock track are the only structure a candidate can be checked against before
    /// decoding — and every square of ink on a page gets offered. "Not a Data Matrix" must therefore
    /// be silent, or the channel is noise.
    /// </remarks>
    [Fact]
    public void AGridThatIsNotADataMatrixIsPassedOverWithoutARefusal()
    {
        var nonsense = new bool[16, 16];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
                nonsense[y, x] = ((x * 3) + (y * 5)) % 4 == 0;

        Assert.Null(DataMatrixGridReader.Decode(nonsense, out string refusal));
        Assert.Null(refusal);

        // A size no ECC-200 symbol has, likewise.
        Assert.Null(DataMatrixGridReader.Decode(new bool[15, 15], out string sizeRefusal));
        Assert.Null(sizeRefusal);
    }

    /// <summary>Damage beyond ECC-200's reach is refused by name.</summary>
    [Fact]
    public void ASymbolDamagedBeyondRepairIsRefusedByName()
    {
        bool[,] grid = DataMatrixEncoder.Encode("SHIP-4711/B");
        int size = grid.GetLength(0);

        // Destroy the interior wholesale, leaving the finders — so the refusal can only come from
        // the error correction rather than from the structural check.
        for (int y = 1; y < size - 1; y++)
            for (int x = 1; x < size - 1; x++)
                grid[y, x] = ((x * 7) + y) % 3 == 0;

        Assert.Null(DataMatrixGridReader.Decode(grid, out string refusal));
        Assert.Contains("error correction", refusal, StringComparison.Ordinal);
    }

    // ---- C40, Text and X12 (ISO/IEC 16022 §5.2.5–5.2.7) ----

    private const int LatchC40 = 230, LatchText = 239, LatchX12 = 238, Unlatch = 254;

    /// <summary>Two codewords packing three values: <c>c1 × 1600 + c2 × 40 + c3 + 1</c>, high byte first.</summary>
    private static int[] Pair(int c1, int c2, int c3)
    {
        int packed = c1 * 1600 + c2 * 40 + c3 + 1;
        return new[] { packed >> 8, packed & 0xFF };
    }

    private static int[] Words(params object[] parts)
    {
        var all = new System.Collections.Generic.List<int>();
        foreach (object part in parts)
            if (part is int[] many) all.AddRange(many); else all.Add((int)part);
        return all.ToArray();
    }

    private static string Read(int[] dataCodewords, out string refusal)
        => DataMatrixGridReader.Decode(
            DataMatrixEncoder.EncodeCodewords(new System.Collections.Generic.List<int>(dataCodewords)), out refusal);

    public static TheoryData<string, int[]> EncodationCases => new()
    {
        // C40 basic set: A=14, I=22, M=26; space 3, digits from 4.
        { "AIM", Words(LatchC40, Pair(14, 22, 26), Unlatch) },
        { "A1 ", Words(LatchC40, Pair(14, 5, 3), Unlatch) },
        // Shift 2 (value 1) then '!' (0).
        { "Z!", Words(LatchC40, Pair(39, 1, 0), Unlatch) },
        // Shift 1 (0) then 9 is HT; Shift 3 (2) then 1 is 'a' (96 + 1); a space; and a trailing Shift 1
        // (0), which is the encoder's padding and adds nothing.
        { "\ta ", Words(LatchC40, Pair(0, 9, 2), Pair(1, 3, 0), Unlatch) },
        // Shift 2 then 30 is upper shift: the next character, 'A', gains 128.
        { "Á", Words(LatchC40, Pair(1, 30, 14), Unlatch) },
        // Text: the basic set is lowercase and Shift 3 gives the capitals.
        { "ab ", Words(LatchText, Pair(14, 15, 3), Unlatch) },
        { "Q{ ", Words(LatchText, Pair(2, 17, 2), Pair(27, 3, 0), Unlatch) },
        // X12: 0 CR, 1 '*', 2 '>', 3 space, 4-13 digits, 14-39 capitals.
        { "A0*", Words(LatchX12, Pair(14, 4, 1), Unlatch) },
        { "\r> ", Words(LatchX12, Pair(0, 2, 3), Unlatch) },
        // Back in ASCII after the unlatch: 'B' is 66 + 1.
        { "AIMB", Words(LatchC40, Pair(14, 22, 26), Unlatch, 67) },
    };

    [Theory]
    [MemberData(nameof(EncodationCases))]
    public void C40TextAndX12Segments_AreDecoded(string expected, int[] dataCodewords)
    {
        Assert.Equal(expected, Read(dataCodewords, out string refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void ASingleCodewordLeftAtTheEndOfTheData_IsAscii()
    {
        // Eight codewords fill a 14×14 symbol exactly: the latch, three pairs, and one last codeword,
        // which cannot be a pair and is read in ASCII ('X' is 88 + 1).
        int[] words = Words(LatchC40, Pair(14, 15, 16), Pair(17, 18, 19), Pair(20, 21, 22), 89);
        Assert.Equal(8, words.Length);
        Assert.Equal("ABCDEFGHIX", Read(words, out string refusal));
        Assert.Null(refusal);
    }

    // ---- EDIFACT (ISO/IEC 16022 §5.2.8) and Base 256 (§5.2.9) ----

    private const int LatchEdifact = 240, LatchBase256 = 231, EdifactUnlatch = 31;

    /// <summary>Six-bit values packed most significant first, the last codeword zero-filled.</summary>
    private static int[] Edifact(params int[] values)
    {
        var bytes = new System.Collections.Generic.List<int>();
        int bits = 0, count = 0;
        foreach (int v in values)
        {
            bits = (bits << 6) | v;
            count += 6;
            while (count >= 8) { count -= 8; bytes.Add((bits >> count) & 0xFF); }
        }
        if (count > 0) bytes.Add((bits << (8 - count)) & 0xFF);
        return bytes.ToArray();
    }

    /// <summary>ISO/IEC 16022's 255-state randomising of a Base 256 codeword at a 1-based position.</summary>
    private static int R(int value, int position) => (value + (149 * position) % 255 + 1) % 256;

    public static TheoryData<string, int[]> EdifactAndBase256Cases => new()
    {
        // A, B, C are 1, 2, 3 (64 is added below 32), then the unlatch fills the third codeword and
        // 'D' is ASCII 68 + 1.
        { "ABCD", Words(LatchEdifact, Edifact(1, 2, 3, EdifactUnlatch), 69) },
        // An unlatch as the second value ends the segment at the next codeword boundary: two codewords.
        // Its second codeword is always 0xF0 — the EDIFACT latch itself — so enough data follows that
        // reading it again as a latch would swallow 'D', 'E' and 'F'.
        { "XDEF", Words(LatchEdifact, Edifact(24, EdifactUnlatch), 69, 70, 71) },
        // As the third value it ends three codewords in, discarding the rest of the third.
        { "ABF", Words(LatchEdifact, Edifact(1, 2, EdifactUnlatch), 71) },
        // From 32 up a value stands for itself: '0', space, and '^' (30 + 64).
        { "0 ^", Words(LatchEdifact, Edifact(48, 32, 30, EdifactUnlatch)) },
        // Base 256 with a length of 2, then ASCII 'B'.
        { "HéB", Words(LatchBase256, R(2, 2), R(72, 3), R(233, 4), 67) },
        // A length of 0 runs to the end of the symbol: five codewords fill a 12×12 exactly.
        { "ABC", Words(LatchBase256, R(0, 2), R(65, 3), R(66, 4), R(67, 5)) },
    };

    [Theory]
    [MemberData(nameof(EdifactAndBase256Cases))]
    public void EdifactAndBase256Segments_AreDecoded(string expected, int[] dataCodewords)
    {
        Assert.Equal(expected, Read(dataCodewords, out string refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void AnEdifactSegmentReachingTheEndOfTheSymbol_LeavesItsLastCodewordsToAscii()
    {
        // Eight codewords fill a 14×14 exactly: the latch, two full groups, and one codeword that
        // cannot hold a group, read in ASCII ('D' is 68 + 1).
        int[] one = Words(LatchEdifact, Edifact(1, 2, 3, 4), Edifact(5, 6, 7, 8), 69);
        Assert.Equal(8, one.Length);
        Assert.Equal("ABCDEFGHD", Read(one, out string refusal));
        Assert.Null(refusal);

        // Twelve fill a 16×16: three groups, then two codewords — still too few for a group.
        int[] two = Words(LatchEdifact, Edifact(1, 2, 3, 4), Edifact(5, 6, 7, 8), Edifact(9, 10, 11, 12), 69, 70);
        Assert.Equal(12, two.Length);
        Assert.Equal("ABCDEFGHIJKLDE", Read(two, out refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void ABase256LengthFrom250_TakesASecondCodeword()
    {
        // 250 × (251 − 249) + 3 = 503 bytes, then ASCII 'Z'. No listed symbol holds that many
        // codewords, so this reads the codeword stream directly.
        var parts = new System.Collections.Generic.List<int> { LatchBase256, R(251, 2), R(3, 3) };
        var expected = new System.Text.StringBuilder();
        for (int n = 0; n < 503; n++)
        {
            int b = (n * 7 + 1) % 256;
            parts.Add(R(b, parts.Count + 1));
            expected.Append((char)b);
        }
        parts.Add(91);
        expected.Append('Z');

        int[] words = parts.ToArray();
        Assert.Equal(expected.ToString(), DataMatrixGridReader.ReadAscii(words, words.Length, out string refusal));
        Assert.Null(refusal);
    }

    [Fact]
    public void ABase256LengthPastTheData_IsRefusedByName()
    {
        // Three codewords fill a 10×10 exactly; the length claims two bytes where one remains.
        Assert.Null(Read(Words(LatchBase256, R(2, 2), R(65, 3)), out string refusal));
        Assert.Contains("more bytes than the symbol holds", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ABase256LengthWhoseSecondCodewordIsMissing_IsRefusedByName()
    {
        Assert.Null(Read(Words(66, LatchBase256, R(250, 3)), out string refusal));
        Assert.Contains("length is truncated", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void ABase256LatchAsTheLastCodeword_IsRefusedByName()
    {
        Assert.Null(Read(Words(66, 67, LatchBase256), out string refusal));
        Assert.Contains("has no length", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void PadCodewordsAreRandomisedWithoutWrappingAt254()
    {
        // 26 ASCII codewords take a 22×22 (30 data codewords), so positions 27–30 are padding: 129,
        // then 129 + ((149 × P) mod 253 + 1), less 254 only when that exceeds 254. At 28 it is
        // exactly 254 (149 × 28 mod 253 = 124); at 29, 150; at 30, 299 − 254 = 45.
        bool[,] grid = DataMatrixEncoder.Encode("ABCDEFGHIJKLMNOPQRSTUVWXYZ");
        int symbol = DataMatrixLayout.WithSize(grid.GetLength(0), grid.GetLength(1));
        Assert.Equal(30, DataMatrixLayout.DataCodewords(symbol));

        int[] codewords = DataMatrixGridReader.ReadCodewords(symbol, grid);
        Assert.Equal(new[] { 129, 254, 150, 45 }, codewords[26..30]);
    }

    /// <summary>A malformed grid is refused rather than indexed.</summary>
    [Fact]
    public void AMalformedGridIsRefusedRatherThanIndexed()
    {
        Assert.Null(DataMatrixGridReader.Decode(null, out _));
        Assert.Null(DataMatrixGridReader.Decode(new bool[10, 12], out _));
    }
}
