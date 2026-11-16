// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What reading <see cref="PdfFontEmbedder.EmbedFont"/> found: it wrote into encrypted documents,
/// overwrote a page's existing font resource of the same name, and would write a page with no
/// object number as object 0.
/// </summary>
public class FontEmbedderFindingsTests
{
    private static byte[] Font() => MinimalTrueType.BuildWithDistinctWidths("A");

    private static byte[] OnePage(string pageResources = "", string pagesResources = "") => new PdfFixtureBuilder().Header()
        .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
        .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 " + pagesResources + " >>")
        .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " + pageResources + " >>")
        .Finish();

    private static PdfDictionary FontsOfPage0(byte[] pdf)
    {
        var doc = PdfDocument.Open(new MemoryStream(pdf));
        return (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]);
    }

    /// <summary>
    /// An encrypted document is refused before anything is written. It loads no catalog, so the
    /// font objects went into the file in clear text, attached to no page, and the call returned.
    /// </summary>
    [Fact]
    public void AnEncryptedDocumentIsRefused()
    {
        byte[] encrypted = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] >>")
            .Obj(4, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -3904 " +
                    "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
                    "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>")
            .Finish(" /Encrypt 4 0 R /ID [<01020304050607080910111213141516> <01020304050607080910111213141516>]");

        using var output = new MemoryStream();
        Assert.Throws<PdfEncryptedDocumentException>(() =>
            PdfFontEmbedder.EmbedFont(new MemoryStream(encrypted), output, Font(), "F9"));
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// A name a page already uses is refused. Set over it, the page's existing text in that font
    /// re-rendered in this one. A free name is the control, and the page keeps its own font beside it.
    /// </summary>
    [Fact]
    public void ANameThePageAlreadyUsesIsRefused_AFreeOneIsAdded()
    {
        byte[] pdf = OnePage("/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");

        var ex = Assert.Throws<ArgumentException>(() =>
            PdfFontEmbedder.EmbedFont(new MemoryStream(pdf), new MemoryStream(), Font(), "F1"));
        Assert.Equal("resourceName", ex.ParamName);

        using var output = new MemoryStream();
        PdfFontEmbedder.EmbedFont(new MemoryStream(pdf), output, Font(), "F2");
        PdfDictionary fonts = FontsOfPage0(output.ToArray());
        Assert.Equal("Helvetica", ((PdfName)((PdfDictionary)fonts[new PdfName("F1")])[PdfName.BaseFont]).Value);
        Assert.NotNull(fonts[new PdfName("F2")]);
    }

    /// <summary>
    /// A page with no object number — a direct dictionary in <c>/Kids</c> — cannot be superseded,
    /// and is refused rather than written as object 0, the head of the free list.
    /// </summary>
    [Fact]
    public void APageWithNoObjectNumberIsRefused()
    {
        byte[] pdf = new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>] /Count 1 >>")
            .Finish();

        var ex = Assert.Throws<PdfException>(() =>
            PdfFontEmbedder.EmbedFont(new MemoryStream(pdf), new MemoryStream(), Font(), "F9"));
        Assert.Contains("no object number", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A page whose <c>/Resources</c> or <c>/Font</c> the parser cannot read is refused: rebuilt from
    /// nothing, the page was superseded holding only the new font, and its own fonts and images went.
    /// The readable spelling of the same document is the control, its font kept beside the new one.
    /// </summary>
    [Theory]
    [InlineData("/Resources 4 0 R", "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>", "/Resources could not be read")]
    [InlineData("/Resources << /Font 4 0 R >>", "<< /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>", "/Font could not be read")]
    public void AdversarialUnreadableResources_AreRefused_AReadableOneKeepsItsFont(string resources, string object4, string reason)
    {
        byte[] Pdf(string body) => new PdfFixtureBuilder().Header()
            .Obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
            .Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
            .Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] " + resources + " >>")
            .Obj(4, body)
            .Finish();

        using var refused = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() =>
            PdfFontEmbedder.EmbedFont(new MemoryStream(Pdf("]")), refused, Font(), "F9"));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, refused.Length);

        using var output = new MemoryStream();
        PdfFontEmbedder.EmbedFont(new MemoryStream(Pdf(object4)), output, Font(), "F9");
        PdfDictionary fonts = FontsOfPage0(output.ToArray());
        Assert.Equal("Helvetica", ((PdfName)((PdfDictionary)fonts[new PdfName("F1")])[PdfName.BaseFont]).Value);
        Assert.NotNull(fonts[new PdfName("F9")]);
    }

    /// <summary>The same for a name the page inherits from its page-tree node.</summary>
    [Fact]
    public void AnInheritedNameIsRefusedToo()
    {
        byte[] pdf = OnePage(pagesResources: "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>");

        Assert.Throws<ArgumentException>(() =>
            PdfFontEmbedder.EmbedFont(new MemoryStream(pdf), new MemoryStream(), Font(), "F1"));
    }
}
