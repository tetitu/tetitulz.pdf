// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Provides minimal OpenType GSUB script shaping for complex scripts.
    ///
    /// <para>
    /// Handles the script-level transformations required before glyph rendering:
    /// <list type="bullet">
    ///   <item><b>Arabic joining:</b> Maps base code points to the correct isolated /
    ///     initial / medial / final positional forms using Unicode Arabic Presentation
    ///     Forms block (U+FE70–U+FEFF) and the Arabic Presentation Forms-A (U+FB50–U+FDFF).</item>
    ///   <item><b>Hebrew:</b> Already in logical visual order for RTL runs handled by
    ///     <see cref="UnicodeBidi"/>; no further shaping required for basic Latin-script
    ///     PDF text extraction.</item>
    ///   <item><b>Devanagari / Indic:</b> Applies basic consonant-cluster reordering rules
    ///     (half-forms, matras) as a best-effort pass for simple sequences.
    ///     Full GSUB/GPOS shaping is outside scope.</item>
    ///   <item><b>Script detection:</b> Classifies each code point into its Unicode script
    ///     block so callers can select the appropriate font/direction.</item>
    /// </list>
    /// </para>
    ///
    /// <b>Limitations:</b> This is a rule-based pre-processor, not a full OpenType layout
    /// engine. For accurate Indic, Khmer, or Myanmar shaping, an external engine such as
    /// a full shaper should be used.
    /// </summary>
    internal static class OpenTypeShaper
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Applies script-specific positional shaping to a string and returns the
        /// transformed code-point sequence ready for glyph lookup.
        /// </summary>
        /// <param name="text">Input text (logical order).</param>
        /// <returns>
        /// Shaped code-point sequence. Each Arabic letter is replaced by its positional
        /// presentation form (U+FE70–U+FEFF) whenever the text holds any — a word beside more Hebrew
        /// or Han than Arabic joins as it would alone, since the Arabic pass touches Arabic letters
        /// only — and Devanagari is reordered whenever the text holds any. Other scripts are
        /// returned as they are.
        /// </returns>
        public static string Shape(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;

            bool arabic = false, devanagari = false;
            foreach (char c in text)
            {
                Script s = ClassifyScript(c);
                if (s == Script.Arabic) arabic = true;
                else if (s == Script.Devanagari) devanagari = true;
            }

            if (arabic) text = ShapeArabic(text);
            if (devanagari) text = ShapeDevanagari(text);
            return text;
        }

        /// <summary>
        /// Classifies a Unicode code point into its script block.
        /// </summary>
        public static Script ClassifyScript(int codePoint)
        {
            if (codePoint >= 0x0600 && codePoint <= 0x06FF) return Script.Arabic;
            if (codePoint >= 0x0750 && codePoint <= 0x077F) return Script.Arabic;
            if (codePoint >= 0x0870 && codePoint <= 0x08FF) return Script.Arabic;
            if (codePoint >= 0xFB50 && codePoint <= 0xFDFF) return Script.Arabic;
            if (codePoint >= 0xFE70 && codePoint <= 0xFEFF) return Script.Arabic;
            if (codePoint >= 0x0590 && codePoint <= 0x05FF) return Script.Hebrew;
            if (codePoint >= 0x0900 && codePoint <= 0x097F) return Script.Devanagari;
            if (codePoint >= 0x0980 && codePoint <= 0x09FF) return Script.Bengali;
            if (codePoint >= 0x0A00 && codePoint <= 0x0A7F) return Script.Gurmukhi;
            if (codePoint >= 0x0A80 && codePoint <= 0x0AFF) return Script.Gujarati;
            if (codePoint >= 0x0B00 && codePoint <= 0x0B7F) return Script.Oriya;
            if (codePoint >= 0x0B80 && codePoint <= 0x0BFF) return Script.Tamil;
            if (codePoint >= 0x0C00 && codePoint <= 0x0C7F) return Script.Telugu;
            if (codePoint >= 0x0C80 && codePoint <= 0x0CFF) return Script.Kannada;
            if (codePoint >= 0x0D00 && codePoint <= 0x0D7F) return Script.Malayalam;
            if (codePoint >= 0x0E00 && codePoint <= 0x0E7F) return Script.Thai;
            if (codePoint >= 0x1000 && codePoint <= 0x109F) return Script.Myanmar;
            if (codePoint >= 0x1780 && codePoint <= 0x17FF) return Script.Khmer;
            if (codePoint >= 0x4E00 && codePoint <= 0x9FFF) return Script.Han;
            if (codePoint >= 0x3040 && codePoint <= 0x309F) return Script.Hiragana;
            if (codePoint >= 0x30A0 && codePoint <= 0x30FF) return Script.Katakana;
            if (codePoint >= 0xAC00 && codePoint <= 0xD7AF) return Script.Hangul;
            return Script.Latin;
        }

        /// <summary>
        /// Returns true if the script runs right-to-left.
        /// </summary>
        public static bool IsRightToLeft(Script script)
            => script == Script.Arabic || script == Script.Hebrew;

        // ----------------------------------------------------------------
        //  Arabic shaping
        // ----------------------------------------------------------------

        private static string ShapeArabic(string text)
        {
            // Apply Arabic joining to produce positional forms.
            // Strategy: for each Arabic base letter, determine its joining context
            // (isolated, initial, medial, final) and map to the presentation form.

            int len = text.Length;
            var shaped = new int[len];
            for (int i = 0; i < len; i++)
                shaped[i] = text[i];

            for (int i = 0; i < len; i++)
            {
                int cp = shaped[i];
                // Only a letter with presentation forms has one to take. A letter without them is
                // left as it is here; it still joins its neighbours (JoinsOnRight, CanJoinOnLeft),
                // and a producer draws its position through the font (TryGetUnformedJoiningFeature).
                if (!_arabicForms.ContainsKey(cp)) continue;

                // Neighbours are read from `text`, NOT from `shaped`. Substitution happens in
                // place, so by the time letter i is examined letter i-1 has already been replaced
                // by a presentation form — which the joining rules do not recognise, making
                // canJoinRight false for every letter in every word. No letter could ever take a
                // medial or final form, so Arabic rendered as a row of isolated shapes.
                //
                // The scan also skips TRANSPARENT characters. Unicode joining treats a non-spacing
                // mark as invisible to the join, so the harakat in the ordinary vocalised spelling
                // of a word must not break it: بَيْت is one connected word, not four letters.
                int prev = PreviousJoiningChar(text, i);
                int next = NextJoiningChar(text, i);

                bool canJoinLeft  = next >= 0 && JoinsOnRight(next);
                bool canJoinRight = prev >= 0 && CanJoinOnLeft(prev);

                int form;
                if (canJoinRight && canJoinLeft)
                    form = GetArabicForm(cp, ArabicForm.Medial);
                else if (canJoinRight)
                    form = GetArabicForm(cp, ArabicForm.Final);
                else if (canJoinLeft)
                    form = GetArabicForm(cp, ArabicForm.Initial);
                else
                    form = GetArabicForm(cp, ArabicForm.Isolated);

                if (form != 0) shaped[i] = form;
            }

            var sb = new StringBuilder(len);
            foreach (int cp in shaped)
            {
                // `shaped` holds raw UTF-16 CODE UNITS (copied from text[i] above), not code
                // points, so a supplementary character arrives here as its two surrogate halves.
                // char.ConvertFromUtf32 throws ArgumentOutOfRangeException for anything in
                // U+D800–U+DFFF, and nothing between here and PdfOperations.ShapeText /
                // TextRunComposer.Compose / FormAppearance / PdfTemplateEngine catches it — so one
                // emoji in an Arabic-dominant string aborted the whole fill. Append the half
                // verbatim: the pair re-forms in the output string unchanged, which is the same
                // distinction CMapParser.HexToUnicodeString makes for an unpaired surrogate.
                if (cp >= 0xD800 && cp <= 0xDFFF) sb.Append((char)cp);
                else sb.Append(char.ConvertFromUtf32(cp));
            }
            return sb.ToString();
        }

        private enum ArabicForm { Isolated, Final, Initial, Medial }

        /// <summary>
        /// True for the characters joining treats as <b>transparent</b>: they sit between two
        /// letters without breaking the connection between them.
        /// </summary>
        /// <remarks>
        /// Without this, <c>بَيْت</c> — the ordinary vocalised spelling taught in every Arabic
        /// textbook and used throughout educational and Quranic PDFs — shapes as four isolated
        /// letters, because the mark between two letters is not itself a letter. The set is
        /// Unicode's (<see cref="ArabicJoiningTypes"/>). It was a hand range, U+06D6–U+06ED, that
        /// missed the honorific marks (U+0610–U+061A) and the Extended-A marks, and counted as
        /// marks five characters that are not: the end-of-ayah sign, two ornaments, and the small
        /// waw and small yeh, so a join passed through them.
        /// </remarks>
        private static bool IsJoiningTransparent(int cp)
            => ArabicJoiningTypes.Of(cp) == ArabicJoiningTypes.Transparent;

        /// <summary>Nearest non-transparent character before <paramref name="index"/>, or -1.</summary>
        private static int PreviousJoiningChar(string text, int index)
        {
            for (int j = index - 1; j >= 0; j--)
                if (!IsJoiningTransparent(text[j])) return text[j];
            return -1;
        }

        /// <summary>Nearest non-transparent character after <paramref name="index"/>, or -1.</summary>
        private static int NextJoiningChar(string text, int index)
        {
            for (int j = index + 1; j < text.Length; j++)
                if (!IsJoiningTransparent(text[j])) return text[j];
            return -1;
        }

        /// <summary>
        /// True when <paramref name="cp"/> connects to the letter after it. A letter the form table
        /// holds joins leftwards when it has an initial form of its own; a right-joining one (alef,
        /// dal, reh, waw, teh marbuta, jeh…) and the non-joining hamza have none. Any other
        /// character answers by its Unicode joining type: a dual-joining letter without
        /// presentation forms, the tatweel and the zero width joiner join.
        /// </summary>
        /// <remarks>
        /// <para>Not a hand list of the right-joining letters with "everything else in U+0621–U+064A"
        /// as its default, which would count hamza, which joins on neither side, as dual-joining and
        /// every letter past U+064A as joining on neither side. The table is the one statement of
        /// what it draws.</para>
        /// <para>A letter outside the table — U+0750 onwards, the Kashmiri and Farsi yeh variants
        /// U+0620 and U+063B–U+063F — joined on neither side, so its neighbours did not join to it
        /// either. Unicode's data now answers for it.</para>
        /// </remarks>
        private static bool CanJoinOnLeft(int cp)
            => _arabicForms.TryGetValue(cp, out int[] forms)
                ? forms[(int)ArabicForm.Initial] != forms[(int)ArabicForm.Isolated]
                : ArabicJoiningTypes.JoinsNext(ArabicJoiningTypes.Of(cp));

        /// <summary>
        /// True when <paramref name="cp"/> connects to the letter before it — every joining letter, the
        /// tatweel, but not the non-joining hamza, whose only form is its isolated one. A character the
        /// table lacks answers by its Unicode joining type.
        /// </summary>
        private static bool JoinsOnRight(int cp)
            => _arabicForms.TryGetValue(cp, out int[] forms)
                ? forms[(int)ArabicForm.Final] != forms[(int)ArabicForm.Isolated]
                : ArabicJoiningTypes.JoinsPrevious(ArabicJoiningTypes.Of(cp));

        /// <summary>
        /// The OpenType joining feature (<c>isol</c>, <c>fina</c>, <c>init</c>, <c>medi</c>) of the
        /// letter at <paramref name="index"/> in <paramref name="shaped"/> — <see cref="Shape"/>'s
        /// output — when it is a joining letter with no presentation forms, which <see cref="Shape"/>
        /// leaves as it is. A producer reaches its joined shape through the font's own <c>GSUB</c>.
        /// </summary>
        /// <remarks>
        /// <para>Read from the forms its neighbours took, which <see cref="Shape"/> chose by the same
        /// joining rules: the letter joins the one before it when that one took an initial or
        /// medial form (or is itself an unformed joining letter, or a join-causing character), and
        /// the one after when that took a final or medial form. So the letter's position and its
        /// neighbours' cannot disagree. Transparent characters are skipped, as
        /// <see cref="Shape"/> skips them.</para>
        /// <para>A right-joining letter never joins the letter after it. The lam-alef ligature
        /// (<see cref="ArabicLigatures"/>) never joins the letter after it either, and joins the one
        /// before it in its final form.</para>
        /// </remarks>
        internal static bool TryGetUnformedJoiningFeature(string shaped, int index, out string feature)
        {
            feature = null;
            if (shaped == null || index < 0 || index >= shaped.Length) return false;
            int cp = shaped[index];
            if (_arabicForms.ContainsKey(cp)) return false;
            char type = ArabicJoiningTypes.Of(cp);
            if (type != ArabicJoiningTypes.DualJoining && type != ArabicJoiningTypes.RightJoining) return false;

            int prev = PreviousJoiningChar(shaped, index);
            int next = NextJoiningChar(shaped, index);
            bool right = prev >= 0 && ReachesNext(prev);
            bool left = type == ArabicJoiningTypes.DualJoining && next >= 0 && ReachesPrevious(next);

            feature = JoiningFeatures[(int)(right && left ? ArabicForm.Medial
                                            : right ? ArabicForm.Final
                                            : left ? ArabicForm.Initial
                                            : ArabicForm.Isolated)];
            return true;
        }

        /// <summary>Whether a character of <see cref="Shape"/>'s output connects to the one after it.</summary>
        /// <remarks>A lam-alef ligature needs no case: as a presentation form outside the Arabic
        /// blocks it is non-joining to <see cref="ArabicJoiningTypes"/>, which is right, since the
        /// alef it ends in does not join the letter after it.</remarks>
        private static bool ReachesNext(int cp)
        {
            if (TryGetJoiningFeature(cp, out _, out string f)) return f == "init" || f == "medi";
            return !_arabicForms.ContainsKey(cp) && CanJoinOnLeft(cp);
        }

        /// <summary>Whether a character of <see cref="Shape"/>'s output connects to the one before it.</summary>
        private static bool ReachesPrevious(int cp)
        {
            if (TryGetJoiningFeature(cp, out _, out string f)) return f == "fina" || f == "medi";
            if (ArabicLigatures.Decomposition(cp) != null) return ArabicLigatures.IsFinal(cp);
            return !_arabicForms.ContainsKey(cp) && JoinsOnRight(cp);
        }

        /// <summary>
        /// Returns the Unicode code point for the given Arabic positional form,
        /// or 0 if no mapping is available.
        /// Uses the Presentation Forms-B block (U+FE70–U+FEFF).
        /// </summary>
        private static int GetArabicForm(int cp, ArabicForm form)
        {
            if (!_arabicForms.TryGetValue(cp, out var forms)) return 0;
            return forms[(int)form];
        }

        /// <summary>
        /// The base Arabic letter a presentation form came from — the inverse of
        /// <see cref="Shape"/>'s substitution.
        /// </summary>
        /// <remarks>
        /// A PDF producer that shapes text must still record what the reader should <b>extract</b>.
        /// Writing the presentation form into <c>/ToUnicode</c> would make a search for "بيت" miss
        /// the very text that was drawn, and copy-paste yield code points no keyboard produces.
        /// <para>The inverse is well defined here only because this shaper's substitution is
        /// strictly <b>one code point in, one out</b>. A general OpenType shaper produces ligature
        /// clusters, where several characters collapse into one glyph and no per-glyph inverse exists;
        /// that is exactly what blocked doing this with an external shaper. The lam-alef ligature, which a
        /// producer does form (<see cref="ArabicLigatures"/>, after this), keeps the property by
        /// standing as its own code point, which <c>/ToUnicode</c> writes as lam and alef.</para>
        /// </remarks>
        /// <returns>
        /// <c>true</c> and the base letter when <paramref name="presentationForm"/> is a form this
        /// shaper emits; <c>false</c> for anything else, including a base letter itself.
        /// </returns>
        public static bool TryGetBasePresentationForm(int presentationForm, out int baseCodePoint)
        {
            if (_formToBase == null)
            {
                var map = new Dictionary<int, int>(_arabicForms.Count * 4);
                foreach (var kv in _arabicForms)
                    foreach (int form in kv.Value)
                        // First writer wins: several positional slots repeat the same form for
                        // non-joining letters, and they all come from the one base.
                        if (form != 0 && !map.ContainsKey(form)) map[form] = kv.Key;
                _formToBase = map;
            }
            return _formToBase.TryGetValue(presentationForm, out baseCodePoint);
        }

        private static Dictionary<int, int> _formToBase;

        /// <summary>
        /// The OpenType joining feature a presentation form this shaper emits stands for — <c>isol</c>,
        /// <c>fina</c>, <c>init</c> or <c>medi</c> — and its base letter: what a font that maps the
        /// base letter but not the form reaches the form through, in its own <c>GSUB</c>.
        /// </summary>
        /// <remarks>
        /// Where one form fills several positions (a right-joining letter's isolated form stands for
        /// its initial too, its final for its medial), the first position names it: isolated before
        /// final before initial before medial, which is what the form is.
        /// </remarks>
        internal static bool TryGetJoiningFeature(int presentationForm, out int baseCodePoint, out string feature)
        {
            feature = null;
            if (!TryGetBasePresentationForm(presentationForm, out baseCodePoint)) return false;
            int[] forms = _arabicForms[baseCodePoint];
            for (int slot = 0; slot < forms.Length; slot++)
            {
                if (forms[slot] != presentationForm) continue;
                feature = JoiningFeatures[slot];
                return true;
            }
            return false;
        }

        /// <summary>The joining feature of each position, in the order of <see cref="ArabicForm"/>.</summary>
        private static readonly string[] JoiningFeatures = { "isol", "fina", "init", "medi" };

        // Mapping: base letter → [isolated, final, initial, medial] presentation forms.
        // Only the most common Arabic letters are included; others fall back to base form.
        private static readonly Dictionary<int, int[]> _arabicForms = new Dictionary<int, int[]>
        {
            // HAMZA
            { 0x0621, new[] { 0xFE80, 0xFE80, 0xFE80, 0xFE80 } },
            // ALEF WITH MADDA ABOVE
            { 0x0622, new[] { 0xFE81, 0xFE82, 0xFE81, 0xFE82 } },
            // ALEF WITH HAMZA ABOVE
            { 0x0623, new[] { 0xFE83, 0xFE84, 0xFE83, 0xFE84 } },
            // WAW WITH HAMZA ABOVE
            { 0x0624, new[] { 0xFE85, 0xFE86, 0xFE85, 0xFE86 } },
            // ALEF WITH HAMZA BELOW
            { 0x0625, new[] { 0xFE87, 0xFE88, 0xFE87, 0xFE88 } },
            // YEH WITH HAMZA ABOVE
            { 0x0626, new[] { 0xFE89, 0xFE8A, 0xFE8B, 0xFE8C } },
            // ALEF
            { 0x0627, new[] { 0xFE8D, 0xFE8E, 0xFE8D, 0xFE8E } },
            // BA
            { 0x0628, new[] { 0xFE8F, 0xFE90, 0xFE91, 0xFE92 } },
            // TA MARBUTA
            { 0x0629, new[] { 0xFE93, 0xFE94, 0xFE93, 0xFE94 } },
            // TA
            { 0x062A, new[] { 0xFE95, 0xFE96, 0xFE97, 0xFE98 } },
            // THA
            { 0x062B, new[] { 0xFE99, 0xFE9A, 0xFE9B, 0xFE9C } },
            // JIM
            { 0x062C, new[] { 0xFE9D, 0xFE9E, 0xFE9F, 0xFEA0 } },
            // HA
            { 0x062D, new[] { 0xFEA1, 0xFEA2, 0xFEA3, 0xFEA4 } },
            // KHA
            { 0x062E, new[] { 0xFEA5, 0xFEA6, 0xFEA7, 0xFEA8 } },
            // DAL
            { 0x062F, new[] { 0xFEA9, 0xFEAA, 0xFEA9, 0xFEAA } },
            // THAL
            { 0x0630, new[] { 0xFEAB, 0xFEAC, 0xFEAB, 0xFEAC } },
            // RA
            { 0x0631, new[] { 0xFEAD, 0xFEAE, 0xFEAD, 0xFEAE } },
            // ZAIN
            { 0x0632, new[] { 0xFEAF, 0xFEB0, 0xFEAF, 0xFEB0 } },
            // SIN
            { 0x0633, new[] { 0xFEB1, 0xFEB2, 0xFEB3, 0xFEB4 } },
            // SHIN
            { 0x0634, new[] { 0xFEB5, 0xFEB6, 0xFEB7, 0xFEB8 } },
            // SAD
            { 0x0635, new[] { 0xFEB9, 0xFEBA, 0xFEBB, 0xFEBC } },
            // DAD
            { 0x0636, new[] { 0xFEBD, 0xFEBE, 0xFEBF, 0xFEC0 } },
            // TAH
            { 0x0637, new[] { 0xFEC1, 0xFEC2, 0xFEC3, 0xFEC4 } },
            // ZAH
            { 0x0638, new[] { 0xFEC5, 0xFEC6, 0xFEC7, 0xFEC8 } },
            // AIN
            { 0x0639, new[] { 0xFEC9, 0xFECA, 0xFECB, 0xFECC } },
            // GHAIN
            { 0x063A, new[] { 0xFECD, 0xFECE, 0xFECF, 0xFED0 } },
            // FA
            { 0x0641, new[] { 0xFED1, 0xFED2, 0xFED3, 0xFED4 } },
            // QAF
            { 0x0642, new[] { 0xFED5, 0xFED6, 0xFED7, 0xFED8 } },
            // KAF
            { 0x0643, new[] { 0xFED9, 0xFEDA, 0xFEDB, 0xFEDC } },
            // LAM
            { 0x0644, new[] { 0xFEDD, 0xFEDE, 0xFEDF, 0xFEE0 } },
            // MIM
            { 0x0645, new[] { 0xFEE1, 0xFEE2, 0xFEE3, 0xFEE4 } },
            // NUN
            { 0x0646, new[] { 0xFEE5, 0xFEE6, 0xFEE7, 0xFEE8 } },
            // HA (small)
            { 0x0647, new[] { 0xFEE9, 0xFEEA, 0xFEEB, 0xFEEC } },
            // WAW
            { 0x0648, new[] { 0xFEED, 0xFEEE, 0xFEED, 0xFEEE } },
            // ALEF MAKSURA — dual-joining in Unicode (ArabicShaping.txt: D). Its initial and medial
            // forms are the Uighur, Kazakh and Kyrgyz ones in Presentation Forms-A (U+FBE8, U+FBE9);
            // Arabic ends a word with it, so those two are reached only where the text joins it on.
            { 0x0649, new[] { 0xFEEF, 0xFEF0, 0xFBE8, 0xFBE9 } },
            // YEH
            { 0x064A, new[] { 0xFEF1, 0xFEF2, 0xFEF3, 0xFEF4 } },

            // The letters of Persian, Urdu and the other languages written in Arabic script, from the
            // Presentation Forms-A block: generated from the Unicode Character Database (each form
            // decomposes as <isolated|final|initial|medial> and its letter), not typed. U WITH HAMZA ABOVE
            // is left out: Unicode encodes only its isolated form, though the letter joins on its right,
            // and the table is what the joining predicates below read.
            // ALEF WASLA
            { 0x0671, new[] { 0xFB50, 0xFB51, 0xFB50, 0xFB51 } },
            // TTEH
            { 0x0679, new[] { 0xFB66, 0xFB67, 0xFB68, 0xFB69 } },
            // TTEHEH
            { 0x067A, new[] { 0xFB5E, 0xFB5F, 0xFB60, 0xFB61 } },
            // BEEH
            { 0x067B, new[] { 0xFB52, 0xFB53, 0xFB54, 0xFB55 } },
            // PEH
            { 0x067E, new[] { 0xFB56, 0xFB57, 0xFB58, 0xFB59 } },
            // TEHEH
            { 0x067F, new[] { 0xFB62, 0xFB63, 0xFB64, 0xFB65 } },
            // BEHEH
            { 0x0680, new[] { 0xFB5A, 0xFB5B, 0xFB5C, 0xFB5D } },
            // NYEH
            { 0x0683, new[] { 0xFB76, 0xFB77, 0xFB78, 0xFB79 } },
            // DYEH
            { 0x0684, new[] { 0xFB72, 0xFB73, 0xFB74, 0xFB75 } },
            // TCHEH
            { 0x0686, new[] { 0xFB7A, 0xFB7B, 0xFB7C, 0xFB7D } },
            // TCHEHEH
            { 0x0687, new[] { 0xFB7E, 0xFB7F, 0xFB80, 0xFB81 } },
            // DDAL
            { 0x0688, new[] { 0xFB88, 0xFB89, 0xFB88, 0xFB89 } },
            // DAHAL
            { 0x068C, new[] { 0xFB84, 0xFB85, 0xFB84, 0xFB85 } },
            // DDAHAL
            { 0x068D, new[] { 0xFB82, 0xFB83, 0xFB82, 0xFB83 } },
            // DUL
            { 0x068E, new[] { 0xFB86, 0xFB87, 0xFB86, 0xFB87 } },
            // RREH
            { 0x0691, new[] { 0xFB8C, 0xFB8D, 0xFB8C, 0xFB8D } },
            // JEH
            { 0x0698, new[] { 0xFB8A, 0xFB8B, 0xFB8A, 0xFB8B } },
            // VEH
            { 0x06A4, new[] { 0xFB6A, 0xFB6B, 0xFB6C, 0xFB6D } },
            // PEHEH
            { 0x06A6, new[] { 0xFB6E, 0xFB6F, 0xFB70, 0xFB71 } },
            // KEHEH
            { 0x06A9, new[] { 0xFB8E, 0xFB8F, 0xFB90, 0xFB91 } },
            // NG
            { 0x06AD, new[] { 0xFBD3, 0xFBD4, 0xFBD5, 0xFBD6 } },
            // GAF
            { 0x06AF, new[] { 0xFB92, 0xFB93, 0xFB94, 0xFB95 } },
            // NGOEH
            { 0x06B1, new[] { 0xFB9A, 0xFB9B, 0xFB9C, 0xFB9D } },
            // GUEH
            { 0x06B3, new[] { 0xFB96, 0xFB97, 0xFB98, 0xFB99 } },
            // NOON GHUNNA
            { 0x06BA, new[] { 0xFB9E, 0xFB9F, 0xFB9E, 0xFB9F } },
            // RNOON
            { 0x06BB, new[] { 0xFBA0, 0xFBA1, 0xFBA2, 0xFBA3 } },
            // HEH DOACHASHMEE
            { 0x06BE, new[] { 0xFBAA, 0xFBAB, 0xFBAC, 0xFBAD } },
            // HEH WITH YEH ABOVE
            { 0x06C0, new[] { 0xFBA4, 0xFBA5, 0xFBA4, 0xFBA5 } },
            // HEH GOAL
            { 0x06C1, new[] { 0xFBA6, 0xFBA7, 0xFBA8, 0xFBA9 } },
            // KIRGHIZ OE
            { 0x06C5, new[] { 0xFBE0, 0xFBE1, 0xFBE0, 0xFBE1 } },
            // OE
            { 0x06C6, new[] { 0xFBD9, 0xFBDA, 0xFBD9, 0xFBDA } },
            // U
            { 0x06C7, new[] { 0xFBD7, 0xFBD8, 0xFBD7, 0xFBD8 } },
            // YU
            { 0x06C8, new[] { 0xFBDB, 0xFBDC, 0xFBDB, 0xFBDC } },
            // KIRGHIZ YU
            { 0x06C9, new[] { 0xFBE2, 0xFBE3, 0xFBE2, 0xFBE3 } },
            // VE
            { 0x06CB, new[] { 0xFBDE, 0xFBDF, 0xFBDE, 0xFBDF } },
            // FARSI YEH
            { 0x06CC, new[] { 0xFBFC, 0xFBFD, 0xFBFE, 0xFBFF } },
            // E
            { 0x06D0, new[] { 0xFBE4, 0xFBE5, 0xFBE6, 0xFBE7 } },
            // YEH BARREE
            { 0x06D2, new[] { 0xFBAE, 0xFBAF, 0xFBAE, 0xFBAF } },
            // YEH BARREE WITH HAMZA ABOVE
            { 0x06D3, new[] { 0xFBB0, 0xFBB1, 0xFBB0, 0xFBB1 } },
        };

        // ----------------------------------------------------------------
        //  Devanagari shaping
        // ----------------------------------------------------------------

        private static string ShapeDevanagari(string text)
        {
            // Apply basic Devanagari reordering:
            // Pre-base reordering matras (e.g. U+093F VOWEL SIGN I) move before their consonant.
            // This is a simplified rule for the most common matra.

            var chars = new List<char>(text);
            for (int i = 1; i < chars.Count; i++)
            {
                char c = chars[i];
                // Pre-base matras that need to be placed before the preceding consonant:
                // U+093F (DEVANAGARI VOWEL SIGN I), U+0940 (VOWEL SIGN II) stays post.
                if (c == '\u093F') // VOWEL SIGN I — pre-base, move before preceding consonant
                {
                    chars.RemoveAt(i);
                    // Find the start of the consonant cluster (skip virama-joined forms).
                    int insertPos = i - 1;
                    while (insertPos > 0 && chars[insertPos - 1] == '\u094D') // VIRAMA
                        insertPos -= 2;
                    // The loop guards insertPos BEFORE the decrement but not after, so a
                    // virama-initial fragment \u2014 which real text split mid-cluster produces \u2014
                    // leaves it at -1 and List.Insert(-1, \u2026) throws ArgumentOutOfRangeException
                    // straight out of Shape. Nothing between here and PdfOperations.ShapeText /
                    // TextRunComposer.Compose / FormAppearance / PdfTemplateEngine catches it, so
                    // one such fragment in a form field or template slot aborted the operation.
                    if (insertPos < 0) insertPos = 0;
                    chars.Insert(insertPos, c);
                }
            }
            return new string(chars.ToArray());
        }

    }

    // ----------------------------------------------------------------
    //  Script classification enum
    // ----------------------------------------------------------------

    /// <summary>Unicode script classification.</summary>
    public enum Script
    {
        Latin,
        Arabic,
        Hebrew,
        Devanagari,
        Bengali,
        Gurmukhi,
        Gujarati,
        Oriya,
        Tamil,
        Telugu,
        Kannada,
        Malayalam,
        Thai,
        Myanmar,
        Khmer,
        Han,
        Hiragana,
        Katakana,
        Hangul
    }
}
