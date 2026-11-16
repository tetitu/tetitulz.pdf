// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.4.3.6's dash pattern, and the two renderers agreeing about how to read the <c>d</c> operator.
///
/// <para><b>The arithmetic was duplicated and the parsing was not.</b> <c>EffectiveDashPeriod</c>
/// and <c>NormaliseDashPhase</c> existed byte-identically in both writers, kept in step by a comment
/// reading "Same function as <c>PdfRenderContext.NormaliseDashPhase</c>". The operator handlers,
/// which nobody had thought to compare, disagreed: the raster one required both operands and ignored
/// the operator otherwise, the SVG one accepted the array alone and supplied a phase of 0. So
/// <c>d [6 6]</c> with the phase missing left the previous dash in force in one renderer and
/// installed a new one in the other.</para>
///
/// <para>Assertions read the emitted dash geometry — the attribute values in the export, the
/// painted pixels in the raster — because a wrong dash is still a stroke of the right colour along
/// the right path.</para>
/// </summary>
public sealed class DashPatternParityTests
{
    // ====================================================================
    //  the operand-count divergence
    // ====================================================================

    /// <summary>
    /// <c>d [6 6]</c> with no phase operand is not a <c>d</c>: §8.4.3.6 gives the operator two
    /// operands. Both renderers must leave the dash state alone, so the solid line set before it
    /// stays solid.
    /// </summary>
    [Fact]
    public void DashOperatorMissingItsPhase_ChangesNothingInEitherRenderer()
    {
        // A solid stroke, then a malformed `d`, then the stroke that would show a dash.
        const string content = "0 0 0 RG 4 w [] 0 d [6 6] d 10 45 m 80 45 l S";

        string svg = RenderSvg(Pdf(content));
        var stroked = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "path" && e.Attribute("stroke")?.Value == "rgb(0,0,0)");
        Assert.Null(stroked.Attribute("stroke-dasharray"));

        using var bmp = PixelAssert.Render(Pdf(content));
        Assert.True(InkAlongTheLine(bmp) > 60,
            "a solid 70-unit line should ink most of its length; a dash would ink about half");
    }

    /// <summary>The control: with the phase present, both renderers do install the dash.</summary>
    [Fact]
    public void WellFormedDashOperator_IsHonouredInBothRenderers()
    {
        const string content = "0 0 0 RG 4 w [6 6] 0 d 10 45 m 80 45 l S";

        string svg = RenderSvg(Pdf(content));
        var stroked = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "path" && e.Attribute("stroke")?.Value == "rgb(0,0,0)");
        Assert.Equal("6,6", stroked.Attribute("stroke-dasharray")?.Value);

        using var bmp = PixelAssert.Render(Pdf(content));
        int ink = InkAlongTheLine(bmp);
        Assert.InRange(ink, 25, 50);   // roughly half of 70
    }

    /// <summary>
    /// An empty array turns dashing off (§8.4.3.6). The phase operand is then meaningless, and
    /// keeping it — which the raster path did — leaves a stale offset for whatever <c>d</c> comes
    /// next.
    /// </summary>
    [Fact]
    public void EmptyDashArray_ClearsBothThePatternAndThePhase()
    {
        // [] 12 d clears the dash and must not remember 12; the following [6 6] 0 d then starts
        // at phase 0. With the phase remembered, the second `d` would also start at 0 — the two
        // are told apart by the *next* operator that sets only an array, which is malformed, so
        // the observable claim is simply that the line is solid and then dashed from its start.
        const string content = "0 0 0 RG 4 w [] 12 d [6 6] 0 d 10 45 m 80 45 l S";

        string svg = RenderSvg(Pdf(content));
        var stroked = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "path" && e.Attribute("stroke")?.Value == "rgb(0,0,0)");

        Assert.Equal("6,6", stroked.Attribute("stroke-dasharray")?.Value);
        Assert.Null(stroked.Attribute("stroke-dashoffset"));   // phase 0 is SVG's default
    }

    // ====================================================================
    //  the shared arithmetic
    // ====================================================================

    /// <summary>
    /// A negative phase is legal (§8.4.3.6) and must be brought into <c>[0, period)</c> — SVG
    /// rejects a negative <c>stroke-dashoffset</c>, so emitting it verbatim dropped the offset
    /// entirely.
    /// </summary>
    [Fact]
    public void NegativePhase_IsNormalisedIntoTheFirstPeriod()
    {
        // Even-length array: period = 12. −4 normalises to 8.
        string svg = RenderSvg(Pdf("0 0 0 RG 4 w [6 6] -4 d 10 45 m 80 45 l S"));
        var stroked = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "path" && e.Attribute("stroke")?.Value == "rgb(0,0,0)");

        Assert.Equal("8", stroked.Attribute("stroke-dashoffset")?.Value);
    }

    /// <summary>
    /// An <b>odd-length</b> array's period is twice its sum, because the on/off roles swap on each
    /// repeat. Reducing modulo the plain sum lands the phase half a period off and inverts the
    /// dashes — gaps where the ink belongs.
    /// </summary>
    [Fact]
    public void OddLengthArray_NormalisesAgainstTwiceItsSum()
    {
        // [5] sums to 5, so the effective period is 10. −4 must become 6, not 1.
        string svg = RenderSvg(Pdf("0 0 0 RG 4 w [5] -4 d 10 45 m 80 45 l S"));
        var stroked = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "path" && e.Attribute("stroke")?.Value == "rgb(0,0,0)");

        Assert.Equal("6", stroked.Attribute("stroke-dashoffset")?.Value);
    }

    // ====================================================================
    //  helpers
    // ====================================================================

    /// <summary>Dark pixels along the row the stroke runs down — its inked length.</summary>
    private static int InkAlongTheLine(SKBitmap bmp)
    {
        int y = PixelAssert.Y(bmp, 45);
        int n = 0;
        for (int x = PixelAssert.X(10); x < PixelAssert.X(80); x++)
            if (bmp.GetPixel(x, y).Red < 128) n++;
        return n;
    }

    private static byte[] Pdf(string content) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            " /Resources << >> >>",
            Stream("", content));

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
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
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
