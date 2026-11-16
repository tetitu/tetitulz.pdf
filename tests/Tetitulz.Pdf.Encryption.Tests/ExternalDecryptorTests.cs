// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Encryption;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// <see cref="IExternalDecryptor"/> — opening an <c>/Adobe.PubSec</c> document with a key that
/// never leaves the device holding it.
/// </summary>
/// <remarks>
/// <para><b>The stub here decrypts with <c>System.Security.Cryptography.Pkcs.EnvelopedCms</c>,
/// deliberately, and not with BouncyCastle.</b> BouncyCastle is exactly what a hardware key cannot
/// satisfy — it needs an <c>AsymmetricKeyParameter</c>, and a non-exportable CNG key cannot produce
/// one — so a stub built on it would prove the plumbing while dodging the reason the seam exists.
/// <c>EnvelopedCms.Decrypt</c> is the mechanism a Windows smart-card implementation would actually
/// use; the only thing this stub does that a card does not is hold its key in memory.</para>
///
/// <para><b>Two properties matter beyond "it opens".</b> That the file key is still derived from
/// <i>every</i> <c>/Recipients</c> entry, not just the one the device opened — a reader that got
/// that wrong would pass any single-recipient test. And that the device is asked to open exactly
/// one envelope: <c>/Recipients</c> is attacker-controlled, and a card asked to open envelopes not
/// addressed to it prompts for a PIN each time, which is a lockout a hostile file must not be able
/// to reach.</para>
/// </remarks>
public class ExternalDecryptorTests
{
    private const string Marker = "ExternalDecryptorRoundTripMarker";

    // ================================================================ the device

    /// <summary>
    /// A stand-in for a smart card: it will name its certificate, and it will open an envelope
    /// addressed to that certificate. It counts both, because <b>how often</b> it is asked is as
    /// much part of the contract as what it answers.
    /// </summary>
    private sealed class DeviceStub : IExternalDecryptor
    {
        private readonly X509Certificate2 _cert;

        internal int CertificateReads;
        internal int UnwrapCalls;
        internal readonly List<byte[]> Received = new();

        /// <summary>Simulates a device that recognises the envelope and still refuses it.</summary>
        internal bool Declines;

        /// <summary>Overrides what <see cref="GetRecipientCertificate"/> answers.</summary>
        internal byte[]? CertificateOverride;
        internal bool OverrideCertificate;

        internal DeviceStub(X509Certificate2 cert) => _cert = cert;

        public byte[] GetRecipientCertificate()
        {
            CertificateReads++;
            return OverrideCertificate ? CertificateOverride! : _cert.RawData;
        }

        public byte[] UnwrapEnvelope(byte[] cmsEnvelopedData)
        {
            UnwrapCalls++;
            Received.Add(cmsEnvelopedData);
            if (Declines) return null!;

            var enveloped = new EnvelopedCms();
            enveloped.Decode(cmsEnvelopedData);
            enveloped.Decrypt(new X509Certificate2Collection(_cert));
            return enveloped.ContentInfo.Content;
        }
    }

    // ================================================================ fixtures

    private const string PfxPassword = "pfx-pw";

    /// <summary>A self-signed RSA recipient: its DER certificate, a PKCS#12, and the live cert.</summary>
    private static (byte[] certDer, byte[] pfx, X509Certificate2 cert) MakeRecipient(
        string commonName = "CN=External Test Recipient")
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(commonName, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        byte[] pfx = cert.Export(X509ContentType.Pfx, PfxPassword);

        // Re-imported from the PFX so the instance the stub holds carries a usable private key on
        // every host, rather than the ephemeral key handle CreateSelfSigned leaves behind.
        return (cert.Export(X509ContentType.Cert), pfx, X509CertificateLoader.LoadPkcs12(pfx, PfxPassword));
    }

    /// <summary>A one-page document carrying <see cref="Marker"/> in its content stream.</summary>
    private static byte[] SourcePdf()
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

