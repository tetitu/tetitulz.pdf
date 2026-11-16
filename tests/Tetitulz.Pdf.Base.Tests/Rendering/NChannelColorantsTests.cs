// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A spot ink's appearance alone — what the overprint preview multiplies in and a PSD's spot channel
/// carries — is the Separation a DeviceN space's <c>/Colorants</c> defines for it, where it defines one:
/// ISO 32000-1 §8.6.6.5, the Separation's tint transform describes "the appearance of that colorant
/// alone, whereas those of a DeviceN colour space describe only the appearance of its colorants in
/// combination". Until 2026-10-22 the DeviceN's transform was sliced, the other inks at 0.
/// </summary>
/// <remarks>
/// The fixture's DeviceN transform paints Spot1 at full tint as magenta and yellow (red); its
/// <c>/Colorants</c> define Spot1 as cyan. The reference renderer's composite slices the DeviceN (red), as this
/// renderer did; the specification's sentence decides.
/// </remarks>
public class NChannelColorantsTests
{
    /// <summary>With <c>/Colorants</c> naming the ink, its Separation answers: cyan at full tint, white at none.</summary>
    [Theory]
    [InlineData("/NChannel")]
    [InlineData("/DeviceN")]
    public void ADeviceNSpotInk_AppearsAsItsOwnSeparation(string subtype)
    {
        using var doc = Open(Space($"<< /Subtype {subtype} /Colorants << /Spot1 [/Separation /Spot1 /DeviceCMYK 6 0 R] >> >>"));
        SKColor[] ink = OverprintPreview.Appearance(doc, CS1(doc), "Spot1");

        Assert.Equal(PdfColor.CmykToRgb(1, 0, 0, 0), ink[255]);
        Assert.Equal(PdfColor.CmykToRgb(0, 0, 0, 0), ink[0]);
    }

    /// <summary>
    /// The controls: with no <c>/Colorants</c>, or <c>/Colorants</c> that do not define this ink, the
    /// DeviceN's own transform answers, the ink alone — red.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" << /Subtype /NChannel >>")]
    [InlineData(" << /Subtype /NChannel /Colorants << /Spot2 [/Separation /Spot2 /DeviceCMYK 6 0 R] >> >>")]
    [InlineData(" << /Subtype /NChannel /Colorants << /Spot1 [/Separation /Spot2 /DeviceCMYK 6 0 R] >> >>")]
    [InlineData(" << /Subtype /NChannel /Colorants << /Spot1 [/Indexed /Spot1 /DeviceCMYK 6 0 R] >> >>")]
    public void WithoutItsOwnSeparation_TheDeviceNTransformAnswers(string attributes)
    {
        using var doc = Open(Space(attributes));
        Assert.Equal(PdfColor.CmykToRgb(0, 1, 1, 0), OverprintPreview.Appearance(doc, CS1(doc), "Spot1")[255]);
    }

    /// <summary>
    /// Only a DeviceN has attributes: a Separation with a stray fifth element holding <c>/Colorants</c>
    /// still answers through its own transform, red.
    /// </summary>
    [Fact]
    public void ASeparationsStrayFifthElement_IsNotItsAttributes()
    {
        using var doc = Open("[/Separation /Spot1 /DeviceCMYK 5 0 R << /Colorants << /Spot1 [/Separation /Spot1 /DeviceCMYK 6 0 R] >> >>]");
        Assert.Equal(PdfColor.CmykToRgb(0, 1, 1, 0), OverprintPreview.Appearance(doc, CS1(doc), "Spot1")[255]);
    }

    private static string Space(string attributes) => "[/DeviceN [/Spot1] /DeviceCMYK 5 0 R " + attributes + "]";

    private static PdfArray CS1(PdfDocument doc)
    {
        var resources = PdfResolve.Dict(doc, doc.Pages[0].Dictionary, PdfName.Resources);
        var spaces = PdfResolve.Dict(doc, resources, PdfName.ColorSpace);
        return (PdfArray)doc.ResolveObject(spaces[new PdfName("CS1")]);
    }

    private static PdfDocument Open(string space)
    {
        const string content = "/CS1 cs 1 scn 0 0 100 100 re f";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources << /ColorSpace << /CS1 " + space + " >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>",
        };
        var sb = new StringBuilder("%PDF-1.6\n");
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
        return PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
    }
}
