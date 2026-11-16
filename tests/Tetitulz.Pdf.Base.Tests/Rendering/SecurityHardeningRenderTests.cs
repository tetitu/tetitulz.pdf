// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Penetration-test regression suite for the rendering pipeline (PT review, 2026-06-08,
/// round 36). Adversarial inputs that, pre-fix, caused a StackOverflow or a multi-GB
/// allocation when rendering an untrusted PDF.
/// </summary>
public class SecurityHardeningRenderTests
{
    private static byte[] BuildPdf(string content, string pageExtra, params string[] extraObjs)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var off = new List<int>();
        void Obj(string b) { off.Add(sb.Length); sb.Append(b); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R {pageExtra} >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        foreach (var o in extraObjs) Obj(o);
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(off.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void SelfReferentialTilingPattern_DoesNotStackOverflow()
    {
        // A tiling pattern whose cell content re-paints itself via /P0. Pre-fix, the inner
        // RenderToBitmap interpreter reset RenderDepth to 0, so the MaxRenderDepth guard never
        // fired and the pattern recursed unbounded → StackOverflow.
        string cell = "/Pattern cs /P0 scn 0 0 10 10 re f";
        string pattern = "5 0 obj\n<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 " +
            $"/BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << /Pattern << /P0 5 0 R >> >> /Length {cell.Length} >>\n" +
            $"stream\n{cell}\nendstream\nendobj\n";
        byte[] pdf = BuildPdf("/Pattern cs /P0 scn 0 0 100 100 re f",
            "/Resources << /Pattern << /P0 5 0 R >> >>", pattern);

        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        Assert.NotNull(png);
        Assert.True(png.Length > 0);
    }

    [Fact]
    public void OversizedImage_SkippedNotAllocated_Svg()
    {
        // An image XObject declaring 60000×60000 (w·h·comps overflows int; raw allocation
        // would be multi-GB). The SVG renderer must skip it and still produce valid SVG.
        string img = "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 60000 /Height 60000 " +
                     "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\nabc\nendstream\nendobj\n";
        byte[] pdf = BuildPdf("q 100 0 0 100 0 0 cm /Img Do Q",
            "/Resources << /XObject << /Img 5 0 R >> >>", img);

        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);
        Assert.NotNull(svg);                          // completed without OOM
        Assert.DoesNotContain("data:image/png", svg); // the oversized image was skipped
    }

    [Fact]
    public void VisibilityExpression_DeeplyNested_TerminatesWithoutStackOverflow()
    {
        // [/Not [/Not [/Not … ]]] 5000 deep. Pre-fix, EvaluateVisibilityExpression ↔
        // EvaluateVeOperand recursed once per level → StackOverflow. The depth cap stops it.
        PdfArray cur = new PdfArray();
        cur.Add(new PdfName("Or")); // empty Or → leaf
        for (int i = 0; i < 5000; i++)
        {
            var outer = new PdfArray();
            outer.Add(new PdfName("Not"));
            outer.Add(cur);
            cur = outer;
        }
        // Must return (fail-open) rather than throw/StackOverflow.
        bool visible = ContentStreamInterpreter.EvaluateVisibilityExpression(cur, new HashSet<int>(), null);
        Assert.True(visible);
    }
}
