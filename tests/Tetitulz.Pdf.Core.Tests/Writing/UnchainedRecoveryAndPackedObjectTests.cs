// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// The two places where an object stream made a write lose data quietly: an appended revision that
/// has to re-declare the whole document, and an object packed into an <c>/ObjStm</c> by
/// <see cref="PdfCompressedFileWriter"/>.
///
/// <para>Both assert on the <b>output</b> — the reopened document, or the reported warning — because
/// both defects shipped behind a positive count and a null diagnostic.</para>
/// </summary>
public class UnchainedRecoveryAndPackedObjectTests
{
    private const int CatalogNumber = 1;
    private const int PagesNumber = 2;
    private const int FontNumber = 3;
    private const int AppendedNumber = 40;

    // ===============================================================
    //  Fixtures
    // ===============================================================

    private static List<(int objectNumber, PdfObject obj)> MinimalDocument()
    {
        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(PagesNumber, 0));

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        pages.Set(PdfName.Kids, new PdfArray());
        pages.Set(PdfName.Count, new PdfNumber(0));

        return new List<(int, PdfObject)>
        {
            (CatalogNumber, catalog),
            (PagesNumber, pages),
        };
    }

    private static PdfDictionary TrailerFor()
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(CatalogNumber, 0));
        return trailer;
    }

    private static byte[] WriteCompressed(IReadOnlyList<(int objectNumber, PdfObject obj)> objects)
    {
        using var buffer = new MemoryStream();
        PdfCompressedFileWriter.Write(buffer, new PdfVersion(1, 7), objects, TrailerFor());
        return buffer.ToArray();
    }

    /// <summary>
    /// Overwrites the digits after the last <c>startxref</c> with zeroes — same length, so every
    /// other byte offset in the file stays valid. This is the "damaged tail" of the finding: the
    /// writer can no longer verify a previous section, so the appended one must stand alone.
    /// </summary>
    private static byte[] ZeroTheStartXrefOffset(byte[] pdf)
    {
        var copy = (byte[])pdf.Clone();
        string text = Encoding.ASCII.GetString(copy);
        int keyword = text.LastIndexOf("startxref", StringComparison.Ordinal);
        Assert.True(keyword >= 0, "fixture: the compressed writer must emit a startxref");

        int i = keyword + "startxref".Length;
        while (i < copy.Length && (copy[i] == (byte)'\r' || copy[i] == (byte)'\n' || copy[i] == (byte)' ')) i++;
        int digits = 0;
        while (i + digits < copy.Length && copy[i + digits] >= (byte)'0' && copy[i + digits] <= (byte)'9') digits++;
        Assert.True(digits > 0, "fixture: startxref must be followed by an offset");

        for (int d = 0; d < digits; d++) copy[i + d] = (byte)'0';
        return copy;
    }

    // ===============================================================
    //  1. An unchained revision has to name the objects that live in an /ObjStm
    // ===============================================================

    [Fact]
    public void Unchained_revision_redeclares_the_catalog_that_lives_inside_an_object_stream()
    {
        byte[] original = WriteCompressed(MinimalDocument());

        using PdfDocument source = PdfDocument.Open(new MemoryStream(original));

        // Fixture check: the defect only exists for objects the writer packed, so the test is
        // meaningless unless the catalog really is a type-2 entry.
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed,
            source.CrossReferenceTable.GetEntry(CatalogNumber).Type);

        byte[] damaged = ZeroTheStartXrefOffset(original);

        var appended = new PdfDictionary();
        appended.Set(PdfName.Type, PdfName.Page);

        var options = new IncrementalUpdateOptions
        {
            Document = source,
            // 0 means "I know of no previous section": with the tail zeroed, the scan cannot
            // rescue it either, so the appended section is standalone.
            PreviousXrefOffset = 0,
            Format = XrefSectionFormat.CrossReferenceStream,
        };

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(
            damaged, output,
            new List<IndirectObject> { new IndirectObject(AppendedNumber, 0, appended) },
            options);

        // Reopening is the assertion: with the catalog missing from the appended section, PdfDocument
        // throws "Cannot find document catalog" and the file is unreadable.
        using PdfDocument reopened = PdfDocument.Open(new MemoryStream(output.ToArray()));

        Assert.Equal(CrossReferenceEntry.EntryType.Compressed,
            reopened.CrossReferenceTable.GetEntry(CatalogNumber).Type);

        var catalog = Assert.IsType<PdfDictionary>(reopened.Parser.GetObject(CatalogNumber));
        Assert.Equal(PdfName.Catalog, catalog.GetName(PdfName.Type));

        // …and the revision really was standalone, not quietly chained to the original section.
        Assert.Null(reopened.Trailer[PdfName.Prev]);
    }

    [Fact]
    public void Unchained_classic_revision_reports_the_objects_it_could_not_name()
    {
        // A classic xref table has no type field, so a compressed object cannot be re-declared in
        // one at all. The honest outcome is a warning that says so — not a count that omits them.
        byte[] original = WriteCompressed(MinimalDocument());
        using PdfDocument source = PdfDocument.Open(new MemoryStream(original));
        byte[] damaged = ZeroTheStartXrefOffset(original);

        var diagnostics = new List<string>();
        var options = new IncrementalUpdateOptions
        {
            Document = source,
            PreviousXrefOffset = 0,
            Format = XrefSectionFormat.ClassicTable,
            DiagnosticSink = (operation, detail, _) => diagnostics.Add(operation + ": " + detail),
        };

        var appended = new PdfDictionary();
        appended.Set(PdfName.Type, PdfName.Page);

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(
            damaged, output,
            new List<IndirectObject> { new IndirectObject(AppendedNumber, 0, appended) },
            options);

        Assert.Contains(diagnostics, d =>
            d.Contains("AddRecoveryEntries") &&
            d.Contains("live in object streams and could not be re-declared"));
    }

    // ===============================================================
    //  2. A stream nested inside a PACKED object is still a loss, and must be reported
    // ===============================================================

    [Fact]
    public void Packed_object_holding_a_direct_stream_reports_the_dropped_stream_data()
    {
        // §7.3.8 forbids a stream anywhere but at the top level, so PdfWriter can only write this
        // one's dictionary and its bytes are lost. The counter that exists to say so lived on a
        // throwaway writer, so every packed object's loss went unreported.
        var fontFile = new PdfStream();
        fontFile.Set(PdfName.Length1, new PdfNumber(4));
        fontFile.RawData = new byte[] { 0x00, 0x01, 0x02, 0x03 };

        var font = new PdfDictionary();
        font.Set(PdfName.Type, PdfName.Font);
        font.Set(PdfName.FontFile2, fontFile);   // direct, not a reference — the shape that loses data

        List<(int objectNumber, PdfObject obj)> objects = MinimalDocument();
        objects.Add((FontNumber, font));

        byte[] bytes = WriteCompressed(objects);

        Assert.NotNull(PdfCompressedFileWriter.LastWriteWarning);
        Assert.Contains("1 stream(s) were nested inside another object",
            PdfCompressedFileWriter.LastWriteWarning);

        // The claim is precise: the object WAS packed (so the outer writer never saw it) and its
        // stream data really is absent from the file.
        using PdfDocument document = PdfDocument.Open(new MemoryStream(bytes));
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed,
            document.CrossReferenceTable.GetEntry(FontNumber).Type);
    }

    [Fact]
    public void A_document_with_no_nested_stream_reports_no_warning()
    {
        // The counterpart: the accumulation must not report a loss that did not happen, or the
        // warning is noise a caller learns to ignore.
        byte[] bytes = WriteCompressed(MinimalDocument());

        Assert.Null(PdfCompressedFileWriter.LastWriteWarning);
        using PdfDocument document = PdfDocument.Open(new MemoryStream(bytes));
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed,
            document.CrossReferenceTable.GetEntry(CatalogNumber).Type);
    }
}
