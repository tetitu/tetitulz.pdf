// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A signature's <c>/VRI</c> entry is read beside the document-wide <c>/OCSPs</c> and <c>/CRLs</c>, never
/// instead of them: evidence is matched to its certificate already, so a list kept for another signature
/// cannot vouch for this one, and an entry a later revision adds — empty — hid a listing the document-wide
/// CRL held, turning a revoked signer into one nothing was checked for.
/// </summary>
public sealed class VriBesideDocumentWideListsTests
{
    private static readonly SecureRandom Random = new();

    private static byte[] Crl(SigningTestCrypto.Issued ca, BigInteger revokedSerial)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(ca.Cert.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddHours(-1));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(7));
        gen.AddExtension(X509Extensions.CrlNumber, false, new CrlNumber(BigInteger.ValueOf(Random.Next())));
        gen.AddCrlEntry(revokedSerial, DateTime.UtcNow.AddHours(-2), CrlReason.KeyCompromise);
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", ca.Private, Random)).GetEncoded();
    }

    private static PdfDocument Open(string dss, byte[] crl)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            dss,
            "<< /Length " + crl.Length + " >>\nstream\n" + Encoding.Latin1.GetString(crl) + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())));
    }

    [Fact]
    public void AdversarialEmptyVriEntry_DoesNotHideADocumentWideListing()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] contents = { 0x30, 0x03, 0x02, 0x01, 0x07 };   // stands for the signature's trimmed /Contents
        string key = Convert.ToHexString(SHA1.HashData(contents));
        byte[] crl = Crl(ca, leaf.Cert.SerialNumber);

        using var withEntry = Open($"<< /CRLs [5 0 R] /VRI << /{key} << >> >> >>", crl);
        var warnings = new List<string>();
        Assert.Equal(PdfRevocationStatus.Revoked, RevocationChecker.Check(withEntry, leaf.Cert, ca.Cert, warnings, null, null, contents));
        Assert.Contains(warnings, w => w.Contains("REVOKED", StringComparison.Ordinal));

        // The entry's own list still counts, alone.
        using var inEntry = Open($"<< /VRI << /{key} << /CRL [5 0 R] >> >> >>", crl);
        Assert.Equal(PdfRevocationStatus.Revoked, RevocationChecker.Check(inEntry, leaf.Cert, ca.Cert, new List<string>(), null, null, contents));

        // Control: an entry for another signature, and no document-wide list, says nothing about this one.
        using var otherEntry = Open("<< /VRI << /0000000000000000000000000000000000000000 << /CRL [5 0 R] >> >> >>", crl);
        Assert.Equal(PdfRevocationStatus.NotChecked, RevocationChecker.Check(otherEntry, leaf.Cert, ca.Cert, new List<string>(), null, null, contents));
    }
}
