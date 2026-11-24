// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfOcgManager.CreateLayer"/> and <see cref="PdfOcgManager.AssignObjectToLayer"/> —
/// the authoring half of optional content.
/// </summary>
/// <remarks>
/// <para><b>The round trip is the point.</b> Asserting that <c>CreateLayer</c> wrote a
/// <c>/Type /OCG</c> object would restate the writer. What a caller needs is that the layer is
/// <i>discoverable</i>, so these read it back through <c>ListLayers</c> — a reader that knows
/// nothing about how the group got there. A group written into the
/// file but not into <c>/OCProperties /OCGs</c> is invisible to a viewer's layer panel, and it is
/// exactly what <c>ListLayers</c> would fail to find.</para>
///
/// <para><b>Visibility is asserted separately from existence</b>, because a group lands in
/// <c>/ON</c> or <c>/OFF</c> and a writer that put every group in one list would still produce a
/// discoverable layer.</para>
/// </remarks>
public sealed class OcgAuthoringTests
{
    [Fact]
    public void ACreatedLayerIsDiscoverableByTheReader()
    {
        using var output = new MemoryStream();
        int ocgNum;
        using (var input = new MemoryStream(OnePagePdf()))
            ocgNum = PdfOcgManager.CreateLayer(input, output, "Watermark");

        Assert.True(ocgNum > 0, "no layer object number was returned");

        output.Position = 0;
        var layers = PdfOcgManager.ListLayers(output);

        OcgInfo created = Assert.Single(layers);
        Assert.Equal("Watermark", created.Name);
        Assert.Equal(ocgNum, created.ObjectNumber);
    }

    /// <summary>
    /// The requested default visibility reaches the configuration, in both directions.
    /// </summary>
    /// <remarks>
    /// Both values are exercised because a writer that always wrote <c>/ON</c> — or that dropped the
    /// group into neither list and let <c>/BaseState</c> decide — would satisfy the visible case on
    /// its own.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACreatedLayerCarriesTheRequestedDefaultVisibility(bool visible)
    {
        using var output = new MemoryStream();
        using (var input = new MemoryStream(OnePagePdf()))
            PdfOcgManager.CreateLayer(input, output, "Layer", visible);

        output.Position = 0;
        OcgInfo created = Assert.Single(PdfOcgManager.ListLayers(output));

        Assert.Equal(visible, created.DefaultVisible);
    }

    /// <summary>
    /// A second layer joins the first rather than replacing it.
    /// </summary>
    /// <remarks>
    /// The inventory and both visibility lists are rewritten wholesale on every call, so "append"
    /// versus "replace" is a real choice the code makes. This is the assertion that catches the
    /// replacing version — which would look correct for any document with exactly one layer, i.e.
    /// every other test here.
    /// </remarks>
    [Fact]
    public void CreatingASecondLayerKeepsTheFirst()
    {
        using var first = new MemoryStream();
        using (var input = new MemoryStream(OnePagePdf()))
            PdfOcgManager.CreateLayer(input, first, "Alpha");

        using var second = new MemoryStream();
        first.Position = 0;
        PdfOcgManager.CreateLayer(first, second, "Beta", visible: false);

        second.Position = 0;
        var names = PdfOcgManager.ListLayers(second).Select(l => l.Name).ToList();

        Assert.Contains("Alpha", names);
        Assert.Contains("Beta", names);
    }

    /// <summary>
    /// Assigning an image XObject to a layer sets its <c>/OC</c> and <b>keeps its pixels</b>.
    /// </summary>
    /// <remarks>
    /// The stream-data assertion is not incidental. <c>PdfStream</c> derives from
    /// <c>PdfDictionary</c>, so a clone written against the base type compiles and silently produces
    /// a dictionary with the stream's keys, a lying <c>/Length</c> and no data — the image then
    /// vanishes from the output with no exception. This library has shipped that bug before;
    /// <c>PdfDictionary.CloneShallow</c> now guards it, and this is the assertion that would notice
    /// if a future edit here stopped going through it.
    /// </remarks>
    [Fact]
    public void AssigningAStreamObjectToALayerPreservesItsData()
    {
        byte[] pdf = OnePagePdf();

        using var withLayer = new MemoryStream();
        int ocgNum;
        using (var input = new MemoryStream(pdf))
            ocgNum = PdfOcgManager.CreateLayer(input, withLayer, "Images");

        using var assigned = new MemoryStream();
        withLayer.Position = 0;
        bool written = PdfOcgManager.AssignObjectToLayer(withLayer, assigned, ImageObjectNumber, ocgNum);

        Assert.True(written, "no revision was appended");

        assigned.Position = 0;
        using var doc = PdfDocument.Open(assigned);
        var image = doc.GetObject(ImageObjectNumber) as PdfStream;

        Assert.NotNull(image);
        Assert.Equal(ocgNum, (image![PdfName.OC] as PdfReference)?.ReferenceObjectNumber);
        Assert.Equal(ImagePayload.Length, image.RawData?.Length ?? 0);
    }

    /// <summary>
    /// An object number that names nothing usable is refused, and the output is the input.
    /// </summary>
    /// <remarks>
    /// An <c>/OC</c> on something that is not a dictionary or stream is not a construct the
    /// specification defines, and a viewer would ignore it — so writing one would produce a file
    /// that looks edited and behaves as though it were not. Returning <c>false</c> with the bytes
    /// passed through is the honest answer, and it is the one a caller can act on.
    /// </remarks>
    [Fact]
    public void AssigningAnUnknownObjectIsRefusedAndPassesTheBytesThrough()
    {
        byte[] pdf = OnePagePdf();

        using var output = new MemoryStream();
        using (var input = new MemoryStream(pdf))
            Assert.False(PdfOcgManager.AssignObjectToLayer(input, output, 9999, 1));

        Assert.Equal(pdf, output.ToArray());
    }

    // ------------------------------------------------------------------

    private const int ImageObjectNumber = 5;
    private static readonly byte[] ImagePayload = { 0xFF, 0x00, 0x7F, 0x10 };

    /// <summary>One page with a tiny image XObject, so there is a stream to assign to a layer.</summary>
    private static byte[] OnePagePdf()
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.5\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
            + " /Resources << /XObject << /Im1 5 0 R >> >> >>\nendobj\n");

        const string content = "q 100 0 0 100 20 20 cm /Im1 Do Q\n";
        Obj(string.Format(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}endstream\nendobj\n",
            content.Length, content));

        var payload = new StringBuilder();
        foreach (byte b in ImagePayload) payload.Append((char)b);
        Obj(string.Format(CultureInfo.InvariantCulture,
            "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 2"
            + " /ColorSpace /DeviceGray /BitsPerComponent 8 /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            ImagePayload.Length, payload));

        int xref = sb.Length;
        int size = offsets.Count + 1;
        sb.Append("xref\n0 " + size.ToString(CultureInfo.InvariantCulture) + "\n0000000000 65535 f \n");
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size " + size.ToString(CultureInfo.InvariantCulture)
                  + " /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
