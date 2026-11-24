// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Which copy the recovery scan serves when an incrementally updated file holds several. ISO 32000-2
/// §7.5.6: changes "shall be appended to the end of the file", and a reader "shall build its
/// cross-reference information in such a way that the most recent copy of each object shall be the
/// one accessed". Once the cross-reference data is unusable, position in the file is the only
/// evidence of recency left — for a top-level object and an object-stream member alike, and for the
/// trailer entries too.
/// </summary>
/// <remarks>
/// <para><b>Every fixture is read twice.</b> The intact file is parsed through its own
/// cross-reference chain first — the file's word on which copy is current — and then with its final
/// <c>startxref</c> overwritten, digit for digit, by an offset inside the header, which leaves every
/// other byte where it was and forces the scan. Recovery must agree with the chain.</para>
/// <para>The defect was the order the scan's two stages resolved conflicts in: top-level objects were
/// last-wins, but object-stream members were added first-wins in the order their containers first
/// appeared, and could never displace a top-level copy; the trailer came from the last literal
/// <c>trailer</c> keyword, so a later revision that had only a cross-reference stream was never
/// consulted.</para>
/// </remarks>
public class RecoveryNewestCopyTests
{
    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string Pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";

    // ================================================================
    //  Objects
    // ================================================================

    /// <summary>
    /// The audit's fixture: revision 1 keeps the page in object stream 20 at 100 wide, revision 2
    /// supersedes it in object stream 40 at 300 wide.
    /// </summary>
    [Fact]
    public void Recovery_serves_an_object_stream_member_from_the_newest_revision()
    {
        byte[] intact = PageMovedBetweenObjectStreams();
        Assert.Equal(300d, PageWidth(ParseIntact(intact)));

        byte[] damaged = WithStartxrefIntoTheHeader(intact);
        Assert.Equal(300d, PageWidth(ParseDamaged(damaged)));

        // End to end: the page tree a caller walks reaches the same copy.
        using var document = PdfDocument.Open(new MemoryStream(damaged));
        Assert.Equal(300d, ((PdfNumber)document.Pages[0].MediaBox[2]).DoubleValue);
    }

    /// <summary>
    /// A top-level copy in revision 1 and an object-stream member in revision 2. A member could never
    /// displace a top-level entry, however much newer it was.
    /// </summary>
    [Fact]
    public void Recovery_prefers_a_newer_object_stream_member_over_an_older_top_level_copy()
    {
        byte[] intact = PageMovedFromTopLevelIntoAnObjectStream();
        Assert.Equal(300d, PageWidth(ParseIntact(intact)));

        Assert.Equal(300d, PageWidth(ParseDamaged(WithStartxrefIntoTheHeader(intact))));
    }

    /// <summary>
    /// The honest counterpart: a member in revision 1 and a top-level copy in revision 2. Preferring
    /// object-stream members as such would pass the two tests above and serve 100 here.
    /// </summary>
    [Fact]
    public void Recovery_keeps_a_newer_top_level_copy_over_an_older_object_stream_member()
    {
        byte[] intact = PageMovedFromAnObjectStreamToTopLevel();
        Assert.Equal(300d, PageWidth(ParseIntact(intact)));

        Assert.Equal(300d, PageWidth(ParseDamaged(WithStartxrefIntoTheHeader(intact))));
    }

    /// <summary>
    /// Object 7 is the indirect <c>/Length</c> of revision 1's object stream, so recovery has to read
    /// — and cache — revision 1's copy of it just to index that stream. Revision 2 supersedes object 7
    /// inside object stream 40. Installing the newer entry is not enough while the older object is
    /// still sitting in the cache under the same number.
    /// </summary>
    [Fact]
    public void Recovery_serves_the_newer_copy_of_an_object_it_already_read_to_index_an_older_object_stream()
    {
        byte[] intact = LengthObjectSupersededInALaterObjectStream();
        Assert.Equal(2, MarkerOf(ParseIntact(intact).GetObject(7)));

        PdfParser recovered = ParseDamaged(WithStartxrefIntoTheHeader(intact));

        Assert.Equal(2, MarkerOf(recovered.GetObject(7)));
        // The older stream is still indexed: its members came from the copy of 7 that was current
        // when it was read.
        var catalog = Assert.IsType<PdfDictionary>(recovered.GetObject(1));
        Assert.Equal(PdfName.Catalog, catalog.GetName(PdfName.Type));
    }

