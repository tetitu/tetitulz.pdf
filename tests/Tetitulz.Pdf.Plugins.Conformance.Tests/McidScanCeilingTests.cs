// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// The ceilings UA50's content scan works under — the per-page one, and the document-wide one it
/// did not have.
/// </summary>
/// <remarks>
/// <para><b>Found by reading the two content walks in this assembly against each other</b>, which is
/// the only thing that can show a ceiling present in one and absent in the other: each walk is
/// individually reasonable, and no test of either can see what the other bounds.
/// <c>PdfXValidator</c> carries both a per-page and a per-document ceiling, with the sentence
/// <i>"A per-page cap alone does not bound a 50 000-page file"</i> written at the second;
/// <c>PdfUaChecker.DefinedMcids</c> had only the first.</para>
///
/// <para><b>These assert the guard's own evidence — a reported UA99 — and never a wall clock.</b>
/// Under a full-solution run elapsed time measures the machine's backlog rather than the code's
/// work.</para>
/// </remarks>
public class McidScanCeilingTests
{
    /// <summary>8 MiB — <c>PdfUaChecker.MaxMcidScanBytesPerPage</c>, which is private.</summary>
    private const int PerPage = 8 * 1024 * 1024;

    /// <summary>128 MiB — <c>MaxMcidScanBytesPerDocument</c>, i.e. exactly sixteen full pages.</summary>
    private const int PagesToExhaustDocumentCeiling = 16;

    /// <summary>
    /// <b>A page whose content exceeds the per-page ceiling is reported, not silently treated as
    /// defining no identifiers.</b>
    /// </summary>
    /// <remarks>
    /// The direction matters: an empty set of defined MCIDs would make every identifier the
    /// structure tree names look missing, turning one oversized page into a page's worth of UA50
    /// violations on a file that may be perfectly conformant.
    /// </remarks>
    [Fact]
    public void AnOversizedPageIsReportedRatherThanReadAsDefiningNoMcids()
    {
        byte[] pdf = TaggedPages(pageCount: 1, decodedBytesPerPage: PerPage + 1024);

        var result = PdfUaChecker.Validate(new MemoryStream(pdf));

        Assert.Contains(result.Violations, v => v.Code == "UA99");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA50");
    }

