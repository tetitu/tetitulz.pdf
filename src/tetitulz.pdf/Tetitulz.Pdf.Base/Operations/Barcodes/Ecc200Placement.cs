// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// ECC-200 codeword placement, ISO/IEC 16022 Annex F — one walk, driven both directions.
    /// </summary>
    /// <remarks>
    /// <para><b>The walk is parameterised rather than duplicated, and the reason is the walk
    /// itself.</b> Annex F steps diagonally through the mapping matrix placing eight-module "utah"
    /// shapes, wrapping negative coordinates round with an offset that depends on the matrix size
    /// modulo 8, and has four special cases for the corners. A second implementation of that which
    /// <i>reads</i> rather than writes is the classic near-duplicate: it would agree with the first
    /// on almost every size, disagree on one, and nothing would say which.</para>
    ///
    /// <para>So <see cref="Trace"/> runs exactly the same walk as <see cref="Place"/> and simply
    /// records where each codeword's bits landed. The reader then looks positions up instead of
    /// re-deriving them.</para>
    ///
    /// <para><b>This operates on the mapping matrix, never on the symbol.</b> The symbol carries
    /// each region's finder rows and columns; the mapping matrix is what is left once they are
    /// removed. Calling it with the symbol's own dimensions places data where the finder patterns
    /// will be painted, which is a defect this code shipped with — see
    /// <see cref="DataMatrixLayout"/>.</para>
    /// </remarks>
    internal static class Ecc200Placement
    {
        /// <summary>Modules a single codeword occupies.</summary>
        private const int CodewordModules = 8;

        /// <summary>Writes codeword bits into the mapping matrix.</summary>
        internal static void Place(bool?[,] array, int nrow, int ncol, List<int> codewords)
        {
            bool[,] occupied = Walk(nrow, ncol, (row, col, index, bit) =>
            {
                int value = index < codewords.Count ? codewords[index] : 0;
                array[row, col] = ((value >> (CodewordModules - bit)) & 1) == 1;
            });

            // The two modules Annex F cannot reach when a dimension is 2 more than a multiple of 4.
            if (!occupied[nrow - 1, ncol - 1])
            {
                array[nrow - 1, ncol - 1] = true;
                array[nrow - 2, ncol - 2] = true;
            }
        }

        /// <summary>
        /// Records which codeword and which bit each mapping-matrix cell carries.
        /// </summary>
        /// <param name="positions">
        /// Receives, for every cell the placement writes, the codeword index and the bit number
        /// (1–8, most significant first) that landed there.
        /// </param>
        /// <param name="nrow">Mapping-matrix rows.</param>
        /// <param name="ncol">Mapping-matrix columns.</param>
        internal static void Trace((int Codeword, int Bit)?[,] positions, int nrow, int ncol)
            => Walk(nrow, ncol, (row, col, index, bit) => positions[row, col] = (index, bit));

        /// <summary>
        /// Annex F's diagonal walk. Returns which cells it wrote.
        /// </summary>
        private static bool[,] Walk(int nrow, int ncol, Action<int, int, int, int> onModule)
        {
            var occupied = new bool[nrow, ncol];
            int index = 0;
            int row = 4, col = 0;

            do
            {
                if (row == nrow && col == 0)
                    CornerA(occupied, nrow, ncol, index++, onModule);
                else if (row == nrow - 2 && col == 0 && (ncol % 4) != 0)
                    CornerB(occupied, nrow, ncol, index++, onModule);
                else if (row == nrow - 2 && col == 0 && (ncol % 8) == 4)
                    CornerC(occupied, nrow, ncol, index++, onModule);
                else if (row == nrow + 4 && col == 2 && (ncol % 8) == 0)
                    CornerD(occupied, nrow, ncol, index++, onModule);

                do
                {
                    if (row < nrow && col >= 0 && !occupied[row, col])
                        Utah(occupied, nrow, ncol, row, col, index++, onModule);
                    row -= 2; col += 2;
                } while (row >= 0 && col < ncol);
                row += 1; col += 3;

                do
                {
                    if (row >= 0 && col < ncol && !occupied[row, col])
                        Utah(occupied, nrow, ncol, row, col, index++, onModule);
                    row += 2; col -= 2;
                } while (row < nrow && col >= 0);
                row += 3; col += 1;
            } while (row < nrow || col < ncol);

            return occupied;
        }

        /// <summary>
        /// Places one module, wrapping a coordinate that has run off the top or the left.
        /// </summary>
        /// <remarks>
        /// The wrap offsets depend on the matrix size modulo 8, which is why the mapping matrix's
        /// dimensions — not the symbol's — have to be the ones passed in.
        /// </remarks>
        private static void Module(
            bool[,] occupied, int nrow, int ncol, int row, int col,
            int index, int bit, Action<int, int, int, int> onModule)
        {
            if (row < 0) { row += nrow; col += 4 - ((nrow + 4) % 8); }
            if (col < 0) { col += ncol; row += 4 - ((ncol + 4) % 8); }
            // ISO/IEC 21471 Annex E (DMRE): the column wrap can carry the row past the bottom, which
            // then wraps to the top — on 26×40 and 26×48 (mappings 24×36, 24×44), never on an ISO/IEC
            // 16022 size. Independent writers and readers both do this.
            if (row >= nrow) row -= nrow;

            occupied[row, col] = true;
            onModule(row, col, index, bit);
        }

        private static void Utah(
            bool[,] o, int nrow, int ncol, int row, int col, int index,
            Action<int, int, int, int> f)
        {
            Module(o, nrow, ncol, row - 2, col - 2, index, 1, f);
            Module(o, nrow, ncol, row - 2, col - 1, index, 2, f);
            Module(o, nrow, ncol, row - 1, col - 2, index, 3, f);
            Module(o, nrow, ncol, row - 1, col - 1, index, 4, f);
            Module(o, nrow, ncol, row - 1, col,     index, 5, f);
            Module(o, nrow, ncol, row,     col - 2, index, 6, f);
            Module(o, nrow, ncol, row,     col - 1, index, 7, f);
            Module(o, nrow, ncol, row,     col,     index, 8, f);
        }

        private static void CornerA(
            bool[,] o, int nrow, int ncol, int index, Action<int, int, int, int> f)
        {
            Module(o, nrow, ncol, nrow - 1, 0,        index, 1, f);
            Module(o, nrow, ncol, nrow - 1, 1,        index, 2, f);
            Module(o, nrow, ncol, nrow - 1, 2,        index, 3, f);
            Module(o, nrow, ncol, 0,        ncol - 2, index, 4, f);
            Module(o, nrow, ncol, 0,        ncol - 1, index, 5, f);
            Module(o, nrow, ncol, 1,        ncol - 1, index, 6, f);
            Module(o, nrow, ncol, 2,        ncol - 1, index, 7, f);
            Module(o, nrow, ncol, 3,        ncol - 1, index, 8, f);
        }

        private static void CornerB(
            bool[,] o, int nrow, int ncol, int index, Action<int, int, int, int> f)
        {
            Module(o, nrow, ncol, nrow - 3, 0,        index, 1, f);
            Module(o, nrow, ncol, nrow - 2, 0,        index, 2, f);
            Module(o, nrow, ncol, nrow - 1, 0,        index, 3, f);
            Module(o, nrow, ncol, 0,        ncol - 4, index, 4, f);
            Module(o, nrow, ncol, 0,        ncol - 3, index, 5, f);
            Module(o, nrow, ncol, 0,        ncol - 2, index, 6, f);
            Module(o, nrow, ncol, 0,        ncol - 1, index, 7, f);
            Module(o, nrow, ncol, 1,        ncol - 1, index, 8, f);
        }

        private static void CornerC(
            bool[,] o, int nrow, int ncol, int index, Action<int, int, int, int> f)
        {
            Module(o, nrow, ncol, nrow - 3, 0,        index, 1, f);
            Module(o, nrow, ncol, nrow - 2, 0,        index, 2, f);
            Module(o, nrow, ncol, nrow - 1, 0,        index, 3, f);
            Module(o, nrow, ncol, 0,        ncol - 2, index, 4, f);
            Module(o, nrow, ncol, 0,        ncol - 1, index, 5, f);
            Module(o, nrow, ncol, 1,        ncol - 1, index, 6, f);
            Module(o, nrow, ncol, 2,        ncol - 1, index, 7, f);
            Module(o, nrow, ncol, 3,        ncol - 1, index, 8, f);
        }

        private static void CornerD(
            bool[,] o, int nrow, int ncol, int index, Action<int, int, int, int> f)
        {
            Module(o, nrow, ncol, nrow - 1, 0,        index, 1, f);
            Module(o, nrow, ncol, nrow - 1, ncol - 1, index, 2, f);
            Module(o, nrow, ncol, 0,        ncol - 3, index, 3, f);
            Module(o, nrow, ncol, 0,        ncol - 2, index, 4, f);
            Module(o, nrow, ncol, 0,        ncol - 1, index, 5, f);
            Module(o, nrow, ncol, 1,        ncol - 3, index, 6, f);
            Module(o, nrow, ncol, 1,        ncol - 2, index, 7, f);
            Module(o, nrow, ncol, 1,        ncol - 1, index, 8, f);
        }
    }
}
