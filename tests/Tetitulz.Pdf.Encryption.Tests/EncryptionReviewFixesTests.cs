// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>Regressions from the 2026-11-12 review of the encryption handlers, each against ISO 32000-2's text.</summary>
public sealed class EncryptionReviewFixesTests
{
    private static readonly byte[] FileId =
    {
        0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
        0x99, 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xF0, 0x0F
    };

    private const string Marker = "ReviewFixMarker";

    private static byte[] Aes128Document()
    {
        var pdf = new TestPdfs.LegacyRc4.RevisionWriter(FileId, LegacyCipher.Aes128);
        pdf.Object(1, 0, "<< /Type /Catalog /Pages 2 0 R >>");
        pdf.Object(2, 0, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        pdf.Object(3, 0, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>");
        pdf.Stream(4, 0, $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET");
        pdf.EncryptDictionary(5);
        pdf.EndRevision(xrefStreamObjectNumber: 6, rootObjectNumber: 1, encryptObjectNumber: 5);
        return pdf.ToArray();
    }

    private static string DecryptedContent(byte[] encrypted)
    {
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(encrypted), output, "");
        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        return Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
    }

    /// <summary>
    /// ISO 32000-2 Table 20: /Length is "only if V is 2 or 3", and V 4 is a 128-bit key. A /V 4
    /// document that leaves /Length out was decrypted with a 40-bit key and its password refused. The
    /// same document with /Length decrypts (the control).
    /// </summary>
    [Fact]
    public void AV4DocumentWithoutLength_DecryptsWithA128BitKey()
    {
        byte[] withLength = Aes128Document();
        Assert.Contains(Marker, DecryptedContent(withLength), StringComparison.Ordinal);

        string text = Encoding.Latin1.GetString(withLength);
        int at = text.IndexOf(" /Length 128", StringComparison.Ordinal);
        Assert.True(at > 0);
        byte[] without = (byte[])withLength.Clone();
        Encoding.ASCII.GetBytes(new string(' ', " /Length 128".Length)).CopyTo(without, at);

        Assert.Contains(Marker, DecryptedContent(without), StringComparison.Ordinal);
    }

    /// <summary>
    /// /EncryptMetadata means nothing below V 4 (Table 21), so leaving the metadata plain under RC4
    /// makes it garbage to a reader: refused, as the public-key encryptor refuses it. AES-128 accepts it.
    /// </summary>
    [Fact]
    public void EncryptMetadataFalse_UnderRc4_IsRefused_AndUnderAesIsAccepted()
    {
        byte[] plain = PublicKeyRoundTripTests.SourcePdf();
        var rc4 = new EncryptionParameters { UserPassword = "u", OwnerPassword = "o", Algorithm = EncryptionAlgorithm.RC4_128, EncryptMetadata = false };
        Assert.Throws<ArgumentException>(() => PdfEncryptor.Encrypt(new MemoryStream(plain), new MemoryStream(), rc4));

        var aes = new EncryptionParameters { UserPassword = "u", OwnerPassword = "o", Algorithm = EncryptionAlgorithm.AES128, EncryptMetadata = false };
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(plain), output, aes);
        Assert.Contains("/EncryptMetadata false", Encoding.Latin1.GetString(output.ToArray()), StringComparison.Ordinal);
    }

    private static byte[] Aes256(string userPassword)
    {
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(new MemoryStream(PublicKeyRoundTripTests.SourcePdf()), output,
            new EncryptionParameters { UserPassword = userPassword, OwnerPassword = "owner", Algorithm = EncryptionAlgorithm.AES256 });
        return output.ToArray();
    }

    /// <summary>
    /// A password holding a lone surrogate made String.Normalize throw out of a Try door; it is no
    /// match now. The right password is still accepted (the control).
    /// </summary>
    [Fact]
    public void AdversarialLoneSurrogatePassword_IsNoMatch_NotAThrow()
    {
        byte[] encrypted = Aes256("secret");
        using var doc = PdfDocument.Open(new MemoryStream(encrypted));

        Assert.False(PdfIncrementalEncryption.TryCreate(doc, "\uD800", out _, out string error));
        Assert.NotNull(error);
        Assert.True(PdfIncrementalEncryption.TryCreate(doc, "secret", out var encryptor, out _));
        Assert.NotNull(encryptor);
    }

    /// <summary>
    /// ISO 32000-2 §7.6.4.3.3 step b truncates the UTF-8 password to exactly 127 bytes. Sixty-four
    /// Hebrew letters are 128 bytes, the 127th a continuation byte: the key is computed over those
    /// 127 bytes (the standard's Algorithm 2.B over /U's validation salt matches /U), and the
    /// password opens the file.
    /// </summary>
    [Fact]
    public void ALongPassword_IsCutAtExactly127Bytes()
    {
        string password = new string('א', 64);
        byte[] encrypted = Aes256(password);

        using (var doc = PdfDocument.Open(new MemoryStream(encrypted)))
        {
            byte[] u = ((PdfString)doc.ResolveObject(doc.EncryptionDictionary![PdfName.U])).RawBytes;
            byte[] pw = Encoding.UTF8.GetBytes(password).Take(127).ToArray();
            byte[] hash = PdfEncryptionPrimitives.ComputeHashR6(pw, u.Skip(32).Take(8).ToArray());
            Assert.Equal(u.Take(32).ToArray(), hash.Take(32).ToArray());
        }

        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(new MemoryStream(encrypted), output, password);
        Assert.Contains(PublicKeyRoundTripTests.Marker, PublicKeyRoundTripTests.PageContent(output.ToArray()), StringComparison.Ordinal);
    }

    /// <summary>
    /// ISO 32000-2 Table 25: a public-key handler states its crypt filter's /Length in bits — 128
    /// under AESV2, 256 under AESV3.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES128, 128)]
    [InlineData(EncryptionAlgorithm.AES256, 256)]
    public void APublicKeyCryptFilterLength_IsInBits(EncryptionAlgorithm algorithm, int bits)
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(new MemoryStream(PublicKeyRoundTripTests.SourcePdf()), output, certDer, PdfPermissions.All, algorithm);
        byte[] encrypted = output.ToArray();

        using (var doc = PdfDocument.Open(new MemoryStream(encrypted)))
        {
            var cf = (PdfDictionary)doc.ResolveObject(doc.EncryptionDictionary![new PdfName("CF")]);
            var filter = (PdfDictionary)doc.ResolveObject(cf[new PdfName("DefaultCryptFilter")]);
            Assert.Equal(bits, ((PdfNumber)doc.ResolveObject(filter[PdfName.Length])).IntValue);
        }
        Assert.Contains(PublicKeyRoundTripTests.Marker,
            PublicKeyRoundTripTests.PageContent(PublicKeyRoundTripTests.Decrypt(encrypted, pfx)), StringComparison.Ordinal);
    }

    /// <summary>
    /// The public-key decryptor clears the per-document diagnostic slots it shares with the password
    /// handler: a document whose object stream was refused, then a clean public-key document, left the
    /// first one's refusal in place.
    /// </summary>
    [Fact]
    public void APublicKeyDecrypt_ClearsThePreviousDocumentsDiagnostics()
    {
        using (var sink = new MemoryStream())
            PdfDecryptor.Decrypt(new MemoryStream(TestPdfs.LegacyRc4.BuildWithUnexpandableObjectStream(FileId)), sink, "");
        Assert.NotNull(PdfDecryptor.LastObjectStreamRejection);

        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(new MemoryStream(PublicKeyRoundTripTests.SourcePdf()), output, certDer, PdfPermissions.All, EncryptionAlgorithm.AES256);
        PublicKeyRoundTripTests.Decrypt(output.ToArray(), pfx);

        Assert.Null(PdfDecryptor.LastObjectStreamRejection);
    }
}
