// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Byte-level layout of a classic cross-reference table (PDF 32000-1:2008, §7.5.4).
    ///
    /// <para><b>Each entry shall be exactly <see cref="EntryLength"/> bytes, end-of-line
    /// included.</b> The layout is a 10-digit byte offset, a space, a 5-digit generation number, a
    /// space, a one-character type (<c>n</c> or <c>f</c>), then a two-byte EOL — so the readable
    /// spellings are <c>"… n\r\n"</c> or <c>"… n \n"</c>. Getting this wrong by one byte shifts
    /// every subsequent entry's meaning for a strict reader.</para>
    ///
    /// <para>This type exists because that row was hand-spelled in seven files. Two of them built it
    /// with <c>StringBuilder.AppendLine</c>, which appends <see cref="System.Environment.NewLine"/>
    /// — 19 visible characters plus a two-byte CRLF is <b>21</b> bytes on Windows and 20 on Linux,
    /// i.e. the emitted PDF was malformed, and differently malformed per platform. Use the members
    /// here rather than writing the row inline.</para>
    /// </summary>
    public static class PdfXrefFormat
    {
        /// <summary>Length in bytes of one cross-reference entry, including its EOL.</summary>
        public const int EntryLength = 20;

        /// <summary>
        /// Largest representable generation number — the field is five digits. Also the generation
        /// the head-of-free-list entry carries by convention.
        /// </summary>
        public const int MaxGenerationNumber = 65535;

        /// <summary>Type character for an in-use entry.</summary>
        public const char InUseType = 'n';

        /// <summary>Type character for a free entry.</summary>
        public const char FreeType = 'f';

        /// <summary>
        /// The head of the free-list: object 0, generation <see cref="MaxGenerationNumber"/>, free.
        /// Exactly <see cref="EntryLength"/> bytes including the trailing newline.
        /// </summary>
        public const string FreeListHead = "0000000000 65535 f \n";

        /// <summary>
        /// Largest byte offset the 10-digit fixed-width field can hold — 9 999 999 999, about
        /// 9.31 GiB.
        /// </summary>
        /// <remarks>
        /// A file past this needs a cross-reference <i>stream</i> (PDF 1.5+), whose fields are
        /// sized to the values they carry.
        /// </remarks>
        public const long MaxOffset = 9_999_999_999L;

        /// <summary>
        /// Formats one cross-reference entry, exactly <see cref="EntryLength"/> bytes including the
        /// trailing newline.
        /// </summary>
        /// <param name="offset">Byte offset of the object, or 0 for a free entry.</param>
        /// <param name="generation">Generation number; clamped to <see cref="MaxGenerationNumber"/>.</param>
        /// <param name="inUse"><c>true</c> for an <c>n</c> entry, <c>false</c> for an <c>f</c> entry.</param>
        /// <exception cref="System.InvalidOperationException">
        /// <paramref name="offset"/> exceeds <see cref="MaxOffset"/>, which cannot be written in ten
        /// digits.
        /// </exception>
        /// <remarks>
        /// <b>The offset ceiling is enforced here, not only at the caller.</b> In <c>PdfWriter</c>
        /// alone, this helper — the one the doc above tells everyone to use, and the one four other
        /// writers do use — would happily format an 11-digit offset. That is a
        /// <b>21-byte</b> row against the <see cref="EntryLength"/> invariant this very type
        /// declares, which shifts the meaning of every entry after it. Refusing beats emitting a
        /// table that is silently one byte out of step.
        /// </remarks>
        public static string Entry(long offset, int generation = 0, bool inUse = true)
        {
            if (offset < 0) offset = 0;
            if (offset > MaxOffset)
                throw new System.InvalidOperationException(
                    $"PDF xref offset {offset} exceeds the 10-digit fixed-width xref format limit " +
                    "(~9.31 GiB). Switch to a cross-reference stream (PDF 1.5+) for files this large.");
            if (generation < 0) generation = 0;
            if (generation > MaxGenerationNumber) generation = MaxGenerationNumber;

            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:D10} {1:D5} {2} \n",
                offset,
                generation,
                inUse ? InUseType : FreeType);
        }
    }
}
