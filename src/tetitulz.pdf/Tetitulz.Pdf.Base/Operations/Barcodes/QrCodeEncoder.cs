// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Barcodes
{
    /// <summary>
    /// Pure-managed QR Code encoder (byte-mode, error-correction level M).
    /// Supports versions 1–10 (21×21 to 57×57 modules). Output is a boolean matrix
    /// suitable for rendering as PDF content-stream black/white modules.
    /// </summary>
    internal static class QrCodeEncoder
    {
        /// <summary>
        /// Highest error-correction codeword count <see cref="QrLayout.EcPerBlock(int)"/> returns
        /// (v3 and v10 both use 26). Generators for every degree up to this are precomputed at type
        /// initialisation.
        /// </summary>
        private const int MaxEcCodewordsPerBlock = 26;

        /// <summary>
        /// The field arithmetic, shared with <c>DataMatrixEncoder</c> through
        /// <see cref="GaloisField256"/>. Both carried a token-for-token copy of the tables, the
        /// generator construction and the RS encoder; only the primitive polynomial differed, and
        /// that difference is the standard rather than drift.
        /// </summary>
        /// <remarks>
        /// Generators are precomputed inside <see cref="GaloisField256"/> and never lazily cached
        /// -- the reason is on that type, and it is a real concurrency defect rather than a
        /// preference.
        /// </remarks>
        private static readonly GaloisField256 Gf =
            new GaloisField256(GaloisField256.QrPrimitive, MaxEcCodewordsPerBlock);

        /// <summary>Returns a square boolean module grid for the given UTF-8 text. True = dark module.</summary>
        public static bool[,] Encode(string text)
        {
            if (text == null) throw new ArgumentNullException(nameof(text));
            byte[] data = Encoding.UTF8.GetBytes(text);

            int version = SelectVersion(data.Length);

            // ── Build data bit stream ───────────────────────────────────────
            var bits = new BitBuilder();
            bits.Append(QrLayout.ByteMode, 4);      // mode indicator (byte mode)
            int lenBits = QrLayout.CharacterCountBits(version);   // char-count indicator width
            bits.Append(data.Length, lenBits);
            foreach (byte b in data) bits.Append(b, 8);

            int totalData = QrLayout.DataCodewords(version);
            int totalDataBits = totalData * 8;

            // Terminator up to 4 bits if room
            int pad = Math.Min(4, totalDataBits - bits.Count);
            if (pad > 0) bits.Append(0, pad);
            // Byte-align
            while (bits.Count % 8 != 0) bits.Append(0, 1);
            // Pad bytes 0xEC, 0x11 alternating
            bool ec = true;
            while (bits.Count < totalDataBits)
            {
                bits.Append(ec ? 0xEC : 0x11, 8);
                ec = !ec;
            }

            return EncodeCodewords(bits.ToBytes(), version);
        }

        /// <summary>
        /// The symbol for data codewords already encoded — split into blocks, error-corrected,
        /// interleaved, placed and masked exactly as <see cref="Encode"/> does. For a caller (a test)
        /// that needs a data stream this encoder does not write: another mode, an empty or a truncated
        /// segment. <paramref name="dataCodewords"/> must be <see cref="QrLayout.DataCodewords(int)"/> long.
        /// </summary>
        internal static bool[,] EncodeCodewords(byte[] dataCodewords, int version)
        {
            int size = QrLayout.SizeOf(version);

            // ── Split into EC blocks and compute EC codewords ──────────────
            int ecPerBlock = QrLayout.EcPerBlock(version);
            int[] blockSizes = QrLayout.BlockSizes(version);

            var dataBlocks = new List<byte[]>();
            var ecBlocks   = new List<byte[]>();
            int offset = 0;
            foreach (int blockSize in blockSizes)
            {
                var blk = new byte[blockSize];
                Array.Copy(dataCodewords, offset, blk, 0, blockSize);
                offset += blockSize;
                dataBlocks.Add(blk);
                ecBlocks.Add(ReedSolomon(blk, ecPerBlock));
            }

            // Interleave: data column-major then EC column-major
            var interleaved = new List<byte>();
            int maxDataLen = 0; foreach (var blk in dataBlocks) if (blk.Length > maxDataLen) maxDataLen = blk.Length;
            for (int col = 0; col < maxDataLen; col++)
                foreach (var blk in dataBlocks)
                    if (col < blk.Length) interleaved.Add(blk[col]);
            for (int col = 0; col < ecPerBlock; col++)
                foreach (var blk in ecBlocks)
                    interleaved.Add(blk[col]);

            // ── Lay out matrix ─────────────────────────────────────────────
            // Function patterns, the reserved map and the dark module all come from QrLayout,
            // which the reader walks in the other direction. One definition or they drift.
            QrLayout.BuildFunctionPatterns(version, out bool[,] matrix, out bool[,] reserved);

            // Lay data bits column-pair by column-pair from right to left
            PlaceData(matrix, reserved, size, interleaved);

            // ── Mask selection ─────────────────────────────────────────────
            int bestMask = 0;
            bool[,] best = null;
            int bestPenalty = int.MaxValue;
            for (int m = 0; m < 8; m++)
            {
                var candidate = ApplyMask(matrix, reserved, size, m);
                ApplyFormat(candidate, size, m);
                int p = Penalty(candidate, size);
                if (p < bestPenalty) { bestPenalty = p; bestMask = m; best = candidate; }
            }

            // Apply version info (v≥7) — deferred here once
            if (version >= 7) ApplyVersion(best, version, size);

            return best;
        }

        // ── Version selection ────────────────────────────────────────────────

        private static int SelectVersion(int dataLen)
        {
            // Capacities from the codeword table (QrLayout.ByteCapacity): 14 bytes at v1 to 213 at
            // v10, which is ISO/IEC 18004 Table 11's ECC-M byte-mode column.
            for (int v = QrLayout.MinVersion; v <= QrLayout.MaxVersion; v++)
                if (dataLen <= QrLayout.ByteCapacity(v)) return v;
            throw new ArgumentException(
                "QR data exceeds version-10 ECC-M capacity (" + QrLayout.ByteCapacity(QrLayout.MaxVersion) + " bytes).");
        }

        // ── Reed-Solomon ─────────────────────────────────────────────────────

        /// <summary>The RS codewords, through the shared field. Every element is 0-255, so the
        /// narrowing to <c>byte</c> is lossless.</summary>
        private static byte[] ReedSolomon(byte[] data, int ecLen)
        {
            var input = new int[data.Length];
            for (int i = 0; i < data.Length; i++) input[i] = data[i];

            int[] ec = Gf.ErrorCorrection(input, ecLen);
            var result = new byte[ecLen];
            for (int i = 0; i < ecLen; i++) result[i] = (byte)ec[i];
            return result;
        }

        // ── Matrix layout ───────────────────────────────────────────────────

        private static void PlaceData(bool[,] m, bool[,] r, int size, List<byte> codewords)
        {
            int bitIdx = 0;
            int totalBits = codewords.Count * 8;

            foreach ((int y, int x) in QrLayout.DataModuleOrder(size, r))
            {
                bool bit = false;
                if (bitIdx < totalBits)
                {
                    bit = ((codewords[bitIdx / 8] >> (7 - (bitIdx % 8))) & 1) == 1;
                    bitIdx++;
                }
                m[y, x] = bit;
            }
        }

        // ── Masks and penalty ───────────────────────────────────────────────

        private static bool[,] ApplyMask(bool[,] src, bool[,] reserved, int size, int mask)
        {
            var dst = new bool[size, size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    bool v = src[y, x];
                    if (!reserved[y, x] && QrLayout.MaskBit(mask, y, x)) v = !v;
                    dst[y, x] = v;
                }
            return dst;
        }

        private static int Penalty(bool[,] m, int size)
        {
            int p = 0;
            // Rule 1: runs of 5+ same-colour modules in rows/columns
            for (int y = 0; y < size; y++)
            {
                int run = 1;
                for (int x = 1; x < size; x++)
                {
                    if (m[y, x] == m[y, x - 1]) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                    else run = 1;
                }
            }
            for (int x = 0; x < size; x++)
            {
                int run = 1;
                for (int y = 1; y < size; y++)
                {
                    if (m[y, x] == m[y - 1, x]) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                    else run = 1;
                }
            }
            // Rule 2: 2x2 same colour
            for (int y = 0; y < size - 1; y++)
                for (int x = 0; x < size - 1; x++)
                    if (m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) p += 3;
            // Rule 3: finder-like patterns (simplified)
            // Rule 4: dark ratio deviation from 50%
            int dark = 0;
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++) if (m[y, x]) dark++;
            int pct = (dark * 100) / (size * size);
            int dev = Math.Abs(pct - 50);
            p += (dev / 5) * 10;
            return p;
        }

        // ── Format & version info ───────────────────────────────────────────

        private static void ApplyFormat(bool[,] m, int size, int mask)
        {
            int code = QrLayout.FormatCode(QrLayout.EccLevelM, mask);

            for (int bit = 0; bit < 15; bit++)
            {
                bool set = ((code >> bit) & 1) == 1;

                (int ay, int ax) = QrLayout.FormatPositionA(bit);
                (int by, int bx) = QrLayout.FormatPositionB(bit, size);
                m[ay, ax] = set;
                m[by, bx] = set;
            }
        }

        private static void ApplyVersion(bool[,] m, int version, int size)
        {
            // 6 version bits + 12 BCH ECC, primitive 0x1F25
            int bch = version << 12;
            for (int i = 5; i >= 0; i--)
                if (((bch >> (i + 12)) & 1) == 1) bch ^= 0x1F25 << i;
            int code = (version << 12) | bch;
            for (int i = 0; i < 18; i++)
            {
                bool bit = ((code >> i) & 1) == 1;
                int y = i / 3;
                int x = i % 3;
                m[y, size - 11 + x] = bit;
                m[size - 11 + x, y] = bit;
            }
        }

        // ── BitBuilder helper ────────────────────────────────────────────────

        private sealed class BitBuilder
        {
            private readonly List<byte> _bytes = new List<byte>();
            private int _bitIdx = 0;
            public int Count => _bytes.Count * 8 + _bitIdx;

            public void Append(int value, int bitCount)
            {
                for (int i = bitCount - 1; i >= 0; i--)
                {
                    if (_bitIdx == 0) _bytes.Add(0);
                    int bit = (value >> i) & 1;
                    if (bit == 1) _bytes[_bytes.Count - 1] |= (byte)(1 << (7 - _bitIdx));
                    _bitIdx = (_bitIdx + 1) % 8;
                }
            }

            public byte[] ToBytes() => _bytes.ToArray();
        }
    }
}
