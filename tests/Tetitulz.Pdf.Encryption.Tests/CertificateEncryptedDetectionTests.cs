// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Encryption;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Encryption.Tests;

/// <summary>
/// A certificate-encrypted document must not be classified as a password-encrypted one.
/// </summary>
/// <remarks>
/// <c>/V</c> and the <c>/StdCF</c> <c>/CFM</c> describe the CIPHER, not who holds the key: an
/// <c>/Adobe.PubSec</c> document — which this assembly writes itself, through
/// <c>PdfPublicKeyEncryptor</c> — carries <c>/V 5</c> and <c>/CFM /AESV3</c> exactly as a
/// <c>/Standard</c> AES-256 one does. <c>TryDetectAlgorithm</c> read only those, so
/// <c>TryDeriveParameters</c> — public, and documented as answering "what protection will my output
/// actually have?" and as failing rather than downgrading — returned <c>true</c> with
/// <c>AES256</c> and an EMPTY password, i.e. announced a silent change from certificate-based to
/// password-based protection.
/// </remarks>
public class CertificateEncryptedDetectionTests
{
    [Fact]
    public void GetEncryptionInfo_ReportsTheSecurityHandler()
    {
        using var ms = new MemoryStream(CertificateEncrypted());

        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(ms);

        Assert.Equal("Adobe.PubSec", info.Filter);
    }

    [Fact]
    public void TryDeriveParameters_RefusesACertificateEncryptedDocument()
    {
        using var ms = new MemoryStream(CertificateEncrypted());
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(ms);

        bool ok = PdfEncryptedRoundTrip.TryDeriveParameters(
            info, new EncryptedRoundTripOptions(), out EncryptionParameters parameters, out string reason);

        Assert.False(ok, "a certificate-encrypted document was reported as reproducible with a password");
        Assert.Null(parameters);
        Assert.Contains("Adobe.PubSec", reason);
    }

    /// <summary>
    /// The control: an ordinary password-encrypted document must still derive parameters, so the
    /// refusal above is about the handler and not about refusing everything.
    /// </summary>
    [Fact]
    public void TryDeriveParameters_StillAcceptsAStandardDocument()
    {
        using var plain = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var encrypted = new MemoryStream();
        PdfEncryptor.Encrypt(plain, encrypted, new EncryptionParameters
        {
            UserPassword = "pw",
            OwnerPassword = "pw",
            Algorithm = EncryptionAlgorithm.AES256,
        });

        using var ms = new MemoryStream(encrypted.ToArray());
        EncryptionInfo info = PdfDecryptor.GetEncryptionInfo(ms);

        bool ok = PdfEncryptedRoundTrip.TryDeriveParameters(
            info, new EncryptedRoundTripOptions { OpenPassword = "pw" },
            out EncryptionParameters parameters, out string reason);

        Assert.True(ok, reason);
        Assert.Equal(EncryptionAlgorithm.AES256, parameters!.Algorithm);
    }

    /// <summary>
    /// A foreign security handler is refused by name, whether <c>/Filter</c> is written inline or
    /// as its own object.
    /// </summary>
    /// <remarks>
    /// <c>GetEncryptionInfo</c>, <c>PdfPublicKeyDecryptor</c> and the password path's handler check
    /// all resolve <c>/Filter</c>: unresolved, an indirect <c>/Adobe.PubSec</c> would pass the check
    /// as if it were <c>/Standard</c> and the caller be told the password was wrong.
    /// </remarks>
    [Theory]
    [InlineData("/Adobe.PubSec")]
    [InlineData("6 0 R")]
    public void Decrypt_RefusesAForeignHandlerByName_WhicheverWayFilterIsSpelled(string filter)
    {
        string encrypt = $"<< /Filter {filter} /V 5 /R 6 /Length 256 /P -4 >>";
        byte[] pdf = BuildEncrypted(encrypt, "/Adobe.PubSec");

        var ex = Assert.ThrowsAny<Exception>(() =>
            PdfDecryptor.Decrypt(new MemoryStream(pdf), new MemoryStream(), "pw"));

        Assert.Contains("Unsupported encryption handler: /Adobe.PubSec", ex.Message);
    }

    /// <summary>Catalog, empty page tree, the given /Encrypt dictionary, and a name at object 6.</summary>
    private static byte[] BuildEncrypted(string encryptDict, string object6)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "null",
            "null",
            encryptDict,
            object6,
        };
        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1)
          .Append(" /Root 1 0 R /Encrypt 5 0 R /ID [<0123456789ABCDEF0123456789ABCDEF> <0123456789ABCDEF0123456789ABCDEF>] >>\n")
          .Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] CertificateEncrypted()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Test Recipient", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var input = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());
        using var output = new MemoryStream();
        PdfPublicKeyEncryptor.Encrypt(input, output, cert.Export(X509ContentType.Cert));
        return output.ToArray();
    }
}
