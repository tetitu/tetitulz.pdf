// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts
{
    /// <summary>
    /// <see cref="NotdefReferences"/>: which shown codes reference the <c>.notdef</c> glyph (ISO 32000-2
    /// §9.7.6.3, §9.6.5.2, §9.6.6.4) — the answer PDF/UA-2's §8.4.5.9 and PDF/A's 6.2.11.8 share.
    /// </summary>
    public class NotdefReferencesTests
    {
        private const string Cmap =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Test def\n" +
            "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
            "1 begincidrange <0001> <0005> 10 endcidrange\n" +
            "1 begincidchar <0006> 0 endcidchar\n" +
            "endcmap CMapName currentdict /CMap defineresource pop end end";

        /// <summary>
        /// A composite font, through its embedded CMap: a code mapped to CID 0, a valid code no mapping
        /// covers (§9.7.6.3: a notdef mapping or CID 0 — the codec still answers its own value, for the
        /// renderer), and a trailing byte no two-byte codespace admits; a mapped code is not one.
        /// </summary>
        [Fact]
        public void ACompositeFont_Cid0_AnUnmappedCode_AndAnInvalidOne()
        {
            using var doc = Open(
                "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding 3 0 R /DescendantFonts [4 0 R] >>",
                $"<< /Type /CMap /Length {Cmap.Length} >>\nstream\n{Cmap}\nendstream",
                "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 5 0 R /DW 1000 >>",
                Descriptor(4));
            var reader = NotdefReferences.For(Font(doc), doc);

            var found = reader.In(new byte[] { 0x00, 0x02, 0x00, 0x06, 0x00, 0x09, 0x07 });
            Assert.Equal(new[] { (6, NotdefCause.Cid0), (9, NotdefCause.NoCidMapping), (7, NotdefCause.NoCodespace) },
                found.Select(r => (r.Code, r.Cause)));
            Assert.Null(reader.Unreadable);

            var codec = CMapCodec.FromFontEncoding(Font(doc), doc);
            var unmapped = codec.Decode(new byte[] { 0x00, 0x09 }).Single();
            Assert.False(unmapped.CidMapped);
            Assert.Equal(9, unmapped.Cid);                       // what the renderer still draws
            Assert.True(codec.Decode(new byte[] { 0x00, 0x02 }).Single().CidMapped);
        }

        /// <summary>
        /// The codec's other unmapped routes: a code width the CMap declares no range for (one-byte codes
        /// beside two-byte ranges), a code inside no range of its width, and a CMap with no ranges at all
        /// whose only mappings are <c>cidchar</c>s.
        /// </summary>
        [Fact]
        public void UnmappedCodes_ThroughEveryRouteTheCodecHas()
        {
            const string mixed =
                "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Mixed def\n" +
                "2 begincodespacerange <00> <7F> <8000> <FFFF> endcodespacerange\n" +
                "1 begincidchar <41> 5 endcidchar\n" +
                "1 begincidrange <8000> <8001> 20 endcidrange\n" +
                "endcmap CMapName currentdict /CMap defineresource pop end end";
            using var doc = Open(
                "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding 3 0 R /DescendantFonts [4 0 R] >>",
                $"<< /Type /CMap /Length {mixed.Length} >>\nstream\n{mixed}\nendstream",
                "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 5 0 R /DW 1000 >>",
                Descriptor(4));
            var found = NotdefReferences.For(Font(doc), doc).In(new byte[] { 0x41, 0x42, 0x80, 0x00, 0x80, 0x05 });
            Assert.Equal(new[] { (0x42, NotdefCause.NoCidMapping), (0x8005, NotdefCause.NoCidMapping) }, found.Select(r => (r.Code, r.Cause)));

            const string charsOnly =
                "/CIDInit /ProcSet findresource begin 12 dict begin begincmap /CMapName /Chars def\n" +
                "1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
                "1 begincidchar <0041> 7 endcidchar\n" +
                "endcmap CMapName currentdict /CMap defineresource pop end end";
            using var chars = Open(
                "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding 3 0 R /DescendantFonts [4 0 R] >>",
                $"<< /Type /CMap /Length {charsOnly.Length} >>\nstream\n{charsOnly}\nendstream",
                "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 5 0 R /DW 1000 >>",
                Descriptor(4));
            Assert.Equal(new[] { 0x42 }, NotdefReferences.For(Font(chars), chars).In(new byte[] { 0x00, 0x41, 0x00, 0x42 }).Select(r => r.Code));
        }

        /// <summary>
        /// An embedded Type 1 font with no <c>/Encoding</c> takes its program's built-in encoding as the base
        /// (Table 114): a code the program's encoding names no glyph for references <c>.notdef</c>.
        /// </summary>
        [Fact]
        public void AnEmbeddedType1_TheProgramsOwnEncodingIsTheBase()
        {
            var (program, length1, length2) = Tetitulz.Pdf.TestHelpers.MinimalType1.Build(
                new[] { (".notdef", Tetitulz.Pdf.TestHelpers.MinimalType1.AdvanceOnly(0)), ("A", Tetitulz.Pdf.TestHelpers.MinimalType1.AdvanceOnly(500)) },
                encoding: new Dictionary<int, string> { [65] = "A" });
            string data = Tetitulz.Pdf.Core.Text.Latin1Text.Decode(program);
            using var doc = Open(
                "<< /Type /Font /Subtype /Type1 /BaseFont /Test /FontDescriptor 3 0 R >>",
                "<< /Type /FontDescriptor /FontName /Test /Flags 32 /FontBBox [0 0 1000 1000] /ItalicAngle 0 /Ascent 800 /Descent -200 " +
                "/CapHeight 700 /StemV 80 /FontFile 4 0 R >>",
                $"<< /Length {program.Length} /Length1 {length1} /Length2 {length2} /Length3 0 >>\nstream\n{data}\nendstream");
            var found = NotdefReferences.For(Font(doc), doc).In(new byte[] { 65, 66 });
            Assert.Equal(new[] { (66, NotdefCause.UnnamedCode) }, found.Select(r => (r.Code, r.Cause)));
        }

        /// <summary>Under Identity-H every code is mapped; only CID 0 references <c>.notdef</c>.</summary>
        [Fact]
        public void IdentityH_OnlyCid0()
        {
            using var doc = Open(
                "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H /DescendantFonts [3 0 R] >>",
                "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 4 0 R /DW 1000 >>",
                Descriptor(4));
            var found = NotdefReferences.For(Font(doc), doc).In(new byte[] { 0x00, 0x00, 0x00, 0x41, 0xFF, 0xFF });
            Assert.Equal(new[] { (0, NotdefCause.Cid0) }, found.Select(r => (r.Code, r.Cause)));
        }

        /// <summary>
        /// A simple font under a named encoding: a code the encoding names no glyph for — WinAnsi's 0 —
        /// references <c>.notdef</c> (the published profiles reads the name through the encoding); WinAnsi's 129 is the
        /// bullet, 65 is A. With no <c>/Encoding</c> and no embedded program the base is not known, so
        /// nothing is judged.
        /// </summary>
        [Fact]
        public void ASimpleFont_ACodeTheEncodingLeavesUnnamed()
        {
            using var doc = Open("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            var found = NotdefReferences.For(Font(doc), doc).In(new byte[] { 0, 65, 129, 1 });
            Assert.Equal(new[] { (0, NotdefCause.UnnamedCode), (1, NotdefCause.UnnamedCode) }, found.Select(r => (r.Code, r.Cause)));

            using var bare = Open("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            Assert.Empty(NotdefReferences.For(Font(bare), bare).In(new byte[] { 0, 65 }));
        }

        /// <summary>
        /// A non-symbolic TrueType font names its codes through its encoding; a symbolic one sends them to
        /// its <c>cmap</c> directly (§9.6.6.4), so an unnamed code there is not judged.
        /// </summary>
        [Fact]
        public void ATrueTypeFont_UnnamedCodesCountOnlyWhenNonSymbolic()
        {
            using var plain = Open("<< /Type /Font /Subtype /TrueType /BaseFont /Arial /Encoding /WinAnsiEncoding /FontDescriptor 3 0 R >>", Descriptor(32));
            Assert.Equal(new[] { 0 }, NotdefReferences.For(Font(plain), plain).In(new byte[] { 0, 65 }).Select(r => r.Code));

            using var symbolic = Open("<< /Type /Font /Subtype /TrueType /BaseFont /Arial /Encoding /WinAnsiEncoding /FontDescriptor 3 0 R >>", Descriptor(4));
            Assert.Empty(NotdefReferences.For(Font(symbolic), symbolic).In(new byte[] { 0, 65 }));
        }

        /// <summary>
        /// A Type 3 font's encoding is its dictionary: a <c>/Differences</c> name that is <c>.notdef</c> and
        /// a code outside every entry both reference <c>.notdef</c>; a named code does not.
        /// </summary>
        [Fact]
        public void AType3Font_ANamedNotdef_AndACodeOutsideItsDifferences()
        {
            using var doc = Open(
                "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000] /FontMatrix [0.001 0 0 0.001 0 0] " +
                "/Encoding << /Differences [65 /a /.notdef] >> /CharProcs << /a 3 0 R >> /FirstChar 65 /LastChar 66 /Widths [500 500] >>",
                "<< /Length 10 >>\nstream\n500 0 d0\n\nendstream");
            var found = NotdefReferences.For(Font(doc), doc).In(new byte[] { 65, 66, 67 });
            Assert.Equal(new[] { (66, NotdefCause.NamedNotdef), (67, NotdefCause.UnnamedCode) }, found.Select(r => (r.Code, r.Cause)));
        }

        private static string Descriptor(int flags) =>
            $"<< /Type /FontDescriptor /FontName /Test /Flags {flags.ToString(CultureInfo.InvariantCulture)} /FontBBox [0 0 1000 1000] " +
            "/ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>";

        /// <summary>The font is object 2; the catalog, 1, has no pages, which these readers do not need.</summary>
        private static PdfDictionary Font(PdfDocument doc) => (PdfDictionary)doc.ResolveObject(new PdfReference(2, 0));

        private static PdfDocument Open(params string[] bodies)
        {
            var objects = new List<string> { "<< /Type /Catalog >>" };
            objects.AddRange(bodies);
            var sb = new StringBuilder("%PDF-1.7\n");
            var offsets = new List<int>();
            for (int i = 0; i < objects.Count; i++)
            {
                offsets.Add(sb.Length);
                sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
            }
            int xref = sb.Length;
            sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
            foreach (int offset in offsets) sb.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
            return PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())));   // a program's bytes are kept
        }
    }
}
