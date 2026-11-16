// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tables from layouts and graphics the caller already extracted: the PDF-to-Office converters read the
/// layouts themselves, and the stream door extracted the document's text again inside, twice in all.
/// </summary>
public sealed class PdfTableExtractorLayoutsDoorTests
{
    private static byte[] GridPdf()
    {
        string[][] rows = { new[] { "Region", "Units", "Total" }, new[] { "North", "12", "340" }, new[] { "South", "45", "678" } };
        float[] columnX = { 60f, 220f, 380f };
        var content = new StringBuilder("BT /F1 11 Tf\n");
        float y = 700f;
        foreach (string[] row in rows)
        {
            for (int c = 0; c < row.Length; c++)
                content.AppendFormat(CultureInfo.InvariantCulture, "1 0 0 1 {0} {1} Tm ({2}) Tj\n", columnX[c], y, row[c]);
            y -= 30f;
        }
        return MinimalPdfBuilder.CreateMinimalPdf(contentStream: content.Append("ET\n").ToString());
    }

    private static string[] Shape(IReadOnlyList<PdfTable> tables)
        => tables.SelectMany(t => t.Rows.SelectMany(r => r.Cells.Select(c => $"{t.PageIndex}:{c.Column}:{c.Text}"))).ToArray();

    /// <summary>
    /// The layouts door answers what the stream door answers for the same document — through the extractor and
    /// through the facade alike — and the answer is the grid.
    /// </summary>
    [Fact]
    public void TheLayoutsDoor_AnswersTheStreamDoorsTables()
    {
        byte[] pdf = GridPdf();
        var fromStream = PdfTableExtractor.Extract(new MemoryStream(pdf));
        var layouts = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf));
        var graphics = PdfGraphicsExtractor.Extract(new MemoryStream(pdf));

        var fromLayouts = PdfTableExtractor.Extract(layouts, graphics);
        var fromFacade = PdfOperations.ExtractTables(layouts, graphics);

        Assert.Contains("0:1:12", Shape(fromStream));
        Assert.Equal(Shape(fromStream), Shape(fromLayouts));
        Assert.Equal(Shape(fromStream), Shape(fromFacade));
    }

    /// <summary>No pages is refused by name; more graphics than pages is refused as a mismatch, and one a page is accepted.</summary>
    [Fact]
    public void TheLayoutsDoor_RefusesNoPages_AndMoreGraphicsThanPages()
    {
        byte[] pdf = GridPdf();
        var layouts = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf));
        var graphics = PdfGraphicsExtractor.Extract(new MemoryStream(pdf));

        Assert.Equal("pages", Assert.Throws<ArgumentNullException>(() => PdfTableExtractor.Extract(null, graphics)).ParamName);

        var doubled = graphics.Concat(graphics).ToList();
        var mismatch = Assert.Throws<ArgumentException>(() => PdfTableExtractor.Extract(layouts, doubled));
        Assert.Equal("graphics", mismatch.ParamName);

        Assert.NotEmpty(PdfTableExtractor.Extract(layouts, graphics));
        Assert.NotEmpty(PdfTableExtractor.Extract(layouts, null));
    }

    /// <summary>
    /// A caller's malformed layout — a null page, a null line among real ones — is read as the walks read one:
    /// the nulls passed over, the real lines' table still found.
    /// </summary>
    [Fact]
    public void AMalformedLayout_WithNullPagesAndLines_IsPassedOver()
    {
        byte[] pdf = GridPdf();
        var real = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf))[0];
        var holed = new PdfPageTextLayout(0, real.Lines.Concat(new PdfTextLine[] { null }).ToList());

        var tables = PdfTableExtractor.Extract(new PdfPageTextLayout[] { holed, null }, null);

        Assert.Contains("0:1:12", Shape(tables));
    }
}
