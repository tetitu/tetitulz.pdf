// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfOcgManager.AssignPageContentToLayer"/> — §8.11.3.2's marked-content route, which
/// is the only way to put ink written directly into a content stream on a layer.
/// </summary>
/// <remarks>
/// <para><b>Why the other route cannot do it.</b> <c>AssignObjectToLayer</c> writes an <c>/OC</c>
/// key on an object, which covers everything a document draws <i>through a reference</i> — an
/// annotation, an image, a form XObject. Ink in a content stream has no object to hang a key on.
/// </para>
///
/// <para><b>The property list has to be indirect, and that is what needed a <c>/Properties</c>
/// entry.</b> §14.6.2 permits an inline <c>BDC</c> property list only when it holds no indirect
/// reference, and an OCG is always one. So the assertions here are about two things existing
/// <i>together</i>: the <c>BDC</c> naming a resource, and a resource of that name resolving to the
/// group. Either alone is a file a viewer ignores.</para>
///
/// <para><b>The original streams must come back byte-identical.</b> The wrap is a stream before and
/// a stream after — §7.8.2 concatenates a <c>/Contents</c> array as if it were one stream — so a
/// signature over the original content still verifies. Re-serialising would have been invisible to
/// every structural assertion and would have cost exactly that.</para>
/// </remarks>
public sealed class OcgMarkedContentTests
{
    /// <summary>The page's content is wrapped, and the group is reachable from the wrap.</summary>
    [Fact]
    public void ThePageContentIsWrappedInASectionNamingTheGroup()
    {
        (byte[] pdf, int ocgNum) = LayeredPage();

        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);
        PdfPage page = doc.Pages[0];

        string content = ContentOf(doc, page);
        Assert.Contains(" BDC", content, StringComparison.Ordinal);
        Assert.Contains("EMC", content, StringComparison.Ordinal);

        // The name in the BDC, resolved through /Properties, must be the group itself. Asserting
        // that a BDC appears — or that /Properties exists — proves neither half of that.
        string propertyName = PropertyNameInBdc(content);
        var properties = doc.ResolveObject(page.Resources?[PdfName.Properties]) as PdfDictionary;

        Assert.True(properties != null, "the page has no /Properties, so the BDC name resolves to nothing");

