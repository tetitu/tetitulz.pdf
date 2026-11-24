// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Attributing a revision to the signature(s) that cover it, and the field walk that has to be
/// right for either to work.
///
/// <para><b>Why the fixtures nest a signature under a parent field.</b> The walk this replaced
/// read only the top level of <c>/Fields</c> and tested each entry's own <c>/FT</c>, so the shape
/// a common viewer produces when it adds a second signature to an existing form — a parent field node
/// holding the signature as a kid — reported as an <i>unsigned</i> document. Every fixture here
/// puts at least one signature one level down, because a flat fixture cannot tell the two walks
/// apart.</para>
///
/// <para>Nothing here signs anything: attribution reads the <b>declared</b> <c>/ByteRange</c>,
/// which is what lets it live in <c>Base</c>, an assembly that does not reference
/// <c>Tetitulz.Pdf.Signing</c> and must not start.</para>
/// </summary>
public sealed class PdfRevisionSignatureAttributionTests
{
    // A fixed-width slot, rewritten in place once the final byte offsets are known — exactly how
    // a real signer reserves room for a /ByteRange it cannot know until the file is complete.
    // Everything written into it is 32 characters wide, so no offset already recorded moves.
    private const string ByteRangeLead = "/ByteRange [0 ";
    private const string ByteRangeSlot = "0000000000 0000000000 0000000000";
    private static readonly string ContentsSlot = "<" + new string('0', 64) + ">";

    /// <summary>
    /// A one-revision PDF whose only signature (<c>Parent.Sig1</c>) hangs off a parent field node.
    /// </summary>
    /// <param name="shortfall">
    /// How far short of the end of the file the signature's <c>/ByteRange</c> stops. 0 is what
    /// this repo's own signer writes; 1 is a third-party signer that ends at the <c>F</c> of
    /// <c>%%EOF</c>; a large value puts the end mid-file, where it covers no revision at all.
    /// </param>
    /// <param name="unnamed">
    /// Drops <c>/T</c> from the signature field <i>and</i> its parent, so the field has no name
    /// anywhere in its chain. Legal, and unnameable — see
    /// <see cref="ListWithSignatures_ASignatureFieldWithNoNameAnywhereIsStillAttributed"/>.
    /// </param>
    private static byte[] BuildSignedPdf(
        int shortfall = 0, bool encrypted = false, bool indirectFields = false, bool unnamed = false)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var off = new long[8];

