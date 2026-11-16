// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Writing
{
    /// <summary>
    /// Builds the indirect-object graph for embedding a <see cref="TrueTypeFontProgram"/>
    /// as a composite (Type0 / Identity-H) font in a written PDF:
    ///   Type0 → CIDFontType2 (CIDToGIDMap /Identity) → FontDescriptor → FontFile2,
    /// plus a /W glyph-width array and a /ToUnicode CMap (so the text is selectable
    /// and copy-pastes back to its original code points).
    ///
    /// The 2-byte show-text codes ARE the glyph ids (Identity-H + CIDToGIDMap /Identity),
    /// so callers emit <c>&lt;GID GID …&gt; Tj</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Two outline formats, one object graph.</b> <c>glyf</c> outlines embed as
    /// CIDFontType2 + <c>/FontFile2</c> and CFF ones as CIDFontType0 + <c>/FontFile3 /OpenType</c>,
    /// and that pair of differences — plus <c>/CIDToGIDMap</c>, which Table 117 gives only to
    /// CIDFontType2 — is the whole of what the two branches do not share. Everything else is one
    /// answer to one question, so it is written once: <see cref="Descriptor"/> (Table 122),
    /// <see cref="DescendantCidFont"/> (Table 117, including the Adobe-Identity-0
    /// <c>/CIDSystemInfo</c>), <see cref="Type0Dictionary"/> (Table 114), <see cref="FlateStream"/>
    /// for every compressed stream object and <see cref="Tagged"/> for §9.6.4's subset tag.</para>
    ///
    /// <para>They were written out twice, and the CFF copy's own comments said so — "exactly as on
    /// the TrueType path above", "for the same reason stated above". A comment asserting that two
    /// implementations agree is this repository's documented duplication marker: the descriptor
    /// alone is nine entries that ISO 32000-1 requires to match between <c>/FontName</c> and
    /// <c>/BaseFont</c>, and a tenth added to one branch is a file strict validators reject on
    /// every font that took the other.</para>
    /// </remarks>
    internal static class Type0FontBuilder
    {
        /// <summary>
        /// Allocates and returns the objects for one embedded font. The returned
        /// <c>type0ObjNum</c> is the object referenced from a page's /Font resource.
        /// <paramref name="usedGidToUnicode"/> drives /W and /ToUnicode (only the
        /// glyphs actually drawn are described); <paramref name="usedGidToText"/> names the glyphs
        /// that stand for several letters — a ligature the font's <c>GSUB</c> formed — and the text
        /// /ToUnicode writes for each (<see cref="Text.GlyphMapping.Text"/>).
        /// </summary>
        public static (int type0ObjNum, List<(int objNum, PdfObject obj)> objects) BuildObjects(
            ref int nextObjNum,
            TrueTypeFontProgram program,
            IReadOnlyDictionary<ushort, int> usedGidToUnicode,
            IReadOnlyDictionary<ushort, string> usedGidToText = null)
        {
            var objects = new List<(int, PdfObject)>();
            string baseFont = SanitizeName(program.PostScriptName);

            // CFF/OpenType (.otf) outlines: embed the whole program as FontFile3 in a
            // CIDFontType0 descendant (the glyf-based subsetter doesn't apply).
            if (program.HasCff)
                return BuildCffObjects(ref nextObjNum, program, usedGidToUnicode, usedGidToText, baseFont, objects);

            // Subset the font to just the used glyphs. The content stream's 2-byte
            // codes stay the ORIGINAL glyph ids (used as CIDs); a /CIDToGIDMap stream
            // remaps each CID to the compacted glyph id in the subset, so the content
            // never has to change. Falls back to the full font + /Identity on failure.
            var usedGids = usedGidToUnicode.Keys.ToList();
            var (subset, oldToNew) = TrueTypeSubsetter.Subset(program.FontData, usedGids);
            byte[] raw = subset ?? program.FontData;

            // The gate is `subset != null`, not "we asked to subset": the line above falls back to
            // the WHOLE program whenever TrueTypeSubsetter declines (no glyf/loca table, or
            // anything it throws on), and a tag on a complete program is the same lie in the other
            // direction. See Tagged for the rest.
            if (subset != null)
                baseFont = Tagged(baseFont, usedGids);

            // ---- FontFile2 (the compressed TrueType program) ----
            var fontFile = FlateStream(raw, length1: raw.Length);
            int fontFileNum = Add(ref nextObjNum, objects, fontFile);

            // ---- CIDToGIDMap stream (only when subsetted/remapped) ----
            PdfObject cidToGidMap = PdfName.Identity;
            if (oldToNew != null && oldToNew.Count > 0)
            {
                int maxCid = oldToNew.Keys.Max();
                byte[] map = new byte[(maxCid + 1) * 2];
                foreach (var kv in oldToNew)
                {
                    map[kv.Key * 2] = (byte)(kv.Value >> 8);
                    map[kv.Key * 2 + 1] = (byte)kv.Value;
                }
                cidToGidMap = new PdfReference(Add(ref nextObjNum, objects, FlateStream(map)), 0);
            }

            // ---- FontDescriptor ----
            int descNum = Add(ref nextObjNum, objects,
                Descriptor(program, baseFont, PdfName.FontFile2, fontFileNum));

            // ---- CIDFontType2 (descendant) ----
            int cidNum = Add(ref nextObjNum, objects, DescendantCidFont(
                program, baseFont, PdfName.CIDFontType2, descNum, cidToGidMap, usedGidToUnicode));

            // ---- ToUnicode CMap ----
            int toUniNum = Add(ref nextObjNum, objects,
                FlateStream(ToUnicodeCMapWriter.Build(usedGidToUnicode, usedGidToText)));

            // ---- Type0 (the referenced font) ----
            int type0Num = Add(ref nextObjNum, objects, Type0Dictionary(baseFont, cidNum, toUniNum));

            return (type0Num, objects);
        }

        // Embeds a CFF/OpenType font program (CIDFontType0 + FontFile3 /Subtype /OpenType).
        // Content emits glyph ids as CIDs under Identity-H, which renders these fonts in the
        // common readers.
        //
        // §9.7.4.2: a CID-keyed CFF's CIDs reach its glyphs through its charset, so a program whose
        // charset orders its CIDs any other way (a CJK OpenType font) is embedded with its charset
        // made the identity first (Core.Fonts.Cff.CffCharsetIdentity) — then CID == GID holds for it
        // as it does for a CFF with no CID keying, where there is no charset indirection at all.
        //
        // Subsetting is by CHARSTRING BLANKING (Core.Fonts.Cff.CffSubsetter), not by rebuilding the
        // font: unused charstrings are overwritten in place with endchar, so every offset in the
        // CFF stays valid and only Flate sees the difference. The file length is identical; the
        // compressed /FontFile3 is not. GID == CID here, so usedGidToUnicode's keys ARE the glyph
        // set to keep — no CIDToGIDMap is involved and none is needed, which is why this branch
        // passes null for it.
        private static (int, List<(int, PdfObject)>) BuildCffObjects(ref int nextObjNum,
            TrueTypeFontProgram program, IReadOnlyDictionary<ushort, int> usedGidToUnicode,
            IReadOnlyDictionary<ushort, string> usedGidToText,
            string baseFont, List<(int, PdfObject)> objects)
        {
            var keepGids = new List<int>(usedGidToUnicode.Count);
            foreach (ushort gid in usedGidToUnicode.Keys) keepGids.Add(gid);

            byte[] identityCharset = Fonts.Cff.CffCharsetIdentity.Rewrite(program.FontData, out string charsetReason)
                ?? throw new PdfException(
                    "The CFF font program '" + baseFont + "' is CID-keyed with a charset that is not the identity " +
                    "and cannot be rewritten in its own bytes (" + charsetReason + "), so the glyph ids the content " +
                    "names cannot be its CIDs; the font cannot be embedded.");

            var subset = Fonts.Cff.CffSubsetter.BlankUnusedGlyphs(identityCharset, keepGids);
            byte[] raw = subset.Font ?? identityCharset;

            // The gate is `subset.Font != null`, exactly as on the TrueType path above. A refusal
            // (CFF2, a seac component that could not be resolved, a font whose every glyph is used)
            // means the WHOLE program is embedded, and a tag on a complete font is the same lie in
            // the other direction. This path wrote no tag at all until CFF subsetting landed, which
            // was correct for as long as nothing here was ever a subset.
            if (subset.Font != null)
                baseFont = Tagged(baseFont, usedGidToUnicode.Keys);

            var ff3 = FlateStream(raw);
            ff3.Set(PdfName.Subtype, PdfName.OpenType);
            int ff3Num = Add(ref nextObjNum, objects, ff3);

            int descNum = Add(ref nextObjNum, objects,
                Descriptor(program, baseFont, PdfName.FontFile3, ff3Num));

            int cidNum = Add(ref nextObjNum, objects, DescendantCidFont(
                program, baseFont, PdfName.CIDFontType0, descNum, cidToGidMap: null, usedGidToUnicode));

            int toUniNum = Add(ref nextObjNum, objects,
                FlateStream(ToUnicodeCMapWriter.Build(usedGidToUnicode, usedGidToText)));

            int type0Num = Add(ref nextObjNum, objects, Type0Dictionary(baseFont, cidNum, toUniNum));

            return (type0Num, objects);
        }

        /// <summary>
        /// ISO 32000-1 §9.6.4's <c>"ABCDEF+"</c> subset tag, applied to a name that may already
        /// carry one.
        /// </summary>
        /// <remarks>
        /// The tag is a claim that the embedded program holds only some of the font's glyphs, and
        /// §9.8.1 then requires the descriptor's <c>/FontName</c> to equal the <c>/BaseFont</c> — so
        /// it goes on all THREE names a branch writes (the descriptor's <c>/FontName</c>, the
        /// descendant's <c>/BaseFont</c> and the Type0's) or on none, and tagging only the outer one
        /// produces a file strict validators reject. Tagging one name is what a caller gets wrong;
        /// re-tagging the single <c>baseFont</c> local before any of the three is written is what
        /// makes that impossible here. <see cref="SubsetTag"/> is the single rule this writer and the
        /// Accessibility font remediator both reach, so one library labels one situation one way.
        /// </remarks>
        private static string Tagged(string baseFont, IEnumerable<ushort> usedGids)
        {
            string untagged = SubsetTag.Strip(baseFont);
            return SubsetTag.Apply(untagged, SubsetTag.Make(untagged, usedGids));
        }

        /// <summary>
        /// The <c>/FontDescriptor</c> both branches write (Table 122), differing only in which
        /// <c>/FontFileN</c> key names the embedded program.
        /// </summary>
        private static PdfDictionary Descriptor(
            TrueTypeFontProgram program, string baseFont, PdfName fontFileKey, int fontFileObjNum)
        {
            var bbox = new PdfArray();
            bbox.Add(new PdfNumber(program.BBoxXMin));
            bbox.Add(new PdfNumber(program.BBoxYMin));
            bbox.Add(new PdfNumber(program.BBoxXMax));
            bbox.Add(new PdfNumber(program.BBoxYMax));

            var desc = new PdfDictionary();
            desc.Set(PdfName.Type, PdfName.FontDescriptor);
            desc.Set(PdfName.FontName, new PdfName(baseFont));
            desc.Set(PdfName.Flags, new PdfNumber(program.Flags));
            desc.Set(PdfName.FontBBox, bbox);
            desc.Set(PdfName.ItalicAngle, new PdfNumber(program.ItalicAngle));
            desc.Set(PdfName.Ascent, new PdfNumber(program.Ascent));
            desc.Set(PdfName.Descent, new PdfNumber(program.Descent));
            desc.Set(PdfName.CapHeight, new PdfNumber(program.CapHeight));
            desc.Set(PdfName.StemV, new PdfNumber(program.StemV));
            desc.Set(fontFileKey, new PdfReference(fontFileObjNum, 0));
            return desc;
        }

        /// <summary>
        /// The descendant CIDFont (Table 117) both branches write: Adobe-Identity-0
        /// <c>/CIDSystemInfo</c>, the descriptor, <c>/DW</c> and <c>/W</c>.
        /// </summary>
        /// <remarks>
        /// <c>cidToGidMap</c> is written when it is non-null and omitted when it is not. Table 117
        /// defines the entry for CIDFontType2 only — a CFF descendant reaches its glyphs through
        /// the charset, so writing one there would be meaningless rather than merely redundant.
        /// </remarks>
        private static PdfDictionary DescendantCidFont(
            TrueTypeFontProgram program,
            string baseFont,
            PdfName subtype,
            int descNum,
            PdfObject cidToGidMap,
            IReadOnlyDictionary<ushort, int> usedGidToUnicode)
        {
            var cid = CompositeFontDictionaries.DescendantCidFont(subtype, baseFont,
                CompositeFontDictionaries.IdentityCidSystemInfo(), new PdfReference(descNum, 0));
            if (cidToGidMap != null)
                cid.Set(PdfName.CIDToGIDMap, cidToGidMap);
            cid.Set(PdfName.DW, new PdfNumber(program.GetAdvance(0)));
            cid.Set(PdfName.W, BuildWidths(program, usedGidToUnicode.Keys));
            return cid;
        }

        /// <summary>The Type0 font (Table 114) both branches write — identical on either.</summary>
        private static PdfDictionary Type0Dictionary(string baseFont, int cidNum, int toUniNum)
        {
            var type0 = CompositeFontDictionaries.Type0(baseFont, PdfName.IdentityH, new PdfReference(cidNum, 0));
            type0.Set(PdfName.ToUnicode, new PdfReference(toUniNum, 0));
            return type0;
        }

        /// <summary>
        /// A Flate-compressed stream object with its <c>/Length</c> and <c>/Filter</c> — the shape
        /// all four streams this writer emits share (the program, the <c>/CIDToGIDMap</c> and the
        /// <c>/ToUnicode</c> CMap).
        /// </summary>
        /// <remarks>
        /// <c>length1</c> is <c>/Length1</c>, the uncompressed size, which Table 127 requires for
        /// a <c>/FontFile2</c> and defines for nothing else this writer emits.
        /// </remarks>
        private static PdfStream FlateStream(byte[] decoded, int? length1 = null)
        {
            byte[] encoded = FlateEncodeFilter.Encode(decoded);
            var stream = new PdfStream { RawData = encoded };
            stream.Set(PdfName.Length, new PdfNumber(encoded.Length));
            if (length1.HasValue) stream.Set(PdfName.Length1, new PdfNumber(length1.Value));
            stream.Set(PdfName.Filter, PdfName.FlateDecode);
            return stream;
        }

        /// <summary>
        /// Takes the next object number, stamps it onto <paramref name="obj"/> and appends it to
        /// the graph. One place, because an object added without being numbered is written as a
        /// direct object at number 0 and every reference to it dangles.
        /// </summary>
        private static int Add(ref int nextObjNum, List<(int, PdfObject)> objects, PdfObject obj)
        {
            int number = nextObjNum++;
            obj.ObjectNumber = number;
            obj.GenerationNumber = 0;
            objects.Add((number, obj));
            return number;
        }

        private static PdfArray BuildWidths(TrueTypeFontProgram program, IEnumerable<ushort> usedGids)
        {
            // /W [ gid [w] gid2 [w2] … ] — one entry per used glyph (CID == GID).
            var w = new PdfArray();
            foreach (ushort gid in usedGids.Distinct().OrderBy(g => g))
            {
                w.Add(new PdfNumber(gid));
                var inner = new PdfArray();
                inner.Add(new PdfNumber(program.GetAdvance(gid)));
                w.Add(inner);
            }
            return w;
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "EmbeddedFont";
            var sb = new StringBuilder();
            foreach (char c in name)
                sb.Append(c > 0x20 && c < 0x7F && c != '/' && c != '(' && c != ')'
                          && c != '<' && c != '>' && c != '[' && c != ']' && c != '{'
                          && c != '}' && c != '%' && c != '#' ? c : '-');
            return sb.ToString();
        }
    }
}
