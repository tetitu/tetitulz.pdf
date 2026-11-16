// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// §12.3.2.2 explicit destination array → page index, through the three readers that follow a
/// destination: link annotations, outline items and the named-destination tree. All three used to
/// carry their own copy of the walk; <c>Operations.PdfDestinationPage</c> is now the only one.
/// </summary>
/// <remarks>
/// <para><b>What the copies actually did before the merge, since this round is strict about not
/// billing hygiene as a bug fix.</b> On page references and on numeric page indices — in range and
/// out — the three <i>agreed</i>, and the numeric out-of-range case was the one the named-dest copy
/// documented as "matching <c>PdfLinkExtractor.PageIndexFromDestArray</c>". There was exactly one
/// separating input, and it is the last test here: only the named-dest copy required the referenced
/// object number to be <b>positive</b>. §7.3.10 reserves object 0 permanently for the head of the
/// free list, so <c>0 0 R</c> designates nothing — but a page dictionary written directly inside
/// <c>/Kids</c> is a <c>PdfObject</c> whose <c>ObjectNumber</c> is the default <c>0</c>, and the
/// page tree tolerates that shape. For such a file the link and outline copies matched that page
/// and reported a confident, wrong page index.</para>
///
/// <para>Every assertion is an index or a null, never "a list came back": an unresolved destination
/// yields <c>-1</c> / <c>null</c>, which is also the correct answer for a genuinely broken one, so
/// only the exact value separates the two.</para>
/// </remarks>
public class ExplicitDestinationPageIndexTests
{
    // ==================================================================
    //  Agreement across the three readers, on the inputs they shared
    // ==================================================================

    /// <summary>
    /// A destination naming page 2 by reference resolves to index 1 for a link, an outline item and
    /// a named destination alike.
    /// </summary>
    [Fact]
    public void APageReferenceResolvesToThatPagesIndex()
    {
        byte[] pdf = TwoPagePdf("[4 0 R /Fit]");

        Assert.Equal(1, LinkTarget(pdf));
        Assert.Equal(1, BookmarkPage(pdf));
        Assert.Equal(1, NamedDestPage(pdf));
    }

    /// <summary>
    /// The first page is index 0 and not confused with "unresolved": the readers' failure value is
    /// <c>-1</c> / <c>null</c> precisely so that page 0 stays a real answer.
    /// </summary>
    [Fact]
    public void AReferenceToTheFirstPageResolvesToIndexZero()
    {
        byte[] pdf = TwoPagePdf("[3 0 R /XYZ null null null]");

        Assert.Equal(0, LinkTarget(pdf));
        Assert.Equal(0, BookmarkPage(pdf));
        Assert.Equal(0, NamedDestPage(pdf));
    }

    /// <summary>
    /// §12.3.2.2's other spelling: the page as a zero-based integer, used by remote go-to targets.
    /// </summary>
    [Fact]
    public void ANumericPageIndexIsTakenAsWritten()
    {
        byte[] pdf = TwoPagePdf("[1 /Fit]");

        Assert.Equal(1, LinkTarget(pdf));
        Assert.Equal(1, BookmarkPage(pdf));
        Assert.Equal(1, NamedDestPage(pdf));
    }

    /// <summary>
    /// A numeric index past the end of the document is a broken destination, not page 0 and not the
    /// last page. Range-checking it is the behaviour all three copies had, and the one the merged
    /// implementation keeps.
    /// </summary>
    [Fact]
    public void ANumericPageIndexPastTheLastPageResolvesToNothing()
    {
        byte[] pdf = TwoPagePdf("[7 /Fit]");

        Assert.Equal(-1, LinkTarget(pdf));
        Assert.Null(BookmarkPage(pdf));
        Assert.Equal(-1, NamedDestPage(pdf));
    }

    /// <summary>
    /// A reference to an object that is not a page in this document resolves to nothing rather than
    /// to whichever page happens to share its position in the list.
    /// </summary>
    [Fact]
    public void AReferenceToANonPageObjectResolvesToNothing()
    {
        // Object 5 is a content stream, not a page.
        byte[] pdf = TwoPagePdf("[5 0 R /Fit]");

        Assert.Equal(-1, LinkTarget(pdf));
        Assert.Null(BookmarkPage(pdf));
        Assert.Equal(-1, NamedDestPage(pdf));
    }

    /// <summary>
    /// A destination whose first element is neither a page reference nor a number — here a string —
    /// resolves to nothing instead of being coerced.
    /// </summary>
    [Fact]
    public void AMalformedFirstElementResolvesToNothing()
    {
        byte[] pdf = TwoPagePdf("[(page two) /Fit]");

        Assert.Equal(-1, LinkTarget(pdf));
        Assert.Null(BookmarkPage(pdf));
        Assert.Equal(-1, NamedDestPage(pdf));
    }

    // ==================================================================
    //  The separating input
    // ==================================================================

