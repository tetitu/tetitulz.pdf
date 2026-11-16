// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// An edit appended to a document whose cross-reference chain cannot be named <b>re-declares</b> the
/// original objects, rather than producing a file that has lost them.
/// </summary>
/// <remarks>
/// <para><b>Which of two broken outcomes a caller got was decided by whether the edit happened to
/// rewrite the catalog.</b> The appended revision declared only the edited objects, and with no
/// nameable previous section there was no <c>/Prev</c> to inherit the rest from. An edit that does
/// not touch the catalog — <c>RotateAllPages</c>, <c>PdfFormFiller</c>, <c>PdfFormFlattener</c>,
/// <c>PdfImageReplacer</c> — produced a file that throws <c>"Cannot find document catalog."</c>.
/// One that <i>does</i> rewrite it — <c>PdfBookmarksManager</c>, <c>PdfOcgManager</c>,
/// <c>PdfNamedDestinations</c>, <c>PdfMetadataWriter</c>, <c>PdfAttachmentManager</c> — produced one
/// that <b>opens</b>, with a catalog and zero pages. <b>The silent case is the dangerous one</b>,
/// because the caller sees a document that works.</para>
///
/// <para><b>Recover here, refuse for signing.</b> The asymmetry is the argument: a broken edit is
/// loudly broken and retryable, while a broken signature is quietly authoritative. Recovery reads a
/// <i>scan</i>-recovered table, which has no concept of a free entry, so an object deleted in a
/// later revision comes back — acceptable when the caller can look at the result, not when a
/// signature freezes it. See <c>SigningHelpers.RefuseUnnameableCrossReferenceChain</c> and
/// <c>UnnameableXrefSigningTests</c>.</para>
/// </remarks>
public sealed class UnchainedEditRecoveryTests
{
    /// <summary>
    /// The premise, asserted before every case. <b>The second assertion is the load-bearing half</b>:
    /// without it a test cannot distinguish "the writer lost the page" from "the fixture never had
    /// one".
    /// </summary>
    private static byte[] Damaged()
    {
        byte[] pdf = ZeroTheStartXrefOffset(MinimalPdfBuilder.CreateMinimalPdf("Recoverable"));

        using var probe = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(probe);
        Assert.Equal(0, doc.Parser.StartXrefOffset);   // the chain really is unnameable
        Assert.Single(doc.Pages);              // and it really is a one-page document

        return pdf;
    }

    /// <summary>
    /// An edit that does <b>not</b> rewrite the catalog. Red before the fix with
    /// <c>PdfParseException("Cannot find document catalog.")</c> — the loud half.
    /// </summary>
    [Fact]
    public void AnEditThatDoesNotTouchTheCatalogStillReopensWithItsPage()
    {
        AssertReopensWithOnePage(Rotate(Damaged()));
    }

    /// <summary>
    /// An edit that <b>does</b> rewrite the catalog — the silent half, which produced a document
    /// that opened with zero pages and threw nothing at all.
    /// </summary>
    [Fact]
    public void AnEditThatRewritesTheCatalogStillReopensWithItsPage()
    {
        using var input = new MemoryStream(Damaged());
        using var output = new MemoryStream();
        PdfBookmarksManager.WriteBookmarks(input, output,
            new List<PdfBookmark> { new PdfBookmark("Chapter 1", 0, 0) });

        AssertReopensWithOnePage(output.ToArray());
    }

    /// <summary>
    /// A standalone section must carry the free-list head. Without it the table is not a complete
    /// one, which is exactly what a section with no <c>/Prev</c> claims to be.
    /// </summary>
    [Fact]
    public void AStandaloneSectionCarriesTheFreeListHead()
    {
        using var ms = new MemoryStream(Rotate(Damaged()));
        using var doc = PdfDocument.Open(ms);

        var zero = doc.CrossReferenceTable.GetEntry(0);
        Assert.NotNull(zero);
        Assert.Equal(CrossReferenceEntry.EntryType.Free, zero.Type);
    }

