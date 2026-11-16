// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// An object stream written by one revision keeps every object it was written with. When a later revision rewrites,
/// moves or deletes one of those objects, the decrypted document must hold the later state.
/// </summary>
/// <remarks>
/// <para>ISO 32000-2 §7.5.6: "When a PDF reader reads the PDF file, it shall build its cross-reference information in
/// such a way that the most recent copy of each object shall be the one accessed from the PDF file." <c>PdfParser</c>
/// merges the table that way — newest section first, first entry wins — but <c>PdfDecryptor.WriteDecrypted</c> then
/// expanded every object stream a current entry names and wrote <i>all</i> of its members over what it had already
/// decrypted. A page from the older object stream replaced the newer page, and the decrypted file showed the document
/// as it was before the update.</para>
/// <para><b>The loss did not stay in the decrypted copy.</b> <see cref="PdfEncryptedRoundTrip"/> re-encrypts that copy
/// as the whole new document, and <see cref="PdfEncryptedIncrementalUpdate"/> hands it to the plan that decides what the
/// next revision writes, so the later revision's change was lost from the file itself. The last two tests assert
/// exactly that, on the bytes each one produces.</para>
/// <para>Every fixture comes from <see cref="TestPdfs.LegacyRc4.RevisionWriter"/>, which computes its own RC4 keys and
/// writes cross-reference streams — the only kind that can place an object inside an object stream.</para>
/// </remarks>
public class SupersededObjectStreamMemberTests
{
    private static readonly byte[] FileId =
    {
        0x51, 0x7A, 0x0C, 0x33, 0x9E, 0x46, 0xD2, 0x18,
        0x6B, 0xF0, 0x25, 0x87, 0x4D, 0xB9, 0x60, 0x0E
    };

    private const string RevisionOneMarker = "page-content-as-revision-one-left-it";
    private const string RevisionTwoMarker = "page-content-as-revision-two-left-it";

    private const string Catalog = "<< /Type /Catalog /Pages 2 0 R >>";
    private const string PageTree = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";

