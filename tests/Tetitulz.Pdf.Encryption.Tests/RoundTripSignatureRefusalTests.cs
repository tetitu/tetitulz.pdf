// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="PdfEncryptedRoundTrip"/> refuses a <b>signed</b> document unless the caller sets
/// <see cref="EncryptedRoundTripOptions.AllowSignatureInvalidation"/>.
/// </summary>
/// <remarks>
/// <para><b>The refusal is the point of the class's safety contract.</b> Neutralising the
/// <c>AllowSignatureInvalidation</c> gate, or the <c>HasSignature</c> check that feeds it, leaves
/// the rest of Encryption's and Accessibility's suites green.</para>
///
/// <para><b>What the refusal protects.</b> The round trip decrypts, runs the caller's operation, and
/// re-encrypts — which rewrites every string and stream in the file. A signature's
/// <c>/ByteRange</c> then covers bytes that no longer exist, so the signature is destroyed. The
/// document still opens and still looks signed; verification simply fails. Producing that silently
/// from a call the caller believed was a content edit is the outcome the gate exists to prevent, and
/// it is exactly the kind of damage a green test suite is supposed to make impossible to ship.</para>
///
/// <para><b>The fixture is a signature field with a value, and it has to be.</b> <c>/SigFlags 1</c>
/// on a form with <c>/Fields []</c> is not a signed document: §12.7.2 makes bit 1 mean a signature
/// <i>field</i> exists — not that anything is signed — so the round trip could rewrite it without
/// destroying anything. The guard asks
/// <c>PdfSignatureDetection.HasSignature</c>, the same question the optimizer, the sanitizer, the
/// linearizer and both conformance converters ask, which requires a field with a <c>/V</c>. Writing
/// a signature dictionary by hand keeps this assembly clear of the signing stack, which it may not
/// reference.</para>
/// </remarks>
public class RoundTripSignatureRefusalTests
{
    private const string User = "secret";
    private const string Owner = "owner";

    /// <summary>The operation the round trip runs between decrypt and re-encrypt.</summary>
    private static void CopyThrough(Stream input, Stream output) => input.CopyTo(output);

    private static RoundTripResult Run(byte[] encrypted, bool allowInvalidation, out byte[] produced)
    {
        using var input = new MemoryStream(encrypted, writable: false);
        using var output = new MemoryStream();

        RoundTripResult result = PdfEncryptedRoundTrip.Run(
            input, output,
            new EncryptedRoundTripOptions
            {
                OpenPassword = Owner,
                AllowSignatureInvalidation = allowInvalidation,
            },
            CopyThrough);

        produced = output.ToArray();
        return result;
    }

