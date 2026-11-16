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
/// A shading on separation plates is sampled from its own components (the plate's tint per sample,
/// <c>ShadingSpace.OnPlate</c>), not from its sRGB pixels through each plate's filter. Pinned to
/// the reference renderer.3 separation output at 72 dpi (its plates store 255 − ink), within one level, at
/// x = 5, 25, 50, 75 and 95 across a 100-point axial ramp.
/// </summary>
public class ShadingPlateTests
{
    private static readonly int[] Xs = { 5, 25, 50, 75, 95 };

    private const string RichBlack =
        "/Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 1] /C1 [0.6 0.5 0.4 1] /N 1 >> /Extend [true true] >> >>";

    private const string SpotRamp =
        "/Shading << /Sh << /ShadingType 2 /ColorSpace [/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>] /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> /Extend [true true] >> >>";

    private const string CyanRamp =
        "/Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceCMYK /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >> /Extend [true true] >> >>";

    private const string Overprint = " /ExtGState << /GS << /OP true /op true /OPM 1 >> >>";

    private static readonly int[] Zero = { 0, 0, 0, 0, 0 }, Full = { 255, 255, 255, 255, 255 }, Ramp = { 14, 65, 129, 193, 244 };

    public static IEnumerable<object[]> ReferencePlates()
    {
        // A rich black ramp: K stays full while C, M, Y climb — which the sRGB route cannot keep
        // (its dark colours collapse onto black).
        yield return new object[] { "rich black", "/Sh sh", RichBlack, new Dictionary<string, int[]>
        {
            ["Cyan"] = new[] { 8, 39, 77, 115, 146 }, ["Magenta"] = new[] { 7, 32, 64, 96, 122 },
            ["Yellow"] = new[] { 5, 26, 51, 77, 97 }, ["Black"] = Full,
        } };
        // The same as a shading pattern.
        yield return new object[] { "rich black pattern", "/Pattern cs /P scn 0 0 100 20 re f",
            RichBlack.Replace("/Shading << /Sh <<", "/Pattern << /P << /PatternType 2 /Shading <<") + " >>", new Dictionary<string, int[]>
        {
            ["Cyan"] = new[] { 8, 39, 77, 115, 146 }, ["Magenta"] = new[] { 7, 32, 64, 96, 122 },
            ["Yellow"] = new[] { 5, 26, 51, 77, 97 }, ["Black"] = Full,
        } };
        // A spot ramp: its tints on its own plate, nothing on the process plates.
        yield return new object[] { "spot", "/Sh sh", SpotRamp, new Dictionary<string, int[]>
        {
            ["Spot"] = Ramp, ["Cyan"] = Zero, ["Magenta"] = Zero, ["Yellow"] = Zero, ["Black"] = Zero,
        } };
        // Overprinting the spot ramp over black: the process plates are left alone.
        yield return new object[] { "overprinting spot", "0 0 0 1 k 0 0 100 20 re f /GS gs /Sh sh", SpotRamp + Overprint, new Dictionary<string, int[]>
        {
            ["Spot"] = Ramp, ["Black"] = Full, ["Cyan"] = Zero,
        } };
        // §8.6.7: OPM 1 does not apply to shadings, so an overprinting DeviceCMYK shading whose
        // black is zero still knocks the black underneath out.
        yield return new object[] { "overprinting CMYK under OPM 1", "0 0 0 1 k 0 0 100 20 re f /GS gs /Sh sh", CyanRamp + Overprint, new Dictionary<string, int[]>
        {
            ["Cyan"] = Ramp, ["Black"] = Zero, ["Magenta"] = Zero,
        } };
    }

    /// <summary>Every plate within one level of the reference renderer's, and none reported as approximated.</summary>
    [Theory]
    [MemberData(nameof(ReferencePlates))]
    public void TheShadingsPlates_AreTheReferences(string what, string content, string resources, Dictionary<string, int[]> expected)
    {
        PdfSeparations s = PdfSeparationRenderer.RenderPage(new MemoryStream(Pdf(content, resources)), 0, 72);
        foreach (var (plate, inks) in expected) AssertPlate(s, plate, inks, what);
    }

    /// <summary>
    /// A DeviceN shading of a spot and a process ink: each name's tint lands on its own plate —
    /// the spot rising, magenta falling, as the reference renderer has it. Object 5 is the tint transform,
    /// (spot, magenta) → (0, magenta, 0, 0).
    /// </summary>
    [Fact]
    public void ADeviceNShading_PutsEachNamesTintOnItsPlate()
    {
        const string fn = "{ exch pop 0 exch 0 0 }";
        string res = "/Shading << /Sh << /ShadingType 2 /ColorSpace [/DeviceN [/Spot /Magenta] /DeviceCMYK 5 0 R] /Coords [0 0 100 0] " +
                     "/Function << /FunctionType 2 /Domain [0 1] /C0 [0 1] /C1 [1 0] /N 1 >> /Extend [true true] >> >>";
        byte[] pdf = Pdf("/Sh sh", res, "<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1 0 1] /Length " + fn.Length + " >>\nstream\n" + fn + "\nendstream");
        PdfSeparations s = PdfSeparationRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        AssertPlate(s, "Spot", Ramp, "DeviceN");
        AssertPlate(s, "Magenta", new[] { 241, 190, 126, 62, 11 }, "DeviceN");
        AssertPlate(s, "Cyan", Zero, "DeviceN");
    }

    /// <summary>
    /// A DeviceGray shading stays on black through an output profile, as a DeviceGray fill does
    /// (the reference renderer's DeviceGrayToK) — the profile here would make any grey it converts pure cyan.
    /// </summary>
    [Fact]
    public void ADeviceGrayShading_StaysOnBlack_ThroughAnOutputProfile()
    {
        string res = "/Shading << /Sh << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 100 0] " +
                     "/Function << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >> /Extend [true true] >> >>";
        byte[] profile = SyntheticCmykProfile.Build(b2a: SyntheticCmykProfile.ConstantCyan(0x8000));
        PdfSeparations s = PdfSeparationRenderer.RenderPage(new MemoryStream(Pdf("/Sh sh", res)), 0, 72, null,
            new PdfSeparationOptions { OutputIccProfile = profile });
        AssertPlate(s, "Black", Ramp, "DeviceGray");
        AssertPlate(s, "Cyan", Zero, "DeviceGray");
    }

    private static void AssertPlate(PdfSeparations s, string plate, int[] inks, string what)
    {
        PdfSeparationPlate p = s[plate];
        Assert.Equal(0, p.ApproximatedPaints);
        int[] ours = Xs.Select(x => (int)p.Tints[10 * s.Width + x]).ToArray();
        for (int i = 0; i < Xs.Length; i++)
            Assert.True(Math.Abs(ours[i] - inks[i]) <= 1, $"{what}, {plate}: [{string.Join(", ", ours)}], expected [{string.Join(", ", inks)}]");
    }

    private static byte[] Pdf(string content, string resources, params string[] extra)
    {
        string[] objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 20] /Resources << " + resources + " >> /Contents 4 0 R >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
        }.Concat(extra).ToArray();
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