        off[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields ");
        sb.Append(indirectFields ? "7 0 R" : "[4 0 R]");
        sb.Append(" /SigFlags 3 >> >>\nendobj\n");

        off[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        off[3] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>\nendobj\n");

        // The parent field node: no /FT of its own, which is why the old top-level-only walk
        // skipped it and never saw the signature underneath.
        off[4] = sb.Length;
        sb.Append("4 0 obj\n<< " + (unnamed ? "" : "/T (Parent) ") + "/Kids [5 0 R] >>\nendobj\n");

        off[5] = sb.Length;
        sb.Append("5 0 obj\n<< /FT /Sig " + (unnamed ? "" : "/T (Sig1) ") + "/V 6 0 R /Type /Annot /Subtype /Widget " +
                  "/Rect [0 0 0 0] /Parent 4 0 R >>\nendobj\n");

        off[6] = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached " +
                  ByteRangeLead + ByteRangeSlot + "] /Contents " + ContentsSlot + " >>\nendobj\n");

        int size = 7;

        if (encrypted)
        {
            off[7] = sb.Length;
            sb.Append("7 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 " +
                      "/O <" + new string('A', 64) + "> /U <" + new string('B', 64) + "> >>\nendobj\n");
            size = 8;
        }
        else if (indirectFields)
        {
            off[7] = sb.Length;
            sb.Append("7 0 obj\n[4 0 R]\nendobj\n");
            size = 8;
        }

        long xref = sb.Length;
        sb.Append("xref\n0 " + size + "\n0000000000 65535 f \n");
        for (int i = 1; i < size; i++) sb.AppendFormat("{0:D10} 00000 n \n", off[i]);
        sb.Append("trailer\n<< /Size " + size + " /Root 1 0 R");
        if (encrypted)
            sb.Append(" /Encrypt 7 0 R /ID [<0102030405060708090A0B0C0D0E0F10> <0102030405060708090A0B0C0D0E0F10>]");
        sb.Append(" >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] bytes = Encoding.ASCII.GetBytes(sb.ToString());
        SetByteRange(bytes, 0, TailCovering(bytes, 0, bytes.Length - shortfall));
        return bytes;
    }

    /// <summary>
    /// The same document after a second signing pass: an appended revision adding
    /// <c>Sig2</c> — a top-level field with a separate widget kid, the shape that makes the same
    /// signature reachable twice through the field tree.
    /// </summary>
    /// <param name="widgetRepeatsValue">
    /// Gives the widget kid a <c>/V</c> of its own naming the <i>same</i> signature dictionary.
    /// Then it is not a value-less widget any more, and only identity de-duplication keeps the
    /// one signature from being counted twice.
    /// </param>
    private static byte[] BuildTwiceSignedPdf(bool widgetRepeatsValue = false)
    {
        byte[] revision0 = BuildSignedPdf();
        var off = new long[10];
        var sb = new StringBuilder();

        off[7] = revision0.Length + sb.Length;
        sb.Append("7 0 obj\n<< /FT /Sig /T (Sig2) /V 8 0 R /Kids [9 0 R] >>\nendobj\n");

        off[8] = revision0.Length + sb.Length;
        sb.Append("8 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached " +
                  ByteRangeLead + ByteRangeSlot + "] /Contents " + ContentsSlot + " >>\nendobj\n");

        // A widget with no /T and no /V. It inherits /FT /Sig from field 7, so the field walk
        // hands it back as a signature field too — the /V check is the only thing between that
        // and reporting Sig2 twice.
        off[9] = revision0.Length + sb.Length;
        sb.Append("9 0 obj\n<< /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /Parent 7 0 R" +
                  (widgetRepeatsValue ? " /V 8 0 R" : "") + " >>\nendobj\n");

        off[1] = revision0.Length + sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R " +
                  "/AcroForm << /Fields [4 0 R 7 0 R] /SigFlags 3 >> >>\nendobj\n");

        // Objects 2-6 are unchanged, so their revision-0 offsets are repeated verbatim; rewriting
        // the whole table in an update is legal and keeps the fixture readable.
        for (int i = 2; i <= 6; i++) off[i] = OffsetOfObject(revision0, i);

        long prevXref = StartXrefOffsetOf(revision0);
        long xref = revision0.Length + sb.Length;
        sb.Append("xref\n0 10\n0000000000 65535 f \n");
        for (int i = 1; i <= 9; i++) sb.AppendFormat("{0:D10} 00000 n \n", off[i]);
        sb.AppendFormat("trailer\n<< /Size 10 /Root 1 0 R /Prev {0} >>\n", prevXref);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] appended = Encoding.ASCII.GetBytes(sb.ToString());
        var whole = new byte[revision0.Length + appended.Length];
        Buffer.BlockCopy(revision0, 0, whole, 0, revision0.Length);
        Buffer.BlockCopy(appended, 0, whole, revision0.Length, appended.Length);

