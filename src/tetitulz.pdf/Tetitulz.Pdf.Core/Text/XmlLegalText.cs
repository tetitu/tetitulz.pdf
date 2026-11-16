// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Xml;

namespace Tetitulz.Pdf.Core.Text
{
    /// <summary>
    /// Text made legal in an XML document: the ODT and DOCX writers' model strings and an XMP packet's
    /// title alike. A code point XML 1.0 forbids — a stray control character, a lone surrogate, U+FFFF —
    /// makes a document not well-formed when written raw, and an <see cref="XmlWriter"/> throw on it.
    /// </summary>
    internal static class XmlLegalText
    {
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

                if (XmlConvert.IsXmlChar(c))   // false for a lone surrogate and for the forbidden controls
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
