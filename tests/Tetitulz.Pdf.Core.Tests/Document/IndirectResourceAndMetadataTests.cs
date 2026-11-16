// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// <see cref="PdfResources"/>' four category getters and <see cref="PdfCatalog.GetMetadataStream"/>
/// follow an <b>indirect</b> entry (§7.3.10) rather than answering "absent".
/// </summary>
/// <remarks>
/// <para><b>Public API with no caller inside this repository</b> — nothing constructs a
/// <see cref="PdfResources"/> and nothing calls <c>GetMetadataStream</c> — so no other test could see
/// either one stop resolving. A sweep of <c>Core/Document</c> confirmed it: removing the resolve from
/// <c>Fonts</c>, <c>XObjects</c> or <c>GetMetadataStream</c> survived the Core, Base and Accessibility
/// suites alike. An external consumer is exactly who would be told "this page has no fonts" by a
/// file that keeps its <c>/Font</c> dictionary in its own object, which producers commonly do.</para>
///
/// <para>Each test pairs the indirect spelling with the direct one, so a getter that returned
/// nothing at all could not pass.</para>
/// </remarks>
public sealed class IndirectResourceAndMetadataTests
{
    private static PdfDocument Open(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append($"{i + 1} 0 obj\n{bodies[i]}\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append($"xref\n0 {bodies.Count + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {bodies.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
    }

    /// <summary>A resources dictionary whose four categories are each an indirect object.</summary>
    private static PdfResources IndirectResources(PdfDocument doc)
        => new(doc.GetObject(4) as PdfDictionary, doc);

    private static PdfDocument ResourceDocument() => Open(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources 4 0 R >>",
        // Object 4: every category indirect. Object 9: every category direct, the control.
        "<< /Font 5 0 R /XObject 6 0 R /ExtGState 7 0 R /ColorSpace 8 0 R >>",
        "<< /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>",
        "<< /Im0 10 0 R >>",
        "<< /GS0 << /Type /ExtGState /CA 0.5 >> >>",
        "<< /CS0 /DeviceRGB >>",
        "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> " +
        "/XObject << /Im0 10 0 R >> /ExtGState << /GS0 << /CA 0.5 >> >> /ColorSpace << /CS0 /DeviceRGB >> >>",
        "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray " +
        "/BitsPerComponent 8 /Length 1 >>\nstream\n\u0000\nendstream",
    });

    private static PdfResources DirectResources(PdfDocument doc)
        => new(doc.GetObject(9) as PdfDictionary, doc);

    /// <summary>The named entry of a category, resolved, as a dictionary (a stream is one too).</summary>
    private static PdfDictionary Entry(PdfDocument doc, PdfDictionary category, string key)
        => doc.ResolveObject(category?[new PdfName(key)]) as PdfDictionary;

    [Fact]
    public void AnIndirectFontDictionaryIsFollowed()
    {
        using PdfDocument doc = ResourceDocument();

        Assert.Equal("Helvetica", Entry(doc, IndirectResources(doc).Fonts, "F1")?.GetName(PdfName.BaseFont)?.Value);
        Assert.Equal("Helvetica", Entry(doc, DirectResources(doc).Fonts, "F1")?.GetName(PdfName.BaseFont)?.Value);
    }

    [Fact]
    public void AnIndirectXObjectDictionaryIsFollowed()
    {
        using PdfDocument doc = ResourceDocument();

        Assert.Equal("Image", Entry(doc, IndirectResources(doc).XObjects, "Im0")?.GetName(PdfName.Subtype)?.Value);
        Assert.Equal("Image", Entry(doc, DirectResources(doc).XObjects, "Im0")?.GetName(PdfName.Subtype)?.Value);
    }

    [Fact]
    public void AnIndirectExtGStateDictionaryIsFollowed()
    {
        using PdfDocument doc = ResourceDocument();

        Assert.Equal(0.5, (Entry(doc, IndirectResources(doc).ExtGState, "GS0")?[PdfName.CA] as PdfNumber)?.DoubleValue);
        Assert.Equal(0.5, (Entry(doc, DirectResources(doc).ExtGState, "GS0")?[PdfName.CA] as PdfNumber)?.DoubleValue);
    }

    [Fact]
    public void AnIndirectColorSpaceDictionaryIsFollowed()
    {
        using PdfDocument doc = ResourceDocument();

        Assert.Equal("DeviceRGB", (IndirectResources(doc).ColorSpaces?[new PdfName("CS0")] as PdfName)?.Value);
        Assert.Equal("DeviceRGB", (DirectResources(doc).ColorSpaces?[new PdfName("CS0")] as PdfName)?.Value);
    }

    /// <summary>
    /// The catalog's <c>/Metadata</c> is always an indirect stream (§7.3.8 makes every stream
    /// indirect), so the resolve is not an edge case — without it the method answers null for
    /// every file that has metadata at all.
    /// </summary>
    [Fact]
    public void TheCatalogsMetadataStreamIsFollowed()
    {
        const string packet = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>";
        using PdfDocument doc = Open(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            $"<< /Type /Metadata /Subtype /XML /Length {packet.Length} >>\nstream\n{packet}\nendstream",
        });

        PdfStream metadata = doc.Catalog.GetMetadataStream();

        Assert.NotNull(metadata);
        Assert.Equal(packet, Encoding.ASCII.GetString(metadata.GetDecodedData()));
    }
}
