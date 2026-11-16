// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Running an operation on an <c>/Adobe.PubSec</c> document — GAPS.md §22b's last public-key row.
/// </summary>
/// <remarks>
/// <para><b>The asymmetry this closes.</b> "Run any operation on an encrypted PDF" was true only of
/// password-protected documents: the round trip took a <c>string</c> password for its decrypt step,
/// and a public-key document has none. Everything after the plaintext was already
/// handler-independent, so what was missing was a credential, not cryptography.</para>
///
/// <para><b>Re-encryption is the half that genuinely differs.</b> A public-key document does not
/// carry its recipients' certificates — each <c>/Recipients</c> entry identifies one by issuer and
/// serial so its holder can recognise it — so there is nothing in the input to re-encrypt to and
/// the caller states the list.</para>
/// </remarks>
public sealed class PublicKeyRoundTripOperationTests
{
    private static byte[] Encrypt(byte[] certDer, bool pdfMac = false) =>
        Encrypt(certDer, PdfPermissions.All, pdfMac);

    private static byte[] Encrypt(byte[] certDer, PdfPermissions permissions, bool pdfMac)
    {
        using var input = new MemoryStream(PublicKeyRoundTripTests.SourcePdf());
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(
            input, output, new[] { certDer }, permissions, EncryptionAlgorithm.AES256, pdfMac);
        return output.ToArray();
    }

    private static EncryptedRoundTripOptions Opening(byte[] pfx) =>
        new EncryptedRoundTripOptions
        {
            Credential = PdfDecryptionCredential.Pkcs12(
                new MemoryStream(pfx, writable: false), PublicKeyRoundTripTests.PfxPassword),
        };

    /// <summary>An operation that appends a page-content marker, so the output proves it ran.</summary>
    private const string Stamp = "RoundTripOperationRan";

    private static void Stamper(Stream input, Stream output)
    {
        byte[] bytes = new BinaryReader(input).ReadBytes((int)input.Length);
        string text = Encoding.Latin1.GetString(bytes);

        // Crude on purpose: this test is about the round trip's decrypt/re-encrypt halves, not
        // about page editing, so the operation only has to be observable in the output.
        int at = text.IndexOf(PublicKeyRoundTripTests.Marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "the operation was handed a document without the marker, so it was not decrypted");

        byte[] produced = Encoding.Latin1.GetBytes(
            text.Replace(PublicKeyRoundTripTests.Marker, Stamp));
        output.Write(produced, 0, produced.Length);
    }

