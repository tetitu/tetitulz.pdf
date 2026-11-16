// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Finds Data Matrix symbols on a rendered page and samples each into a module grid.
    /// </summary>
    /// <remarks>
    /// <para><b>The L is the handle.</b> A Data Matrix has no finder squares to look for — its
    /// structure is a solid dark column down the left and a solid dark row along the bottom, meeting
    /// at the bottom-left corner. Two solid runs meeting at a right angle, in the proportions of some
    /// ECC-200 size, is therefore the whole of the search, and it is cheap: a solid run is exactly
    /// what row-run extraction already produces.</para>
    ///
    /// <para><b>The size is not guessed, it is tried.</b> ECC-200 has fifteen square and six
    /// rectangular sizes; a candidate is sampled at each one whose proportions its arms fit and whose
    /// modules are big enough to sample, and the decoder's finder check throws out the wrong ones
    /// immediately — it compares every finder and clock-track module, so a mis-sized sampling fails
    /// on the alternating track within a few modules. Deciding the size by counting clock transitions
    /// would be one more thing to get wrong for no gain.</para>
    ///
    /// <para><b>Axis-aligned only</b>, for the same reason as <see cref="QrLocator"/>: a barcode on
    /// a page was typeset, not photographed.</para>
    /// </remarks>
    internal static class DataMatrixLocator
    {
        /// <summary>Smallest module size, in pixels, that can be sampled reliably.</summary>
        private const double MinModulePixels = 1.5;

        /// <summary>
        /// Fewest tone changes along the top edge before a square is worth sampling.
        /// </summary>
        /// <remarks>
        /// <b>The clock track is what tells a symbol from a blob.</b> A Data Matrix's top edge
        /// alternates every module, so even the smallest size changes tone eight times across it; a
        /// solid square of ink changes none. This is the cheap structural test that the L alone
        /// cannot give, and without it the arm check matches any filled rectangle on the page.
        /// </remarks>
        private const int MinTopEdgeTransitions = 4;

        /// <summary>
        /// How far the vertical arm's length may differ from the one a size's proportions predict, as
        /// a fraction of that prediction.
        /// </summary>
        /// <remarks>
        /// A square symbol's two arms are the same length; a rectangular one's stand in its rows to
        /// its columns, from 8:32 to 16:36. Allowing a tenth covers the pixel rounding an arbitrary
        /// DPI produces without admitting the L-shaped junctions that ordinary rules and table borders
        /// make, whose proportions are arbitrary.
        /// </remarks>
        private const double ArmLengthTolerance = 0.1;

        /// <summary>
        /// Every Data Matrix grid this can sample out of the page, with the pixel box each came from, offered
        /// to <paramref name="accept"/>, which answers whether the grid was read; an L's other sizes are not
        /// offered once one is, and the search stops when the grids not read have spent the
        /// <see cref="CandidateBudget"/>.
        /// </summary>
        internal static void Locate(
            byte[] pixels, int width, int height, int stride, int threshold,
            Func<(bool[,] Grid, int Left, int Top, int Right, int Bottom), bool> accept)
            => Locate(pixels, width, height, stride, threshold, accept, new CandidateBudget(), out _);

        /// <summary>The same, spending <paramref name="budget"/>, with how many pixels the left arms' walks read — the bounds' evidence.</summary>
        internal static void Locate(
            byte[] pixels, int width, int height, int stride, int threshold,
            Func<(bool[,] Grid, int Left, int Top, int Right, int Bottom), bool> accept, CandidateBudget budget, out long armSteps)
        {
            armSteps = 0;

            for (int y = 0; y < height && !budget.Spent; y++)
            {
                List<BarcodeScanline.Run> runs =
                    BarcodeScanline.Runs(pixels, width, stride, y, threshold);
                if (runs == null) continue;

                foreach (BarcodeScanline.Run run in runs)
                {
                    if (!run.IsDark) continue;

                    int span = run.Length;
                    if (span < DataMatrixLayout.Rows(0) * MinModulePixels) continue;

                    // The bottom arm is only the bottom arm if the left arm rises from its start. No size's
                    // left arm is longer than its bottom one by more than the tolerance, so the walk stops one
                    // pixel past that: walked to the top of the ink, a page of tall bars cost each of its runs
                    // its whole height.
                    int arm = VerticalArm(pixels, stride, height, threshold, run.Start, y,
                                          SaturatingCast.ToInt(span * (1 + ArmLengthTolerance)) + 1);
                    armSteps += arm;
                    if (!FitsSomeSize(span, arm)) continue;

                    // Rows below this one repeat the same bottom arm; take the lowest by requiring
                    // the row beneath to be light at the corner.
                    if (y + 1 < height
                        && BarcodeScanline.IsDark(pixels, stride, run.Start, y + 1, threshold))
                        continue;

                    int top = y - arm + 1;
                    if (top < 0) continue;
                    if (TopEdgeTransitions(pixels, stride, threshold, run.Start, top, span)
                        < MinTopEdgeTransitions)
                        continue;

                    Sample(pixels, width, height, stride, threshold, run.Start, top, span, arm, accept, budget);
                    if (budget.Spent) break;
                }
            }
        }

        /// <summary>The candidates offered on a page where none is read, in the order offered.</summary>
        internal static List<(bool[,] Grid, int Left, int Top, int Right, int Bottom)> Locate(
            byte[] pixels, int width, int height, int stride, int threshold)
        {
            var found = new List<(bool[,], int, int, int, int)>();
            Locate(pixels, width, height, stride, threshold, candidate => { found.Add(candidate); return false; });
            return found;
        }

        /// <summary>Whether an L's arms stand in the proportions of any ECC-200 size.</summary>
        private static bool FitsSomeSize(int span, int arm)
        {
            for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
                if (FitsSize(symbol, span, arm)) return true;
            return false;
        }

        /// <summary>Whether the vertical arm is the length this size's proportions predict.</summary>
        private static bool FitsSize(int symbol, int span, int arm)
        {
            double expected = (double)span * DataMatrixLayout.Rows(symbol) / DataMatrixLayout.Columns(symbol);
            return System.Math.Abs(arm - expected) <= ArmLengthTolerance * expected;
        }

        /// <summary>
        /// Samples the candidate at every ECC-200 size its arms fit and whose modules are big enough, until
        /// <paramref name="accept"/> reads one, charging <paramref name="budget"/> for each it does not.
        /// </summary>
        private static void Sample(
            byte[] pixels, int width, int height, int stride, int threshold,
            int left, int top, int span, int arm,
            Func<(bool[,] Grid, int Left, int Top, int Right, int Bottom), bool> accept, CandidateBudget budget)
        {
            for (int symbol = 0; symbol < DataMatrixLayout.Count; symbol++)
            {
                if (!FitsSize(symbol, span, arm)) continue;

                int rows = DataMatrixLayout.Rows(symbol), columns = DataMatrixLayout.Columns(symbol);
                double moduleWidth = (double)span / columns, moduleHeight = (double)arm / rows;
                if (moduleWidth < MinModulePixels || moduleHeight < MinModulePixels) continue;

                var grid = new bool[rows, columns];
                bool complete = true;

                for (int row = 0; row < rows && complete; row++)
                    for (int column = 0; column < columns; column++)
                    {
                        int x = SaturatingCast.ToInt(left + ((column + 0.5) * moduleWidth));
                        int y = SaturatingCast.ToInt(top + ((row + 0.5) * moduleHeight));

                        if (x < 0 || x >= width || y < 0 || y >= height) { complete = false; break; }
                        grid[row, column] = BarcodeScanline.IsDark(pixels, stride, x, y, threshold);
                    }

                if (!complete) continue;
                if (accept((grid, left, top, left + span, top + arm))) return;
                budget.Fail(rows, columns);
            }
        }

        /// <summary>Tone changes along the candidate square's top edge.</summary>
        private static int TopEdgeTransitions(
            byte[] pixels, int stride, int threshold, int left, int top, int span)
        {
            int transitions = 0;
            bool previous = BarcodeScanline.IsDark(pixels, stride, left, top, threshold);

            for (int x = left + 1; x < left + span; x++)
            {
                bool dark = BarcodeScanline.IsDark(pixels, stride, x, top, threshold);
                if (dark != previous) transitions++;
                previous = dark;
            }

            return transitions;
        }

        /// <summary>
        /// How far a solid dark column rises from (x, y), counted to at most <paramref name="limit"/>: a row is the
        /// column's while either of its first two pixels is dark.
        /// </summary>
        /// <remarks>
        /// A symbol drawn module by module leaves a seam where two modules meet, and where the column's edge falls
        /// part way into a pixel that pixel is lighter at each seam: walked up the edge pixel alone, a symbol at
        /// some positions on the page had a left arm one module long, and a sheet of twenty labels lost two to it.
        /// The pixel beside it is inside the module; above the symbol both are the quiet zone's.
        /// </remarks>
        private static int VerticalArm(
            byte[] pixels, int stride, int height, int threshold, int x, int y, int limit)
        {
            int length = 0;
            int at = y;

            while (at >= 0 && length < limit
                   && (BarcodeScanline.IsDark(pixels, stride, x, at, threshold)
                       || BarcodeScanline.IsDark(pixels, stride, x + 1, at, threshold)))
            {
                length++;
                at--;
            }

            return length;
        }
    }
}
