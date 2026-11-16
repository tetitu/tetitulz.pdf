// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Reads an sfnt table directory — the <b>read</b> counterpart of <see cref="SfntBuilder"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Who this is for.</b> The readers that <i>re-emit</i> the font: whatever they do not
    /// understand, they write back out. A lenient read is right for a reader that only wants
    /// metrics — a short <c>hhea</c> should yield approximate ascent, not a refusal — and wrong for
    /// one whose output is a new font file. The metric readers therefore keep their own lenient
    /// loops and are not callers here; what they share with this is
    /// <see cref="IsCollection"/> alone.</para>
    ///
    /// <para><b>Three re-emitters had three different answers to the same malformed input.</b>
    /// <c>Plugins.Maintenance.TrueTypeGlyphPruner</c> refused a tag carrying a byte outside the
    /// printable ASCII range OpenType allows, with a comment describing the damage exactly.
    /// <c>Core.Fonts.SfntTablePruner</c> and <c>Core.Fonts.Cff.CffSubsetter</c>, one and two folders
    /// away, decoded the tag with <see cref="System.Text.Encoding.ASCII"/> — which substitutes
    /// <c>'?'</c> for any byte above 0x7E — and handed the result to <see cref="SfntBuilder"/>,
    /// which writes the tag back out <i>from that string</i>. A table tagged
    /// <c>0x80 'P' 'O' 'S'</c> came back named <c>?POS</c>, sorted to a different place in the
    /// directory, and the prune reported success with no reason. <b>The output is a self-consistent
    /// sfnt that is no longer the font the document embedded.</b> Measured, not reasoned:
    /// <c>SfntTableDirectoryTests</c> pins it.</para>
    ///
    /// <para>A duplicate tag is refused for the same reason rather than resolved. Keeping the first
    /// (which the plugin did) drops a table the input had; keeping both (which the two Core copies
    /// did) writes a directory in which a binary-searching reader may land on either. Both are a
    /// different font returned as a success, and neither is a guess this layer is entitled to
    /// make.</para>
    ///
    /// <para><b>No table-count cap.</b> "The directory must fit inside the file" already bounds the
    /// loop at <c>length / 16</c>, and that is what all three callers had. A 512-entry cap exists in
    /// the metric readers, which is a different contract — they read a directory they will not write
    /// back.</para>
    ///
    /// <para><b>Public rather than internal</b> for the same reason as <see cref="SfntBuilder"/>:
    /// <c>Plugins.Maintenance</c> needs it and a plugin cannot see Core's internals.</para>
    /// </remarks>
    public static class SfntTableDirectory
    {
        /// <summary>Bytes in the sfnt header before the first directory record.</summary>
        private const int HeaderBytes = 12;

        /// <summary>
        /// How many bytes the records may declare in all, per byte of font: disjoint tables total
        /// less than one, and a directory past this one is overlapping on purpose.
        /// </summary>
        private const int MaxDeclaredTableBytesPerFontByte = 2;

        /// <summary>Bytes per record: tag(4) + checksum(4) + offset(4) + length(4).</summary>
        private const int EntryBytes = 16;

        /// <summary>The <c>'ttcf'</c> signature of a TrueType Collection.</summary>
        public const uint VersionCollection = 0x74746366u;

        /// <summary>The legacy Apple <c>'true'</c> sfnt version.</summary>
        public const uint VersionAppleTrueType = 0x74727565u;

        /// <summary>
        /// The one refusal message for a collection, so every caller says the same thing about the
        /// same file and names the same way out of it.
        /// </summary>
        public const string CollectionRefusal =
            "TrueType Collections ('ttcf') carry several faces in one file, and a PDF font program " +
            "is a single face; extract the face you want and supply that";

        /// <summary>One table's position in the font, as the directory declares it.</summary>
        public readonly struct TableRecord
        {
            internal TableRecord(string tag, int offset, int length)
            {
                Tag = tag; Offset = offset; Length = length;
            }

            /// <summary>The four-character tag, verified to be printable ASCII.</summary>
            public string Tag { get; }

            /// <summary>Byte offset of the table within the font, verified to be in range.</summary>
            public int Offset { get; }

            /// <summary>Byte length of the table, verified to lie inside the font.</summary>
            public int Length { get; }
        }

        /// <summary>The sfnt version word, or 0 when the bytes are too short to hold one.</summary>
        public static uint ReadVersion(byte[] font)
            => font == null || font.Length < 4 ? 0u : ReadU32(font, 0);

        /// <summary>
        /// Whether <paramref name="font"/> is a TrueType Collection.
        /// </summary>
        /// <remarks>
        /// <b>Checked by signature, not by what the directory read produces.</b> A <c>ttcf</c>
        /// header puts <c>majorVersion</c> (1) exactly where an sfnt keeps <c>numTables</c>, and the
        /// first face's u32 offset where the first record's tag goes — so a reader without this test
        /// does not fault, it finds one table with a nonsense name at a nonsense offset, locates
        /// nothing, and returns defaults for every metric it was asked for. Six readers in this tree
        /// refused a collection by name; three did not, and two of those three were behind the
        /// public <c>PdfOperations.GetFontMetrics</c> / <c>EmbedFont</c>, where the result was a
        /// font descriptor of zeros and a <c>/FontFile2</c> holding a whole collection.
        /// <c>Core.Fonts.FontFileLocator</c> — this library's own font search — returns
        /// <c>*.ttc</c>, and a collection is the normal shipping form for CJK faces.
        /// </remarks>
        public static bool IsCollection(byte[] font) => ReadVersion(font) == VersionCollection;

        /// <summary>
        /// Reads every record, or refuses with a reason naming what was wrong.
        /// </summary>
        /// <param name="font">The font program. Never modified.</param>
        /// <param name="tables">The records, in directory order.</param>
        /// <param name="reason">Why the directory was refused; <c>null</c> on success.</param>
        /// <returns><c>true</c> when every record was read and verified.</returns>
        public static bool TryRead(byte[] font, out IReadOnlyList<TableRecord> tables, out string reason)
        {
            tables = null;
            reason = null;

            if (font == null || font.Length < HeaderBytes)
            {
                reason = "the font is empty or shorter than an sfnt header";
                return false;
            }

            if (IsCollection(font))
            {
                reason = CollectionRefusal;
                return false;
            }

            int count = (font[4] << 8) | font[5];
            if (count <= 0 || HeaderBytes + (long)count * EntryBytes > font.Length)
            {
                reason = "the table directory does not fit inside the font";
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<TableRecord>(count);
            long declared = 0;

            for (int i = 0; i < count; i++)
            {
                int at = HeaderBytes + i * EntryBytes;

                if (!TryReadTag(font, at, out string tag))
                {
                    reason = "a table tag contains a byte outside the printable ASCII range " +
                             "OpenType allows, so it could not be carried through unchanged";
                    return false;
                }

                long offset = ReadU32(font, at + 8);
                long length = ReadU32(font, at + 12);

                if (offset + length > font.Length)
                {
                    reason = "table \"" + tag + "\" lies outside the font (" + offset + "+" +
                             length + " of " + font.Length + ")";
                    return false;
                }

                if (!seen.Add(tag))
                {
                    reason = "the directory names table \"" + tag + "\" twice, and which of the two " +
                             "a reader takes is not a choice this layer can make for it";
                    return false;
                }

                result.Add(new TableRecord(tag, (int)offset, (int)length));
                declared += length;
            }

            // Each table lies inside the font, but nothing above says they lie beside each other.
            // Tables that all span the whole font pass every record's check, and a re-emitter then
            // copies the font once per record: 512 records made 512 copies of a 20 MB font. Real
            // tables are disjoint and total less than the font; twice it leaves room for a
            // producer's few overlapping or padded records.
            if (declared > MaxDeclaredTableBytesPerFontByte * (long)font.Length)
            {
                reason = "the tables declare " + declared + " bytes in a font of " + font.Length +
                         ", so they overlap, and a re-emitter copying each would multiply the font";
                return false;
            }

            tables = result;
            return true;
        }

        /// <summary>A copy of one table's bytes.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="font"/> is null.</exception>
        public static byte[] Payload(byte[] font, TableRecord record)
        {
            if (font == null) throw new ArgumentNullException(nameof(font));
            var data = new byte[record.Length];
            Array.Copy(font, record.Offset, data, 0, record.Length);
            return data;
        }

        /// <summary>
        /// Reads a four-byte tag, rejecting any byte outside 0x20–0x7E.
        /// </summary>
        /// <remarks>
        /// OpenType §5.2 restricts a tag to those bytes. The point of rejecting rather than
        /// substituting is that the caller re-emits: a substituted character is written back out as
        /// itself, silently renaming the table.
        /// </remarks>
        private static bool TryReadTag(byte[] font, int at, out string tag)
        {
            tag = null;
            var chars = new char[4];
            for (int i = 0; i < 4; i++)
            {
                byte b = font[at + i];
                if (b < 0x20 || b > 0x7E) return false;
                chars[i] = (char)b;
            }
            tag = new string(chars);
            return true;
        }

        private static uint ReadU32(byte[] data, int at) => IO.BigEndian.U32(data, at);
    }
}
