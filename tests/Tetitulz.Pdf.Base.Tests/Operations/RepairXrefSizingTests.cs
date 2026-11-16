// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A rebuilt cross-reference table is indexed by <b>object number</b>, so it must be sized by the
/// highest object number written — never by how many objects were recovered.
/// </summary>
/// <remarks>
/// <para>
/// <c>RebuildFromRaw</c> passed <c>recovered + 1</c> to both <c>WriteXref</c> and
/// <c>WriteTrailer</c>, and <c>WriteXref</c> looped <c>for (i = 1; i &lt; size)</c>. For a file
/// whose numbering has a gap — objects 1, 2, 3 and 9 — that is a table declaring <c>0 5</c> with
/// rows for 1–4 only. <b>Object 9 was copied into the body and then had no cross-reference entry
/// at all</b>, so every reference to it resolved to null in the "repaired" file, and the trailer's
/// <c>/Size 5</c> said the document contains no such object. Sparse numbering is what an
/// incrementally-updated or partially-recovered file looks like, which is precisely the input this
/// tool exists for.
/// </para>
/// <para>
/// The two halves are asserted separately on purpose. Sizing by the maximum object number is only
/// half a fix: the rows for the numbers inside the gap must be <b>free</b> entries (§7.5.4), not
/// in-use rows pointing at offset 0 — a dangling <c>n</c> row sends a reader to the file header and
/// is worse than an absent object. And the dense case is asserted too, so "size by the maximum"
/// cannot be satisfied by always writing a table of 65 536 rows.
/// </para>
/// </remarks>
public class RepairXrefSizingTests
{
    /// <summary>The highest object number in the sparse fixture; 4–8 are the gap.</summary>
    private const int GappedObjectNumber = 9;

    // ================================================================
    //  The defect: an object above the recovered count has no xref row
    // ================================================================

    /// <summary>
    /// <b>The headline case.</b> Every recovered object must be readable back out of the repaired
    /// file <i>by its own number</i>, and the highest-numbered one must carry its own payload.
    /// </summary>
    [Fact]
    public void EveryObjectOfAFileWithANumberingGapIsReadableBackByItsOwnNumber()
    {
        Repair(SparselyNumberedPdf(), out byte[] repaired);

        using var doc = PdfDocument.Open(new MemoryStream(repaired));

        Assert.Equal(PdfName.Catalog, Assert.IsType<PdfDictionary>(doc.GetObject(1)).GetName(PdfName.Type));
        Assert.Equal(PdfName.Pages, Assert.IsType<PdfDictionary>(doc.GetObject(2)).GetName(PdfName.Type));
        Assert.Equal(PdfName.Page, Assert.IsType<PdfDictionary>(doc.GetObject(3)).GetName(PdfName.Type));

        var gapped = Assert.IsType<PdfDictionary>(doc.GetObject(GappedObjectNumber));
        Assert.Equal(4242, gapped.GetInteger(new PdfName("Serial")) ?? -1);
    }

    /// <summary>
    /// The reference the page makes to the high-numbered object must resolve, which is how a real
    /// consumer reaches it. An object with no xref row resolves to null however it is reached.
    /// </summary>
    [Fact]
    public void AReferenceToTheHighestNumberedObjectStillResolvesInTheRepairedFile()
    {
        Repair(SparselyNumberedPdf(), out byte[] repaired);

        using var doc = PdfDocument.Open(new MemoryStream(repaired));
        var page = Assert.IsType<PdfDictionary>(doc.GetObject(3));
        var target = Assert.IsType<PdfDictionary>(doc.ResolveObject(page[new PdfName("Serial")]));

        Assert.Equal(4242, target.GetInteger(new PdfName("Serial")) ?? -1);
    }

    /// <summary>
    /// <c>/Size</c> is one greater than the highest object number, not one greater than the count.
    /// They coincide only when numbering is dense from 1, which is why the gapped fixture is the
    /// one that can tell them apart: four objects, highest number 9, so <c>/Size 10</c>.
    /// </summary>
    [Fact]
    public void TheTrailerSizeIsTheHighestObjectNumberPlusOneNotTheRecoveredCount()
    {
        var result = Repair(SparselyNumberedPdf(), out byte[] repaired);

        Assert.Equal(4, result.RecoveredObjectCount);

        using var doc = PdfDocument.Open(new MemoryStream(repaired));
        Assert.Equal(GappedObjectNumber + 1, doc.Trailer.GetInteger(PdfName.Size) ?? -1);
    }

