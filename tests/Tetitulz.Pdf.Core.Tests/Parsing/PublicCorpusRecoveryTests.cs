// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Files from another reader's test corpus that an erring recovery reads short or wrong, each against
/// the reader it exercises. They run when
/// <c>TETITULZ_PDF_CORPUS</c> names another reader's <c>test/pdfs</c> folder and skip otherwise.
/// </summary>
public class PublicCorpusRecoveryTests
{
    /// <summary>Offsets shifted by one object: every entry points at the body of the object before it. Both pages are read from their own headers; one of two was, and the optimizer saved a damaged file.</summary>
    [CorpusFact("issue7229.pdf")]
    public void Issue7229_EveryObjectIsReadFromItsOwnHeader_BothPages()
    {
        using var doc = PdfDocument.Open(new MemoryStream(File.ReadAllBytes(TestCorpus.Find("issue7229.pdf")!), writable: false));
        Assert.Equal(2, doc.Pages.Count);
    }

    /// <summary>A traditional subsection start of 4294967296: the table holds no object numbered <c>int.MaxValue</c> nor any wrapped negative one, and the document still opens through the scan.</summary>
    [CorpusFact("GHOSTSCRIPT-698804-1-fuzzed.pdf")]
    public void FuzzedFile698804_ASubsectionStartBeyondInt_DoesNotWrapIntoTheTable()
    {
        using var doc = PdfDocument.Open(new MemoryStream(File.ReadAllBytes(TestCorpus.Find("GHOSTSCRIPT-698804-1-fuzzed.pdf")!), writable: false));
        Assert.Null(doc.Parser.CrossReferenceTable.GetEntry(int.MaxValue));
        Assert.Null(doc.Parser.CrossReferenceTable.GetEntry(int.MinValue));
        Assert.Null(doc.Parser.CrossReferenceTable.GetEntry(int.MinValue + 1));
        Assert.True(doc.Pages.Count >= 1, "the document's page was not recovered");
    }

    /// <summary>An object stream of 65 542 members, the catalog among them, opens: the default cap is above it.</summary>
    [CorpusFact("bug1978317.pdf")]
    public void Bug1978317_AnObjectStreamOf65542Members_Opens()
    {
        using var doc = PdfDocument.Open(new MemoryStream(File.ReadAllBytes(TestCorpus.Find("bug1978317.pdf")!), writable: false));
        Assert.Equal(1, doc.Pages.Count);
        Assert.Equal(PdfName.Catalog, (doc.ResolveObject(doc.Trailer[PdfName.Root]) as PdfDictionary)?.GetName(PdfName.Type));
    }
}
