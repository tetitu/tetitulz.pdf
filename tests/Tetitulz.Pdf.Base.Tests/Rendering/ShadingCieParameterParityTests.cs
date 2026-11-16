// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Xml.Linq;
using System.Linq;
using System.Globalization;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A shading over a CIE colour space honours that space's parameter dictionary — in the raster
/// renderer as well as the SVG exporter.
/// </summary>
/// <remarks>
/// <para><b>The same file rendered two different greys.</b> <c>SvgShadingSpace</c> answered the
/// <c>CalGray</c>, <c>CalRGB</c> and <c>Lab</c> cases itself (it delegates too now — see the last test), honouring <c>/Gamma</c>,
/// <c>/Matrix</c>, <c>/WhitePoint</c> and <c>/Range</c>. The raster <c>ShadingSpace</c> handed its
/// components to <c>IccColorTransform.ResolveAlternateColor</c>, whose fallback is
/// <c>PdfColor.ComponentsToColor</c> — where <b>CalGray is aliased onto DeviceGray</b> and CalRGB
/// onto DeviceRGB. An axial shading over <c>[/CalGray &lt;&lt; /Gamma 2.2 &gt;&gt;]</c> sampled at
/// 0.5 therefore gave <c>rgb(128,128,128)</c> from the renderer and <c>rgb(55,55,55)</c> from the
/// export.</para>
///
/// <para><b>It was the third occurrence of one fix.</b> The <c>sc</c>/<c>scn</c> ladder and the
/// image path had both already grown these branches — the former's comment records the identical
/// symptom, "<c>/Gamma</c> was silently dropped for every fill and stroke while the image path
/// applied it" — and the shading path was missed twice. The branches now live in
/// <c>ResolveAlternateColor</c>, the entry point whose own remarks say that "routing them one at a
/// time is how the pair drifts", so every nested position gets them at once.</para>
/// </remarks>
public sealed class ShadingCieParameterParityTests
{
    /// <summary>
    /// <b>Gamma 2.2 at a mid-grey is the separating input.</b> 0.5<sup>2.2</sup> ≈ 0.218 is the
    /// luminance (§8.6.5.2), sRGB ≈128, where a renderer dropping the parameter dictionary — gamma 1,
    /// luminance 0.5 — paints ≈188: far apart enough that no rounding rule confuses them.
    /// </summary>
    [Fact]
    public void TheRasterRendererHonoursACalGrayShadingsGamma()
    {
        using SKBitmap bitmap = Render(CalGrayShadingPdf());
        SKColor mid = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);