    /// <summary>
    /// <b>An operation runs on a public-key document and the output is re-encrypted to the same
    /// recipient.</b>
    /// </summary>
    [Fact]
    public void AnOperationRunsAndTheOutputStillOpensForItsRecipient()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer);

        EncryptedRoundTripOptions options = Opening(pfx);
        options.Policy = ReEncryptionPolicy.Recipients;
        options.RecipientCertificates = new[] { certDer };

        using var output = new MemoryStream();
        RoundTripResult result = PdfEncryptedRoundTrip.Run(
            new MemoryStream(encrypted, writable: false), output, options, Stamper);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Equal(ReEncryptionPolicy.Recipients, result.PolicyApplied);

        byte[] produced = output.ToArray();

        // Still encrypted: the stamp is not readable in the raw bytes.
        Assert.DoesNotContain(Stamp, Encoding.Latin1.GetString(produced), StringComparison.Ordinal);

        // And it opens for the recipient, carrying what the operation did.
        byte[] decrypted = PublicKeyRoundTripTests.Decrypt(produced, pfx);
        Assert.Contains(Stamp, PublicKeyRoundTripTests.PageContent(decrypted), StringComparison.Ordinal);
    }

    /// <summary>
    /// The document's permissions and its PDF MAC survive the round trip — the things
    /// <c>ReuseOpenPassword</c> carries across for a password document.
    /// </summary>
    [Fact]
    public void ThePermissionsAndThePdfMacAreCarriedAcross()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, PdfPermissions.Print, pdfMac: true);

        EncryptedRoundTripOptions options = Opening(pfx);
        options.Policy = ReEncryptionPolicy.Recipients;
        options.RecipientCertificates = new[] { certDer };

        using var output = new MemoryStream();
        RoundTripResult result = PdfEncryptedRoundTrip.Run(
            new MemoryStream(encrypted, writable: false), output, options, Stamper);

        Assert.True(result.Succeeded, result.FailureReason);
        byte[] produced = output.ToArray();

        // The permissions: the input granted Print only, and the output must not have widened them.
        // Asserted against the INPUT's own /P rather than a constant, so the claim is "carried
        // across" rather than "happens to equal this number".
        Assert.Equal(PValueOf(encrypted), PValueOf(produced));
        Assert.NotEqual(PValueOf(encrypted), PValueOf(Encrypt(certDer, PdfPermissions.All, pdfMac: true)));

        // The token is carried across AND is valid over the new file — a token copied without
        // being recomputed would cover the input's bytes, not these.
        using var store = new MemoryStream(pfx, writable: false);
        PdfMacVerification mac = PdfMacVerifier.Verify(
            new MemoryStream(produced, writable: false), store, PublicKeyRoundTripTests.PfxPassword);

        Assert.True(mac.IsProtected, "the PDF MAC was dropped by the round trip");
        Assert.True(mac.IsValid, mac.Error);
    }

    /// <summary>The <c>/P</c> value of an encrypted document, read from its encryption dictionary.</summary>
    private static int PValueOf(byte[] pdf)
    {
        using var stream = new MemoryStream(pdf, writable: false);
        return PdfDecryptor.GetEncryptionInfo(stream).PValue;
    }

    /// <summary>
    /// <b>An empty recipient list is refused before the file is read</b>, rather than producing a
    /// document nobody can open.
    /// </summary>
    /// <remarks>
    /// That failure would be indistinguishable from success until someone tried to open the output,
    /// which is the worst moment to discover it.
    /// </remarks>
    [Fact]
    public void AnEmptyRecipientListIsRefusedBeforeAnythingIsWritten()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();

        EncryptedRoundTripOptions options = Opening(pfx);
        options.Policy = ReEncryptionPolicy.Recipients;

        using var output = new MemoryStream();
        var ex = Assert.Throws<ArgumentException>(() => PdfEncryptedRoundTrip.Run(
            new MemoryStream(Encrypt(certDer), writable: false), output, options, Stamper));

        Assert.Contains("RecipientCertificates", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// <b>The default policy is still refused for a public-key document, and that refusal is the
    /// point of the guard.</b>
    /// </summary>
    /// <remarks>
    /// <para><c>ReuseOpenPassword</c> would re-encrypt a certificate-protected document with a
    /// password — and since such a document has none, with an <i>empty</i> one — turning a file only
    /// a key holder can read into a file anyone can. Nothing about the output would look wrong:
    /// <c>/V 5 /CFM AESV3</c> is what both handlers write, so the scheme change is invisible in the
    /// encryption dictionary.</para>
    ///
    /// <para>The refusal names the three policies that <i>are</i> honest about the output's handler,
    /// because a caller who reaches this needs to choose one rather than work out why the obvious
    /// call failed.</para>
    /// </remarks>
    [Fact]
    public void TheDefaultPolicyIsRefusedForAPublicKeyDocumentRatherThanChangingTheScheme()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();

        EncryptedRoundTripOptions options = Opening(pfx);   // Policy left at its default

        using var output = new MemoryStream();
        RoundTripResult result = PdfEncryptedRoundTrip.Run(
            new MemoryStream(Encrypt(certDer), writable: false), output, options, Stamper);

        Assert.False(result.Succeeded);
        Assert.Contains("no password to reuse", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains("Recipients", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// A password credential against a public-key document is refused by name, not left to fail as
    /// a wrong password.
    /// </summary>
    [Fact]
    public void APasswordIsRefusedForAPublicKeyDocument()
    {
        var (certDer, _) = PublicKeyRoundTripTests.MakeRecipient();

        var options = new EncryptedRoundTripOptions
        {
            OpenPassword = "not a password this document has",
            Policy = ReEncryptionPolicy.None,
        };

        using var output = new MemoryStream();
        RoundTripResult result = PdfEncryptedRoundTrip.Run(
            new MemoryStream(Encrypt(certDer), writable: false), output, options, Stamper);

        Assert.False(result.Succeeded);
        Assert.Contains("/Adobe.PubSec", result.FailureReason, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// <c>ReEncryptionPolicy.None</c> emits the decrypted document, which for a public-key input is
    /// the same promise it makes for a password one.
    /// </summary>
    [Fact]
    public void ThePolicyNoneEmitsADecryptedDocument()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();

        EncryptedRoundTripOptions options = Opening(pfx);
        options.Policy = ReEncryptionPolicy.None;

        using var output = new MemoryStream();
        RoundTripResult result = PdfEncryptedRoundTrip.Run(
            new MemoryStream(Encrypt(certDer), writable: false), output, options, Stamper);

        Assert.True(result.Succeeded, result.FailureReason);
        Assert.Contains(Stamp, PublicKeyRoundTripTests.PageContent(output.ToArray()),
                        StringComparison.Ordinal);
    }
}
