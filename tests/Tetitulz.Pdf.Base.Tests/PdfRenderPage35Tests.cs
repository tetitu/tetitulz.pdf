// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Core.Writing;
using Xunit;
using Xunit.Abstractions;

namespace Tetitulz.Pdf.Base.Tests;

public sealed class PdfRenderPage35Tests
{
    private readonly ITestOutputHelper _out;

    public PdfRenderPage35Tests(ITestOutputHelper output) => _out = output;

    [Theory]
    [InlineData(0, "p1")]
    [InlineData(1, "p2")]
    public void RenderAndInspect_DarkRectanglePage_HasDarkPixels(int pageIndex, string label)
    {
        byte[] pdf = BuildTwoPagePdfWithDarkRect();
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, pageIndex, dpi: 150);

        using var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        _out.WriteLine($"{label}: page {pageIndex + 1} → {bmp.Width}x{bmp.Height}");

        int darkCount = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var px = bmp.GetPixel(x, y);
                if (px.Red < 100 && px.Green < 100 && px.Blue < 100) darkCount++;
            }

        _out.WriteLine($"{label}: dark pixels = {darkCount}");
        Assert.True(darkCount > 100, $"Expected painted dark rectangle on page {pageIndex + 1}, found {darkCount} dark pixels");
    }

    private static byte[] BuildTwoPagePdfWithDarkRect()
    {
        var doc = new PdfDocumentBuilder();
        for (int i = 0; i < 2; i++)
        {
            var page = doc.AddPage(612, 792);
            page.SetFillColorRgb(0f, 0f, 0f);
            page.Rectangle(100, 600, 200, 100);
            page.Fill();
        }
        using var outMs = new MemoryStream();
        doc.Build(outMs);
        return outMs.ToArray();
    }
}
