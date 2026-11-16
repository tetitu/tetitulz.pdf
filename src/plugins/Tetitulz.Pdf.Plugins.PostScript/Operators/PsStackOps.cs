// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>Operand-stack manipulation (PLRM §8.1).</summary>
    internal static class PsStackOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("pop",   i => i.Operands.Pop());
            op("exch",  Exch);
            op("dup",   i => i.Operands.Push(i.Operands.Peek()));
            op("copy",  Copy);
            op("index", Index);
            op("roll",  Roll);
            op("clear", i => i.Operands.Clear());
            op("count", i => i.Operands.Push(PsObject.Int(i.Operands.Count)));
            op("mark",  i => i.Operands.Push(PsObject.Mark));
            op("cleartomark", ClearToMark);
            op("counttomark", CountToMark);

            // '[' is literally the mark operator — PLRM §8.1 defines them as the same thing.
            op("[", i => i.Operands.Push(PsObject.Mark));
        }

        private static void Exch(PsInterpreter i)
        {
            var b = i.Operands.Pop();
            var a = i.Operands.Pop();
            i.Operands.Push(b);
            i.Operands.Push(a);
        }

        /// <summary>
        /// <c>copy</c> is two operators sharing a name: with an integer on top it duplicates the top
        /// n operands; with a composite it copies one composite into another. Only the first is
        /// implemented here — the composite form lives with the array/string operators.
        /// </summary>
        private static void Copy(PsInterpreter i)
        {
            var top = i.Operands.Peek();
            if (top.Type != PsType.Integer)
            {
                PsArrayStringOps.CopyComposite(i);
                return;
            }

            int n = i.Operands.PopInt("copy");
            if (n < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "copy");
            if (n > i.Operands.Count) throw new PsErrorException(PsErrorKind.StackUnderflow, "copy");

            // Charge the work before doing it: `count copy` inside a loop is quadratic growth.
            i.Tick(n);

            // Snapshot first — pushing while reading would copy the copies.
            var snapshot = new PsObject[n];
            for (int k = 0; k < n; k++) snapshot[k] = i.Operands.Peek(n - 1 - k);
            for (int k = 0; k < n; k++) i.Operands.Push(snapshot[k]);
        }

        private static void Index(PsInterpreter i)
        {
            int n = i.Operands.PopInt("index");
            if (n < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "index");
            i.Operands.Push(i.Operands.Peek(n));
        }

        /// <summary>
        /// <c>n j roll</c> — rotate the top n operands by j, positive j moving toward the top.
        /// </summary>
        private static void Roll(PsInterpreter i)
        {
            int j = i.Operands.PopInt("roll");
            int n = i.Operands.PopInt("roll");

            if (n < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "roll");
            if (n == 0) return;
            if (n > i.Operands.Count) throw new PsErrorException(PsErrorKind.StackUnderflow, "roll");

            i.Tick(n);

            var window = new PsObject[n];
            for (int k = 0; k < n; k++) window[k] = i.Operands.Peek(n - 1 - k);

            // Normalise j into [0, n) for both signs — C# % keeps the sign of the dividend.
            int shift = ((j % n) + n) % n;
            if (shift != 0)
            {
                var rolled = new PsObject[n];
                for (int k = 0; k < n; k++) rolled[(k + shift) % n] = window[k];
                window = rolled;
            }

            for (int k = 0; k < n; k++) i.Operands.Set(n - 1 - k, window[k]);
        }

        /// <summary>
        /// Index of the topmost mark, or -1.
        ///
        /// <para><b>The scan is charged here, before it runs, because the not-found path has no
        /// result to charge afterwards.</b> Charging only what is found would leave a full-depth
        /// scan that ends in <c>unmatchedmark</c> invisible to the budget: <c>mark 0 1 9997 { } for
        /// { counttomark pop } loop</c> would perform ~9 999 comparisons per iteration while charging
        /// about four ticks, and the op budget would permit millions of iterations — an operator
        /// that re-reads the whole stack costs O(stack) and must be charged O(stack), as
        /// <c>pathbbox</c> and <c>clip</c> are for the path.</para>
        ///
        /// <para>The callers' own <c>Tick(mark)</c> stays: it charges <i>building</i> the array or
        /// dictionary, which is separate work from finding the mark.</para>
        /// </summary>
        internal static int FindMark(PsInterpreter i)
        {
            i.Tick(i.Operands.Count);

            for (int k = 0; k < i.Operands.Count; k++)
                if (i.Operands.Peek(k).Type == PsType.Mark) return k;
            return -1;
        }

        private static void ClearToMark(PsInterpreter i)
        {
            int mark = FindMark(i);
            if (mark < 0) throw new PsErrorException(PsErrorKind.UnmatchedMark, "cleartomark");
            for (int k = 0; k <= mark; k++) i.Operands.Pop();
        }

        private static void CountToMark(PsInterpreter i)
        {
            int mark = FindMark(i);
            if (mark < 0) throw new PsErrorException(PsErrorKind.UnmatchedMark, "counttomark");
            i.Operands.Push(PsObject.Int(mark));
        }
    }
}