        SetByteRange(whole, revision0.Length, TailCovering(whole, revision0.Length, whole.Length));
        return whole;
    }

    /// <summary>
    /// The three numbers after <c>/ByteRange [0</c> for a range that covers everything except the
    /// <c>/Contents</c> hex and ends at <paramref name="end"/>.
    /// </summary>
    private static string TailCovering(byte[] bytes, int searchFrom, long end)
    {
        int hex = IndexOf(bytes, ContentsSlot, searchFrom);   // the '<' of the hex string
        long start2 = hex + ContentsSlot.Length;              // the range resumes just after it
        return hex.ToString("D10") + " " + start2.ToString("D10") + " " + (end - start2).ToString("D10");
    }

    /// <summary>
    /// Overwrites the first <c>/ByteRange</c> at or after <paramref name="searchFrom"/>. The
    /// replacement must be exactly as wide as the slot, or every offset in the file behind it
    /// would move and the fixture would stop being a valid PDF.
    /// </summary>
    private static void SetByteRange(byte[] bytes, int searchFrom, string tail)
    {
        if (tail.Length != ByteRangeSlot.Length)
            throw new InvalidOperationException("a fixture patch must not change the file's length");
        int at = IndexOf(bytes, ByteRangeLead, searchFrom) + ByteRangeLead.Length;
        Buffer.BlockCopy(Encoding.ASCII.GetBytes(tail), 0, bytes, at, tail.Length);
    }

    private static int IndexOf(byte[] haystack, string needle, int from)
    {
        byte[] n = Encoding.ASCII.GetBytes(needle);
        for (int i = from; i + n.Length <= haystack.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < n.Length; j++)
                if (haystack[i + j] != n[j]) { hit = false; break; }
            if (hit) return i;
        }
        throw new InvalidOperationException($"fixture is missing '{needle}'");
    }

    private static long OffsetOfObject(byte[] bytes, int objectNumber) =>
        IndexOf(bytes, objectNumber + " 0 obj\n", 0);

    private static long StartXrefOffsetOf(byte[] bytes)
    {
        int at = IndexOf(bytes, "startxref\n", 0) + "startxref\n".Length;
        int end = at;
        while (end < bytes.Length && bytes[end] >= (byte)'0' && bytes[end] <= (byte)'9') end++;
        return long.Parse(Encoding.ASCII.GetString(bytes, at, end - at));
    }

    // ================================================================
    //  The field walk (PdfIncrementalSaveValidator)
    // ================================================================

    [Fact]
    public void InspectIncrementalSave_SignatureNestedUnderAParentField_IsReported()
    {
        byte[] pdf = BuildSignedPdf();

        using var ms = new MemoryStream(pdf);
        var info = PdfOperations.InspectIncrementalSave(ms);

        var only = Assert.Single(info.SignatureReports);
        Assert.Equal("Parent.Sig1", only.FieldName);
        Assert.Equal(0L, only.ByteRange[0]);
        Assert.Equal((long)pdf.Length, only.ByteRange[2] + only.ByteRange[3]);
        Assert.True(only.CoversWholeFile);
        Assert.True(only.IsOutermost);
        Assert.Null(only.Issue);
    }

    [Fact]
    public void InspectIncrementalSave_SignatureFieldWithAWidgetKid_IsReportedExactlyOnce()
    {
        // The widget inherits /FT /Sig, so a walk that trusted the field type alone would report
        // Sig2 twice and turn a two-signature document into a three-signature one.
        byte[] pdf = BuildTwiceSignedPdf();

        using var ms = new MemoryStream(pdf);
        var info = PdfOperations.InspectIncrementalSave(ms);

        Assert.Equal(new[] { "Parent.Sig1", "Sig2" },
            info.SignatureReports.Select(r => r.FieldName).OrderBy(n => n, StringComparer.Ordinal));
        Assert.False(info.SignatureReports.Single(r => r.FieldName == "Parent.Sig1").IsOutermost);
        Assert.True(info.SignatureReports.Single(r => r.FieldName == "Sig2").IsOutermost);
    }

    [Fact]
    public void InspectIncrementalSave_TwoFieldNodesNamingOneSignatureDictionary_ReportItOnce()
    {
        // The other half of the pair: here the widget carries its own /V naming the same object,
        // so the value-less-widget test above cannot fire and identity de-duplication is the only
        // thing left. A two-signature document must not report as a three-signature one.
        byte[] pdf = BuildTwiceSignedPdf(widgetRepeatsValue: true);

        using var ms = new MemoryStream(pdf);
        var info = PdfOperations.InspectIncrementalSave(ms);

        Assert.Equal(2, info.SignatureReports.Count);
        Assert.Equal(1, info.SignatureReports.Count(r => r.FieldName == "Sig2"));
    }

    [Fact]
    public void InspectIncrementalSave_IndirectFieldsArray_StillFindsTheSignature()
    {
        // /Fields written as "7 0 R". The non-resolving read this replaced returned null for it,
        // which is indistinguishable from a form with no fields at all.
        byte[] pdf = BuildSignedPdf(indirectFields: true);

        using var ms = new MemoryStream(pdf);
        var info = PdfOperations.InspectIncrementalSave(ms);

        var only = Assert.Single(info.SignatureReports);
        Assert.Equal("Parent.Sig1", only.FieldName);
        Assert.True(only.CoversWholeFile);
    }

    // ================================================================
    //  Attribution (PdfRevisions.ListWithSignatures)
    // ================================================================

    [Fact]
    public void ListWithSignatures_AttributesEachSignatureToTheRevisionItCovers()
    {
        byte[] pdf = BuildTwiceSignedPdf();

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.True(attribution.SignaturesExamined);
        Assert.Equal(2, attribution.Revisions.Count);
        Assert.Equal(new[] { "Parent.Sig1" }, attribution.Revisions[0].SignedBy);
        Assert.Equal(new[] { "Sig2" }, attribution.Revisions[1].SignedBy);
        Assert.Empty(attribution.SignaturesCoveringNoRevision);
    }

    [Fact]
    public void ListWithSignatures_ASignatureDoesNotCoverTheRevisionsAppendedAfterIt()
    {
        // The distinction the whole feature turns on: Sig1's digest was computed before revision 1
        // existed, so it covers revision 0 and nothing later. An implementation that hands every
        // signature to every revision passes the test above and fails this one.
        byte[] pdf = BuildTwiceSignedPdf();

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.DoesNotContain("Parent.Sig1", attribution.Revisions[1].SignedBy);
        Assert.DoesNotContain("Sig2", attribution.Revisions[0].SignedBy);
    }

    [Fact]
    public void ListWithSignatures_ByteRangeEndingBeforeTheEolAfterEof_StillCoversTheRevision()
    {
        // A revision's Length includes the end-of-line after %%EOF; a signer that stops at the F
        // falls one byte short of it. That is the same revision, not a missing one.
        byte[] pdf = BuildSignedPdf(shortfall: 1);

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.Equal(new[] { "Parent.Sig1" }, Assert.Single(attribution.Revisions).SignedBy);
        Assert.Empty(attribution.SignaturesCoveringNoRevision);
    }

    [Fact]
    public void ListWithSignatures_ByteRangeEndingMidFile_IsReportedAsCoveringNoRevision()
    {
        // Twenty bytes short lands inside the trailer — a real boundary is never that close, so
        // this signature covers no revision and the caller is told rather than left to infer it
        // from a shorter list than it expected.
        byte[] pdf = BuildSignedPdf(shortfall: 20);

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.True(attribution.SignaturesExamined);
        Assert.Empty(Assert.Single(attribution.Revisions).SignedBy);
        Assert.Equal(new[] { "Parent.Sig1" }, attribution.SignaturesCoveringNoRevision);
    }

    [Fact]
    public void ListWithSignatures_MalformedNegativeByteRangeLength_CoversNoRevision()
    {
        // Adversarial: /ByteRange [0 n <fileLength> -1] arithmetically ends one byte before the
        // file does, which is inside the tolerance a legitimate signer gets. A range with a
        // negative segment covers nothing, and the operands are checked before the sum for
        // exactly this reason.
        byte[] pdf = BuildSignedPdf();
        SetByteRange(pdf, 0, "0000000000 " + pdf.Length.ToString("D10") + " -000000001");

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.Empty(Assert.Single(attribution.Revisions).SignedBy);
        Assert.Equal(new[] { "Parent.Sig1" }, attribution.SignaturesCoveringNoRevision);
    }

    /// <summary>
    /// <b>Adversarial: the two malformed operands cancel.</b> The test above has a negative
    /// <i>length</i>, which the overflow test rejects on its own — subtracting a negative from
    /// <c>long.MaxValue</c> wraps, so that comparison refuses every input and the explicit negative
    /// check never decides anything. This is the shape where it does: a negative <c>offset2</c>
    /// paid back by an oversized <c>length2</c>, so the sum lands <b>exactly</b> on the revision
    /// boundary while neither operand is anywhere near large enough to overflow.
    ///
    /// <para>Without the operand check a forged <c>/ByteRange</c> would therefore be reported as
    /// covering a revision it does not describe — a wrong answer to a signature-coverage question,
    /// which is the one direction this API must not err in.</para>
    /// </summary>
    [Fact]
    public void ListWithSignatures_ANegativeSecondOffsetCancelledByItsLength_CoversNoRevision()
    {
        byte[] pdf = BuildSignedPdf();

        // /ByteRange [0 100 -100 <fileLength + 100>]  ⇒  -100 + (len + 100) == len exactly.
        SetByteRange(pdf, 0, "0000000100 -000000100 " + (pdf.Length + 100).ToString("D10"));

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.True(attribution.SignaturesExamined);
        Assert.Empty(Assert.Single(attribution.Revisions).SignedBy);
        Assert.Equal(new[] { "Parent.Sig1" }, attribution.SignaturesCoveringNoRevision);
    }

    /// <summary>
    /// A signature field with no <c>/T</c> anywhere in its <c>/Parent</c> chain is legal and
    /// unnameable. It is attributed under a placeholder rather than dropped: dropping it would
    /// under-report how many signatures cover a revision, and a caller counting <c>SignedBy</c>
    /// entries would never learn the document claimed one more than it could name.
    /// </summary>
    [Fact]
    public void ListWithSignatures_ASignatureFieldWithNoNameAnywhereIsStillAttributed()
    {
        byte[] pdf = BuildSignedPdf(unnamed: true);

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.True(attribution.SignaturesExamined);
        Assert.Equal(new[] { "(unnamed)" }, Assert.Single(attribution.Revisions).SignedBy);
        Assert.Empty(attribution.SignaturesCoveringNoRevision);
    }

    [Fact]
    public void ListWithSignatures_UnsignedDocument_ReportsAnExaminedFormWithNoSignatures()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMinimalPdf();

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.True(attribution.SignaturesExamined);
        Assert.Null(attribution.NotExaminedReason);
        // Examined and empty — NOT null, which is what "we never looked" means.
        Assert.Empty(Assert.Single(attribution.Revisions).SignedBy);
    }

    [Fact]
    public void ListWithSignatures_EncryptedDocument_SaysTheSignaturesWereNotExamined()
    {
        // The form is unreadable here — /T is a string and strings are encrypted — so the honest
        // answer is "not examined". Returning an empty attribution would read as "unsigned".
        byte[] pdf = BuildSignedPdf(encrypted: true);

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.False(attribution.SignaturesExamined);
        Assert.Contains("encrypted", attribution.NotExaminedReason, StringComparison.Ordinal);
        // The byte-level half still stands: the revisions are enumerated regardless.
        Assert.Equal((long)pdf.Length, Assert.Single(attribution.Revisions).Length);
        Assert.Null(attribution.Revisions[0].SignedBy);
    }

    /// <summary>
    /// The trailer points <c>/Root</c> at an object that is not there. The parser recovers the
    /// catalog from the file's own <c>/Type /Catalog</c> object (GAPS §7c), so the
    /// document opens and the attribution runs: the signature is read, and the revision is its.
    /// </summary>
    [Fact]
    public void ListWithSignatures_MalformedCatalogReference_IsRecovered_AndTheSignatureAttributed()
    {
        byte[] pdf = BuildSignedPdf();
        int at = IndexOf(pdf, "/Root 1 0 R", 0);
        Buffer.BlockCopy(Encoding.ASCII.GetBytes("/Root 9 0 R"), 0, pdf, at, "/Root 9 0 R".Length);

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.True(attribution.SignaturesExamined, attribution.NotExaminedReason);
        Assert.Equal((long)pdf.Length, Assert.Single(attribution.Revisions).Length);
        Assert.NotNull(attribution.Revisions[0].SignedBy);
        Assert.Contains("names object 9", Core.Parsing.PdfParser.LastRootRecovery, StringComparison.Ordinal);
    }

    [Fact]
    public void ListWithSignatures_NoRecoverableCatalog_KeepsTheRevisionsAndSaysWhy()
    {
        // The trailer points /Root at an object that is not there AND the catalog object no longer
        // says /Type /Catalog, so the recovery scan finds nothing and the document will not open.
        // The revision list is a byte-level read and owes the object graph nothing, so it must
        // survive — while the attribution half says, in as many words, that it did not run.
        byte[] pdf = BuildSignedPdf();
        int at = IndexOf(pdf, "/Root 1 0 R", 0);
        Buffer.BlockCopy(Encoding.ASCII.GetBytes("/Root 9 0 R"), 0, pdf, at, "/Root 9 0 R".Length);
        int type = IndexOf(pdf, "/Type /Catalog", 0);
        Buffer.BlockCopy(Encoding.ASCII.GetBytes("/Type /Catalox"), 0, pdf, type, "/Type /Catalox".Length);

        using var input = new MemoryStream(pdf);
        var attribution = PdfRevisions.ListWithSignatures(input);

        Assert.False(attribution.SignaturesExamined);
        Assert.Contains("object graph", attribution.NotExaminedReason, StringComparison.Ordinal);
        Assert.Equal((long)pdf.Length, Assert.Single(attribution.Revisions).Length);
        Assert.Null(attribution.Revisions[0].SignedBy);
        Assert.Empty(attribution.SignaturesCoveringNoRevision);
    }

    [Fact]
    public void List_DoesNotClaimToHaveExaminedTheSignatures()
    {
        // The plain byte-level enumeration never opens the object graph, so it must leave the
        // three-state property at "not examined" rather than at an empty list.
        byte[] pdf = BuildSignedPdf();

        using var input = new MemoryStream(pdf);
        var revisions = PdfRevisions.List(input);

        Assert.Null(Assert.Single(revisions).SignedBy);
    }

    [Fact]
    public void GetRevisionsWithSignatures_FacadeAgreesWithTheOperation()
    {
        byte[] pdf = BuildTwiceSignedPdf();

        using var viaFacade = new MemoryStream(pdf);
        using var viaOperation = new MemoryStream(pdf);
        var a = PdfOperations.GetRevisionsWithSignatures(viaFacade);
        var b = PdfRevisions.ListWithSignatures(viaOperation);

        Assert.Equal(b.SignaturesExamined, a.SignaturesExamined);
        Assert.Equal(b.Revisions.Count, a.Revisions.Count);
        for (int i = 0; i < a.Revisions.Count; i++)
            Assert.Equal(b.Revisions[i].SignedBy, a.Revisions[i].SignedBy);
    }
}
