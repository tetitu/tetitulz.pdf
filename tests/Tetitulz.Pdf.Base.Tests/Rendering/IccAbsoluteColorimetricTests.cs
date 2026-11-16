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
/// Absolute colorimetric as itself: the relative table's colour scaled by the profile's media white
/// over D50 (<c>IccProfile.MediaWhiteScale</c>), the reference colour engine's rule — no scaling for a version 2 display
/// profile, whose <c>wtpt</c> is the illuminant. Against the reference renderer.3 (the reference colour engine) rendering
/// <c>/AbsoluteColorimetric ri</c> to PNG where the host has the profiles; synthetic matrix and grey
/// profiles everywhere.
/// </summary>
public class IccAbsoluteColorimetricTests
{
    // Components and the reference renderer's sRGB under absolute colorimetric, per profile.
    public static IEnumerable<object[]> Rswop() => new[]
    {
        new object[] { "0 0 0 0",          246, 243, 243 },   // the paper shows
        new object[] { "1 0 0 0",            0, 151, 205 },
        new object[] { "0.6 0.5 0.4 0.3",   93,  88,  90 },
        new object[] { "0 0 0 1",           32,  31,  31 },   // never compensated
        new object[] { "0.2 0.1 0 0",      199, 203, 223 },
    };

    // A version 2 printer RGB profile whose media white is D65: absolute shifts every colour.
    public static IEnumerable<object[]> Epson() => new[]
    {
        new object[] { "1 1 1",       236, 255, 255 },
        new object[] { "1 0 0",       255,  25,  23 },
        new object[] { "0.2 0.5 0.8",   0, 130, 237 },
        new object[] { "0.5 0.5 0.5", 118, 130, 148 },
    };

    // A version 2 display profile with a D65 wtpt: the reference colour engine reads its media white as D50, so absolute
    // is relative.
    public static IEnumerable<object[]> SrgbDisplay() => new[]
    {
        new object[] { "1 1 1",       255, 255, 255 },
        new object[] { "0.2 0.5 0.8",  51, 127, 204 },
    };

    [RealIccProfileTheory("RSWOP.icm")]
    [MemberData(nameof(Rswop))]
    public void AbsoluteColorimetric_ThroughRswop_IsTheReferences(string components, int r, int g, int b)
        => AssertReference("RSWOP.icm", components, r, g, b);

    [RealIccProfileTheory("Epson IJ Printer 07.icc")]
    [MemberData(nameof(Epson))]
    public void AbsoluteColorimetric_ThroughAD65PrinterProfile_IsTheReferences(string components, int r, int g, int b)
        => AssertReference("Epson IJ Printer 07.icc", components, r, g, b);

    [RealIccProfileTheory("sRGB Color Space Profile.icm")]
    [MemberData(nameof(SrgbDisplay))]
    public void AbsoluteColorimetric_ThroughAVersion2DisplayProfile_IsRelative_AsTheReferenceHasIt(string components, int r, int g, int b)
        => AssertReference("sRGB Color Space Profile.icm", components, r, g, b);

    /// <summary>One swatch within one level of the reference renderer's absolute-colorimetric render.</summary>
    private static void AssertReference(string profileName, string components, int r, int g, int b)
    {
        byte[] profile = File.ReadAllBytes(RealIccProfile.Path(profileName));
        int n = components.Split(' ').Length;
        var ours = Pixel(Pdf(profile, n, $"/AbsoluteColorimetric ri /CS0 cs {components} sc 0 0 100 100 re f"), raster: true);
        Assert.True(Math.Abs(ours.Item1 - r) <= 1 && Math.Abs(ours.Item2 - g) <= 1 && Math.Abs(ours.Item3 - b) <= 1,
            $"{profileName} {components}: {ours}, reference ({r}, {g}, {b})");
    }

