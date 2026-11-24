// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Tests for <see cref="PdfColor"/>, the single source of truth for PDF colour-space
/// → sRGB conversion shared by the SkiaSharp interpreter and the SVG renderer — the precise
/// CalRGB transform included, not plain DeviceRGB.
/// </summary>
public sealed class PdfColorTests
{
    /// <summary>
    /// Out-of-range CMYK operands are clamped <b>before</b> the conversion, not only after it.
    /// </summary>
    /// <remarks>
    /// <para><b>The hazard is the one written at <c>CmykToRgb</c>, and nothing tested it.</b>
    /// Clamping the result alone is not enough: <c>c = k = 2</c> gives <c>(1−2)(1−2) = 1</c>, so an
    /// out-of-range pair paints <b>white</b> where it must paint black — a fully-inked area coming
    /// out blank. Every existing fixture passes operands already in 0..1, where the clamp cannot be
    /// observed at all.</para>
    /// <para><b>Found by a mutation that was itself wrong first.</b> Removing only the <c>k</c>
    /// clamp survived the whole suite — and would have anyway, because the surviving <c>c</c> clamp
    /// still forces the product to 0. That is CLAUDE.md's third explanation for a survivor,
    /// accidentally equivalent, and reading the callee is what separated it from the fourth.</para>
    /// <para>A content stream can carry these: <c>2 2 2 2 k</c> is syntactically valid and §8.6.4.4
    /// leaves an out-of-range component to the reader, so this is a real input rather than a
    /// theoretical one.</para>
    /// </remarks>
    [Theory]
    [InlineData(2f, 2f, 2f, 2f)]
    [InlineData(2f, 0f, 0f, 2f)]
    [InlineData(5f, 5f, 5f, 5f)]
    public void CmykToRgb_OperandsAboveOne_ClampBeforeConversion_AndStayBlack(
        float c, float m, float y, float k)
    {
        SKColor painted = PdfColor.CmykToRgb(c, m, y, k);

        Assert.Equal(0, painted.Red);
        Assert.Equal(0, painted.Green);
        Assert.Equal(0, painted.Blue);
    }

    /// <summary>
    /// Negative operands clamp the same way, and the in-range control shows the clamp is not simply
    /// painting everything black.
    /// </summary>
    [Fact]
    public void CmykToRgb_InRangeOperands_AreUnaffectedByTheClamp()
    {
        Assert.Equal(new SKColor(255, 255, 255), PdfColor.CmykToRgb(0f, 0f, 0f, 0f));
        Assert.Equal(new SKColor(0, 0, 0), PdfColor.CmykToRgb(0f, 0f, 0f, 1f));
        // Negative components are the other side of the same clamp: -1 must read as 0, i.e. no ink.
        Assert.Equal(new SKColor(255, 255, 255), PdfColor.CmykToRgb(-1f, -1f, -1f, -1f));
    }

    /// <summary>
    /// For a colour space this build does not recognise, the <b>component count is the only
    /// evidence left</b>, and three components means RGB.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is worth pinning.</b> An unresolved <c>/ICCBased</c> alternate, a
    /// <c>/DeviceN</c> whose tint transform could not be evaluated, or a name that is not a space at
    /// all all land on this arity fallback — and it is what most viewers do. A mutation sweep over
    /// CLAUDE.md's shared decision points widened the CMYK arm from four components to three, so
    /// every such three-operand colour painted as CMYK instead: pure red became cyan. <b>Nothing
    /// reddened</b> — not Base's suite, and not the SVG or raster plugin parity suites either.</para>
    /// <para>The three arms are asserted together because they are one ladder: a test for the RGB
    /// arm alone would pass on a fallback that had stopped distinguishing gray or CMYK.</para>
    /// </remarks>
    [Fact]
    public void ComponentsToColor_AnUnknownSpace_FallsBackOnItsComponentCount()
    {
        // Three components → RGB. Under a shifted ladder this paints cyan instead of red.
        Assert.Equal(new SKColor(255, 0, 0), PdfColor.ComponentsToColor(new[] { 1f, 0f, 0f }, "NotAColourSpace"));

        // One → gray, replicated across the channels.
        SKColor gray = PdfColor.ComponentsToColor(new[] { 0.5f }, "NotAColourSpace");
        Assert.Equal(gray.Red, gray.Green);
        Assert.Equal(gray.Green, gray.Blue);
        Assert.InRange(gray.Red, 126, 129);

        // Four → CMYK. Pure cyan ink leaves green and blue only.
        Assert.Equal(new SKColor(0, 255, 255), PdfColor.ComponentsToColor(new[] { 1f, 0f, 0f, 0f }, "NotAColourSpace"));
    }

