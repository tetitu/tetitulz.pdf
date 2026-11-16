// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A DeviceRGB colour reaches the process plates through the graphics state's black generation and
/// undercolour removal (<c>/BG</c>, <c>/UCR</c> and their <c>2</c> forms, ISO 32000-1 §10.3.4):
/// c = 1 − r, m = 1 − g, y = 1 − b, k = min(c, m, y); each ink less UCR(k), black BG(k).
/// </summary>
/// <remarks>
/// <para>Held to the reference renderer's separation output under <c>-dUseFastColor=true</c>, the path on which it
/// reads them (2026-10-22): <c>0.2 0.4 0.6 rg</c> under BG(k) = k/2, UCR(k) = 0 inks 204, 153, 102, 51;
/// under BG(k) = k, UCR(k) = k, 102, 51, 0, 102. Its own default is no black generation; this renderer's
/// is the exact inverse of <c>CmykToRgb</c>, kept where the file sets neither. The reference renderer carried a
/// <c>gs</c>'s functions past the <c>Q</c> that closed it; they are graphics-state parameters (Table 52),
/// which <c>Q</c> restores, and here they are restored. Until 2026-10-22 all four keys were parsed,
/// ignored and reported.</para>
/// </remarks>
public class BlackGenerationTests
{
    private const string HalfK = "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [0.5] /N 1 >>";
    private const string WholeK = "<< /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >>";
    private const string NoneK = "<< /FunctionType 2 /Domain [0 1] /Range [-1 1] /C0 [0] /C1 [0] /N 1 >>";
    private const string WholeUcr = "<< /FunctionType 2 /Domain [0 1] /Range [-1 1] /C0 [0] /C1 [1] /N 1 >>";

    /// <summary>The two probes, inks as the reference renderer's separation output has them.</summary>
    [Theory]
    [InlineData(HalfK, NoneK, 204, 153, 102, 51)]
    [InlineData(WholeK, WholeUcr, 102, 51, 0, 102)]
    public void ADeviceRgbFill_GoesThroughBlackGenerationAndUndercolorRemoval_AsTheReferenceDoes(
        string bg, string ucr, int cyan, int magenta, int yellow, int black)
    {
        PdfSeparations s = Render("/GS1 gs 0.2 0.4 0.6 rg 0 0 100 100 re f", $"/BG {bg} /UCR {ucr}");
        Assert.Equal(new[] { cyan, magenta, yellow, black }, Inks(s, 50, 50));
    }

    /// <summary>
    /// Q restores them, and with neither set the plates keep the device's conversion, the exact
    /// inverse (0.2 0.4 0.6: k 0.4, the inks normalised by 1 − k — 170, 85, 0, 102).
    /// </summary>
    [Fact]
    public void QRestoresThem_AndWithNeitherSet_TheExactInverseStands()
    {
        PdfSeparations s = Render("q /GS1 gs 0.2 0.4 0.6 rg 0 0 50 100 re f Q 0.2 0.4 0.6 rg 50 0 50 100 re f",
            $"/BG {HalfK} /UCR {NoneK}");
        Assert.Equal(new[] { 204, 153, 102, 51 }, Inks(s, 25, 50));
        Assert.Equal(new[] { 170, 85, 0, 102 }, Inks(s, 75, 50));
    }