    // ================================================================
    //  Trailer
    // ================================================================

    /// <summary>
    /// Revision 1 is classic, so the file's last literal <c>trailer</c> keyword belongs to it;
    /// revision 2 is a cross-reference-stream update that names a new <c>/Info</c>.
    /// </summary>
    [Fact]
    public void Recovery_takes_trailer_entries_from_an_xref_stream_newer_than_the_last_trailer_keyword()
    {
        byte[] intact = XrefStreamUpdateAfterAClassicTrailer();
        PdfParser walked = ParseIntact(intact);
        Assert.Equal(5, InfoObjectNumber(walked));
        Assert.IsType<PdfArray>(walked.Trailer[PdfName.ID]);

        PdfParser recovered = ParseDamaged(WithStartxrefIntoTheHeader(intact));

        Assert.Equal(5, InfoObjectNumber(recovered));
        // An entry only the older trailer carries still fills in, exactly as the /Prev walk merges it.
        Assert.IsType<PdfArray>(recovered.Trailer[PdfName.ID]);
        // The newer dictionary's cross-reference plumbing is not a trailer entry.
        Assert.False(recovered.Trailer.ContainsKey(PdfName.W));
    }

    /// <summary>
    /// The honest counterpart: the cross-reference stream is revision 1 and the <c>trailer</c>
    /// keyword revision 2. Ranking every cross-reference stream above every keyword would pass the
    /// test above and name revision 1's <c>/Info</c> here.
    /// </summary>
    [Fact]
    public void Recovery_keeps_trailer_entries_from_a_trailer_keyword_newer_than_the_xref_stream()
    {
        byte[] intact = ClassicUpdateAfterAnXrefStream();
        Assert.Equal(6, InfoObjectNumber(ParseIntact(intact)));

        Assert.Equal(6, InfoObjectNumber(ParseDamaged(WithStartxrefIntoTheHeader(intact))));
    }

    // ================================================================
    //  /Root, when no trailer dictionary names one
    // ================================================================

    /// <summary>
    /// Two catalogs under two object numbers — a "save as" that renumbered — and no trailer anywhere
    /// names a <c>/Root</c>: revision 1's catalog is top-level object 1, revision 2's is object 9
    /// inside an object stream written later. The one written last is the document's, whatever kind
    /// of entry holds it; a top-level candidate does not outrank every member regardless.
    /// </summary>
    [Fact]
    public void Recovery_names_the_latest_catalog_as_root_whichever_kind_of_entry_holds_it()
    {
        PdfParser parser = ParseDamaged(WithStartxrefIntoTheHeader(RenumberedCatalogIntoAnObjectStream()));

        Assert.Equal(9, RootObjectNumber(parser));
        Assert.Equal(2, MarkerOf(parser.GetObject(9)));
    }

    /// <summary>
    /// The honest counterpart: revision 1's catalog inside an object stream (object 9), revision 2's
    /// top-level (object 1). Preferring members as such would pass the test above and answer 9 here.
    /// </summary>
    [Fact]
    public void Recovery_keeps_a_newer_top_level_catalog_over_an_older_object_stream_member()
    {
        PdfParser parser = ParseDamaged(WithStartxrefIntoTheHeader(RenumberedCatalogToTopLevel()));

        Assert.Equal(1, RootObjectNumber(parser));
        Assert.Equal(2, MarkerOf(parser.GetObject(1)));
    }

    // ================================================================
    //  Every `trailer` keyword, newest first
    // ================================================================

    /// <summary>
    /// The tail was cut off inside the last <c>trailer</c> dictionary, so nothing it names can be
    /// read, and the <c>startxref</c> that survives — revision 1's — points into the header, so the
    /// file can only be recovered. The revision before the cut still names <c>/Root</c> and
    /// <c>/Info</c>, which a reading of the last keyword alone would lose.
    /// </summary>
    /// <remarks>
    /// Truncation alone would not do: with revision 2's <c>startxref</c> gone, the parser finds
    /// revision 1's and reads the file through its chain, which is right and is not recovery.
    /// </remarks>
    [Fact]
    public void Recovery_falls_back_to_an_earlier_trailer_keyword_when_the_last_ones_dictionary_is_unreadable()
    {
        byte[] intact = TwoClassicRevisionsWithDistinctTrailers();
        byte[] damaged = WithStartxrefIntoTheHeader(TruncatedInsideTheLastTrailer(intact));

        PdfParser parser = ParseDamaged(damaged);

        Assert.Equal(1, RootObjectNumber(parser));
        Assert.Equal(4, InfoObjectNumber(parser));
    }

