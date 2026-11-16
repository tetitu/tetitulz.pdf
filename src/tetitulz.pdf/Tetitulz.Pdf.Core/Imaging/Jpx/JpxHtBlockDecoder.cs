// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// The HT block decoder (ITU-T T.814 clause 7): one HT code-block's cleanup pass — MEL, VLC and
    /// MagSgn bit-streams over a quad scan — and its SigProp and MagRef refinement passes, into the
    /// band in <see cref="JpxBlockDecoder"/>'s convention, so everything after tier 1 is the same for
    /// an HT code-block as for a Part 1 one.
    /// </summary>
    /// <remarks>
    /// <para><b>Which HT set.</b> A code-block's coding passes are 3·P0 placeholder passes, then HT
    /// sets of a cleanup and up to two refinement passes (§B.1). Each set re-codes the block at one
    /// more bit-plane, so the decoder reads one set: the last whose cleanup segment has bytes, with
    /// S<sub>blk</sub> = P + P0 + S<sub>skip</sub> (§B.3) — the sets before it are skipped, as §7.1.1 says
    /// only one set is processed.</para>
    /// <para><b>Where the bits land.</b> T.814 hands T.800's inverse quantisation N<sub>b</sub> =
    /// S<sub>blk</sub> + 1 + z<sub>n</sub> magnitude bits (§7.6): the cleanup's μ<sub>n</sub> is the
    /// magnitude down to bit-plane M<sub>b</sub> − 1 − S<sub>blk</sub>, the plane a Part 1 block's
    /// first cleanup pass would decode, and a refinement bit is the plane below it. A value is
    /// written as twice the magnitude with E-6's half at the sample's last decoded plane — the Part 1
    /// decoder's form — and the ROI rule is the same (§H.1).</para>
    /// <para><b>Malformed input</b> (T.814's error()): a bit-stream read past its segment, a
    /// codeword the table does not hold, an exponent bound past what a band holds. The block is left
    /// zero and the problem reported; nothing reads outside the segment buffers.</para>
    /// <para>An instance is a workspace, reused across the code-blocks of a decode by one thread.</para>
    /// </remarks>
    internal sealed class JpxHtBlockDecoder
    {
        /// <summary>
        /// The largest exponent bound U<sub>q</sub> read: m<sub>n</sub> ≤ U<sub>q</sub> MagSgn bits and
        /// the implicit one above them fit a <c>long</c>. T.814 allows magnitudes to 2<sup>74</sup>
        /// (§7.3.2, the RGN-shifted 37-plane case); no band this decoder holds has planes past
        /// <see cref="JpxBlockDecoder.MaxBitPlanes"/>, so a bound past this is malformed for it.
        /// </summary>
        internal const int MaxExponentBound = 62;

        private int _w, _h, _qw, _qh, _pw;
        private byte[] _sigma = Array.Empty<byte>();
        private byte[] _exponent = Array.Empty<byte>();
        private byte[] _negative = Array.Empty<byte>();
        private byte[] _refined = Array.Empty<byte>();   // z_n
        private byte[] _bit = Array.Empty<byte>();       // r_n
        private long[] _mu = Array.Empty<long>();
        private byte[] _cleanup = Array.Empty<byte>();
        private byte[] _refinement = Array.Empty<byte>();
        private int _lcup, _pcup, _lref;
        private string _error;

        // MagSgn (§7.1.2) and VLC (§7.1.4) a word at a time; MEL (§7.1.3, §7.3.3), SigProp (§7.1.5) and
        // MagRef (§7.1.6) a bit at a time.
        private MagSgnReader _ms;
        private VlcReader _vlc;
        private int _melPos, _melBits, _melTmp, _melK, _melRun, _melOne;
        private int _spPos, _spBits, _spTmp, _spLast;
        private int _mrPos, _mrBits, _mrTmp, _mrLast;

        /// <summary>The one-block form the tests use: the block's values in raster order, or zeros with <paramref name="problem"/>.</summary>
        internal static int[] Decode(JpxCodeBlock block, byte[] data, JpxCodeBlockStyle style,
            int magnitudeBitPlanes, int roiShift, out string problem)
        {
            int w = Math.Max(0, (int)block.Bounds.Width), h = Math.Max(0, (int)block.Bounds.Height);
            var result = new int[w * h];
            new JpxHtBlockDecoder().Decode(block, data, style, magnitudeBitPlanes, roiShift, result, 0, w, out problem);
            return result;
        }

        /// <summary>
        /// Decodes <paramref name="block"/> into <paramref name="target"/>, sample (x, y) at
        /// <c>targetOffset + y · targetStride + x</c>; the rectangle is cleared when the block yields
        /// nothing. An <c>int</c> target holds bands of up to <see cref="JpxBlockDecoder.MaxNarrowBitPlanes"/> planes.
        /// </summary>
        internal void Decode(JpxCodeBlock block, byte[] data, JpxCodeBlockStyle style, int magnitudeBitPlanes, int roiShift,
            int[] target, int targetOffset, int targetStride, out string problem)
        {
            if (!DecodeToWorkspace(block, data, style, magnitudeBitPlanes, roiShift, JpxBlockDecoder.MaxNarrowBitPlanes, out int plane, out int passes, out problem))
            {
                for (int y = 0; y < _h; y++) Array.Clear(target, targetOffset + y * targetStride, _w);
                return;
            }
            if (passes == 1 && roiShift == 0)
            {
                // Value's cleanup-only case, no ROI, a row at a time: (2μ + 1)·2^plane where σ, signed.
                for (int y = 0; y < _h; y++)
                {
                    int t = targetOffset + y * targetStride, i = y * _pw;
                    for (int x = 0; x < _w; x++, t++, i++)
                    {
                        int v = _sigma[i] != 0 ? (int)((2 * _mu[i] + 1) << plane) : 0;
                        target[t] = _negative[i] != 0 ? -v : v;
                    }
                }
                return;
            }
            long threshold = JpxBlockDecoder.RoiThreshold(roiShift);
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                    target[targetOffset + y * targetStride + x] = (int)Value(y * _pw + x, plane, passes, roiShift, threshold);
        }

        /// <summary>
        /// After a decode into a <c>long</c> target: the plane the decoded HT set's cleanup pass ended at, and
        /// the passes it decoded (Z<sub>blk</sub>, §B.3) — 0 when no set had cleanup bytes or the block was
        /// malformed. What a transcoder re-codes the block's values to.
        /// </summary>
        internal int DecodedPlane { get; private set; }

        /// <summary>See <see cref="DecodedPlane"/>.</summary>
        internal int DecodedPasses { get; private set; }

        /// <summary>The same into a <c>long</c> target, for a band past <see cref="JpxBlockDecoder.MaxNarrowBitPlanes"/>.</summary>
        internal void Decode(JpxCodeBlock block, byte[] data, JpxCodeBlockStyle style, int magnitudeBitPlanes, int roiShift,
            long[] target, int targetOffset, int targetStride, out string problem)
        {
            bool decoded = DecodeToWorkspace(block, data, style, magnitudeBitPlanes, roiShift, JpxBlockDecoder.MaxBitPlanes, out int plane, out int passes, out problem);
            DecodedPlane = plane;
            DecodedPasses = decoded ? passes : 0;
            if (!decoded)
            {
                for (int y = 0; y < _h; y++) Array.Clear(target, targetOffset + y * targetStride, _w);
                return;
            }
            long threshold = JpxBlockDecoder.RoiThreshold(roiShift);
            for (int y = 0; y < _h; y++)
                for (int x = 0; x < _w; x++)
                    target[targetOffset + y * targetStride + x] = Value(y * _pw + x, plane, passes, roiShift, threshold);
        }

        /// <summary>
        /// Sample <paramref name="i"/>'s value (§7.6) in the block decoder's form: the cleanup's
        /// magnitude down to <paramref name="plane"/>, a refinement bit at the plane below when the
        /// sample was refined, the half at the last decoded plane, the ROI scaling, the sign.
        /// </summary>
        private long Value(int i, int plane, int passes, int roiShift, long threshold)
        {
            // μ and the sign are written only where a pass made the sample significant (see Prepare).
            long mu = _sigma[i] != 0 ? _mu[i] : 0;
            long v;
            if (passes > 1 && _refined[i] != 0)
                v = mu != 0 ? (((mu << 1) | _bit[i]) * 2 + 1) << (plane - 1)
                            : _bit[i] != 0 ? 3L << (plane - 1) : 0;
            else
                v = mu != 0 ? (2 * mu + 1) << plane : 0;
            if (roiShift > 0 && v >= threshold) v >>= roiShift;
            return _negative[i] != 0 ? -v : v;
        }

        /// <summary>
        /// Picks the HT set, joins its segments and runs its passes into the workspace. False when
        /// the target is to be cleared: nothing to decode, or the block is malformed
        /// (<paramref name="problem"/>). <paramref name="plane"/> is the cleanup's least significant
        /// plane; <paramref name="passes"/> is Z<sub>blk</sub>, as far as it can be honoured.
        /// </summary>
        private bool DecodeToWorkspace(JpxCodeBlock block, byte[] data, JpxCodeBlockStyle style, int magnitudeBitPlanes,
            int roiShift, int maxPlanes, out int plane, out int passes, out string problem)
        {
            problem = null;
            plane = passes = 0;
            _w = Math.Max(0, (int)block.Bounds.Width);
            _h = Math.Max(0, (int)block.Bounds.Height);
            if (_w == 0 || _h == 0 || block.Passes == 0) return false;

            if (!JoinSegments(block, data, out int set, out passes)) return false;   // no HT set has cleanup bytes: Z_blk = 0
            int bandPlanes = magnitudeBitPlanes + roiShift;
            if (bandPlanes > maxPlanes)
            {
                problem = $"the band has {bandPlanes} bit-planes; at most {maxPlanes} can be held";
                return false;
            }
            int skipped = block.ZeroBitPlanes + block.HtPlaceholderPasses / 3 + set;   // S_blk (§B.3)
            plane = bandPlanes - 1 - skipped;
            if (plane < 0)
            {
                problem = $"the HT code-block skips {skipped} bit-planes of the band's {bandPlanes} (S_blk, T.814 §B.3), leaving none for its cleanup pass";
                return false;
            }
            if (passes > 1 && plane == 0)
            {
                problem = "the HT code-block's refinement passes would decode below bit-plane 0; they are ignored";
                passes = 1;
            }

            Prepare(refinement: passes >= 2);
            if (!Cleanup(skipped + 1))
            {
                problem = "HT cleanup pass: " + _error;
                return false;
            }
            if (passes >= 2)
            {
                SigProp((style & JpxCodeBlockStyle.VerticallyCausal) != 0);
                if (passes == 3) MagRef();
            }
            return true;
        }

        /// <summary>
        /// Chooses the HT set to decode — the last whose cleanup segment has bytes — and copies its
        /// cleanup and refinement segments out of the chunks (a segment's bytes are its codeword
        /// segments', concatenated, §B.3). False when no set has cleanup bytes.
        /// </summary>
        private bool JoinSegments(JpxCodeBlock block, byte[] data, out int set, out int passes)
        {
            set = -1;
            passes = 0;
            int placeholder = block.HtPlaceholderPasses;
            var chunks = block.Chunks;

            // Each chunk ends at a pass of set T or the last pass of its packet (§B.3), so the chunk
            // belongs to the segment of its last pass.
            int cursor = 0;
            for (int c = 0; c < chunks.Count; c++)
            {
                cursor += chunks[c].Passes;
                int rel = cursor - 1 - placeholder;
                if (rel >= 0 && rel % 3 == 0 && chunks[c].Data.Length > 0) set = Math.Max(set, rel / 3);
            }
            if (set < 0) return false;

            int lcup = 0, lref = 0;
            cursor = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                cursor = 0;
                for (int c = 0; c < chunks.Count; c++)
                {
                    JpxCodeChunk chunk = chunks[c];
                    cursor += chunk.Passes;
                    int rel = cursor - 1 - placeholder;
                    if (rel < 0 || rel / 3 != set) continue;
                    int length = (int)chunk.Data.Length;
                    if (rel % 3 == 0)
                    {
                        if (pass == 1) Array.Copy(data, chunk.Data.Start, _cleanup, lcup, length);
                        lcup += length;
                    }
                    else
                    {
                        if (pass == 1) Array.Copy(data, chunk.Data.Start, _refinement, lref, length);
                        lref += length;
                    }
                }
                if (pass == 0)
                {
                    if (_cleanup.Length < lcup) _cleanup = new byte[lcup];
                    if (_refinement.Length < lref) _refinement = new byte[lref];
                    lcup = lref = 0;
                }
            }
            _lcup = lcup;
            _lref = lref;

            int inSet = Math.Min(3, block.Passes - placeholder - 3 * set);
            passes = lref == 0 ? 1 : inSet;   // Z_blk (§B.3)
            return true;
        }

        /// <summary>
        /// Sizes the workspace for the block. Only what is read before it is written is cleared: the cleanup
        /// pass writes every quad's σ and exponent (<see cref="SetSignificance"/>) before anything reads them,
        /// μ and the sign are read only where σ or a refinement bit says a pass wrote them, and the refinement
        /// marks — read throughout SigProp, MagRef and the output — are cleared when the set has those passes.
        /// </summary>
        private void Prepare(bool refinement)
        {
            _qw = (_w + 1) >> 1;
            _qh = (_h + 1) >> 1;
            _pw = 2 * _qw;
            int n = _pw * 2 * _qh;
            if (_sigma.Length < n)
            {
                _sigma = new byte[n];
                _exponent = new byte[n];
                _negative = new byte[n];
                _refined = new byte[n];
                _bit = new byte[n];
                _mu = new long[n];
            }
            else if (refinement)
            {
                Array.Clear(_refined, 0, n);
                Array.Clear(_bit, 0, n);
            }
            _error = null;
        }

        // ---------------------------------------------------------------- the cleanup pass (§7.3)

        /// <summary>
        /// The HT cleanup pass (§7.3) over the quad scan of §7.2, in quad pairs (§7.3.4). False with
        /// <c>_error</c> on malformed input. <paramref name="magnitudeBits"/> is S<sub>blk</sub> + 1:
        /// a magnitude needing more is past the band.
        /// </summary>
        private bool Cleanup(int magnitudeBits)
        {
            if (_lcup < 2) return Error($"the cleanup segment is {_lcup} bytes; it needs at least 2 (§7.1.1)");
            int scup = (_cleanup[_lcup - 1] << 4) + (_cleanup[_lcup - 2] & 0x0F);
            if (scup < 2 || scup > _lcup || scup > 4079)
                return Error($"the suffix length Scup is {scup} for a {_lcup}-byte segment; it must be 2 to min(Lcup, 4079) (§7.1.1)");
            _pcup = _lcup - scup;
            InitReaders();

            for (int qy = 0; qy < _qh; qy++)
            {
                bool initial = qy == 0;
                int[] table = JpxHtCodeTables.Peek(initial);
                for (int qx = 0; qx < _qw; qx += 2)
                {
                    int t1 = DecodeSigEmb(Context(qx, qy, initial), table);
                    SetSignificance(qx, qy, t1);
                    bool second = qx + 1 < _qw;
                    int t2 = 0;
                    if (second)
                    {
                        t2 = DecodeSigEmb(Context(qx + 1, qy, initial), table);
                        SetSignificance(qx + 1, qy, t2);
                    }
                    if (VlcFailed()) return false;

                    bool off1 = (t1 & 0x10) != 0, off2 = (t2 & 0x10) != 0;
                    int u1 = 0, u2 = 0;
                    if (initial && off1 && off2)
                    {
                        // Both residuals non-zero in the first line-pair: one MEL symbol for the pair (§7.3.6).
                        if (MelSymbol() == 1)
                        {
                            int p1 = UPrefix(), p2 = UPrefix();
                            int s1 = USuffix(p1), s2 = USuffix(p2);
                            int e1 = UExtension(s1), e2 = UExtension(s2);
                            u1 = 2 + p1 + s1 + 4 * e1;
                            u2 = 2 + p2 + s2 + 4 * e2;
                        }
                        else
                        {
                            int p1 = UPrefix();
                            int p2 = p1 > 2 ? _vlc.Read(1) + 1 : UPrefix();
                            int s1 = USuffix(p1), s2 = USuffix(p2);
                            int e1 = UExtension(s1), e2 = UExtension(s2);
                            u1 = p1 + s1 + 4 * e1;
                            u2 = p2 + s2 + 4 * e2;
                        }
                    }
                    else
                    {
                        int p1 = off1 ? UPrefix() : 0, p2 = off2 ? UPrefix() : 0;
                        int s1 = off1 ? USuffix(p1) : 0, s2 = off2 ? USuffix(p2) : 0;
                        int e1 = off1 ? UExtension(s1) : 0, e2 = off2 ? UExtension(s2) : 0;
                        u1 = p1 + s1 + 4 * e1;
                        u2 = p2 + s2 + 4 * e2;
                    }
                    if (VlcFailed()) return false;

                    if (!MagSgn(qx, qy, t1, u1, initial, magnitudeBits)) return false;
                    if (second && !MagSgn(qx + 1, qy, t2, u2, initial, magnitudeBits)) return false;
                }
            }
            return _error == null;
        }

        private int Index(int x, int y) => y * _pw + x;

        /// <summary>The coding context c<sub>q</sub> of quad (qx, qy), equations (1) and (2) of §7.3.5.</summary>
        private int Context(int qx, int qy, bool initial)
        {
            int x = 2 * qx, y = 2 * qy;
            byte[] s = _sigma;
            if (initial)
            {
                if (qx == 0) return 0;
                int f = s[Index(x - 2, y)], sf = s[Index(x - 2, y + 1)];
                int w = s[Index(x - 1, y)], sw = s[Index(x - 1, y + 1)];
                return (f | sf) + 2 * w + 4 * sw;
            }
            int n = s[Index(x, y - 1)], ne = s[Index(x + 1, y - 1)];
            int nw = 0, nf = 0, west = 0, southwest = 0;
            if (qx > 0)
            {
                nw = s[Index(x - 1, y - 1)];
                west = s[Index(x - 1, y)];
                southwest = s[Index(x - 1, y + 1)];
            }
            if (qx + 1 < _qw) nf = s[Index(x + 2, y - 1)];
            return (nw | n) + 2 * (west | southwest) + 4 * (ne | nf);
        }

        /// <summary>The quad's four samples in scan order j = 0 to 3 (§7.2): top-left, bottom-left, top-right, bottom-right.</summary>
        private int SampleIndex(int qx, int qy, int j) => Index(2 * qx + (j >> 1), 2 * qy + (j & 1));

        /// <summary>The quad's σ from its pattern ρ, and an exponent of 0 for each sample until <see cref="MagSgn"/> sets a significant one's — the quad row below reads them (§7.3.7).</summary>
        private void SetSignificance(int qx, int qy, int t)
        {
            for (int j = 0; j < 4; j++)
            {
                int s = SampleIndex(qx, qy, j);
                _sigma[s] = (byte)((t >> j) & 1);
                _exponent[s] = 0;
            }
        }

        /// <summary>
        /// decodeSigEMB (§7.3.5): for an all-zero-context quad, a MEL symbol first, 0 meaning an
        /// insignificant quad; otherwise a CxtVLC codeword. The result is the table's packed entry
        /// (<see cref="JpxHtCodeTables.Lookup"/>), 0 for an insignificant quad. The codeword is read in one
        /// step from the next seven bits (<see cref="JpxHtCodeTables.Peek"/>): every context's code is complete,
        /// so exactly one codeword begins those bits, and its length is what is consumed.
        /// </summary>
        private int DecodeSigEmb(int context, int[] table)
        {
            if (context == 0 && MelSymbol() == 0) return 0;
            int entry = table[context * 128 + _vlc.Peek7()];
            _vlc.Skip(entry >> 16);
            return entry & 0xFFFF;
        }

        /// <summary>
        /// The quad's exponent predictor and bound (§7.3.7), then its samples' MagSgn values
        /// (§7.3.8): magnitudes, signs and exponents into the workspace.
        /// </summary>
        private bool MagSgn(int qx, int qy, int t, int u, bool initial, int magnitudeBits)
        {
            int rho = t & 0xF;
            if (rho == 0) return true;
            int known = (t >> 5) & 0xF, knownOne = (t >> 9) & 0xF;

            int kappa = 1;
            if (!initial && (rho & (rho - 1)) != 0)   // γ_q: more than one significant sample (equation (6))
            {
                int x = 2 * qx, y = 2 * qy - 1;
                byte[] e = _exponent;
                int max = Math.Max(e[Index(x, y)], e[Index(x + 1, y)]);
                if (qx > 0) max = Math.Max(max, e[Index(x - 1, y)]);
                if (qx + 1 < _qw) max = Math.Max(max, e[Index(x + 2, y)]);
                kappa = Math.Max(1, max - 1);
            }
            int bound = kappa + u;   // U_q
            if (bound > MaxExponentBound)
                return Error($"a quad's exponent bound is {bound}; this decoder reads at most {MaxExponentBound} (§7.3.7)");

            for (int j = 0; j < 4; j++)
            {
                if (((rho >> j) & 1) == 0) continue;
                int m = bound - ((known >> j) & 1);
                long v = _ms.Read(m);   // m_n bits, least significant first (§7.3.8)
                v |= (long)((knownOne >> j) & 1) << m;
                if (_ms.Problem != null) return Error(_ms.Problem);

                long mu = (v >> 1) + 1;
                if ((mu >> magnitudeBits) != 0)
                    return Error($"a magnitude of {mu} needs more than the code-block's {magnitudeBits} bit-planes (§7.6)");
                int s = SampleIndex(qx, qy, j);
                _mu[s] = mu;
                _negative[s] = (byte)(v & 1);
                _exponent[s] = (byte)BitLength(2 * mu - 1);
            }
            return true;
        }

        /// <summary>The number of bits <paramref name="v"/> needs (Table 1's exponent for 2μ − 1).</summary>
        private static int BitLength(long v)
        {
#if NET
            return v <= 0 ? 0 : System.Numerics.BitOperations.Log2((ulong)v) + 1;
#else
            int n = 0;
            while (v > 0) { v >>= 1; n++; }
            return n;
#endif
        }

        // ---------------------------------------------------------------- unsigned residuals (§7.3.6)

        /// <summary>The prefix (§7.3.6): 1, 01, 001 or 000 in the order read — 1, 2, 3 or 5.</summary>
        private int UPrefix()
        {
            int bits = _vlc.Peek7();
            if ((bits & 1) != 0) { _vlc.Skip(1); return 1; }
            if ((bits & 2) != 0) { _vlc.Skip(2); return 2; }
            _vlc.Skip(3);
            return (bits & 4) != 0 ? 3 : 5;
        }

        /// <summary>The suffix: one bit after prefix 3, five after prefix 5, least significant first.</summary>
        private int USuffix(int prefix) => prefix < 3 ? 0 : _vlc.Read(prefix == 3 ? 1 : 5);

        /// <summary>The extension: four bits when the suffix reached 28.</summary>
        private int UExtension(int suffix) => suffix < 28 ? 0 : _vlc.Read(4);

        /// <summary>Whether a VLC read so far reached a problem; if so it becomes the block's error.</summary>
        private bool VlcFailed()
        {
            string problem = _vlc.Problem;
            if (problem != null) Error(problem);
            return _error != null;
        }

        // ---------------------------------------------------------------- the bit-streams (§7.1)

        private void InitReaders()
        {
            _ms.Start(_cleanup, _pcup);
            _melPos = _pcup;
            _melBits = _melTmp = 0;
            _melK = _melRun = _melOne = 0;
            _vlc.Start(_cleanup, _lcup, _pcup);
        }

        /// <summary>modDcup (§7.1.1): the last byte reads as 0xFF and the second-last's low nibble as 1s.</summary>
        private int ModDcup(int pos)
        {
            if (pos == _lcup - 1) return 0xFF;
            if (pos == _lcup - 2) return _cleanup[pos] | 0x0F;
            return _cleanup[pos];
        }

        /// <summary>
        /// importMagSgnBit (§7.1.2), a word at a time: the segment's bytes from its start, seven bits of each
        /// after an 0xFF (whose stuffing bit must be 0), then one 0xFF at P<sub>cup</sub>, least significant bit
        /// first. Bytes are gathered ahead of the reads into a 64-bit accumulator; a set stuffing bit, or a byte
        /// past the one at P<sub>cup</sub>, is noted at the stream bit where its bits begin and becomes a
        /// <see cref="Problem"/> only once a read passes that bit — exactly where the bit-at-a-time procedure,
        /// which loaded a byte when its first bit was asked for, raised it.
        /// </summary>
        internal struct MagSgnReader
        {
            private byte[] _data;
            private int _pos, _end, _last, _count;
            private ulong _acc;
            private long _consumed, _errorAt;
            private string _why;

            internal void Start(byte[] data, int pcup)
            {
                _data = data;
                _pos = 0;
                _end = pcup;
                _last = 0;
                _count = 0;
                _acc = 0;
                _consumed = 0;
                _errorAt = long.MaxValue;
                _why = null;
            }

            /// <summary>The next <paramref name="m"/> bits, 0 to 62, the first read the least significant.</summary>
            internal long Read(int m)
            {
                if (m > 32)
                {
                    long low = Read(32);
                    return low | (Read(m - 32) << 32);
                }
                if (_count < m) Fill();
                long v = (long)(_acc & ((1UL << m) - 1));
                _acc >>= m;
                _count -= m;
                _consumed += m;
                return v;
            }

            /// <summary>What the bits read so far ran into, or null.</summary>
            internal string Problem => _consumed > _errorAt ? _why : null;

            private void Fill()
            {
                while (_count <= 56)
                {
                    int bits = _last == 0xFF ? 7 : 8;
                    int b;
                    if (_pos < _end)
                    {
                        b = _data[_pos];
                        if (bits == 7 && (b & 0x80) != 0) Note("a MagSgn byte after 0xFF has its stuffing bit set (§7.1.2)");
                    }
                    else if (_pos == _end) b = 0xFF;
                    else
                    {
                        Note("the MagSgn bit-stream is read past its segment (§7.1.2)");
                        b = 0;
                    }
                    _acc |= (ulong)(b & ((1 << bits) - 1)) << _count;
                    _count += bits;
                    _last = b;
                    _pos++;
                }
            }

            /// <summary>The first problem by position wins: the byte being loaded starts at the stream bit after those gathered.</summary>
            private void Note(string why)
            {
                long at = _consumed + _count;
                if (at < _errorAt)
                {
                    _errorAt = at;
                    _why = why;
                }
            }
        }

        /// <summary>
        /// importVLCBit (§7.1.4), a word at a time, backwards from the segment's end: the high nibble of the
        /// second-last byte (its low nibble read as 1s, modDcup — three bits of it when they are 111), then each
        /// byte down to P<sub>cup</sub>, seven bits of one whose low seven bits are all 1 after a byte above 0x8F,
        /// least significant bit first. A byte below P<sub>cup</sub> reads as 0 and is a <see cref="Problem"/> once
        /// a read passes its first bit, as <see cref="MagSgnReader"/> notes its own.
        /// </summary>
        internal struct VlcReader
        {
            private byte[] _data;
            private int _pos, _end, _last, _count;
            private ulong _acc;
            private long _consumed, _errorAt;
            private string _why;

            internal void Start(byte[] data, int lcup, int pcup)
            {
                _data = data;
                _end = pcup;
                _consumed = 0;
                _errorAt = long.MaxValue;
                _why = null;
                _last = data[lcup - 2] | 0x0F;
                int nibble = _last >> 4;
                int bits = (nibble & 7) < 7 ? 4 : 3;
                _acc = (ulong)(nibble & ((1 << bits) - 1));
                _count = bits;
                _pos = lcup - 3;
            }

            /// <summary>The next seven bits without consuming them, the first the least significant.</summary>
            internal int Peek7()
            {
                if (_count < 7) Fill();
                return (int)(_acc & 0x7F);
            }

            /// <summary>Consumes <paramref name="n"/> bits a <see cref="Peek7"/> has made available.</summary>
            internal void Skip(int n)
            {
                _acc >>= n;
                _count -= n;
                _consumed += n;
            }

            /// <summary>The next <paramref name="n"/> bits, 0 to 32, the first read the least significant.</summary>
            internal int Read(int n)
            {
                if (_count < n) Fill();
                int v = (int)(_acc & ((1UL << n) - 1));
                Skip(n);
                return v;
            }

            /// <summary>What the bits read so far ran into, or null.</summary>
            internal string Problem => _consumed > _errorAt ? _why : null;

            private void Fill()
            {
                while (_count <= 56)
                {
                    int b;
                    if (_pos >= _end) b = _data[_pos];
                    else
                    {
                        long at = _consumed + _count;
                        if (at < _errorAt)
                        {
                            _errorAt = at;
                            _why = "the VLC bit-stream is read past the MagSgn bytes (§7.1.4)";
                        }
                        b = 0;
                    }
                    int bits = _last > 0x8F && (b & 0x7F) == 0x7F ? 7 : 8;
                    _acc |= (ulong)(b & ((1 << bits) - 1)) << _count;
                    _count += bits;
                    _last = b;
                    _pos--;
                }
            }
        }

        /// <summary>importMELBit (§7.1.3).</summary>
        private int MelBit()
        {
            if (_melBits == 0)
            {
                _melBits = _melTmp == 0xFF ? 7 : 8;
                if (_melPos < _lcup)
                {
                    _melTmp = ModDcup(_melPos);
                    _melPos++;
                }
                else _melTmp = 0xFF;
            }
            _melBits--;
            return (_melTmp >> _melBits) & 1;
        }

        /// <summary>decodeMELSym (§7.3.3), with MEL_E from Table 2.</summary>
        private int MelSymbol()
        {
            if (_melRun == 0 && _melOne == 0)
            {
                int eval = JpxHtCodeTables.MelExponent[_melK];
                if (MelBit() == 1)
                {
                    _melRun = 1 << eval;
                    _melK = Math.Min(12, _melK + 1);
                }
                else
                {
                    _melRun = 0;
                    while (eval > 0)
                    {
                        _melRun = 2 * _melRun + MelBit();
                        eval--;
                    }
                    _melK = Math.Max(0, _melK - 1);
                    _melOne = 1;
                }
            }
            if (_melRun > 0)
            {
                _melRun--;
                return 0;
            }
            _melOne = 0;
            return 1;
        }

        /// <summary>importSigPropBit (§7.1.5): bytes past the segment read as 0.</summary>
        private int SigPropBit()
        {
            if (_spBits == 0)
            {
                _spBits = _spLast == 0xFF ? 7 : 8;
                if (_spPos < _lref)
                {
                    _spTmp = _refinement[_spPos];
                    _spPos++;
                    if ((_spTmp & (1 << _spBits)) != 0) Error("a SigProp byte after 0xFF has its stuffing bit set (§7.1.5)");
                }
                else _spTmp = 0;
                _spLast = _spTmp;
            }
            int bit = _spTmp & 1;
            _spTmp >>= 1;
            _spBits--;
            return bit;
        }

        /// <summary>importMagRefBit (§7.1.6), backwards from the refinement segment's end; bytes before it read as 0.</summary>
        private int MagRefBit()
        {
            if (_mrBits == 0)
            {
                if (_mrPos >= 0)
                {
                    _mrTmp = _refinement[_mrPos];
                    _mrPos--;
                }
                else _mrTmp = 0;
                _mrBits = 8;
                if (_mrLast > 0x8F && (_mrTmp & 0x7F) == 0x7F) _mrBits = 7;
                _mrLast = _mrTmp;
            }
            int bit = _mrTmp & 1;
            _mrTmp >>= 1;
            _mrBits--;
            return bit;
        }

        // ---------------------------------------------------------------- the refinement passes (§7.4, §7.5)

        /// <summary>
        /// The HT SigProp pass (§7.4): over the stripe scan, a column-group of four columns at a time,
        /// a refinement bit for each insignificant sample with a significant or refined neighbour, then
        /// a sign for each sample that bit made significant. A neighbour not yet visited has no
        /// refinement bit, so testing every neighbour's is testing the scan-causal ones'.
        /// </summary>
        private void SigProp(bool verticallyCausal)
        {
            _spPos = _spBits = _spTmp = _spLast = 0;
            for (int y0 = 0; y0 < _h; y0 += 4)
            {
                int y1 = Math.Min(_h, y0 + 4);
                // Vertically causal: the propagation neighbourhood stops at the stripe's last row (§7.4).
                int below = verticallyCausal ? y1 : _h;
                for (int gx = 0; gx < _w; gx += 4)
                {
                    int gx1 = Math.Min(_w, gx + 4);
                    for (int x = gx; x < gx1; x++)
                        for (int y = y0; y < y1; y++)
                        {
                            int i = Index(x, y);
                            if (_sigma[i] != 0 || !HasSignificantNeighbour(x, y, below)) continue;
                            _refined[i] = 1;
                            _bit[i] = (byte)SigPropBit();
                        }
                    for (int x = gx; x < gx1; x++)
                        for (int y = y0; y < y1; y++)
                        {
                            int i = Index(x, y);
                            if (_bit[i] != 0) _negative[i] = (byte)SigPropBit();
                        }
                }
            }
        }

        /// <summary>Whether any neighbour of (x, y) in the block, above row <paramref name="below"/>, is significant or refined to 1.</summary>
        private bool HasSignificantNeighbour(int x, int y, int below)
        {
            int xa = Math.Max(0, x - 1), xb = Math.Min(_w - 1, x + 1);
            int ya = Math.Max(0, y - 1), yb = Math.Min(below - 1, y + 1);
            for (int ny = ya; ny <= yb; ny++)
                for (int nx = xa; nx <= xb; nx++)
                {
                    if (nx == x && ny == y) continue;
                    int i = Index(nx, ny);
                    if ((_sigma[i] | _bit[i]) != 0) return true;
                }
            return false;
        }

        /// <summary>The HT MagRef pass (§7.5): a refinement bit for every sample the cleanup pass made significant, in the stripe scan.</summary>
        private void MagRef()
        {
            _mrPos = _lref - 1;
            _mrBits = 0;
            _mrLast = 0xFF;
            _mrTmp = 0;
            for (int y0 = 0; y0 < _h; y0 += 4)
            {
                int y1 = Math.Min(_h, y0 + 4);
                for (int x = 0; x < _w; x++)
                    for (int y = y0; y < y1; y++)
                    {
                        int i = Index(x, y);
                        if (_sigma[i] == 0) continue;
                        _refined[i] = 1;
                        _bit[i] = (byte)MagRefBit();
                    }
            }
        }

        private bool Error(string why)
        {
            if (_error == null) _error = why;
            return false;
        }
    }
}