    /// <summary>
    /// The subsection header and the row count must agree with <c>/Size</c> — a table declaring
    /// <c>0 10</c> and carrying five rows is a different defect with the same trailer.
    /// </summary>
    [Fact]
    public void TheXrefSubsectionCoversEveryObjectNumberUpToTheHighest()
    {
        Repair(SparselyNumberedPdf(), out byte[] repaired);

        Assert.Equal("0 " + (GappedObjectNumber + 1), SubsectionHeader(repaired));
        Assert.Equal(GappedObjectNumber + 1, XrefRowCount(repaired));
    }

    /// <summary>
    /// §7.5.4: object 0 is the head of the free list, and an object number covered by the table but
    /// absent from the file is a <b>free</b> entry. A gap row written as in-use would point every
    /// reader at byte offset 0 — the file header — which is worse than a missing object.
    /// </summary>
    [Fact]
    public void TheNumbersInsideTheGapAreFreeEntriesAndTheRecoveredOnesAreInUse()
    {
        Repair(SparselyNumberedPdf(), out byte[] repaired);

        Assert.Equal(PdfXrefFormat.FreeType, EntryType(repaired, 0));
        Assert.Equal(PdfXrefFormat.MaxGenerationNumber, EntryGeneration(repaired, 0));

        for (int i = 1; i <= 3; i++)
        {
            Assert.Equal(PdfXrefFormat.InUseType, EntryType(repaired, i));
            Assert.True(EntryOffset(repaired, i) > 0, $"object {i} has an in-use row at offset 0");
        }

        for (int i = 4; i <= 8; i++)
        {
            Assert.Equal(PdfXrefFormat.FreeType, EntryType(repaired, i));
            Assert.Equal(0, EntryOffset(repaired, i));
        }

        Assert.Equal(PdfXrefFormat.InUseType, EntryType(repaired, GappedObjectNumber));
        Assert.True(EntryOffset(repaired, GappedObjectNumber) > 0);
    }

    // ================================================================
    //  The other half: the dense case must not grow
    // ================================================================

    /// <summary>
    /// Dense numbering from 1 is the case where count and maximum coincide, so the table must be
    /// exactly as long as it always was. Without this, "size by the maximum object number" is
    /// satisfied by writing 65 536 rows for every repair.
    /// </summary>
    [Fact]
    public void ADenselyNumberedFileStillGetsATableOfExactlyItsOwnLength()
    {
        var result = Repair(DenselyNumberedPdf(), out byte[] repaired);

        Assert.Equal(4, result.RecoveredObjectCount);
        Assert.Equal("0 5", SubsectionHeader(repaired));
        Assert.Equal(5, XrefRowCount(repaired));

        using var doc = PdfDocument.Open(new MemoryStream(repaired));
        Assert.Equal(5, doc.Trailer.GetInteger(PdfName.Size) ?? -1);
        Assert.Equal(4242,
            Assert.IsType<PdfDictionary>(doc.GetObject(4)).GetInteger(new PdfName("Serial")) ?? -1);
    }

    /// <summary>
    /// A file with a single object numbered 1 gets a two-row table: the free-list head and that
    /// object. The smallest case the loop bounds have to get right.
    /// </summary>
    [Fact]
    public void ASingleObjectFileGetsTheFreeListHeadAndOneRow()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog >>\nendobj\n");
        sb.Append("%%EOF\n");

        Repair(Encoding.Latin1.GetBytes(sb.ToString()), out byte[] repaired);

