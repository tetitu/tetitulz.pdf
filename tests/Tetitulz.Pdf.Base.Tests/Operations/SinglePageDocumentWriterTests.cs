// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SinglePageDocumentWriter"/>: the one-page skeleton the SVG converter and the barcode
/// writer emit, read as raw bytes as well as parsed.
/// </summary>
/// <remarks>
/// The page object closed one dictionary more than it opened — four <c>&lt;&lt;</c> and five
/// <c>&gt;&gt;</c> — in every file both generators wrote. This library's parser tolerated the stray
/// delimiter, so no test that parsed the output could see it; only the raw bytes can.
/// </remarks>
public class SinglePageDocumentWriterTests
{
    /// <summary>Every object's dictionaries open and close in pairs, with and without a graphics state.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("/GS0 << /ca 0.5 /CA 1 >>")]
    public void EveryObjectsDictionariesBalance(string states)
    {
        string pdf = Encoding.Latin1.GetString(SinglePageDocumentWriter.Build("0 0 m", "[0 0 10 10]", "/WinAnsiEncoding", states));

        var objects = Regex.Matches(pdf, @"\d+ 0 obj(.*?)endobj", RegexOptions.Singleline);
        Assert.Equal(4, objects.Count);
        foreach (Match o in objects)
        {
            string body = o.Groups[1].Value;
            int stream = body.IndexOf("stream", System.StringComparison.Ordinal);
            if (stream >= 0) body = body.Substring(0, stream);   // a content stream's bytes are not syntax
            Assert.Equal(Regex.Matches(body, "<<").Count, Regex.Matches(body, ">>").Count);
        }
    }

    /// <summary>
    /// Annotations are objects of their own, numbered from 5, which the page's <c>/Annots</c> refers
    /// to; the file's cross-reference table finds them, and every object's dictionaries balance.
    /// </summary>
    /// <summary>
    /// The header is followed by the binary comment PdfWriter writes — <c>%</c> and four bytes above
    /// 127, which ISO 19005 6.1.2 requires of a PDF/A file — and every cross-reference offset still
    /// points at its object's first byte.
    /// </summary>
    [Fact]
    public void TheHeaderCarriesTheBinaryCommentAndTheOffsetsFollowIt()
    {
        byte[] bytes = SinglePageDocumentWriter.Build("0 0 m", "[0 0 10 10]", "/WinAnsiEncoding");
        string pdf = Encoding.Latin1.GetString(bytes);

        Assert.StartsWith("%PDF-1.4\n%", pdf);
        Assert.All(bytes.Skip(10).Take(4), b => Assert.True(b > 127, $"byte {b} is not above 127"));
        Assert.Equal((byte)'\n', bytes[14]);

        int xrefAt = int.Parse(Regex.Match(pdf, @"startxref\s+(\d+)").Groups[1].Value);
        Assert.StartsWith("xref", pdf.Substring(xrefAt));
        var entries = Regex.Matches(pdf.Substring(xrefAt), @"(\d{10}) 00000 n");
        Assert.Equal(4, entries.Count);
        for (int i = 0; i < entries.Count; i++)
            Assert.StartsWith($"{i + 1} 0 obj", pdf.Substring(int.Parse(entries[i].Groups[1].Value)));
    }

