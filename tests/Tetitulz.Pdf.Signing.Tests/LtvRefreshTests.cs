// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfLtvRefresher.AddVerificationInfo"/> — appending revocation evidence to an
/// already-signed document, which is what makes PAdES B-LT/B-LTA a refresh cycle rather than a
/// one-shot.
/// </summary>
/// <remarks>
/// <para><b>Every assertion reads the parsed <c>/DSS</c> of the output</b>, never a byte count. A
/// refresh that wrote nothing, wrote a stub catalog, or dropped the existing store all produce a
/// larger file — the standing rule for this work says so explicitly, and the merge case below is
/// exactly the shape where a size check passes while the document is ruined.</para>
///
/// <para><b>The merge is the point.</b> This repository has shipped a defect where revocation data
/// was replaced rather than merged, destroying a B-LT signature's evidence, and no test caught it
/// because the loss is an <i>absence</i>. So the load-bearing test is the one that refreshes a
/// document which already has a <c>/DSS</c> and asserts the old entries are still referenced.</para>
/// </remarks>
public class LtvRefreshTests
{
    [Fact]
    public void EvidenceIsAppendedAndReachableFromTheCatalog()
    {
        byte[] refreshed = Refresh(PdfWithDss(null),
            new LtvEvidence(ocspResponses: new[] { Bytes("ocsp-A") }, crls: new[] { Bytes("crl-A") }));

        var dss = Dss(refreshed);
        Assert.Equal(new[] { "ocsp-A" }, Streams(refreshed, dss, "OCSPs"));
        Assert.Equal(new[] { "crl-A" }, Streams(refreshed, dss, "CRLs"));
    }

    /// <summary>
    /// <b>The one that matters.</b> A document that already carries evidence must still carry it
    /// afterwards. Replacing the store instead of merging leaves a file that opens, verifies its
    /// newest signature, and has silently lost what an older one depends on.
    /// </summary>
    [Fact]
    public void ExistingEvidenceSurvivesTheRefresh()
    {
        byte[] original = PdfWithDss(new[] { "crl-OLD" });

        byte[] refreshed = Refresh(original, new LtvEvidence(crls: new[] { Bytes("crl-NEW") }));

        var dss = Dss(refreshed);
        var crls = Streams(refreshed, dss, "CRLs");
        Assert.Contains("crl-OLD", crls);
        Assert.Contains("crl-NEW", crls);
        Assert.Equal(2, crls.Count);
    }

    /// <summary>The result reports the split, so a caller can tell a merge from a rewrite.</summary>
    [Fact]
    public void TheResultDistinguishesAddedFromCarriedForward()
    {
        using var input = new MemoryStream(PdfWithDss(new[] { "crl-OLD" }), writable: false);
        using var output = new MemoryStream();
        LtvRefreshResult result = PdfLtvRefresher.AddVerificationInfo(
            input, output, new LtvEvidence(crls: new[] { Bytes("crl-NEW") }));

        Assert.Equal(1, result.CrlsAdded);
        Assert.Equal(1, result.CrlsCarriedForward);
    }

    /// <summary>
    /// Carried forward <b>by reference</b>: the old stream keeps its object number and is not
    /// rewritten. Copying the objects would grow the file by the whole store on every refresh.
    /// </summary>
    [Fact]
    public void CarriedForwardEvidenceKeepsItsObjectNumber()
    {
        byte[] original = PdfWithDss(new[] { "crl-OLD" });
        int before = OnlyCrlObjectNumber(original);

        byte[] refreshed = Refresh(original, new LtvEvidence(crls: new[] { Bytes("crl-NEW") }));

        var dss = Dss(refreshed);
        var refs = ((PdfArray)Resolve(refreshed, dss[new PdfName("CRLs")])!).ToList();
        Assert.Contains(refs, r => r is PdfReference pr && pr.ReferenceObjectNumber == before);
    }

