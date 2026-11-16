// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// The library could produce a document it could not open: <see cref="PdfPublicKeyEncryptor"/> wrote
/// <c>/Adobe.PubSec</c> and every read path refused it, because <see cref="PdfDecryptor"/> handles
/// <c>/Standard</c> only. Its own header said so.
///
/// <para><b>These tests are the asymmetry stated as an assertion:</b> what this library encrypts,
/// this library opens — and the plaintext that comes back is the plaintext that went in. Asserting
/// that decryption "returned a PDF" would pass for a decryptor that emitted the ciphertext
/// unchanged.</para>
/// </summary>
public class PublicKeyRoundTripTests
{
    /// <summary>Marker text placed in the source document, so the round trip has something to prove.</summary>
    internal const string Marker = "PublicKeyRoundTripMarker";

    /// <summary>A self-signed RSA recipient: the DER certificate, and a PKCS#12 holding its key.</summary>
    internal static (byte[] certDer, byte[] pfx) MakeRecipient(string commonName = "CN=Test Recipient")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(commonName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        return (cert.Export(X509ContentType.Cert), cert.Export(X509ContentType.Pfx, PfxPassword));
    }

    internal const string PfxPassword = "pfx-pw";

    /// <summary>A one-page document carrying <see cref="Marker"/> in a content stream.</summary>
    internal static byte[] SourcePdf()
    {
        var sb = new StringBuilder();
        string content = $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET";
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };

        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] Encrypt(byte[] pdf, byte[] certDer, EncryptionAlgorithm algorithm)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, certDer, PdfPermissions.All, algorithm);
        return output.ToArray();
    }

    internal static byte[] Decrypt(byte[] encrypted, byte[] pfx, string password = PfxPassword)
    {
        using var input = new MemoryStream(encrypted);
        using var store = new MemoryStream(pfx);
        using var output = new MemoryStream();
        PdfPublicKeyDecryptor.Decrypt(input, output, store, password);
        return output.ToArray();
    }

    /// <summary>The page's decoded content stream, as text.</summary>
    internal static string PageContent(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return Encoding.GetEncoding("iso-8859-1").GetString(doc.Pages[0].GetContentStreamData());
    }

    // ================================================================ the asymmetry, closed

    /// <summary>
    /// What this library encrypts, this library opens — for every algorithm the encryptor offers.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES256)]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    public void EncryptThenDecrypt_RecoversThePlaintext(EncryptionAlgorithm algorithm)
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] source = SourcePdf();

        byte[] encrypted = Encrypt(source, certDer, algorithm);

        // The control: the marker really is hidden by the encryption, so recovering it below is
        // evidence of decryption rather than of the marker having been in the clear all along.
        Assert.DoesNotContain(Marker, Encoding.GetEncoding("iso-8859-1").GetString(encrypted));

        byte[] decrypted = Decrypt(encrypted, pfx);

        Assert.Contains(Marker, PageContent(decrypted));
    }

    /// <summary>The decrypted document no longer declares itself encrypted.</summary>
    [Fact]
    public void Decrypt_RemovesTheEncryptDictionary()
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] decrypted = Decrypt(Encrypt(SourcePdf(), certDer, EncryptionAlgorithm.AES256), pfx);

        using var ms = new MemoryStream(decrypted);
        using var doc = PdfDocument.Open(ms);

        Assert.False(doc.IsEncrypted);
        Assert.Null(doc.Trailer[PdfName.Encrypt]);
    }

    /// <summary>
    /// A document encrypted for several recipients opens for <b>each</b> of them independently —
    /// which is the whole reason the handler exists.
    /// </summary>
    /// <remarks>
    /// This also pins the key derivation's dependence on the <b>order and completeness</b> of the
    /// <c>/Recipients</c> array: the file key is <c>H(seed ‖ recipient₁ ‖ recipient₂ ‖ …)</c>, so a
    /// reader that hashed only the blob it opened, or hashed them in a different order, derives a
    /// different key — and would pass a single-recipient test.
    /// </remarks>
    [Fact]
    public void Decrypt_WithSeveralRecipients_OpensForEachOfThem()
    {
        var (firstCert, firstPfx) = MakeRecipient("CN=First");
        var (secondCert, secondPfx) = MakeRecipient("CN=Second");

        using var input = new MemoryStream(SourcePdf());
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, new[] { firstCert, secondCert },
            PdfPermissions.All, EncryptionAlgorithm.AES256);
        byte[] encrypted = output.ToArray();

        Assert.Contains(Marker, PageContent(Decrypt(encrypted, firstPfx)));
        Assert.Contains(Marker, PageContent(Decrypt(encrypted, secondPfx)));
    }

    /// <summary>
    /// A document whose <c>/Recipients</c> lives <b>only</b> on the crypt filter still opens.
    /// </summary>
    /// <remarks>
    /// <para><b>This test exists because a mutation survived.</b> Disabling the crypt-filter read
    /// entirely changed nothing, since <see cref="PdfPublicKeyEncryptor"/> writes the array in both
    /// places — on the <c>/Encrypt</c> dictionary and on <c>/DefaultCryptFilter</c> — so every fixture here
    /// answered from the fallback. §7.6.5 puts the array on the crypt filter for V≥4 and other
    /// producers write it there alone, so the branch is interop-critical and was unfalsifiable.</para>
    /// <para>The fixture renames the <b>first</b> occurrence of the key, which is the
    /// <c>/Encrypt</c>-level one, to an equal-length inert name. Only the key changes: the blobs
    /// the key derivation hashes are the same bytes in the same order, so a correct reader recovers
    /// the same file key from the crypt filter's copy.</para>
    /// </remarks>
    [Fact]
    public void Decrypt_WithRecipientsOnlyOnTheCryptFilter_StillOpens()
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] encrypted = Encrypt(SourcePdf(), certDer, EncryptionAlgorithm.AES256);

        byte[] cryptFilterOnly = RenameFirst(encrypted, "/Recipients", "/Recipient0");

        // The control: the /Encrypt-level key really is gone, so opening the document below is
        // evidence that the crypt filter's copy was read.
        using (var check = new MemoryStream(cryptFilterOnly))
        using (var doc = PdfDocument.Open(check))
        {
            var dict = doc.EncryptionDictionary;
            Assert.Null(doc.ResolveObject(dict[new PdfName("Recipients")]));
            var cf = Assert.IsType<PdfDictionary>(doc.ResolveObject(dict[PdfName.CF]));
            var filter = Assert.IsType<PdfDictionary>(doc.ResolveObject(cf[PdfName.DefaultCryptFilter]));
            Assert.NotNull(doc.ResolveObject(filter[new PdfName("Recipients")]));
        }

        Assert.Contains(Marker, PageContent(Decrypt(cryptFilterOnly, pfx)));
    }

    /// <summary>
    /// Replaces the first occurrence of <paramref name="from"/> with <paramref name="to"/>, which
    /// must be the same length so every byte offset in the file stays valid.
    /// </summary>
    private static byte[] RenameFirst(byte[] pdf, string from, string to)
    {
        Assert.Equal(from.Length, to.Length);

        byte[] needle = Encoding.ASCII.GetBytes(from);
        byte[] replacement = Encoding.ASCII.GetBytes(to);

        for (int i = 0; i + needle.Length <= pdf.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && pdf[i + j] == needle[j]) j++;
            if (j != needle.Length) continue;

            var copy = (byte[])pdf.Clone();
            Array.Copy(replacement, 0, copy, i, replacement.Length);
            return copy;
        }

        Assert.Fail($"'{from}' does not appear in the encrypted document");
        return pdf;
    }

    // ================================================================ refusals

    /// <summary>
    /// A key the document was not encrypted for is refused, and the message says so rather than
    /// producing a file full of noise.
    /// </summary>
    [Fact]
    public void Decrypt_WithAKeyTheDocumentWasNotEncryptedFor_IsRefused()
    {
        var (certDer, _) = MakeRecipient("CN=Intended");
        var (_, strangerPfx) = MakeRecipient("CN=Stranger");

        byte[] encrypted = Encrypt(SourcePdf(), certDer, EncryptionAlgorithm.AES256);

        var ex = Assert.Throws<PdfException>(() => Decrypt(encrypted, strangerPfx));
        Assert.Contains("not encrypted for this key", ex.Message);
    }

    /// <summary>A wrong PKCS#12 password is named as such, not reported as a missing recipient.</summary>
    [Fact]
    public void Decrypt_WithTheWrongStorePassword_SaysSo()
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] encrypted = Encrypt(SourcePdf(), certDer, EncryptionAlgorithm.AES256);

        var ex = Assert.Throws<PdfException>(() => Decrypt(encrypted, pfx, "wrong"));
        Assert.Contains("PKCS#12", ex.Message);
    }

    /// <summary>
    /// A password-protected document handed to the public-key decryptor is told which decryptor it
    /// wants. "Wrong handler" and "wrong key" are different problems with different fixes.
    /// </summary>
    [Fact]
    public void Decrypt_GivenAPasswordProtectedDocument_PointsAtTheOtherDecryptor()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(input, encrypted, new EncryptionParameters
        {
            UserPassword = "user", OwnerPassword = "owner",
        });

        var (_, pfx) = MakeRecipient();
        var ex = Assert.Throws<PdfException>(() => Decrypt(encrypted.ToArray(), pfx));

        Assert.Contains("/Standard", ex.Message);
        Assert.Contains("PdfDecryptor", ex.Message);
    }

    /// <summary>An unencrypted document is refused rather than copied through.</summary>
    [Fact]
    public void Decrypt_GivenAnUnencryptedDocument_IsRefused()
    {
        var (_, pfx) = MakeRecipient();
        var ex = Assert.Throws<PdfException>(() => Decrypt(MinimalPdfBuilder.CreateMinimalPdf(), pfx));
        Assert.Contains("not encrypted", ex.Message);
    }

    // ================================================================ which handler?

    /// <summary>
    /// <c>IsPublicKeyEncrypted</c> tells the two handlers apart, so a caller knows which decryptor
    /// to reach for without catching an exception to find out.
    /// </summary>
    [Fact]
    public void IsPublicKeyEncrypted_DistinguishesTheThreeCases()
    {
        var (certDer, _) = MakeRecipient();

        using var pubSec = new MemoryStream(Encrypt(SourcePdf(), certDer, EncryptionAlgorithm.AES256));
        Assert.True(PdfPublicKeyDecryptor.IsPublicKeyEncrypted(pubSec));

        using var plain = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        Assert.False(PdfPublicKeyDecryptor.IsPublicKeyEncrypted(plain));

        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var standard = new MemoryStream();
        PdfEncryptor.Encrypt(input, standard, new EncryptionParameters
        {
            UserPassword = "user", OwnerPassword = "owner",
        });
        standard.Position = 0;
        Assert.False(PdfPublicKeyDecryptor.IsPublicKeyEncrypted(standard));
    }

    /// <summary>...and it leaves the caller's stream where it found it.</summary>
    [Fact]
    public void IsPublicKeyEncrypted_RestoresTheStreamPosition()
    {
        var (certDer, _) = MakeRecipient();
        using var ms = new MemoryStream(Encrypt(SourcePdf(), certDer, EncryptionAlgorithm.AES256));
        ms.Position = 7;

        PdfPublicKeyDecryptor.IsPublicKeyEncrypted(ms);

        Assert.Equal(7, ms.Position);
    }
}
