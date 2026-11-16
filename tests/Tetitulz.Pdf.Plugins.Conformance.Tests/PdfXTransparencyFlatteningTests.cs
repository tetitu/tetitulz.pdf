// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfXConversionOptions.FlattenTransparency"/>: PDF/X-1a conversion flattens transparency
/// (X09) to the page's separations — CMYK, its spot ink kept as a DeviceN channel — instead of stopping
/// at it.
/// </summary>
/// <remarks>
/// The fixture paints opaque black, then at half opacity a spot ink (Rubine, alternate DeviceCMYK). The
/// test profile is a header-only CMYK profile, which the plates cannot convert RGB through — so the
/// converter flattens without it and says so.
/// </remarks>
public class PdfXTransparencyFlatteningTests
{
    [Fact]
    public void ATransparentPage_IsFlattenedToItsSeparations_AndTheResultValidates()
    {
        var (output, result) = Convert(PdfXConformanceLevel.X1a, flatten: true);

        Assert.Equal(new[] { 0 }, result.FlattenedPages);
        Assert.Empty(result.Blockers);
        Assert.True(result.IsConformant);

        var validation = PdfXValidator.Validate(new MemoryStream(output), PdfXConformanceLevel.X1a);
        Assert.DoesNotContain(validation.Violations, v => v.Code == "X09" || v.Code == "X08");

        // FlattenDpi reached the flattener: the 200-point page at 36 dpi is a 100-pixel image, in DeviceN.
        using var doc = Core.Document.PdfDocument.Open(new MemoryStream(output));
        var resources = (Core.Objects.PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[Core.Objects.PdfName.Resources]);
        var xobjects = (Core.Objects.PdfDictionary)doc.ResolveObject(resources[Core.Objects.PdfName.XObject]);
        var image = (Core.Objects.PdfStream)doc.ResolveObject(xobjects.First().Value);
        Assert.Equal(100, ((Core.Objects.PdfNumber)doc.ResolveObject(image[Core.Objects.PdfName.Width])).IntValue);
        var space = (Core.Objects.PdfArray)doc.ResolveObject(image[Core.Objects.PdfName.ColorSpace]);
        Assert.Equal("DeviceN", ((Core.Objects.PdfName)space[0]).Value);
    }

    /// <summary>The control: without the option, the transparency is a blocker and nothing is flattened.</summary>
    [Fact]
    public void WithoutTheOption_TheTransparencyIsABlocker()
    {
        var (_, result) = Convert(PdfXConformanceLevel.X1a, flatten: false);

        Assert.Empty(result.FlattenedPages);
        Assert.Contains(result.Blockers, b => b.StartsWith("X09"));
    }

    /// <summary>PDF/X-4 permits transparency, so the option leaves its pages as they are.</summary>
    [Fact]
    public void X4_IsNotFlattened()
    {
        var (_, result) = Convert(PdfXConformanceLevel.X4, flatten: true);
        Assert.Empty(result.FlattenedPages);
    }

    /// <summary>
    /// A profile the plates cannot convert through — here, one with no tables — is set aside for the
    /// flattening, and the result says so rather than failing.
    /// </summary>
    [Fact]
    public void AProfileThePlatesCannotUse_IsSetAside_WithAWarning()
    {
        var (_, result) = Convert(PdfXConformanceLevel.X1a, flatten: true);
        Assert.Contains(result.Warnings, w => w.Contains("not a CMYK output profile"));
    }

    /// <summary>
    /// <see cref="PdfXConversionOptions.FlattenKeepOpaqueContentAsVector"/> reaches the flattener: the
    /// opaque black square stays a vector fill, and the image covers only the translucent spot
    /// rectangle — 104 points round it at 36 dpi, 52 pixels, not the page's 100 — and validates.
    /// </summary>
    [Fact]
    public void KeepingOpaqueContentAsVector_FlattensOnlyTheTranslucentSpot()
    {
        var (output, result) = Convert(PdfXConformanceLevel.X1a, flatten: true, keepVector: true);

        Assert.Equal(new[] { 0 }, result.FlattenedPages);
        Assert.Empty(result.Blockers);
        var validation = PdfXValidator.Validate(new MemoryStream(output), PdfXConformanceLevel.X1a);
        Assert.DoesNotContain(validation.Violations, v => v.Code == "X09" || v.Code == "X08");

        using var doc = Core.Document.PdfDocument.Open(new MemoryStream(output));
        var resources = (Core.Objects.PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[Core.Objects.PdfName.Resources]);
        var xobjects = (Core.Objects.PdfDictionary)doc.ResolveObject(resources[Core.Objects.PdfName.XObject]);
        var image = (Core.Objects.PdfStream)doc.ResolveObject(xobjects.First().Value);
        Assert.Equal(52, ((Core.Objects.PdfNumber)doc.ResolveObject(image[Core.Objects.PdfName.Width])).IntValue);
        var ops = Core.Text.ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData());
        Assert.Contains(ops, o => o.Operator == "f");
    }

    private static (byte[] Output, PdfXConversionResult Result) Convert(PdfXConformanceLevel level, bool flatten, bool keepVector = false)
    {
        using var output = new MemoryStream();
        var result = PdfXConverter.Convert(new MemoryStream(Pdf()), output, new PdfXConversionOptions
        {
            Level = level,
            IccProfile = IccProfileFixtures.Cmyk(),
            FlattenTransparency = flatten,
            FlattenDpi = 36,
            FlattenKeepOpaqueContentAsVector = keepVector,
            Trapped = false,                 // X10 asks for /Trapped; the converter writes it when told
        });
        return (output.ToArray(), result);
    }

    private static byte[] Pdf()
    {
        const string content = "0 0 0 1 k 0 0 100 100 re f /GS0 gs /CS0 cs 1 scn 50 25 100 50 re f\n";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R " +
            "/Resources << /ExtGState << /GS0 << /ca 0.5 >> >> " +
            "/ColorSpace << /CS0 [/Separation /Rubine /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>] >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };

        using var ms = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.Latin1.GetBytes(s); ms.Write(b, 0, b.Length); }
        Write("%PDF-1.4\n");
        var offsets = new List<long>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(ms.Length);
            Write($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        long xref = ms.Length;
        var sb = new StringBuilder($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (long o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        Write(sb.ToString());
        return ms.ToArray();
    }
}
