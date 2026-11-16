// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Org.BouncyCastle.Cms;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfCounterSigner"/>: which signatures it agrees to counter-sign, the digest it writes,
/// and what its size estimate is made of.
/// </summary>
public sealed class CounterSignatureBoundaryTests
{
    /// <summary>
    /// Only a detached CMS — <c>adbe.pkcs7.detached</c> or <c>ETSI.CAdES.detached</c> — carries a
    /// SignerInfo a counter-signature can attach to. Any other <c>/SubFilter</c> is refused before
    /// anything is written; the two detached forms are counter-signed.
    /// </summary>
    /// <remarks>
    /// The sub-filter is rewritten in place to a name of the same length, so every offset in the
    /// file — the <c>/ByteRange</c> included — still holds.
    /// </remarks>
    [Theory]
    [InlineData("adbe.pkcs7.detached", true)]
    [InlineData("ETSI.CAdES.detached", true)]
    [InlineData("adbe.x509.rsa_sha1 ", false)]
    [InlineData("adbe.pkcs7.sha1    ", false)]
    public void OnlyADetachedCmsIsCounterSigned(string subFilter, bool accepted)
    {
        var (signed, counterPfx, counterPw) = SignedDocument();
        byte[] pdf = WithSubFilter(signed, subFilter);

        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        void CounterSign() => PdfCounterSigner.AddCounterSignature(input, output, "Signature1", counterPfx, counterPw);

        if (accepted)
        {
            CounterSign();
            Assert.NotEmpty(CounterSigners(output.ToArray()));
        }
        else
        {
            var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfSigningException>(CounterSign);
            Assert.Contains("only supported for adbe.pkcs7.detached / ETSI.CAdES.detached", ex.Message);
            Assert.Equal(0, output.Length);
        }
    }

    /// <summary>
    /// The counter-signature is made with the digest the caller asked for: SHA-512 is written as
    /// SHA-512, and the SHA-256 control as SHA-256.
    /// </summary>
    [Theory]
    [InlineData("SHA-512", "2.16.840.1.101.3.4.2.3")]
    [InlineData("SHA-256", "2.16.840.1.101.3.4.2.1")]
    public void TheCounterSignatureUsesTheRequestedDigest(string digest, string expectedOid)
    {
        var (signed, counterPfx, counterPw) = SignedDocument();

        using var input = new MemoryStream(signed);
        using var output = new MemoryStream();
        PdfCounterSigner.AddCounterSignature(input, output, "Signature1", counterPfx, counterPw, digest);

        var counterSigners = CounterSigners(output.ToArray());
        Assert.NotEmpty(counterSigners);
        Assert.All(counterSigners, cs => Assert.Equal(expectedOid, cs.DigestAlgOid));
    }

    /// <summary>
    /// The size estimate counts the counter-signer's actual certificate: two certificates of
    /// different encoded sizes give estimates exactly that many bytes apart.
    /// </summary>
    [Fact]
    public void EstimateCmsSize_CountsTheCertificatesOwnSize()
    {
        var root = SigningTestCrypto.Ca("CN=Estimate Root");
        var small = new X509Certificate2(SigningTestCrypto.Leaf(root, "CN=S").Cert.GetEncoded());
        var large = new X509Certificate2(SigningTestCrypto.Leaf(root,
            "CN=A Considerably Longer Counter-Signer Name, O=An Organisation With A Long Name, " +
            "OU=A Department With A Long Name, L=Somewhere, C=IL").Cert.GetEncoded());
        Assert.NotEqual(small.RawData.Length, large.RawData.Length);   // guards the fixture

        byte[] original = new byte[3000];

        int difference = PdfCounterSigner.EstimateCmsSize(original, large)
                       - PdfCounterSigner.EstimateCmsSize(original, small);

        Assert.Equal(large.RawData.Length - small.RawData.Length, difference);
    }

    // ------------------------------------------------------------------ fixtures

    private static (byte[] Signed, byte[] CounterPfx, string CounterPassword) SignedDocument()
    {
        var root = SigningTestCrypto.Ca();
        var signer = SigningTestCrypto.Leaf(root, "CN=Original Signer");
        var counter = SigningTestCrypto.Leaf(root, "CN=Counter Signer");

        var (signerPfx, signerPw) = SigningTestCrypto.Pfx(signer, root.Cert);
        var (counterPfx, counterPw) = SigningTestCrypto.Pfx(counter, root.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), signerPfx, signerPw);
        return (signed, counterPfx, counterPw);
    }

    private static byte[] WithSubFilter(byte[] pdf, string subFilter)
    {
        string text = Encoding.Latin1.GetString(pdf);
        const string written = "/SubFilter /adbe.pkcs7.detached";
        Assert.Equal(1, CountOf(text, written));   // guards the fixture: the signer's own form
        Assert.Equal("adbe.pkcs7.detached".Length, subFilter.Length);
        return Encoding.Latin1.GetBytes(text.Replace(written, "/SubFilter /" + subFilter));
    }

    private static int CountOf(string text, string value)
    {
        int count = 0;
        for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static List<SignerInformation> CounterSigners(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var contents = doc.AcroForm!.FindSignatureField("Signature1")!.SignatureValue!.GetString(PdfName.Contents);
        var cms = new CmsSignedData(SigningDefaults.TrimToAsn1Object(contents!.RawBytes));

        var result = new List<SignerInformation>();
        foreach (SignerInformation s in cms.GetSignerInfos().GetSigners())
            result.AddRange(s.GetCounterSignatures().GetSigners());
        return result;
    }
}
