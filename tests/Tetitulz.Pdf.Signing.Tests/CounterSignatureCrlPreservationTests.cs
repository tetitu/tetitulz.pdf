// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Counter-signing must preserve the original <c>SignedData</c>'s CRLs.
/// </summary>
/// <remarks>
/// <para><c>CmsSignedData.ReplaceCertificatesAndCrls</c> <b>replaces</b> that field rather than
/// merging into it, so passing <c>null</c> strips every CRL the CMS carried — which on a PAdES
/// B-LT / LTV signature is its embedded revocation material. Counter-signing would add its
/// SignerInfo and destroy the long-term validation data in the same call, and report success.</para>
/// <para>This is a fix that was <b>lost once already</b>, by a parallel merge, precisely because it
/// is an <i>absence</i>: nothing asserted a CRL survived, so removing the argument broke no test
/// (GAPS.md §16, and the parallel-merge rule in CLAUDE.md).</para>
/// <para><b>The fixture is the hard part, and it is built rather than spliced.</b> Neither
/// <c>PdfSigner</c> nor <see cref="SigningTestCrypto"/> produces a CMS carrying a CRL, and
/// <c>InjectCounterSignature</c> is private. So a normally-signed document's <c>/Contents</c> is
/// read out, the CRL is added to that CMS with BouncyCastle, and the re-encoded bytes are written
/// back into the same reserved hex region — the region the <c>/ByteRange</c> excludes, so the
/// original signature still covers exactly what it did before.
/// <see cref="TheFixtureItself_CarriesACrlBeforeCounterSigning"/> pins that the fixture really is
/// what the test claims; without it a fixture that silently lost the CRL would make the assertion
/// below unfalsifiable.</para>
/// </remarks>
public sealed class CounterSignatureCrlPreservationTests
{
    [Fact]
    public void TheFixtureItself_CarriesACrlBeforeCounterSigning()
    {
        var (signedPdf, _, _, crlIssuer) = SignedDocumentWithEmbeddedCrl();

        var crls = CmsOf(signedPdf).GetCrls().EnumerateMatches(null).ToList();

        Assert.Single(crls);
        Assert.Equal(crlIssuer, crls[0].IssuerDN.ToString());
    }

