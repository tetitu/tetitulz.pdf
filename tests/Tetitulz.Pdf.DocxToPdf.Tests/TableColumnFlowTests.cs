// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A table that flows into the next column of a multi-column section draws those rows in that column. The table
/// was laid out once, against the column it starts in, and the rows past the column's foot were drawn at the
/// first column's x, over its content, leaving the second column empty (Wave 22, DocxToPdf-4:3).
/// </summary>
public sealed class TableColumnFlowTests
{
    private static string TwoColumnTable(int rows)
    {
        var tbl = new StringBuilder("<w:tbl><w:tblPr><w:tblW w:w=\"2000\" w:type=\"dxa\"/></w:tblPr><w:tblGrid><w:gridCol w:w=\"2000\"/></w:tblGrid>");
        for (int i = 1; i <= rows; i++) tbl.Append($"<w:tr><w:tc><w:p><w:r><w:t>row{i}</w:t></w:r></w:p></w:tc></w:tr>");
        tbl.Append("</w:tbl>");
        return Encoding.Latin1.GetString(DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\"><w:body>"
            + tbl + "<w:p/><w:sectPr><w:cols w:num=\"2\" w:space=\"720\"/></w:sectPr></w:body></w:document>"),
            new DocxToPdfOptions { UseLegacyRenderer = true, CompressStreams = false }));
    }

    /// <summary>
    /// Where each row's text is drawn on the first page: the <c>Td</c> before it, plus the translation the
    /// <c>q … cm … Q</c> around it applies.
    /// </summary>
    private static Dictionary<string, (double X, double Y)> RowPositions(string pdf)
    {
        int end = pdf.IndexOf("endstream", System.StringComparison.Ordinal);
        string page = pdf.Substring(0, end);
        var positions = new Dictionary<string, (double, double)>();
        var shifts = new Stack<double>();
        double shift = 0, x = 0, y = 0;
        foreach (string line in page.Split('\n'))
        {
            if (line == "q") shifts.Push(shift);
            else if (line == "Q") shift = shifts.Count > 0 ? shifts.Pop() : 0;
            else if (Regex.Match(line, @"^1 0 0 1 (-?[\d.]+) 0 cm$") is { Success: true } cm)
                shift += double.Parse(cm.Groups[1].Value, CultureInfo.InvariantCulture);
            else if (Regex.Match(line, @"^(-?[\d.]+) (-?[\d.]+) Td$") is { Success: true } td)
            {
                x = double.Parse(td.Groups[1].Value, CultureInfo.InvariantCulture);
                y = double.Parse(td.Groups[2].Value, CultureInfo.InvariantCulture);
            }
            else if (Regex.Match(line, @"^\((row\d+)\) Tj$") is { Success: true } tj)
                positions[tj.Groups[1].Value] = (x + shift, y);
        }
        return positions;
    }

    [Fact]
    public void RowsThatFlowIntoTheSecondColumn_AreDrawnThere()
    {
        var rows = RowPositions(TwoColumnTable(60));

        Assert.True(rows["row1"].X < 150, $"row1 at {rows["row1"].X}");
        Assert.True(rows.Values.Max(p => p.X) > 300, "no row was drawn in the second column");
        // No two rows of the page are drawn in one place.
        Assert.Equal(rows.Count, rows.Values.Select(p => (System.Math.Round(p.X), System.Math.Round(p.Y))).Distinct().Count());
    }
}
