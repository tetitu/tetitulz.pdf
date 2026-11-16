// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>Thrown when a <see cref="WorkBudget"/> is exhausted — i.e. an operation
    /// performed more work than allowed while processing untrusted input.</summary>
    public sealed class WorkBudgetExceededException : PdfException
    {
        public WorkBudgetExceededException(string message) : base(message) { }

        internal WorkBudgetExceededException(string message, WorkBudget budget) : base(message) { Budget = budget; }

        /// <summary>
        /// The budget that refused — its own, or the parent it forwards to. A decoder that turns
        /// its own budget's refusal into a reported fallback lets a parent's refusal through by
        /// comparing this to the parent it was given.
        /// </summary>
        public WorkBudget Budget { get; }
    }

    /// <summary>
    /// A reusable aggregate work budget for bounding loops / iterations when processing
    /// UNTRUSTED input, so an amplification ("loop bomb") cannot spin the CPU indefinitely.
    ///
    /// <para>Per-loop caps do NOT compose: three nested loops each individually capped at 1e6
    /// still multiply to 1e18 body executions. The fix is ONE budget for the whole operation,
    /// ticked in every loop iteration / per unit of work; when it is exhausted it throws
    /// <see cref="WorkBudgetExceededException"/>, unwinding every nested loop at once. This
    /// centralises the otherwise-scattered <c>if (++n &gt; MAX) throw</c> guards so the aggregate
    /// bound is a single, testable object rather than ad-hoc counters per call site.</para>
    ///
    /// <para>Use this for the <i>tick-style</i> bound (count work as it happens, e.g. the Type 4
    /// PostScript calculator's repeat/for loops). It is intentionally NOT used where the bound
    /// can be computed up front (e.g. CCITT clamps its row count, JBIG2 validates instance counts,
    /// the parser caps xref entries) — that compute-max-before-allocating shape is the right
    /// pattern for those and needs no per-iteration object.</para>
    ///
    /// Not thread-safe by design: create one budget per operation (per thread/call). The one
    /// exception is a budget made <i>shared</i> (<see cref="WorkBudget(long, string, bool)"/>), whose
    /// ticks are atomic so that workers splitting one operation charge one total — the JPEG 2000
    /// decoder's tiles across threads; every other budget pays nothing for it.
    ///
    /// <para><b>Not the same thing as <c>Tetitulz.Pdf.Base.Operations.WalkBudget</c>.</b> This one
    /// <i>throws</i> when the budget runs out, which aborts the operation; <c>WalkBudget</c>
    /// returns <c>false</c> so the caller can return a partial result and report it as partial.
    /// They are two policies, not two copies — pick by what the caller must do when the work does
    /// not fit.</para>
    /// </summary>
    public sealed class WorkBudget
    {
        private readonly long _total;
        private readonly string _what;
        private readonly bool _shared;
        private readonly WorkBudget _parent;
        private long _remaining;

        /// <param name="budget">Maximum units of work allowed (negative is treated as 0).</param>
        /// <param name="what">Short label naming the bounded operation, used in the exception.</param>
        public WorkBudget(long budget, string what = "operation") : this(budget, what, false) { }

        /// <param name="budget">Maximum units of work allowed (negative is treated as 0).</param>
        /// <param name="what">Short label naming the bounded operation, used in the exception.</param>
        /// <param name="shared">True for a budget several threads tick at once: each tick is then atomic, and <see cref="Spent"/> is the sum of all of them.</param>
        public WorkBudget(long budget, string what, bool shared) : this(budget, what, shared, null) { }

        /// <param name="budget">Maximum units of work allowed (negative is treated as 0).</param>
        /// <param name="what">Short label naming the bounded operation, used in the exception.</param>
        /// <param name="shared">True for a budget several threads tick at once: each tick is then atomic, and <see cref="Spent"/> is the sum of all of them.</param>
        /// <param name="parent">
        /// A budget this one also spends — a page's, where this one is a stream's. Every tick is
        /// forwarded to it, clamped to what this budget had left, so a parent is charged what its
        /// children spent and never a child's overrun; the parent's refusal is thrown as its own
        /// (<see cref="WorkBudgetExceededException.Budget"/>) and comes before this budget's check.
        /// A JBIG2 stream of a few dozen bytes spends its whole stream budget — the composition's
        /// 128 Mi pixels, about three seconds — so without a budget of the page's own, a page of such
        /// images would cost that many times three seconds.
        /// </param>
        public WorkBudget(long budget, string what, bool shared, WorkBudget parent)
        {
            _total = budget < 0 ? 0 : budget;
            _remaining = _total;
            _what = string.IsNullOrEmpty(what) ? "operation" : what;
            _shared = shared;
            _parent = parent;
        }

        /// <summary>Whether ticks from several threads are safe on this budget.</summary>
        public bool IsShared => _shared;

        /// <summary>The budget this one also spends, or null.</summary>
        public WorkBudget Parent => _parent;

        /// <summary>Total budget the instance was created with.</summary>
        public long Total => _total;

        /// <summary>Units consumed so far.</summary>
        public long Spent => _total - _remaining;

        /// <summary>True once the budget is exhausted (a subsequent <see cref="Tick"/> throws).</summary>
        public bool IsExhausted => _remaining <= 0;

        /// <summary>
        /// Consumes <paramref name="cost"/> units (default 1) and throws
        /// <see cref="WorkBudgetExceededException"/> once the budget goes negative. Call this
        /// once per loop iteration (and/or per unit of work) inside an untrusted-input loop.
        /// </summary>
        /// <remarks>
        /// <b>A negative cost is charged as nothing, never as a credit.</b> A cost is most often a
        /// product of a file's own fields, and a field that has gone negative makes the product
        /// negative. Taken as it was, the tick added that much to the budget and to its parent. A
        /// JBIG2 symbol width that ran to −2 147 483 600 would credit a parser's whole-document budget
        /// with 38 G units, and the next stream decode its full 128 Mi under it. What was spent stays
        /// spent.
        /// </remarks>
        public void Tick(long cost = 1)
        {
            if (cost < 0) cost = 0;
            if (_parent != null)
            {
                // What this budget can still pay is what the parent is charged: a tick that
                // overruns this budget is refused below, and the parent must not pay the overrun.
                long left = _shared ? System.Threading.Interlocked.Read(ref _remaining) : _remaining;
                _parent.Tick(Math.Min(cost, Math.Max(0L, left)));
            }
            long remaining = _shared ? System.Threading.Interlocked.Add(ref _remaining, -cost) : (_remaining -= cost);
            if (remaining < 0)
                throw new WorkBudgetExceededException(
                    $"Work budget for {_what} exceeded ({_total} units); adversarial / amplifying input.", this);
        }
    }
}
