// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>P2b — geometric / label-to-value queries over an extracted page layout.</summary>
public class PdfSpatialQueryTests
{
    private const int Alef = 0x05D0, Bet = 0x05D1;

    private static PdfPageTextLayout Layout()
    {
        // y=700: LTR pair "ID" then "99".   y=650: RTL Hebrew label (right) with value "55" to its left.
        byte[] pdf = new InvoicePdfBuilder()
            .AsciiRun("ID", x: 50, y: 700, step: 4)
            .AsciiRun("99", x: 90, y: 700, step: 4)
            .HebRunRtl(InvoicePdfBuilder.Hb(Alef, Bet), xRight: 250, y: 650)
            .AsciiRun("55", x: 200, y: 650)
            .Build();
        using var ms = new MemoryStream(pdf);
        return PdfStructuredTextExtractor.Extract(ms, new WordAssemblyOptions { ApplyBidi = true })[0];
    }

    private static PdfTextWord Word(PdfPageTextLayout page, string text)
        => page.Lines.SelectMany(l => l.Words).First(w => w.Text == text);

    [Fact]
    public void WordToTheRightOf_LtrLabel_FindsValue()
    {
        var page = Layout();
        var value = PdfSpatialQuery.WordToTheRightOf(page, Word(page, "ID"));
        Assert.NotNull(value);
        Assert.Equal("99", value.Text);
    }

    [Fact]
    public void ValueWordFor_RtlLabel_FindsValueToTheLeft()
    {
        var page = Layout();
        var label = page.Lines.SelectMany(l => l.Words).First(w => w.IsRtl);
        var value = PdfSpatialQuery.ValueWordFor(page, label);
        Assert.NotNull(value);
        Assert.Equal("55", value.Text);
    }

    [Fact]
    public void WordsInRect_ReturnsOnlyWordsInRegion()
    {
        var page = Layout();
        var words = PdfSpatialQuery.WordsInRect(page, 40f, 695f, 120f, 20f);
        var texts = words.Select(w => w.Text).ToList();
        Assert.Contains("ID", texts);
        Assert.Contains("99", texts);
        Assert.DoesNotContain("55", texts);
    }

    [Fact]
    public void NearestWord_ReturnsClosest()
    {
        var page = Layout();
        var w = PdfSpatialQuery.NearestWord(page, 95f, 703f);
        Assert.NotNull(w);
        Assert.Equal("99", w.Text);
    }

    [Fact]
    public void WordToTheRightOf_RightmostWord_ReturnsNull_AndSetsLastError()
    {
        var page = Layout();
        var result = PdfSpatialQuery.WordToTheRightOf(page, Word(page, "99"));
        Assert.Null(result);
        Assert.NotNull(PdfSpatialQuery.LastError);
    }
}
