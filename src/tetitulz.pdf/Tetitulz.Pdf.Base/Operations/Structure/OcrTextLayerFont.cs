// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Base.Operations.Structure
{
    /// <summary>
    /// The font an invisible OCR text layer is written with when the recognised text does not fit
    /// <c>/WinAnsiEncoding</c> — Hebrew, Arabic, Greek, Cyrillic, CJK.
    /// </summary>
    /// <remarks>
    /// <para><b>A Type0 / Identity-H font whose two-byte codes are UTF-16 code units, with a
    /// <c>/ToUnicode</c> that maps each one to itself, and a glyphless embedded font program.</b>
    /// That is an unusual thing to write and it is right for exactly one situation, so here is the
    /// reasoning.</para>
    /// <para>This text is painted in <b>render mode 3</b>. No glyph is ever drawn from it, by
    /// anyone, ever — the visible page is the scan underneath. The only consumer is text
    /// extraction and search, which reads <c>/ToUnicode</c> and never looks at an outline. So a
    /// real face would add hundreds of kilobytes per document, and raise a licensing question the
    /// caller cannot answer, to supply glyphs that are by construction invisible.</para>
    /// <para><b>The program it does embed has two empty glyphs and nothing else</b> (about a
    /// hundred bytes; every code reaches the second through <c>/CIDToGIDMap</c>, at the font's
    /// 1000-unit width). Embedding none would be refused by PDF/A: ISO 19005-1 §6.3.4 asks for
    /// every font program to be embedded, and its corrigendum carries no exemption for text
    /// drawn in mode 3 — so a flattened or OCR'd page could not be PDF/A-1. Tesseract embeds a
    /// glyphless font for the same reason.</para>
    /// <para><b>Do not copy this pattern to a visible text writer.</b> There the outlines are the
    /// whole point, and a non-embedded composite font is how a document comes out as a row of
    /// blank boxes on a machine that lacks the face. The AcroForm and signature appearance
    /// generators embed, and must.</para>
    /// <para>Code units rather than glyph ids means no <c>cmap</c> lookup, no shaping decision and
    /// no subsetting: the content stream carries the text itself, and extraction is the identity
    /// function on it. A surrogate pair travels as its two units and <c>/ToUnicode</c> maps each,
    /// which is how a reader reassembles the astral code point.</para>
    /// </remarks>
    internal static class OcrTextLayerFont
    {
        /// <summary>
        /// The resource names the layer references this font by: this and a number, the first the
        /// page does not use (<c>PdfOcrLayerWriter.FreeFontName</c>) — OcrF1 on a page with no layer.
        /// </summary>
        internal const string ResourcePrefix = "OcrF";

        /// <summary>
        /// Default glyph width, in glyph-space units. Every code gets the same one: an invisible
        /// glyph's advance is never composited with anything, and each word is positioned by its
        /// own text matrix, so a per-code width would describe outlines that do not exist.
        /// </summary>
        private const int DefaultWidth = 1000;

        /// <summary>The UTF-16 code units of <paramref name="text"/>, which are also its codes.</summary>
        internal static ushort[] CodeUnits(string text)
        {
            if (string.IsNullOrEmpty(text)) return Array.Empty<ushort>();
            var units = new ushort[text.Length];
            for (int i = 0; i < text.Length; i++) units[i] = text[i];
            return units;
        }

        /// <summary>
        /// Builds the font's indirect objects.
        /// </summary>
        /// <param name="nextObjNum">Next free object number; advanced for each object allocated.</param>
        /// <param name="usedCodes">
        /// Every code unit the layer shows. Only these reach <c>/ToUnicode</c>, so a page of Hebrew
        /// does not carry a map of the whole Basic Multilingual Plane.
        /// </param>
        /// <returns>The Type0 font's object number, and every object to add to the update.</returns>
        internal static (int fontObjNum, List<(int objNum, PdfObject obj)> objects) BuildObjects(
            ref int nextObjNum, ICollection<ushort> usedCodes)
        {
            var objects = new List<(int, PdfObject)>();

            // Each code unit maps to itself — the identity that makes the content stream its own
            // text — through the one /ToUnicode writer, shared with the composite-font path.
            byte[] toUnicode = ToUnicodeCMapWriter.Build(
                usedCodes.Select(code => new KeyValuePair<ushort, int>(code, code)));
            int toUnicodeObjNum = nextObjNum++;
            var toUnicodeStream = new PdfStream();
            toUnicodeStream.RawData = toUnicode;
            toUnicodeStream.Set(PdfName.Length, new PdfNumber(toUnicode.Length));
            toUnicodeStream.ObjectNumber = toUnicodeObjNum;
            toUnicodeStream.GenerationNumber = 0;
            objects.Add((toUnicodeObjNum, toUnicodeStream));

            // The glyphless program, and the map sending every CID to its one empty glyph.
            int fontFileObjNum = nextObjNum++;
            byte[] program = Core.IO.Filters.FlateEncodeFilter.Encode(GlyphlessProgram());
            var fontFile = new PdfStream { RawData = program };
            fontFile.Set(PdfName.Filter, PdfName.FlateDecode);
            fontFile.Set(PdfName.Length, new PdfNumber(program.Length));
            fontFile.ObjectNumber = fontFileObjNum;
            objects.Add((fontFileObjNum, fontFile));

            int cidToGidObjNum = nextObjNum++;
            var map = new byte[2 * 65536];
            for (int i = 1; i < map.Length; i += 2) map[i] = 1;
            byte[] encodedMap = Core.IO.Filters.FlateEncodeFilter.Encode(map);
            var cidToGid = new PdfStream { RawData = encodedMap };
            cidToGid.Set(PdfName.Filter, PdfName.FlateDecode);
            cidToGid.Set(PdfName.Length, new PdfNumber(encodedMap.Length));
            cidToGid.ObjectNumber = cidToGidObjNum;
            objects.Add((cidToGidObjNum, cidToGid));

            int descriptorObjNum = nextObjNum++;
            var descriptor = new PdfDictionary();
            descriptor.Set(PdfName.Type, PdfName.FontDescriptor);
            descriptor.Set(PdfName.FontName, new PdfName(BaseFontName));
            // Symbolic (bit 3), because the codes are code units rather than a standard encoding's
            // character codes. The metrics are nominal: nothing measures an invisible glyph.
            descriptor.Set(PdfName.Flags, new PdfNumber(4));
            descriptor.Set(PdfName.ItalicAngle, new PdfNumber(0));
            descriptor.Set(PdfName.Ascent, new PdfNumber(880));
            descriptor.Set(PdfName.Descent, new PdfNumber(-120));
            descriptor.Set(PdfName.CapHeight, new PdfNumber(700));
            descriptor.Set(PdfName.StemV, new PdfNumber(80));
            var bbox = new PdfArray();
            bbox.Add(new PdfNumber(0)); bbox.Add(new PdfNumber(-120));
            bbox.Add(new PdfNumber(1000)); bbox.Add(new PdfNumber(880));
            descriptor.Set(PdfName.FontBBox, bbox);
            descriptor.Set(PdfName.FontFile2, new PdfReference(fontFileObjNum, 0));
            descriptor.ObjectNumber = descriptorObjNum;
            descriptor.GenerationNumber = 0;
            objects.Add((descriptorObjNum, descriptor));

            int descendantObjNum = nextObjNum++;
            var descendant = CompositeFontDictionaries.DescendantCidFont(PdfName.CIDFontType2, BaseFontName,
                CompositeFontDictionaries.IdentityCidSystemInfo(), new PdfReference(descriptorObjNum, 0));
            descendant.Set(PdfName.DW, new PdfNumber(DefaultWidth));
            descendant.Set(PdfName.CIDToGIDMap, new PdfReference(cidToGidObjNum, 0));
            descendant.ObjectNumber = descendantObjNum;
            descendant.GenerationNumber = 0;
            objects.Add((descendantObjNum, descendant));

            int fontObjNum = nextObjNum++;
            var font = CompositeFontDictionaries.Type0(BaseFontName, PdfName.IdentityH,
                new PdfReference(descendantObjNum, 0));
            font.Set(PdfName.ToUnicode, new PdfReference(toUnicodeObjNum, 0));
            font.ObjectNumber = fontObjNum;
            font.GenerationNumber = 0;
            objects.Add((fontObjNum, font));

            return (fontObjNum, objects);
        }

        /// <summary>
        /// A TrueType program of two empty glyphs, each <see cref="DefaultWidth"/> wide in a
        /// 1000-unit em — the six tables an embedded TrueType program needs (§9.9), and no
        /// <c>cmap</c>: a CIDFontType2 reaches its glyphs through <c>/CIDToGIDMap</c>.
        /// </summary>
        internal static byte[] GlyphlessProgram()
        {
            const int glyphs = 2;
            var head = new byte[54];
            Be32(head, 0, 0x00010000); Be32(head, 12, 0x5F0F3CF5); Be16(head, 16, 0x000B);
            Be16(head, 18, 1000);                                     // unitsPerEm
            Be16(head, 38, unchecked((short)-120) & 0xFFFF); Be16(head, 40, 1000); Be16(head, 42, 880);   // yMin, xMax, yMax
            Be16(head, 50, 0);                                        // short loca offsets
            var hhea = new byte[36];
            Be32(hhea, 0, 0x00010000); Be16(hhea, 4, 880); Be16(hhea, 6, unchecked((short)-120) & 0xFFFF);
            Be16(hhea, 10, DefaultWidth); Be16(hhea, 34, glyphs);
            var maxp = new byte[32];
            Be32(maxp, 0, 0x00010000); Be16(maxp, 4, glyphs);
            var hmtx = new byte[glyphs * 4];
            for (int g = 0; g < glyphs; g++) Be16(hmtx, g * 4, DefaultWidth);
            var loca = new byte[(glyphs + 1) * 2];          // short offsets, every glyph empty
            return Core.Fonts.SfntBuilder.Build(Core.Fonts.SfntBuilder.VersionTrueType, new List<(string, byte[])>
            {
                ("glyf", Array.Empty<byte>()), ("head", head), ("hhea", hhea),
                ("hmtx", hmtx), ("loca", loca), ("maxp", maxp),
            });
        }

        private static void Be16(byte[] d, int o, int v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }
        private static void Be32(byte[] d, int o, uint v)
        {
            d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
        }

        /// <summary>
        /// The name this font reports. Not a real face and deliberately not the name of one: a
        /// reader that substituted for "Arial" here would draw invisible Arial, which is the same
        /// nothing, but a log line naming Arial would be a lie about what the file contains.
        /// </summary>
        private const string BaseFontName = "TetitulzOcrInvisible";
    }
}
