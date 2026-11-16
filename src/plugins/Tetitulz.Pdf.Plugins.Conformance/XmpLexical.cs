// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Plugins.Conformance
{
    /// <summary>
    /// The lexical forms of the XMP Specification's simple value types (January 2004 and June 2005
    /// editions, "Basic Value Types"), read by hand rather than by a pattern: leading and trailing
    /// XML white space is allowed, nothing else outside the form.
    /// </summary>
    internal static class XmpLexical
    {
        /// <summary>Integer: an optional sign, then decimal digits.</summary>
        internal static bool IsInteger(string text)
        {
            string s = Trim(text);
            int i = SkipSign(s, 0);
            return Digits(s, ref i) > 0 && i == s.Length;
        }

        /// <summary>Real: an optional sign, digits, and optionally a period and more digits.</summary>
        internal static bool IsReal(string text)
        {
            string s = Trim(text);
            int i = SkipSign(s, 0);
            if (Digits(s, ref i) == 0) return false;
            if (i < s.Length && s[i] == '.')
            {
                i++;
                Digits(s, ref i);
            }
            return i == s.Length;
        }

        /// <summary>Rational: a signed integer numerator, a slash, and a signed integer denominator.</summary>
        internal static bool IsRational(string text)
        {
            string s = Trim(text);
            int slash = s.IndexOf('/');
            return slash > 0 && IsInteger(s.Substring(0, slash)) && IsInteger(s.Substring(slash + 1));
        }

        /// <summary>Boolean: <c>True</c> or <c>False</c>, as the specification spells them.</summary>
        internal static bool IsBoolean(string text)
        {
            string s = Trim(text);
            return s == "True" || s == "False";
        }

        /// <summary>
        /// Date: the W3C profile of ISO 8601 the specification names — <c>YYYY</c>, <c>YYYY-MM</c>,
        /// <c>YYYY-MM-DD</c>, then optionally <c>Thh:mm</c>, <c>:ss</c> and a fraction, and a time zone
        /// (<c>Z</c> or <c>+hh:mm</c> / <c>-hh:mm</c>).
        /// </summary>
        internal static bool IsDate(string text)
        {
            string s = Trim(text);
            int i = 0;
            if (!Fixed(s, ref i, 4)) return false;
            if (i == s.Length) return true;
            if (!Literal(s, ref i, '-') || !Fixed(s, ref i, 2) || !InRange(s, i - 2, 1, 12)) return false;
            if (i == s.Length) return true;
            if (!Literal(s, ref i, '-') || !Fixed(s, ref i, 2) || !InRange(s, i - 2, 1, 31)) return false;
            if (i == s.Length) return true;
            if (!Literal(s, ref i, 'T') || !Fixed(s, ref i, 2) || !InRange(s, i - 2, 0, 23)) return false;
            if (!Literal(s, ref i, ':') || !Fixed(s, ref i, 2) || !InRange(s, i - 2, 0, 59)) return false;
            if (i < s.Length && s[i] == ':')
            {
                i++;
                if (!Fixed(s, ref i, 2) || !InRange(s, i - 2, 0, 60)) return false;
                if (i < s.Length && s[i] == '.')
                {
                    i++;
                    if (Digits(s, ref i) == 0) return false;
                }
            }
            if (i == s.Length) return true;
            if (s[i] == 'Z') return i + 1 == s.Length;
            if (s[i] != '+' && s[i] != '-') return false;
            i++;
            return Fixed(s, ref i, 2) && InRange(s, i - 2, 0, 23)
                && Literal(s, ref i, ':') && Fixed(s, ref i, 2) && InRange(s, i - 2, 0, 59)
                && i == s.Length;
        }

        private static string Trim(string text) => (text ?? "").Trim(' ', '\t', '\r', '\n');

        private static int SkipSign(string s, int i) => i < s.Length && (s[i] == '+' || s[i] == '-') ? i + 1 : i;

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static int Digits(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && IsDigit(s[i])) i++;
            return i - start;
        }

        private static bool Fixed(string s, ref int i, int count)
        {
            if (i + count > s.Length) return false;
            for (int k = 0; k < count; k++)
                if (!IsDigit(s[i + k])) return false;
            i += count;
            return true;
        }

        private static bool Literal(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) return false;
            i++;
            return true;
        }

        private static bool InRange(string s, int at, int min, int max)
        {
            int value = (s[at] - '0') * 10 + (s[at + 1] - '0');
            return value >= min && value <= max;
        }
    }
}
