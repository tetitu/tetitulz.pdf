// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// The Builder path draws a table's borders from its <c>w:tcBorders</c>, <c>w:tblBorders</c> and style, resolved
/// per cell as the legacy renderer resolves them. It read none of them: every table drew the full default grid,
/// whatever its borders said, and a mirrored table's sides were not swapped.
/// </summary>
public sealed class BuilderTableBordersTests
{
    private static string Docx(string tblPrExtra)
        => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\"><w:body>"
           + "<w:tbl><w:tblPr><w:tblW w:w=\"5000\" w:type=\"pct\"/>" + tblPrExtra + "</w:tblPr>"
           + "<w:tblGrid><w:gridCol w:w=\"4000\"/><w:gridCol w:w=\"4000\"/></w:tblGrid>"
           + "<w:tr><w:tc><w:p><w:r><w:t>a</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>b</w:t></w:r></w:p></w:tc></w:tr>"
           + "<w:tr><w:tc><w:p><w:r><w:t>c</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>d</w:t></w:r></w:p></w:tc></w:tr>"
           + "</w:tbl><w:p/></w:body></w:document>";

    /// <summary>Every stroked segment of page one: its width and its two ends.</summary>
    private static List<(double W, double X1, double Y1, double X2, double Y2)> Strokes(string tblPrExtra)
    {
        byte[] pdf = DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(Docx(tblPrExtra)),
            new DocxToPdfOptions { UseLegacyRenderer = false });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        string content = Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
        static double D(Group g) => double.Parse(g.Value, CultureInfo.InvariantCulture);
        return Regex.Matches(content, @"([\d.]+) w\n(-?[\d.]+) (-?[\d.]+) m\n(-?[\d.]+) (-?[\d.]+) l\nS")
            .Select(m => (D(m.Groups[1]), D(m.Groups[2]), D(m.Groups[3]), D(m.Groups[4]), D(m.Groups[5]))).ToList();
    }

    private const string OuterOnly = "<w:tblBorders><w:top w:val=\"single\" w:sz=\"4\"/><w:left w:val=\"single\" w:sz=\"4\"/>"
        + "<w:bottom w:val=\"single\" w:sz=\"4\"/><w:right w:val=\"single\" w:sz=\"4\"/>"
        + "<w:insideH w:val=\"none\"/><w:insideV w:val=\"none\"/></w:tblBorders>";

    /// <summary>
    /// Outer borders and no inside ones: the table's top and bottom are its only horizontal lines; the control, the
    /// same table with no borders named, draws its default grid, the line between its rows included.
    /// </summary>
    [Fact]
    public void ATableWithNoInsideBorders_DrawsNoLineBetweenItsRows()
    {
        static int HorizontalLevels(List<(double W, double X1, double Y1, double X2, double Y2)> strokes)
            => strokes.Where(s => s.Y1 == s.Y2).Select(s => Math.Round(s.Y1)).Distinct().Count();

        Assert.Equal(2, HorizontalLevels(Strokes(OuterOnly)));
        Assert.Equal(3, HorizontalLevels(Strokes("")));
    }

    /// <summary>
    /// A mirrored table's 3-pt <c>w:left</c> is drawn on its visual right, as the legacy renderer draws it; the
    /// control, unmirrored, on its left.
    /// </summary>
    [Fact]
    public void AMirroredTablesStartBorder_IsDrawnOnItsOtherSide()
    {
        const string borders = "<w:tblBorders><w:left w:val=\"single\" w:sz=\"24\"/><w:right w:val=\"single\" w:sz=\"4\"/></w:tblBorders>";

        static double ThickVerticalX(List<(double W, double X1, double Y1, double X2, double Y2)> strokes)
        {
            var thick = strokes.Where(s => s.X1 == s.X2 && s.W >= 2.9).Select(s => s.X1).Distinct().ToList();
            Assert.Single(thick);
            return thick[0];
        }

        var plain = Strokes(borders);
        var mirrored = Strokes("<w:bidiVisual/>" + borders);
        double left = plain.Where(s => s.X1 == s.X2).Min(s => s.X1), right = plain.Where(s => s.X1 == s.X2).Max(s => s.X1);

        Assert.Equal(left, ThickVerticalX(plain));
        Assert.Equal(right, ThickVerticalX(mirrored));
    }
}