    private static byte[] Encrypt(byte[] pdf, params byte[][] certDers)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, certDers, PdfPermissions.All, EncryptionAlgorithm.AES256);
        return output.ToArray();
    }

    private static byte[] Decrypt(byte[] encrypted, IExternalDecryptor device)
    {
        using var input = new MemoryStream(encrypted);
        using var output = new MemoryStream();
        PdfPublicKeyDecryptor.Decrypt(input, output, device);
        return output.ToArray();
    }

    private static byte[] DecryptWithPfx(byte[] encrypted, byte[] pfx)
    {
        using var input = new MemoryStream(encrypted);
        using var store = new MemoryStream(pfx);
        using var output = new MemoryStream();
        PdfPublicKeyDecryptor.Decrypt(input, output, store, PfxPassword);
        return output.ToArray();
    }

    /// <summary>The page's decoded content stream, as text.</summary>
    private static string PageContent(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return Encoding.GetEncoding("iso-8859-1").GetString(doc.Pages[0].GetContentStreamData());
    }

    // ================================================================ the seam works

    /// <summary>
    /// A key the library never sees opens the document, and produces byte-for-byte what the PKCS#12
    /// path produces from the same input.
    /// </summary>
    /// <remarks>
    /// The equality is the load-bearing half. "The external path returned a PDF" would pass for a
    /// path that recovered a different seed, skipped the permissions offset, or hashed the
    /// recipients in another order — each of which yields a document that parses and is wrong.
    /// </remarks>
    [Fact]
    public void ADeviceHeldKeyOpensTheDocumentAndAgreesWithThePkcs12Path()
    {
        var (certDer, pfx, cert) = MakeRecipient();
        using (cert)
        {
            byte[] encrypted = Encrypt(SourcePdf(), certDer);

            // The control: the marker really is hidden, so recovering it is evidence of decryption.
            Assert.DoesNotContain(Marker, Encoding.GetEncoding("iso-8859-1").GetString(encrypted));

            var device = new DeviceStub(cert);
            byte[] throughDevice = Decrypt(encrypted, device);

            Assert.Contains(Marker, PageContent(throughDevice));
            Assert.Equal(DecryptWithPfx(encrypted, pfx), throughDevice);
            Assert.Equal(1, device.UnwrapCalls);
        }
    }

    /// <summary>
    /// The decrypted document no longer declares itself encrypted — the external path runs the same
    /// object walk and trailer rebuild, not a second copy of it.
    /// </summary>
    [Fact]
    public void TheExternalPathRemovesTheEncryptDictionaryToo()
    {
        var (certDer, _, cert) = MakeRecipient();
        using (cert)
        {
            byte[] decrypted = Decrypt(Encrypt(SourcePdf(), certDer), new DeviceStub(cert));

            using var ms = new MemoryStream(decrypted);
            using var doc = PdfDocument.Open(ms);

            Assert.False(doc.IsEncrypted);
            Assert.Null(doc.Trailer[Core.Objects.PdfName.Encrypt]);
        }
    }

    // ================================================================ the derivation stays here

    /// <summary>
    /// <b>The file key is derived from every <c>/Recipients</c> entry, not just the opened one.</b>
    /// </summary>
    /// <remarks>
    /// The key is <c>H(seed ‖ recipient₁ ‖ recipient₂ ‖ …)</c>. The device supplies only the seed,
    /// so a path that hashed just the envelope it opened — or reordered them — derives a different
    /// key and produces rubbish. With one recipient the two are the same computation, which is why
    /// this fixture uses five and puts the device's own certificate last.
    /// </remarks>
    [Fact]
    public void TheKeyIsDerivedFromEveryRecipientNotOnlyTheOpenedOne()
    {
        var (aDer, _, aCert) = MakeRecipient("CN=Other A");
        var (bDer, _, bCert) = MakeRecipient("CN=Other B");
        var (cDer, _, cCert) = MakeRecipient("CN=Other C");
        var (dDer, _, dCert) = MakeRecipient("CN=Other D");
        var (mineDer, minePfx, mineCert) = MakeRecipient("CN=The Device");

        using (aCert) using (bCert) using (cCert) using (dCert) using (mineCert)
        {
            byte[] encrypted = Encrypt(SourcePdf(), aDer, bDer, cDer, dDer, mineDer);
            var device = new DeviceStub(mineCert);

            byte[] decrypted = Decrypt(encrypted, device);

            Assert.Contains(Marker, PageContent(decrypted));
            Assert.Equal(DecryptWithPfx(encrypted, minePfx), decrypted);
        }
    }

    /// <summary>
    /// <b>The device is asked to open exactly one envelope, the one addressed to it.</b>
    /// </summary>
    /// <remarks>
    /// This is why <see cref="IExternalDecryptor.GetRecipientCertificate"/> exists at all: the
    /// certificate match happens in the library, against public data, before the key is touched. A
    /// path that handed every envelope to the device would work identically here and would prompt a
    /// card for a PIN once per entry of an attacker-supplied array — enough refusals lock it.
    /// </remarks>
    [Fact]
    public void TheDeviceIsAskedToOpenExactlyOneEnvelopeOfMany()
    {
        var (aDer, _, aCert) = MakeRecipient("CN=Other A");
        var (bDer, _, bCert) = MakeRecipient("CN=Other B");
        var (cDer, _, cCert) = MakeRecipient("CN=Other C");
        var (mineDer, _, mineCert) = MakeRecipient("CN=The Device");

        using (aCert) using (bCert) using (cCert) using (mineCert)
        {
            // The device's certificate is LAST, so a path that tried them in order would have made
            // three refused calls before reaching it.
            var device = new DeviceStub(mineCert);
            Decrypt(Encrypt(SourcePdf(), aDer, bDer, cDer, mineDer), device);

            Assert.Equal(1, device.UnwrapCalls);
            Assert.Equal(1, device.CertificateReads);
        }
    }

    /// <summary>
    /// The single envelope the device receives is the one <b>its own</b> key opens, and not the
    /// other recipient's.
    /// </summary>
    /// <remarks>
    /// Asserted by capability rather than by reading the recipient identifier: what the library
    /// promises an implementation is that the blob it hands over is openable, and the other
    /// recipient's blob failing to open is what makes that a claim rather than a coincidence.
    /// </remarks>
    [Fact]
    public void TheEnvelopeHandedOverIsTheOneAddressedToTheDevice()
    {
        var (otherDer, _, otherCert) = MakeRecipient("CN=Other");
        var (mineDer, _, mineCert) = MakeRecipient("CN=The Device");

        using (otherCert) using (mineCert)
        {
            var device = new DeviceStub(mineCert);
            Decrypt(Encrypt(SourcePdf(), otherDer, mineDer), device);

            byte[] handedOver = Assert.Single(device.Received);

            Assert.Equal(24, OpenWith(handedOver, mineCert).Length);   // 4-byte /P mask + 20-byte seed
            Assert.Throws<CryptographicException>(() => OpenWith(handedOver, otherCert));
        }
    }

    private static byte[] OpenWith(byte[] cms, X509Certificate2 cert)
    {
        var enveloped = new EnvelopedCms();
        enveloped.Decode(cms);
        enveloped.Decrypt(new X509Certificate2Collection(cert));
        return enveloped.ContentInfo.Content;
    }

    // ================================================================ refusals, each for its own reason

    /// <summary>
    /// A device holding a key the document was not encrypted for is refused <b>without the key
    /// being touched</b>.
    /// </summary>
    [Fact]
    public void ADeviceThatIsNotARecipientIsRefusedBeforeTheKeyIsUsed()
    {
        var (recipientDer, _, recipientCert) = MakeRecipient("CN=The Recipient");
        var (_, _, strangerCert) = MakeRecipient("CN=A Stranger");

        using (recipientCert) using (strangerCert)
        {
            var device = new DeviceStub(strangerCert);

            var ex = Assert.Throws<PdfException>(
                () => Decrypt(Encrypt(SourcePdf(), recipientDer), device));

            Assert.Contains("recipients", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, device.UnwrapCalls);
        }
    }

    /// <summary>
    /// A device that recognises the envelope and declines it is named as the cause, rather than
    /// reported as a document encrypted for somebody else.
    /// </summary>
    /// <remarks>
    /// The two are different problems with different fixes — a wrong card versus a card that
    /// refused — and a null return must not be read as a seed of zero length either.
    /// </remarks>
    [Fact]
    public void ADeviceThatDeclinesIsNamedRatherThanBlamedOnTheDocument()
    {
        var (certDer, _, cert) = MakeRecipient();
        using (cert)
        {
            var device = new DeviceStub(cert) { Declines = true };

            var ex = Assert.Throws<PdfException>(
                () => Decrypt(Encrypt(SourcePdf(), certDer), device));

            Assert.Contains("declined", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("None of the document's recipients", ex.Message);
            Assert.Equal(1, device.UnwrapCalls);
        }
    }

    /// <summary>
    /// A device that names <b>no</b> certificate is told that, not told the document was encrypted
    /// for somebody else.
    /// </summary>
    /// <remarks>
    /// <b>The two unusable-certificate diagnostics are asserted separately because they are
    /// different problems.</b> A first draft of this asserted only the substring both messages
    /// share, and the mutation that deletes this guard survived: the parse failure downstream
    /// produced a message that also said "external decryptor", so the weaker assertion could not
    /// tell "you returned nothing" from "you returned something that is not a certificate" — and
    /// those have different fixes.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void ADeviceThatNamesNoCertificateIsToldExactlyThat(byte[]? certificate)
    {
        var (certDer, _, cert) = MakeRecipient();
        using (cert)
        {
            var device = new DeviceStub(cert) { OverrideCertificate = true, CertificateOverride = certificate };

            var ex = Assert.Throws<PdfException>(
                () => Decrypt(Encrypt(SourcePdf(), certDer), device));

            Assert.Contains("returned no recipient certificate", ex.Message);
            Assert.Equal(0, device.UnwrapCalls);
        }
    }

    /// <summary>
    /// A device that names something that is not a certificate is told <b>that</b>, and the
    /// document is not blamed for it.
    /// </summary>
    /// <remarks>
    /// <c>X509CertificateParser.ReadCertificate</c> answers <c>null</c> here rather than throwing.
    /// Guarded with a <c>catch</c> alone, the null flowed on, every <c>RecipientID.Match</c> said
    /// no, and the caller was told the document had been encrypted for somebody else.
    /// </remarks>
    [Fact]
    public void ADeviceCertificateThatIsNotACertificateIsToldExactlyThat()
    {
        var (certDer, _, cert) = MakeRecipient();
        using (cert)
        {
            var device = new DeviceStub(cert)
            {
                OverrideCertificate = true,
                CertificateOverride = new byte[] { 1, 2, 3, 4 },
            };

            var ex = Assert.Throws<PdfException>(
                () => Decrypt(Encrypt(SourcePdf(), certDer), device));

            Assert.Contains("not a DER-encoded X.509 certificate", ex.Message);
            Assert.DoesNotContain("None of the document's recipients", ex.Message);
            Assert.Equal(0, device.UnwrapCalls);
        }
    }

    /// <summary>A password-protected document is refused by name, not opened as if it were PubSec.</summary>
    [Fact]
    public void AStandardHandlerDocumentIsRefusedByName()
    {
        var (_, _, cert) = MakeRecipient();
        using (cert)
        {
            using var input = new MemoryStream(SourcePdf());
            using var encrypted = new MemoryStream();
            PdfEncryptor.Encrypt(input, encrypted, new EncryptionParameters
            {
                UserPassword = "user-pw",
                OwnerPassword = "owner-pw",
                Algorithm = EncryptionAlgorithm.AES256,
            });

            var ex = Assert.Throws<PdfException>(
                () => Decrypt(encrypted.ToArray(), new DeviceStub(cert)));

            Assert.Contains("/Adobe.PubSec", ex.Message);
        }
    }

    [Fact]
    public void NullArgumentsAreRefused()
    {
        var (_, _, cert) = MakeRecipient();
        using (cert)
        {
            var device = new DeviceStub(cert);
            using var stream = new MemoryStream();

            Assert.Throws<ArgumentNullException>(
                () => PdfPublicKeyDecryptor.Decrypt(null!, stream, device));
            Assert.Throws<ArgumentNullException>(
                () => PdfPublicKeyDecryptor.Decrypt(stream, null!, device));
            Assert.Throws<ArgumentNullException>(
                () => PdfPublicKeyDecryptor.Decrypt(stream, stream, (IExternalDecryptor)null!));
        }
    }
}
