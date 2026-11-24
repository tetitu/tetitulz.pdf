// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// What the read-only operations do with an <b>encrypted</b> document.
/// </summary>
/// <remarks>
/// The rendering entry points decrypt (see <c>EncryptedRenderParityTests</c>). The extraction
/// entry points on <c>PdfOperations</c> take no password at all, so this file pins what a caller
/// actually observes — the point being that "returned an empty string" and "this PDF contains no
/// text" must not be the same answer.
/// </remarks>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.DecryptorHolderCollection.Name)]
public sealed class EncryptedExtractionTests
{
    private const string UserPassword = "s3cret";

    private static byte[] EncryptedTextPdf()
    {
        PdfEncryptionOperations.Register();

        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf(
            contentStream: "BT /F1 24 Tf 72 700 Td (Locked away) Tj ET"));
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }

    /// <summary>
    /// A one-page document with an outline item, a URI link annotation and an <c>/Info</c> author —
    /// one observable per converted read-only entry point (<c>GetBookmarks</c>,
    /// <c>ExtractLinks</c>, <c>GetMetadata</c>). All three observables are PDF <b>strings</b>, so
    /// each is ciphertext once the file is encrypted and reading it back is evidence of decryption
    /// rather than of enumeration.
    /// </summary>
    private static byte[] LinkedAndBookmarkedPdf()
    {
        var sb = new System.Text.StringBuilder();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append("%PDF-1.7\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                  + " /Contents 4 0 R /Annots [7 0 R] /Resources << >> >>\nendobj\n");

        const string cs = "q Q";
        int o4 = sb.Length;
        sb.AppendFormat(ci, "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
                        cs.Length, cs);

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>\nendobj\n");

        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Title (Chapter One) /Parent 5 0 R /Dest [3 0 R /Fit] >>\nendobj\n");

        int o7 = sb.Length;
        sb.Append("7 0 obj\n<< /Type /Annot /Subtype /Link /Rect [72 700 300 720] /Border [0 0 0]"
                  + " /A << /S /URI /URI (https://example.test/locked) >> >>\nendobj\n");

        int o8 = sb.Length;
        sb.Append("8 0 obj\n<< /Author (Encrypted Reader) >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5, o6, o7, o8 })
            sb.AppendFormat(ci, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 9 /Root 1 0 R /Info 8 0 R >>\n");
        sb.AppendFormat(ci, "startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] EncryptedLinkedAndBookmarkedPdf()
    {
        PdfEncryptionOperations.Register();

        using var input = new MemoryStream(LinkedAndBookmarkedPdf());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }

    /// <summary>
    /// A one-page document carrying a real AcroForm signature field. The <c>/Contents</c> and
    /// <c>/ByteRange</c> are placeholders — <c>GetSignatures</c> reads metadata and never verifies,
    /// so no actual CMS blob is needed to exercise it.
    /// </summary>
    private static byte[] SignedPdf()
    {
        var sb = new System.Text.StringBuilder();
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        sb.Append("%PDF-1.7\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R"
                  + " /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                  + " /Contents 4 0 R /Annots [5 0 R] /Resources << >> >>\nendobj\n");

        const string cs = "q Q";
        int o4 = sb.Length;
        sb.AppendFormat(ci, "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
                        cs.Length, cs);

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /T (Signature1)"
                  + " /Rect [0 0 0 0] /P 3 0 R /V 6 0 R >>\nendobj\n");

        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite"
                  + " /SubFilter /adbe.pkcs7.detached /Reason (Approval) /Location (Haifa)"
                  + " /M (D:20260808120000+03'00') /ByteRange [0 0 0 0] /Contents <00> >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5, o6 })
            sb.AppendFormat(ci, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat(ci, "startxref\n{0}\n%%EOF\n", xref);
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] SignedThenEncryptedPdf()
    {
        PdfEncryptionOperations.Register();

        using var input = new MemoryStream(SignedPdf());
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = "owner-only",
            Algorithm = EncryptionAlgorithm.AES128,
            Permissions = PdfPermissions.All,
        });
        return output.ToArray();
    }

    /// <summary>
    /// An encrypted, signed document must not be reported as unsigned.
    /// </summary>
    /// <remarks>
    /// <para><c>GetSignatures</c> returned an <b>empty list</b> for this document — a caller asking
    /// "is this signed?" was told <i>no</i>. That is a wrong answer to a security question, not a
    /// missing feature (GAPS.md §2c).</para>
    /// <para>It had been left out of the object-graph conversion on the grounds that a signature's
    /// <c>/ByteRange</c> indexes the file as stored, so a decrypted re-serialisation would digest
    /// bytes that never existed. That argument is sound — <b>about verification</b>, which lives in
    /// <c>Tetitulz.Pdf.Signing</c> and reads its own stream. <c>GetSignatures</c> only lists, and
    /// <c>PdfSignatureInfo</c> carries no <c>/ByteRange</c>, no offsets and no digest, so there is
    /// nothing for a re-serialisation to invalidate.</para>
    /// <para>The fixture is signed <b>and</b> encrypted deliberately: against the unsigned fixture
    /// an empty list is what a working and a broken reader both produce, so it could not tell them
    /// apart. <c>/Reason</c> is a <i>string</i>, hence encrypted in the file — reading it back
    /// proves the decryption actually happened rather than the field merely being enumerated.</para>
    /// </remarks>
    [Fact]
    public void EncryptedSignedDocument_SignaturesAreListedWhenThePasswordIsSupplied()
    {
        byte[] pdf = SignedThenEncryptedPdf();

        // Without the password the registered decryptor refuses, loudly — never a silent empty
        // list, which is the failure this test exists to prevent recurring.
        using (var ms = new MemoryStream(pdf))
            Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => PdfOperations.GetSignatures(ms));

        using (var ms = new MemoryStream(pdf))
        {
            var sigs = PdfOperations.GetSignatures(ms, UserPassword);

            var sig = Assert.Single(sigs);
            Assert.True(sig.HasSignature);
            Assert.Equal("Signature1", sig.FieldName);
            Assert.Equal("Approval", sig.Reason);          // encrypted string, decrypted back
            Assert.Equal("adbe.pkcs7.detached", sig.SubFilter);
        }
    }

    /// <summary>The control: the same document unencrypted lists the same signature.</summary>
    [Fact]
    public void PlainSignedDocument_ListsTheSameSignature()
    {
        using var ms = new MemoryStream(SignedPdf());

        var sig = Assert.Single(PdfOperations.GetSignatures(ms));
        Assert.Equal("Signature1", sig.FieldName);
        Assert.Equal("Approval", sig.Reason);
    }

    /// <summary>
    /// Every read-only entry point routed through <c>WithDecrypted</c> reads an encrypted document
    /// when given the password — and returns the <b>decrypted strings</b>, not merely a non-empty
    /// list.
    /// </summary>
    /// <remarks>
    /// <para>The fixture carries a bookmark title and a link URI on purpose. Both are PDF
    /// <i>strings</i>, so both are ciphertext in the file: reading them back proves decryption
    /// happened, where "the list has one entry" would also pass for a reader that merely enumerated
    /// the object graph without decrypting the leaves.</para>
    /// </remarks>
    [Fact]
    public void ConvertedReadOnlyEntryPoints_ReadAnEncryptedDocumentWhenGivenThePassword()
    {
        byte[] pdf = EncryptedLinkedAndBookmarkedPdf();

        // Without the password the registered decryptor refuses, loudly — never a silent empty
        // list, which is the shape this whole file exists to keep out.
        using (var ms = new MemoryStream(pdf))
            Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => PdfOperations.GetMetadata(ms));
        using (var ms = new MemoryStream(pdf))
            Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => PdfOperations.GetBookmarks(ms));
        using (var ms = new MemoryStream(pdf))
            Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() => PdfOperations.ExtractLinks(ms));

        using (var ms = new MemoryStream(pdf))
        {
            var bookmark = Assert.Single(PdfOperations.GetBookmarks(ms, UserPassword));
            Assert.Equal("Chapter One", bookmark.Title);      // encrypted string, decrypted back
        }

        using (var ms = new MemoryStream(pdf))
        {
            var link = Assert.Single(PdfOperations.ExtractLinks(ms, UserPassword));
            Assert.Equal("https://example.test/locked", link.Uri);
            Assert.Equal(Base.Operations.Extraction.PdfLinkKind.Uri, link.Kind);
        }

        using (var ms = new MemoryStream(pdf))
            Assert.Equal("Encrypted Reader", PdfOperations.GetMetadata(ms, UserPassword).Author);
    }

    /// <summary>
    /// With <b>no decryptor registered at all</b>, every reader routed through
    /// <c>WithDecrypted</c> says why it read nothing — it no longer reports an encrypted document
    /// as an empty one.
    /// </summary>
    /// <remarks>
    /// <para>This is defect (b) of the encrypted-read triage, and it is the silent-empty shape in
    /// its purest form: no decryptor means the bytes pass through unchanged, no exception is raised,
    /// the catalog is unreadable, and the reader answers "no bookmarks, no links, no metadata" —
    /// exactly what it answers for a plain document that genuinely has none.
    /// <c>ExtractText</c> was the only entry point guarding against it, and a guard that each of the
    /// ~44 remaining readers has to remember is one they will not.</para>
    /// <para>Putting the check inside <c>WithDecrypted</c> is what makes it inherited rather than
    /// remembered; asserting it here on <i>three</i> different readers is what pins that it is the
    /// helper reporting and not one method.</para>
    /// <para>The bookmark and link readers now refuse an encrypted document themselves
    /// (<c>Base.Operations.EncryptedRead</c>); the helper's reason is set before they are called,
    /// so it stands beside the refusal.</para>
    /// </remarks>
    [Fact]
    public void EncryptedDocument_WithNoDecryptorRegistered_ObjectGraphReadsRefuseOrSayWhy()
    {
        byte[] pdf = EncryptedLinkedAndBookmarkedPdf();   // registers, so save/restore around the calls
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;

            using (var ms = new MemoryStream(pdf))
            {
                Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(() => PdfOperations.GetBookmarks(ms));
                Assert.Contains("encrypted", PdfOperations.LastExtractionError,
                    System.StringComparison.OrdinalIgnoreCase);
            }

            using (var ms = new MemoryStream(pdf))
            {
                Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(() => PdfOperations.ExtractLinks(ms));
                Assert.Contains("encrypted", PdfOperations.LastExtractionError,
                    System.StringComparison.OrdinalIgnoreCase);
            }

            using (var ms = new MemoryStream(pdf))
            {
                PdfOperations.GetMetadata(ms);
                Assert.Contains("encrypted", PdfOperations.LastExtractionError,
                    System.StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { PdfDecryptorHolder.Current = saved; }
    }

    /// <summary>
    /// The diagnostic belongs to the <i>call</i>, not to the process: a successful object-graph read
    /// clears the reason an earlier failed one left behind.
    /// </summary>
    /// <remarks>
    /// The <c>LastPermsValidation</c> lesson, restated for this slot: <c>[ThreadStatic]</c> storage
    /// gives per-thread isolation, not per-document scope. A slot a caller reads <i>after</i> an
    /// operation has to be reset at the <i>start</i> of that operation, on every path — and
    /// <c>WithDecrypted</c> now serves four entry points, so the stale value would cross methods
    /// too, not just documents.
    /// </remarks>
    [Fact]
    public void ReadError_IsClearedByTheNextSuccessfulObjectGraphRead()
    {
        byte[] pdf = EncryptedLinkedAndBookmarkedPdf();
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;
            using var locked = new MemoryStream(pdf);
            Assert.Throws<Tetitulz.Pdf.Core.PdfEncryptedDocumentException>(() => PdfOperations.GetBookmarks(locked));
        }
        finally { PdfDecryptorHolder.Current = saved; }
        Assert.NotNull(PdfOperations.LastExtractionError);

        // A different reader, on a document that needs no decryption at all.
        using var plain = new MemoryStream(LinkedAndBookmarkedPdf());
        var link = Assert.Single(PdfOperations.ExtractLinks(plain));

        Assert.Equal("https://example.test/locked", link.Uri);
        Assert.Null(PdfOperations.LastExtractionError);
    }

    /// <summary>
    /// <b>Byte-level</b> operations are a third category, and they are already correct: they read
    /// the file as bytes rather than as an object graph, so encryption does not hide anything from
    /// them — and decrypting would <i>break</i> them, since a re-serialised file has entirely
    /// different offsets from the ones a revision boundary is defined by.
    /// </summary>
    /// <remarks>
    /// Recorded because the obvious generalisation — "read-only entry points are silent on
    /// encrypted input, so route them all through the decryptor" — is wrong here, and applying it
    /// would turn a working operation into a broken one.
    /// </remarks>
    [Fact]
    public void ByteLevelOperations_WorkOnAnEncryptedDocumentAndMustNotDecrypt()
    {
        byte[] pdf = EncryptedTextPdf();

        using var ms = new MemoryStream(pdf);
        var revisions = PdfOperations.GetRevisions(ms);

        // An encrypted file still has a %%EOF/startxref chain, so its revisions enumerate — and the
        // sole revision spans the WHOLE file. Asserting the byte length rather than "the list is
        // non-empty" is what makes this falsifiable: a reader that mis-locates the boundary still
        // returns a non-empty list, just a wrong one.
        var only = Assert.Single(revisions);
        Assert.Equal(0, only.Index);
        Assert.Equal(pdf.Length, only.Length);
    }

    /// <summary>The control: unencrypted, the fixture's text is extractable.</summary>
    [Fact]
    public void PlainDocument_TextIsExtracted()
    {
        using var ms = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf(
            contentStream: "BT /F1 24 Tf 72 700 Td (Locked away) Tj ET"));

        Assert.Contains("Locked away", PdfOperations.ExtractText(ms));
    }

    /// <summary>
    /// An encrypted document must not silently extract as empty: a caller cannot distinguish that
    /// from a scanned page with no text layer, which is a completely different problem with a
    /// completely different fix.
    /// </summary>
    [Fact]
    public void EncryptedDocument_DoesNotSilentlyExtractAsEmpty()
    {
        using var ms = new MemoryStream(EncryptedTextPdf());

        // With a decryptor registered, a missing password is a loud failure — the same contract the
        // raster path has ("a wrong password throws rather than silently rendering nothing").
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(
            () => PdfOperations.ExtractText(ms));
        Assert.Contains("password", ex.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ...and with <b>no decryptor registered at all</b> — the state a host that never referenced
    /// <c>Tetitulz.Pdf.Encryption</c> is in — it returns empty but records why.
    /// </summary>
    /// <remarks>
    /// This is the case that was silently wrong: empty text, no exception, nothing to inspect. The
    /// holder is nulled and restored around the call because it is process-global; the assertion
    /// would otherwise depend on whether some earlier test in the run had registered one.
    /// </remarks>
    [Fact]
    public void EncryptedDocument_WithNoDecryptorRegistered_ReportsWhyItReadNothing()
    {
        byte[] pdf = EncryptedTextPdf();          // registers, so capture and restore around the call
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;
            using var ms = new MemoryStream(pdf);

            string text = PdfOperations.ExtractText(ms);

            Assert.True(string.IsNullOrEmpty(text));
            Assert.NotNull(PdfOperations.LastExtractionError);
            Assert.Contains("encrypted", PdfOperations.LastExtractionError,
                System.StringComparison.OrdinalIgnoreCase);
        }
        finally { PdfDecryptorHolder.Current = saved; }
    }

    /// <summary>
    /// The <b>per-page</b> overload refuses the same way, and says so.
    /// </summary>
    /// <remarks>
    /// <para><b>An absolute assertion, because the parity test cannot be one.</b>
    /// <c>TextExtractionOverloadParityTests</c> compares the stream and file-path overloads against
    /// each other and explains why it must: what an encrypted document does depends on whether a
    /// decryptor is registered, which is process-wide state a neighbouring class may have set. That
    /// makes it blind in exactly one direction — a change applied to <i>both</i> sides leaves it
    /// green, which is CLAUDE.md's third blind spot.</para>
    /// <para>That direction is the live one now that both overloads share a single prologue: a
    /// break in the refusal is by construction applied to both. This pins the outcome for the page
    /// overload the way its whole-document sibling above already does.</para>
    /// </remarks>
    [Fact]
    public void EncryptedDocument_PerPage_WithNoDecryptorRegistered_ReportsWhyItReadNothing()
    {
        byte[] pdf = EncryptedTextPdf();
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;
            using var ms = new MemoryStream(pdf);

            string text = PdfOperations.ExtractText(ms, 1);

            Assert.True(string.IsNullOrEmpty(text));
            Assert.NotNull(PdfOperations.LastExtractionError);
            Assert.Contains("encrypted", PdfOperations.LastExtractionError,
                System.StringComparison.OrdinalIgnoreCase);
        }
        finally { PdfDecryptorHolder.Current = saved; }
    }

    /// <summary>With the password, the text comes out.</summary>
    [Fact]
    public void EncryptedDocument_WithPassword_ExtractsTheText()
    {
        using var ms = new MemoryStream(EncryptedTextPdf());

        string text = PdfOperations.ExtractText(ms, UserPassword);

        Assert.Contains("Locked away", text);
        Assert.Null(PdfOperations.LastExtractionError);
    }

    /// <summary>
    /// The diagnostic is reset at the start of every call, so a success after a failure does not
    /// leave the previous document's reason behind.
    /// </summary>
    /// <remarks>
    /// This is the <c>LastPermsValidation</c> lesson: a slot a caller reads <i>after</i> an
    /// operation has to be cleared <i>at the start</i> of that operation, on every path — being
    /// <c>[ThreadStatic]</c> does not make it per-document.
    /// </remarks>
    [Fact]
    public void ExtractionError_IsClearedByTheNextSuccessfulCall()
    {
        byte[] pdf = EncryptedTextPdf();
        var saved = PdfDecryptorHolder.Current;
        try
        {
            PdfDecryptorHolder.Current = null;
            using var locked = new MemoryStream(pdf);
            PdfOperations.ExtractText(locked);
        }
        finally { PdfDecryptorHolder.Current = saved; }
        Assert.NotNull(PdfOperations.LastExtractionError);

        using var plain = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf(
            contentStream: "BT /F1 24 Tf 72 700 Td (Open) Tj ET"));
        PdfOperations.ExtractText(plain);

        Assert.Null(PdfOperations.LastExtractionError);
    }
}