    /// <summary>
    /// <c>/BG2</c> and <c>/UCR2</c> win over <c>/BG</c> and <c>/UCR</c>; <c>/Default</c> for both is
    /// the device's own conversion again; a key a later <c>gs</c> leaves out keeps its function, and an
    /// absent UCR is the identity — full removal, here with BG(k) = k/2: 102, 51, 0, 51.
    /// </summary>
    [Fact]
    public void TheTwoForms_Win_DefaultResets_AndAnAbsentFunctionIsTheIdentity()
    {
        string res = $"/BG {WholeK} /BG2 {HalfK} /UCR {WholeUcr} /UCR2 {NoneK}";
        Assert.Equal(new[] { 204, 153, 102, 51 }, Inks(Render("/GS1 gs 0.2 0.4 0.6 rg 0 0 100 100 re f", res), 50, 50));

        Assert.Equal(new[] { 170, 85, 0, 102 },
            Inks(Render("/GS1 gs /GS2 gs 0.2 0.4 0.6 rg 0 0 100 100 re f", res, "/BG2 /Default /UCR2 /Default"), 50, 50));

        Assert.Equal(new[] { 102, 51, 0, 51 }, Inks(Render("/GS1 gs 0.2 0.4 0.6 rg 0 0 100 100 re f", $"/BG {HalfK}"), 50, 50));
        Assert.Equal(new[] { 204, 153, 102, 51 },
            Inks(Render("/GS1 gs /GS2 gs 0.2 0.4 0.6 rg 0 0 100 100 re f", $"/BG {HalfK} /UCR {NoneK}", "/OP false"), 50, 50));

        // A later gs setting /UCR alone keeps the earlier /BG: k/2 black, full removal — 102, 51, 0, 51.
        Assert.Equal(new[] { 102, 51, 0, 51 },
            Inks(Render("/GS1 gs /GS2 gs 0.2 0.4 0.6 rg 0 0 100 100 re f", $"/BG {HalfK} /UCR {NoneK}", $"/UCR {WholeUcr}"), 50, 50));
    }

    /// <summary>
    /// Only DeviceRGB is converted so: a DeviceCMYK fill keeps its components, a DeviceGray fill stays on
    /// the black plate, and an RGB colour set with <c>sc</c> in a bare <c>/DeviceRGB</c> space is converted
    /// as <c>rg</c>'s is.
    /// </summary>
    [Fact]
    public void OnlyADeviceRgbColour_IsConverted()
    {
        string res = $"/BG {HalfK} /UCR {NoneK}";
        Assert.Equal(new[] { 51, 102, 153, 26 }, Inks(Render("/GS1 gs 0.2 0.4 0.6 0.1 k 0 0 100 100 re f", res), 50, 50));
        Assert.Equal(new[] { 0, 0, 0, 153 }, Inks(Render("/GS1 gs 0.4 g 0 0 100 100 re f", res), 50, 50));
        Assert.Equal(new[] { 204, 153, 102, 51 }, Inks(Render("/GS1 gs /DeviceRGB cs 0.2 0.4 0.6 sc 0 0 100 100 re f", res), 50, 50));
        // A grey set after an RGB colour is grey again: the black plate alone, not /BG's.
        Assert.Equal(new[] { 0, 0, 0, 153 }, Inks(Render("/GS1 gs 0.2 0.4 0.6 rg 0.4 g 0 0 100 100 re f", res), 50, 50));
    }

    /// <summary>
    /// The stroke has its own colour space: an RGB stroke over a grey fill goes through <c>/BG</c> and
    /// <c>/UCR</c>, the fill does not.
    /// </summary>
    [Fact]
    public void AStroke_IsConvertedByItsOwnColourSpace()
    {
        PdfSeparations s = Render("/GS1 gs 0.4 g 0.2 0.4 0.6 RG 20 w 0 50 m 100 50 l S 0 0 100 20 re f",
            $"/BG {HalfK} /UCR {NoneK}");
        Assert.Equal(new[] { 204, 153, 102, 51 }, Inks(s, 50, 50));
        Assert.Equal(new[] { 0, 0, 0, 153 }, Inks(s, 50, 10));
    }

    private static int[] Inks(PdfSeparations s, int x, int y)
    {
        var inks = new int[4];
        string[] plates = { "Cyan", "Magenta", "Yellow", "Black" };
        for (int i = 0; i < 4; i++) inks[i] = s[plates[i]].Tints[(s.Height - y) * s.Width + x];
        return inks;
    }

    private static PdfSeparations Render(string content, string gs1, string gs2 = "")
    {
        using var ms = new MemoryStream(Pdf(content, gs1, gs2));
        return PdfSeparationRenderer.RenderPage(ms, 0, 72);
    }

    private static byte[] Pdf(string content, string gs1, string gs2)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << /ExtGState << /GS1 << " + gs1
                + " >> /GS2 << " + gs2 + " >> >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
