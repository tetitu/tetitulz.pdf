// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// What tier 1 produced for one code-block: the codeword segment, the planes and passes it
    /// codes, and per coding pass the prefix of the segment that decodes it and the distortion it
    /// removes — what a rate allocator ranks passes by and a layer carries.
    /// </summary>
    internal sealed class JpxBlockCode
    {
        internal JpxBlockCode(byte[] data, int planes, int passes, int[] lengths, double[] distortion)
        {
            Data = data;
            Planes = planes;
            Passes = passes;
            Lengths = lengths;
            Distortion = distortion;
        }

        /// <summary>The single codeword segment (C.2.9 termination); empty for a block of zeros.</summary>
        internal byte[] Data { get; }

        /// <summary>The magnitude bit-planes coded — the bits of the largest magnitude; 0 for a block of zeros.</summary>
        internal int Planes { get; }

        /// <summary>3 · <see cref="Planes"/> − 2, or 0.</summary>
        internal int Passes { get; }

        /// <summary>
        /// Per pass, cumulative: the shortest prefix of <see cref="Data"/> from which a decoder
        /// padding with <c>0xFF</c> reproduces passes 0 to k exactly (<see cref="MqEncoder.PrefixLengths"/>).
        /// Non-decreasing; the last may fall short of <see cref="Data"/>'s length, since the
        /// termination's final bytes are ones the padding supplies.
        /// </summary>
        internal int[] Lengths { get; }

        /// <summary>
        /// Per pass, cumulative: the squared error, summed over the block in the units of the
        /// integers tier 1 was given, that passes 0 to k remove — each sample counted against the
        /// value a decoder reconstructs from the planes so far (E-6 with r = ½ below the last
        /// decoded plane; the integer itself once every plane is decoded). Unweighted: the band's
        /// step and synthesis gain are the caller's.
        /// </summary>
        internal double[] Distortion { get; }
    }

    /// <summary>
    /// Tier 1's encoder (T.800 Annex D): codes one code-block's quantised coefficients — the cleanup
    /// pass of the most significant non-zero bit-plane, then significance propagation, magnitude
    /// refinement and cleanup for each plane below — in the stripe scan of §D.1, over the shared
    /// context model (<see cref="JpxBlockContexts"/>) and the MQ coder (Annex C), as one codeword
    /// segment terminated by C.2.9's FLUSH. No code-block style flag is used but vertical causality
    /// (§D.7) when asked for, so every pass is arithmetic-coded and the segment runs to the last pass.
    /// The mirror of <see cref="JpxBlockDecoder"/>.
    /// </summary>
    internal static class JpxBlockEncoder
    {
        /// <summary>
        /// Encodes <paramref name="coefficients"/> (sign-magnitude integers, raster order,
        /// <paramref name="width"/> by <paramref name="height"/>) of a band with
        /// <paramref name="orientation"/> (0 LL, 1 HL, 2 LH, 3 HH). <paramref name="planes"/> is the
        /// number of magnitude bit-planes coded — the bits of the largest magnitude — and
        /// <paramref name="passes"/> is 3·planes − 2. A block of zeros has no planes, no passes and no bytes.
        /// </summary>
        internal static byte[] Encode(int[] coefficients, int width, int height, int orientation, out int planes, out int passes)
        {
            JpxBlockCode code = Encode(coefficients, width, height, orientation);
            planes = code.Planes;
            passes = code.Passes;
            return code.Data;
        }

        /// <summary>As the other overload, with each pass's prefix length and distortion reduction beside the bytes.</summary>
        internal static JpxBlockCode Encode(int[] coefficients, int width, int height, int orientation)
        {
            if (coefficients == null) throw new ArgumentNullException(nameof(coefficients));
            foreach (int c in coefficients)
                if (c == int.MinValue) throw new ArgumentOutOfRangeException(nameof(coefficients), "a coefficient's magnitude must fit a positive integer");
            return Encode(JpxForwardComponentTransform.Widen(coefficients), width, height, orientation);
        }

        /// <summary>The block encoder over <c>long</c> coefficients — up to <see cref="JpxBlockDecoder.MaxBitPlanes"/> magnitude bit-planes, which T.800's 38-bit samples need.</summary>
        internal static JpxBlockCode Encode(long[] coefficients, int width, int height, int orientation, bool verticallyCausal = false)
        {
            if (coefficients == null) throw new ArgumentNullException(nameof(coefficients));
            if (width < 1 || height < 1 || coefficients.Length != width * height)
                throw new ArgumentException("the coefficients must fill a code-block of at least one sample", nameof(coefficients));

            long max = 0;
            foreach (long c in coefficients)
            {
                if (c == long.MinValue) throw new ArgumentOutOfRangeException(nameof(coefficients), "a coefficient's magnitude must fit a positive integer");
                long m = Math.Abs(c);
                if (m > max) max = m;
            }
            int planes = 0;
            while ((max >> planes) != 0) planes++;
            if (planes == 0) return new JpxBlockCode(new byte[0], 0, 0, new int[0], new double[0]);
            if (planes > JpxBlockDecoder.MaxBitPlanes)
                throw new ArgumentOutOfRangeException(nameof(coefficients), $"a coefficient needs {planes} magnitude bit-planes; a decoder holds at most {JpxBlockDecoder.MaxBitPlanes}");

            var coder = new MqEncoder(JpxBlockContexts.ContextCount);
            JpxBlockContexts.ResetStates(coder);
            var t1 = new Passes(coefficients, width, height, orientation, verticallyCausal, coder);
            int passes = 3 * planes - 2;
            var marks = new List<MqEncoder.Checkpoint>(passes);
            var distortion = new double[passes];
            for (int plane = planes - 1; plane >= 0; plane--)
            {
                if (plane < planes - 1)
                {
                    t1.SignificancePass(plane);
                    marks.Add(coder.Mark());
                    distortion[marks.Count - 1] = t1.DistortionRemoved;
                    t1.RefinementPass(plane);
                    marks.Add(coder.Mark());
                    distortion[marks.Count - 1] = t1.DistortionRemoved;
                }
                t1.CleanupPass(plane);
                marks.Add(coder.Mark());
                distortion[marks.Count - 1] = t1.DistortionRemoved;
            }
            byte[] data = coder.FinishCodewordSegment();
            return new JpxBlockCode(data, planes, passes, MqEncoder.PrefixLengths(marks, data), distortion);
        }

        /// <summary>The three passes over the shared context model, reading each sample's magnitude bits.</summary>
        private sealed class Passes
        {
            private readonly int _w, _h;
            private readonly JpxBlockContexts _cx;
            private readonly long[] _mag;       // magnitudes, at the context model's bordered indices
            private readonly bool[] _negative;
            private readonly MqEncoder _coder;

            /// <summary>The squared error removed so far, summed over the samples coded (see <see cref="JpxBlockCode.Distortion"/>).</summary>
            internal double DistortionRemoved { get; private set; }

            internal Passes(long[] coefficients, int w, int h, int orientation, bool causal, MqEncoder coder)
            {
                _w = w;
                _h = h;
                _cx = new JpxBlockContexts(w, h, orientation, causal);
                _mag = new long[(w + 2) * (h + 2)];
                _negative = new bool[_mag.Length];
                _coder = coder;
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        long c = coefficients[y * w + x];
                        int i = _cx.Index(x, y);
                        _mag[i] = Math.Abs(c);
                        _negative[i] = c < 0;
                    }
            }

            private int Bit(int i, int plane) => (int)((_mag[i] >> plane) & 1);

            /// <summary>
            /// What a decoder reconstructs for magnitude <paramref name="m"/> with the planes down to
            /// <paramref name="plane"/> decoded: 0 while insignificant, else the decoded bits with the
            /// half of E-6 (r = ½) below them — the integer itself at plane 0, where the half is dropped.
            /// </summary>
            private static double Reconstruction(long m, int plane)
            {
                long decoded = (m >> plane) << plane;
                return decoded == 0 ? 0 : plane == 0 ? m : decoded + (1L << (plane - 1));
            }

            private static double Error(long m, int plane)
            {
                double e = m - Reconstruction(m, plane);
                return e * e;
            }

            /// <summary>The sample at <paramref name="i"/> became significant at <paramref name="plane"/>: its error was its whole magnitude squared.</summary>
            private void BecameSignificant(int i, int plane)
            {
                long m = _mag[i];
                DistortionRemoved += (double)m * m - Error(m, plane);
            }

            /// <summary>§D.3.1: insignificant samples with a non-zero context; each is marked visited whether or not it becomes significant.</summary>
            internal void SignificancePass(int plane)
            {
                for (int y0 = 0; y0 < _h; y0 += 4)
                    for (int x = 0; x < _w; x++)
                        for (int y = y0, yEnd = Math.Min(y0 + 4, _h); y < yEnd; y++)
                        {
                            int i = _cx.Index(x, y);
                            if (_cx.IsSignificant(i)) continue;
                            int context = _cx.ZeroCodingContext(i, y);
                            if (context == 0) continue;
                            int bit = Bit(i, plane);
                            _coder.Encode(context, bit);
                            if (bit == 1)
                            {
                                CodeSign(i, y);
                                BecameSignificant(i, plane);
                            }
                            _cx.SetVisited(i);
                        }
            }

            /// <summary>§D.3.3: significant samples not coded in this plane's significance pass.</summary>
            internal void RefinementPass(int plane)
            {
                for (int y0 = 0; y0 < _h; y0 += 4)
                    for (int x = 0; x < _w; x++)
                        for (int y = y0, yEnd = Math.Min(y0 + 4, _h); y < yEnd; y++)
                        {
                            int i = _cx.Index(x, y);
                            if (!_cx.NeedsRefinement(i)) continue;
                            _coder.Encode(_cx.RefinementContext(i, y), Bit(i, plane));
                            _cx.SetRefined(i);
                            DistortionRemoved += Error(_mag[i], plane + 1) - Error(_mag[i], plane);
                        }
            }

            /// <summary>§D.3.4: the samples neither significant nor coded in this plane, with run-length coding of all-zero columns (Table D.5).</summary>
            internal void CleanupPass(int plane)
            {
                for (int y0 = 0; y0 < _h; y0 += 4)
                    for (int x = 0; x < _w; x++)
                    {
                        int yEnd = Math.Min(y0 + 4, _h);
                        int y = y0;
                        if (yEnd - y0 == 4 && _cx.ColumnIsClear(x, y0))
                        {
                            int first = -1;
                            for (int k = 0; k < 4; k++)
                                if (Bit(_cx.Index(x, y0 + k), plane) == 1)
                                {
                                    first = k;
                                    break;
                                }
                            if (first < 0)
                            {
                                _coder.Encode(JpxBlockContexts.RunLength, 0);   // all four stay insignificant
                                continue;
                            }
                            _coder.Encode(JpxBlockContexts.RunLength, 1);
                            _coder.Encode(JpxBlockContexts.Uniform, first >> 1);
                            _coder.Encode(JpxBlockContexts.Uniform, first & 1);
                            y = y0 + first;
                            CodeSign(_cx.Index(x, y), y);
                            BecameSignificant(_cx.Index(x, y), plane);
                            y++;
                        }
                        for (; y < yEnd; y++)
                        {
                            int i = _cx.Index(x, y);
                            if (!_cx.IsUncoded(i)) continue;
                            int bit = Bit(i, plane);
                            _coder.Encode(_cx.ZeroCodingContext(i, y), bit);
                            if (bit == 1)
                            {
                                CodeSign(i, y);
                                BecameSignificant(i, plane);
                            }
                        }
                    }
                _cx.ClearVisited();
            }

            /// <summary>D-1: the sign, XORed with the bit its context prescribes; the sample then becomes significant.</summary>
            private void CodeSign(int i, int y)
            {
                int context = _cx.SignContext(i, y, out int xorBit);
                _coder.Encode(context, (_negative[i] ? 1 : 0) ^ xorBit);
                _cx.SetSignificant(i, _negative[i]);
            }
        }
    }
}