    /// <summary>
    /// The fixture for the test below really does contain a page whose dictionary carries object
    /// number 0 and a destination spelled <c>0 0 R</c>. Without both, the next test passes for the
    /// wrong reason — a destination that matches no page for want of a page to match.
    /// </summary>
    [Fact]
    public void TheDirectPageFixtureCarriesAnObjectNumberZeroPageAndAZeroReference()
    {
        using var ms = new MemoryStream(DirectPageDictPdf());
        using var doc = PdfDocument.Open(ms);

        PdfPage page = Assert.Single(doc.Pages);
        Assert.Equal(0, page.Dictionary.ObjectNumber);

        var annots = doc.ResolveObject(page.Dictionary[PdfName.Annots]) as PdfArray;
        Assert.NotNull(annots);
        var annot = doc.ResolveObject(annots[0]) as PdfDictionary;
        Assert.NotNull(annot);
        var dest = doc.ResolveObject(annot[PdfName.Dest]) as PdfArray;
        Assert.NotNull(dest);
        var pageRef = dest[0] as PdfReference;
        Assert.NotNull(pageRef);
        Assert.Equal(0, pageRef.ReferenceObjectNumber);
    }

    /// <summary>
    /// <c>0 0 R</c> designates no object (§7.3.10 reserves object 0 for the free-list head), so a
    /// destination spelled that way resolves to nothing — even in a document whose only page was
    /// written as a direct dictionary inside <c>/Kids</c> and therefore carries object number 0
    /// itself.
    /// </summary>
    /// <remarks>
    /// This is the one input on which the three copies disagreed. The link and outline copies
    /// compared object numbers without requiring a positive one, so <c>0 == 0</c> matched and both
    /// reported page 0 for a destination pointing nowhere. The named-destination copy's stricter
    /// clause is what the merged implementation kept.
    /// </remarks>
    [Fact]
    public void AZeroObjectNumberReferenceResolvesToNothing_EvenAgainstADirectPageDictionary()
    {
        byte[] pdf = DirectPageDictPdf();

        Assert.Equal(-1, LinkTarget(pdf));
        Assert.Null(BookmarkPage(pdf));
        Assert.Equal(-1, NamedDestPage(pdf));
    }

    // ------------------------------------------------------------------
    //  The three readers
    // ------------------------------------------------------------------

    private static int LinkTarget(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        PdfLink link = Assert.Single(PdfLinkExtractor.ExtractLinks(ms));
        Assert.Equal(PdfLinkKind.GoTo, link.Kind);
        return link.TargetPageIndex;
    }

    private static int? BookmarkPage(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        PdfBookmark bookmark = Assert.Single(PdfBookmarksManager.ReadBookmarks(ms));
        Assert.Equal("B1", bookmark.Title);
        return bookmark.PageNumber;
    }

    private static int NamedDestPage(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        NamedDestination dest = Assert.Single(PdfNamedDestinations.ListDestinations(ms));
        Assert.Equal("D1", dest.Name);
        return dest.PageIndex;
    }

    // ------------------------------------------------------------------
    //  Fixtures
    // ------------------------------------------------------------------

    /// <summary>
    /// Two pages (objects 3 and 4), and the same destination array reached three ways: from a
    /// <c>/Link</c> annotation's <c>/Dest</c>, from an outline item's <c>/Dest</c>, and from the
    /// <c>/Names /Dests</c> name tree.
    /// </summary>
    private static byte[] TwoPagePdf(string dest)
    {
        return Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 8 0 R"
                + " /Names << /Dests << /Names [(D1) " + dest + "] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R"
                + " /Annots [7 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 6 0 R >>",
            Stream("", "q Q\n"),
            Stream("", "q Q\n"),
            "<< /Type /Annot /Subtype /Link /Rect [10 10 30 30] /Dest " + dest + " >>",
            "<< /Type /Outlines /First 9 0 R /Count 1 >>",
            "<< /Title (B1) /Parent 8 0 R /Dest " + dest + " >>");
    }

    /// <summary>
    /// One page written as a <b>direct</b> dictionary inside <c>/Kids</c> — malformed, tolerated by
    /// <c>PdfPageTree</c>, and the only way to obtain a page whose <c>ObjectNumber</c> is the
    /// default 0. Every destination in it is <c>[0 0 R /Fit]</c>.
    /// </summary>
    private static byte[] DirectPageDictPdf()
    {
        const string Dest = "[0 0 R /Fit]";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R"
                + " /Names << /Dests << /Names [(D1) " + Dest + "] >> >> >>",
            "<< /Type /Pages /Count 1 /Kids [<< /Type /Page /MediaBox [0 0 200 200]"
                + " /Contents 3 0 R /Annots [4 0 R] >>] >>",
            Stream("", "q Q\n"),
            "<< /Type /Annot /Subtype /Link /Rect [10 10 30 30] /Dest " + Dest + " >>",
            "<< /Type /Outlines /First 6 0 R /Count 1 >>",
            "<< /Title (B1) /Parent 5 0 R /Dest " + Dest + " >>");
    }

    private static string Stream(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "endstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
