// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Cross-reference resolution for hybrid-reference files (§7.5.8.4), and the file-controlled
/// offsets that reach <c>Stream.Position</c>.
/// </summary>
/// <remarks>
/// <para>
/// A hybrid file exists so a pre-1.5 reader can open a document that uses object streams. Its
/// classic table marks every object-stream member <b>free</b> — a pre-1.5 reader then treats it as
/// deleted — and records the real type-2 entry in a cross-reference stream named by
/// <c>/XRefStm</c>. Two independent defects made that construction unreadable here, and each needs
/// its own fixture because either one alone can mask the other:
/// </para>
/// <list type="number">
///   <item><b>The free entry won permanently.</b> <c>AddEntry</c> was unconditionally first-wins
///   and the classic table is parsed first, so <c>GetObject</c> hit its <c>default:</c> branch and
///   returned <c>PdfNull</c> for every object in an object stream. When the catalog is one of them
///   — the normal case for an a common viewer "compatible" save — the file would not open at all.</item>
///   <item><b><c>/Prev</c> was enqueued before <c>/XRefStm</c>.</b> The queue is FIFO, so the
///   OLDER revision's entry claimed the slot and the contemporary <c>/XRefStm</c> entry was
///   discarded — the superseded object, returned silently as current.</item>
/// </list>
/// </remarks>
public class HybridXrefAndOffsetTests
{
    // ==================================================================
    //  Hybrid-reference resolution
    // ==================================================================

    /// <summary>
    /// Isolates defect 1: one revision, no <c>/Prev</c>. Object 4 is free in the classic table and
    /// type-2 in the <c>/XRefStm</c>, so only the free-entry rule decides the outcome.
    /// </summary>
    [Fact]
    public void HybridFile_ObjectMarkedFreeInClassicTable_ResolvesThroughTheXRefStm()
    {
        byte[] pdf = BuildSingleRevisionHybrid();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var obj = doc.Parser.GetObject(4);

        var dict = Assert.IsType<PdfDictionary>(obj);
        Assert.Equal(2, dict.GetInteger(new PdfName("Marker")));
    }

    /// <summary>
    /// Isolates defect 2: the current revision's <c>/XRefStm</c> against an older <c>/Prev</c>
    /// section that still holds a NORMAL entry for the same object. The newest revision's classic
    /// table does not mention object 4 at all, so no free entry is present to mask the ordering.
    /// </summary>
    [Fact]
    public void HybridFile_XRefStmEntryWins_OverTheOlderPrevSection()
    {
        byte[] pdf = BuildUpdatedHybrid();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var dict = Assert.IsType<PdfDictionary>(doc.Parser.GetObject(4));

        Assert.Equal(2, dict.GetInteger(new PdfName("Marker")));
    }

    /// <summary>
    /// The relaxation must stay scoped to the <c>/XRefStm</c> merge. An older section may never
    /// resurrect an object the newest revision genuinely freed, or every incremental deletion in
    /// every ordinary file would come back.
    /// </summary>
    [Fact]
    public void OrdinaryIncrementalUpdate_FreedObjectStaysFreed()
    {
        byte[] pdf = BuildUpdateThatFreesObject4();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.IsType<PdfNull>(doc.Parser.GetObject(4));
    }

