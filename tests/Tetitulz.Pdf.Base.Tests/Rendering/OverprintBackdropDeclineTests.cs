// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The three cases the per-paint overprint compositor used to decline — a non-rectangular clip, a
/// transparency group's layer, a soft mask — are simulated: the raster renderer composites a page
/// that overprints on its separation plates and recombines them (<c>OverprintPreview</c>).
/// </summary>
/// <remarks>
/// <para><b>What this file used to test.</b> The compositor read back the page's sRGB pixels, and
/// its <c>BackdropDecline</c> refused when it could not — reporting the reason and painting
/// normally. Since 2026-10-04 the compositor is gone, and each case is a magenta fill overprinting
/// cyan that must come out blue, with nothing reported. The names say "simulated" now, because a
/// test whose name and body disagree is the failure no guard can catch.</para>
///
/// <para>The acceptance control is the same fill with none of the three conditions: also blue, and
/// also silent — so each case's blue is the overprint, not a fixture that happens to paint blue.</para>
/// </remarks>
public class OverprintBackdropDeclineTests
{
    /// <summary>A cyan page, painted first, under every case.</summary>
    private const string CyanBackdrop = "1 0 0 0 k 0 0 100 100 re f ";

    /// <summary>A triangle, so the clip is not a rectangle.</summary>
    private const string TriangleClip = "10 10 m 90 10 l 50 90 l h W n ";

    [Fact]
    public void APathOverprintUnderANonRectangularClip_IsSimulated()
        => AssertBlueAndSilent(CyanBackdrop + "q /GS1 gs 0 1 0 0 k " + TriangleClip + "20 20 60 60 re f Q");

    /// <summary>The control: the same fill, no triangle, no mask, no group — blue and silent too.</summary>
    [Fact]
    public void TheSamePathFillWithNoClipNoMaskAndNoGroup_IsSimulated()
        => AssertBlueAndSilent(CyanBackdrop + "q /GS1 gs 0 1 0 0 k 20 20 60 60 re f Q");

    /// <summary>Inside a knockout transparency group, which always composites through a layer.</summary>
    [Fact]
    public void APathOverprintInsideATransparencyGroup_IsSimulated()
        => AssertBlueAndSilent(CyanBackdrop + "q /GS1 gs /Fx1 Do Q");

    /// <summary>
    /// Under a luminosity soft mask whose group is white, so the mask passes the fill through: a
    /// black group would hide the fill whatever overprint did, and could not show it happened.
    /// </summary>
    [Fact]
    public void APathOverprintUnderASoftMask_IsSimulated()
        => AssertBlueAndSilent(CyanBackdrop + "q /GS2 gs 0 1 0 0 k 20 20 60 60 re f Q");

    // ================================================================
    //  Fixture
    // ================================================================

    /// <summary>
    /// Renders <paramref name="content"/> with overprint simulation on: the fill's centre (inside
    /// the triangle too) is cyan plus magenta — blue — and nothing is reported on this thread.
    /// </summary>
    private static void AssertBlueAndSilent(string content)
    {
        var reasons = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;

        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.OverprintNotSimulated)
                lock (reasons) reasons.Add(detail ?? string.Empty);
        }

        RenderedPage page;
        RenderDiagnostics.OnEvent += Sink;
        try
        {
            page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(BuildPdf(content)), 0, 72,
                new PdfRenderOptions { SimulateOverprint = true });
        }
        finally { RenderDiagnostics.OnEvent -= Sink; }

        Assert.Empty(reasons);
        int i = 60 * page.Stride + 50 * 4;   // user space (50, 40)
        var at = (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
        Assert.True(at.Item1 <= 1 && at.Item2 <= 1 && at.Item3 >= 254, $"cyan overprinted by magenta is {at}, expected blue");
    }

    /// <summary>
    /// A 100x100 page carrying <c>/GS1</c> (<c>/op true /OP true /OPM 1</c>), <c>/GS2</c> — the same
    /// three entries plus a luminosity <c>/SMask</c> — and a knockout transparency-group form doing
    /// the overprinting fill.
    /// </summary>
    private static byte[] BuildPdf(string content)
    {
        const string formContent = "/GS1 gs 0 1 0 0 k 20 20 60 60 re f";
        const string maskContent = "1 g 0 0 100 100 re f";

        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << " +
            "/ExtGState << /GS1 5 0 R /GS2 7 0 R >> /XObject << /Fx1 6 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /ExtGState /op true /OP true /OPM 1 >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100]" +
            " /Group << /S /Transparency /K true >>" +
            " /Resources << /ExtGState << /GS1 5 0 R >> >>" +
            $" /Length {formContent.Length} >>\nstream\n{formContent}\nendstream",
            "<< /Type /ExtGState /op true /OP true /OPM 1" +
            " /SMask << /S /Luminosity /G 8 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 100 100]" +
            " /Group << /S /Transparency /CS /DeviceGray >>" +
            $" /Length {maskContent.Length} >>\nstream\n{maskContent}\nendstream",
        };

        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
