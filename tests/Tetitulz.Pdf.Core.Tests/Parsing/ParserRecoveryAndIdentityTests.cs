// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// Five ways the parser used to answer a question it could not actually answer, each of them
/// indistinguishable from a healthy read:
///
/// <list type="number">
///   <item>a stream whose <c>/Length</c> is unresolvable and whose <c>endstream</c> is not found
///   materialises as zero bytes, with no diagnostic;</item>
///   <item>the recovery scan matches only a literal space before <c>obj</c>, so a file written
///   <c>1 0\nobj</c> — legal per §7.3.10 — recovers nothing at all;</item>
///   <item>the recovery scan looks only for the <c>trailer</c> keyword and never indexes an
///   <c>/ObjStm</c>, so no PDF 1.5+ file can be recovered;</item>
///   <item>an <c>/ObjStm</c>'s own index object numbers are parsed and discarded, so a shifted
///   index returns a different object under the requested number;</item>
///   <item><c>GetObject</c> never checks that the <c>N G obj</c> header at the cross-reference
///   offset names the object that was asked for.</item>
/// </list>
///
/// <para>Every assertion below is on a parsed object, a resolved object number or the reported
/// reason — never on "it opened" or "it did not throw".</para>
/// </summary>
public class ParserRecoveryAndIdentityTests
{
    private const string CatalogBody = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string PagesBody   = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
    private const string PageBody    = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>";

    private static PdfParser ParserOver(byte[] pdf)
    {
        var parser = new PdfParser(new MemoryStream(pdf));
        parser.ParseHeader();
        parser.ParseCrossReferences();
        return parser;
    }

    // ================================================================
    //  1. An unresolvable /Length with no endstream is REPORTED
    // ================================================================

    /// <summary>
    /// <c>/Length</c> names a missing object and the file contains no <c>endstream</c> keyword, so
    /// the scan window closes empty and the stream is read as zero bytes. The zero-byte answer is
    /// the one outcome guaranteed to be wrong, and it used to be reported as a success — a 9 MB
    /// image whose <c>/Length</c> pointed at a damaged xref rendered as nothing while
    /// <c>GetDecodedData()</c> returned an empty array.
    /// </summary>
    [Fact]
    public void GetObject_UnresolvableLengthAndNoEndstream_ReportsTheEmptyRead()
    {
        byte[] pdf = BuildPdfWithStream("/Length 99 0 R", "HELLO-PAYLOAD", writeEndstream: false);
        var parser = ParserOver(pdf);

        var stream = Assert.IsType<PdfStream>(parser.GetObject(5));
        Assert.Empty(stream.RawData);

        string reason = PdfParser.LastObjectParseError;
        Assert.NotNull(reason);
        Assert.Contains("obj 5", reason);
        Assert.Contains("endstream", reason);
        Assert.Contains("read as empty", reason);
    }

    /// <summary>
    /// The symmetric case: a <b>declared</b> length that is demonstrably wrong (no
    /// <c>endstream</c> sits at it) and a scan that finds no boundary either. The known-wrong
    /// number is still used — there is nothing better to use — but the caller is told so.
    /// </summary>
    [Fact]
    public void GetObject_WrongDeclaredLengthAndNoEndstream_ReportsTheUnverifiedLength()
    {
        byte[] pdf = BuildPdfWithStream("/Length 4", "HELLO-PAYLOAD", writeEndstream: false);
        var parser = ParserOver(pdf);

        var stream = Assert.IsType<PdfStream>(parser.GetObject(5));
        Assert.Equal(4, stream.RawData.Length);

        string reason = PdfParser.LastObjectParseError;
        Assert.NotNull(reason);
        Assert.Contains("obj 5", reason);
        Assert.Contains("unverified", reason);
    }

    /// <summary>
    /// The honest negative for both of the above: a stream whose declared length is right reports
    /// nothing. Without this, setting the slot unconditionally would satisfy the two tests above.
    /// </summary>
    [Fact]
    public void GetObject_CorrectDeclaredLength_ReportsNothing()
    {
        byte[] pdf = BuildPdfWithStream("/Length 13", "HELLO-PAYLOAD", writeEndstream: true);
        var parser = ParserOver(pdf);

        var stream = Assert.IsType<PdfStream>(parser.GetObject(5));
        Assert.Equal("HELLO-PAYLOAD", Encoding.ASCII.GetString(stream.RawData));
        Assert.Null(PdfParser.LastObjectParseError);
    }

