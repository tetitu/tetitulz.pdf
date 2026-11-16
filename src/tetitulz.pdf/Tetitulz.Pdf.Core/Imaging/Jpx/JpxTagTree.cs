// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// A tag-tree decoder (T.800 §B.10.2): a two-dimensional array of non-negative integers coded
    /// through a quadtree whose every node holds the minimum of its children. Each query asks whether
    /// a leaf's value is below a threshold, and reads only the bits that question needs; what the
    /// bits establish — a node's value, or a lower bound on it — is kept for the next query.
    /// </summary>
    internal sealed class JpxTagTree
    {
        private const int Unknown = int.MaxValue;

        private readonly int[] _levelWidth;    // level 0 is the leaves; the last level is the 1x1 root
        private readonly int[] _levelOffset;
        private readonly int[] _value;         // known value, or Unknown
        private readonly int[] _low;           // current lower bound
        private readonly int[] _path;          // scratch: node indices from leaf to root

        internal JpxTagTree(int width, int height)
        {
            int levels = 1;
            for (int w = width, h = height; w > 1 || h > 1; w = (w + 1) >> 1, h = (h + 1) >> 1) levels++;
            _levelWidth = new int[levels];
            _levelOffset = new int[levels];
            int nodes = 0;
            for (int i = 0, w = width, h = height; i < levels; i++, w = (w + 1) >> 1, h = (h + 1) >> 1)
            {
                _levelWidth[i] = w;
                _levelOffset[i] = nodes;
                nodes += w * h;
            }
            _value = new int[nodes];
            _low = new int[nodes];
            _path = new int[levels];
            Reset();
        }

        internal int Width => _levelWidth[0];

        internal void Reset()
        {
            for (int i = 0; i < _value.Length; i++)
            {
                _value[i] = Unknown;
                _low[i] = 0;
            }
        }

        /// <summary>
        /// Reads bits until leaf (<paramref name="x"/>, <paramref name="y"/>) is known to be below
        /// <paramref name="threshold"/> or known not to be, and says which. <paramref name="exhausted"/>
        /// is true when the bits ran out first, in which case the answer is false.
        /// </summary>
        internal bool Decode(JpxBitReader bits, int x, int y, int threshold, out bool exhausted)
        {
            exhausted = false;
            int depth = 0;
            for (int level = 0, lx = x, ly = y; level < _levelWidth.Length; level++, lx >>= 1, ly >>= 1)
                _path[depth++] = _levelOffset[level] + ly * _levelWidth[level] + lx;

            int low = 0;
            int node = 0;
            for (int i = depth - 1; i >= 0; i--)
            {
                node = _path[i];
                if (low > _low[node]) _low[node] = low;
                else low = _low[node];
                while (low < threshold && low < _value[node])
                {
                    if (!bits.TryReadBit(out int bit))
                    {
                        exhausted = true;
                        return false;
                    }
                    if (bit == 1) _value[node] = low;
                    else low++;
                }
                _low[node] = low;
            }
            return _value[node] < threshold;
        }

        /// <summary>The leaf's value once <see cref="Decode"/> has established it; <c>-1</c> before that.</summary>
        internal int ValueOf(int x, int y)
        {
            int v = _value[_levelOffset[0] + y * _levelWidth[0] + x];
            return v == Unknown ? -1 : v;
        }
    }
}
