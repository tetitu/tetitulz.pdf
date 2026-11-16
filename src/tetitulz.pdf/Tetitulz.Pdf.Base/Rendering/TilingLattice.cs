// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// One period of a tiling pattern (ISO 32000-2 §8.7.3.1): the tile is <c>/XStep</c> by
    /// <c>/YStep</c>, anchored at the cell's <c>/BBox</c> bottom-left corner, and holds every cell
    /// of the lattice that reaches into it — one where the step is at least the cell, several where
    /// a <c>/BBox</c> larger than its step makes neighbouring cells overlap. Both renderers build
    /// their tile from it, the raster as a bitmap and the SVG writer as a <c>&lt;pattern&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>A cell shifted by (<c>i·XStep</c>, <c>j·YStep</c>) covers the tile
    /// <c>[0, XStep) × [0, YStep)</c> (relative to the cell's corner) for <c>i</c> and <c>j</c> from
    /// <c>0</c> down to <c>1 − ⌈extent / step⌉</c>. The copies come in lattice order — <c>j</c> then
    /// <c>i</c>, ascending — so where cells overlap, the one further up and to the right is painted
    /// over its neighbour, in both renderers alike.</para>
    /// <para><b>Bounded</b>: past <see cref="MaxCopies"/> the tile holds the cell once, at the right
    /// period, and <see cref="Approximated"/> says the overlap was dropped.</para>
    /// </remarks>
    internal readonly struct TilingLattice
    {
        /// <summary>The most cell copies one tile is built from.</summary>
        internal const int MaxCopies = 64;

        // A cell that exceeds its step by less than this is taken to fit it (float /BBox noise).
        private const float FitEpsilon = 1e-4f;

        private TilingLattice(float stepX, float stepY, IReadOnlyList<(float Dx, float Dy)> copies, bool approximated)
        {
            StepX = stepX;
            StepY = stepY;
            Copies = copies;
            Approximated = approximated;
        }

        /// <summary>The tile's width: the horizontal period.</summary>
        internal float StepX { get; }

        /// <summary>The tile's height: the vertical period.</summary>
        internal float StepY { get; }

        /// <summary>Each cell copy's offset from the cell's own place, in pattern space, in paint order.</summary>
        internal IReadOnlyList<(float Dx, float Dy)> Copies { get; }

        /// <summary>True when the overlap needed more than <see cref="MaxCopies"/> copies and was dropped.</summary>
        internal bool Approximated { get; }

        /// <summary>The lattice for a cell of <paramref name="cellW"/> by <paramref name="cellH"/> at the given steps (nonzero; the sign is ignored).</summary>
        internal static TilingLattice For(float cellW, float cellH, float xStep, float yStep)
        {
            float stepX = Math.Abs(xStep), stepY = Math.Abs(yStep);
            double nx = CopiesAlong(cellW, stepX), ny = CopiesAlong(cellH, stepY);
            if (nx * ny > MaxCopies)
                return new TilingLattice(stepX, stepY, new[] { (0f, 0f) }, approximated: true);

            var copies = new List<(float, float)>(SaturatingCast.ToInt(nx * ny));
            for (int j = 1 - SaturatingCast.ToInt(ny); j <= 0; j++)
                for (int i = 1 - SaturatingCast.ToInt(nx); i <= 0; i++)
                    copies.Add((i * stepX, j * stepY));
            return new TilingLattice(stepX, stepY, copies, approximated: false);
        }

        /// <summary>How many cells along one axis reach into one period: ⌈extent / step⌉, at least 1.</summary>
        private static double CopiesAlong(float extent, float step)
            => Math.Max(1.0, Math.Ceiling(extent / (double)step - FitEpsilon));
    }
}
