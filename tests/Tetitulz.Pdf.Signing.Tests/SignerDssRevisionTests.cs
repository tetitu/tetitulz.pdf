// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The <c>/DSS</c> revision <see cref="PdfSigner"/> appends for <c>EmbedLtv</c> keeps the long-term
/// validation data a document already carries, and carries the trailer forward.
/// </summary>
/// <remarks>
/// <para><b>What this closes.</b> The signer wrote its own <c>/DSS</c> revision while
/// <see cref="PdfLtvRefresher"/> wrote the other, and they disagreed. The signer's held only the new
/// signer's responses, so a counter-signature with <c>EmbedLtv</c> unreferenced every OCSP response,
/// CRL and <c>/VRI</c> entry the first signature relied on; and its trailer named four keys, so
/// <c>/Info</c> left the newest revision (ISO 32000-2 §7.5.6: "the added trailer shall contain all
/// the entries except the Prev entry … from the previous trailer"). Both are absences, and the path
/// needs a live OCSP responder, so nothing noticed.</para>
///
/// <para><b>Why <c>EmbedDss</c> is called directly.</b> <c>Sign</c> reaches it only after fetching
/// revocation data over the network. It hands the fetched evidence to this same method, which is the
/// whole of what happens after the fetch.</para>
/// </remarks>
public sealed class SignerDssRevisionTests
{
    [Fact]
    public void EvidenceTheDocumentAlreadyCarriedIsStillReferenced()
    {
        byte[] withLtv = SignThenEmbed(ocsp: "ocsp-NEW");

        Assert.Equal(new[] { "crl-OLD" }, DssStreams(withLtv, "CRLs"));
        Assert.Equal(new[] { "ocsp-NEW" }, DssStreams(withLtv, "OCSPs"));
    }

    [Fact]
    public void ASignaturesVriEntrySurvives()
    {
        byte[] withLtv = SignThenEmbed(ocsp: "ocsp-NEW");

        using var doc = PdfDocument.Open(new MemoryStream(withLtv, writable: false));
        var dss = (PdfDictionary)doc.ResolveObject(doc.Catalog.Dictionary[PdfName.DSS])!;
        var vri = doc.ResolveObject(dss[new PdfName("VRI")]) as PdfDictionary;
        var entry = doc.ResolveObject(vri?[new PdfName("AABBCC")]) as PdfDictionary;
        var crls = doc.ResolveObject(entry?[new PdfName("CRL")]) as PdfArray;

        // The entry still names the signature's own CRL, by content — not merely a key that exists.
        var crl = doc.ResolveObject(Assert.Single(crls!)) as PdfStream;
        Assert.Equal("crl-OLD", Encoding.ASCII.GetString(crl!.RawData!));
    }

    /// <summary>
    /// Read from the raw bytes of the last trailer: <see cref="PdfDocument.Trailer"/> merges the
    /// revision chain and answers from an older trailer when the newest one dropped a key.
    /// </summary>
    [Fact]
    public void TheAppendedTrailerKeepsEveryKey()
    {
        byte[] withLtv = SignThenEmbed(ocsp: "ocsp-NEW");

        string text = Encoding.ASCII.GetString(withLtv);
        string lastTrailer = text.Substring(text.LastIndexOf("trailer", StringComparison.Ordinal));

        Assert.Contains("/Info", lastTrailer, StringComparison.Ordinal);
        Assert.Contains("/ID", lastTrailer, StringComparison.Ordinal);
        Assert.Contains("/TetitulzProducerKey (kept)", lastTrailer, StringComparison.Ordinal);
    }

    /// <summary>
    /// The revision is appended, not written over the signature: the signature still verifies over
    /// the bytes it signed, and the document still has its pages and its form.
    /// </summary>
    [Fact]
    public void TheSignatureAndTheDocumentSurviveTheRevision()
    {
        byte[] withLtv = SignThenEmbed(ocsp: "ocsp-NEW");

        using var doc = PdfDocument.Open(new MemoryStream(withLtv, writable: false));
        Assert.Single(doc.Pages);

        var result = new PdfSignatureVerifier().VerifyAll(doc).Single();
        Assert.True(result.IsIntegrityValid);
        Assert.NotEmpty(DssStreams(withLtv, "OCSPs"));
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] SignThenEmbed(string ocsp)
    {
        var ca = SigningTestCrypto.Ca("CN=DSS Revision Root");
        var leaf = SigningTestCrypto.Leaf(ca, "CN=DSS Revision Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        byte[] signed = SignedPdfFixtures.Sign(PdfWithLtvData(), pfx, password);

        using var file = new MemoryStream();
        file.Write(signed, 0, signed.Length);
        PdfSigner.EmbedDss(file, new LtvEvidence(ocspResponses: new[] { Encoding.ASCII.GetBytes(ocsp) }));
        return file.ToArray();
    }

    private static List<string> DssStreams(byte[] pdf, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var dss = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.DSS]) as PdfDictionary;
        var result = new List<string>();
        if (doc.ResolveObject(dss?[new PdfName(key)]) is not PdfArray arr) return result;
        foreach (var item in arr)
            if (doc.ResolveObject(item) is PdfStream s && s.RawData != null)
                result.Add(Encoding.ASCII.GetString(s.RawData));
        return result;
    }

    /// <summary>
    /// A one-page document that already carries a <c>/DSS</c> — a CRL and a <c>/VRI</c> entry
    /// referencing it — and a trailer with <c>/Info</c>, <c>/ID</c> and a key no writer knows.
    /// </summary>
    private static byte[] PdfWithLtvData()
    {
        var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(file.Position); Ascii(s); }

        const string content = "BT /F1 12 Tf 72 700 Td (signed content) Tj ET\n";
        byte[] crl = Encoding.ASCII.GetBytes("crl-OLD");

        Ascii("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /DSS 6 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}endstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        Obj("6 0 obj\n<< /Type /DSS /CRLs [7 0 R] /VRI << /AABBCC << /CRL [7 0 R] >> >> >>\nendobj\n");
        offsets.Add(file.Position);
        Ascii($"7 0 obj\n<< /Length {crl.Length} >>\nstream\n");
        file.Write(crl, 0, crl.Length);
        Ascii("\nendstream\nendobj\n");
        Obj("8 0 obj\n<< /Producer (Tetitulz test) >>\nendobj\n");

        long xref = file.Position;
        int size = offsets.Count + 1;
        Ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (long o in offsets) Ascii(o.ToString("D10") + " 00000 n \n");
        Ascii($"trailer\n<< /Size {size} /Root 1 0 R /Info 8 0 R /ID [<0102> <0304>] "
              + "/TetitulzProducerKey (kept) >>\n"
              + $"startxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }
}
