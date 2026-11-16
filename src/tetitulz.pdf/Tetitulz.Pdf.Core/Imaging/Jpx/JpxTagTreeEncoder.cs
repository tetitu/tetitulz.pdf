// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// A tag-tree encoder (T.800 §B.10.2), the mirror of <see cref="JpxTagTree"/>: the leaves'
    /// values are set first, every node above holds the minimum of its children, and each
    /// <see cref="Encode"/> emits exactly the bits the decoder's query with the same threshold will
    /// read — a 0 for each unit the node's value exceeds what the decoder knows so far, a 1 when
    /// the value is reached, nothing once the node is known or the threshold is met.
    /// </summary>
    internal sealed class JpxTagTreeEncoder
    {
        private const int Unknown = int.MaxValue;

        private readonly int[] _levelWidth;    // level 0 is the leaves; the last level is the 1x1 root
        private readonly int[] _levelHeight;
        private readonly int[] _levelOffset;
        private readonly int[] _value;         // the node's value: a leaf's as set, a parent's the minimum below it
        private readonly int[] _coded;         // what the bits emitted so far have fixed — the decoder's knowledge — or Unknown
        private readonly int[] _low;           // the decoder's current lower bound
        private readonly int[] _path;          // scratch: node indices from leaf to root
        private bool _built;

        internal JpxTagTreeEncoder(int width, int height)
        {
            int levels = 1;
            for (int w = width, h = height; w > 1 || h > 1; w = (w + 1) >> 1, h = (h + 1) >> 1) levels++;
            _levelWidth = new int[levels];
            _levelHeight = new int[levels];
            _levelOffset = new int[levels];
            int nodes = 0;
            for (int i = 0, w = width, h = height; i < levels; i++, w = (w + 1) >> 1, h = (h + 1) >> 1)
            {
                _levelWidth[i] = w;
                _levelHeight[i] = h;
                _levelOffset[i] = nodes;
                nodes += w * h;
            }
            _value = new int[nodes];
            _coded = new int[nodes];
            _low = new int[nodes];
            _path = new int[levels];
            for (int i = 0; i < nodes; i++) _coded[i] = Unknown;
        }

        private JpxTagTreeEncoder(JpxTagTreeEncoder source)
        {
            _levelWidth = source._levelWidth;
            _levelHeight = source._levelHeight;
            _levelOffset = source._levelOffset;
            _value = (int[])source._value.Clone();
            _coded = (int[])source._coded.Clone();
            _low = (int[])source._low.Clone();
            _path = new int[source._path.Length];
            _built = source._built;
        }

        /// <summary>A copy in this tree's state — what the decoder knows so far — that codes on independently; a rate allocator tries a layer from one and keeps the base.</summary>
        internal JpxTagTreeEncoder Clone() => new JpxTagTreeEncoder(this);

        /// <summary>Sets leaf (<paramref name="x"/>, <paramref name="y"/>)'s value; every leaf is set before the first <see cref="Encode"/>.</summary>
        internal void SetValue(int x, int y, int value)
        {
            if (_built) throw new InvalidOperationException("a tag tree's values are fixed once coding has begun");
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "a tag tree holds non-negative integers");
            _value[_levelOffset[0] + y * _levelWidth[0] + x] = value;
        }

        /// <summary>
        /// Lowers leaf (<paramref name="x"/>, <paramref name="y"/>)'s value after coding has begun,
        /// and every parent whose minimum it lowers — what a rate allocator learns as it decides a
        /// block's first layer, having built the tree with "not yet" (a large value) for the
        /// undecided. A value never rises, so a higher value is ignored; a value the bits already
        /// emitted contradict — a node coded to its value, or a decoder lower bound above the new
        /// value — is refused, because those bits are in packets already measured.
        /// </summary>
        internal void Lower(int x, int y, int value)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(nameof(value), "a tag tree holds non-negative integers");
            if (!_built)
            {
                int leaf = _levelOffset[0] + y * _levelWidth[0] + x;
                if (value < _value[leaf]) _value[leaf] = value;
                return;
            }
            for (int level = 0, lx = x, ly = y; level < _levelWidth.Length; level++, lx >>= 1, ly >>= 1)
            {
                int node = _levelOffset[level] + ly * _levelWidth[level] + lx;
                if (value >= _value[node]) return;   // this node's minimum, and every one above, stays
                if (_coded[node] != Unknown || _low[node] > value)
                    throw new InvalidOperationException($"a tag tree node already coded to {(_coded[node] != Unknown ? _coded[node] : _low[node])} cannot take the value {value}");
                _value[node] = value;
            }
        }

        /// <summary>
        /// Emits the bits that let a decoder answer whether leaf (<paramref name="x"/>,
        /// <paramref name="y"/>) is below <paramref name="threshold"/>: the mirror of
        /// <see cref="JpxTagTree.Decode"/>, step for step.
        /// </summary>
        internal void Encode(JpxBitWriter bits, int x, int y, int threshold)
        {
            if (!_built) Build();
            int depth = 0;
            for (int level = 0, lx = x, ly = y; level < _levelWidth.Length; level++, lx >>= 1, ly >>= 1)
                _path[depth++] = _levelOffset[level] + ly * _levelWidth[level] + lx;

            int low = 0;
            for (int i = depth - 1; i >= 0; i--)
            {
                int node = _path[i];
                if (low > _low[node]) _low[node] = low;
                else low = _low[node];
                while (low < threshold && low < _coded[node])
                {
                    if (low == _value[node])
                    {
                        bits.WriteBit(1);
                        _coded[node] = low;
                    }
                    else
                    {
                        bits.WriteBit(0);
                        low++;
                    }
                }
                _low[node] = low;
            }
        }

        /// <summary>Every parent becomes the minimum of its children, leaves up.</summary>
        private void Build()
        {
            for (int level = 1; level < _levelWidth.Length; level++)
                for (int i = 0; i < _levelWidth[level] * _levelHeight[level]; i++) _value[_levelOffset[level] + i] = Unknown;
            for (int level = 0; level + 1 < _levelWidth.Length; level++)
            {
                int w = _levelWidth[level], h = _levelHeight[level], pw = _levelWidth[level + 1];
                for (int ly = 0; ly < h; ly++)
                    for (int lx = 0; lx < w; lx++)
                    {
                        int child = _levelOffset[level] + ly * w + lx;
                        int parent = _levelOffset[level + 1] + (ly >> 1) * pw + (lx >> 1);
                        if (_value[child] < _value[parent]) _value[parent] = _value[child];
                    }
            }
            _built = true;
        }
    }
}
