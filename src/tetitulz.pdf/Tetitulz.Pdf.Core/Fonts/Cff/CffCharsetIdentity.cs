// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;

namespace Tetitulz.Pdf.Core.Fonts.Cff
{
    /// <summary>
    /// Makes a CID-keyed CFF's charset the identity — CID <i>i</i> for glyph <i>i</i> — in the
    /// program's own bytes, so a writer whose content codes are glyph ids under Identity-H
    /// (<c>Type0FontBuilder</c>) embeds a program whose CIDs are those glyph ids. ISO 32000-1
    /// §9.7.4.2: a CID-keyed CFF's CIDs reach its glyphs through its charset, so a glyph id written as
    /// a CID into a font whose charset orders its CIDs any other way selects another glyph, or none.
    /// </summary>
    /// <remarks>
    /// <para>Written over the old charset's bytes, which leaves every other offset in the program
    /// where it was: the identity is one range, five bytes as a format 2 charset (format, first CID,
    /// a two-byte count), four as a format 1 (a one-byte count, for fewer than 258 glyphs). A charset
    /// of two or more glyphs in format 0, or of two or more ranges, is at least five bytes; a format 1
    /// charset of one range is four, and takes the format 1 identity when the glyph count allows. The
    /// bytes the identity leaves are zeroed; nothing refers into a charset. The one shape that cannot
    /// be rewritten — a format 1 single range over 258 or more glyphs starting at a CID other than 1 —
    /// is refused with the reason.</para>
    /// <para>The glyph order, the FDSelect, the charstrings and the font's metrics are untouched: a
    /// CID-keyed font's FDSelect and charstrings are indexed by glyph id, which does not change.</para>
    /// </remarks>
    internal static class CffCharsetIdentity
    {
        /// <summary>
        /// <paramref name="program"/> with its charset the identity: the same array when it is not a
        /// CID-keyed CFF1 (bare, or inside an <c>'OTTO'</c> sfnt), names a predefined charset, or is
        /// the identity already; a patched copy otherwise. Null, with <paramref name="reason"/>, when
        /// the charset is not the identity and will not fit one in its own bytes, or the program's
        /// header, INDEXes or Top DICT will not read.
        /// </summary>
        internal static byte[] Rewrite(byte[] program, out string reason)
        {
            reason = null;
            if (program == null || program.Length < 4) return program;

            if (!TryLocateCff(program, out int cffBase, out int cffLength)) return program;
            if (program[cffBase] != 1) return program;   // CFF2, or not a CFF

            int cffEnd = cffBase + cffLength;
            try
            {
                int headerSize = program[cffBase + 2];
                if (!CffParser.TryLocateIndex(program, cffBase + headerSize, out _, out _, out int namesEnd)
                    || !CffParser.TryLocateIndex(program, namesEnd, out int[] topStarts, out int[] topLengths, out _)
                    || topStarts.Length == 0)
                {
                    reason = "its Name or Top DICT INDEX cannot be read";
                    return null;
                }

                var topBytes = new byte[topLengths[0]];
                Array.Copy(program, topStarts[0], topBytes, 0, topBytes.Length);
                var topDict = CffParser.ParseDict(topBytes);
                const int OpRos = (12 << 8) | 30;
                if (!topDict.ContainsKey(OpRos)) return program;   // not CID-keyed: CID == GID already

                int charsetOffset = CffParser.GetDictInt(topDict, 15, 0);
                if (charsetOffset < 4) return program;   // a predefined charset is read as the identity

                int charStringsOffset = CffParser.GetDictInt(topDict, 17, -1);
                if (charStringsOffset <= 0
                    || !CffParser.TryLocateIndex(program, cffBase + charStringsOffset, out int[] glyphStarts, out _, out _))
                {
                    reason = "its CharStrings INDEX cannot be read";
                    return null;
                }
                int glyphCount = glyphStarts.Length;
                if (glyphCount <= 1) return program;   // only .notdef: the charset names nothing

                int charsetAt = cffBase + charsetOffset;
                if (charsetAt >= cffEnd)
                {
                    reason = "its charset offset lies past the end of the program";
                    return null;
                }
                if (!MeasureCharset(program, charsetAt, cffEnd, glyphCount, out int size, out bool identity))
                {
                    reason = "its charset format is not 0, 1 or 2";
                    return null;
                }
                if (identity) return program;

                int nLeft = glyphCount - 2;   // the glyphs after the first, GID 1
                var patched = (byte[])program.Clone();
                if (size >= 5)
                {
                    patched[charsetAt] = 2;
                    patched[charsetAt + 1] = 0;
                    patched[charsetAt + 2] = 1;
                    patched[charsetAt + 3] = (byte)(nLeft >> 8);
                    patched[charsetAt + 4] = (byte)nLeft;
                    for (int i = charsetAt + 5; i < charsetAt + size; i++) patched[i] = 0;
                    return patched;
                }
                if (size == 4 && nLeft <= 0xFF)
                {
                    patched[charsetAt] = 1;
                    patched[charsetAt + 1] = 0;
                    patched[charsetAt + 2] = 1;
                    patched[charsetAt + 3] = (byte)nLeft;
                    return patched;
                }

                reason = "its charset occupies " + size.ToString(CultureInfo.InvariantCulture) +
                         " bytes, and the identity for " + glyphCount.ToString(CultureInfo.InvariantCulture) +
                         " glyphs needs five";
                return null;
            }
            catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is OverflowException)
            {
                reason = "its structure cannot be read: " + ex.GetType().Name;
                return null;
            }
        }

