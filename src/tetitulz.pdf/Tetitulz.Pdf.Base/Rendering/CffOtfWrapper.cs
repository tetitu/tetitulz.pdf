// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Wraps raw CFF (Type1C / FontFile3) bytes in a minimal OpenType/CFF (OTF)
    /// SFNT envelope so that SkiaSharp can load the font and apply its
    /// native Type 2 hint engine instead of our path-only fallback.
    ///
    /// The resulting bytes are a valid .otf file with 9 required tables:
    ///   CFF , OS/2, cmap, head, hhea, hmtx, maxp, name, post
    ///
    /// We use GID-based glyph rendering (SKCanvas.DrawGlyphs with explicit glyph
    /// indices) so the cmap only needs to be syntactically valid — it maps nothing
    /// meaningful. CharCode→GID translation continues to come from CffGlyphSource.
    /// </summary>
    internal static class CffOtfWrapper
    {
        /// <summary>
        /// Attempts to build an OTF envelope around <paramref name="rawCff"/>.
        /// Returns <c>null</c> on any failure (corrupt CFF, zero glyphs, etc.).
        /// </summary>
        public static byte[] TryBuild(byte[] rawCff, CffFont font) => TryBuild(rawCff, font, glyphIndexedCmap: false);

        /// <summary>
        /// The first code point of the glyph-indexed <c>cmap</c>: glyph <i>g</i> is reached as
        /// U+F0000 + <i>g</i>, in Plane 15's Private Use Area, whose 65 534 code points cover every
        /// glyph a CFF1 font can hold but one.
        /// </summary>
        public const int GlyphIndexedCmapBase = 0xF0000;

        /// <summary>The last glyph index the glyph-indexed <c>cmap</c> can address (U+FFFFD).</summary>
        public const int GlyphIndexedCmapMaxGlyph = 0xFFFFD - GlyphIndexedCmapBase;

        /// <summary>
        /// As <see cref="TryBuild(byte[], CffFont)"/>, with the <c>cmap</c> chosen by the consumer:
        /// the stub for one that draws by glyph index (SkiaSharp), or — when
        /// <paramref name="glyphIndexedCmap"/> is set — a <c>(3, 10)</c> format-12 table mapping
        /// U+F0000 + <i>g</i> to glyph <i>g</i> for every glyph, for a consumer that selects glyphs
        /// through the <c>cmap</c> and nothing else: a browser loading the font from an SVG's
        /// <c>@font-face</c>, which is handed the text as those code points.
        /// </summary>
        /// <remarks>
        /// A Private Use mapping rather than the font's own encoding, because the font's encoding is
        /// not the document's: the PDF's <c>/Encoding</c> or CMap decides which glyph a code draws,
        /// and a bare CFF's built-in encoding — or none, for a CID-keyed program — says nothing about
        /// it. Addressing glyphs by index is the one mapping both sides agree on, and the PDF's
        /// text stays available through the invisible overlay the writer emits beside the run.
        /// </remarks>
        public static byte[] TryBuild(byte[] rawCff, CffFont font, bool glyphIndexedCmap)
        {
            if (rawCff == null || rawCff.Length < 4 || font == null || font.Charstrings.Count == 0)
                return null;
            try { return Build(rawCff, font, glyphIndexedCmap); }
            catch { return null; }
        }

        // ----------------------------------------------------------------
        //  Core builder
        // ----------------------------------------------------------------

        private static byte[] Build(byte[] rawCff, CffFont font, bool glyphIndexedCmap)
        {
            int numGlyphs = font.Charstrings.Count;

            // Build each required OpenType table.
            byte[] cffTable  = rawCff;
            byte[] os2Table  = BuildOS2();
            byte[] cmapTable = glyphIndexedCmap ? BuildGlyphIndexedCmap(numGlyphs) : BuildCmap();
            byte[] headTable = BuildHead();
            byte[] hheaTable = BuildHhea(numGlyphs);
            byte[] hmtxTable = BuildHmtx(font);
            byte[] maxpTable = BuildMaxp(numGlyphs);
            byte[] nameTable = BuildName();
            byte[] postTable = BuildPost();

            // Tag-sorted table list (OpenType requires alphabetical tag order).
            var tables = new (string tag, byte[] data)[]
            {
                ("CFF ", cffTable),
                ("OS/2", os2Table),
                ("cmap", cmapTable),
                ("head", headTable),
                ("hhea", hheaTable),
                ("hmtx", hmtxTable),
                ("maxp", maxpTable),
                ("name", nameTable),
                ("post", postTable),
            };

            // Layout, checksums and the head fix-up are Core.Fonts.SfntBuilder's job. This method
            // owns what is CFF-specific — which tables to synthesise and what goes in them — and
            // nothing about how an sfnt container is laid out. That split is why CFF subsetting has
            // an emitter to build on: SfntBuilder takes the sfnt version as a parameter, where the
            // two TrueType-only assemblers hardcode or refuse it.
            //
            // The sfnt rules: tag order (already ascending here), padding, same "checksum over padded bytes, length field
            // actual", 0xB1B0AFBA - sum. Pinned by CffOtfWrapperSfntTests.
            return Tetitulz.Pdf.Core.Fonts.SfntBuilder.Build(
                Tetitulz.Pdf.Core.Fonts.SfntBuilder.VersionOpenTypeCff, tables);
        }

        // ----------------------------------------------------------------
        //  Table builders
        // ----------------------------------------------------------------

        private static byte[] BuildHead()
        {
            var b = new byte[54];
            W32(b, 0,  0x00010000);  // version 1.0
            W32(b, 4,  0x00010000);  // fontRevision
            // offset 8: checkSumAdjustment — filled by Build()
            W32(b, 12, 0x5F0F3CF5); // magicNumber
            W16(b, 16, 0x000B);      // flags: baseline at y=0, lsb at x=0, ppem integer
            W16(b, 18, 1000);        // unitsPerEm (standard for CFF)
            // created / modified (offsets 20–35): 0 = Unix epoch
            // xMin,yMin,xMax,yMax (offsets 36–43): 0 (unknown bounding box)
            // macStyle (offset 44): 0
            W16(b, 46, 8);           // lowestRecPPEM
            W16s(b, 48, 2);          // fontDirectionHint = 2 (L-to-R)
            // indexToLocFormat (offset 50): 0 (not used for CFF; glyf absent)
            // glyphDataFormat (offset 52): 0
            return b;
        }

        private static byte[] BuildHhea(int numGlyphs)
        {
            var b = new byte[36];
            W32(b, 0,  0x00010000);  // version 1.0
            W16s(b, 4,  800);        // Ascender
            W16s(b, 6,  -200);       // Descender
            W16s(b, 8,  0);          // LineGap
            W16(b, 10,  1000);       // advanceWidthMax
            // minLSB, minRSB, xMaxExtent: 0
            W16s(b, 18, 1);          // caretSlopeRise
            // caretSlopeRun, caretOffset, reserved×4: 0
            // metricDataFormat (offset 32): 0
            W16(b, 34, (ushort)numGlyphs); // numOfLongHorMetrics
            return b;
        }

        private static byte[] BuildHmtx(CffFont font)
        {
            int ng = font.Charstrings.Count;
            var b  = new byte[ng * 4]; // 4 bytes per glyph: advanceWidth (u16) + lsb (s16)
            // Each glyph's own advance, read from its charstring: FreeType and CoreText take a CFF
            // glyph's advance from hmtx, so a table of defaultWidthX gives every glyph that sets its
            // own width the wrong one there (DirectWrite reads the charstring, which hides it on Windows).
            // The head table's unitsPerEm is 1000, the units TryGetAdvance answers in.
            for (int i = 0; i < ng; i++)
            {
                double w = font.TryGetAdvance(i, out double advance) ? advance : font.DefaultWidthX;
                ushort aw = (ushort)Math.Min(Math.Max(SaturatingCast.ToInt(Math.Round(w)), 0), 65535);
                W16(b, i * 4,     aw);
                // lsb (offset i*4+2): 0
            }
            return b;
        }

        private static byte[] BuildMaxp(int numGlyphs)
        {
            var b = new byte[6];
            W32(b, 0, 0x00005000);           // version 0.5 (CFF fonts must use 0.5)
            W16(b, 4, (ushort)numGlyphs);
            return b;
        }

        private static byte[] BuildOS2()
        {
            var b = new byte[78];
            // version (0), xAvgCharWidth (0)
            W16(b, 4,  400);    // usWeightClass = Normal
            W16(b, 6,  5);      // usWidthClass = Medium
            // fsType = 0 (no restrictions), subscript/superscript/strikeout = 0
            // sFamilyClass = 0, panose[10] = 0, ulUnicodeRange 1-4 = 0
            b[58] = (byte)'U'; b[59] = (byte)'K'; b[60] = (byte)'W'; b[61] = (byte)'N'; // achVendID
            W16(b, 62, 0x0040); // fsSelection = REGULAR
            W16(b, 64, 0x0020); // usFirstCharIndex = SPACE
            W16(b, 66, 0x00FF); // usLastCharIndex
            W16s(b, 68, 800);   // sTypoAscender
            W16s(b, 70, -200);  // sTypoDescender
            // sTypoLineGap = 0
            W16(b, 74, 1000);   // usWinAscent
            W16(b, 76, 200);    // usWinDescent (positive)
            return b;
        }

        private static byte[] BuildCmap()
        {
            // Format-0 subtable mapping all 256 characters to GID 0 (.notdef).
            // We bypass this map and render by GID directly, so correctness is irrelevant.
            const int subtableLen = 6 + 256; // format(2)+length(2)+language(2)+glyphIdArray[256]
            var sub = new byte[subtableLen];
            W16(sub, 0, 0);                        // format 0
            W16(sub, 2, (ushort)subtableLen);      // length
            // language = 0; glyphIdArray = all 0

            // cmap table = header(4) + 1 EncodingRecord(8) + subtable
            const int recOffset = 12; // 4 header + 8 record
            var b = new byte[recOffset + subtableLen];
            // version = 0
            W16(b, 2, 1);         // numTables
            W16(b, 4, 3);         // platformID = Windows
            W16(b, 6, 1);         // encodingID = Unicode BMP
            W32(b, 8, recOffset); // offset to subtable
            Array.Copy(sub, 0, b, recOffset, sub.Length);
            return b;
        }

        /// <summary>
        /// A <c>(3, 10)</c> format-12 subtable with one sequential group: U+F0000 + <i>g</i> → glyph
        /// <i>g</i> for <i>g</i> in 0 … min(numGlyphs, 65 534) − 1. One group, because the mapping is
        /// the identity shifted into the Private Use Area; the table is 28 bytes whatever the glyph count.
        /// </summary>
        private static byte[] BuildGlyphIndexedCmap(int numGlyphs)
        {
            int mapped = Math.Min(numGlyphs, GlyphIndexedCmapMaxGlyph + 1);
            const int subtableLen = 16 + 12;   // format-12 header + one SequentialMapGroup
            var sub = new byte[subtableLen];
            W16(sub, 0, 12);                                      // format
            // reserved (2) = 0
            W32(sub, 4, subtableLen);                             // length
            // language (4) = 0
            W32(sub, 12, 1);                                      // numGroups
            W32(sub, 16, (uint)GlyphIndexedCmapBase);             // startCharCode
            W32(sub, 20, (uint)(GlyphIndexedCmapBase + mapped - 1)); // endCharCode
            W32(sub, 24, 0);                                      // startGlyphID

            const int recOffset = 12;   // 4 header + 8 record
            var b = new byte[recOffset + subtableLen];
            // version = 0
            W16(b, 2, 1);          // numTables
            W16(b, 4, 3);          // platformID = Windows
            W16(b, 6, 10);         // encodingID = Unicode full repertoire
            W32(b, 8, recOffset);  // offset to subtable
            Array.Copy(sub, 0, b, recOffset, sub.Length);
            return b;
        }

        private static byte[] BuildName()
        {
            // One Mac-Roman record for nameID=1 (Family Name).
            byte[] str = System.Text.Encoding.ASCII.GetBytes("Embedded Font");
            int hdrSize = 6 + 12; // header(6) + 1 NameRecord(12)
            var b = new byte[hdrSize + str.Length];
            // format = 0
            W16(b, 2, 1);                     // count
            W16(b, 4, (ushort)hdrSize);       // stringOffset
            W16(b, 6,  1);                    // platformID = Mac
            W16(b, 8,  0);                    // encodingID = Mac Roman
            W16(b, 10, 0);                    // languageID
            W16(b, 12, 1);                    // nameID = Family
            W16(b, 14, (ushort)str.Length);
            W16(b, 16, 0);                    // offset within string storage
            Array.Copy(str, 0, b, hdrSize, str.Length);
            return b;
        }

        private static byte[] BuildPost()
        {
            var b = new byte[32];
            W32(b, 0, 0x00030000); // format 3.0 — no glyph-name data
            // italicAngle = 0
            W16s(b, 8,  -100);     // underlinePosition
            W16s(b, 10,  50);      // underlineThickness
            // isFixedPitch, minMemType42, maxMemType42, minMemType1, maxMemType1 = 0
            return b;
        }

        // ----------------------------------------------------------------
        //  Binary helpers (big-endian)
        // ----------------------------------------------------------------

        private static void W32(byte[] b, int off, uint v)
        {
            b[off    ] = (byte)(v >> 24);
            b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >>  8);
            b[off + 3] = (byte) v;
        }

        private static void W16(byte[] b, int off, ushort v)
        {
            b[off    ] = (byte)(v >> 8);
            b[off + 1] = (byte) v;
        }

        private static void W16s(byte[] b, int off, short v) => W16(b, off, (ushort)v);
    }
}
