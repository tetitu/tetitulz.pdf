// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// <see cref="EmbeddedGlyphSets"/>: what an embedded program holds, for the subset rules that
    /// compare a <c>/CharSet</c> or <c>/CIDSet</c> with it (ISO 32000-1 Tables 122 and 124). The
    /// Conformance plugin's <c>FontSubsetSetsTests</c> drive it through whole documents; these pin its
    /// own readings, and that a program it cannot read is said so rather than answered.
    /// </summary>
    public class EmbeddedGlyphSetsTests
    {
        private static PdfObject Same(PdfObject o) => o;

        [Fact]
        public void CidSetCids_ReadsTheHighOrderBitOfEachByteFirst()
        {
            Assert.Equal(new[] { 0, 15, 17 }, EmbeddedGlyphSets.CidSetCids(Stream(new byte[] { 0x80, 0x01, 0x40 })).OrderBy(c => c));
            Assert.Empty(EmbeddedGlyphSets.CidSetCids(Stream(new byte[3])));
        }

        /// <summary>A <c>/CIDSet</c> longer than 65 536 bits names no CID past 65 535.</summary>
        [Fact]
        public void CidSetCids_AnOverlongSet_NamesNoCidPast65535()
        {
            var bits = Enumerable.Repeat((byte)0xFF, 8192 + 100).ToArray();
            var cids = EmbeddedGlyphSets.CidSetCids(Stream(bits));
            Assert.Equal(65536, cids.Count);
            Assert.Equal(65535, cids.Max());
        }

        [Fact]
        public void CharSetNames_ReadsNamesWithOrWithoutWhitespaceBetween()
            => Assert.Equal(new[] { "A", "B", "C", "space" },
                EmbeddedGlyphSets.CharSetNames(new PdfString(Encoding.ASCII.GetBytes("/A /B\n/C/space/"))).OrderBy(n => n, StringComparer.Ordinal));

        [Fact]
        public void GlyphNames_ACffProgramsCharset()
        {
            var descriptor = Descriptor(PdfName.FontFile3, Stream(MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("AB"))));
            var names = EmbeddedGlyphSets.GlyphNames(descriptor, Same, out string unreadable);
            Assert.Null(unreadable);
            Assert.Equal(new[] { ".notdef", "A", "B" }, names.OrderBy(n => n, StringComparer.Ordinal));
        }

        [Fact]
        public void GlyphNames_AType1ProgramsCharStrings_AndLengthsThatDoNotDescribeIt()
        {
            var (program, length1, length2) = MinimalType1.Build(new[]
            {
                (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(500)),
            });
            var names = EmbeddedGlyphSets.GlyphNames(Descriptor(PdfName.FontFile, Type1Stream(program, length1, length2)), Same, out string unreadable);
            Assert.Null(unreadable);
            Assert.Equal(new[] { ".notdef", "A" }, names.OrderBy(n => n, StringComparer.Ordinal));

            Assert.Null(EmbeddedGlyphSets.GlyphNames(Descriptor(PdfName.FontFile, Type1Stream(program, length1, program.Length)), Same, out unreadable));
            Assert.Contains("/Length1 and /Length2 do not describe the program", unreadable, StringComparison.Ordinal);
        }

        [Fact]
        public void Cids_ACidKeyedCffsCharset()
        {
            var cidFont = CidFont(PdfName.FontFile3, Stream(MinimalOpenTypeCff.BuildCidKeyed(new[] { 0, 3, 7 })), null);
            Assert.Equal(new[] { 0, 3, 7 }, EmbeddedGlyphSets.Cids(cidFont, Same, out _).OrderBy(c => c));
        }

        /// <summary>
        /// §9.6.6.1, Table 114: an embedded font's implicit base is its program's built-in encoding, so a
        /// code an encoding dictionary without <c>/BaseEncoding</c> leaves unnamed takes the program's name,
        /// and <c>/Differences</c> win where they speak; a named encoding is its table alone.
        /// </summary>
        [Fact]
        public void GlyphNamesByCode_TheDictionaryFirst_ThenTheProgramsBuiltInEncodingForTheImplicitBase()
        {
            var (program, length1, length2) = MinimalType1.Build(
                new[] { (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(500)), ("B", MinimalType1.AdvanceOnly(500)) },
                encoding: new System.Collections.Generic.Dictionary<int, string> { [65] = "A", [66] = "B", [5] = "A" });

            PdfDictionary FontWith(PdfObject encoding)
            {
                var font = new PdfDictionary();
                font.Set(PdfName.FontDescriptor, Descriptor(PdfName.FontFile, Type1Stream(program, length1, length2)));
                if (encoding != null) font.Set(PdfName.Encoding, encoding);
                return font;
            }

            var differences = new PdfDictionary();
            differences.Set(PdfName.Differences, new PdfArray { new PdfNumber(65), new PdfName("B") });
            var overridden = EmbeddedGlyphSets.GlyphNamesByCode(FontWith(differences), Same, out _);
            Assert.Equal("B", overridden[65]);
            Assert.Equal("B", overridden[66]);

            var builtIn = EmbeddedGlyphSets.GlyphNamesByCode(FontWith(null), Same, out _);
            Assert.Equal("A", builtIn[65]);
            Assert.Equal("B", builtIn[66]);

            var named = EmbeddedGlyphSets.GlyphNamesByCode(FontWith(new PdfName("WinAnsiEncoding")), Same, out _);
            Assert.Equal("A", named[65]);
            Assert.Equal("quotesingle", named[39]);   // WinAnsi's, which the program's encoding does not hold
            Assert.False(named.ContainsKey(5));       // WinAnsi leaves 5 unnamed, and a named encoding is its table alone
            Assert.Equal("A", builtIn[5]);             // the potency: the program's encoding does name it
        }

        /// <summary>A CFF that is not CID-keyed, under a CIDFont: each glyph's CID is its index.</summary>
        [Fact]
        public void Cids_ACffThatIsNotCidKeyed_HoldsEveryGlyphIndex()
        {
            var cidFont = CidFont(PdfName.FontFile3, Stream(MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("AB"))), null);
            Assert.Equal(new[] { 0, 1, 2 }, EmbeddedGlyphSets.Cids(cidFont, Same, out _).OrderBy(c => c));
        }

        /// <summary>
        /// A Type 1 program cut short inside its <c>/CharStrings</c> — the lengths describing what is
        /// there — has not said which glyphs it defines.
        /// </summary>
        [Fact]
        public void GlyphNames_AType1ProgramCutShort_IsUnreadable()
        {
            var (program, length1, length2) = MinimalType1.Build(new[]
            {
                (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(500)), ("B", MinimalType1.AdvanceOnly(500)),
            });
            int kept = length2 / 2;
            byte[] cut = program.Take(length1 + kept).ToArray();
            Assert.Null(EmbeddedGlyphSets.GlyphNames(Descriptor(PdfName.FontFile, Type1Stream(cut, length1, kept)), Same, out string unreadable));
            Assert.Contains("could not be read in full", unreadable, StringComparison.Ordinal);
        }

        /// <summary>
        /// A Type 1 program that parses, but whose <c>/CharStrings</c> holds an entry the scan had to
        /// skip, has not said every glyph it defines: the names it did read are not the answer.
        /// </summary>
        [Fact]
        public void GlyphNames_AType1ProgramWithASkippedCharStringsEntry_IsUnreadable()
        {
            var glyphs = new[] { (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(500)) };
            var (clean, cleanLength1, cleanLength2) = MinimalType1.Build(glyphs);
            Assert.NotNull(EmbeddedGlyphSets.GlyphNames(Descriptor(PdfName.FontFile, Type1Stream(clean, cleanLength1, cleanLength2)), Same, out _));

            var (program, length1, length2) = MinimalType1.Build(glyphs, extraCharStringsText: "/B notALength RD ND\n");
            Assert.Null(EmbeddedGlyphSets.GlyphNames(Descriptor(PdfName.FontFile, Type1Stream(program, length1, length2)), Same, out string unreadable));
            Assert.Contains("could not be read in full", unreadable, StringComparison.Ordinal);
        }

        /// <summary>A <c>maxp</c> that claims more glyphs than <c>loca</c> has entries for is refused before <c>loca</c> is read.</summary>
        [Fact]
        public void Cids_AMaxpClaimingMoreGlyphsThanLocaHolds_IsUnreadable()
        {
            byte[] font = MinimalTrueType.Build("AB");
            int maxp = TableOffset(font, "maxp");
            font[maxp + 4] = 0x03;
            font[maxp + 5] = 0xE8;   // 1000 glyphs
            Assert.Null(EmbeddedGlyphSets.Cids(CidFont(PdfName.FontFile2, Stream(font), null), Same, out string unreadable));
            Assert.Contains("loca table is shorter than maxp's 1000 glyphs", unreadable, StringComparison.Ordinal);
        }

        /// <summary>
        /// A TrueType CIDFont holds the CIDs whose glyph has outline data: <c>.notdef</c>, which
        /// <see cref="MinimalTrueType.Build(string)"/> leaves empty, is not one of them.
        /// </summary>
        [Fact]
        public void Cids_ATrueTypeProgramsOutlinedGlyphs_ThroughTheCidToGidMap()
        {
            byte[] font = MinimalTrueType.Build("AB");
            Assert.Equal(new[] { 1, 2 }, EmbeddedGlyphSets.Cids(CidFont(PdfName.FontFile2, Stream(font), null), Same, out _).OrderBy(c => c));
            Assert.Equal(new[] { 1, 2 }, EmbeddedGlyphSets.Cids(CidFont(PdfName.FontFile2, Stream(font), new PdfName("Identity")), Same, out _).OrderBy(c => c));
            var map = Stream(new byte[] { 0, 0, 0, 2, 0, 0, 0, 1 });   // CID 1 → glyph 2, CID 3 → glyph 1
            Assert.Equal(new[] { 1, 3 }, EmbeddedGlyphSets.Cids(CidFont(PdfName.FontFile2, Stream(font), map), Same, out _).OrderBy(c => c));

            Assert.Null(EmbeddedGlyphSets.Cids(CidFont(PdfName.FontFile2, Stream(font), new PdfName("Custom")), Same, out string unreadable));
            Assert.Contains("neither /Identity nor a stream", unreadable, StringComparison.Ordinal);
        }

        /// <summary>
        /// A CID is the file's: an embedded CMap's <c>cidchar</c> names any integer. Past the
        /// <c>/CIDToGIDMap</c> — two billion, whose doubled index wrapped negative — or below zero, the CID
        /// has no glyph (false), never an exception out of the checker; under <c>/Identity</c> a negative
        /// one is not present either. The map's own CIDs are the potency.
        /// </summary>
        [Fact]
        public void GlyphPresence_ACidOutsideEveryMap_HasNoGlyph()
        {
            byte[] font = MinimalTrueType.Build("AB");
            PdfDictionary Type0(PdfObject? cidToGid)
            {
                var type0 = new PdfDictionary();
                type0.Set(PdfName.Subtype, new PdfName("Type0"));
                type0.Set(PdfName.DescendantFonts, new PdfArray { CidFont(PdfName.FontFile2, Stream(font), cidToGid) });
                return type0;
            }

            var mapped = EmbeddedGlyphSets.GlyphPresence(Type0(Stream(new byte[] { 0, 0, 0, 2 })), Same, out _)!;
            Assert.True(mapped(1));
            foreach (int cid in new[] { 2_000_000_000, int.MaxValue, -5, int.MinValue })
                Assert.False(mapped(cid));

            var identity = EmbeddedGlyphSets.GlyphPresence(Type0(null), Same, out _)!;
            Assert.True(identity(2));
            Assert.False(identity(-5));
        }

        /// <summary>
        /// A <c>loca</c> table that decreases, or points past <c>glyf</c>, does not index the glyphs: the
        /// answer is "unreadable", never a set read from it.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Cids_AHostileLocaTable_IsUnreadable(bool pastGlyf)
        {
            byte[] font = MinimalTrueType.Build("AB");
            int loca = TableOffset(font, "loca");
            // Short format: entries are offsets / 2, and loca is [0, 0, end of glyph 1, end of glyph 2].
            // The last entry set below glyph 2's start (a decrease), or past the end of glyf.
            int value = pastGlyf ? 0x7FFF : 0;
            font[loca + 6] = (byte)(value >> 8);
            font[loca + 7] = (byte)value;
            Assert.Null(EmbeddedGlyphSets.Cids(CidFont(PdfName.FontFile2, Stream(font), null), Same, out string unreadable));
            Assert.Contains("loca table does not index glyf", unreadable, StringComparison.Ordinal);
        }

        private static int TableOffset(byte[] font, string tag)
        {
            int tables = (font[4] << 8) | font[5];
            for (int i = 0; i < tables; i++)
            {
                int record = 12 + 16 * i;
                if (Encoding.ASCII.GetString(font, record, 4) == tag)
                    return (font[record + 8] << 24) | (font[record + 9] << 16) | (font[record + 10] << 8) | font[record + 11];
            }
            throw new InvalidOperationException("no " + tag);
        }

        private static PdfStream Stream(byte[] data)
        {
            var stream = new PdfStream();
            stream.RawData = data;
            stream.Set(PdfName.Length, new PdfNumber(data.Length));
            return stream;
        }

        private static PdfStream Type1Stream(byte[] program, int length1, int length2)
        {
            var stream = Stream(program);
            stream.Set(PdfName.Length1, new PdfNumber(length1));
            stream.Set(PdfName.Length2, new PdfNumber(length2));
            return stream;
        }

        private static PdfDictionary Descriptor(PdfName key, PdfStream program)
        {
            var descriptor = new PdfDictionary();
            descriptor.Set(key, program);
            return descriptor;
        }

        private static PdfDictionary CidFont(PdfName key, PdfStream program, PdfObject? cidToGid)
        {
            var cidFont = new PdfDictionary();
            cidFont.Set(PdfName.FontDescriptor, Descriptor(key, program));
            if (cidToGid != null) cidFont.Set(PdfName.CIDToGIDMap, cidToGid);
            return cidFont;
        }
    }
}
