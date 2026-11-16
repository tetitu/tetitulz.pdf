// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

public sealed class Wave3Pdf20Tests
{
    [Fact]
    public void ReadDocumentParts_DocumentWithoutDPart_ReturnsNull()
    {
        byte[] pdf = BuildBlankPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Null(PdfOperations.ReadDocumentParts(ms));
    }

    [Fact]
    public void InspectUnencryptedWrapper_PlainPdf_NotAWrapper()
    {
        byte[] pdf = BuildBlankPdf();
        using var ms = new MemoryStream(pdf);
        var info = PdfOperations.InspectUnencryptedWrapper(ms);
        Assert.False(info.IsWrapper);
    }

    [Fact]
    public void ReadGeoViewports_PlainPdf_EmptyList()
    {
        byte[] pdf = BuildBlankPdf();
        using var ms = new MemoryStream(pdf);
        var vps = PdfOperations.ReadGeoViewports(ms);
        Assert.Empty(vps);
    }

    [Fact]
    public void ReadPageThumbnail_NoThumbOnPage_ReturnsNull()
    {
        byte[] pdf = BuildBlankPdf();
        using var ms = new MemoryStream(pdf);
        Assert.Null(PdfOperations.ReadPageThumbnail(ms, 0));
    }

    [Fact]
    public void GeneratePageThumbnails_OnePage_AttachesThumb()
    {
        byte[] pdf = BuildBlankPdf();
        using var src = new MemoryStream(pdf);
        using var dst = new MemoryStream();
        int count = PdfOperations.GeneratePageThumbnails(src, dst, dpi: 18);
        Assert.Equal(1, count);

        dst.Position = 0;
        // After the incremental update we should be able to read the /Thumb back.
        byte[] withThumbs = dst.ToArray();
        using var reread = new MemoryStream(withThumbs);
        var info = PdfOperations.ReadPageThumbnail(reread, 0);
        Assert.NotNull(info);
        Assert.True(info.Width > 0);
        Assert.True(info.Height > 0);
    }

    private static byte[] BuildBlankPdf()
    {
        var b = new PdfDocumentBuilder();
        var page = b.AddPage(612, 792);
        page.SetFillColorRgb(0.8f, 0.8f, 0.8f);
        page.Rectangle(100, 100, 200, 200);
        page.Fill();
        using var ms = new MemoryStream();
        b.Build(ms);
        return ms.ToArray();
    }
}
