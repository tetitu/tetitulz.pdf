// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Tetitulz.Pdf.TestHelpers;

/// <summary>
/// Builds a genuine sfnt TrueType font containing <c>.notdef</c> plus one glyph per requested
/// character, and reads a format-4 cmap back out of one.
///
/// <para>
/// This exists so the tests never touch the filesystem and never depend on which fonts the host
/// has installed, while still exercising the real metric parser and the real subsetter: a
/// hand-waved byte array would make every assertion about <c>/Length1</c>, the descriptor or
/// the subset cmap meaningless.
/// </para>
/// </summary>
public static class MinimalTrueType
{
    /// <summary>
    /// <see cref="Build(string)"/> with caller-supplied tables laid over the generated ones — a
    /// hand-built <c>cmap</c>, say, to reach a subtable form the generator does not write.
    /// </summary>
    public static byte[] BuildWithTables(string characters, Dictionary<string, byte[]> extraTables)
        => Build(characters, 3, 1, distinctWidths: false, extraTables: extraTables);

    public static byte[] Build(string characters)
        => Build(characters, cmapPlatformId: 3, cmapEncodingId: 1);

    /// <summary>
    /// Advance width this builder gives <c>.notdef</c> when <see cref="BuildWithDistinctWidths"/>
    /// is used. Deliberately unlike <see cref="MappedGlyphAdvance"/> so a test can tell a real
    /// glyph lookup from the <c>.notdef</c> fallback — with the uniform 600 that
    /// <see cref="Build(string)"/> gives every glyph, the two are indistinguishable.
    /// </summary>
    public const int NotdefAdvance = 100;

    /// <summary>Advance width given to the first mapped glyph by <see cref="BuildWithDistinctWidths"/>.</summary>
    public const int MappedGlyphAdvance = 610;

    /// <summary>
    /// As <see cref="Build(string)"/>, but <c>.notdef</c> is <see cref="NotdefAdvance"/> wide and
    /// glyph <c>i</c> is <see cref="MappedGlyphAdvance"/><c> + 10*(i-1)</c>, so an advance read
    /// back out of a PDF <c>/Widths</c> array identifies which glyph was looked up.
    /// </summary>
/// <summary>
    /// A <b>symbolic</b> font: a single <c>(3, 0)</c> cmap whose codes sit in the Windows symbol
    /// PUA at <c>0xF000 + code</c>, which is how symbol.ttf, wingdings and marlett are really
    /// built — and which nothing in this helper could express before.
    /// </summary>
    /// <remarks>
    /// That gap is why a silent defect stayed green: the embedder looked up the plain code point,
    /// found nothing, and laid every glyph out at .notdef''s advance. A synthetic (3, 0) font whose
    /// codes are the literal characters does not reproduce it, because the plain lookup succeeds.
    /// Advances are distinct so a wrong width is a wrong NUMBER, not a wrong shape.
    /// </remarks>
    public static byte[] BuildSymbolic(string characters)
        => Build(characters, cmapPlatformId: 3, cmapEncodingId: 0, distinctWidths: true,
                 cmapCodeOffset: 0xF000);

    public static byte[] BuildWithDistinctWidths(string characters)
        => Build(characters, 3, 1, distinctWidths: true);

    /// <summary>
    /// As <see cref="BuildSymbolic"/>, plus <c>vhea</c> and <c>vmtx</c> tables giving every glyph an
    /// advance height of <paramref name="advanceHeight"/> (units per em is 1000, so that is also its
    /// PDF glyph-space value). <paramref name="vmtxBytes"/> truncates the <c>vmtx</c> table to that
    /// many bytes when non-negative, for a program whose vertical metrics cannot be read.
    /// </summary>
    public static byte[] BuildSymbolicWithVerticalMetrics(string characters, int advanceHeight, int vmtxBytes = -1)
    {
        int numGlyphs = new SortedSet<char>(characters ?? string.Empty).Count + 1;

        var vmtx = new MemoryStream();
        for (int i = 0; i < numGlyphs; i++)
        {
            WriteUInt16(vmtx, advanceHeight);
            WriteInt16(vmtx, 0);                                   // topSideBearing
        }
        byte[] vmtxTable = vmtx.ToArray();
        if (vmtxBytes >= 0 && vmtxBytes < vmtxTable.Length) Array.Resize(ref vmtxTable, vmtxBytes);

        // vhea shares hhea's layout: numOfLongVerMetrics sits where numberOfHMetrics does.
        byte[] vhea = BuildHhea(numGlyphs);

        var extra = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            { "vhea", vhea },
            { "vmtx", vmtxTable },
        };
        // A truncated vmtx must not be the file's last table, or the file's own end catches it and a
        // reader's check against the table's length is never needed. Tables are placed in tag order.
        if (vmtxBytes >= 0) extra["zpad"] = new byte[64];

