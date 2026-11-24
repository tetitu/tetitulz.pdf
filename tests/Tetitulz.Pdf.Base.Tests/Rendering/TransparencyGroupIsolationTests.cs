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
/// A transparency group's <c>/I</c> (isolated) attribute, PDF §11.6.6 Table 147.
///
/// <para><b>Why it must be read.</b> Skia's <c>SaveLayer</c> always begins transparent, so unread,
/// every group that needed unit compositing would render <b>isolated</b> — and <c>/I</c> defaults
/// to <i>false</i>, so the default case would be the wrong one. The layer is seeded with the
/// backdrop when the group is non-isolated.</para>
///
/// <para><b>Why the fixture looks like this.</b> The difference is only observable when three
/// things line up: the group must actually need a layer (here <c>ca 0.99</c> at the <c>Do</c> —
/// with <c>ca 1</c>, Normal blending and no mask, the content draws straight to the page and
/// non-isolated behaviour already fell out for free), the group must use a <b>non-Normal blend
/// mode inside</b>, and the backdrop must be non-empty. Red multiplied by the green page is
/// black; red multiplied by nothing — an isolated group's transparent backdrop — is still red.
/// So this fixture reads out as black vs red, which is as far apart as the two behaviours get.</para>
/// </summary>
public class TransparencyGroupIsolationTests
{
    /// <summary>
    /// <c>/I false</c>: Multiply inside the group must see the green page beneath it.
    /// Red would be the isolated result.
    /// </summary>
    [Fact]
    public void NonIsolatedGroup_BlendsAgainstThePageBackdrop()
    {
        using var bmp = Render(BuildPdf(isolated: false));
        SKColor c = bmp.GetPixel(50, 50);

        // red × green = black.
        Assert.True(c.Red < 80 && c.Green < 80 && c.Blue < 80,
            $"a non-isolated group's Multiply should have blended against the green page " +
            $"(→ black), got {c}");
    }

    /// <summary>
    /// <c>/I true</c>: the group's backdrop is transparent, so Multiply has nothing to darken
    /// against and the red survives. This is the control — it pins that the renderer reads the flag
    /// rather than always seeding.
    /// </summary>
    [Fact]
    public void IsolatedGroup_DoesNotBlendAgainstThePageBackdrop()
    {
        using var bmp = Render(BuildPdf(isolated: true));
        SKColor c = bmp.GetPixel(50, 50);

        Assert.True(c.Red > 180 && c.Green < 90,
            $"an isolated group's Multiply has a transparent backdrop, so the fill should stay " +
            $"red, got {c}");
    }

    /// <summary>
    /// Outside the group's painted rectangle the page must be untouched — the seeded backdrop
    /// must not leak back as a visible change, which is the failure mode of seeding without a
    /// compositing rule that resolves to the backdrop where the group painted nothing.
    /// </summary>
    [Fact]
    public void NonIsolatedGroup_LeavesTheBackdropUnchangedWhereItPaintedNothing()
    {
        using var bmp = Render(BuildPdf(isolated: false));
        SKColor c = bmp.GetPixel(5, 5);   // outside the group's 20..80 rect

        Assert.True(c.Green > 180 && c.Red < 90,
            $"the page outside the group should still be green, got {c}");
    }

    // ----------------------------------------------------------------

    private static SKBitmap Render(byte[] pdf)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);  // 1 pt = 1 px
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    private static byte[] BuildPdf(bool isolated)
    {
        // Page: green fill, then the group at ca 0.99 (just enough to require a layer without
        // materially changing the colour that comes back).
        const string pageContent = "0 1 0 rg 0 0 100 100 re f q /GS0 gs /Fx Do Q";
        // Group: red fill in Multiply.
        const string formContent = "/GS1 gs 1 0 0 rg 20 20 60 60 re f";

        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");
        var off = new List<int>();
        void Obj(string body) { off.Add(pdf.Length); pdf.Append(body); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources " +
            "<< /XObject << /Fx 5 0 R >> /ExtGState << /GS0 6 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {pageContent.Length} >>\nstream\n{pageContent}\nendstream\nendobj\n");
        Obj($"5 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 100 100] " +
            $"/Group << /S /Transparency /I {(isolated ? "true" : "false")} >> " +
            $"/Resources << /ExtGState << /GS1 7 0 R >> >> /Length {formContent.Length} >>\n" +
            $"stream\n{formContent}\nendstream\nendobj\n");
        Obj("6 0 obj\n<< /Type /ExtGState /ca 0.99 >>\nendobj\n");
        Obj("7 0 obj\n<< /Type /ExtGState /BM /Multiply >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(off.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
