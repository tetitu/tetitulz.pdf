// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Charts
{
    /// <summary>
    /// Formats a chart's numbers using a bounded subset of Excel format codes.
    ///
    /// <para>
    /// <b>A faithful interpreter is deliberately out of scope.</b> The real language is four-section
    /// parsing, <c>0 # ? , . %</c> placeholders, literals and escapes, <c>[Red]</c> colours,
    /// <c>[&gt;100]</c> conditions, scaling commas, fractions and forty-odd locale-sensitive date
    /// tokens — a multi-week component that belongs in Core, not inside a chart renderer. What is
    /// here covers what charts actually carry, and anything else degrades to General rather than
    /// failing.
    /// </para>
    ///
    /// <para>
    /// The important part is <see cref="General"/>, which is <b>tick-aware</b>. Formatting an axis
    /// label with <c>ToString()</c> produces <c>0.30000000000000004</c> on an ordinary 0.1 step,
    /// and a plain rounding produces <c>0 0 0 0</c> on a 0–0.001 axis. Deriving the decimal count
    /// from the step is what avoids both.
    /// </para>
    /// </summary>
    internal static class ExcelNumberFormat
    {
        /// <summary>The decimal places a format code is formatted to, at most — Excel's own limit.</summary>
        internal const int MaxDecimalPlaces = 30;

        /// <summary>Never throws: an unusable code yields <see cref="General"/>.</summary>
        internal static string Format(double value, string formatCode, double step)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "";
            if (string.IsNullOrEmpty(formatCode)
                || string.Equals(formatCode, "General", StringComparison.OrdinalIgnoreCase))
                return General(value, step);

            try
            {
                return FormatCore(value, formatCode, step);
            }
            catch
            {
                // A format code is untrusted text from the file. Losing the formatting is a
                // cosmetic downgrade; letting it escape would lose the whole conversion.
                return General(value, step);
            }
        }

        /// <summary>
        /// The default rendering: as many decimals as the axis step needs, and no more.
        /// </summary>
        internal static string General(double value, double step)
        {
            int decimals = 0;
            if (step > 0 && step < 1)
                decimals = Math.Min(6, Math.Max(0, SaturatingCast.ToInt(Math.Ceiling(-Math.Log10(step)))));

            string s = value.ToString("F" + decimals.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);

            // "-0" is an artefact of rounding a tiny negative; it is never what the data says.
            if (s == "-0" || s.StartsWith("-0.", StringComparison.Ordinal)
                && double.Parse(s, CultureInfo.InvariantCulture) == 0)
                s = s.Substring(1);
            return s;
        }

        private static string FormatCore(double value, string code, double step)
        {
            // Section 1 is for positives and zero; section 2 for negatives, with the sign already
            // carried by the section itself. Sections 3 and 4 are ignored.
            string[] sections = SplitSections(code);
            string section = sections[0];
            bool stripSign = false;
            if (value < 0 && sections.Length > 1 && sections[1].Length > 0)
            {
                section = sections[1];
                stripSign = true;
            }

            section = StripBracketTags(section);
            if (section.Length == 0) return General(value, step);

            if (LooksLikeDate(section)) return FormatDate(value, section);

            double v = stripSign ? Math.Abs(value) : value;
            return FormatNumeric(v, section, step);
        }

        private static string[] SplitSections(string code)
        {
            var sections = new System.Collections.Generic.List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if (c == '"') { inQuotes = !inQuotes; current.Append(c); continue; }
                if (c == '\\' && i + 1 < code.Length) { current.Append(c).Append(code[++i]); continue; }
                if (c == ';' && !inQuotes) { sections.Add(current.ToString()); current.Clear(); continue; }
                current.Append(c);
            }
            sections.Add(current.ToString());
            return sections.ToArray();
        }

        /// <summary>
        /// Removes <c>[Red]</c>, <c>[$-409]</c>, <c>[&gt;100]</c> and friends. Dropping them is
        /// better than failing on them: the number still formats, only the colour or condition is
        /// lost, and neither is visible in a chart label anyway.
        /// </summary>
        private static string StripBracketTags(string section)
        {
            var sb = new StringBuilder(section.Length);
            int depth = 0;
            foreach (char c in section)
            {
                if (c == '[') { depth++; continue; }
                if (c == ']') { if (depth > 0) depth--; continue; }
                if (depth == 0) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// Whether the code is a date pattern rather than a numeric one.
        ///
        /// <para>
        /// <b>Quoted and escaped literals must be excluded from the sniff.</b> They are ordinary
        /// prose, and prose is full of date letters — <c>"unterminated</c> contains an <c>m</c> and
        /// a <c>d</c>, and reading it as a date pattern turns 42 into "02/10/1900".
        /// </para>
        /// </summary>
        private static bool LooksLikeDate(string section)
        {
            string pattern = WithoutLiterals(section);
            if (pattern.IndexOf('0') >= 0 || pattern.IndexOf('#') >= 0) return false;

            foreach (char c in pattern)
                if (c == 'y' || c == 'Y' || c == 'd' || c == 'D' || c == 'm' || c == 'M'
                    || c == 'h' || c == 'H' || c == 's' || c == 'S')
                    return true;
            return false;
        }

        /// <summary>The format code with quoted and backslash-escaped literal text removed.</summary>
        private static string WithoutLiterals(string section)
        {
            var sb = new StringBuilder(section.Length);
            bool inQuotes = false;

            for (int i = 0; i < section.Length; i++)
            {
                char c = section[i];
                if (c == '"') { inQuotes = !inQuotes; continue; }
                if (c == '\\') { i++; continue; }          // the escaped character is a literal
                if (!inQuotes) sb.Append(c);
            }

            // An unterminated quote means the rest of the code is literal text; WithoutLiterals has
            // already dropped it, which is exactly the behaviour that stops it reading as a date.
            return sb.ToString();
        }

        private static string FormatDate(double serial, string section)
        {
            // Invariant month names — a documented limit: the chart's own locale is not available
            // here, and inventing one would be worse than being consistently English.
            DateTime date;
            try { date = DateTime.FromOADate(serial); }
            catch (ArgumentException) { return serial.ToString(CultureInfo.InvariantCulture); }

            string net = ToDotNetDatePattern(section);
            try { return date.ToString(net, CultureInfo.InvariantCulture); }
            catch (FormatException) { return date.ToString("d", CultureInfo.InvariantCulture); }
        }

        /// <summary>
        /// An Excel date code as a .NET custom pattern. The letters agree but for the month: Excel's
        /// <c>m</c> is the month unless the nearest token before it is an hour or the nearest after it
        /// a second, where it is the minute (ECMA-376 Part 1 §18.8.31), and .NET tells the two apart by
        /// case (<c>M</c>, <c>m</c>). <c>AM/PM</c> and <c>A/P</c> are the designator and make the hour
        /// a twelve-hour one. Every other character, a quoted or escaped literal included, is escaped
        /// one by one, so a separator is written as the code has it, and a one-letter result is marked
        /// custom (<c>%d</c>), since .NET reads a lone letter as one of its standard formats.
        /// </summary>
        internal static string ToDotNetDatePattern(string section)
        {
            // Letter: a token's lower-case letter ('t' the AM/PM designator); '\0' a literal character.
            var tokens = new System.Collections.Generic.List<(char Letter, int Count, char Literal)>();
            bool twelveHour = false;

            for (int i = 0; i < section.Length;)
            {
                char c = section[i];
                if (c == '"')
                {
                    int end = section.IndexOf('"', i + 1);
                    if (end < 0) end = section.Length;
                    for (int k = i + 1; k < end; k++) tokens.Add(('\0', 0, section[k]));
                    i = end + 1;
                    continue;
                }
                if (c == '\\')
                {
                    if (i + 1 < section.Length) tokens.Add(('\0', 0, section[i + 1]));
                    i += 2;
                    continue;
                }
                if (StartsWithIgnoreCase(section, i, "AM/PM")) { tokens.Add(('t', 2, '\0')); twelveHour = true; i += 5; continue; }
                if (StartsWithIgnoreCase(section, i, "A/P")) { tokens.Add(('t', 1, '\0')); twelveHour = true; i += 3; continue; }

                char lower = char.ToLowerInvariant(c);
                if (lower is 'y' or 'd' or 'm' or 'h' or 's')
                {
                    int n = 1;
                    while (i + n < section.Length && char.ToLowerInvariant(section[i + n]) == lower) n++;
                    tokens.Add((lower, n, '\0'));
                    i += n;
                    continue;
                }
                tokens.Add(('\0', 0, c));
                i++;
            }

            var sb = new StringBuilder(section.Length + 8);
            for (int t = 0; t < tokens.Count; t++)
            {
                var (letter, count, literal) = tokens[t];
                switch (letter)
                {
                    case '\0': sb.Append('\\').Append(literal); break;
                    case 'y': sb.Append(count <= 2 ? "yy" : "yyyy"); break;
                    case 'd': sb.Append('d', Math.Min(count, 4)); break;
                    case 'h': sb.Append(twelveHour ? 'h' : 'H', Math.Min(count, 2)); break;
                    case 's': sb.Append('s', Math.Min(count, 2)); break;
                    case 't': sb.Append('t', count); break;
                    case 'm':
                        bool minute = NearestLetter(tokens, t, -1) == 'h' || NearestLetter(tokens, t, +1) == 's';
                        if (minute) sb.Append('m', Math.Min(count, 2));
                        else sb.Append('M', Math.Min(count, 4));
                        break;
                }
            }

            return sb.Length == 1 ? "%" + sb : sb.ToString();
        }

        /// <summary>The letter of the nearest token from <paramref name="at"/> in <paramref name="step"/>'s direction, literals passed over; '\0' for none.</summary>
        private static char NearestLetter(System.Collections.Generic.List<(char Letter, int Count, char Literal)> tokens, int at, int step)
        {
            for (int k = at + step; k >= 0 && k < tokens.Count; k += step)
                if (tokens[k].Letter != '\0') return tokens[k].Letter;
            return '\0';
        }

        private static bool StartsWithIgnoreCase(string s, int at, string what)
            => at + what.Length <= s.Length
               && string.Compare(s, at, what, 0, what.Length, StringComparison.OrdinalIgnoreCase) == 0;

        private static string FormatNumeric(double value, string section, double step)
        {
            bool percent = section.IndexOf('%') >= 0;
            if (percent) value *= 100;

            // A comma immediately before the end (or the decimal point) scales by a thousand each.
            // Every comma after the last placeholder is one, so they are found and removed in one
            // pass: removing one and searching again would copy the code once a comma, and a code of a
            // hundred thousand commas would not finish. The division stays one thousand at a time, so
            // the value rounds the same; past zero it cannot move.
            string body = WithoutTrailingScalingCommas(section, out int scalingCommas);
            for (int k = 0; k < scalingCommas && value != 0; k++) value /= 1000.0;

            bool thousands = HasGroupingComma(body);

            int decimals = DecimalPlaces(body);
            if (decimals < 0) decimals = step > 0 && step < 1
                ? Math.Min(6, Math.Max(0, SaturatingCast.ToInt(Math.Ceiling(-Math.Log10(step)))))
                : 0;
            // The count is the file's — one per placeholder after the point — and it becomes a
            // fixed-point precision: uncapped, "0." and a million zeros would write a label of two
            // megabytes, once an axis label. Excel's own Format Cells stops at thirty places.
            if (decimals > MaxDecimalPlaces) decimals = MaxDecimalPlaces;

            // Excel rounds halves away from zero; .NET's "F" formatting rounds to even, so 1234.5
            // at zero decimals would print 1234 where the source document shows 1235.
            string numeric = RoundHalfAwayFromZero(value, decimals).ToString(
                (thousands ? "N" : "F") + decimals.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);

            // Re-attach whatever literal text sat around the number placeholders.
            var (prefix, suffix) = Affixes(body);
            return prefix + numeric + suffix + (percent ? "%" : "");
        }

        /// <summary>
        /// <paramref name="body"/> without the commas after its last placeholder (<c>0</c>, <c>#</c>,
        /// <c>?</c>) — every comma, when it has none — and how
        /// many there were.
        /// </summary>
        internal static string WithoutTrailingScalingCommas(string body, out int count)
        {
            count = 0;
            int last = body.Length - 1;
            while (last >= 0 && body[last] != '0' && body[last] != '#' && body[last] != '?') last--;

            StringBuilder sb = null;
            for (int i = last + 1; i < body.Length; i++)
            {
                if (body[i] != ',') { sb?.Append(body[i]); continue; }
                if (sb == null) sb = new StringBuilder(body, 0, i, body.Length);
                count++;
            }
            return sb == null ? body : sb.ToString();
        }

        private static bool HasGroupingComma(string body)
        {
            for (int i = 1; i < body.Length - 1; i++)
                if (body[i] == ',' && IsPlaceholder(body[i - 1]) && IsPlaceholder(body[i + 1]))
                    return true;
            return false;
        }

        private static int DecimalPlaces(string body)
        {
            int dot = body.IndexOf('.');
            if (dot < 0) return HasAnyPlaceholder(body) ? 0 : -1;

            int n = 0;
            for (int i = dot + 1; i < body.Length && IsPlaceholder(body[i]); i++) n++;
            return n;
        }

        private static (string prefix, string suffix) Affixes(string body)
        {
            int first = -1, last = -1;
            for (int i = 0; i < body.Length; i++)
                if (IsPlaceholder(body[i]) || body[i] == '.' || body[i] == ',')
                {
                    if (first < 0) first = i;
                    last = i;
                }
            if (first < 0) return ("", "");

            return (Literal(body.Substring(0, first)), Literal(body.Substring(last + 1)));
        }

        private static string Literal(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') continue;
                if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
                if (c == '%' || c == '*' || c == '_') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static double RoundHalfAwayFromZero(double value, int decimals)
        {
            if (decimals < 0 || decimals > 15) return value;
            try
            {
                return Math.Round(value, decimals, MidpointRounding.AwayFromZero);
            }
            catch (OverflowException)
            {
                return value;
            }
        }

        private static bool IsPlaceholder(char c) => c == '0' || c == '#' || c == '?';

        private static bool HasAnyPlaceholder(string s)
        {
            foreach (char c in s) if (IsPlaceholder(c)) return true;
            return false;
        }
    }
}
