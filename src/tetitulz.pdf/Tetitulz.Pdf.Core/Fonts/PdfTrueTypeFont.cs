// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A <c>/Subtype /TrueType</c> simple font. The width, <c>/Differences</c> and decode state is
    /// <see cref="PdfSimpleFont"/>'s; what is here is §9.6.6.4 <b>glyph selection</b> against the
    /// embedded font program, which the Type 1 side has no counterpart for.
    /// </summary>
    internal sealed class PdfTrueTypeFont : PdfSimpleFont
    {
        // ---- built-in (font-program) glyph selection, §9.6.6.4 -------------------
        // All of this is resolved LAZILY: it needs the embedded font program, and decompressing
        // a /FontFile2 on the text-extraction path (which never asks for a glyph id) would be a
        // pure cost. Nothing below runs unless a renderer asks for a glyph id.

        private readonly PdfDictionary _fontDict;
        private readonly PdfDocument   _document;
        private readonly bool          _symbolic;
        private readonly bool          _hasEncodingEntry;

        /// <summary>Code → glyph id derived from <c>/Differences</c> (post table, or a gNN name).</summary>
        private readonly Dictionary<int, int> _differenceGlyphIds;

        private bool _differenceGlyphIdsResolved;
        private bool _programProbed;
        private bool _builtInIssueReported;
        private FontProgramTables _program;

        [ThreadStatic] private static string _lastFontProgramError;

        /// <summary>
        /// Why the embedded font program could not be consulted for glyph selection on this thread,
        /// or <c>null</c> when it was read successfully (or was never needed). Read it after
        /// <see cref="TryGetGlyphId"/> returns <c>false</c> for a font that should have had a
        /// built-in encoding. <c>[ThreadStatic]</c> per the CLAUDE.md diagnostics contract.
        /// </summary>
        internal static string LastFontProgramError => _lastFontProgramError;

        public PdfTrueTypeFont(PdfDictionary fontDict, PdfDocument document, Dictionary<int, string> toUnicode)
        {
            _differenceGlyphIds = new Dictionary<int, int>();
            _fontDict           = fontDict;
            _document           = document;

            BaseFont = ReadBaseFontAndWidths(fontDict, document, toUnicode);
            // /MissingWidth and §9.6.6.4's Symbolic flag — one implementation for both simple
            // subtypes, in PdfSimpleFont. §9.6.6.4 keys the whole encoding decision on that bit:
            // a symbolic font's codes address the font's own built-in cmap, NOT a Latin text
            // encoding.
            _symbolic = ReadFontDescriptor(fontDict, document);

            // §9.6.6's whole encoding ladder — one implementation for both simple subtypes, in
            // PdfSimpleFont. It answers _hasEncodingEntry on the way past, because §9.6.6.4 keys
            // UsesBuiltInEncoding on "symbolic AND no /Encoding entry" and that entry is read once.
            //
            // A symbolic font has no text encoding at all; its codes go through the font's built-in
            // (3,0) cmap (see TryGetGlyphId). WinAnsi is kept for the *text-extraction* view of a
            // symbolic font because there is no better answer for a code whose only meaning is a
            // glyph index — /ToUnicode is the producer's way to say what the text was, and it
            // already wins above.
            _hasEncodingEntry = ReadEncoding(fontDict, document, _symbolic);
        }

        // §9.6.6.4 resolves a TrueType /Differences name against the font's own post table FIRST and
        // only then through the Adobe glyph list, and a subset name ("g35", "cid12") is in neither —
        // and falling back to the base encoding would draw the wrong glyph. The raw
        // names are PdfSimpleFont._differenceGlyphNames; resolving them is deferred to
        // EnsureDifferenceGlyphIds so the font program is read only if a renderer asks.

        // GetWidth, HasExplicitWidth and Decode are PdfSimpleFont's — including the §9.6.2.2
        // Standard-14 AFM width fallback. Table 113 carries the same "Required except for the
        // standard 14 fonts" note as Table 111, so a /Subtype /TrueType /BaseFont /Arial dictionary
        // may legally omit /Widths — and measured by /MissingWidth, which defaults to 0, every glyph
        // would draw at zero advance and pile up at one x.

        // ================================================================
        //  §9.6.6.4 built-in glyph selection
        // ================================================================

        /// <summary>
        /// True when the FontDescriptor <c>/Flags</c> Symbolic bit (bit 3, value 4) is set, as
        /// decided by <see cref="PdfSimpleFont.ReadFontDescriptor"/> — the one test both simple
        /// subtypes now apply.
        /// </summary>
        internal bool IsSymbolic => _symbolic;

        /// <summary>
        /// True when §9.6.6.4 says the character codes address the font program's own built-in
        /// cmap rather than a text encoding: the font is symbolic and carries no <c>/Encoding</c>.
        /// </summary>
        internal bool UsesBuiltInEncoding => _symbolic && !_hasEncodingEntry;

        /// <summary>
        /// Returns <c>true</c> the first time it is called on this instance, so a renderer can
        /// report an unusable built-in encoding once per font rather than once per show-text
        /// operator.
        /// </summary>
        internal bool ShouldReportBuiltInMappingIssue()
        {
            if (_builtInIssueReported) return false;
            _builtInIssueReported = true;
            return true;
        }

        /// <summary>
        /// True when this font can name a glyph id for at least one character code without going
        /// through Unicode — either because <c>/Differences</c> resolved against the font's
        /// <c>post</c> table (or used a <c>gNN</c>-style subset name), or because the font is
        /// symbolic with a built-in <c>(3,0)</c> cmap. Reads the embedded font program on first
        /// call.
        /// </summary>
        internal bool HasBuiltInGlyphMapping
        {
            get
            {
                EnsureDifferenceGlyphIds();
                if (_differenceGlyphIds.Count > 0) return true;
                if (!UsesBuiltInEncoding) return false;
                EnsureProgram();
                return (_program?.SymbolCmap?.Count ?? 0) > 0
                    || (_program?.MacCmap?.Count ?? 0) > 0;
            }
        }

        /// <summary>
        /// Maps a raw character code to a glyph id in the <b>embedded</b> font program, per
        /// §9.6.6.4. The order is the spec's: an explicit <c>/Differences</c> name resolved through
        /// the font's <c>post</c> table wins; otherwise a symbolic font with no <c>/Encoding</c>
        /// goes through its <c>(3,0)</c> cmap (addressed by the raw code, then by the widely-used
        /// <c>0xF000 | code</c> convention), then through a <c>(1,0)</c> Macintosh cmap.
        /// </summary>
        /// <remarks>
        /// The returned id indexes the <i>embedded</i> program, so a caller that substituted a
        /// system font must not use it.
        /// </remarks>
        internal bool TryGetGlyphId(int charCode, out ushort glyphId)
        {
            glyphId = 0;

            EnsureDifferenceGlyphIds();
            if (_differenceGlyphIds.TryGetValue(charCode, out int diffGid)
                && diffGid > 0 && diffGid <= ushort.MaxValue)
            {
                glyphId = (ushort)diffGid;
                return true;
            }

            if (!UsesBuiltInEncoding) return false;
            EnsureProgram();
            if (_program == null) return false;

            if (TryLookup(_program.SymbolCmap, charCode, out glyphId)) return true;
            // The (3,0) table is conventionally populated at 0xF000 + code (the Windows symbol
            // PUA range); real symbol fonts do one or the other, so both are tried.
            if (charCode >= 0 && charCode <= 0xFF
                && TryLookup(_program.SymbolCmap, 0xF000 | charCode, out glyphId)) return true;
            if (TryLookup(_program.MacCmap, charCode, out glyphId)) return true;
            return false;
        }

        private static bool TryLookup(Dictionary<int, int> map, int code, out ushort glyphId)
        {
            glyphId = 0;
            if (map == null) return false;
            if (!map.TryGetValue(code, out int gid) || gid <= 0 || gid > ushort.MaxValue) return false;
            glyphId = (ushort)gid;
            return true;
        }

        private void EnsureDifferenceGlyphIds()
        {
            if (_differenceGlyphIdsResolved) return;
            _differenceGlyphIdsResolved = true;
            if (_differenceGlyphNames.Count == 0) return;

            EnsureProgram();
            var post = _program?.PostNames;

            // Stores the resolved id even when it is 0 (.notdef) — TryGetGlyphId's own
            // `diffGid > 0` gate below is unaffected either way (a stored 0 fails that gate
            // exactly as a missing key would), but DifferenceResolvesToNotdef needs to tell
            // "this name explicitly resolves to glyph 0" from "this name resolves to nothing at
            // all," which a dictionary that only ever stored positive values could never say.
            foreach (var kv in _differenceGlyphNames)
            {
                if (post != null && post.TryGetValue(kv.Value, out int postGid)
                    && postGid >= 0 && postGid <= ushort.MaxValue)
                    _differenceGlyphIds[kv.Key] = postGid;
                else if (TryParseGlyphIndexName(kv.Value, out int convGid))
                    _differenceGlyphIds[kv.Key] = convGid;
            }
        }

        /// <summary>
        /// True when <paramref name="charCode"/>'s <c>/Differences</c> name resolves — via the
        /// embedded font's own <c>post</c> table, or via the <c>gNN</c>/<c>indexNN</c>/<c>cidNN</c>
        /// glyph-index-name convention <see cref="TryParseGlyphIndexName"/> recognises — to glyph
        /// id 0, the <c>.notdef</c> glyph (ISO 32000-2:2020 §9.6.6.4/§9.7.6.3).
        /// </summary>
        /// <remarks>
        /// <para>Deliberately narrower than the full §9.6.6.4 ladder <see cref="TryGetGlyphId"/>
        /// walks: it answers only from <c>/Differences</c>, never from the symbol/Mac <c>cmap</c>
        /// fallback. A TrueType <c>cmap</c> subtable's own "no entry for this code" sentinel is
        /// bitwise identical to an explicit mapping to glyph 0 — the subtable formats have no
        /// third value — so a raw <c>cmap</c> lookup returning 0 can never be told apart from "not
        /// mapped here at all," and this checker's own "unresolvable ⇒ skip, never guess" rule
        /// means that half of the ladder must stay silent on this question forever, not just until
        /// someone gets around to it. <c>/Differences</c> is different: a code's name is either
        /// found in the embedded <c>post</c> table (the font's own glyph-name table) or spelled out
        /// directly as a glyph index by the file's own author, and either one naming glyph 0 is a
        /// deliberate, legible fact — not a format-level coincidence.</para>
        /// </remarks>
        internal bool DifferenceResolvesToNotdef(int charCode)
        {
            EnsureDifferenceGlyphIds();
            return _differenceGlyphIds.TryGetValue(charCode, out int diffGid) && diffGid == 0;
        }

        /// <summary>
        /// Recognises the subset glyph-name conventions that encode a glyph id directly:
        /// <c>g35</c>, <c>G35</c>, <c>glyph35</c>, <c>cid35</c>, <c>index35</c>. These name no
        /// character, so neither the Adobe glyph list nor any encoding can resolve them.
        /// </summary>
        internal static bool TryParseGlyphIndexName(string name, out int glyphId)
        {
            glyphId = 0;
            if (string.IsNullOrEmpty(name)) return false;

            // Longest prefix first: "glyph12" also starts with "g".
            int start = -1;
            if (name.StartsWith("glyph", StringComparison.Ordinal)) start = 5;
            else if (name.StartsWith("index", StringComparison.Ordinal)) start = 5;
            else if (name.StartsWith("cid", StringComparison.Ordinal)) start = 3;
            else if (name[0] == 'g' || name[0] == 'G') start = 1;
            if (start < 0 || start >= name.Length) return false;

            int digits = name.Length - start;
            // A glyph id is at most 65535, so a longer run of digits is not one — and bounding
            // the count is what stops an int32 accumulator wrapping a huge number back into range.
            if (digits > 5) return false;

            int value = 0;
            for (int i = start; i < name.Length; i++)
            {
                char c = name[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            if (value > ushort.MaxValue) return false;
            glyphId = value;
            return true;
        }

        private void EnsureProgram()
        {
            if (_programProbed) return;
            _programProbed = true;
            _lastFontProgramError = null;

            try
            {
                var fdObj = _fontDict?[PdfName.FontDescriptor];
                var fd = (_document?.ResolveObject(fdObj) ?? fdObj) as PdfDictionary;
                if (fd == null)
                {
                    _lastFontProgramError = "font has no /FontDescriptor, so no font program to read";
                    return;
                }

                // Deliberately /FontFile2 only. §9.6.6.4's post-table and (3,0) rules are about
                // TrueType programs; a /FontFile3 CFF has no `post` glyph names and its glyph
                // order is not what the OTF wrapper hands SkiaSharp.
                var ffObj = fd[PdfName.FontFile2];
                if (ffObj == null)
                {
                    _lastFontProgramError = "font has no /FontFile2 TrueType program";
                    return;
                }

                if ((_document?.ResolveObject(ffObj) ?? ffObj) is not PdfStream ffStream)
                {
                    _lastFontProgramError = "/FontFile2 is not a stream";
                    return;
                }

                byte[] data = FontStreamBytes.Program(ffStream);
                if (data == null || data.Length < 12)
                {
                    _lastFontProgramError = "/FontFile2 decoded to " + (data?.Length ?? 0) + " bytes";
                    return;
                }

                _program = FontProgramTables.Parse(data);
                if (_program == null)
                    _lastFontProgramError = "/FontFile2 is not a parseable sfnt font program";
            }
            catch (Exception ex)
            {
                _lastFontProgramError = ex.GetType().Name + ": " + ex.Message;
            }
        }

        // ================================================================
        //  Minimal sfnt reader: the two tables §9.6.6.4 needs
        // ================================================================

        /// <summary>
        /// The <c>cmap</c> subtables and <c>post</c> glyph names a simple TrueType font needs for
        /// §9.6.6.4 glyph selection.
        /// </summary>
        /// <remarks>
        /// <para>Deliberately not <see cref="TrueTypeFontProgram"/>, which is the write-side parser.
        /// Two reasons:</para>
        ///
        /// <para><b>1. The maps must stay separate, and that is the load-bearing reason.</b>
        /// <see cref="TryGetGlyphId"/> tries the <c>(3,0)</c> table at the raw code, then at
        /// <c>0xF000 | code</c> (the Windows symbol PUA convention), and only then the <c>(1,0)</c>
        /// Macintosh table — an ordered walk over two <i>distinct</i> maps.
        /// <c>TrueTypeFontProgram</c> merges every subtable into one dictionary in which Unicode
        /// wins and the symbol/Mac tier only fills gaps, so that order cannot be expressed through
        /// it. Routing this through it would change which glyph a symbolic font draws.</para>
        ///
        /// <para><b>2. It has no <c>post</c> glyph-name reader.</b> It reads <c>post</c> only for
        /// the italic angle and the fixed-pitch flag; <see cref="PostNames"/> has no counterpart
        /// there.</para>
        ///
        /// <para>Not "the write-side parser reads only <i>Unicode</i> cmap subtables":
        /// <c>TrueTypeFontProgram</c> has a <c>(3,0)</c>/<c>(1,0)</c> fallback tier, and the
        /// obvious response to that ("Core reads those, so merge them") is a behaviour change on the
        /// render path that reason 1 forbids.</para>
        /// </remarks>
        // internal, not private: directly testable, as its sibling reader TrueTypeFontProgram is, so
        // the two are held to the same answer on a truncated subtable. Nested in an
        // already-internal class, so this adds nothing to the public surface.
        internal sealed class FontProgramTables
        {
            /// <summary>The <c>(3,0)</c> Windows-Symbol subtable: raw code → glyph id.</summary>
            internal Dictionary<int, int> SymbolCmap;

            /// <summary>The <c>(1,0)</c> Macintosh-Roman subtable: byte code → glyph id.</summary>
            internal Dictionary<int, int> MacCmap;

            /// <summary>Custom <c>post</c> v2.0 glyph names → glyph id.</summary>
            internal Dictionary<string, int> PostNames;

            // Mirrors TrueTypeFontProgram's caps deliberately, so the two readers cannot disagree
            // about what a plausible font declares. (They live here rather than in PdfLimits
            // because they bound one parser's internal tables, not a document-level resource.)
            private const int  MaxCmapEntries = 0x20000;
            private const long MaxCmapWork    = 0x40000;
            private const int  MaxTables      = 512;

            /// <summary>The first 258 <c>post</c> name indices are the standard Macintosh set.</summary>
            private const int PostStandardNameCount = 258;

            internal static FontProgramTables Parse(byte[] d)
            {
                if (d == null || d.Length < 12) return null;

                uint sfnt = U32(d, 0);
                if (sfnt == 0x74746366u) return null;   // 'ttcf' collection — unsupported

                int numTables = U16(d, 4);
                if (numTables <= 0 || numTables > MaxTables) return null;
                if (12L + numTables * 16L > d.Length) return null;

                long cmapOff = -1, postOff = -1, postLen = 0;
                for (int i = 0; i < numTables; i++)
                {
                    int rec = 12 + i * 16;
                    string tag = Tag(d, rec);
                    long off = U32(d, rec + 8);
                    long len = U32(d, rec + 12);
                    if (off < 0 || off >= d.Length) continue;
                    if (tag == "cmap") cmapOff = off;
                    else if (tag == "post") { postOff = off; postLen = len; }
                }

                var t = new FontProgramTables();
                if (cmapOff >= 0) t.ReadCmap(d, cmapOff);
                if (postOff >= 0) t.PostNames = ReadPostNames(d, postOff, postLen);
                return t;
            }

            private void ReadCmap(byte[] d, long tableOff)
            {
                if (tableOff + 4 > d.Length) return;
                int subtables = U16(d, (int)tableOff + 2);
                if (subtables <= 0 || subtables > MaxTables) return;
                if (tableOff + 4 + subtables * 8L > d.Length) return;

                for (int i = 0; i < subtables; i++)
                {
                    int rec = (int)tableOff + 4 + i * 8;
                    int platform = U16(d, rec);
                    int encoding = U16(d, rec + 2);
                    long sub = tableOff + U32(d, rec + 4);
                    if (sub < 0 || sub + 4 > d.Length) continue;

                    if (platform == 3 && encoding == 0 && SymbolCmap == null)
                        SymbolCmap = ReadSubtable(d, sub);
                    else if (platform == 1 && encoding == 0 && MacCmap == null)
                        MacCmap = ReadSubtable(d, sub);
                }
            }

            private static Dictionary<int, int> ReadSubtable(byte[] d, long off)
            {
                int format = U16(d, (int)off);
                return format switch
                {
                    0 => ReadFormat0(d, off),
                    4 => ReadFormat4(d, off),
                    6 => ReadFormat6(d, off),
                    _ => null,
                };
            }

            /// <summary>
            /// Format 0: a 256-byte glyph index array. A <b>truncated</b> one yields the prefix
            /// that is present rather than nothing.
            /// </summary>
            /// <remarks>
            /// This required all 256 bytes and returned <c>null</c> otherwise, while
            /// <c>TrueTypeFontProgram.ParseCmap0</c> — the read side of the same table — loops
            /// while bytes remain and keeps what it finds. So for a symbolic font whose format-0
            /// subtable runs past end of file, <c>SymbolCmap</c>/<c>MacCmap</c> came back null here
            /// and §9.6.6.4 byte-code glyph selection fell through <b>entirely</b>, while the other
            /// reader mapped the font. All-or-nothing is the wrong trade for a table whose entries
            /// are independent: the codes that are present are still correct.
            /// </remarks>
            private static Dictionary<int, int> ReadFormat0(byte[] d, long off)
            {
                if (off + 6 > d.Length) return null;

                int start = (int)off + 6;
                int available = Math.Min(256, d.Length - start);
                if (available <= 0) return null;

                var map = new Dictionary<int, int>(available);
                for (int c = 0; c < available; c++)
                {
                    int gid = d[start + c];
                    if (gid != 0) map[c] = gid;
                }
                return map;
            }

            private static Dictionary<int, int> ReadFormat6(byte[] d, long off)
            {
                if (off + 10 > d.Length) return null;
                int first = U16(d, (int)off + 6);
                int count = U16(d, (int)off + 8);
                var map = new Dictionary<int, int>();
                for (int i = 0; i < count && off + 10 + i * 2L + 2 <= d.Length; i++)
                {
                    int gid = U16(d, (int)off + 10 + i * 2);
                    if (gid != 0) map[first + i] = gid;
                }
                return map;
            }

            private static Dictionary<int, int> ReadFormat4(byte[] d, long off)
            {
                if (off + 14 > d.Length) return null;
                int segCount = U16(d, (int)off + 6) / 2;
                if (segCount <= 0) return null;

                long endBase   = off + 14;
                long startBase = endBase + segCount * 2L + 2;
                long deltaBase = startBase + segCount * 2L;
                long rangeBase = deltaBase + segCount * 2L;
                if (rangeBase + segCount * 2L > d.Length) return null;

                var map = new Dictionary<int, int>();
                long work = 0;
                for (int seg = 0; seg < segCount; seg++)
                {
                    if (work > MaxCmapWork || map.Count > MaxCmapEntries) break;
                    int end   = U16(d, (int)(endBase   + seg * 2));
                    int start = U16(d, (int)(startBase + seg * 2));
                    int delta = (short)U16(d, (int)(deltaBase + seg * 2));
                    int range = U16(d, (int)(rangeBase + seg * 2));
                    if (start == 0xFFFF) break;
                    if (end < start || end - start > MaxCmapEntries) continue;

                    for (int c = start; c <= end && c != 0xFFFF; c++)
                    {
                        if (work++ > MaxCmapWork) break;
                        int gid;
                        if (range == 0)
                        {
                            gid = (c + delta) & 0xFFFF;
                        }
                        else
                        {
                            long p = rangeBase + seg * 2L + range + (c - start) * 2L;
                            if (p + 2 > d.Length) continue;
                            int raw = U16(d, (int)p);
                            gid = raw == 0 ? 0 : (raw + delta) & 0xFFFF;
                        }
                        if (gid != 0) map[c] = gid;
                    }
                }
                return map;
            }

            /// <summary>
            /// Reads the custom names of a <c>post</c> version 2.0 table. Indices below
            /// <see cref="PostStandardNameCount"/> name a standard Macintosh glyph, which the Adobe
            /// glyph list already resolves — the whole point of this lookup is the names it cannot,
            /// which are always in the custom range.
            /// </summary>
            private static Dictionary<string, int> ReadPostNames(byte[] d, long off, long declaredLen)
            {
                if (off + 34 > d.Length) return null;
                if (U32(d, (int)off) != 0x00020000u) return null;   // only v2.0 carries names

                int numGlyphs = U16(d, (int)off + 32);
                if (numGlyphs <= 0) return null;

                long indexBase = off + 34;
                if (indexBase + numGlyphs * 2L > d.Length) return null;

                long end = declaredLen > 0 && off + declaredLen <= d.Length ? off + declaredLen : d.Length;

                // Pascal strings, in order, for every index >= 258.
                var names = new List<string>();
                long p = indexBase + numGlyphs * 2L;
                while (p < end && names.Count <= numGlyphs)
                {
                    int len = d[(int)p];
                    p++;
                    if (p + len > end) break;
                    names.Add(len == 0 ? string.Empty : Latin1(d, (int)p, len));
                    p += len;
                }
                if (names.Count == 0) return null;

                var map = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int gid = 0; gid < numGlyphs; gid++)
                {
                    int index = U16(d, (int)(indexBase + gid * 2));
                    if (index < PostStandardNameCount) continue;
                    int k = index - PostStandardNameCount;
                    if (k >= names.Count) continue;
                    string name = names[k];
                    // First glyph wins: a duplicated name is a malformed font, and the lowest id
                    // is the deterministic choice.
                    if (name.Length > 0 && !map.ContainsKey(name)) map[name] = gid;
                }
                return map.Count > 0 ? map : null;
            }

            // ---- big-endian primitives (bounds are checked by every caller) ----
            private static int  U16(byte[] d, int p) => (d[p] << 8) | d[p + 1];
            private static uint U32(byte[] d, int p) => IO.BigEndian.U32(d, p);
            private static string Tag(byte[] d, int p) =>
                new string(new[] { (char)d[p], (char)d[p + 1], (char)d[p + 2], (char)d[p + 3] });
            // One implementation, in Core.Text.Latin1Text — six byte-identical copies existed.
            private static string Latin1(byte[] d, int p, int len)
                => Text.Latin1Text.Decode(d, p, len);
        }
    }
}
