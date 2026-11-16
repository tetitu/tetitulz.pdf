// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// Wave 17, R5.4 and R5.6: the <c>/Adobe.PubSec</c> handler against ISO 32000-2 §7.6.5 rather than
/// against itself. The round-trip suites prove the encryptor and the decryptor agree; they proved
/// it while both put the permissions <i>before</i> the seed in the CMS content (Table 27 says
/// after) and both left out the four <c>0xFF</c> bytes §7.6.5.3 step c) adds to the file-key digest
/// when the metadata stays in plaintext. So every assertion here is made by a second
/// implementation: the test opens the envelope itself and derives the key from the standard's
/// text, or builds the whole document from the standard's text and asks the decryptor to open it.
/// </summary>
public class PublicKeyEnvelopeLayoutTests
{
    private const string PfxPassword = "pfx-pw";
    private const string Marker = "EnvelopeLayoutMarker";
    private const string MetadataMarker = "PlaintextMetadataMarker";

    /// <summary>The content stream's object number in every fixture here — and in <see cref="SourcePdf"/>.</summary>
    private const int ContentObjectNumber = 4;

    private const int MetadataObjectNumber = 6;

    private static readonly Encoding Latin1 = Encoding.Latin1;

    // ================================================================ what the encryptor writes

    /// <summary>
    /// The enveloped content is the 20-byte seed followed by the 4-byte permissions (Table 27), and
    /// the file key is the standard's digest of that seed and the recipient blobs — checked by
    /// deriving the key here and decrypting the content stream with it. Permissions other than
    /// <c>All</c>, so the four bytes are not <c>FF FF FF FF</c> on either reading.
    /// </summary>
    [Theory]
    [InlineData(EncryptionAlgorithm.AES128)]
    [InlineData(EncryptionAlgorithm.AES256)]
    public void TheEnvelopeHoldsTheSeedThenThePermissions_AndTheKeyIsTheStandardsDigest(EncryptionAlgorithm algorithm)
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] encrypted = Encrypt(SourcePdf(), certDer, PdfPermissions.Print | PdfPermissions.Copy, algorithm, encryptMetadata: true);

        using var doc = Open(encrypted);
        int p = ((PdfNumber)doc.ResolveObject(doc.EncryptionDictionary![PdfName.P])).IntValue;
        List<byte[]> recipients = Recipients(doc);
        byte[] content = OpenEnvelope(recipients.Single(), pfx);

        Assert.Equal(24, content.Length);
        Assert.Equal(new[] { (byte)(p >> 24), (byte)(p >> 16), (byte)(p >> 8), (byte)p }, content.Skip(20).ToArray());
        Assert.NotEqual(unchecked((int)0xFFFFFFFF), p);

        byte[] seed = content.Take(20).ToArray();
        byte[] key = StandardsFileKey(seed, recipients, algorithm, plaintextMetadata: false);
        Assert.Contains(Marker, Latin1.GetString(DecryptStream(doc, ContentObjectNumber, key, algorithm)));
    }

    /// <summary>
    /// Metadata left in plaintext: the stream is written as it was, <c>/EncryptMetadata false</c>
    /// sits on the crypt filter (Table 27) and not on <c>/Encrypt</c>, and the key is the digest
    /// with step c)'s four <c>0xFF</c> bytes — a key derived without them does not open the content.
    /// The library's own decryptor then opens the document and finds both markers.
    /// </summary>
    [Fact]
    public void AMetadataLeftInPlaintext_IsFlaggedOnTheCryptFilter_AndHashesFourFFBytesIntoTheKey()
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] encrypted = Encrypt(WithMetadata(MetadataMarker), certDer, PdfPermissions.All, EncryptionAlgorithm.AES128, encryptMetadata: false);

        Assert.Contains(MetadataMarker, Latin1.GetString(encrypted), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, Latin1.GetString(encrypted), StringComparison.Ordinal);

        using (var doc = Open(encrypted))
        {
            PdfDictionary encrypt = doc.EncryptionDictionary!;
            var cf = (PdfDictionary)doc.ResolveObject(encrypt[PdfName.CF]);
            var filter = (PdfDictionary)doc.ResolveObject(cf[PdfName.DefaultCryptFilter]);
            Assert.False(((PdfBoolean)filter[PdfName.EncryptMetadata]).Value);
            Assert.Null(encrypt[PdfName.EncryptMetadata]);

            List<byte[]> recipients = Recipients(doc);
            byte[] seed = OpenEnvelope(recipients.Single(), pfx).Take(20).ToArray();

            byte[] withSuffix = StandardsFileKey(seed, recipients, EncryptionAlgorithm.AES128, plaintextMetadata: true);
            Assert.Contains(Marker, Latin1.GetString(DecryptStream(doc, ContentObjectNumber, withSuffix, EncryptionAlgorithm.AES128)));

            byte[] withoutSuffix = StandardsFileKey(seed, recipients, EncryptionAlgorithm.AES128, plaintextMetadata: false);
            Assert.DoesNotContain(Marker, Latin1.GetString(TryDecryptStream(doc, ContentObjectNumber, withoutSuffix, EncryptionAlgorithm.AES128)));
        }

        byte[] decrypted = Decrypt(encrypted, pfx);
        Assert.Contains(Marker, PageContent(decrypted));
        Assert.Contains(MetadataMarker, Latin1.GetString(Stream(decrypted, MetadataObjectNumber).GetDecodedData()));
    }

    /// <summary>
    /// <c>/EncryptMetadata</c> is meaningful only with crypt filters (Table 21), which RC4-128 is
    /// written without — refused, where AES-128 is accepted above.
    /// </summary>
    [Fact]
    public void PlaintextMetadataWithRc4_IsRefused()
    {
        var (certDer, _) = MakeRecipient();

        var ex = Assert.Throws<ArgumentException>(() =>
            Encrypt(SourcePdf(), certDer, PdfPermissions.All, EncryptionAlgorithm.RC4_128, encryptMetadata: false));
        Assert.Contains("/V 4", ex.Message);
    }

    /// <summary>
    /// The whole-file round trip carries a document's plaintext metadata across when it re-encrypts
    /// for recipients — the output's metadata is still readable without a key.
    /// </summary>
    [Fact]
    public void TheRoundTripKeepsPlaintextMetadataPlaintextForRecipients()
    {
        var (certDer, pfx) = MakeRecipient();

        byte[] standard;
        using (var input = new MemoryStream(WithMetadata(MetadataMarker)))
        using (var output = new MemoryStream())
        {
            PdfEncryptor.Encrypt(input, output, new EncryptionParameters
            {
                UserPassword = "u", OwnerPassword = "o",
                Algorithm = EncryptionAlgorithm.AES128, EncryptMetadata = false,
            });
            standard = output.ToArray();
        }

        byte[] forRecipients;
        using (var input = new MemoryStream(standard))
        using (var output = new MemoryStream())
        {
            RoundTripResult result = PdfEncryptedRoundTrip.Run(input, output,
                new EncryptedRoundTripOptions
                {
                    OpenPassword = "u",
                    Policy = ReEncryptionPolicy.Recipients,
                    RecipientCertificates = new[] { certDer },
                },
                (plain, produced) => plain.CopyTo(produced));
            Assert.True(result.Succeeded, result.FailureReason);
            forRecipients = output.ToArray();
        }

        Assert.Contains(MetadataMarker, Latin1.GetString(forRecipients), StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, Latin1.GetString(forRecipients), StringComparison.Ordinal);
        Assert.Contains(Marker, PageContent(Decrypt(forRecipients, pfx)));
    }

    /// <summary>
    /// <see cref="ReEncryptionPolicy.KeepOriginal"/> on a certificate-encrypted document keeps every
    /// recipient with no certificates supplied: the output's <c>/Recipients</c> envelopes are the
    /// input's byte for byte, and the recipient who did not run the trip opens it.
    /// </summary>
    [Fact]
    public void TheRoundTripKeepingTheOriginal_KeepsEveryRecipient()
    {
        var (certA, pfxA) = MakeRecipient();
        var (certB, pfxB) = MakeRecipient();

        byte[] encrypted;
        using (var input = new MemoryStream(WithMetadata(MetadataMarker)))
        using (var output = new MemoryStream())
        {
            PdfPublicKeyEncryptor.Encrypt(input, output, new[] { certA, certB }, PdfPermissions.All,
                EncryptionAlgorithm.AES256, enablePdfMac: false, encryptMetadata: true);
            encrypted = output.ToArray();
        }

        byte[] kept;
        using (var input = new MemoryStream(encrypted))
        using (var output = new MemoryStream())
        using (var store = new MemoryStream(pfxA))
        {
            RoundTripResult result = PdfEncryptedRoundTrip.Run(input, output,
                new EncryptedRoundTripOptions
                {
                    Policy = ReEncryptionPolicy.KeepOriginal,
                    Credential = PdfDecryptionCredential.Pkcs12(store, PfxPassword),
                },
                (plain, produced) => plain.CopyTo(produced));
            Assert.True(result.Succeeded, result.FailureReason);
            kept = output.ToArray();
        }

        using (var before = Open(encrypted))
        using (var after = Open(kept))
        {
            var original = Recipients(before);
            var written = Recipients(after);
            Assert.Equal(2, written.Count);
            for (int i = 0; i < original.Count; i++) Assert.Equal(original[i], written[i]);
        }
        Assert.Contains(Marker, PageContent(Decrypt(kept, pfxB)));
        Assert.DoesNotContain(Marker, Latin1.GetString(kept), StringComparison.Ordinal);
    }

    // ================================================================ what the decryptor opens

    /// <summary>
    /// A document built here from the standard's text alone — envelope, digest, cipher — opens:
    /// with the metadata encrypted, and with it in plaintext whether the producer put
    /// <c>/EncryptMetadata false</c> on the crypt filter (Table 27) or on <c>/Encrypt</c>
    /// (Table 21, where an independent implementation reads and writes it).
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void ADocumentBuiltFromTheStandardsText_Opens(bool plaintextMetadata, bool flagOnCryptFilter)
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] built = BuildByTheStandard(certDer, pfx, plaintextMetadata, flagOnCryptFilter, version: "4");

        byte[] decrypted = Decrypt(built, pfx);

        Assert.Contains(Marker, PageContent(decrypted));
        Assert.Contains(MetadataMarker, Latin1.GetString(Stream(decrypted, MetadataObjectNumber).GetDecodedData()));
    }

    /// <summary>
    /// Which cipher the document is under is the crypt filter's answer, not <c>/V</c>'s alone: a
    /// <c>/V 4</c> public-key document whose filter's <c>/CFM</c> is <c>/V2</c> is RC4, and opens.
    /// </summary>
    [Fact]
    public void ADocumentWhoseCryptFilterIsRc4_Opens()
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] built = BuildByTheStandard(certDer, pfx, plaintextMetadata: false, flagOnCryptFilter: false, version: "4", rc4: true);

        byte[] decrypted = Decrypt(built, pfx);

        Assert.Contains(Marker, PageContent(decrypted));
        Assert.Contains(MetadataMarker, Latin1.GetString(Stream(decrypted, MetadataObjectNumber).GetDecodedData()));
    }

    /// <summary>
    /// The public-key writer writes the graph the parser hands it; a resolved <c>/DecodeParms</c>
    /// goes back as its reference before it does (<see cref="ParserInlinedObjectTests"/>).
    /// </summary>
    [Fact]
    public void Encrypt_KeepsAResolvedDecodeParmsChainIntact()
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] encrypted = Encrypt(TestPdfs.Plaintext.Build(indirectDecodeParms: true), certDer, PdfPermissions.All,
            EncryptionAlgorithm.AES128, encryptMetadata: true);

        ParserInlinedObjectTests.AssertChainIntact(Decrypt(encrypted, pfx));
    }

    /// <summary>
    /// A <c>/V</c> beyond the <see cref="int"/> range is reported saturated, not as the sign-flipped
    /// value an unchecked cast of the double gave (<c>V=-2147483648</c>).
    /// </summary>
    [Fact]
    public void AnOutOfRangeVersion_IsReportedSaturated()
    {
        var (certDer, pfx) = MakeRecipient();
        byte[] built = BuildByTheStandard(certDer, pfx, plaintextMetadata: false, flagOnCryptFilter: false, version: "99999999999.5");

        var ex = Assert.Throws<PdfException>(() => Decrypt(built, pfx));
        Assert.Contains("V=2147483647", ex.Message);
    }

    // ================================================================ the standard, implemented here

    /// <summary>
    /// §7.6.5.3: SHA-1 (128-bit keys) or SHA-256 (256-bit) over the seed, every recipient blob in
    /// array order, and — metadata in plaintext — four <c>0xFF</c> bytes; the first n/8 bytes.
    /// </summary>
    private static byte[] StandardsFileKey(byte[] seed, List<byte[]> recipients, EncryptionAlgorithm algorithm, bool plaintextMetadata)
    {
        var input = new List<byte>(seed);
        foreach (byte[] der in recipients) input.AddRange(der);
        if (plaintextMetadata) input.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });

        return algorithm == EncryptionAlgorithm.AES256
            ? SHA256.HashData(input.ToArray())
            : SHA1.HashData(input.ToArray()).Take(16).ToArray();
    }

    /// <summary>Algorithm 1's per-object key for AESV2; AESV3 uses the file key as it is.</summary>
    private static byte[] ObjectKey(byte[] fileKey, int objectNumber, EncryptionAlgorithm algorithm)
        => algorithm == EncryptionAlgorithm.AES256
            ? fileKey
            : PdfEncryptionPrimitives.DeriveObjectKeyMd5(fileKey, objectNumber, 0, isAes: true);

    private static byte[] DecryptStream(PdfDocument doc, int objectNumber, byte[] fileKey, EncryptionAlgorithm algorithm)
        => PdfDecryptor.AesCbcDecrypt(ObjectKey(fileKey, objectNumber, algorithm), Stream(doc, objectNumber).RawData);

    /// <summary>As above, but a wrong key's padding failure is an empty answer rather than a throw.</summary>
    private static byte[] TryDecryptStream(PdfDocument doc, int objectNumber, byte[] fileKey, EncryptionAlgorithm algorithm)
    {
        try { return DecryptStream(doc, objectNumber, fileKey, algorithm); }
        catch (PdfException) { return Array.Empty<byte>(); }
        catch (CryptographicException) { return Array.Empty<byte>(); }
    }

    /// <summary>
    /// An <c>/Adobe.PubSec</c> AESV2 document written from the standard's text: the envelope by
    /// <see cref="EnvelopedCms"/> (seed, then the permissions), the key by
    /// <see cref="StandardsFileKey"/>, each stream under Algorithm 1 — and none of it by the
    /// class under test.
    /// </summary>
    private static byte[] BuildByTheStandard(byte[] certDer, byte[] pfx, bool plaintextMetadata, bool flagOnCryptFilter, string version,
        bool rc4 = false)
    {
        int permissions = -3904;   // print and copy, the reserved bits set, nothing else
        byte[] seed = RandomNumberGenerator.GetBytes(20);
        byte[] content = seed
            .Concat(new[] { (byte)(permissions >> 24), (byte)(permissions >> 16), (byte)(permissions >> 8), (byte)permissions })
            .ToArray();

        using X509Certificate2 cert = X509CertificateLoader.LoadCertificate(certDer);
        var cms = new EnvelopedCms(new ContentInfo(content), new AlgorithmIdentifier(new Oid("2.16.840.1.101.3.4.1.2")));
        cms.Encrypt(new CmsRecipient(cert));
        byte[] recipient = cms.Encode();

        byte[] fileKey = StandardsFileKey(seed, new List<byte[]> { recipient }, EncryptionAlgorithm.AES128, plaintextMetadata);

        string page = $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET";
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><dc:title>{MetadataMarker}</dc:title></x:xmpmeta>";
        // Algorithm 1's per-object key, under the filter's cipher: AESV2 with its IV and padding, or
        // V2 (RC4) over the bytes as they are.
        byte[] Encipher(int objectNumber, byte[] plain) => rc4
            ? PdfEncryptionPrimitives.Rc4Process(PdfEncryptionPrimitives.DeriveObjectKeyMd5(fileKey, objectNumber, 0, isAes: false), plain)
            : PdfEncryptor.AesCbcEncrypt(ObjectKey(fileKey, objectNumber, EncryptionAlgorithm.AES128), plain);
        string pageBytes = Latin1.GetString(Encipher(ContentObjectNumber, Latin1.GetBytes(page)));
        string xmpBytes = plaintextMetadata
            ? xmp
            : Latin1.GetString(Encipher(MetadataObjectNumber, Latin1.GetBytes(xmp)));

        string hex = BitConverter.ToString(recipient).Replace("-", "");
        string flag = plaintextMetadata ? " /EncryptMetadata false" : "";
        string cfm = rc4 ? "V2" : "AESV2";
        string encrypt =
            $"<< /Filter /Adobe.PubSec /SubFilter /adbe.pkcs7.s5 /V {version} /R 4 /Length 128 /P {permissions} " +
            $"/Recipients [<{hex}>] " +
            $"/CF << /DefaultCryptFilter << /Type /CryptFilter /CFM /{cfm} /AuthEvent /DocOpen /Length 128 " +
            $"/Recipients [<{hex}>]{(flagOnCryptFilter ? flag : "")} >> >> " +
            $"/StmF /DefaultCryptFilter /StrF /DefaultCryptFilter{(flagOnCryptFilter ? "" : flag)} >>";

        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {pageBytes.Length} >>\nstream\n{pageBytes}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /Metadata /Subtype /XML /Length {xmpBytes.Length} >>\nstream\n{xmpBytes}\nendstream",
            encrypt,
        }, " /Encrypt 7 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>]");
    }

    // ================================================================ fixtures

    private static (byte[] certDer, byte[] pfx) MakeRecipient()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Envelope Layout Recipient", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return (cert.Export(X509ContentType.Cert), cert.Export(X509ContentType.Pfx, PfxPassword));
    }

    /// <summary>The envelope's content, opened with the platform's CMS rather than the library's BouncyCastle path.</summary>
    private static byte[] OpenEnvelope(byte[] der, byte[] pfx)
    {
        using X509Certificate2 cert = X509CertificateLoader.LoadPkcs12(pfx, PfxPassword);
        var enveloped = new EnvelopedCms();
        enveloped.Decode(der);
        enveloped.Decrypt(new X509Certificate2Collection(cert));
        return enveloped.ContentInfo.Content;
    }

    private static List<byte[]> Recipients(PdfDocument doc)
    {
        var array = (PdfArray)doc.ResolveObject(doc.EncryptionDictionary![new PdfName("Recipients")]);
        var result = new List<byte[]>();
        for (int i = 0; i < array.Count; i++) result.Add(((PdfString)doc.ResolveObject(array[i])).RawBytes);
        return result;
    }

    private static byte[] Encrypt(byte[] pdf, byte[] certDer, PdfPermissions permissions, EncryptionAlgorithm algorithm, bool encryptMetadata)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, new[] { certDer }, permissions, algorithm, enablePdfMac: false, encryptMetadata);
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, byte[] pfx)
    {
        using var input = new MemoryStream(encrypted);
        using var store = new MemoryStream(pfx);
        using var output = new MemoryStream();
        PdfPublicKeyDecryptor.Decrypt(input, output, store, PfxPassword);
        return output.ToArray();
    }

    private static PdfDocument Open(byte[] pdf) => PdfDocument.Open(new MemoryStream(pdf, writable: false));

    private static PdfStream Stream(PdfDocument doc, int objectNumber)
        => (PdfStream)doc.ResolveObject(new PdfReference(objectNumber, 0));

    private static PdfStream Stream(byte[] pdf, int objectNumber)
    {
        using var doc = Open(pdf);
        return Stream(doc, objectNumber);
    }

    private static string PageContent(byte[] pdf)
    {
        using var doc = Open(pdf);
        return Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static byte[] SourcePdf()
    {
        string content = $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET";
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        }, "");
    }

    private static byte[] WithMetadata(string marker)
    {
        string content = $"BT /F1 12 Tf 72 700 Td ({Marker}) Tj ET";
        string xmp = $"<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><dc:title>{marker}</dc:title></x:xmpmeta>";
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Type /Metadata /Subtype /XML /Length {xmp.Length} >>\nstream\n{xmp}\nendstream",
        }, "");
    }

    /// <summary>Bodies as Latin-1 text — one char per byte, so cipher bytes pass through and every <c>/Length</c> is right.</summary>
    private static byte[] Build(string[] bodies, string trailerExtra)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length + 1];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append('\n').Append("0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Length; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R").Append(trailerExtra).Append(" >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Latin1.GetBytes(sb.ToString());
    }
}
