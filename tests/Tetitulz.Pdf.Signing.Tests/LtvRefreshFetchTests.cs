// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfLtvRefresher.RefreshVerificationInfo"/> — the convenience half that reads a
/// document's own signatures and gathers evidence for them.
/// </summary>
/// <remarks>
/// <para><b>Every test here supplies its own evidence provider, and that is the point of the
/// seam.</b> The default provider reaches an OCSP responder over the network, which a test cannot
/// assert against and must not depend on. Substituting it leaves everything that is actually this
/// method's job under test: which certificate each signature contributes, what reaches the
/// <c>/DSS</c>, and what happens when a signer yields nothing.</para>
///
/// <para>The one thing these deliberately do <b>not</b> cover is the network fetch itself. That is
/// <c>DssBuilder</c>'s, and it has its own tests.</para>
/// </remarks>
public class LtvRefreshFetchTests
{
    private static readonly byte[] FakeOcsp = { 0x30, 0x03, 0x01, 0x01, 0x00 };
    private static readonly byte[] FakeCrl = { 0x30, 0x03, 0x02, 0x01, 0x07 };

    /// <summary>Evidence a provider returns reaches the appended <c>/DSS</c>.</summary>
    [Fact]
    public void ProvidedEvidenceReachesTheDss()
    {
        byte[] signed = SignedDocument();

        using var input = new MemoryStream(signed, writable: false);
        using var output = new MemoryStream();

        LtvRefreshResult result = PdfLtvRefresher.RefreshVerificationInfo(input, output,
            new LtvRefreshOptions
            {
                EvidenceProvider = (_, __) => new LtvEvidence(new[] { FakeOcsp }, new[] { FakeCrl }, null),
            });

        Assert.Equal(1, result.OcspAdded);
        Assert.Equal(1, result.CrlsAdded);

        // ...and the bytes are really in the file, not merely counted.
        var (ocsp, crls, _) = DssContents(output.ToArray());
        Assert.Contains(ocsp, b => b.SequenceEqual(FakeOcsp));
        Assert.Contains(crls, b => b.SequenceEqual(FakeCrl));
    }

    /// <summary>
    /// The provider is handed <b>that signature's own</b> certificate, which is the whole reason
    /// this method exists rather than the caller passing evidence blindly.
    /// </summary>
    [Fact]
    public void TheProviderSeesTheSignersOwnCertificate()
    {
        byte[] signed = SignedDocument(signerName: "CN=Refresh Signer");
        var seen = new List<string>();

        using var input = new MemoryStream(signed, writable: false);
        using var output = new MemoryStream();

        PdfLtvRefresher.RefreshVerificationInfo(input, output, new LtvRefreshOptions
        {
            EvidenceProvider = (cert, chain) =>
            {
                seen.Add(cert.SubjectDN.ToString());
                Assert.NotEmpty(chain);          // the CMS carried a chain, not a bare leaf
                return new LtvEvidence(new[] { FakeOcsp }, null, null);
            },
        });

        string only = Assert.Single(seen);
        Assert.Contains("Refresh Signer", only);
    }

    /// <summary>
    /// A provider that yields nothing is not a failure - but the result must say so rather than
    /// reading as a successful refresh.
    /// </summary>
    /// <remarks>
    /// The dangerous outcome here is the quiet one: a document whose responders were all
    /// unreachable gains no evidence, and a caller that checks only "did it throw" archives a file
    /// that is no better protected than before.
    /// </remarks>
    [Fact]
    public void AProviderThatYieldsNothingAddsNoEvidence()
    {
        byte[] signed = SignedDocument();

        using var input = new MemoryStream(signed, writable: false);
        using var output = new MemoryStream();

        LtvRefreshResult result = PdfLtvRefresher.RefreshVerificationInfo(input, output,
            new LtvRefreshOptions
            {
                EvidenceProvider = (_, __) => null,
                IncludeSignerChains = false,
            });

        Assert.Equal(0, result.OcspAdded);
        Assert.Equal(0, result.CrlsAdded);
        Assert.Equal(0, result.CertificatesAdded);
    }

    /// <summary>
    /// The signer's chain is added by default, because a verifier that cannot reach the issuing CA
    /// needs it in the document - and is left out when asked.
    /// </summary>
    [Fact]
    public void TheSignerChainIsIncludedByDefaultAndOmittedOnRequest()
    {
        byte[] signed = SignedDocument();

        int withChain = CertificatesAdded(signed, includeChains: true);
        int without = CertificatesAdded(signed, includeChains: false);

        Assert.True(withChain > 0, "the signer's own chain must reach /DSS /Certs by default");
        Assert.Equal(0, without);
    }

