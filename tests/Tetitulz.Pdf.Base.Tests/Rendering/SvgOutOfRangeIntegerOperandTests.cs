// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The SVG writer reads an integer operand of <c>J</c>, <c>j</c> and <c>Tr</c> saturated, as the
/// raster's <c>ContentOperand.Int</c> does: the low 32 bits of 4294967296 are 0 and of 4294967298
/// are 2, a mode and a cap the file never asked for, under which hidden text showed and a butt cap
/// exported square.
/// </summary>
public class SvgOutOfRangeIntegerOperandTests
{
    [Fact]
    public void AnOutOfRangeTextRenderMode_IsReadSaturated_NotByItsLow32Bits()
    {
        string huge = Svg(TextPdf("4294967296"));
        string saturated = Svg(TextPdf("2147483647"));
        string visible = Svg(TextPdf("0"));

        Assert.Equal(saturated, huge);
        Assert.NotEqual(visible, huge);
    }

    [Fact]
    public void AnOutOfRangeLineCap_IsReadSaturated_NotByItsLow32Bits()
    {
        string huge = Svg(StrokePdf("4294967298 J"));
        string square = Svg(StrokePdf("2 J"));

        Assert.Contains("stroke-linecap=\"square\"", square, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke-linecap=\"square\"", huge, StringComparison.Ordinal);
        Assert.Contains("<path", huge, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOutOfRangeLineJoin_IsReadSaturated_NotByItsLow32Bits()
    {
        string huge = Svg(StrokePdf("4294967297 j"));
        string round = Svg(StrokePdf("1 j"));

        Assert.Contains("stroke-linejoin=\"round\"", round, StringComparison.Ordinal);
        Assert.DoesNotContain("stroke-linejoin=\"round\"", huge, StringComparison.Ordinal);
        Assert.Contains("<path", huge, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    private static string Svg(byte[] pdf) => PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);

    private static byte[] TextPdf(string mode) =>
        Pdf($"BT /F1 24 Tf {mode} Tr 10 50 Td (Secret) Tj ET", "/Resources << /Font << /F1 5 0 R >> >>");

    private static byte[] StrokePdf(string style) =>
        Pdf($"0 0 1 RG 20 w {style} 30 50 m 70 50 l 70 90 l S", "/Resources << >>");

    private static byte[] Pdf(string content, string resources)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R " + resources + " >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