    /// <summary>
    /// One object stream holding two catalogs: the member written later in it ranks higher, for the
    /// same reason a later container does. Two catalogs in one stream is not a shape a producer
    /// writes; this pins the tie-break so the choice cannot depend on table order.
    /// </summary>
    [Fact]
    public void Recovery_prefers_the_later_member_when_one_object_stream_holds_two_catalogs()
    {
        PdfParser parser = ParseDamaged(WithStartxrefIntoTheHeader(TwoCatalogsInOneObjectStream()));

        Assert.Equal(10, RootObjectNumber(parser));
    }

    /// <summary>
    /// The honest counterpart: with every keyword readable, the last one's entries win. Reading them
    /// oldest first would pass the test above and name revision 1's <c>/Info</c> here.
    /// </summary>
    [Fact]
    public void Recovery_lets_the_last_trailer_keyword_outrank_the_earlier_ones()
    {
        byte[] intact = TwoClassicRevisionsWithDistinctTrailers();
        Assert.Equal(6, InfoObjectNumber(ParseIntact(intact)));

        PdfParser recovered = ParseDamaged(WithStartxrefIntoTheHeader(intact));

        Assert.Equal(6, InfoObjectNumber(recovered));
        // …and an entry only the earlier keyword carries still fills in.
        Assert.IsType<PdfArray>(recovered.Trailer[PdfName.ID]);
    }

    /// <summary>
    /// A <c>trailer</c> keyword inside an object — an uncompressed embedded PDF's — is not this
    /// file's trailer, however many keywords are read. The bogus one names an <c>/Info</c> the real
    /// trailer, read last, does not carry; it must not fill in.
    /// </summary>
    [Fact]
    public void Recovery_ignores_a_trailer_keyword_inside_an_object()
    {
        PdfParser parser = ParseDamaged(WithStartxrefIntoTheHeader(BogusTrailerKeyword(insideAnObject: true)));

        Assert.Equal(1, RootObjectNumber(parser));
        Assert.Null(parser.Trailer[PdfName.Info]);
    }

    /// <summary>A word that merely ends in <c>trailer</c> is not the keyword.</summary>
    [Fact]
    public void Recovery_ignores_a_word_ending_in_trailer()
    {
        PdfParser parser = ParseDamaged(WithStartxrefIntoTheHeader(BogusTrailerKeyword(insideAnObject: false)));

        Assert.Equal(1, RootObjectNumber(parser));
        Assert.Null(parser.Trailer[PdfName.Info]);
    }

    /// <summary>
    /// Each keyword costs a dictionary parse, so the number consulted is capped: with one more
    /// keyword than <see cref="PdfLimits.MaxRecoveredTrailerDictionaries"/>, the newest that many are
    /// read and the oldest is not.
    /// </summary>
    [Fact]
    public void Adversarial_run_of_trailer_keywords_is_read_only_up_to_the_cap()
    {
        int keywords = PdfLimits.MaxRecoveredTrailerDictionaries + 1;
        byte[] damaged = WithStartxrefIntoTheHeader(ManyTrailerKeywords(keywords));

        PdfParser parser = ParseDamaged(damaged);

        // Keyword k carries /Tk. The newest is keyword `keywords`; the cap reaches back to keyword 2.
        Assert.True(parser.Trailer.ContainsKey(new PdfName("T" + Num(keywords))));
        Assert.True(parser.Trailer.ContainsKey(new PdfName("T2")));
        Assert.False(parser.Trailer.ContainsKey(new PdfName("T1")));
    }

    // ================================================================
    //  The recovery entry budget still bounds object-stream members
    // ================================================================