    /// <summary>
    /// A document with no signature is refused. An empty <c>/DSS</c> appended to an unsigned
    /// document is evidence about nothing.
    /// </summary>
    [Fact]
    public void AnUnsignedDocumentIsRefused()
    {
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf(), writable: false);
        using var output = new MemoryStream();

        var ex = Assert.Throws<PdfSigningException>(
            () => PdfLtvRefresher.RefreshVerificationInfo(input, output));

        Assert.Contains("no signature", ex.Message);
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The existing store is carried forward, which is the rule the whole class exists to protect.
    /// </summary>
    /// <remarks>
    /// This path builds its evidence differently from <c>AddVerificationInfo</c>'s callers, so it
    /// gets its own assertion: a refresh that replaced the store instead of merging would destroy
    /// the evidence an earlier B-LT signature depends on, and a lost array is an absence that no
    /// assertion about the new evidence would notice.
    /// </remarks>
    [Fact]
    public void AnExistingStoreSurvivesTheRefresh()
    {
        byte[] signed = SignedDocument();

        // First refresh puts one OCSP in; the second must not remove it.
        byte[] once = Refresh(signed, FakeOcsp);
        byte[] twice = Refresh(once, new byte[] { 0x30, 0x03, 0x01, 0x01, 0x01 });

        var (ocsp, _, _) = DssContents(twice);
        Assert.Contains(ocsp, b => b.SequenceEqual(FakeOcsp));
        Assert.True(ocsp.Count >= 2, $"expected both responses to survive, found {ocsp.Count}");
    }

    /// <summary>
    /// Two field nodes naming the <b>same</b> signature dictionary are one signature, so the
    /// provider is called once and its evidence appended once.
    /// </summary>
    /// <remarks>
    /// <para><b>The ordinary fixture cannot reach this.</b> A signed document's field and widget are
    /// merged into one dictionary, so exactly one node carries its own <c>/V</c> and the
    /// deduplication is inert — a mutation deleting it reddened nothing until this test existed.
    /// The hazard is the one <c>SignatureFieldWalk.DistinctSignatures</c> is documented against:
    /// <c>GetSignatureFields</c> is deliberately permissive, and two nodes can name one signature.</para>
    ///
    /// <para>Without the dedupe the same evidence is gathered and written twice, which inflates the
    /// <c>/DSS</c> on every refresh cycle — the exact failure a long-term-validation store cannot
    /// afford, because each cycle compounds the last.</para>
    /// </remarks>
    [Fact]
    public void TwoFieldsNamingOneSignatureAreRefreshedOnce()
    {
        byte[] cms = SignatureContentsOf(SignedDocument());
        int calls = 0;

        using var input = new MemoryStream(TwoFieldsOneSignature(cms), writable: false);
        using var output = new MemoryStream();

        LtvRefreshResult result = PdfLtvRefresher.RefreshVerificationInfo(input, output,
            new LtvRefreshOptions
            {
                EvidenceProvider = (_, __) => { calls++; return new LtvEvidence(new[] { FakeOcsp }, null, null); },
                IncludeSignerChains = false,
            });

        Assert.Equal(1, calls);
        Assert.Equal(1, result.OcspAdded);
    }

    /// <summary>
    /// A signature field's widget kid inherits <c>/FT /Sig</c>, so the field walk hands it back as a
    /// signature field too — with no <c>/V</c> of its own. It is not a second signature, and reading
    /// the <c>/Contents</c> of a value it does not have is not a reason to fail the refresh.
    /// </summary>
    [Fact]
    public void AWidgetKidWithNoValueOfItsOwnIsNotASignature()
    {
        byte[] cms = SignatureContentsOf(SignedDocument());
        int calls = 0;

        using var input = new MemoryStream(TwoFieldsOneSignature(cms, widgetKidWithoutValue: true), writable: false);
        using var output = new MemoryStream();

        PdfLtvRefresher.RefreshVerificationInfo(input, output,
            new LtvRefreshOptions
            {
                EvidenceProvider = (_, __) => { calls++; return new LtvEvidence(new[] { FakeOcsp }, null, null); },
                IncludeSignerChains = false,
            });

        Assert.Equal(1, calls);
    }

    // ------------------------------------------------------------------

