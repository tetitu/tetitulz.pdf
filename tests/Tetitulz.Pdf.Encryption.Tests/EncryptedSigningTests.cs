// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="PdfEncryptionOperations.Sign"/> and <see cref="PdfEncryptionOperations.VerifySignatures"/>:
/// a password-encrypted document signed <b>in place</b> — the revision appended encrypted under the
/// document's own file key — and verified from its two views.
/// </summary>
/// <remarks>
/// <para><b>The raw bytes are read, because a writer that emits raw bytes must be read as raw
/// bytes.</b> Parsing the output cannot tell an encrypted <c>/Reason</c> from a clear one, and
/// cannot tell a <c>/Contents</c> written in the clear — as ISO 32000-2 §7.6.2 requires — from one
/// the writer encrypted and the reader then "decrypted" into the same digits by luck. So the reason
/// text is asserted absent from the file and the CMS hex asserted present in it.</para>
/// </remarks>
public sealed class EncryptedSigningTests
{
    private const string Password = "sign-me-7f";
    private const string Reason = "Encrypted signing reason 8f3a1c";

    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void AnEncryptedDocumentIsSignedInPlaceAndTheSignatureVerifies(EncryptionAlgorithm algorithm)
    {
        byte[] encrypted = Encrypt(MinimalPdf(), algorithm);
        var (pfx, pw) = Certificate();

        using var output = new MemoryStream();
        SignaturePlan plan = PdfEncryptionOperations.Sign(
            new MemoryStream(encrypted, writable: false), output, Password, Parameters(pfx, pw));
        byte[] signed = output.ToArray();

        // Still encrypted, and the original bytes are the output's prefix — an in-place revision.
        Assert.Equal("Signature1", plan.FieldName);
        Assert.True(signed.Length > encrypted.Length, "nothing was appended");
        Assert.Equal(encrypted, signed.Take(encrypted.Length).ToArray());
        using (var doc = PdfDocument.Open(new MemoryStream(signed)))
        {
            Assert.True(doc.IsEncrypted);
            Assert.NotNull(doc.Trailer[PdfName.Encrypt]);
        }

        // Opens with the same password, and the decrypted view carries the signed field.
        byte[] decrypted = Decrypt(signed, Password);
        byte[] contentsAsStored;
        using (var view = PdfDocument.Open(new MemoryStream(decrypted)))
        {
            Assert.Single(view.Pages);
            var field = Assert.Single(view.AcroForm!.GetSignatureFields());
            Assert.Equal("Signature1", field.FieldName);
            Assert.True(field.HasSignature);
            Assert.Equal(Reason, field.SignatureValue!.GetString(PdfName.Reason)!.GetText());
            contentsAsStored = field.SignatureValue.GetString(PdfName.Contents)!.RawBytes;
        }

        // Raw bytes: the reason is ciphertext in the file, the /Contents hex is not (§7.6.2).
        string raw = Encoding.Latin1.GetString(signed);
        Assert.DoesNotContain(Reason, raw);
        Assert.Equal(0x30, contentsAsStored[0]);   // a DER SEQUENCE, not decrypted noise
        Assert.Contains("/Contents <" + Hex(contentsAsStored) + ">", raw);

        // And it verifies, from the two views.
        var result = Assert.Single(PdfEncryptionOperations.VerifySignatures(new MemoryStream(signed), Password));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        Assert.True(result.CoversEntireDocument);
        Assert.True(result.IsValid, string.Join("; ", result.ValidationErrors));
        Assert.Equal("Signature1", result.FieldName);
        Assert.Equal(Reason, result.Reason);
    }

