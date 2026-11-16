// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A vertically merged cell is drawn without a line across it: the continuation's top edge was left off, and the
/// cell above still drew its bottom edge there (Wave 22, DocxToPdf-4:2).
/// </summary>
public sealed class VerticalMergeBorderTests
{
    private const string Box =
        "<w:tcBorders><w:top w:val=\"single\" w:sz=\"8\"/><w:left w:val=\"single\" w:sz=\"8\"/>"
        + "<w:bottom w:val=\"single\" w:sz=\"8\"/><w:right w:val=\"single\" w:sz=\"8\"/></w:tcBorders>";

    private const string RestartMerge = "<w:vMerge w:val=\"restart\"/>";
    private const string ContinueMerge = "<w:vMerge/>";

    private static string Cell(string merge, string text)
        => $"<w:tc><w:tcPr>{merge}{Box}</w:tcPr><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:tc>";

    private static string TwoRows(string first, string second) => "<w:tr>" + first + "</w:tr><w:tr>" + second + "</w:tr>";

    /// <summary>The heights of every horizontal stroke on the page, rounded to a point.</summary>
    private static int[] HorizontalStrokes(string rows)
    {
        byte[] pdf = DocxToPdfConverter.ConvertToBytes(HostileDocxInputTests.RawDocx(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\"><w:body>"
            + "<w:tbl>" + rows + "</w:tbl><w:p/></w:body></w:document>"),
            new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = true });
        return Regex.Matches(Encoding.Latin1.GetString(pdf), @"(-?[\d.]+) (-?[\d.]+) m\n(-?[\d.]+) (-?[\d.]+) l\nS")
            .Select(m => (y1: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                          y2: double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)))
            .Where(s => Math.Abs(s.y1 - s.y2) < 0.01)
            .Select(s => (int)Math.Round(s.y1))
            .Distinct().OrderBy(y => y).ToArray();
    }

    /// <summary>
    /// The merged cell's top and bottom are drawn and nothing between them: the strokes are the
    /// unmerged control's outer two, its middle one gone.
    /// </summary>
    [Fact]
    public void AVerticallyMergedCell_HasNoLineAcrossIt()
    {
        int[] merged = HorizontalStrokes(TwoRows(Cell(RestartMerge, "top"), Cell(ContinueMerge, "")));
        int[] separate = HorizontalStrokes(TwoRows(Cell("", "top"), Cell("", "")));

        Assert.Equal(new[] { separate[0], separate[^1] }, merged);
    }

    /// <summary>The control: the same two rows unmerged draw the line between them, strictly inside.</summary>
    [Fact]
    public void TwoUnmergedRows_HaveTheLineBetweenThem()
    {
        int[] separate = HorizontalStrokes(TwoRows(Cell("", "top"), Cell("", "under")));

        Assert.Equal(3, separate.Length);
        Assert.InRange(separate[1], separate[0] + 1, separate[2] - 1);
    }
}
