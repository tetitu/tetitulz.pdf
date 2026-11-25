// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// The boundaries of <see cref="PdfStructureTreeWriter"/>: which pages get a <c>/StructParents</c>
/// key, what a top-level element names as its
/// parent, and an MCID that falls outside a page's marked content.
/// </summary>
public class PdfStructureTreeWriterBoundaryTests
{
    /// <summary>
    /// A page with no marked content gets no <c>/StructParents</c> key, and the tagged page
    /// beside it is keyed from 0. Keys number only the pages that have content to map.
    /// </summary>
    [Fact]
    public void AnUntaggedPage_GetsNoStructParentsKey_AndTheTaggedPageIsKeyedFromZero()
    {
        var builder = new PdfDocumentBuilder().WithCompression(false);
        var untagged = builder.AddPage(200, 200);
        string fontUntagged = untagged.AddStandardFont(StandardFonts.Helvetica);
        untagged.BeginText().SetFont(fontUntagged, 12).SetTextPosition(10, 100).ShowText("plain").EndText();

        var tagged = builder.AddPage(200, 200);
        var para = builder.Structure.Root.AddChild(PdfStructureTypes.P);
        string fontTagged = tagged.AddStandardFont(StandardFonts.Helvetica);
        tagged.BeginMarkedContent(para);
        tagged.BeginText().SetFont(fontTagged, 12).SetTextPosition(10, 100).ShowText("tagged").EndText();
        tagged.EndMarkedContent();

        using var ms = new MemoryStream();
        builder.Build(ms);
        using var doc = PdfDocument.Open(new MemoryStream(ms.ToArray()));

        Assert.Null(doc.Pages[0].Dictionary.Get(PdfName.StructParents));
        Assert.Equal(0, ((PdfNumber)doc.ResolveObject(doc.Pages[1].Dictionary.Get(PdfName.StructParents))).IntValue);
        Assert.Equal(1, ((PdfNumber)doc.ResolveObject(StructRoot(doc).Get(PdfName.ParentTreeNextKey))).IntValue);
    }

    /// <summary>
    /// A top-level element's <c>/P</c> is the structure tree root, not the element beside it in the
    /// numbering.
    /// </summary>
    [Fact]
    public void ATopLevelElement_ItsParentIsTheStructTreeRoot()
    {
        var builder = new PdfDocumentBuilder().WithCompression(false);
        var para = builder.Structure.Root.AddChild(PdfStructureTypes.P);
        var page = builder.AddPage(200, 200);
        string font = page.AddStandardFont(StandardFonts.Helvetica);
        page.BeginMarkedContent(para);
        page.BeginText().SetFont(font, 12).SetTextPosition(10, 100).ShowText("x").EndText();
        page.EndMarkedContent();

        using var ms = new MemoryStream();
        builder.Build(ms);
        using var doc = PdfDocument.Open(new MemoryStream(ms.ToArray()));

        var root = StructRoot(doc);
        var kids = (PdfArray)doc.ResolveObject(root.Get(PdfName.K));
        var element = (PdfDictionary)doc.ResolveObject(kids[0]);

        var parentRef = Assert.IsType<PdfReference>(element.Get(PdfName.P));
        var parent = (PdfDictionary)doc.ResolveObject(parentRef);
        Assert.Equal(PdfName.StructTreeRoot, parent.Get(PdfName.Type));
    }

    /// <summary>
    /// An element claiming an MCID at or beyond a page's marked content is ignored, rather than
    /// indexing past the page's array of owners. The page keeps the one MCID it drew.
    /// </summary>
    [Fact]
    public void AnMcidBeyondThePagesMarkedContent_IsIgnoredNotIndexed()
    {
        var builder = new PdfDocumentBuilder().WithCompression(false);
        var para = builder.Structure.Root.AddChild(PdfStructureTypes.P);
        var page = builder.AddPage(200, 200);
        string font = page.AddStandardFont(StandardFonts.Helvetica);
        page.BeginMarkedContent(para);
        page.BeginText().SetFont(font, 12).SetTextPosition(10, 100).ShowText("x").EndText();
        page.EndMarkedContent();

        // Claim MCID 1 on a page that has only MCID 0.
        para.AddContent(page, page.MarkedContentCount);

        using var ms = new MemoryStream();
        builder.Build(ms);
        using var doc = PdfDocument.Open(new MemoryStream(ms.ToArray()));

        var parentTree = (PdfDictionary)doc.ResolveObject(StructRoot(doc).Get(PdfName.ParentTree));
        var nums = (PdfArray)doc.ResolveObject(parentTree.Get(PdfName.Nums));
        var perMcid = (PdfArray)doc.ResolveObject(nums[1]);
        Assert.Equal(1, perMcid.Count);
    }

    private static PdfDictionary StructRoot(PdfDocument doc)
        => (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot));
}
