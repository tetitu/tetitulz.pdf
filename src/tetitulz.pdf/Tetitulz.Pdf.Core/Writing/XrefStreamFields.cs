// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// The field layout of a cross-reference <b>stream</b> (ISO 32000-1 §7.5.8): how wide each of
    /// the three fields is, what <c>/W</c> and <c>/Index</c> therefore say, and how one row is
    /// packed into the stream's bytes.
    /// </summary>
    /// <remarks>
    /// <para><b>Two writers emit these streams and each had its own copy of all of it.</b>
    /// <see cref="IncrementalUpdateWriter"/> appends one per revision and
    /// <see cref="PdfCompressedFileWriter"/> writes one per file, and between them they carried two
    /// byte-for-byte identical <c>MaxValueForWidth</c> methods, two field-2 width computations, two
    /// <c>/Index</c> run-length compactions, two <c>/W</c> arrays and two record packers — the same
    /// five answers to the same five questions, differing only in the name of the row struct they
    /// walked.</para>
    ///
    /// <para><b>The pairing is what makes a copy dangerous, not the arithmetic.</b> <c>/W</c>
    /// <i>declares</i> the widths that the packer <i>uses</i>; a reader takes the declaration at its
    /// word and slices the payload by it. Change one side alone — widen field 2 in the packer, add a
    /// third byte to the generation field, stop clamping — and the stream is not corrupt in any way
    /// a writer can notice: it parses, and every entry after the first names the wrong object. With
    /// the declaration and the packing in one type, the pair cannot be half-changed.</para>
    ///
    /// <para><see cref="PdfXrefFormat"/> is the counterpart for the classic §7.5.4 table, and its
    /// own summary records that that row was hand-spelled in seven files before it existed. This is
    /// the same consolidation one section later; the two are deliberately separate types because a
    /// table's fields are fixed-width decimal text and a stream's are sized to the values they
    /// carry, which is the whole reason §7.5.8 exists.</para>
    /// </remarks>
    internal static class XrefStreamFields
    {
        /// <summary>Field 1 — the entry type — is one byte (Table 18).</summary>
        internal const int TypeFieldWidth = 1;

        /// <summary>
        /// Field 3 — a generation for a type-1 row, an index within the container for a type-2 one —
        /// is written in two bytes, which covers <see cref="PdfXrefFormat.MaxGenerationNumber"/>.
        /// </summary>
        internal const int GenerationFieldWidth = 2;

        /// <summary>
        /// Bytes needed for field 2, which carries a byte offset for a type-1 row and an
        /// object-stream number for a type-2 one. Four covers every file below 4 GiB — every real
        /// document — and the width widens rather than truncating a larger offset into an unreadable
        /// cross-reference.
        /// </summary>
        internal static int Field2Width<TRow>(IReadOnlyList<TRow> rows, Func<TRow, long> field2)
        {
            long max = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                long value = field2(rows[i]);
                if (value > max) max = value;
            }

            int width = 4;
            while (width < 8 && max > MaxValueForWidth(width))
                width++;
            return width;
        }

        private static long MaxValueForWidth(int width)
            => width >= 8 ? long.MaxValue : (1L << (8 * width)) - 1;

        /// <summary>
        /// <c>/W</c> (Table 17): the widths of the three fields, in order, exactly as
        /// <see cref="WriteRecord"/> writes them.
        /// </summary>
        internal static PdfArray WidthArray(int field2Width)
        {
            var w = new PdfArray();
            w.Add(new PdfNumber(TypeFieldWidth));         // field 1: entry type
            w.Add(new PdfNumber(field2Width));            // field 2: offset / object-stream number
            w.Add(new PdfNumber(GenerationFieldWidth));   // field 3: generation / index in stream
            return w;
        }

        /// <summary>
        /// <c>/Index</c> (Table 17): the rows' object numbers as <c>[first count …]</c> pairs, one
        /// pair per run of consecutive numbers. <paramref name="sortedRows"/> must already be in
        /// object-number order — the run detection compares each row with the one before it.
        /// </summary>
        internal static PdfArray IndexArray<TRow>(IReadOnlyList<TRow> sortedRows, Func<TRow, int> objectNumber)
        {
            var index = new PdfArray();

            int i = 0;
            while (i < sortedRows.Count)
            {
                int start = objectNumber(sortedRows[i]);
                int j = i + 1;
                while (j < sortedRows.Count &&
                       objectNumber(sortedRows[j]) == objectNumber(sortedRows[j - 1]) + 1)
                    j++;

                index.Add(new PdfNumber(start));
                index.Add(new PdfNumber(j - i));
                i = j;
            }

            return index;
        }

        /// <summary>Bytes one row occupies, for the payload buffer's size.</summary>
        internal static int RecordSize(int field2Width)
            => TypeFieldWidth + field2Width + GenerationFieldWidth;

        /// <summary>
        /// Packs one row into <paramref name="data"/> at <paramref name="p"/>, advancing it:
        /// the type byte, then field 2 big-endian at <paramref name="field2Width"/> bytes, then
        /// field 3 big-endian in two, clamped to <see cref="PdfXrefFormat.MaxGenerationNumber"/> so
        /// a bogus value can never overflow its slot.
        /// </summary>
        internal static void WriteRecord(
            byte[] data, ref int p, byte type, long field2, int field3, int field2Width)
        {
            data[p++] = type;

            for (int b = field2Width - 1; b >= 0; b--)
                data[p++] = (byte)((field2 >> (8 * b)) & 0xFF);

            if (field3 < 0) field3 = 0;
            if (field3 > PdfXrefFormat.MaxGenerationNumber) field3 = PdfXrefFormat.MaxGenerationNumber;
            data[p++] = (byte)((field3 >> 8) & 0xFF);
            data[p++] = (byte)(field3 & 0xFF);
        }
    }
}
