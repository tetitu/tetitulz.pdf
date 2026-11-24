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
/// A luminosity soft mask's <c>/BC</c> backdrop colour (§11.6.5.2), and the two renderers agreeing
/// about what its numbers mean.
///
/// <para><b>They did not, and on the commonest four-component value they inverted each other.</b>
/// The raster path read <c>/BC</c> as raw RGB components whatever its length, so a DeviceCMYK
/// backdrop lost <c>/K</c>: <c>/BC [0 0 0 0]</c> — CMYK <i>white</i>, what a producer writes to mean
/// "leave the uncovered area unmasked" — became RGB black, luma 0, and the mask hid everything.
/// <c>/BC [0 0 0 1]</c>, CMYK black, produced the same RGB black, so the two opposite backdrops
/// were indistinguishable. The SVG writer switched on the component count and got the CMYK case
/// right, while conceding in its own comment that the arity was a guess.</para>
///
/// <para>Both now go through <c>PdfColor.SoftMaskBackdrop</c>, which reads the space §11.6.5.2
/// names — the mask group's <c>/G /Group /CS</c> — and falls back to the arity only when the group
/// declares none.</para>
///
/// <para>The fixtures paint <b>nothing</b> inside the mask group, so its alpha is 0 everywhere and
/// the mask value comes from the backdrop alone. That is what makes the assertions statements about
/// <c>/BC</c> rather than about the group's content.</para>
/// </summary>
public sealed class SoftMaskBackdropTests
{
    // ====================================================================
    //  the inverted pair
    // ====================================================================

    /// <summary>
    /// CMYK white as a backdrop leaves the mask fully open, so the black rectangle underneath it
    /// paints, rather than leave the white page.
    /// </summary>
    [Fact]
    public void CmykWhiteBackdrop_LeavesTheMaskOpen()
    {
        using var bmp = PixelAssert.Render(BackdropPdf("/DeviceCMYK", "[0 0 0 0]"));
        Assert.InRange(PixelAssert.At(bmp, 45, 45).Red, 0, 8);
    }

    /// <summary>
    /// CMYK black is the opposite backdrop and must give the opposite result — the mask is shut and
    /// the rectangle does not paint. Stated as its own test because the pair is the whole point:
    /// a backdrop read wrongly would give <b>both</b> this outcome.
    /// </summary>
    [Fact]
    public void CmykBlackBackdrop_ShutsTheMask()
    {
        using var bmp = PixelAssert.Render(BackdropPdf("/DeviceCMYK", "[0 0 0 1]"));
        Assert.InRange(PixelAssert.At(bmp, 45, 45).Red, 247, 255);
    }

    /// <summary>
    /// The two together, as one assertion, because "they differ" is the claim the defect
    /// contradicted and neither test above states on its own.
    /// </summary>
    [Fact]
    public void CmykWhiteAndBlackBackdrops_AreNotTheSameMask()
    {
        using var white = PixelAssert.Render(BackdropPdf("/DeviceCMYK", "[0 0 0 0]"));
        using var black = PixelAssert.Render(BackdropPdf("/DeviceCMYK", "[0 0 0 1]"));

        int w = PixelAssert.At(white, 45, 45).Red;
        int b = PixelAssert.At(black, 45, 45).Red;
        Assert.True(b - w > 200,
            $"CMYK white and black backdrops must give opposite masks; got {w} and {b}");
    }

    // ====================================================================
    //  the space comes from /G /Group /CS, not from the component count
    // ====================================================================

    /// <summary>
    /// The same three numbers under two declared spaces. <c>/BC [1 0 0]</c> is RGB red — luma
    /// ≈ 0.299, a mostly-shut mask — and Lab <c>L*=1</c>, which is very nearly black. Nothing about
    /// the array distinguishes them; only <c>/Group /CS</c> does, which is why an arity-based
    /// reader cannot get both right.
    /// </summary>
    [Fact]
    public void ThreeComponentBackdrop_IsReadInTheGroupsDeclaredSpace()
    {
        using var rgb = PixelAssert.Render(BackdropPdf("/DeviceRGB", "[1 0 0]"));
        using var lab = PixelAssert.Render(
            BackdropPdf("[/Lab << /WhitePoint [0.9505 1 1.089] /Range [-100 100 -100 100] >>]",
                        "[1 0 0]"));

        int rgbInk = 255 - PixelAssert.At(rgb, 45, 45).Red;
        int labInk = 255 - PixelAssert.At(lab, 45, 45).Red;

        // BT.601 luma of pure red is 0.299 → roughly a third of the black shows through…
        Assert.InRange(rgbInk, 60, 95);
        // …where L* = 1 is near-black, so almost none does.
        Assert.InRange(labInk, 0, 20);
    }

    /// <summary>
    /// A group that declares no <c>/CS</c> at all — §11.6.6 permits it — is where the component
    /// count is the only evidence, and four components still have to mean CMYK.
    /// </summary>
    [Fact]
    public void BackdropWithNoGroupColorSpace_FallsBackToTheComponentCount()
    {
        using var bmp = PixelAssert.Render(BackdropPdf(groupCs: null, bc: "[0 0 0 0]"));
        Assert.InRange(PixelAssert.At(bmp, 45, 45).Red, 0, 8);
    }

    // ====================================================================
    //  raster / SVG parity
    // ====================================================================

    /// <summary>
    /// The SVG export must reach the same backdrop colour. It emits it as the mask element's
    /// background rectangle fill, so the assertion reads the CSS colour out of the parsed document
    /// rather than a substring of the text.
    /// </summary>
    [Theory]
    [InlineData("/DeviceCMYK", "[0 0 0 0]", "rgb(255,255,255)")]
    [InlineData("/DeviceCMYK", "[0 0 0 1]", "rgb(0,0,0)")]
    [InlineData("/DeviceRGB",  "[1 0 0]",   "rgb(255,0,0)")]
    public void SvgExport_UsesTheSameBackdropColour(string groupCs, string bc, string expected)
    {
        string svg = RenderSvg(BackdropPdf(groupCs, bc));

        var mask = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "mask");
        var fills = mask.Descendants()
            .Select(e => e.Attribute("fill")?.Value)
            .Where(v => v != null)
            .ToList();

        Assert.Contains(expected, fills);
    }

    // ====================================================================
    //  fixture
    // ====================================================================

    /// <summary>
    /// A black rectangle over a 90×90 page, modulated by a luminosity soft mask whose group paints
    /// nothing — so every mask pixel is the backdrop, weighted by (1 − 0).
    /// </summary>
    private static byte[] BackdropPdf(string groupCs, string bc)
    {
        string group = groupCs == null
            ? "/Group << /Type /Group /S /Transparency >>"
            : $"/Group << /Type /Group /S /Transparency /CS {groupCs} >>";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GM 6 0 R >> >> >>",
            Stream("", "0 0 0 rg /GM gs 0 0 90 90 re f"),
            Stream($"/Type /XObject /Subtype /Form /BBox [0 0 90 90] {group} /Resources << >>", ""),
            $"<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 5 0 R /BC {bc} >> >>");
    }

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