    [Fact]
    public void AnnotationsAreIndirectObjectsThePageRefersTo()
    {
        byte[] bytes = SinglePageDocumentWriter.Build("", "[0 0 10 10]", "/WinAnsiEncoding", null,
            new[] { "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] >>", "<< /Type /Annot /Subtype /Link /Rect [2 2 3 3] >>" });
        using var doc = PdfDocument.Open(new MemoryStream(bytes));

        var annots = Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Annots]));
        Assert.Equal(new[] { 5, 6 }, annots.Select(a => Assert.IsType<PdfReference>(a).ReferenceObjectNumber));
        var second = Assert.IsType<PdfDictionary>(doc.ResolveObject(annots[1]));
        Assert.Equal(2.0, Assert.IsType<PdfNumber>(Assert.IsType<PdfArray>(second[PdfName.Rect])[0]).DoubleValue);

        string pdf = Encoding.Latin1.GetString(bytes);
        foreach (Match o in Regex.Matches(pdf, @"\d+ 0 obj(.*?)endobj", RegexOptions.Singleline))
        {
            string body = o.Groups[1].Value;
            int stream = body.IndexOf("stream", System.StringComparison.Ordinal);
            if (stream >= 0) body = body.Substring(0, stream);
            Assert.Equal(Regex.Matches(body, "<<").Count, Regex.Matches(body, ">>").Count);
        }
        Assert.Contains("/Size 7", pdf, System.StringComparison.Ordinal);
        // The raw table, which a lenient reader rebuilds when it is short: seven entries, the free
        // head and six objects.
        Assert.Equal(7, Regex.Matches(pdf, @"\d{10} \d{5} [fn]").Count);
    }

    /// <summary>
    /// A stream object gets its <c>/Length</c> from the writer, and the page's <c>/XObject</c> resource
    /// names it; the bytes come back as written.
    /// </summary>
    [Fact]
    public void AStreamObjectIsAnXObjectThePageNames()
    {
        byte[] data = { 1, 2, 3, 0x0A, 0xFF };
        byte[] bytes = SinglePageDocumentWriter.Build("", "[0 0 10 10]", "/WinAnsiEncoding", null, "/Im0 5 0 R",
            new[] { SinglePageDocumentWriter.ExtraObject.StreamObject("/Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8", data) });
        using var doc = PdfDocument.Open(new MemoryStream(bytes));

        var xobjects = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.XObject]));
        var image = Assert.IsType<PdfStream>(doc.ResolveObject(xobjects[new PdfName("Im0")]));
        Assert.Equal(data, image.RawData);
        Assert.Contains("/Length 5 >>", Encoding.Latin1.GetString(bytes), System.StringComparison.Ordinal);
        Assert.Equal(6, Regex.Matches(Encoding.Latin1.GetString(bytes), @"\d{10} \d{5} [fn]").Count);
        Assert.Null(doc.Pages[0].Dictionary[PdfName.Annots]);   // a stream is not an annotation
    }

    /// <summary>
    /// <c>Resources</c> is the page's resource dictionary, byte for byte — so an object written from
    /// it, which a Form XObject names, holds what the page holds — and a plain object is written as
    /// given, with no stream and no <c>/Annots</c> entry.
    /// </summary>
    [Fact]
    public void ResourcesAreThePagesOwnAndAPlainObjectIsWrittenAsGiven()
    {
        const string states = "/GS0 << /ca 0.5 /CA 0.5 >>", xobjects = "/Fm0 6 0 R", fonts = "/F2 << /Type /Font /Subtype /Type1 /BaseFont /Courier >>";
        string resources = SinglePageDocumentWriter.Resources("/WinAnsiEncoding", states, xobjects, fonts);
        byte[] bytes = SinglePageDocumentWriter.Build("", "[0 0 10 10]", "/WinAnsiEncoding", states, xobjects,
            new[] { SinglePageDocumentWriter.ExtraObject.Plain(resources) }, fonts);
        string pdf = Encoding.Latin1.GetString(bytes);

        string page = Regex.Match(pdf, @"3 0 obj(.*?)endobj", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("/Resources " + resources + "\n", page, System.StringComparison.Ordinal);
        Assert.Equal("\n" + resources + "\n", Regex.Match(pdf, @"5 0 obj(.*?)endobj", RegexOptions.Singleline).Groups[1].Value);
        Assert.Equal(Regex.Matches(resources, "<<").Count, Regex.Matches(resources, ">>").Count);
        using var doc = PdfDocument.Open(new MemoryStream(bytes));
        Assert.Null(doc.Pages[0].Dictionary[PdfName.Annots]);
        var fontDict = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]));
        Assert.Equal(2, fontDict.Keys.Count);
    }

    /// <summary>The page's patterns are its <c>/Pattern</c> resource; without them it has none.</summary>
    [Fact]
    public void PatternsAreThePagesPatternResource()
    {
        byte[] bytes = SinglePageDocumentWriter.Build("", "[0 0 10 10]", "/WinAnsiEncoding", null, null,
            new[] { SinglePageDocumentWriter.ExtraObject.Plain("<< /Type /Pattern /PatternType 2 >>") }, null, "/P0 5 0 R");
        using var with = PdfDocument.Open(new MemoryStream(bytes));
        using var without = PdfDocument.Open(new MemoryStream(SinglePageDocumentWriter.Build("", "[0 0 10 10]", "/WinAnsiEncoding")));

        var patterns = Assert.IsType<PdfDictionary>(with.ResolveObject(with.Pages[0].Resources[new PdfName("Pattern")]));
        Assert.Equal(5, Assert.IsType<PdfReference>(patterns[new PdfName("P0")]).ReferenceObjectNumber);
        Assert.Null(without.Pages[0].Resources[new PdfName("Pattern")]);
    }

    /// <summary>
    /// Other fonts are entries of the page's <c>/Font</c> beside Helvetica's <c>/F1</c>, and the
    /// page's dictionaries still balance in the raw bytes.
    /// </summary>
    [Fact]
    public void OtherFontsAreEntriesBesideF1()
    {
        byte[] bytes = SinglePageDocumentWriter.Build("", "[0 0 10 10]", "/WinAnsiEncoding", null, null, null,
            "/F2 << /Type /Font /Subtype /Type1 /BaseFont /Courier >>");
        using var doc = PdfDocument.Open(new MemoryStream(bytes));

        var fonts = Assert.IsType<PdfDictionary>(doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]));
        Assert.Equal("Helvetica", ((PdfName)((PdfDictionary)fonts[new PdfName("F1")])[PdfName.BaseFont]).Value);
        Assert.Equal("Courier", ((PdfName)((PdfDictionary)fonts[new PdfName("F2")])[PdfName.BaseFont]).Value);
        string page = Regex.Match(Encoding.Latin1.GetString(bytes), @"3 0 obj(.*?)endobj", RegexOptions.Singleline).Groups[1].Value;
        Assert.Equal(Regex.Matches(page, "<<").Count, Regex.Matches(page, ">>").Count);
    }

    /// <summary>
    /// The graphics states are entries of the page's <c>/ExtGState</c> resource, beside the font;
    /// without them the resources hold the font alone.
    /// </summary>
    [Fact]
    public void TheGraphicsStatesAreThePagesExtGStateResource()
    {
        using var with = PdfDocument.Open(new MemoryStream(
            SinglePageDocumentWriter.Build("", "[0 0 10 10]", "/WinAnsiEncoding", "/GS0 << /ca 0.5 /CA 1 >>")));
        using var without = PdfDocument.Open(new MemoryStream(
            SinglePageDocumentWriter.Build("", "[0 0 10 10]", "/WinAnsiEncoding")));

        var states = Assert.IsType<PdfDictionary>(with.ResolveObject(with.Pages[0].Resources[PdfName.ExtGState]));
        var gs = Assert.IsType<PdfDictionary>(with.ResolveObject(states[new PdfName("GS0")]));
        Assert.Equal(0.5, Assert.IsType<PdfNumber>(gs[PdfName.ca]).DoubleValue, 6);
        Assert.NotNull(with.Pages[0].Resources[PdfName.Font]);

        Assert.Null(without.Pages[0].Resources[PdfName.ExtGState]);
        Assert.NotNull(without.Pages[0].Resources[PdfName.Font]);
    }
}
