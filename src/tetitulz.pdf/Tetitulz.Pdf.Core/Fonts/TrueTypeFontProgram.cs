// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Parses a raw TrueType/OpenType font program for the purpose of EMBEDDING it
    /// into a written PDF as a composite (Type0 / CIDFontType2) font. Exposes the
    /// full Unicode→glyph-id map (cmap format 4 and 12), per-glyph advance widths
    /// (hmtx), units-per-em, and the FontDescriptor metrics — everything needed by
    /// <see cref="Tetitulz.Pdf.Core.Writing.Type0FontBuilder"/>.
    ///
    /// This is the WRITE-side counterpart to the read-side font parsers in this
    /// folder; it is pure-managed (no SkiaSharp) so it can be used from Core-only
    /// consumers such as the DocxToPdf feature.
    /// </summary>
    public sealed class TrueTypeFontProgram
    {
        /// <summary>Raw font bytes (used verbatim for the /FontFile2 stream).</summary>
        public byte[] FontData { get; private set; }
        /// <summary>
        /// What <see cref="PostScriptName"/> holds when the font's <c>name</c> table supplied no
        /// usable record — a placeholder, not a name the font chose.
        /// </summary>
        /// <remarks>
        /// Named rather than left as a literal because a caller with its own fallback has to be
        /// able to tell "the font is called this" from "the font named itself nothing", and a
        /// second copy of the string in another assembly is how those two answers drift apart.
        /// </remarks>
        public const string UnnamedPostScriptName = "EmbeddedFont";

        public string PostScriptName { get; private set; } = UnnamedPostScriptName;
        public string FamilyName { get; private set; }
        public int UnitsPerEm { get; private set; } = 1000;
        public int Ascent { get; private set; } = 800;
        public int Descent { get; private set; } = -200;
        public int CapHeight { get; private set; } = 700;
        public int StemV { get; private set; } = 80;
        public int Flags { get; private set; } = 32; // Nonsymbolic
        public double ItalicAngle { get; private set; }
        public int BBoxXMin { get; private set; }
        public int BBoxYMin { get; private set; }
        public int BBoxXMax { get; private set; } = 1000;
        public int BBoxYMax { get; private set; } = 1000;
        public bool HasCff { get; private set; }

        /// <summary>
        /// What the font's <c>OS/2</c> <c>fsType</c> permits.
        /// <see cref="FontEmbeddingLicense.Undeclared"/> when the program carries no <c>OS/2</c>
        /// table — a bare CFF or Type 1 program says nothing about embedding.
        /// </summary>
        public FontEmbeddingLicense EmbeddingLicense { get; private set; }
        public int GlyphCount { get; private set; }

        private readonly Dictionary<int, int> _cmap = new Dictionary<int, int>();
        private int[] _glyphWidths = Array.Empty<int>();

        /// <summary><c>vmtx</c> advance heights by glyph id, or null when the program has no usable
        /// <c>vhea</c>/<c>vmtx</c> pair.</summary>
        private int[] _glyphHeights;

        // Upper bound on the number of code-point→glyph entries we will ingest from a
        // (crafted) cmap subtable.  The BMP is 0x10000 code points and the full Unicode
        // range is ~0x110000; a legitimate font never maps more than that, but a malicious
        // format-4 subtable with ~32767 segments can claim ~2 billion in-bounds inserts.
        // Cap total inserts to stop the hang + memory blow-up (mirrors the MaxRangeLength
        // clamp used in CMapParser).
        private const int MaxCmapEntries = 0x20000;

        private TrueTypeFontProgram() { }

        /// <summary>Maps a Unicode code point to its glyph id. Returns false if unmapped.</summary>
        public bool TryGetGlyph(int codepoint, out ushort glyphId)
        {
            if (_cmap.TryGetValue(codepoint, out int gid) && gid > 0 && gid <= ushort.MaxValue)
            {
                glyphId = (ushort)gid;
                return true;
            }
            glyphId = 0;
            return false;
        }

        /// <summary>True when the font has a glyph for the code point.</summary>
        public bool Covers(int codepoint) => _cmap.TryGetValue(codepoint, out int gid) && gid > 0;

        /// <summary>Every code point the <c>cmap</c> maps, with its glyph — for reading the table backwards.</summary>
        internal IReadOnlyDictionary<int, int> CodePointGlyphs => _cmap;

        /// <summary>
        /// True when the font can draw the code point in composed text (<c>Core.Text.TextRunComposer</c>):
        /// it maps it, or it is an Arabic presentation form the composer draws as its base letter
        /// through the font's own <c>GSUB</c> joining feature. The question a producer asks of the
        /// shaped run's code points (<c>TextRunComposer.CodePointsToCover</c>).
        /// </summary>
        /// <remarks>
        /// A font may map the letters and leave the presentation forms out of its <c>cmap</c> — web
        /// fonts and subsets often do — and reach each form through <c>init</c>, <c>medi</c>,
        /// <c>fina</c> or <c>isol</c>. <see cref="Covers"/> refused such a font for Arabic altogether.
        /// </remarks>
        public bool CoversShaped(int codepoint) => Covers(codepoint) || TryGetJoiningForm(codepoint, out _, out _);

        /// <summary>
        /// For an Arabic presentation form the font does not map, its base letter's glyph and the
        /// joining feature (<c>isol</c>, <c>fina</c>, <c>init</c>, <c>medi</c>) the font's <c>GSUB</c>
        /// reaches the form through — when it maps the letter and has that feature for Arabic. The
        /// isolated form is the letter's own glyph, so a font with no <c>isol</c> draws it as the letter.
        /// </summary>
        internal bool TryGetJoiningForm(int codepoint, out ushort baseGlyph, out string feature)
        {
            baseGlyph = 0;
            if (!Core.Text.OpenTypeShaper.TryGetJoiningFeature(codepoint, out int baseCodePoint, out feature)) return false;
            if (!TryGetGlyph(baseCodePoint, out baseGlyph)) return false;
            return feature == "isol" || Substitution?.HasJoiningFeature("arab", feature) == true;
        }

        /// <summary>
        /// True when the program's <c>cmap</c> table declares a subtable for this platform/encoding
        /// pair — whatever its format, and whether or not this parser reads that format. The
        /// question PDF/UA-2 §8.4.5.7 and PDF/A ask of an embedded TrueType program: (3,1) Microsoft
        /// Unicode, (1,0) Macintosh Roman, (3,0) Microsoft Symbol.
        /// </summary>
        public bool HasCmapSubtable(int platformId, int encodingId)
            => _cmapSubtables.Contains((platformId, encodingId));

        /// <summary>
        /// How many distinct platform/encoding pairs the program's <c>cmap</c> table declares — the
        /// count PDF/A's and PDF/UA-1's TrueType rules ask about ("exactly one entry", "more than one").
        /// </summary>
        public int CmapSubtableCount => _cmapSubtables.Count;

        private readonly HashSet<(int PlatformId, int EncodingId)> _cmapSubtables =
            new HashSet<(int PlatformId, int EncodingId)>();

        /// <summary>
        /// True when the program carries vertical metrics — a <c>vhea</c> table and a <c>vmtx</c> table
        /// long enough for the metrics it declares.
        /// </summary>
        internal bool HasVerticalMetrics => _glyphHeights != null;

        /// <summary>
        /// The <c>vmtx</c> advance height of a glyph in 1000-unit PDF space; false when the program has
        /// no vertical metrics or the glyph id is outside them. Unlike <see cref="GetAdvance"/> there
        /// is no fallback glyph: a caller comparing metrics must not be handed another glyph's.
        /// </summary>
        internal bool TryGetVerticalAdvance(int glyphId, out int advance)
        {
            advance = 0;
            if (_glyphHeights == null || glyphId < 0 || glyphId >= _glyphHeights.Length) return false;
            advance = Scale(_glyphHeights[glyphId]);
            return true;
        }

        /// <summary>
        /// The font's GPOS mark attachment, or <c>null</c> when it has none — read on first use, once.
        /// </summary>
        internal OpenTypeMarkPositioning MarkPositioning
        {
            get
            {
                if (!_markPositioningRead)
                {
                    _markPositioning = OpenTypeMarkPositioning.TryLoad(FontData, UnitsPerEm);
                    _markPositioningRead = true;
                }
                return _markPositioning;
            }
        }

        private OpenTypeMarkPositioning _markPositioning;
        private bool _markPositioningRead;

        /// <summary>
        /// The font's <c>GSUB</c> <c>rclt</c> substitutions (<see cref="OpenTypeMarkPositioning.TryLoadSubstitution"/>),
        /// or <c>null</c> when it has none — read on first use, once.
        /// </summary>
        internal OpenTypeMarkPositioning Substitution
        {
            get
            {
                if (!_substitutionRead)
                {
                    _substitution = OpenTypeMarkPositioning.TryLoadSubstitution(FontData);
                    _substitutionRead = true;
                }
                return _substitution;
            }
        }

        private OpenTypeMarkPositioning _substitution;
        private bool _substitutionRead;

        /// <summary>Advance width of a glyph in 1000-unit PDF space.</summary>
        public int GetAdvance(ushort glyphId)
        {
            if (glyphId < _glyphWidths.Length)
                return Scale(_glyphWidths[glyphId]);
            return _glyphWidths.Length > 0 ? Scale(_glyphWidths[0]) : 500;
        }

        /// <summary>
        /// Converts a value in this font's design units to the 1000-units-per-em glyph space a
        /// PDF font dictionary uses. Fonts whose <c>head</c> table is missing or declares
        /// <c>unitsPerEm == 0</c> are passed through unscaled rather than divided by zero.
        /// </summary>
        private int Scale(int fontUnits) =>
            UnitsPerEm > 0
                ? SaturatingCast.ToInt(Math.Round(fontUnits * PdfUnits.GlyphSpaceUnitsPerEm / UnitsPerEm))
                : fontUnits;

        // ----------------------------------------------------------------
        //  Parser
        // ----------------------------------------------------------------

        /// <summary>
        /// Parses font bytes; returns null if not a usable TrueType/OpenType font.
        ///
        /// <para>A <b>WOFF 1.0</b> or <b>WOFF 2.0</b> container is unwrapped first (see
        /// <see cref="WoffDecoder"/> and <see cref="Woff2Decoder"/>),
        /// so <see cref="FontData"/> — the bytes the caller will write as <c>/FontFile2</c> —
        /// is always a plain sfnt. Doing it here rather than at each call site is deliberate:
        /// this method is the one gate every font consumer in the tree already passes through,
        /// so @font-face, a caller's font provider, the Builder's file scan and the accessibility
        /// remediator all gain WOFF support from one four-byte signature check, and a font that
        /// is not a WOFF takes a bit-identical path to before.</para>
        /// </summary>
        public static TrueTypeFontProgram Parse(byte[] data)
        {
            _lastParseError = null;
            try
            {
                if (data == null || data.Length < 12) return Refuse("fewer than 12 bytes: no sfnt header");

                // WOFF 2.0: Brotli-compressed and TRANSFORMED, so unwrapping it is a
                // reconstruction rather than an inflate (Woff2Decoder). Unwrapped here, at the one
                // gate every font consumer already passes through, for the same reason WOFF 1.0 is:
                // @font-face, a caller's font provider, the Builder's file scan and the
                // accessibility remediator all gain it from one signature check.
                //
                // A container that will not unwrap still returns null rather than being parsed as a
                // nonsense sfnt: the WOFF2 header happens to place a plausible u16 where sfnt keeps
                // numTables, so the bytes DO parse — into a program with no cmap and default
                // metrics, which reaches the caller as "this font does not cover your text" instead
                // of "I could not read this container".
                if (Woff2Decoder.IsWoff2(data))
                {
                    data = Woff2Decoder.Decode(data);
                    if (data == null || data.Length < 12) return Refuse(Woff2Decoder.LastError ?? "the WOFF 2.0 container unwrapped to fewer than 12 bytes");
                }

                if (WoffDecoder.IsWoff1(data))
                {
                    data = WoffDecoder.Decode(data);
                    if (data == null || data.Length < 12) return Refuse(WoffDecoder.LastError ?? "the WOFF 1.0 container unwrapped to fewer than 12 bytes");
                }

                var m = new TrueTypeFontProgram { FontData = data };
                var r = new Reader(data);

                uint sfVersion = r.U32();
                if (sfVersion == 0x74746366) return Refuse("a font collection ('ttcf'), which is not read");
                int numTables = r.U16();
                r.Skip(6);

                var tables = new Dictionary<string, (uint off, uint len)>(StringComparer.Ordinal);
                for (int i = 0; i < numTables; i++)
                {
                    string tag = r.Tag();
                    r.Skip(4);
                    uint off = r.U32();
                    uint len = r.U32();
                    tables[tag] = (off, len);
                }
                m.HasCff = tables.ContainsKey("CFF ");

                int upm = 1000;
                if (tables.TryGetValue("head", out var head))
                {
                    r.Seek(head.off);
                    r.Skip(18);
                    upm = r.U16();
                    m.UnitsPerEm = upm > 0 ? upm : 1000;
                    r.Skip(16);
                    short xMin = r.I16(), yMin = r.I16(), xMax = r.I16(), yMax = r.I16();
                    m.BBoxXMin = m.Scale(xMin); m.BBoxYMin = m.Scale(yMin);
                    m.BBoxXMax = m.Scale(xMax); m.BBoxYMax = m.Scale(yMax);
                }

                int numGlyphs = 0;
                if (tables.TryGetValue("maxp", out var maxp))
                {
                    r.Seek(maxp.off + 4);
                    numGlyphs = r.U16();
                }
                m.GlyphCount = numGlyphs;

                int numberOfHMetrics = 0;
                if (tables.TryGetValue("hhea", out var hhea))
                {
                    r.Seek(hhea.off);
                    r.Skip(4);
                    short ascender = r.I16(), descender = r.I16();
                    // hhea layout after ascender/descender (offset 8): lineGap, advanceWidthMax,
                    // minLeftSideBearing, minRightSideBearing, xMaxExtent, caretSlopeRise,
                    // caretSlopeRun, caretOffset, 4x reserved, metricDataFormat — 13 int16 fields,
                    // 26 bytes — then numberOfHMetrics at offset 34. Skipping 30 would land 4 bytes
                    // past it (into whatever follows hhea, zero-padding in a well-formed font) and
                    // read it as 0, silently emptying _glyphWidths so GetAdvance returns its flat
                    // fallback for every glyph (SimpleFontWidthProbeTests).
                    r.Skip(26);
                    numberOfHMetrics = r.U16();
                    m.Ascent = m.Scale(ascender);
                    m.Descent = m.Scale(descender);
                    m.CapHeight = SaturatingCast.ToInt(m.Ascent * 0.72);
                }

                if (tables.TryGetValue("hmtx", out var hmtx) && numberOfHMetrics > 0)
                {
                    int n = Math.Max(numGlyphs, numberOfHMetrics);
                    var widths = new int[n];
                    r.Seek(hmtx.off);
                    int lastAW = 0;
                    for (int i = 0; i < numberOfHMetrics; i++)
                    {
                        lastAW = r.U16();
                        r.Skip(2);
                        if (i < n) widths[i] = lastAW;
                    }
                    for (int i = numberOfHMetrics; i < n; i++) widths[i] = lastAW;
                    m._glyphWidths = widths;
                }

                // vhea has hhea's layout, numOfLongVerMetrics at offset 34; vmtx pairs an advance height
                // with a top side bearing. Both are bounds-checked here rather than left to the reader:
                // a truncated vertical table must cost the vertical metrics, not the whole program.
                if (tables.TryGetValue("vhea", out var vhea) && vhea.len >= 36 && (long)vhea.off + 36 <= data.Length
                    && tables.TryGetValue("vmtx", out var vmtx))
                {
                    r.Seek(vhea.off + 34);
                    int numberOfLongVerMetrics = r.U16();
                    if (numberOfLongVerMetrics > 0
                        && vmtx.len >= (uint)numberOfLongVerMetrics * 4
                        && (long)vmtx.off + (long)numberOfLongVerMetrics * 4 <= data.Length)
                    {
                        int n = Math.Max(numGlyphs, numberOfLongVerMetrics);
                        var heights = new int[n];
                        r.Seek(vmtx.off);
                        int lastAH = 0;
                        for (int i = 0; i < numberOfLongVerMetrics; i++)
                        {
                            lastAH = r.U16();
                            r.Skip(2);
                            heights[i] = lastAH;
                        }
                        for (int i = numberOfLongVerMetrics; i < n; i++) heights[i] = lastAH;
                        m._glyphHeights = heights;
                    }
                }

                if (tables.TryGetValue("OS/2", out var os2))
                {
                    r.Seek(os2.off);
                    r.Skip(4); // version + xAvgCharWidth
                    int weight = r.U16();
                    int stem = SaturatingCast.ToInt(10.0 + 220.0 * (weight - 400) / 600.0);
                    m.StemV = stem < 10 ? 10 : (stem > 250 ? 250 : stem);

                    // fsType — what the foundry permits. Read through the shared field reader
                    // rather than seeked here, so the offset lives in exactly one place; see
                    // FontEmbeddingLicense for why it is reported rather than enforced for a
                    // caller-supplied program.
                    m.EmbeddingLicense = FontEmbeddingLicense.FromOs2Table(data, os2.off);

                    r.Seek(os2.off + 62);
                    int fsSelection = r.U16();
                    if ((fsSelection & 0x20) != 0) m.Flags |= 0x40000; // ForceBold
                    if ((fsSelection & 0x01) != 0) m.Flags |= 0x40;    // Italic
                }

                if (tables.TryGetValue("post", out var post))
                {
                    r.Seek(post.off + 4);
                    m.ItalicAngle = r.I32() / 65536.0;
                    r.Skip(4);
                    if (r.I32() != 0) m.Flags |= 1; // FixedPitch
                }

                if (tables.TryGetValue("name", out var name))
                    m.ParseName(r, name.off);

                if (tables.TryGetValue("cmap", out var cmap))
                    m.ParseCmap(r, cmap.off);

                // Usable for Identity encoding even when the cmap is empty (a CIDFont subset
                // legitimately ships none).
                return m;
            }
            catch (Exception ex)
            {
                // A null program is the contract; what the reader threw — a table's bounds, a truncated
                // directory — is said, rather than read as "not a font".
                return Refuse("the sfnt could not be read: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        [ThreadStatic] private static string _lastParseError;

        /// <summary>
        /// Why the last <see cref="Parse"/> on this thread returned null — too short, a WOFF container that would not
        /// unwrap (its decoder's reason), a collection, or what the reader threw — or null when it returned a program.
        /// Reset at the start of every call.
        /// </summary>
        internal static string LastParseError => _lastParseError;

        private static TrueTypeFontProgram Refuse(string why)
        {
            _lastParseError = why;
            return null;
        }

        private void ParseName(Reader r, uint tableOffset)
        {
            r.Seek(tableOffset);
            r.Skip(2);
            int count = r.U16();
            int stringOffset = r.U16();
            uint stringsBase = tableOffset + (uint)stringOffset;

            // Best-scoring record seen so far for each name ID, rather than first-or-last wins.
            int familyScore = -1, postScriptScore = -1;

            for (int i = 0; i < count; i++)
            {
                int platformId = r.U16(), encodingId = r.U16(), languageId = r.U16();
                int nameId = r.U16(), length = r.U16(), offset = r.U16();
                if (nameId != 6 && nameId != 1) continue;       // PostScript name / Family
                if (platformId != 3 && platformId != 1) continue;

                int score = NameRecordScore(platformId, encodingId, languageId);
                if (score < 0) continue;
                if (nameId == 1 && score <= familyScore) continue;
                if (nameId == 6 && score <= postScriptScore) continue;

                uint saved = r.Pos;
                r.Seek(stringsBase + (uint)offset);
                byte[] bytes = r.Bytes(length);
                r.Seek(saved);

                string s = platformId == 3
                    ? Encoding.BigEndianUnicode.GetString(bytes)
                    : Encoding.ASCII.GetString(bytes);
                s = s.Replace("\0", "").Trim();
                if (string.IsNullOrEmpty(s)) continue;

                if (nameId == 1) { FamilyName = s; familyScore = score; }
                else             { PostScriptName = s.Replace(" ", "-"); postScriptScore = score; }
            }
        }

        /// <summary>
        /// How much this <c>name</c> record is preferred for a given name ID — higher wins,
        /// negative means unusable.
        /// </summary>
        /// <remarks>
        /// <para><b>The language ID decides</b>, not table order — which would pick the <i>first</i>
        /// matching record for the family name and the <i>last</i> for the PostScript name. A
        /// <c>name</c> table carries one record per language, and the conventional sort is by
        /// platform then encoding then language — which puts <b>Macintosh</b> records first. A font
        /// whose Mac records are localised and whose English name lives on the Windows platform
        /// therefore yielded a localised <see cref="FamilyName"/>, and for a CJK font it yielded a
        /// non-Latin string decoded as ASCII, i.e. mojibake.</para>
        /// <para>That name is not cosmetic: it is what font substitution matches on
        /// (<c>FontCache</c>, <c>FontFileLocator</c>, <c>IsFamilyMatch</c>), so a localised or
        /// mangled value silently resolves to the wrong face or to none.</para>
        /// <para>The ordering is the usual one — Windows/Unicode English, then Windows/Unicode in
        /// any language, then Macintosh English, then any remaining Macintosh Roman record. Only
        /// the <b>relative</b> order matters; the numbers are spread so a tier can be inserted.</para>
        /// </remarks>
        private static int NameRecordScore(int platformId, int encodingId, int languageId)
        {
            const int WindowsPlatform = 3, MacintoshPlatform = 1;
            const int WindowsEnglishUs = 0x0409, MacintoshEnglish = 0;

            if (platformId == WindowsPlatform)
            {
                // Encodings 1 (BMP) and 10 (full repertoire) are the UTF-16BE ones this method
                // decodes. Symbol (0) is also UTF-16BE text in practice and is accepted below it.
                bool unicode = encodingId == 1 || encodingId == 10;
                if (unicode && languageId == WindowsEnglishUs) return 40;
                if (unicode) return 30;
                return encodingId == 0 ? 10 : -1;
            }

            if (platformId == MacintoshPlatform)
            {
                // Only Roman (0) is safe to read as ASCII, which is what the caller does. Other
                // Macintosh encodings are legacy CJK code pages that would decode to mojibake.
                if (encodingId != 0) return -1;
                return languageId == MacintoshEnglish ? 20 : 5;
            }

            return -1;
        }

        private void ParseCmap(Reader r, uint tableOffset)
        {
            r.Seek(tableOffset);
            r.Skip(2);
            int numSubtables = r.U16();

            uint best4 = 0, best12 = 0, symbolOrMac = 0;
            bool symbolIsThreeZero = false;
            for (int i = 0; i < numSubtables; i++)
            {
                int platformId = r.U16(), encodingId = r.U16();
                uint subOff = tableOffset + r.U32();
                _cmapSubtables.Add((platformId, encodingId));
                bool unicodeBmp = (platformId == 3 && encodingId == 1)
                                  || (platformId == 0 && (encodingId == 3 || encodingId == 4 || encodingId == 6));
                bool unicodeFull = (platformId == 3 && encodingId == 10)
                                   || (platformId == 0 && (encodingId == 4 || encodingId == 6));
                if (unicodeFull && best12 == 0) best12 = subOff;
                else if (unicodeBmp && best4 == 0) best4 = subOff;
                // (3,0) Microsoft Symbol and (1,0) Macintosh Roman. Wingdings, Symbol and most
                // icon webfonts ship ONLY one of these, so a font with real glyphs parsed with an
                // empty cmap: TextRunComposer reported every code point as missing and drew
                // nothing, and SystemFontResolver.Covers rejected the face — while the font in
                // fact had the glyph. (3,0) wins over (1,0) when both are present.
                else if (platformId == 3 && encodingId == 0)
                {
                    if (!symbolIsThreeZero || symbolOrMac == 0) { symbolOrMac = subOff; symbolIsThreeZero = true; }
                }
                else if (platformId == 1 && encodingId == 0 && symbolOrMac == 0)
                {
                    symbolOrMac = subOff;
                }
            }

            // Format 4 first (covers BMP — Hebrew/Arabic live here), then merge any
            // format-12 supplementary mappings on top.
            if (best4 != 0) ParseCmapFormat(r, best4);
            if (best12 != 0) ParseCmapFormat(r, best12);

            // Lowest-priority tier: a symbol / Mac subtable is consulted only where the Unicode
            // ones left a code point unmapped, so a well-formed Unicode font is byte-identical to
            // before. ParseCmap4/12/6/0 never overwrite an existing entry (see AddMapping).
            if (symbolOrMac != 0)
            {
                int before = _cmap.Count;
                _preserveExisting = true;
                try { ParseCmapFormat(r, symbolOrMac); }
                finally { _preserveExisting = false; }
                // The Symbolic flag is set from a (3,0) cmap, as the comment above this method says,
                // and only when that subtable actually supplied mappings.
                if (symbolIsThreeZero && _cmap.Count > before) Flags |= 4;
            }
        }

        private void ParseCmapFormat(Reader r, uint subOffset)
        {
            r.Seek(subOffset);
            int fmt = r.U16();
            if (fmt == 4) ParseCmap4(r, subOffset);
            else if (fmt == 12) ParseCmap12(r, subOffset);
            else if (fmt == 6) ParseCmap6(r, subOffset);
            else if (fmt == 0) ParseCmap0(r, subOffset);

        }

        private void ParseCmap4(Reader r, uint subOffset)
        {
            r.Seek(subOffset);
            r.Skip(2); // format
            r.Skip(2); // length
            r.Skip(2); // language
            int segCount = r.U16() / 2;
            r.Skip(6);

            var endC = new int[segCount];
            var startC = new int[segCount];
            var idDelta = new int[segCount];
            var idRangeOff = new int[segCount];
            for (int i = 0; i < segCount; i++) endC[i] = r.U16();
            r.Skip(2);
            for (int i = 0; i < segCount; i++) startC[i] = r.U16();
            for (int i = 0; i < segCount; i++) idDelta[i] = r.I16();
            uint rangeBase = r.Pos;
            for (int i = 0; i < segCount; i++) idRangeOff[i] = r.U16();

            // Cumulative inner-loop budget.  A crafted format-4 subtable can declare
            // ~32767 segments that EACH iterate the full BMP, yielding ~2 billion
            // in-bounds dictionary writes (CPU DoS — the dict itself never grows past
            // 0xFFFF keys, so a Count cap alone would not fire).  Bound the total work
            // performed across all segments instead.
            long work = 0;
            const long MaxCmapWork = 0x40000; // 256K code-point visits — covers the BMP twice
            for (int seg = 0; seg < segCount; seg++)
            {
                if (work > MaxCmapWork) break;
                int start = startC[seg], end = endC[seg];
                if (start == 0xFFFF) break;
                // Skip implausibly large segments outright; a single format-4 segment can
                // legitimately be no wider than the BMP (0x10000 code points).
                if (end - start > MaxCmapEntries) continue;
                for (int cp = start; cp <= end && cp != 0xFFFF; cp++)
                {
                    if (work++ > MaxCmapWork) break;
                    int gid;
                    if (idRangeOff[seg] == 0)
                        gid = (cp + idDelta[seg]) & 0xFFFF;
                    else
                    {
                        uint ptr = rangeBase + (uint)(seg * 2) + (uint)idRangeOff[seg] + (uint)((cp - start) * 2);
                        if (ptr + 2 > r.Length) { gid = 0; }
                        else
                        {
                            uint saved = r.Pos;
                            r.Seek(ptr);
                            gid = r.U16();
                            r.Seek(saved);
                            if (gid != 0) gid = (gid + idDelta[seg]) & 0xFFFF;
                        }
                    }
                    if (gid != 0) AddMapping(cp, gid);
                }
            }
        }

        private void ParseCmap12(Reader r, uint subOffset)
        {
            r.Seek(subOffset);
            r.Skip(4);  // format(2) + reserved(2)
            r.Skip(4);  // length
            r.Skip(4);  // language
            uint nGroups = r.U32();
            // Cumulative work, as format 4 counts it: groups that overlap one another never raise
            // _cmap.Count, so a hundred thousand copies of one 0x1FFFF-wide group would be
            // 1.3×10^10 writes — groups × width, two counts the file chooses — with neither break
            // firing.
            long work = 0;
            for (uint g = 0; g < nGroups; g++)
            {
                if (_cmap.Count > MaxCmapEntries || work > MaxCmap12Work) break;
                if (r.Pos + 12 > r.Length) break; // truncated group table
                uint startChar = r.U32();
                uint endChar = r.U32();
                uint startGid = r.U32();
                if (endChar < startChar) continue;
                if (endChar - startChar > MaxCmapEntries) continue; // implausibly large group
                // The loop ends AT endChar, not by `cp <= endChar` after cp++: a group ending at
                // 0xFFFFFFFF made cp wrap to 0 and map the low code points to the group's glyphs until
                // MaxCmapEntries stopped it.
                for (uint cp = startChar; ; cp++)
                {
                    if (_cmap.Count > MaxCmapEntries || work++ > MaxCmap12Work) break;
                    int gid = (int)(startGid + (cp - startChar));
                    if (gid != 0) AddMapping((int)cp, gid);
                    if (cp == endChar) break;
                }
            }
            Cmap12CodePointVisits = work;
        }

        /// <summary>
        /// The code points a format-12 subtable's groups may visit together: four times the entries
        /// the map holds, enough for every real font's groups and a few overlaps.
        /// </summary>
        private const long MaxCmap12Work = 4L * MaxCmapEntries;

        /// <summary>The code points the last format-12 subtable parsed visited: the budget's evidence, for tests.</summary>
        internal long Cmap12CodePointVisits { get; private set; }

        /// <summary>
        /// Adds a code point → glyph mapping. While <see cref="_preserveExisting"/> is set — i.e.
        /// while the lowest-priority (3,0)/(1,0) tier is being merged — an existing mapping wins,
        /// so a well-formed Unicode font parses byte-identically to before the tier existed.
        /// </summary>
        private void AddMapping(int codePoint, int glyphId)
        {
            if (_preserveExisting && _cmap.ContainsKey(codePoint)) return;
            _cmap[codePoint] = glyphId;
        }

        private bool _preserveExisting;

        /// <summary>
        /// cmap format 0 — a 256-byte "one glyph index per code" array. This is what a (1,0)
        /// Macintosh Roman subtable almost always is, and it had no case in the format dispatch,
        /// so such a font produced an empty cmap.
        /// </summary>
        private void ParseCmap0(Reader r, uint subOffset)
        {
            r.Seek(subOffset);
            r.Skip(2); r.Skip(2); r.Skip(2); // format, length, language
            for (int code = 0; code < 256 && r.Pos + 1 <= r.Length; code++)
            {
                int gid = r.U8();
                if (gid != 0) AddMapping(code, gid);
            }
        }

        private void ParseCmap6(Reader r, uint subOffset)
        {
            r.Seek(subOffset);
            r.Skip(2); r.Skip(2); r.Skip(2); // format, length, language
            int first = r.U16();
            int count = r.U16();
            // Bound the loop by the actual buffer length: a truncated subtable with a
            // large declared count would otherwise read past the end of the font bytes
            // (the outer try/catch would catch it, but that discards the whole cmap).
            for (int i = 0; i < count && r.Pos + 2 <= r.Length && _cmap.Count <= MaxCmapEntries; i++)
            {
                int gid = r.U16();
                if (gid != 0) AddMapping(first + i, gid);
            }
        }

        // ----------------------------------------------------------------
        //  Big-endian reader
        // ----------------------------------------------------------------
        private sealed class Reader
        {
            private readonly byte[] _d;
            private int _p;
            public Reader(byte[] d) { _d = d; }
            public uint Pos => (uint)_p;
            public uint Length => (uint)_d.Length;
            public void Seek(uint o) => _p = (int)o;
            public void Skip(int n) => _p += n;
            public uint U32() { uint v = ((uint)_d[_p] << 24) | ((uint)_d[_p + 1] << 16) | ((uint)_d[_p + 2] << 8) | _d[_p + 3]; _p += 4; return v; }
            public int I32() { return (int)U32(); }
            public int U8() { int v = _d[_p]; _p += 1; return v; }
            public int U16() { int v = (_d[_p] << 8) | _d[_p + 1]; _p += 2; return v; }
            public short I16() { short v = (short)((_d[_p] << 8) | _d[_p + 1]); _p += 2; return v; }
            public string Tag() { var s = new string(new[] { (char)_d[_p], (char)_d[_p + 1], (char)_d[_p + 2], (char)_d[_p + 3] }); _p += 4; return s; }
            public byte[] Bytes(int n) { var b = new byte[n]; Array.Copy(_d, _p, b, 0, Math.Min(n, _d.Length - _p)); _p += n; return b; }
        }
    }
}
