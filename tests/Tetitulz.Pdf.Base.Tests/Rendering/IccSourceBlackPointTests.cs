// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Black-point compensation on the source side of the ordinary render: an <c>/ICCBased</c> colour
/// into sRGB with its profile's black point landing on sRGB black (<see cref="IccBlackPoint.Source"/>,
/// <c>IccProfile.TryEvaluate</c>). Against the reference renderer.3 (the reference colour engine) rendering the same page to PNG
/// at its default — compensation on — where the host has RSWOP.icm; synthetic profiles everywhere.
/// </summary>
public class IccSourceBlackPointTests
{
    public static IEnumerable<object[]> ReferenceSwatches()
    {
        // ICCBased RSWOP CMYK at relative colorimetric unless noted; the reference renderer's PNG (sRGB).
        yield return new object[] { "1 1 1 1",         "", 0, 0, 0 };
        yield return new object[] { "0 0 0 1",         "", 2, 0, 0 };
        yield return new object[] { "0.6 0.5 0.4 0.3", "", 93, 87, 89 };
        yield return new object[] { "0.2 0.1 0 0",     "", 209, 212, 233 };
        yield return new object[] { "0.1 0.1 0.1 0.8", "", 70, 69, 67 };
        yield return new object[] { "1 0 0 0",         "", 0, 157, 214 };
        yield return new object[] { "0 0 0 0.5",       "", 152, 153, 153 };
        yield return new object[] { "1 1 1 1",         "/Perceptual ri", 3, 0, 0 };
    }

    /// <summary>
    /// Every swatch within one level of the reference renderer's compensated render. Uncompensated, 100 % CMYK
    /// is sRGB 24/23/23 and K alone 34/33/33 (the reference renderer with <c>-dBlackPtComp=0</c> agrees with
    /// that too); compensated, both are black. Perceptual is compensated as well, but RSWOP's
    /// perceptual black is already as dark as sRGB's.
    /// </summary>
    [RealIccProfileTheory("RSWOP.icm")]
    [MemberData(nameof(ReferenceSwatches))]
    public void IccBasedRswopColour_IsCompensatedAsTheReferenceHasIt(string cmyk, string ri, int r, int g, int b)
    {
        byte[] profile = File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm"));
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(Pdf(profile, $"{ri} /CS0 cs {cmyk} sc 0 0 100 100 re f")), 0, 72);
        int i = 50 * page.Stride + 50 * 4;
        var ours = (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
        Assert.True(Math.Abs(ours.Item1 - r) <= 1 && Math.Abs(ours.Item2 - g) <= 1 && Math.Abs(ours.Item3 - b) <= 1,
            $"{ri} {cmyk}: {ours}, reference ({r}, {g}, {b})");
    }

    /// <summary>RSWOP's relative source black point is its perceptual black measured colorimetrically (B2A0 out, A2B1 back), which is not L* 0.</summary>
    [RealIccProfileFact("RSWOP.icm")]
    public void RswopsRelativeSourceBlackPoint_IsItsPerceptualBlackAsItPrints()
    {
        byte[] p = File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm"));
        IccProfile parsed = IccProfile.Parse(p);
        double relative = IccBlackPoint.Source(p, "prtr", "CMYK", parsed.LutFor, IccRenderingIntent.MediaRelativeColorimetric);
        double absolute = IccBlackPoint.Source(p, "prtr", "CMYK", parsed.LutFor, IccRenderingIntent.AbsoluteColorimetric);
        Assert.InRange(relative, 5, 20);
        Assert.Equal(0, absolute);
    }

    /// <summary>
    /// On the synthetic toe profile (CMYK → L* 20 at full black): compensation takes full black to
    /// sRGB black under relative colorimetric, and absolute colorimetric — never compensated — leaves
    /// it at the table's L* 20 grey; white is white either way.
    /// </summary>
    [Fact]
    public void TheProfilesBlack_LandsOnSrgbBlack_ExceptUnderAbsolute()
    {
        byte[] p = SyntheticCmykProfile.Build(
            b2a: SyntheticCmykProfile.KFromLightness(),
            a2b: SyntheticCmykProfile.LightnessFromKWithToe());
        IccProfile parsed = IccProfile.Parse(p);
        float[] black = { 0, 0, 0, 1 }, white = { 0, 0, 0, 0 };

        Assert.True(parsed.TryEvaluate(black, IccRenderingIntent.MediaRelativeColorimetric, out SKColor compensated));
        Assert.True(parsed.TryEvaluate(black, IccRenderingIntent.AbsoluteColorimetric, out SKColor raw));
        // A few levels off zero: the fixture writes a* = b* as 0x8080, which legacy Lab reads as +0.5.
        Assert.True(compensated.Red <= 4 && compensated.Green <= 4 && compensated.Blue <= 4, $"compensated black is {compensated}");
        Assert.InRange(raw.Red, 40, 60);   // L* 20 is sRGB ~48
        Assert.True(parsed.TryEvaluate(white, IccRenderingIntent.MediaRelativeColorimetric, out SKColor w));
        Assert.True(w.Red >= 253, $"white is {w}");
    }

