// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A page's Type 4 functions spend one budget (<see cref="PdfLimits.MaxFunctionWorkPerPage"/>): a tint
/// transform runs once a pixel, and one evaluation's own ceiling (two million operations, its loops
/// included) multiplied by the pixels was the only bound. Here an 8 × 8 five-ink DeviceN image's tint
/// transform loops two hundred times a pixel: under a page budget of 5 000 the image is refused by the
/// page's budget and reported, in both renderers; under the default it is drawn blue.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class FunctionWorkBudgetTests
{
    [Fact]
    public void ALoopingTintTransform_SpendsThePagesBudget_InBothRenderers()
    {
        byte[] pdf = DeviceNImagePage();
        try
        {
            PdfLimits.MaxFunctionWorkPerPage = 5000;
            var raster = Events(() => PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72));
            Assert.Contains(raster, e => IsPageRefusal(e.Exception));
            using (SKBitmap refused = SKBitmap.Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72)))
                Assert.NotEqual(SKColors.Blue, refused.GetPixel(4, 4));

            var svg = Events(() => PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
            Assert.Contains(svg, e => IsPageRefusal(e.Exception));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }

        var drawn = Events(() => PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72));
        Assert.DoesNotContain(drawn, e => e.Exception is WorkBudgetExceededException);
        using SKBitmap bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72));
        Assert.Equal(SKColors.Blue, bmp.GetPixel(4, 4));
    }

    private static bool IsPageRefusal(Exception ex)
        => ex is WorkBudgetExceededException w && w.Message.Contains("MaxFunctionWorkPerPage", StringComparison.Ordinal);

    private static List<(RenderDiagnosticKind Kind, string Detail, Exception Exception)> Events(Action action)
    {
        var events = new List<(RenderDiagnosticKind, string, Exception)>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId == ownThread) lock (events) events.Add((kind, detail, ex));
        }
        RenderDiagnostics.OnEvent += Sink;
        try { action(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        lock (events) return events.ToList();
    }

    /// <summary>An 8 × 8 pt page filled by an 8 × 8 DeviceN image of five inks, every sample 0xFF.</summary>
    private static byte[] DeviceNImagePage()
    {
        const string program = "{ 0 1 199 { pop } for pop pop pop pop pop 0 0 1 }";
        string samples = string.Concat(Enumerable.Repeat("FF", 8 * 8 * 5)) + ">";
        const string content = "q 8 0 0 8 0 0 cm /Im Do Q";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 8 8] /Contents 4 0 R /Resources << /XObject << /Im 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 8 " +
                "/ColorSpace [/DeviceN [/A /B /C /D /E] /DeviceRGB 6 0 R] /Filter /ASCIIHexDecode " +
                $"/Length {samples.Length} >>\nstream\n{samples}\nendstream",
            "<< /FunctionType 4 /Domain [0 1 0 1 0 1 0 1 0 1] /Range [0 1 0 1 0 1] " +
                $"/Length {program.Length} >>\nstream\n{program}\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
