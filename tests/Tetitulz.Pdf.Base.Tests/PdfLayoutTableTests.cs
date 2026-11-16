// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Layout;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests
{
    public class PdfLayoutTableTests
    {
        // 1. Builder with no columns produces no cells (builds without crash).
        [Fact]
        public void PdfTableBuilder_NoColumns_BuildsEmpty()
        {
            var builder = new PdfTableBuilder();
            var doc = new PdfLayoutDocument();
            var page = doc.AddPage();

            // Build should succeed and return the original StartY (no columns = no-op).
            float resultY = builder.Build(page);
            Assert.Equal(builder.StartY, resultY);
        }

        // 2. AddColumn increments ColumnCount.
        [Fact]
        public void PdfTableBuilder_AddColumn_CountIncreases()
        {
            var builder = new PdfTableBuilder();
            Assert.Equal(0, builder.ColumnCount);

            builder.AddColumn(100f);
            Assert.Equal(1, builder.ColumnCount);

            builder.AddColumn(50f, 150f);
            Assert.Equal(2, builder.ColumnCount);
        }

        // 3. AddRow returns a PdfTableRowBuilder.
        [Fact]
        public void PdfTableBuilder_AddRow_ReturnsRowBuilder()
        {
            var builder = new PdfTableBuilder();
            var rowBuilder = builder.AddRow();
            Assert.NotNull(rowBuilder);
            Assert.IsType<PdfTableRowBuilder>(rowBuilder);
        }

        // 4. AddCell on a row builder returns the same row builder (fluent).
        [Fact]
        public void PdfTableRowBuilder_AddCell_ReturnsRowBuilder()
        {
            var builder = new PdfTableBuilder();
            var rowBuilder = builder.AddRow();
            var returned = rowBuilder.AddCell("Hello");
            Assert.Same(rowBuilder, returned);
        }

        /// <summary>
        /// 5. Building a 2-column, 3-row table has to put all six cells on the page: a background
        /// fill, a border and the caller's text for each, at the declared column widths, in three
        /// equal rows. A <c>Build</c> that laid out nothing also throws nothing.
        /// </summary>
        [Fact]
        public void PdfTableBuilder_Build_DrawsABackgroundABorderAndTheTextOfEveryCell()
        {
            var doc = new PdfLayoutDocument();
            var page = doc.AddPage();

            var table = new PdfTableBuilder();
            table.AddColumn(150f).AddColumn(200f);
            table.AddRow().AddCell("Col A").AddCell("Col B");
            table.AddRow().AddCell("R2C1").AddCell("R2C2");
            table.AddRow().AddCell("R3C1").AddCell("R3C2");

            float finalY = table.Build(page);
            string cs = ContentStreamOf(page);

            var fills   = RectOps(cs, "f");
            var borders = RectOps(cs, "S");
            Assert.Equal(6, fills.Count);
            Assert.Equal(6, borders.Count);

            // The declared column widths reach every row, in the order they were added.
            Assert.Equal(new[] { 150f, 200f, 150f, 200f, 150f, 200f },
                         fills.ConvertAll(r => r.W).ToArray());

            foreach (string text in new[] { "Col A", "Col B", "R2C1", "R2C2", "R3C1", "R3C2" })
                Assert.Contains("(" + text + ") Tj", cs, StringComparison.Ordinal);

            // Three equal rows stacked downwards, and the returned cursor is the last row's bottom.
            float rowHeight = fills[0].H;
            Assert.True(rowHeight > 0, "rows must have a height");
            Assert.All(fills, r => Assert.Equal(rowHeight, r.H, 3));
            Assert.Equal(table.StartY - rowHeight,     fills[0].Y, 3);
            Assert.Equal(table.StartY - 3 * rowHeight, finalY,     3);
        }

        // 6. AddPage returns a PdfPageBuilder.
        [Fact]
        public void PdfLayoutDocument_AddPage_ReturnsPageBuilder()
        {
            var doc = new PdfLayoutDocument();
            var pageBuilder = doc.AddPage();
            Assert.NotNull(pageBuilder);
            Assert.IsType<PdfPageBuilder>(pageBuilder);
        }

        /// <summary>
        /// 7. Save produces a PDF that <b>reopens</b> — with the pages that were added, and with
        /// the text that was drawn on them. A sibling test already checks the <c>%PDF-</c> header;
        /// what neither it nor the old <c>ms.Length > 0</c> could tell you is whether the bytes
        /// after that header describe the document the caller built.
        /// </summary>
        [Fact]
        public void PdfLayoutDocument_Save_ProducesAReadableDocumentWithTheContentThatWasAdded()
        {
            var doc = new PdfLayoutDocument();
            doc.AddPage().DrawText("Saved", 72, 700, fontSize: 12, r: 0, g: 0, b: 0);
            doc.AddPage();

            using var ms = new MemoryStream();
            doc.Save(ms);

            ms.Position = 0;
            using var reopened = PdfDocument.Open(ms);
            Assert.Equal(2, reopened.Pages.Count);

            ms.Position = 0;
            Assert.Contains("Saved", PdfOperations.ExtractText(ms));
        }

        // 8. The output starts with the %PDF- header.
        [Fact]
        public void PdfLayoutDocument_Save_OutputStartsWithPdfHeader()
        {
            var doc = new PdfLayoutDocument();
            doc.AddPage();

            using var ms = new MemoryStream();
            doc.Save(ms);

            ms.Position = 0;
            var buf = new byte[5];
            ms.Read(buf, 0, 5);
            string header = Encoding.ASCII.GetString(buf);
            Assert.Equal("%PDF-", header);
        }

        /// <summary>
        /// 9. Every argument <c>DrawText</c> is given has to appear in the content stream it
        /// builds — string, position, size and fill colour. A non-black colour is used on purpose:
        /// with 0,0,0 the operands are indistinguishable from a hard-coded black.
        /// </summary>
        [Fact]
        public void PdfPageBuilder_DrawText_EmitsATextObjectCarryingEveryArgument()
        {
            var doc = new PdfLayoutDocument();
            var page = doc.AddPage();

            page.DrawText("Test text", 50f, 700f, 12f, r: 1f, g: 0f, b: 0.5f);

            string cs = ContentStreamOf(page);
            Assert.Contains("BT",               cs, StringComparison.Ordinal);
            Assert.Contains("/F1 12 Tf",        cs, StringComparison.Ordinal);
            Assert.Contains("1 0 0.5 rg",       cs, StringComparison.Ordinal);
            Assert.Contains("50 700 Td",        cs, StringComparison.Ordinal);
            Assert.Contains("(Test text) Tj",   cs, StringComparison.Ordinal);
            Assert.Contains("ET",               cs, StringComparison.Ordinal);
        }

        // 10. TableCellStyle defaults are reasonable.
        [Fact]
        public void TableCellStyle_Defaults_AreReasonable()
        {
            var style = new TableCellStyle();

            Assert.True(style.BorderThickness > 0, "BorderThickness should be positive");
            Assert.True(style.PaddingLeft >= 0, "PaddingLeft should be non-negative");
            // Default background should be white
            Assert.Equal(1f, style.BackgroundR);
            Assert.Equal(1f, style.BackgroundG);
            Assert.Equal(1f, style.BackgroundB);
            // Default text should be black
            Assert.Equal(0f, style.TextR);
            Assert.Equal(0f, style.TextG);
            Assert.Equal(0f, style.TextB);
        }

        // 11. Multiple rows are all rendered (no crash, resultY decreases).
        [Fact]
        public void PdfTableBuilder_MultipleRows_AllRendered()
        {
            var doc = new PdfLayoutDocument();
            var page = doc.AddPage();

            var table = new PdfTableBuilder { StartY = 700f };
            table.AddColumn(100f);

            table.AddRow().AddCell("Row 1");
            table.AddRow().AddCell("Row 2");
            table.AddRow().AddCell("Row 3");

            float finalY = table.Build(page);

            // After 3 rows the cursor should have moved down (Y decreased).
            Assert.True(finalY < table.StartY, "Y should decrease after rendering rows");
        }

        // 12. Fixed column width is honoured (column Width property matches what was passed in).
        [Fact]
        public void PdfTableBuilder_FixedColumnWidth_Honored()
        {
            var table = new PdfTableBuilder();
            table.AddColumn(120f);
            table.AddColumn(80f, 200f);

            // Access columns via the internal property
            var cols = table.Columns;

            Assert.Equal(2, cols.Count);
            Assert.Equal(120f, cols[0].Width);
            Assert.False(cols[0].IsFlexible);

            Assert.Equal(80f, cols[1].MinWidth);
            Assert.Equal(200f, cols[1].MaxWidth);
            Assert.True(cols[1].IsFlexible);
        }

        // ── Helpers ──────────────────────────────────────────────────────────
        //
        // PdfPageBuilder.Build() hands back the content stream it accumulated, so what the
        // layout code actually drew is readable without rendering or reparsing a file.

        private static string ContentStreamOf(PdfPageBuilder page) =>
            Encoding.Latin1.GetString(page.Build());

        /// <summary>Every <c>x y w h re &lt;op&gt;</c> rectangle painted with the given operator.</summary>
        private static List<(float X, float Y, float W, float H)> RectOps(string cs, string paintOp)
        {
            var rects = new List<(float, float, float, float)>();
            var re = new Regex(@"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re " + paintOp + @"\b");
            foreach (Match m in re.Matches(cs))
                rects.Add((F(m.Groups[1].Value), F(m.Groups[2].Value),
                           F(m.Groups[3].Value), F(m.Groups[4].Value)));
            return rects;

            static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
        }
    }
}
