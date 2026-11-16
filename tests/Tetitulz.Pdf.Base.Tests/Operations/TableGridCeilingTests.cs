// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A ruled grid of more than <see cref="PdfTableExtractor.MaxGridCells"/> cells is not built as a
/// table, and <see cref="PdfTableExtractor.LastError"/> says so. A few hundred kilobytes of strokes
/// made millions of cells, each of whose four borders was a scan of every ruling on the page.
/// </summary>
public sealed class TableGridCeilingTests
{
    [Fact]
    public void AdversarialGridPastTheCeiling_IsNotATable_AndSaysSo()
    {
        const int lines = 520;   // 519 × 519 cells
        using var input = new MemoryStream(Grid(lines));

        var tables = PdfTableExtractor.Extract(input, new TableExtractionOptions { Strategy = TableStrategy.Lines });

        Assert.Empty(tables);
        Assert.Contains("ceiling", PdfTableExtractor.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains("519 × 519", PdfTableExtractor.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>The control: a grid under the ceiling is read, every cell's four borders found, and nothing said.</summary>
    [Fact]
    public void AGridUnderTheCeiling_IsReadAsATable()
    {
        const int lines = 40;
        using var input = new MemoryStream(Grid(lines));

        var table = Assert.Single(PdfTableExtractor.Extract(input, new TableExtractionOptions { Strategy = TableStrategy.Lines }));

        Assert.Equal(lines - 1, table.Rows.Count);
        Assert.Equal(lines - 1, table.ColumnCount);
        Assert.Equal(1f, table.Confidence);
        Assert.Null(PdfTableExtractor.LastError);
    }

    // `lines` horizontal and `lines` vertical strokes 5 pt apart, a full grid.
    private static byte[] Grid(int lines)
    {
        float size = 10 + 5 * lines;
        var content = new StringBuilder("0.1 w\n");
        string F(float v) => v.ToString(CultureInfo.InvariantCulture);
        for (int i = 0; i < lines; i++)
        {
            float at = 10 + 5 * i, end = 10 + 5 * (lines - 1);
            content.Append("10 ").Append(F(at)).Append(" m ").Append(F(end)).Append(' ').Append(F(at)).Append(" l S\n");
            content.Append(F(at)).Append(" 10 m ").Append(F(at)).Append(' ').Append(F(end)).Append(" l S\n");
        }

        string body = content.ToString();
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {F(size)} {F(size)}] /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {body.Length} >>\nstream\n{body}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
