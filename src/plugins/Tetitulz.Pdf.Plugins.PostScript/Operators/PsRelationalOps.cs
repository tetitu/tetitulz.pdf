// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Relational, boolean and bitwise operators (PLRM §8.3).
    ///
    /// <para><b>The boolean operators are genuinely dual.</b> <c>and</c>, <c>or</c>, <c>xor</c> and
    /// <c>not</c> are logical on booleans and <b>bitwise</b> on integers — <c>5 3 and</c> is 1, not
    /// a typecheck. <c>Core.Functions.PdfFunctionEvaluator</c> implements only the boolean half,
    /// which is correct for Type 4 (whose operands are all numbers-as-booleans) and would silently
    /// give the wrong answer for the integer masks real PostScript uses.</para>
    /// </summary>
    internal static class PsRelationalOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            // Popped into locals rather than written as Equal(Pop(), Pop()). That form is correct
            // today — C# evaluates arguments left to right and PsObject.Equals is symmetric — but it
            // makes the reader re-derive both premises, and `eq` is the operator most likely to grow
            // an asymmetric arm later. Pure readability; no input behaves differently.
            op("eq", i => Compare(i, equal: true));
            op("ne", i => Compare(i, equal: false));

            op("gt", i => Compare(i, "gt", c => c >  0));
            op("ge", i => Compare(i, "ge", c => c >= 0));
            op("lt", i => Compare(i, "lt", c => c <  0));
            op("le", i => Compare(i, "le", c => c <= 0));

            op("and", i => Logical(i, "and", (a, b) => a & b, (a, b) => a && b));
            op("or",  i => Logical(i, "or",  (a, b) => a | b, (a, b) => a || b));
            op("xor", i => Logical(i, "xor", (a, b) => a ^ b, (a, b) => a ^ b));
            op("not", Not);

            op("bitshift", BitShift);

            op("true",  i => i.Operands.Push(PsObject.True));
            op("false", i => i.Operands.Push(PsObject.False));
        }

        private static void PushBool(PsInterpreter i, bool v) => i.Operands.Push(PsObject.Bool(v));

        /// <summary><c>eq</c> / <c>ne</c>; the semantics live on <see cref="PsObject.Equals(PsObject)"/>.</summary>
        /// <remarks>
        /// Two strings are compared by content, so the comparison is charged its length, as every other walk of a string
        /// is (<see cref="ChargeStrings"/>): uncharged, <c>a b eq</c> over two equal million-byte strings was one step of
        /// the twenty million and five milliseconds of work, and a loop of it ran for hours inside the budget.
        /// </remarks>
        private static void Compare(PsInterpreter i, bool equal)
        {
            var b = i.Operands.Pop();
            var a = i.Operands.Pop();
            ChargeStrings(i, a, b);
            PushBool(i, a.Equals(b) == equal);
        }

        /// <summary>Charges a comparison of two strings the bytes it may read — the shorter's length; nothing for any other pair.</summary>
        private static void ChargeStrings(PsInterpreter i, PsObject a, PsObject b)
        {
            if (a.Type == PsType.String && b.Type == PsType.String)
                i.Tick(Math.Max(1, Math.Min(a.AsString().Length, b.AsString().Length)));
        }

        /// <summary>
        /// <c>gt/ge/lt/le</c> compare two numbers, or two strings lexicographically by byte —
        /// anything else is a typecheck.
        /// </summary>
        private static void Compare(PsInterpreter i, string name, Func<int, bool> accept)
        {
            var b = i.Operands.Pop();
            var a = i.Operands.Pop();

            if (a.IsNumber && b.IsNumber)
            {
                PushBool(i, accept(a.Num.CompareTo(b.Num)));
                return;
            }

            if (a.Type == PsType.String && b.Type == PsType.String)
            {
                ChargeStrings(i, a, b);
                PushBool(i, accept(CompareStrings(a.AsString(), b.AsString())));
                return;
            }

            throw new PsErrorException(PsErrorKind.TypeCheck, name);
        }

        private static int CompareStrings(PsString a, PsString b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int k = 0; k < n; k++)
            {
                int d = a[k].CompareTo(b[k]);
                if (d != 0) return d;
            }
            return a.Length.CompareTo(b.Length);
        }

        private static void Logical(PsInterpreter i, string name,
                                    Func<int, int, int> bitwise, Func<bool, bool, bool> logical)
        {
            var b = i.Operands.Pop();
            var a = i.Operands.Pop();

            if (a.Type == PsType.Boolean && b.Type == PsType.Boolean)
            {
                PushBool(i, logical(a.AsBool(), b.AsBool()));
                return;
            }

            if (a.Type == PsType.Integer && b.Type == PsType.Integer)
            {
                i.Operands.Push(PsObject.Int(bitwise(a.AsInt(), b.AsInt())));
                return;
            }

            throw new PsErrorException(PsErrorKind.TypeCheck, name);
        }

        private static void Not(PsInterpreter i)
        {
            var a = i.Operands.Pop();
            if (a.Type == PsType.Boolean) { PushBool(i, !a.AsBool()); return; }
            if (a.Type == PsType.Integer) { i.Operands.Push(PsObject.Int(~a.AsInt())); return; }
            throw new PsErrorException(PsErrorKind.TypeCheck, "not");
        }

        /// <summary>
        /// <c>int shift bitshift</c> — left for a positive shift, right for a negative one.
        /// A shift of 32 or more is undefined in C# (it masks to 5 bits), so it is clamped to 0
        /// here rather than producing the surprising wrap-around the CLR would give.
        /// </summary>
        private static void BitShift(PsInterpreter i)
        {
            int shift = i.Operands.PopInt("bitshift");
            int value = i.Operands.PopInt("bitshift");

            int result;
            if (shift >= 32 || shift <= -32) result = 0;
            else if (shift >= 0) result = value << shift;
            else result = (int)((uint)value >> -shift);   // PLRM: right shift is logical, not arithmetic

            i.Operands.Push(PsObject.Int(result));
        }
    }
}
