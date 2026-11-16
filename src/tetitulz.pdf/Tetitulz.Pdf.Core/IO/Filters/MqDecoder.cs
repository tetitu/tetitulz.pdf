// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Tetitulz.Pdf.Core.IO.Filters
{
    /// <summary>
    /// The arithmetic decoder's registers of T.88 §E.3 / T.800 Annex C — A, the interval; C, the
    /// code register; CT, the bits until the next byte — as a value. <see cref="MqDecoder"/> keeps
    /// one; a caller decoding a long run of decisions may take a copy into a local, decode through
    /// <see cref="MqDecoder.Decode(ref MqRegisters, int)"/>, and hand it back, so that the three
    /// stay in registers rather than in the object across every decision.
    /// </summary>
    internal struct MqRegisters
    {
        /// <summary>The interval range, 16-bit effective.</summary>
        internal int A;

        /// <summary>The code register, 32-bit; CHIGH is its top 16 bits.</summary>
        internal uint C;

        /// <summary>The renormalisation counter: bits left before BYTEIN.</summary>
        internal int CT;
    }

    /// <summary>
    /// The MQ arithmetic decoder, shared by JBIG2 (ITU-T T.88 Annex E) and JPEG 2000 (ITU-T T.800
    /// Annex C). The two specifications define the same coder: the same 47-state probability table,
    /// the same INITDEC, DECODE and BYTEIN.
    /// </summary>
    /// <remarks>
    /// <para><b>What JPEG 2000 needs that JBIG2 does not, and why both are parameters here rather
    /// than a second decoder.</b> A JPEG 2000 codeword segment ends part-way through a buffer that
    /// holds the next segment's bytes, and the decoder must read <c>0xFF</c> past the segment's end
    /// rather than those bytes — so the constructor takes an end offset, and JBIG2 passes the
    /// buffer's length. A JPEG 2000 code-block also restarts the coder at each new segment while
    /// keeping its context states (<see cref="Restart(int, int)"/>, and <see cref="Restart(byte[], int, int)"/>
    /// for a segment in another buffer, so one decoder serves every code-block of a decode), and
    /// starts three contexts in non-zero states (<see cref="State"/> packs one). A copy of the coder
    /// per standard would agree until one was corrected.</para>
    ///
    /// <para>A = 16-bit interval; C = 32-bit code register (CHIGH is its top 16 bits); CT = bits
    /// until the next byte. BYTEIN treats <c>0xFF</c> followed by a byte above <c>0x8F</c> as a
    /// marker — the end of the data — and pads with <c>0xFF</c> from there; reading past the end
    /// offset does the same.</para>
    ///
    /// <para><b>The fast path is the MPS without renormalisation.</b> Most decisions of a JPEG 2000
    /// code-block, and of a JBIG2 region, are the more probable symbol with the interval still
    /// above half: <see cref="Decode(ref MqRegisters, int)"/> settles that case in a few operations
    /// on the registers it is handed and leaves the exchanges and the renormalisation to
    /// <see cref="DecodeSlow"/>, so that the common case inlines into the caller's loop with A and
    /// C in machine registers. The arithmetic is DECODE's exactly, in the same order;
    /// <see cref="Decode(int)"/> is the same procedure over the decoder's own registers.</para>
    /// </remarks>
    internal sealed class MqDecoder
    {
        // Q-coder probability table: T.88 Table E.1, the same table T.800 Annex C gives. 47 entries.
        private static readonly int[] Qe =
        {
            0x5601, 0x3401, 0x1801, 0x0AC1, 0x0521, 0x0221,
            0x5601, 0x5401, 0x4801, 0x3801, 0x3001, 0x2401,
            0x1C01, 0x1601, 0x5601, 0x5401, 0x5101, 0x4801,
            0x3801, 0x3401, 0x3001, 0x2801, 0x2401, 0x2201,
            0x1C01, 0x1801, 0x1601, 0x1401, 0x1201, 0x1101,
            0x0AC1, 0x09C1, 0x08A1, 0x0521, 0x0441, 0x02A1,
            0x0221, 0x0141, 0x0111, 0x0085, 0x0049, 0x0025,
            0x0015, 0x0009, 0x0005, 0x0001, 0x5601
        };

        private static readonly int[] Nmps =
        {
             1,  2,  3,  4,  5, 38,  7,  8,  9, 10, 11, 12, 13, 29,
            15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30,
            31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46
        };

        private static readonly int[] Nlps =
        {
             1,  6,  9, 12, 29, 33,  6, 14, 14, 14, 17, 18, 20, 21,
            14, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 23, 24, 25, 26, 27,
            28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 43, 46
        };

        private static readonly int[] Swch =
        {
             1, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
             1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
             0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
        };

        /// <summary>
        /// The tables above unrolled over the 94 packed states (<see cref="State"/>: index and MPS):
        /// each state's Qe, and the packed state an MPS and an LPS leave it in, the MPS switch
        /// applied — so a decision reads its next state from one entry and branches on neither the
        /// index nor the switch.
        /// </summary>
        private static readonly int[] QeOfState = Unroll(s => Qe[s >> 1]);
        private static readonly int[] StateAfterMps = Unroll(s => (Nmps[s >> 1] << 1) | (s & 1));
        private static readonly int[] StateAfterLps = Unroll(s => (Nlps[s >> 1] << 1) | ((s & 1) ^ Swch[s >> 1]));

        private static int[] Unroll(Func<int, int> entry)
        {
            var table = new int[2 * 47];
            for (int s = 0; s < table.Length; s++) table[s] = entry(s);
            return table;
        }

        /// <summary>The Q-coder tables, for the encoder and for unit tests.</summary>
        internal static IReadOnlyList<int> QeTable      => Qe;
        internal static IReadOnlyList<int> NmpsTable    => Nmps;
        internal static IReadOnlyList<int> NlpsTable    => Nlps;
        internal static IReadOnlyList<int> SwitchTable  => Swch;

        /// <summary>
        /// Context states, packed as <c>(index &lt;&lt; 1) | MPS</c> (see <see cref="State"/>) and indexed by
        /// a caller-defined context number — each JBIG2 region decoder, and JPEG 2000's tier 1, keeps
        /// its own context-index space. All start at state 0, MPS 0.
        /// </summary>
        public int[] ContextStates { get; }

        private byte[] _data;
        private int _pos;
        private int _end;      // exclusive; bytes at or past it read as 0xFF
        private byte _b;       // current input byte (for marker detection)
        private MqRegisters _r;

        /// <summary>A decoder over <paramref name="data"/> from <paramref name="startOffset"/> to its end.</summary>
        public MqDecoder(byte[] data, int startOffset, int contextCount)
            : this(data, startOffset, data?.Length ?? 0, contextCount)
        {
        }

        /// <summary>
        /// A decoder over the codeword segment <c>[start, end)</c> of <paramref name="data"/>. Bytes
        /// outside the segment are never read.
        /// </summary>
        public MqDecoder(byte[] data, int start, int end, int contextCount)
        {
            _data = data ?? Array.Empty<byte>();
            ContextStates = new int[contextCount];
            Restart(start, end);
        }

        /// <summary>
        /// Packs a context state for <see cref="ContextStates"/>: probability-state
        /// <paramref name="index"/> (0–46) and most-probable symbol <paramref name="mps"/> (0 or 1).
        /// </summary>
        internal static int State(int index, int mps) => (index << 1) | mps;

        /// <summary>The Qe table index of a packed context state.</summary>
        internal static int IndexOf(int state) => state >> 1;

        /// <summary>The MPS of a packed context state.</summary>
        internal static int MpsOf(int state) => state & 1;

        /// <summary>
        /// The decoder's registers. A caller that decodes through <see cref="Decode(ref MqRegisters, int)"/>
        /// takes them first and stores them back before anything else decodes through the object.
        /// </summary>
        internal MqRegisters Registers
        {
            get => _r;
            set => _r = value;
        }

        /// <summary>
        /// Starts decoding the codeword segment <c>[start, end)</c>: the interval, code register and
        /// input position are re-initialised (INITDEC) and the context states are kept. A caller
        /// whose coding style also resets the contexts sets <see cref="ContextStates"/> itself.
        /// </summary>
        public void Restart(int start, int end)
        {
            _end = Math.Min(Math.Max(0, end), _data.Length);
            _pos = Math.Min(Math.Max(0, start), _end);
            InitDec();
        }

        /// <summary>
        /// As <see cref="Restart(int, int)"/>, over the segment <c>[start, end)</c> of
        /// <paramref name="data"/>, which the decoder reads from then on — the context states are
        /// kept, as a JPEG 2000 decoder reusing one coder for the code-blocks of a tile needs.
        /// </summary>
        public void Restart(byte[] data, int start, int end)
        {
            _data = data ?? Array.Empty<byte>();
            Restart(start, end);
        }

        // INITDEC per T.88 E.3.5 (T.800 Annex C defines the same procedure).
        private void InitDec()
        {
            _b = NextByte();
            _r.C = (uint)_b << 16;
            ByteIn(ref _r);
            _r.C <<= 7;
            _r.CT -= 7;
            _r.A = 0x8000;
        }

        /// <summary>
        /// The marker rule of T.88 E.3.3 and T.800 C.3.4 / D.6: after an 0xFF, a byte above 0x8F is
        /// the start of a marker (or the segment's end), and the coder reads 1s from there. The one
        /// statement for the MQ decoder's BYTEIN and the raw (bypass) reader.
        /// </summary>
        internal static bool EndsSegment(byte afterFF) => afterFF > 0x8F;

        // BYTEIN per T.88 E.3.3 (and T.800 Annex C) — MSB-first, with marker padding for 0xFF
        // followed by a byte above 0x8F.
        private void ByteIn(ref MqRegisters r)
        {
            if (_b == 0xFF)
            {
                byte b1 = _pos < _end ? _data[_pos] : (byte)0xFF;
                if (EndsSegment(b1))
                {
                    // Treat the rest of the segment as padded 0xFF.
                    r.C += 0xFF00;
                    r.CT = 8;
                }
                else
                {
                    _b = b1;
                    _pos++;
                    r.C += (uint)_b << 9;
                    r.CT = 7;
                }
            }
            else
            {
                _b = NextByte();
                r.C += (uint)_b << 8;
                r.CT = 8;
            }
        }

        private byte NextByte()
        {
            if (_pos >= _end) return 0xFF;
            return _data[_pos++];
        }

        /// <summary>
        /// Decodes one bit using the Q-coder state for the given context index, over the decoder's
        /// own registers. Implements T.88 §E.3.2 DECODE(CX) with LPS_EXCHANGE and MPS_EXCHANGE.
        /// </summary>
        public int Decode(int cx) => Decode(ref _r, cx);

        /// <summary>
        /// DECODE(CX) over the registers <paramref name="r"/> — a copy of <see cref="Registers"/> the
        /// caller holds and hands back. The MPS decision that needs no renormalisation is settled
        /// here; the rest in <see cref="DecodeSlow"/>.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Decode(ref MqRegisters r, int cx)
        {
            int state = ContextStates[cx];
            int qe = QeOfState[state];
            int a = r.A - qe;
            uint c = r.C;
            r.A = a;
            if (((c >> 16) >= (uint)qe) & ((a & 0x8000) != 0))
            {
                // CHIGH is at or above Qe: the MPS sub-interval; the interval stays normalised.
                r.C = c - ((uint)qe << 16);
                return state & 1;
            }
            // Through a copy, so that the caller's registers are never exposed by address and can
            // stay in machine registers; the copy costs the slow path six moves.
            MqRegisters t = r;
            int d = DecodeSlow(ref t, cx, state, qe);
            r = t;
            return d;
        }

        /// <summary>
        /// The rest of DECODE, after <see cref="Decode(ref MqRegisters, int)"/> subtracted Qe from A:
        /// the LPS path and MPS_EXCHANGE, each followed by RENORMD. Whether it is inlined is left
        /// to the JIT: on lossless content more than half the decisions come here, and forcing it
        /// out of line cost 7 % on p0_07 (the instruction-cache argument for doing so lost).
        /// </summary>
        private int DecodeSlow(ref MqRegisters r, int cx, int state, int qe)
        {
            int d;
            if ((r.C >> 16) < (uint)qe)
            {
                // LPS decoded (the code lies in the top [A-Qe, A) subinterval
                // from the encoder's perspective → bottom half of the decoder).
                if (r.A < qe)
                {
                    // Ranges swapped — this is actually the MPS.
                    d = state & 1;
                    ContextStates[cx] = StateAfterMps[state];
                }
                else
                {
                    d = (state & 1) ^ 1;
                    ContextStates[cx] = StateAfterLps[state];
                }
                r.A = qe;
            }
            else
            {
                // The MPS sub-interval with A below half (the fast path took the other case).
                r.C -= (uint)qe << 16;
                if (r.A < qe)
                {
                    // Exchange: decoded is LPS.
                    d = (state & 1) ^ 1;
                    ContextStates[cx] = StateAfterLps[state];
                }
                else
                {
                    d = state & 1;
                    ContextStates[cx] = StateAfterMps[state];
                }
            }
            Renormalize(ref r);
            return d;
        }

        /// <summary>
        /// RENORMD: A and C shift left until A's bit 15 is set, with a BYTEIN each time CT runs
        /// out. On .NET the shifts between two BYTEINs are made at once — the count is A's leading
        /// zeros, A being below 0x8000 here and above 0 — which is the procedure's result bit for
        /// bit without a branch per bit.
        /// </summary>
        private void Renormalize(ref MqRegisters r)
        {
#if NET
            int n = System.Numerics.BitOperations.LeadingZeroCount((uint)r.A) - 16;
            while (n > r.CT)
            {
                int s = r.CT;
                r.A <<= s;
                r.C <<= s;
                n -= s;
                r.CT = 0;
                ByteIn(ref r);
            }
            r.A <<= n;
            r.C <<= n;
            r.CT -= n;
#else
            do
            {
                if (r.CT == 0) ByteIn(ref r);
                r.A <<= 1;
                r.C <<= 1;
                r.CT--;
            }
            while ((r.A & 0x8000) == 0);
#endif
        }
    }
}
