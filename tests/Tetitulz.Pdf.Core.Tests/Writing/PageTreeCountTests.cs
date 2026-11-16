// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// A written <c>/Pages</c> root's <c>/Count</c> agrees with its <c>/Kids</c> (§7.7.3.2).
/// </summary>
/// <remarks>
/// <para><b>Found by mutation, not by review.</b> Five writers built this node by hand; when the
/// consolidated <see cref="PdfPageTreeWriter"/> was mutated to emit <c>kids.Count + 1</c>, the
/// PageEdits suite reddened (4 tests) and <b>Core, Base and DocxToPdf stayed entirely green</b>.
/// Four writers were emitting a page count nothing verified.</para>
///
/// <para><b>Why the library's own tests cannot see it.</b> <c>PdfPageTree</c> resolves pages by
/// walking <c>/Kids</c>, so <c>doc.Pages.Count</c> is right whatever <c>/Count</c> says — this
/// library stays perfectly self-consistent while writing a file that tells every other reader
/// something different. §7.7.3.2 defines <c>/Count</c> as the number of leaf nodes, and readers
/// that trust it (page-count display, print ranges, "N of M") would disagree with the pages that
/// actually exist. The mismatch is invisible from inside and obvious from outside, which is why it
/// needs asserting on the <b>written bytes</b>.</para>
/// </remarks>
public class PageTreeCountTests
{
    /// <summary>The shared decision itself: <c>/Count</c> is derived, never supplied.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public void BuildRootDerivesCountFromKids(int pageCount)
    {
        PdfDictionary pages = PdfPageTreeWriter.BuildRoot(
            Enumerable.Range(10, pageCount).ToArray(), objectNumber: 1);

        var kids = pages[PdfName.Kids] as PdfArray;
        Assert.NotNull(kids);
        Assert.Equal(pageCount, kids!.Count);
        Assert.Equal(pageCount, ((PdfNumber)pages[PdfName.Count]!).IntValue);
        Assert.Equal("Pages", ((PdfName)pages[PdfName.Type]!).Value);
    }

    /// <summary>
    /// The two overloads agree. They are separate implementations of the kids array — one builds
    /// it, one is handed it — so nothing but a test stops them diverging.
    /// </summary>
    [Fact]
    public void TheTwoOverloadsAgree()
    {
        var explicitKids = new PdfArray();
        foreach (int n in new[] { 4, 5, 6 }) explicitKids.Add(new PdfReference(n, 0));

        PdfDictionary fromKids = PdfPageTreeWriter.BuildRoot(explicitKids, 1);
        PdfDictionary fromNums = PdfPageTreeWriter.BuildRoot(new[] { 4, 5, 6 }, 1);

        Assert.Equal(((PdfNumber)fromKids[PdfName.Count]!).IntValue,
                     ((PdfNumber)fromNums[PdfName.Count]!).IntValue);

        var a = (PdfArray)fromKids[PdfName.Kids]!;
        var b = (PdfArray)fromNums[PdfName.Kids]!;
        Assert.Equal(a.Count, b.Count);
        for (int i = 0; i < a.Count; i++)
        {
            var ra = (PdfReference)a[i];
            var rb = (PdfReference)b[i];
            Assert.Equal(ra.ObjectNumber, rb.ObjectNumber);
            Assert.Equal(ra.GenerationNumber, rb.GenerationNumber);
        }
    }

    /// <summary>A null argument is a caller bug, not an empty page tree.</summary>
    [Fact]
    public void NullArgumentsThrow()
    {
        Assert.Throws<ArgumentNullException>(() => PdfPageTreeWriter.BuildRoot((PdfArray)null!, 1));
        Assert.Throws<ArgumentNullException>(() => PdfPageTreeWriter.BuildRoot((int[])null!, 1));
    }

    /// <summary>
    /// End to end, over the bytes <see cref="PdfDocumentBuilder"/> actually writes: the
    /// <c>/Pages</c> node in the file must claim exactly the number of pages it lists.
    /// </summary>
    /// <remarks>
    /// This is the half the unit tests above cannot cover — that the writer <i>uses</i> the shared
    /// helper. Covering a function is not covering the decision that calls it.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void DocumentBuilderWritesACountMatchingItsKids(int pageCount)
    {
        var builder = new PdfDocumentBuilder();
        for (int i = 0; i < pageCount; i++)
            builder.AddPage(612, 792);

        using var ms = new MemoryStream();
        builder.Build(ms);
        ms.Position = 0;

        using var doc = PdfDocument.Open(ms);

        // The /Pages node, read back from the written file.
        PdfDictionary pages = doc.Catalog!.PageTreeRoot;
        Assert.NotNull(pages);

        var kids = doc.ResolveObject(pages![PdfName.Kids]) as PdfArray;
        Assert.NotNull(kids);

        int declared = ((PdfNumber)doc.ResolveObject(pages[PdfName.Count])!).IntValue;

        Assert.Equal(pageCount, kids!.Count);
        Assert.Equal(pageCount, declared);

        // …and the reader agrees, so this is not asserting two views of one wrong number.
        Assert.Equal(pageCount, doc.Pages.Count);
    }
}
