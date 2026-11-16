// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Net;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// PAdES B-LTA in one call (GAPS §20b, 2026-10-15): <see cref="PdfSigner.Sign(Stream, Stream, SignatureParameters)"/>
/// with <see cref="SignatureParameters.ApplyPadesBaseline"/> at <see cref="PadesBaseline.B_LTA"/>
/// appends the document timestamp itself — a third revision over the signature and its <c>/DSS</c>,
/// from the same loopback TSA — where the caller had to run <c>PdfDocumentTimestamper</c> as a second
/// pass. B-LT is the control: one TSA request, one signature, no <c>/DocTimeStamp</c>.
/// </summary>
/// <remarks>
/// The test certificates name no OCSP responder or CRL, so the <c>/DSS</c> stage reports its
/// revocation warnings through the <see cref="PdfSigningException"/> the signer has always thrown
/// <i>after</i> writing the file; the document timestamp is appended before that throw, so the file
/// the caller holds is the archived one. The tests read the output through the exception.
/// </remarks>
public sealed class PadesBLtaOneCallTests
{
    private readonly Lazy<(SigningTestCrypto.Issued Root, byte[] Pfx, string Password)> Keys = new(() =>
    {
        var root = SigningTestCrypto.Ca("CN=B-LTA Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=B-LTA Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        return (root, pfx, password);
    });

    [Fact]
    public void BLta_AppendsTheDocumentTimestamp_InTheSameCall()
    {
        var root = Keys.Value.Root;
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);

        byte[] signed = SignAtLevel(PadesBaseline.B_LTA, tsa);
        var results = Verify(signed, root);

        Assert.Equal(2, tsa.Requests);   // the signature's timestamp, then the document's
        var signature = Assert.Single(results, r => r.SubFilter != "ETSI.RFC3161");
        var archive = Assert.Single(results, r => r.SubFilter == "ETSI.RFC3161");
        Assert.True(signature.IsIntegrityValid);
        Assert.True(signature.HasTrustedTimestamp, "Warnings: " + string.Join(" | ", signature.Warnings));
        Assert.True(archive.IsIntegrityValid);
        Assert.True(archive.CoversEntireDocument, "the document timestamp must cover the signature and the /DSS");
        Assert.Equal(signed.Length, archive.ByteRangeCoverageEnd);
        Assert.Equal("DocTimeStamp1", archive.FieldName);
    }

    /// <summary>The control: B-LT stops at the /DSS — one TSA request, one signature field.</summary>
    [Fact]
    public void BLt_AppendsNoDocumentTimestamp()
    {
        var root = Keys.Value.Root;
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);

        byte[] signed = SignAtLevel(PadesBaseline.B_LT, tsa);
        var results = Verify(signed, root);

        Assert.Equal(1, tsa.Requests);
        var only = Assert.Single(results);
        Assert.NotEqual("ETSI.RFC3161", only.SubFilter);
        Assert.True(only.HasTrustedTimestamp);
    }

    /// <summary>The signed bytes are not moved by the third revision: the signature still verifies with the document timestamp appended after it.</summary>
    [Fact]
    public void TheDocumentTimestamp_LeavesTheSignedBytesWhereTheyWere()
    {
        var root = Keys.Value.Root;
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);

        byte[] lta = SignAtLevel(PadesBaseline.B_LTA, tsa);

        var signature = Verify(lta, root).Single(r => r.SubFilter != "ETSI.RFC3161");
        Assert.True(signature.IsIntegrityValid);
        Assert.False(signature.CoversEntireDocument, "two revisions follow the signature");
        Assert.True(signature.ByteRangeCoverageEnd < lta.Length);
    }

    /// <summary>A plan cannot carry the third revision: B-LTA is refused by name, before the <c>EmbedLtv</c> refusal it also implies.</summary>
    [Fact]
    public void Plan_RefusesBLta_ByName()
    {
        var parameters = new SignatureParameters
        {
            CertificateStream = new MemoryStream(Keys.Value.Pfx),
            CertificatePassword = Keys.Value.Password,
        }.ApplyPadesBaseline(PadesBaseline.B_LTA, "http://127.0.0.1:1/tsa");

        using var doc = PdfDocument.Open(new MemoryStream(SignedPdfFixtures.MinimalPdf(), writable: false));
        var ex = Assert.Throws<PdfSigningException>(() => new PdfSigner().Plan(doc, 100, parameters));

        Assert.Contains("B-LTA", ex.Message, StringComparison.Ordinal);
        Assert.Contains("document timestamp", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private byte[] SignAtLevel(PadesBaseline level, LoopbackTsa tsa)
    {
        var parameters = new SignatureParameters
        {
            CertificateStream = new MemoryStream(Keys.Value.Pfx),
            CertificatePassword = Keys.Value.Password,
            // No proxy: the TSA is on the loopback interface, and a system proxy must not see the request.
            TimestampNetwork = new SigningNetworkOptions { Proxy = new WebProxy() },
        }.ApplyPadesBaseline(level, tsa.Url);

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        // The test chain names no responder, so the /DSS stage reports its warnings after the
        // file is written; the output is complete when it throws.
        var ex = Assert.Throws<PdfSigningException>(() => new PdfSigner().Sign(input, output, parameters));
        Assert.Contains("signed successfully", ex.Message, StringComparison.Ordinal);
        return output.ToArray();
    }

    private static System.Collections.Generic.IReadOnlyList<SignatureVerificationResult> Verify(byte[] pdf, SigningTestCrypto.Issued root)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { root.Cert },
        });
    }
}
