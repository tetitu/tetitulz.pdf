// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Separations through a CMYK output profile (<see cref="PdfSeparationOptions.OutputIccProfile"/>):
/// colours without CMYK components reach the process plates through the
/// profile at their intent, DeviceGray stays on black, DeviceCMYK and spot inks keep their own.
/// Against the reference renderer.3 with <c>-sOutputICCProfile=RSWOP.icm</c> (its defaults: relative
/// colorimetric, black-point compensation on) where the host has the profile.
/// </summary>
public class SeparationOutputProfileTests
{
    private const string Page = @"
1 0 0 rg 0 150 50 50 re f
0.2 0.4 0.8 rg 50 150 50 50 re f
0.5 g 100 150 50 50 re f
1 0 0 0 k 150 150 50 50 re f
0.6 0.5 0.4 1 k 0 100 50 50 re f
/Perceptual ri 0.2 0.4 0.8 rg 50 100 50 50 re f /RelativeColorimetric ri
/CSl cs 50 20 -30 sc 100 100 50 50 re f
0 0 0 rg 150 100 50 50 re f
q 100 0 0 50 0 0 cm /Im1 Do Q
/CSs cs 1 scn 100 0 100 50 re f
";

    private static PdfSeparations Render(byte[] profile, bool bpc = true)
    {
        using var ms = new MemoryStream(Pdf());
        return PdfSeparationRenderer.RenderPage(ms, 0, 72, null,
            new PdfSeparationOptions { OutputIccProfile = profile, BlackPointCompensation = bpc });
    }

    private static int[] Inks(PdfSeparations s, int x, int y) =>
        new[] { "Cyan", "Magenta", "Yellow", "Black" }.Select(p => (int)s[p].Tints[(s.Height - y) * s.Width + x]).ToArray();

    public static IEnumerable<object[]> ReferencePoints()
    {
        yield return new object[] { "rgb red",             25, 175, new[] { 0, 255, 252, 2 } };
        yield return new object[] { "rgb blue",            75, 175, new[] { 238, 113, 0, 3 } };
        yield return new object[] { "DeviceGray 0.5",     125, 175, new[] { 0, 0, 0, 127 } };
        yield return new object[] { "rgb blue perceptual", 75, 125, new[] { 241, 113, 0, 6 } };
        yield return new object[] { "Lab 50 20 -30",      125, 125, new[] { 136, 153, 6, 0 } };
        yield return new object[] { "rgb black",          175, 125, new[] { 5, 7, 6, 245 } };
        yield return new object[] { "image red",           25, 25, new[] { 0, 255, 252, 2 } };
        yield return new object[] { "image blue",          75, 25, new[] { 238, 113, 0, 3 } };
    }

