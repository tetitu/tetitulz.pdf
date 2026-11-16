// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A stream whose decode failed is not decoded again by the same render. Both renderers memoised only
/// successful decodes, so an 88-byte JBIG2 image whose decode fails, drawn 845 times, was decoded 845
/// times at up to 50 ms each — 11 s for an 11 KB file (the release campaign's slow unit, 2026-10-24) —
/// and a tiling cell whose stream fails was decoded at every fill. The first attempt's report stands
/// and no later attempt is made; the evidence is the report count, one where it was one a draw.
/// </summary>
public sealed class FailedDecodeMemoTests
{
    private const string FailingJbig2 =
        " /Type /XObject /Subtype /Image /Width 32 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 1 /Filter /JBIG2Decode";

    [Fact]
    public void AdversarialFailingImageDrawnFiftyTimes_IsDecodedOnce_InBothRenderers()
    {
        byte[] pdf = ImageDrawn(50, FailingJbig2, "not a JBIG2 stream at all, in any segment");

        int raster = Count(RenderDiagnosticKind.ImageDecodeFailed, () =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            PdfPageRenderer.RenderPageToPixels(ms, 0, 36);
        });
        Assert.True(raster == 1, $"raster reported {raster} times: {LastDetails}");
        int svg = Count(RenderDiagnosticKind.ImageDecodeFailed, () =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        });
        Assert.True(svg == 1, $"svg reported {svg} times: {LastDetails}");
    }

    /// <summary>The control: an image that decodes is drawn every time and nothing is reported.</summary>
    [Fact]
    public void ADecodableImageDrawnFiftyTimes_ReportsNothing_AndIsDrawn()
    {
        byte[] pdf = ImageDrawn(50,
            " /Type /XObject /Subtype /Image /Width 32 /Height 16 /ColorSpace /DeviceGray /BitsPerComponent 1",
            new string('\0', 32 / 8 * 16));   // every bit 0: black

        RenderedPage page = null;
        Assert.Equal(0, Count(RenderDiagnosticKind.ImageDecodeFailed, () =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            page = PdfPageRenderer.RenderPageToPixels(ms, 0, 36);
        }));
        Assert.True(HasADarkPixel(page), "the black image was drawn");
    }

    [Fact]
    public void AdversarialTilingPatternWhoseCellFailsToDecode_IsDecodedOnceOverTwentyFills_InBothRenderers()
    {
        byte[] pdf = PatternFilled(20);

        Assert.Equal(1, Count(RenderDiagnosticKind.TilingPatternDecodeFailed, () =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            PdfPageRenderer.RenderPageToPixels(ms, 0, 36);
        }));
        Assert.Equal(1, Count(RenderDiagnosticKind.TilingPatternDecodeFailed, () =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        }));
    }

    /// <summary>
    /// The memo is one for the whole render: a tiling cell is rendered in an offscreen context of its
    /// own at every fill, and the failing image it draws was decoded once per fill until the offscreen
    /// context shared the page's memo (the SVG writer's child writers share its state already).
    /// </summary>
    [Fact]
    public void AdversarialFailingImageInsideATilingCell_IsDecodedOnceOverTwentyFills_InBothRenderers()
    {
        byte[] pdf = PatternCellDrawingImage(20);

        int raster = Count(RenderDiagnosticKind.ImageDecodeFailed, () =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            PdfPageRenderer.RenderPageToPixels(ms, 0, 36);
        });
        Assert.True(raster == 1, $"raster reported {raster} times: {LastDetails}");
        int svg = Count(RenderDiagnosticKind.ImageDecodeFailed, () =>
        {
            using var ms = new MemoryStream(pdf, writable: false);
            PdfSvgRenderer.RenderPageToSvg(ms, 0);
        });
        Assert.True(svg == 1, $"svg reported {svg} times: {LastDetails}");
    }

    /// <summary>How many <paramref name="kind"/> reports <paramref name="render"/> makes on this thread.</summary>
    private static int Count(RenderDiagnosticKind kind, Action render)
    {
        int thread = Thread.CurrentThread.ManagedThreadId, count = 0;
        var details = new HashSet<string>(StringComparer.Ordinal);
        void Collect(RenderDiagnosticKind k, string detail, Exception ex)
        {
            if (k != kind || Thread.CurrentThread.ManagedThreadId != thread) return;
            Interlocked.Increment(ref count);
            lock (details) details.Add((detail ?? "") + (ex == null ? "" : " [" + ex.GetType().Name + "]"));
        }
        RenderDiagnostics.OnEvent += Collect;
        try { render(); }
        finally { RenderDiagnostics.OnEvent -= Collect; }
        LastDetails = string.Join(" | ", details);
        return count;
    }

    /// <summary>The distinct reports the last <see cref="Count"/> saw, for a failure's message.</summary>
    private static string LastDetails = "";

    private static bool HasADarkPixel(RenderedPage page)
    {
        if (page == null) return false;
        byte[] px = page.Pixels;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
            {
                int i = y * page.Stride + x * 4;
                if (px[i] < 64 && px[i + 1] < 64 && px[i + 2] < 64) return true;
            }
        return false;
    }

    /// <summary>One page drawing object 5, an image with <paramref name="imageKeys"/> and <paramref name="imageData"/>, <paramref name="draws"/> times.</summary>
    private static byte[] ImageDrawn(int draws, string imageKeys, string imageData)
    {
        var content = new StringBuilder();
        for (int i = 0; i < draws; i++)
            content.Append("q 20 0 0 10 ").Append(10 + (i % 10) * 25).Append(' ').Append(10 + (i / 10) * 15).Append(" cm /Im Do Q\n");
        var bodies = new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Contents 4 0 R /Resources << /XObject << /Im 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", content.ToString()),
            RenderFormXObjectBudgetTests.StreamObj(5, imageKeys, imageData),
        };
        return RenderFormXObjectBudgetTests.Assemble(bodies);
    }

    /// <summary>One page filling <paramref name="fills"/> squares with a tiling pattern whose cell draws the failing JBIG2 image (object 6).</summary>
    private static byte[] PatternCellDrawingImage(int fills)
    {
        var content = new StringBuilder("/Pattern cs /P1 scn\n");
        for (int i = 0; i < fills; i++)
            content.Append(10 + (i % 10) * 25).Append(' ').Append(10 + (i / 10) * 30).Append(" 20 20 re f\n");
        var bodies = new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Contents 4 0 R /Resources << /Pattern << /P1 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", content.ToString()),
            RenderFormXObjectBudgetTests.StreamObj(5,
                " /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /XObject << /Im 6 0 R >> >>",
                "q 10 0 0 10 0 0 cm /Im Do Q\n"),
            RenderFormXObjectBudgetTests.StreamObj(6, FailingJbig2, "not a JBIG2 stream at all, in any segment"),
        };
        return RenderFormXObjectBudgetTests.Assemble(bodies);
    }

    /// <summary>One page filling <paramref name="fills"/> squares with a tiling pattern whose cell stream is Flate garbage.</summary>
    private static byte[] PatternFilled(int fills)
    {
        var content = new StringBuilder("/Pattern cs /P1 scn\n");
        for (int i = 0; i < fills; i++)
            content.Append(10 + (i % 10) * 25).Append(' ').Append(10 + (i / 10) * 30).Append(" 20 20 re f\n");
        var bodies = new List<string>
        {
            RenderFormXObjectBudgetTests.Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            RenderFormXObjectBudgetTests.Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            RenderFormXObjectBudgetTests.Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 120] /Contents 4 0 R /Resources << /Pattern << /P1 5 0 R >> >> >>"),
            RenderFormXObjectBudgetTests.StreamObj(4, "", content.ToString()),
            RenderFormXObjectBudgetTests.StreamObj(5,
                " /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Filter /FlateDecode",
                "this is not a zlib stream, so the cell cannot be decoded"),
        };
        return RenderFormXObjectBudgetTests.Assemble(bodies);
    }
}