    /// <summary>
    /// <b>The document-wide ceiling stops the scan, and says which page it stopped at.</b>
    /// </summary>
    /// <remarks>
    /// <para>Sixteen pages of exactly the per-page ceiling consume the document ceiling precisely,
    /// so the seventeenth reaches it with nothing left. Before this ceiling existed, a per-page cap
    /// of 8 MiB multiplied by the page count and bounded nothing — the shape
    /// <c>PdfXValidator</c> had already written a sentence about.</para>
    ///
    /// <para>The fixture is at production scale on purpose: the ceiling is a private constant, and a
    /// test that lowered it would pass over the forwarding that production uses.</para>
    /// </remarks>
    [Fact]
    public void TheDocumentWideCeilingStopsTheScanAndNamesThePageItStoppedAt()
    {
        byte[] pdf = TaggedPages(PagesToExhaustDocumentCeiling + 1, PerPage);

        var result = PdfUaChecker.Validate(new MemoryStream(pdf));

        Assert.Contains(result.Violations,
            v => v.Code == "UA99" && v.Message.IndexOf("document content ceiling",
                                                      StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The acceptance control, and the one that stops both tests above from passing on a checker
    /// that reports UA99 for everything: an ordinary small tagged page is scanned, finds its
    /// identifier, and reports neither code.
    /// </summary>
    [Fact]
    public void AnOrdinaryTaggedPageIsScannedAndReportsNeitherCode()
    {
        byte[] pdf = TaggedPages(pageCount: 1, decodedBytesPerPage: 0);

        var result = PdfUaChecker.Validate(new MemoryStream(pdf));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA50");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
    }

    // ==================================================================
    //  The ceilings belong to the caller
    // ==================================================================

    /// <summary>
    /// <b>Both ceilings are the caller's to set.</b> The same document is scanned under a ceiling
    /// that admits it and refused under one that does not — so the parameter reaches the decision
    /// rather than being accepted and ignored.
    /// </summary>
    /// <remarks>
    /// Only a caller knows whether the next file is a hostile upload or a 400-page press proof, so
    /// a library that hard-codes its DoS ceilings has picked for them. <c>PdfXValidator</c> has
    /// taken this pair since the PDF/X round; this walk did not, which is the same divergence the
    /// missing document ceiling was.
    /// </remarks>
    [Theory]
    [InlineData(64 * 1024, PerPage, true)]        // per-page ceiling below the content
    [InlineData(PerPage, 64 * 1024, true)]        // document ceiling below the content
    [InlineData(PerPage, PerPage, false)]         // both admit it
    public void BothCeilingsAreTheCallersToSet(int maxPage, long maxDocument, bool expectRefusal)
    {
        byte[] pdf = TaggedPages(pageCount: 1, decodedBytesPerPage: 128 * 1024);

        var result = PdfUaChecker.Validate(new MemoryStream(pdf), maxPage, maxDocument);

        Assert.Equal(expectRefusal, result.Violations.Any(v => v.Code == "UA99"));
        // Either way the check must not invent UA50: a page it declined to read defines nothing it
        // is entitled to report as missing.
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA50");
    }

    /// <summary>A non-positive ceiling is refused by name rather than meaning "unlimited".</summary>
    [Theory]
    [InlineData(0, PerPage, "maxPageContentBytes")]
    [InlineData(PerPage, 0, "maxDocumentContentBytes")]
    public void ANonPositiveCeilingIsRefusedByName(int maxPage, long maxDocument, string expected)
    {
        byte[] pdf = TaggedPages(pageCount: 1, decodedBytesPerPage: 0);

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfUaChecker.Validate(new MemoryStream(pdf), maxPage, maxDocument));

        Assert.Equal(expected, ex.ParamName);
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// <paramref name="pageCount"/> tagged pages, each marking its text with MCID 0 and each named
    /// by its own <c>&lt;P&gt;</c> structure element, so UA50's scan runs for every one of them.
    /// </summary>
    /// <param name="decodedBytesPerPage">
    /// Total decoded size of each page's content stream, padded with spaces — content-stream
    /// whitespace, so the padding is syntactically inert. 0 leaves the content unpadded.
    /// </param>
    private static byte[] TaggedPages(int pageCount, int decodedBytesPerPage)
    {
        const string marked = "/P << /MCID 0 >> BDC\nBT /F1 12 Tf 20 100 Td (Hi) Tj ET\nEMC\n";
        byte[] contentBytes = Encoding.ASCII.GetBytes(
            decodedBytesPerPage <= marked.Length
                ? marked
                : marked + new string(' ', decodedBytesPerPage - marked.Length));

        // Flate so the FILE stays small while the DECODED size is what the ceilings see. Without
        // this the fixture would be as large as the limit it is testing.
        byte[] compressed = Deflate(contentBytes);

        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        void EmitRaw(byte[] b) => acc.AddRange(b);
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        //  1 catalog | 2 pages | 3 font | 4 struct tree root | 5 parent tree
        //  then per page i (0-based): 6+3i page, 7+3i content, 8+3i struct elem
        int PageObj(int i) => 6 + 3 * i;
        int ContentObj(int i) => 7 + 3 * i;
        int ElemObj(int i) => 8 + 3 * i;

        Emit("%PDF-1.7\n");

        BeginObj(1);
        Emit("<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R " +
             "/MarkInfo << /Marked true >> /Lang (en-US) >>\nendobj\n");

        BeginObj(2);
        Emit("<< /Type /Pages /Count " + pageCount + " /Kids [" +
             string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{PageObj(i)} 0 R")) +
             "] >>\nendobj\n");

        BeginObj(3);
        Emit("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        BeginObj(4);
        Emit("<< /Type /StructTreeRoot /K [" +
             string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{ElemObj(i)} 0 R")) +
             "] /ParentTree 5 0 R >>\nendobj\n");

        BeginObj(5);
        Emit("<< /Nums [" +
             string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{i} [{ElemObj(i)} 0 R]")) +
             "] >>\nendobj\n");

        for (int i = 0; i < pageCount; i++)
        {
            BeginObj(PageObj(i));
            Emit($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents {ContentObj(i)} 0 R " +
                 $"/StructParents {i} /Resources << /Font << /F1 3 0 R >> >> >>\nendobj\n");

            BeginObj(ContentObj(i));
            Emit($"<< /Length {compressed.Length} /Filter /FlateDecode >>\nstream\n");
            EmitRaw(compressed);
            Emit("\nendstream\nendobj\n");

            BeginObj(ElemObj(i));
            Emit($"<< /Type /StructElem /S /P /P 4 0 R /Pg {PageObj(i)} 0 R /K 0 >>\nendobj\n");
        }

        int xref = acc.Count;
        int count = ElemObj(pageCount - 1) + 1;
        Emit($"xref\n0 {count}\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++)
            Emit(offsets.TryGetValue(i, out int off) ? $"{off:D10} 00000 n \n" : "0000000000 65535 f \n");
        Emit($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }

    /// <summary>zlib-wrapped deflate, which is what <c>/FlateDecode</c> means (RFC 1950).</summary>
    private static byte[] Deflate(byte[] raw)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            z.Write(raw, 0, raw.Length);
        return ms.ToArray();
    }
}
