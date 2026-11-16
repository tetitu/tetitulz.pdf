// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Hebrew presentation forms (U+FB1D–U+FB4F): a letter with a point drawn as one glyph — bet with
    /// dagesh (U+FB31), shin with its dot (U+FB2A) — and each form's canonical decomposition.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a producer composes them.</b> The Hebrew fonts on a Windows host draw a dagesh in a
    /// letter, a shin's dot and a holam on vav as the presentation form's glyph: their <c>GSUB</c>
    /// composes the pair, their <c>GPOS</c> anchors those points on no base of their own, and their
    /// other points anchor on the presentation form as on any letter. A rule-based producer that
    /// draws the pair as two glyphs leaves the point where the outline puts it; composing it where the
    /// font has the form's glyph draws what the font designed.</para>
    /// <para><b>The text is unchanged.</b> The forms are composition-excluded — Unicode never composes
    /// to them — so the composed run is a drawing decision only, and <c>/ToUnicode</c> maps each form's
    /// glyph back to its decomposition (<see cref="Decomposition"/>, which
    /// <c>Core.Writing.ToUnicodeCMapWriter</c> asks): a search for the word as typed finds it.</para>
    /// <para>The table is the platform's own canonical decomposition of the block, not typed here.</para>
    /// </remarks>
    internal static class HebrewPresentationForms
    {
        private const char First = 'יִ', Last = 'ﭏ';

        private static readonly Dictionary<int, string> Decompositions = new Dictionary<int, string>();

        // (what a mark composes onto, the mark) → the presentation form. A three-character form is
        // reached from either two-character form, so the marks may come in either order.
        private static readonly Dictionary<(int Base, char Mark), int> Pairs = new Dictionary<(int, char), int>();

        static HebrewPresentationForms()
        {
            for (char c = First; c <= Last; c++)
            {
                string d = c.ToString().Normalize(NormalizationForm.FormD);
                if (d.Length >= 2 && d != c.ToString()) Decompositions[c] = d;
            }
            var byText = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var pair in Decompositions) byText[pair.Value] = pair.Key;
            foreach (var pair in Decompositions)
            {
                string d = pair.Value;
                if (d.Length == 2) Pairs[(d[0], d[1])] = pair.Key;
                else if (d.Length == 3)
                {
                    if (byText.TryGetValue(d.Substring(0, 2), out int a)) Pairs[(a, d[2])] = pair.Key;
                    if (byText.TryGetValue(new string(new[] { d[0], d[2] }), out int b)) Pairs[(b, d[1])] = pair.Key;
                }
            }
        }

        /// <summary>A presentation form's canonical decomposition, or <c>null</c> for any other code point.</summary>
        internal static string Decomposition(int codePoint)
            => codePoint >= First && codePoint <= Last && Decompositions.TryGetValue(codePoint, out string d) ? d : null;

        /// <summary>
        /// <paramref name="text"/> with each Hebrew letter and the points it composes with replaced by
        /// the presentation form, wherever <paramref name="drawable"/> says the font has the form's
        /// glyph. A point composes with the letter before it across the other points of the same
        /// letter; points that compose with nothing stay where they were.
        /// </summary>
        internal static string Compose(string text, Func<int, bool> drawable)
        {
            if (string.IsNullOrEmpty(text) || drawable == null || !HasPoint(text)) return text;
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                int current = text[i];
                int end = i + 1;
                while (end < text.Length && IsPoint(text[end])) end++;
                if (end == i + 1) { sb.Append(text[i]); continue; }

                var marks = new List<char>(end - i - 1);
                for (int k = i + 1; k < end; k++) marks.Add(text[k]);
                bool composed = true;
                while (composed)
                {
                    composed = false;
                    for (int k = 0; k < marks.Count; k++)
                    {
                        if (Pairs.TryGetValue((current, marks[k]), out int form) && drawable(form))
                        {
                            current = form;
                            marks.RemoveAt(k);
                            composed = true;
                            break;
                        }
                    }
                }
                sb.Append((char)current);
                foreach (char m in marks) sb.Append(m);
                i = end - 1;
            }
            return sb.ToString();
        }

        /// <summary>A Hebrew point, accent or mark (U+0591–U+05C7), which is what composes onto a letter.</summary>
        private static bool IsPoint(char c) => c >= '֑' && c <= 'ׇ' && c != '־' && c != '׀' && c != '׃' && c != '׆';

        private static bool HasPoint(string text)
        {
            foreach (char c in text) if (IsPoint(c)) return true;
            return false;
        }
    }
}
