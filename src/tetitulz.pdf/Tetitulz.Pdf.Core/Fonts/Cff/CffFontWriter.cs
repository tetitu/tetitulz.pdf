// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Fonts.Cff
{
    /// <summary>
    /// Assembles a bare-CFF font: header, the four INDEXes, a charset and a Private DICT.
    /// </summary>
    /// <remarks>
    /// <para><b>Every offset in the Top DICT is written as a five-byte integer, always.</b> CFF's
    /// DICT encoding lets a small number take one byte, so a naive writer's Top DICT changes size
    /// when the offsets it holds change — and the offsets depend on where the Top DICT ends, which
    /// is a fixed point that has to be iterated to. Encoding them at full width makes the DICT's
    /// size independent of its contents, so one pass places everything.</para>
    ///
    /// <para><b>The counterpart of <see cref="CffParser"/>, not of
    /// <see cref="CffSubsetter"/>.</b> The subsetter blanks charstrings in place and never changes
    /// a length; this builds a font that did not exist. They share the format and nothing
    /// else.</para>
    /// </remarks>
    internal static class CffFontWriter
    {
        /// <summary>SIDs 0..390 are the standard strings; a custom name starts here.</summary>
        private const int FirstCustomSid = 391;

        /// <summary>What to build.</summary>
        internal sealed class Font
        {
            /// <summary>PostScript name, which becomes the single Name INDEX entry.</summary>
            internal string Name = "Converted";

            /// <summary>Glyph names in order. Index 0 must be <c>.notdef</c>.</summary>
            internal readonly List<string> GlyphNames = new List<string>();

            /// <summary>Type 2 charstrings, one per name, in the same order.</summary>
            internal readonly List<byte[]> CharStrings = new List<byte[]>();

            /// <summary>The font matrix, or null for CFF's 0.001 default.</summary>
            internal double[] FontMatrix;

            internal double[] FontBBox = { 0, 0, 1000, 1000 };

            /// <summary>Widths a charstring omits, per the Private DICT.</summary>
            internal double DefaultWidthX;

            /// <summary>The value a charstring's width prefix is relative to.</summary>
            internal double NominalWidthX;
        }

        /// <summary>Builds the font, or throws when it is not well formed.</summary>
        internal static byte[] Build(Font font)
        {
            if (font == null) throw new ArgumentNullException(nameof(font));
            if (font.GlyphNames.Count != font.CharStrings.Count)
                throw new ArgumentException("every glyph needs exactly one charstring", nameof(font));
            if (font.GlyphNames.Count == 0)
                throw new ArgumentException("a CFF needs at least .notdef", nameof(font));

            // Custom strings, in the order they are first needed. The charset writes SIDs, so this
            // has to be settled before anything is placed.
            var customStrings = new List<string>();
            // Each name's place in that list, beside it: a font converted from a file names as many
            // glyphs as the file does, and a search of the list for each cost them squared.
            var customIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var sids = new int[font.GlyphNames.Count];

            for (int i = 0; i < font.GlyphNames.Count; i++)
            {
                string name = font.GlyphNames[i] ?? ".notdef";
                int standard = StandardSid(name);
                if (standard >= 0) { sids[i] = standard; continue; }

                if (!customIndex.TryGetValue(name, out int at))
                {
                    at = customStrings.Count;
                    customStrings.Add(name);
                    customIndex[name] = at;
                }
                sids[i] = FirstCustomSid + at;
            }

            byte[] nameIndex = Index(new List<byte[]> { Latin1(font.Name) });
            byte[] stringIndex = Index(Latin1All(customStrings));
            byte[] globalSubrIndex = Index(new List<byte[]>());
            byte[] charStringsIndex = Index(font.CharStrings);
            byte[] charset = Charset(sids);
            byte[] privateDict = PrivateDict(font);

            // The Top DICT's own size does not depend on the values it holds, because every offset
            // below is written at full width -- so the layout can be computed before the offsets
            // are known.
            byte[] topDictProbe = TopDict(font, 0, 0, 0, privateDict.Length);
            int topDictIndexSize = Index(new List<byte[]> { topDictProbe }).Length;

            const int headerSize = 4;
            int afterHeader = headerSize + nameIndex.Length + topDictIndexSize
                              + stringIndex.Length + globalSubrIndex.Length;

            int charsetOffset = afterHeader;
            int charStringsOffset = charsetOffset + charset.Length;
            int privateOffset = charStringsOffset + charStringsIndex.Length;

            byte[] topDict = TopDict(font, charsetOffset, charStringsOffset, privateOffset,
                                     privateDict.Length);
            byte[] topDictIndex = Index(new List<byte[]> { topDict });

            if (topDictIndex.Length != topDictIndexSize)
                throw new InvalidOperationException(
                    "the Top DICT changed size once its offsets were known, which the fixed-width "
                    + "encoding exists to prevent");

            using var output = new MemoryStream();
            output.WriteByte(1);                 // major
            output.WriteByte(0);                 // minor
            output.WriteByte(headerSize);
            output.WriteByte(2);                 // absolute offset size, unused by readers here

            output.Write(nameIndex, 0, nameIndex.Length);
            output.Write(topDictIndex, 0, topDictIndex.Length);
            output.Write(stringIndex, 0, stringIndex.Length);
            output.Write(globalSubrIndex, 0, globalSubrIndex.Length);
            output.Write(charset, 0, charset.Length);
            output.Write(charStringsIndex, 0, charStringsIndex.Length);
            output.Write(privateDict, 0, privateDict.Length);

            return output.ToArray();
        }

        // ==============================================================
        //  Pieces
        // ==============================================================

        private static byte[] TopDict(Font font, int charsetOffset, int charStringsOffset,
                                      int privateOffset, int privateSize)
        {
            using var d = new MemoryStream();

            if (font.FontMatrix != null && font.FontMatrix.Length == 6)
            {
                foreach (double v in font.FontMatrix) WriteReal(d, v);
                d.WriteByte(12); d.WriteByte(7);          // FontMatrix
            }

            foreach (double v in font.FontBBox) WriteInt(d, SaturatingCast.ToInt(Math.Round(v)));
            d.WriteByte(5);                                // FontBBox

            WriteFixedInt(d, charsetOffset); d.WriteByte(15);        // charset
            WriteFixedInt(d, charStringsOffset); d.WriteByte(17);    // CharStrings

            // Private is size THEN offset, which is the opposite order to how it reads.
            WriteFixedInt(d, privateSize);
            WriteFixedInt(d, privateOffset);
            d.WriteByte(18);                                         // Private

            return d.ToArray();
        }

        private static byte[] PrivateDict(Font font)
        {
            using var d = new MemoryStream();
            WriteInt(d, SaturatingCast.ToInt(Math.Round(font.DefaultWidthX))); d.WriteByte(20);   // defaultWidthX
            WriteInt(d, SaturatingCast.ToInt(Math.Round(font.NominalWidthX))); d.WriteByte(21);   // nominalWidthX
            return d.ToArray();
        }

        /// <summary>Charset format 0: one SID per glyph, <c>.notdef</c> excluded.</summary>
        private static byte[] Charset(int[] sids)
        {
            using var c = new MemoryStream();
            c.WriteByte(0);

            for (int i = 1; i < sids.Length; i++)
            {
                c.WriteByte((byte)(sids[i] >> 8));
                c.WriteByte((byte)sids[i]);
            }
            return c.ToArray();
        }

        /// <summary>
        /// A CFF INDEX: a count, an offset size, count+1 one-based offsets, then the data.
        /// </summary>
        private static byte[] Index(IReadOnlyList<byte[]> items)
        {
            using var index = new MemoryStream();

            if (items == null || items.Count == 0)
            {
                index.WriteByte(0); index.WriteByte(0);
                return index.ToArray();
            }

            // The count is a Card16 (CFF §5): 65 535 items at most. Written as two low bytes, a
            // larger count wrapped silently and the INDEX described the wrong number of offsets
            // — unreachable from a well-formed Type 1, refused all the same.
            if (items.Count > 0xFFFF)
                throw new InvalidOperationException(
                    $"a CFF INDEX holds at most 65535 items (its count is a Card16); this one has {items.Count}.");

            long total = 1;
            foreach (byte[] item in items) total += item?.Length ?? 0;

            int offSize = total <= 0xFF ? 1 : total <= 0xFFFF ? 2 : total <= 0xFFFFFF ? 3 : 4;

            index.WriteByte((byte)(items.Count >> 8));
            index.WriteByte((byte)items.Count);
            index.WriteByte((byte)offSize);

            long at = 1;
            WriteOffset(index, at, offSize);
            foreach (byte[] item in items)
            {
                at += item?.Length ?? 0;
                WriteOffset(index, at, offSize);
            }

            foreach (byte[] item in items)
                if (item != null) index.Write(item, 0, item.Length);

            return index.ToArray();
        }

        private static void WriteOffset(Stream s, long value, int offSize)
        {
            for (int shift = (offSize - 1) * 8; shift >= 0; shift -= 8)
                s.WriteByte((byte)(value >> shift));
        }

        /// <summary>A DICT integer, at whatever width it fits.</summary>
        private static void WriteInt(Stream s, int v)
        {
            if (v >= -107 && v <= 107) { s.WriteByte((byte)(v + 139)); return; }

            if (v >= 108 && v <= 1131)
            {
                int adjusted = v - 108;
                s.WriteByte((byte)((adjusted >> 8) + 247));
                s.WriteByte((byte)adjusted);
                return;
            }
            if (v >= -1131 && v <= -108)
            {
                int adjusted = -v - 108;
                s.WriteByte((byte)((adjusted >> 8) + 251));
                s.WriteByte((byte)adjusted);
                return;
            }
            if (v >= short.MinValue && v <= short.MaxValue)
            {
                s.WriteByte(28);
                s.WriteByte((byte)(v >> 8));
                s.WriteByte((byte)v);
                return;
            }

            WriteFixedInt(s, v);
        }

        /// <summary>
        /// A DICT integer at a <b>fixed</b> five bytes, whatever its value.
        /// </summary>
        /// <remarks>
        /// This is what makes the layout a single pass: an offset written at its natural width
        /// would change the Top DICT's size when the offset changed, and the offsets are measured
        /// from after the Top DICT.
        /// </remarks>
        private static void WriteFixedInt(Stream s, int v)
        {
            s.WriteByte(29);
            s.WriteByte((byte)(v >> 24));
            s.WriteByte((byte)(v >> 16));
            s.WriteByte((byte)(v >> 8));
            s.WriteByte((byte)v);
        }

        /// <summary>A DICT real, in CFF's nibble encoding.</summary>
        /// <remarks>
        /// <b>A negative exponent is the single nibble 0xC</b> (TN #5176 Table 5: <c>E-</c>), and 0xE
        /// is the minus sign of the mantissa only. "1E-05" was written as B, E, 0, 5: this library's
        /// parser read it back, but other CFF readers stop at the non-digit after B and read 1.
        /// </remarks>
        internal static void WriteReal(Stream s, double v)
        {
            string text = v.ToString("G8", System.Globalization.CultureInfo.InvariantCulture);

            var nibbles = new List<int>();
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c >= '0' && c <= '9') nibbles.Add(c - '0');
                else if (c == '.') nibbles.Add(0xA);
                else if (c == 'E' || c == 'e')
                {
                    bool negative = i + 1 < text.Length && text[i + 1] == '-';
                    nibbles.Add(negative ? 0xC : 0xB);
                    if (negative) i++;
                }
                else if (c == '-') nibbles.Add(0xE);
            }
            nibbles.Add(0xF);
            if ((nibbles.Count & 1) != 0) nibbles.Add(0xF);

            s.WriteByte(30);
            for (int i = 0; i < nibbles.Count; i += 2)
                s.WriteByte((byte)((nibbles[i] << 4) | nibbles[i + 1]));
        }

        /// <summary>The SID of a predefined name, or -1.</summary>
        /// <remarks>
        /// Reads <see cref="CffParser.StandardStrings"/> rather than carrying a second copy of the
        /// 391 names: the parser needs SID to name and this needs name to SID, and two lists would
        /// agree until one of them was corrected.
        /// </remarks>
        private static int StandardSid(string name)
        {
            string[] standard = CffParser.StandardStrings;
            for (int i = 0; i < standard.Length; i++)
                if (string.Equals(standard[i], name, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static byte[] Latin1(string s)
        {
            if (string.IsNullOrEmpty(s)) return new byte[0];

            var bytes = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) bytes[i] = s[i] < 256 ? (byte)s[i] : (byte)'?';
            return bytes;
        }

        private static List<byte[]> Latin1All(IReadOnlyList<string> values)
        {
            var list = new List<byte[]>(values.Count);
            foreach (string v in values) list.Add(Latin1(v));
            return list;
        }
    }
}
