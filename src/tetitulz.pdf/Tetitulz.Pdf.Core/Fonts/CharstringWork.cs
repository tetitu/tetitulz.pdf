// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The charstring steps an operation may spend across all its glyphs, beside the per-glyph
    /// <see cref="PdfLimits.MaxCharstringSteps"/>: a font of many glyphs whose charstrings all call one
    /// subroutine that runs to the per-glyph cap costs glyphs × 5 M steps from a few hundred bytes —
    /// about 35 ms a glyph on the shipped build, so a page of a thousand such glyphs was 35 s (07's
    /// DoS hunt, F14). A cap on one factor bounds nothing.
    /// </summary>
    /// <remarks>
    /// <para>Two budgets, both asked here. <b>The page's</b> is ambient: both renderers' operator loops
    /// open it (<see cref="SpendFrom"/>, <see cref="PdfLimits.MaxCharstringStepsPerPage"/>), and the
    /// Type 1 and Type 2 evaluators and the CFF width scanner spend it, as a Type 4 function spends the
    /// page's function budget. <b>A font's</b> is the CFF font's own
    /// (<see cref="PdfLimits.MaxCharstringStepsPerFont"/>), spent by the scanner, whose whole-font
    /// passes — a width table, the subsetter's seac walk — run outside any page.</para>
    /// <para>Not a hand-off between operators: the scope is opened and closed around the whole page, on
    /// the thread that runs it.</para>
    /// </remarks>
    internal static class CharstringWork
    {
        [ThreadStatic] private static WorkBudget _page;
        [ThreadStatic] private static bool _reported;

        /// <summary>
        /// The steps the glyphs charged on this thread actually ran, unclamped — the work a test bounds,
        /// since a glyph that ignored its limit is still refused afterwards, having spent what it was not
        /// allowed.
        /// </summary>
        [ThreadStatic] internal static long StepsRun;

        /// <summary>
        /// The steps one glyph may run now: the per-glyph cap, or less when <paramref name="fontLeft"/>
        /// or the open page's budget has less left.
        /// </summary>
        internal static int StepsAllowed(long fontLeft = long.MaxValue)
        {
            long allowed = Math.Min(PdfLimits.MaxCharstringSteps, Math.Max(0L, fontLeft));
            if (_page != null) allowed = Math.Min(allowed, Left(_page));
            return (int)allowed;
        }

        /// <summary>
        /// Charges a glyph's <paramref name="steps"/> to the open page's budget, and says whether a budget
        /// rather than the per-glyph cap stopped it — it was allowed less than the cap and ran past what it
        /// was allowed — in which case the caller must not keep the result as the glyph's.
        /// </summary>
        internal static bool Charge(long steps, int allowed)
        {
            StepsRun += Math.Max(0L, steps);
            if (_page != null) _page.Tick(Math.Min(Math.Max(0L, steps), Left(_page)));
            return allowed < PdfLimits.MaxCharstringSteps && steps > allowed;
        }

        /// <summary>
        /// Whether a glyph our evaluator ran is too heavy to hand to Skia, which runs a program's
        /// charstrings under no budget of ours: a budget cut it short (<paramref name="cutByBudget"/>),
        /// it ran past <see cref="PdfLimits.MaxCharstringStepsForHinting"/>, or it stopped at the
        /// per-glyph cap (a stopped glyph counts one step past its limit) — its real cost is then
        /// unknown, and a host that sets the cap below the hinting threshold would otherwise have
        /// every abandoned glyph judged light and run whole by the platform's engine. The CFF and the
        /// Type 1 glyph sources both ask it.
        /// </summary>
        internal static bool TooHeavyForSkia(int steps, bool cutByBudget)
            => cutByBudget
               || steps > PdfLimits.MaxCharstringStepsForHinting
               || steps > PdfLimits.MaxCharstringSteps;

        /// <summary>True the first time it is asked inside the open scope: the refusal is reported once a page.</summary>
        internal static bool FirstRefusal()
        {
            if (_reported) return false;
            _reported = true;
            return true;
        }

        private static long Left(WorkBudget budget) => Math.Max(0L, budget.Total - budget.Spent);

        /// <summary>The open page's budget, or null — the parent a font-wide check made during the page spends.</summary>
        internal static WorkBudget CurrentPage => _page;

        /// <summary>
        /// A font program's own steps (<see cref="PdfLimits.MaxCharstringStepsPerFont"/>), spent by every
        /// scan of its glyphs; atomic, since a parsed font may be read from several threads.
        /// </summary>
        internal sealed class FontAccount
        {
            private long _left = PdfLimits.MaxCharstringStepsPerFont;

            /// <summary>The steps left.</summary>
            internal long Left => System.Threading.Interlocked.Read(ref _left);

            /// <summary>Spends <paramref name="steps"/> (a negative count spends nothing).</summary>
            internal void Spend(long steps) => System.Threading.Interlocked.Add(ref _left, -Math.Max(0L, steps));
        }

        /// <summary>
        /// Opens a scope in which every glyph evaluated on this thread also spends
        /// <paramref name="work"/>; null opens nothing. Disposing restores the enclosing scope.
        /// </summary>
        internal static Scope SpendFrom(WorkBudget work) => new Scope(work);

        /// <summary>A <see cref="SpendFrom"/> scope.</summary>
        internal readonly struct Scope : IDisposable
        {
            private readonly WorkBudget _previous;
            private readonly bool _previousReported;
            private readonly bool _opened;

            internal Scope(WorkBudget work)
            {
                _previous = _page;
                _previousReported = _reported;
                _opened = work != null && !ReferenceEquals(work, _page);
                if (_opened) { _page = work; _reported = false; }
            }

            public void Dispose()
            {
                if (!_opened) return;
                _page = _previous;
                _reported = _previousReported;
            }
        }
    }
}
