// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A <c>/DSS</c> list is read once a stream, under the LTV embedder's 20 MB ceiling, and a revocation
/// stream is parsed once a document. Each certificate of each signature's chain re-read the list,
/// decoding every listed entry under the 512 MB image ceiling and parsing and verifying each — so one
/// CRL listed five thousand times was five thousand parses and signature checks a certificate.
/// </summary>
public sealed class DssStreamsReadOnceTests
{
    private static readonly SecureRandom Random = new();

    [Fact]
    public void AdversarialCrlListedThousandsOfTimes_IsReadAndParsedOnce()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        string refs = string.Concat(Enumerable.Repeat("5 0 R ", 5000));
        using var doc = Document("[" + refs + "]", CurrentCrl(ca), flate: false);

        RevocationChecker.StreamsRead = 0;
        RevocationChecker.ParsesMade = 0;
        var warnings = new List<string>();
        PdfRevocationStatus first = RevocationChecker.Check(doc, leaf.Cert, ca.Cert, warnings);
        PdfRevocationStatus again = RevocationChecker.Check(doc, leaf.Cert, ca.Cert, warnings);

        Assert.Equal(PdfRevocationStatus.Good, first);
        Assert.Equal(PdfRevocationStatus.Good, again);
        Assert.Equal(1L, RevocationChecker.StreamsRead);   // once a document, not once a listing or a check
        Assert.Equal(1L, RevocationChecker.ParsesMade);    // once a document
    }

    /// <summary>
    /// The inventory's /VRI entries naming one list read each stream of it once for the document — each entry
    /// decoded every stream again once the parser's memo was full, about 1.8 s an entry for twenty 19 MB streams.
    /// </summary>
    [Fact]
    public void AdversarialVriEntriesNamingOneList_DecodeEachStreamOncePerDocument()
    {
        var ca = SigningTestCrypto.Ca();
        const int entries = 40;
        var vri = new StringBuilder("<<");
        for (int i = 0; i < entries; i++) vri.Append(" /K").Append(i.ToString("X40")).Append(" << /CRL 5 0 R >>");
        vri.Append(" >>");
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /CRLs 5 0 R /VRI " + vri + " >>",
            "[6 0 R 7 0 R 8 0 R]",
        };
        for (int i = 0; i < 3; i++)
        {
            byte[] crl = CurrentCrl(ca);
            bodies.Add("<< /Length " + crl.Length + " >>\nstream\n" + Encoding.Latin1.GetString(crl) + "\nendstream");
        }
        using var doc = Open(bodies);

        RevocationChecker.StreamsRead = 0;
        DssInventoryReader.Read(doc, DateTime.UtcNow);

        Assert.Equal(3L, RevocationChecker.StreamsRead);
    }

    /// <summary>
    /// What one document's revocation streams decode to together is bounded: past the budget a stream is not read,
    /// and the check says so. The control is the same document under the default budget, whose CRL vouches.
    /// </summary>
    [Fact]
    public void RevocationStreamsPastTheDocumentBudget_AreNotRead_AndSaid()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        byte[] crl = CurrentCrl(ca);

        using (var starved = Document("[5 0 R]", crl, flate: true))
        {
            RevocationChecker.LimitReads(starved, crl.Length - 1);
            var warnings = new List<string>();
            Assert.Equal(PdfRevocationStatus.NotChecked, RevocationChecker.Check(starved, leaf.Cert, ca.Cert, warnings));
            Assert.Contains(warnings, w => w.Contains("could not be read", StringComparison.Ordinal));
        }

        using var fed = Document("[5 0 R]", crl, flate: true);
        Assert.Equal(PdfRevocationStatus.Good, RevocationChecker.Check(fed, leaf.Cert, ca.Cert, new List<string>()));
    }

    /// <summary>The control: a listed CRL that lists the certificate still revokes it.</summary>
    [Fact]
    public void AListedCrlNamingTheCertificate_StillRevokesIt()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        using var doc = Document("[5 0 R 5 0 R]", CurrentCrl(ca, leaf.Cert.SerialNumber), flate: false);

        Assert.Equal(PdfRevocationStatus.Revoked, RevocationChecker.Check(doc, leaf.Cert, ca.Cert, new List<string>()));
    }

    /// <summary>A stream inflating past the embedder's 20 MB is not read, and the check says so.</summary>
    [Fact]
    public void AdversarialStreamInflatingPastTheCeiling_IsNotRead_AndSaid()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        using var doc = Document("[5 0 R]", new byte[25 * 1024 * 1024], flate: true);

        var warnings = new List<string>();
        PdfRevocationStatus status = RevocationChecker.Check(doc, leaf.Cert, ca.Cert, warnings);

        Assert.Equal(PdfRevocationStatus.NotChecked, status);
        Assert.Contains(warnings, w => w.Contains("could not be read", StringComparison.Ordinal));
    }

    /// <summary>
    /// The inventory reads a /VRI of two thousand entries naming one CRL with one parse — it parsed the
    /// CRL once an entry — and still reports it on each.
    /// </summary>
    [Fact]
    public void AdversarialVriEntriesNamingOneCrl_AreInventoriedWithOneParse()
    {
        var ca = SigningTestCrypto.Ca();
        const int entries = 2000;
        var vri = new StringBuilder("<<");
        for (int i = 0; i < entries; i++) vri.Append(" /K").Append(i.ToString("X40")).Append(" << /CRL [5 0 R] >>");
        vri.Append(" >>");
        byte[] crl = CurrentCrl(ca);
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /CRLs [5 0 R] /VRI " + vri + " >>",
            "<< /Length " + crl.Length + " >>\nstream\n" + Encoding.Latin1.GetString(crl) + "\nendstream",
        };
        using var doc = Open(bodies);

        RevocationChecker.ParsesMade = 0;
        var inventory = DssInventoryReader.Read(doc, DateTime.UtcNow);

        Assert.Equal(1L, RevocationChecker.ParsesMade);
        Assert.Equal(entries, inventory.VriEvidence.Count);
        Assert.All(inventory.VriEvidence, e => Assert.True(Assert.Single(e.Crls).IsCurrent));
    }

    /// <summary>A list naming more distinct streams than the cap is read to the cap, and the check says so.</summary>
    [Fact]
    public void AdversarialListOfManyDistinctStreams_IsReadToTheCap_AndSaid()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        int streams = RevocationChecker.MaxStreamsPerList + 5;
        using var doc = Document(streams, _ => Encoding.ASCII.GetBytes("not a CRL"));

        RevocationChecker.StreamsRead = 0;
        var warnings = new List<string>();
        RevocationChecker.Check(doc, leaf.Cert, ca.Cert, warnings);

        Assert.Equal(RevocationChecker.MaxStreamsPerList, RevocationChecker.StreamsRead);
        Assert.Contains(warnings, w => w.Contains($"names more than {RevocationChecker.MaxStreamsPerList} streams", StringComparison.Ordinal));
    }

    /// <summary>Entries failing alike are one warning, counted, not a line each.</summary>
    [Fact]
    public void EntriesFailingAlike_AreOneCountedWarning()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var forger = SigningTestCrypto.Ca("CN=Forger");
        // Three distinct CRLs naming the right issuer, signed by the wrong key.
        using var doc = Document(3, _ => ForgedCrl(ca, forger));

        var warnings = new List<string>();
        RevocationChecker.Check(doc, leaf.Cert, ca.Cert, warnings);

        Assert.Single(warnings, w => w.Contains("CRL signature verification failed", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.EndsWith("(3 entries)", StringComparison.Ordinal));
    }

    private static byte[] ForgedCrl(SigningTestCrypto.Issued named, SigningTestCrypto.Issued signer)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(named.Cert.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddHours(-1));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(7));
        gen.AddExtension(X509Extensions.CrlNumber, false, new CrlNumber(BigInteger.ValueOf(Random.Next())));
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", signer.Private, Random)).GetEncoded();
    }

    // A /DSS whose /CRLs names `count` distinct streams, objects 5 onward, each `content(i)`.
    private static PdfDocument Document(int count, Func<int, byte[]> content)
    {
        var refs = string.Join(" ", Enumerable.Range(5, count).Select(n => n + " 0 R"));
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /CRLs [" + refs + "] >>",
        };
        for (int i = 0; i < count; i++)
        {
            byte[] data = content(i);
            bodies.Add("<< /Length " + data.Length + " >>\nstream\n" + Encoding.Latin1.GetString(data) + "\nendstream");
        }
        return Open(bodies);
    }

    private static byte[] CurrentCrl(SigningTestCrypto.Issued ca, BigInteger? revoked = null)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(ca.Cert.SubjectDN);
        gen.SetThisUpdate(DateTime.UtcNow.AddHours(-1));
        gen.SetNextUpdate(DateTime.UtcNow.AddDays(7));
        gen.AddExtension(X509Extensions.CrlNumber, false, new CrlNumber(BigInteger.One));
        if (revoked != null) gen.AddCrlEntry(revoked, DateTime.UtcNow.AddHours(-2), CrlReason.KeyCompromise);
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", ca.Private, Random)).GetEncoded();
    }

    // Catalog 1, pages 2, page 3, the /DSS 4 whose /CRLs is `crls`, the stream 5.
    private static PdfDocument Document(string crls, byte[] stream, bool flate)
    {
        byte[] data = flate ? FlateEncodeFilter.Encode(stream) : stream;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /CRLs " + crls + " >>",
            "<< /Length " + data.Length + (flate ? " /Filter /FlateDecode" : "") + " >>\nstream\n" +
            Encoding.Latin1.GetString(data) + "\nendstream",
        };
        return Open(bodies);
    }

    private static PdfDocument Open(List<string> bodies)
    {
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
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())));
    }
}
