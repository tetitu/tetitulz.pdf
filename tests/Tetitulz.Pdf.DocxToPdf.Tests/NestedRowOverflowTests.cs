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
/// A nested table's row taller than a page was one block, drawn whole, and what fell past the bottom margin was
/// lost — silently at first, then reported (Wave 22, DocxToPdf-4:7). It is cut into page-sized slices now, as a
/// top-level row is, so every line lands on a page; only a single block taller than any page is still reported.
/// </summary>
public sealed class NestedRowOverflowTests
{
    private static (byte[] Pdf, List<string> Reports) Convert(string nestedCellContent)
    {
        byte[] docx = HostileDocxInputTests.RawDocx(
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{HostileDocxInputTests.WordNamespace}\"><w:body>"
            + "<w:tbl><w:tr><w:tc><w:tbl><w:tr><w:tc>" + nestedCellContent + "</w:tc></w:tr></w:tbl><w:p/></w:tc></w:tr></w:tbl>"
            + "<w:p/></w:body></w:document>");

        var reports = new List<string>();
        int thread = Environment.CurrentManagedThreadId;
        void Collect(string m) { if (Environment.CurrentManagedThreadId == thread) reports.Add(m); }
        DocxToPdfConverter.DiagnosticReported += Collect;
        byte[] pdf;
        try { pdf = DocxToPdfConverter.ConvertToBytes(docx, new DocxToPdfOptions { UseLegacyRenderer = true, CompressStreams = false }); }
        finally { DocxToPdfConverter.DiagnosticReported -= Collect; }
        return (pdf, reports);
    }

    private static string Lines(int count)
        => string.Concat(Enumerable.Range(0, count).Select(i => $"<w:p><w:r><w:t>line {i}</w:t></w:r></w:p>"));

    /// <summary>Each "line N" drawn, with its page and baseline, read from the pages' content.</summary>
    private static List<(int Line, int Page, double Y)> DrawnLines(byte[] pdf)
    {
        var drawn = new List<(int, int, double)>();
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        for (int p = 0; p < doc.Pages.Count; p++)
        {
            string content = Encoding.Latin1.GetString(doc.Pages[p].GetContentStreamData() ?? Array.Empty<byte>());
            foreach (Match m in Regex.Matches(content, @"(-?[\d.]+) (-?[\d.]+) Td\n(?:[^\n]*\n){0,6}?\(line (\d+)\) Tj"))
                drawn.Add((int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), p,
                           double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
        }
        return drawn;
    }

    /// <summary>
    /// A hundred lines in one nested row — some two pages of them — are each drawn once, on a page above its
    /// bottom margin, across more than one page; and nothing is reported. Before the slices, some 55 of them fell
    /// below the page.
    /// </summary>
    [Fact]
    public void ANestedRowTallerThanAPage_IsSplitAcrossPages_EveryLineOnAPage()
    {
        var (pdf, reports) = Convert(Lines(100));
        var drawn = DrawnLines(pdf);

        Assert.Equal(Enumerable.Range(0, 100), drawn.Select(d => d.Line).OrderBy(n => n));
        Assert.True(drawn.Select(d => d.Page).Distinct().Count() >= 2, "the row was not split across pages");
        double lowest = drawn.Min(d => d.Y);
        Assert.True(lowest >= 36, $"a line was drawn at y = {lowest}, past the bottom margin");
        Assert.DoesNotContain(reports, r => r.Contains("NestedRowTallerThanPage", StringComparison.Ordinal));
    }

    /// <summary>
    /// The split row keeps one top edge and one bottom edge, as a top-level row split across pages does: a slice
    /// draws the cell's sides, and only the first its top and only the last its bottom. The nested cell's edges are
    /// the horizontal strokes that start furthest right (the outer cell's start at its own, smaller x).
    /// </summary>
    [Fact]
    public void ASplitNestedRow_DrawsItsTopAndBottomOnce()
    {
        var (pdf, _) = Convert(Lines(100));
        var horizontals = new List<double>();
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        for (int p = 0; p < doc.Pages.Count; p++)
        {
            string content = Encoding.Latin1.GetString(doc.Pages[p].GetContentStreamData() ?? Array.Empty<byte>());
            foreach (Match m in Regex.Matches(content, @"(-?[\d.]+) (-?[\d.]+) m\n(-?[\d.]+) (-?[\d.]+) l\nS"))
                if (m.Groups[2].Value == m.Groups[4].Value)
                    horizontals.Add(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
        }

        double nestedLeft = horizontals.Max();
        Assert.True(nestedLeft > horizontals.Min(), "the fixture's nested cell is not inset from the outer one");
        Assert.Equal(2, horizontals.Count(x => x == nestedLeft));
    }

    /// <summary>One line taller than any page — 819 pt type — cannot be split, and is still reported.</summary>
    [Fact]
    public void ABlockTallerThanAnyPage_IsReported()
    {
        var (_, reports) = Convert("<w:p><w:r><w:rPr><w:sz w:val=\"1638\"/></w:rPr><w:t>huge</w:t></w:r></w:p>");
        Assert.Contains(reports, r => r.Contains("NestedRowTallerThanPage", StringComparison.Ordinal));
    }

    /// <summary>The control: a nested row that fits a page is drawn on one page and says nothing.</summary>
    [Fact]
    public void ANestedRowThatFits_IsDrawnWhole_AndNotReported()
    {
        var (pdf, reports) = Convert(Lines(3));
        var drawn = DrawnLines(pdf);

        Assert.Equal(new[] { 0, 1, 2 }, drawn.Select(d => d.Line).OrderBy(n => n));
        Assert.Single(drawn.Select(d => d.Page).Distinct());
        Assert.DoesNotContain(reports, r => r.Contains("NestedRowTallerThanPage", StringComparison.Ordinal));
    }
}