    [Fact]
    public void CounterSigning_PreservesTheOriginalSignedDatasCrls()
    {
        var (signedPdf, counterPfx, counterPw, crlIssuer) = SignedDocumentWithEmbeddedCrl();

        using var input = new MemoryStream(signedPdf);
        using var output = new MemoryStream();
        PdfCounterSigner.AddCounterSignature(input, output, "Signature1", counterPfx, counterPw);

        var counterSigned = CmsOf(output.ToArray());

        var crls = counterSigned.GetCrls().EnumerateMatches(null).ToList();
        Assert.True(crls.Count == 1,
            $"Counter-signing dropped the embedded CRL ({crls.Count} left): a PAdES B-LT " +
            "signature's long-term validation data was destroyed by adding a counter-signature.");
        Assert.Equal(crlIssuer, crls[0].IssuerDN.ToString());

        // And the counter-signature really was added — otherwise "the CRL survived" would be
        // satisfied by an operation that did nothing at all.
        var counterSigners = counterSigned.GetSignerInfos().GetSigners()
            .SelectMany(s => s.GetCounterSignatures().GetSigners().Cast<SignerInformation>())
            .ToList();
        Assert.NotEmpty(counterSigners);
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// A signed PDF whose CMS carries one CRL, plus the counter-signer's PKCS#12 and the CRL's
    /// issuer DN for the assertions.
    /// </summary>
    private static (byte[] pdf, byte[] counterPfx, string counterPw, string crlIssuer)
        SignedDocumentWithEmbeddedCrl()
    {
        var root = SigningTestCrypto.Ca();
        var signer = SigningTestCrypto.Leaf(root, "CN=Original Signer");
        var counter = SigningTestCrypto.Leaf(root, "CN=Counter Signer");

        var (signerPfx, signerPw) = SigningTestCrypto.Pfx(signer, root.Cert);
        var (counterPfx, counterPw) = SigningTestCrypto.Pfx(counter, root.Cert);

        // A generous reserve: the CRL and the counter-signature both have to fit in the hex region
        // reserved at signing time, and the region cannot be grown afterwards without moving every
        // byte the /ByteRange names.
        byte[] signed;
        using (var input = new MemoryStream(SignedPdfFixtures.MinimalPdf()))
        using (var output = new MemoryStream())
        {
            new PdfSigner().Sign(input, output, new SignatureParameters
            {
                CertificateStream = new MemoryStream(signerPfx),
                CertificatePassword = signerPw,
                SignatureFieldName = "Signature1",
                Reason = "test",
                EstimatedSignatureSize = 65536,
            });
            signed = output.ToArray();
        }

        var crl = IssueEmptyCrl(root);
        CmsSignedData original = CmsOf(signed);
        byte[] withCrl = ReplaceContents(signed, "Signature1",
            CmsSignedData.ReplaceCertificatesAndCrls(
                original,
                original.GetCertificates(),
                CollectionUtilities.CreateStore(new List<X509Crl> { crl }),
                original.GetAttributeCertificates()).GetEncoded());

        return (withCrl, counterPfx, counterPw, crl.IssuerDN.ToString());
    }

    /// <summary>A CRL revoking nothing, signed by the test CA — enough to occupy the field.</summary>
    private static X509Crl IssueEmptyCrl(SigningTestCrypto.Issued ca)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(ca.Cert.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddDays(-1));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(30));
        gen.AddExtension(X509Extensions.CrlNumber, false, new CrlNumber(BigInteger.One));
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", ca.Private));
    }

    /// <summary>
    /// Rewrites the signature's <c>/Contents</c> in place with <paramref name="replacement"/>,
    /// padded back to the reserved length. The region sits in the hole the <c>/ByteRange</c>
    /// excludes, so nothing the original signature digests moves.
    /// </summary>
    private static byte[] ReplaceContents(byte[] pdf, string fieldName, byte[] replacement)
    {
        long holeStart, holeEnd;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
        {
            var field = doc.AcroForm!.FindSignatureField(fieldName);
            Assert.NotNull(field);
            var byteRange = field!.SignatureValue!.GetArray(PdfName.ByteRange);
            Assert.NotNull(byteRange);
            holeStart = byteRange!.GetLong(0) + byteRange.GetLong(1);
            holeEnd = byteRange.GetLong(2);
        }

        int open = IndexOf(pdf, (byte)'<', (int)holeStart, (int)holeEnd);
        int close = IndexOf(pdf, (byte)'>', open + 1, (int)holeEnd);
        Assert.True(open >= 0 && close > open, "the /Contents hex delimiters were not found");

        int reservedHexChars = close - open - 1;
        string hex = BitConverter.ToString(replacement).Replace("-", "").ToLowerInvariant();
        Assert.True(hex.Length <= reservedHexChars,
            $"the rewritten CMS ({replacement.Length} bytes) does not fit in the reserved " +
            $"{reservedHexChars / 2} bytes");

        var result = (byte[])pdf.Clone();
        byte[] padded = Encoding.ASCII.GetBytes(hex.PadRight(reservedHexChars, '0'));
        Array.Copy(padded, 0, result, open + 1, padded.Length);
        return result;
    }

    private static CmsSignedData CmsOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var field = doc.AcroForm!.FindSignatureField("Signature1");
        Assert.NotNull(field);
        var contents = field!.SignatureValue!.GetString(PdfName.Contents);
        Assert.NotNull(contents);
        // Trimmed of the /Contents reserve padding, exactly as the production readers do
        // (SigningDefaults.TrimToAsn1Object). A stand-in that parses the hole rather than
        // the object is not standing in for what ships: BouncyCastle 2.6.2 tolerates the
        // tail and 2.7.0 refuses it, so without this the fixture pins the wrong contract.
        return new CmsSignedData(SigningDefaults.TrimToAsn1Object(contents!.RawBytes));
    }

    private static int IndexOf(byte[] data, byte value, int start, int end)
    {
        for (int i = Math.Max(0, start); i < Math.Min(end, data.Length); i++)
            if (data[i] == value) return i;
        return -1;
    }
}
