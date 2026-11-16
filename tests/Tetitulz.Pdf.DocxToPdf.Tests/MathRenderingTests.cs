// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// What survives of an equation as <b>extracted text</b>.
    ///
    /// <para>
    /// Body equations are now typeset in two dimensions, so the fraction-bar and caret markers the
    /// linear fallback produced are gone from that path — the tests that named them have been
    /// deleted rather than reworded, because the behaviour they described must no longer exist.
    /// Geometry is asserted in <c>MathTypesettingTests</c>; what stays here is the claim that the
    /// equation's own characters still reach the page, plus the table-cell path, which is still
    /// linearised and honestly named so.
    /// </para>
    /// </summary>
    public class MathRenderingTests
    {
        private const string Ns =
            "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
            "xmlns:m=\"http://schemas.openxmlformats.org/officeDocument/2006/math\"";

        private static string MathPara(string omathInner)
            => $@"<w:p {Ns}><m:oMath>{omathInner}</m:oMath></w:p>";

        private static string MathRun(string text) => $"<m:r><m:t>{text}</m:t></m:r>";

        private static string ExtractText(string pXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                main.Document = new Document(new Body(new Paragraph(pXml)));
                main.Document.Save();
            }
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(ms.ToArray(),
                new DocxToPdfOptions { CompressStreams = false });

            using var ps = new MemoryStream(pdf);
            using var pdoc = PdfDocument.Open(ps);
            return PdfTextExtractor.ExtractText(pdoc);
        }

        [Fact]
        public void TypesetFraction_StillPutsBothOperandsOnThePage()
        {
            // The bar is now a drawn rule rather than a "/" character, so the claim that survives
            // is about the operands — the rule's geometry is asserted in MathTypesettingTests.
            string text = ExtractText(MathPara(
                $"<m:f><m:num>{MathRun("a")}</m:num><m:den>{MathRun("b")}</m:den></m:f>"));

            Assert.Contains("a", text);
            Assert.Contains("b", text);
        }

        [Fact]
        public void PlainMathRun_TextSurvives()
        {
            string text = ExtractText(MathPara(MathRun("E=mc") +
                "<m:sSup><m:e>" + MathRun("") + "</m:e><m:sup>" + MathRun("2") + "</m:sup></m:sSup>"));

            Assert.Contains("E=mc", text);
            Assert.Contains("2", text);
        }

        [Fact]
        public void Fraction_InTableCell_KeepsBothOperands()
        {
            // Cell equations are typeset now, like body ones, so the "/" the linear fallback
            // produced is gone. The claim that survives is that neither operand was lost; the
            // stacked geometry is asserted in MathTypesettingTests.
            string text = ExtractTableText(MathPara(
                $"<m:f><m:num>{MathRun("p")}</m:num><m:den>{MathRun("q")}</m:den></m:f>"));

            Assert.Contains("p", text);
            Assert.Contains("q", text);
        }

        private static string ExtractTableText(string pXml)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();
                var table = new Table(
                    new TableGrid(new GridColumn { Width = "9000" }),
                    new TableRow(new TableCell(new Paragraph(pXml))));
                main.Document = new Document(new Body(table));
                main.Document.Save();
            }
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(ms.ToArray(),
                new DocxToPdfOptions { CompressStreams = false });

            using var ps = new MemoryStream(pdf);
            using var pdoc = PdfDocument.Open(ps);
            return PdfTextExtractor.ExtractText(pdoc);
        }
    }
}
