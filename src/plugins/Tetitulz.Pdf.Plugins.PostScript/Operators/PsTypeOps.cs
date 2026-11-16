// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Globalization;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Type, attribute and conversion operators (PLRM §8.7).
    ///
    /// <para><c>type</c> is genuinely observable — real prologues branch on it to decide whether a
    /// resource is a name or a dictionary — which is one of the reasons the integer/real
    /// distinction has to be carried through the whole object model rather than collapsed to
    /// <c>double</c>.</para>
    /// </summary>
    internal static class PsTypeOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            op("type", i => i.Operands.Push(PsObject.ExecName(i.Operands.Pop().TypeName)));

            op("cvi", Cvi);
            op("cvr", Cvr);
            op("cvn", Cvn);
            op("cvs", Cvs);
            op("cvrs", Cvrs);
            op("cvx", i => i.Operands.Push(i.Operands.Pop().WithExecutable(true)));
            op("cvlit", i => i.Operands.Push(i.Operands.Pop().WithExecutable(false)));
            op("xcheck", i => i.Operands.Push(PsObject.Bool(i.Operands.Pop().IsExecutable)));

            // Access attributes. This interpreter has no read-only objects — nothing it builds can
            // be marked so, and `readonly` is used by prologues purely as a hint — so the checks
            // answer "yes, you may" rather than pretending to enforce something.
            op("readonly",       Identity);
            op("executeonly",    Identity);
            op("noaccess",       Identity);
            op("rcheck",         i => { i.Operands.Pop(); i.Operands.Push(PsObject.True); });
            op("wcheck",         i => { i.Operands.Pop(); i.Operands.Push(PsObject.True); });

            op("null", i => i.Operands.Push(PsObject.Null));
        }

        private static void Identity(PsInterpreter i) { /* operand stays as-is */ }

        /// <summary>
        /// <c>cvi</c> truncates toward zero; a string operand is parsed first. Saturating rather
        /// than wrapping, because a real can legally hold 1e300 and an unchecked cast would turn
        /// that into a small positive number.
        /// </summary>
        private static void Cvi(PsInterpreter i)
        {
            var o = i.Operands.Pop();

            if (o.Type == PsType.String)
            {
                double parsed = ParseNumericString(i, o.AsString(), "cvi");
                i.Operands.Push(PsObject.Int(Saturate(Math.Truncate(parsed))));
                return;
            }

            if (!o.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, "cvi");
            i.Operands.Push(PsObject.Int(Saturate(Math.Truncate(o.Num))));
        }

        private static void Cvr(PsInterpreter i)
        {
            var o = i.Operands.Pop();

            if (o.Type == PsType.String)
            {
                i.Operands.Push(PsObject.Real(ParseNumericString(i, o.AsString(), "cvr")));
                return;
            }

            if (!o.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, "cvr");
            i.Operands.Push(PsObject.Real(o.Num));
        }

        private static void Cvn(PsInterpreter i)
        {
            var s = i.Operands.PopString("cvn");
            if (s.Length > PsLimits.MaxNameChars)
                throw new PsErrorException(PsErrorKind.LimitCheck, "cvn", $"a name of {s.Length} characters (PsLimits.MaxNameChars)");
            i.Operands.Push(PsObject.LitName(i.LatinFromOperand(s, "cvn")));
        }

        /// <summary>
        /// <c>any string cvs substring</c> — renders the object into the caller's string buffer and
        /// pushes the populated prefix, which shares that buffer.
        /// </summary>
        private static void Cvs(PsInterpreter i)
        {
            var buffer = i.Operands.PopString("cvs");
            var o      = i.Operands.Pop();
            StoreInto(i, buffer, Render(i, o), "cvs");
        }

        /// <summary>
        /// <c>num radix string cvrs substring</c> — the number in the given base, as an unsigned
        /// quantity, written into the caller's buffer.
        ///
        /// <para>The inverse of the radix literal <c>PsNumberParser</c> reads (<c>16#FFFE</c>), and
        /// unsigned for the same reason: PLRM treats a radix number as a bit pattern, so
        /// <c>-1 16 cvrs</c> is <c>FFFFFFFF</c>, not <c>-1</c>. Base 10 is the one case that keeps
        /// the sign, because there it is ordinary decimal.</para>
        /// </summary>
        private static void Cvrs(PsInterpreter i)
        {
            var buffer = i.Operands.PopString("cvrs");
            int radix  = i.Operands.PopInt("cvrs");
            var value  = i.Operands.Pop();

            if (radix < 2 || radix > 36)
                throw new PsErrorException(PsErrorKind.RangeCheck, "cvrs", $"radix {radix}");
            if (!value.IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, "cvrs");

            StoreInto(i, buffer, Radix(i, value, radix), "cvrs");
        }

        private static string Radix(PsInterpreter i, PsObject value, int radix)
        {
            if (radix == 10) return Render(i, value.Type == PsType.Real ? value : PsObject.Int(value.AsLong()));

            uint bits = unchecked((uint)SaturatingCast.ToInt(value.Num));

            if (bits == 0) return "0";

            const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var chars = new char[32];
            int at = chars.Length;
            while (bits != 0) { chars[--at] = Digits[(int)(bits % (uint)radix)]; bits /= (uint)radix; }
            return new string(chars, at, chars.Length - at);
        }

        /// <summary>
        /// Writes <paramref name="text"/> into the caller's buffer and pushes the populated prefix,
        /// which shares its storage. Shared by <c>cvs</c> and <c>cvrs</c>; the rangecheck message
        /// names both lengths, and that wording is observable.
        /// </summary>
        private static void StoreInto(PsInterpreter i, PsString buffer, string text, string op)
        {
            if (text.Length > buffer.Length)
                throw new PsErrorException(PsErrorKind.RangeCheck, op,
                    $"the {buffer.Length}-byte buffer cannot hold {text.Length} characters");

            for (int k = 0; k < text.Length; k++) buffer[k] = (byte)text[k];
            i.Operands.Push(PsObject.Str(buffer.Interval(0, text.Length)));
        }

        /// <summary>
        /// The text <c>cvs</c> produces. Reals use the shortest round-trippable form with the
        /// invariant culture — a decimal comma would make the emitted content stream unparseable
        /// on a European machine, which is the classic form of this bug.
        ///
        /// <para>The string arm goes through <see cref="PsInterpreter.LatinFromOperand"/> like every
        /// other operand-to-text conversion: <c>cvs</c>'s <c>rangecheck</c> on the destination
        /// buffer fires <b>after</b> the render, so an arbitrarily long source string is materialised
        /// as UTF-16 before anything checks it can fit.</para>
        /// </summary>
        private static string Render(PsInterpreter i, PsObject o) => o.Type switch
        {
            PsType.Integer    => o.AsLong().ToString(CultureInfo.InvariantCulture),
            PsType.Real       => o.Num.ToString("R", CultureInfo.InvariantCulture),
            PsType.Boolean    => o.AsBool() ? "true" : "false",
            PsType.Name       => o.AsName(),
            PsType.String     => i.LatinFromOperand(o.AsString(), "cvs"),
            PsType.Operator   => o.AsOperator().Name,
            PsType.Null       => "null",
            PsType.Mark       => "--nostringval--",
            PsType.Array      => "--nostringval--",
            PsType.Dictionary => "--nostringval--",
            _                 => "--nostringval--"
        };

        /// <summary>
        /// Parses a string operand as a number, charged for the conversion it performs.
        ///
        /// <para><b>The operand is whatever the program put on the stack — there is no buffer and no
        /// bound.</b> The callers are <c>cvi</c> and <c>cvr</c>, which pop an arbitrary string.
        /// <c>AsLatin1</c> allocates a <c>char[]</c> and a <c>string</c> — two bytes of UTF-16 per
        /// input byte — and <c>Trim</c> may allocate a second copy, so uncharged,
        /// <c>/s 1000000 string def { s cvi pop } loop</c> would run a 2 MB allocation and a
        /// 1 000 000-character parse per iteration while charging ~6 ticks and zero allocation
        /// bytes.</para>
        ///
        /// <para><see cref="PsInterpreter.LatinFromOperand"/> is the helper that exists so a call
        /// site cannot forget the allocation charge; the <c>Tick</c> is for the scan, which
        /// <c>double.TryParse</c> performs over the whole string whether or not it parses.</para>
        /// </summary>
        private static double ParseNumericString(PsInterpreter i, PsString s, string op)
        {
            string text = i.LatinFromOperand(s, op).Trim();
            i.Tick(s?.Length ?? 0);

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;

            // The message quotes the operand, so an adversarial 1 MB string would otherwise put a
            // 1 MB message through every OnEvent subscriber and into the [ThreadStatic] slot.
            throw new PsErrorException(PsErrorKind.SyntaxError, op,
                $"'{Ellipsis(text)}' is not a number");
        }

        /// <summary>The first 64 characters of <paramref name="text"/>, for a diagnostic.</summary>
        internal static string Ellipsis(string text)
            => text.Length <= 64 ? text : text.Substring(0, 64) + "…";

        private static long Saturate(double d) => SaturatingCast.ToInt(d);
    }
}
