// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What reading <see cref="PdfPageContentAppender"/> found. Appended content named its resources
/// <c>F1</c>, <c>Im1</c>, <c>GS1</c>, and the merge overwrote a page's own resource of the same name.
/// It also ran in whatever graphics state the page's own content left behind, and the class summary
/// said the opposite. An encrypted document read as having no pages.
/// </summary>
public class PageAppendFindingsTests
{
    // ------------------------------------------------------------------ resource names

    /// <summary>
    /// A page whose own text is in <c>/F1</c> (Times), whose own state is <c>/GS1</c> (opaque) and
    /// whose own image is <c>/Im1</c>. These are the names the builder mints first, and the names
    /// ordinary PDFs use most.
    /// </summary>
    private static byte[] PageWithOwnResources() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> /ExtGState << /GS1 << /Type /ExtGState /ca 1 >> >> " +
                "/XObject << /Im1 6 0 R >> >> >>")
        .StreamObj(4, "<<", "/GS1 gs BT /F1 12 Tf 10 10 Td (hi) Tj ET")
        .Obj(5, "<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman >>")
        .Obj(6, ImageFixtures.OnePixelImageDict(ImageFixtures.Red), ImageFixtures.Red)
        .Finish();

    private static PdfDictionary ResourceCategory(PdfDocument doc, PdfName category)
        => (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[category]);

    /// <summary>
    /// Appended content that brings a font, a graphics state and an image leaves the page's own
    /// three alone. Merged under the same names, they replaced them: the page's text re-rendered in
    /// the stamp's font, at the stamp's opacity, and its image became the stamp's.
    /// </summary>
    [Fact]
    public void AppendedResources_DoNotReplaceThePagesOwn()
    {
        using var output = new MemoryStream();
        PdfPageContentAppender.AppendContent(new MemoryStream(PageWithOwnResources()), output, 0, b =>
        {
            string font = b.AddStandardFont("Helvetica");
            string gs = b.AddExtGState(0.3f, 0.3f);
            string image = b.AddImage(ImageFixtures.Green, 1, 1, null, "DeviceRGB", 8);
            b.SetGraphicsState(gs).BeginText().SetFont(font, 20).SetTextPosition(50, 100)
             .ShowText("DRAFT").EndText().DrawImage(image, 0, 0, 10, 10);
        });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var fonts = ResourceCategory(doc, PdfName.Font);
        var states = ResourceCategory(doc, PdfName.ExtGState);
        var xobjects = ResourceCategory(doc, PdfName.XObject);

        // The page's own three, unchanged...
        Assert.Equal("Times-Roman", BaseFont(doc, fonts[new PdfName("F1")]));
        Assert.Equal(1, ((PdfNumber)((PdfDictionary)doc.ResolveObject(states[new PdfName("GS1")]))[PdfName.ca]).DoubleValue);
        Assert.Equal(6, Assert.IsType<PdfReference>(xobjects[new PdfName("Im1")]).ReferenceObjectNumber);

        // ...and the stamp's three beside them, under other names.
        Assert.Contains(fonts, kv => BaseFont(doc, kv.Value) == "Helvetica");
        Assert.Contains(states, kv => ((PdfDictionary)doc.ResolveObject(kv.Value))[PdfName.ca] is PdfNumber ca
                                      && Math.Abs(ca.DoubleValue - 0.3) < 1e-6);
        Assert.Equal(2, xobjects.Count);
    }

    /// <summary>The builder's own contract: a reserved name is never minted, in any category.</summary>
    [Fact]
    public void TheBuilderNeverMintsAReservedName()
    {
        var builder = new PdfContentStreamBuilder();
        builder.ReserveResourceNames(new[] { "F1", "F2", "Im1", "GS1", "Sh1", "OC0" });

        Assert.Equal("F3", builder.AddStandardFont("Helvetica"));
        Assert.Equal("Im2", builder.AddImage(new byte[] { 0, 0, 0 }, 1, 1, null, "DeviceRGB", 8));
        Assert.Equal("GS2", builder.AddExtGState(0.5f, 0.5f));
        Assert.Equal("Sh2", builder.AddAxialShading(0, 0, 1, 1, new[]
        {
            new PdfContentStreamBuilder.ShadingStop(0, 0, 0, 0),
            new PdfContentStreamBuilder.ShadingStop(1, 1, 1, 1),
        }));
        builder.BeginOptionalContent(new PdfReference(9, 0)).EndMarkedContent();
        Assert.Contains("/OC /OC1 BDC", Encoding.ASCII.GetString(builder.Build()), StringComparison.Ordinal);
    }

    /// <summary>The control: with nothing reserved, the first names are what they always were.</summary>
    [Fact]
    public void WithNothingReserved_TheFirstNamesAreUnchanged()
    {
        var builder = new PdfContentStreamBuilder();

        Assert.Equal("F1", builder.AddStandardFont("Helvetica"));
        Assert.Equal("Im1", builder.AddImage(new byte[] { 0, 0, 0 }, 1, 1, null, "DeviceRGB", 8));
        Assert.Equal("GS1", builder.AddExtGState(0.5f, 0.5f));
        builder.BeginOptionalContent(new PdfReference(9, 0)).EndMarkedContent();
        Assert.Contains("/OC /OC0 BDC", Encoding.ASCII.GetString(builder.Build()), StringComparison.Ordinal);
    }

    private static string BaseFont(PdfDocument doc, PdfObject font)
        => ((doc.ResolveObject(font) as PdfDictionary)?[PdfName.BaseFont] as PdfName)?.Value;

    // ------------------------------------------------------------------ graphics state

    /// <summary>
    /// A page whose content leaves a translation in force: <c>cm</c> with no <c>q</c>/<c>Q</c>
    /// around it, which real producers write. It draws nothing itself.
    /// </summary>
    private static byte[] PageLeavingATranslation() => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>")
        .StreamObj(4, "<<", "1 0 0 1 100 100 cm")
        .Finish();

    /// <summary>
    /// Appended content is drawn in the page's initial graphics state, not in the state the page's
    /// own content leaves behind. A black square appended at (0,0) landed at (100,100), because the
    /// page's unclosed <c>cm</c> still applied. That is a watermark or a Bates number drawn off the
    /// page, from a call that reported success.
    /// </summary>
    [Fact]
    public void AppendedContent_IsDrawnInThePagesInitialState()
    {
        using var output = new MemoryStream();
        PdfPageContentAppender.AppendContent(new MemoryStream(PageLeavingATranslation()), output, 0,
            b => b.SetFillColorGray(0).Rectangle(0, 0, 20, 20).Fill());

        var page = PdfOperations.RenderPageToPixels(new MemoryStream(output.ToArray()), 0, 72);

        Assert.True(IsDark(page, 10, 190), "the square was not drawn at the page origin");
        Assert.False(IsDark(page, 110, 90), "the square was drawn where the page's own cm moved it");
    }

    /// <summary>The control: on a page that leaves no state behind, the square lands at the origin too.</summary>
    [Fact]
    public void AppendedContent_OnABalancedPage_IsDrawnAtTheOrigin()
    {
        byte[] balanced = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>")
            .StreamObj(4, "<<", "q 1 0 0 1 100 100 cm Q")
            .Finish();

        using var output = new MemoryStream();
        PdfPageContentAppender.AppendContent(new MemoryStream(balanced), output, 0,
            b => b.SetFillColorGray(0).Rectangle(0, 0, 20, 20).Fill());

        var page = PdfOperations.RenderPageToPixels(new MemoryStream(output.ToArray()), 0, 72);
        Assert.True(IsDark(page, 10, 190));
        Assert.False(IsDark(page, 110, 90));
    }

    /// <summary>Whether the pixel at (x, y), top-left origin, is dark. Channel-order agnostic.</summary>
    private static bool IsDark(RenderedPage page, int x, int y)
    {
        int i = y * page.Stride + x * 4;
        return page.Pixels[i] + page.Pixels[i + 1] + page.Pixels[i + 2] < 100;
    }

    // ------------------------------------------------------------------ encrypted input

    private static byte[] OnePage(bool encrypted)
    {
        var b = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>");
        if (!encrypted) return b.Finish();
        return b.Obj(4, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
                        "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                        "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>")
                .Finish(" /Encrypt 4 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>]");
    }

    /// <summary>
    /// Appending to an encrypted document is refused as such. It loads no catalog, so
    /// <c>AppendContent</c> said "page index out of range" about a page the file has, and
    /// <c>AppendContentToAllPages</c> found no pages and appended an empty revision.
    /// </summary>
    [Fact]
    public void AppendingToAnEncryptedDocument_IsRefused()
    {
        byte[] encrypted = OnePage(encrypted: true);

        using var one = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfPageContentAppender.AppendContent(new MemoryStream(encrypted), one, 0, b => b.Rectangle(0, 0, 1, 1).Fill()));
        Assert.Equal(0, one.Length);

        using var all = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfPageContentAppender.AppendContentToAllPages(new MemoryStream(encrypted), all, (b, _) => b.Rectangle(0, 0, 1, 1).Fill()));
        Assert.Equal(0, all.Length);
    }

    /// <summary>The control: unencrypted, both append.</summary>
    [Fact]
    public void AppendingToTheSameDocumentUnencrypted_Succeeds()
    {
        byte[] plain = OnePage(encrypted: false);

        using var one = new MemoryStream();
        PdfPageContentAppender.AppendContent(new MemoryStream(plain), one, 0, b => b.Rectangle(0, 0, 1, 1).Fill());
        Assert.True(one.Length > plain.Length);

        using var all = new MemoryStream();
        PdfPageContentAppender.AppendContentToAllPages(new MemoryStream(plain), all, (b, _) => b.Rectangle(0, 0, 1, 1).Fill());
        using var doc = PdfDocument.Open(new MemoryStream(all.ToArray()));
        // A page with no content of its own leaves no state behind, so it gets no q/Q bracket:
        // its /Contents is the appended stream alone.
        Assert.Single(Assert.IsType<PdfArray>(doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents])));
    }

    /// <summary>
    /// One revision writes the <c>q</c> and <c>Q</c> streams once, and every page it touches names
    /// the same two. Minting a pair per page would add two objects for every page of a document.
    /// </summary>
    [Fact]
    public void TheBracketStreamsAreSharedByEveryPageOfOneRevision()
    {
        byte[] twoPages = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R >>")
            .StreamObj(4, "<<", "1 0 0 1 10 10 cm")
            .Obj(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 6 0 R >>")
            .StreamObj(6, "<<", "1 0 0 1 10 10 cm")
            .Finish();

        using var output = new MemoryStream();
        PdfPageContentAppender.AppendContentToAllPages(new MemoryStream(twoPages), output,
            (b, _) => b.Rectangle(0, 0, 1, 1).Fill());

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var first = (PdfArray)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Contents]);
        var second = (PdfArray)doc.ResolveObject(doc.Pages[1].Dictionary[PdfName.Contents]);
        Assert.Equal(4, first.Count);   // q, the page's own, Q, the appended stream
        Assert.Equal(((PdfReference)first[0]).ReferenceObjectNumber, ((PdfReference)second[0]).ReferenceObjectNumber);
        Assert.Equal(((PdfReference)first[2]).ReferenceObjectNumber, ((PdfReference)second[2]).ReferenceObjectNumber);
    }
}
