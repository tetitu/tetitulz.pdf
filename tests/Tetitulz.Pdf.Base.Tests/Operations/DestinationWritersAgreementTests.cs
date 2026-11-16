// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The three Base writers of an <c>/XYZ</c> destination — named destinations, the initial view and
/// the outline — now build it in one place, <c>Core.Writing.XyzDestination</c>, and the readers
/// resolve its page through the one §12.3.2.2 lookup.
/// </summary>
public class DestinationWritersAgreementTests
{
    /// <summary>
    /// <b>They disagreed on a page with no object number</b> — a page dictionary written inline in
    /// <c>/Kids</c>, which nothing can reference. Named destinations and the initial view wrote its
    /// index; the outline writer wrote no destination, so the bookmark pointed nowhere. All three
    /// write the index now, and every reader resolves it back to the page.
    /// </summary>
    [Fact]
    public void AllThreeNameAPageWithNoObjectNumberByItsIndex()
    {
        byte[] pdf = InlinePageDocument();

        PdfObject named = DestinationPage(WithNamedDestination(pdf), doc => NamedDest(doc, "here"));
        PdfObject opening = DestinationPage(WithInitialView(pdf),
            doc => (PdfArray)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OpenAction])!);
        PdfObject outline = DestinationPage(WithBookmark(pdf), FirstOutlineDest);

        foreach (PdfObject page in new[] { named, opening, outline })
            Assert.Equal(0, Assert.IsType<PdfNumber>(page).IntValue);

        // And each is read back as the page it names.
        var bookmark = Assert.Single(PdfBookmarksManager.ReadBookmarks(new MemoryStream(WithBookmark(pdf))));
        Assert.Equal(0, bookmark.PageNumber);
    }

    /// <summary>The control: for an ordinary page all three write a reference to the page object.</summary>
    [Fact]
    public void AllThreeNameAnOrdinaryPageByReference()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        PdfObject named = DestinationPage(WithNamedDestination(pdf), doc => NamedDest(doc, "here"));
        PdfObject opening = DestinationPage(WithInitialView(pdf),
            doc => (PdfArray)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.OpenAction])!);
        PdfObject outline = DestinationPage(WithBookmark(pdf), FirstOutlineDest);

        foreach (PdfObject page in new[] { named, opening, outline })
            Assert.Equal(3, Assert.IsType<PdfReference>(page).ReferenceObjectNumber);
    }

    /// <summary>
    /// A page number that is not an integer names no page. The lookup cast the double to an int, so
    /// <c>[1.5 /Fit]</c> read as page 1; the integer control beside it still resolves.
    /// </summary>
    [Fact]
    public void ANonIntegerPageNumberNamesNoPage()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Outlines /First 7 0 R /Last 8 0 R /Count 2 >>",
            "<< /Title (fraction) /Parent 6 0 R /Next 8 0 R /Dest [1.5 /Fit] >>",
            "<< /Title (integer) /Parent 6 0 R /Prev 7 0 R /Dest [1 /Fit] >>");

        var marks = PdfBookmarksManager.ReadBookmarks(new MemoryStream(pdf));

        Assert.Null(marks[0].PageNumber);
        Assert.Equal(1, marks[1].PageNumber);
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>One page, written directly inside <c>/Kids</c> — so it has no object number.</summary>
    private static byte[] InlinePageDocument() => FormPdfFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>] /Count 1 >>");

    private static byte[] WithNamedDestination(byte[] pdf)
    {
        using var output = new MemoryStream();
        PdfNamedDestinations.SetDestination(new MemoryStream(pdf), output, "here", 0, top: 700);
        return output.ToArray();
    }

    private static byte[] WithInitialView(byte[] pdf)
    {
        using var output = new MemoryStream();
        PdfInitialView.SetInitialView(new MemoryStream(pdf), output, new InitialViewSettings { OpenAtPageIndex = 0 });
        return output.ToArray();
    }

    private static byte[] WithBookmark(byte[] pdf)
    {
        using var output = new MemoryStream();
        PdfBookmarksManager.WriteBookmarks(new MemoryStream(pdf), output,
            new List<PdfBookmark> { new PdfBookmark("Start", 0, 0) });
        return output.ToArray();
    }

    private static PdfObject DestinationPage(byte[] pdf, System.Func<PdfDocument, PdfArray> destination)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        PdfArray dest = destination(doc);
        Assert.Equal("XYZ", Assert.IsType<PdfName>(dest[1]).Value);
        return dest[0];
    }

    private static PdfArray NamedDest(PdfDocument doc, string name)
    {
        var names = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Names])!;
        var leaf = (PdfDictionary)doc.ResolveObject(names[PdfName.Dests])!;
        var entries = (PdfArray)doc.ResolveObject(leaf[PdfName.Names])!;
        for (int i = 0; i + 1 < entries.Count; i += 2)
            if (((PdfString)entries[i]).GetText() == name)
                return (PdfArray)doc.ResolveObject(entries[i + 1])!;
        throw new Xunit.Sdk.XunitException("no destination named " + name);
    }

    private static PdfArray FirstOutlineDest(PdfDocument doc)
    {
        var root = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.Outlines])!;
        var first = (PdfDictionary)doc.ResolveObject(root[PdfName.First])!;
        return Assert.IsType<PdfArray>(doc.ResolveObject(first[PdfName.Dest]));
    }
}
