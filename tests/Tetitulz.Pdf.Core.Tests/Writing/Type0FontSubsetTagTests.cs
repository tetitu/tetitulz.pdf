// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing
{
    /// <summary>
    /// ISO 32000-1 §9.6.4 subset tagging in <see cref="Type0FontBuilder"/>.
    ///
    /// <para>
    /// The writer subsets a TrueType program to the glyphs actually drawn, but falls back to the
    /// whole program whenever <see cref="TrueTypeSubsetter"/> declines. Both outcomes are tested,
    /// because <b>both directions are defects</b>: an untagged subset claims a complete font it does
    /// not carry, and a tag on a whole program tells a consumer not to reuse a program it could.
    /// The two arms differ by <i>one table</i> in the fixture — dropping <c>loca</c> makes the
    /// subsetter give up — so a change that tags unconditionally goes red on the second arm and a
    /// change that tags nothing goes red on the first.
    /// </para>
    ///
    /// <para>
    /// The fonts are built here, byte by byte, rather than read from the host: a test that depended
    /// on an installed font would skip on a bare Linux runner, and the interesting inputs (a font
    /// with no <c>loca</c>, a name that already carries a tag) do not exist on any host anyway.
    /// </para>
    /// </summary>
    public class Type0FontSubsetTagTests
    {
        private const string FontName = "TestSubsetFont";

        // ----------------------------------------------------------------
        //  Fixture: a minimal but real sfnt
        // ----------------------------------------------------------------

        /// <summary>Glyphs in the synthetic font: .notdef plus three drawable ones.</summary>
        private const int GlyphCount = 4;

        /// <summary>
        /// Builds a TrueType font with just the tables the two readers in this path consult:
        /// <c>head</c>, <c>hhea</c>, <c>maxp</c>, <c>hmtx</c>, <c>loca</c>, <c>glyf</c> and
        /// <c>name</c>. No <c>cmap</c> — the composite writer takes glyph ids from its caller and
        /// never maps a character, so a cmap would be fixture that the code under test cannot read.
        /// </summary>
        /// <param name="postScriptName">Value of name id 6, which becomes /BaseFont.</param>
        /// <param name="includeLoca">
        /// False drops the <c>loca</c> entry from the table directory. <see cref="TrueTypeSubsetter"/>
        /// requires <c>glyf</c> AND <c>loca</c> and returns null without one, while
        /// <see cref="TrueTypeFontProgram.Parse"/> never looks at <c>loca</c> — so this is the
        /// smallest possible difference that flips "was it subsetted?" without changing anything
        /// else about the font.
        /// </param>
        /// <param name="asCff">
        /// True adds an empty <c>CFF </c> table, which is all
        /// <see cref="TrueTypeFontProgram.HasCff"/> tests for. That routes the writer down the
        /// CIDFontType0 / FontFile3 branch, which embeds the program whole.
        /// </param>
        /// <remarks>
        /// <c>internal</c> rather than private so <see cref="Type0FontGraphTests"/> can drive the
        /// same synthetic font. A second byte-by-byte sfnt in the test project would be a second
        /// set of advances and glyph counts for the assertions to disagree about.
        /// </remarks>
        internal static byte[] BuildFont(
            string postScriptName = FontName, bool includeLoca = true, bool asCff = false)
        {
            var tables = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["head"] = Head(),
                ["hhea"] = Hhea(),
                ["maxp"] = Maxp(),
                ["hmtx"] = Hmtx(),
                ["glyf"] = Glyf(),
                ["name"] = Name(postScriptName),
            };
            if (includeLoca) tables["loca"] = Loca();
            if (asCff) tables["CFF "] = new byte[4];

            return Sfnt(tables);
        }

        /// <summary>head, 54 bytes. Offset 18 is unitsPerEm and offset 50 is indexToLocFormat.</summary>
        private static byte[] Head()
        {
            var b = new byte[54];
            WriteU32(b, 0, 0x00010000);          // version 1.0
            WriteU32(b, 12, 0x5F0F3CF5);         // magicNumber
            WriteU16(b, 18, 1000);               // unitsPerEm
            WriteI16(b, 36, 0);                  // xMin
            WriteI16(b, 38, -200);               // yMin
            WriteI16(b, 40, 1000);               // xMax
            WriteI16(b, 42, 800);                // yMax
            WriteI16(b, 50, 0);                  // indexToLocFormat: short offsets
            return b;
        }

        /// <summary>
        /// hhea, padded to 40 bytes with numberOfHMetrics written <b>twice</b>.
        ///
        /// <para>The two readers disagree about where that field is: <c>TrueTypeSubsetter</c> reads
        /// offset 34, which is where OpenType puts it, and <c>TrueTypeFontProgram.Parse</c> skips 30
        /// bytes past the descender and reads offset 38. Stating the same value in both places keeps
        /// this fixture from silently depending on which of them is right — the offset disagreement
        /// is a real defect in Core, but it is not the one under test here and fixing it belongs in
        /// its own change with its own assertion.</para>
        /// </summary>
        private static byte[] Hhea()
        {
            var b = new byte[40];
            WriteU32(b, 0, 0x00010000);          // version 1.0
            WriteI16(b, 4, 800);                 // ascender
            WriteI16(b, 6, -200);                // descender
            WriteU16(b, 34, GlyphCount);         // numberOfHMetrics (OpenType)
            WriteU16(b, 38, GlyphCount);         // numberOfHMetrics (where Parse looks)
            return b;
        }

        /// <summary>maxp version 0.5 — the six bytes the subsetter and the parser both read.</summary>
        private static byte[] Maxp()
        {
            var b = new byte[6];
            WriteU32(b, 0, 0x00005000);
            WriteU16(b, 4, GlyphCount);
            return b;
        }

        private static byte[] Hmtx()
        {
            var b = new byte[GlyphCount * 4];
            for (int g = 0; g < GlyphCount; g++)
            {
                WriteU16(b, g * 4, (ushort)(500 + g));   // advanceWidth
                WriteI16(b, g * 4 + 2, 10);              // leftSideBearing
            }
            return b;
        }

        /// <summary>Glyph 0 is empty (.notdef, zero-length per loca); glyphs 1-3 are 24-byte
        /// single-contour outlines, so the subsetter has real records to copy and remap.</summary>
        private static byte[] Glyf()
        {
            var ms = new MemoryStream();
            for (int g = 1; g < GlyphCount; g++)
            {
                var glyph = new byte[24];
                WriteI16(glyph, 0, 1);           // numberOfContours: simple glyph
                WriteI16(glyph, 2, 0);           // xMin
                WriteI16(glyph, 4, 0);           // yMin
                WriteI16(glyph, 6, 100);         // xMax
                WriteI16(glyph, 8, 100);         // yMax
                WriteU16(glyph, 10, 2);          // endPtsOfContours[0]: three points
                WriteU16(glyph, 12, 0);          // instructionLength
                glyph[14] = glyph[15] = glyph[16] = 0x37;   // on-curve, short + positive x and y
                glyph[17] = 10; glyph[18] = 20; glyph[19] = 30;   // x deltas
                glyph[20] = 10; glyph[21] = 20; glyph[22] = 30;   // y deltas
                ms.Write(glyph, 0, glyph.Length);
            }
            return ms.ToArray();
        }

        /// <summary>Short-format loca: offsets divided by two, so glyph 0 is empty and each of the
        /// rest occupies 24 bytes.</summary>
        private static byte[] Loca()
        {
            var b = new byte[(GlyphCount + 1) * 2];
            int offset = 0;
            WriteU16(b, 0, 0);                   // glyph 0 starts at 0
            WriteU16(b, 2, 0);                   // ... and is zero-length
            for (int g = 1; g < GlyphCount; g++)
            {
                offset += 24;
                WriteU16(b, (g + 1) * 2, (ushort)(offset / 2));
            }
            return b;
        }

        /// <summary>A name table carrying only name id 6 (PostScript name) on platform 1
        /// (Macintosh / ASCII), which is what <c>TrueTypeFontProgram.ParseName</c> turns into
        /// /BaseFont.</summary>
        private static byte[] Name(string postScriptName)
        {
            byte[] value = Encoding.ASCII.GetBytes(postScriptName);
            var b = new byte[6 + 12 + value.Length];
            WriteU16(b, 0, 0);                   // format 0
            WriteU16(b, 2, 1);                   // count
            WriteU16(b, 4, 18);                  // stringOffset: past the single record
            WriteU16(b, 6, 1);                   // platformID: Macintosh
            WriteU16(b, 8, 0);                   // encodingID: Roman
            WriteU16(b, 10, 0);                  // languageID
            WriteU16(b, 12, 6);                  // nameID: PostScript name
            WriteU16(b, 14, (ushort)value.Length);
            WriteU16(b, 16, 0);                  // offset into the string storage
            Array.Copy(value, 0, b, 18, value.Length);
            return b;
        }

        /// <summary>Wraps tables in an sfnt container. Checksums are left zero: neither reader in
        /// this path verifies them, and a fixture that computed them would be asserting about the
        /// fixture rather than about the writer.</summary>
        private static byte[] Sfnt(Dictionary<string, byte[]> tables)
        {
            var tags = tables.Keys.OrderBy(t => t, StringComparer.Ordinal).ToList();
            int directory = 12 + tags.Count * 16;
            var font = new byte[directory + tags.Sum(t => (tables[t].Length + 3) & ~3)];

            WriteU32(font, 0, 0x00010000);
            WriteU16(font, 4, (ushort)tags.Count);

            int offset = directory, record = 12;
            foreach (string tag in tags)
            {
                for (int i = 0; i < 4; i++) font[record + i] = (byte)tag[i];
                WriteU32(font, record + 8, (uint)offset);
                WriteU32(font, record + 12, (uint)tables[tag].Length);
                Array.Copy(tables[tag], 0, font, offset, tables[tag].Length);
                offset += (tables[tag].Length + 3) & ~3;
                record += 16;
            }
            return font;
        }

        private static void WriteU16(byte[] b, int at, int v)
        {
            b[at] = (byte)(v >> 8); b[at + 1] = (byte)v;
        }

        private static void WriteI16(byte[] b, int at, int v) => WriteU16(b, at, v & 0xFFFF);

        private static void WriteU32(byte[] b, int at, uint v)
        {
            b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16);
            b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v;
        }

        // ----------------------------------------------------------------
        //  Running the writer
        // ----------------------------------------------------------------

        /// <summary>The three names §9.8.1 requires to agree, plus the /CIDToGIDMap that says
        /// whether the program really was subsetted.</summary>
        private readonly struct Emitted
        {
            internal Emitted(string type0BaseFont, string cidBaseFont, string fontName, PdfObject cidToGidMap)
            {
                Type0BaseFont = type0BaseFont;
                CidBaseFont = cidBaseFont;
                FontName = fontName;
                CidToGidMap = cidToGidMap;
            }

            internal string Type0BaseFont { get; }
            internal string CidBaseFont { get; }
            internal string FontName { get; }
            internal PdfObject CidToGidMap { get; }
        }

        /// <summary>Runs the writer over the given font and pulls out the four values under test.
        /// It asserts nothing — the assertions belong in the tests, where they can be read next to
        /// the claim the method's name makes.</summary>
        private static Emitted Emit(byte[] fontBytes, IReadOnlyDictionary<ushort, int> used)
        {
            var program = TrueTypeFontProgram.Parse(fontBytes);
            if (program == null)
                throw new InvalidOperationException("the synthetic font did not parse; fix the fixture");

            int next = 1;
            var (type0Num, objects) = Type0FontBuilder.BuildObjects(ref next, program, used);
            var byNumber = objects.ToDictionary(o => o.objNum, o => o.obj);

            var type0 = (PdfDictionary)byNumber[type0Num];
            var descendants = (PdfArray)type0.Get(PdfName.DescendantFonts);
            var cidFont = (PdfDictionary)byNumber[((PdfReference)descendants[0]).ReferenceObjectNumber];
            var descriptor = (PdfDictionary)byNumber[
                ((PdfReference)cidFont.Get(PdfName.FontDescriptor)).ReferenceObjectNumber];

            return new Emitted(
                ((PdfName)type0.Get(PdfName.BaseFont)).Value,
                ((PdfName)cidFont.Get(PdfName.BaseFont)).Value,
                ((PdfName)descriptor.Get(PdfName.FontName)).Value,
                cidFont.Get(PdfName.CIDToGIDMap));
        }

        private static Dictionary<ushort, int> Used(params ushort[] gids)
        {
            var map = new Dictionary<ushort, int>();
            foreach (ushort gid in gids) map[gid] = 'A' + gid;
            return map;
        }

        // ----------------------------------------------------------------
        //  Tests
        // ----------------------------------------------------------------

        [Fact]
        public void SubsettedFont_TagsAllThreeNamesIdentically()
        {
            var emitted = Emit(BuildFont(), Used(1, 3));

            // §9.6.4: six uppercase letters, a '+', then the original name.
            Assert.Equal(FontName.Length + 7, emitted.Type0BaseFont.Length);
            Assert.Equal('+', emitted.Type0BaseFont[6]);
            for (int i = 0; i < 6; i++)
                Assert.InRange(emitted.Type0BaseFont[i], 'A', 'Z');
            Assert.Equal(FontName, emitted.Type0BaseFont.Substring(7));

            // §9.8.1: the descriptor's /FontName is the same name as the /BaseFont — tagging the
            // outer dictionary alone produces a file strict validators reject.
            Assert.Equal(emitted.Type0BaseFont, emitted.CidBaseFont);
            Assert.Equal(emitted.Type0BaseFont, emitted.FontName);

            // The tag is only honest if a subset really was produced. A /CIDToGIDMap stream is
            // written exactly when the subsetter remapped glyph ids, so this pins the fixture to
            // the branch the assertions above are about.
            Assert.IsType<PdfReference>(emitted.CidToGidMap);
        }

        [Fact]
        public void UnsubsettableFont_LeavesAllThreeNamesUntagged()
        {
            // Same font, minus the loca table: the subsetter declines and the writer embeds the
            // whole program. A tag here would claim a partial glyph set the file does not carry.
            var emitted = Emit(BuildFont(includeLoca: false), Used(1, 3));

            Assert.Equal(FontName, emitted.Type0BaseFont);
            Assert.Equal(FontName, emitted.CidBaseFont);
            Assert.Equal(FontName, emitted.FontName);
            Assert.Equal("Identity", ((PdfName)emitted.CidToGidMap).Value);
        }

        [Fact]
        public void CffFont_LeavesAllThreeNamesUntagged()
        {
            // CFF/OpenType goes down the CIDFontType0 + FontFile3 branch, which has no subsetter at
            // all and embeds the program whole.
            var emitted = Emit(BuildFont(asCff: true), Used(1, 3));

            Assert.Equal(FontName, emitted.Type0BaseFont);
            Assert.Equal(FontName, emitted.CidBaseFont);
            Assert.Equal(FontName, emitted.FontName);
            Assert.Null(emitted.CidToGidMap);
        }

        [Fact]
        public void SubsetTag_IsTheSameWhateverOrderTheGlyphIdsArriveIn()
        {
            // The writer takes the glyph ids from a dictionary's key collection, and Dictionary
            // enumerates in insertion order — so these two describe the same subset and would hash
            // differently if the tag were computed over the ids as they arrive. Byte-identical
            // output is what makes template filling idempotent.
            byte[] font = BuildFont();

            string ascending = Emit(font, Used(1, 2, 3)).Type0BaseFont;
            string descending = Emit(font, Used(3, 2, 1)).Type0BaseFont;

            Assert.Equal(ascending, descending);
        }

        [Fact]
        public void SubsetTag_DiffersWhenTheGlyphSetDiffers()
        {
            // Two subsets of one font must not share a tag: a consumer keying on the tagged name
            // would then treat one glyph set as the other.
            byte[] font = BuildFont();

            string two = Emit(font, Used(1, 2)).Type0BaseFont;
            string three = Emit(font, Used(1, 2, 3)).Type0BaseFont;

            Assert.NotEqual(two, three);
            Assert.Equal(FontName, two.Substring(7));
            Assert.Equal(FontName, three.Substring(7));
        }

        [Fact]
        public void SubsettedFont_ReplacesATagTheProgramAlreadyCarried()
        {
            // A font program's own PostScript name can already be tagged — it may be a subset some
            // earlier tool produced — and the writer's name sanitiser keeps '+', so prefixing
            // blindly would yield "GHIJKL+XYZABC+Prefixed", which no reader matches back to the
            // family.
            var emitted = Emit(BuildFont(postScriptName: "XYZABC+Prefixed"), Used(1, 3));

            Assert.Equal("Prefixed", emitted.Type0BaseFont.Substring(7));
            Assert.Equal(1, emitted.Type0BaseFont.Count(c => c == '+'));
            Assert.Equal(emitted.Type0BaseFont, emitted.FontName);

            // The three assertions above all held with subset tagging deleted outright — "XYZABC+"
            // is itself a well-formed tag, so the incoming name satisfies every shape check the
            // outgoing one does. What actually distinguishes the two is that the tag OUR subset
            // gets is the one for this glyph set, computed over the untagged name: the same
            // program named "Prefixed" from the start must come out identically labelled.
            Assert.NotEqual("XYZABC", emitted.Type0BaseFont.Substring(0, 6));
            Assert.Equal(Emit(BuildFont(postScriptName: "Prefixed"), Used(1, 3)).Type0BaseFont,
                         emitted.Type0BaseFont);
        }
    }
}
