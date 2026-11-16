// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Table-style conditional shading (banding): a table that references a table style
    /// gets its header/total rows, banded rows/columns, and corner cells shaded from the
    /// style's <c>w:tblStylePr</c> entries — gated by <c>w:tblLook</c>. Conversions use
    /// uncompressed streams so the cell-background fill operators can be asserted directly.
    /// </summary>
    public class TableBandingTests
    {
        private static readonly DocxToPdfOptions NoCompress = new DocxToPdfOptions { CompressStreams = false };

        // Builds a 1-column table of `rows` rows referencing a table style that shades the
        // header row (headerFill) and band-1 horizontal rows (bandFill). Optionally puts a
        // direct <w:shd> on row 0 to test precedence.
        private static byte[] BuildStyledTable(int rows, string headerFill, string bandFill,
            bool firstRow, bool hBand, string directRow0Fill = null,
            bool headerBold = false, string headerTextColor = null, string headerBottomBorderColor = null)
        {
            using var ms = new MemoryStream();
            using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
            {
                var main = doc.AddMainDocumentPart();

                // firstRow conditional: optional bold/coloured run formatting (in schema order:
                // rPr before tcPr) plus the header shading.
                var firstRowTsp = new TableStyleProperties { Type = TableStyleOverrideValues.FirstRow };
                if (headerBold || headerTextColor != null)
                {
                    var rpr = new RunPropertiesBaseStyle();
                    if (headerBold) rpr.Append(new Bold());
                    if (headerTextColor != null) rpr.Append(new Color { Val = headerTextColor });
                    firstRowTsp.Append(rpr);
                }
                var headerTcPr = new TableStyleConditionalFormattingTableCellProperties();
                if (headerBottomBorderColor != null) // tcBorders precede shd in schema order
                    headerTcPr.Append(new TableCellBorders(
                        new BottomBorder { Val = BorderValues.Single, Size = 24, Color = headerBottomBorderColor }));
                headerTcPr.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = headerFill });
                firstRowTsp.Append(headerTcPr);

                var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
                stylesPart.Styles = new Styles(
                    new Style(
                        new StyleName { Val = "BandedStyle" },
                        firstRowTsp,
                        new TableStyleProperties(
                            new TableStyleConditionalFormattingTableCellProperties(
                                new Shading { Val = ShadingPatternValues.Clear, Fill = bandFill }))
                        { Type = TableStyleOverrideValues.Band1Horizontal })
                    { Type = StyleValues.Table, StyleId = "BandedStyle" });
                stylesPart.Styles.Save();

                var tblLook = new TableLook
                {
                    FirstRow = OnOffValue.FromBoolean(firstRow),
                    LastRow = OnOffValue.FromBoolean(false),
                    FirstColumn = OnOffValue.FromBoolean(false),
                    LastColumn = OnOffValue.FromBoolean(false),
                    NoHorizontalBand = OnOffValue.FromBoolean(!hBand),
                    NoVerticalBand = OnOffValue.FromBoolean(true),
                };

                var table = new Table(new TableProperties(new TableStyle { Val = "BandedStyle" }, tblLook));
                for (int r = 0; r < rows; r++)
                {
                    TableCell cell = (r == 0 && directRow0Fill != null)
                        ? new TableCell(
                            new TableCellProperties(new Shading { Val = ShadingPatternValues.Clear, Fill = directRow0Fill }),
                            new Paragraph(new Run(new Text($"R{r}"))))
                        : new TableCell(new Paragraph(new Run(new Text($"R{r}"))));
                    table.Append(new TableRow(cell));
                }

                main.Document = new Document(new Body(table));
                main.Document.Save();
            }
            return DocxToPdfConverter.ConvertToBytes(ms.ToArray(), NoCompress);
        }

        // The PDF non-stroking-colour operator the renderer emits for an RRGGBB fill.
        private static string ExpectedRg(string hex)
        {
            int r = Convert.ToInt32(hex.Substring(0, 2), 16);
            int g = Convert.ToInt32(hex.Substring(2, 2), 16);
            int b = Convert.ToInt32(hex.Substring(4, 2), 16);
            return string.Format(CultureInfo.InvariantCulture, "{0:F3} {1:F3} {2:F3} rg",
                r / 255f, g / 255f, b / 255f);
        }

        [Fact]
        public void HeaderRowAndBandedRow_AreShadedFromTableStyle()
        {
            // 3 rows: row0=header, row1=band1, row2=band2 (no band2 style → unshaded).
            byte[] pdf = BuildStyledTable(3, headerFill: "4472C4", bandFill: "D9E2F3",
                firstRow: true, hBand: true);
            string content = Encoding.Latin1.GetString(pdf);

            Assert.Contains(ExpectedRg("4472C4"), content); // header row fill from style
            Assert.Contains(ExpectedRg("D9E2F3"), content); // band-1 row fill from style
        }

        [Fact]
        public void TblLook_DisablingHeaderAndBands_SuppressesStyleShading()
        {
            byte[] pdf = BuildStyledTable(3, headerFill: "4472C4", bandFill: "D9E2F3",
                firstRow: false, hBand: false);
            string content = Encoding.Latin1.GetString(pdf);

            Assert.DoesNotContain(ExpectedRg("4472C4"), content);
            Assert.DoesNotContain(ExpectedRg("D9E2F3"), content);
        }

        [Fact]
        public void DirectCellShading_TakesPrecedenceOverTableStyle()
        {
            // Row 0 carries a direct w:shd (FF0000); it must win over the style's header fill.
            byte[] pdf = BuildStyledTable(2, headerFill: "4472C4", bandFill: "D9E2F3",
                firstRow: true, hBand: true, directRow0Fill: "FF0000");
            string content = Encoding.Latin1.GetString(pdf);

            Assert.Contains(ExpectedRg("FF0000"), content);       // direct cell fill used
            Assert.DoesNotContain(ExpectedRg("4472C4"), content); // style header fill overridden
        }

        [Fact]
        public void HeaderRow_GetsConditionalBoldAndColor_FromTableStyle()
        {
            // The style's firstRow rPr makes the header text bold + white; body rows stay plain.
            byte[] pdf = BuildStyledTable(2, headerFill: "203864", bandFill: "D9E2F3",
                firstRow: true, hBand: true, headerBold: true, headerTextColor: "FFFFFF");
            string content = Encoding.Latin1.GetString(pdf);

            // The face each row is SET in, not a name somewhere in the file.
            Assert.Equal("Helvetica-Bold", Tetitulz.Pdf.TestHelpers.PdfContentProbe.BaseFontOf(pdf, "R0"));   // header run rendered bold
            Assert.Equal("Helvetica", Tetitulz.Pdf.TestHelpers.PdfContentProbe.BaseFontOf(pdf, "R1"));        // body row plain
            Assert.Contains("1.000 1.000 1.000 rg", content);    // header text coloured white
        }

        [Fact]
        public void HeaderRow_GetsConditionalBottomBorder_FromTableStyle()
        {
            // The style's firstRow tcBorders draws a red bottom rule under the header row.
            byte[] pdf = BuildStyledTable(2, headerFill: "4472C4", bandFill: "D9E2F3",
                firstRow: true, hBand: true, headerBottomBorderColor: "FF0000");
            string content = Encoding.Latin1.GetString(pdf);

            Assert.Contains("1.000 0.000 0.000 RG", content); // red stroking colour for the header border
        }
    }
}
