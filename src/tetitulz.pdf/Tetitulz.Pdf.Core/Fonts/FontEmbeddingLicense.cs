// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// What a font program's <c>OS/2</c> <c>fsType</c> field says about embedding it (OpenType
    /// spec, <c>OS/2</c> table, offset 8).
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> Embedding a font redistributes it, and <c>fsType</c> is where a
    /// foundry records what it permits. Nothing in this library read the field — <c>PdfFontEmbedder</c>
    /// parsed it and discarded it on the next line under a comment naming it "the embedding-permissions
    /// field" — so a font whose licence forbids embedding was embedded like any other.</para>
    ///
    /// <para><b>It is metadata, not a contract, and the distinction decides who may override it.</b>
    /// A caller who hands this library a font program has made a licensing decision this code cannot
    /// see: they may hold a licence the bits know nothing about, so refusing their font would be this
    /// library overruling its own user. A font the library picks up off the machine by itself is the
    /// opposite case — nobody decided anything, and the only statement available is the one in the
    /// file. So the rule is: <b>report for a caller-supplied program, skip for a system-substituted
    /// one</b> (see <c>SystemFontResolver</c>, and <c>FontResolutionRequest.AllowRestrictedSystemFonts</c>
    /// for the caller who does hold the licences).</para>
    ///
    /// <para><b>Bits 0–3 are one mutually-exclusive value, not four flags</b>, and reading them as
    /// flags is the classic way to get this wrong: <c>0x0006</c> is <i>Preview &amp; Print</i> (the
    /// least restrictive bit set wins), not "restricted <i>and</i> preview". Hence the mask-and-compare
    /// in <see cref="RestrictedLicense"/> rather than a bit test.</para>
    /// </remarks>
    public readonly struct FontEmbeddingLicense
    {
        /// <summary>Bits 0–3: the mutually-exclusive licence value.</summary>
        private const int LicenceMask = 0x000F;

        /// <summary>Bits 0–3 == 2: "must not be modified, embedded or exchanged".</summary>
        private const int RestrictedValue = 0x0002;

        /// <summary>Bit 8: the font may be embedded whole but not subset.</summary>
        private const int NoSubsettingBit = 0x0100;

        /// <summary>Bit 9: only bitmaps may be embedded, never the outlines.</summary>
        private const int BitmapOnlyBit = 0x0200;

        private FontEmbeddingLicense(bool declared, int fsType)
        {
            IsDeclared = declared;
            FsType = fsType;
        }

        /// <summary>
        /// Whether the font actually carried an <c>OS/2</c> table to read. A bare CFF, a Type 1
        /// program and a malformed sfnt all say nothing about embedding — which is not the same as
        /// permitting it, but is the only honest answer and is how this library treats it.
        /// </summary>
        public bool IsDeclared { get; }

        /// <summary>The raw <c>fsType</c> value, or 0 when <see cref="IsDeclared"/> is false.</summary>
        public int FsType { get; }

        /// <summary>The font declares Restricted License embedding: it must not be embedded.</summary>
        public bool RestrictedLicense => IsDeclared && (FsType & LicenceMask) == RestrictedValue;

        /// <summary>The font permits embedding bitmaps only, so its outlines must not be embedded.</summary>
        public bool BitmapEmbeddingOnly => IsDeclared && (FsType & BitmapOnlyBit) != 0;

        /// <summary>
        /// The font may be embedded whole but not subset. This library subsets by default, so a
        /// caller honouring it must embed the full program.
        /// </summary>
        public bool SubsettingProhibited => IsDeclared && (FsType & NoSubsettingBit) != 0;

        /// <summary>
        /// Whether embedding the font's outlines is permitted — the question every embedding site
        /// asks. False only for a font that says so; an undeclared licence permits.
        /// </summary>
        public bool PermitsOutlineEmbedding => !RestrictedLicense && !BitmapEmbeddingOnly;

        /// <summary>A font that said nothing, because it had no <c>OS/2</c> table to say it in.</summary>
        public static FontEmbeddingLicense Undeclared => default;

        /// <summary>The licence a given <c>fsType</c> value expresses.</summary>
        /// <param name="fsType">The raw 16-bit field.</param>
        public static FontEmbeddingLicense FromFsType(int fsType)
            => new FontEmbeddingLicense(true, fsType & 0xFFFF);

        /// <summary>
        /// Reads the licence out of a font program's bytes. <see cref="Undeclared"/> for anything
        /// this library cannot parse as an sfnt — a bare CFF, a Type 1 program, a font collection, or
        /// a corrupt file.
        /// </summary>
        /// <param name="fontProgram">TrueType, OpenType or WOFF bytes.</param>
        /// <remarks>
        /// Routed through <see cref="TrueTypeFontProgram.Parse"/> rather than walking the table
        /// directory again: that walk already handles WOFF decompression and refuses a <c>ttcf</c>
        /// collection by name, and a second copy of it would be a second set of answers to
        /// "what is in this file".
        /// </remarks>
        public static FontEmbeddingLicense Read(byte[] fontProgram)
            => TrueTypeFontProgram.Parse(fontProgram)?.EmbeddingLicense ?? Undeclared;

        /// <summary>
        /// The licence in an <c>OS/2</c> table the caller has <b>already located</b> — the field is
        /// a big-endian <c>uint16</c> at offset 8.
        /// </summary>
        /// <param name="data">The font program.</param>
        /// <param name="os2TableOffset">Offset of the <c>OS/2</c> table within it.</param>
        /// <remarks>
        /// <para><b>This is the one place that knows where the field lives</b>, and it takes an
        /// offset rather than finding the table because its three callers — the font parser and the
        /// two subsetters — have each already walked the table directory for their own reasons. A
        /// fourth walk would cost a read; a second copy of "offset 8, big-endian" would cost
        /// correctness, and reading two bytes early lands on <c>usWidthClass</c>, which is a small
        /// number that looks entirely plausible as an <c>fsType</c>.</para>
        ///
        /// <para>Bounds-checked rather than trusted: a truncated table yields
        /// <see cref="Undeclared"/>, because a font that does not contain the field has not made a
        /// statement about embedding.</para>
        /// </remarks>
        internal static FontEmbeddingLicense FromOs2Table(byte[] data, long os2TableOffset)
        {
            if (data == null || os2TableOffset < 0 || os2TableOffset + 10 > data.Length)
                return Undeclared;

            int at = (int)os2TableOffset + 8;
            return FromFsType((data[at] << 8) | data[at + 1]);
        }

        /// <summary>
        /// A one-clause description for a diagnostic, or <c>null</c> when the font permits everything
        /// this library does — so a caller can append it only when there is something to say.
        /// </summary>
        public string Describe()
        {
            if (!IsDeclared) return null;
            if (RestrictedLicense)
                return "the font's OS/2 fsType declares Restricted License embedding (0x" +
                       FsType.ToString("X4") + "), which forbids embedding it";
            if (BitmapEmbeddingOnly)
                return "the font's OS/2 fsType permits bitmap embedding only (0x" +
                       FsType.ToString("X4") + "), so its outlines may not be embedded";
            if (SubsettingProhibited)
                return "the font's OS/2 fsType prohibits subsetting (0x" +
                       FsType.ToString("X4") + "); the whole program must be embedded";
            return null;
        }
    }
}