    private static string Page(int contentsObjectNumber, string extraEntries = "")
        => $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contentsObjectNumber} 0 R{extraEntries} >>";

    private static string Content(string marker) => $"BT /F1 12 Tf 72 700 Td ({marker}) Tj ET";

    /// <summary>
    /// Revision 1 stores page 3 (at index 0) and page tree 2 in object stream 6, with the page's content in stream 4.
    /// Revision 2 adds content stream 8 and rewrites page 3 at the top level to show it. The page tree is untouched, so
    /// object stream 6 is still live — and still expanded.
    /// </summary>
    private static byte[] PageRewrittenAtTopLevel()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Stream(4, 0, Content(RevisionOneMarker));
        pdf.EncryptDictionary(5);
        pdf.ObjectStream(6, (3, Page(4)), (2, PageTree));
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);

        pdf.Stream(8, 0, Content(RevisionTwoMarker));
        pdf.Object(3, 0, Page(8));
        pdf.EndRevision(xrefStreamObjectNumber: 9, rootObjectNumber: 1, encryptObjectNumber: 5);
        return pdf.ToArray();
    }

    [Fact]
    public void PageALaterRevisionRewroteAtTheTopLevel_DecryptsToTheRewrittenPage()
    {
        byte[] pdf = PageRewrittenAtTopLevel();

        // The fixture's shape, read off the table the parser merged: page 3 is now a top-level object, while the page
        // tree beside its superseded copy is still in object stream 6.
        using (var encrypted = PdfDocument.Open(new MemoryStream(pdf)))
        {
            Assert.Equal(CrossReferenceEntry.EntryType.Normal, encrypted.CrossReferenceTable.GetEntry(3).Type);
            Assert.Equal(6, encrypted.CrossReferenceTable.GetEntry(2).ObjectStreamNumber);
        }

        (byte[] decrypted, string? dropped) = Decrypt(pdf);

        Assert.Contains(RevisionTwoMarker, PageContent(decrypted), StringComparison.Ordinal);
        Assert.Null(dropped);
    }

    /// <summary>
    /// Revision 2 moves page 3 into a new object stream, 10, at the same index 0 its superseded copy holds in object
    /// stream 6 — so only the stream number tells the two copies apart.
    /// </summary>
    [Fact]
    public void PageALaterRevisionMovedIntoANewerObjectStream_DecryptsToTheMovedPage()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Stream(4, 0, Content(RevisionOneMarker));
        pdf.EncryptDictionary(5);
        pdf.ObjectStream(6, (3, Page(4)), (2, PageTree));
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);

        pdf.Stream(8, 0, Content(RevisionTwoMarker));
        pdf.ObjectStream(10, (3, Page(8)));
        pdf.EndRevision(xrefStreamObjectNumber: 11, rootObjectNumber: 1, encryptObjectNumber: 5);

        (byte[] decrypted, string? dropped) = Decrypt(pdf.ToArray());

        Assert.Contains(RevisionTwoMarker, PageContent(decrypted), StringComparison.Ordinal);
        Assert.Null(dropped);
    }

    /// <summary>
    /// Revision 2 removes the page's only annotation and frees its object number. The annotation still sits in object
    /// stream 6 beside the live page tree, and must not come back.
    /// </summary>
    [Fact]
    public void AnnotationALaterRevisionDeleted_IsNotRestoredFromTheOlderObjectStream()
    {
        const string DeletedNote = "note-the-second-revision-deleted";

        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Object(3, 0, Page(4, " /Annots [11 0 R]"));
        pdf.Stream(4, 0, Content(RevisionOneMarker));
        pdf.EncryptDictionary(5);
        pdf.ObjectStream(6, (2, PageTree),
            (11, $"<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents ({DeletedNote}) >>"));
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);

        pdf.Object(3, 0, Page(4));
        pdf.Free(11, nextGeneration: 1);
        pdf.EndRevision(xrefStreamObjectNumber: 8, rootObjectNumber: 1, encryptObjectNumber: 5);

        (byte[] decrypted, string? dropped) = Decrypt(pdf.ToArray());

        using var doc = PdfDocument.Open(new MemoryStream(decrypted));
        Assert.Equal(1, doc.Pages.Count);                      // object stream 6 was still expanded for the page tree
        Assert.Null(doc.CrossReferenceTable.GetEntry(11));     // the deleted annotation was not written
        Assert.DoesNotContain(DeletedNote, Encoding.ASCII.GetString(decrypted), StringComparison.Ordinal);
        Assert.Null(dropped);                                  // a deletion is not a drop
    }

    /// <summary>
    /// An object stream that lists one object number twice. The entry names index 0; the copy at index 2 comes later in
    /// the stream, which is all "last one written wins" looked at. <c>PdfParser.ParseObjectFromStream</c> reads a
    /// compressed object at the index its entry names, so that is the copy the document holds.
    /// </summary>
    [Fact]
    public void ObjectStreamListingAnObjectNumberTwice_YieldsTheCopyAtTheIndexItsEntryNames()
    {
        const string IndexedCopyMarker = "content-of-the-copy-at-the-index-the-entry-names";
        const string OtherCopyMarker = "content-of-the-copy-at-another-index";

        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Stream(4, 0, Content(IndexedCopyMarker));
        pdf.EncryptDictionary(5);
        pdf.Stream(8, 0, Content(OtherCopyMarker));
        pdf.ObjectStreamWithoutEntries(6, "", (3, Page(4)), (2, PageTree), (3, Page(8)));
        pdf.Compressed(3, objectStreamNumber: 6, index: 0);
        pdf.Compressed(2, objectStreamNumber: 6, index: 1);
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);

        (byte[] decrypted, string? dropped) = Decrypt(pdf.ToArray());

        Assert.Contains(IndexedCopyMarker, PageContent(decrypted), StringComparison.Ordinal);
        Assert.Null(dropped);
    }

    /// <summary>
    /// An entry that places object 12 at index 1 of object stream 6, where object 11 is stored instead. Nothing can supply
    /// object 12, and <see cref="PdfDecryptor.LastObjectStreamRejection"/> is how a caller tells an object missing from
    /// the output from one the file never had.
    /// </summary>
    [Fact]
    public void EntryPlacingAnObjectAtAnIndexThatHoldsAnotherObject_IsReportedAsAbsent()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Object(3, 0, Page(4, " /Annots [12 0 R]"));
        pdf.Stream(4, 0, Content(RevisionOneMarker));
        pdf.EncryptDictionary(5);
        pdf.ObjectStreamWithoutEntries(6, "", (2, PageTree), (11, "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] >>"));
        pdf.Compressed(2, objectStreamNumber: 6, index: 0);
        pdf.Compressed(12, objectStreamNumber: 6, index: 1);
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);

        (byte[] decrypted, string? dropped) = Decrypt(pdf.ToArray());

        Assert.NotNull(dropped);
        Assert.Contains("Object 12", dropped!, StringComparison.Ordinal);
        Assert.Contains("absent from the output", dropped!, StringComparison.Ordinal);

        using var doc = PdfDocument.Open(new MemoryStream(decrypted));
        Assert.Contains(RevisionOneMarker, Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData()), StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream that cannot be expanded at all already reports that everything it held is absent, so its members are not
    /// then reported one by one as well. The refusal here is a <c>/Filter</c> chain longer than
    /// <c>PdfLimits.MaxFilterChainLength</c>, which <c>FilterPipeline</c> rejects before decoding a byte.
    /// </summary>
    [Fact]
    public void ObjectStreamThatCannotBeExpanded_IsReportedOnceForTheStreamAndNotAgainPerObject()
    {
        var chain = new StringBuilder("/Filter [");
        for (int i = 0; i < 17; i++) chain.Append(" /FlateDecode");
        chain.Append(" ]");

        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId);
        pdf.Object(1, 0, Catalog);
        pdf.Object(3, 0, Page(4));
        pdf.Stream(4, 0, Content(RevisionOneMarker));
        pdf.EncryptDictionary(5);
        pdf.ObjectStreamWithoutEntries(6, chain.ToString(), (2, PageTree), (11, "<< /Type /Annot /Subtype /Text >>"));
        pdf.Compressed(2, objectStreamNumber: 6, index: 0);
        pdf.Compressed(11, objectStreamNumber: 6, index: 1);
        pdf.EndRevision(xrefStreamObjectNumber: 7, rootObjectNumber: 1, encryptObjectNumber: 5);

        (_, string? dropped) = Decrypt(pdf.ToArray());

        Assert.NotNull(dropped);
        Assert.Contains("Object stream 6 could not be expanded", dropped!, StringComparison.Ordinal);

        // ONE drop, not three. Only the first reason is kept verbatim — the rest are a count — so counting is the only
        // way to see the members being reported a second time: "(and 2 further object(s) …)" is what that would read.
        Assert.DoesNotContain("further object(s)", dropped!, StringComparison.Ordinal);
    }

    [Fact]
    public void RoundTrip_OfADocumentWhosePageALaterRevisionRewrote_ReEncryptsTheRewrittenPage()
    {
        using var input = new MemoryStream(PageRewrittenAtTopLevel());
        using var output = new MemoryStream();

        RoundTripResult result = PdfEncryptedRoundTrip.Run(input, output, new EncryptedRoundTripOptions(),
            (plain, produced) => plain.CopyTo(produced));

        Assert.True(result.Succeeded, result.FailureReason);

        // The output is a single revision: whichever page the decrypted copy held is now the only page in the file.
        (byte[] decrypted, _) = Decrypt(output.ToArray());
        Assert.Contains(RevisionTwoMarker, PageContent(decrypted), StringComparison.Ordinal);
    }

    [Fact]
    public void IncrementalUpdate_PlannedFromADocumentWhosePageALaterRevisionRewrote_AppendsTheRewrittenPage()
    {
        using var input = new MemoryStream(PageRewrittenAtTopLevel());
        using var output = new MemoryStream();

        // The shape of every page-editing plan: read the page from the decrypted copy, supersede it with an edited clone.
        PdfEncryptedIncrementalUpdate.Run(input, output, "", (doc, firstFree) =>
        {
            PdfDictionary page = PdfDictionary.CloneShallow(doc.Pages[0].Dictionary);
            page.Set(PdfName.Rotate, new PdfNumber(90));
            return new[] { new IndirectObject(3, 0, page) };
        });

        // Read straight off the updated, still-encrypted file. A page dictionary holds no strings, so nothing is decrypted
        // between this assertion and the bytes the update appended.
        using var updated = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var appendedPage = Assert.IsType<PdfDictionary>(updated.Parser.GetObject(3));
        Assert.Equal(90, appendedPage.GetInteger(PdfName.Rotate));
        Assert.Equal(8, Assert.IsType<PdfReference>(appendedPage[PdfName.Contents]).ReferenceObjectNumber);
    }

    private static (byte[] Pdf, string? Dropped) Decrypt(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, "");
        return (output.ToArray(), PdfDecryptor.LastObjectStreamRejection);
    }

    private static string PageContent(byte[] plainPdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(plainPdf));
        return Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
    }
}