    /// <summary>
    /// Every point within one level of the reference renderer's plates through RSWOP: RGB, Lab and an RGB image
    /// through the profile at relative colorimetric with compensation, the <c>/Perceptual</c> swatch
    /// at its own intent, DeviceGray on black alone.
    /// </summary>
    [RealIccProfileTheory("RSWOP.icm")]
    [MemberData(nameof(ReferencePoints))]
    public void ThroughRswop_ThePlatesAreTheReferences(string what, int x, int y, int[] gs)
    {
        PdfSeparations s = Render(File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm")));
        int[] ours = Inks(s, x, y);
        for (int k = 0; k < 4; k++)
            Assert.True(Math.Abs(ours[k] - gs[k]) <= 1, $"{what}: [{string.Join(", ", ours)}], reference [{string.Join(", ", gs)}]");
        Assert.All(s.Plates, p => Assert.Equal(0, p.ApproximatedPaints));
    }

    /// <summary>
    /// DeviceCMYK passes through untouched: the document's CMYK is taken to be in the output space,
    /// the prepress convention — the reference renderer does the same once its default CMYK profile is the
    /// output profile (<c>-sDefaultCMYKProfile=RSWOP.icm</c>: 153/127/102/255); with its own default
    /// CMYK profile it converts between the two.
    /// </summary>
    [RealIccProfileFact("RSWOP.icm")]
    public void DeviceCmyk_PassesThroughTheOutputProfile()
    {
        PdfSeparations s = Render(File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm")));
        int[] rich = Inks(s, 25, 125);
        Assert.Equal(153, rich[0]);
        Assert.InRange(rich[1], 127, 128);
        Assert.Equal(new[] { 102, 255 }, new[] { rich[2], rich[3] });
    }

    /// <summary>Compensation off reaches the plates: sRGB black is then the bare relative table's — black alone, as the reference renderer with <c>-dBlackPtComp=0</c> has it.</summary>
    [RealIccProfileFact("RSWOP.icm")]
    public void BlackPointCompensationOff_LeavesBlackOnTheBlackPlateAlone()
    {
        PdfSeparations s = Render(File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm")), bpc: false);
        Assert.Equal(new[] { 0, 0, 0, 255 }, Inks(s, 175, 125));
    }

    // ------------------------------------------------------------------ synthetic, always run

    /// <summary>
    /// PDF 2.0's <c>/UseBlackPtComp /OFF</c> (ISO 32000-2 §8.6.5.9) reaches the plates as the
    /// option does: sRGB black through the toe profile (black prints at L* 20) is full K under it,
    /// and compensated — the profile's black for sRGB's — outside it.
    /// </summary>
    [Fact]
    public void UseBlackPtCompOff_ReachesThePlates()
    {
        byte[] profile = SyntheticCmykProfile.Build(
            b2a: SyntheticCmykProfile.KFromLightness(),
            a2b: SyntheticCmykProfile.LightnessFromKWithToe());
        PdfSeparations s = PdfSeparationRenderer.RenderPage(
            new MemoryStream(Pdf("q /Off gs 0 0 0 rg 0 0 100 200 re f Q 0 0 0 rg 100 0 100 200 re f")), 0, 72, null,
            new PdfSeparationOptions { OutputIccProfile = profile });
        Assert.Equal(new[] { 0, 0, 0, 255 }, Inks(s, 50, 100));
        Assert.Equal(new[] { 0, 0, 0, 231 }, Inks(s, 150, 100));   // the option's own compensated black
    }

    /// <summary>The same for an image read from its samples onto the plates: an RGB black image is full K under <c>/OFF</c>, compensated outside.</summary>
    [Fact]
    public void UseBlackPtCompOff_ReachesAnImageOnThePlates()
    {
        byte[] profile = SyntheticCmykProfile.Build(
            b2a: SyntheticCmykProfile.KFromLightness(),
            a2b: SyntheticCmykProfile.LightnessFromKWithToe());
        PdfSeparations s = PdfSeparationRenderer.RenderPage(
            new MemoryStream(Pdf("q /Off gs 100 0 0 200 0 0 cm /Im2 Do Q q 100 0 0 200 100 0 cm /Im2 Do Q")), 0, 72, null,
            new PdfSeparationOptions { OutputIccProfile = profile });
        Assert.Equal(new[] { 0, 0, 0, 255 }, Inks(s, 50, 100));
        Assert.Equal(new[] { 0, 0, 0, 231 }, Inks(s, 150, 100));
    }

    /// <summary>
    /// <c>sc</c> in a bare <c>/DeviceGray</c> space is DeviceGray too — black alone through the
    /// profile — while the RGB colour after it, which every colour operator un-marks, goes through it.
    /// </summary>
    [Fact]
    public void DeviceGraySetBySc_StaysOnBlack_AndTheNextColourIsNotGrey()
    {
        byte[] profile = SyntheticCmykProfile.Build(SyntheticCmykProfile.ConstantCyan(0x8000));
        const string content = "/DeviceGray cs 0.5 sc 0 0 100 100 re f 0 1 0 rg 100 0 100 100 re f";
        PdfSeparations s = PdfSeparationRenderer.RenderPage(new MemoryStream(Pdf(content)), 0, 72, null,
            new PdfSeparationOptions { OutputIccProfile = profile });
        Assert.Equal(new[] { 0, 0, 0, 127 }, Inks(s, 50, 50));
        Assert.Equal(128, Inks(s, 150, 50)[0]);
    }

    /// <summary>
    /// With a profile whose table is half a cyan everywhere: RGB and Lab take the table's cyan,
    /// DeviceGray stays on black alone, DeviceCMYK keeps its own cyan, the spot plate is the spot's,
    /// and without the profile the same red has no cyan at all.
    /// </summary>
    [Fact]
    public void ColoursWithoutCmyk_GoThroughTheProfile_OthersKeepTheirOwn()
    {
        byte[] profile = SyntheticCmykProfile.Build(SyntheticCmykProfile.ConstantCyan(0x8000));
        PdfSeparations s = Render(profile);
        Assert.Equal(128, Inks(s, 25, 175)[0]);              // RGB red: the table's cyan
        Assert.Equal(128, Inks(s, 125, 125)[0]);             // Lab
        Assert.Equal(128, Inks(s, 25, 25)[0]);               // RGB image
        Assert.Equal(new[] { 0, 0, 0, 127 }, Inks(s, 125, 175));   // DeviceGray: black alone
        Assert.Equal(255, Inks(s, 175, 175)[0]);             // DeviceCMYK cyan: its own
        Assert.Equal(255, s["Spot1"].Tints[(s.Height - 25) * s.Width + 150]);

        PdfSeparations plain = Render(null);
        Assert.Equal(0, Inks(plain, 25, 175)[0]);
        Assert.Equal(0, Inks(plain, 25, 25)[0]);
    }

    /// <summary>The facade's options overload and the renderer's are one answer, on a page where the profile changes the plates.</summary>
    [Fact]
    public void TheFacade_PassesTheOptionsThrough()
    {
        byte[] profile = SyntheticCmykProfile.Build(SyntheticCmykProfile.ConstantCyan(0x8000));
        var options = new PdfSeparationOptions { OutputIccProfile = profile };
        PdfSeparations direct = PdfSeparationRenderer.RenderPage(new MemoryStream(Pdf()), 0, 72, null, options);
        PdfSeparations facade = PdfOperations.RenderPageSeparations(new MemoryStream(Pdf()), 0, 72, null, options);
        Assert.Equal(direct["Cyan"].Tints, facade["Cyan"].Tints);
        Assert.Equal(128, Inks(facade, 25, 175)[0]);
    }

    [Fact]
    public void AnRgbProfile_IsRefusedAsAnOutput_WithItsReason()
    {
        byte[] rgb = SyntheticCmykProfile.Build(SyntheticCmykProfile.ConstantCyan(0));
        rgb[16] = (byte)'R'; rgb[17] = (byte)'G'; rgb[18] = (byte)'B'; rgb[19] = (byte)' ';
        var ex = Assert.Throws<ArgumentException>(() => Render(rgb));
        Assert.Contains("output ICC profile cannot be used", ex.Message, StringComparison.Ordinal);
    }

    private static byte[] Pdf(string content = null)
    {
        content ??= Page;
        string img = "ÿ\u0000\u00003fÌ";
        string[] objs =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /ColorSpace << " +
            "/CSl [/Lab << /WhitePoint [0.9642 1 0.8249] /Range [-100 100 -100 100] >>] " +
            "/CSs [/Separation /Spot1 /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>] >> " +
            "/XObject << /Im1 5 0 R /Im2 6 0 R >> /ExtGState << /Off << /UseBlackPtComp /OFF >> >> >> /Contents 4 0 R >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 6 >>\nstream\n" + img + "\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\n\u0000\u0000\u0000\nendstream",
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
