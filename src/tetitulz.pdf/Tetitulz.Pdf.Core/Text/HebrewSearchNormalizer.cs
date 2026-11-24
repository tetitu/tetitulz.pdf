// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Which Hebrew differences <see cref="HebrewSearchNormalizer"/> folds away.
    /// </summary>
    /// <remarks>
    /// Flags rather than a single switch because <b>they do not carry the same risk</b>. Three of
    /// them erase distinctions a reader does not perceive as distinctions; one of them erases a
    /// word boundary, and can therefore invent matches. Defaulting them all on would hide that
    /// behind one innocuous-looking parameter.
    /// </remarks>
    [Flags]
    public enum HebrewFolding
    {
        /// <summary>Fold nothing; the text is returned unchanged.</summary>
        None = 0,

        /// <summary>
        /// Strip niqqud and te'amim — every non-spacing mark in U+0591–U+05C7.
        /// </summary>
        /// <remarks>
        /// <b>The one that matters most, and the one that is unambiguously safe.</b> A pointed
        /// document — liturgy, scripture, poetry, a children's book — extracts with its vowel points
        /// present, so an unpointed query finds nothing at all. Stripping them cannot create a match
        /// between two different words: the points are a pronunciation guide layered on a spelling
        /// that is already complete without them.
        /// </remarks>
        Niqqud = 1,

        /// <summary>
        /// Map Hebrew punctuation to its ASCII lookalike: geresh U+05F3 → <c>'</c>,
        /// gershayim U+05F4 → <c>"</c>, maqaf U+05BE → <c>-</c>.
        /// </summary>
        /// <remarks>
        /// Safe and symmetric: both spellings fold to the ASCII form, so a query typed with a plain
        /// apostrophe finds an acronym typeset with a real gershayim, and the reverse. Producers are
        /// wildly inconsistent about which they emit, and a reader cannot tell them apart on the
        /// page.
        /// </remarks>
        Punctuation = 2,

        /// <summary>
        /// Decompose the Alphabetic Presentation Forms (U+FB1D–U+FB4F) to their base letters.
        /// </summary>
        /// <remarks>
        /// A pointed letter stored as a single precomposed code point — U+FB2A is shin-with-a-dot —
        /// is one character to a matcher and two to a reader. Decomposing it lets
        /// <see cref="Niqqud"/> then remove the point, so the two spellings of the same word meet.
        /// </remarks>
        PresentationForms = 4,

        /// <summary>
        /// Map the five final forms to their medial twins: ך→כ, ם→מ, ן→נ, ף→פ, ץ→צ.
        /// </summary>
        /// <remarks>
        /// <para><b>Off by default, because it can invent matches.</b> The final form <i>is</i> the
        /// word boundary. Fold it away on both sides and a search for <c>ארץ</c> becomes a search
        /// for <c>ארצ</c>, which is a substring of <c>ארצות</c> — a hit the document does not
        /// contain, produced by the normalisation rather than found in the text.</para>
        ///
        /// <para>It is offered because the opposite failure is real too: OCR and older encodings
        /// produce the wrong form, and an acronym or an abbreviation can carry a medial letter where
        /// a word would end. Turn it on when the corpus is known to be inconsistent, and expect
        /// substring false positives in exchange. <b>Never turn it on for redaction</b> — see
        /// <c>PdfTextRedactor</c>, which refuses normalised matching outright on the grounds that
        /// inventing a match is the worse error for an operation that destroys content.</para>
        /// </remarks>
        FinalForms = 8,

        /// <summary>
        /// <see cref="Niqqud"/> + <see cref="Punctuation"/> + <see cref="PresentationForms"/> — the
        /// three that cannot manufacture a match.
        /// </summary>
        Default = Niqqud | Punctuation | PresentationForms,
    }

    /// <summary>
    /// Folds the Hebrew spelling differences a reader does not see, so a search can match across
    /// them.
    /// </summary>
    /// <remarks>
    /// <para><b>Public, and in Core, for a specific reason.</b> Three search sites need it — Base's
    /// <c>PdfTextSearch</c>, Base's redaction/autolink path, and the WinForms viewer's find box —
    /// and Core's <c>InternalsVisibleTo</c> does not name the viewer. <c>internal</c> would leave
    /// the viewer's search unfolded while the library's folded, which is this repository's own
    /// recurring defect ("one renderer fixed, the other silently disagreeing") in another
    /// subsystem. Same reasoning CLAUDE.md gives for <c>Latin1Text</c> and
    /// <c>PdfReferenceEquality</c> being public.</para>
    ///
    /// <para><b>The index map is not optional.</b> Folding changes length, and three shipped
    /// contracts are stated in terms of positions in the <i>original</i> string:
    /// <c>TextSearchHit.CharOffset</c>, <c>TextSearchHit.MatchText</c>, and every glyph rectangle
    /// the viewer and the redactor derive by indexing a parallel per-character array. A fold that
    /// returned only a string would satisfy a matcher and silently move every highlight.</para>
    ///
    /// <para><b>NFD alone cannot do this job</b>, which is the trap in the obvious implementation.
    /// Hebrew has no precomposed letter-plus-vowel characters, so canonical decomposition is a
    /// no-op over ordinary pointed text; it only does anything over the presentation-forms block.
    /// Stripping niqqud needs a category-and-range filter. A plan built on "decompose and drop the
    /// marks" gets the rare block right and the actual corpus wrong.</para>
    ///
    /// <para><b>Not included: prefix letters</b> (ו/ה/ב/ל/כ/ש/מ). Those are morphology, not a
    /// canonical mapping — you cannot strip a leading ב from both sides, because בית is a word whose
    /// first letter is not a prefix. It needs a lexicon or an explicit false-positive budget, which
    /// is a different and much larger piece of work than this.</para>
    /// </remarks>
    public static class HebrewSearchNormalizer
    {
        // Niqqud, te'amim and the cantillation marks all live in this block; the filter is
        // "non-spacing mark within it" rather than a list, so a mark this code has never heard of
        // is still removed.
        private const char HebrewMarksFirst = '֑';
        private const char HebrewMarksLast = 'ׇ';

        private const char PresentationFirst = 'יִ';
        private const char PresentationLast = 'ﭏ';

        private const char Geresh = '׳';
        private const char Gershayim = '״';
        private const char Maqaf = '־';

        /// <summary>
        /// Folds <paramref name="text"/> for matching.
        /// </summary>
        /// <param name="text">Text to fold. Null yields null; the empty string yields itself.</param>
        /// <param name="folding">Which differences to erase.</param>
        public static string Fold(string text, HebrewFolding folding = HebrewFolding.Default)
            => Fold(text, folding, out _);

        /// <summary>
        /// Folds <paramref name="text"/> and reports, for each character of the result, the index in
        /// <paramref name="text"/> it came from.
        /// </summary>
        /// <param name="text">Text to fold. Null yields null.</param>
        /// <param name="folding">Which differences to erase.</param>
        /// <param name="sourceIndex">
        /// One entry per character of the returned string: the index in <paramref name="text"/> of
        /// the character that produced it. A decomposed presentation form contributes two entries
        /// with the same index; a stripped mark contributes none.
        /// </param>
        /// <remarks>
        /// The map is what makes a folded match reportable in the caller's own coordinates:
        /// <c>sourceIndex[matchStart]</c> is where the hit begins in the original text, and
        /// <c>sourceIndex[matchEnd - 1] + 1</c> bounds it. Deriving the end from
        /// <c>start + foldedLength</c> instead is wrong whenever anything was stripped inside the
        /// match — which, for pointed Hebrew, is most of the time.
        /// </remarks>
        public static string Fold(string text, HebrewFolding folding, out int[] sourceIndex)
        {
            if (text == null)
            {
                sourceIndex = null;
                return null;
            }

            if (folding == HebrewFolding.None || text.Length == 0)
            {
                sourceIndex = Identity(text.Length);
                return text;
            }

            var sb = new StringBuilder(text.Length);
            var map = new List<int>(text.Length);

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];

                // Decompose first: a presentation form is a letter *and* a point, and the point has
                // to exist as its own character before the niqqud filter below can remove it.
                if ((folding & HebrewFolding.PresentationForms) != 0 &&
                    c >= PresentationFirst && c <= PresentationLast)
                {
                    foreach (char d in Decompose(c))
                        Emit(sb, map, d, i, folding);
                    continue;
                }

                Emit(sb, map, c, i, folding);
            }

            sourceIndex = map.ToArray();
            return sb.ToString();
        }

        /// <summary>
        /// Whether <paramref name="text"/> contains anything <paramref name="folding"/> would
        /// change — a cheap way for a caller to skip the allocation on the overwhelmingly common
        /// all-Latin page.
        /// </summary>
        public static bool WouldChange(string text, HebrewFolding folding = HebrewFolding.Default)
        {
            if (text == null || folding == HebrewFolding.None) return false;

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // Only what Fold would actually decompose: U+FB1E and the block's unassigned code
                // points have no decomposition, and Fold passes them through unchanged.
                if ((folding & HebrewFolding.PresentationForms) != 0 &&
                    c >= PresentationFirst && c <= PresentationLast &&
                    Decompose(c) != c.ToString()) return true;
                if ((folding & HebrewFolding.Niqqud) != 0 && IsHebrewMark(c)) return true;
                if ((folding & HebrewFolding.Punctuation) != 0 &&
                    (c == Geresh || c == Gershayim || c == Maqaf)) return true;
                if ((folding & HebrewFolding.FinalForms) != 0 && MedialFormOf(c) != c) return true;
            }

            return false;
        }

        // ------------------------------------------------------------------

        private static void Emit(StringBuilder sb, List<int> map, char c, int sourceAt,
                                 HebrewFolding folding)
        {
            if ((folding & HebrewFolding.Niqqud) != 0 && IsHebrewMark(c)) return;   // stripped

            if ((folding & HebrewFolding.Punctuation) != 0)
            {
                if (c == Geresh) c = '\'';
                else if (c == Gershayim) c = '"';
                else if (c == Maqaf) c = '-';
            }

            if ((folding & HebrewFolding.FinalForms) != 0) c = MedialFormOf(c);

            sb.Append(c);
            map.Add(sourceAt);
        }

        /// <summary>
        /// A non-spacing mark inside the Hebrew block — a point or a cantillation mark. Both halves
        /// matter: the category alone would strip Latin and Arabic diacritics from a mixed document,
        /// and the range alone would strip the Hebrew punctuation characters that share it (maqaf,
        /// paseq, sof pasuq, nun hafukha).
        /// </summary>
        /// <remarks>
        /// The one test of which characters folding removes as Hebrew marks: search and redaction
        /// here, and the invoice field extractor's label keys (<c>InvoiceValueParser</c> in Base),
        /// which had a copy of its own.
        /// </remarks>
        internal static bool IsHebrewMark(char c)
            => c >= HebrewMarksFirst && c <= HebrewMarksLast &&
               CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark;

        private static char MedialFormOf(char c)
        {
            switch (c)
            {
                case 'ך': return 'כ';   // ך -> כ
                case 'ם': return 'מ';   // ם -> מ
                case 'ן': return 'נ';   // ן -> נ
                case 'ף': return 'פ';   // ף -> פ
                case 'ץ': return 'צ';   // ץ -> צ
                default: return c;
            }
        }

        /// <summary>
        /// Compatibility decomposition of one presentation-form character — its base letters, as
        /// <see cref="HebrewFolding.PresentationForms"/> promises for the whole block.
        /// </summary>
        /// <remarks>
        /// <para>Per character rather than over the whole string, because <c>string.Normalize</c>
        /// reports no index map and this method's whole purpose is to keep one.</para>
        /// <para><b>FormKD, not FormD.</b> The pointed letters (U+FB1D, U+FB1F, U+FB2A–U+FB4E) have
        /// canonical decompositions and come out the same either way; the wide letters
        /// (U+FB20–U+FB29) and the alef-lamed ligature (U+FB4F) have only compatibility ones, so
        /// FormD left them whole — while <see cref="WouldChange"/> counted every character of the
        /// block, and the two answered the same question differently.</para>
        /// </remarks>
        private static string Decompose(char c)
        {
            string one = c.ToString();
            try { return one.Normalize(NormalizationForm.FormKD); }
            catch (ArgumentException) { return one; }   // not a valid Unicode sequence; leave it
        }

        private static int[] Identity(int length)
        {
            var map = new int[length];
            for (int i = 0; i < length; i++) map[i] = i;
            return map;
        }
    }
}
