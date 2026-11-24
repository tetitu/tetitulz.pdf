// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Small, self-contained helpers for normalising and parsing the value strings commonly found on
    /// invoices (amounts, percentages, dates) — with a focus on Israeli / Hebrew documents.  These
    /// are deliberately primitive (a set of <c>TryParse</c> helpers), not a parsing framework: callers
    /// feed them the raw value strings produced by <see cref="PdfFieldExtractor"/> or the structured
    /// extractor.  Every parser fails closed (returns <c>false</c>) rather than guessing on ambiguous
    /// input.
    /// </summary>
    public static class InvoiceValueParser
    {
        /// <summary>
        /// The longest value string the patterns below are run over (CLAUDE.md's rule for a regular
        /// expression over a field the file controls): a value is a line's segment, and a string past
        /// this is not an amount, a percentage or a date.
        /// </summary>
        internal const int MaxValueLength = 4096;

        // The belt beside the cap: every pattern here is unambiguous (linear), and a timeout reads as
        // "not found" — so none is compiled: a compiled pattern's first match pays its JIT inside
        // the timeout window, which a loaded host exceeds, and the finder then answers "no date"
        // for a date it finds a moment later. A second is many times a 4 KB scan.
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

        // "ש"ח" (shekel abbreviation) with the ASCII quote NormalizePunctuation folds every variant to.
        private static readonly string ShekelAbbreviation = "\u05E9\"\u05D7";

        // A number as written: ASCII digits with '.'/',' between them. It starts and ends on a digit,
        // so a sentence's full stop or a list's comma after the number is not taken into it; the
        // sign, a trailing minus and accounting parentheses are read around the match.
        private static readonly Regex NumberRx = new Regex(@"[0-9](?:[0-9.,]*[0-9])?", RegexOptions.None, MatchTimeout);

        // A typographic group separator — thin, narrow no-break or figure space — between a digit and
        // a group of exactly three: grouping, never the gap between two numbers.
        private static readonly Regex TypographicGroupRx =
            new Regex("(?<=[0-9])[" + (char)0x2009 + (char)0x202F + (char)0x2007 + "](?=[0-9]{3}(?![0-9]))", RegexOptions.None, MatchTimeout);

        private static readonly string[] DateFormats =
        {
            "dd/MM/yyyy", "d/M/yyyy", "dd/MM/yy", "d/M/yy",
            "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy",
            "dd-MM-yyyy", "d-M-yyyy", "dd-MM-yy", "d-M-yy",
        };

        // Day-first with one separator used twice; year-first (ISO 8601 and its '/' '.' cousins).
        private static readonly Regex DayFirstRx =
            new Regex(@"(?<![0-9])([0-9]{1,2})([./-])([0-9]{1,2})\2([0-9]{4}|[0-9]{2})(?![0-9])", RegexOptions.None, MatchTimeout);
        private static readonly Regex YearFirstRx =
            new Regex(@"(?<![0-9])([0-9]{4})([./-])([0-9]{1,2})\2([0-9]{1,2})(?![0-9])", RegexOptions.None, MatchTimeout);

        // "15 באוקטובר 2026", "15 אוקטובר, 2026" — the month in Hebrew, optionally prefixed by ב/ל.
        private static readonly string[] HebrewMonths =
        {
            "\u05D9\u05E0\u05D5\u05D0\u05E8",
            "\u05E4\u05D1\u05E8\u05D5\u05D0\u05E8",
            "\u05DE\u05E8\u05E5",
            "\u05D0\u05E4\u05E8\u05D9\u05DC",
            "\u05DE\u05D0\u05D9",
            "\u05D9\u05D5\u05E0\u05D9",
            "\u05D9\u05D5\u05DC\u05D9",
            "\u05D0\u05D5\u05D2\u05D5\u05E1\u05D8",
            "\u05E1\u05E4\u05D8\u05DE\u05D1\u05E8",
            "\u05D0\u05D5\u05E7\u05D8\u05D5\u05D1\u05E8",
            "\u05E0\u05D5\u05D1\u05DE\u05D1\u05E8",
            "\u05D3\u05E6\u05DE\u05D1\u05E8",
        };

        // מרס — the other common spelling of March.
        private const string HebrewMarchAlternative = "\u05DE\u05E8\u05E1";

        private static readonly Regex HebrewMonthRx = BuildHebrewMonthRegex();

        private static readonly string[] EnglishMonths =
            { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

        // A month's name as written in full or abbreviated — and nothing longer: "Marketing" and
        // "Decorations" are not months.
        private const string EnglishMonthWord =
            @"(?<m>jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)(?![A-Za-z])\.?";

        private static readonly Regex EnglishDayMonthRx = new Regex(
            @"(?<![0-9A-Za-z])(?<d>[0-9]{1,2})(?:st|nd|rd|th)?\s+" + EnglishMonthWord + @",?\s+(?<y>[0-9]{4})(?![0-9])",
            RegexOptions.IgnoreCase, MatchTimeout);
        private static readonly Regex EnglishMonthDayRx = new Regex(
            @"(?<![A-Za-z])" + EnglishMonthWord + @"\s+(?<d>[0-9]{1,2})(?:st|nd|rd|th)?,?\s+(?<y>[0-9]{4})(?![0-9])",
            RegexOptions.IgnoreCase, MatchTimeout);

        /// <summary>
        /// Removes the invisible layout marks (<c>UnicodeBidi.IsInvisibleLayoutMark</c>: LRM, RLM,
        /// ALM, the embeddings, overrides and isolates, and the BOM) that frequently appear inside
        /// numbers laid out in RTL context, replaces non-breaking spaces with normal spaces, and
        /// writes Arabic-Indic digits as the ASCII digits they are.  Returns the cleaned string.
        /// </summary>
        public static string NormalizeDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (UnicodeBidi.IsInvisibleLayoutMark(c)) continue;
                sb.Append(c == (char)0x00A0 ? ' ' : AsciiDigit(c));
            }
            return sb.ToString();
        }

        // An Arabic-Indic (U+0660–U+0669) or Extended Arabic-Indic (U+06F0–U+06F9) digit as the
        // ASCII digit it stands for; any other character unchanged. Left alone, `[0-9]` never sees
        // them and an invoice printed in them would yield no amount at all.
        private static char AsciiDigit(char c)
            => c >= '\u0660' && c <= '\u0669' ? (char)('0' + (c - '\u0660'))
             : c >= '\u06F0' && c <= '\u06F9' ? (char)('0' + (c - '\u06F0'))
             : c;

        /// <summary>
        /// Folds the punctuation one abbreviation is written with in different producers to one
        /// form: every double-quote look-alike — Hebrew gershayim (U+05F4), curly and low quotes,
        /// the double prime, two apostrophes or two gereshes — becomes <c>"</c>; every apostrophe
        /// look-alike — geresh (U+05F3), curly and low single quotes, the prime, the grave and acute
        /// accents — becomes <c>'</c>; and the hyphen look-alikes — maqaf (U+05BE), the Unicode
        /// hyphens and the minus sign — become <c>-</c>. Everything <see cref="NormalizeDigits"/>
        /// does is done too.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this exists.</b> <c>סה"כ</c>, <c>מע"מ</c>, <c>ש"ח</c> and <c>מס'</c> are
        /// typed with an ASCII quote on a Hebrew keyboard, autocorrected to a curly one by a word
        /// processor, and set with the gershayim by typographers — three spellings of one label, of
        /// which a caller can be expected to type one.</para>
        /// <para>En and em dashes are left alone: between two numbers they mean a range, not a
        /// minus.</para>
        /// </remarks>
        public static string NormalizePunctuation(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                AppendPunctuation(sb, c);
            return sb.ToString();
        }

        /// <summary>
        /// One character's contribution to <see cref="NormalizePunctuation"/>: nothing for an
        /// invisible layout mark, a space for the no-break space, the ASCII digit for an
        /// Arabic-Indic one, the folded mark for a quote, apostrophe or hyphen look-alike, the
        /// character itself otherwise. A second apostrophe straight after one turns the pair into a
        /// quote.
        /// </summary>
        private static void AppendPunctuation(StringBuilder sb, char c)
        {
            if (UnicodeBidi.IsInvisibleLayoutMark(c)) return;

            char folded;
            switch (c)
            {
                case (char)0x00A0:
                    folded = ' ';
                    break;
                case '\u05F4': case '\u201C': case '\u201D': case '\u201E': case '\u201F':
                case '\u2033': case '\uFF02':
                    folded = '"';
                    break;
                case '\'': case '\u05F3': case '\u2018': case '\u2019': case '\u201A':
                case '\u201B': case '\u2032': case '\uFF07': case '`': case '\u00B4':
                    folded = '\'';
                    break;
                case '\u05BE': case '\u2010': case '\u2011': case '\u2212': case '\uFE63':
                case '\uFF0D':
                    folded = '-';
                    break;
                default:
                    folded = AsciiDigit(c);
                    break;
            }

            // Two apostrophes (or two gereshes) stand for a quote.
            if (folded == '\'' && sb.Length > 0 && sb[sb.Length - 1] == '\'')
            {
                sb[sb.Length - 1] = '"';
                return;
            }
            sb.Append(folded);
        }

        /// <summary>
        /// The form two pieces of text are compared in when one is a label a caller typed and the
        /// other is what a page says: <see cref="NormalizePunctuation"/>, then compatibility
        /// normalisation (NFKC — presentation forms, ligatures and mathematical letters become the
        /// letters they draw), then the Hebrew points and cantillation marks removed, then lower
        /// case.
        /// </summary>
        /// <remarks>
        /// Spaces are kept; a caller matching labels decides what whitespace means. The result is
        /// for comparison only — it is not text to show anyone.
        /// </remarks>
        public static string NormalizeForMatching(string s)
        {
            string t = NormalizePunctuation(s);
            if (t.Length == 0) return t;
            var sb = new StringBuilder(t.Length);
            for (int i = 0; i < t.Length; )
                AppendFolded(sb, NextElement(t, ref i));
            return sb.ToString();
        }

        /// <summary>
        /// One text element of <paramref name="s"/> at <paramref name="i"/> — a character, or a
        /// surrogate pair — folded into a <b>label key</b>, the form <see cref="PdfFieldExtractor"/>
        /// compares a caller's label and a page's line in: <see cref="NormalizeForMatching"/>'s
        /// folding, and then every whitespace character and colon the folding leaves dropped (a
        /// thin space or a full-width colon goes as the plain one does). <paramref name="i"/> is
        /// advanced past the element. May append nothing, or replace the last character (a second
        /// apostrophe makes a quote).
        /// </summary>
        internal static void AppendLabelKey(StringBuilder sb, string s, ref int i)
        {
            string element = NextElement(s, ref i);
            if (element.Length == 1)
            {
                int before = sb.Length;
                AppendPunctuation(sb, element[0]);
                if (sb.Length == before) return;            // removed, or merged into a quote
                element = sb[sb.Length - 1].ToString();
                sb.Length--;
            }

            var folded = new StringBuilder(4);
            AppendFolded(folded, element);
            for (int k = 0; k < folded.Length; k++)
            {
                char d = folded[k];
                if (char.IsWhiteSpace(d) || d == ':') continue;
                sb.Append(d);
            }
        }

        /// <summary>The label key of a whole string (see <see cref="AppendLabelKey"/>).</summary>
        internal static string LabelKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; )
                AppendLabelKey(sb, s, ref i);
            return sb.ToString();
        }

        // The text element at i — a surrogate pair kept whole, so NFKC sees an astral letter — and i
        // moved past it.
        private static string NextElement(string s, ref int i)
        {
            int length = char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            string element = s.Substring(i, length);
            i += length;
            return element;
        }

        /// <summary>
        /// One element's compatibility form with the Hebrew marks removed, in lower case.
        /// </summary>
        /// <remarks>
        /// <b>Normalisation can refuse.</b> <c>String.Normalize</c> throws for a lone surrogate and
        /// for a noncharacter such as U+FFFE — which a font's <c>/ToUnicode</c> can map a glyph to —
        /// and one such glyph must not abort a whole document's extraction. A refused element is kept
        /// as it is written, as Core's search normaliser does.
        /// </remarks>
        private static void AppendFolded(StringBuilder sb, string element)
        {
            if (element.Length == 1)
            {
                char c = element[0];
                // ASCII and the Hebrew block are already in NFKC — the block's marks are removed,
                // its letters and punctuation kept — so most of a page never reaches Normalize.
                if (c < 0x80) { sb.Append(char.ToLowerInvariant(c)); return; }
                if (c >= '\u0590' && c <= '\u05FF') { if (!HebrewSearchNormalizer.IsHebrewMark(c)) sb.Append(c); return; }
            }

            string k;
            try { k = element.Normalize(NormalizationForm.FormKC); }
            catch (ArgumentException) { sb.Append(element); return; }

            foreach (char d in k)
            {
                if (HebrewSearchNormalizer.IsHebrewMark(d)) continue;
                sb.Append(char.ToLowerInvariant(d));
            }
        }

        /// <summary>
        /// Parses a monetary amount such as "₪ 1,234.56", "1234.56 ₪", "$1,000", "1.234,00",
        /// "1,234.56-", "-₪1,234.56" or "(1,234.56)".
        /// <paramref name="currency"/> is set to the ISO 4217 code when a symbol, a code or a Hebrew
        /// name is present — ILS (₪, ש"ח, שקלים, NIS), USD ($, דולר), EUR (€, EURO, יורו), and GBP,
        /// CHF, JPY, CAD, AUD — otherwise <c>null</c>.  Returns false on input with no parseable
        /// number, and on input whose numbers leave the amount ambiguous.
        /// </summary>
        /// <remarks>
        /// <para><b>Which number, when the string holds several.</b> A percentage is never the
        /// amount (<c>18% 180.00</c> is 180). Of the rest, the one a currency sign or code stands
        /// beside wins; failing that, the one written with two decimals; and when that still leaves
        /// two different values (<c>2 x 50.00 = 100.00</c>), nothing is returned — the string does
        /// not say which it means. The same value written twice is not ambiguous.</para>
        /// <para><b>Negative amounts, as credit invoices write them:</b> a minus touching the number
        /// or the currency that touches it, before it (<c>-₪1,234.56</c>) or after it — which is
        /// where the bidi algorithm puts a leading one in a right-to-left line, and how Israeli
        /// accounting software prints it — or the number alone in parentheses. A minus between two
        /// numbers (<c>10-20</c>), a minus a space alone separates from the number, and a hyphen
        /// after a letter (<c>ב-1,170.00</c>, "in 1,170", or <c>INV-1170</c>) are not signs — unless
        /// the letters end a currency code (<c>ILS-500</c>).</para>
        /// <para><b>Separators.</b> With both <c>.</c> and <c>,</c> present the right-most is the
        /// decimal point. A lone <c>,</c> groups thousands when every group after the first is three
        /// digits and the first does not start with 0 (<c>1,234</c>), and is a decimal comma when it
        /// is the only one and one or two digits follow it (<c>12,50</c>) or the integer part is 0
        /// (<c>0,500</c>); anything else is refused. A lone <c>.</c> is the decimal point (the
        /// Israeli convention); several, each followed by three digits, group thousands. A thin,
        /// narrow no-break or figure space before a group of three is grouping; a plain space between
        /// a number of one to three digits and one whose whole part is three digits
        /// (<c>1 234.56</c>) could be grouping or two numbers, and is refused.</para>
        /// </remarks>
        public static bool TryParseAmount(string s, out decimal value, out string currency)
        {
            value = 0m;
            currency = null;
            if (!TryChooseAmount(s, out string t, out AmountCandidate chosen)) return false;

            value = chosen.Value;
            currency = chosen.Currency ?? DetectCurrency(t);
            return true;
        }

        /// <summary>
        /// <see cref="TryParseAmount"/> for a string that must <b>be</b> an amount rather than hold
        /// one: the number taken touches no letter but a currency's, and no letter follows it but a
        /// currency's. "30 ימים, ישלם" in a sentence, "17. מקום שיפוט", "PCN874" and "4,250 ש"ח ומעלה"
        /// are refused; a qualifier, a rate or <c>(ש"ח)</c> before the number is not.
        /// </summary>
        /// <remarks>
        /// The amount is the one <see cref="TryParseAmount"/> takes — the same choice, made once — and
        /// the currencies the one vocabulary the parser reads currencies by: symbols, ISO codes, and
        /// the Hebrew names (<c>שקלים</c>, <c>דולר</c>, <c>יורו</c>).
        /// </remarks>
        internal static bool TryParseAmountValue(string s, out decimal value, out string currency)
        {
            value = 0m;
            currency = null;
            if (!TryChooseAmount(s, out string t, out AmountCandidate chosen) || !EndsTheValue(t, chosen)) return false;

            value = chosen.Value;
            currency = chosen.Currency ?? DetectCurrency(t);
            return true;
        }

        // The amount a string holds — the ladder in TryParseAmount's remarks — and the string as read.
        private static bool TryChooseAmount(string s, out string t, out AmountCandidate chosen)
        {
            t = null;
            chosen = default;
            if (string.IsNullOrWhiteSpace(s) || s.Length > MaxValueLength) return false;

            try
            {
                t = TypographicGroupRx.Replace(NormalizePunctuation(s), string.Empty);
                return TryChooseAmountIn(t, out chosen);
            }
            catch (RegexMatchTimeoutException) { return false; }
        }

        private static bool TryChooseAmountIn(string t, out AmountCandidate chosen)
        {
            chosen = default;
            var candidates = new List<AmountCandidate>();
            Match previous = null;
            foreach (Match m in NumberRx.Matches(t))
            {
                // A percentage is no thousands group of the number beside it: "%18 180" is a rate
                // and an amount.
                bool percentage = IsPercentage(t, m);
                if (!percentage && previous != null && IsBlankGroupContinuation(t, previous, m)) return false;
                previous = percentage ? null : m;
                if (percentage) continue;
                if (!TryParseNumber(m.Value, out decimal magnitude)) continue;
                candidates.Add(new AmountCandidate(
                    IsNegative(t, m) ? -magnitude : magnitude,
                    AdjacentCurrency(t, m),
                    HasTwoDecimals(m.Value),
                    m.Index,
                    m.Length));
            }

            return TryChoose(candidates, out chosen);
        }

        /// <summary>
        /// The most words with letters an amount value may hold before its number, currencies
        /// aside: a qualifier — <c>לחיוב: 24,518.40</c>, <c>לזיכוי ₪ 555.19-</c> — and not a
        /// sentence, <c>המחיר הסופי עבור ליטר סולר ללקוח הינו 6.39 ₪</c>, after a label it names.
        /// </summary>
        internal const int MaxQualifierWords = 2;

        // The amount ends the value: no letter touches it in front but a currency's end, at most
        // MaxQualifierWords words with letters come before it, and after it come no letters but a
        // currency's.
        private static bool EndsTheValue(string t, AmountCandidate amount)
        {
            int start = amount.Index;
            if (start > 0 && char.IsLetter(t[start - 1]) && !TryCurrencyEndingAt(t, start, out _, out _)) return false;
            int qualifiers = 0;
            foreach (string word in t.Substring(0, start).Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
                if (HasLetter(word) && !IsCurrencyWord(word) && ++qualifiers > MaxQualifierWords) return false;
            for (int k = amount.Index + amount.Length; k < t.Length; )
            {
                if (!char.IsLetter(t[k])) { k++; continue; }
                if (!TryCurrencyStartingAt(t, k, out _, out int after)) return false;
                k = after;
            }
            return true;
        }

        /// <summary>
        /// Parses a percentage such as 'מע"מ 17%', "17.5 %" or "%17" (the order some right-to-left
        /// producers store it in) into its numeric value (17, 17.5).
        /// </summary>
        /// <remarks>
        /// A percentage is what <see cref="TryParseAmount"/> passes over as one — the same rule, so
        /// the two never disagree about a string. Two different percentages are refused.
        /// </remarks>
        public static bool TryParsePercent(string s, out decimal percent)
        {
            percent = 0m;
            if (string.IsNullOrWhiteSpace(s) || s.Length > MaxValueLength) return false;
            string t = NormalizePunctuation(s);

            bool any = false;
            try
            {
                foreach (Match m in NumberRx.Matches(t))
                {
                    if (!IsPercentage(t, m) || !TryParseNumber(m.Value, out decimal p)) continue;
                    if (any && p != percent) { percent = 0m; return false; }
                    percent = p;
                    any = true;
                }
            }
            catch (RegexMatchTimeoutException) { percent = 0m; return false; }
            return any;
        }

        /// <summary>
        /// Parses an Israeli-style date written day-first with '/', '.' or '-' separators
        /// (dd/MM/yyyy, dd.MM.yyyy, d.M.yy, …).  Returns false for any other shape.
        /// </summary>
        /// <remarks>
        /// The whole string must be the date. To find a date <i>inside</i> a value string — with a
        /// time after it, a label before it, written year-first or with the month's name — use
        /// <see cref="TryFindDate"/>.
        /// </remarks>
        public static bool TryParseIsraeliDate(string s, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(s)) return false;
            string t = NormalizeDigits(s).Trim();
            return DateTime.TryParseExact(t, DateFormats, CultureInfo.InvariantCulture,
                                          DateTimeStyles.None, out date);
        }

        /// <summary>
        /// Finds the one date a value string holds: day-first numeric (<c>10/06/2026</c>,
        /// <c>10.6.26</c>), year-first (<c>2026-06-10</c>), or with the month named in Hebrew
        /// (<c>10 ביוני 2026</c>) or English (<c>10 Jun 2026</c>, <c>June 10, 2026</c>).
        /// </summary>
        /// <remarks>
        /// <para>Whatever else the string holds — a time, a label, a weekday — is ignored. A string
        /// holding <b>two different dates</b> (a billing period, an issue date beside a due date)
        /// returns <c>false</c>: it does not say which one is meant.</para>
        /// <para>A numeric date is always read day-first: <c>03/04/2026</c> is the 3rd of April.
        /// A two-digit year is placed by the invariant calendar's two-digit-year window.</para>
        /// </remarks>
        public static bool TryFindDate(string s, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(s) || s.Length > MaxValueLength) return false;
            try { return TryFindDateIn(NormalizePunctuation(s), out date); }
            catch (RegexMatchTimeoutException) { date = default; return false; }
        }

        private static bool TryFindDateIn(string t, out DateTime date)
        {
            date = default;

            var found = new List<DateTime>();
            var taken = new List<(int Start, int End)>();

            foreach (Match m in YearFirstRx.Matches(t))
                if (TryMake(m.Groups[1].Value, m.Groups[3].Value, m.Groups[4].Value, out DateTime d))
                    Add(found, taken, d, m);

            foreach (Match m in DayFirstRx.Matches(t))
            {
                if (Overlaps(taken, m)) continue;
                if (TryMake(m.Groups[4].Value, m.Groups[3].Value, m.Groups[1].Value, out DateTime d))
                    Add(found, taken, d, m);
            }

            foreach (Match m in HebrewMonthRx.Matches(t))
            {
                if (Overlaps(taken, m)) continue;
                int month = HebrewMonthNumber(m.Groups[2].Value);
                if (month > 0 && TryMake(m.Groups[3].Value, month.ToString(CultureInfo.InvariantCulture),
                                         m.Groups[1].Value, out DateTime d))
                    Add(found, taken, d, m);
            }

            foreach (var rx in new[] { EnglishDayMonthRx, EnglishMonthDayRx })
                foreach (Match m in rx.Matches(t))
                {
                    if (Overlaps(taken, m)) continue;
                    if (TryMake(m.Groups["y"].Value, EnglishMonthNumber(m.Groups["m"].Value), m.Groups["d"].Value,
                                out DateTime d))
                        Add(found, taken, d, m);
                }

            if (found.Count == 0) return false;
            for (int i = 1; i < found.Count; i++)
                if (found[i] != found[0]) return false;
            date = found[0];
            return true;
        }

        // ----------------------------------------------------------------
        //  Amount helpers
        // ----------------------------------------------------------------

        private readonly struct AmountCandidate
        {
            internal readonly decimal Value;
            internal readonly string Currency;
            internal readonly bool TwoDecimals;
            /// <summary>Where the number is written, in the string as read.</summary>
            internal readonly int Index, Length;

            internal AmountCandidate(decimal value, string currency, bool twoDecimals, int index, int length)
            {
                Value = value;
                Currency = currency;
                TwoDecimals = twoDecimals;
                Index = index;
                Length = length;
            }
        }

        // The ladder in TryParseAmount's remarks: every candidate one value; else the ones beside a
        // currency; else the ones with two decimals — each step accepted only when it leaves one
        // value. Ambiguity at the last step is a refusal.
        private static bool TryChoose(List<AmountCandidate> candidates, out AmountCandidate chosen)
        {
            chosen = default;
            if (candidates.Count == 0) return false;
            if (OneValue(candidates, _ => true, out chosen)) return true;
            if (OneValue(candidates, c => c.Currency != null, out chosen)) return true;
            if (OneValue(candidates, c => c.TwoDecimals, out chosen)) return true;
            return false;
        }

        private static bool OneValue(List<AmountCandidate> candidates, Func<AmountCandidate, bool> keep,
                                     out AmountCandidate chosen)
        {
            chosen = default;
            bool any = false;
            foreach (var c in candidates)
            {
                if (!keep(c)) continue;
                if (!any) { chosen = c; any = true; continue; }
                if (c.Value != chosen.Value) return false;
                if (chosen.Currency == null) chosen = c;   // keep the currency one of the copies names
            }
            return any;
        }

        // "1 234.56": a number of one to three plain digits, then nothing but spaces, then a number
        // whose whole part is exactly three digits — grouping by a space, or two numbers.
        private static bool IsBlankGroupContinuation(string t, Match left, Match right)
        {
            int gapStart = left.Index + left.Length;
            if (right.Index <= gapStart || left.Length > 3) return false;
            if (left.Value.IndexOf('.') >= 0 || left.Value.IndexOf(',') >= 0) return false;
            for (int i = gapStart; i < right.Index; i++)
                if (t[i] != ' ') return false;

            int whole = 0;
            while (whole < right.Length && IsAsciiDigit(right.Value[whole])) whole++;
            return whole == 3;
        }

        private static bool IsPercentage(string t, Match m)
        {
            int after = SkipSpaces(t, m.Index + m.Length);
            if (after < t.Length && t[after] == '%') return true;
            // "%18" — the sign written first, as some right-to-left producers store it; unless a
            // number touching the sign's other side owns it: "18%12,345.60" is a rate and an amount.
            if (m.Index > 0 && t[m.Index - 1] == '%') return m.Index < 2 || !IsAsciiDigit(t[m.Index - 2]);
            // "% 18.00", a space apart ("סה"כ חייב במע"מ % 18.00 1,862.40") — unless a number
            // before the sign owns it, as in "18% 180.00", where 180 is the amount.
            int sign = m.Index;
            while (sign > 0 && t[sign - 1] == ' ') sign--;
            if (sign == m.Index || sign == 0 || t[sign - 1] != '%') return false;
            int owner = sign - 1;
            while (owner > 0 && t[owner - 1] == ' ') owner--;
            return owner == 0 || !IsAsciiDigit(t[owner - 1]);
        }

        private static bool IsNegative(string t, Match m)
        {
            // A leading minus touching the number, or the currency that touches it.
            int lead = StartOfCurrencyBefore(t, m.Index);
            if (lead > 0 && t[lead - 1] == '-' && IsSignPosition(t, lead - 1)) return true;

            // A trailing minus touching the number, or the currency after it.
            int trail = EndOfCurrencyAfter(t, m.Index + m.Length);
            if (trail < t.Length && t[trail] == '-' && (trail + 1 >= t.Length || !IsAsciiDigit(t[trail + 1])))
                return true;

            // "(1,234.56)" — the number alone in parentheses, spaces and a currency allowed inside.
            int open = SkipBackOverCurrency(t, m.Index);
            int close = SkipOverCurrency(t, m.Index + m.Length);
            return open > 0 && t[open - 1] == '(' && close < t.Length && t[close] == ')';
        }

        // The minus at `i` is a sign when nothing comes before it, or something other than a letter
        // or digit, or the end of a currency code ("ILS-500"). After a digit it is a range; after a
        // letter, a hyphen: Hebrew joins a prefix letter to a numeral with one ("ב-1,170.00").
        private static bool IsSignPosition(string t, int i)
        {
            if (i == 0 || !char.IsLetterOrDigit(t[i - 1])) return true;
            return TryCurrencyEndingAt(t, i, out _, out _);
        }

        private static bool HasTwoDecimals(string raw)
        {
            int last = Math.Max(raw.LastIndexOf('.'), raw.LastIndexOf(','));
            return last >= 0 && raw.Length - last - 1 == 2;
        }

        private static int SkipSpaces(string t, int i)
        {
            while (i < t.Length && t[i] == ' ') i++;
            return i;
        }

        // Where a currency code or sign ends just before `i` (spaces allowed between), its start;
        // otherwise `i` itself.
        private static int StartOfCurrencyBefore(string t, int i)
        {
            int k = i;
            while (k > 0 && t[k - 1] == ' ') k--;
            return TryCurrencyEndingAt(t, k, out _, out int start) ? start : i;
        }

        // Where a currency code or sign starts just after `i` (spaces allowed between), its end;
        // otherwise `i` itself.
        private static int EndOfCurrencyAfter(string t, int i)
        {
            int k = SkipSpaces(t, i);
            return TryCurrencyStartingAt(t, k, out _, out int end) ? end : i;
        }

        // The currency written immediately before or after the number (spaces, a sign and an
        // opening or closing parenthesis allowed between them), or null.
        private static string AdjacentCurrency(string t, Match m)
        {
            int i = m.Index;
            while (i > 0 && (t[i - 1] == ' ' || t[i - 1] == '-' || t[i - 1] == '+' || t[i - 1] == '(')) i--;
            if (TryCurrencyEndingAt(t, i, out string before, out _)) return before;

            int j = m.Index + m.Length;
            while (j < t.Length && (t[j] == ' ' || t[j] == '-' || t[j] == ')')) j++;
            return TryCurrencyStartingAt(t, j, out string after, out _) ? after : null;
        }

        private static int SkipBackOverCurrency(string t, int i)
        {
            int k = i;
            while (k > 0 && t[k - 1] == ' ') k--;
            if (TryCurrencyEndingAt(t, k, out _, out int start))
            {
                k = start;
                while (k > 0 && t[k - 1] == ' ') k--;
            }
            return k;
        }

        private static int SkipOverCurrency(string t, int i)
        {
            int k = SkipSpaces(t, i);
            if (TryCurrencyStartingAt(t, k, out _, out int end)) k = SkipSpaces(t, end);
            return k;
        }

        // How a currency token stands in text: a sign touches anything; a Latin code is a whole word;
        // a Hebrew name is a whole Hebrew word but for one prefix letter ("בש"ח", "בדולרים").
        private enum CurrencyForm { Sign, Latin, Hebrew }

        // The one currency vocabulary: the currency an amount is in, and the only letters an amount
        // may have after it (TryParseAmountValue).
        private static readonly (string Token, string Code, CurrencyForm Form)[] CurrencyTokens =
        {
            ("\u20AA", "ILS", CurrencyForm.Sign),
            (ShekelAbbreviation, "ILS", CurrencyForm.Hebrew),
            ("\u05E9\u05E7\u05DC\u05D9\u05DD", "ILS", CurrencyForm.Hebrew),   // shekels
            ("\u05E9\u05E7\u05DC", "ILS", CurrencyForm.Hebrew),               // shekel
            ("\u05E9\u05D7", "ILS", CurrencyForm.Hebrew),                     // the abbreviation without its quote
            ("ILS", "ILS", CurrencyForm.Latin),
            ("NIS", "ILS", CurrencyForm.Latin),
            ("US$", "USD", CurrencyForm.Sign),
            ("$", "USD", CurrencyForm.Sign),
            ("USD", "USD", CurrencyForm.Latin),
            ("\u05D3\u05D5\u05DC\u05E8\u05D9\u05DD", "USD", CurrencyForm.Hebrew),   // dollars
            ("\u05D3\u05D5\u05DC\u05E8", "USD", CurrencyForm.Hebrew),               // dollar
            ("\u20AC", "EUR", CurrencyForm.Sign),
            ("EUR", "EUR", CurrencyForm.Latin),
            ("EURO", "EUR", CurrencyForm.Latin),
            ("\u05D9\u05D5\u05E8\u05D5", "EUR", CurrencyForm.Hebrew),               // euro
            ("\u05D0\u05D9\u05E8\u05D5", "EUR", CurrencyForm.Hebrew),               // euro, the other spelling
            ("GBP", "GBP", CurrencyForm.Latin),
            ("CHF", "CHF", CurrencyForm.Latin),
            ("JPY", "JPY", CurrencyForm.Latin),
            ("CAD", "CAD", CurrencyForm.Latin),
            ("AUD", "AUD", CurrencyForm.Latin),
        };

        // The Hebrew prefix letters that join a word: ב, ל, מ, ו, ה — "בש"ח" is "in shekels".
        private static bool IsHebrewPrefix(char c) => c == '\u05D1' || c == '\u05DC' || c == '\u05DE' || c == '\u05D5' || c == '\u05D4';

        // A currency token ending at `end`: its code and where it starts (a Hebrew name's prefix
        // letter included). A Latin code is a whole word; a Hebrew name is a whole Hebrew word but for
        // one prefix letter.
        private static bool TryCurrencyEndingAt(string t, int end, out string code, out int start)
        {
            foreach (var (token, tokenCode, form) in CurrencyTokens)
            {
                int s = end - token.Length;
                if (s < 0 || string.Compare(t, s, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) != 0)
                    continue;
                if (form == CurrencyForm.Latin && s > 0 && IsAsciiLetter(t[s - 1])) continue;
                if (form == CurrencyForm.Hebrew && s > 0 && IsHebrewLetter(t[s - 1]))
                {
                    if (!IsHebrewPrefix(t[s - 1]) || (s > 1 && IsHebrewLetter(t[s - 2]))) continue;
                    s--;
                }
                code = tokenCode;
                start = s;
                return true;
            }
            code = null;
            start = end;
            return false;
        }

        // A currency token starting at `start` (a prefix letter before a Hebrew name allowed): its
        // code and where it ends. The left boundary is the caller's to have checked where it matters.
        private static bool TryCurrencyStartingAt(string t, int start, out string code, out int end)
        {
            int s = start;
            if (s < t.Length && IsHebrewPrefix(t[s]) && StartsHebrewName(t, s + 1)) s++;
            foreach (var (token, tokenCode, form) in CurrencyTokens)
            {
                if (!Matches(t, s, token)) continue;
                if (s > start && form != CurrencyForm.Hebrew) continue;   // a prefix letter is a Hebrew name's only
                int e = s + token.Length;
                if (form == CurrencyForm.Latin && e < t.Length && IsAsciiLetter(t[e])) continue;
                if (form == CurrencyForm.Hebrew && e < t.Length && IsHebrewLetter(t[e])) continue;
                code = tokenCode;
                end = e;
                return true;
            }
            code = null;
            end = start;
            return false;
        }

        private static bool HasLetter(string s)
        {
            foreach (char c in s)
                if (char.IsLetter(c)) return true;
            return false;
        }

        /// <summary>
        /// Whether a word is a currency and nothing else, brackets and a colon aside — <c>בש"ח:</c>,
        /// <c>(ש"ח)</c>, <c>ILS:</c> — so the field reader does not take it for the next label.
        /// </summary>
        internal static bool IsCurrencyWord(string word)
        {
            if (string.IsNullOrEmpty(word)) return false;
            string t = NormalizePunctuation(word).Trim(' ', ':', '(', ')', '[', ']');
            return t.Length > 0 && TryCurrencyStartingAt(t, 0, out _, out int end) && end == t.Length;
        }

        private static bool StartsHebrewName(string t, int at)
        {
            foreach (var (token, _, form) in CurrencyTokens)
                if (form == CurrencyForm.Hebrew && Matches(t, at, token)) return true;
            return false;
        }

        private static bool Matches(string t, int at, string token)
            => at >= 0 && at + token.Length <= t.Length
               && string.Compare(t, at, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;

        // The currency the string names anywhere, as a whole token: ILS before USD before EUR,
        // then the first other. "DETAILS" does not name ILS.
        private static string DetectCurrency(string t)
        {
            bool ils = false, usd = false, eur = false;
            string other = null;
            for (int i = 0; i < t.Length; i++)
            {
                bool leftBoundary = i == 0 || !(IsAsciiLetter(t[i - 1]) || IsHebrewLetter(t[i - 1]));
                if (!leftBoundary || !TryCurrencyStartingAt(t, i, out string code, out _)) continue;
                if (code == "ILS") ils = true;
                else if (code == "USD") usd = true;
                else if (code == "EUR") eur = true;
                else other ??= code;
            }
            return ils ? "ILS" : usd ? "USD" : eur ? "EUR" : other;
        }

        private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

        private static bool IsHebrewLetter(char c) => c >= '\u05D0' && c <= '\u05EA';

        // ----------------------------------------------------------------
        //  Number parsing (separator rules in TryParseAmount's remarks)
        // ----------------------------------------------------------------

        private static bool TryParseNumber(string raw, out decimal value)
        {
            value = 0m;
            if (string.IsNullOrEmpty(raw)) return false;

            string sign = string.Empty;
            if (raw.StartsWith("+") || raw.StartsWith("-"))
            {
                if (raw[0] == '-') sign = "-";
                raw = raw.Substring(1);
            }

            bool hasDot = raw.IndexOf('.') >= 0;
            bool hasComma = raw.IndexOf(',') >= 0;
            string normalized;

            if (hasDot && hasComma)
            {
                // The right-most separator is the decimal point; the other groups are thousands.
                int lastDot = raw.LastIndexOf('.');
                int lastComma = raw.LastIndexOf(',');
                char dec = lastDot > lastComma ? '.' : ',';
                char thou = dec == '.' ? ',' : '.';
                normalized = raw.Replace(thou.ToString(), string.Empty).Replace(dec, '.');
            }
            else if (hasComma)
            {
                string[] groups = raw.Split(',');
                if (IsThousandsGrouping(groups))
                    normalized = raw.Replace(",", string.Empty);
                else if (groups.Length == 2 && groups[0].Length > 0 && groups[1].Length > 0
                         && (groups[1].Length <= 2 || groups[0] == "0"))
                    normalized = groups[0] + "." + groups[1];   // a decimal comma: "12,50", "0,500"
                else
                    return false;
            }
            else if (hasDot)
            {
                string[] groups = raw.Split('.');
                if (groups.Length == 2)
                    normalized = raw;                            // Israeli: '.' is the decimal point
                else if (IsThousandsGrouping(groups))
                    normalized = raw.Replace(".", string.Empty); // "1.234.567"
                else
                    return false;
            }
            else
            {
                normalized = raw; // plain digits
            }

            if (!decimal.TryParse(sign + normalized, NumberStyles.Number,
                                  CultureInfo.InvariantCulture, out value))
            {
                value = 0m;
                return false;
            }
            return true;
        }

        // "1,234,567": a first group of one to three digits not starting with 0 (a thousands group
        // never does: "0,500" is a decimal comma), then groups of exactly three.
        private static bool IsThousandsGrouping(string[] groups)
        {
            if (groups.Length < 2 || groups[0].Length < 1 || groups[0].Length > 3 || groups[0][0] == '0')
                return false;
            for (int i = 1; i < groups.Length; i++)
                if (groups[i].Length != 3) return false;
            return true;
        }

        // ----------------------------------------------------------------
        //  Date helpers
        // ----------------------------------------------------------------

        private static Regex BuildHebrewMonthRegex()
        {
            var names = new StringBuilder();
            foreach (string month in HebrewMonths) names.Append(month).Append('|');
            names.Append(HebrewMarchAlternative);
            // Day, optional ב/ל prefix glued to the month name, month, optional comma, year. The
            // comma's spaces hang off the comma, so a run of blanks is not split two ways.
            return new Regex(@"(?<![0-9])([0-9]{1,2})\s*(?:-\s*)?([\u05D1\u05DC]?(?:" + names + @"))\s*(?:,\s*)?([0-9]{4})(?![0-9])",
                             RegexOptions.None, MatchTimeout);
        }

        private static int HebrewMonthNumber(string word)
        {
            // Strip the ב/ל prefix only when what follows it is a month: "מאי" itself starts with מ.
            for (int pass = 0; pass < 2; pass++)
            {
                string w = pass == 0 ? word : (word.Length > 1 ? word.Substring(1) : word);
                if (w == HebrewMarchAlternative) return 3;
                int i = Array.IndexOf(HebrewMonths, w);
                if (i >= 0) return i + 1;
            }
            return 0;
        }

        // A matched English month word's number, from its first three letters.
        private static string EnglishMonthNumber(string word)
            => (Array.IndexOf(EnglishMonths, word.Substring(0, 3).ToLowerInvariant()) + 1)
               .ToString(CultureInfo.InvariantCulture);

        private static bool TryMake(string year, string month, string day, out DateTime date)
        {
            date = default;
            if (!int.TryParse(year, NumberStyles.None, CultureInfo.InvariantCulture, out int y) ||
                !int.TryParse(month, NumberStyles.None, CultureInfo.InvariantCulture, out int mo) ||
                !int.TryParse(day, NumberStyles.None, CultureInfo.InvariantCulture, out int d))
                return false;
            if (year.Length <= 2) y = CultureInfo.InvariantCulture.Calendar.ToFourDigitYear(y);
            if (y < 1 || y > 9999 || mo < 1 || mo > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo))
                return false;
            date = new DateTime(y, mo, d);
            return true;
        }

        private static void Add(List<DateTime> found, List<(int, int)> taken, DateTime d, Match m)
        {
            found.Add(d);
            taken.Add((m.Index, m.Index + m.Length));
        }

        private static bool Overlaps(List<(int Start, int End)> taken, Match m)
        {
            foreach (var (start, end) in taken)
                if (m.Index < end && start < m.Index + m.Length) return true;
            return false;
        }
    }
}
