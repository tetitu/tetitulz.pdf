// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// Coverage for <see cref="PdfCompressedFileWriter"/> — the PDF 1.5 compressed file layout
/// (ISO 32000-1 §7.5.7 object streams + §7.5.8 cross-reference stream).
///
/// <para><b>Every assertion here goes through the real parser or through the raw bytes.</b> "the
/// output re-opens" is not enough on its own: a writer with packing disabled outright produces a
/// file that opens perfectly, so the tests that matter assert <i>which cross-reference entry type</i>
/// an object came back through — <c>Compressed</c> for a packed object, <c>Normal</c> for one §7.5.7
/// forbids packing. Those two facts are the whole feature.</para>
///
/// <para>Every document is built in memory (CLAUDE.md: no test reads a PDF from disk).</para>
/// </summary>
public class PdfCompressedFileWriterTests
{
    // ===============================================================
    //  In-memory document construction
    // ===============================================================

    private const int CatalogNumber = 1;
    private const int PagesNumber = 2;
    private const int PageNumber = 3;
    private const int ContentsNumber = 4;
    private const int InfoNumber = 5;

    /// <summary>First object number free for filler / fixture-specific objects.</summary>
    private const int FirstFreeNumber = 6;

    /// <summary>A consecutive pair, packed back to back. See the round-trip test for why.</summary>
    private const int AdjacentNumberA = 900;
    private const int AdjacentNumberB = 901;

    private const string PageContent = "BT /F1 12 Tf 72 700 Td (Compressed writer round trip) Tj ET\n";

    private const float MediaBoxWidth = 612f;
    private const float MediaBoxHeight = 792f;

    /// <summary>
    /// A one-page document: catalog, page tree, page, content stream, /Info. Deliberately holds a
    /// reference cycle (<c>/Kids</c> down, <c>/Parent</c> back up) because every real PDF does, and
    /// a writer that followed references instead of printing them would not terminate.
    /// </summary>
    private static List<(int objectNumber, PdfObject obj)> BuildDocument(int fillerObjects = 0)
    {
        var objects = new List<(int, PdfObject)>();

        var catalog = new PdfDictionary();
        catalog.Set(PdfName.Type, PdfName.Catalog);
        catalog.Set(PdfName.Pages, new PdfReference(PagesNumber, 0));
        objects.Add((CatalogNumber, catalog));

        var pages = new PdfDictionary();
        pages.Set(PdfName.Type, PdfName.Pages);
        var kids = new PdfArray();
        kids.Add(new PdfReference(PageNumber, 0));
        pages.Set(PdfName.Kids, kids);
        pages.Set(PdfName.Count, new PdfNumber(1));
        objects.Add((PagesNumber, pages));

        var page = new PdfDictionary();
        page.Set(PdfName.Type, PdfName.Page);
        page.Set(PdfName.Parent, new PdfReference(PagesNumber, 0));
        page.Set(PdfName.MediaBox, MediaBox());
        page.Set(PdfName.Contents, new PdfReference(ContentsNumber, 0));
        page.Set(PdfName.Resources, new PdfDictionary());
        objects.Add((PageNumber, page));

        objects.Add((ContentsNumber, ContentStream(PageContent)));

        var info = new PdfDictionary();
        info.Set(PdfName.Producer, new PdfString("Tetitulz.Pdf compressed-writer tests"));
        objects.Add((InfoNumber, info));

        for (int i = 0; i < fillerObjects; i++)
            objects.Add((FirstFreeNumber + i, Filler(i)));

        return objects;
    }

    private static PdfArray MediaBox()
    {
        var box = new PdfArray();
        box.Add(new PdfNumber(0));
        box.Add(new PdfNumber(0));
        box.Add(new PdfNumber(MediaBoxWidth));
        box.Add(new PdfNumber(MediaBoxHeight));
        return box;
    }

    private static PdfStream ContentStream(string content)
    {
        var stream = new PdfStream();
        stream.RawData = Encoding.ASCII.GetBytes(content);
        return stream;
    }

