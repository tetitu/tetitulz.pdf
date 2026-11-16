// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Encryption;

/// <summary>
/// PDF-level wiring for ISO/TS 32004 standalone PDF MAC tokens, through the full
/// <see cref="PdfEncryptor"/> pipeline: the <c>/KDFSalt</c> encrypt-dictionary entry, the trailer's
/// <c>/AuthCode</c> dictionary, its <c>/ByteRange</c> placeholder arithmetic, and the token itself.
/// <see cref="PdfMacTokenTests"/> covers the CMS token builder in isolation against RFC vectors;
/// these tests are about the file-level plumbing around it — most importantly, that the
/// <c>/ByteRange</c> really does cover the whole file except the <c>/MAC</c> value, and that the
/// token's own <c>dataDigest</c> really is SHA-256 of exactly those bytes.
/// </summary>
public class PdfMacIntegrationTests
{
    private const string UserPassword = "user-secret";
    private const string OwnerPassword = "owner-secret";

    private static byte[] EncryptWithMac(byte[] plain, EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
    {
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = algorithm,
            EnablePdfMac = true,
        });
        return output.ToArray();
    }

    [Fact]
    public void Encrypt_WritesA32ByteKDFSalt()
    {
        byte[] encrypted = EncryptWithMac(MinimalPdfBuilder.CreateMinimalPdf("kdf-salt"));

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        var encryptDict = doc.ResolveObject(doc.Trailer[PdfName.Encrypt]) as PdfDictionary;
        Assert.NotNull(encryptDict);

        var salt = encryptDict[PdfName.KDFSalt] as PdfString;
        Assert.NotNull(salt);
        Assert.Equal(32, salt.RawBytes.Length);
    }

    /// <summary>
    /// ISO/TS 32004 Table 5 allows a token only where the encryption dictionary's <c>/V</c> is at
    /// least 5. Below it nothing authenticates the permission that says a token is required, so one
    /// written there could be removed unseen; the encryptor refuses, and writes nothing.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_40)]
    [InlineData(EncryptionAlgorithm.RC4_40R3)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    [InlineData(EncryptionAlgorithm.AES128)]
    public void Encrypt_RefusesAPdfMacBelowEncryptionVersion5(EncryptionAlgorithm algorithm)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("mac-below-v5"));
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() => PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = algorithm,
            EnablePdfMac = true,
        }));

        Assert.Contains("at least 5", ex.Message, StringComparison.Ordinal);
        Assert.Contains(algorithm.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The refusal is the token's: the same algorithms encrypt as before with no token asked for,
    /// and the two written at <c>/V 5</c> and later seal a document whose token verifies.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.RC4_128, false)]
    [InlineData(EncryptionAlgorithm.AES128, false)]
    [InlineData(EncryptionAlgorithm.AES256, true)]
    [InlineData(EncryptionAlgorithm.AES256GCM, true)]
    public void Encrypt_WritesWhatTheStandardAllows(EncryptionAlgorithm algorithm, bool pdfMac)
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("mac-allowed"));
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = algorithm,
            EnablePdfMac = pdfMac,
        });

        PdfMacVerification verified = PdfMacVerifier.Verify(new MemoryStream(output.ToArray(), writable: false), UserPassword);
        Assert.Equal(pdfMac, verified.IsProtected);
        Assert.Equal(pdfMac, verified.IsValid);
        Assert.Null(verified.Error);
    }

    [Fact]
    public void Encrypt_ClearsPermissionBit13()
    {
        byte[] encrypted = EncryptWithMac(MinimalPdfBuilder.CreateMinimalPdf("perm-bit-13"));

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        var encryptDict = doc.ResolveObject(doc.Trailer[PdfName.Encrypt]) as PdfDictionary;
        int p = (encryptDict[PdfName.P] as PdfNumber).IntValue;

        // ISO/TS 32004 §5.1.2: bit 13 (0-based bit 12, mask 0x1000) zero means "a PDF MAC token is
        // required in every revision" — the opposite of the reserved-as-1 default every other
        // encrypted document in this suite gets.
        Assert.Equal(0, p & 0x1000);
    }

    [Fact]
    public void Encrypt_WithoutPdfMac_LeavesPermissionBit13Set()
    {
        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf("no-mac"));
        using var output = new MemoryStream();
        PdfEncryptor.Encrypt(input, output, new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = EncryptionAlgorithm.AES128,
        });

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var encryptDict = doc.ResolveObject(doc.Trailer[PdfName.Encrypt]) as PdfDictionary;
        int p = (encryptDict[PdfName.P] as PdfNumber).IntValue;

        Assert.NotEqual(0, p & 0x1000);
    }

    [Fact]
    public void Encrypt_WritesAuthCodeWithStandaloneLocation()
    {
        byte[] encrypted = EncryptWithMac(MinimalPdfBuilder.CreateMinimalPdf("authcode-location"));

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        var authCode = doc.Trailer[PdfName.AuthCode] as PdfDictionary;
        Assert.NotNull(authCode);

        var location = authCode[PdfName.MACLocation] as PdfName;
        Assert.Equal("Standalone", location?.Value);
    }

    /// <summary>
    /// The property the whole mechanism exists for: the <c>/ByteRange</c> in the trailer's
    /// <c>/AuthCode</c> covers the entire file except the <c>/MAC</c> value, and the token's own
    /// <c>dataDigest</c> is SHA-256 of exactly those bytes — verified independently of
    /// <see cref="PdfEncryptor"/>, by re-reading the raw file bytes and re-parsing the CMS token.
    /// </summary>
    [Fact]
    public void Encrypt_ByteRangeCoversWholeFileExceptMac_AndDataDigestMatches()
    {
        byte[] encrypted = EncryptWithMac(MinimalPdfBuilder.CreateMinimalPdf("byte-range-coverage"));

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        var authCode = doc.Trailer[PdfName.AuthCode] as PdfDictionary;
        Assert.NotNull(authCode);

        var byteRange = authCode[PdfName.ByteRange] as PdfArray;
        Assert.NotNull(byteRange);
        Assert.Equal(4, byteRange.Count);

        long r0 = ((PdfNumber)byteRange[0]).LongValue;
        long r1 = ((PdfNumber)byteRange[1]).LongValue;
        long r2 = ((PdfNumber)byteRange[2]).LongValue;
        long r3 = ((PdfNumber)byteRange[3]).LongValue;

        Assert.Equal(0, r0);
        // The gap [r1, r2) must be exactly the /MAC hex string: it opens with '<' and the byte
        // immediately before the second range starts is '>'.
        Assert.Equal('<', (char)encrypted[r1]);
        Assert.Equal('>', (char)encrypted[r2 - 1]);
        // The whole file must be covered: nothing lies past the second range.
        Assert.Equal(encrypted.Length, r2 + r3);

        byte[] rangeData = new byte[r1 + r3];
        Array.Copy(encrypted, 0, rangeData, 0, r1);
        Array.Copy(encrypted, r2, rangeData, r1, r3);

        byte[] expectedDigest;
        using (var sha256 = System.Security.Cryptography.SHA256.Create())
            expectedDigest = sha256.ComputeHash(rangeData);

        var authCodeMac = authCode[PdfName.MAC] as PdfString;
        Assert.NotNull(authCodeMac);

        var contentInfo = ContentInfo.GetInstance(Asn1Object.FromByteArray(authCodeMac.RawBytes));
        var authenticatedData = AuthenticatedData.GetInstance(contentInfo.Content);

        // §6.2: PdfMacIntegrityInfo ::= SEQUENCE { version INTEGER, dataDigest OCTET STRING, ... }
        byte[] pdfMacIntegrityInfo =
            Asn1OctetString.GetInstance(authenticatedData.EncapsulatedContentInfo.Content).GetOctets();
        var integrityInfoSeq = Asn1Sequence.GetInstance(pdfMacIntegrityInfo);
        byte[] dataDigest = Asn1OctetString.GetInstance(integrityInfoSeq[1]).GetOctets();

        Assert.Equal(expectedDigest, dataDigest);
    }

    /// <summary>
    /// The MAC key itself round-trips: derive the KEK from the real file encryption key and the
    /// written <c>/KDFSalt</c>, unwrap the recipient's wrapped key, and recompute HMAC-SHA256 over
    /// the token's authenticated attributes — independent of <see cref="PdfMacToken"/>'s own
    /// internals except for the three primitives already pinned against RFC vectors elsewhere.
    /// </summary>
    [Fact]
    public void Encrypt_MacVerifiesUnderTheFileEncryptionKeyAndKdfSalt()
    {
        // The file key is the decryptor's own derivation of it (ResolveEncryption), which the
        // password tests hold to the standard; what this test adds is everything after the key.
        byte[] plain = MinimalPdfBuilder.CreateMinimalPdf("mac-key-roundtrip");
        using var input = new MemoryStream(plain);
        using var output = new MemoryStream();
        var parameters = new EncryptionParameters
        {
            UserPassword = UserPassword,
            OwnerPassword = OwnerPassword,
            Algorithm = EncryptionAlgorithm.AES256,
            EnablePdfMac = true,
        };
        PdfEncryptor.Encrypt(input, output, parameters);
        byte[] encrypted = output.ToArray();

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        var encryptDict = doc.ResolveObject(doc.Trailer[PdfName.Encrypt]) as PdfDictionary;
        byte[] kdfSalt = (encryptDict[PdfName.KDFSalt] as PdfString).RawBytes;

        var authCode = doc.Trailer[PdfName.AuthCode] as PdfDictionary;
        byte[] tokenBytes = (authCode[PdfName.MAC] as PdfString).RawBytes;

        var contentInfo = ContentInfo.GetInstance(Asn1Object.FromByteArray(tokenBytes));
        var authenticatedData = AuthenticatedData.GetInstance(contentInfo.Content);

        var recipientInfo = Org.BouncyCastle.Asn1.Cms.RecipientInfo.GetInstance(authenticatedData.RecipientInfos[0]);
        var passwordRecipientInfo = Org.BouncyCastle.Asn1.Cms.PasswordRecipientInfo.GetInstance(recipientInfo.Info);

        byte[] fileKey = PdfDecryptor.ResolveEncryption(doc, UserPassword).Key;

        byte[] kek = PdfMacToken.DeriveKek(fileKey, kdfSalt);
        byte[] wrappedKey = passwordRecipientInfo.EncryptedKey.GetOctets();
        byte[] macKey = PdfMacToken.UnwrapKey(kek, wrappedKey);

        byte[] authAttrsBytes = authenticatedData.AuthAttrs.GetEncoded(Asn1Encodable.Der);
        byte[] recomputedMac = PdfMacToken.ComputeHmacSha256(macKey, authAttrsBytes);

        Assert.Equal(authenticatedData.Mac.GetOctets(), recomputedMac);
    }

    [Fact]
    public void Decrypt_StillOpensNormally_WithUserPassword()
    {
        byte[] encrypted = EncryptWithMac(MinimalPdfBuilder.CreateMinimalPdf("still-decrypts"));

        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfDecryptor.Decrypt(input, output, UserPassword);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        string content = System.Text.Encoding.ASCII.GetString(doc.Pages[0].GetContentStreamData());
        Assert.Contains("still-decrypts", content);
    }
}
