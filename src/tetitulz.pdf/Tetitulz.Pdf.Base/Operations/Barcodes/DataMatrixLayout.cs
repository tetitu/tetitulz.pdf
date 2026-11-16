// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// ECC-200 symbol geometry — the one answer, shared by the encoder and the reader.
    /// </summary>
    /// <remarks>
    /// <para><b>A Data Matrix has two different grids, and confusing them is the defect this class
    /// was extracted to fix.</b> The <i>symbol</i> is what gets printed: a solid L down the left and
    /// along the bottom, an alternating clock track along the top and right, and data in between.
    /// The <i>mapping matrix</i> is only the data — the symbol with each region's two finder rows
    /// and two finder columns removed. ISO/IEC 16022 Annex F's placement algorithm operates on the
    /// mapping matrix. Run it over the symbol instead and the finder patterns are painted straight
    /// over placed data.</para>
    ///
    /// <para><b>The arithmetic says which grid is which, and it is checkable without the
    /// standard.</b> For every one of the twenty-one sizes below, the mapping matrix holds exactly as
    /// many modules as the codewords need — allowing for the four modules ECC-200 spends on a fixed
    /// corner pattern whenever both mapping dimensions are 2 more than a multiple of 4.
    /// <c>DataMatrixLayoutTests</c> asserts that over the whole table, which is what a wrong region
    /// geometry cannot satisfy.</para>
    ///
    /// <para><b>A leaf: pure data and arithmetic, no static state, so neither direction takes a type
    /// initializer on the other.</b></para>
    /// </remarks>
    internal static class DataMatrixLayout
    {
        // rows, cols, data codewords, ec codewords, region rows, region columns, interleaved
        // Reed-Solomon blocks, and 1 for an ISO 21471 extension (DMRE) size. The fifteen square sizes,
        // then the six rectangular ones, then the nine larger squares and the eighteen DMRE sizes
        // (appended, so the first twenty-one keep their indices). Every row is an independent implementation's version table
        // (symbol size, data-region size, error-correction codewords per block, blocks and data
        // codewords) and was checked against a second one — all 48 sizes agree.
        private static readonly int[,] Table =
        {
            {  10,  10,    3,    5, 1, 1,  1, 0 },
            {  12,  12,    5,    7, 1, 1,  1, 0 },
            {  14,  14,    8,   10, 1, 1,  1, 0 },
            {  16,  16,   12,   12, 1, 1,  1, 0 },
            {  18,  18,   18,   14, 1, 1,  1, 0 },
            {  20,  20,   22,   18, 1, 1,  1, 0 },
            {  22,  22,   30,   20, 1, 1,  1, 0 },
            {  24,  24,   36,   24, 1, 1,  1, 0 },
            {  26,  26,   44,   28, 1, 1,  1, 0 },
            {  32,  32,   62,   36, 2, 2,  1, 0 },
            {  36,  36,   86,   42, 2, 2,  1, 0 },
            {  40,  40,  114,   48, 2, 2,  1, 0 },
            {  44,  44,  144,   56, 2, 2,  1, 0 },
            {  48,  48,  174,   68, 2, 2,  1, 0 },
            {  52,  52,  204,   84, 2, 2,  2, 0 },
            {   8,  18,    5,    7, 1, 1,  1, 0 },
            {   8,  32,   10,   11, 1, 2,  1, 0 },
            {  12,  26,   16,   14, 1, 1,  1, 0 },
            {  12,  36,   22,   18, 1, 2,  1, 0 },
            {  16,  36,   32,   24, 1, 2,  1, 0 },
            {  16,  48,   49,   28, 1, 2,  1, 0 },
            // ISO/IEC 16022's larger squares, 64×64 to 144×144: four and six regions a side.
            {  64,  64,  280,  112, 4, 4,  2, 0 },
            {  72,  72,  368,  144, 4, 4,  4, 0 },
            {  80,  80,  456,  192, 4, 4,  4, 0 },
            {  88,  88,  576,  224, 4, 4,  4, 0 },
            {  96,  96,  696,  272, 4, 4,  4, 0 },
            { 104, 104,  816,  336, 4, 4,  6, 0 },
            { 120, 120, 1050,  408, 6, 6,  6, 0 },
            { 132, 132, 1304,  496, 6, 6,  8, 0 },
            { 144, 144, 1558,  620, 6, 6, 10, 0 },
            // ISO 21471's rectangular extension (DMRE), read but never written: not every reader knows it.
            {   8,  48,   18,   15, 1, 2,  1, 1 },
            {   8,  64,   24,   18, 1, 4,  1, 1 },
            {   8,  80,   32,   22, 1, 4,  1, 1 },
            {   8,  96,   38,   28, 1, 4,  1, 1 },
            {   8, 120,   49,   32, 1, 6,  1, 1 },
            {   8, 144,   63,   36, 1, 6,  1, 1 },
            {  12,  64,   43,   27, 1, 4,  1, 1 },
            {  12,  88,   64,   36, 1, 4,  1, 1 },
            {  16,  64,   62,   36, 1, 4,  1, 1 },
            {  20,  36,   44,   28, 1, 2,  1, 1 },
            {  20,  44,   56,   34, 1, 2,  1, 1 },
            {  20,  64,   84,   42, 1, 4,  1, 1 },
            {  22,  48,   72,   38, 1, 2,  1, 1 },
            {  24,  48,   80,   41, 1, 2,  1, 1 },
            {  24,  64,  108,   46, 1, 4,  1, 1 },
            {  26,  40,   70,   38, 1, 2,  1, 1 },
            {  26,  48,   90,   42, 1, 2,  1, 1 },
            {  26,  64,  118,   50, 1, 4,  1, 1 },
        };

        /// <summary>Modules ECC-200 spends on the fixed corner pattern, when it needs one.</summary>
        internal const int CornerModules = 4;

        /// <summary>ISO/IEC 16022 Table 3: the next codeword is the upper half of Latin-1.</summary>
        internal const int UpperShiftCodeword = 235;

        /// <summary>The pad codeword that ends a short payload.</summary>
        internal const int PadCodeword = 129;

        /// <summary>Lowest codeword value encoding a pair of digits.</summary>
        internal const int DigitPairBase = 130;

        /// <summary>How many symbol sizes this handles.</summary>
        internal static int Count => Table.GetLength(0);

        internal static int Rows(int symbol) => Table[symbol, 0];

        internal static int Columns(int symbol) => Table[symbol, 1];

        internal static int DataCodewords(int symbol) => Table[symbol, 2];

        internal static int EcCodewords(int symbol) => Table[symbol, 3];

        internal static int RegionRows(int symbol) => Table[symbol, 4];

        internal static int RegionColumns(int symbol) => Table[symbol, 5];

        /// <summary>How many Reed–Solomon blocks the symbol's codewords are interleaved across.</summary>
        internal static int InterleavedBlocks(int symbol) => Table[symbol, 6];

        /// <summary>Whether the size is ISO 21471's rectangular extension (DMRE): read, never written.</summary>
        internal static bool IsExtension(int symbol) => Table[symbol, 7] == 1;

        /// <summary>
        /// Whether the size is 144×144, whose blocks are uneven (eight of 156 data codewords, two of
        /// 155) and whose error correction two orders exist for: ISO/IEC 16022's, block b's at
        /// b, b + 10, …; and the one common readers and writers use by default, every block's shifted
        /// by two (the "skew" special version 24).
        /// </summary>
        internal static bool IsLargest(int symbol) => Rows(symbol) == 144;

        /// <summary>
        /// One interleaved block's codewords out of the symbol's stream: its data codewords, which are
        /// every <see cref="InterleavedBlocks"/>-th from <paramref name="block"/>, then its
        /// error-correction codewords, likewise interleaved after all the data — the order ECC-200
        /// writes them in. The blocks of every size here are equal, so each holds an equal share.
        /// </summary>
        /// <remarks>
        /// <b>A block is corrected, and its error correction computed, on its own.</b> Error
        /// correction computed over the whole stream as one block is a different code, which no
        /// conforming reader corrects with — and over GF(256) a code word is at most 255 symbols, so
        /// a 52×52's 288 as one word was refused by this library's own reader too. Its 52×52
        /// symbols were written that way, and read nowhere, until the block count was in the
        /// table.
        /// </remarks>
        internal static int[] Block(int symbol, int[] stream, int block, bool skewed = false)
        {
            int blocks = InterleavedBlocks(symbol);
            int data = DataCodewords(symbol), ec = EcCodewords(symbol);
            int dataInBlock = (data - block + blocks - 1) / blocks;   // 144×144's last two are a codeword short
            var words = new int[dataInBlock + ec / blocks];
            int w = 0;
            for (int i = block; i < data; i += blocks) words[w++] = stream[i];
            for (int i = EcStart(block, blocks, skewed); i < ec; i += blocks) words[w++] = stream[data + i];
            return words;
        }

        /// <summary>Writes one block's codewords back into the stream where <see cref="Block"/> took them from.</summary>
        internal static void PutBlock(int symbol, int[] stream, int block, int[] words, bool skewed = false)
        {
            int blocks = InterleavedBlocks(symbol);
            int data = DataCodewords(symbol), ec = EcCodewords(symbol);
            int w = 0;
            for (int i = block; i < data; i += blocks) stream[i] = words[w++];
            for (int i = EcStart(block, blocks, skewed); i < ec; i += blocks) stream[data + i] = words[w++];
        }

        /// <summary>Where block <paramref name="block"/>'s error correction starts: at its own index, or two on in the skewed order (<see cref="IsLargest"/>).</summary>
        private static int EcStart(int block, int blocks, bool skewed) => skewed ? (block + 2) % blocks : block;

        /// <summary>Rows in the mapping matrix — the symbol without its finder patterns.</summary>
        internal static int MappingRows(int symbol)
            => ((Rows(symbol) / RegionRows(symbol)) - 2) * RegionRows(symbol);

        /// <summary>Columns in the mapping matrix.</summary>
        internal static int MappingColumns(int symbol)
            => ((Columns(symbol) / RegionColumns(symbol)) - 2) * RegionColumns(symbol);

        /// <summary>Whether this size spends four modules on ECC-200's fixed corner pattern.</summary>
        /// <remarks>
        /// It does when both mapping dimensions are 2 more than a multiple of 4 — the case Annex F's
        /// placement cannot fill, and which it closes with a fixed 2×2 mark at the bottom-right
        /// instead. Without accounting for it, four of the fifteen square sizes look four bits too
        /// large. Every rectangular size has a column count divisible by 4, and the walk fills its
        /// corner: reading the rows alone would give three of them a pattern they do not have.
        /// </remarks>
        internal static bool HasCornerPattern(int symbol)
            => MappingRows(symbol) % 4 == 2 && MappingColumns(symbol) % 4 == 2;

        /// <summary>Whether this is one of the square sizes.</summary>
        internal static bool IsSquare(int symbol) => Rows(symbol) == Columns(symbol);

        /// <summary>
        /// The smallest symbol of the chosen shape holding <paramref name="codewords"/> data codewords,
        /// or -1 — never a DMRE size, which not every reader knows, and never 144×144, whose
        /// error-correction order readers disagree on (<see cref="IsLargest"/>).
        /// </summary>
        internal static int SmallestFor(int codewords, bool rectangular = false)
        {
            // The table ascends within each shape (the larger squares are appended after 52×52), so
            // the first fit is the smallest.
            for (int symbol = 0; symbol < Count; symbol++)
                if (IsSquare(symbol) != rectangular && !IsExtension(symbol) && !IsLargest(symbol)
                    && DataCodewords(symbol) >= codewords) return symbol;
            return -1;
        }

        /// <summary>The symbol with these dimensions, or -1 when no size matches.</summary>
        internal static int WithSize(int rows, int columns)
        {
            for (int symbol = 0; symbol < Count; symbol++)
                if (Rows(symbol) == rows && Columns(symbol) == columns) return symbol;
            return -1;
        }

        /// <summary>
        /// Where a mapping-matrix cell lands in the printed symbol.
        /// </summary>
        /// <remarks>
        /// <b>Each region carries its own finder.</b> A 32×32 symbol is four 16×16 regions, each
        /// contributing a 14×14 slice of the one 28×28 mapping matrix — so a mapping row does not
        /// simply shift by one, it shifts by two for every region boundary it has crossed.
        /// </remarks>
        internal static void ToSymbol(
            int symbol, int mappingRow, int mappingColumn, out int row, out int column)
        {
            int dataRows = (Rows(symbol) / RegionRows(symbol)) - 2;
            int dataColumns = (Columns(symbol) / RegionColumns(symbol)) - 2;

            int regionRow = mappingRow / dataRows;
            int regionColumn = mappingColumn / dataColumns;

            row = (regionRow * (dataRows + 2)) + 1 + (mappingRow % dataRows);
            column = (regionColumn * (dataColumns + 2)) + 1 + (mappingColumn % dataColumns);
        }

        /// <summary>
        /// Paints every region's finder pattern: a solid L on the left and bottom, an alternating
        /// clock track on the top and right.
        /// </summary>
        /// <remarks>
        /// <para><b>Order matters, and getting it backwards costs a module.</b> The clock track is
        /// laid first and the solid L over it, so that where the two meet the L wins. Painted the
        /// other way round, the right-hand clock track overwrites the bottom-right corner — and for
        /// every even-sized symbol that corner falls on the track's light phase, so the solid bottom
        /// bar came out one module short of the symbol's width.</para>
        ///
        /// <para>That is not a cosmetic difference. The bar <i>is</i> the handle a reader finds the
        /// symbol by: <see cref="DataMatrixLocator"/> takes the symbol's width from its length, so a
        /// bar one module short made every module position drift, and the clock track stopped
        /// matching about a third of the way across.</para>
        /// </remarks>
        internal static void PaintFinders(int symbol, bool[,] matrix)
        {
            int regionHeight = Rows(symbol) / RegionRows(symbol);
            int regionWidth = Columns(symbol) / RegionColumns(symbol);

            for (int regionRow = 0; regionRow < RegionRows(symbol); regionRow++)
                for (int regionColumn = 0; regionColumn < RegionColumns(symbol); regionColumn++)
                {
                    int top = regionRow * regionHeight;
                    int left = regionColumn * regionWidth;

                    // Clock track: the top edge dark from the corner by the L; the right edge dark
                    // from the solid bottom corner up, so light at the top-right corner (a region is
                    // an even number of modules high). A writer and reader sharing the opposite phase
                    // would pass every round trip while an independent reader finds no symbol.
                    for (int x = 0; x < regionWidth; x++) matrix[top, left + x] = x % 2 == 0;
                    for (int y = 0; y < regionHeight; y++)
                        matrix[top + y, left + regionWidth - 1] = y % 2 == 1;

                    // The solid L, last, so it owns both corners it touches.
                    for (int y = 0; y < regionHeight; y++) matrix[top + y, left] = true;
                    for (int x = 0; x < regionWidth; x++)
                        matrix[top + regionHeight - 1, left + x] = true;
                }
        }
    }
}
