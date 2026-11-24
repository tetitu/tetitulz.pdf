// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Unwraps a <b>WOFF 1.0</b> container (W3C WOFF File Format 1.0) back into the plain sfnt
    /// every other font consumer in this tree already understands. A WOFF 1.0 file is the same
    /// TrueType/OpenType tables in the same order, each optionally zlib-compressed, behind a
    /// 44-byte header and a 20-byte-per-table directory — so unwrapping is a re-layout plus an
    /// inflate, not a font transformation.
    ///
    /// <para><b>WOFF 2.0 is a different job and lives in <see cref="Woff2Decoder"/>.</b> It
    /// Brotli-compresses a <i>transformed</i> table set — <c>glyf</c>/<c>loca</c> are re-encoded,
    /// not merely deflated — so unwrapping one is a reconstruction, and it shares nothing with this
    /// file but the four-byte signature test. <see cref="IsWoff2"/> stays here because the two
    /// signatures are read the same way and a caller that has one has the other.</para>
    ///
    /// <para><b>Every buffer here is sized from what actually inflates.</b> The directory's
    /// <c>origLength</c> is a number the file chose; trusting it is how a 20-byte container asks
    /// for a 4&#160;GB allocation. Each table is inflated through
    /// <see cref="FlateDecodeFilter"/> with <see cref="StreamFilter.MaxOutputBytes"/> set to
    /// what is left of this call's budget, so the ceiling bounds what is allocated rather than
    /// being noticed after the fact.</para>
    ///
    /// <para><c>internal</c> on purpose: the single caller is
    /// <see cref="TrueTypeFontProgram.Parse"/>, which is the one hook every font consumer in the
    /// tree already goes through (@font-face, a caller's font provider, the Builder's file scan,
    /// the accessibility remediator). A public decoder would be a second thing to remember to
    /// call.</para>
    /// </summary>
    internal static class WoffDecoder
    {
        /// <summary>'wOFF' — the WOFF 1.0 signature (WOFF 1.0 §3, WOFFHeader.signature).</summary>
        private const uint Woff1Signature = 0x774F4646;

        /// <summary>'wOF2' — the WOFF 2.0 signature.</summary>
        private const uint Woff2Signature = 0x774F4632;

        private const int HeaderSize = 44;
        private const int WoffDirectoryEntrySize = 20;
        private const int SfntDirectoryEntrySize = 16;
        private const int SfntHeaderSize = 12;

        /// <summary>
        /// Ceiling on the reassembled sfnt. 64&#160;MiB is comfortably above the largest fonts
        /// that exist at all (the Source Han pan-CJK OpenType collections are ~40&#160;MB) and
        /// absurdly above anything served as a *web* font, which is what a WOFF is. It is a cap
        /// on the possible, not on the polite: the budget is what stops a 200-byte container
        /// whose tables are zlib zero-bombs from inflating without limit.
        /// </summary>
        private const long MaxSfntBytes = 64L * 1024 * 1024;

        /// <summary>True when these bytes open with the WOFF 1.0 signature.</summary>
        internal static bool IsWoff1(byte[] data) => HasSignature(data, Woff1Signature);

        /// <summary>
        /// True when these bytes open with the WOFF 2.0 signature. Kept beside its WOFF 1.0 sibling
        /// so the two containers are recognised in one place; <see cref="Woff2Decoder.IsWoff2"/> is
        /// the same test, stated where the decoding happens.
        /// </summary>
        internal static bool IsWoff2(byte[] data) => HasSignature(data, Woff2Signature);

        private static bool HasSignature(byte[] data, uint signature) =>
            data != null && data.Length >= 4 && ReadU32(data, 0) == signature;

        /// <summary>
        /// The sfnt inside a WOFF 1.0 container, or <c>null</c> when the container is malformed,
        /// hostile, or larger than <see cref="MaxSfntBytes"/> once inflated. Never throws: the
        /// caller's contract is a null font program, and a font that fails to unwrap is
        /// indistinguishable to it from one that fails to parse.
        /// </summary>
        internal static byte[] Decode(byte[] woff)
        {
            _lastError = null;
            try
            {
                byte[] sfnt = DecodeCore(woff);
                if (sfnt == null)
                    _lastError = "the WOFF 1.0 container is malformed (header, directory or a table's lengths), or past " +
                                 MaxSfntBytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes once inflated";
                return sfnt;
            }
            catch (Exception ex)
            {
                // Includes the PdfException FlateDecodeFilter throws for a stream that exceeds
                // its budget — a refusal, and the whole point of setting the budget — and what the
                // reader threw past its checks, named here rather than read as "not a font".
                _lastError = "the WOFF 1.0 container did not unwrap: " + ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        [ThreadStatic] private static string _lastError;

        /// <summary>Why the last <see cref="Decode"/> on this thread returned null, or null when it returned a font. Reset at the start of every call.</summary>
        internal static string LastError => _lastError;

        private static byte[] DecodeCore(byte[] woff)
        {
            if (!IsWoff1(woff) || woff.Length < HeaderSize) return null;

            uint flavor = ReadU32(woff, 4);            // becomes the sfnt version verbatim
            int numTables = ReadU16(woff, 12);
            if (numTables == 0) return null;

            // The directory must fit inside the bytes we were handed. This is what bounds
            // numTables — a separate "sane count" cap would be a second, looser statement of the
            // same fact, and the looser one is the one that eventually gets trusted.
            long directoryEnd = (long)HeaderSize + (long)numTables * WoffDirectoryEntrySize;
            if (directoryEnd > woff.Length) return null;

            var entries = new List<Entry>(numTables);
            var tags = new HashSet<string>(StringComparer.Ordinal);
            long budget = MaxSfntBytes;

            for (int i = 0; i < numTables; i++)
            {
                int at = HeaderSize + i * WoffDirectoryEntrySize;
                var entry = new Entry
                {
                    Tag = ReadTag(woff, at),
                    Checksum = ReadU32(woff, at + 16),
                    Order = i,
                };

                // A tag names one table. Two entries with one tag shared Reassemble's offset slot:
                // both directory records pointed at the second table's data, the first keeping its
                // own length, and the sfnt came out malformed despite this decoder's promise.
                if (!tags.Add(entry.Tag)) return null;

                long offset = ReadU32(woff, at + 4);
                long compLength = ReadU32(woff, at + 8);
                long origLength = ReadU32(woff, at + 12);

                // Long arithmetic throughout: offset and compLength are uint32 in the file, so
                // `offset + compLength` in 32 bits wraps and a crafted pair sails past a naive
                // in-bounds check.
                if (offset < 0 || compLength < 0 || offset + compLength > woff.Length) return null;
                if (compLength > origLength) return null;   // WOFF 1.0 §4: never larger than the original

                if (compLength == origLength)
                {
                    if (compLength > budget) return null;
                    entry.Data = new byte[compLength];
                    Array.Copy(woff, (int)offset, entry.Data, 0, (int)compLength);
                }
                else
                {
                    var compressed = new byte[compLength];
                    Array.Copy(woff, (int)offset, compressed, 0, (int)compLength);

                    // The budget, not origLength, sizes the inflate. A table declaring
                    // origLength == 4 and inflating to 4 GB is exactly the file this guard is for.
                    var flate = new FlateDecodeFilter { MaxOutputBytes = budget };
                    entry.Data = flate.Decode(compressed, null);
                    if (entry.Data == null) return null;
                }

                // Cannot go negative: a stored table was checked against the budget above, and an
                // inflated one was capped at it, so no "budget < 0" check follows.
                budget -= entry.Data.Length;
                entries.Add(entry);
            }

            // The sfnt DIRECTORY must be sorted by tag (OpenType §5.1); a WOFF directory is
            // supposed to be too, but a file that is not still has to come out well-formed.
            //
            // The table DATA is a separate question, not answered the same way: WOFF 1.0 §6 requires the reconstructed data to be in the same order as the
            // original font, and Entry.Order carries that. It matters because this decoder passes
            // each table's checksum AND head.checkSumAdjustment through verbatim rather than
            // recomputing them -- and the adjustment is a function of the whole file, offsets
            // included. Re-laying the data invalidated it, so a byte-identical round trip, which
            // §5 promises for a well-formed input, was unreachable.
            entries.Sort((a, b) => string.CompareOrdinal(a.Tag, b.Tag));

            return Reassemble(flavor, entries);
        }

        /// <summary>Writes the sfnt header, the 16-byte-per-table directory, and 4-byte-aligned table data.</summary>
        private static byte[] Reassemble(uint flavor, List<Entry> entries)
        {
            int numTables = entries.Count;
            long total = SfntHeaderSize + (long)numTables * SfntDirectoryEntrySize;
            foreach (Entry e in entries) total += Align4(e.Data.Length);
            if (total > MaxSfntBytes || total > int.MaxValue) return null;

            var output = new byte[total];
            WriteU32(output, 0, flavor);
            WriteU16(output, 4, numTables);

            // searchRange / entrySelector / rangeShift: the binary-search hints from OpenType
            // §5.1. Nothing in this tree reads them, but a consumer that does gets a wrong answer
            // rather than a missing one if they are left zero.
            int entrySelector = 0;
            while (1 << (entrySelector + 1) <= numTables) entrySelector++;
            int searchRange = (1 << entrySelector) * SfntDirectoryEntrySize;
            WriteU16(output, 6, searchRange);
            WriteU16(output, 8, entrySelector);
            WriteU16(output, 10, numTables * SfntDirectoryEntrySize - searchRange);

            // Offsets are assigned in CONTAINER order (WOFF §6: the reconstructed data is in the
            // same order as the original font), while the directory below is written in tag order.
            // SfntBuilder structurally cannot express that split -- it lays data out in its own
            // sorted order -- which is one of the reasons this is not routed through it.
            var byContainerOrder = new List<Entry>(entries);
            byContainerOrder.Sort((a, b) => a.Order.CompareTo(b.Order));

            var offsetByTag = new Dictionary<string, int>(numTables, StringComparer.Ordinal);
            int cursor = SfntHeaderSize + numTables * SfntDirectoryEntrySize;
            foreach (Entry e in byContainerOrder)
            {
                offsetByTag[e.Tag] = cursor;
                Array.Copy(e.Data, 0, output, cursor, e.Data.Length);
                cursor += Align4(e.Data.Length);
            }

            for (int i = 0; i < numTables; i++)
            {
                Entry e = entries[i];
                int at = SfntHeaderSize + i * SfntDirectoryEntrySize;
                WriteTag(output, at, e.Tag);
                WriteU32(output, at + 4, e.Checksum);
                WriteU32(output, at + 8, (uint)offsetByTag[e.Tag]);
                // The length is what inflated, not what the container declared it would. A
                // directory whose length disagrees with the bytes behind it points every reader
                // at the wrong place, and origLength is the half of that pair we do not control.
                WriteU32(output, at + 12, (uint)e.Data.Length);
            }
            return output;
        }

        private sealed class Entry
        {
            public string Tag;
            public uint Checksum;
            public byte[] Data;

            /// <summary>Position in the WOFF container, which is the ORIGINAL sfnt's data order.</summary>
            public int Order;
        }

        private static int Align4(int n) => (n + 3) & ~3;

        private static uint ReadU32(byte[] d, int at) => IO.BigEndian.U32(d, at);

        private static int ReadU16(byte[] d, int at) => IO.BigEndian.U16(d, at);

        private static string ReadTag(byte[] d, int at) =>
            new string(new[] { (char)d[at], (char)d[at + 1], (char)d[at + 2], (char)d[at + 3] });

        private static void WriteU32(byte[] d, int at, uint v) => IO.BigEndian.Put32(d, at, v);

        private static void WriteU16(byte[] d, int at, int v) => IO.BigEndian.Put16(d, at, v);

        private static void WriteTag(byte[] d, int at, string tag)
        {
            for (int i = 0; i < 4; i++) d[at + i] = i < tag.Length ? (byte)tag[i] : (byte)' ';
        }
    }
}