    // ------------------------------------------------------------------ /UseBlackPtComp

    public static IEnumerable<object[]> ReferenceUseBlackPtComp()
    {
        // Compensation off: the reference renderer's -dBlackPtComp=0 PNG, which its per-state switch matches
        // when the key is written as a boolean (9.53 reads /OFF, a name, as on — the spec's values
        // are names). On and Default: its compensated PNG.
        yield return new object[] { "/Off", "1 1 1 1",   24, 23, 23 };
        yield return new object[] { "/Off", "0 0 0 1",   34, 33, 33 };
        yield return new object[] { "/Off", "0 0 0 0.5", 154, 155, 155 };
        yield return new object[] { "/On",  "1 1 1 1",   0, 0, 0 };
        yield return new object[] { "/Def", "0 0 0 1",   2, 0, 0 };
    }

    /// <summary>
    /// PDF 2.0's <c>/UseBlackPtComp</c> (ISO 32000-2 §8.6.5.9) switches the compensation: <c>/OFF</c>
    /// gives the reference renderer's uncompensated colour, <c>/ON</c> and <c>/Default</c> its compensated one.
    /// </summary>
    [RealIccProfileTheory("RSWOP.icm")]
    [MemberData(nameof(ReferenceUseBlackPtComp))]
    public void UseBlackPtComp_SwitchesTheCompensation_AsTheReferenceHasIt(string gs, string cmyk, int r, int g, int b)
    {
        byte[] profile = File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm"));
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(Pdf(profile, $"{gs} gs /CS0 cs {cmyk} sc 0 0 100 100 re f")), 0, 72);
        int i = 50 * page.Stride + 50 * 4;
        var ours = (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
        Assert.True(Math.Abs(ours.Item1 - r) <= 1 && Math.Abs(ours.Item2 - g) <= 1 && Math.Abs(ours.Item3 - b) <= 1,
            $"{gs} {cmyk}: {ours}, reference ({r}, {g}, {b})");
    }

    // Five full-black strips, 20 wide: OFF; OFF then a value the key does not have; OFF then
    // Default; OFF then ON; after the Q that ends the first four.
    private const string Strips =
        "q /Off gs /CS0 cs 0 0 0 1 sc 0 0 20 100 re f " +
        "/Bogus gs 0 0 0 1 sc 20 0 20 100 re f " +
        "q /Def gs 0 0 0 1 sc 40 0 20 100 re f Q " +
        "q /On gs 0 0 0 1 sc 60 0 20 100 re f Q Q " +
        "/CS0 cs 0 0 0 1 sc 80 0 20 100 re f";

    private static readonly bool[] StripsCompensated = { false, false, true, true, true };

    private static byte[] ToeProfile() => SyntheticCmykProfile.Build(
        b2a: SyntheticCmykProfile.KFromLightness(),
        a2b: SyntheticCmykProfile.LightnessFromKWithToe());