        Assert.True(mid.Red > 116 && mid.Red < 142,
            $"a CalGray shading with /Gamma 2.2 paints ≈128 at the midpoint; got {mid} " +
            "(≈188 means /Gamma was dropped)");
        Assert.Equal(mid.Red, mid.Green);
        Assert.Equal(mid.Red, mid.Blue);
    }

    /// <summary>
    /// <b>The parity assertion.</b> Export and render must agree — the divergence was invisible
    /// from inside either one, because each was self-consistent across the whole gradient.
    /// </summary>
    [Fact]
    public void TheSvgExportAgreesWithTheRasterRendererOnACalGrayShading()
    {
        byte[] pdf = CalGrayShadingPdf();

        using SKBitmap raster = Render(pdf);
        SKColor rasterMid = raster.GetPixel(raster.Width / 2, raster.Height / 2);

        string svg;
        using (var input = new MemoryStream(pdf))
            svg = PdfSvgRenderer.RenderPageToSvg(input, 0);

        // The exporter writes gradient stops as rgb(...) triples; the midpoint of a two-stop
        // linear ramp is the average of its ends, which is what the raster midpoint samples too.
        (int r, int g, int b) svgMid = MidpointOfGradient(svg);

        Assert.InRange(svgMid.r, rasterMid.Red - 6, rasterMid.Red + 6);
        Assert.InRange(svgMid.g, rasterMid.Green - 6, rasterMid.Green + 6);
        Assert.InRange(svgMid.b, rasterMid.Blue - 6, rasterMid.Blue + 6);
    }

    /// <summary>
    /// The control. With <c>/Gamma 1</c> the midpoint's luminance is 0.5 itself — sRGB ≈188, lighter
    /// than DeviceGray's 128 (the reference renderer: 187/188) — which is what makes the case above a statement
    /// about <c>/Gamma</c> rather than about shadings in general. It used to be named
    /// <c>AGammaOfOneStillPaintsThePlainMidGrey</c> and assert 128, on the mistaken ground that the
    /// CIE space and the device space "agree by definition"; §8.6.5.2 defines luminance.
    /// </summary>
    [Fact]
    public void AGammaOfOnePaintsTheLinearLuminance()
    {
        using SKBitmap bitmap = Render(CalGrayShadingPdf(gamma: "1"));
        SKColor mid = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);

        Assert.InRange(mid.Red, 178, 198);
    }

    // ------------------------------------------------------------------

    private static SKBitmap Render(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToImage(input, 0, PdfImageFormat.Png, 72);
        Assert.NotNull(png);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>
    /// <summary>
    /// The exporter samples the shading function into 32 stops, so the colour at the middle of
    /// the ramp is the stop nearest offset 0.5 — not the average of the ends. Averaging the ends
    /// measures SVG's sRGB interpolation between them, which is a different question and was
    /// this test's first wrong answer.
    /// </summary>
    private static (int r, int g, int b) MidpointOfGradient(string svg)
    {
        var stops = System.Text.RegularExpressions.Regex.Matches(
            svg, @"offset=""([0-9.]+)""[^>]*stop-color=""rgb\((\d+),\s*(\d+),\s*(\d+)\)""");
        Assert.True(stops.Count >= 3,
            $"the export must sample the shading into stops; svg had {stops.Count}");

        System.Text.RegularExpressions.Match best = null;
        double bestDistance = double.MaxValue;
        foreach (System.Text.RegularExpressions.Match stop in stops)
        {
            double offset = double.Parse(stop.Groups[1].Value,
                System.Globalization.CultureInfo.InvariantCulture);
            double distance = Math.Abs(offset - 0.5);
            if (distance < bestDistance) { bestDistance = distance; best = stop; }
        }

        Assert.True(bestDistance < 0.05,
            $"no stop near the midpoint of the ramp (nearest was {bestDistance:F3} away)");
        return (int.Parse(best.Groups[2].Value), int.Parse(best.Groups[3].Value),
                int.Parse(best.Groups[4].Value));
    }

    /// <summary>
    /// A page filled by an axial shading over <c>[/CalGray &lt;&lt; /Gamma g &gt;&gt;]</c>, ramping
    /// a single component from 0 to 1 across the page.
    /// </summary>
    private static byte[] CalGrayShadingPdf(string gamma = "2.2")
    {
        const string content = "q /Sh0 sh Q\n";

        var sb = new StringBuilder();
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R "
          + "/Resources << /Shading << /Sh0 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj("5 0 obj\n<< /ShadingType 2 /ColorSpace [/CalGray << /WhitePoint [0.9505 1 1.089] "
          + $"/Gamma {gamma} >>] /Coords [0 0 100 0] /Function 6 0 R /Extend [true true] >>\nendobj\n");
        Obj("6 0 obj\n<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// <b>And the SVG side resolved nothing.</b> Once the raster path delegated, the SVG writer kept its
    /// own CalGray/CalRGB/Lab answers, reached through overloads whose document argument it never
    /// passed — so an indirect <c>/Gamma 7 0 R</c> read as absent there and was honoured here. It goes
    /// through <c>ResolveAlternateColor</c> too now. Gamma 4 separates the readings: about 71 against
    /// gamma 1's 188.
    /// </summary>
    [Fact]
    public void AnIndirectCalGrayGammaIsHonouredInBothRenderers()
    {
        byte[] indirect = IndirectGammaPdf(gammaIndirect: true);
        byte[] direct = IndirectGammaPdf(gammaIndirect: false);

        SKColor svgIndirect = FirstSvgStop(indirect), svgDirect = FirstSvgStop(direct);
        Assert.Equal(svgDirect, svgIndirect);

        using var bmp = PixelAssert.Render(indirect);
        SKColor raster = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(raster.Red - svgIndirect.Red, -3, 3);

        Assert.True(svgIndirect.Red < 100, $"gamma 4 at 0.5 is dark, got {svgIndirect}");
    }

    private static SKColor FirstSvgStop(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);
        string col = XDocument.Parse(svg).Descendants()
            .First(e => e.Name.LocalName == "stop").Attribute("stop-color")!.Value;
        int[] v = col.Substring(col.IndexOf('(') + 1).TrimEnd(')').Split(',')
            .Select(t => int.Parse(t.Trim(), CultureInfo.InvariantCulture)).ToArray();
        return new SKColor((byte)v[0], (byte)v[1], (byte)v[2]);
    }

    private static byte[] IndirectGammaPdf(bool gammaIndirect)
    {
        const string content = "/Sh0 sh";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /Shading << /Sh0 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /ShadingType 2 /ColorSpace [/CalGray 6 0 R] /Coords [0 0 100 0] /Extend [true true]"
                + " /Function << /FunctionType 2 /Domain [0 1] /C0 [0.5] /C1 [0.5] /N 1 >> >>",
            "<< /WhitePoint [0.9505 1 1.089] /Gamma " + (gammaIndirect ? "7 0 R" : "4") + " >>",
            "4",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
