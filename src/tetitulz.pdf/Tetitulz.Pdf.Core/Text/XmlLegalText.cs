// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Text made legal in an XML document. A code point XML 1.0 forbids — a stray control character, a lone
    /// surrogate, U+FFFF — makes a document not well-formed when written raw, and an <see cref="XmlWriter"/>
    /// throw on it. Two answers, each for its own text: <see cref="Replace"/> marks the loss in text a caller
    /// authored (the Builder's ODT and DOCX writers, an XMP packet's title); <see cref="Remove"/> drops what has
    /// no glyph from text read out of a PDF (the SVG export, the PDF to Office converters), where U+FFFD would
    /// put a visible box in place of nothing.
    /// </summary>
    public static class XmlLegalText
    {
        /// <summary>
        /// <paramref name="s"/> with every code point XML 1.0 does not allow removed — tab, line feed and carriage
        /// return kept, a surrogate pair kept whole and a lone half dropped. Null is the empty string. Allocates
        /// nothing when the text is already legal.
        /// </summary>
        public static string Remove(string s)
        {
            if (string.IsNullOrEmpty(s)) return s ?? "";
            int first = -1;
            for (int i = 0; i < s.Length; i++)
            {
                if (IsLegalAt(s, i)) continue;
                first = i;
                break;
            }
            if (first < 0) return s;

            var sb = new StringBuilder(s.Length);
            sb.Append(s, 0, first);
            for (int i = first; i < s.Length; i++)
                if (IsLegalAt(s, i)) sb.Append(s[i]);
            return sb.ToString();
        }

        /// <summary>
        /// Whether the code unit at <paramref name="i"/> is XML 1.0 <c>Char</c> content. A surrogate is judged by
        /// its pair: both halves of a well-formed pair are kept, a half without its partner is not.
        /// </summary>
        private static bool IsLegalAt(string s, int i)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c)) return i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]);
            if (char.IsLowSurrogate(c)) return i > 0 && char.IsHighSurrogate(s[i - 1]);
            return IsLegalChar(c);
        }

        /// <summary>
        /// Whether a code unit that is no surrogate is XML 1.0 <c>Char</c> (§2.2): tab, line feed, carriage return,
        /// U+0020 to U+D7FF and U+E000 to U+FFFD. Not <see cref="XmlConvert.IsXmlChar"/>, which admits U+FFFE and
        /// U+FFFF.
        /// </summary>
        private static bool IsLegalChar(char c)
            => c == '\t' || c == '\n' || c == '\r' || (c >= ' ' && c < '\uD800') || (c >= (char)0xE000 && c <= (char)0xFFFD);

        /// <summary>
        /// <paramref name="s"/> with every code point XML 1.0 does not allow replaced by U+FFFD;
        /// <paramref name="changed"/> says whether any was. Null is the empty string.
        /// </summary>
        internal static string Replace(string s, out bool changed)
        {
            changed = false;
            if (string.IsNullOrEmpty(s)) return s ?? "";

            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];

                // A supplementary code point (U+10000..U+10FFFF) is a valid XML char; keep the pair whole.
                if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
                {
                    sb?.Append(c).Append(s[i + 1]);
                    i++;
                    continue;
                }

                if (IsLegalChar(c))   // false for a lone surrogate, the forbidden controls, U+FFFE and U+FFFF
                {
                    sb?.Append(c);
                }
                else
                {
                    if (sb == null) { sb = new StringBuilder(s.Length); sb.Append(s, 0, i); }
                    sb.Append('�');
                    changed = true;
                }
            }
            return sb?.ToString() ?? s;
        }
    }
}