    /// <summary>
    /// On the toe profile (full black is L* 20, sRGB ~48 uncompensated): <c>/OFF</c> lasts until
    /// the state is restored, survives a value the key does not have, and <c>/Default</c> and
    /// <c>/ON</c> switch it back on — in the raster renderer.
    /// </summary>
    [Fact]
    public void UseBlackPtCompOff_LastsUntilRestored_AndDefaultOrOnEndIt_Raster()
    {
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(Pdf(ToeProfile(), Strips)), 0, 72);
        for (int s = 0; s < 5; s++)
        {
            int red = page.Pixels[50 * page.Stride + (s * 20 + 10) * 4];
            AssertBlackOrToeGrey(StripsCompensated[s], red, $"strip {s}");
        }
    }

    /// <summary>The same strips through the SVG writer: each path's fill.</summary>
    [Fact]
    public void UseBlackPtCompOff_LastsUntilRestored_AndDefaultOrOnEndIt_Svg()
    {
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(Pdf(ToeProfile(), Strips)), 0);
        var fills = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Select(e => e.Attribute("fill")?.Value)
            .Where(f => f != null && f.StartsWith("rgb(", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(5, fills.Count);
        for (int s = 0; s < 5; s++)
            AssertBlackOrToeGrey(StripsCompensated[s], int.Parse(fills[s].Substring(4).Split(',')[0]), $"strip {s}: {fills[s]}");
    }

    /// <summary>
    /// An image's own <c>/Intent</c> names a table, not a compensation (§8.9.5.2): under <c>/OFF</c>
    /// it stays uncompensated in both renderers; the same image outside is compensated (the control).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnImagesOwnIntent_KeepsTheStatesCompensationOff(bool raster)
    {
        foreach (bool off in new[] { true, false })
        {
            byte[] pdf = Pdf(ToeProfile(), (off ? "/Off gs " : "") + "100 0 0 100 0 0 cm /Im0 Do", image: true);
            int red;
            if (raster)
            {
                RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
                red = page.Pixels[50 * page.Stride + 50 * 4];
            }
            else
            {
                using SKBitmap png = JpxTestPdf.DecodeFirstEmbeddedPng(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
                red = png.GetPixel(0, 0).Red;
            }
            AssertBlackOrToeGrey(!off, red, off ? "image under /OFF" : "image, compensation on");
        }
    }

    /// <summary>
    /// Perceptual under <c>/OFF</c> still reads the perceptual table, in both renderers, whether the
    /// image names it or <c>ri</c> does — on a profile whose perceptual table prints black at L* 0 and
    /// whose relative table at L* 20, so the wrong table shows as grey. (The SVG writer hands the
    /// decode the <c>ri</c> intent by name; the modifier must not make that name unrecognisable.)
    /// </summary>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void PerceptualUnderOff_StillSelectsThePerceptualTable(bool raster, bool imageNamesIt)
    {
        byte[] profile = SyntheticCmykProfile.Build(
            b2a: SyntheticCmykProfile.KFromLightness(),
            a2b: SyntheticCmykProfile.LightnessFromKWithToe(),
            perceptualA2b: SyntheticCmykProfile.LightnessFromK());
        byte[] pdf = imageNamesIt
            ? Pdf(profile, "/Off gs 100 0 0 100 0 0 cm /Im0 Do", image: true)
            : Pdf(profile, "/Perceptual ri /Off gs 100 0 0 100 0 0 cm /Im0 Do", image: true, imageIntent: null);
        int red;
        if (raster)
        {
            RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
            red = page.Pixels[50 * page.Stride + 50 * 4];
        }
        else
        {
            using SKBitmap png = JpxTestPdf.DecodeFirstEmbeddedPng(PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));
            red = png.GetPixel(0, 0).Red;
        }
        Assert.True(red <= 4, $"perceptual black under /OFF is {red}; the relative table's would be ~48");
    }

    private static void AssertBlackOrToeGrey(bool compensated, int red, string what)
    {
        if (compensated) Assert.True(red <= 4, $"{what}: {red}, expected compensated black");
        else Assert.True(red >= 40 && red <= 60, $"{what}: {red}, expected the table's L* 20 grey");
    }

    private static byte[] Pdf(byte[] profile, string content, bool image = false, string imageIntent = "/Perceptual")
    {
        string prof = Encoding.Latin1.GetString(profile);
        const string sample = "\u0000\u0000\u0000\u00ff";   // one full-black CMYK sample
        string[] objs =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ColorSpace << /CS0 [/ICCBased 5 0 R] >> " +
            "/ExtGState << /Off << /UseBlackPtComp /OFF >> /On << /UseBlackPtComp /ON >> /Def << /UseBlackPtComp /Default >> /Bogus << /UseBlackPtComp /Sometimes >> >> " +
            (image ? "/XObject << /Im0 6 0 R >> " : "") + ">> /Contents 4 0 R >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /N 4 /Length " + prof.Length + " >>\nstream\n" + prof + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace [/ICCBased 5 0 R] /BitsPerComponent 8 " + (imageIntent == null ? "" : "/Intent " + imageIntent + " ") + "/Length 4 >>\nstream\n" + sample + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (int i = 0; i < objs.Length; i++) { offs[i] = sb.Length; sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n"); }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