    /// <summary>
    /// A member count comes from the file, so every member recovery indexes is charged against the
    /// parser's cross-reference budget. Choosing the newest copy means collecting before installing;
    /// the charge has to stay on the collection.
    /// </summary>
    /// <remarks>
    /// The scan charges the four top-level headers it takes first (objects 20, 21, 40 and 41), so a
    /// budget of six leaves two for the members.
    /// </remarks>
    [Fact]
    public void Adversarial_object_stream_members_recovered_by_scanning_are_charged_against_the_entry_budget()
    {
        byte[] damaged = WithStartxrefIntoTheHeader(PageMovedBetweenObjectStreams());
        var parser = new PdfParser(new MemoryStream(damaged)) { MaxCrossReferenceEntries = 6 };
        parser.ParseHeader();
        parser.ParseCrossReferences();

        Assert.Contains("recovery stopped at the 6-entry cross-reference limit", PdfParser.LastXrefSectionError);

        // The two members charged before the refusal are indexed; the third never was.
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, parser.CrossReferenceTable.GetEntry(1).Type);
        Assert.Equal(CrossReferenceEntry.EntryType.Compressed, parser.CrossReferenceTable.GetEntry(2).Type);
        Assert.Null(parser.CrossReferenceTable.GetEntry(3));
    }

    // ================================================================
    //  Reading
    // ================================================================

    private static PdfParser ParseIntact(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        Assert.Null(PdfParser.LastXrefSectionError);
        return parser;
    }

    /// <summary>Parses a file from <see cref="WithStartxrefIntoTheHeader"/>, proving the scan ran.</summary>
    private static PdfParser ParseDamaged(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        Assert.Contains("xref section at 3 was skipped", PdfParser.LastXrefSectionError);
        return parser;
    }

    private static double PageWidth(PdfParser parser)
    {
        var page = Assert.IsType<PdfDictionary>(parser.GetObject(3));
        var box = Assert.IsType<PdfArray>(page[PdfName.MediaBox]);
        return Assert.IsType<PdfNumber>(box[2]).DoubleValue;
    }

    private static long MarkerOf(PdfObject obj)
    {
        var dict = Assert.IsType<PdfDictionary>(obj);
        return Assert.IsType<PdfNumber>(dict[new PdfName("Marker")]).LongValue;
    }

    private static int InfoObjectNumber(PdfParser parser) =>
        Assert.IsType<PdfReference>(parser.Trailer[PdfName.Info]).ReferenceObjectNumber;

    private static int RootObjectNumber(PdfParser parser) =>
        Assert.IsType<PdfReference>(parser.Trailer[PdfName.Root]).ReferenceObjectNumber;

    // ================================================================
    //  Fixtures
    // ================================================================

    /// <summary>
    /// The same file with the operand of its final <c>startxref</c> overwritten, digit for digit, by
    /// 3 — an offset inside the <c>%PDF-</c> header. Nothing else moves.
    /// </summary>
    private static byte[] WithStartxrefIntoTheHeader(byte[] pdf)
    {
        byte[] damaged = (byte[])pdf.Clone();
        // One char per byte, so an index into the text is an index into the bytes.
        string text = Encoding.ASCII.GetString(damaged);
        int start = text.LastIndexOf("startxref", StringComparison.Ordinal) + "startxref".Length;
        while (damaged[start] == (byte)'\n' || damaged[start] == (byte)'\r') start++;
        int end = start;
        while (end < damaged.Length && damaged[end] >= (byte)'0' && damaged[end] <= (byte)'9') end++;
        Assert.True(end > start, "the fixture carries no startxref operand");

        for (int i = start; i < end; i++) damaged[i] = (byte)'0';
        damaged[end - 1] = (byte)'3';
        return damaged;
    }

    private static string PageOfWidth(int width) =>
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + Num(width) + " " + Num(width) + "] >>";

    private static string Num(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Revision 1: catalog, page tree and a 100-wide page in object stream 20. Revision 2: the page, 300 wide, in object stream 40.</summary>
    private static byte[] PageMovedBetweenObjectStreams()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        ObjectStream(pdf, 20, null, (1, Catalog), (2, Pages), (3, PageOfWidth(100)));
        long xref1 = pdf.Position;
        XrefStream(pdf, 21, "/Size 22 /Root 1 0 R",
            (0, 0, 0, 65535), (1, 2, 20, 0), (2, 2, 20, 1), (3, 2, 20, 2),
            (20, 1, pdf.Positions[20], 0), (21, 1, xref1, 0));
        pdf.Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        ObjectStream(pdf, 40, null, (3, PageOfWidth(300)));
        long xref2 = pdf.Position;
        XrefStream(pdf, 41, "/Size 42 /Root 1 0 R /Prev " + Num(xref1),
            (3, 2, 40, 0), (40, 1, pdf.Positions[40], 0), (41, 1, xref2, 0));
        pdf.Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>Revision 1: all three objects top-level, page 100 wide. Revision 2: the page, 300 wide, in object stream 40.</summary>
    private static byte[] PageMovedFromTopLevelIntoAnObjectStream()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        pdf.Obj(1, Catalog).Obj(2, Pages).Obj(3, PageOfWidth(100));
        long xref1 = pdf.Position;
        XrefStream(pdf, 21, "/Size 22 /Root 1 0 R",
            (0, 0, 0, 65535), (1, 1, pdf.Positions[1], 0), (2, 1, pdf.Positions[2], 0),
            (3, 1, pdf.Positions[3], 0), (21, 1, xref1, 0));
        pdf.Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        ObjectStream(pdf, 40, null, (3, PageOfWidth(300)));
        long xref2 = pdf.Position;
        XrefStream(pdf, 41, "/Size 42 /Root 1 0 R /Prev " + Num(xref1),
            (3, 2, 40, 0), (40, 1, pdf.Positions[40], 0), (41, 1, xref2, 0));
        pdf.Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>Revision 1: all three objects in object stream 20, page 100 wide. Revision 2: the page, 300 wide, top-level.</summary>
    private static byte[] PageMovedFromAnObjectStreamToTopLevel()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        ObjectStream(pdf, 20, null, (1, Catalog), (2, Pages), (3, PageOfWidth(100)));
        long xref1 = pdf.Position;
        XrefStream(pdf, 21, "/Size 22 /Root 1 0 R",
            (0, 0, 0, 65535), (1, 2, 20, 0), (2, 2, 20, 1), (3, 2, 20, 2),
            (20, 1, pdf.Positions[20], 0), (21, 1, xref1, 0));
        pdf.Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        pdf.Obj(3, PageOfWidth(300));
        long xref2 = pdf.Position;
        XrefStream(pdf, 41, "/Size 42 /Root 1 0 R /Prev " + Num(xref1),
            (3, 1, pdf.Positions[3], 0), (41, 1, xref2, 0));
        pdf.Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>
    /// Revision 1: object stream 20, whose <c>/Length</c> is object 7, holding catalog, page tree and
    /// page. Revision 2: object 7 superseded by <c>&lt;&lt; /Marker 2 &gt;&gt;</c> in object stream 40.
    /// </summary>
    private static byte[] LengthObjectSupersededInALaterObjectStream()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        int length = ObjectStream(pdf, 20, "7 0 R", (1, Catalog), (2, Pages), (3, PageOfWidth(100)));
        pdf.Obj(7, Num(length));
        long xref1 = pdf.Position;
        XrefStream(pdf, 21, "/Size 22 /Root 1 0 R",
            (0, 0, 0, 65535), (1, 2, 20, 0), (2, 2, 20, 1), (3, 2, 20, 2),
            (7, 1, pdf.Positions[7], 0), (20, 1, pdf.Positions[20], 0), (21, 1, xref1, 0));
        pdf.Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        ObjectStream(pdf, 40, null, (7, "<< /Marker 2 >>"));
        long xref2 = pdf.Position;
        XrefStream(pdf, 41, "/Size 42 /Root 1 0 R /Prev " + Num(xref1),
            (7, 2, 40, 0), (40, 1, pdf.Positions[40], 0), (41, 1, xref2, 0));
        pdf.Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    private const string CatalogOfWidth1 = "<< /Type /Catalog /Pages 2 0 R /Marker 1 >>";
    private const string CatalogOfWidth2 = "<< /Type /Catalog /Pages 2 0 R /Marker 2 >>";

    /// <summary>
    /// No trailer anywhere names a <c>/Root</c>. Revision 1: catalog 1 top-level (<c>/Marker 1</c>),
    /// page tree and page top-level. Revision 2: catalog 9 (<c>/Marker 2</c>) in object stream 40.
    /// </summary>
    private static byte[] RenumberedCatalogIntoAnObjectStream()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        pdf.Obj(1, CatalogOfWidth1).Obj(2, Pages).Obj(3, PageOfWidth(100));
        long xref1 = pdf.Position;
        XrefStream(pdf, 21, "/Size 22",
            (0, 0, 0, 65535), (1, 1, pdf.Positions[1], 0), (2, 1, pdf.Positions[2], 0),
            (3, 1, pdf.Positions[3], 0), (21, 1, xref1, 0));
        pdf.Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        ObjectStream(pdf, 40, null, (9, CatalogOfWidth2));
        long xref2 = pdf.Position;
        XrefStream(pdf, 41, "/Size 42 /Prev " + Num(xref1),
            (9, 2, 40, 0), (40, 1, pdf.Positions[40], 0), (41, 1, xref2, 0));
        pdf.Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>
    /// The mirror: revision 1's catalog 9 (<c>/Marker 1</c>) in object stream 20 with the page tree
    /// and page; revision 2's catalog 1 (<c>/Marker 2</c>) top-level.
    /// </summary>
    private static byte[] RenumberedCatalogToTopLevel()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        ObjectStream(pdf, 20, null, (9, CatalogOfWidth1), (2, Pages), (3, PageOfWidth(100)));
        long xref1 = pdf.Position;
        XrefStream(pdf, 21, "/Size 22",
            (0, 0, 0, 65535), (9, 2, 20, 0), (2, 2, 20, 1), (3, 2, 20, 2),
            (20, 1, pdf.Positions[20], 0), (21, 1, xref1, 0));
        pdf.Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        pdf.Obj(1, CatalogOfWidth2);
        long xref2 = pdf.Position;
        XrefStream(pdf, 41, "/Size 42 /Prev " + Num(xref1),
            (1, 1, pdf.Positions[1], 0), (41, 1, xref2, 0));
        pdf.Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>
    /// No trailer names a <c>/Root</c>; object stream 20 holds catalog 9 (<c>/Marker 1</c>) at index 0
    /// and catalog 10 (<c>/Marker 2</c>) at index 1, with the page tree and page between them.
    /// </summary>
    private static byte[] TwoCatalogsInOneObjectStream()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        ObjectStream(pdf, 20, null, (9, CatalogOfWidth1), (2, Pages), (3, PageOfWidth(100)), (10, CatalogOfWidth2));
        long xref1 = pdf.Position;
        XrefStream(pdf, 21, "/Size 22",
            (0, 0, 0, 65535), (9, 2, 20, 0), (2, 2, 20, 1), (3, 2, 20, 2), (10, 2, 20, 3),
            (20, 1, pdf.Positions[20], 0), (21, 1, xref1, 0));
        pdf.Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>
    /// Two classic revisions with distinct trailers: revision 1's names <c>/Info 4 0 R</c> and an
    /// <c>/ID</c>; revision 2's names <c>/Info 6 0 R</c> and — contrary to §7.5.6, so that the merge
    /// is observable — no <c>/ID</c>.
    /// </summary>
    private static byte[] TwoClassicRevisionsWithDistinctTrailers()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.4\n");
        pdf.Obj(1, Catalog).Obj(2, Pages).Obj(3, PageOfWidth(100)).Obj(4, "<< /Title (revision 1) >>");
        long xref1 = pdf.Position;
        pdf.Ascii("xref\n0 5\n0000000000 65535 f \n");
        for (int n = 1; n <= 4; n++)
            pdf.Ascii(pdf.Positions[n].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Ascii("trailer\n<< /Size 5 /Root 1 0 R /Info 4 0 R /ID " + FileIdentifier + " >>\n")
           .Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        pdf.Obj(6, "<< /Title (revision 2) >>");
        long xref2 = pdf.Position;
        pdf.Ascii("xref\n6 1\n" + pdf.Positions[6].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n")
           .Ascii("trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R /Prev " + Num(xref1) + " >>\n")
           .Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>The file cut off inside its last <c>trailer</c> dictionary, after <c>/Info 6</c>.</summary>
    private static byte[] TruncatedInsideTheLastTrailer(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        int cut = text.LastIndexOf("/Info 6", StringComparison.Ordinal) + "/Info 6".Length;
        var truncated = new byte[cut];
        Buffer.BlockCopy(pdf, 0, truncated, 0, cut);
        return truncated;
    }

    /// <summary>
    /// Objects 1–3, then a bogus <c>trailer</c> keyword naming <c>/Info 3 0 R</c> — inside an
    /// uncompressed embedded-file stream (object 8), or as the tail of the word <c>Xtrailer</c> —
    /// then the real trailer, which names no <c>/Info</c>, and a <c>startxref</c>.
    /// </summary>
    private static byte[] BogusTrailerKeyword(bool insideAnObject)
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.4\n");
        pdf.Obj(1, Catalog).Obj(2, Pages).Obj(3, PageOfWidth(100));

        const string Bogus = "trailer\n<< /Info 3 0 R >>\n";
        if (insideAnObject)
        {
            pdf.Positions[8] = pdf.Position;
            pdf.Ascii("8 0 obj\n<< /Type /EmbeddedFile /Length " + Num(Bogus.Length) + " >>\nstream\n" + Bogus +
                      "\nendstream\nendobj\n");
        }
        else
        {
            pdf.Ascii("X" + Bogus);
        }

        long xref = pdf.Position;
        pdf.Ascii("xref\n0 4\n0000000000 65535 f \n");
        for (int n = 1; n <= 3; n++)
            pdf.Ascii(pdf.Positions[n].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Ascii("trailer\n<< /Size 9 /Root 1 0 R >>\nstartxref\n" + Num(xref) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>
    /// Objects 1–3 followed by <paramref name="count"/> <c>trailer</c> dictionaries, keyword k
    /// carrying <c>/Tk k</c>, oldest first, and a <c>startxref</c> naming the last of them.
    /// </summary>
    private static byte[] ManyTrailerKeywords(int count)
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.4\n");
        pdf.Obj(1, Catalog).Obj(2, Pages).Obj(3, PageOfWidth(100));
        long xref = pdf.Position;
        pdf.Ascii("xref\n0 4\n0000000000 65535 f \n");
        for (int n = 1; n <= 3; n++)
            pdf.Ascii(pdf.Positions[n].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        for (int k = 1; k <= count; k++)
            pdf.Ascii("trailer\n<< /Size 4 /Root 1 0 R /T" + Num(k) + " " + Num(k) + " >>\n");
        pdf.Ascii("startxref\n" + Num(xref) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    private const string FileIdentifier =
        "[<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]";

    /// <summary>
    /// Revision 1: classic, trailer <c>/Info 4 0 R</c> and an <c>/ID</c>. Revision 2: a
    /// cross-reference-stream update naming <c>/Info 5 0 R</c> and — contrary to §7.5.6, so that the
    /// merge is observable — no <c>/ID</c>.
    /// </summary>
    private static byte[] XrefStreamUpdateAfterAClassicTrailer()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        pdf.Obj(1, Catalog).Obj(2, Pages).Obj(3, PageOfWidth(100)).Obj(4, "<< /Title (revision 1) >>");
        long xref1 = pdf.Position;
        pdf.Ascii("xref\n0 5\n0000000000 65535 f \n");
        for (int n = 1; n <= 4; n++)
            pdf.Ascii(pdf.Positions[n].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        pdf.Ascii("trailer\n<< /Size 5 /Root 1 0 R /Info 4 0 R /ID " + FileIdentifier + " >>\n")
           .Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        pdf.Obj(5, "<< /Title (revision 2) >>");
        long xref2 = pdf.Position;
        XrefStream(pdf, 6, "/Size 7 /Root 1 0 R /Info 5 0 R /Prev " + Num(xref1),
            (5, 1, pdf.Positions[5], 0), (6, 1, xref2, 0));
        pdf.Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>
    /// Revision 1: a cross-reference-stream file naming <c>/Info 4 0 R</c>. Revision 2: a classic
    /// update whose <c>trailer</c> names <c>/Info 6 0 R</c>.
    /// </summary>
    private static byte[] ClassicUpdateAfterAnXrefStream()
    {
        var pdf = new PdfBytes();
        pdf.Ascii("%PDF-1.5\n");
        pdf.Obj(1, Catalog).Obj(2, Pages).Obj(3, PageOfWidth(100)).Obj(4, "<< /Title (revision 1) >>");
        long xref1 = pdf.Position;
        XrefStream(pdf, 5, "/Size 6 /Root 1 0 R /Info 4 0 R",
            (0, 0, 0, 65535), (1, 1, pdf.Positions[1], 0), (2, 1, pdf.Positions[2], 0),
            (3, 1, pdf.Positions[3], 0), (4, 1, pdf.Positions[4], 0), (5, 1, xref1, 0));
        pdf.Ascii("startxref\n" + Num(xref1) + "\n%%EOF\n");

        pdf.Obj(6, "<< /Title (revision 2) >>");
        long xref2 = pdf.Position;
        pdf.Ascii("xref\n6 1\n" + pdf.Positions[6].ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n")
           .Ascii("trailer\n<< /Size 7 /Root 1 0 R /Info 6 0 R /Prev " + Num(xref1) + " >>\n")
           .Ascii("startxref\n" + Num(xref2) + "\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>A PDF assembled byte by byte, recording where each object's header starts.</summary>
    private sealed class PdfBytes
    {
        private readonly MemoryStream _ms = new MemoryStream();

        internal readonly Dictionary<int, long> Positions = new Dictionary<int, long>();

        internal long Position => _ms.Position;

        internal PdfBytes Ascii(string text)
        {
            byte[] bytes = Encoding.ASCII.GetBytes(text);
            _ms.Write(bytes, 0, bytes.Length);
            return this;
        }

        internal PdfBytes Raw(byte[] bytes)
        {
            _ms.Write(bytes, 0, bytes.Length);
            return this;
        }

        internal PdfBytes Obj(int number, string body)
        {
            Positions[number] = Position;
            return Ascii(Num(number) + " 0 obj\n" + body + "\nendobj\n");
        }

        internal byte[] ToArray() => _ms.ToArray();
    }

    /// <summary>
    /// An uncompressed object stream (§7.5.7) holding <paramref name="members"/> in order. With
    /// <paramref name="lengthReference"/> the dictionary's <c>/Length</c> is that reference, and the
    /// caller writes the object it names; the decoded length is returned either way.
    /// </summary>
    private static int ObjectStream(PdfBytes pdf, int number, string lengthReference,
        params (int objectNumber, string body)[] members)
    {
        var index = new StringBuilder();
        var bodies = new StringBuilder();
        foreach (var (objectNumber, body) in members)
        {
            index.Append(Num(objectNumber)).Append(' ').Append(Num(bodies.Length)).Append(' ');
            bodies.Append(body).Append('\n');
        }

        string data = index.ToString() + bodies;
        pdf.Positions[number] = pdf.Position;
        pdf.Ascii(Num(number) + " 0 obj\n<< /Type /ObjStm /N " + Num(members.Length) + " /First " +
                  Num(index.Length) + " /Length " + (lengthReference ?? Num(data.Length)) +
                  " >>\nstream\n" + data + "\nendstream\nendobj\n");
        return data.Length;
    }

    /// <summary>
    /// One uncompressed cross-reference stream, <c>/W [1 4 2]</c>, with a one-entry <c>/Index</c>
    /// subsection per row so the rows need not be contiguous.
    /// </summary>
    private static void XrefStream(PdfBytes pdf, int number, string trailerEntries,
        params (int objectNumber, byte type, long field2, int field3)[] rows)
    {
        var index = new StringBuilder();
        var data = new byte[rows.Length * 7];
        for (int i = 0; i < rows.Length; i++)
        {
            var (objectNumber, type, field2, field3) = rows[i];
            index.Append(Num(objectNumber)).Append(" 1 ");
            int at = i * 7;
            data[at] = type;
            data[at + 1] = (byte)(field2 >> 24);
            data[at + 2] = (byte)(field2 >> 16);
            data[at + 3] = (byte)(field2 >> 8);
            data[at + 4] = (byte)field2;
            data[at + 5] = (byte)(field3 >> 8);
            data[at + 6] = (byte)field3;
        }

        pdf.Positions[number] = pdf.Position;
        pdf.Ascii(Num(number) + " 0 obj\n<< /Type /XRef " + trailerEntries + " /W [1 4 2] /Index [" +
                  index.ToString().TrimEnd() + "] /Length " + Num(data.Length) + " >>\nstream\n")
           .Raw(data)
           .Ascii("\nendstream\nendobj\n");
    }
}