    /// <summary>
    /// The refusal itself — and it asserts the output was <b>never written</b>, not merely that a
    /// flag came back false. A guard that reported failure after producing a re-encrypted file would
    /// have destroyed the signature already.
    /// </summary>
    [Fact]
    public void ASignedDocumentIsRefusedAndNothingIsWritten()
    {
        RoundTripResult result = Run(EncryptedSignedPdf(), allowInvalidation: false, out byte[] produced);

        Assert.False(result.Succeeded);
        Assert.Empty(produced);
        Assert.Contains("signature", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Opting in proceeds — otherwise the refusal would be indistinguishable from a round trip that
    /// cannot handle this document at all, and the option would be untestable in either direction.
    /// </summary>
    [Fact]
    public void OptingInProceedsAndSaysTheSignatureWasInvalidated()
    {
        RoundTripResult result = Run(EncryptedSignedPdf(), allowInvalidation: true, out byte[] produced);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotEmpty(produced);
        Assert.Contains(result.Warnings, w => w.IndexOf("signature", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// The control that keeps the refusal from degenerating into "never round-trips": the same
    /// document without the signature flag goes through with no opt-in and no warning.
    /// </summary>
    [Fact]
    public void AnUnsignedDocumentNeedsNoOptIn()
    {
        RoundTripResult result = Run(EncryptedPdf(signed: false), allowInvalidation: false, out byte[] produced);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotEmpty(produced);
        Assert.DoesNotContain(result.Warnings,
            w => w.IndexOf("signature", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// A form whose signature field is <b>empty</b> round-trips with no opt-in: nothing is signed,
    /// so nothing can be invalidated.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the case the old guard got wrong.</b> It read <c>/SigFlags</c> bit 1 and
    /// refused, but §12.7.2 makes that bit say a signature <i>field</i> exists — a document prepared
    /// for signing, which every other whole-file rewriter in the library happily rewrites.
    /// <c>PdfSignatureDetection</c> says so in as many words at its own field walk: "an unsigned,
    /// empty signature field is a placeholder and rewriting it invalidates nothing". A rewriter that
    /// answers that question differently from the rest is the defect the shared predicate exists to
    /// prevent.</para>
    /// <para>The flag is set here <i>and</i> the field is present, so the only thing separating this
    /// fixture from the refused one is the field's <c>/V</c>.</para>
    /// </remarks>
    [Fact]
    public void AFormWhoseSignatureFieldIsEmptyNeedsNoOptIn()
    {
        RoundTripResult result = Run(EncryptedPdf(Signature.FieldWithoutValue),
                                     allowInvalidation: false, out byte[] produced);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.NotEmpty(produced);
        Assert.DoesNotContain(result.Warnings,
            w => w.IndexOf("signature", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// A certification signature reached only through <c>/Perms /DocMDP</c> — no signature field at
    /// all — is still refused.
    /// </summary>
    /// <remarks>
    /// The second half of what the shared predicate sees and a <c>/SigFlags</c> read never could:
    /// this document's form is empty and its flag unset, so the old guard's field-free path was its
    /// only chance and the fixture proves the walk is not the only route in.
    /// </remarks>
    [Fact]
    public void ACertifiedDocumentWithNoSignatureFieldIsRefused()
    {
        RoundTripResult result = Run(EncryptedPdf(Signature.PermsDocMdpOnly),
                                     allowInvalidation: false, out byte[] produced);

        Assert.False(result.Succeeded);
        Assert.Empty(produced);
        Assert.Contains("signature", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------

    /// <summary>What the fixture puts in the catalog.</summary>
    private enum Signature
    {
        /// <summary>No form at all.</summary>
        None,
        /// <summary>A signature field carrying a signature dictionary in its <c>/V</c>.</summary>
        FieldWithValue,
        /// <summary>A signature field prepared for signing, with no <c>/V</c>.</summary>
        FieldWithoutValue,
        /// <summary>An empty form, and a certification reached through <c>/Perms /DocMDP</c>.</summary>
        PermsDocMdpOnly,
    }

    private static byte[] EncryptedSignedPdf() => EncryptedPdf(Signature.FieldWithValue);

    private static byte[] EncryptedPdf(bool signed)
        => EncryptedPdf(signed ? Signature.FieldWithValue : Signature.None);

    private static byte[] EncryptedPdf(Signature shape)
    {
        using var plain = new MemoryStream(PlainPdf(shape));
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword  = User,
            OwnerPassword = Owner,
            Algorithm     = EncryptionAlgorithm.AES256,
        });
        return encrypted.ToArray();
    }

    /// <summary>
    /// A one-page document carrying the catalog entries <paramref name="shape"/> names. Every
    /// variant sets <c>/SigFlags</c> where a producer would, so the bit is never what separates
    /// them — only the signature field's <c>/V</c>, or <c>/Perms</c>, is.
    /// </summary>
    private static byte[] PlainPdf(Signature shape)
    {
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string catalogExtras = shape switch
        {
            Signature.FieldWithValue    => " /AcroForm << /Fields [4 0 R] /SigFlags 3 >>",
            Signature.FieldWithoutValue => " /AcroForm << /Fields [4 0 R] /SigFlags 3 >>",
            Signature.PermsDocMdpOnly   => " /AcroForm << /Fields [] /SigFlags 1 >> /Perms << /DocMDP 5 0 R >>",
            _                           => "",
        };

        sb.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R" + catalogExtras + " >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        // Object 4 is the field, 5 the signature dictionary. Both are written for every shape so
        // the cross-reference table is one size and a variant differs only in what points at them.
        Obj("4 0 obj\n<< /FT /Sig /T (Signature1) /Ff 0"
          + (shape == Signature.FieldWithValue ? " /V 5 0 R" : "") + " >>\nendobj\n");
        Obj("5 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
          + "/ByteRange [0 0 0 0] /Contents <00000000> >>\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
                        offsets.Count + 1, xref);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
