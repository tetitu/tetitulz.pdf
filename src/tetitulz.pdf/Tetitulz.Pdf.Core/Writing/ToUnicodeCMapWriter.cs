// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// A <c>/ToUnicode</c> CMap (§9.10.3) for two-byte codes: each code to the Unicode text it
    /// stands for.
    /// </summary>
    /// <remarks>
    /// <para><b>Two copies</b> — the composite-font writer (<see cref="Type0FontBuilder"/>, glyph id
    /// to code point) and Base's invisible OCR text layer (code unit to itself) — wrote the same
    /// program: the same preamble, the same full two-byte codespace, the same 100-entry sections and
    /// the same trailer, each with its own copy of the section limit.</para>
    /// <para><b>Public</b> because <c>Plugins.PostScript</c> writes a Type 42 font's map through it
    /// (glyph id to the character its glyph name stands for), and a plugin cannot see Core's
    /// internals.</para>
    /// </remarks>
    public static class ToUnicodeCMapWriter
    {
        /// <summary>
        /// Maximum number of <c>bfchar</c> entries in one <c>beginbfchar</c>/<c>endbfchar</c>
        /// section. The CMap specification caps a section at 100, so a longer mapping is written as
        /// consecutive sections.
        /// </summary>
        private const int MaxBfCharEntriesPerSection = 100;

        /// <summary>
        /// The CMap mapping each code to its code point, ascending by code. A code whose code point
        /// is not positive has no text and is left out.
        /// </summary>
        /// <remarks>
        /// A code point above the Basic Multilingual Plane is written as its surrogate pair; a lone
        /// surrogate code unit — which the OCR layer maps to itself, one half of a pair at a time —
        /// is at or below U+FFFF and is written as the unit. A Hebrew presentation form, which a
        /// producer draws for a letter and its point, is written as that letter and point
        /// (<c>Core.Text.HebrewPresentationForms.Decomposition</c>), and a lam-alef ligature as lam and
        /// alef (<c>Core.Text.ArabicLigatures.Decomposition</c>) — the text that was typed. A code
        /// <paramref name="codeToText"/> names is written as that text instead: a ligature the font's
        /// own <c>GSUB</c> formed, whose letters the composer recorded
        /// (<c>Core.Text.GlyphMapping.Text</c>).
        /// </remarks>
        /// <param name="codeToCodePoint">Each two-byte code and the code point it stands for.</param>
        /// <param name="codeToText">Codes whose text is more than one code point, written as that text; or null.</param>
        /// <returns>The CMap program, ASCII.</returns>
        public static byte[] Build(IEnumerable<KeyValuePair<ushort, int>> codeToCodePoint,
                                     IReadOnlyDictionary<ushort, string> codeToText = null)
        {
            var sb = new StringBuilder();
            sb.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
            sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
            sb.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
            sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

            var entries = codeToCodePoint.Where(kv => kv.Value > 0).OrderBy(kv => kv.Key).ToList();
            for (int i = 0; i < entries.Count; i += MaxBfCharEntriesPerSection)
            {
                int n = Math.Min(MaxBfCharEntriesPerSection, entries.Count - i);
                sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
                for (int j = 0; j < n; j++)
                {
                    var kv = entries[i + j];
                    string ligature = null;
                    if (codeToText != null && codeToText.TryGetValue(kv.Key, out string text) && !string.IsNullOrEmpty(text))
                        ligature = text;
                    sb.Append('<').Append(kv.Key.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                      .Append((ligature
                               ?? Text.HebrewPresentationForms.Decomposition(kv.Value)
                               ?? Text.ArabicLigatures.Decomposition(kv.Value)) is string typed
                              ? Utf16Hex(typed)
                              : Utf16Hex(kv.Value)).Append(">\n");
                }
                sb.Append("endbfchar\n");
            }

            sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        /// <summary>A string's UTF-16 code units in hex.</summary>
        private static string Utf16Hex(string text)
        {
            var sb = new StringBuilder(text.Length * 4);
            foreach (char c in text) sb.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string Utf16Hex(int codepoint)
        {
            if (codepoint <= 0xFFFF) return codepoint.ToString("X4", CultureInfo.InvariantCulture);
            // Surrogate pair for the supplementary planes.
            int c = codepoint - 0x10000;
            int hi = 0xD800 + (c >> 10);
            int lo = 0xDC00 + (c & 0x3FF);
            return hi.ToString("X4", CultureInfo.InvariantCulture) + lo.ToString("X4", CultureInfo.InvariantCulture);
        }
    }
}