        var reference = properties![new PdfName(propertyName)] as PdfReference;
        Assert.True(reference != null,
            $"/Properties has no entry named '{propertyName}', which is the name the BDC uses");
        Assert.Equal(ocgNum, reference!.ReferenceObjectNumber);
    }

    /// <summary>The marked content is optional content, not some other kind of tag.</summary>
    /// <remarks>
    /// The operand order is <c>/OC /name BDC</c>, the tag ahead of the property list (§14.6.2,
    /// §8.11.3.2). A section tagged anything else is a marked-content sequence a viewer's layer
    /// panel will never consult, and it would satisfy every "a BDC is present" assertion.
    /// </remarks>
    [Fact]
    public void TheSectionIsTaggedAsOptionalContent()
    {
        (byte[] pdf, _) = LayeredPage();

        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);

        ContentStreamOperation section = ContentStreamParser.Parse(Encoding.ASCII.GetBytes(ContentOf(doc, doc.Pages[0])))
            .First(op => op.Operator == "BDC");
        Assert.Equal("OC", (section.Operands[0] as PdfName)?.Value);
    }

    /// <summary>What a layer is for: the page's ink goes when the layer is off, and comes back when it is on.</summary>
    [Fact]
    public void Turning_the_layer_off_takes_the_pages_content_away()
    {
        (byte[] pdf, _) = LayeredPage();

        Assert.True(Painted(pdf), "the layer is on: the rectangle shows");

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        Assert.Equal(1, PdfOcgManager.SetLayerVisibility(input, output, "Wrapped", visible: false));
        Assert.False(Painted(output.ToArray()), "the layer is off, and its content was still drawn");
    }

    private static bool Painted(byte[] pdf)
    {
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        int at = (page.Height - 200) * page.Stride + 200 * (page.Stride / page.Width);
        return page.Pixels[at] < 128 || page.Pixels[at + 1] < 128 || page.Pixels[at + 2] < 128;
    }

    /// <summary>The original content stream is unchanged, byte for byte.</summary>
    /// <remarks>
    /// <b>This is what makes the operation signature-safe</b>, and it is the reason the wrap is two
    /// new streams rather than a rewrite: a re-serialised stream renders identically and breaks
    /// every digest over it, which no assertion about the page's appearance could distinguish.
    /// </remarks>
    [Fact]
    public void TheOriginalContentStreamIsNotRewritten()
    {
        byte[] before;
        using (var ms = new MemoryStream(OnePagePdf()))
        using (PdfDocument doc = PdfDocument.Open(ms))
            before = StreamBytes(doc, doc.Pages[0]).Single();

        (byte[] pdf, _) = LayeredPage();

        using var after = new MemoryStream(pdf);
        using PdfDocument updated = PdfDocument.Open(after);

        byte[][] streams = StreamBytes(updated, updated.Pages[0]).ToArray();

        Assert.Equal(3, streams.Length);          // BDC, the original, EMC
        Assert.Equal(before, streams[1]);
    }

    /// <summary>The wrap goes outside the existing content, not inside it.</summary>
    /// <remarks>
    /// Order is the whole meaning here: a <c>BDC</c> after the ink it is meant to enclose leaves
    /// that ink outside the layer and opens a section over nothing. The concatenated stream is the
    /// only place that shows it, because each of the three streams is individually well-formed.
    /// </remarks>
    [Fact]
    public void TheSectionEnclosesTheContentRatherThanFollowingIt()
    {
        (byte[] pdf, _) = LayeredPage();

        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);

        string content = ContentOf(doc, doc.Pages[0]);

        int bdc = content.IndexOf("BDC", StringComparison.Ordinal);
        int ink = content.IndexOf("re", StringComparison.Ordinal);
        int emc = content.IndexOf("EMC", StringComparison.Ordinal);

        Assert.True(bdc >= 0 && ink > bdc && emc > ink,
            "the section does not enclose the page's ink: " + content);
    }

    /// <summary>Wrapping twice nests two sections and keeps both property entries.</summary>
    /// <remarks>
    /// <b>The composition case, and the one a single-use fixture cannot reach.</b> Two layers over
    /// one page need two <c>/Properties</c> entries; a name reused for the second would move the
    /// first section onto the second group — a silent change to a part of the page nobody asked
    /// about. Both entries surviving is the evidence.
    /// </remarks>
    [Fact]
    public void WrappingForTwoLayersKeepsBothPropertyEntries()
    {
        byte[] pdf = OnePagePdf();

        int first, second;
        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            first = PdfOcgManager.CreateLayer(input, output, "First");
            pdf = output.ToArray();
        }
        using (var input = new MemoryStream(pdf))
        using (var output = new MemoryStream())
        {
            second = PdfOcgManager.CreateLayer(input, output, "Second");
            pdf = output.ToArray();
        }

        pdf = Wrap(pdf, first);
        pdf = Wrap(pdf, second);

        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);
        PdfPage page = doc.Pages[0];

        var properties = doc.ResolveObject(page.Resources?[PdfName.Properties]) as PdfDictionary;
        Assert.True(properties != null, "the page lost its /Properties on the second wrap");

        int[] referenced = properties!.Keys
            .Select(k => (properties[k] as PdfReference)?.ReferenceObjectNumber ?? 0)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { first, second }.OrderBy(n => n).ToArray(), referenced);

        string content = ContentOf(doc, page);
        Assert.Equal(2, Occurrences(content, "BDC"));
        Assert.Equal(2, Occurrences(content, "EMC"));
    }

    /// <summary>A page index that names no page is refused and the document passes through.</summary>
    /// <remarks>
    /// Named with <c>Malformed</c> so the guard reads the non-throw correctly: the behaviour under
    /// test is the refusal, and the assertion is that the output is the input.
    /// </remarks>
    [Fact]
    public void AMalformedPageIndexIsRefusedAndTheDocumentIsCopiedThrough()
    {
        byte[] pdf = OnePagePdf();

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();

        Assert.False(PdfOcgManager.AssignPageContentToLayer(input, output, 5, 1));
        Assert.Equal(pdf, output.ToArray());
    }

    // ==================================================================== helpers

    private static (byte[] Pdf, int OcgNumber) LayeredPage()
        => OcgMarkedContentFixture.LayeredPage();

    private static byte[] Wrap(byte[] pdf, int ocgNumber)
        => OcgMarkedContentFixture.Wrap(pdf, ocgNumber);

    private static byte[] OnePagePdf() => OcgMarkedContentFixture.OnePagePdf();


    /// <summary>The page's content streams concatenated, as §7.8.2 says a reader sees them.</summary>
    private static string ContentOf(PdfDocument doc, PdfPage page)
        => string.Join("\n", StreamBytes(doc, page).Select(b => Encoding.ASCII.GetString(b)));

    private static IEnumerable<byte[]> StreamBytes(PdfDocument doc, PdfPage page)
    {
        PdfObject raw = doc.ResolveObject(page.Dictionary[PdfName.Contents]);

        if (raw is PdfStream single) return new[] { single.GetDecodedData() };
        if (raw is PdfArray array)
            return array.Select(e => doc.ResolveObject(e))
                        .OfType<PdfStream>()
                        .Select(s => s.GetDecodedData())
                        .ToArray();

        return Array.Empty<byte[]>();
    }

    /// <summary>The resource name a <c>/OC /name BDC</c> uses.</summary>
    private static string PropertyNameInBdc(string content)
    {
        ContentStreamOperation section = ContentStreamParser.Parse(Encoding.ASCII.GetBytes(content))
            .FirstOrDefault(op => op.Operator == "BDC");
        Assert.True(section != null, "no BDC in the page: " + content);
        Assert.Equal("OC", (section.Operands[0] as PdfName)?.Value);

        PdfName name = section.Operands[1] as PdfName;
        Assert.True(name != null, "the BDC names no property list: " + content);
        return name.Value;
    }

    private static int Occurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
