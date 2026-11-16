// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Tetitulz.Pdf.DocxToPdf.Tests.Helpers
{
    internal static class MinimalDocxBuilder
    {
        public static byte[] CreateMinimalDocx(string textContent = "Hello World")
        {
            using (var ms = new MemoryStream())
            {
                using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
                {
                    var mainPart = doc.AddMainDocumentPart();
                    mainPart.Document = new Document(
                        new Body(
                            new Paragraph(
                                new Run(new Text(textContent)))));
                    mainPart.Document.Save();
                }
                return ms.ToArray();
            }
        }

        public static byte[] CreateDocxWithStyles(string text, bool bold = false, bool italic = false,
            string fontSize = "24")
        {
            using (var ms = new MemoryStream())
            {
                using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
                {
                    var mainPart = doc.AddMainDocumentPart();

                    var runProps = new RunProperties();
                    if (bold) runProps.Append(new Bold());
                    if (italic) runProps.Append(new Italic());
                    if (fontSize != null) runProps.Append(new FontSize { Val = fontSize });

                    var run = new Run(runProps, new Text(text));

                    mainPart.Document = new Document(
                        new Body(
                            new Paragraph(run)));
                    mainPart.Document.Save();
                }
                return ms.ToArray();
            }
        }

        public static byte[] CreateDocxWithTable(int rows, int cols)
        {
            using (var ms = new MemoryStream())
            {
                using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
                {
                    var mainPart = doc.AddMainDocumentPart();
                    var table = new Table();

                    for (int r = 0; r < rows; r++)
                    {
                        var row = new TableRow();
                        for (int c = 0; c < cols; c++)
                        {
                            var cell = new TableCell(
                                new Paragraph(
                                    new Run(new Text($"R{r + 1}C{c + 1}"))));
                            row.Append(cell);
                        }
                        table.Append(row);
                    }

                    mainPart.Document = new Document(new Body(table));
                    mainPart.Document.Save();
                }
                return ms.ToArray();
            }
        }

        public static byte[] CreateMultiParagraphDocx(int paragraphCount)
        {
            using (var ms = new MemoryStream())
            {
                using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
                {
                    var mainPart = doc.AddMainDocumentPart();
                    var body = new Body();

                    for (int i = 1; i <= paragraphCount; i++)
                    {
                        body.Append(new Paragraph(
                            new Run(new Text($"Paragraph {i}. This is sample text for testing."))));
                    }

                    mainPart.Document = new Document(body);
                    mainPart.Document.Save();
                }
                return ms.ToArray();
            }
        }
    }
}