    [Fact]
    public void ComponentsToColor_DeviceGray_ReplicatesToRgb()
    {
        var c = PdfColor.ComponentsToColor(new[] { 0.5f }, "DeviceGray");
        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Green, c.Blue);
        Assert.InRange(c.Red, 126, 128);
    }

    [Fact]
    public void ComponentsToColor_DeviceCMYK_PureK_IsBlack()
    {
        var c = PdfColor.ComponentsToColor(new[] { 0f, 0f, 0f, 1f }, "DeviceCMYK");
        Assert.Equal(new SKColor(0, 0, 0), c);
    }

    [Fact]
    public void ComponentsToColor_DeviceRGB_PassesThrough()
    {
        var c = PdfColor.ComponentsToColor(new[] { 1f, 0f, 0f }, "DeviceRGB");
        Assert.Equal(255, c.Red);
        Assert.Equal(0, c.Green);
        Assert.Equal(0, c.Blue);
    }

    [Fact]
    public void LabToSRgb_Endpoints_AreBlackAndWhite()
    {
        var black = PdfColor.LabToSRgb(0f, 0f, 0f);
        var white = PdfColor.LabToSRgb(100f, 0f, 0f);
        Assert.InRange(black.Red, 0, 4);
        Assert.InRange(white.Red, 250, 255);
        // Neutral Lab (a=b=0) stays neutral grey.
        Assert.Equal(white.Red, white.Green);
        Assert.Equal(white.Green, white.Blue);
    }

    [Fact]
    public void CalRgbToSRgb_AppliesGamma()
    {
        // Gamma 2.2 linearises 0.5 → 0.5^2.2 (≈0.22), which is darker than the
        // identity (gamma 1.0) transform — the difference an SVG renderer ignoring the
        // gamma would discard.
        var input = new[] { 0.5f, 0.5f, 0.5f };
        var identity = PdfColor.CalRgbToSRgb(input, null);
        var gamma22  = PdfColor.CalRgbToSRgb(input, MakeCalDict(2.2));
        Assert.True(gamma22.Red < identity.Red,
            $"gamma 2.2 ({gamma22.Red}) should be darker than identity ({identity.Red})");
    }

    [Fact]
    public void ToCssRgb_FormatsExpected()
    {
        Assert.Equal("rgb(10,20,30)", PdfColor.ToCssRgb(new SKColor(10, 20, 30)));
    }

    // ====================================================================
    //  Regression: SVG renderer now applies CalRGB via the shared helper.
    // ====================================================================

    [Fact]
    public void SvgRenderer_CalRgbFill_MatchesSharedHelper()
    {
        // [/CalRGB << /Gamma [2.2 2.2 2.2] /WhitePoint [...] >>] fill of mid-grey.
        byte[] pdf = BuildCalRgbPdf();
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        string expected = PdfColor.ToCssRgb(
            PdfColor.CalRgbToSRgb(new[] { 0.5f, 0.5f, 0.5f }, MakeCalDict(2.2)));

        Assert.Contains($"fill=\"{expected}\"", svg);
        // The old code treated CalRGB as plain DeviceRGB → rgb(127,127,127).
        Assert.DoesNotContain("rgb(127,127,127)", svg);
    }

    private static PdfDictionary MakeCalDict(double gamma)
    {
        var g = new PdfArray();
        g.Add(new PdfNumber(gamma)); g.Add(new PdfNumber(gamma)); g.Add(new PdfNumber(gamma));
        var wp = new PdfArray();
        wp.Add(new PdfNumber(0.9505)); wp.Add(new PdfNumber(1.0)); wp.Add(new PdfNumber(1.089));
        var d = new PdfDictionary();
        d[new PdfName("Gamma")] = g;
        d[new PdfName("WhitePoint")] = wp;
        return d;
    }

    private static byte[] BuildCalRgbPdf()
    {
        const string content =
            "/CS0 cs 0.5 0.5 0.5 sc 100 100 200 200 re f";
        const string csRes =
            "/ColorSpace << /CS0 [/CalRGB << /Gamma [2.2 2.2 2.2]" +
            " /WhitePoint [0.9505 1.0 1.089] >>] >>";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << " + csRes + " >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
