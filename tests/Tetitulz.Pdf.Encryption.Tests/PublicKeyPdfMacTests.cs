// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Cms;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Security;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Encryption;
using Xunit;
using ContentInfo = Org.BouncyCastle.Asn1.Cms.ContentInfo;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// ISO/TS 32004 PDF MAC under the <b>public-key</b> security handler
/// (<see cref="PdfPublicKeyEncryptor"/>), and the ISO 32000-2 §7.6.4.1 crypt-filter name it depends
/// on: §6.4 keys the token with "the crypt filter named DefaultCryptFilter".
/// </summary>
public class PublicKeyPdfMacTests
{
    private static byte[] Encrypt(byte[] certDer, bool pdfMac, EncryptionAlgorithm algorithm = EncryptionAlgorithm.AES256)
    {
        using var input = new MemoryStream(PublicKeyRoundTripTests.SourcePdf());
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, new[] { certDer }, PdfPermissions.All, algorithm, pdfMac);
        return output.ToArray();
    }

    private static PdfDictionary EncryptDictionary(PdfDocument doc) => doc.EncryptionDictionary;

    [Theory]
    [InlineData(EncryptionAlgorithm.AES256)]
    [InlineData(EncryptionAlgorithm.AES128)]
    public void CryptFilter_IsNamedDefaultCryptFilter_AndStreamsAndStringsUseIt(EncryptionAlgorithm algorithm)
    {
        var (certDer, _) = PublicKeyRoundTripTests.MakeRecipient();
        using var doc = PdfDocument.Open(new MemoryStream(Encrypt(certDer, pdfMac: false, algorithm)));

        var cf = Assert.IsType<PdfDictionary>(doc.ResolveObject(EncryptDictionary(doc)[PdfName.CF]));
        Assert.IsType<PdfDictionary>(doc.ResolveObject(cf[PdfName.DefaultCryptFilter]));
        Assert.Null(cf[PdfName.StdCF]);
        Assert.Equal(PdfName.DefaultCryptFilter, EncryptDictionary(doc)[PdfName.StmF]);
        Assert.Equal(PdfName.DefaultCryptFilter, EncryptDictionary(doc)[PdfName.StrF]);
    }

    [Fact]
    public void WithoutPdfMac_NoSaltNoTokenAndBit13StaysSet()
    {
        var (certDer, _) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, pdfMac: false);
        using var doc = PdfDocument.Open(new MemoryStream(encrypted));

        Assert.Null(EncryptDictionary(doc)[PdfName.KDFSalt]);
        Assert.NotEqual(0, ((PdfNumber)EncryptDictionary(doc)[PdfName.P]).IntValue & 0x1000);
        Assert.DoesNotContain("/AuthCode", Encoding.Latin1.GetString(encrypted));
    }

    [Fact]
    public void WithPdfMac_WritesSaltClearsBit13AndSealsTheWholeFile()
    {
        var (certDer, _) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, pdfMac: true);
        using var doc = PdfDocument.Open(new MemoryStream(encrypted));

        Assert.Equal(32, Assert.IsType<PdfString>(EncryptDictionary(doc)[PdfName.KDFSalt]).RawBytes.Length);
        Assert.Equal(0, ((PdfNumber)EncryptDictionary(doc)[PdfName.P]).IntValue & 0x1000);
        Assert.Equal(2, doc.Version.Major);

        (long[] range, byte[] token) = AuthCode(encrypted);
        Assert.Equal(0, range[0]);
        Assert.Equal((byte)'<', encrypted[range[1]]);
        Assert.Equal((byte)'>', encrypted[range[2] - 1]);
        Assert.Equal(encrypted.Length, range[2] + range[3]);

        byte[] covered = encrypted.Take((int)range[1]).Concat(encrypted.Skip((int)range[2])).ToArray();
        Assert.Equal(SHA256.HashData(covered), DataDigest(token));
    }

    /// <summary>
    /// The token is keyed with the document's file key, derived here independently of the library:
    /// open the recipient's envelope with its private key, take the 20-byte seed that precedes the 4
    /// permission bytes (Table 27), and hash it with every <c>/Recipients</c> blob (SHA-256 for AES-256,
    /// §7.6.5.3; the metadata is encrypted, so step c) adds nothing).
    /// </summary>
    [Fact]
    public void WithPdfMac_TheTokenVerifiesUnderTheFileKeyAndSalt()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, pdfMac: true);

        using var doc = PdfDocument.Open(new MemoryStream(encrypted));
        PdfDictionary encrypt = EncryptDictionary(doc);
        byte[] kdfSalt = ((PdfString)encrypt[PdfName.KDFSalt]).RawBytes;
        var recipients = ((PdfArray)doc.ResolveObject(encrypt[new PdfName("Recipients")]))
            .Select(o => ((PdfString)doc.ResolveObject(o)).RawBytes).ToList();

        using var certificate = X509CertificateLoader.LoadPkcs12(pfx, PublicKeyRoundTripTests.PfxPassword,
            X509KeyStorageFlags.Exportable);
        using RSA rsa = certificate.GetRSAPrivateKey();
        var privateKey = DotNetUtilities.GetRsaKeyPair(RSA.Create(rsa.ExportParameters(true))).Private;

        var envelope = new CmsEnvelopedData(recipients[0]);
        RecipientInformation recipient = envelope.GetRecipientInfos().GetRecipients().Cast<RecipientInformation>().Single();
        byte[] content = recipient.GetContent(privateKey);
        byte[] seed = content.Take(20).ToArray();

        byte[] fileKey = SHA256.HashData(seed.Concat(recipients.SelectMany(r => r)).ToArray());

        var authenticatedData = AuthenticatedData.GetInstance(ContentInfo.GetInstance(Asn1Object.FromByteArray(AuthCode(encrypted).Token)).Content);
        var passwordRecipient = PasswordRecipientInfo.GetInstance(Org.BouncyCastle.Asn1.Cms.RecipientInfo.GetInstance(authenticatedData.RecipientInfos[0]).Info);
        byte[] macKey = PdfMacToken.UnwrapKey(PdfMacToken.DeriveKek(fileKey, kdfSalt), passwordRecipient.EncryptedKey.GetOctets());
        Assert.Equal(authenticatedData.Mac.GetOctets(),
            PdfMacToken.ComputeHmacSha256(macKey, authenticatedData.AuthAttrs.GetEncoded(Asn1Encodable.Der)));
    }

    /// <summary>
    /// <b>The token this library writes for a public-key document is one it can also check.</b>
    /// </summary>
    /// <remarks>
    /// <para>§6.4 keys a PDF MAC on "the file encryption key used by the document's security
    /// handler" — whichever handler that is. Everything after that key is handler-independent, so
    /// the public-key path needed a credential rather than a second verifier.</para>
    ///
    /// <para>The test above derives the key and the MAC by hand, deliberately, and stays that way:
    /// it is the independent check that the token is right. This one asserts the library agrees
    /// with it, which is a different claim and the one a caller depends on.</para>
    /// </remarks>
    [Fact]
    public void WithPdfMac_TheTokenVerifiesThroughTheVerifier()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, pdfMac: true);

        PdfMacVerification result = VerifyWithPfx(encrypted, pfx);

        Assert.True(result.IsProtected);
        Assert.True(result.IsValid, result.Error);
        Assert.Equal("Standalone", result.Location);
    }

    /// <summary>A byte changed inside the covered range is caught, as for a password document.</summary>
    [Fact]
    public void WithPdfMac_AByteChangedInsideTheCoveredRangeIsReported()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, pdfMac: true);
        Assert.True(VerifyWithPfx(encrypted, pfx).IsValid, "the fixture must verify before it is altered");

        // The minor version digit of the header: inside the first covered range under any
        // /ByteRange, and nothing downstream branches on it.
        byte[] tampered = (byte[])encrypted.Clone();
        int at = Encoding.ASCII.GetString(tampered).IndexOf("%PDF-", StringComparison.Ordinal);
        Assert.True(at >= 0, "the fixture has no PDF header to alter");
        int minor = at + "%PDF-".Length + 2;
        tampered[minor] = tampered[minor] == (byte)'0' ? (byte)'1' : (byte)'0';

        PdfMacVerification result = VerifyWithPfx(tampered, pfx);

        Assert.False(result.IsValid);
        Assert.Contains("dataDigest", result.Error, StringComparison.Ordinal);
    }

    /// <summary>A public-key document written without a token is unprotected, not invalid.</summary>
    [Fact]
    public void WithoutPdfMac_ThePublicKeyDocumentIsReportedUnprotected()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();

        PdfMacVerification result = VerifyWithPfx(Encrypt(certDer, pdfMac: false), pfx);

        Assert.False(result.IsProtected);
        Assert.Null(result.Error);
    }

    /// <summary>
    /// <b>A token removed from a public-key document is reported, whatever the dictionary is edited
    /// to say.</b> The trailer's <c>/AuthCode</c>, the <c>/KDFSalt</c> and the dictionary's
    /// <c>/P</c> are plain text, and all three are changed here; the requirement is read from the
    /// permissions inside the recipient's envelope (ISO/TS 32004 Table 4), which is hashed into the
    /// file key and so cannot be edited by someone who cannot open it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATokenRemovedIsReported_TheEnvelopeSaysOneIsRequired(bool dictionaryEditedToo)
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, pdfMac: true);
        Assert.True(VerifyWithPfx(encrypted, pfx).IsValid, "the fixture must verify before it is altered");

        string text = Encoding.Latin1.GetString(encrypted);
        text = ReplaceFirst(text, "/AuthCode", "/AuthCodX");
        if (dictionaryEditedToo)
        {
            text = ReplaceFirst(text, "/KDFSalt", "/KDFSalX");
            var p = System.Text.RegularExpressions.Regex.Match(
                text, @"/P (-?\d+)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
            Assert.True(p.Success, "the fixture's encryption dictionary has no /P to edit");
            int original = int.Parse(p.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(0, original & 0x1000);
            string edited = (original | 0x1000).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(edited.Length <= p.Groups[1].Length, "the edited /P must fit where the original stood");
            text = text.Substring(0, p.Groups[1].Index) + edited.PadRight(p.Groups[1].Length)
                 + text.Substring(p.Groups[1].Index + p.Groups[1].Length);
        }

        PdfMacVerification result = VerifyWithPfx(Encoding.Latin1.GetBytes(text), pfx);

        Assert.True(result.IsProtected);
        Assert.False(result.IsValid);
        Assert.Contains("this recipient's envelope", result.Error, StringComparison.Ordinal);
    }

    private static string ReplaceFirst(string text, string from, string to)
    {
        Assert.Equal(from.Length, to.Length);
        int at = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the fixture does not contain {from}");
        return text.Substring(0, at) + to + text.Substring(at + from.Length);
    }

    /// <summary>
    /// The password overload names the route rather than refusing generically — an
    /// <c>/Adobe.PubSec</c> document has no password at all.
    /// </summary>
    [Fact]
    public void ThePasswordOverloadPointsAPublicKeyDocumentAtTheCertificateOverload()
    {
        var (certDer, _) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, pdfMac: true);

        var ex = Assert.Throws<Core.PdfException>(
            () => PdfMacVerifier.Verify(new MemoryStream(encrypted, writable: false), "any password"));

        Assert.Contains("/Adobe.PubSec", ex.Message, StringComparison.Ordinal);
        Assert.Contains("IExternalDecryptor", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A revision can be appended to a public-key document, and its PDF MAC token is refreshed
    /// with it.</b>
    /// </summary>
    /// <remarks>
    /// <para>§6.5.1 requires the most recent unsigned revision's <c>/ByteRange</c> to cover the
    /// entire file, so the previous token stops covering it the moment anything is appended —
    /// Annex B.2.2.2 names exactly that as what a stale token indicates. A password-protected
    /// document keeps a valid token through an append; this is the public-key leg of the
    /// same guarantee.</para>
    ///
    /// <para><b>The assertion is that the token verifies afterwards</b>, not merely that bytes were
    /// added. A revision appended without resealing leaves a token that covers a prefix of the
    /// file, which parses, opens and decrypts perfectly well — and is stale.</para>
    /// </remarks>
    [Fact]
    public void WithPdfMac_AnAppendedRevisionKeepsTheTokenValid()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, pdfMac: true);
        Assert.True(VerifyWithPfx(encrypted, pfx).IsValid, "the fixture must verify before it is appended to");

        using var store = new MemoryStream(pfx, writable: false);
        PdfDecryptionCredential credential =
            PdfDecryptionCredential.Pkcs12(store, PublicKeyRoundTripTests.PfxPassword);

        using var output = new MemoryStream();
        PdfEncryptedIncrementalUpdate.Run(
            new MemoryStream(encrypted, writable: false), output, credential,
            (decrypted, firstFree) => new EncryptedRevision(
                new[] { new Core.Writing.IndirectObject(firstFree, 0, new PdfString("appended")) }));

        byte[] appended = output.ToArray();

        // The original bytes are still the output's prefix — an in-place revision.
        Assert.True(appended.Length > encrypted.Length, "nothing was appended");
        Assert.Equal(encrypted, appended.Take(encrypted.Length).ToArray());

        PdfMacVerification result = VerifyWithPfx(appended, pfx);
        Assert.True(result.IsProtected);
        Assert.True(result.IsValid, result.Error);
    }

    /// <summary>
    /// A password credential against a public-key document is refused by name, rather than failing
    /// later as a wrong password.
    /// </summary>
    [Fact]
    public void APasswordCredentialIsRefusedForAPublicKeyDocument()
    {
        var (certDer, _) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] encrypted = Encrypt(certDer, pdfMac: true);

        using var output = new MemoryStream();
        var ex = Assert.Throws<Core.PdfException>(() => PdfEncryptedIncrementalUpdate.Run(
            new MemoryStream(encrypted, writable: false), output,
            PdfDecryptionCredential.Password("not a password this document has"),
            (decrypted, firstFree) => new EncryptedRevision(
                new[] { new Core.Writing.IndirectObject(firstFree, 0, new PdfString("x")) })));

        Assert.Contains("/Adobe.PubSec", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    private static PdfMacVerification VerifyWithPfx(byte[] pdf, byte[] pfx)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var store = new MemoryStream(pfx, writable: false);
        return PdfMacVerifier.Verify(input, store, PublicKeyRoundTripTests.PfxPassword);
    }

    [Fact]
    public void WithPdfMac_TheDocumentStillDecrypts()
    {
        var (certDer, pfx) = PublicKeyRoundTripTests.MakeRecipient();
        byte[] decrypted = PublicKeyRoundTripTests.Decrypt(Encrypt(certDer, pdfMac: true), pfx);
        Assert.Contains(PublicKeyRoundTripTests.Marker, PublicKeyRoundTripTests.PageContent(decrypted));
    }

    [Theory]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.RC4_128)]
    public void WithPdfMac_AnAlgorithmBelowV5_IsRefusedBeforeAnythingIsWritten(EncryptionAlgorithm algorithm)
    {
        var (certDer, _) = PublicKeyRoundTripTests.MakeRecipient();
        using var input = new MemoryStream(PublicKeyRoundTripTests.SourcePdf());
        using var output = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() =>
            PdfPublicKeyEncryptor.Encrypt(input, output, new[] { certDer }, PdfPermissions.All, algorithm, enablePdfMac: true));
        Assert.Equal("algorithm", ex.ParamName);
        Assert.Equal(0, output.Length);
    }

    /// <summary>An output the seal could not read back.</summary>
    private sealed class WriteOnlyStream : MemoryStream
    {
        public override bool CanRead => false;
    }

    [Fact]
    public void WithPdfMac_AnOutputThatCannotBeReadBack_IsRefusedBeforeAnythingIsWritten()
    {
        var (certDer, _) = PublicKeyRoundTripTests.MakeRecipient();
        using var input = new MemoryStream(PublicKeyRoundTripTests.SourcePdf());
        using var output = new WriteOnlyStream();

        var ex = Assert.Throws<ArgumentException>(() =>
            PdfPublicKeyEncryptor.Encrypt(input, output, new[] { certDer }, PdfPermissions.All,
                EncryptionAlgorithm.AES256, enablePdfMac: true));
        Assert.Equal("output", ex.ParamName);
        Assert.Equal(0, output.Length);
    }

    private static (long[] Range, byte[] Token) AuthCode(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int authCode = text.LastIndexOf("/AuthCode", StringComparison.Ordinal);
        Assert.True(authCode >= 0, "no /AuthCode was written");
        int open = text.IndexOf('[', text.IndexOf("/ByteRange", authCode, StringComparison.Ordinal));
        int close = text.IndexOf(']', open);
        long[] range = text.Substring(open + 1, close - open - 1)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(long.Parse).ToArray();
        byte[] token = Convert.FromHexString(text.Substring((int)range[1] + 1, (int)(range[2] - range[1]) - 2));
        return (range, token);
    }

    private static byte[] DataDigest(byte[] token)
    {
        var authenticatedData = AuthenticatedData.GetInstance(ContentInfo.GetInstance(Asn1Object.FromByteArray(token)).Content);
        byte[] integrityInfo = Asn1OctetString.GetInstance(authenticatedData.EncapsulatedContentInfo.Content).GetOctets();
        return Asn1OctetString.GetInstance(Asn1Sequence.GetInstance(integrityInfo)[1]).GetOctets();
    }
}
