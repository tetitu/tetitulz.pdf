// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The LTV refresher gathers evidence for at most <see cref="PdfLimits.MaxSignaturesPerDocument"/>
/// signatures, the verifier's budget, and says when it stops. Each signature is a CMS parse and a round
/// of network fetches, and a file chooses how many signature dictionaries it holds.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class LtvRefreshSignatureBudgetTests
{
    [Fact]
    public void AdversarialSignaturesPastTheBudget_AreNotRefreshed_AndSaid()
    {
        byte[] pdf = Signatures(3, SignatureContentsOf(SignedDocument()));
        int calls = 0;
        LtvRefreshResult result;
        try
        {
            PdfLimits.MaxSignaturesPerDocument = 2;
            using var output = new MemoryStream();
            result = PdfLtvRefresher.RefreshVerificationInfo(new MemoryStream(pdf, writable: false), output,
                new LtvRefreshOptions { EvidenceProvider = (_, __) => { calls++; return null; }, IncludeSignerChains = false });
        }
        finally { PdfLimits.ResetToDefaults(); }

        Assert.Equal(2, calls);
        Assert.Contains(result.Warnings, w => w.Contains("more than 2 signatures", StringComparison.Ordinal));
    }

    /// <summary>The control: under the budget every signature is refreshed and nothing is said of it.</summary>
    [Fact]
    public void SignaturesWithinTheBudget_AreAllRefreshed()
    {
        byte[] pdf = Signatures(3, SignatureContentsOf(SignedDocument()));
        int calls = 0;
        using var output = new MemoryStream();
        LtvRefreshResult result = PdfLtvRefresher.RefreshVerificationInfo(new MemoryStream(pdf, writable: false), output,
            new LtvRefreshOptions { EvidenceProvider = (_, __) => { calls++; return null; }, IncludeSignerChains = false });

        Assert.Equal(3, calls);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("MaxSignaturesPerDocument", StringComparison.Ordinal));
    }

    /// <summary>
    /// Signatures repeating one CMS are fetched for once on the network path: the evidence would be the same, and
    /// each fetch round is a walk of network requests the file can repeat as many times as it holds signatures. The
    /// walk's own "nothing to fetch" warning — the signer names no CRL distribution point — is the evidence: one,
    /// not three. A caller's provider is still asked once a signature (the tests above).
    /// </summary>
    [Fact]
    public void SignaturesRepeatingOneCms_AreFetchedForOnce()
    {
        byte[] pdf = Signatures(3, SignatureContentsOf(SignedDocument()));
        using var output = new MemoryStream();
        LtvRefreshResult result = PdfLtvRefresher.RefreshVerificationInfo(new MemoryStream(pdf, writable: false), output,
            new LtvRefreshOptions { IncludeSignerChains = false });

        Assert.Single(result.Warnings, w => w.Contains("No CRL distribution-point URL found", StringComparison.Ordinal));
    }

    private static byte[] SignedDocument()
    {
        var root = SigningTestCrypto.Ca();
        var signer = SigningTestCrypto.Leaf(root, "CN=Ltv Budget Signer");
        var (pfx, pw) = SigningTestCrypto.Pfx(signer, root.Cert);

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = pw,
            SignatureFieldName = "Signature1",
            EstimatedSignatureSize = 65536,
        });
        return output.ToArray();
    }

    private static byte[] SignatureContentsOf(byte[] signed)
    {
        using var doc = PdfDocument.Open(new MemoryStream(signed, writable: false));
        foreach (var field in doc.AcroForm.GetSignatureFields())
            if (field.SignatureValue is { } sig && doc.ResolveObject(sig[PdfName.Contents]) is PdfString { RawBytes.Length: > 0 } contents)
                return contents.RawBytes;
        throw new InvalidOperationException("the fixture document carries no signature");
    }

    /// <summary>An AcroForm of <paramref name="count"/> signature fields, each its own signature dictionary over one CMS.</summary>
    private static byte[] Signatures(int count, byte[] cms)
    {
        string hex = string.Concat(cms.Select(b => b.ToString("X2")));
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(sb.Length); sb.Append(s); }
        string fields = string.Join(" ", Enumerable.Range(0, count).Select(i => (4 + 2 * i) + " 0 R"));
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [" + fields + "] >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        for (int i = 0; i < count; i++)
        {
            int field = 4 + 2 * i;
            Obj($"{field} 0 obj\n<< /FT /Sig /T (Sig{i}) /V {field + 1} 0 R >>\nendobj\n");
            Obj($"{field + 1} 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached /Contents <{hex}> >>\nendobj\n");
        }
        int xref = sb.Length;
        int size = offsets.Count + 1;
        sb.Append("xref\n0 ").Append(size).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(size).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