    /// <summary>
    /// The separations' output side: sRGB through RSWOP under absolute colorimetric — the PCS colour
    /// divided by the paper's white, no compensation, and no white fix-up (white prints a little
    /// ink) — within one level of the reference renderer's separation output with <c>-sOutputICCProfile=RSWOP.icm</c>.
    /// </summary>
    [RealIccProfileFact("RSWOP.icm")]
    public void IntoRswop_AbsoluteColorimetricPlates_AreTheReferences()
    {
        const string content = "/AbsoluteColorimetric ri 1 1 1 rg 0 0 20 20 re f 1 0 0 rg 20 0 20 20 re f " +
                               "0.2 0.4 0.8 rg 40 0 20 20 re f 0.5 0.5 0.5 rg 60 0 20 20 re f 0 0 0 rg 80 0 20 20 re f";
        PdfSeparations s = PdfSeparationRenderer.RenderPage(new MemoryStream(PlainPdf(content)), 0, 72, null,
            new PdfSeparationOptions { OutputIccProfile = File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm")) });
        var expected = new Dictionary<string, int[]>
        {
            ["Cyan"] = new[] { 1, 0, 241, 64, 0 }, ["Magenta"] = new[] { 4, 255, 103, 54, 0 },
            ["Yellow"] = new[] { 7, 255, 0, 53, 0 }, ["Black"] = new[] { 0, 0, 5, 80, 255 },
        };
        foreach (var (plate, inks) in expected)
        {
            int[] ours = Enumerable.Range(0, 5).Select(i => (int)s[plate].Tints[10 * s.Width + i * 20 + 10]).ToArray();
            for (int i = 0; i < 5; i++)
                Assert.True(Math.Abs(ours[i] - inks[i]) <= 1, $"{plate}: [{string.Join(", ", ours)}], reference [{string.Join(", ", inks)}]");
        }
    }

    // ------------------------------------------------------------------ synthetic, always run

    /// <summary>
    /// Into an output profile whose paper is 80 % of D50, absolute colorimetric divides the colour by
    /// that white: an sRGB mid grey prints lighter than under relative (here K from L* alone, so the
    /// ink is 1 − L*/100 of the divided colour). And a paper brighter than D50's white, which sRGB
    /// white cannot reach, is not forced to no ink under absolute, as relative's white fix-up forces it.
    /// </summary>
    [Fact]
    public void IntoAnOutputProfile_AbsoluteDividesByThePapersWhite()
    {
        byte[] profile = SyntheticCmykProfile.Build(
            b2a: SyntheticCmykProfile.KFromLightness(),
            mediaWhite: new[] { 0.8 * PdfColor.D50X, 0.8 * PdfColor.D50Y, 0.8 * PdfColor.D50Z });
        var grey = new SKColor(128, 128, 128);
        var r = new float[4]; var a = new float[4];
        Assert.True(IccOutputTransform.TryCreate(profile, IccRenderingIntent.MediaRelativeColorimetric, false, out _).TryConvert(grey, r));
        Assert.True(IccOutputTransform.TryCreate(profile, IccRenderingIntent.AbsoluteColorimetric, false, out _).TryConvert(grey, a));

        PdfColor.SRgbToXyzD50(grey, out double x, out double y, out double z);
        PdfColor.XyzD50ToLab(x / 0.8, y / 0.8, z / 0.8, out double l, out _, out _);
        Assert.InRange(a[3], 1 - l / 100 - 0.01, 1 - l / 100 + 0.01);
        Assert.True(a[3] < r[3] - 0.05f, $"absolute K {a[3]} against relative {r[3]}");

        byte[] brightPaper = SyntheticCmykProfile.Build(
            b2a: SyntheticCmykProfile.KFromLightness(),
            mediaWhite: new[] { 1.25 * PdfColor.D50X, 1.25 * PdfColor.D50Y, 1.25 * PdfColor.D50Z });
        var w = new float[4];
        Assert.True(IccOutputTransform.TryCreate(brightPaper, IccRenderingIntent.AbsoluteColorimetric, false, out _).TryConvert(SKColors.White, w));
        Assert.True(w[3] > 0.05f, $"absolute white K {w[3]}");   // white at Y 0.8 of that paper: L* 92, K ~0.08
        Assert.True(IccOutputTransform.TryCreate(brightPaper, IccRenderingIntent.MediaRelativeColorimetric, false, out _).TryConvert(SKColors.White, w));
        Assert.Equal(0f, w[3]);
    }

    /// <summary>A white at 90 % of D50: absolute colorimetric renders the profile's white as this grey.</summary>
    private static readonly (byte, byte, byte) Dimmed = Srgb(PdfColor.XyzD50ToSRgb(0.9 * PdfColor.D50X, 0.9 * PdfColor.D50Y, 0.9 * PdfColor.D50Z));

    private static byte[] MatrixRgb(string deviceClass, double whiteScale, int majorVersion = 2)
    {
        byte[] p = IccTransformTests.BuildProfile(deviceClass, "RGB ", "XYZ ",
            ("rXYZ", IccTransformTests.XyzTag(0.4361, 0.2225, 0.0139)),
            ("gXYZ", IccTransformTests.XyzTag(0.3851, 0.7169, 0.0971)),
            ("bXYZ", IccTransformTests.XyzTag(0.1431, 0.0606, 0.7141)),
            ("rTRC", IccTransformTests.CurvGamma(1.0)),
            ("gTRC", IccTransformTests.CurvGamma(1.0)),
            ("bTRC", IccTransformTests.CurvGamma(1.0)),
            ("wtpt", IccTransformTests.XyzTag(whiteScale * PdfColor.D50X, whiteScale * PdfColor.D50Y, whiteScale * PdfColor.D50Z)));
        p[8] = (byte)majorVersion;
        return p;
    }

    /// <summary>
    /// A matrix/TRC input profile and a grey one whose media white is 90 % of D50: their white is
    /// that grey under absolute colorimetric and white under relative.
    /// </summary>
    [Fact]
    public void TheMediaWhite_ScalesMatrixAndGreyProfiles()
    {
        IccProfile rgb = IccProfile.Parse(MatrixRgb("scnr", 0.9));
        Assert.True(rgb.TryEvaluate(new[] { 1f, 1f, 1f }, IccRenderingIntent.AbsoluteColorimetric, out SKColor rgbAbs));
        Assert.True(rgb.TryEvaluate(new[] { 1f, 1f, 1f }, IccRenderingIntent.MediaRelativeColorimetric, out SKColor rgbRel));
        AssertNear(Dimmed, Srgb(rgbAbs), "matrix, absolute");
        Assert.True(rgbRel.Red >= 253, $"matrix, relative: {rgbRel}");

        byte[] grey = IccTransformTests.BuildProfile("scnr", "GRAY", "XYZ ",
            ("kTRC", IccTransformTests.CurvGamma(1.0)),
            ("wtpt", IccTransformTests.XyzTag(0.9 * PdfColor.D50X, 0.9 * PdfColor.D50Y, 0.9 * PdfColor.D50Z)));
        grey[8] = 2;
        IccProfile g = IccProfile.Parse(grey);
        Assert.True(g.TryEvaluate(new[] { 1f }, IccRenderingIntent.AbsoluteColorimetric, out SKColor greyAbs));
        AssertNear(Dimmed, Srgb(greyAbs), "grey, absolute");
    }

    /// <summary>
    /// A version 2 display profile is not scaled — its wtpt is the illuminant (the reference colour engine) — but a
    /// version 4 one is; and a white at D50 to the tag's four decimals (0.9642/1/0.8249 against the
    /// exact constant's 0.8251) scales nothing, so a neutral grey gets no cast.
    /// </summary>
    [Fact]
    public void DisplayProfilesOfVersion2_AndAD50White_AreNotScaled()
    {
        var white = new[] { 1f, 1f, 1f };
        Assert.True(IccProfile.Parse(MatrixRgb("mntr", 0.9, 2)).TryEvaluate(white, IccRenderingIntent.AbsoluteColorimetric, out SKColor v2));
        Assert.True(IccProfile.Parse(MatrixRgb("mntr", 0.9, 4)).TryEvaluate(white, IccRenderingIntent.AbsoluteColorimetric, out SKColor v4));
        Assert.True(v2.Red >= 253, $"v2 display: {v2}");
        AssertNear(Dimmed, Srgb(v4), "v4 display");

        byte[] d50 = IccTransformTests.BuildProfile("scnr", "RGB ", "XYZ ",
            ("rXYZ", IccTransformTests.XyzTag(0.4361, 0.2225, 0.0139)),
            ("gXYZ", IccTransformTests.XyzTag(0.3851, 0.7169, 0.0971)),
            ("bXYZ", IccTransformTests.XyzTag(0.1431, 0.0606, 0.7141)),
            ("rTRC", IccTransformTests.CurvGamma(1.0)),
            ("gTRC", IccTransformTests.CurvGamma(1.0)),
            ("bTRC", IccTransformTests.CurvGamma(1.0)),
            ("wtpt", IccTransformTests.XyzTag(0.9642, 1.0, 0.8249)));
        IccProfile p = IccProfile.Parse(d50);
        var grey = new[] { 0.5f, 0.5f, 0.5f };
        Assert.True(p.TryEvaluate(grey, IccRenderingIntent.AbsoluteColorimetric, out SKColor abs));
        Assert.True(p.TryEvaluate(grey, IccRenderingIntent.MediaRelativeColorimetric, out SKColor rel));
        Assert.Equal(rel, abs);
    }

    /// <summary>
    /// Both renderers read <c>/AbsoluteColorimetric ri</c>: the synthetic input profile's white fills
    /// as the dimmed grey in each, and as white under the default intent.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BothRenderers_HonourAbsoluteColorimetric(bool raster)
    {
        byte[] profile = MatrixRgb("scnr", 0.9);
        AssertNear(Dimmed, Pixel(Pdf(profile, 3, "/AbsoluteColorimetric ri /CS0 cs 1 1 1 sc 0 0 100 100 re f"), raster), raster ? "raster" : "svg");
        var relative = Pixel(Pdf(profile, 3, "/CS0 cs 1 1 1 sc 0 0 100 100 re f"), raster);
        Assert.True(relative.Item1 >= 253, $"relative: {relative}");
    }

    // ------------------------------------------------------------------ helpers

    private static (byte, byte, byte) Srgb(SKColor c) => (c.Red, c.Green, c.Blue);

    private static void AssertNear((int, int, int) expected, (int, int, int) actual, string what) =>
        Assert.True(Math.Abs(expected.Item1 - actual.Item1) <= 1 && Math.Abs(expected.Item2 - actual.Item2) <= 1
                    && Math.Abs(expected.Item3 - actual.Item3) <= 1, $"{what}: {actual}, expected {expected}");

    private static (byte, byte, byte) Pixel(byte[] pdf, bool raster)
    {
        if (raster)
        {
            RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
            int i = 50 * page.Stride + 50 * 4;
            return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
        }
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);
        string fill = XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "path")
            .Select(e => e.Attribute("fill")?.Value).First(f => f != null && f.StartsWith("rgb(", StringComparison.Ordinal));
        byte[] c = fill.Substring(4, fill.Length - 5).Split(',').Select(byte.Parse).ToArray();
        return (c[0], c[1], c[2]);
    }

    private static byte[] Pdf(byte[] profile, int n, string content)
    {
        string prof = Encoding.Latin1.GetString(profile);
        string[] objs =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ColorSpace << /CS0 [/ICCBased 5 0 R] >> >> /Contents 4 0 R >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /N " + n + " /Length " + prof.Length + " >>\nstream\n" + prof + "\nendstream",
        };
        return Build(objs);
    }

    private static byte[] PlainPdf(string content) => Build(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 20] /Contents 4 0 R >>",
        "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
    });

    private static byte[] Build(string[] objs)
    {
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
