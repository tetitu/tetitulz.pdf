// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.DocxToPdf.Rendering.MathTypesetting;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Whitespace is not evidence of font coverage.
    ///
    /// <para><b>The defect.</b> <c>RunRenderer.Render</c> flattens a <c>w:r</c> into ONE string, so
    /// <c>&lt;w:tab/&gt;</c> arrives as <c>'\t'</c> and <c>&lt;w:br/&gt;</c> as <c>'\n'</c> in the
    /// same <c>textStr</c> as the Hebrew or CJK that put the run on the embedded-font path. Both
    /// DocxToPdf resolvers then took the caller's set through a bare <c>.Distinct()</c> and demanded
    /// a <c>cmap</c> entry for every member. Most faces map neither U+0009 nor U+000A, so the run
    /// asked for coverage no font could give: resolution returned null and the run fell through to
    /// the standard-14 path, where <c>EncodeWinAnsiLiteral</c> draws every non-WinAnsi character as
    /// a space — correctly-sized BLANK lines for text a font on the machine (or shipped inside the
    /// DOCX itself) could draw. <c>Core.Fonts.SystemFontResolver.Distinct</c> is the one of this
    /// tree's four coverage resolvers that already excluded the four whitespace code points;
    /// the duplication register recorded it.</para>
    ///
    /// <para><b>Why nothing caught it.</b> The failure is a perfect imitation of the failure it is
    /// not: losing the run is exactly what happens when the host genuinely has no Hebrew font, and
    /// <c>ReportNoCoveringFont</c> filters the reported code points through
    /// <c>IsWinAnsiCodePoint</c> — which lets <c>'\t'</c> through — so the diagnostic named the
    /// Hebrew letters and never mentioned the tab that actually caused it.</para>
    ///
    /// <para>The fixture is a synthetic <c>cmap</c>-only sfnt rather than a host font, because
    /// whether a real font carries a control-code entry varies by vendor: an assertion resting on
    /// that would pass or fail by machine.</para>
    /// </summary>
    public class FontCoverageWhitespaceTests
    {
        private const string Family = "HebrewOnlyEmbedded";
        private const string Key = "{52F7CD7B-1A3C-4E2F-9B8A-0123456789AB}";

        private const int Aleph = 0x05D0;   // א — mapped by the fixture font
        private const int Bet = 0x05D1;     // ב — mapped by the fixture font
        private const int CyrillicA = 0x0410;   // А — deliberately NOT mapped

        [Fact]
        public void TabAndLineBreakInARun_DoNotDisqualifyTheDocumentsOwnEmbeddedFont()
        {
            using var ctx = OpenContextWithEmbeddedFont();

            // Exactly what RunRenderer hands the resolver for "<w:tab/>א ב<w:br/>".
            IEnumerable<int> required = UnicodeText.CodePoints("\tא ב\n");

            var prog = ctx.Context.ResolveEmbeddedFont(Family, false, false, required);

            Assert.False(prog is null,
                "the author's own embedded font must still be chosen: a tab is not evidence of coverage");

            // And it really is the tab-less face — the requirement was DROPPED, not satisfied.
            Assert.False(prog!.Covers('\t'));
            Assert.False(prog.Covers('\n'));
            Assert.False(prog.Covers(' '));
            Assert.True(prog.TryGetGlyph(Aleph, out ushort aleph));
            Assert.Equal(1, aleph);
        }

        [Fact]
        public void ARealGlyphTheFontLacks_StillDisqualifiesIt()
        {
            // The control: only whitespace was excused. A code point the font genuinely cannot draw
            // must still lose it the run, or the rule above could be "coverage is never checked".
            using var ctx = OpenContextWithEmbeddedFont();

            var prog = ctx.Context.ResolveEmbeddedFont(Family, false, false, new[] { CyrillicA });

            Assert.True(prog is null, "a Cyrillic code point is not in the fixture font's cmap");
        }

        [Fact]
        public void RequiredCodePoints_DropsTheFourWhitespaceCodePointsAndNothingElse()
        {
            int[] required = FontResolver.RequiredCodePoints(
                UnicodeText.CodePoints(" \tא\r\nבא"));

            // Distinct, order-preserving, and the four whitespace members gone.
            Assert.Equal(new[] { Aleph, Bet }, required);
        }

        [Fact]
        public void EquationTextHoldingATab_IsSetInTheEmbeddedFontRatherThanReservedAsAPhantom()
        {
            using var ctx = OpenContextWithEmbeddedFont();

            var style = MathStyle.Root(Family, 12, false, null);
            var box = MathMeasure.Glyphs("\tאב", style, ctx.Context);

            // MathMeasure re-checks coverage itself after resolving, so it is a second site that
            // has to ask the question the same way; a phantom here means it did not.
            var glyphs = Assert.IsType<MathGlyphBox>(box);
            Assert.Equal("EF1", glyphs.EmbeddedResName);
        }

        // ------------------------------------------------------------------ fixture

        /// <summary>A live <see cref="DocxRenderContext"/> over a DOCX embedding the fixture font.</summary>
        private static ContextHandle OpenContextWithEmbeddedFont()
        {
            var ms = new MemoryStream(BuildDocxWithEmbeddedFont(Family, Key, HebrewOnlyFont()));
            var wd = WordprocessingDocument.Open(ms, false);
            return new ContextHandle(ms, wd, new DocxRenderContext(wd, new DocxToPdfOptions()));
        }

        private sealed class ContextHandle : System.IDisposable
        {
            private readonly MemoryStream _ms;
            private readonly WordprocessingDocument _wd;

            internal ContextHandle(MemoryStream ms, WordprocessingDocument wd, DocxRenderContext context)
            {
                _ms = ms; _wd = wd; Context = context;
            }

            internal DocxRenderContext Context { get; }

            public void Dispose() { _wd.Dispose(); _ms.Dispose(); }
        }

        private static byte[] BuildDocxWithEmbeddedFont(string family, string fontKey, byte[] fontData)
        {
            // XOR is involutive, so Deobfuscate is also the obfuscator for building the fixture.
            byte[] obfuscated = ObfuscatedFontReader.Deobfuscate(fontData, fontKey);

            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(new Run(new Text("hi")))));
                main.Document.Save();

                var ftPart = main.AddNewPart<FontTablePart>();
                var fontPart = ftPart.AddNewPart<FontPart>(
                    "application/vnd.openxmlformats-officedocument.obfuscatedFont", "rIdEmb1");
                using (var fs = new MemoryStream(obfuscated)) fontPart.FeedData(fs);
                string rId = ftPart.GetIdOfPart(fontPart);

                ftPart.Fonts = new Fonts(
                    new Font(new EmbedRegularFont { Id = rId, FontKey = fontKey }) { Name = family });
                ftPart.Fonts.Save();
            }
            return ms.ToArray();
        }

        /// <summary>
        /// An sfnt whose only table is a <c>cmap</c> mapping U+05D0–U+05D5 (א–ו) to glyphs 1–6.
        /// Nothing else is mapped — in particular not U+0009, U+000A, U+000D or U+0020.
        /// </summary>
        private static byte[] HebrewOnlyFont()
        {
            byte[] subtable = Format4(0x05D0, 0x05D5, firstGlyph: 1);

            // cmap: header(4) + one 8-byte encoding record, then the subtable body.
            var cmap = new List<byte>();
            AddU16(cmap, 0);            // version
            AddU16(cmap, 1);            // numTables
            AddU16(cmap, 3);            // platform 3 (Microsoft)
            AddU16(cmap, 1);            // encoding 1 (Unicode BMP)
            AddU32(cmap, 12);           // offset to the subtable body (4 + 8)
            cmap.AddRange(subtable);

            // sfnt: header(12) + one 16-byte table directory entry.
            var font = new List<byte>();
            AddU32(font, 0x00010000);   // version 1.0
            AddU16(font, 1);            // numTables
            AddU16(font, 0); AddU16(font, 0); AddU16(font, 0);
            font.AddRange(new[] { (byte)'c', (byte)'m', (byte)'a', (byte)'p' });
            AddU32(font, 0);                    // checksum (unchecked by the parser)
            AddU32(font, 28);                   // offset (12 + 16)
            AddU32(font, (uint)cmap.Count);     // length
            font.AddRange(cmap);
            return font.ToArray();
        }

        /// <summary>A format-4 subtable mapping [first..last] to consecutive glyphs from firstGlyph.</summary>
        private static byte[] Format4(int first, int last, int firstGlyph)
        {
            const int segCount = 2;             // the real segment plus the 0xFFFF terminator
            var b = new List<byte>();
            AddU16(b, 4);                       // format
            AddU16(b, 16 + 8 * segCount);       // length
            AddU16(b, 0);                       // language
            AddU16(b, segCount * 2);            // segCountX2
            AddU16(b, 0); AddU16(b, 0); AddU16(b, 0);   // searchRange, entrySelector, rangeShift

            AddU16(b, last); AddU16(b, 0xFFFF); // endCode[]
            AddU16(b, 0);                       // reservedPad
            AddU16(b, first); AddU16(b, 0xFFFF);// startCode[]
            AddU16(b, (firstGlyph - first) & 0xFFFF);   // idDelta: glyph = (code + delta) & 0xFFFF
            AddU16(b, 1);                       // terminator: 0xFFFF + 1 = 0
            AddU16(b, 0); AddU16(b, 0);         // idRangeOffset[]
            return b.ToArray();
        }

        private static void AddU16(List<byte> b, int v)
        {
            b.Add((byte)(v >> 8)); b.Add((byte)v);
        }

        private static void AddU32(List<byte> b, uint v)
        {
            b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v);
        }
    }
}