    /// <summary>The raw CMS bytes out of a signed document's first signature.</summary>
    private static byte[] SignatureContentsOf(byte[] signed)
    {
        using var ms = new MemoryStream(signed, writable: false);
        using var doc = PdfDocument.Open(ms);

        foreach (var field in doc.AcroForm.GetSignatureFields())
        {
            var sig = field.SignatureValue;
            if (sig == null) continue;
            var contents = doc.ResolveObject(sig[PdfName.Contents]) as PdfString;
            if (contents?.RawBytes is { Length: > 0 } bytes) return bytes;
        }
        throw new InvalidOperationException("the fixture document carries no signature");
    }

    /// <summary>
    /// An AcroForm whose two signature fields both name object 6 — one signature, two nodes.
    /// </summary>
    /// <param name="cms">The signature's CMS.</param>
    /// <param name="widgetKidWithoutValue">
    /// Gives <c>SigA</c> a widget kid carrying no <c>/T</c> and no <c>/V</c>: it inherits
    /// <c>/FT /Sig</c>, so the field walk returns it as a signature field with no value.
    /// </param>
    private static byte[] TwoFieldsOneSignature(byte[] cms, bool widgetKidWithoutValue = false)
    {
        var hex = new System.Text.StringBuilder(cms.Length * 2);
        foreach (byte b in cms) hex.Append(b.ToString("X2"));

        var body = new System.Text.StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(body.Length); body.Append(s); }

        body.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R 5 0 R] >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        Obj("4 0 obj\n<< /FT /Sig /T (SigA) /V 6 0 R" + (widgetKidWithoutValue ? " /Kids [7 0 R]" : "") + " >>\nendobj\n");
        Obj("5 0 obj\n<< /FT /Sig /T (SigB) /V 6 0 R >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Contents <"
            + hex + "> >>\nendobj\n");
        if (widgetKidWithoutValue)
            Obj("7 0 obj\n<< /Type /Annot /Subtype /Widget /Rect [0 0 0 0] /Parent 4 0 R >>\nendobj\n");

        int xref = body.Length;
        int size = offsets.Count + 1;
        body.Append("xref\n0 ").Append(size).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in offsets) body.Append(o.ToString("D10")).Append(" 00000 n \n");
        body.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n")
            .Append(xref).Append("\n%%EOF\n");

        return System.Text.Encoding.ASCII.GetBytes(body.ToString());
    }

    private static byte[] Refresh(byte[] pdf, byte[] ocspBytes)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        PdfLtvRefresher.RefreshVerificationInfo(input, output, new LtvRefreshOptions
        {
            EvidenceProvider = (_, __) => new LtvEvidence(new[] { ocspBytes }, null, null),
            IncludeSignerChains = false,
        });
        return output.ToArray();
    }

    private static int CertificatesAdded(byte[] pdf, bool includeChains)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        return PdfLtvRefresher.RefreshVerificationInfo(input, output, new LtvRefreshOptions
        {
            EvidenceProvider = (_, __) => null,
            IncludeSignerChains = includeChains,
        }).CertificatesAdded;
    }

    /// <summary>The decoded streams in the output's newest <c>/DSS</c>.</summary>
    private static (List<byte[]> Ocsp, List<byte[]> Crls, List<byte[]> Certs) DssContents(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        using var doc = PdfDocument.Open(ms);

        var catalog = doc.Catalog.Dictionary;
        var dss = doc.ResolveObject(catalog[new PdfName("DSS")]) as PdfDictionary;
        Assert.True(dss != null, "the refreshed document carries no /DSS");

        return (Read(doc, dss, "OCSPs"), Read(doc, dss, "CRLs"), Read(doc, dss, "Certs"));
    }

    private static List<byte[]> Read(PdfDocument doc, PdfDictionary dss, string key)
    {
        var result = new List<byte[]>();
        if (doc.ResolveObject(dss[new PdfName(key)]) is not PdfArray arr) return result;
        foreach (var item in arr)
            if (doc.ResolveObject(item) is PdfStream s)
                result.Add(s.GetDecodedData());
        return result;
    }

    private static byte[] SignedDocument(string signerName = "CN=Ltv Signer")
    {
        var root = SigningTestCrypto.Ca();
        var signer = SigningTestCrypto.Leaf(root, signerName);
        var (pfx, pw) = SigningTestCrypto.Pfx(signer, root.Cert);

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();

        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = "Signature1",
            Reason = "ltv",
            EstimatedSignatureSize = 65536,
        });
        return output.ToArray();
    }
}