        /// <summary>
        /// The bytes a §13 charset occupies for <paramref name="glyphCount"/> glyphs and whether it
        /// assigns every glyph its own id; false for a format byte the format does not define.
        /// </summary>
        private static bool MeasureCharset(byte[] d, int at, int end, int glyphCount, out int size, out bool identity)
        {
            size = 0;
            identity = true;
            int p = at;
            byte format = d[p++];

            if (format == 0)
            {
                for (int gid = 1; gid < glyphCount && p + 1 < end; gid++, p += 2)
                    if (((d[p] << 8) | d[p + 1]) != gid) identity = false;
                size = p - at;
                return true;
            }

            if (format == 1 || format == 2)
            {
                int entryBytes = format == 1 ? 3 : 4;
                int g = 1;
                while (g < glyphCount && p + entryBytes - 1 < end)
                {
                    int first = (d[p] << 8) | d[p + 1];
                    int nLeft = format == 1 ? d[p + 2] : (d[p + 2] << 8) | d[p + 3];
                    p += entryBytes;
                    if (first != g) identity = false;
                    g += nLeft + 1;
                }
                size = p - at;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Where the CFF lies: the whole array for a bare program, the <c>'CFF '</c> table of an
        /// <c>'OTTO'</c> sfnt; false for an sfnt without one or with a record past its end.
        /// </summary>
        private static bool TryLocateCff(byte[] d, out int offset, out int length)
        {
            offset = 0;
            length = d.Length;
            bool otto = d.Length >= 12 && d[0] == 'O' && d[1] == 'T' && d[2] == 'T' && d[3] == 'O';
            if (!otto) return true;

            int numTables = (d[4] << 8) | d[5];
            for (int i = 0; i < numTables; i++)
            {
                int entry = 12 + i * 16;
                if (entry + 16 > d.Length) break;
                if (d[entry] != 'C' || d[entry + 1] != 'F' || d[entry + 2] != 'F' || d[entry + 3] != ' ') continue;

                long tableOffset = ((long)d[entry + 8] << 24) | ((long)d[entry + 9] << 16) | ((long)d[entry + 10] << 8) | d[entry + 11];
                long tableLength = ((long)d[entry + 12] << 24) | ((long)d[entry + 13] << 16) | ((long)d[entry + 14] << 8) | d[entry + 15];
                if (tableLength < 4 || tableOffset + tableLength > d.Length) return false;
                offset = (int)tableOffset;
                length = (int)tableLength;
                return true;
            }
            return false;
        }
    }
}
