// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Overprint simulation, PDF §11.7.4.2.
///
/// <para><b>The rule.</b> With <c>/op</c> (or <c>/OP</c>) true and <c>/OPM</c> 1, a DeviceCMYK
/// component whose value is <b>0</b> leaves that colorant of the backdrop alone instead of
/// zeroing it — inks add rather than replace. Magenta printed over cyan is blue, not magenta.</para>
///
/// <para><b>The fixture.</b> A cyan page (<c>1 0 0 0 k</c>) with a magenta rectangle
/// (<c>0 1 0 0 k</c>) painted over it under overprint. Simulated, the source's zero cyan preserves
/// the page's cyan and the result is CMYK (1,1,0,0) = blue. Unsimulated it is plain magenta. Blue
/// versus magenta is the whole behaviour in one pixel.</para>
///
/// <para>The colour components are what is asserted, not just "something changed" — an
/// implementation that merely darkened, or that multiplied, would not land on blue.</para>
/// </summary>
public class OverprintSimulationTests
{
    [Fact]
    public void Overprint_Simulated_PreservesTheBackdropColorantForAZeroComponent()
    {
        using var bmp = Render(BuildPdf(), simulate: true);
        SKColor c = bmp.GetPixel(50, 50);

        // CMYK (1,1,0,0) → blue.
        Assert.True(c.Blue > 200 && c.Red < 60 && c.Green < 60,
            $"magenta overprinting cyan should composite to blue, got {c}");
    }

    /// <summary>
    /// The control: with simulation off (the default) the same file must render exactly as before
    /// — plain magenta. This is what keeps the feature from silently changing existing output.
    /// </summary>
    [Fact]
    public void Overprint_NotSimulatedByDefault_PaintsNormally()
    {
        using var bmp = Render(BuildPdf(), simulate: false);
        SKColor c = bmp.GetPixel(50, 50);

        Assert.True(c.Red > 200 && c.Blue > 200 && c.Green < 60,
            $"with simulation off the fill should paint as plain magenta, got {c}");
    }

    /// <summary>Outside the overprinted rectangle the page must be untouched cyan.</summary>
    [Fact]
    public void Overprint_Simulated_LeavesTheRestOfThePageAlone()
    {
        using var bmp = Render(BuildPdf(), simulate: true);
        SKColor c = bmp.GetPixel(5, 5);

        Assert.True(c.Green > 200 && c.Blue > 200 && c.Red < 60,
            $"the page outside the overprinted rect should still be cyan, got {c}");
    }

    /// <summary>
    /// An RGB source colour — which §11.7.4.2 excludes from nonzero overprint mode — writes every
    /// process colorant, so simulated it covers the cyan: plain magenta, and nothing reported. With
    /// simulation off the overprint is still <i>reported</i>, so a caller can tell "no overprint in
    /// this file" from "not simulated here".
    /// </summary>
    [Fact]
    public void Overprint_WithNonCmykSource_IsSimulated_AndReportedWhenSimulationIsOff()
    {
        // Same file, but the overprinting fill is set with `rg` instead of `k`.
        byte[] pdf = BuildPdf(overprintFillOperator: "1 0 1 rg");

        var simulated = Reports(() => { using var bmp = Render(pdf, simulate: true); AssertMagenta(bmp.GetPixel(50, 50)); });
        Assert.Empty(simulated);
        var off = Reports(() => { using var bmp = Render(pdf, simulate: false); });
        Assert.Contains(off, d => d.Contains("SimulateOverprint is off"));
    }

    private static void AssertMagenta(SKColor c) =>
        Assert.True(c.Red > 250 && c.Green < 5 && c.Blue > 250, $"an RGB overprint writes every colorant: expected magenta, got {c}");

    /// <summary>The <c>OverprintNotSimulated</c> details <paramref name="act"/> raises on this thread.</summary>
    private static List<string> Reports(System.Action act)
    {
        var reported = new List<string>();
        int ownThread = System.Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, System.Exception ex)
        {
            if (kind == RenderDiagnosticKind.OverprintNotSimulated && System.Environment.CurrentManagedThreadId == ownThread)
                lock (reported) reported.Add(detail ?? string.Empty);
        }
        RenderDiagnostics.OnEvent += Sink;
        try { act(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        return reported;
    }

    // ----------------------------------------------------------------

    private static SKBitmap Render(byte[] pdf, bool simulate)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72,
            new PdfRenderOptions { SimulateOverprint = simulate });
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    private static byte[] BuildPdf(string overprintFillOperator = "0 1 0 0 k")
    {
        string content =
            "1 0 0 0 k 0 0 100 100 re f " +                       // cyan page
            $"q /GS0 gs {overprintFillOperator} 20 20 60 60 re f Q"; // overprinting rectangle

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(pdf.Length); pdf.Append(body); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " +
            "/Resources << /ExtGState << /GS0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /ExtGState /OP true /op true /OPM 1 >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(off.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
