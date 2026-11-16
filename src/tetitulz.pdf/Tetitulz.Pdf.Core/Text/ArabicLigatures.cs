// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// The lam-alef ligature (U+FEF5–U+FEFC): a lam joined to a following alef is written as one
    /// glyph, which Arabic typography requires — lam then alef as two glyphs is a misspelling to a
    /// reader, not a style. The form is chosen from the shaped lam: initial gives the isolated
    /// ligature, medial the final one; the alef may carry a madda or a hamza above or below.
    /// </summary>
    /// <remarks>
    /// <para><b>After joining, and only where the font has the glyph.</b> <see cref="OpenTypeShaper"/>
    /// decides each letter's form from its logical neighbours; this then replaces an adjacent lam and
    /// final alef by the ligature, like the Hebrew presentation forms
    /// (<see cref="HebrewPresentationForms"/>), keeping one glyph for one code point in the run. A lam
    /// and alef with a mark between them are left as they are.</para>
    /// <para><b>The text is unchanged.</b> <c>/ToUnicode</c> writes the ligature as its compatibility
    /// decomposition — lam, then the alef (<see cref="Decomposition"/>).</para>
    /// <para>The eight pairs are Unicode's Arabic Presentation Forms-B, whose names say which is
    /// isolated and which final; the decompositions are the platform's.</para>
    /// </remarks>
    internal static class ArabicLigatures
    {
        private const int LamInitial = 0xFEDF, LamMedial = 0xFEE0;

        // final alef form → (ligature after an initial lam, ligature after a medial lam)
        private static readonly Dictionary<int, (int Isolated, int Final)> ByAlef = new Dictionary<int, (int, int)>
        {
            [0xFE82] = (0xFEF5, 0xFEF6),   // alef with madda above
            [0xFE84] = (0xFEF7, 0xFEF8),   // alef with hamza above
            [0xFE88] = (0xFEF9, 0xFEFA),   // alef with hamza below
            [0xFE8E] = (0xFEFB, 0xFEFC),   // alef
        };

        /// <summary>
        /// The ligature's text — lam and the alef, as typed (its compatibility decomposition recomposed:
        /// an alef with hamza is the one code point U+0623 or U+0625, not alef and a combining hamza) —
        /// or <c>null</c> for any other code point.
        /// </summary>
        internal static string Decomposition(int codePoint)
            => codePoint >= 0xFEF5 && codePoint <= 0xFEFC
                ? ((char)codePoint).ToString().Normalize(NormalizationForm.FormKC)
                : null;

        /// <summary>
        /// True for the ligature's final form — the one after a medial lam, which joins the letter
        /// before it.
        /// </summary>
        internal static bool IsFinal(int codePoint)
        {
            foreach (var pair in ByAlef.Values)
                if (pair.Final == codePoint) return true;
            return false;
        }

        /// <summary>
        /// <paramref name="shaped"/> — text <see cref="OpenTypeShaper"/> has shaped — with each lam
        /// directly followed by a final alef replaced by the ligature, wherever <paramref name="drawable"/>
        /// says the font has its glyph — unless <paramref name="fontForms"/> says the font's own
        /// required rules redraw the two forms, in which case they are left for those rules.
        /// </summary>
        /// <remarks>
        /// <b>The font's rules win over the encoded ligature.</b> A font's mark anchors are on the
        /// glyphs its <c>GSUB</c> produces from lam and alef — one ligature glyph (mark-to-ligature,
        /// <c>GPOS</c> type 5), or two contextual pieces with mark-to-base anchors, which is how Segoe
        /// UI draws lam-alef; the glyph its <c>cmap</c> maps U+FEFB to may be another, with no anchors,
        /// and a fatha on the encoded ligature then stayed at its pen position. Where the rules redraw
        /// the pair it is left as two forms and <c>TextRunComposer</c>'s substitution pass draws what
        /// the positioning knows; the text is the same lam and alef either way.
        /// </remarks>
        internal static string Compose(string shaped, Func<int, bool> drawable, Func<int, int, bool> fontForms = null)
        {
            if (string.IsNullOrEmpty(shaped) || drawable == null || shaped.IndexOf((char)LamInitial) < 0 && shaped.IndexOf((char)LamMedial) < 0)
                return shaped;
            var sb = new StringBuilder(shaped.Length);
            for (int i = 0; i < shaped.Length; i++)
            {
                char c = shaped[i];
                if ((c == LamInitial || c == LamMedial) && i + 1 < shaped.Length
                    && ByAlef.TryGetValue(shaped[i + 1], out var ligatures))
                {
                    if (fontForms != null && fontForms(c, shaped[i + 1]))
                    {
                        sb.Append(c);
                        continue;
                    }

                    int ligature = c == LamInitial ? ligatures.Isolated : ligatures.Final;
                    if (drawable(ligature))
                    {
                        sb.Append((char)ligature);
                        i++;
                        continue;
                    }
                }
                sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
