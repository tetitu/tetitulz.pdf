// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.DocxToPdf.Tests.Helpers;
using Xunit;
using BuilderExport = Tetitulz.Pdf.Builder.Export;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Wave 17 R16.1–R16.3: what the converter does with input built to hurt it. A package part
    /// declaring gigabytes is refused before the Open XML SDK reads it (R16.2); an image whose
    /// frame header claims a size past the authored-image rule is skipped and reported on both
    /// render paths instead of being written as declared (R16.1); a NaN option is refused naming
    /// the member (R16.3); and a page too small for its own margins still converts to a document
    /// that opens — the layout has no loop a degenerate geometry holds open.
    /// </summary>
    public class HostileDocxInputTests
    {
        // The JPEG's XObject dictionary, which is written uncompressed on both paths (the Builder
        // path deflates its content streams whatever the option says, so the `Do` operator is not
        // readable there); a frame the converter refused has no XObject at all.
        private static readonly Regex JpegXObject = new(@"/Filter\s*/DCTDecode", RegexOptions.Compiled);

        private static string Latin1(byte[] pdf) => Encoding.Latin1.GetString(pdf);

        private static DocxToPdfOptions Legacy => new DocxToPdfOptions { CompressStreams = false };
        private static DocxToPdfOptions ViaBuilder => new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = false };

        // ------------------------------------------------------------------ R16.2

        /// <summary>
        /// A 257 MB part — zeros, so it deflates to a few hundred kilobytes — in an otherwise valid
        /// document. The refusal names the part and the limit, and comes before any parsing.
        /// </summary>
        [Fact]
        public void OversizedPart_IsRefusedBeforeThePackageIsOpened()
        {
            byte[] docx = WithExtraPart(MinimalDocxBuilder.CreateMinimalDocx("Hello"), "word/media/bomb.bin",
                PdfLimits.MaxDocxPartBytes + 1);

            var ex = Assert.Throws<DocxToPdfException>(() => DocxToPdfConverter.ConvertToBytes(docx, Legacy));
            Assert.Contains("word/media/bomb.bin", ex.Message, StringComparison.Ordinal);
            Assert.Contains("MaxDocxPartBytes", ex.Message, StringComparison.Ordinal);
        }

        /// <summary>The control: a part just inside the cap converts (the part is never read, so this is quick).</summary>
        [Fact]
        public void PartAtTheCap_Converts()
        {
            byte[] docx = WithExtraPart(MinimalDocxBuilder.CreateMinimalDocx("Hello"), "word/media/large.bin", 1024 * 1024);
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx, Legacy);
            Assert.Equal(1, Pages(pdf));
        }

        private static byte[] WithExtraPart(byte[] docx, string name, long bytes)
        {
            using var ms = new MemoryStream();
            ms.Write(docx, 0, docx.Length);
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                using var s = entry.Open();
                var chunk = new byte[1024 * 1024];
                for (long written = 0; written < bytes; written += chunk.Length)
                    s.Write(chunk, 0, (int)Math.Min(chunk.Length, bytes - written));
            }
            return ms.ToArray();
        }

        // ------------------------------------------------------------------ R16.1

        /// <summary>
        /// A JPEG whose SOF0 claims 65 535 × 65 535 pixels, with no scan data at all: the legacy
        /// renderer and the Builder path both skip it and say why, rather than write the frame's size
        /// into the output as declared.
        /// </summary>
        [Fact]
        public void OversizedJpegFrame_IsSkippedAndReported_OnBothPaths()
        {
            byte[] docx = DocxImageBuilder.CreateDocxWithImage(DocxImageBuilder.MakeJpegHeader(65535, 65535), "image/jpeg");

            string legacy = Latin1(DocxToPdfConverter.ConvertToBytes(docx, Legacy));
            Assert.DoesNotMatch(JpegXObject, legacy);
            Assert.Contains("per-side limit", DocxToPdfConverter.LastDiagnostic ?? "", StringComparison.Ordinal);

            string builder = Latin1(DocxToPdfConverter.ConvertToBytes(docx, ViaBuilder));
            Assert.DoesNotMatch(JpegXObject, builder);
            Assert.Contains("per-side limit", BuilderExport.BuilderDiagnostics.LastError?.Detail ?? "", StringComparison.Ordinal);
        }

        /// <summary>The control: a small JPEG frame is embedded on both paths.</summary>
        [Fact]
        public void SmallJpegFrame_IsEmbedded_OnBothPaths()
        {
            byte[] docx = DocxImageBuilder.CreateDocxWithImage(DocxImageBuilder.MakeJpegHeader(4, 4), "image/jpeg");
            Assert.Matches(JpegXObject, Latin1(DocxToPdfConverter.ConvertToBytes(docx, Legacy)));
            Assert.Matches(JpegXObject, Latin1(DocxToPdfConverter.ConvertToBytes(docx, ViaBuilder)));
        }

        // ------------------------------------------------------------------ R16.3

        [Theory]
        [InlineData(double.NaN, 792, 72, 12, "PageWidth")]
        [InlineData(0, 792, 72, 12, "PageWidth")]
        [InlineData(612, double.PositiveInfinity, 72, 12, "PageHeight")]
        [InlineData(612, 792, -1, 12, "MarginLeft")]
        [InlineData(612, 792, 72, double.NaN, "DefaultFontSize")]
        public void Options_NonFiniteOrNonPositive_AreRefusedNamingTheMember(
            double pageW, double pageH, double marginLeft, double fontSize, string member)
        {
            byte[] docx = MinimalDocxBuilder.CreateMinimalDocx("Hello");
            var options = new DocxToPdfOptions
            {
                PageWidth = pageW, PageHeight = pageH, MarginLeft = marginLeft, DefaultFontSize = fontSize,
            };

            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DocxToPdfConverter.ConvertToBytes(docx, options));
            Assert.Equal("options", ex.ParamName);
            Assert.Contains(member, ex.Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// A section whose margins swallow its Letter page: it laid 2 000 words out one character a line and one
        /// line a page — 20 001 pages on one path, 2 000 on the other, from a kilobyte. The margins give way and are
        /// reported, so the words fill a page or two; the control, the same words under the section's sane margins,
        /// fills as many.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void MarginsThatSwallowThePage_GiveWay_AndAreReported(bool legacy)
        {
            static byte[] Docx(int marginTwips) => GeometryDocx(new SectionProperties(
                new PageSize { Width = 12240U, Height = 15840U },
                new PageMargin { Top = marginTwips, Bottom = marginTwips, Left = (uint)marginTwips, Right = (uint)marginTwips }), words: 2000);

            var reports = new List<string>();
            int thread = Environment.CurrentManagedThreadId;
            void Collect(string m) { if (Environment.CurrentManagedThreadId == thread) reports.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            byte[] swallowed;
            try { swallowed = DocxToPdfConverter.ConvertToBytes(Docx(8000), legacy ? Legacy : ViaBuilder); }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }
            byte[] sane = DocxToPdfConverter.ConvertToBytes(Docx(1440), legacy ? Legacy : ViaBuilder);

            Assert.InRange(Pages(swallowed), 1, Pages(sane) + 1);
            Assert.Contains(reports, r => r.Contains("SectionPageSize.Resolve", StringComparison.Ordinal));
        }

        /// <summary>
        /// A section page 50 pt wide, under margins that swallow it: the conversion's own 72 pt margins would leave
        /// no room either, so both give way to none — the Builder's <c>PageContentBox</c> rule, the last step — and
        /// the words fill the narrow page in columns of its width, not a character a page. Without that step the
        /// 72 pt margins came back and the page had no room at all.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ANarrowPageUnderSwallowingMargins_KeepsItsWholeWidth(bool legacy)
        {
            byte[] docx = GeometryDocx(new SectionProperties(
                new PageSize { Width = 1000U, Height = 15840U },
                new PageMargin { Top = 1440, Bottom = 1440, Left = 5000U, Right = 5000U }), words: 2000);

            int pages = Pages(DocxToPdfConverter.ConvertToBytes(docx, legacy ? Legacy : ViaBuilder));

            // A 50 pt column holds a word or so a line and some 54 lines a page: well under a hundred pages
            // for 2 000 words, where a character a line would be thousands.
            Assert.InRange(pages, 1, 100);
        }

        /// <summary>A caller's margins that leave no room on the caller's page are refused, naming the options.</summary>
        [Theory]
        [InlineData(306.0, 72.0)]
        [InlineData(72.0, 380.0)]
        public void OptionsMarginsThatSwallowThePage_AreRefused(double side, double topAndBottom)
        {
            byte[] docx = MinimalDocxBuilder.CreateMinimalDocx("Hello");
            var options = new DocxToPdfOptions { MarginLeft = side, MarginRight = side, MarginTop = topAndBottom, MarginBottom = topAndBottom };

            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => DocxToPdfConverter.ConvertToBytes(docx, options));
            Assert.Equal("options", ex.ParamName);
            Assert.Contains("leave", ex.Message, StringComparison.Ordinal);
            Assert.NotEmpty(DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { MarginLeft = 200, MarginRight = 200 }));
        }

        /// <summary>
        /// A section whose page is one point square under margins of 500 points each — no room for
        /// a glyph, let alone a line — on both paths. The question is the H lens's: the layout ends,
        /// and the file opens.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void TinyPageUnderHugeMargins_Terminates(bool legacy)
        {
            byte[] docx = SectionDocx(pageTwips: 20, marginTwips: 10000, words: 200);
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx, legacy ? Legacy : ViaBuilder);
            Assert.True(Pages(pdf) >= 1);
        }

        /// <summary>
        /// A <c>w:pgSz</c> dimension of zero describes no page: it is ignored and reported, on both paths alike — where
        /// one path wrote a page of no area for every line (300 words, 3981 pages) and the other failed the whole
        /// conversion. The control: a page size that is given is the page's.
        /// </summary>
        [Theory]
        [InlineData(0U, 0U, true)]
        [InlineData(0U, 0U, false)]
        [InlineData(12240U, 0U, true)]
        [InlineData(12240U, 0U, false)]
        public void AZeroPageDimension_IsIgnoredAndReported(uint widthTwips, uint heightTwips, bool legacy)
        {
            var seen = new System.Collections.Generic.List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) seen.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            byte[] pdf;
            try
            {
                pdf = DocxToPdfConverter.ConvertToBytes(
                    GeometryDocx(new SectionProperties(new PageSize { Width = widthTwips, Height = heightTwips }), words: 300),
                    legacy ? Legacy : ViaBuilder);
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            Assert.Equal(1, Pages(pdf));
            var box = MediaBox(pdf);
            Assert.True(box.Width > 0 && box.Height > 0, $"{box.Width} by {box.Height}");
            if (widthTwips > 0) Assert.Equal(widthTwips / 20.0, box.Width, 3);
            Assert.Contains(seen, m => m.Contains("w:pgSz", StringComparison.Ordinal));

            byte[] given = DocxToPdfConverter.ConvertToBytes(
                GeometryDocx(new SectionProperties(new PageSize { Width = 8400U, Height = 11900U }), words: 10),
                legacy ? Legacy : ViaBuilder);
            Assert.Equal((420.0, 595.0), MediaBox(given));
        }

        private static (double Width, double Height) MediaBox(byte[] pdf)
        {
            var m = Regex.Match(Latin1(pdf), @"/MediaBox\s*\[\s*0\s+0\s+([0-9.]+)\s+([0-9.]+)\s*\]");
            Assert.True(m.Success, "a MediaBox is written");
            return (double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
                    double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture));
        }
        internal const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        /// <summary>A document part of one paragraph whose run sits inside <paramref name="hyperlinks"/> nested hyperlinks: the run's text is <paramref name="hyperlinks"/> + 5 elements deep.</summary>
        private static string NestedHyperlinks(int hyperlinks)
        {
            var p = new StringBuilder("<w:p>");
            for (int i = 0; i < hyperlinks; i++) p.Append("<w:hyperlink w:anchor=\"a\">");
            p.Append("<w:r><w:t>Deep</w:t></w:r>");
            for (int i = 0; i < hyperlinks; i++) p.Append("</w:hyperlink>");
            p.Append("</w:p>");
            return $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{p}</w:body></w:document>";
        }

        /// <summary>
        /// Hostile input: content nested past <c>PdfLimits.MaxDocxXmlDepth</c> is refused before the package is
        /// opened — the Open XML SDK loads elements by recursion, and content controls nested 5000 deep overflowed
        /// the stack, which ended the process. The boundary exactly: a run 256 elements deep converts, 257 is
        /// refused naming the limit, on both paths.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AHostilePartNestedPastTheDepthLimit_IsRefusedAtTheBoundary(bool legacy)
        {
            Assert.Equal(256, Tetitulz.Pdf.Core.Constants.PdfLimits.MaxDocxXmlDepth);
            byte[] at = RawDocx(NestedHyperlinks(251));
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(at, legacy ? Legacy : ViaBuilder);
            Assert.Equal(1, Pages(pdf));

            var past = Assert.Throws<DocxToPdfException>(() => DocxToPdfConverter.ConvertToBytes(RawDocx(NestedHyperlinks(252)), legacy ? Legacy : ViaBuilder));
            Assert.Contains("MaxDocxXmlDepth", past.Message, StringComparison.Ordinal);
            Assert.Contains("word/document.xml", past.Message, StringComparison.Ordinal);

            var deep = new StringBuilder();
            for (int i = 0; i < 5000; i++) deep.Append("<w:sdt><w:sdtContent>");
            deep.Append("<w:p><w:r><w:t>x</w:t></w:r></w:p>");
            for (int i = 0; i < 5000; i++) deep.Append("</w:sdtContent></w:sdt>");
            byte[] hostile = RawDocx($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{deep}</w:body></w:document>");
            Assert.Contains("MaxDocxXmlDepth", Assert.Throws<DocxToPdfException>(() => DocxToPdfConverter.ConvertToBytes(hostile, legacy ? Legacy : ViaBuilder)).Message, StringComparison.Ordinal);
        }

        /// <summary>
        /// Hostile input: a part in UTF-16 is read for its depth as the SDK reads it — the check is by the
        /// reader's own encoding detection, not by a first byte of '&lt;', which a UTF-16 part does not have.
        /// </summary>
        [Fact]
        public void AHostileUtf16PartNestedPastTheDepthLimit_IsRefused()
        {
            string xml = NestedHyperlinks(400).Replace("encoding=\"UTF-8\"", "encoding=\"UTF-16\"");
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                using var part = new MemoryStream(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray());
                main.FeedData(part);
            }
            Assert.Contains("MaxDocxXmlDepth", Assert.Throws<DocxToPdfException>(() => DocxToPdfConverter.ConvertToBytes(ms.ToArray(), Legacy)).Message, StringComparison.Ordinal);
        }
        /// <summary>
        /// Hostile input, and ordinary: a paragraph without a <c>w:pPr</c> was scanned to its end for its style once
        /// for every run's font and size, which made it quadratic in its runs — 4000 runs, 7 s on either path, a
        /// minute at 8000. Its properties are read once per paragraph; the count is the evidence, two paragraphs
        /// the control.
        /// </summary>
        [Fact]
        public void AHostileParagraphOfManyRuns_ReadsItsPropertiesOnce()
        {
            var body = new StringBuilder("<w:p>");
            for (int i = 0; i < 1000; i++) body.Append("<w:r><w:t xml:space=\"preserve\">word </w:t></w:r>");
            body.Append("</w:p><w:p><w:r><w:t>second</w:t></w:r></w:p>");
            byte[] docx = RawDocx($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{body}</w:body></w:document>");
            using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
            var resolver = new Tetitulz.Pdf.DocxToPdf.Rendering.StyleResolver(doc);
            var paragraphs = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().ToList();

            foreach (var run in paragraphs[0].Elements<Run>())
            {
                resolver.ResolveFontName(run, paragraphs[0]);
                resolver.ResolveFontSizeInPoints(run, paragraphs[0], 11);
            }
            Assert.Equal(1000, paragraphs[0].Elements<Run>().Count());   // potency: every run asked, twice
            Assert.Equal(1, resolver.ParagraphPropertyReads);

            resolver.ResolveFontName(paragraphs[1].Elements<Run>().First(), paragraphs[1]);
            Assert.Equal(2, resolver.ParagraphPropertyReads);
        }

        /// <summary>
        /// A flush of a paragraph's runs asks its tabs and direction, and a page break is a flush: a paragraph
        /// of many breaks and no <c>w:pPr</c> was scanned to its end once a break. Read once, whatever asks.
        /// </summary>
        [Fact]
        public void AHostileParagraphOfManyBreaks_ReadsItsPropertiesOnce()
        {
            var body = new StringBuilder("<w:p>");
            for (int i = 0; i < 500; i++) body.Append("<w:r><w:br w:type=\"page\"/></w:r>");
            body.Append("</w:p>");
            byte[] docx = RawDocx($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{body}</w:body></w:document>");
            using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
            var resolver = new Tetitulz.Pdf.DocxToPdf.Rendering.StyleResolver(doc);
            var paragraph = doc.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().Single();

            for (int i = 0; i < 500; i++)
            {
                resolver.ResolveTabs(paragraph);
                resolver.ResolveParagraphRtl(paragraph);
            }

            Assert.Equal(1, resolver.ParagraphPropertyReads);
        }

        /// <summary>
        /// A table with no <c>w:tblPr</c> was scanned through all its rows for one, by every cell: a few
        /// kilobytes of deflated rows were quadratic. Read once a table, a second table the control, and the
        /// answer the same whether or not there is one.
        /// </summary>
        [Fact]
        public void AHostileTableOfManyRows_ReadsItsPropertiesOnce()
        {
            var bare = new StringBuilder("<w:tbl>");
            for (int i = 0; i < 300; i++) bare.Append("<w:tr><w:tc><w:p/></w:tc></w:tr>");
            bare.Append("</w:tbl>");
            string body = bare + "<w:tbl><w:tblPr><w:jc w:val=\"center\"/></w:tblPr><w:tr><w:tc><w:p/></w:tc></w:tr></w:tbl>";
            byte[] docx = RawDocx($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{body}</w:body></w:document>");
            using var doc = WordprocessingDocument.Open(new MemoryStream(docx), false);
            var resolver = new Tetitulz.Pdf.DocxToPdf.Rendering.StyleResolver(doc);
            var tables = doc.MainDocumentPart!.Document!.Body!.Elements<Table>().ToList();

            for (int i = 0; i < 300; i++) Assert.Null(resolver.TablePropertiesOf(tables[0]));
            Assert.Equal(1, resolver.TablePropertyReads);

            Assert.NotNull(resolver.TablePropertiesOf(tables[1]));
            Assert.Null(resolver.TablePropertiesOf(tables[0]));
            Assert.Equal(2, resolver.TablePropertyReads);
        }

        /// <summary>The whole converter over such a table reads it once too: no site went round the door.</summary>
        [Fact]
        public void AHostileTableOfManyRows_ConvertsLaidOutInFull()
        {
            var bare = new StringBuilder("<w:tbl>");
            for (int i = 0; i < 300; i++) bare.Append("<w:tr><w:tc><w:p><w:r><w:t>cell").Append(i).Append("</w:t></w:r></w:p></w:tc></w:tr>");
            bare.Append("</w:tbl><w:p/>");
            byte[] docx = RawDocx($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{bare}</w:body></w:document>");

            byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx);

            string text = PdfOperationsText(pdf);
            Assert.Contains("cell0", text, System.StringComparison.Ordinal);
            Assert.Contains("cell299", text, System.StringComparison.Ordinal);
        }

        private static string PdfOperationsText(byte[] pdf)
        {
            using var document = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
            return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(document);
        }

        /// <summary>
        /// A complex field's instruction runs are kept until they reach 1024 characters, its name being its first word: appended
        /// to a string run by run, a field of 100 000 instruction runs copied its instruction once per run. The cap is
        /// the evidence — a PAGE whose name lies past it is not recognised and its cached result is drawn — and a
        /// PAGE followed by the same padding, the control, is drawn as the page's number.
        /// </summary>
        [Theory]
        [InlineData(false, "1")]
        [InlineData(true, "7")]
        public void AFieldInstructionPastItsCap_IsNotRead(bool namePastTheCap, string expected)
        {
            string padding = new string(' ', 1100);
            string Instr(string text) => $"<w:r><w:instrText xml:space=\"preserve\">{text}</w:instrText></w:r>";
            string instruction = namePastTheCap ? Instr(padding) + Instr("PAGE") : Instr("PAGE") + Instr(padding);
            string p = "<w:p><w:r><w:fldChar w:fldCharType=\"begin\"/></w:r>"
                       + instruction
                       + "<w:r><w:fldChar w:fldCharType=\"separate\"/></w:r><w:r><w:t>7</w:t></w:r><w:r><w:fldChar w:fldCharType=\"end\"/></w:r></w:p>";
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(
                RawDocx($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{p}</w:body></w:document>"), ViaBuilder);
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            Assert.Equal(expected, Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc).Trim());
        }
        /// <summary>A document whose paragraph style is the last of a <c>w:basedOn</c> chain of <paramref name="chain"/> styles, the font set on the first.</summary>
        private static WordprocessingDocument ChainedStyles(int chain, int runs)
        {
            var styles = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:styles xmlns:w=\"{WordNamespace}\">");
            for (int i = 0; i < chain; i++)
                styles.Append($"<w:style w:type=\"paragraph\" w:styleId=\"S{i}\">{(i > 0 ? $"<w:basedOn w:val=\"S{i - 1}\"/>" : "")}"
                              + (i == 0 ? "<w:rPr><w:rFonts w:ascii=\"BaseFont\"/></w:rPr>" : "") + "</w:style>");
            styles.Append("</w:styles>");
            var p = new StringBuilder($"<w:p><w:pPr><w:pStyle w:val=\"S{chain - 1}\"/></w:pPr>");
            for (int i = 0; i < runs; i++) p.Append("<w:r><w:t xml:space=\"preserve\">word </w:t></w:r>");
            p.Append("</w:p>");
            var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                using (var d = new MemoryStream(Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{p}</w:body></w:document>"))) main.FeedData(d);
                using (var s = new MemoryStream(Encoding.UTF8.GetBytes(styles.ToString()))) main.AddNewPart<StyleDefinitionsPart>().FeedData(s);
            }
            ms.Position = 0;
            return WordprocessingDocument.Open(ms, false);
        }

        /// <summary>
        /// Hostile input: every run walks its style's <c>w:basedOn</c> chain until a property is found, and the file
        /// chooses both the chain's length and the run count — 20 000 styles under 2000 runs took over three minutes.
        /// A chain is followed through <see cref="Rendering.StyleResolver.MaxStyleChain"/> styles, the step count the
        /// evidence, and the cut is reported once; a chain of ten, the control, finds the font on its first style.
        /// </summary>
        [Fact]
        public void AHostileStyleChain_IsFollowedOnlySoFar()
        {
            var seen = new System.Collections.Generic.List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) seen.Add(m); }
            Tetitulz.Pdf.DocxToPdf.Rendering.DocxDiagnostics.OnEvent += Collect;
            try
            {
                using var deep = ChainedStyles(5000, 100);
                var resolver = new Tetitulz.Pdf.DocxToPdf.Rendering.StyleResolver(deep);
                var paragraph = deep.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();
                foreach (var run in paragraph.Elements<Run>()) resolver.ResolveFontName(run, paragraph);
                Assert.InRange(resolver.StyleChainSteps, 100, 100L * Tetitulz.Pdf.DocxToPdf.Rendering.StyleResolver.MaxStyleChain);
                Assert.Single(seen, m => m.Contains("w:basedOn chain", StringComparison.Ordinal));

                using var shallow = ChainedStyles(10, 1);
                var control = new Tetitulz.Pdf.DocxToPdf.Rendering.StyleResolver(shallow);
                var p = shallow.MainDocumentPart!.Document!.Body!.Elements<Paragraph>().First();
                Assert.Equal("BaseFont", control.ResolveFontName(p.Elements<Run>().First(), p));
            }
            finally { Tetitulz.Pdf.DocxToPdf.Rendering.DocxDiagnostics.OnEvent -= Collect; }
        }
        /// <summary>
        /// Hostile input, and the shape of documents built by copying: every numbered paragraph scanned the numbering
        /// part's every definition — 20 000 definitions under 2000 items took 10 s. The part is indexed once per
        /// document, on both paths (the count is the evidence); and the numbers are still right, the last item
        /// numbered 50 by the last definition, the one the items name.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AHostileNumberingPart_IsIndexedOncePerDocument(bool legacy)
        {
            var numbering = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:numbering xmlns:w=\"{WordNamespace}\">");
            for (int i = 0; i < 500; i++)
                numbering.Append($"<w:abstractNum w:abstractNumId=\"{i}\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/><w:lvlText w:val=\"%1.\"/></w:lvl></w:abstractNum>");
            for (int i = 0; i < 500; i++)
                numbering.Append($"<w:num w:numId=\"{i + 1}\"><w:abstractNumId w:val=\"{i}\"/></w:num>");
            // A second numId 500, later, to a bullet definition: the first of an id is the one read, as it always was.
            numbering.Append("<w:abstractNum w:abstractNumId=\"9999\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/><w:lvlText w:val=\"o\"/></w:lvl></w:abstractNum>");
            numbering.Append("<w:num w:numId=\"500\"><w:abstractNumId w:val=\"9999\"/></w:num>");
            numbering.Append("</w:numbering>");
            var body = new StringBuilder();
            for (int i = 0; i < 50; i++)
                body.Append($"<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"500\"/></w:numPr></w:pPr><w:r><w:t>item{i}</w:t></w:r></w:p>");
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                using (var d = new MemoryStream(Encoding.UTF8.GetBytes($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{WordNamespace}\"><w:body>{body}</w:body></w:document>"))) main.FeedData(d);
                using (var n = new MemoryStream(Encoding.UTF8.GetBytes(numbering.ToString()))) main.AddNewPart<NumberingDefinitionsPart>().FeedData(n);
            }

            int before = Tetitulz.Pdf.DocxToPdf.Rendering.ListRenderer.NumberingIndexesBuilt;
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(ms.ToArray(), legacy ? Legacy : ViaBuilder);
            Assert.Equal(1, Tetitulz.Pdf.DocxToPdf.Rendering.ListRenderer.NumberingIndexesBuilt - before);

            using var read = PdfDocument.Open(new MemoryStream(pdf));
            string text = Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(read);
            Assert.Contains("item0", text, StringComparison.Ordinal);
            Assert.Contains("50.", text, StringComparison.Ordinal);
        }
        internal static byte[] RawDocx(string documentXml) => RawDocx(documentXml, numberingXml: null);

        /// <summary>A package of <paramref name="documentXml"/> and, when given, a numbering part of <paramref name="numberingXml"/>.</summary>
        internal static byte[] RawDocx(string documentXml, string numberingXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                using var part = new MemoryStream(Encoding.UTF8.GetBytes(documentXml));
                main.FeedData(part);
                if (numberingXml != null)
                {
                    var numbering = main.AddNewPart<NumberingDefinitionsPart>();
                    using var numberingPart = new MemoryStream(Encoding.UTF8.GetBytes(numberingXml));
                    numbering.FeedData(numberingPart);
                }
            }
            return ms.ToArray();
        }

        private static byte[] GeometryDocx(SectionProperties section, int words)
        {
            var text = new StringBuilder();
            for (int i = 0; i < words; i++) text.Append("word").Append(i).Append(' ');
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(
                    new Paragraph(new Run(new Text(text.ToString().Trim()) { Space = SpaceProcessingModeValues.Preserve })),
                    section));
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static byte[] SectionDocx(uint pageTwips, int marginTwips, int words)
        {
            var text = new StringBuilder();
            for (int i = 0; i < words; i++) text.Append("word").Append(i).Append(' ');

            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body(
                    new Paragraph(new Run(new Text(text.ToString().Trim()) { Space = SpaceProcessingModeValues.Preserve })),
                    new SectionProperties(
                        new PageSize { Width = pageTwips, Height = pageTwips },
                        new PageMargin { Top = marginTwips, Bottom = marginTwips, Left = (uint)marginTwips, Right = (uint)marginTwips }));
                main.Document = new Document(body);
                main.Document.Save();
            }
            return ms.ToArray();
        }

        private static int Pages(byte[] pdf)
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
            return doc.Pages.Count;
        }
    }
}
