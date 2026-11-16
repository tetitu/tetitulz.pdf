// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The nine-digit check digit that Israeli identity, company and nonprofit numbers share.
    /// </summary>
    /// <remarks>
    /// <para><b>One algorithm, three identifiers, and that is the useful fact.</b> A
    /// <c>ת"ז</c> (national identity number), a <c>ח"פ</c> (company registration number) and an
    /// <c>עמותה</c> (nonprofit) number are all nine digits validated the same way, so <b>nothing
    /// here can tell them apart</b>. Registration practice gives company numbers a leading 5 and
    /// nonprofits 58, but that is a convention of the registrar rather than a rule of the number,
    /// and a validator that enforced it would reject legitimate values. Callers that need the
    /// distinction have to get it from context — a label on the form, the field it came from —
    /// never from the digits.</para>
    ///
    /// <para><b>The check digit is a filter, not proof.</b> It is a single decimal digit, so
    /// roughly <b>one in ten</b> arbitrary nine-digit numbers passes: an invoice number, an order
    /// reference or a phone number without its separators will sometimes validate. That is
    /// tolerable for "find candidates a human should look at" and is <i>not</i> tolerable as the
    /// sole gate on an irreversible operation — which is why the redaction path that uses this
    /// reports before it destroys.</para>
    ///
    /// <para><b>Left-padding to nine is part of the algorithm, not a convenience.</b> The number is
    /// routinely written without its leading zeros — an identity card shows nine digits, a form
    /// field often holds eight — and the weights are positional, so checking an eight-digit string
    /// as though it were eight digits long applies the wrong weight to every one of them.</para>
    ///
    /// <para>A leaf: no static fields, so no type initializer.</para>
    /// </remarks>
    public static class IsraeliIdentifiers
    {
        /// <summary>How many digits the identifier has once padded.</summary>
        public const int DigitCount = 9;

        /// <summary>
        /// Whether <paramref name="value"/> is a valid nine-digit Israeli identifier.
        /// </summary>
        /// <param name="value">
        /// The number as written. Separators, whitespace and bidi control characters are ignored;
        /// anything else — a letter, a currency sign — makes it invalid rather than being skipped,
        /// because a string that is not purely a number is not one of these identifiers.
        /// </param>
        /// <returns>
        /// <c>false</c> for null, for a value carrying anything but digits and separators, for more
        /// than nine digits, and for nine digits whose check digit does not agree.
        /// </returns>
        public static bool IsValid(string value)
            => TryNormalize(value, out string normalized) && HasValidCheckDigit(normalized);

        /// <summary>
        /// Extracts the digits of <paramref name="value"/> and left-pads them to
        /// <see cref="DigitCount"/>.
        /// </summary>
        /// <param name="value">The number as written.</param>
        /// <param name="normalized">
        /// Nine digits when this returns <c>true</c>; <c>null</c> otherwise. <b>Normalising says
        /// nothing about validity</b> — it reports that the string is shaped like one of these
        /// identifiers, and <see cref="IsValid"/> is what says whether it is one.
        /// </param>
        /// <remarks>
        /// Accepted alongside the digits: ASCII space, the separators <c>-</c> <c>/</c> <c>.</c>,
        /// the non-breaking space, and the invisible bidi controls that a number laid out inside
        /// Hebrew text routinely carries (LRM/RLM/ALM, the embedding and override marks, the
        /// isolates, and the BOM) — <see cref="UnicodeBidi.IsInvisibleLayoutMark"/>, the one list,
        /// which the invoice value parser strips for the same reason: those characters are in the
        /// string because of how the text was laid out, not because of what it says.
        /// </remarks>
        public static bool TryNormalize(string value, out string normalized)
        {
            normalized = null;
            if (string.IsNullOrEmpty(value)) return false;

            var digits = new StringBuilder(DigitCount);
            foreach (char c in value)
            {
                if (c >= '0' && c <= '9')
                {
                    if (digits.Length == DigitCount) return false;   // too long to be one of these
                    digits.Append(c);
                    continue;
                }

                if (IsIgnorable(c)) continue;
                return false;
            }

            if (digits.Length == 0) return false;

            normalized = digits.ToString().PadLeft(DigitCount, '0');
            return true;
        }

        /// <summary>
        /// Where <paramref name="text"/> may hold one of these identifiers, left to right, as
        /// (index, length): eight or nine ASCII digits in groups joined by single hyphens — and by
        /// single spaces too when <paramref name="spaceJoins"/> — adjoining no other digit, and no
        /// part of a longer number written with a decimal or thousands separator. Whether each is
        /// valid is <see cref="IsValid"/>'s question.
        /// </summary>
        /// <remarks>
        /// <para><b>The one scanner</b>, for the redactor and the invoice extractor alike. They
        /// asked the question with two expressions that disagreed: one let a space join groups and
        /// greedily swallowed the phone number after an identifier, then threw the whole away for
        /// having eleven digits.</para>
        /// <para><b>A hyphen joins one number, a space may separate two.</b> A hyphenated run is
        /// one number, and longer than nine digits it is none of these — an account number does
        /// not offer nine-digit windows. With <paramref name="spaceJoins"/>, numbers one space
        /// apart are joined from each in turn as far as nine digits allow, so <c>514 286 558</c> is
        /// one candidate and <c>514286558 03-1234567</c> two.</para>
        /// <para>Eight digits count because the leading zero of a nine-digit number is routinely
        /// dropped when it is written down.</para>
        /// </remarks>
        internal static List<(int Index, int Length)> Candidates(string text, bool spaceJoins)
        {
            var found = new List<(int Index, int Length)>();
            if (string.IsNullOrEmpty(text)) return found;

            // The hyphen-joined numbers, left to right.
            var numbers = new List<(int Start, int End, int Digits)>();
            for (int i = 0; i < text.Length; )
            {
                if (!IsDigit(text[i])) { i++; continue; }
                int start = i, digits = 0;
                while (true)
                {
                    while (i < text.Length && IsDigit(text[i])) { i++; digits++; }
                    if (i + 1 < text.Length && text[i] == '-' && IsDigit(text[i + 1])) { i++; continue; }
                    break;
                }
                numbers.Add((start, i, digits));
            }

            for (int n = 0; n < numbers.Count; n++)
            {
                int digits = numbers[n].Digits, last = n;
                if (spaceJoins)
                    while (last + 1 < numbers.Count
                           && numbers[last + 1].Start == numbers[last].End + 1 && text[numbers[last].End] == ' '
                           && digits + numbers[last + 1].Digits <= DigitCount)
                        digits += numbers[++last].Digits;
                if (digits < DigitCount - 1 || digits > DigitCount) continue;

                int from = numbers[n].Start, to = numbers[last].End;
                if (IsInsideALongerNumber(text, from, to)) continue;
                found.Add((from, to - from));
            }
            return found;
        }

        // A separator with a digit beyond it — "1,234.56789012" offers 56789012 — makes the run a
        // fragment of a longer number; a full stop ending a sentence after the number does not.
        private static bool IsInsideALongerNumber(string text, int from, int to)
            => (from >= 2 && IsNumericSeparator(text[from - 1]) && IsDigit(text[from - 2]))
               || (to + 1 < text.Length && IsNumericSeparator(text[to]) && IsDigit(text[to + 1]));

        private static bool IsNumericSeparator(char c) => c == '.' || c == ',';

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        /// <summary>
        /// The check digit itself, over a string of exactly <see cref="DigitCount"/> digits.
        /// </summary>
        /// <remarks>
        /// Each digit is weighted 1, 2, 1, 2 … from the left; a product above nine has nine
        /// subtracted (equivalently, its two digits are added); the total must be a multiple of ten.
        /// Callers reach this through <see cref="IsValid"/>, which is the one that also handles the
        /// padding the weights depend on.
        /// </remarks>
        private static bool HasValidCheckDigit(string nineDigits)
        {
            int sum = 0;
            for (int i = 0; i < DigitCount; i++)
            {
                int product = (nineDigits[i] - '0') * (i % 2 == 0 ? 1 : 2);
                sum += product > 9 ? product - 9 : product;
            }

            return sum % 10 == 0;
        }

        /// <summary>
        /// Separators and invisible marks that may sit inside a written number.
        /// </summary>
        /// <remarks>
        /// Written as code points so the CODE stays ASCII — half of this set is invisible,
        /// and a literal here is a character the next reader cannot see, cannot select and cannot
        /// tell from the one beside it. <c>InvoiceValueParser</c> builds its currency signs the
        /// same way for the same reason.
        /// </remarks>
        private static bool IsIgnorable(char c)
        {
            switch (c)
            {
                case ' ':
                case '-':
                case '/':
                case '.':
                case '\u00A0':   // no-break space
                    return true;
                default:
                    // LRM, RLM, ALM, the BOM, the embeddings, overrides and isolates: the one list.
                    return UnicodeBidi.IsInvisibleLayoutMark(c);
            }
        }
    }
}