    /// <summary>
    /// A small dictionary of the shape a real document has hundreds of (annotation, font
    /// descriptor, structure element). Distinct per index so deduplication cannot make the size
    /// test pass for the wrong reason.
    /// </summary>
    private static PdfDictionary Filler(int index)
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Page);
        dict.Set(PdfName.Count, new PdfNumber(index));
        dict.Set(PdfName.Title, new PdfString("filler object number " +
            index.ToString(CultureInfo.InvariantCulture)));
        dict.Set(PdfName.Parent, new PdfReference(PagesNumber, 0));
        return dict;
    }

    private static PdfDictionary Trailer(bool withId = true)
    {
        var trailer = new PdfDictionary();
        trailer.Set(PdfName.Root, new PdfReference(CatalogNumber, 0));
        trailer.Set(PdfName.Info, new PdfReference(InfoNumber, 0));
        if (withId)
        {
            var id = new PdfArray();
            id.Add(new PdfString(new byte[16], isHex: true));
            id.Add(new PdfString(new byte[16], isHex: true));
            trailer.Set(PdfName.ID, id);
        }
        return trailer;
    }

    private static byte[] WriteCompressed(
        IReadOnlyList<(int objectNumber, PdfObject obj)> objects,
        PdfDictionary trailer,
        PdfCompressedWriteOptions options = null,
        PdfVersion? version = null)
    {
        using var buffer = new MemoryStream();
        PdfCompressedFileWriter.Write(buffer, version ?? new PdfVersion(1, 7), objects, trailer, options);
        return buffer.ToArray();
    }

    /// <summary>The same objects through the existing classic-xref path, for the size comparison.</summary>
    private static byte[] WriteClassic(
        IReadOnlyList<(int objectNumber, PdfObject obj)> objects, PdfDictionary trailer)
    {
        using var buffer = new MemoryStream();
        var writer = new PdfWriter(buffer);
        writer.WriteHeader(new PdfVersion(1, 7));

        var entries = new List<(int objNum, long offset)>();
        int highest = 0;
        foreach ((int number, PdfObject obj) in objects)
        {
            entries.Add((number, writer.WriteIndirectObject(number, 0, obj)));
            if (number > highest) highest = number;
        }

        long xref = writer.WriteXrefTable(entries);
        var finalTrailer = PdfDictionary.CloneShallow(trailer);
        finalTrailer.Set(PdfName.Size, new PdfNumber(highest + 1));
        writer.WriteTrailer(finalTrailer, xref);
        return buffer.ToArray();
    }

    private static PdfDocument Open(byte[] bytes) => PdfDocument.Open(new MemoryStream(bytes));

    private static CrossReferenceEntry.EntryType EntryTypeOf(PdfDocument document, int objectNumber)
    {
        CrossReferenceEntry entry = document.CrossReferenceTable.GetEntry(objectNumber);
        Assert.NotNull(entry);
        return entry.Type;
    }

    /// <summary>Every object in the file whose dictionary declares <c>/Type /ObjStm</c>.</summary>
    private static List<PdfStream> ObjectStreamsIn(PdfDocument document)
    {
        var found = new List<PdfStream>();
        foreach (CrossReferenceEntry entry in document.CrossReferenceTable.AllEntries)
        {
            if (entry.Type != CrossReferenceEntry.EntryType.Normal) continue;
            if (document.Parser.GetObject(entry.ObjectNumber) is PdfStream stream &&
                stream.GetName(PdfName.Type) == PdfName.ObjStm)
            {
                found.Add(stream);
            }
        }
        return found;
    }

    // ===============================================================
    //  1. Round trip through the real parser
    // ===============================================================

    [Fact]
    public void Written_document_reopens_with_every_value_it_was_given()
    {
        List<(int objectNumber, PdfObject obj)> objects = BuildDocument(fillerObjects: 20);

        // Two adjacent objects that are bare integers. They exist to make the separator byte
        // between object-stream bodies load-bearing: written back to back with nothing between
        // them, "12" and "34" tokenize as the single number 1234 and the first object comes back
        // wrong. Every other object in this fixture is a dictionary, which self-delimits.
        objects.Add((AdjacentNumberA, new PdfNumber(12)));
        objects.Add((AdjacentNumberB, new PdfNumber(34)));

        byte[] bytes = WriteCompressed(objects, Trailer());

        using PdfDocument document = Open(bytes);

        Assert.Equal(12, ((PdfNumber)document.Parser.GetObject(AdjacentNumberA)).LongValue);
        Assert.Equal(34, ((PdfNumber)document.Parser.GetObject(AdjacentNumberB)).LongValue);

        // Catalog — reached through the trailer's /Root, so this also proves the xref stream's
        // dictionary really is the trailer.
        Assert.Single(document.Pages);
        var catalog = (PdfDictionary)document.Parser.GetObject(CatalogNumber);
        Assert.Equal(PdfName.Catalog, catalog.GetName(PdfName.Type));
        Assert.Equal(new[] { PdfName.Type, PdfName.Pages }, catalog.Keys.ToArray());

        // A nested value: /MediaBox is a direct array inside a packed dictionary, so it survives
        // only if the object body was written and indexed at the right offset.
        PdfArray mediaBox = document.Pages[0].MediaBox;
        Assert.Equal(4, mediaBox.Count);
        Assert.Equal(MediaBoxWidth, ((PdfNumber)mediaBox[2]).DoubleValue, 3);
        Assert.Equal(MediaBoxHeight, ((PdfNumber)mediaBox[3]).DoubleValue, 3);

        // A stream's decoded bytes.
        Assert.Equal(PageContent, Encoding.ASCII.GetString(document.Pages[0].GetContentStreamData()));

        // /Info, and a string inside it — strings are the value most easily mangled by an
        // off-by-one in the object-stream index.
        var info = (PdfDictionary)document.Parser.GetObject(InfoNumber);
        Assert.Equal("Tetitulz.Pdf compressed-writer tests", info.GetString(PdfName.Producer).GetText());

        // Every filler object comes back with its own distinct payload — a single wrong /First or
        // a missing separator byte would make neighbours bleed into each other.
        for (int i = 0; i < 20; i++)
        {
            var filler = (PdfDictionary)document.Parser.GetObject(FirstFreeNumber + i);
            Assert.Equal(i, filler.GetInteger(PdfName.Count));
            Assert.Equal("filler object number " + i.ToString(CultureInfo.InvariantCulture),
                filler.GetString(PdfName.Title).GetText());
        }
    }

    [Fact]
    public void Header_declares_at_least_pdf_1_5_because_object_streams_need_it()
    {
        byte[] bytes = WriteCompressed(BuildDocument(), Trailer(), version: new PdfVersion(1, 3));

        Assert.Equal("%PDF-1.5", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Contains("predates object streams", PdfCompressedFileWriter.LastWriteWarning);

        // §7.5.2: the binary comment must follow the header so a text-mode transfer is detectable.
        Assert.Equal(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3 }, bytes.Skip(9).Take(5).ToArray());

        // A version the caller asked for that already allows object streams is left alone.
        byte[] modern = WriteCompressed(BuildDocument(), Trailer(), version: new PdfVersion(1, 7));
        Assert.Equal("%PDF-1.7", Encoding.ASCII.GetString(modern, 0, 8));
        Assert.Null(PdfCompressedFileWriter.LastWriteWarning);
    }

    // ===============================================================
    //  2. The objects really landed in an object stream
    // ===============================================================

    [Fact]
    public void Packed_objects_are_reached_through_a_type_2_cross_reference_entry()
    {
        byte[] bytes = WriteCompressed(BuildDocument(fillerObjects: 20), Trailer());

        // The ObjStm dictionary is written in the clear even though its body is compressed.
        Assert.Contains("/ObjStm", Encoding.ASCII.GetString(bytes));

        using PdfDocument document = Open(bytes);

        // The catalog — a named object — is reachable, and it is reachable *through an object
        // stream*: with packing disabled this entry would be Normal and the document identical
        // in every other respect.
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, EntryTypeOf(document, CatalogNumber));
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, EntryTypeOf(document, PagesNumber));
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, EntryTypeOf(document, PageNumber));
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, EntryTypeOf(document, InfoNumber));

        CrossReferenceEntry catalogEntry = document.CrossReferenceTable.GetEntry(CatalogNumber);
        List<PdfStream> objectStreams = ObjectStreamsIn(document);
        Assert.Single(objectStreams);
        Assert.Contains(objectStreams,
            s => s.ObjectNumber == catalogEntry.ObjectStreamNumber);

        // /N and /First are what make the stream readable at all; assert them against the objects
        // the xref says live in this stream rather than against a constant.
        PdfStream objStm = objectStreams[0];
        int compressedCount = document.CrossReferenceTable.AllEntries
            .Count(e => e.Type == CrossReferenceEntry.EntryType.Compressed);
        Assert.Equal(compressedCount, objStm.GetInteger(PdfName.N));
        Assert.True(objStm.GetInteger(PdfName.First) > 0);
        Assert.Equal(PdfName.FlateDecode, objStm.GetName(PdfName.Filter));

        // The trailer travels in the cross-reference stream, not a `trailer` keyword.
        Assert.DoesNotContain("\ntrailer", Encoding.ASCII.GetString(bytes));
        Assert.Contains("/XRef", Encoding.ASCII.GetString(bytes));
    }

    // ===============================================================
    //  3. Size win against the existing classic path
    // ===============================================================

    [Fact]
    public void Many_small_objects_are_materially_smaller_than_the_classic_xref_path()
    {
        List<(int objectNumber, PdfObject obj)> objects = BuildDocument(fillerObjects: 300);
        PdfDictionary trailer = Trailer();

        int compressed = WriteCompressed(objects, trailer).Length;
        int classic = WriteClassic(objects, trailer).Length;

        // Measured on this fixture: 4 521 bytes against 34 564, an 87% saving. Asserting a
        // quarter leaves 2x headroom, so the test pins the capability rather than the exact
        // deflate output of one zlib version — while still being far out of reach for a writer
        // that packed nothing (which lands within a few hundred bytes of `classic`).
        Assert.True(compressed * 4 < classic,
            $"compressed={compressed} classic={classic}: the object-stream layout must bring a " +
            "document of 300 small dictionaries under a quarter of the classic-xref size.");
    }

    // ===============================================================
    //  4. Streams stay top-level (§7.5.7)
    // ===============================================================

    [Fact]
    public void A_stream_is_written_top_level_and_never_inside_an_object_stream()
    {
        byte[] bytes = WriteCompressed(BuildDocument(fillerObjects: 10), Trailer());

        using PdfDocument document = Open(bytes);

        // Type 1 — an offset into the file, not an index into an ObjStm.
        Assert.Equal(CrossReferenceEntry.EntryType.Normal, EntryTypeOf(document, ContentsNumber));
        Assert.True(document.CrossReferenceTable.GetEntry(ContentsNumber).Offset > 0);

        // …and the object at that offset really is the content stream, with its bytes intact.
        var contents = (PdfStream)document.Parser.GetObject(ContentsNumber);
        Assert.Equal(PageContent, Encoding.ASCII.GetString(contents.GetDecodedData()));

        // The decoded object-stream bodies must not mention the stream at all: a stream serialised
        // into an ObjStm would lose its data silently (PdfWriter can only write a nested stream's
        // dictionary), which parses and then renders a blank page.
        foreach (PdfStream objStm in ObjectStreamsIn(document))
        {
            string decoded = Encoding.ASCII.GetString(objStm.GetDecodedData());
            Assert.DoesNotContain("Compressed writer round trip", decoded);
        }
    }

    [Fact]
    public void An_object_named_as_a_streams_indirect_length_stays_top_level()
    {
        // §7.5.7's fourth exclusion. A reader that had to inflate an object stream to learn how
        // long that same stream is could never bootstrap; PdfWriter happens to rewrite /Length as
        // a direct number, so this is belt-and-braces — and untested code is how belt-and-braces
        // silently becomes wrong.
        const int LengthNumber = FirstFreeNumber + 10;

        List<(int objectNumber, PdfObject obj)> objects = BuildDocument(fillerObjects: 10);
        var stream = ContentStream("q Q\n");
        stream.Set(PdfName.Length, new PdfReference(LengthNumber, 0));
        objects.Add((FirstFreeNumber + 11, stream));
        objects.Add((LengthNumber, new PdfNumber(4)));

        byte[] bytes = WriteCompressed(objects, Trailer());

        using PdfDocument document = Open(bytes);
        Assert.Equal(CrossReferenceEntry.EntryType.Normal, EntryTypeOf(document, LengthNumber));

        // Its gen-0 neighbour of the same shape still packs, so this is the /Length rule firing
        // rather than bare numbers being excluded wholesale.
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, EntryTypeOf(document, FirstFreeNumber));
    }

    // ===============================================================
    //  5. Adversarial input
    // ===============================================================

    [Fact]
    public void Oversized_object_count_splits_across_several_object_streams_rather_than_one_huge_one()
    {
        const int PerStream = 8;
        List<(int objectNumber, PdfObject obj)> objects = BuildDocument(fillerObjects: 60);

        byte[] bytes = WriteCompressed(objects, Trailer(),
            new PdfCompressedWriteOptions { MaxObjectsPerObjectStream = PerStream });

        using PdfDocument document = Open(bytes);

        List<PdfStream> objectStreams = ObjectStreamsIn(document);
        int packed = document.CrossReferenceTable.AllEntries
            .Count(e => e.Type == CrossReferenceEntry.EntryType.Compressed);

        Assert.True(objectStreams.Count >= packed / PerStream,
            $"{packed} packed objects at {PerStream} per stream need at least " +
            $"{packed / PerStream} object streams; found {objectStreams.Count}.");

        // No stream may exceed the cap — a reader is entitled to refuse an over-large /N, and
        // PdfParser refuses one above PdfLimits.MaxObjStmEntries outright.
        foreach (PdfStream objStm in objectStreams)
            Assert.InRange(objStm.GetInteger(PdfName.N).Value, 1, PerStream);

        // Splitting must not lose anything: every filler object still resolves to its own payload.
        for (int i = 0; i < 60; i++)
        {
            var filler = (PdfDictionary)document.Parser.GetObject(FirstFreeNumber + i);
            Assert.Equal(i, filler.GetInteger(PdfName.Count));
        }
    }

    [Fact]
    public void A_cyclic_reference_graph_does_not_hang_and_round_trips()
    {
        // /Kids → page → /Parent → pages: the cycle every real PDF has. A writer that resolved
        // references instead of printing "N G R" would never return from this.
        List<(int objectNumber, PdfObject obj)> objects = BuildDocument();

        // Deepen the cycle so a naive traversal could not bottom out by luck.
        var self = new PdfDictionary();
        self.Set(PdfName.Parent, new PdfReference(FirstFreeNumber, 0));   // points at itself
        self.Set(PdfName.Kids, new PdfArray());
        objects.Add((FirstFreeNumber, self));

        byte[] bytes = WriteCompressed(objects, Trailer());

        using PdfDocument document = Open(bytes);
        var recovered = (PdfDictionary)document.Parser.GetObject(FirstFreeNumber);
        Assert.Equal(FirstFreeNumber, recovered.GetReference(PdfName.Parent).ReferenceObjectNumber);
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, EntryTypeOf(document, FirstFreeNumber));
        Assert.Single(document.Pages);
    }

    [Fact]
    public void A_malformed_directly_self_nesting_object_is_refused_before_anything_is_written()
    {
        // Not a reference cycle — the dictionary *contains itself directly*, which no parser can
        // produce but a caller assembling an object graph can. PdfWriter.WriteObject recurses
        // through direct values, so serialising this would never terminate.
        var looping = new PdfDictionary();
        looping.Set(PdfName.Kids, looping);

        List<(int objectNumber, PdfObject obj)> objects = BuildDocument();
        objects.Add((FirstFreeNumber, looping));

        using var buffer = new MemoryStream();
        var thrown = Assert.Throws<InvalidOperationException>(() =>
            PdfCompressedFileWriter.Write(buffer, new PdfVersion(1, 7), objects, Trailer()));

        // The guard's own evidence: it names the offending object, and it fired before any byte
        // reached the caller's stream, so there is no truncated file to clean up.
        Assert.Contains(FirstFreeNumber.ToString(CultureInfo.InvariantCulture), thrown.Message);
        Assert.Equal(0, buffer.Length);
    }

    /// <summary>
    /// The <b>other</b> half of <c>EnsureDirectStructureIsFinite</c>'s two guards — a direct
    /// structure that is exponentially wide without ever nesting deep enough to trip the depth cap.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing reached this before.</b> The self-nesting test above shares this method's
    /// only other coverage, and it is a linear cycle: depth grows by one node per level, so it
    /// trips <c>PdfLimits.MaxObjectGraphDepth</c> (256) at node 257 — thousands of nodes short of
    /// <c>MaxDirectNodesPerObject</c> (1,000,000). The budget's own doc comment names the shape
    /// that needs it — "a graph that shares one sub-object twice per level is exponential in its
    /// depth without ever exceeding it" — and nothing built that shape.</para>
    /// <para>Twenty levels, each holding the previous level's dictionary <b>twice</b> (not by
    /// reference — <c>PdfReference</c> is a leaf to this walk, exactly as it is to
    /// <c>PdfWriter</c>), is a complete binary tree of depth 20: about two million walked nodes
    /// from 21 distinct dictionary instances, comfortably over the budget and nowhere near the
    /// depth cap.</para>
    /// </remarks>
    [Fact]
    public void AnExponentiallyWideDirectStructure_IsRefusedByTheNodeBudget_NotTheDepthCap()
    {
        var shared = new PdfDictionary();
        for (int level = 0; level < 20; level++)
        {
            var next = new PdfDictionary();
            next.Set(new PdfName("A"), shared);
            next.Set(new PdfName("B"), shared);
            shared = next;
        }

        List<(int objectNumber, PdfObject obj)> objects = BuildDocument();
        objects.Add((FirstFreeNumber, shared));

        using var buffer = new MemoryStream();
        var thrown = Assert.Throws<PdfException>(() =>
            PdfCompressedFileWriter.Write(buffer, new PdfVersion(1, 7), objects, Trailer()));

        Assert.Contains(FirstFreeNumber.ToString(CultureInfo.InvariantCulture), thrown.Message);
        Assert.Contains("direct nodes", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("levels", thrown.Message, StringComparison.Ordinal);
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void An_object_with_a_nonzero_generation_stays_top_level()
    {
        // §7.5.8.3: a type-2 entry has no generation field, so a compressed object's generation is
        // implicitly 0. Packing a gen>0 object would silently renumber it to gen 0.
        var revised = new PdfDictionary { GenerationNumber = 3 };
        revised.Set(PdfName.Title, new PdfString("second generation"));

        List<(int objectNumber, PdfObject obj)> objects = BuildDocument(fillerObjects: 10);
        objects.Add((FirstFreeNumber + 10, revised));

        byte[] bytes = WriteCompressed(objects, Trailer());

        using PdfDocument document = Open(bytes);
        CrossReferenceEntry entry = document.CrossReferenceTable.GetEntry(FirstFreeNumber + 10);

        Assert.Equal(CrossReferenceEntry.EntryType.Normal, entry.Type);
        Assert.Equal(3, entry.GenerationNumber);
        Assert.Contains(" 3 obj", Encoding.ASCII.GetString(bytes));

        // The neighbouring gen-0 objects still pack, so this is an exclusion rather than the
        // writer having given up on packing altogether.
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, EntryTypeOf(document, FirstFreeNumber));
    }

    [Fact]
    public void An_object_too_large_for_the_configured_ceiling_is_written_top_level_and_reported()
    {
        var big = new PdfDictionary();
        big.Set(PdfName.Title, new PdfString(new string('x', 8192)));

        List<(int objectNumber, PdfObject obj)> objects = BuildDocument(fillerObjects: 4);
        objects.Add((FirstFreeNumber + 4, big));

        // The floor is 1 KiB, so this ceiling is honoured exactly and the 8 KiB object cannot fit.
        byte[] bytes = WriteCompressed(objects, Trailer(),
            new PdfCompressedWriteOptions { MaxObjectStreamBytes = 1024 });

        Assert.Contains("exceeds the 1024-byte object-stream ceiling",
            PdfCompressedFileWriter.LastWriteWarning);

        using PdfDocument document = Open(bytes);
        Assert.Equal(CrossReferenceEntry.EntryType.Normal, EntryTypeOf(document, FirstFreeNumber + 4));
        var recovered = (PdfDictionary)document.Parser.GetObject(FirstFreeNumber + 4);
        Assert.Equal(8192, recovered.GetString(PdfName.Title).GetText().Length);
    }

    [Fact]
    public void An_empty_object_list_still_produces_a_file_the_parser_accepts()
    {
        byte[] bytes = WriteCompressed(new List<(int, PdfObject)>(), Trailer());

        // No objects means no object stream — but the cross-reference stream, its own entry and
        // the free-list head must still be there, or `startxref` points at nothing.
        Assert.Contains("/XRef", Encoding.ASCII.GetString(bytes));

        using PdfDocument document = Open(bytes);
        Assert.Equal(CrossReferenceEntry.EntryType.Free,
            document.CrossReferenceTable.GetEntry(0).Type);
        Assert.Empty(ObjectStreamsIn(document));
    }

    // ===============================================================
    //  6. Encrypted documents
    // ===============================================================

    [Fact]
    public void The_encryption_dictionary_is_never_packed_into_an_object_stream()
    {
        // §7.5.7: a reader must parse /Encrypt before it can decrypt anything — including the
        // object stream it would otherwise have to inflate to find it.
        const int EncryptNumber = FirstFreeNumber + 10;

        var encrypt = new PdfDictionary();
        encrypt.Set(PdfName.Filter, PdfName.Standard);
        encrypt.Set(PdfName.V, new PdfNumber(2));
        encrypt.Set(PdfName.R, new PdfNumber(3));
        encrypt.Set(PdfName.P, new PdfNumber(-1));

        List<(int objectNumber, PdfObject obj)> objects = BuildDocument(fillerObjects: 10);
        objects.Add((EncryptNumber, encrypt));

        PdfDictionary trailer = Trailer();
        trailer.Set(PdfName.Encrypt, new PdfReference(EncryptNumber, 0));

        byte[] bytes = WriteCompressed(objects, trailer);

        // The writer says out loud that it has no encryptor rather than emitting a file whose
        // objects a reader will try to decrypt.
        Assert.Contains("/Encrypt", PdfCompressedFileWriter.LastWriteWarning);
        Assert.Contains("written in the clear", PdfCompressedFileWriter.LastWriteWarning);

        // Read the cross-reference stream directly: PdfDocument.Open on a document declaring
        // /Encrypt does not load the catalog, but the xref entries are still authoritative.
        var parsed = new PdfParser(new MemoryStream(bytes));
        parsed.ParseCrossReferences();

        Assert.Equal(CrossReferenceEntry.EntryType.Normal,
            parsed.CrossReferenceTable.GetEntry(EncryptNumber).Type);

        // The neighbours still pack — the exclusion is the /Encrypt object, not the document.
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed,
            parsed.CrossReferenceTable.GetEntry(FirstFreeNumber).Type);

        // /Encrypt is carried into the cross-reference stream dictionary, which is the trailer.
        Assert.Equal(EncryptNumber,
            ((PdfReference)parsed.Trailer[PdfName.Encrypt]).ReferenceObjectNumber);
    }

    // ===============================================================
    //  Diagnostics
    // ===============================================================

    [Fact]
    public void LastWriteWarning_is_cleared_by_the_next_clean_write()
    {
        WriteCompressed(BuildDocument(), Trailer(), version: new PdfVersion(1, 3));
        Assert.NotNull(PdfCompressedFileWriter.LastWriteWarning);

        WriteCompressed(BuildDocument(), Trailer(), version: new PdfVersion(1, 7));
        Assert.Null(PdfCompressedFileWriter.LastWriteWarning);
    }

    [Fact]
    public void A_duplicate_object_number_collapses_to_the_last_occurrence_and_is_reported()
    {
        List<(int objectNumber, PdfObject obj)> objects = BuildDocument();

        var first = new PdfDictionary();
        first.Set(PdfName.Title, new PdfString("first"));
        var second = new PdfDictionary();
        second.Set(PdfName.Title, new PdfString("second"));

        objects.Add((FirstFreeNumber, first));
        objects.Add((FirstFreeNumber, second));

        byte[] bytes = WriteCompressed(objects, Trailer());

        Assert.Contains("appears more than once", PdfCompressedFileWriter.LastWriteWarning);

        using PdfDocument document = Open(bytes);
        var recovered = (PdfDictionary)document.Parser.GetObject(FirstFreeNumber);
        Assert.Equal("second", recovered.GetString(PdfName.Title).GetText());
    }

    [Fact]
    public void A_missing_trailer_ID_is_synthesised_deterministically()
    {
        byte[] first = WriteCompressed(BuildDocument(fillerObjects: 5), Trailer(withId: false));
        byte[] second = WriteCompressed(BuildDocument(fillerObjects: 5), Trailer(withId: false));

        // Byte-identical output is the property that matters: an /ID seeded from a clock or a
        // Random would make two runs over the same objects differ.
        Assert.Equal(first, second);

        using PdfDocument document = Open(first);
        var id = (PdfArray)document.Trailer[PdfName.ID];
        Assert.Equal(2, id.Count);
        Assert.Equal(16, ((PdfString)id[0]).RawBytes.Length);
        Assert.Equal(16, ((PdfString)id[1]).RawBytes.Length);

        // …and it is not the all-zero placeholder a failed digest would leave behind.
        Assert.Contains(((PdfString)id[0]).RawBytes, b => b != 0);
    }
}
