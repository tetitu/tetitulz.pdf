// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.IO;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Answers "could this font file cover these code points?" and "what family does it call
    /// itself?" by reading the sfnt table directory and the one table each question needs, rather
    /// than the whole file.
    ///
    /// <para><b>Why this exists.</b> The exhaustive font scans in <c>DocxToPdf</c>'s
    /// <c>FontResolver</c> and <c>Builder</c>'s <c>BuilderFontResolver</c> walk every font on the
    /// machine — for coverage when nothing covers a run's text, and for a <b>family-name</b>
    /// match whenever the requested family is not in <c>FontResolver</c>'s small filename map,
    /// which includes most of what modern Word emits. Each candidate cost a full
    /// <see cref="File.ReadAllBytes(string)"/>. Measured on a host with 441 font files: 395 ms and
    /// 461 MB allocated for the scan, against <b>34 ms and 3.6 MB</b> for the directory and
    /// <c>cmap</c> alone — 11.6× the speed and 129× less allocated.
    /// <c>FontScanBenchmarks</c> keeps both numbers honest.</para>
    ///
    /// <para><b>Why it is not a second implementation of the coverage rule.</b> Two
    /// implementations of one rule silently disagreeing is this repository's most-repeated
    /// defect class. So this does not parse a
    /// <c>cmap</c> itself. It extracts the table's bytes, reassembles them through
    /// <see cref="SfntBuilder"/> into a synthetic single-table sfnt, and hands that to
    /// <see cref="TrueTypeFontProgram.Parse(byte[])"/> — <b>the same parser, reaching the same
    /// <see cref="TrueTypeFontProgram.Covers"/></b>. The only thing this file decides is which bytes
    /// to feed it.</para>
    ///
    /// <para>That is sound because both tables parse independently of every other one:
    /// <see cref="TrueTypeFontProgram.Covers"/> reads only the code-point map and its construction
    /// never consults <c>maxp</c>'s glyph count (used for <c>hmtx</c> sizing) or <c>head</c>, and
    /// <see cref="TrueTypeFontProgram.FamilyName"/> comes from <c>name</c> record 1 alone. Verified
    /// empirically rather than argued — <c>FontCoverageProbeParityTests</c> asserts the probe and a
    /// full parse agree on <b>every font installed on the host</b>, for coverage and for the
    /// family name.</para>
    ///
    /// <para><b>It may only ever reject.</b> <see cref="Covers"/> returns <c>false</c> only where a
    /// full parse would reach the same verdict, and <c>null</c> — meaning "read the file properly" —
    /// for everything it cannot decide: WOFF containers, font collections it should not guess at,
    /// unreadable files, and any <c>cmap</c> too large to be worth holding. A caller must treat
    /// <c>null</c> and <c>true</c> identically, as "do the full read", so that the shipping decision
    /// is always made by the real program.</para>
    /// </summary>
    internal static class FontCoverageProbe
    {
        /// <summary>An sfnt header is 12 bytes; each table directory entry is 16.</summary>
        private const int HeaderBytes = 12;
        private const int DirectoryEntryBytes = 16;

        /// <summary>
        /// A directory cannot plausibly hold more than this. Bounds the one allocation made before
        /// anything about the file has been validated.
        /// </summary>
        private const int MaxTables = 512;

        /// <summary>
        /// Past this a table is not worth probing — the point is to read less than the file, and
        /// Core's own parse caps apply on the full path.
        /// </summary>
        private const int MaxTableBytes = 4 * 1024 * 1024;

        /// <summary>
        /// <c>false</c> when the file definitively does not cover every one of
        /// <paramref name="codePoints"/>; <c>true</c> when it does; <c>null</c> when the question
        /// could not be answered cheaply and the caller should do the full read.
        /// </summary>
        internal static bool? Covers(string path, IReadOnlyList<int> codePoints)
        {
            if (codePoints == null || codePoints.Count == 0) return null;

            var program = ParseTableOnly(path, "cmap", missingTableIsAnswer: true);
            if (program == null) return null;

            // An Arabic presentation form the cmap lacks, over a letter it has, may be reached through
            // the font's GSUB (TrueTypeFontProgram.CoversShaped), which a cmap-only parse cannot see:
            // undecided, never a rejection.
            bool undecided = false;
            foreach (int codePoint in codePoints)
            {
                if (program.Covers(codePoint)) continue;
                if (Text.OpenTypeShaper.TryGetJoiningFeature(codePoint, out int letter, out _) && program.Covers(letter))
                {
                    undecided = true;
                    continue;
                }
                return false;
            }
            return undecided ? (bool?)null : true;
        }

        /// <summary>
        /// The font's <c>name</c>-table family, or <c>null</c> when it could not be read cheaply — in
        /// which case the caller should do the full read rather than treat the font as unnamed.
        ///
        /// <para>This is the half that matters most in practice. <c>FontResolver.CandidateFiles</c>
        /// falls back to matching every installed font's family name whenever the requested family is
        /// missing from its 17-entry filename map, so an ordinary document naming a font like Aptos or
        /// Calibri Light would, read whole, read and parse <b>every font on the machine</b> — 210 MiB
        /// for a one-paragraph file. That is the common case, not the "nothing covers the text" edge
        /// case.</para>
        /// </summary>
        internal static string FamilyName(string path)
            => ParseTableOnly(path, "name", missingTableIsAnswer: false)?.FamilyName;

        /// <summary>
        /// Extracts one table and reads it back through the real parser.
        ///
        /// <para><paramref name="missingTableIsAnswer"/> is what distinguishes the two questions. A
        /// font with no <c>cmap</c> covers nothing, which is a definite verdict, so the coverage caller
        /// wants an empty program rather than a deferral. A font with no <c>name</c> tells us nothing
        /// about its family, so that caller must defer to the full read instead.</para>
        /// </summary>
        private static TrueTypeFontProgram ParseTableOnly(string path, string tag, bool missingTableIsAnswer)
        {
            if (string.IsNullOrEmpty(path)) return null;

            try
            {
                using var stream = File.OpenRead(path);
                if (stream.Length < HeaderBytes) return null;

                var header = new byte[HeaderBytes];
                if (StreamBytes.Fill(stream, header, 0, header.Length) < header.Length) return null;

                uint version = ReadU32(header, 0);

                // 'wOFF' / 'wOF2': the sfnt directory is compressed or absent, so the bytes at
                // offset 4 are not a table count. Parse handles WOFF 1 by decoding and refuses
                // WOFF 2 by name; either way that decision belongs to it, not here.
                if (version == 0x774F4646u || version == 0x774F4632u) return null;

                // 'ttcf': a collection, which Parse also declines. An empty program claims it covers
                // nothing, which is the verdict the full path reaches too — without reading what is
                // usually the largest file on the machine.
                if (version == 0x74746366u)
                    return missingTableIsAnswer ? EmptyProgram() : null;

                int numTables = (header[4] << 8) | header[5];
                if (numTables <= 0 || numTables > MaxTables) return null;

                long directoryBytes = (long)numTables * DirectoryEntryBytes;
                if (HeaderBytes + directoryBytes > stream.Length) return null;

                var directory = new byte[directoryBytes];
                if (StreamBytes.Fill(stream, directory, 0, directory.Length) < directory.Length) return null;

                if (!TryFindTable(directory, numTables, tag, stream.Length, out long offset, out int length))
                    return missingTableIsAnswer ? EmptyProgram() : null;
                if (length > MaxTableBytes) return null;

                stream.Seek(offset, SeekOrigin.Begin);
                var table = new byte[length];
                if (StreamBytes.Fill(stream, table, 0, table.Length) < table.Length) return null;

                // The whole point: one table, reassembled by the single sfnt assembler, read by the
                // one parser. Every other table is deliberately absent -- Parse treats them all as
                // optional, and neither answer consults them.
                return TrueTypeFontProgram.Parse(SfntBuilder.Build(version, new[] { (tag, table) }));
            }
            catch
            {
                // Unreadable, truncated, or a shape the reassembly could not express: let the full
                // path decide, and report whatever it reports.
                return null;
            }
        }

        /// <summary>
        /// A program with no tables: covers nothing. Stands for "the full parse reaches this same
        /// verdict", so the caller need not read the file to learn it.
        /// </summary>
        private static TrueTypeFontProgram EmptyProgram()
            => TrueTypeFontProgram.Parse(SfntBuilder.Build(0x00010000u, Array.Empty<(string, byte[])>()));

        /// <summary>The named entry's offset and length, bounds-checked against the file.</summary>
        private static bool TryFindTable(byte[] directory, int numTables, string tag, long fileLength,
            out long offset, out int length)
        {
            offset = 0;
            length = 0;

            for (int i = 0; i < numTables; i++)
            {
                int entry = i * DirectoryEntryBytes;
                if (directory[entry] != (byte)tag[0] || directory[entry + 1] != (byte)tag[1] ||
                    directory[entry + 2] != (byte)tag[2] || directory[entry + 3] != (byte)tag[3]) continue;

                long tableOffset = ReadU32(directory, entry + 8);
                long tableLength = ReadU32(directory, entry + 12);

                if (tableLength <= 0 || tableOffset < HeaderBytes) return false;
                if (tableOffset + tableLength > fileLength) return false;
                if (tableLength > int.MaxValue) return false;

                offset = tableOffset;
                length = (int)tableLength;
                return true;
            }
            return false;
        }

        private static uint ReadU32(byte[] buffer, int index) => IO.BigEndian.U32(buffer, index);
    }
}