        Assert.Equal("0 2", SubsectionHeader(repaired));
        Assert.Equal(2, XrefRowCount(repaired));
        Assert.Equal(PdfXrefFormat.InUseType, EntryType(repaired, 1));
    }

    // ================================================================
    //  Recovering nothing must say so
    // ================================================================

    /// <summary>
    /// A repair that recovered no objects at all reported <c>0</c> objects and an <b>empty</b> error
    /// list, so a caller could not tell "there was nothing to recover" from "recognition refused
    /// every object in the file". The reason is reported, not thrown — the rest of this tool
    /// tolerates failures and records them on <see cref="PdfRepairResult.Errors"/>.
    /// </summary>
    [Fact]
    public void RecoveringNoObjectsIsReportedRatherThanLookingLikeACleanRepair()
    {
        byte[] nothingToRecover = Encoding.Latin1.GetBytes("%PDF-1.4\nnot a pdf body at all\n%%EOF\n");

        var result = Repair(nothingToRecover, out byte[] repaired);

        Assert.Equal(0, result.RecoveredObjectCount);
        Assert.False(result.IsClean);
        Assert.NotEmpty(result.Errors);

        // The table for an empty document is the free-list head alone, and /Size is 1.
        Assert.Equal("0 1", SubsectionHeader(repaired));
        Assert.Equal(1, XrefRowCount(repaired));
    }

    /// <summary>
    /// The complement, so the report above cannot be "always complain": a repair that recovered
    /// objects stays clean.
    /// </summary>
    [Fact]
    public void ARepairThatRecoveredObjectsStaysClean()
    {
        var result = Repair(SparselyNumberedPdf(), out _);

        Assert.True(result.IsClean, string.Join("; ", result.Errors));
    }

    // ================================================================
    //  Fixtures and xref readers
    // ================================================================

    private static PdfRepairResult Repair(byte[] damaged, out byte[] repaired)
    {
        using var input = new MemoryStream(damaged);
        using var output = new MemoryStream();
        var result = PdfRepair.Repair(input, output);
        repaired = output.ToArray();
        Assert.True(repaired.Length > 0, "repair produced nothing to assert about");
        return result;
    }

    /// <summary>
    /// Four objects numbered 1, 2, 3 and <see cref="GappedObjectNumber"/>, with <b>no</b>
    /// cross-reference table, so repair has to rebuild one. The gap is what separates "sized by the
    /// count" from "sized by the highest object number".
    /// </summary>
    private static byte[] SparselyNumberedPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Serial "
                  + GappedObjectNumber + " 0 R >>\nendobj\n");
        sb.Append(GappedObjectNumber + " 0 obj\n<< /Serial 4242 >>\nendobj\n");
        sb.Append("%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>The same document numbered 1, 2, 3, 4 — count and maximum coincide.</summary>
    private static byte[] DenselyNumberedPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Serial 4 0 R >>\nendobj\n");
        sb.Append("4 0 obj\n<< /Serial 4242 >>\nendobj\n");
        sb.Append("%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>
    /// Start of the classic cross-reference <b>table</b>: an <c>xref</c> keyword at the start of a
    /// line. <c>LastIndexOf("xref\n")</c> matches inside <c>startxref\n</c>, which sits later in
    /// every file.
    /// </summary>
    private static int XrefTableStart(string text)
    {
        int at = text.Length;
        while (true)
        {
            at = text.LastIndexOf("xref\n", at - 1, StringComparison.Ordinal);
            if (at < 0) return -1;
            if (at == 0 || text[at - 1] == '\n') return at;
        }
    }

    /// <summary>The single subsection header, e.g. <c>"0 10"</c>.</summary>
    private static string SubsectionHeader(byte[] repaired)
    {
        string text = Encoding.Latin1.GetString(repaired);
        int xref = XrefTableStart(text);
        Assert.True(xref >= 0, "the repaired file has no classic xref table");

        int headerStart = text.IndexOf('\n', xref) + 1;
        int headerEnd = text.IndexOf('\n', headerStart);
        return text.Substring(headerStart, headerEnd - headerStart);
    }

    /// <summary>Number of fixed-width rows between the subsection header and <c>trailer</c>.</summary>
    private static int XrefRowCount(byte[] repaired)
    {
        string text = Encoding.Latin1.GetString(repaired);
        int firstRow = FirstRowOffset(text);
        int trailer = text.IndexOf("trailer", firstRow, StringComparison.Ordinal);
        Assert.True(trailer > firstRow, "the repaired file has no trailer after its xref table");

        int span = trailer - firstRow;
        Assert.Equal(0, span % PdfXrefFormat.EntryLength);
        return span / PdfXrefFormat.EntryLength;
    }

    private static int FirstRowOffset(string text)
    {
        int xref = XrefTableStart(text);
        Assert.True(xref >= 0, "the repaired file has no classic xref table");
        return text.IndexOf('\n', text.IndexOf('\n', xref) + 1) + 1;
    }

    /// <summary>One fixed-width row, by object number.</summary>
    private static string Row(byte[] repaired, int objNum)
    {
        string text = Encoding.Latin1.GetString(repaired);
        int row = FirstRowOffset(text) + objNum * PdfXrefFormat.EntryLength;
        Assert.True(row + PdfXrefFormat.EntryLength <= text.Length,
            $"object {objNum} is past the end of the rebuilt xref table");
        return text.Substring(row, PdfXrefFormat.EntryLength);
    }

    private static char EntryType(byte[] repaired, int objNum) => Row(repaired, objNum)[17];

    private static long EntryOffset(byte[] repaired, int objNum)
        => long.Parse(Row(repaired, objNum).Substring(0, 10));

    private static int EntryGeneration(byte[] repaired, int objNum)
        => int.Parse(Row(repaired, objNum).Substring(11, 5));
}
