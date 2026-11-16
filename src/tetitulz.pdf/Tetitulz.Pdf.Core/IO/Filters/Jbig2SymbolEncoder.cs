// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// The symbol half of <see cref="Jbig2EncodeFilter"/>: the connected components of a bilevel
    /// image, the distinct ones as a symbol dictionary (T.88 §6.5, §7.4.3), every occurrence as a
    /// text-region instance (§6.4, §7.4.4), and what is too large to be a symbol as a generic region
    /// — all arithmetic-coded, all <b>lossless</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>Exact matching only.</b> JBIG2's reputation for substituting one character for another
    /// comes from encoders that match symbols approximately; two components are one symbol here only
    /// when their bitmaps are identical, so the decoded page is the input page bit for bit. The win is
    /// smaller than a lossy encoder's and still large on a typed page, where the same glyph bitmap
    /// recurs hundreds of times.</para>
    /// <para><b>The encoder mirrors the decoder's procedures, one table each.</b> The integer coding
    /// ranges are <c>Jbig2IntegerDecoder.Ranges</c> and its PREV rule <c>Advance</c>; the symbol-ID
    /// contexts follow <c>DecodeSymbolId</c>; the context layout of each segment's single MQ coder —
    /// the generic contexts, then IADH, IADW, IAAI, IAEX; the nine text-region integer coders, then
    /// IAID — is the decoder's own, so a dictionary this writes starts its statistics exactly where
    /// the decoder expects to find them. The symbol bitmaps go through
    /// <see cref="Jbig2EncodeFilter.CodeGenericBitmap"/>, the one context model.</para>
    /// <para><b>Order is the standard's.</b> Symbols are written in height classes of ascending height,
    /// ascending width within a class (§6.5.5's non-negative deltas), and that order is the symbol ID
    /// the text region uses. Instances go in strips of one row (LOGSBSTRIPS 0), REFCORNER TOPLEFT,
    /// sorted by their top row and then their left edge, so every IADT, IAFS and IADS is the decoder's
    /// §6.4.5 walk read backwards.</para>
    /// <para><b>Components, not characters.</b> A connected component (8-connected) is the unit; a
    /// glyph of two parts (an <c>i</c>, a colon) is two symbols at two positions, which costs a few
    /// bits per instance and keeps the encoder free of any notion of text. A component larger than
    /// <see cref="MaxSymbolSide"/> on a side or <see cref="MaxSymbolArea"/> in area — a rule, a frame,
    /// a halftoned picture — is not a symbol and is painted into the residual generic region, cropped
    /// to the residual's own bounds.</para>
    /// </remarks>
    internal static class Jbig2SymbolEncoder
    {
        /// <summary>The longest side a component may have and still be a symbol.</summary>
        internal const int MaxSymbolSide = 512;

        /// <summary>The largest bounding-box area a component may have and still be a symbol.</summary>
        internal const int MaxSymbolArea = 512 * 256;

        /// <summary>
        /// More runs than this and the image is a halftone or noise, not a page of symbols: the
        /// analysis declines and the caller encodes a generic region.
        /// </summary>
        internal const int MaxRuns = 4_000_000;

        /// <summary>A distinct symbol bitmap, packed MSB first with a row stride of its own.</summary>
        internal sealed class Symbol
        {
            internal int Width, Height, Stride;
            internal byte[] Bits;
        }

        /// <summary>One occurrence of a symbol: its top-left corner on the page.</summary>
        internal struct Instance
        {
            internal int Symbol, X, Y;
        }

        /// <summary>What the analysis found: the symbols in dictionary order, the instances, the residual.</summary>
        internal sealed class Plan
        {
            internal List<Symbol> Symbols;
            internal List<Instance> Instances;
            /// <summary>The residual, as a packed page-sized bitmap, or null when every component was a symbol.</summary>
            internal byte[] Residual;
            internal int ResidualStride;
            internal int ResidualMinX, ResidualMinY, ResidualMaxX, ResidualMaxY;
            internal bool HasResidual => Residual != null;
        }

        // ================================================================== analysis

        /// <summary>
        /// Finds the components of <paramref name="bits"/> and sorts them into a symbol dictionary, the
        /// instances that use it and the residual; <see langword="null"/> when there is no symbol to
        /// write — a blank image, one of only oversized components, or one of too many runs.
        /// </summary>
        internal static Plan Analyse(byte[] bits, int width, int height, int stride)
        {
            var runY = new List<int>();
            var runX0 = new List<int>();
            var runX1 = new List<int>();
            var parent = new List<int>();

            int prevStart = 0, prevEnd = 0;
            for (int y = 0; y < height; y++)
            {
                int rowStart = runY.Count;
                int j = prevStart;
                int x = 0;
                while (x < width)
                {
                    if (GetPixel(bits, stride, x, y) == 0) { x++; continue; }
                    int x0 = x;
                    while (x < width && GetPixel(bits, stride, x, y) == 1) x++;
                    int x1 = x - 1;

                    int me = runY.Count;
                    runY.Add(y); runX0.Add(x0); runX1.Add(x1); parent.Add(me);
                    if (me >= MaxRuns) return null;

                    // 8-connectivity: a run on the row above touches this one when their spans
                    // overlap or meet diagonally. Both rows' runs are sorted by x0, so the runs that
                    // ended before x0 - 1 cannot touch this run or any later one on this row.
                    while (j < prevEnd && runX1[j] < x0 - 1) j++;
                    for (int k = j; k < prevEnd && runX0[k] <= x1 + 1; k++)
                        Union(parent, k, me);
                }
                prevStart = rowStart;
                prevEnd = runY.Count;
            }

            if (runY.Count == 0) return null;

            // Components: a root per component, its bounding box.
            var componentOf = new Dictionary<int, int>();
            var minX = new List<int>(); var minY = new List<int>(); var maxX = new List<int>(); var maxY = new List<int>();
            var runComponent = new int[runY.Count];
            for (int r = 0; r < runY.Count; r++)
            {
                int root = Find(parent, r);
                if (!componentOf.TryGetValue(root, out int c))
                {
                    c = minX.Count;
                    componentOf[root] = c;
                    minX.Add(runX0[r]); maxX.Add(runX1[r]); minY.Add(runY[r]); maxY.Add(runY[r]);
                }
                else
                {
                    if (runX0[r] < minX[c]) minX[c] = runX0[r];
                    if (runX1[r] > maxX[c]) maxX[c] = runX1[r];
                    if (runY[r] < minY[c]) minY[c] = runY[r];
                    if (runY[r] > maxY[c]) maxY[c] = runY[r];
                }
                runComponent[r] = c;
            }

            // Which components are symbols; build each symbol's bitmap and the residual.
            int componentCount = minX.Count;
            var isSymbol = new bool[componentCount];
            var symbolBits = new byte[componentCount][];
            int pageStride = (width + 7) / 8;
            byte[] residual = null;
            int rMinX = int.MaxValue, rMinY = int.MaxValue, rMaxX = -1, rMaxY = -1;
            for (int c = 0; c < componentCount; c++)
            {
                int w = maxX[c] - minX[c] + 1, h = maxY[c] - minY[c] + 1;
                isSymbol[c] = w <= MaxSymbolSide && h <= MaxSymbolSide && (long)w * h <= MaxSymbolArea;
                if (isSymbol[c]) symbolBits[c] = new byte[((w + 7) / 8) * h];
            }
            for (int r = 0; r < runY.Count; r++)
            {
                int c = runComponent[r];
                if (isSymbol[c])
                {
                    int w = maxX[c] - minX[c] + 1;
                    int sStride = (w + 7) / 8;
                    int row = runY[r] - minY[c];
                    for (int x = runX0[r]; x <= runX1[r]; x++)
                        symbolBits[c][row * sStride + ((x - minX[c]) >> 3)] |= (byte)(0x80 >> ((x - minX[c]) & 7));
                }
                else
                {
                    residual ??= new byte[pageStride * height];
                    for (int x = runX0[r]; x <= runX1[r]; x++)
                        residual[runY[r] * pageStride + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                    if (runX0[r] < rMinX) rMinX = runX0[r];
                    if (runX1[r] > rMaxX) rMaxX = runX1[r];
                    if (runY[r] < rMinY) rMinY = runY[r];
                    if (runY[r] > rMaxY) rMaxY = runY[r];
                }
            }

            // Distinct symbols, by exact bitmap; every symbol-sized component is an instance.
            var distinct = new Dictionary<SymbolKey, int>();
            var symbols = new List<Symbol>();
            var instances = new List<Instance>();
            for (int c = 0; c < componentCount; c++)
            {
                if (!isSymbol[c]) continue;
                int w = maxX[c] - minX[c] + 1, h = maxY[c] - minY[c] + 1;
                var key = new SymbolKey(w, h, symbolBits[c]);
                if (!distinct.TryGetValue(key, out int id))
                {
                    id = symbols.Count;
                    distinct[key] = id;
                    symbols.Add(new Symbol { Width = w, Height = h, Stride = (w + 7) / 8, Bits = symbolBits[c] });
                }
                instances.Add(new Instance { Symbol = id, X = minX[c], Y = minY[c] });
            }
            if (instances.Count == 0) return null;

            // Dictionary order: height classes ascending, widths ascending within a class — the
            // symbol IDs the text region will name.
            var order = new int[symbols.Count];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int byHeight = symbols[a].Height.CompareTo(symbols[b].Height);
                if (byHeight != 0) return byHeight;
                int byWidth = symbols[a].Width.CompareTo(symbols[b].Width);
                return byWidth != 0 ? byWidth : a.CompareTo(b);
            });
            var newId = new int[symbols.Count];
            var sorted = new List<Symbol>(symbols.Count);
            for (int i = 0; i < order.Length; i++) { newId[order[i]] = i; sorted.Add(symbols[order[i]]); }
            for (int i = 0; i < instances.Count; i++)
            {
                Instance inst = instances[i];
                inst.Symbol = newId[inst.Symbol];
                instances[i] = inst;
            }
            // Instance order: strips of one row, top to bottom, left to right within a row.
            instances.Sort((a, b) => a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X));

            return new Plan
            {
                Symbols = sorted,
                Instances = instances,
                Residual = residual,
                ResidualStride = pageStride,
                ResidualMinX = rMinX, ResidualMinY = rMinY, ResidualMaxX = rMaxX, ResidualMaxY = rMaxY,
            };
        }

        private static int Find(List<int> parent, int i)
        {
            int root = i;
            while (parent[root] != root) root = parent[root];
            while (parent[i] != root) { int next = parent[i]; parent[i] = root; i = next; }
            return root;
        }

        private static void Union(List<int> parent, int a, int b)
        {
            int ra = Find(parent, a), rb = Find(parent, b);
            if (ra != rb) parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }

        private static int GetPixel(byte[] bits, int stride, int x, int y)
            => (bits[(y * stride) + (x >> 3)] >> (7 - (x & 7))) & 1;

        /// <summary>A symbol's identity: its size and every byte of its bitmap.</summary>
        private readonly struct SymbolKey : IEquatable<SymbolKey>
        {
            private readonly int _width, _height, _hash;
            private readonly byte[] _bits;

            internal SymbolKey(int width, int height, byte[] bits)
            {
                _width = width; _height = height; _bits = bits;
                unchecked
                {
                    int h = (int)2166136261 ^ width;
                    h = (h * 16777619) ^ height;
                    for (int i = 0; i < bits.Length; i++) h = (h * 16777619) ^ bits[i];
                    _hash = h;
                }
            }

            public bool Equals(SymbolKey other)
            {
                if (_width != other._width || _height != other._height || _bits.Length != other._bits.Length) return false;
                for (int i = 0; i < _bits.Length; i++) if (_bits[i] != other._bits[i]) return false;
                return true;
            }

            public override bool Equals(object obj) => obj is SymbolKey k && Equals(k);
            public override int GetHashCode() => _hash;
        }

        // ================================================================== the coded bodies

        /// <summary>
        /// The symbol dictionary's arithmetic data (§6.5.5–§6.5.10 as the decoder reads them): height
        /// classes, each symbol a generic region on the dictionary's coder, the class closed by an OOB
        /// width; then the export flags — no symbol excluded, every symbol exported.
        /// </summary>
        internal static byte[] CodeSymbolDictionary(IReadOnlyList<Symbol> symbols)
        {
            const int genericContexts = 1 << 16;
            int intTable = Jbig2Decoder.Jbig2IntegerDecoder.ContextTableSize;
            var mq = new MqEncoder(genericContexts + 4 * intTable);
            var iadh = new Jbig2IntegerEncoder(mq, genericContexts + 0 * intTable);
            var iadw = new Jbig2IntegerEncoder(mq, genericContexts + 1 * intTable);
            var iaex = new Jbig2IntegerEncoder(mq, genericContexts + 3 * intTable);

            int currentHeight = 0;
            int i = 0;
            while (i < symbols.Count)
            {
                int h = symbols[i].Height;
                iadh.Encode(h - currentHeight);
                currentHeight = h;

                int symWidth = 0;
                while (i < symbols.Count && symbols[i].Height == h)
                {
                    Symbol s = symbols[i];
                    iadw.Encode(s.Width - symWidth);
                    symWidth = s.Width;
                    Jbig2EncodeFilter.CodeGenericBitmap(mq, 0, s.Bits, s.Stride, s.Width, s.Height);
                    i++;
                }
                iadw.EncodeOob();
            }

            // §6.5.10: runs alternate exclude / include, starting with exclude.
            iaex.Encode(0);
            iaex.Encode(symbols.Count);
            return mq.Finish();
        }

        /// <summary>
        /// The text region's arithmetic data (§6.4.5 as the decoder reads it): STRIPT, then per strip
        /// its DT and first S, and per instance its symbol ID and the S delta to the next, the strip
        /// closed by an OOB. One row per strip, TOPLEFT, no refinement.
        /// </summary>
        internal static byte[] CodeTextRegion(IReadOnlyList<Symbol> symbols, IReadOnlyList<Instance> instances)
        {
            int intTable = Jbig2Decoder.Jbig2IntegerDecoder.ContextTableSize;
            int symCodeLen = Jbig2Decoder.SymbolCodeLength(symbols.Count);
            int iaidSize = 1 << Math.Min(symCodeLen, 16);
            var mq = new MqEncoder(9 * intTable + iaidSize);
            var iadt = new Jbig2IntegerEncoder(mq, 0 * intTable);
            var iafs = new Jbig2IntegerEncoder(mq, 1 * intTable);
            var iads = new Jbig2IntegerEncoder(mq, 2 * intTable);
            int iaidBase = 9 * intTable;

            iadt.Encode(0);                               // STRIPT: no initial offset
            int stripT = 0, firstS = 0;
            int k = 0;
            while (k < instances.Count)
            {
                int t = instances[k].Y;
                iadt.Encode(t - stripT);
                stripT = t;

                int x = instances[k].X;
                iafs.Encode(x - firstS);
                firstS = x;
                int curS = x;

                while (true)
                {
                    Instance inst = instances[k];
                    EncodeSymbolId(mq, iaidBase, symCodeLen, inst.Symbol);
                    curS += symbols[inst.Symbol].Width - 1;     // the decoder's advance for TOPLEFT, not transposed
                    k++;

                    if (k < instances.Count && instances[k].Y == t)
                    {
                        iads.Encode(instances[k].X - curS);
                        curS = instances[k].X;
                    }
                    else
                    {
                        iads.EncodeOob();
                        break;
                    }
                }
            }
            return mq.Finish();
        }

        /// <summary>
        /// Codes a symbol ID in <paramref name="idBits"/> bits, most significant first, each under the
        /// context the bits so far select — the mirror of the decoder's <c>DecodeSymbolId</c>.
        /// </summary>
        internal static void EncodeSymbolId(MqEncoder mq, int baseContext, int idBits, int id)
        {
            if (idBits <= 0) return;
            int prev = 1;
            int tableSize = 1 << Math.Min(idBits, 16);
            for (int i = idBits - 1; i >= 0; i--)
            {
                int bit = (id >> i) & 1;
                mq.Encode(baseContext + (prev % tableSize), bit);
                prev = (prev << 1) | bit;
            }
        }

        // ================================================================== segments

        /// <summary>
        /// The symbol dictionary segment body (§7.4.3): flags for arithmetic, no refinement, template 0,
        /// the four template-0 AT pixels at their nominal positions, the export and new counts, the data.
        /// </summary>
        internal static byte[] SymbolDictionaryBody(int symbolCount, byte[] coded)
        {
            var body = new MemoryStream();
            body.WriteByte(0); body.WriteByte(0);                // flags: SDHUFF 0, SDREFAGG 0, SDTEMPLATE 0
            Jbig2EncodeFilter.WriteTemplate0At(body);
            Jbig2EncodeFilter.WriteU32(body, symbolCount);       // SDNUMEXSYMS
            Jbig2EncodeFilter.WriteU32(body, symbolCount);       // SDNUMNEWSYMS
            body.Write(coded, 0, coded.Length);
            return body.ToArray();
        }

        /// <summary>
        /// The text region segment body (§7.4.4): the region information field for the whole page,
        /// flags for arithmetic, no refinement, one row per strip, TOPLEFT, OR, the instance count, the data.
        /// </summary>
        internal static byte[] TextRegionBody(int width, int height, int instanceCount, byte[] coded)
        {
            var body = new MemoryStream();
            Jbig2EncodeFilter.WriteRegionInfo(body, width, height, 0, 0);
            // SBHUFF 0, REFINE 0, LOGSBSTRIPS 0, REFCORNER 1 (TOPLEFT), TRANSPOSED 0, SBCOMBOP OR,
            // SBDEFPIXEL 0, SBDSOFFSET 0, RTEMPLATE 0.
            Jbig2EncodeFilter.WriteU16(body, 1 << 4);
            Jbig2EncodeFilter.WriteU32(body, instanceCount);
            body.Write(coded, 0, coded.Length);
            return body.ToArray();
        }
    }

    /// <summary>
    /// The arithmetic integer <b>encoding</b> procedure of T.88 Annex A — the mirror of
    /// <c>Jbig2Decoder.Jbig2IntegerDecoder</c>, over the same 512-context block and the same
    /// <c>Ranges</c> table: the sign, then as many ones as ranges skipped and a zero (five ones for the
    /// last range), then the magnitude less the range's offset in the range's bits. OOB is a sign of 1
    /// with a magnitude of 0.
    /// </summary>
    internal sealed class Jbig2IntegerEncoder
    {
        private readonly MqEncoder _mq;
        private readonly int _base;

        internal Jbig2IntegerEncoder(MqEncoder mq, int baseContext)
        {
            _mq = mq ?? throw new ArgumentNullException(nameof(mq));
            _base = baseContext;
        }

        internal void Encode(int value) => EncodeCore(value < 0 ? 1 : 0, Math.Abs((long)value));

        internal void EncodeOob() => EncodeCore(1, 0);

        private void EncodeCore(int sign, long magnitude)
        {
            var ranges = Jbig2Decoder.Jbig2IntegerDecoder.Ranges;
            int prev = 1;
            Bit(ref prev, sign);

            int range = 0;
            while (range < ranges.Length - 1 && magnitude >= ranges[range + 1].Offset) range++;
            for (int i = 0; i < range; i++) Bit(ref prev, 1);
            if (range < ranges.Length - 1) Bit(ref prev, 0);

            long v = magnitude - ranges[range].Offset;
            for (int i = ranges[range].Bits - 1; i >= 0; i--) Bit(ref prev, (int)((v >> i) & 1));
        }

        private void Bit(ref int prev, int bit)
        {
            _mq.Encode(_base + prev, bit);
            prev = Jbig2Decoder.Jbig2IntegerDecoder.Advance(prev, bit);
        }
    }
}
