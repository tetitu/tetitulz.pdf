// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// The HT block encoder (ITU-T T.814 clause 7, the writer's side): one code-block's quantised
    /// coefficients as a single HT cleanup segment — MEL, VLC and MagSgn bit-streams over the quad
    /// scan of §7.2 — coding every magnitude bit, so the block needs one HT set and no refinement
    /// passes. The mirror of <see cref="JpxHtBlockDecoder"/>, which reads exactly what this writes.
    /// </summary>
    /// <remarks>
    /// <para><b>One cleanup pass, down to bit-plane 0.</b> The cleanup codes μ<sub>n</sub> = |q<sub>n</sub>|
    /// whole, so S<sub>blk</sub> is M<sub>b</sub> − 1 (§B.3, §7.6): the block's "zero bit-planes" in
    /// the packet header are M<sub>b</sub> − 1 whatever its magnitudes, and its pass count is one.
    /// SigProp and MagRef, which refine one plane below the cleanup's, have nothing left to code.</para>
    /// <para><b>Choices the standard leaves to the encoder</b>, made as the decoder's model needs and
    /// no more: the exponent bound U<sub>q</sub> is the larger of the quad's largest exponent and its
    /// predictor κ<sub>q</sub>; of the Annex C entries consistent with the quad, the one whose codeword
    /// costs least once the MagSgn bits its known-bit pattern saves are counted; the MEL coder's
    /// unfinished run is closed with a 1 (the decoder never asks for the zeros it implies); a stream's
    /// last byte is padded as <c>tools/htj2k-fixtures/make_segments.py</c> pads it, so the segments
    /// <c>JpxHtBlockDecoderTests</c> hard-codes are what this writes, byte for byte.</para>
    /// </remarks>
    internal static class JpxHtBlockEncoder
    {
        /// <summary>The largest suffix (MEL and VLC bytes) the segment's last two bytes can state (§7.1.1).</summary>
        internal const int MaxSuffixLength = 4079;

        /// <summary>
        /// The cleanup segment coding <paramref name="coefficients"/> down to <paramref name="plane"/>: each
        /// magnitude shifted right by it, the sign kept — what an HT set at that plane carries (§7.6). Null
        /// when nothing is left at that plane.
        /// </summary>
        internal static byte[] EncodeCleanupAtPlane(long[] coefficients, int width, int height, int plane)
        {
            if (plane == 0) return EncodeCleanup(coefficients, width, height, out _);
            var shifted = new long[coefficients.Length];
            for (int i = 0; i < shifted.Length; i++)
            {
                long q = coefficients[i];
                long mu = Math.Abs(q) >> plane;
                shifted[i] = q < 0 ? -mu : mu;
            }
            return EncodeCleanup(shifted, width, height, out _);
        }

        /// <summary>
        /// The squared error, in the units of the coefficients, left by a cleanup at <paramref name="plane"/>:
        /// nothing at plane 0, where the integer itself is decoded; above it, each magnitude against the
        /// midpoint (μ + ½)·2<sup>plane</sup> a decoder reconstructs it at, or against 0 when μ is 0.
        /// </summary>
        internal static double RemainingDistortion(long[] coefficients, int plane)
        {
            if (plane == 0) return 0;
            double sum = 0;
            foreach (long q in coefficients)
            {
                long m = Math.Abs(q);
                long mu = m >> plane;
                double reconstructed = mu == 0 ? 0 : (mu + 0.5) * (1L << plane);
                double e = m - reconstructed;
                sum += e * e;
            }
            return sum;
        }

        /// <summary>
        /// The cleanup segment for <paramref name="coefficients"/> (raster order, <paramref name="width"/> ×
        /// <paramref name="height"/>), or <c>null</c> when every coefficient is 0 — a block with no coding
        /// pass. <paramref name="distortion"/> is the squared magnitude the pass removes: all of it.
        /// </summary>
        internal static byte[] EncodeCleanup(long[] coefficients, int width, int height, out double distortion)
        {
            if (width <= 0 || height <= 0 || coefficients == null || coefficients.Length < (long)width * height)
                throw new ArgumentException("the block's coefficients do not fill its size");

            int qw = (width + 1) >> 1, qh = (height + 1) >> 1, pw = 2 * qw;
            int n = pw * 2 * qh;
            var sigma = new byte[n];
            var exponent = new byte[n];
            var value = new long[n];   // v_n = 2(μ_n − 1) + s_n (§7.3.8)
            bool any = false;
            distortion = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    long q = coefficients[y * width + x];
                    if (q == 0) continue;
                    long mu = Math.Abs(q);
                    if (mu > (1L << JpxHtBlockDecoder.MaxExponentBound) - 1)
                        throw new ArgumentOutOfRangeException(nameof(coefficients), "a magnitude past what an HT cleanup pass can carry");
                    int i = y * pw + x;
                    sigma[i] = 1;
                    value[i] = 2 * (mu - 1) + (q < 0 ? 1 : 0);
                    exponent[i] = (byte)BitLength(2 * mu - 1);
                    distortion += (double)mu * mu;
                    any = true;
                }
            if (!any) return null;

            var vlc = new BitList();
            var mel = new MelEncoder();
            var magsgn = new BitList();

            for (int qy = 0; qy < qh; qy++)
            {
                bool initial = qy == 0;
                byte[] table = initial ? JpxHtCodeTables.Table0 : JpxHtCodeTables.Table1;
                for (int qx = 0; qx < qw; qx += 2)
                {
                    bool second = qx + 1 < qw;
                    var q1 = Plan(qx, qy, initial, table, sigma, exponent, value, qw, pw);
                    var q2 = second ? Plan(qx + 1, qy, initial, table, sigma, exponent, value, qw, pw) : default;

                    SigEmb(q1, vlc, mel);
                    if (second) SigEmb(q2, vlc, mel);

                    bool off1 = q1.UOff != 0, off2 = second && q2.UOff != 0;
                    if (initial && off1 && off2)
                    {
                        // Both residuals non-zero in the first line-pair: one MEL symbol for the pair (§7.3.6).
                        if (q1.U > 2 && q2.U > 2)
                        {
                            mel.Add(1);
                            int a = q1.U - 2, b = q2.U - 2;
                            WritePrefix(vlc, a); WritePrefix(vlc, b);
                            WriteSuffix(vlc, a); WriteSuffix(vlc, b);
                            WriteExtension(vlc, a); WriteExtension(vlc, b);
                        }
                        else
                        {
                            mel.Add(0);
                            WritePrefix(vlc, q1.U);
                            if (PrefixOf(q1.U) > 2) vlc.Add(q2.U - 1);   // u2 is 1 or 2 here: one bit
                            else WritePrefix(vlc, q2.U);
                            WriteSuffix(vlc, q1.U); WriteSuffix(vlc, q2.U);
                            WriteExtension(vlc, q1.U); WriteExtension(vlc, q2.U);
                        }
                    }
                    else
                    {
                        if (off1) WritePrefix(vlc, q1.U);
                        if (off2) WritePrefix(vlc, q2.U);
                        if (off1) WriteSuffix(vlc, q1.U);
                        if (off2) WriteSuffix(vlc, q2.U);
                        if (off1) WriteExtension(vlc, q1.U);
                        if (off2) WriteExtension(vlc, q2.U);
                    }

                    MagSgn(q1, value, magsgn, pw);
                    if (second) MagSgn(q2, value, magsgn, pw);
                }
            }

            return Assemble(magsgn, mel.Finish(), vlc);
        }

        // ---------------------------------------------------------------- one quad

        /// <summary>What the writer has decided about one quad: where it is, its context, pattern, bound and table entry.</summary>
        private struct Quad
        {
            internal int Qx, Qy, Context, Rho, UOff, U, Bound, Known, KnownOne, Codeword, Length;
        }

        /// <summary>
        /// A quad's context (§7.3.5), significance pattern, predictor and bound (§7.3.7), and the Annex C
        /// entry for them. Every sample the context and the predictor read is causal, so the encoder's
        /// full knowledge of the block gives the values the decoder will have.
        /// </summary>
        private static Quad Plan(int qx, int qy, bool initial, byte[] table, byte[] sigma, byte[] exponent, long[] value, int qw, int pw)
        {
            var q = new Quad { Qx = qx, Qy = qy };
            int x = 2 * qx, y = 2 * qy;
            int S(int sx, int sy) => sigma[sy * pw + sx];

            if (initial)
                q.Context = qx == 0 ? 0 : (S(x - 2, y) | S(x - 2, y + 1)) + 2 * S(x - 1, y) + 4 * S(x - 1, y + 1);
            else
            {
                int nw = 0, west = 0, southwest = 0, nf = 0;
                if (qx > 0) { nw = S(x - 1, y - 1); west = S(x - 1, y); southwest = S(x - 1, y + 1); }
                if (qx + 1 < qw) nf = S(x + 2, y - 1);
                q.Context = (nw | S(x, y - 1)) + 2 * (west | southwest) + 4 * (S(x + 1, y - 1) | nf);
            }

            int maxExponent = 0;
            for (int j = 0; j < 4; j++)
            {
                int i = SampleIndex(qx, qy, j, pw);
                if (sigma[i] == 0) continue;
                q.Rho |= 1 << j;
                maxExponent = Math.Max(maxExponent, exponent[i]);
            }
            if (q.Rho == 0)
            {
                if (q.Context != 0) Choose(ref q, table, value, pw);   // an insignificant quad in a non-zero context still has a codeword
                return q;
            }

            int kappa = 1;
            if (!initial && (q.Rho & (q.Rho - 1)) != 0)
            {
                int ay = y - 1;
                int max = Math.Max(exponent[ay * pw + x], exponent[ay * pw + x + 1]);
                if (qx > 0) max = Math.Max(max, exponent[ay * pw + x - 1]);
                if (qx + 1 < qw) max = Math.Max(max, exponent[ay * pw + x + 2]);
                kappa = Math.Max(1, max - 1);
            }
            q.Bound = Math.Max(maxExponent, kappa);
            q.UOff = q.Bound > kappa ? 1 : 0;
            q.U = q.Bound - kappa;
            Choose(ref q, table, value, pw);
            return q;
        }

        /// <summary>
        /// The Annex C entry for the quad's context, pattern and residual offset whose known-bit pattern
        /// is true of the quad — each known sample's bit U<sub>q</sub> − 1 is what ϵ<sup>1</sup> says —
        /// and costs least: its codeword's length less the MagSgn bits the known bits save.
        /// </summary>
        private static void Choose(ref Quad q, byte[] table, long[] value, int pw)
        {
            int best = int.MaxValue;
            bool found = false;
            EntryIndex index = table == JpxHtCodeTables.Table0 ? Index0 : table == JpxHtCodeTables.Table1 ? Index1 : new EntryIndex(table);
            int key = EntryIndex.Key(q.Context, q.Rho, q.UOff);
            for (int e = index.Start[key]; e < index.Start[key + 1]; e++)
            {
                int i = index.Entries[e];
                int known = table[i + 3], knownOne = table[i + 4];
                bool consistent = true;
                for (int j = 0; j < 4 && consistent; j++)
                {
                    if (((known >> j) & 1) == 0) continue;
                    long v = value[SampleIndex(q.Qx, q.Qy, j, pw)];
                    int bit = (int)((v >> (q.Bound - 1)) & 1);
                    consistent = bit == ((knownOne >> j) & 1);
                }
                if (!consistent) continue;
                int cost = table[i + 6] - PopCount(known);
                if (cost >= best) continue;
                best = cost;
                found = true;
                q.Known = known;
                q.KnownOne = knownOne;
                q.Codeword = table[i + 5];
                q.Length = table[i + 6];
            }
            if (!found)
                throw new InvalidOperationException($"no CxtVLC entry for context {q.Context}, pattern 0x{q.Rho:X}, offset {q.UOff} (T.814 Annex C)");
        }

        private static readonly EntryIndex Index0 = new EntryIndex(JpxHtCodeTables.Table0), Index1 = new EntryIndex(JpxHtCodeTables.Table1);

        /// <summary>
        /// An Annex C table's entries grouped by (context, pattern, residual offset) — the three fields a quad
        /// fixes — each group in the table's own order, so <see cref="Choose"/> reads the entries a scan of
        /// the whole table would accept, and settles a tie as that scan did.
        /// </summary>
        internal sealed class EntryIndex
        {
            /// <summary>c<sub>q</sub> has three bits, ρ<sub>q</sub> four, u<sub>off</sub> one (§7.3.5).</summary>
            internal static int Key(int context, int rho, int uOff) => ((context & 7) << 5) | ((rho & 15) << 1) | (uOff & 1);

            internal EntryIndex(byte[] table)
            {
                int count = table.Length / JpxHtCodeTables.FieldsPerEntry;
                var keys = new int[count];
                for (int n = 0; n < count; n++)
                {
                    int i = n * JpxHtCodeTables.FieldsPerEntry;
                    keys[n] = Key(table[i], table[i + 1], table[i + 2]);
                    Start[keys[n] + 1]++;
                }
                for (int k = 0; k < 256; k++) Start[k + 1] += Start[k];
                Entries = new int[count];
                var next = (int[])Start.Clone();
                for (int n = 0; n < count; n++) Entries[next[keys[n]]++] = n * JpxHtCodeTables.FieldsPerEntry;
            }

            /// <summary>Where each key's entries begin in <see cref="Entries"/>; the key after it, where they end.</summary>
            internal int[] Start { get; } = new int[257];

            /// <summary>Each entry's offset in the table, grouped by key.</summary>
            internal int[] Entries { get; }
        }

        /// <summary>
        /// The writer's side of decodeSigEMB (§7.3.5): a MEL symbol for an all-zero context, then the
        /// codeword — none for a quad insignificant there, which <see cref="Plan"/> gives no entry.
        /// </summary>
        private static void SigEmb(Quad q, BitList vlc, MelEncoder mel)
        {
            if (q.Context == 0) mel.Add(q.Rho == 0 ? 0 : 1);
            for (int k = 0; k < q.Length; k++) vlc.Add((q.Codeword >> k) & 1);
        }

        /// <summary>Each significant sample's MagSgn bits (§7.3.8): the low m = U<sub>q</sub> − k bits of v<sub>n</sub>, least significant first.</summary>
        private static void MagSgn(Quad q, long[] value, BitList magsgn, int pw)
        {
            for (int j = 0; j < 4; j++)
            {
                if (((q.Rho >> j) & 1) == 0) continue;
                int m = q.Bound - ((q.Known >> j) & 1);
                long v = value[SampleIndex(q.Qx, q.Qy, j, pw)];
                for (int i = 0; i < m; i++) magsgn.Add((int)((v >> i) & 1));
            }
        }

        private static int SampleIndex(int qx, int qy, int j, int pw) => (2 * qy + (j & 1)) * pw + 2 * qx + (j >> 1);

        // ---------------------------------------------------------------- unsigned residuals (§7.3.6)

        /// <summary>The prefix value p the decoder's UPrefix returns for residual <paramref name="u"/>: 1, 2, 3 (for 3–4) or 5 (for 5 and up).</summary>
        private static int PrefixOf(int u) => u <= 2 ? u : u <= 4 ? 3 : 5;

        private static void WritePrefix(BitList vlc, int u)
        {
            switch (PrefixOf(u))
            {
                case 1: vlc.Add(1); break;
                case 2: vlc.Add(0); vlc.Add(1); break;
                case 3: vlc.Add(0); vlc.Add(0); vlc.Add(1); break;
                default: vlc.Add(0); vlc.Add(0); vlc.Add(0); break;
            }
        }

        /// <summary>The suffix: one bit (u − 3) after prefix 3, five bits after prefix 5 — u − 5, or 28 to 31 when an extension follows.</summary>
        private static void WriteSuffix(BitList vlc, int u)
        {
            int p = PrefixOf(u);
            if (p < 3) return;
            if (p == 3) { vlc.Add(u - 3); return; }
            int s = u - 5 < 28 ? u - 5 : 28 + ((u - 33) & 3);
            for (int i = 0; i < 5; i++) vlc.Add((s >> i) & 1);
        }

        /// <summary>The extension, four bits, when the suffix reached 28: u = 33 + (suffix − 28) + 4e.</summary>
        private static void WriteExtension(BitList vlc, int u)
        {
            if (PrefixOf(u) < 5 || u - 5 < 28) return;
            int e = (u - 33) >> 2;
            if (e > 15) throw new InvalidOperationException($"an unsigned residual of {u} is past what §7.3.6 can code");
            for (int i = 0; i < 4; i++) vlc.Add((e >> i) & 1);
        }

        // ---------------------------------------------------------------- the bit-streams (§7.1)

        /// <summary>
        /// The segment: MagSgn bytes, then the MEL bytes forward and the VLC bytes backward from the end,
        /// whose last two bytes carry Scup (§7.1.1).
        /// </summary>
        private static byte[] Assemble(BitList magsgnBits, List<int> melBits, BitList vlcBits)
        {
            List<byte> magsgn = MagSgnBytes(magsgnBits);
            List<byte> mel = MelBytes(melBits);
            byte nibble = VlcBytes(vlcBits, out List<byte> vlcRest);   // vlcRest[0] is D[Lcup − 3]

            int scup = mel.Count + vlcRest.Count + 2;
            if (scup > MaxSuffixLength)
                throw new InvalidOperationException($"the block's MEL and VLC streams need {scup} bytes; a cleanup segment states at most {MaxSuffixLength} (T.814 §7.1.1)");

            var segment = new byte[magsgn.Count + scup];
            int at = 0;
            foreach (byte b in magsgn) segment[at++] = b;
            foreach (byte b in mel) segment[at++] = b;
            for (int i = vlcRest.Count - 1; i >= 0; i--) segment[at++] = vlcRest[i];
            segment[at++] = (byte)((nibble << 4) | (scup & 0x0F));
            segment[at] = (byte)(scup >> 4);
            return segment;
        }

        /// <summary>
        /// The MagSgn bytes importMagSgnBit reads (§7.1.2): least significant bit first, seven bits after a
        /// 0xFF (its top bit the stuffing 0), the last byte padded with zeros the decoder never reads.
        /// </summary>
        private static List<byte> MagSgnBytes(BitList bits)
        {
            var bytes = new List<byte>();
            int i = 0, last = 0;
            while (i < bits.Count)
            {
                int capacity = last == 0xFF ? 7 : 8;
                int b = 0;
                for (int k = 0; k < capacity && i < bits.Count; k++, i++) b |= bits[i] << k;
                bytes.Add((byte)b);
                last = b;
            }
            return bytes;
        }

        /// <summary>The MEL bytes importMELBit reads (§7.1.3): most significant bit first, seven bits after a 0xFF, padded with ones.</summary>
        private static List<byte> MelBytes(List<int> bits)
        {
            var bytes = new List<byte>();
            int i = 0, last = 0;
            while (i < bits.Count)
            {
                int capacity = last == 0xFF ? 7 : 8;
                int b = 0;
                for (int k = 0; k < capacity; k++) b = (b << 1) | (i < bits.Count ? bits[i++] : 1);
                bytes.Add((byte)b);
                last = b;
            }
            return bytes;
        }

        /// <summary>
        /// The VLC bytes importVLCBit reads backwards (§7.1.4). The first bits go in the upper nibble of
        /// D[Lcup − 2] — three of them when their low three are all ones, with bit 7 the stuffing 0 — then a
        /// byte at a time below it, least significant bit first, with seven bits in a byte when the byte
        /// above exceeds 0x8F and these seven are all ones. Returns that nibble; <paramref name="rest"/> holds
        /// D[Lcup − 3], D[Lcup − 4], ….
        /// </summary>
        private static byte VlcBytes(BitList bits, out List<byte> rest)
        {
            rest = new List<byte>();
            int i = 0;
            int nibble = 0;
            for (int k = 0; k < 4; k++) nibble |= (i < bits.Count ? bits[i++] : 0) << k;
            // Three ones in its low bits make the nibble a three-bit one (the decoder's (tmp & 7) == 7):
            // only those three are carried, and bit 7 of the byte is the stuffing 0.
            if ((nibble & 0x7) == 0x7) { nibble = 0x7; i = 3; }
            int last = (nibble << 4) | 0x0F;   // the decoder's modDcup of D[Lcup − 2]
            while (i < bits.Count)
            {
                bool sevenOnes = last > 0x8F && i + 7 <= bits.Count && AllOnes(bits, i, 7);
                // A byte above 0x8F makes seven ones here a 7-bit byte; padding must not create them either.
                int b = 0;
                if (sevenOnes)
                {
                    b = 0x7F;
                    i += 7;
                }
                else
                {
                    int k = 0;
                    for (; k < 8 && i < bits.Count; k++, i++) b |= bits[i] << k;
                    if (last > 0x8F && (b & 0x7F) == 0x7F && k < 8)
                    {
                        // Fewer than eight data bits left and they, with padding, read as a 7-bit byte:
                        // the decoder takes the low seven, which are ours; bit 7 is the stuffing 0.
                        b &= 0x7F;
                    }
                }
                rest.Add((byte)b);
                last = b;
            }
            return (byte)nibble;
        }

        private static bool AllOnes(BitList bits, int from, int count)
        {
            for (int k = 0; k < count; k++) if (bits[from + k] == 0) return false;
            return true;
        }

        private static int BitLength(long v)
        {
            int n = 0;
            while (v > 0) { v >>= 1; n++; }
            return n;
        }

        private static int PopCount(int v)
        {
            int n = 0;
            while (v != 0) { n += v & 1; v >>= 1; }
            return n;
        }

        /// <summary>A growable list of bits.</summary>
        private sealed class BitList
        {
            private readonly List<byte> _bits = new List<byte>();
            internal int Count => _bits.Count;
            internal int this[int i] => _bits[i];
            internal void Add(int bit) => _bits.Add((byte)(bit & 1));
        }

        /// <summary>
        /// The MEL coder decodeMELSym inverts (§7.3.3, MEL_E of Table 2): a run of 2<sup>E</sup> zeros as a
        /// 1, a 1 as a 0 followed by the run so far in E bits, most significant first.
        /// </summary>
        private sealed class MelEncoder
        {
            private readonly List<int> _bits = new List<int>();
            private int _k, _run;

            internal void Add(int symbol)
            {
                int e = JpxHtCodeTables.MelExponent[_k];
                if (symbol == 0)
                {
                    if (++_run == 1 << e)
                    {
                        _bits.Add(1);
                        _run = 0;
                        _k = Math.Min(12, _k + 1);
                    }
                    return;
                }
                _bits.Add(0);
                for (int i = e - 1; i >= 0; i--) _bits.Add((_run >> i) & 1);
                _run = 0;
                _k = Math.Max(0, _k - 1);
            }

            /// <summary>The bits, an unfinished run closed with a 1: the decoder never asks for the zeros past the block.</summary>
            internal List<int> Finish()
            {
                if (_run > 0) { _bits.Add(1); _run = 0; }
                return _bits;
            }
        }
    }
}