    /// <summary>
    /// The writer says what it had to do. This overload has no options object and no diagnostic
    /// sink, so the slot is its only channel — and it is reset at the start of every call, because
    /// a previous document's message must never answer this document's question.
    /// </summary>
    [Fact]
    public void TheRecoveryIsReportedAndTheSlotIsClearedForAChainedDocument()
    {
        Rotate(Damaged());
        Assert.Contains("re-declared", IncrementalUpdateWriter.LastUnchainedRecovery ?? "",
            StringComparison.Ordinal);

        Rotate(MinimalPdfBuilder.CreateMinimalPdf());
        Assert.Null(IncrementalUpdateWriter.LastUnchainedRecovery);
    }

    /// <summary>
    /// <b>The floor, and the one that protects twenty-eight call sites.</b> On an undamaged document
    /// the output must be byte-for-byte what it was: this overload's contract is that it always
    /// writes a classic <c>xref</c> table with no trailer whitelist and no synthesised <c>/ID</c>,
    /// and no functional assertion in the suite would notice that changing.
    /// </summary>
    [Fact]
    public void AnUndamagedDocumentIsWrittenByteForByteAsBefore()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf("Unchanged");

        byte[] first = Rotate(pdf);
        Assert.Equal(first, Rotate(pdf));              // deterministic to begin with
        Assert.Null(IncrementalUpdateWriter.LastUnchainedRecovery);

        // An incremental update leaves the original bytes untouched and appends, so the appended
        // revision is exactly the tail past the input's length.
        Assert.True(first.Length > pdf.Length, "the edit must have appended a revision");
        string appended = Encoding.Latin1.GetString(first, pdf.Length, first.Length - pdf.Length);
        Assert.Contains("xref", appended, StringComparison.Ordinal);

        // It chains, so it must NOT carry object 0 — that free-list head is the one byte-level
        // difference recovery introduces, and this is what pins it to the unchained case.
        Assert.DoesNotContain("65535 f", appended, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// An edit that rewrites page dictionaries and nothing else — no catalog, no page-tree node.
    /// </summary>
    private static byte[] Rotate(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfOperations.RotateAllPages(input, output, 90);
        return output.ToArray();
    }

    private static void AssertReopensWithOnePage(byte[] edited)
    {
        using var ms = new MemoryStream(edited);
        using var doc = PdfDocument.Open(ms);

        Assert.NotNull(doc.Catalog);
        Assert.Single(doc.Pages);
    }

    /// <summary>
    /// Overwrites the digits after the last <c>startxref</c> with zeroes — <b>same length</b>, so
    /// every other byte offset in the file stays valid. Offset 0 is the only value that defeats
    /// every discovery mechanism: it fails a tail scan by value and is rejected outright by the
    /// verifying classifier's <c>offset &lt;= 0</c> guard, so no wider re-scan can rescue it.
    /// Trailing junk would not do — 2000 bytes sits inside
    /// <c>IncrementalUpdateWriter.StartXrefScanWindowBytes</c> (4 KB), so a test written on it
    /// would pass with the feature absent.
    /// </summary>
    private static byte[] ZeroTheStartXrefOffset(byte[] pdf)
    {
        var copy = (byte[])pdf.Clone();
        string text = Encoding.Latin1.GetString(copy);
        int keyword = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(keyword >= 0, "fixture: the builder must emit a startxref");

        int i = keyword + "startxref".Length;
        while (i < copy.Length &&
               (copy[i] == (byte)'\r' || copy[i] == (byte)'\n' || copy[i] == (byte)' ')) i++;

        int digits = 0;
        while (i + digits < copy.Length &&
               copy[i + digits] >= (byte)'0' && copy[i + digits] <= (byte)'9') digits++;
        Assert.True(digits > 0, "fixture: startxref must be followed by an offset");

        for (int d = 0; d < digits; d++) copy[i + d] = (byte)'0';
        return copy;
    }
}
