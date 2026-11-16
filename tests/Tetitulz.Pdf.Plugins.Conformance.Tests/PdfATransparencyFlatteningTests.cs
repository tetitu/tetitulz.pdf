// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfAConversionOptions.FlattenTransparency"/>: PDF/A-1 conversion flattens the
/// transparency ISO 19005-1 §6.4 forbids instead of stopping at it.
/// </summary>
/// <remarks>
/// The fixture is a page drawn at half opacity (<c>/ca 0.5</c>, A14) with text in Helvetica, which it
/// does not embed (A06). Flattened, both go: the page's resources are replaced by its image, and the
/// text comes back as an invisible layer in an embedded glyphless font — so the converted document
/// validates clean, where unflattened it carries both blockers.
/// </remarks>
public class PdfATransparencyFlatteningTests
{
    [Fact]
    public void ATransparentPage_IsFlattened_AndTheResultValidates()
    {
        var (output, result) = Convert(PdfAConformancePart.Part1, flatten: true);

        Assert.Equal(new[] { 0 }, result.FlattenedPages);
        Assert.Empty(result.Blockers);
        Assert.True(result.IsConformant);
        Assert.True(result.DeclaredPdfAIdentifier);

        var validation = PdfAValidator.Validate(new MemoryStream(output), PdfAConformancePart.Part1);
        Assert.Empty(validation.Violations);

        // FlattenDpi reached the flattener: the 200-point page at 72 dpi is a 200-pixel image.
        using var doc = PdfDocument.Open(new MemoryStream(output));
        var resources = (Core.Objects.PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[Core.Objects.PdfName.Resources]);
        var xobjects = (Core.Objects.PdfDictionary)doc.ResolveObject(resources[Core.Objects.PdfName.XObject]);
        var image = (Core.Objects.PdfStream)doc.ResolveObject(xobjects.First().Value);
        Assert.Equal(200, ((Core.Objects.PdfNumber)doc.ResolveObject(image[Core.Objects.PdfName.Width])).IntValue);
    }

    /// <summary>The control: without the option, the same page carries both blockers and is not flattened.</summary>
    [Fact]
    public void WithoutTheOption_TheTransparencyIsABlocker()
    {
        var (_, result) = Convert(PdfAConformancePart.Part1, flatten: false);

        Assert.Empty(result.FlattenedPages);
        Assert.Contains(result.Blockers, b => b.StartsWith("A14"));
        Assert.Contains(result.Blockers, b => b.StartsWith("A06"));
        Assert.False(result.IsConformant);
    }

    /// <summary>A flattened page's text still extracts from the converted document.</summary>
    [Fact]
    public void AFlattenedPagesText_StillExtracts()
    {
        var (output, _) = Convert(PdfAConformancePart.Part1, flatten: true);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        Assert.Contains("Archived invoice", PdfTextExtractor.ExtractText(doc.Pages[0]));
    }

    /// <summary>PDF/A-2 permits transparency, so the option leaves its pages as they are.</summary>
    [Fact]
    public void PartTwo_IsNotFlattened()
    {
        var (_, result) = Convert(PdfAConformancePart.Part2, flatten: true);
        Assert.Empty(result.FlattenedPages);
    }

    /// <summary>
    /// <see cref="PdfAConversionOptions.FlattenKeepOpaqueContentAsVector"/> reaches the flattener: A14
    /// goes, and the image covers only what was drawn at half opacity — the rectangle and the text
    /// after it — not the 200-point page, the opaque square's left edge staying vector. The text is
    /// still in its unembedded font (invisible now), so A06 stays: the page was not replaced, and its
    /// fonts are the converter's other business.
    /// </summary>
    [Fact]
    public void KeepingOpaqueContentAsVector_FlattensOnlyTheTranslucentRectangle()
    {
        var (output, result) = Convert(PdfAConformancePart.Part1, flatten: true, keepVector: true);

        Assert.Equal(new[] { 0 }, result.FlattenedPages);
        Assert.DoesNotContain(result.Blockers, b => b.StartsWith("A14"));

        using var doc = PdfDocument.Open(new MemoryStream(output));
        // The text the region covered is in the image now, and kept for extraction in mode 3: the
        // unembedded Helvetica no longer renders anything, so A06 (fonts used for rendering) is
        // silent on it — the page content's own mode 3 is the evidence.
        Assert.DoesNotContain(result.Blockers, b => b.StartsWith("A06"));
        Assert.Contains("3 Tr", Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
        var resources = (Core.Objects.PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[Core.Objects.PdfName.Resources]);
        var xobjects = (Core.Objects.PdfDictionary)doc.ResolveObject(resources[Core.Objects.PdfName.XObject]);
        var image = (Core.Objects.PdfStream)doc.ResolveObject(xobjects.First().Value);
        Assert.InRange(((Core.Objects.PdfNumber)doc.ResolveObject(image[Core.Objects.PdfName.Width])).IntValue, 102, 199);
        Assert.Contains("Archived invoice", PdfTextExtractor.ExtractText(doc.Pages[0]));
    }

    private static (byte[] Output, PdfAConversionResult Result) Convert(PdfAConformancePart part, bool flatten, bool keepVector = false)
    {
        var options = new PdfAConversionOptions
        {
            IccProfile = IccProfileFixtures.Rgb(),
            FlattenTransparency = flatten,
            FlattenDpi = 72,
            FlattenKeepOpaqueContentAsVector = keepVector,
        };
        using var output = new MemoryStream();
        var input = new MemoryStream(Pdf());
        PdfAConversionResult result = part switch
        {
            PdfAConformancePart.Part2 => PdfAConverter.ConvertToPdfA2b(input, output, options),
            _ => PdfAConverter.ConvertToPdfA1b(input, output, options),
        };
        return (output.ToArray(), result);
    }

    private static byte[] Pdf()
    {
        const string content = "0 0 1 rg 0 0 100 100 re f /GS0 gs 1 0 0 rg 50 25 100 50 re f\n" +
                               "BT /F1 12 Tf 10 80 Td (Archived invoice) Tj ET\n";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R " +
            "/Resources << /ExtGState << /GS0 << /ca 0.5 >> >> /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>",
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