        return Build(characters, cmapPlatformId: 3, cmapEncodingId: 0, distinctWidths: true,
                     extraTables: extra, cmapCodeOffset: 0xF000);
    }

    /// <summary>
    /// As <see cref="Build(string)"/>, but with the cmap subtable declared under an arbitrary
    /// platform/encoding pair. <c>(3, 0)</c> is the Windows-Symbol table that marlett, symbol,
    /// wingdings and webdings ship — real, ordinary fonts whose glyphs are nonetheless
    /// invisible to a subsetter that only looks for <c>(3, 1)</c>.
    /// </summary>
    public static byte[] Build(string characters, int cmapPlatformId, int cmapEncodingId)
        => Build(characters, cmapPlatformId, cmapEncodingId, distinctWidths: false);

    /// <summary>
    /// The two above combined: an arbitrary cmap platform/encoding <b>and</b> the distinct advances
    /// that let a caller tell a real glyph lookup from the <c>.notdef</c> fallback.
    /// </summary>
    /// <remarks>
    /// Needed because the uniform-600 overload cannot witness a cmap defect at all: a reader that
    /// found the subtable and one that found nothing both produce 600 everywhere, so a test built
    /// on it passes whether or not the subtable was read. That is the trap
    /// <see cref="NotdefAdvance"/>'s own remarks name, and it is exactly the shape of question a
    /// <c>(3, 0)</c> or <c>(1, 0)</c> font asks.
    /// </remarks>
    public static byte[] BuildWithDistinctWidths(string characters, int cmapPlatformId, int cmapEncodingId)
        => Build(characters, cmapPlatformId, cmapEncodingId, distinctWidths: true);

    /// <summary>
    /// As <see cref="Build(string)"/>, but with a chosen <c>name</c>-table family (records 1 and
    /// 6) instead of the fixed "TestFont".
    ///
    /// <para>Exists because a font's family is the one thing about it that a <b>filename</b> need
    /// not agree with, and a resolver that matches filenames cannot tell the two apart. A test for
    /// the family-name pass has to be able to write a font called one thing into a file called
    /// another — with the default builder every fixture is "TestFont" and the distinction cannot
    /// be expressed at all.</para>
    /// </summary>
    public static byte[] BuildNamed(string characters, string familyName)
        => Build(characters, 3, 1, distinctWidths: false, familyName: familyName);

    /// <summary>
    /// A font whose <c>OS/2</c> <c>fsType</c> (offset 8) declares a chosen embedding licence.
    ///
    /// <para>Exists because the default builder leaves the field 0 — "installable" — so no fixture
    /// built on it can tell "the licence permits embedding" from "the licence was never read". Every
    /// other byte of the table is <see cref="BuildOs2"/>'s, so a test varying only this parameter is
    /// varying only the licence.</para>
    /// </summary>
    /// <param name="characters">The code points the font covers.</param>
    /// <param name="fsType">
    /// The raw 16-bit field. <c>0x0002</c> is Restricted License, <c>0x0200</c> bitmap-only,
    /// <c>0x0100</c> no-subsetting; bits 0–3 are one mutually-exclusive value, so <c>0x0006</c> is
    /// Preview &amp; Print rather than "restricted and preview".
    /// </param>
    /// <param name="familyName">The <c>name</c> table family, or null for the default.</param>
    public static byte[] BuildWithFsType(string characters, int fsType, string familyName = null)
    {
        byte[] os2 = BuildOs2();
        WriteUInt16At(os2, 8, fsType);
        return Build(characters, 3, 1, distinctWidths: false,
                     extraTables: new Dictionary<string, byte[]>(StringComparer.Ordinal)
                     {
                         { "OS/2", os2 },
                     },
                     familyName: familyName);
    }

    /// <summary>
    /// A font whose <c>name</c> table carries a <b>Macintosh localised</b> family name before the
    /// Windows English one — the record order a real localised font has, since the conventional
    /// sort is platform, then encoding, then language, and Macintosh is platform 1.
    ///
    /// <para>Exists because the default builder emits a single Windows/en-US record per name ID,
    /// so no fixture built on it can distinguish "picked the English name" from "picked the only
    /// name there was". The reader's record <i>preference</i> is unobservable without a table that
    /// offers a choice.</para>
    /// </summary>
    /// <param name="macLanguageId">
    /// Macintosh language ID for the first record. 0 is English; anything else is a localised name
    /// that must lose to the Windows English record.
    /// </param>
    public static byte[] BuildWithLocalisedNameTable(string characters, string macFamilyName,
        string windowsFamilyName, int macLanguageId = 11)
        => Build(characters, 3, 1, distinctWidths: false,
                 extraTables: new Dictionary<string, byte[]>(StringComparer.Ordinal)
                 {
                     { "name", BuildLocalisedName(macFamilyName, windowsFamilyName, macLanguageId) },
                 });

    /// <summary>
    /// A four-record <c>name</c> table: Macintosh family + PostScript name first, then the Windows
    /// English pair. Macintosh strings are single-byte (platform 1 is read as ASCII); Windows
    /// strings are UTF-16BE.
    /// </summary>
    private static byte[] BuildLocalisedName(string macFamily, string windowsFamily, int macLanguageId)
    {
        byte[] macFamilyBytes = Encoding.ASCII.GetBytes(macFamily);
        byte[] macPostBytes   = Encoding.ASCII.GetBytes(macFamily + "-PS");
        byte[] winFamilyBytes = Encoding.BigEndianUnicode.GetBytes(windowsFamily);
        byte[] winPostBytes   = Encoding.BigEndianUnicode.GetBytes(windowsFamily + "-PS");

        const int RecordCount = 4;
        var s = new MemoryStream();
        WriteUInt16(s, 0);                              // format
        WriteUInt16(s, RecordCount);                    // count
        WriteUInt16(s, 6 + 12 * RecordCount);           // stringOffset

        int offset = 0;
        void Record(int platformId, int encodingId, int languageId, int nameId, byte[] value)
        {
            WriteUInt16(s, platformId);
            WriteUInt16(s, encodingId);
            WriteUInt16(s, languageId);
            WriteUInt16(s, nameId);
            WriteUInt16(s, value.Length);
            WriteUInt16(s, offset);
            offset += value.Length;
        }

        // Macintosh Roman first, as a real table would sort it.
        Record(1, 0, macLanguageId, 1, macFamilyBytes);
        Record(1, 0, macLanguageId, 6, macPostBytes);
        Record(3, 1, 0x0409,        1, winFamilyBytes);
        Record(3, 1, 0x0409,        6, winPostBytes);

        s.Write(macFamilyBytes, 0, macFamilyBytes.Length);
        s.Write(macPostBytes,   0, macPostBytes.Length);
        s.Write(winFamilyBytes, 0, winFamilyBytes.Length);
        s.Write(winPostBytes,   0, winPostBytes.Length);
        return s.ToArray();
    }

    /// <summary>The code points of <paramref name="text"/>, surrogate pairs combined.</summary>
    private static IEnumerable<int> CodePointsOf(string text)
    {
        string s = text ?? string.Empty;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                yield return char.ConvertToUtf32(s[i], s[i + 1]);
                i++;
                continue;
            }
            yield return s[i];
        }
    }

    private static byte[] Build(string characters, int cmapPlatformId, int cmapEncodingId,
                                bool distinctWidths,
                                Dictionary<string, byte[]> extraTables = null,
                                string familyName = null,
                                int cmapCodeOffset = 0)
    {
        // Code points, not UTF-16 units: a surrogate pair is one mapped character, and taking the
        // halves separately would map two glyphs neither of which any cmap lookup can reach.
        var codePoints = new List<int>(new SortedSet<int>(CodePointsOf(characters)));
        int numGlyphs = codePoints.Count + 1;            // glyph 0 is .notdef

        // ---- glyf + loca (short format, so every offset must be even) ----
        var glyf = new MemoryStream();
        var loca = new List<int> { 0, 0 };               // .notdef occupies no bytes
        foreach (int _ in codePoints)
        {
            WriteSimpleGlyph(glyf);
            while (glyf.Length % 4 != 0) glyf.WriteByte(0);
            loca.Add((int)glyf.Length);
        }
        byte[] glyfBytes = glyf.ToArray();

        var locaBytes = new MemoryStream();
        foreach (int offset in loca) WriteUInt16(locaBytes, offset / 2);

        // ---- hmtx ----
        var hmtx = new MemoryStream();
        for (int i = 0; i < numGlyphs; i++)
        {
            int advance = !distinctWidths ? 600
                        : i == 0 ? NotdefAdvance
                        : MappedGlyphAdvance + 10 * (i - 1);
            WriteUInt16(hmtx, advance);
            WriteInt16(hmtx, 0);
        }

        var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            { "head", BuildHead() },
            { "hhea", BuildHhea(numGlyphs) },
            { "maxp", BuildMaxp(numGlyphs) },
            { "hmtx", hmtx.ToArray() },
            { "loca", locaBytes.ToArray() },
            { "glyf", glyfBytes },
            { "cmap", BuildCmap(codePoints, cmapPlatformId, cmapEncodingId, cmapCodeOffset) },
            { "name", BuildName(familyName) },
            { "post", BuildPost() },
            { "OS/2", BuildOs2() },
        };

        if (extraTables != null)
            foreach (var extra in extraTables)
                tables[extra.Key] = extra.Value;

        return Assemble(tables);
    }

    /// <summary>
    /// As <see cref="Build(string)"/>, but carrying the OpenType Layout and signature tables a PDF
    /// reader never consults — <c>GSUB</c>, <c>GPOS</c>, <c>GDEF</c> and <c>DSIG</c>.
    ///
    /// <para>Exists so a test can prove that font <b>pruning actually fires</b>. The plain builder
    /// emits only tables a reader needs, so <c>SfntTablePruner</c> correctly finds nothing to drop
    /// against it and returns null — which makes every "pruning happened" assertion built on it
    /// vacuously true while looking perfectly reasonable. A fixture that cannot reach the feature is
    /// the failure this repository keeps finding.</para>
    ///
    /// <para>The payloads are filler rather than valid layout tables, and that is sound for this
    /// purpose: pruning is a table-directory operation that never parses their contents, and the
    /// tables are dropped rather than rewritten. <paramref name="paddingBytes"/> makes them large
    /// enough that a size reduction is unambiguous.</para>
    /// </summary>
    public static byte[] BuildWithDroppableTables(string characters, int paddingBytes = 4096)
    {
        var filler = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string tag in new[] { "GSUB", "GPOS", "GDEF", "DSIG" })
        {
            var payload = new byte[paddingBytes];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0x7F);
            filler[tag] = payload;
        }
        return Build(characters, 3, 1, distinctWidths: false, extraTables: filler);
    }

    /// <summary>
    /// Wraps <paramref name="face"/> in a two-face <c>ttcf</c> collection, relocating each copy's
    /// table directory so the result is a <b>valid</b> collection.
    /// </summary>
    /// <remarks>
    /// The relocation is what makes this evidence. A <c>ttcf</c> signature with rubbish behind it
    /// would be refused by anything, so a refusal proved nothing; a reader that ignores the
    /// signature reads a valid collection as one table named by four bytes of a face offset, finds
    /// no <c>head</c> or <c>hhea</c>, and returns defaults for every metric.
    /// </remarks>
    public static byte[] BuildCollection(byte[] face)
    {
        int numTables = ReadUInt16(face, 4);
        int headerBytes = 12 + 4 * 2;                 // ttcf header + two u32 face offsets
        int secondFaceAt = headerBytes + face.Length;

        var ttc = new List<byte>(headerBytes + face.Length * 2);
        ttc.AddRange(Encoding.ASCII.GetBytes("ttcf"));
        ttc.AddRange(Be16(1)); ttc.AddRange(Be16(0));  // version 1.0
        ttc.AddRange(Be32(2));                         // numFonts
        ttc.AddRange(Be32((uint)headerBytes));
        ttc.AddRange(Be32((uint)secondFaceAt));
        ttc.AddRange(RelocateTables(face, numTables, headerBytes));
        ttc.AddRange(RelocateTables(face, numTables, secondFaceAt));
        return ttc.ToArray();
    }

    /// <summary>
    /// Overwrites the four tag bytes of the directory record naming <paramref name="existingTag"/>.
    /// </summary>
    /// <remarks>
    /// Used two ways: give a table a tag byte outside OpenType's 0x20–0x7E range, or give it a tag
    /// another table already has. Both are malformed, and both are handled differently by a reader
    /// that re-emits the font than by one that only reads metrics out of it.
    /// </remarks>
    public static byte[] WithTableTagBytes(byte[] font, string existingTag, byte[] fourBytes)
    {
        if (fourBytes == null || fourBytes.Length != 4)
            throw new ArgumentException("a tag is exactly four bytes", nameof(fourBytes));

        var copy = (byte[])font.Clone();
        int numTables = ReadUInt16(copy, 4);
        for (int i = 0; i < numTables; i++)
        {
            int at = 12 + i * 16;
            if (Encoding.ASCII.GetString(copy, at, 4) != existingTag) continue;
            Array.Copy(fourBytes, 0, copy, at, 4);
            return copy;
        }
        throw new ArgumentException($"the font has no \"{existingTag}\" table", nameof(existingTag));
    }

    private static byte[] RelocateTables(byte[] face, int numTables, int delta)
    {
        var copy = (byte[])face.Clone();
        for (int i = 0; i < numTables; i++)
        {
            int at = 12 + i * 16 + 8;
            Be32(ReadUInt32(copy, at) + (uint)delta).CopyTo(copy, at);
        }
        return copy;
    }

    private static byte[] Be16(int v) => new[] { (byte)(v >> 8), (byte)v };

    private static byte[] Be32(uint v) =>
        new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };

    /// <summary>Reads the Unicode → glyph-ID mapping out of a format-4 cmap.</summary>
    public static Dictionary<int, int> ReadCmap(byte[] font)
    {
        var result = new Dictionary<int, int>();
        Dictionary<string, (int offset, int length)> tables = ReadTableDirectory(font);
        if (!tables.TryGetValue("cmap", out (int offset, int length) cmap)) return result;

        int numSubtables = ReadUInt16(font, cmap.offset + 2);
        int subtableOffset = -1;
        for (int i = 0; i < numSubtables; i++)
        {
            int record = cmap.offset + 4 + i * 8;
            if (ReadUInt16(font, record) == 3 && ReadUInt16(font, record + 2) == 1)
            {
                subtableOffset = cmap.offset + (int)ReadUInt32(font, record + 4);
                break;
            }
        }
        if (subtableOffset < 0 || ReadUInt16(font, subtableOffset) != 4) return result;

        int segCount = ReadUInt16(font, subtableOffset + 6) / 2;
        int endBase   = subtableOffset + 14;
        int startBase = endBase + segCount * 2 + 2;
        int deltaBase = startBase + segCount * 2;
        int rangeBase = deltaBase + segCount * 2;

        for (int seg = 0; seg < segCount; seg++)
        {
            int end   = ReadUInt16(font, endBase + seg * 2);
            int start = ReadUInt16(font, startBase + seg * 2);
            int delta = (short)ReadUInt16(font, deltaBase + seg * 2);
            int range = ReadUInt16(font, rangeBase + seg * 2);
            if (start == 0xFFFF) continue;

            for (int c = start; c <= end && c != 0xFFFF; c++)
            {
                int gid;
                if (range == 0)
                {
                    gid = (c + delta) & 0xFFFF;
                }
                else
                {
                    int position = rangeBase + seg * 2 + range + (c - start) * 2;
                    if (position + 1 >= font.Length) continue;
                    int raw = ReadUInt16(font, position);
                    gid = raw == 0 ? 0 : (raw + delta) & 0xFFFF;
                }
                if (gid != 0) result[c] = gid;
            }
        }
        return result;
    }

    /// <summary>
    /// The glyph count the font's <c>maxp</c> declares, or -1 when it has none. A subset that
    /// collapsed to <c>.notdef</c> alone reports 1 — a well-formed font that draws nothing.
    /// </summary>
    public static int ReadNumGlyphs(byte[] font)
    {
        Dictionary<string, (int offset, int length)> tables = ReadTableDirectory(font);
        return tables.TryGetValue("maxp", out (int offset, int length) maxp)
            ? ReadUInt16(font, maxp.offset + 4)
            : -1;
    }

    internal static Dictionary<string, (int, int)> ReadTableDirectory(byte[] font)
    {
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        int numTables = ReadUInt16(font, 4);
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + i * 16;
            string tag = Encoding.ASCII.GetString(font, record, 4);
            tables[tag] = ((int)ReadUInt32(font, record + 8), (int)ReadUInt32(font, record + 12));
        }
        return tables;
    }

    // ---- individual tables ------------------------------------------

    /// <summary>A one-contour, one-point glyph — enough for the subsetter to copy real outline
    /// bytes rather than an empty range.</summary>
    private static void WriteSimpleGlyph(Stream s)
    {
        WriteInt16(s, 1);        // numberOfContours
        WriteInt16(s, 0);        // xMin
        WriteInt16(s, 0);        // yMin
        WriteInt16(s, 500);      // xMax
        WriteInt16(s, 700);      // yMax
        WriteUInt16(s, 0);       // endPtsOfContours[0] — a single point
        WriteUInt16(s, 0);       // instructionLength
        s.WriteByte(0x01);       // flags[0]: on-curve, 16-bit deltas
        WriteInt16(s, 100);      // xCoordinate
        WriteInt16(s, 100);      // yCoordinate
    }

    internal static byte[] BuildHead()
    {
        var s = new MemoryStream();
        WriteUInt32(s, 0x00010000);   // version
        WriteUInt32(s, 0x00010000);   // fontRevision
        WriteUInt32(s, 0);            // checkSumAdjustment
        WriteUInt32(s, 0x5F0F3CF5);   // magicNumber
        WriteUInt16(s, 0);            // flags
        WriteUInt16(s, 1000);         // unitsPerEm
        for (int i = 0; i < 16; i++) s.WriteByte(0);   // created + modified
        WriteInt16(s, 0);             // xMin
        WriteInt16(s, -200);          // yMin
        WriteInt16(s, 1000);          // xMax
        WriteInt16(s, 900);           // yMax
        WriteUInt16(s, 0);            // macStyle
        WriteUInt16(s, 8);            // lowestRecPPEM
        WriteInt16(s, 2);             // fontDirectionHint
        WriteInt16(s, 0);             // indexToLocFormat: short
        WriteInt16(s, 0);             // glyphDataFormat
        return s.ToArray();           // 54 bytes; indexToLocFormat is at offset 50
    }

    internal static byte[] BuildHhea(int numGlyphs)
    {
        var s = new MemoryStream();
        WriteUInt32(s, 0x00010000);   // version
        WriteInt16(s, 800);           // ascender
        WriteInt16(s, -200);          // descender
        WriteInt16(s, 0);             // lineGap
        WriteUInt16(s, 600);          // advanceWidthMax
        WriteInt16(s, 0);             // minLeftSideBearing
        WriteInt16(s, 0);             // minRightSideBearing
        WriteInt16(s, 600);           // xMaxExtent
        WriteInt16(s, 1);             // caretSlopeRise
        WriteInt16(s, 0);             // caretSlopeRun
        WriteInt16(s, 0);             // caretOffset
        for (int i = 0; i < 4; i++) WriteInt16(s, 0);   // reserved
        WriteInt16(s, 0);             // metricDataFormat
        WriteUInt16(s, numGlyphs);    // numberOfHMetrics — offset 34
        WriteUInt32(s, 0);            // trailing zero padding (see note below)
        return s.ToArray();
    }

    internal static byte[] BuildMaxp(int numGlyphs)
    {
        var s = new MemoryStream();
        WriteUInt32(s, 0x00010000);   // version 1.0
        WriteUInt16(s, numGlyphs);    // numGlyphs — offset 4
        for (int i = 0; i < 13; i++) WriteUInt16(s, 0);
        return s.ToArray();           // 32 bytes
    }

    /// <summary>
    /// A one-subtable <c>cmap</c>: <b>format 4</b> when every code point is in the BMP, and
    /// <b>format 12</b> as soon as one is not.
    /// </summary>
    /// <remarks>
    /// <para><b>Format 4 cannot carry a supplementary code point at all</b> — its segments are
    /// 16-bit — so a font built for astral text through it maps nothing, and a test written on one
    /// fails for a reason that has nothing to do with what it is testing. That is why Builder's
    /// astral right-to-left guard sat unreachable in GAPS.md §5: not because the library could not
    /// draw the text (<c>TrueTypeFontProgram</c> has read format 12 all along), but because no
    /// fixture could construct the input.</para>
    ///
    /// <para><b>A BMP font's bytes are unchanged</b>, deliberately: the format-4 branch is the
    /// original code, reached whenever the old overload would have been, so every existing fixture
    /// produces the file it produced before.</para>
    /// </remarks>
    internal static byte[] BuildCmap(List<int> codePoints, int platformId, int encodingId, int codeOffset = 0)
    {
        foreach (int cp in codePoints)
            if (cp > 0xFFFF)
                return BuildCmap12(codePoints, platformId, encodingId, codeOffset);

        var chars = new List<char>(codePoints.Count);
        foreach (int cp in codePoints) chars.Add((char)cp);

        int segCount = chars.Count + 1;               // + the mandatory 0xFFFF terminator
        int subtableLength = 16 + 8 * segCount;

        var s = new MemoryStream();
        WriteUInt16(s, 0);            // cmap version
        WriteUInt16(s, 1);            // one subtable
        WriteUInt16(s, platformId);   // platformID (3 = Windows)
        WriteUInt16(s, encodingId);   // encodingID (1 = Unicode BMP, 0 = Symbol)
        WriteUInt32(s, 12);           // offset to the subtable

        WriteUInt16(s, 4);            // format
        WriteUInt16(s, subtableLength);
        WriteUInt16(s, 0);            // language
        WriteUInt16(s, segCount * 2);
        WriteUInt16(s, 2);            // searchRange
        WriteUInt16(s, 0);            // entrySelector
        WriteUInt16(s, segCount * 2 - 2);  // rangeShift

        foreach (char c in chars) WriteUInt16(s, c | codeOffset);   // endCode
        WriteUInt16(s, 0xFFFF);
        WriteUInt16(s, 0);                             // reservedPad
        foreach (char c in chars) WriteUInt16(s, c | codeOffset);   // startCode
        WriteUInt16(s, 0xFFFF);

        // One segment per character, so idDelta alone resolves the glyph and no glyphIdArray
        // is needed. Glyph IDs start at 1 because glyph 0 is .notdef.
        for (int i = 0; i < chars.Count; i++)
            WriteUInt16(s, ((i + 1) - (chars[i] | codeOffset)) & 0xFFFF);
        WriteUInt16(s, 1);                             // terminator delta

        for (int i = 0; i < segCount; i++) WriteUInt16(s, 0);   // idRangeOffset
        return s.ToArray();
    }

    /// <summary>
    /// A format-12 (segmented coverage) subtable: one group per code point, glyph ids from 1.
    /// </summary>
    /// <remarks>
    /// The groups are 32-bit, which is the whole reason this format exists — and they must be in
    /// increasing order of start code, which the caller's sorted set already guarantees.
    /// </remarks>
    private static byte[] BuildCmap12(List<int> codePoints, int platformId, int encodingId, int codeOffset)
    {
        int groups = codePoints.Count;
        int subtableLength = 16 + 12 * groups;

        var s = new MemoryStream();
        WriteUInt16(s, 0);            // cmap version
        WriteUInt16(s, 1);            // one subtable
        WriteUInt16(s, platformId);
        WriteUInt16(s, encodingId);
        WriteUInt32(s, 12);           // offset to the subtable

        WriteUInt16(s, 12);           // format
        WriteUInt16(s, 0);            // reserved
        WriteUInt32(s, (uint)subtableLength);
        WriteUInt32(s, 0);            // language
        WriteUInt32(s, (uint)groups);

        // One group per code point, so startGlyphID alone resolves it. Glyph 0 is .notdef, so the
        // first mapped code point is glyph 1 — the same numbering the format-4 branch uses.
        for (int i = 0; i < groups; i++)
        {
            uint code = (uint)(codePoints[i] | codeOffset);
            WriteUInt32(s, code);          // startCharCode
            WriteUInt32(s, code);          // endCharCode
            WriteUInt32(s, (uint)(i + 1)); // startGlyphID
        }

        return s.ToArray();
    }

    internal static byte[] BuildName() => BuildName(null);

    internal static byte[] BuildName(string familyName)
    {
        byte[] value = Encoding.BigEndianUnicode.GetBytes(
            string.IsNullOrEmpty(familyName) ? "TestFont" : familyName);
        var s = new MemoryStream();
        WriteUInt16(s, 0);                    // format
        WriteUInt16(s, 2);                    // count
        WriteUInt16(s, 6 + 12 * 2);           // stringOffset

        foreach (int nameId in new[] { 1, 6 })
        {
            WriteUInt16(s, 3);                // platformID: Windows
            WriteUInt16(s, 1);                // encodingID: Unicode BMP
            WriteUInt16(s, 0x0409);           // languageID: en-US
            WriteUInt16(s, nameId);
            WriteUInt16(s, value.Length);
            WriteUInt16(s, nameId == 1 ? 0 : value.Length);
        }

        s.Write(value, 0, value.Length);
        s.Write(value, 0, value.Length);
        return s.ToArray();
    }

    internal static byte[] BuildPost()
    {
        var s = new MemoryStream();
        WriteUInt32(s, 0x00030000);   // version 3.0 — no glyph names
        WriteUInt32(s, 0);            // italicAngle
        WriteInt16(s, -100);          // underlinePosition
        WriteInt16(s, 50);            // underlineThickness
        WriteUInt32(s, 0);            // isFixedPitch
        for (int i = 0; i < 4; i++) WriteUInt32(s, 0);
        return s.ToArray();           // 32 bytes
    }

    internal static byte[] BuildOs2()
    {
        var table = new byte[86];     // OS/2 version 1
        WriteUInt16At(table, 0, 1);       // version
        WriteUInt16At(table, 4, 400);     // usWeightClass: regular
        WriteUInt16At(table, 6, 5);       // usWidthClass: medium
        WriteUInt16At(table, 62, 0x0040); // fsSelection: REGULAR
        return table;
    }

    internal static byte[] Assemble(Dictionary<string, byte[]> tables)
        => Assemble(tables, 0x00010000);

    /// <summary>
    /// As <see cref="Assemble(Dictionary{string, byte[]})"/>, but for a caller that needs a
    /// different sfnt version — <c>0x4F54544F</c> (<c>'OTTO'</c>) for OpenType/CFF, which
    /// <see cref="MinimalOpenTypeCff"/> builds.
    /// </summary>
    internal static byte[] Assemble(Dictionary<string, byte[]> tables, long sfntVersion)
    {
        List<string> tags = tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
        int numTables = tags.Count;

        var output = new MemoryStream();
        WriteUInt32(output, sfntVersion);
        WriteUInt16(output, numTables);
        WriteUInt16(output, 16);                // searchRange (not validated by anything here)
        WriteUInt16(output, 0);                 // entrySelector
        WriteUInt16(output, numTables * 16 - 16);

        int dataOffset = 12 + numTables * 16;
        var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string tag in tags)
        {
            offsets[tag] = dataOffset;
            dataOffset += (tables[tag].Length + 3) & ~3;
        }

        foreach (string tag in tags)
        {
            output.Write(Encoding.ASCII.GetBytes(tag), 0, 4);
            WriteUInt32(output, 0);             // checksum: not verified by this library
            WriteUInt32(output, offsets[tag]);
            WriteUInt32(output, tables[tag].Length);
        }

        foreach (string tag in tags)
        {
            byte[] data = tables[tag];
            output.Write(data, 0, data.Length);
            for (int pad = data.Length; pad % 4 != 0; pad++) output.WriteByte(0);
        }

        return output.ToArray();
    }

    // ---- big-endian primitives --------------------------------------

    internal static void WriteUInt16(Stream s, int value)
    {
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    internal static void WriteInt16(Stream s, int value) => WriteUInt16(s, value & 0xFFFF);

    internal static void WriteUInt32(Stream s, long value)
    {
        s.WriteByte((byte)(value >> 24));
        s.WriteByte((byte)(value >> 16));
        s.WriteByte((byte)(value >> 8));
        s.WriteByte((byte)value);
    }

    /// <summary>
    /// Internal rather than private since <see cref="MinimalOpenTypeCff"/> stamps an <c>fsType</c>
    /// into the shared <c>OS/2</c> table this class builds.
    /// </summary>
    internal static void WriteUInt16At(byte[] table, int position, int value)
    {
        table[position]     = (byte)(value >> 8);
        table[position + 1] = (byte)value;
    }

    internal static int ReadUInt16(byte[] data, int offset)
        => (data[offset] << 8) | data[offset + 1];

    internal static uint ReadUInt32(byte[] data, int offset)
        => ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16)
         | ((uint)data[offset + 2] << 8) | data[offset + 3];
}