    /// <summary>
    /// The relaxation is also scoped by what the replacement <i>is</i>. §7.5.8.4's construction
    /// exists so the REAL type-2 row can supersede the classic table's free marker; one free entry
    /// displacing another only swaps in a different generation and next-free link, which carries
    /// no information and is not something an <c>/XRefStm</c> has any business doing.
    /// </summary>
    /// <remarks>
    /// Asserted against the table directly because the difference is invisible through
    /// <c>GetObject</c> — a free entry resolves to <c>PdfNull</c> whatever its generation says —
    /// which is exactly why the narrower guard needs its own test rather than riding on one of the
    /// document-level fixtures above.
    /// </remarks>
    [Fact]
    public void XRefStmMerge_DoesNotLetOneFreeEntryDisplaceAnother()
    {
        var table = new CrossReferenceTable();
        table.AddEntry(new CrossReferenceEntry
        {
            ObjectNumber = 4,
            GenerationNumber = 0,
            Type = CrossReferenceEntry.EntryType.Free,
        });

        table.AddEntry(new CrossReferenceEntry
        {
            ObjectNumber = 4,
            GenerationNumber = 7,
            Type = CrossReferenceEntry.EntryType.Free,
        }, allowReplacingFree: true);

        Assert.Equal(0, table.GetEntry(4).GenerationNumber);

        // The honest counterpart: an in-use entry from the same merge path still gets through, or
        // the assertion above would pass with the whole relaxation deleted.
        table.AddEntry(new CrossReferenceEntry
        {
            ObjectNumber = 4,
            ObjectStreamNumber = 5,
            IndexInObjectStream = 0,
            Type = CrossReferenceEntry.EntryType.Compressed,
        }, allowReplacingFree: true);

        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, table.GetEntry(4).Type);
    }

    // ==================================================================
    //  File-controlled offsets reaching Stream.Position
    // ==================================================================

    /// <summary>
    /// An entry offset is file-supplied and <c>GetObject</c> seeks to it. A NEGATIVE one is caught
    /// a layer earlier — <c>ParseXrefSectionAt</c> refuses the whole section, which is covered by
    /// <c>HybridXrefAndSectionRecoveryTests</c> — but an offset that merely points PAST EOF is
    /// syntactically valid, survives into the table, and is bounded here.
    /// </summary>
    /// <remarks>
    /// The reason string is asserted rather than just the <c>PdfNull</c>. Seeking past EOF does not
    /// throw; <c>ParseIndirectObject</c> simply fails on end-of-stream, so without the range check
    /// the caller gets <c>PdfNull</c> plus a message about an unexpected token — which is
    /// indistinguishable from a genuinely corrupt object body and points a reader at the wrong
    /// half of the file. Only the range check says the OFFSET was the problem.
    /// <para>The file holds no <c>4 0 obj</c> header: an object whose header is in the file is read
    /// from there instead (<c>ParserRecoveryAndIdentityTests</c>), so a fixture that wrote object 4
    /// would not reach the range check.</para>
    /// </remarks>
    [Fact]
    public void MalformedOutOfRangeEntryOffset_WithNoHeaderToReadFrom_ReportsThroughLastObjectParseError()
    {
        byte[] pdf = BuildWithOutOfRangeEntryOffset();
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        var obj = doc.Parser.GetObject(4);

        Assert.IsType<PdfNull>(obj);
        Assert.Contains("outside the file", PdfParser.LastObjectParseError, StringComparison.Ordinal);

        // The rest of the section is unaffected: one unusable entry is not a damaged table.
        var page = Assert.IsType<PdfDictionary>(doc.Parser.GetObject(3));
        Assert.Equal(PdfName.Page, page.GetName(PdfName.Type));
    }

    /// <summary>
    /// The same class of value reached <c>ParseCrossReferences</c> through <c>/Prev</c>, where it
    /// threw before a single object was touched — so the document could not be opened at all.
    /// </summary>
    /// <remarks>
    /// The reason string is asserted, not just the open. Widening the section catch to
    /// <see cref="Exception"/> also stops the throw, so "the document opens" alone cannot tell
    /// whether the offset was <b>rejected</b> as out of range or merely allowed to fail deeper and
    /// be swallowed. Only the range check produces "outside the file"; letting
    /// <c>Stream.Position</c> reject it yields "must be a non-negative value" instead.
    /// </remarks>
    [Fact]
    public void MalformedNegativePrevOffset_IsRejectedAsOutOfRangeAndTheDocumentStillOpens()
    {
        byte[] pdf = BuildWithNegativePrev();
        using var ms = new MemoryStream(pdf);

        using var doc = PdfDocument.Open(ms);

        var dict = Assert.IsType<PdfDictionary>(doc.Parser.GetObject(4));
        Assert.Equal(1, dict.GetInteger(new PdfName("Marker")));

        Assert.NotNull(PdfParser.LastXrefSectionError);
        Assert.Contains("outside the file", PdfParser.LastXrefSectionError, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>ParseCrossReferences</c> caught only <c>PdfParseException</c>, but the xref-stream path
    /// decodes a stream: <c>FlateDecodeFilter</c> throws the BASE <c>PdfException</c> and
    /// <c>FilterPipeline</c> throws <c>PdfFilterException</c>, which derives from
    /// <see cref="Exception"/> directly. Neither was caught, so a corrupt payload in the newest
    /// section escaped both the section-skip and the scan-recovery fallback and the file became
    /// unopenable.
    /// </summary>
    [Fact]
    public void MalformedFlatePayloadInNewestXrefStream_IsSkippedAndTheDocumentStillOpens()
    {
        byte[] pdf = BuildWithCorruptXrefStreamAtHead();
        using var ms = new MemoryStream(pdf);

        using var doc = PdfDocument.Open(ms);

        var dict = Assert.IsType<PdfDictionary>(doc.Parser.GetObject(4));
        Assert.Equal(1, dict.GetInteger(new PdfName("Marker")));

        // …and the caller can tell it was read from a recovered/older state rather than the
        // section the file pointed at.
        Assert.NotNull(PdfParser.LastXrefSectionError);
        Assert.Contains("skipped", PdfParser.LastXrefSectionError, StringComparison.Ordinal);
    }

    // ==================================================================
    //  Fixture builders
    // ==================================================================

    /// <summary>Assembles PDF bytes while recording each object's byte offset.</summary>
    private sealed class Builder
    {
        private readonly MemoryStream _ms = new MemoryStream();
        public readonly Dictionary<int, long> Offsets = new Dictionary<int, long>();

        public long Position => _ms.Length;

        public void Raw(string s)
        {
            byte[] b = Encoding.ASCII.GetBytes(s);
            _ms.Write(b, 0, b.Length);
        }

        public void RawBytes(byte[] b) => _ms.Write(b, 0, b.Length);

        public void Obj(int number, string body)
        {
            Offsets[number] = _ms.Length;
            Raw($"{number} 0 obj\n{body}\nendobj\n");
        }

        public void StreamObj(int number, string dictBody, byte[] data)
        {
            Offsets[number] = _ms.Length;
            Raw($"{number} 0 obj\n<< {dictBody} /Length {data.Length} >>\nstream\n");
            RawBytes(data);
            Raw("\nendstream\nendobj\n");
        }

        public byte[] ToArray() => _ms.ToArray();
    }

    private const string CatalogBody = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string PagesBody = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string PageBody = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>";

    /// <summary>A 20-byte classic xref entry.</summary>
    private static string Entry(long offset, int gen, char kind)
        => offset.ToString("D10") + " " + gen.ToString("D5") + " " + kind + " \n";

    /// <summary>An uncompressed /ObjStm holding object 4 as <c>&lt;&lt; /Marker n &gt;&gt;</c>.</summary>
    private static byte[] ObjStmData(int marker, out int first)
    {
        string header = "4 0 ";
        string body = $"<< /Marker {marker} >>";
        first = header.Length;
        return Encoding.ASCII.GetBytes(header + body);
    }

    /// <summary>
    /// An uncompressed /XRef stream with <c>/W [1 2 1]</c> and <c>/Index [4 1]</c>, recording
    /// object 4 as type 2 at index 0 of object stream 5.
    /// </summary>
    private static byte[] XrefStreamData()
        => new byte[] { 2, 0, 5, 0 };

    private static byte[] BuildSingleRevisionHybrid()
    {
        var b = new Builder();
        b.Raw("%PDF-1.5\n");
        b.Obj(1, CatalogBody);
        b.Obj(2, PagesBody);
        b.Obj(3, PageBody);
        byte[] objStm = ObjStmData(2, out int first);
        b.StreamObj(5, $"/Type /ObjStm /N 1 /First {first}", objStm);
        b.StreamObj(6, "/Type /XRef /W [1 2 1] /Index [4 1] /Size 7 /Root 1 0 R", XrefStreamData());

        long xrefPos = b.Position;
        b.Raw("xref\n0 7\n");
        b.Raw(Entry(0, 65535, 'f'));
        b.Raw(Entry(b.Offsets[1], 0, 'n'));
        b.Raw(Entry(b.Offsets[2], 0, 'n'));
        b.Raw(Entry(b.Offsets[3], 0, 'n'));
        b.Raw(Entry(0, 0, 'f'));                 // object 4 — free, per §7.5.8.4
        b.Raw(Entry(b.Offsets[5], 0, 'n'));
        b.Raw(Entry(b.Offsets[6], 0, 'n'));
        b.Raw($"trailer\n<< /Size 7 /Root 1 0 R /XRefStm {b.Offsets[6]} >>\n");
        b.Raw($"startxref\n{xrefPos}\n%%EOF\n");
        return b.ToArray();
    }

    private static byte[] BuildUpdatedHybrid()
    {
        var b = new Builder();
        b.Raw("%PDF-1.5\n");
        b.Obj(1, CatalogBody);
        b.Obj(2, PagesBody);
        b.Obj(3, PageBody);
        b.Obj(4, "<< /Marker 1 >>");             // revision 1's version, a NORMAL entry

        long xref1 = b.Position;
        b.Raw("xref\n0 5\n");
        b.Raw(Entry(0, 65535, 'f'));
        b.Raw(Entry(b.Offsets[1], 0, 'n'));
        b.Raw(Entry(b.Offsets[2], 0, 'n'));
        b.Raw(Entry(b.Offsets[3], 0, 'n'));
        b.Raw(Entry(b.Offsets[4], 0, 'n'));
        b.Raw("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        b.Raw($"startxref\n{xref1}\n%%EOF\n");

        // ---- revision 2: object 4 superseded inside an object stream ----
        byte[] objStm = ObjStmData(2, out int first);
        b.StreamObj(5, $"/Type /ObjStm /N 1 /First {first}", objStm);
        b.StreamObj(6, "/Type /XRef /W [1 2 1] /Index [4 1] /Size 7 /Root 1 0 R", XrefStreamData());

        long xref2 = b.Position;
        // Deliberately covers only 5 and 6 — object 4 is named ONLY by the /XRefStm, so nothing
        // but the enqueue order decides whether /Prev's stale entry or the stream's entry wins.
        b.Raw("xref\n5 2\n");
        b.Raw(Entry(b.Offsets[5], 0, 'n'));
        b.Raw(Entry(b.Offsets[6], 0, 'n'));
        b.Raw($"trailer\n<< /Size 7 /Root 1 0 R /XRefStm {b.Offsets[6]} /Prev {xref1} >>\n");
        b.Raw($"startxref\n{xref2}\n%%EOF\n");
        return b.ToArray();
    }

    private static byte[] BuildUpdateThatFreesObject4()
    {
        var b = new Builder();
        b.Raw("%PDF-1.5\n");
        b.Obj(1, CatalogBody);
        b.Obj(2, PagesBody);
        b.Obj(3, PageBody);
        b.Obj(4, "<< /Marker 1 >>");

        long xref1 = b.Position;
        b.Raw("xref\n0 5\n");
        b.Raw(Entry(0, 65535, 'f'));
        b.Raw(Entry(b.Offsets[1], 0, 'n'));
        b.Raw(Entry(b.Offsets[2], 0, 'n'));
        b.Raw(Entry(b.Offsets[3], 0, 'n'));
        b.Raw(Entry(b.Offsets[4], 0, 'n'));
        b.Raw("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        b.Raw($"startxref\n{xref1}\n%%EOF\n");

        // ---- revision 2: ordinary incremental update that DELETES object 4 ----
        long xref2 = b.Position;
        b.Raw("xref\n4 1\n");
        b.Raw(Entry(0, 1, 'f'));
        b.Raw($"trailer\n<< /Size 5 /Root 1 0 R /Prev {xref1} >>\n");
        b.Raw($"startxref\n{xref2}\n%%EOF\n");
        return b.ToArray();
    }

    /// <summary>
    /// Object 4's entry names a byte position far beyond the end of the file. Ten digits is the
    /// prescribed field width, so this is a perfectly well-formed entry — the section parses, the
    /// other four entries stay usable, and only the seek can detect the problem. Object 4 itself is
    /// not written, so no header of its own is there to read it from.
    /// </summary>
    private static byte[] BuildWithOutOfRangeEntryOffset()
    {
        var b = new Builder();
        b.Raw("%PDF-1.5\n");
        b.Obj(1, CatalogBody);
        b.Obj(2, PagesBody);
        b.Obj(3, PageBody);

        long xref = b.Position;
        b.Raw("xref\n0 5\n");
        b.Raw(Entry(0, 65535, 'f'));
        b.Raw(Entry(b.Offsets[1], 0, 'n'));
        b.Raw(Entry(b.Offsets[2], 0, 'n'));
        b.Raw(Entry(b.Offsets[3], 0, 'n'));
        b.Raw(Entry(9_999_999_999L, 0, 'n'));    // the largest legal 10-digit offset, far past EOF
        b.Raw("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        b.Raw($"startxref\n{xref}\n%%EOF\n");
        return b.ToArray();
    }

    private static byte[] BuildWithNegativePrev()
    {
        var b = new Builder();
        b.Raw("%PDF-1.5\n");
        b.Obj(1, CatalogBody);
        b.Obj(2, PagesBody);
        b.Obj(3, PageBody);
        b.Obj(4, "<< /Marker 1 >>");

        long xref = b.Position;
        b.Raw("xref\n0 5\n");
        b.Raw(Entry(0, 65535, 'f'));
        b.Raw(Entry(b.Offsets[1], 0, 'n'));
        b.Raw(Entry(b.Offsets[2], 0, 'n'));
        b.Raw(Entry(b.Offsets[3], 0, 'n'));
        b.Raw(Entry(b.Offsets[4], 0, 'n'));
        b.Raw("trailer\n<< /Size 5 /Root 1 0 R /Prev -1 >>\n");
        b.Raw($"startxref\n{xref}\n%%EOF\n");
        return b.ToArray();
    }

    private static byte[] BuildWithCorruptXrefStreamAtHead()
    {
        var b = new Builder();
        b.Raw("%PDF-1.5\n");
        b.Obj(1, CatalogBody);
        b.Obj(2, PagesBody);
        b.Obj(3, PageBody);
        b.Obj(4, "<< /Marker 1 >>");

        long xref1 = b.Position;
        b.Raw("xref\n0 5\n");
        b.Raw(Entry(0, 65535, 'f'));
        b.Raw(Entry(b.Offsets[1], 0, 'n'));
        b.Raw(Entry(b.Offsets[2], 0, 'n'));
        b.Raw(Entry(b.Offsets[3], 0, 'n'));
        b.Raw(Entry(b.Offsets[4], 0, 'n'));
        b.Raw("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        b.Raw($"startxref\n{xref1}\n%%EOF\n");

        // ---- revision 2: an xref stream whose Flate payload is garbage ----
        b.StreamObj(6, $"/Type /XRef /Filter /FlateDecode /W [1 2 1] /Index [4 1] /Size 7 " +
                       $"/Root 1 0 R /Prev {xref1}",
            new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0xFB, 0xFA });

        b.Raw($"startxref\n{b.Offsets[6]}\n%%EOF\n");
        return b.ToArray();
    }
}
