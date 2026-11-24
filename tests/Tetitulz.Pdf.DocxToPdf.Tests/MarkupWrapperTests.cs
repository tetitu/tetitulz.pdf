// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// The elements that only mark the content they hold (Rendering.MarkupWrappers), on both paths: text inside a
    /// smart tag, inline or block custom XML, a moved run or a bidi wrapper, a table row or cell inside a content
    /// control or custom XML, and — on the legacy path — more than a cell paragraph's own runs reach the output.
    /// And a hyperlink's target (Rendering.HyperlinkTargets): the Builder path writes a link for an external one.
    /// </summary>
    public class MarkupWrapperTests
    {
        private static DocxToPdfOptions Options(bool legacy)
            => new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = legacy };

        private static string Document(string body)
            => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\" "
               + "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">"
               + $"<w:body>{body}</w:body></w:document>";

        private static string Text(byte[] pdf)
        {
            using var doc = PdfDocument.Open(new MemoryStream(pdf));
            return Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc);
        }

        private static string Cell(string inner) => $"<w:tbl><w:tr><w:tc>{inner}</w:tc></w:tr></w:tbl><w:p/>";

        public static IEnumerable<object[]> InlineWrappers()
        {
            var wrappers = new[]
            {
                ("smartTag", "<w:smartTag w:element=\"x\">", "</w:smartTag>"),
                ("customXml", "<w:customXml w:element=\"x\">", "</w:customXml>"),
                ("sdt", "<w:sdt><w:sdtContent>", "</w:sdtContent></w:sdt>"),
                ("ins", "<w:ins w:id=\"1\" w:author=\"a\">", "</w:ins>"),
                ("moveTo", "<w:moveTo w:id=\"2\" w:author=\"a\">", "</w:moveTo>"),
                ("dir", "<w:dir w:val=\"ltr\">", "</w:dir>"),
                ("bdo", "<w:bdo w:val=\"ltr\">", "</w:bdo>"),
                ("hyperlink", "<w:hyperlink w:anchor=\"a\">", "</w:hyperlink>"),
                ("none", "", ""),
            };
            foreach (var (name, open, close) in wrappers)
                foreach (bool inCell in new[] { false, true })
                    foreach (bool legacy in new[] { true, false })
                        yield return new object[] { name, open, close, inCell, legacy };
        }

        /// <summary>A run inside each inline wrapper, twice nested, in a body paragraph and in a table cell, is drawn on both paths; no wrapper, the control.</summary>
        [Theory]
        [MemberData(nameof(InlineWrappers))]
        public void ARunInsideAnInlineWrapper_IsDrawn(string name, string open, string close, bool inCell, bool legacy)
        {
            string paragraph = $"<w:p><w:r><w:t>Plain</w:t></w:r>{open}{open}<w:r><w:t>Wrapped</w:t></w:r>{close}{close}</w:p>";
            string text = Text(DocxToPdfConverter.ConvertToBytes(
                HostileDocxInputTests.RawDocx(Document(inCell ? Cell(paragraph) : paragraph)), Options(legacy)));
            Assert.Contains("Plain", text, StringComparison.Ordinal);
            Assert.True(text.Contains("Wrapped", StringComparison.Ordinal), $"{name}: '{text}'");
        }

        /// <summary>
        /// Every smart tag's start and end tags are taken out of the SDK's serialisation in one read, what they
        /// hold kept in place and escaped as it was; the result reads back as typed runs.
        /// </summary>
        [Fact]
        public void WithoutSmartTags_OpensEveryLevelInOneRead()
        {
            string w = HostileDocxInputTests.WordNamespace;
            string fragment = $"<w:smartTag w:element=\"a\" xmlns:w=\"{w}\"><w:r><w:t>a &amp; b</w:t></w:r>"
                              + "<w:smartTag w:element=\"b\"><w:r><w:t>c</w:t></w:r></w:smartTag></w:smartTag>";

            string opened = Tetitulz.Pdf.DocxToPdf.Rendering.MarkupWrappers.WithoutSmartTags(fragment);
            var holder = new DocumentFormat.OpenXml.Wordprocessing.Paragraph { InnerXml = opened };

            Assert.DoesNotContain("smartTag", opened, StringComparison.Ordinal);
            Assert.Contains("a &amp; b", opened, StringComparison.Ordinal);
            Assert.Equal(new[] { "a & b", "c" },
                holder.Elements<DocumentFormat.OpenXml.Wordprocessing.Run>().Select(r => r.InnerText).ToArray());
        }

        /// <summary>
        /// Two hundred nested smart tags around a run cost what one does, on both paths — not each level serialised
        /// and parsed again on its own, the subtree's size times its depth. The run is
        /// drawn either way; the conversion's allocation on this thread is compared with one level's.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void DeeplyNestedSmartTags_CostWhatOneDoes(bool legacy)
        {
            string run = "<w:r><w:t>" + string.Concat(Enumerable.Repeat("Nested ", 3000)) + "</w:t></w:r>";
            string Nested(int depth)
                => "<w:p>" + string.Concat(Enumerable.Repeat("<w:smartTag w:element=\"x\">", depth)) + run
                   + string.Concat(Enumerable.Repeat("</w:smartTag>", depth)) + "</w:p>";

            long Allocated(int depth, out string text)
            {
                byte[] docx = HostileDocxInputTests.RawDocx(Document(Nested(depth)));
                long before = GC.GetAllocatedBytesForCurrentThread();
                byte[] pdf = DocxToPdfConverter.ConvertToBytes(docx, Options(legacy));
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                text = Text(pdf);
                return allocated;
            }

            long one = Allocated(1, out _);
            long deep = Allocated(200, out string deepText);

            Assert.Contains("Nested Nested", deepText, StringComparison.Ordinal);
            Assert.True(deep < one * 3 / 2 + 4L * 1024 * 1024, $"200 levels allocated {deep} bytes, one level {one}");
        }

        /// <summary>
        /// A tracked deletion and a move's source are not shown — beside a run that is, the potency — and the Builder
        /// path does not report their text as content it could not map.
        /// </summary>
        [Theory]
        [InlineData("<w:del w:id=\"1\" w:author=\"a\"><w:r><w:delText>Gone</w:delText></w:r></w:del>", true)]
        [InlineData("<w:del w:id=\"1\" w:author=\"a\"><w:r><w:delText>Gone</w:delText></w:r></w:del>", false)]
        [InlineData("<w:moveFrom w:id=\"1\" w:author=\"a\"><w:r><w:t>Gone</w:t></w:r></w:moveFrom>", true)]
        [InlineData("<w:moveFrom w:id=\"1\" w:author=\"a\"><w:r><w:t>Gone</w:t></w:r></w:moveFrom>", false)]
        public void ARemovedRun_IsNotDrawnNorReportedLost(string removed, bool legacy)
        {
            var seen = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) seen.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            string text;
            try
            {
                text = Text(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(
                    Document($"<w:p><w:r><w:t>Kept</w:t></w:r>{removed}</w:p>")), Options(legacy)));
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }
            Assert.Contains("Kept", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Gone", text, StringComparison.Ordinal);
            Assert.DoesNotContain(seen, m => m.Contains("Gone", StringComparison.Ordinal));
        }

        public static IEnumerable<object[]> StructureWrappers()
        {
            string Tc(string t) => $"<w:tc><w:p><w:r><w:t>{t}</w:t></w:r></w:p></w:tc>";
            var cases = new[]
            {
                ("row in sdt", $"<w:tbl><w:tr>{Tc("Plain")}</w:tr><w:sdt><w:sdtContent><w:tr>{Tc("Wrapped")}</w:tr></w:sdtContent></w:sdt></w:tbl><w:p/>"),
                ("cell in sdt", $"<w:tbl><w:tr>{Tc("Plain")}<w:sdt><w:sdtContent>{Tc("Wrapped")}</w:sdtContent></w:sdt></w:tr></w:tbl><w:p/>"),
                ("row in customXml", $"<w:tbl><w:tr>{Tc("Plain")}</w:tr><w:customXml w:element=\"r\"><w:tr>{Tc("Wrapped")}</w:tr></w:customXml></w:tbl><w:p/>"),
                ("cell in customXml", $"<w:tbl><w:tr>{Tc("Plain")}<w:customXml w:element=\"c\">{Tc("Wrapped")}</w:customXml></w:tr></w:tbl><w:p/>"),
                ("block customXml", "<w:p><w:r><w:t>Plain</w:t></w:r></w:p><w:customXml w:element=\"b\"><w:p><w:r><w:t>Wrapped</w:t></w:r></w:p></w:customXml>"),
                ("block sdt in a cell", $"<w:tbl><w:tr>{Tc("Plain")}<w:tc><w:sdt><w:sdtContent><w:p><w:r><w:t>Wrapped</w:t></w:r></w:p></w:sdtContent></w:sdt></w:tc></w:tr></w:tbl><w:p/>"),
            };
            foreach (var (name, xml) in cases)
                foreach (bool legacy in new[] { true, false })
                    yield return new object[] { name, xml, legacy };
        }

        /// <summary>A row, a cell or a paragraph inside a content control or custom XML is drawn on both paths, beside one that is not, the control.</summary>
        [Theory]
        [MemberData(nameof(StructureWrappers))]
        public void ContentInsideAStructureWrapper_IsDrawn(string name, string body, bool legacy)
        {
            string text = Text(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(Document(body)), Options(legacy)));
            Assert.Contains("Plain", text, StringComparison.Ordinal);
            Assert.True(text.Contains("Wrapped", StringComparison.Ordinal), $"{name}: '{text}'");
        }

        /// <summary>
        /// An external hyperlink is written as a link on both paths — the Builder path read only a hyperlink's
        /// anchor, and wrote none; an internal one, the control, stays a GoTo.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AnExternalHyperlink_IsALinkOnBothPaths(bool legacy)
        {
            using var ms = new MemoryStream();
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var rel = main.AddHyperlinkRelationship(new Uri("https://example.com/target"), true);
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.Hyperlink(new DocumentFormat.OpenXml.Wordprocessing.Run(
                            new DocumentFormat.OpenXml.Wordprocessing.Text("Visit"))) { Id = rel.Id })));
                main.Document.Save();
            }
            string raw = Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(ms.ToArray(), Options(legacy)));
            Assert.Contains("(https://example.com/target)", raw, StringComparison.Ordinal);
        }

        /// <summary>
        /// A hyperlink in a header leads where its relationship in the header's own part says: the renderer looked
        /// relationships up in the main document part, where a header's are not. Both paths; the body's control is
        /// <see cref="AnExternalHyperlink_IsALinkOnBothPaths"/>.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AHeadersHyperlink_LeadsWhereItsOwnPartSays(bool legacy)
        {
            using var ms = new MemoryStream();
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var header = main.AddNewPart<DocumentFormat.OpenXml.Packaging.HeaderPart>();
                var rel = header.AddHyperlinkRelationship(new Uri("https://example.com/from-header"), true);
                header.Header = new DocumentFormat.OpenXml.Wordprocessing.Header(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                    new DocumentFormat.OpenXml.Wordprocessing.Hyperlink(new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text("HeaderLink"))) { Id = rel.Id }));
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text("Body"))),
                    new DocumentFormat.OpenXml.Wordprocessing.SectionProperties(new DocumentFormat.OpenXml.Wordprocessing.HeaderReference
                    { Type = DocumentFormat.OpenXml.Wordprocessing.HeaderFooterValues.Default, Id = main.GetIdOfPart(header) })));
                main.Document.Save();
            }
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(ms.ToArray(), Options(legacy));
            Assert.Contains("HeaderLink", Text(pdf), StringComparison.Ordinal);
            Assert.Contains("(https://example.com/from-header)", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        }

        /// <summary>
        /// A direction wrapper is applied on both paths and reported on neither, since both resolve a line's text
        /// whole. The override's text reversed is the potency check.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void ADirectionOverride_IsAppliedAndNotReported_OnBothPaths(bool legacy)
        {
            var seen = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) seen.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            string text;
            try
            {
                text = Text(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(Document(
                    "<w:p><w:bdo w:val=\"rtl\"><w:r><w:t>One</w:t></w:r></w:bdo><w:dir w:val=\"rtl\"><w:r><w:t>Two</w:t></w:r></w:dir></w:p>")), Options(legacy)));
            }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }
            Assert.Contains("enO", text, StringComparison.Ordinal);
            Assert.DoesNotContain(seen, m => m.Contains("bidirectional algorithm", StringComparison.Ordinal));
        }

        /// <summary>
        /// <c>w:bdo</c> is an LRO or RLO and <c>w:dir</c> an LRE or RLE, each closed by a PDF (ECMA-376-1:2016
        /// §17.3.2.3, §17.3.2.8), on both paths, in a body paragraph and in a table cell: the clause's own example —
        /// <c>part number: </c> then an RTL override of <c>ad-326D-FG</c> — reads <c>part number: GF-D623-da</c>; an
        /// override reverses the runs it spans as one; an RTL embedding orders its numbers right to left; an LTR
        /// wrapper, stated or by default, leaves its text as written — the control.
        /// </summary>
        [Theory]
        [InlineData("<w:r><w:t xml:space=\"preserve\">part number: </w:t></w:r><w:bdo w:val=\"rtl\"><w:r><w:t>ad-326D-FG</w:t></w:r></w:bdo>", "part number: GF-D623-da")]
        [InlineData("<w:bdo w:val=\"rtl\"><w:r><w:t>ab</w:t></w:r><w:r><w:t>cd</w:t></w:r></w:bdo>", "dcba")]
        [InlineData("<w:bdo w:val=\"rtl\"><w:r><w:t>ab</w:t></w:r></w:bdo><w:r><w:t>cd</w:t></w:r>", "bacd")]   // the override ends with its wrapper
        [InlineData("<w:dir w:val=\"rtl\"><w:r><w:t>1 2 3</w:t></w:r></w:dir>", "3 2 1")]
        [InlineData("<w:dir w:val=\"ltr\"><w:r><w:t>1 2 3</w:t></w:r></w:dir>", "1 2 3")]
        [InlineData("<w:bdo><w:r><w:t>ab</w:t></w:r><w:r><w:t>cd</w:t></w:r></w:bdo>", "abcd")]
        [InlineData("<w:r><w:t>ab</w:t></w:r><w:r><w:t>cd</w:t></w:r>", "abcd")]
        public void ABidiWrapper_OrdersItsText_OnBothPathsAndInACell(string runs, string expected)
        {
            foreach (bool legacy in new[] { true, false })
            {
                string body = Text(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(Document($"<w:p>{runs}</w:p>")), Options(legacy)));
                Assert.Equal(expected, body.Trim());
                string cell = Text(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(Document(Cell($"<w:p>{runs}</w:p>"))), Options(legacy)));
                Assert.Equal(expected, cell.Trim());
            }
        }

        /// <summary>
        /// An override of shaped text draws its characters in the override's direction whatever their own, which a
        /// level alone does not say - on both paths. Read back through the plain extractor, which takes a
        /// line's drawn order as visual and its direction from the first strong character so drawn: Hebrew held
        /// left to right is drawn alef, bet, gimel from the left and reads back reversed; "alef-bet cd" held right
        /// to left is drawn "dc" then the two letters reversed, and reads back with "dc" first. The control is each
        /// run with no wrapper.
        /// </summary>
        /// <remarks>
        /// Core's BidiControlStack tells the Builder path of the override: a token shaped once in its paragraph's
        /// direction would draw Hebrew under an override as it would without one.
        /// </remarks>
        [HebrewCapableDocxFontFact]
        public void AnOverrideOfShapedText_DrawsItInTheOverridesDirection_OnBothPaths()
        {
            const string held = "<w:r><w:rPr><w:rtl/></w:rPr><w:t xml:space=\"preserve\">{0}</w:t></w:r>";
            // In a body paragraph and in a table cell, whose legacy layout has its own piece function, on both paths.
            string Read(string inner, bool cell, bool legacy)
                => Text(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(Document(
                    cell ? Cell($"<w:p>{inner}</w:p>") : $"<w:p>{inner}</w:p>")), Options(legacy))).Trim();
            foreach (var (direction, text, overridden, plain) in new[]
                     {
                         ("ltr", "\u05D0\u05D1\u05D2", "\u05D2\u05D1\u05D0", "\u05D0\u05D1\u05D2"),
                         ("rtl", "\u05D0\u05D1 cd", "dc \u05D0\u05D1", "cd \u05D0\u05D1"),
                     })
            {
                string run = string.Format(System.Globalization.CultureInfo.InvariantCulture, held, text);
                foreach (bool legacy in new[] { true, false })
                    foreach (bool cell in new[] { false, true })
                    {
                        Assert.Equal(overridden, Read($"<w:bdo w:val=\"{direction}\">{run}</w:bdo>", cell, legacy));
                        Assert.Equal(plain, Read(run, cell, legacy));
                    }
            }
        }

        /// <summary>
        /// A wrapper's controls take no room: a right-aligned line ends at the margin with or without one around
        /// its run, so both start at the same place — in a body paragraph and in a table cell.
        /// </summary>
        [Fact]
        public void AWrappersControls_TakeNoRoomOnTheLine()
        {
            foreach (bool cell in new[] { false, true })
            {
                string Paragraph(string inner) => $"<w:p><w:pPr><w:jc w:val=\"right\"/></w:pPr>{inner}</w:p>";
                double StartOf(string inner)
                    => LegacyLineStart(Document(cell ? Cell(Paragraph(inner)) : Paragraph(inner)), "(Edge) Tj");

                double plain = StartOf("<w:r><w:t>Edge</w:t></w:r>");
                Assert.True(plain > 400, $"right aligned at {plain}");
                Assert.Equal(plain, StartOf("<w:dir w:val=\"ltr\"><w:r><w:t>Edge</w:t></w:r></w:dir>"), 2);
            }
        }

        /// <summary>
        /// A numbered paragraph opening with a wrapper takes its marker's size from its first run of text, not the
        /// wrapper's control: the marker of a 20-point run is drawn at 20 points.
        /// </summary>
        [Fact]
        public void AListMarkerBeforeAWrapper_TakesTheSizeOfTheFirstRun()
        {
            const string numbering = "<w:numbering xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\">"
                + "<w:abstractNum w:abstractNumId=\"0\"><w:lvl w:ilvl=\"0\"><w:start w:val=\"1\"/><w:numFmt w:val=\"decimal\"/>"
                + "<w:lvlText w:val=\"%1.\"/></w:lvl></w:abstractNum><w:num w:numId=\"1\"><w:abstractNumId w:val=\"0\"/></w:num></w:numbering>";
            string body = "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr>"
                + "<w:dir w:val=\"ltr\"><w:r><w:rPr><w:sz w:val=\"40\"/></w:rPr><w:t>Big</w:t></w:r></w:dir></w:p>";

            string raw = Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(
                HostileDocxInputTests.RawDocx(Document(body), numbering), Options(true)));
            int marker = raw.IndexOf("(1. ) Tj", StringComparison.Ordinal);
            Assert.True(marker >= 0, "marker not drawn");
            var sizes = System.Text.RegularExpressions.Regex.Matches(raw.Substring(0, marker), @"/\w+\s+(-?[\d.]+)\s+Tf");
            Assert.NotEmpty(sizes);
            Assert.Equal(20.0, double.Parse(sizes[sizes.Count - 1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), 2);
        }

        /// <summary>
        /// A cell holding nothing but an empty wrapper is as tall as an empty cell: its controls make no line of
        /// their own, so the paragraph after the table starts where it would after an empty one.
        /// </summary>
        [Fact]
        public void ACellOfAnEmptyWrapper_IsAsTallAsAnEmptyCell()
        {
            double After(string cellContent)
            {
                string raw = Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(Document(
                    $"<w:tbl><w:tr><w:tc>{cellContent}</w:tc></w:tr></w:tbl><w:p><w:r><w:t>After</w:t></w:r></w:p>")), Options(true)));
                int at = raw.IndexOf("(After) Tj", StringComparison.Ordinal);
                Assert.True(at >= 0, "(After) Tj not drawn");
                var starts = System.Text.RegularExpressions.Regex.Matches(raw.Substring(0, at), @"BT\s+(-?[\d.]+)\s+(-?[\d.]+)\s+Td");
                return double.Parse(starts[starts.Count - 1].Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            }

            Assert.Equal(After("<w:p/>"), After("<w:p><w:dir w:val=\"rtl\"></w:dir></w:p>"), 2);
        }

        /// <summary>Where the legacy renderer starts the text object that draws <paramref name="literal"/> first.</summary>
        private static double LegacyLineStart(string document, string literal)
        {
            string raw = Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(document), Options(true)));
            int at = raw.IndexOf(literal, StringComparison.Ordinal);
            Assert.True(at >= 0, literal + " not drawn");
            var starts = System.Text.RegularExpressions.Regex.Matches(raw.Substring(0, at), @"BT\s+(-?[\d.]+)\s+(-?[\d.]+)\s+Td");
            Assert.NotEmpty(starts);
            return double.Parse(starts[starts.Count - 1].Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// A right-to-left embedding wrapping onto several lines orders every line right to left: the legacy path
        /// resolves a line at a time, so the embedding a line leaves open is carried to the next. Each line's
        /// numbers then read largest first; carried nowhere, every line after the first read smallest first.
        /// </summary>
        [Fact]
        public void AnEmbeddingAcrossALineBreak_OrdersEveryLine_OnTheLegacyPath()
        {
            string numbers = string.Join(" ", Enumerable.Range(10, 90));
            string text = Text(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(Document(
                $"<w:p><w:dir w:val=\"rtl\"><w:r><w:t>{numbers}</w:t></w:r></w:dir></w:p>")), Options(true)));

            var lines = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            Assert.True(lines.Count >= 3, $"{lines.Count} lines: the numbers should wrap");
            foreach (string line in lines)
            {
                int[] values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                Assert.True(values.Length > 1, line);
                Assert.True(values.Zip(values.Skip(1), (a, b) => a > b).All(x => x), $"not right to left: {line}");
            }
        }

        /// <summary>
        /// On the Builder path a paragraph's bookmark is a link target: a hyperlink to <c>Target</c> leads to the
        /// paragraph holding it, though a <c>_Toc1</c> bookmark comes first there (the model keeps one name a
        /// paragraph, and the one a link names wins). Without a target, every link to a bookmark would be
        /// dropped. The control: a link to a bookmark no paragraph holds is no link.
        /// </summary>
        [Fact]
        public void ABookmarkALinkNames_IsItsTarget_OnTheBuilderPath()
        {
            string Raw(string anchor) => Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(Document(
                $"<w:p><w:hyperlink w:anchor=\"{anchor}\"><w:r><w:t>Go</w:t></w:r></w:hyperlink></w:p>"
                + "<w:p><w:bookmarkStart w:id=\"0\" w:name=\"_Toc1\"/><w:bookmarkEnd w:id=\"0\"/>"
                + "<w:bookmarkStart w:id=\"1\" w:name=\"Target\"/><w:bookmarkEnd w:id=\"1\"/><w:r><w:t>There</w:t></w:r></w:p>")), Options(false)));

            Assert.Matches(@"/S\s*/GoTo\s*/D\s*\(Target\)", Raw("Target"));
            Assert.DoesNotMatch(@"/Subtype\s*/Link", Raw("Elsewhere"));
        }

        /// <summary>
        /// A hyperlink in a table cell is a link on both paths — to a bookmark and to a URL — over the text it
        /// covers, and the rest of the cell's line is not.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AHyperlinkInATableCell_IsALinkOverItsText_OnBothPaths(bool legacy)
        {
            using var ms = new MemoryStream();
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var rel = main.AddHyperlinkRelationship(new Uri("https://example.com/from-cell"), true);
                var W = typeof(DocumentFormat.OpenXml.Wordprocessing.Run).Namespace;
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Table(new DocumentFormat.OpenXml.Wordprocessing.TableRow(
                        new DocumentFormat.OpenXml.Wordprocessing.TableCell(new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                            new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Plain ") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
                            new DocumentFormat.OpenXml.Wordprocessing.Hyperlink(new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text("Web"))) { Id = rel.Id },
                            new DocumentFormat.OpenXml.Wordprocessing.Hyperlink(new DocumentFormat.OpenXml.Wordprocessing.Run(
                                new DocumentFormat.OpenXml.Wordprocessing.Text(" Mark") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve })) { Anchor = "a" })))),
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(
                        new DocumentFormat.OpenXml.Wordprocessing.BookmarkStart { Id = "0", Name = "a" },
                        new DocumentFormat.OpenXml.Wordprocessing.BookmarkEnd { Id = "0" },
                        new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text("Target")))));
                main.Document.Save();
            }
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(ms.ToArray(), Options(legacy));
            string raw = Encoding.Latin1.GetString(pdf);

            Assert.Contains("Plain Web Mark", Text(pdf), StringComparison.Ordinal);
            Assert.Contains("(https://example.com/from-cell)", raw, StringComparison.Ordinal);
            // Two links — the URL and the bookmark — and none over "Plain".
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(raw, @"/Subtype\s*/Link").Count);
            // Each box covers its line of text — at least 8 pt tall at the default size — not a sliver
            // under the baseline.
            var rects = System.Text.RegularExpressions.Regex.Matches(raw,
                @"/Rect\s*\[\s*(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s*\]");
            Assert.Equal(2, rects.Count);
            foreach (System.Text.RegularExpressions.Match r in rects)
            {
                double bottom = double.Parse(r.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                double top = double.Parse(r.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                Assert.True(top - bottom >= 8, $"link box {r.Value}");
            }
        }

        /// <summary>
        /// A header's table and a paragraph inside a content control there: both paths draw both, in order — the
        /// Builder path through the header's blocks, where it once dropped both in silence and then reported the
        /// table's text as absent.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void AHeadersTableAndContentControl_AreDrawnOnBothPaths(bool legacy)
        {
            using var ms = new MemoryStream();
            using (var doc = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var header = main.AddNewPart<DocumentFormat.OpenXml.Packaging.HeaderPart>();
                using (var xml = new MemoryStream(Encoding.UTF8.GetBytes(
                    $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:hdr xmlns:w=\"{HostileDocxInputTests.WordNamespace}\">"
                    + "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Letterhead</w:t></w:r></w:p></w:tc></w:tr></w:tbl>"
                    + "<w:sdt><w:sdtContent><w:p><w:r><w:t>Controlled</w:t></w:r></w:p></w:sdtContent></w:sdt></w:hdr>")))
                    header.FeedData(xml);
                main.Document = new DocumentFormat.OpenXml.Wordprocessing.Document(new DocumentFormat.OpenXml.Wordprocessing.Body(
                    new DocumentFormat.OpenXml.Wordprocessing.Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(
                        new DocumentFormat.OpenXml.Wordprocessing.Text("Body"))),
                    new DocumentFormat.OpenXml.Wordprocessing.SectionProperties(new DocumentFormat.OpenXml.Wordprocessing.HeaderReference
                    { Type = DocumentFormat.OpenXml.Wordprocessing.HeaderFooterValues.Default, Id = main.GetIdOfPart(header) })));
                main.Document.Save();
            }

            var seen = new List<string>();
            int collectingThread = System.Environment.CurrentManagedThreadId;
            void Collect(string m) { if (System.Environment.CurrentManagedThreadId == collectingThread) seen.Add(m); }
            DocxToPdfConverter.DiagnosticReported += Collect;
            string text;
            try { text = Text(DocxToPdfConverter.ConvertToBytes(ms.ToArray(), Options(legacy))); }
            finally { DocxToPdfConverter.DiagnosticReported -= Collect; }

            int letterhead = text.IndexOf("Letterhead", StringComparison.Ordinal);
            Assert.True(letterhead >= 0, "the header's table is drawn");
            Assert.True(text.IndexOf("Controlled", StringComparison.Ordinal) > letterhead, "the content control's paragraph after it");
            Assert.True(text.IndexOf("Body", StringComparison.Ordinal) >= 0, "the body is drawn");
            Assert.DoesNotContain(seen, m => m.Contains("table in a header", StringComparison.Ordinal));
        }
    }
}
