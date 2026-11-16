// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Resolves the glyph IDs (GIDs) for a positioned PDF text run, given the font's
    /// /CIDToGIDMap, Identity mapping, or Unicode cmap (with a four-stage fallback).
    ///
    /// <para>Extracted verbatim from <see cref="ContentStreamInterpreter"/> so that the
    /// raster renderer and <see cref="PdfSvgRenderer"/> share one implementation — both
    /// produce identical glyph selection, which is what guarantees the SVG vector-outline
    /// output matches the bitmap output.</para>
    /// </summary>
    internal static class GlyphIdResolver
    {
        /// <summary>
        /// The glyph ID <paramref name="typeface"/> maps <paramref name="codePoint"/> to, or 0 when
        /// it has none — the <b>one</b> place a one-off cmap probe creates its <see cref="SKFont"/>.
        /// </summary>
        /// <remarks>
        /// <para><b>Why a font at all.</b> <c>SKTypeface.GetGlyph</c> is obsolete in favour of
        /// <see cref="SKFont"/>, which is disposable — so a bare call becomes three lines, and five
        /// scattered copies of those three lines is how one of them ends up leaking a font. This is
        /// the one copy.</para>
        ///
        /// <para><b>The size is irrelevant and that was measured, not assumed.</b> A glyph ID is a
        /// cmap lookup, so it does not depend on the font's size: the same typeface answers 36 for
        /// <c>'A'</c> at the default size and at 144pt, and agrees with the obsolete typeface call
        /// on every code point tried including the absent ones (which are 0 both ways). That is
        /// what makes a default-constructed font correct here, and what would make a <i>shared</i>
        /// one correct if a caller ever needs to avoid the allocation.</para>
        ///
        /// <para><b>For a loop, hoist the font instead of calling this per character.</b>
        /// <see cref="FromUnicode"/> and the fallback ladder in <see cref="Resolve"/> each build one
        /// and keep it for the whole run; this exists for the single-probe callers in
        /// <c>FontCache</c>, which ask one question about one candidate face.</para>
        /// </remarks>
        internal static ushort Glyph(SKTypeface typeface, int codePoint)
        {
            if (typeface == null) return 0;
            using var font = new SKFont(typeface);
            return font.GetGlyph(codePoint);
        }

        /// <summary>
        /// Returns glyph IDs for each Unicode code point in <paramref name="text"/>.
        /// Returns an empty array when <paramref name="typeface"/> is null or
        /// <paramref name="text"/> is empty.
        /// Using explicit glyph IDs (rather than passing the raw string to SkiaSharp)
        /// bypasses the platform text stack's BiDi reordering, which would double-shape
        /// pre-positioned PDF text streams.
        /// </summary>
        internal static ushort[] FromUnicode(string text, SKTypeface typeface)
        {
            if (string.IsNullOrEmpty(text) || typeface == null)
                return Array.Empty<ushort>();
            using var font = new SKFont(typeface);
            var ids = new List<ushort>(text.Length);
            for (int i = 0; i < text.Length; )
            {
                int cp = CodePointAt(text, i, out int units);
                ids.Add(font.GetGlyph(cp));
                i += units;
            }
            return ids.ToArray();
        }

        /// <summary>
        /// The code point at <paramref name="i"/> and the UTF-16 units it takes: a surrogate pair's
        /// scalar, else the unit itself — a lone surrogate standing for itself, which no font maps.
        /// </summary>
        /// <remarks>
        /// <c>char.ConvertToUtf32</c> throws on a lone surrogate, and decoded text can hold one: a
        /// <c>/ToUnicode</c> destination longer than four digits keeps it
        /// (<c>CMapParser.HexToUnicodeString</c>). The throw dropped the whole text run and left the
        /// text matrix where the run had moved it.
        /// </remarks>
        internal static int CodePointAt(string text, int i, out int units)
        {
            if (char.IsSurrogatePair(text, i))
            {
                units = 2;
                return char.ConvertToUtf32(text[i], text[i + 1]);
            }
            units = 1;
            return text[i];
        }

        /// <summary>
        /// Resolves the GID for each decoded character code: /CIDToGIDMap, Identity, or Unicode
        /// cmap with a five-stage fallback (the simple font's encoded character → decoded codepoint
        /// → raw ASCII → Latin ligature → Windows PUA F0xx), the first also taken for a code whose
        /// decoded text is U+FFFD.
        /// </summary>
        /// <remarks>
        /// Takes codes already split by the font's encoding CMap rather than a byte array plus a
        /// fixed stride. The stride form could not express a mixed-width CMap, and it indexed
        /// /CIDToGIDMap by raw code — correct only for Identity encodings, wrong for every
        /// predefined CJK CMap, where the code must be mapped to a CID first.
        /// </remarks>
        internal static ushort[] Resolve(
            FontCache fc, string fontResourceName, PdfDictionary resources, PdfDocument doc,
            IReadOnlyList<CMapCodec.Code> codes, string text, SKTypeface typeface)
        {
            var cidToGid = fc.GetCidToGid(fontResourceName, resources, doc);
            bool isIdentityCidToGid = fc.GetIsIdentityCidToGid(fontResourceName, resources, doc);
            ushort[] glyphIds;
            if (cidToGid != null && cidToGid.Count > 0)
            {
                glyphIds = new ushort[codes.Count];
                for (int gi = 0; gi < codes.Count; gi++)
                    glyphIds[gi] = cidToGid.TryGetValue(codes[gi].Cid, out int gid) ? (ushort)gid : (ushort)0;
            }
            else if (isIdentityCidToGid)
            {
                // CIDToGIDMap = /Identity: CID == GID.
                glyphIds = new ushort[codes.Count];
                for (int gi = 0; gi < codes.Count; gi++)
                    glyphIds[gi] = (ushort)codes[gi].Cid;
            }
            else
            {
                // A symbolic TrueType font with no /Encoding has no encoded characters: its codes
                // address the built-in cmap, which ApplyBuiltInGlyphIds answers.
                var encodedFont = fc.GetPdfFont(fontResourceName, resources, doc) as PdfSimpleFont;
                if (encodedFont is PdfTrueTypeFont builtIn && builtIn.UsesBuiltInEncoding) encodedFont = null;

                if (encodedFont != null && typeface != null)
                {
                    // §9.6.6.4: a simple font's glyph is selected through the character its own
                    // encoding gives the code — one glyph a code. /ToUnicode says what the text
                    // means and takes no part: a code it maps to another letter drew that letter,
                    // and one it maps to several characters drew one glyph per character and
                    // moved every later glyph of the run against its code.
                    glyphIds = new ushort[codes.Count];
                    using var byEncoding = new SKFont(typeface);
                    for (int gi = 0; gi < codes.Count; gi++)
                        if (codes[gi].ByteLength == 1 && encodedFont.TryGetEncodedCharacter(codes[gi].Value, out char encoded))
                            glyphIds[gi] = byEncoding.GetGlyph(encoded);
                }
                else
                {
                    glyphIds = FromUnicode(text, typeface);
                }

                // §9.6.6.4: a simple TrueType font can name its glyphs directly — a symbolic font
                // through its built-in (3,0) cmap, and any font through a /Differences name its
                // `post` table defines. That is an ANSWER, not a fallback, so it overwrites what
                // the Unicode round-trip produced rather than only patching GID 0, for a symbolic
                // font's ASCII-range codes as well.
                bool[] programAnswered = ApplyBuiltInGlyphIds(fc, fontResourceName, resources, doc, codes, ref glyphIds);

                // A code the encoding gives no character the face has (a /Differences name the
                // Adobe Glyph List lacks, a code outside the base encoding) falls to its own text:
                // what the code decodes to, never the run's text indexed by the code's position —
                // except with no font to decode through, where the caller's text is all there is.
                if (typeface != null)
                {
                    bool fontDecodes = fc.GetPdfFont(fontResourceName, resources, doc) != null;
                    // One font for the whole ladder: it is disposable, and the probes below run per
                    // character, so a per-probe font would allocate several per glyph.
                    using var probe = new SKFont(typeface);
                    for (int gi = 0; gi < glyphIds.Length && gi < codes.Count; gi++)
                    {
                        string decodedText = fontDecodes
                            ? fc.DecodeCode(fontResourceName, codes[gi], resources, doc)
                            : gi < text.Length ? text.Substring(gi, char.IsSurrogatePair(text, gi) ? 2 : 1) : null;
                        int decoded = string.IsNullOrEmpty(decodedText) ? -1 : CodePointAt(decodedText, 0, out _);
                        // U+FFFD is a producer saying the code has no text meaning (InDesign maps its
                        // tab leaders so, to keep them out of extracted text), not naming a glyph: the
                        // glyph drawn through it is the font's replacement glyph or its .notdef.
                        bool noMeaning = decoded == 0xFFFD;
                        if (glyphIds[gi] != 0 && !noMeaning) continue;
                        if (programAnswered != null && programAnswered[gi]) continue;

                        int cc = codes[gi].Value;
                        // The code's character under the font's own encoding — §9.6.6.4's selector,
                        // which /ToUnicode takes no part in.
                        if (encodedFont != null && codes[gi].ByteLength == 1
                            && encodedFont.TryGetEncodedCharacter(cc, out char encoded))
                        {
                            ushort encodedGid = probe.GetGlyph(encoded);
                            if (encodedGid > 0) { glyphIds[gi] = encodedGid; continue; }
                        }
                        // A U+FFFD the encoding cannot place keeps the font's own replacement glyph.
                        if (glyphIds[gi] != 0) continue;
                        // Try the decoded code point (correct for /Differences-encoded fonts).
                        if (decoded >= 0)
                        {
                            ushort altGid = probe.GetGlyph(decoded);
                            if (altGid > 0) { glyphIds[gi] = altGid; continue; }
                        }
                        // Fallback: raw char code as Unicode codepoint (WinAnsiEncoding / StandardEncoding).
                        bool decodedIsNonAscii = decoded >= 0 && (decoded > 0x7E || decoded < 0x20);
                        if (!decodedIsNonAscii && cc >= 0x20 && cc <= 0x7E)
                        {
                            ushort altGid = probe.GetGlyph(cc);
                            if (altGid > 0) glyphIds[gi] = altGid;
                        }
                        // Ligature component fallback.
                        if (glyphIds[gi] == 0 && decodedIsNonAscii && decoded <= char.MaxValue)
                        {
                            int compCp = LigatureFirstComponentCode((char)decoded);
                            if (compCp > 0)
                            {
                                ushort compGid = probe.GetGlyph(compCp);
                                if (compGid > 0) glyphIds[gi] = compGid;
                            }
                        }
                        // Windows Private Use Area fallback (U+F000..U+F0FF) — legacy Hebrew/
                        // Greek/Cyrillic TrueType fonts store glyphs at 0xF000 | charByte.
                        if (glyphIds[gi] == 0 && decodedIsNonAscii && codes[gi].ByteLength == 1)
                        {
                            ushort f0Gid = probe.GetGlyph(0xF000 | cc);
                            if (f0Gid > 0) glyphIds[gi] = f0Gid;
                        }
                    }
                }
            }
            return glyphIds;
        }

        /// <summary>
        /// Overwrites the glyph ids of every code the font's <b>own program</b> maps, per
        /// §9.6.6.4 — the symbolic <c>(3,0)</c> cmap, and <c>/Differences</c> names resolved
        /// through the <c>post</c> table or a <c>gNN</c> subset convention.
        /// </summary>
        /// <remarks>
        /// Gated on the typeface actually being the embedded program. A glyph id is an index into
        /// that program; against a system substitute it would select an unrelated glyph, which is
        /// strictly worse than the Unicode path the substitute can serve correctly.
        /// </remarks>
        /// <returns>Which codes the program answered, or <c>null</c> when it answered none.</returns>
        private static bool[] ApplyBuiltInGlyphIds(
            FontCache fc, string fontResourceName, PdfDictionary resources, PdfDocument doc,
            IReadOnlyList<CMapCodec.Code> codes, ref ushort[] glyphIds)
        {
            if (fc == null || codes == null || codes.Count == 0) return null;
            if (fc.GetPdfFont(fontResourceName, resources, doc) is not PdfTrueTypeFont ttf) return null;

            if (fc.IsSystemFallbackFont(fontResourceName, resources, doc))
            {
                if (ttf.UsesBuiltInEncoding && ttf.ShouldReportBuiltInMappingIssue())
                    RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                        $"Font '{fontResourceName}' is symbolic with no /Encoding, so §9.6.6.4 " +
                        "requires its built-in (3,0) cmap, but the embedded program could not be " +
                        "loaded and a system substitute is in use; glyph selection falls back to " +
                        "Unicode and may pick the wrong glyphs.");
                return null;
            }

            if (!ttf.HasBuiltInGlyphMapping)
            {
                if (ttf.UsesBuiltInEncoding && ttf.ShouldReportBuiltInMappingIssue())
                    RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                        $"Font '{fontResourceName}' is symbolic with no /Encoding but exposes no " +
                        "built-in cmap; falling back to Unicode glyph selection. " +
                        (PdfTrueTypeFont.LastFontProgramError ?? "the font program has no (3,0) or (1,0) subtable"));
                return null;
            }

            if (glyphIds.Length != codes.Count)
            {
                var resized = new ushort[codes.Count];
                Array.Copy(glyphIds, resized, Math.Min(glyphIds.Length, codes.Count));
                glyphIds = resized;
            }

            var answered = new bool[codes.Count];
            for (int gi = 0; gi < codes.Count; gi++)
                if (ttf.TryGetGlyphId(codes[gi].Value, out ushort gid))
                {
                    glyphIds[gi] = gid;
                    answered[gi] = true;
                }
            return answered;
        }

        /// <summary>
        /// Splits <paramref name="bytes"/> into character codes using the font's encoding CMap.
        /// Simple fonts have no CMap: each byte is its own code and its own CID.
        /// </summary>
        internal static IReadOnlyList<CMapCodec.Code> DecodeCodes(
            FontCache fc, string fontResourceName, PdfDictionary resources, PdfDocument doc,
            byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
                return Array.Empty<CMapCodec.Code>();

            var codec = fc?.GetEncodingCodec(fontResourceName, resources, doc);
            if (codec != null) return codec.Decode(bytes);

            var simple = new CMapCodec.Code[bytes.Length];
            for (int i = 0; i < bytes.Length; i++)
                simple[i] = new CMapCodec.Code(bytes[i], bytes[i], 1);
            return simple;
        }

        /// <summary>
        /// The code <see cref="DecodeCodes"/> would give at <paramref name="offset"/> under
        /// <paramref name="codec"/> (the font's encoding CMap, or <c>null</c> for one byte a code),
        /// and how many bytes it takes — at least one. For a caller stepping through a shown string
        /// without a list per string.
        /// </summary>
        internal static int NextCode(CMapCodec codec, byte[] bytes, int offset, out CMapCodec.Code code)
        {
            if (codec != null) return codec.ReadCode(bytes, offset, out code);
            code = new CMapCodec.Code(bytes[offset], bytes[offset], 1);
            return 1;
        }

        /// <summary>
        /// Returns the standard char code for the first component of a Latin ligature,
        /// or 0 if the char is not a recognised ligature.  Used to render a visible
        /// fallback when the embedded font lacks the combined ligature glyph.
        /// </summary>
        internal static int LigatureFirstComponentCode(char decoded) => decoded switch
        {
            'ﬀ' => 0x66, // ff  → 'f'
            'ﬁ' => 0x66, // fi  → 'f'
            'ﬂ' => 0x66, // fl  → 'f'
            'ﬃ' => 0x66, // ffi → 'f'
            'ﬄ' => 0x66, // ffl → 'f'
            'ﬅ' => 0x73, // ſt  → 's' (long s)
            'ﬆ' => 0x73, // st  → 's'
            _ => 0
        };
    }
}
