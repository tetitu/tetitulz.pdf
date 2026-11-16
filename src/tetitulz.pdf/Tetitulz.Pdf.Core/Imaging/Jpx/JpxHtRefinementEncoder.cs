// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>One HT set as the encoder writes it (ITU-T T.814 §B.1): its cleanup and refinement segments, and the error it leaves.</summary>
    internal sealed class JpxHtSet
    {
        internal JpxHtSet(int plane, byte[] cleanup, byte[] refinement, int sigPropLength, int passes, double remaining)
        {
            Plane = plane;
            Cleanup = cleanup;
            Refinement = refinement ?? Array.Empty<byte>();
            SigPropLength = sigPropLength;
            Passes = passes;
            Remaining = remaining;
        }

        /// <summary>The cleanup pass's least significant plane; the refinement passes code the plane below it.</summary>
        internal int Plane { get; }

        /// <summary>The cleanup segment.</summary>
        internal byte[] Cleanup { get; }

        /// <summary>The refinement segment — SigProp bytes forward, then the MagRef bytes reversed (Annex F) — empty for one pass.</summary>
        internal byte[] Refinement { get; }

        /// <summary>
        /// The refinement segment's first bytes that hold every SigProp bit: a decoder given only these reads
        /// the SigProp pass whole, so a layer may end the set there — the prefix of the segment a codeword
        /// segment ending at the SigProp pass carries.
        /// </summary>
        internal int SigPropLength { get; }

        /// <summary>The coding passes the set carries: 1 to 3.</summary>
        internal int Passes { get; }

        /// <summary>The squared error left, in the coefficients' units, against the decoder's reconstruction of what the set carries.</summary>
        internal double Remaining { get; }

        internal int Length => Cleanup.Length + Refinement.Length;

        /// <summary>The segments end to end: what a code-block's codeword segments carry, the cleanup's first.</summary>
        internal byte[] Data()
        {
            var data = new byte[Length];
            Cleanup.CopyTo(data, 0);
            Refinement.CopyTo(data, Cleanup.Length);
            return data;
        }
    }

    /// <summary>
    /// The HT SigProp and MagRef passes on the writer's side (ITU-T T.814 §7.4, §7.5): after a cleanup at
    /// plane p, SigProp codes bit p − 1 of each insignificant sample with a significant neighbour — or a
    /// scan-causal one SigProp made significant — and the sign of each it makes significant, a column-group
    /// of a four-line stripe at a time; MagRef codes bit p − 1 of every sample the cleanup made significant.
    /// The bits are packed and the refinement segment terminated by Annex F's procedures (emitSPBit,
    /// emitMRBit, termSPPacker, termSPandMRPackers), which keep a 0xFF from preceding a byte above 0x8F and
    /// from ending the segment. The mirror of <see cref="JpxHtBlockDecoder"/>'s SigProp and MagRef.
    /// </summary>
    internal static class JpxHtRefinementEncoder
    {
        /// <summary>
        /// The sets a cleanup at <paramref name="plane"/> can head — the cleanup alone, then (above plane 0)
        /// with SigProp and with SigProp and MagRef, each left out when its refinement segment would be empty,
        /// the decoder then counting one pass (§B.3) — sharing one cleanup and one scan. None when nothing is
        /// significant at the plane.
        /// </summary>
        internal static JpxHtSet[] EncodeSets(long[] coefficients, int width, int height, int plane, bool verticallyCausal)
        {
            byte[] cleanup = JpxHtBlockEncoder.EncodeCleanupAtPlane(coefficients, width, height, plane);
            if (cleanup == null) return Array.Empty<JpxHtSet>();
            var sets = new List<JpxHtSet>(3) { new JpxHtSet(plane, cleanup, null, 0, 1, Remaining(coefficients, plane, 1, null)) };
            if (plane == 0) return sets.ToArray();

            Refine(coefficients, width, height, plane, verticallyCausal, out byte[] candidate, out SigPropPacker sp, out MagRefPacker mr);
            byte[] sigProp = sp.Terminated();
            if (sigProp.Length > 0) sets.Add(new JpxHtSet(plane, cleanup, sigProp, sigProp.Length, 2, Remaining(coefficients, plane, 2, candidate)));
            byte[] both = Join(sp, mr, out int sigPropLength);
            if (both.Length > 0) sets.Add(new JpxHtSet(plane, cleanup, both, sigPropLength, 3, Remaining(coefficients, plane, 3, candidate)));
            return sets.ToArray();
        }

        /// <summary>
        /// The set at <paramref name="plane"/> with exactly <paramref name="passes"/> passes (1 to 3; 2 and 3
        /// need a plane above 0), its refinement segment empty if that is what the passes come to; null when
        /// nothing is significant at the plane.
        /// </summary>
        internal static JpxHtSet EncodeSet(long[] coefficients, int width, int height, int plane, int passes, bool verticallyCausal)
        {
            if (passes < 1 || passes > 3) throw new ArgumentOutOfRangeException(nameof(passes), "an HT set has 1 to 3 coding passes");
            if (passes > 1 && plane < 1) throw new ArgumentOutOfRangeException(nameof(plane), "the refinement passes code the plane below the cleanup's, and there is none below 0");
            byte[] cleanup = JpxHtBlockEncoder.EncodeCleanupAtPlane(coefficients, width, height, plane);
            if (cleanup == null) return null;
            if (passes == 1) return new JpxHtSet(plane, cleanup, null, 0, 1, Remaining(coefficients, plane, 1, null));
            Refine(coefficients, width, height, plane, verticallyCausal, out byte[] candidate, out SigPropPacker sp, out MagRefPacker mr);
            byte[] refinement;
            int sigPropLength;
            if (passes == 2)
            {
                refinement = sp.Terminated();
                sigPropLength = refinement.Length;
            }
            else
            {
                refinement = Join(sp, mr, out sigPropLength);
            }
            return new JpxHtSet(plane, cleanup, refinement, sigPropLength, passes, Remaining(coefficients, plane, passes, candidate));
        }

        /// <summary>Both refinement passes' bits after a cleanup at <paramref name="plane"/>, packed; <paramref name="candidate"/> marks the samples SigProp coded.</summary>
        private static void Refine(long[] q, int w, int h, int plane, bool verticallyCausal,
            out byte[] candidate, out SigPropPacker sp, out MagRefPacker mr)
        {
            int n = w * h;
            var sigma = new byte[n];
            for (int i = 0; i < n; i++) sigma[i] = (byte)((Math.Abs(q[i]) >> plane) != 0 ? 1 : 0);
            candidate = new byte[n];
            sp = new SigPropPacker();
            SigProp(q, w, h, plane, sigma, candidate, verticallyCausal, sp);
            mr = new MagRefPacker();
            MagRef(q, w, h, plane, sigma, mr);
        }

        /// <summary>
        /// The SigProp pass (§7.4) over the stripe scan: in each column-group of a stripe, a magnitude bit for
        /// every insignificant sample with a significant sample, or one SigProp has set, in its propagation
        /// neighbourhood — no neighbour not yet visited has been set, so asking every neighbour asks the
        /// scan-causal ones — then a sign (1 for negative) for each sample a 1 made significant. Vertically
        /// causal, the neighbourhood stops at the stripe's last line.
        /// </summary>
        private static void SigProp(long[] q, int w, int h, int plane, byte[] sigma, byte[] candidate, bool verticallyCausal, SigPropPacker sp)
        {
            var set = new byte[w * h];
            for (int y0 = 0; y0 < h; y0 += 4)
            {
                int y1 = Math.Min(h, y0 + 4);
                int below = verticallyCausal ? y1 : h;
                for (int gx = 0; gx < w; gx += 4)
                {
                    int gx1 = Math.Min(w, gx + 4);
                    for (int x = gx; x < gx1; x++)
                        for (int y = y0; y < y1; y++)
                        {
                            int i = y * w + x;
                            if (sigma[i] != 0 || !HasSignificantNeighbour(sigma, set, w, x, y, below)) continue;
                            candidate[i] = 1;
                            int bit = (int)((Math.Abs(q[i]) >> (plane - 1)) & 1);
                            set[i] = (byte)bit;
                            sp.Emit(bit);
                        }
                    for (int x = gx; x < gx1; x++)
                        for (int y = y0; y < y1; y++)
                        {
                            int i = y * w + x;
                            if (set[i] != 0) sp.Emit(q[i] < 0 ? 1 : 0);
                        }
                }
            }
        }

        private static bool HasSignificantNeighbour(byte[] sigma, byte[] set, int w, int x, int y, int below)
        {
            int xa = Math.Max(0, x - 1), xb = Math.Min(w - 1, x + 1);
            int ya = Math.Max(0, y - 1), yb = Math.Min(below - 1, y + 1);
            for (int ny = ya; ny <= yb; ny++)
                for (int nx = xa; nx <= xb; nx++)
                {
                    if (nx == x && ny == y) continue;
                    int i = ny * w + nx;
                    if ((sigma[i] | set[i]) != 0) return true;
                }
            return false;
        }

        /// <summary>The MagRef pass (§7.5): bit <paramref name="plane"/> − 1 of every sample the cleanup made significant, in the stripe scan.</summary>
        private static void MagRef(long[] q, int w, int h, int plane, byte[] sigma, MagRefPacker mr)
        {
            for (int y0 = 0; y0 < h; y0 += 4)
            {
                int y1 = Math.Min(h, y0 + 4);
                for (int x = 0; x < w; x++)
                    for (int y = y0; y < y1; y++)
                    {
                        int i = y * w + x;
                        if (sigma[i] != 0) mr.Emit((int)((Math.Abs(q[i]) >> (plane - 1)) & 1));
                    }
            }
        }

        /// <summary>
        /// The squared error a set leaves, in the coefficients' units: each magnitude against the midpoint of the
        /// interval its known bits leave — bits down to <paramref name="plane"/>, or down to the plane below for a
        /// sample MagRef refined (3 passes) or SigProp coded (2 or 3) — with nothing left where every bit down to
        /// plane 0 is known (<see cref="JpxHtBlockEncoder.RemainingDistortion"/>'s model).
        /// </summary>
        internal static double Remaining(long[] q, int plane, int passes, byte[] candidate)
        {
            double sum = 0;
            for (int i = 0; i < q.Length; i++)
            {
                long m = Math.Abs(q[i]);
                bool refined = passes >= 2 && ((m >> plane) != 0 ? passes == 3 : candidate[i] != 0);
                int known = refined ? plane - 1 : plane;
                if (known == 0) continue;
                long mu = m >> known;
                double reconstructed = mu == 0 ? 0 : (mu + 0.5) * Math.Pow(2, known);
                double e = m - reconstructed;
                sum += e * e;
            }
            return sum;
        }

        /// <summary>
        /// termSPandMRPackers (Annex F): the SigProp bytes, the unfinished SigProp and MagRef bytes fused into
        /// one when their bits allow (or both written), then the MagRef bytes reversed. The packers are left
        /// as they were. <paramref name="sigPropLength"/> counts the bytes up to the last that holds a SigProp bit.
        /// </summary>
        internal static byte[] Join(SigPropPacker sp, MagRefPacker mr, out int sigPropLength)
        {
            var spBytes = new List<byte>(sp.Bytes);
            var mrBytes = new List<byte>(mr.Bytes);
            int spMask = 0xFF >> (8 - sp.Bits);
            spMask |= (1 << sp.Max) & 0x80;   // the stuffing bit after a 0xFF
            int mrMask = 0xFF >> (8 - mr.Bits);
            if ((spMask | mrMask) != 0)
            {
                int fuse = sp.Tmp | mr.Tmp;
                if ((((fuse ^ sp.Tmp) & spMask) | ((fuse ^ mr.Tmp) & mrMask)) == 0)
                {
                    spBytes.Add((byte)fuse);
                }
                else
                {
                    spBytes.Add((byte)sp.Tmp);
                    mrBytes.Add((byte)mr.Tmp);
                }
            }
            sigPropLength = spBytes.Count;
            var segment = new byte[spBytes.Count + mrBytes.Count];
            spBytes.CopyTo(segment, 0);
            for (int i = 0; i < mrBytes.Count; i++) segment[segment.Length - 1 - i] = mrBytes[i];
            return segment;
        }

        /// <summary>initSPPacker and emitSPBit (Annex F): least significant bit first, seven bits in a byte after a 0xFF.</summary>
        internal sealed class SigPropPacker
        {
            internal List<byte> Bytes { get; } = new List<byte>();
            internal int Bits { get; private set; }
            internal int Max { get; private set; } = 8;
            internal int Tmp { get; private set; }

            internal void Emit(int bit)
            {
                Tmp |= (bit & 1) << Bits;
                Bits++;
                if (Bits == Max)
                {
                    Bytes.Add((byte)Tmp);
                    Max = Tmp == 0xFF ? 7 : 8;
                    Tmp = 0;
                    Bits = 0;
                }
            }

            /// <summary>termSPPacker, on a copy: the unfinished byte if it holds a 1, and a 0x00 after a final 0xFF.</summary>
            internal byte[] Terminated()
            {
                var bytes = new List<byte>(Bytes);
                int max = Max;
                if (Tmp != 0)
                {
                    bytes.Add((byte)Tmp);
                    max = Tmp == 0xFF ? 7 : 8;
                }
                if (max == 7) bytes.Add(0x00);
                return bytes.ToArray();
            }
        }

        /// <summary>initMRPacker and emitMRBit (Annex F): least significant bit first; after a byte above 0x8F, seven ones make a byte, its top bit the stuffing 0.</summary>
        internal sealed class MagRefPacker
        {
            private int _last = 0xFF;

            internal List<byte> Bytes { get; } = new List<byte>();
            internal int Bits { get; private set; }
            internal int Tmp { get; private set; }

            internal void Emit(int bit)
            {
                Tmp |= (bit & 1) << Bits;
                Bits++;
                if (_last > 0x8F && Tmp == 0x7F) Bits++;
                if (Bits == 8)
                {
                    Bytes.Add((byte)Tmp);
                    _last = Tmp;
                    Tmp = 0;
                    Bits = 0;
                }
            }
        }
    }
}