    /// <summary>
    /// A named destination added to a signed encrypted document through
    /// <see cref="PdfEncryptedDestinations"/> leaves the signature intact — the reason that route
    /// appends rather than round-trips. It no longer covers the whole file, which is what an
    /// appended revision means; the round trip would have broken it.
    /// </summary>
    [Fact]
    public void ADestinationAddedAfterSigning_KeepsTheSignatureValid()
    {
        PdfEncryptionOperations.Register();
        byte[] encrypted = Encrypt(MinimalPdf(), EncryptionAlgorithm.AES128);
        var (pfx, pw) = Certificate();
        byte[] signed = SignBytes(encrypted, pfx, pw, "Signature1");

        using var output = new MemoryStream();
        PdfEncryptedDestinations.SetDestination(new MemoryStream(signed, writable: false), output, Password, "After-signing", 0);
        byte[] updated = output.ToArray();

        Assert.Equal(signed, updated.Take(signed.Length).ToArray());
        var result = Assert.Single(PdfEncryptionOperations.VerifySignatures(new MemoryStream(updated), Password));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        Assert.False(result.CoversEntireDocument);
        Assert.Single(Base.Operations.Structure.PdfNamedDestinations.ListDestinations(new MemoryStream(updated), Password));
    }

    [Fact]
    public void AWrongPasswordIsRefusedBeforeAnythingIsWritten()
    {
        byte[] encrypted = Encrypt(MinimalPdf(), EncryptionAlgorithm.AES128);
        var (pfx, pw) = Certificate();

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() => PdfEncryptionOperations.Sign(
            new MemoryStream(encrypted, writable: false), output, "not-the-password", Parameters(pfx, pw)));

