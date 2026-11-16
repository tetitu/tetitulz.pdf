// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfAConversionOptions.ConvertColour"/>: the device colour an output intent does not admit (A16–A18)
/// converted into the intent's own family — DeviceCMYK into an RGB intent, DeviceRGB into a CMYK one.
/// </summary>
public class PdfAColourConversionTests
{
    private static readonly string[] ColourChecks = { "A16", "A17", "A18" };

    [Fact]
    public void DeviceCmyk_UnderAnRgbIntent_IsConvertedIntoIt()
    {
        byte[] srgb = CompactIccProfiles.Bytes(CompactIccProfiles.sRgbv4);
        byte[] pdf = Page("0.1 0.7 0.3 0.05 k 10 10 50 50 re f");

        var control = Convert(pdf, new PdfAConversionOptions { IccProfile = srgb });
        Assert.NotEmpty(ColourViolations(control.Output));   // the control: CMYK under an RGB intent

        var converted = Convert(pdf, new PdfAConversionOptions { IccProfile = srgb, ConvertColour = true });
        Assert.Empty(ColourViolations(converted.Output));
        Assert.Equal(1, converted.Result.ColourConversion.ColoursConverted);
        Assert.True(converted.Result.IsConformant, string.Join(" | ", converted.Result.Blockers));
    }

    [Fact]
    public void DeviceRgb_UnderACmykIntent_IsConvertedIntoIt_AndLabIsLeft()
    {
        byte[] press = SyntheticCmykProfile.BlackFromLightness();
        byte[] pdf = Page("1 0 0 rg 10 10 50 50 re f /L0 cs 50 20 -10 sc 70 10 20 20 re f",
                          "/ColorSpace << /L0 [/Lab << /WhitePoint [0.9642 1 0.8249] >>] >>");

        var control = Convert(pdf, new PdfAConversionOptions { IccProfile = press });
        Assert.NotEmpty(ColourViolations(control.Output));

        var converted = Convert(pdf, new PdfAConversionOptions { IccProfile = press, ConvertColour = true });
        Assert.Empty(ColourViolations(converted.Output));
        Assert.Equal(1, converted.Result.ColourConversion.ColoursConverted);   // the rg; Lab is admitted
        Assert.Equal(0, converted.Result.ColourConversion.ColourSpacesConverted);
    }

    [Fact]
    public void AnIntentThatIsNeitherRgbNorCmyk_IsSaid()
    {
        var converted = Convert(Page("0.1 0.7 0.3 0.05 k 10 10 50 50 re f"),
            new PdfAConversionOptions { IccProfile = CompactIccProfiles.Bytes(CompactIccProfiles.sGreyv4), ConvertColour = true });
        Assert.Null(converted.Result.ColourConversion);
        Assert.Contains(converted.Result.Warnings, w => w.Contains("an RGB or CMYK profile", StringComparison.Ordinal) && w.Contains("GRAY", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ fixtures

    private static (byte[] Output, PdfAConversionResult Result) Convert(byte[] pdf, PdfAConversionOptions options)
    {
        var output = new MemoryStream();
        var result = PdfAConverter.ConvertToPdfA2b(new MemoryStream(pdf), output, options);
        return (output.ToArray(), result);
    }

    private static List<string> ColourViolations(byte[] pdf)
        => PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part2).Violations
            .Where(v => ColourChecks.Contains(v.Code)).Select(v => v.Code + ": " + v.Message).ToList();

    private static byte[] Page(string content, string resources = "")
    {
        string[] objects =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << {resources} >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
