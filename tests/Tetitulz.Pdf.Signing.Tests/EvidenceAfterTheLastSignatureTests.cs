// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// Bytes after the last signature's <c>/ByteRange</c> are the shadow-attack error — unless the
/// revisions holding them add nothing but evidence about the signatures: the <c>/DSS</c> a B-LT
/// signing or an LTV refresh appends after the signature, or a blockchain anchoring's proof in the
/// catalog. Those are said as a warning and leave the signature valid; anything else in the tail
/// keeps the error.
/// </summary>
public class EvidenceAfterTheLastSignatureTests
{
    private readonly SigningTestCrypto.Issued Root = SigningTestCrypto.Ca("CN=Evidence Tail Root");

    private byte[] Signed()
    {
        var leaf = SigningTestCrypto.Leaf(Root, "CN=Evidence Tail Leaf");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, Root.Cert);
        return SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, password);
    }

    private SignatureVerificationResult Verify(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);
        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { Root.Cert },
        });
        return Assert.Single(results);
    }

    [Fact]
    public void AnLtvRefreshAppendedAfterTheSignature_LeavesItValid_AndIsSaidAsAWarning()
    {
        byte[] signed = Signed();
        using var input = new MemoryStream(signed, writable: false);
        using var output = new MemoryStream();
        PdfLtvRefresher.AddVerificationInfo(input, output,
            new LtvEvidence(crls: new[] { Encoding.ASCII.GetBytes("crl-bytes") }));

        var result = Verify(output.ToArray());

        Assert.True(result.IsValid, string.Join(" | ", result.ValidationErrors));
        Assert.Contains(result.Warnings, w => w.Contains("hold only revocation evidence"));
        Assert.DoesNotContain(result.ValidationErrors, e => e.Contains("appended after the last signature"));
    }

    /// <summary>
    /// The catalog superseded after the signature, as the Ethereum plugin supersedes it: a
    /// <c>/BlockchainProof</c> in the proof's shape is evidence; any other new entry is the error.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACatalogEntryAppendedAfterTheSignature_IsEvidenceOnlyWhenItIsAProof(bool proof)
    {
        byte[] appended = AppendCatalogEntry(Signed(),
            proof ? PdfName.BlockchainProof : new PdfName("ReviewProbe"),
            proof ? ProofDictionary() : new PdfString("x"));

        var result = Verify(appended);

        Assert.Equal(proof, result.IsValid);
        if (proof)
            Assert.Contains(result.Warnings, w => w.Contains("anchoring proof") && w.Contains("/BlockchainProof added"));
        else
            Assert.Contains(result.ValidationErrors, e => e.Contains("appended after the last signature's ByteRange are unsigned"));
    }

    private static PdfDictionary ProofDictionary()
    {
        var proof = new PdfDictionary();
        proof.Set(PdfName.TxHash, new PdfString("0x1"));
        proof.Set(PdfName.DocumentHash, new PdfString("00"));
        return proof;
    }

    private static byte[] AppendCatalogEntry(byte[] pdf, PdfName key, PdfObject value)
    {
        using var source = new MemoryStream(pdf, writable: false);
        var parser = new PdfParser(source);
        parser.ParseHeader();
        parser.ParseCrossReferences();
        var trailer = parser.Trailer;
        var rootRef = Assert.IsType<PdfReference>(trailer[PdfName.Root]);
        var root = Assert.IsAssignableFrom<PdfDictionary>(parser.ResolveObject(rootRef));

        var catalog = new PdfDictionary();
        foreach (var kv in root) catalog.Set(kv.Key, kv.Value);
        catalog.Set(key, value);

        using var output = new MemoryStream();
        IncrementalUpdateWriter.WriteUpdate(pdf, output,
            new[] { new IndirectObject(rootRef.ReferenceObjectNumber, rootRef.ReferenceGenerationNumber, catalog) },
            new IncrementalUpdateOptions
            {
                OriginalTrailer = trailer,
                Size = parser.CrossReferenceTable.GetMaxObjectNumber() + 1,
            });
        return output.ToArray();
    }
}