        Assert.Contains("password", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void AnUnencryptedDocumentIsRefusedAndPointedAtTheSigner()
    {
        var (pfx, pw) = Certificate();

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfException>(() => PdfEncryptionOperations.Sign(
            new MemoryStream(MinimalPdf(), writable: false), output, Password, Parameters(pfx, pw)));

        Assert.Contains("not encrypted", ex.Message);
        Assert.Contains("PdfSigner", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// <b>A PDF MAC-protected document is signed with the token attached to the signature</b> —
    /// ISO/TS 32004 §6.5.2's <c>AttachedToSig</c> placement, which this used to refuse.
    /// </summary>
    /// <remarks>
    /// <para>The two placements are not interchangeable and neither could stand in for the other:
    /// a standalone token would cover the signature's own placeholders and stop verifying the
    /// moment the CMS was written into them, while the signature's <c>/ByteRange</c> covers the
    /// token. The token therefore travels inside the CMS as an unsigned attribute, and the trailer's
    /// <c>AuthCode</c> dictionary only says where to find it.</para>
    ///
    /// <para><b>The attribute is read back with the BCL's CMS parser, not the one that wrote it.</b>
    /// BouncyCastle building a structure and BouncyCastle reading it back agree by construction;
    /// <c>System.Security.Cryptography.Pkcs</c> is an independent implementation, so finding the
    /// attribute there is evidence about the bytes rather than about our own round trip.</para>
    /// </remarks>
    [Fact]
    public void APdfMacProtectedDocumentIsSignedWithTheTokenAttachedToTheSignature()
    {
        byte[] sealedDoc = Encrypt(MinimalPdf(), EncryptionAlgorithm.AES256, pdfMac: true);
        var (pfx, pw) = Certificate();

        using var output = new MemoryStream();
        SignaturePlan plan = PdfEncryptionOperations.Sign(
            new MemoryStream(sealedDoc, writable: false), output, Password, Parameters(pfx, pw));
        byte[] signed = output.ToArray();

        // Table 6, and what is absent is required to be absent.
        using (var doc = PdfDocument.Open(new MemoryStream(signed)))
        {
            var authCode = Assert.IsType<PdfDictionary>(doc.Trailer![PdfName.AuthCode]);
            Assert.Equal("AttachedToSig", authCode.GetName(PdfName.MACLocation)!.Value);

            // ReferenceObjectNumber is what the reference POINTS AT. PdfObject.ObjectNumber is the
            // parser's stamp for the object carrying it, which for a direct reference in a trailer
            // is 0 — it compiles, it reads naturally, and it asserts nothing.
            var sigRef = Assert.IsType<PdfReference>(authCode[PdfName.SigObjRef]);
            Assert.Equal(plan.SignatureObjectNumber, sigRef.ReferenceObjectNumber);

            Assert.Null(authCode[PdfName.ByteRange]);
            Assert.Null(authCode[PdfName.MAC]);
        }

        // The token itself, found by an independent CMS parser.
        byte[] contents;
        using (var view = PdfDocument.Open(new MemoryStream(Decrypt(signed, Password))))
            contents = view.AcroForm!.GetSignatureFields().Single()
                           .SignatureValue!.GetString(PdfName.Contents)!.RawBytes;

        var cms = new System.Security.Cryptography.Pkcs.SignedCms();
        cms.Decode(CmsPrefix(contents));
        var attribute = Assert.Single(
            cms.SignerInfos[0].UnsignedAttributes.Cast<System.Security.Cryptography.CryptographicAttributeObject>()
               .Where(a => a.Oid.Value == "1.0.32004.1.2"));
        Assert.Single(attribute.Values);

        // And the signature it is attached to still verifies over the whole document.
        var result = Assert.Single(PdfEncryptionOperations.VerifySignatures(new MemoryStream(signed), Password));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        Assert.True(result.CoversEntireDocument);
    }

    /// <summary>
    /// <b>Attaching the PDF MAC token does not change how the CMS is encoded.</b>
    /// </summary>
    /// <remarks>
    /// <para>The post-processor decodes the finished signature, adds an unsigned attribute and
    /// re-encodes it. Re-encoding is exactly where a structure silently changes representation, and
    /// the length form is invisible to every assertion about what the CMS <i>means</i> — both forms
    /// parse to the same SignedData, and this library's own reader would never notice.</para>
    ///
    /// <para>So the two are compared against each other rather than against a constant: whatever
    /// the ordinary path produces, the MAC path must produce the same. That makes this a test about
    /// the change rather than about BouncyCastle's defaults, which are not ours to pin.</para>
    /// </remarks>
    [Fact]
    public void AttachingTheMacTokenLeavesTheCmsEncodingAsTheOrdinaryPathWritesIt()
    {
        var (pfx, pw) = Certificate();

        byte[] plain = SignedContents(Encrypt(MinimalPdf(), EncryptionAlgorithm.AES256), pfx, pw);
        byte[] withMac = SignedContents(
            Encrypt(MinimalPdf(), EncryptionAlgorithm.AES256, pdfMac: true), pfx, pw);

        // Byte 0 is the tag, byte 1 the length form: 0x80 is indefinite (BER), anything else is a
        // declared length (DER's form). Comparing both bytes compares the encoding, not the content.
        Assert.Equal(plain[0], withMac[0]);
        Assert.Equal(plain[1], withMac[1]);

        // And the form itself, absolutely rather than relatively. ISO 32000-2 §12.8.3.3.1 and the
        // CAdES/PAdES profiles require DER, whose lengths are always declared; an indefinite length
        // is the one thing that cannot be DER. Asserting only that the two paths AGREE is blind to
        // a change that moves both — which is precisely how the BER encoding went unnoticed.
        Assert.Equal(0x30, plain[0]);
        Assert.NotEqual(0x80, plain[1]);
    }

    /// <summary>
    /// <b>A reserve that fits the signature but not the signature plus its PDF MAC token is
    /// refused, and the refusal says why.</b>
    /// </summary>
    /// <remarks>
    /// <para>The token is attached <i>after</i> the CMS has been built and measured, so the fit
    /// check the signer already performs was made against a value that is no longer the one being
    /// written. Without a second check the enlarged CMS reaches the placeholder it does not fit.</para>
    ///
    /// <para><b>The reserve is derived from a real signature rather than guessed.</b> A hard-coded
    /// number would drift with the certificate, the digest or BouncyCastle's encoding, and the test
    /// would then either stop overflowing (passing while asserting nothing) or overflow at the
    /// first check instead of the second (passing for the wrong reason). Measuring the ordinary
    /// signature and adding a small margin puts the reserve provably between the two sizes.</para>
    /// </remarks>
    [Fact]
    public void AReserveTooSmallForTheMacTokenIsRefusedNamingTheToken()
    {
        var (pfx, pw) = Certificate();

        int plainLength = CmsPrefix(
            SignedContents(Encrypt(MinimalPdf(), EncryptionAlgorithm.AES256), pfx, pw)).Length;

        SignatureParameters parameters = Parameters(pfx, pw);
        parameters.EstimatedSignatureSize = plainLength + 64;

        using var output = new MemoryStream();
        var ex = Assert.Throws<PdfSigningException>(() => PdfEncryptionOperations.Sign(
            new MemoryStream(Encrypt(MinimalPdf(), EncryptionAlgorithm.AES256, pdfMac: true), writable: false),
            output, Password, parameters));

        Assert.Contains("PDF MAC token", ex.Message, StringComparison.Ordinal);
        Assert.Contains("EstimatedSignatureSize", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Signs <paramref name="encrypted"/> and returns the signature's <c>/Contents</c>.</summary>
    private static byte[] SignedContents(byte[] encrypted, byte[] pfx, string pw)
    {
        using var output = new MemoryStream();
        PdfEncryptionOperations.Sign(
            new MemoryStream(encrypted, writable: false), output, Password, Parameters(pfx, pw));

        using var view = PdfDocument.Open(new MemoryStream(Decrypt(output.ToArray(), Password)));
        return view.AcroForm!.GetSignatureFields().Single()
                   .SignatureValue!.GetString(PdfName.Contents)!.RawBytes;
    }

    /// <summary>
    /// The CMS alone, taken from the fixed-width <c>/Contents</c> reserve by reading the encoded
    /// structure's own extent.
    /// </summary>
    /// <remarks>
    /// <para><b>Not by trimming the trailing zero padding</b>, which is the obvious way and is
    /// wrong twice over: an encoding may legitimately end in a zero byte, and an indefinite-length
    /// one <i>always</i> does — its terminator is two of them.</para>
    ///
    /// <para><b>BER rules, not DER</b>, because the structure in the file is BER: BouncyCastle
    /// writes <c>SignedData</c> with indefinite lengths (<c>30 80 … 00 00</c>), so a reader that
    /// insists on a declared length finds one of zero and hands the parser two bytes.
    /// <see cref="System.Formats.Asn1.AsnDecoder"/> is used rather than a hand-rolled walk exactly
    /// because indefinite lengths nest.</para>
    /// </remarks>
    private static byte[] CmsPrefix(byte[] contents)
    {
        System.Formats.Asn1.AsnDecoder.ReadEncodedValue(
            contents, System.Formats.Asn1.AsnEncodingRules.BER,
            out _, out _, out int bytesConsumed);

        return contents.Take(bytesConsumed).ToArray();
    }

    /// <summary>
    /// The two-view verifier digests the ORIGINAL file: bytes appended after the signature are seen,
    /// and a byte changed inside the signed range breaks integrity.
    /// </summary>
    [Fact]
    public void VerifySignaturesReadsTheBytesOfTheOriginalFile()
    {
        // RC4: a stream cipher, so one flipped ciphertext byte garbles one byte of page content
        // and nothing else — under AES it could land in a padding block and fail the decrypt
        // itself, which is a different refusal from the one this asserts.
        byte[] encrypted = Encrypt(MinimalPdf(), EncryptionAlgorithm.RC4_128);
        var (pfx, pw) = Certificate();

        using var output = new MemoryStream();
        PdfEncryptionOperations.Sign(new MemoryStream(encrypted, writable: false), output, Password, Parameters(pfx, pw));
        byte[] signed = output.ToArray();

        byte[] appended = signed.Concat(Encoding.ASCII.GetBytes("\n% trailing bytes nobody signed\n")).ToArray();
        var trailing = Assert.Single(PdfEncryptionOperations.VerifySignatures(new MemoryStream(appended), Password));
        Assert.True(trailing.IsIntegrityValid, string.Join("; ", trailing.ValidationErrors));
        Assert.False(trailing.CoversEntireDocument, "the appended bytes were not seen; the decrypted copy's length was used");
        Assert.Contains(trailing.ValidationErrors, e => e.Contains("appended"));

        // A byte of the page's encrypted content stream — inside the original, inside the signed range.
        string raw = Encoding.Latin1.GetString(encrypted);
        int contentObject = raw.IndexOf("4 0 obj", StringComparison.Ordinal);
        int streamData = raw.IndexOf("stream\n", contentObject, StringComparison.Ordinal) + "stream\n".Length + 4;
        byte[] tampered = (byte[])signed.Clone();
        tampered[streamData] ^= 0x01;
        var broken = Assert.Single(PdfEncryptionOperations.VerifySignatures(new MemoryStream(tampered), Password));
        Assert.False(broken.IsIntegrityValid, "a changed byte inside the signed range verified");
    }

    /// <summary>
    /// A second signature on an already-signed encrypted document is a revision appended
    /// <i>after</i> the first, and the verifier judges what that revision changed — against the
    /// ORIGINAL's cross-reference offsets. Signing is what ISO 32000-2 Table 257 permits after a
    /// signature, so the first signature stays valid.
    /// </summary>
    /// <remarks>
    /// This is the case the other verify tests cannot reach: with nothing appended after a signature
    /// the change analysis never consults its offset source, so an analysis pointed at the decrypted
    /// copy — a single fresh serialisation whose offsets describe nothing in the file — passes them
    /// all. Here it would read the copy's bytes at the original's offsets and report the first
    /// signature's document changed beyond what it allows.
    /// </remarks>
    [Fact]
    public void ASecondSignatureRevisionIsJudgedAgainstTheOriginalsOffsets()
    {
        byte[] encrypted = Encrypt(MinimalPdf(), EncryptionAlgorithm.AES128);
        var (pfx, pw) = Certificate();

        byte[] once = SignBytes(encrypted, pfx, pw, "Signature1");
        byte[] twice = SignBytes(once, pfx, pw, "Signature2");
        Assert.Equal(once, twice.Take(once.Length).ToArray());   // the second is a revision after the first

        var results = PdfEncryptionOperations.VerifySignatures(new MemoryStream(twice), Password);
        Assert.Equal(2, results.Count);
        var first = Assert.Single(results, r => r.FieldName == "Signature1");
        var second = Assert.Single(results, r => r.FieldName == "Signature2");

        Assert.True(first.IsIntegrityValid, string.Join("; ", first.ValidationErrors));
        Assert.True(second.IsIntegrityValid, string.Join("; ", second.ValidationErrors));
        Assert.False(first.CoversEntireDocument);
        Assert.True(second.CoversEntireDocument);
        Assert.True(first.IsValid, string.Join("; ", first.ValidationErrors));
        Assert.True(second.IsValid, string.Join("; ", second.ValidationErrors));
    }

    /// <summary>
    /// The shadow attack on an encrypted document: a revision after the signature rewrites the
    /// page's content stream. The CMS still verifies over what it signed; what reports the rewrite
    /// is the change analysis, and it finds the rewritten object only through the ORIGINAL's
    /// cross-reference offsets — the decrypted copy is one fresh serialisation in which nothing
    /// sits past the signed end, so an analysis pointed at it reports the document intact.
    /// </summary>
    [Fact]
    public void APageRewrittenAfterSigningIsReportedFromTheOriginalsOffsets()
    {
        byte[] encrypted = Encrypt(MinimalPdf(), EncryptionAlgorithm.AES128);
        var (pfx, pw) = Certificate();
        byte[] signed = SignBytes(encrypted, pfx, pw, "Signature1");

        // A later revision, encrypted like any other, that supersedes the page's content stream.
        using var rewritten = new MemoryStream();
        PdfEncryptedIncrementalUpdate.Run(new MemoryStream(signed, writable: false), rewritten, Password,
            (doc, firstFree) =>
            {
                var content = new PdfStream();
                content.RawData = Encoding.ASCII.GetBytes("BT /F1 12 Tf 72 700 Td (rewritten after signing) Tj ET\n");
                return new[] { new IndirectObject(4, 0, content) };
            });
        byte[] attacked = rewritten.ToArray();
        Assert.Equal(signed, attacked.Take(signed.Length).ToArray());

        var result = Assert.Single(PdfEncryptionOperations.VerifySignatures(new MemoryStream(attacked), Password));
        Assert.True(result.IsIntegrityValid, string.Join("; ", result.ValidationErrors));
        Assert.False(result.CoversEntireDocument);
        Assert.Contains(result.ValidationErrors, e => e.StartsWith("Changed after signing", StringComparison.Ordinal));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void VerifySignaturesRefusesAnUnencryptedDocument()
    {
        var ex = Assert.Throws<PdfException>(() =>
            PdfEncryptionOperations.VerifySignatures(new MemoryStream(MinimalPdf(), writable: false), Password));
        Assert.Contains("not encrypted", ex.Message);
    }

    /// <summary>
    /// The read side of §7.6.2: the whole-file decryptor leaves a signature's <c>/Contents</c> as
    /// stored while it decrypts everything around it. Asserted independently of the verifier,
    /// which would report the same defect as a broken digest.
    /// </summary>
    [Fact]
    public void TheDecryptorLeavesASignatureContentsAsStored()
    {
        byte[] encrypted = Encrypt(MinimalPdf(), EncryptionAlgorithm.AES256);
        var (pfx, pw) = Certificate();

        using var output = new MemoryStream();
        PdfEncryptionOperations.Sign(new MemoryStream(encrypted, writable: false), output, Password, Parameters(pfx, pw));
        byte[] signed = output.ToArray();

        // The hex digits as they sit in the file, read raw.
        string raw = Encoding.Latin1.GetString(signed);
        int at = raw.LastIndexOf("/Contents <", StringComparison.Ordinal) + "/Contents <".Length;
        string storedHex = raw.Substring(at, raw.IndexOf('>', at) - at);

        using var view = PdfDocument.Open(new MemoryStream(Decrypt(signed, Password)));
        var field = Assert.Single(view.AcroForm!.GetSignatureFields());
        Assert.Equal(storedHex, Hex(field.SignatureValue!.GetString(PdfName.Contents)!.RawBytes));
        Assert.Equal(Reason, field.SignatureValue.GetString(PdfName.Reason)!.GetText());   // …and the rest was decrypted
    }

    /// <summary>
    /// The write side of §7.6.2 in the whole-file encryptor, which re-serialises a document it is
    /// given: a signature dictionary already in the input keeps its <c>/Contents</c> in the clear
    /// while its <c>/Reason</c> is encrypted like every other string.
    /// </summary>
    [Fact]
    public void TheWholeFileEncryptorLeavesASignatureContentsInTheClear()
    {
        byte[] encrypted = Encrypt(PdfWithSignatureDictionary(), EncryptionAlgorithm.AES128);

        string raw = Encoding.Latin1.GetString(encrypted);
        Assert.Contains("/Contents <3081020102>", raw);
        Assert.DoesNotContain("(a reason in the clear)", raw);

        using var view = PdfDocument.Open(new MemoryStream(Decrypt(encrypted, Password)));
        var sig = (PdfDictionary)view.GetObject(5);
        Assert.Equal(new byte[] { 0x30, 0x81, 0x02, 0x01, 0x02 }, sig.GetString(PdfName.Contents)!.RawBytes);
        Assert.Equal("a reason in the clear", sig.GetString(PdfName.Reason)!.GetText());
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A one-page PDF: objects 1..5, no AcroForm.</summary>
    private static byte[] MinimalPdf()
    {
        const string content = "BT /F1 12 Tf 72 700 Td (signed content) Tj ET\n";
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        return Assemble(sb, offsets);
    }

    /// <summary>The same page, object 5 being an (unverifiable) signature dictionary with a clear-text <c>/Reason</c>.</summary>
    private static byte[] PdfWithSignatureDictionary()
    {
        const string content = "BT 72 700 Td (signed content) Tj ET\n";
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached"
          + " /ByteRange [0 10 20 30] /Contents <3081020102> /Reason (a reason in the clear) >>\nendobj\n");
        return Assemble(sb, offsets);
    }

    private static byte[] Assemble(StringBuilder sb, List<int> offsets)
    {
        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", offsets.Count + 1, xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Encrypt(byte[] plaintext, EncryptionAlgorithm algorithm, bool pdfMac = false)
    {
        using var plain = new MemoryStream(plaintext);
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = Password,
            OwnerPassword = "owner-" + Password,
            Algorithm = algorithm,
            EnablePdfMac = pdfMac,
        });
        return encrypted.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, string password)
    {
        using var source = new MemoryStream(encrypted, writable: false);
        using var plain = new MemoryStream();
        PdfDecryptor.Decrypt(source, plain, password);
        return plain.ToArray();
    }

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    private static byte[] SignBytes(byte[] encrypted, byte[] pfx, string pw, string fieldName)
    {
        using var output = new MemoryStream();
        var parameters = Parameters(pfx, pw);
        parameters.SignatureFieldName = fieldName;
        PdfEncryptionOperations.Sign(new MemoryStream(encrypted, writable: false), output, Password, parameters);
        return output.ToArray();
    }

    private static SignatureParameters Parameters(byte[] pfx, string pw) => new()
    {
        CertificateStream = new MemoryStream(pfx),
        CertificatePassword = pw,
        SignatureFieldName = "Signature1",
        Reason = Reason,
        Location = "Tel Aviv",
    };

    // ------------------------------------------------------------------ certificates

    /// <summary>
    /// A root and a leaf, in memory, packaged as PKCS#12 — the least of
    /// <c>Tetitulz.Pdf.Signing.Tests.SigningTestCrypto</c> this file needs. Duplicated because test
    /// projects may not reference one another (CLAUDE.md).
    /// </summary>
    private static (byte[] Pfx, string Password) Certificate()
    {
        var random = new SecureRandom();
        // From the per-test pool: distinct within the test, reused across tests (RsaTestKeys).
        AsymmetricCipherKeyPair Keys() => Tetitulz.Pdf.TestHelpers.RsaTestKeys.Next();
        X509V3CertificateGenerator NewGen(X509Name issuer, X509Name subject, AsymmetricKeyParameter publicKey)
        {
            var gen = new X509V3CertificateGenerator();
            gen.SetSerialNumber(BigInteger.ValueOf(DateTime.UtcNow.Ticks));
            gen.SetIssuerDN(issuer);
            gen.SetSubjectDN(subject);
            gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
            gen.SetNotAfter(DateTime.UtcNow.AddYears(5));
            gen.SetPublicKey(publicKey);
            return gen;
        }

        var rootKeys = Keys();
        var rootDn = new X509Name("CN=Encrypted Signing Root");
        var rootGen = NewGen(rootDn, rootDn, rootKeys.Public);
        rootGen.AddExtension(X509Extensions.BasicConstraints, true, new BasicConstraints(true));
        rootGen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.DigitalSignature));
        X509Certificate root = rootGen.Generate(new Asn1SignatureFactory("SHA256WithRSA", rootKeys.Private, random));

        var leafKeys = Keys();
        var leafGen = NewGen(rootDn, new X509Name("CN=Encrypted Signing Leaf"), leafKeys.Public);
        leafGen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        X509Certificate leaf = leafGen.Generate(new Asn1SignatureFactory("SHA256WithRSA", rootKeys.Private, random));

        const string password = "test123";
        var store = new Org.BouncyCastle.Pkcs.Pkcs12StoreBuilder().Build();
        store.SetKeyEntry("test", new Org.BouncyCastle.Pkcs.AsymmetricKeyEntry(leafKeys.Private),
            new[] { new Org.BouncyCastle.Pkcs.X509CertificateEntry(leaf), new Org.BouncyCastle.Pkcs.X509CertificateEntry(root) });
        using var ms = new MemoryStream();
        store.Save(ms, password.ToCharArray(), random);
        return (ms.ToArray(), password);
    }
}
