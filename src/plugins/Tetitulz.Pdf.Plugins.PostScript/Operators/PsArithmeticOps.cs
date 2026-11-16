// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Arithmetic and mathematical operators (PLRM §8.2).
    ///
    /// <para>The semantics here were cross-checked against
    /// <c>Core.Functions.PdfFunctionEvaluator</c>, which implements the same operator set for Type 4
    /// functions and gets several subtle cases right: <c>atan</c> returns <b>degrees</b> in
    /// [0, 360), <c>exp</c> takes base then exponent, and division by zero yields 0 rather than
    /// throwing. Those are copied deliberately rather than re-derived.</para>
    ///
    /// <para>Where this differs from that evaluator, it is because Type 4 is float-only by
    /// specification and PostScript is not: <c>add</c> of two integers is an integer here, and
    /// <c>idiv</c> / <c>mod</c> / <c>bitshift</c> demand integers and raise <c>typecheck</c>
    /// otherwise.</para>
    /// </summary>
    internal static class PsArithmeticOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("add", i => Binary(i, "add", (a, b) => a + b));
            op("sub", i => Binary(i, "sub", (a, b) => a - b));
            op("mul", i => Binary(i, "mul", (a, b) => a * b));
            op("div", Div);
            op("idiv", IDiv);
            op("mod", Mod);
            op("neg", i => Unary(i, "neg", a => -a));
            op("abs", i => Unary(i, "abs", Math.Abs));

            // ceiling/floor/round/truncate preserve the operand's type: PLRM says the result is an
            // integer if the operand was, and a real (with an integral value) if it was a real.
            op("ceiling",  i => UnaryRounding(i, "ceiling",  Math.Ceiling));
            op("floor",    i => UnaryRounding(i, "floor",    Math.Floor));
            // PLRM round: a tie goes to the greater integer (-6.5 round is -6.0).
            op("round",    i => UnaryRounding(i, "round",    d => Math.Floor(d) + (d - Math.Floor(d) >= 0.5 ? 1 : 0)));
            op("truncate", i => UnaryRounding(i, "truncate", Math.Truncate));

            // PLRM: `sqrt` of a negative number, `ln` and `log` of zero or a negative number are
            // rangecheck errors — an error the program can catch with `stopped`, and that `$error`
            // records. They were clamped instead (sqrt to 0, the logarithms to the smallest positive
            // double), so `{ -1 sqrt } stopped` answered false where every real interpreter answers
            // true, and `0 ln` pushed -744.44 — a number nothing in the program asked for — into
            // whatever coordinate came next.
            op("sqrt", i => UnaryReal(i, "sqrt", a => a >= 0, Math.Sqrt));
            op("sin",  i => UnaryReal(i, "sin",  a => Math.Sin(a * Math.PI / 180.0)));
            op("cos",  i => UnaryReal(i, "cos",  a => Math.Cos(a * Math.PI / 180.0)));
            op("atan", Atan);
            op("exp",  Exp);
            op("ln",   i => UnaryReal(i, "ln",  a => a > 0, Math.Log));
            op("log",  i => UnaryReal(i, "log", a => a > 0, Math.Log10));

            op("rand", Rand);
            op("srand", SRand);
            op("rrand", i => i.Operands.Push(PsObject.Int(_randState)));
        }

        // ------------------------------------------------------------------
        //  Shapes
        // ------------------------------------------------------------------

        /// <summary>
        /// Applies <paramref name="f"/>, keeping the result an integer when both operands were.
        /// PostScript Level 2 integers are 32-bit and an overflowing integer operation converts to
        /// real (PLRM §3.2.1) rather than wrapping — so the range check is the specified behaviour,
        /// not a safety bolt-on.
        /// </summary>
        private static void Binary(PsInterpreter i, string name, Func<double, double, double> f)
        {
            var b = i.Operands.Pop();
            var a = i.Operands.Pop();
            if (!a.IsNumber || !b.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, name);

            double r = f(a.Num, b.Num);
            bool bothInt = a.Type == PsType.Integer && b.Type == PsType.Integer;

            i.Operands.Push(bothInt && r >= int.MinValue && r <= int.MaxValue && r == Math.Floor(r)
                ? PsObject.Int(SaturatingCast.ToLong(r))
                : PsObject.Real(r));
        }

        private static void Unary(PsInterpreter i, string name, Func<double, double> f)
        {
            var a = i.Operands.Pop();
            if (!a.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, name);

            double r = f(a.Num);
            i.Operands.Push(a.Type == PsType.Integer && r >= int.MinValue && r <= int.MaxValue
                ? PsObject.Int(SaturatingCast.ToLong(r))
                : PsObject.Real(r));
        }

        private static void UnaryRounding(PsInterpreter i, string name, Func<double, double> f)
        {
            var a = i.Operands.Pop();
            if (!a.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, name);
            if (a.Type == PsType.Integer) { i.Operands.Push(a); return; }   // already integral
            i.Operands.Push(PsObject.Real(f(a.Num)));
        }

        /// <summary>Always yields a real, whatever the operand was (<c>sin</c>, <c>cos</c>, …).</summary>
        private static void UnaryReal(PsInterpreter i, string name, Func<double, double> f)
        {
            var a = i.Operands.Pop();
            if (!a.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, name);
            i.Operands.Push(PsObject.Real(f(a.Num)));
        }

        /// <summary>
        /// <see cref="UnaryReal(PsInterpreter, string, Func{double, double})"/> for an operator
        /// with a domain (<c>sqrt</c>, <c>ln</c>, <c>log</c>): an operand outside
        /// <paramref name="inDomain"/> is a <c>rangecheck</c>, never clamped into it.
        /// </summary>
        private static void UnaryReal(PsInterpreter i, string name, Func<double, bool> inDomain,
                                      Func<double, double> f)
        {
            var a = i.Operands.Pop();
            if (!a.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, name);
            if (!inDomain(a.Num))
                throw new PsErrorException(PsErrorKind.RangeCheck, name,
                    a.Num.ToString(System.Globalization.CultureInfo.InvariantCulture));
            i.Operands.Push(PsObject.Real(f(a.Num)));
        }

        // ------------------------------------------------------------------
        //  The ones with their own rules
        // ------------------------------------------------------------------

        /// <summary><c>div</c> always yields a real, even for two integers that divide evenly.</summary>
        private static void Div(PsInterpreter i)
        {
            var b = i.Operands.Pop();
            var a = i.Operands.Pop();
            if (!a.IsNumber || !b.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, "div");

            // PLRM says undefinedresult; PdfFunctionEvaluator pushes 0 instead, because a shading
            // that divides by zero should still produce a colour. Here a raised error is
            // catchable by `stopped` and recoverable at top level, so the spec behaviour is safe
            // to keep — and a program that divides by zero has a bug worth surfacing.
            if (b.Num == 0) throw new PsErrorException(PsErrorKind.UndefinedResult, "div");

            i.Operands.Push(PsObject.Real(a.Num / b.Num));
        }

        private static void IDiv(PsInterpreter i)
        {
            int b = i.Operands.PopInt("idiv");
            int a = i.Operands.PopInt("idiv");
            if (b == 0) throw new PsErrorException(PsErrorKind.UndefinedResult, "idiv");

            // int.MinValue / -1 overflows a 32-bit int; PostScript's integers are 32-bit too, so
            // the result converts to real rather than trapping.
            if (a == int.MinValue && b == -1) { i.Operands.Push(PsObject.Real(-(double)int.MinValue)); return; }

            i.Operands.Push(PsObject.Int(a / b));
        }

        private static void Mod(PsInterpreter i)
        {
            int b = i.Operands.PopInt("mod");
            int a = i.Operands.PopInt("mod");
            if (b == 0) throw new PsErrorException(PsErrorKind.UndefinedResult, "mod");
            if (a == int.MinValue && b == -1) { i.Operands.Push(PsObject.Int(0)); return; }
            i.Operands.Push(PsObject.Int(a % b));
        }

        /// <summary>
        /// <c>num den atan angle</c> — the angle in <b>degrees</b>, normalised to [0, 360).
        /// </summary>
        private static void Atan(PsInterpreter i)
        {
            double den = i.Operands.PopNumber("atan");
            double num = i.Operands.PopNumber("atan");
            if (num == 0 && den == 0) throw new PsErrorException(PsErrorKind.UndefinedResult, "atan");

            double deg = Math.Atan2(num, den) * 180.0 / Math.PI;
            if (deg < 0) deg += 360.0;
            i.Operands.Push(PsObject.Real(deg));
        }

        /// <summary><c>base exponent exp result</c> — note the operand order.</summary>
        private static void Exp(PsInterpreter i)
        {
            double exponent = i.Operands.PopNumber("exp");
            double @base    = i.Operands.PopNumber("exp");

            double r = Math.Pow(@base, exponent);
            if (double.IsNaN(r) || double.IsInfinity(r))
                throw new PsErrorException(PsErrorKind.UndefinedResult, "exp");

            i.Operands.Push(PsObject.Real(r));
        }

        // ------------------------------------------------------------------
        //  Pseudo-random
        // ------------------------------------------------------------------

        // A FIXED seed, deliberately — not Environment.TickCount. A conversion has to be
        // byte-reproducible: the same PostScript must yield the same content stream every run, or
        // no test can assert on the output and no caller can diff two conversions.
        //
        // [ThreadStatic] ALONE DOES NOT DELIVER THAT, and for a while this did not: per-thread
        // storage is not per-document scope. The generator carried its position out of one
        // conversion and into the next on the same thread, so converting the same program twice in
        // one process produced two different content streams — exactly the guarantee above,
        // broken. CLAUDE.md records the same shape as a decryption bug (LastPermsValidation).
        // A slot a caller observes per operation has to be reset AT THE START of that operation;
        // ResetPerConversionState is where that happens, and both public entry points call it.
        private const int DefaultSeed = 1;
        [ThreadStatic] private static int _randStateStorage;
        [ThreadStatic] private static bool _randSeeded;

        private static int _randState
        {
            get { if (!_randSeeded) { _randStateStorage = DefaultSeed; _randSeeded = true; } return _randStateStorage; }
            set { _randStateStorage = value; _randSeeded = true; }
        }

        /// <summary>
        /// Re-seeds the generator. Called by the facade on entry to every conversion, so <c>rand</c>
        /// yields the same sequence for the same program however many conversions preceded it on
        /// this thread.
        /// </summary>
        internal static void ResetPerConversionState()
        {
            _randStateStorage = DefaultSeed;
            _randSeeded = true;
        }

        /// <summary>A deterministic 31-bit LCG; the exact sequence is unspecified by the PLRM.</summary>
        private static void Rand(PsInterpreter i)
        {
            int next = unchecked(_randState * 1103515245 + 12345);
            _randState = next;
            i.Operands.Push(PsObject.Int(next & 0x7FFFFFFF));
        }

        private static void SRand(PsInterpreter i)
        {
            _randState = i.Operands.PopInt("srand");
        }
    }
}
