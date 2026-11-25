// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Plugins.Maintenance;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>Facade API tests.</summary>
public sealed class Wave2FacadeTests
{
    // -------- ExtractImage --------

    [Fact]
    public void ExtractImage_OnPageWithoutImages_ReturnsZeroCount()
    {
        byte[] pdf = BuildBlankPdf();
        using var ms = new MemoryStream(pdf);
        int count = PdfOperations.CountImagesOnPage(ms, 0);
        Assert.Equal(0, count);
    }

    [Fact]
    public void ExtractImage_InvalidIndex_Throws()
    {
        byte[] pdf = BuildBlankPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PdfOperations.ExtractImage(ms, 0, 0));
    }

    // -------- ComparePdfVisual --------

    [Fact]
    public void ComparePdfVisual_IdenticalDocs_AreEqualTrue()
    {
        byte[] pdf = BuildBlankPdf();
        using var l = new MemoryStream(pdf);
        using var r = new MemoryStream(pdf);
        var result = PdfOperations.ComparePdfVisual(l, r, dpi: 72, thresholdPct: 0.1f);
        Assert.True(result.AreEqual, $"Expected AreEqual=true; pages diff={string.Join(",", result.Pages)}");
        Assert.Single(result.Pages);
        Assert.Equal(0, result.Pages[0].DifferingPixels);
    }

    [Fact]
    public void ComparePdfVisual_DifferentDocs_AreEqualFalse()
    {
        byte[] a = BuildBlankPdf();
        byte[] b = BuildPdfWithBlackRect();
        using var l = new MemoryStream(a);
        using var r = new MemoryStream(b);
        var result = PdfOperations.ComparePdfVisual(l, r, dpi: 72, thresholdPct: 0.1f);
        Assert.False(result.AreEqual);
        Assert.True(result.Pages[0].DifferingPixels > 0);
    }

    // -------- Outline JSON --------

    [Fact]
    public void OutlineJson_RoundTrip_PreservesStructure()
    {
        byte[] pdf = BuildBlankPdf();

        var bookmarks = new List<PdfBookmark>
        {
            new PdfBookmark("Chapter 1", 0, 0, new List<PdfBookmark>
            {
                new PdfBookmark("Section 1.1", 0, 1)
            }),
            new PdfBookmark("Chapter 2", 0, 0)
        };

        using var withOutline = new MemoryStream();
        using (var src = new MemoryStream(pdf))
            PdfBookmarksManager.WriteBookmarks(src, withOutline, bookmarks);

        withOutline.Position = 0;
        string json = PdfOperations.ExportOutlineJson(withOutline);
        Assert.Contains("Chapter 1", json);
        Assert.Contains("Section 1.1", json);
        Assert.Contains("Chapter 2", json);

        // Round-trip: import the json into a fresh stream and re-export.
        withOutline.Position = 0;
        using var rewritten = new MemoryStream();
        PdfOperations.ImportOutlineJson(withOutline, rewritten, json);

        rewritten.Position = 0;
        string json2 = PdfOperations.ExportOutlineJson(rewritten);
        Assert.Contains("Chapter 1", json2);
        Assert.Contains("Section 1.1", json2);
        Assert.Contains("Chapter 2", json2);
    }

    // -------- OptimizeWithMetrics --------

    [Fact]
    public void OptimizeWithMetrics_ReturnsByteCounts()
    {
        byte[] pdf = BuildPdfWithBlackRect();
        using var src = new MemoryStream(pdf);
        using var dst = new MemoryStream();
        var result = PdfOptimizerWithMetrics.OptimizeWithMetrics(src, dst);

        Assert.True(result.BeforeBytes > 0);
        Assert.True(result.AfterBytes  > 0);
        Assert.Equal(pdf.Length, result.BeforeBytes);
        Assert.True(result.ReductionPct >= 0f); // may be 0 for already-compressed tiny files
    }

    // -------- helpers --------

    private static byte[] BuildBlankPdf()
    {
        var b = new PdfDocumentBuilder();
        b.AddPage(612, 792);
        using var ms = new MemoryStream();
        b.Build(ms);
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithBlackRect()
    {
        var b = new PdfDocumentBuilder();
        var page = b.AddPage(612, 792);
        page.SetFillColorRgb(0, 0, 0);
        page.Rectangle(100, 100, 400, 400);
        page.Fill();
        using var ms = new MemoryStream();
        b.Build(ms);
        return ms.ToArray();
    }
}
