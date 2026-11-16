// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Opt-in Tagged PDF (<see cref="DocxToPdfOptions.TaggedPdf"/>): body paragraphs/headings are
    /// wrapped in marked content and exposed via a /StructTreeRoot (Document → P / Hn) with a parent
    /// tree. Default output stays untagged and byte-for-byte unchanged.
    /// </summary>
    public class TaggedPdfTests
    {
        private static byte[] Convert(bool tagged, Action<Body> build)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var body = new Body();
                build(body);
                main.Document = new Document(body);
                main.Document.Save();
            }
            return DocxToPdfConverter.ConvertToBytes(ms.ToArray(),
                new DocxToPdfOptions { CompressStreams = false, TaggedPdf = tagged });
        }

        [Fact]
        public void TaggedPdf_EmitsStructureTreeAndMarkedContent()
        {
            byte[] pdf = Convert(tagged: true, body =>
            {
                body.Append(new Paragraph(
                    new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
                    new Run(new Text("Title"))));
                body.Append(new Paragraph(new Run(new Text("Body paragraph text."))));
            });
            string c = Encoding.Latin1.GetString(pdf);

            Assert.Contains("/MarkInfo", c);
            Assert.Contains("/Marked true", c);
            Assert.Contains("/StructTreeRoot", c);
            Assert.Contains("/Document", c);          // document structure element
            Assert.Contains("/H1", c);                // heading element + its marked-content tag
            Assert.Contains("/S /P", c);              // a paragraph structure element
            Assert.Contains("/MCID 0", c);            // first marked-content id on the page
            Assert.Contains("BDC", c);                // marked content begin
            Assert.Contains("EMC", c);                // marked content end
            Assert.Contains("/ParentTree", c);        // MCID → element number tree
            Assert.Contains("/StructParents", c);     // page key into the parent tree
        }

        /// <summary>
        /// Each line's marked content is numbered on the page it is drawn on: a paragraph on page two
        /// starts that page's identifiers at zero and its element points at page two. The page was found
        /// by searching the page list once a line (Wave 22, DocxToPdf-2:1); in body flow it is the last
        /// page, which is now asked first.
        /// </summary>
        [Fact]
        public void TaggedPdf_NumbersEachPagesMarkedContentOnThatPage()
        {
            byte[] pdf = Convert(tagged: true, body =>
            {
                body.Append(new Paragraph(new Run(new Text("First page."))));
                body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page }), new Run(new Text("Second page."))));
            });

            using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
            Assert.Equal(2, doc.Pages.Count);
            var pages = new System.Collections.Generic.List<int>();
            var firstMcids = new System.Collections.Generic.List<int>();
            for (int n = 1; n < 400; n++)
            {
                if (!(doc.GetObject(n) is Tetitulz.Pdf.Core.Objects.PdfDictionary d)) continue;
                if (!(d[Tetitulz.Pdf.Core.Objects.PdfName.S] is Tetitulz.Pdf.Core.Objects.PdfName s) || s.Value != "P") continue;
                var pg = (Tetitulz.Pdf.Core.Objects.PdfReference)d[Tetitulz.Pdf.Core.Objects.PdfName.Pg];
                pages.Add(pg.ReferenceObjectNumber);
                object k = doc.ResolveObject(d[Tetitulz.Pdf.Core.Objects.PdfName.K]);
                var first = k is Tetitulz.Pdf.Core.Objects.PdfArray a ? a[0] : (Tetitulz.Pdf.Core.Objects.PdfObject)k;
                firstMcids.Add(((Tetitulz.Pdf.Core.Objects.PdfNumber)first).IntValue);
            }

            Assert.Equal(new[] { doc.Pages[0].Dictionary.ObjectNumber, doc.Pages[1].Dictionary.ObjectNumber }, pages.ToArray());
            Assert.Equal(new[] { 0, 0 }, firstMcids.ToArray());
        }

        [Fact]
        public void Untagged_ByDefault_NoStructureTree()
        {
            byte[] pdf = Convert(tagged: false, body =>
                body.Append(new Paragraph(new Run(new Text("Plain.")))));
            string c = Encoding.Latin1.GetString(pdf);

            Assert.DoesNotContain("/StructTreeRoot", c);
            Assert.DoesNotContain("/MarkInfo", c);
            Assert.DoesNotContain("BDC", c);
        }
    }
}