    // ================================================================
    //  2. Recovery accepts any §7.3.10 white-space before `obj`
    // ================================================================

    /// <summary>
    /// A damaged file whose producer writes <c>1 0\nobj</c>. §7.3.10 allows any white-space there;
    /// the scan required a 0x20 space on both sides of the generation number, so recovery found
    /// zero objects and <c>PdfDocument.Open</c> failed with "Cannot find document catalog" on a
    /// file whose every object was intact.
    /// </summary>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    [InlineData(" ")]
    public void Open_NoStartxrefAndNewlineBeforeObj_RecoversTheWholeDocument(string separator)
    {
        byte[] pdf = BuildClassicPdfWithoutStartxref(separator);
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);

        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
        Assert.Single(doc.Pages);
        Assert.Equal(612d, ((PdfNumber)doc.Pages[0].MediaBox[2]).DoubleValue);
    }

    /// <summary>
    /// <c>endobj</c> ends in <c>obj</c>, and the number before it is the object's own body. A scan
    /// that matched it would mint an entry pointing into the middle of the previous object.
    /// </summary>
    [Fact]
    public void Open_RecoveryScan_DoesNotMatchTheObjInEndobj()
    {
        byte[] pdf = BuildClassicPdfWithoutStartxref("\n");
        var parser = ParserOver(pdf);

        // Objects 1..3 and nothing else: `endobj` contributed no entries, and neither did the
        // `/Count 1` sitting immediately before one.
        Assert.Equal(3, parser.CrossReferenceTable.Count);
        for (int n = 1; n <= 3; n++)
            Assert.Equal(CrossReferenceEntry.EntryType.Normal, parser.CrossReferenceTable.GetEntry(n).Type);
    }

    // ================================================================
    //  3. Recovery of a PDF 1.5+ file: /ObjStm members and /Root
    // ================================================================

    /// <summary>
    /// The scan can only see objects written as <c>N G obj</c>, and a PDF 1.5+ producer puts the
    /// catalog and the whole page tree inside an object stream. Recovery therefore has to index
    /// the container's own §7.5.7 index and add the type-2 entries itself.
    /// </summary>
    [Fact]
    public void RecoveredObjectStream_ContributesTypeTwoEntriesForItsMembers()
    {
        var parser = ParserOver(BuildObjStmPdfWithBrokenStartxref(rootInXrefStream: true));

        for (int n = 1; n <= 3; n++)
        {
            var entry = parser.CrossReferenceTable.GetEntry(n);
            Assert.NotNull(entry);
            Assert.Equal(CrossReferenceEntry.EntryType.Compressed, entry.Type);
            Assert.Equal(4, entry.ObjectStreamNumber);
            Assert.Equal(n - 1, entry.IndexInObjectStream);
        }
    }

    /// <summary>
    /// A cross-reference-stream file has no <c>trailer</c> keyword — its trailer IS the xref
    /// stream's dictionary — so the keyword search found nothing and the trailer stayed empty.
    ///
    /// <para><c>/Info</c> is what makes this test falsifiable: <c>/Root</c> alone cannot fail,
    /// because the catalog fallback below reaches the same object 1 by a different route, so
    /// deleting this merge entirely still left a correct <c>/Root</c> behind. Only a key no other
    /// path can produce distinguishes them — which is exactly the mutation that survived until
    /// this assertion was added.</para>
    /// </summary>
    [Fact]
    public void RecoveredXrefStreamDictionary_SuppliesTheTrailerKeys()
    {
        var parser = ParserOver(BuildObjStmPdfWithBrokenStartxref(rootInXrefStream: true));

        var root = Assert.IsType<PdfReference>(parser.Trailer[PdfName.Root]);
        Assert.Equal(1, root.ReferenceObjectNumber);

        var info = Assert.IsType<PdfReference>(parser.Trailer[PdfName.Info]);
        Assert.Equal(9, info.ReferenceObjectNumber);

        // …and the cross-reference plumbing of the section that could not be read is NOT carried
        // into the trailer as if it described a usable table.
        Assert.False(parser.Trailer.ContainsKey(PdfName.W));
        Assert.False(parser.Trailer.ContainsKey(PdfName.Index));
    }

    /// <summary>
    /// Last resort: no <c>trailer</c> keyword and no <c>/Root</c> anywhere, with the catalog living
    /// inside the object stream where the scanned bytes are compressed and cannot be probed. The
    /// members indexed in the step above are what make it findable at all.
    /// </summary>
    [Fact]
    public void RecoveredCompressedCatalog_BecomesRootWhenNothingElseNamesOne()
    {
        var parser = ParserOver(BuildObjStmPdfWithBrokenStartxref(rootInXrefStream: false));

        var root = Assert.IsType<PdfReference>(parser.Trailer[PdfName.Root]);
        Assert.Equal(1, root.ReferenceObjectNumber);
        Assert.Equal(PdfName.Catalog,
            ((PdfDictionary)parser.GetObject(root.ReferenceObjectNumber)).GetName(PdfName.Type));
    }

    /// <summary>End to end: the whole modern document comes back, not just a trailer key.</summary>
    [Fact]
    public void Open_ModernFileWithBrokenStartxref_RecoversCatalogAndPages()
    {
        using var ms = new MemoryStream(BuildObjStmPdfWithBrokenStartxref(rootInXrefStream: true));
        using var doc = PdfDocument.Open(ms);

        Assert.Equal(PdfName.Catalog, doc.Catalog.Dictionary.GetName(PdfName.Type));
        Assert.Single(doc.Pages);
        Assert.Equal(792d, ((PdfNumber)doc.Pages[0].MediaBox[3]).DoubleValue);
    }

    // ================================================================
    //  4. The /ObjStm index has to agree with the xref entry
    // ================================================================

    /// <summary>
    /// The xref says object 3 sits at index 1 of the stream, whose own index says index 1 is
    /// object 2. §7.5.7 stores that number precisely so the disagreement is checkable; it used to
    /// be parsed and discarded, so the caller received object 2's dictionary stamped with object
    /// 3's number and nothing said otherwise.
    /// </summary>
    [Fact]
    public void GetObject_ObjStmIndexNamingADifferentObject_IsRefusedAndReported()
    {
        var parser = ParserOver(BuildObjStmPdf(objectThreeMemberIndex: 1));

        Assert.IsType<PdfNull>(parser.GetObject(3));

        string reason = PdfParser.LastObjectParseError;
        Assert.NotNull(reason);
        Assert.Contains("obj 3", reason);
        Assert.Contains("names obj 2", reason);
    }

    /// <summary>The honest negative: the agreeing index still returns the object it names.</summary>
    [Fact]
    public void GetObject_ObjStmIndexAgreeing_ReturnsTheObjectAndReportsNothing()
    {
        var parser = ParserOver(BuildObjStmPdf(objectThreeMemberIndex: 2));

        var dict = Assert.IsType<PdfDictionary>(parser.GetObject(3));
        Assert.Equal(PdfName.Page, dict.GetName(PdfName.Type));
        Assert.Null(PdfParser.LastObjectParseError);
    }

    // ================================================================
    //  5. The object at the offset must be the object requested
    // ================================================================

    /// <summary>
    /// Object 3's cross-reference offset points at object 2's header — the shape a web server
    /// prefixing bytes, a naive concatenation or a deliberately aimed offset produces. Asking for
    /// 3 returned 2's dictionary, cached it under 3, and left the diagnostic slot null; then it
    /// refused with the reason. Since GAPS §19h item 5 the file's own <c>3 0 obj</c> header is found
    /// and the object read from there — never object 2's body — with nothing to report, as a
    /// shifted table loses nothing a scan can still find (another reader issue7229 was saved one page short).
    /// </summary>
    [Fact]
    public void GetObject_OffsetHoldingADifferentObject_IsReadFromTheObjectsOwnHeader()
    {
        var parser = ParserOver(BuildClassicPdf(aimObject3AtObject2: true));

        var page = Assert.IsType<PdfDictionary>(parser.GetObject(3));
        Assert.Equal(PdfName.Page, page.GetName(PdfName.Type));
        Assert.Null(PdfParser.LastObjectParseError);
        // Object 2 is still itself: the misaimed entry did not displace what its own offset holds.
        Assert.Equal(PdfName.Pages, Assert.IsType<PdfDictionary>(parser.GetObject(2)).GetName(PdfName.Type));
    }

    /// <summary>
    /// The refusal the fallback does not reach: object 3's entry points at object 2 and the file holds
    /// no <c>3 0 obj</c> header anywhere, so there is nothing to read it from — PdfNull, and the entry's
    /// own reason, never object 2's body under the wrong number.
    /// </summary>
    [Fact]
    public void GetObject_OffsetHoldingADifferentObject_WithNoHeaderOfItsOwn_IsRefusedAndReported()
    {
        var parser = ParserOver(BuildClassicPdf(aimObject3AtObject2: true, writeObject3: false));

        Assert.IsType<PdfNull>(parser.GetObject(3));

        string reason = PdfParser.LastObjectParseError;
        Assert.NotNull(reason);
        Assert.Contains("obj 3", reason);
        Assert.Contains("holds obj 2", reason);
    }

    /// <summary>
    /// An entry pointing past the end of the file is recovered the same way when the object's header
    /// is in the file, and refused with the offset's reason when it is not.
    /// </summary>
    [Fact]
    public void GetObject_OffsetOutsideTheFile_IsReadFromTheObjectsOwnHeader_OrRefused()
    {
        var recovered = ParserOver(BuildClassicPdf(aimObject3AtObject2: false, object3OffsetPastEof: true));
        var page = Assert.IsType<PdfDictionary>(recovered.GetObject(3));
        Assert.Equal(PdfName.Page, page.GetName(PdfName.Type));
        Assert.Null(PdfParser.LastObjectParseError);

        var absent = ParserOver(BuildClassicPdf(aimObject3AtObject2: false, object3OffsetPastEof: true, writeObject3: false));
        Assert.IsType<PdfNull>(absent.GetObject(3));
        Assert.Contains("outside the file", PdfParser.LastObjectParseError);
    }

    /// <summary>
    /// A traditional cross-reference subsection whose start is past what an object number can be
    /// (<c>4294967296 1</c>) is a malformed section, not an object numbered int.MaxValue with the
    /// next entries wrapped to negative numbers — which is what the saturating read produced (another reader
    /// GHOSTSCRIPT-698804-1-fuzzed). The section is skipped and the scan recovery reads the objects.
    /// </summary>
    [Fact]
    public void ClassicXref_SubsectionStartBeyondInt_IsNotSaturatedIntoTheTable()
    {
        var parser = ParserOver(BuildClassicPdfWithSubsectionStart("4294967296"));

        Assert.Null(parser.CrossReferenceTable.GetEntry(int.MaxValue));
        Assert.Null(parser.CrossReferenceTable.GetEntry(int.MinValue));
        Assert.Equal(PdfName.Page, Assert.IsType<PdfDictionary>(parser.GetObject(3)).GetName(PdfName.Type));
    }

    /// <summary>The honest negative: correct offsets return their own objects and report nothing.</summary>
    [Fact]
    public void GetObject_CorrectOffsets_ReturnEachObjectAndReportNothing()
    {
        var parser = ParserOver(BuildClassicPdf(aimObject3AtObject2: false));

        var page = Assert.IsType<PdfDictionary>(parser.GetObject(3));
        Assert.Equal(PdfName.Page, page.GetName(PdfName.Type));
        Assert.Null(PdfParser.LastObjectParseError);
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    /// <summary>
    /// A classic file whose object 5 is a stream carrying <paramref name="dictEntries"/>. When
    /// <paramref name="writeEndstream"/> is false the keyword is omitted from the whole file, so
    /// the endstream scan genuinely finds nothing.
    /// </summary>
    private static byte[] BuildPdfWithStream(string dictEntries, string payload, bool writeEndstream)
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        long o1 = file.Position; Write("1 0 obj\n" + CatalogBody + "\nendobj\n");
        long o5 = file.Position;
        Write("5 0 obj\n<< " + dictEntries + " >>\nstream\n" + payload +
              (writeEndstream ? "\nendstream\nendobj\n" : "\n"));

        long xref = file.Position;
        Write("xref\n0 1\n0000000000 65535 f \n1 1\n" + o1.ToString("D10", CultureInfo.InvariantCulture) +
              " 00000 n \n5 1\n" + o5.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n" +
              xref.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>
    /// A three-object classic file with NO <c>startxref</c>, so opening it always goes through the
    /// recovery scan. <paramref name="separator"/> is written between the generation number and
    /// the <c>obj</c> keyword.
    /// </summary>
    private static byte[] BuildClassicPdfWithoutStartxref(string separator)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0").Append(separator).Append("obj\n").Append(CatalogBody).Append("\nendobj\n");
        sb.Append("2 0").Append(separator).Append("obj\n").Append(PagesBody).Append("\nendobj\n");
        sb.Append("3 0").Append(separator).Append("obj\n").Append(PageBody).Append("\nendobj\n");
        sb.Append("%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// A well-formed classic file. With <paramref name="aimObject3AtObject2"/> the cross-reference
    /// entry for object 3 records object 2's byte offset instead of its own; with
    /// <paramref name="object3OffsetPastEof"/> an offset past the end of the file; with
    /// <paramref name="writeObject3"/> false the file holds no <c>3 0 obj</c> at all, so nothing
    /// can be recovered from a header.
    /// </summary>
    private static byte[] BuildClassicPdf(bool aimObject3AtObject2, bool writeObject3 = true, bool object3OffsetPastEof = false)
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        long o1 = file.Position; Write("1 0 obj\n" + CatalogBody + "\nendobj\n");
        long o2 = file.Position; Write("2 0 obj\n" + PagesBody + "\nendobj\n");
        long o3 = file.Position;
        if (writeObject3) Write("3 0 obj\n" + PageBody + "\nendobj\n");

        long xref = file.Position;
        long recorded3 = aimObject3AtObject2 ? o2 : object3OffsetPastEof ? 9_000_000_000L : o3;
        var xs = new StringBuilder();
        xs.Append("xref\n0 4\n0000000000 65535 f \n");
        xs.Append(o1.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o2.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(recorded3.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }

    /// <summary>
    /// The three-object classic file whose one cross-reference subsection header reads
    /// <c>&lt;start&gt; 3</c> instead of <c>1 3</c> — the entries are object 1's, 2's and 3's.
    /// </summary>
    /// <summary>
    /// Two values the first bound let through: a count that overflows the sum with the start (the
    /// check compared the wrapped, negative sum), and a start past what <c>long</c> holds (read as 0).
    /// Each is a malformed section handed to the scan recovery, which reads object 3.
    /// </summary>
    [Theory]
    [InlineData("1", "9223372036854775807")]
    [InlineData("99999999999999999999", "3")]
    public void ClassicXref_SubsectionHeaderPastLongOrOverflowingItsSum_IsRefusedToTheScan(string start, string count)
    {
        var parser = ParserOver(BuildClassicPdfWithSubsectionStart(start, count));

        Assert.Equal(PdfName.Page, Assert.IsType<PdfDictionary>(parser.GetObject(3)).GetName(PdfName.Type));
        Assert.Null(parser.CrossReferenceTable.GetEntry(int.MaxValue));
        // Refused for the header, by name — not one step later on the entries the wrapped count left
        // unread, which also ends in the scan but says nothing about the cause.
        Assert.Contains("outside the range an object number can have", PdfParser.LastXrefSectionError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A first subsection headed <c>1 4</c> whose first entry is the free head is a <c>0 4</c>
    /// subsection mislabelled, as lenient readers take it: every entry belongs one number lower, so
    /// objects 1 to 3 have their own offsets, nothing is recovered and nothing is reported. Moving
    /// the head alone left object 1 with no entry and gave 2 and 3 their predecessors' offsets.
    /// </summary>
    [Fact]
    public void ClassicXref_FirstSubsectionLabelledFromOneWithTheFreeHead_IsReadFromZeroThroughout()
    {
        var parser = ParserOver(BuildClassicPdfLabelledFromOne(out long[] offsets));

        for (int n = 1; n <= 3; n++)
            Assert.Equal(offsets[n - 1], parser.CrossReferenceTable.GetEntry(n).Offset);
        Assert.Null(parser.CrossReferenceTable.GetEntry(4));
        Assert.Equal(PdfName.Page, Assert.IsType<PdfDictionary>(parser.GetObject(3)).GetName(PdfName.Type));
        Assert.Null(PdfParser.LastObjectHeaderRecovery);
    }

    /// <summary>The three-object classic file whose one subsection reads <c>1 4</c> over the free head and objects 1 to 3.</summary>
    private static byte[] BuildClassicPdfLabelledFromOne(out long[] offsets)
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        long o1 = file.Position; Write("1 0 obj\n" + CatalogBody + "\nendobj\n");
        long o2 = file.Position; Write("2 0 obj\n" + PagesBody + "\nendobj\n");
        long o3 = file.Position; Write("3 0 obj\n" + PageBody + "\nendobj\n");
        offsets = new[] { o1, o2, o3 };

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n1 4\n0000000000 65535 f \n");
        xs.Append(o1.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o2.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o3.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }

    private static byte[] BuildClassicPdfWithSubsectionStart(string start, string count = "3")
    {
        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.4\n");
        long o1 = file.Position; Write("1 0 obj\n" + CatalogBody + "\nendobj\n");
        long o2 = file.Position; Write("2 0 obj\n" + PagesBody + "\nendobj\n");
        long o3 = file.Position; Write("3 0 obj\n" + PageBody + "\nendobj\n");

        long xref = file.Position;
        var xs = new StringBuilder();
        xs.Append("xref\n0 1\n0000000000 65535 f \n").Append(start).Append(' ').Append(count).Append('\n');
        xs.Append(o1.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o2.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append(o3.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        xs.Append("trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n")
          .Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        Write(xs.ToString());
        return file.ToArray();
    }

    /// <summary>
    /// A PDF 1.5 file whose catalog (1), page tree (2) and page (3) live in object stream 4, with
    /// cross-reference stream 5. <paramref name="objectThreeMemberIndex"/> is the index the xref
    /// records for object 3 — 2 is the truth, 1 points at object 2's slot instead.
    /// </summary>
    private static byte[] BuildObjStmPdf(int objectThreeMemberIndex, long startxrefOverride = -1,
                                         bool rootInXrefStream = true)
    {
        // Bodies are numbered 1, 2, 3 and stored at member indices 0, 1, 2.
        string[] bodies = { CatalogBody, PagesBody, PageBody };
        var pairs = new StringBuilder();
        var payload = new StringBuilder();
        for (int i = 0; i < bodies.Length; i++)
        {
            pairs.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(' ')
                 .Append(payload.Length.ToString(CultureInfo.InvariantCulture)).Append(' ');
            payload.Append(bodies[i]).Append('\n');
        }
        string index = pairs.ToString();
        byte[] objStmData = Encoding.ASCII.GetBytes(index + payload);

        var file = new MemoryStream();
        void Write(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }

        Write("%PDF-1.5\n");
        long objStmOffset = file.Position;
        Write("4 0 obj\n<< /Type /ObjStm /N 3 /First " +
              index.Length.ToString(CultureInfo.InvariantCulture) + " /Length " +
              objStmData.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        file.Write(objStmData, 0, objStmData.Length);
        Write("\nendstream\nendobj\n");

        long xrefOffset = file.Position;
        var xref = new MemoryStream();
        void Entry(byte type, uint f2, ushort f3)
        {
            xref.WriteByte(type);
            xref.WriteByte((byte)(f2 >> 24)); xref.WriteByte((byte)(f2 >> 16));
            xref.WriteByte((byte)(f2 >> 8));  xref.WriteByte((byte)f2);
            xref.WriteByte((byte)(f3 >> 8));  xref.WriteByte((byte)f3);
        }

        Entry(0, 0, 0xFFFF);                                   // 0: free-list head
        Entry(2, 4, 0);                                        // 1 -> member 0 (the catalog)
        Entry(2, 4, 1);                                        // 2 -> member 1 (the page tree)
        Entry(2, 4, (ushort)objectThreeMemberIndex);           // 3 -> member 2, or the wrong slot
        Entry(1, (uint)objStmOffset, 0);                       // 4: the ObjStm
        Entry(1, (uint)xrefOffset, 0);                         // 5: the xref stream

        byte[] xrefBytes = xref.ToArray();
        // /Info names an object that need not exist: a trailer key is carried forward as written,
        // and it is the one thing only this dictionary can supply.
        Write("5 0 obj\n<< /Type /XRef /Size 6 /W [1 4 2] " +
              (rootInXrefStream ? "/Root 1 0 R /Info 9 0 R " : "") + "/Length " +
              xrefBytes.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
        file.Write(xrefBytes, 0, xrefBytes.Length);
        Write("\nendstream\nendobj\n");
        Write("startxref\n" +
              (startxrefOverride >= 0 ? startxrefOverride : xrefOffset).ToString(CultureInfo.InvariantCulture) +
              "\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>
    /// The same modern file with a <c>startxref</c> pointing into the header comment, which is the
    /// single most recoverable real-world corruption. The section refuses to parse, so recovery is
    /// the only path to the catalog.
    /// </summary>
    private static byte[] BuildObjStmPdfWithBrokenStartxref(bool rootInXrefStream) =>
        BuildObjStmPdf(objectThreeMemberIndex: 2, startxrefOverride: 3, rootInXrefStream: rootInXrefStream);
}