    /// <summary>
    /// The superseded catalog must be the <b>whole</b> catalog. A stub carrying only /DSS would
    /// destroy /Pages, and the document would open with no pages at all.
    /// </summary>
    [Fact]
    public void TheSupersededCatalogKeepsTheRestOfTheDocument()
    {
        byte[] refreshed = Refresh(PdfWithDss(null), new LtvEvidence(crls: new[] { Bytes("crl-A") }));

        using var doc = PdfDocument.Open(new MemoryStream(refreshed, false));
        Assert.NotNull(doc.Catalog);
        Assert.Equal(1, doc.Pages.Count);
    }

    /// <summary>
    /// <c>/VRI</c> is signature-scoped, so a refresh that knows no signature leaves it alone —
    /// <b>contents and all</b>.
    /// </summary>
    /// <remarks>
    /// The earlier version of this test used a <c>/VRI</c> entry that was an empty dictionary, so
    /// it could only assert that a key existed. An entry with nothing in it has nothing to lose:
    /// the assertion held however badly the entry itself was handled. This fixture gives the entry
    /// a <c>/CRL</c> array and a <c>/TU</c> timestamp, so the test can see them survive.
    /// </remarks>
    [Fact]
    public void VriEntryContentsSurviveTheRefresh()
    {
        byte[] refreshed = Refresh(PdfWithVriHoldingCrl(),
            new LtvEvidence(crls: new[] { Bytes("crl-NEW") }));

        using var doc = PdfDocument.Open(new MemoryStream(refreshed, false));
        var dss = doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.DSS]) as PdfDictionary;
        var vri = doc.ResolveObject(dss![new PdfName("VRI")]) as PdfDictionary;
        var entry = doc.ResolveObject(vri![new PdfName("AABBCC")]) as PdfDictionary;
        Assert.NotNull(entry);

        var crlArr = doc.ResolveObject(entry![new PdfName("CRL")]) as PdfArray;
        Assert.NotNull(crlArr);
        var s = doc.ResolveObject(crlArr![0]) as PdfStream;
        Assert.Equal("crl-OLD", Encoding.ASCII.GetString(s!.RawData!));
        Assert.NotNull(doc.ResolveObject(entry[new PdfName("TU")]) as PdfString);
    }

    /// <summary>/DSS with /CRLs [5 0 R] and a /VRI entry that references the same stream.</summary>
    private static byte[] PdfWithVriHoldingCrl()
    {
        var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(file.Position); Ascii(s); }

        Ascii("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        Obj("4 0 obj\n<< /Type /DSS /CRLs [5 0 R] "
            + "/VRI << /AABBCC << /CRL [5 0 R] /TU (2026) >> >> >>\nendobj\n");

        byte[] data = Bytes("crl-OLD");
        offsets.Add(file.Position);
        Ascii($"5 0 obj\n<< /Length {data.Length} >>\nstream\n");
        file.Write(data, 0, data.Length);
        Ascii("\nendstream\nendobj\n");

        long xref = file.Position;
        int size = offsets.Count + 1;
        Ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (long o in offsets) Ascii(o.ToString("D10") + " 00000 n \n");
        Ascii($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary><c>/VRI</c> is signature-scoped, so a refresh that knows no signature leaves it alone.</summary>
    [Fact]
    public void VriIsCarriedForwardUntouched()
    {
        byte[] refreshed = Refresh(PdfWithDss(null, vriKey: "AABBCC"),
            new LtvEvidence(crls: new[] { Bytes("crl-A") }));

        var dss = Dss(refreshed);
        var vri = Resolve(refreshed, dss[new PdfName("VRI")]) as PdfDictionary;
        Assert.NotNull(vri);
        Assert.Contains(vri!.Keys, k => k.Value == "AABBCC");
    }

    /// <summary>
    /// The merge covers <b>every</b> array, not just <c>/CRLs</c>. Each carry-forward is separate
    /// code, so proving one proves nothing about the others.
    /// </summary>
    [Fact]
    public void ExistingOcspAndCertsSurviveToo()
    {
        byte[] original = PdfWithStore(ocsp: new[] { "ocsp-OLD" }, certs: new[] { "cert-OLD" });

        byte[] refreshed = Refresh(original,
            new LtvEvidence(ocspResponses: new[] { Bytes("ocsp-NEW") },
                            certificates: new[] { Bytes("cert-NEW") }));

        var dss = Dss(refreshed);
        Assert.Equal(new[] { "ocsp-OLD", "ocsp-NEW" }, Streams(refreshed, dss, "OCSPs"));
        Assert.Equal(new[] { "cert-OLD", "cert-NEW" }, Streams(refreshed, dss, "Certs"));
    }

    /// <summary>
    /// Counts that differ, so the two numbers cannot be swapped without the test noticing. The
    /// earlier version used one of each.
    /// </summary>
    [Fact]
    public void AddedAndCarriedForwardAreDistinguishable()
    {
        using var input = new MemoryStream(PdfWithDss(new[] { "crl-1", "crl-2", "crl-3" }), writable: false);
        using var output = new MemoryStream();
        LtvRefreshResult result = PdfLtvRefresher.AddVerificationInfo(
            input, output, new LtvEvidence(crls: new[] { Bytes("crl-NEW") }));

        Assert.Equal(1, result.CrlsAdded);
        Assert.Equal(3, result.CrlsCarriedForward);
    }

    /// <summary>
    /// A null entry is refused <b>before</b> the original is copied out. Writing the document and
    /// then throwing leaves a truncated file the caller may not know to discard.
    /// </summary>
    [Fact]
    public void ANullEvidenceEntryIsRefusedWithoutWritingOutput()
    {
        using var input = new MemoryStream(PdfWithDss(null), writable: false);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() => PdfLtvRefresher.AddVerificationInfo(
            input, output, new LtvEvidence(crls: new byte[][] { null })));
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The appended revision carries the input trailer's keys forward — <c>/Info</c>, <c>/ID</c>
    /// changes the document''s identity — <c>/ID</c> is what a reader matches revisions on — and
    /// identity, and nothing else in this file would notice.
    /// </summary>
    [Fact]
    public void TheAppendedTrailerKeepsInfoAndId()
    {
        byte[] refreshed = Refresh(PdfWithStore(ocsp: new[] { "ocsp-OLD" }),
            new LtvEvidence(crls: new[] { Bytes("crl-NEW") }));

        // Read as RAW BYTES from the LAST trailer in the file. `PdfDocument.Trailer` merges the
        // revision chain, so it answers from the ORIGINAL trailer even when the appended one
        // dropped both keys — the first version of this test asserted that and passed with the
        // copy deleted. Where a writer emits bytes, something has to read bytes.
        string text = Encoding.ASCII.GetString(refreshed);
        int last = text.LastIndexOf("trailer", StringComparison.Ordinal);
        Assert.True(last >= 0, "no trailer in the output");
        string appended = text.Substring(last);

        Assert.Contains("/Info", appended, StringComparison.Ordinal);
        Assert.Contains("/ID", appended, StringComparison.Ordinal);

        // And a key this writer has never heard of. Asserting only /Info and /ID would pass on a
        // hand-spelled trailer that names exactly those and silently drops everything else — which
        // is what this writer did, and what TimestampTrailerKeyPreservationTests already pins for
        // the sibling writer. A trailer builder with a DENY list is the difference.
        Assert.Contains("/TetitulzProducerKey (kept)", appended, StringComparison.Ordinal);
    }
    /// <summary>
    /// §7.5.6 — the catalog is superseded at the generation the cross-reference table gives it. This
    /// writer read the generation off the trailer's <c>/Root</c> reference, so a file whose reference
    /// disagrees with its table had the catalog re-declared at a generation that supersedes nothing.
    /// </summary>
    [Fact]
    public void TheCatalogIsSupersededAtTheGenerationTheTableGivesIt()
    {
        byte[] refreshed = Refresh(PdfWithGenerationTwoCatalogNamedAsZero(),
            new LtvEvidence(crls: new[] { Bytes("crl-A") }));

        string text = Encoding.ASCII.GetString(refreshed);
        string appended = text.Substring(text.IndexOf("%%EOF", StringComparison.Ordinal));

        Assert.Contains("1 2 obj", appended, StringComparison.Ordinal);
        Assert.Contains("/Root 1 2 R", appended, StringComparison.Ordinal);
    }

    /// <summary>
    /// A document whose catalog is object 1 generation 2 in its cross-reference table, while the
    /// trailer names it <c>1 0 R</c>.
    /// </summary>
    private static byte[] PdfWithGenerationTwoCatalogNamedAsZero()
    {
        var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(file.Position); Ascii(s); }

        Ascii("%PDF-1.7\n");
        Obj("1 2 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        long xref = file.Position;
        Ascii("xref\n0 4\n0000000000 65535 f \n");
        Ascii(offsets[0].ToString("D10") + " 00002 n \n");
        Ascii(offsets[1].ToString("D10") + " 00000 n \n");
        Ascii(offsets[2].ToString("D10") + " 00000 n \n");
        Ascii($"trailer\n<< /Size 4 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>An encrypted document is refused before anything is written to the output.</summary>
    [Fact]
    public void AnEncryptedDocumentIsRefusedWithoutWritingOutput()
    {
        byte[] pdf = EncryptedLookingPdf();

        // The fixture must actually be read as encrypted, or the branch under test is never
        // reached and everything below is vacuous.
        using (var probe = PdfDocument.Open(new MemoryStream(pdf, false)))
            Assert.True(probe.IsEncrypted, "the fixture is not being read as encrypted");

        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();

        // The specific refusal, not ThrowsAny: a NullReferenceException from reading a null
        // catalog would satisfy ThrowsAny while meaning the guard had been deleted — which is how
        // the sibling writer's own remarks record this failing before.
        var ex = Assert.Throws<PdfSigningException>(() =>
            PdfLtvRefresher.AddVerificationInfo(input, output, new LtvEvidence(crls: new[] { Bytes("x") })));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, output.Length);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Refresh(byte[] pdf, LtvEvidence evidence)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        PdfLtvRefresher.AddVerificationInfo(input, output, evidence);
        return output.ToArray();
    }

    private static PdfObject? Resolve(byte[] pdf, PdfObject? obj)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, false));
        return doc.ResolveObject(obj);
    }

    private static PdfDictionary Dss(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, false));
        var d = doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.DSS]) as PdfDictionary;
        Assert.NotNull(d);
        return d!;
    }

    /// <summary>The decoded payload of every stream an array references.</summary>
    private static List<string> Streams(byte[] pdf, PdfDictionary dss, string key)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, false));
        var result = new List<string>();
        if (doc.ResolveObject(dss[new PdfName(key)]) is not PdfArray arr) return result;
        foreach (var item in arr)
            if (doc.ResolveObject(item) is PdfStream s && s.RawData != null)
                result.Add(Encoding.ASCII.GetString(s.RawData));
        return result;
    }

    private static int OnlyCrlObjectNumber(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, false));
        var dss = doc.ResolveObject(doc.Catalog!.Dictionary[PdfName.DSS]) as PdfDictionary;
        var arr = doc.ResolveObject(dss![new PdfName("CRLs")]) as PdfArray;
        return ((PdfReference)arr![0]).ReferenceObjectNumber;
    }

    /// <summary>
    /// A document whose /DSS carries the given payloads in each array, plus a trailer /ID and
    /// /Info — without those the refresher's copy of both could be deleted unnoticed.
    /// </summary>
    private static byte[] PdfWithStore(string[]? ocsp = null, string[]? certs = null)
    {
        var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(file.Position); Ascii(s); }

        Ascii("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        int next = 6;   // 5 is /Info; payload streams start at 6
        var ocspRefs = new StringBuilder();
        var certRefs = new StringBuilder();
        int ocspStart = next;
        if (ocsp != null) { foreach (var _ in ocsp) { ocspRefs.Append(next).Append(" 0 R "); next++; } }
        int certStart = next;
        if (certs != null) { foreach (var _ in certs) { certRefs.Append(next).Append(" 0 R "); next++; } }

        var sb = new StringBuilder("4 0 obj\n<< /Type /DSS ");
        if (ocsp != null && ocsp.Length > 0) sb.Append("/OCSPs [").Append(ocspRefs).Append("] ");
        if (certs != null && certs.Length > 0) sb.Append("/Certs [").Append(certRefs).Append("] ");
        sb.Append(">>\nendobj\n");
        Obj(sb.ToString());
        Obj("5 0 obj\n<< /Producer (Tetitulz test) >>\nendobj\n");

        void WriteStreams(string[]? payloads)
        {
            if (payloads == null) return;
            foreach (string s in payloads)
            {
                byte[] data = Bytes(s);
                offsets.Add(file.Position);
                Ascii($"{offsets.Count} 0 obj\n<< /Length {data.Length} >>\nstream\n");
                file.Write(data, 0, data.Length);
                Ascii("\nendstream\nendobj\n");
            }
        }
        WriteStreams(ocsp);
        WriteStreams(certs);

        long xref = file.Position;
        int size = offsets.Count + 1;
        Ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (long o in offsets) Ascii(o.ToString("D10") + " 00000 n \n");
        Ascii($"trailer\n<< /Size {size} /Root 1 0 R /Info 5 0 R /ID [<0102> <0304>] "
              + "/TetitulzProducerKey (kept) >>\n"
              + $"startxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }
    /// <summary>A minimal document, optionally already carrying a /DSS with the given CRL payloads.</summary>
    private static byte[] PdfWithDss(string[]? crls, string? vriKey = null)
    {
        var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(file.Position); Ascii(s); }

        Ascii("%PDF-1.7\n");

        string dssEntry = string.Empty;
        bool hasDss = crls != null || vriKey != null;
        if (hasDss) dssEntry = "/DSS 4 0 R ";

        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R {dssEntry}>>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        if (hasDss)
        {
            int firstCrl = 5;
            var refs = new StringBuilder();
            if (crls != null)
                for (int i = 0; i < crls.Length; i++) refs.Append(firstCrl + i).Append(" 0 R ");

            var sb = new StringBuilder("4 0 obj\n<< /Type /DSS ");
            if (crls != null && crls.Length > 0) sb.Append("/CRLs [").Append(refs).Append("] ");
            if (vriKey != null) sb.Append("/VRI << /").Append(vriKey).Append(" << >> >> ");
            sb.Append(">>\nendobj\n");
            Obj(sb.ToString());

            if (crls != null)
            {
                foreach (string payload in crls)
                {
                    byte[] data = Bytes(payload);
                    offsets.Add(file.Position);
                    Ascii($"{offsets.Count} 0 obj\n<< /Length {data.Length} >>\nstream\n");
                    file.Write(data, 0, data.Length);
                    Ascii("\nendstream\nendobj\n");
                }
            }
        }

        long xref = file.Position;
        int size = offsets.Count + 1;
        Ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (long o in offsets) Ascii(o.ToString("D10") + " 00000 n \n");
        Ascii($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    /// <summary>A document whose trailer declares /Encrypt, which is what the refusal keys on.</summary>
    private static byte[] EncryptedLookingPdf()
    {
        var b = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string s) { offsets.Add(b.Length); b.Append(s); }
        b.Append("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        // /O and /U must be full 32-byte hex strings. With a short <00> the parser does not read
        // the document as encrypted at all — the first version of this fixture used that, so
        // doc.IsEncrypted was false, the catalog-null guard fired instead, and the test passed
        // while never reaching the encryption refusal it is named for.
        Obj("4 0 obj\n<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 "
            + "/O <0000000000000000000000000000000000000000000000000000000000000000> "
            + "/U <0000000000000000000000000000000000000000000000000000000000000000> >>\nendobj\n");
        int count = offsets.Count + 1;
        int xref = b.Length;
        b.Append($"xref\n0 {count}\n0000000000 65535 f \n");
        foreach (int o in offsets) b.Append(o.ToString("D10")).Append(" 00000 n \n");
        b.Append($"trailer\n<< /Size {count} /Root 1 0 R /Encrypt 4 0 R "
                 + "/ID [<0102> <0102>] >>\nstartxref\n" + xref + "\n%%EOF\n");
        return Encoding.ASCII.GetBytes(b.ToString());
    }
}