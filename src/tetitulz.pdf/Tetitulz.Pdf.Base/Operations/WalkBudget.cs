// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// A step budget for a bounded document traversal: work is charged before it is done, and once
    /// refused the budget stays <see cref="Exhausted"/>, so a caller can tell "finished" from "gave
    /// up part-way".
    /// </summary>
    /// <remarks>
    /// <para>Moved down from <c>Tetitulz.Pdf.Accessibility</c> ahead of the font-embedding
    /// remediator (plan 04 D2, dependency layer 6). It is <b>shared rather than copied</b> because
    /// the glyph-usage walk and the font-site walk each need one and the two must charge
    /// identically: a budget generous on one side and strict on the other silently changes which
    /// documents get their fonts subsetted, which is a difference in output nobody would think to
    /// look for.</para>
    ///
    /// <para><b>Not the same thing as <see cref="Tetitulz.Pdf.Core.Util.WorkBudget"/>, and not a
    /// duplicate of it.</b> They encode two different policies, and which one a caller needs is a
    /// real decision: <c>WorkBudget</c> <i>throws</i> when it runs out, aborting the whole
    /// operation; <c>WalkBudget</c> returns <c>false</c> and sets <see cref="Exhausted"/>, so the
    /// caller can return a partial result <i>and say that it is partial</i>. Merging them would
    /// force one caller class to change its failure mode. Pick by what the caller must do when the
    /// work does not fit.</para>
    ///
    /// <para>Not thread-safe, and not meant to be: build and use one per operation.</para>
    /// </remarks>
    public sealed class WalkBudget
    {
        private int _remaining;

        public WalkBudget(int steps) { _remaining = steps; }

        /// <summary>The steps left: what a caller may size a decode to before it is charged for one.</summary>
        public int Remaining => _remaining;

        /// <summary>True once <see cref="Take()"/> has been refused at least once.</summary>
        public bool Exhausted { get; private set; }

        /// <summary>Consumes one step; false when the budget is spent.</summary>
        public bool Take()
        {
            if (_remaining <= 0)
            {
                Exhausted = true;
                return false;
            }
            _remaining--;
            return true;
        }

        /// <summary>
        /// Consumes <paramref name="units"/> steps at once; false when the budget cannot cover
        /// them, in which case nothing is consumed and the budget is marked exhausted.
        ///
        /// <para>Work whose cost is proportional to a stream's <i>length</i> has to be charged
        /// before it is done. Charging per token produced is not the same thing: a stream that
        /// yields no tokens at all — pure whitespace, or nothing but <c>%</c> comments — still
        /// costs a full tokenising pass plus a token array proportional to its size, and would
        /// otherwise be re-scanned free of charge on every invocation.</para>
        /// </summary>
        public bool Take(int units)
        {
            if (units < 1) units = 1;
            if (_remaining < units)
            {
                _remaining = 0;
                Exhausted  = true;
                return false;
            }
            _remaining -= units;
            return true;
        }
    }
}
