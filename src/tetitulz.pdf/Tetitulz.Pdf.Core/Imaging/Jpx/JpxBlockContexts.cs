// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// The per-sample coding state of one code-block for the <b>encoder</b>, and the context rules
    /// of T.800 Annex D asked over it — zero coding (Table D.1) and sign (Tables D.2, D.3) through
    /// the decoder's one statement of those tables (<see cref="JpxBlockDecoder.ZeroCodingContext"/>,
    /// <see cref="JpxBlockDecoder.SignContext"/>, from which its lookup tables are also built),
    /// magnitude refinement (Table D.4), the run-length column test (Table D.5) and the initial
    /// states of Table D.7 (<see cref="JpxBlockDecoder.ResetContexts"/> is the decoder's rule too). The
    /// decoder keeps its own state in flag words for speed; a context rule the two halves computed
    /// separately would agree until one was corrected, and the failure mode is a codestream only
    /// this library can read.
    /// </summary>
    /// <remarks>
    /// The samples carry a one-sample insignificant border, so no neighbour read needs a bounds
    /// check. Indices into the state are <see cref="Index"/>'s; a row's y is passed alongside where
    /// the vertically causal rule (§D.7) needs to know whether the row below lies in the next stripe.
    /// </remarks>
    internal sealed class JpxBlockContexts
    {
        /// <summary>Nine zero-coding, five sign, three refinement, run-length, uniform — the decoder's numbering, read from it.</summary>
        internal const int ContextCount = JpxBlockDecoder.ContextCount;

        /// <summary>The first sign context (9); the five run to 13.</summary>
        internal const int SignBase = JpxBlockDecoder.SignBase;

        /// <summary>The first magnitude-refinement context (14); the three run to 16.</summary>
        internal const int RefineBase = JpxBlockDecoder.RefineBase;

        internal const int RunLength = JpxBlockDecoder.RunLength;
        internal const int Uniform = JpxBlockDecoder.Uniform;

        // Per-sample state bits.
        private const byte Significant = 1, Visited = 2, Refined = 4, Negative = 8;

        private readonly int _w, _h, _stride, _orientation;
        private readonly bool _causal;
        private readonly byte[] _state;

        internal JpxBlockContexts(int width, int height, int orientation, bool causal)
        {
            _w = width;
            _h = height;
            _stride = width + 2;
            _orientation = orientation;
            _causal = causal;
            _state = new byte[_stride * (height + 2)];
        }

        internal int Width => _w;
        internal int Height => _h;

        /// <summary>The state index of sample (x, y).</summary>
        internal int Index(int x, int y) => (y + 1) * _stride + x + 1;

        internal bool IsSignificant(int i) => (_state[i] & Significant) != 0;

        internal bool IsNegative(int i) => (_state[i] & Negative) != 0;

        /// <summary>Coded in this bit-plane's significance propagation pass (§D.3.1), so later passes leave it alone.</summary>
        internal bool IsVisited(int i) => (_state[i] & Visited) != 0;

        /// <summary>Significant and not yet visited in this plane: what the refinement pass codes (§D.3.3).</summary>
        internal bool NeedsRefinement(int i) => (_state[i] & (Significant | Visited)) == Significant;

        /// <summary>Neither significant nor visited: what the cleanup pass codes (§D.3.4).</summary>
        internal bool IsUncoded(int i) => (_state[i] & (Significant | Visited)) == 0;

        internal void SetSignificant(int i, bool negative)
        {
            _state[i] |= Significant;
            if (negative) _state[i] |= Negative;
        }

        internal void SetVisited(int i) => _state[i] |= Visited;

        internal void SetRefined(int i) => _state[i] |= Refined;

        internal void ClearVisited()
        {
            for (int i = 0; i < _state.Length; i++) _state[i] &= unchecked((byte)~Visited);
        }

        /// <summary>Table D.7 on the encoder side, from the decoder's one statement (<see cref="JpxBlockDecoder.ResetContexts"/>): the coder's contexts set to the states the decoder starts from.</summary>
        internal static void ResetStates(MqEncoder coder)
        {
            var states = new int[ContextCount];
            JpxBlockDecoder.ResetContexts(states);
            for (int c = 0; c < ContextCount; c++) coder.SetState(c, MqDecoder.IndexOf(states[c]), MqDecoder.MpsOf(states[c]));
        }

        /// <summary>
        /// Whether the four samples of the stripe column at (x, y0) are all uncoded with a zero
        /// context — the run-length mode of Table D.5. The caller checks the column is full.
        /// </summary>
        internal bool ColumnIsClear(int x, int y0)
        {
            for (int y = y0; y < y0 + 4; y++)
            {
                int i = Index(x, y);
                if ((_state[i] & (Significant | Visited)) != 0 || ZeroCodingContext(i, y) != 0) return false;
            }
            return true;
        }

        /// <summary>Whether the row below <paramref name="y"/> lies in the next stripe and is therefore unseen (§D.7).</summary>
        private bool BelowIsCut(int y) => _causal && (y & 3) == 3;

        /// <summary>Table D.1: the sums ΣH, ΣV, ΣD from this state, then the decoder's one statement of the table.</summary>
        internal int ZeroCodingContext(int i, int y)
        {
            int s = _stride;
            int h = (_state[i - 1] & Significant) + (_state[i + 1] & Significant);
            int v = _state[i - s] & Significant;
            int d = (_state[i - s - 1] & Significant) + (_state[i - s + 1] & Significant);
            if (!BelowIsCut(y))
            {
                v += _state[i + s] & Significant;
                d += (_state[i + s - 1] & Significant) + (_state[i + s + 1] & Significant);
            }
            return JpxBlockDecoder.ZeroCodingContext(_orientation, h, v, d);
        }

        /// <summary>Tables D.2 and D.3: the contributions from this state, then the decoder's one statement of the tables (D-1's XOR bit included).</summary>
        internal int SignContext(int i, int y, out int xorBit)
        {
            int hc = Contribution(i - 1) + Contribution(i + 1);
            int vc = Contribution(i - _stride) + (BelowIsCut(y) ? 0 : Contribution(i + _stride));
            return JpxBlockDecoder.SignContext(hc, vc, out xorBit);
        }

        /// <summary>Table D.4: 16 after the first refinement, else 15 with a significant neighbour, else 14.</summary>
        internal int RefinementContext(int i, int y) =>
            (_state[i] & Refined) != 0 ? RefineBase + 2
                : AnySignificantNeighbour(i, y) ? RefineBase + 1 : RefineBase;

        private int Contribution(int i) =>
            (_state[i] & Significant) == 0 ? 0 : (_state[i] & Negative) != 0 ? -1 : 1;

        private bool AnySignificantNeighbour(int i, int y)
        {
            int s = _stride;
            int sum = (_state[i - 1] | _state[i + 1] | _state[i - s] | _state[i - s - 1] | _state[i - s + 1]) & Significant;
            if (!BelowIsCut(y)) sum |= (_state[i + s] | _state[i + s - 1] | _state[i + s + 1]) & Significant;
            return sum != 0;
        }
    }
}
