// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// What an embedded font program holds, for the rules that compare a font descriptor's
    /// <c>/CharSet</c> (ISO 32000-1 Table 122) or <c>/CIDSet</c> (Table 124) with it — ISO 19005-1
    /// 6.3.5, ISO 19005-2/3 6.2.11.4.2, ISO 14289-1 7.21.4.2. The narrow public door to Core's
    /// <c>internal</c> Type 1, CFF and sfnt readers, as <see cref="SimpleFontWidthProbe"/> is for widths.
    /// </summary>
    /// <remarks>
    /// Every answer is <see langword="null"/> when the program cannot say exactly — no program, a
    /// charset the CFF parser approximates, a Type 1 <c>/CharStrings</c> read short, a <c>loca</c> table
    /// that does not index <c>glyf</c> — with the reason in <c>unreadable</c> when a program was there
    /// and could not be read, so a checker reports what it could not judge rather than a verdict.
    /// </remarks>
    public static class EmbeddedGlyphSets
    {
        /// <summary>The glyph every font program carries and neither set need list.</summary>
        public const string NotDefGlyph = ".notdef";

        /// <summary>The CIDs a <c>/CIDSet</c> can name: one bit each, CID 0 to 65 535.</summary>
        private const int CidCount = 65536;

        /// <summary>
        /// The glyph names the simple font's embedded Type 1 program defines — a <c>/FontFile</c>
        /// program's <c>/CharStrings</c>, or a <c>/FontFile3</c> CFF's charset — or
        /// <see langword="null"/>.
        /// </summary>
        public static ISet<string> GlyphNames(PdfDictionary descriptor, Func<PdfObject, PdfObject> resolve, out string unreadable)
        {
            unreadable = null;
            if (descriptor == null || resolve == null) return null;
            try
            {
                if (resolve(descriptor[PdfName.FontFile]) is PdfStream)
                {
                    Type1Font font = ReadType1(descriptor, resolve, out unreadable);
                    return font == null ? null : new HashSet<string>(font.CharStrings.Keys, StringComparer.Ordinal);
                }
                if (resolve(descriptor[PdfName.FontFile3]) is PdfStream cffStream)
                {
                    CffFont cff = CffParser.Parse(FontStreamBytes.Program(cffStream));
                    if (cff == null)
                    {
                        unreadable = "its CFF program could not be read";
                        return null;
                    }
                    if (cff.IsCidKeyed || !cff.HasExactGlyphNames) return null;
                    return new HashSet<string>(cff.GlyphNameToGid.Keys, StringComparer.Ordinal);
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                unreadable = "its program could not be decoded (" + ex.GetType().Name + ")";
            }
            return null;
        }

        /// <summary>
        /// The CIDs the CIDFont's embedded program holds — a CID-keyed CFF's charset, every glyph of a
        /// CFF that is not CID-keyed (its CID is its glyph index), or for a <c>CIDFontType2</c> each CID
        /// whose glyph, through <c>/CIDToGIDMap</c> (<c>/Identity</c> when absent), has outline data in
        /// <c>glyf</c> — or <see langword="null"/>.
        /// </summary>
        public static ISet<int> Cids(PdfDictionary cidFont, Func<PdfObject, PdfObject> resolve, out string unreadable)
        {
            unreadable = null;
            if (cidFont == null || resolve == null) return null;
            var descriptor = resolve(cidFont[PdfName.FontDescriptor]) as PdfDictionary;
            if (descriptor == null) return null;
            try
            {
                if (resolve(descriptor[PdfName.FontFile3]) is PdfStream cffStream)
                {
                    CffFont cff = CffParser.Parse(FontStreamBytes.Program(cffStream));
                    if (cff == null)
                    {
                        unreadable = "its CFF program could not be read";
                        return null;
                    }
                    var cids = new HashSet<int>();
                    if (cff.IsCidKeyed)
                        foreach (int cid in cff.CharCodeToGid.Keys) cids.Add(cid);
                    else
                        for (int gid = 0; gid < cff.Charstrings.Count; gid++) cids.Add(gid);
                    return cids;
                }
                if (resolve(descriptor[PdfName.FontFile2]) is PdfStream ttStream)
                {
                    bool[] outlined = OutlinedGlyphs(FontStreamBytes.Program(ttStream), out unreadable);
                    if (outlined == null) return null;
                    var cids = new HashSet<int>();
                    PdfObject map = resolve(cidFont[PdfName.CIDToGIDMap]);
                    if (map == null || (map is PdfName n && n.Value == "Identity"))
                    {
                        for (int gid = 0; gid < outlined.Length; gid++)
                            if (outlined[gid]) cids.Add(gid);
                        return cids;
                    }
                    if (!(map is PdfStream mapStream))
                    {
                        unreadable = "its /CIDToGIDMap is neither /Identity nor a stream";
                        return null;
                    }
                    byte[] gids = mapStream.GetDecodedData();
                    int count = Math.Min(gids.Length / 2, CidCount);
                    for (int cid = 0; cid < count; cid++)
                    {
                        int gid = (gids[2 * cid] << 8) | gids[2 * cid + 1];
                        if (gid < outlined.Length && outlined[gid]) cids.Add(cid);
                    }
                    return cids;
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                unreadable = "its program could not be decoded (" + ex.GetType().Name + ")";
            }
            return null;
        }

        /// <summary>
        /// The glyph name each code of an embedded simple Type 1 font selects (ISO 32000-1 §9.6.6): the
        /// dictionary's own (<see cref="SimpleFontEncodingNames.ByCode"/>), and for a code it leaves to the
        /// implicit base — no <c>/Encoding</c>, or an encoding dictionary with no <c>/BaseEncoding</c> —
        /// the embedded program's built-in encoding, which Table 114 makes that base once a program is
        /// embedded. <see langword="null"/>, with the reason, when the program is there and its encoding
        /// cannot be read.
        /// </summary>
        public static IReadOnlyDictionary<int, string> GlyphNamesByCode(PdfDictionary font, Func<PdfObject, PdfObject> resolve, out string unreadable)
        {
            unreadable = null;
            if (font == null || resolve == null) return new Dictionary<int, string>();
            var names = new Dictionary<int, string>();
            foreach (var entry in SimpleFontEncodingNames.ByCode(font, resolve)) names[entry.Key] = entry.Value;
            PdfObject encoding = resolve(font[PdfName.Encoding]);
            bool implicitBase = encoding == null || (encoding is PdfDictionary dictionary && dictionary[PdfName.BaseEncoding] == null);
            if (!implicitBase) return names;

            IReadOnlyDictionary<int, string> builtIn = BuiltInEncoding(resolve(font[PdfName.FontDescriptor]) as PdfDictionary, resolve, out unreadable);
            if (builtIn == null) return unreadable == null ? names : null;
            foreach (var entry in builtIn)
                if (!names.ContainsKey(entry.Key)) names[entry.Key] = entry.Value;
            return names;
        }

        /// <summary>
        /// Whether a font's embedded program defines the glyph a code selects — ISO 19005-1 6.3.5,
        /// 19005-2/3 6.2.11.4.1, 19005-4 6.2.10.4.1, ISO 14289-1 7.21.4.1: "embedded fonts shall define all
        /// glyphs referenced for rendering". The code is a simple font's byte, or a composite font's CID
        /// (the caller decodes the string). The answer is <see langword="true"/>, <see langword="false"/>,
        /// or <see langword="null"/> where the font does not say which glyph the code selects (an unnamed
        /// code, <c>.notdef</c>, CID 0); the function itself is <see langword="null"/> for a font with no
        /// embedded program this reads, with the reason when one was there and could not be read.
        /// </summary>
        /// <remarks>
        /// A Type 1 font: the code's name (<see cref="GlyphNamesByCode"/>) among the program's glyph names.
        /// A Type 3 font: the name among its <c>/CharProcs</c>. A simple TrueType font (§9.6.6.4): a symbolic
        /// font's code directly or in the symbol area (0xF000, 0xF100, 0xF200 plus the code), a non-symbolic
        /// font's glyph name through the Adobe Glyph List to Unicode, or through MacRoman to its Macintosh
        /// code — a glyph other than 0. <see cref="TrueTypeFontProgram"/> merges its subtables into one map,
        /// so a code a subtable the rule would not consult maps is found too: the reading can pass a glyph,
        /// never fail one it should pass. A CIDFont: a CFF's charset holds the CID, or a TrueType program's
        /// glyph through <c>/CIDToGIDMap</c> is neither 0 nor past its glyph count.
        /// </remarks>
        public static Func<int, bool?> GlyphPresence(PdfDictionary font, Func<PdfObject, PdfObject> resolve, out string unreadable)
        {
            unreadable = null;
            if (font == null || resolve == null) return null;
            string subtype = (resolve(font[PdfName.Subtype]) as PdfName)?.Value;
            try
            {
                if (subtype == "Type3")
                {
                    if (!(resolve(font[PdfName.CharProcs]) is PdfDictionary procs)) return null;
                    IReadOnlyDictionary<int, string> names = SimpleFontEncodingNames.ByCode(font, resolve);
                    return code => names.TryGetValue(code, out string n) && n != NotDefGlyph ? procs[new PdfName(n)] != null : (bool?)null;
                }

                var descriptor = subtype == "Type0" ? null : resolve(font[PdfName.FontDescriptor]) as PdfDictionary;
                if (subtype == "Type1" || subtype == "MMType1")
                {
                    if (descriptor == null || (descriptor[PdfName.FontFile] == null && descriptor[PdfName.FontFile3] == null)) return null;
                    ISet<string> defined = GlyphNames(descriptor, resolve, out unreadable);
                    if (defined == null) return null;
                    IReadOnlyDictionary<int, string> names = GlyphNamesByCode(font, resolve, out unreadable);
                    if (names == null) return null;
                    return code => names.TryGetValue(code, out string n) && n != NotDefGlyph ? defined.Contains(n) : (bool?)null;
                }

                if (subtype == "TrueType")
                {
                    if (!(resolve(descriptor?[PdfName.FontFile2]) is PdfStream ttStream)) return null;
                    byte[] data = FontStreamBytes.Program(ttStream);
                    TrueTypeFontProgram program = TrueTypeFontProgram.Parse(data);
                    if (program == null)
                    {
                        unreadable = "its TrueType program could not be read";
                        return null;
                    }
                    // A subset can keep a glyph's slot and drop its outline (test file
                    // 6-2-11-4-1-t02-fail-c maps 'a' to such a glyph): that glyph is not defined. A
                    // whitespace character's glyph is empty by nature, so the rule reads it as defined;
                    // where the character is not known, an empty glyph is not judged.
                    bool[] outlined = OutlinedGlyphs(data, out _);
                    bool symbolic = resolve(descriptor[PdfName.Flags]) is PdfNumber flags && flags.IsInteger && (flags.LongValue & 4) != 0;
                    // True: mapped to an outlined glyph; null: mapped to an empty one; false: not mapped.
                    bool? Maps(int codePoint)
                    {
                        if (!program.TryGetGlyph(codePoint, out ushort gid) || gid == 0) return false;
                        return outlined == null || gid >= outlined.Length || outlined[gid] ? true : (bool?)null;
                    }
                    if (symbolic)
                        return code =>
                        {
                            bool? best = false;
                            foreach (int codePoint in new[] { code, 0xF000 + code, 0xF100 + code, 0xF200 + code })
                            {
                                bool? found = Maps(codePoint);
                                if (found == true) return true;
                                if (found == null) best = null;
                            }
                            return best;
                        };
                    IReadOnlyDictionary<int, string> names = SimpleFontEncodingNames.ByCode(font, resolve);
                    bool hasEncoding = font[PdfName.Encoding] != null;
                    var macCodes = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int c = 0; c < SimpleFontEncodingNames.MacRoman.Length; c++)
                        if (SimpleFontEncodingNames.MacRoman[c] != null && !macCodes.ContainsKey(SimpleFontEncodingNames.MacRoman[c]))
                            macCodes[SimpleFontEncodingNames.MacRoman[c]] = c;
                    return code =>
                    {
                        if (!names.TryGetValue(code, out string n))
                            n = hasEncoding ? null : AdobeStandardEncodingNames.Get(code);
                        if (n == null || n == NotDefGlyph) return null;
                        bool knowsUnicode = PdfGlyphNames.TryGetUnicode(n, out char unicode);
                        bool? viaUnicode = knowsUnicode ? Maps(unicode) : false;
                        bool? viaMac = macCodes.TryGetValue(n, out int mac) ? Maps(mac) : false;
                        if (viaUnicode == true || viaMac == true) return true;
                        if (viaUnicode == null || viaMac == null)
                            return knowsUnicode && char.IsWhiteSpace(unicode) ? true : false;   // mapped, but to an empty glyph
                        return knowsUnicode || macCodes.ContainsKey(n) ? false : (bool?)null;
                    };
                }

                if (subtype == "Type0")
                {
                    var cidFont = resolve(font[PdfName.DescendantFonts]) is PdfArray descendants && descendants.Count > 0
                        ? resolve(descendants[0]) as PdfDictionary : null;
                    var cidDescriptor = resolve(cidFont?[PdfName.FontDescriptor]) as PdfDictionary;
                    if (cidDescriptor == null) return null;
                    if (resolve(cidDescriptor[PdfName.FontFile3]) is PdfStream)
                    {
                        ISet<int> held = Cids(cidFont, resolve, out unreadable);
                        if (held == null) return null;
                        return cid => cid == 0 ? (bool?)null : held.Contains(cid);
                    }
                    if (resolve(cidDescriptor[PdfName.FontFile2]) is PdfStream ttStream)
                    {
                        TrueTypeFontProgram program = TrueTypeFontProgram.Parse(FontStreamBytes.Program(ttStream));
                        if (program == null)
                        {
                            unreadable = "its TrueType program could not be read";
                            return null;
                        }
                        int glyphs = program.GlyphCount;
                        PdfObject map = resolve(cidFont[PdfName.CIDToGIDMap]);
                        // A CID is the file's — an embedded CMap's cidchar names any integer — so it is
                        // judged against the map before an index is computed from it.
                        if (map == null || (map is PdfName name && name.Value == "Identity"))
                            return cid => cid == 0 ? (bool?)null : cid > 0 && cid < glyphs;
                        if (!(map is PdfStream mapStream)) return null;
                        byte[] gids = mapStream.GetDecodedData();
                        return cid =>
                        {
                            if (cid == 0) return null;
                            if (cid < 0 || cid >= gids.Length / 2) return false;
                            int gid = (gids[2 * cid] << 8) | gids[2 * cid + 1];
                            return gid != 0 && gid < glyphs;
                        };
                    }
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                unreadable = "its program could not be decoded (" + ex.GetType().Name + ")";
            }
            return null;
        }

        /// <summary>
        /// The CIDs a composite font's shown string selects, through the font's own encoding CMap
        /// (§9.7.5: a predefined name — Identity, or one registered — or an embedded stream; the one
        /// splitter, <c>CMapCodec</c>), codes no codespace range matches left out. <see langword="null"/>,
        /// with the reason in <paramref name="undecoded"/>, when the CMap the font declares could not be
        /// honoured — an unregistered predefined name, a stream that does not read — where the codec would
        /// otherwise split the bytes as Identity and the CIDs would be a guess.
        /// </summary>
        public static Func<byte[], IReadOnlyList<int>> CidReader(PdfDictionary type0Font, PdfDocument document, out string undecoded)
        {
            undecoded = null;
            if (type0Font == null) return null;
            CMapCodec codec = CMapCodec.FromFontEncoding(type0Font, document);
            if (CMapCodec.LastEncodingError != null)
            {
                undecoded = CMapCodec.LastEncodingError;
                return null;
            }
            return shown =>
            {
                var cids = new List<int>();
                if (shown == null) return cids;
                foreach (CMapCodec.Code code in codec.Decode(shown))
                    if (code.CodespaceMatched) cids.Add(code.Cid);
                return cids;
            };
        }

        /// <summary>
        /// A simple font program's own code → glyph name table: a Type 1 program's <c>/Encoding</c>
        /// (StandardEncoding when it names that), or a CFF's encoding through its charset. Null when there
        /// is no such program, or a CFF's names are not exact.
        /// </summary>
        private static IReadOnlyDictionary<int, string> BuiltInEncoding(PdfDictionary descriptor, Func<PdfObject, PdfObject> resolve, out string unreadable)
        {
            unreadable = null;
            if (descriptor == null) return null;
            try
            {
                if (resolve(descriptor[PdfName.FontFile]) is PdfStream)
                {
                    Type1Font font = ReadType1(descriptor, resolve, out unreadable);
                    if (font == null) return null;
                    var names = new Dictionary<int, string>();
                    IReadOnlyList<string> table = font.Encoding ?? (IReadOnlyList<string>)AdobeStandardEncodingNames.Table;
                    for (int code = 0; code < table.Count && code < 256; code++)
                        if (table[code] != null && table[code] != NotDefGlyph) names[code] = table[code];
                    return names;
                }
                if (resolve(descriptor[PdfName.FontFile3]) is PdfStream cffStream)
                {
                    CffFont cff = CffParser.Parse(FontStreamBytes.Program(cffStream));
                    if (cff == null)
                    {
                        unreadable = "its CFF program could not be read";
                        return null;
                    }
                    if (cff.IsCidKeyed || !cff.HasExactGlyphNames) return null;
                    var byGid = new Dictionary<int, string>();
                    foreach (var entry in cff.GlyphNameToGid) byGid[entry.Value] = entry.Key;
                    var names = new Dictionary<int, string>();
                    foreach (var entry in cff.CharCodeToGid)
                        if (entry.Key >= 0 && entry.Key < 256 && byGid.TryGetValue(entry.Value, out string name) && name != NotDefGlyph)
                            names[entry.Key] = name;
                    return names;
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                unreadable = "its program could not be decoded (" + ex.GetType().Name + ")";
            }
            return null;
        }

        /// <summary>A <c>/FontFile</c> program, its lengths checked and its <c>/CharStrings</c> read in full, or null with the reason.</summary>
        private static Type1Font ReadType1(PdfDictionary descriptor, Func<PdfObject, PdfObject> resolve, out string unreadable)
        {
            unreadable = null;
            if (!(resolve(descriptor[PdfName.FontFile]) is PdfStream type1)) return null;
            byte[] data = FontStreamBytes.Program(type1);
            if (!(resolve(type1[PdfName.Length1]) is PdfNumber l1) || !l1.IsInteger
                || !(resolve(type1[PdfName.Length2]) is PdfNumber l2) || !l2.IsInteger
                || l1.LongValue <= 0 || l2.LongValue < 0 || l1.LongValue + l2.LongValue > data.Length)
            {
                unreadable = "its /Length1 and /Length2 do not describe the program";
                return null;
            }
            Type1Font font = Type1Parser.Parse(data, (int)l1.LongValue, (int)l2.LongValue);
            if (font == null || !font.CharStringsComplete)
            {
                unreadable = "its Type 1 /CharStrings could not be read in full";
                return null;
            }
            return font;
        }

        /// <summary>
        /// The CIDs a <c>/CIDSet</c> stream identifies: bit <i>n</i> of the stream, high-order bit of each
        /// byte first, stands for CID <i>n</i> (Table 124). Bits past CID 65 535 name nothing.
        /// </summary>
        public static ISet<int> CidSetCids(PdfStream cidSet)
        {
            var cids = new HashSet<int>();
            if (cidSet == null) return cids;
            byte[] bits = cidSet.GetDecodedData();
            int bytes = Math.Min(bits.Length, CidCount / 8);
            for (int i = 0; i < bytes; i++)
            {
                if (bits[i] == 0) continue;
                for (int b = 0; b < 8; b++)
                    if ((bits[i] & (0x80 >> b)) != 0) cids.Add(i * 8 + b);
            }
            return cids;
        }

        /// <summary>
        /// The glyph names a <c>/CharSet</c> string lists — "a string listing the character names", each
        /// a name object (<c>/A/B/C</c>), whitespace between them allowed (Table 122).
        /// </summary>
        public static ISet<string> CharSetNames(PdfString charSet)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (charSet == null) return names;
            string text = Text.Latin1Text.Decode(charSet.RawBytes);
            foreach (string part in text.Split('/'))
            {
                string name = part.Trim(' ', '\t', '\r', '\n', '\f', '\0');
                if (name.Length > 0) names.Add(name);
            }
            return names;
        }

        /// <summary>
        /// Which glyphs of a TrueType program have outline data — <c>loca[g] &lt; loca[g + 1]</c> — or
        /// <see langword="null"/> when the program has no <c>glyf</c>/<c>loca</c> or its <c>loca</c>
        /// decreases or points past <c>glyf</c> (§5.3.4 of the OpenType <c>loca</c> table).
        /// </summary>
        private static bool[] OutlinedGlyphs(byte[] font, out string unreadable)
        {
            unreadable = null;
            if (!SfntTableDirectory.TryRead(font, out var tables, out string reason))
            {
                unreadable = "its TrueType table directory could not be read (" + reason + ")";
                return null;
            }
            SfntTableDirectory.TableRecord? head = null, maxp = null, loca = null, glyf = null;
            foreach (var t in tables)
            {
                if (t.Tag == "head") head = t;
                else if (t.Tag == "maxp") maxp = t;
                else if (t.Tag == "loca") loca = t;
                else if (t.Tag == "glyf") glyf = t;
            }
            if (head == null || maxp == null || loca == null || glyf == null
                || head.Value.Length < 54 || maxp.Value.Length < 6)
            {
                unreadable = "its TrueType program has no head, maxp, loca and glyf tables to read glyphs from";
                return null;
            }
            int indexToLoc = (short)((font[head.Value.Offset + 50] << 8) | font[head.Value.Offset + 51]);
            int glyphs = (font[maxp.Value.Offset + 4] << 8) | font[maxp.Value.Offset + 5];
            int entry = indexToLoc == 0 ? 2 : 4;
            if ((long)(glyphs + 1) * entry > loca.Value.Length)
            {
                unreadable = "its loca table is shorter than maxp's " + glyphs.ToString(CultureInfo.InvariantCulture) + " glyphs";
                return null;
            }
            var outlined = new bool[glyphs];
            int at = loca.Value.Offset;
            long previous = ReadLoca(font, at, indexToLoc);
            for (int g = 0; g < glyphs; g++)
            {
                long next = ReadLoca(font, at + (g + 1) * entry, indexToLoc);
                if (next < previous || next > glyf.Value.Length)
                {
                    unreadable = "its loca table does not index glyf (glyph " + g.ToString(CultureInfo.InvariantCulture) + ")";
                    return null;
                }
                outlined[g] = next > previous;
                previous = next;
            }
            return outlined;
        }

        /// <summary>One <c>loca</c> entry at <paramref name="at"/>, in bytes: a short one doubled (<c>indexToLocFormat</c> 0), a long one as it is.</summary>
        internal static long ReadLoca(byte[] font, int at, int indexToLoc)
            => indexToLoc == 0
                ? ((font[at] << 8) | font[at + 1]) * 2L
                : ((long)font[at] << 24) | ((long)font[at + 1] << 16) | ((long)font[at + 2] << 8) | font[at + 3];
    }
}
