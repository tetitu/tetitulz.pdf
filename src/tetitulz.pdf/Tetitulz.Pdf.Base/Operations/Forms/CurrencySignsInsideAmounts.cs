// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Operations.Forms
{
    /// <summary>
    /// Amounts drawn with their currency sign inside them, taken apart: the sign put beside the
    /// amount, the amount one word again.
    /// </summary>
    /// <remarks>
    /// <para>Word lays <c>₪ 3,539.43</c> in a right-to-left paragraph as <c>3 ₪,539.43</c> — the
    /// integer part, a space, the sign, then the rest — and the page shows it so. Read as it stands
    /// it is the amount 3, or 43 for <c>43 ₪.42</c>. The amount is the digits on both sides of the
    /// sign, which goes before them, where Chromium draws it.</para>
    /// <para>Only a run of digits followed, a word space on, by a word that opens with a currency
    /// sign and a separator and holds only digits and separators after it is taken apart: an
    /// amount followed by its sign alone (<c>5 ₪</c>) is as it is. The glyphs are read in their
    /// visual order (<see cref="TextDirection.VisualGlyphs"/>), not the words' text, which a
    /// right-to-left line gives reversed.</para>
    /// </remarks>
    internal static class CurrencySignsInsideAmounts
    {
        /// <summary>
        /// <paramref name="pages"/> with each such amount taken apart; a page or line holding none is
        /// the same instance.
        /// </summary>
        internal static List<PdfPageTextLayout> MovedOut(IReadOnlyList<PdfPageTextLayout> pages)
        {
            var result = new List<PdfPageTextLayout>(pages.Count);
            foreach (var page in pages)
            {
                bool changed = false;
                var lines = new List<PdfTextLine>();
                foreach (var line in TextLayouts.LinesOf(page))
                {
                    var moved = MovedOut(line);
                    changed |= !ReferenceEquals(moved, line);
                    lines.Add(moved);
                }
                result.Add(changed ? new PdfPageTextLayout(page.PageNumber, lines, page.Chars) : page);
            }
            return result;
        }

        private static PdfTextLine MovedOut(PdfTextLine line)
        {
            var words = TextLayouts.WordsOf(line).OrderBy(w => w.X).ToList();
            bool changed = false;
            for (int i = 0; i + 1 < words.Count; i++)
            {
                if (!TryTakeApart(words, i, out PdfTextWord sign, out PdfTextWord amount)) continue;
                words[i] = sign;
                words[i + 1] = amount;
                changed = true;
            }
            if (!changed) return line;

            float left = words.Min(w => w.X), right = words.Max(w => w.X + w.Width);
            return new PdfTextLine(TextDirection.ToReadingOrder(words), left, line.Y, right - left, line.Height,
                                   words, line.IsRtl) { IsVertical = line.IsVertical };
        }

        private static bool TryTakeApart(List<PdfTextWord> words, int i, out PdfTextWord sign, out PdfTextWord amount)
        {
            sign = amount = null;
            var a = words[i];
            var b = words[i + 1];
            var head = TextDirection.VisualGlyphs(a);
            var tail = TextDirection.VisualGlyphs(b);
            if (head.Count == 0 || tail.Count < 3) return false;
            if (!head.All(g => g.Char.Length == 1 && IsDigit(g.Char[0]))) return false;
            var mark = tail[0];
            if (mark.Char.Length != 1 || CharUnicodeInfo.GetUnicodeCategory(mark.Char[0]) != UnicodeCategory.CurrencySymbol)
                return false;
            if (!IsSeparator(tail[1].Char) || !IsDigitGlyph(tail[2])) return false;
            if (!tail.Skip(1).All(g => IsDigitGlyph(g) || IsSeparator(g.Char))) return false;

            // A word space between them, no more: the sign belongs to the digits before it.
            float space = mark.X - (a.X + a.Width);
            if (space < 0f || space > a.Height) return false;

            // The sign goes before the amount, where nothing else is.
            float x = a.X - space - mark.Width;
            if (i > 0 && words[i - 1].X + words[i - 1].Width > x) return false;

            var signGlyph = new PdfTextChar(mark.Char, x, mark.Y, mark.Width, mark.Height, mark.IsRtl)
            {
                IsInvisible = mark.IsInvisible,
                InReversedRun = mark.InReversedRun,
            };
            sign = new PdfTextWord(mark.Char, x, b.Y, mark.Width, b.Height, new[] { signGlyph }, isRtl: false);

            // The rest closes up on the integer part, as one number's glyphs stand.
            var last = head[head.Count - 1];
            float shift = tail[1].X - (last.X + last.Width);
            var glyphs = head.Concat(tail.Skip(1).Select(g => Shifted(g, shift))).ToList();
            string text = string.Concat(glyphs.Select(g => g.Char));
            float right = b.X + b.Width - shift;
            amount = new PdfTextWord(text, a.X, a.Y, right - a.X, System.Math.Max(a.Height, b.Height), glyphs, isRtl: false);
            return true;
        }

        private static PdfTextChar Shifted(PdfTextChar g, float shift)
            => new PdfTextChar(g.Char, g.X - shift, g.Y, g.Width, g.Height, g.IsRtl)
            {
                IsInvisible = g.IsInvisible,
                InReversedRun = g.InReversedRun,
            };

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        private static bool IsDigitGlyph(PdfTextChar g) => g.Char.Length == 1 && IsDigit(g.Char[0]);

        private static bool IsSeparator(string s) => s == "." || s == ",";
    }
}
