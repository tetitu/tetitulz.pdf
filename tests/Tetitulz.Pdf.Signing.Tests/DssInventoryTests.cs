// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Asn1.Oiw;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfSigningOperations.GetDssInventory(Stream, DateTime)"/> — what revocation evidence
/// a document carries and whether it is current.
/// </summary>
/// <remarks>
/// <para><b>The assertions are on the parsed <c>/DSS</c> contents</b>, per the standing rule for
/// this work: never on a byte count, and never merely that a call returned something. A document
/// with no LTV evidence and a document whose evidence expired last week both produce a perfectly
/// valid inventory object, so only the contents separate them.</para>
///
/// <para><b>Currency is judged against an instant the test supplies</b>, which is what makes the
/// stale and current cases the same fixture with a different clock rather than two fixtures that
/// might differ in some other way.</para>
/// </remarks>
public class DssInventoryTests
{
    private static readonly SecureRandom Random = new SecureRandom();
    private static int _crlNumber;

    [Fact]
    public void ADocumentWithNoDssReportsNoneAndIsNotCurrent()
    {
        DssInventory inv = Read(PdfWithDss(null, null));

        Assert.False(inv.HasDss);
        Assert.Empty(inv.OcspResponses);
        Assert.Empty(inv.Crls);
        Assert.False(inv.IsCurrent);
    }

    /// <summary>
    /// A <c>/DSS</c> that exists and holds nothing is a producer that began the job and did not
    /// finish it. It must not read as current: there is no evidence standing behind the signature.
    /// </summary>
    [Fact]
    public void AnEmptyDssIsPresentButNotCurrent()
    {
        DssInventory inv = Read(PdfWithDss(Array.Empty<byte[]>(), null));

        Assert.True(inv.HasDss);
        Assert.Empty(inv.Crls);
        Assert.False(inv.IsCurrent);
    }

    /// <summary>A CRL whose <c>nextUpdate</c> has not passed is current, and says so.</summary>
    [Fact]
    public void ACrlWithinItsNextUpdateIsCurrent()
    {
        var ca = SigningTestCrypto.Ca("CN=Inventory CA");
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        byte[] crl = Crl(ca, now.AddDays(-1), now.AddDays(6));

        DssInventory inv = Read(PdfWithDss(new[] { crl }, null), now);

        var entry = Assert.Single(inv.Crls);
        Assert.Equal(DssEvidenceKind.Crl, entry.Kind);
        Assert.Equal(now.AddDays(-1), entry.ThisUpdate!.Value, TimeSpan.FromSeconds(2));
        Assert.Equal(now.AddDays(6), entry.NextUpdate!.Value, TimeSpan.FromSeconds(2));
        Assert.True(entry.IsCurrent, entry.Issue);
        Assert.Null(entry.Issue);
        Assert.True(inv.IsCurrent);
    }

    /// <summary>
    /// The same CRL, judged a fortnight later. This is the case the whole feature exists for: the
    /// signature still verifies, and the evidence behind it has expired.
    /// </summary>
    [Fact]
    public void TheSameCrlIsStaleOnceItsNextUpdateHasPassed()
    {
        var ca = SigningTestCrypto.Ca("CN=Inventory CA");
        DateTime issued = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        byte[] crl = Crl(ca, issued.AddDays(-1), issued.AddDays(6));

        DssInventory inv = Read(PdfWithDss(new[] { crl }, null), issued.AddDays(14));

        var entry = Assert.Single(inv.Crls);
        Assert.False(entry.IsCurrent);
        Assert.Contains("stale", entry.Issue!, StringComparison.OrdinalIgnoreCase);
        Assert.False(inv.IsCurrent);
    }

    /// <summary>
    /// A CRL with no <c>nextUpdate</c> is not held to OCSP's seven days — RFC 5280 §5.1.2.5 makes it
    /// optional, and such a cap would refuse valid revocation data — but to the verifier's default
    /// cap of 365 days, past which default verification reads absence from it as unknown — so a
    /// five-year-old one is not current. The OCSP rule is the stricter one, which is why the reader
    /// asks a different predicate for each.
    /// </summary>
    [Fact]
    public void ACrlWithNoNextUpdateIsAgedOutAtTheVerifiersCapNotOcspsWeek()
    {
        var ca = SigningTestCrypto.Ca("CN=Inventory CA");
        DateTime issued = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        byte[] crl = Crl(ca, issued, null);

        var withinTheCap = Assert.Single(Read(PdfWithDss(new[] { crl }, null), issued.AddDays(300)).Crls);
        Assert.Null(withinTheCap.NextUpdate);
        Assert.True(withinTheCap.IsCurrent, withinTheCap.Issue);

        var pastTheCap = Assert.Single(Read(PdfWithDss(new[] { crl }, null), issued.AddYears(5)).Crls);
        Assert.False(pastTheCap.IsCurrent);
        Assert.Contains("365 days", pastTheCap.Issue, StringComparison.Ordinal);
    }

    /// <summary>Bytes that are not a CRL are reported with a reason, not silently counted.</summary>
    [Fact]
    public void UnreadableEvidenceIsReportedRatherThanCounted()
    {
        DssInventory inv = Read(PdfWithDss(new[] { Encoding.ASCII.GetBytes("<html>404</html>") }, null));

        var entry = Assert.Single(inv.Crls);
        Assert.False(entry.IsCurrent);
        Assert.NotNull(entry.Issue);
        Assert.Null(entry.ThisUpdate);
        Assert.False(inv.IsCurrent);
    }

    /// <summary>The <c>/VRI</c> keys are enumerated, so a caller can see per-signature evidence.</summary>
    [Fact]
    public void VriKeysAreEnumerated()
    {
        DssInventory inv = Read(PdfWithDss(Array.Empty<byte[]>(), new[] { "A1B2C3", "D4E5F6" }));
        Assert.Equal(new[] { "A1B2C3", "D4E5F6" }, inv.VriKeys.OrderBy(k => k, StringComparer.Ordinal));
    }

    /// <summary>
    /// An encrypted document is refused rather than reported as carrying nothing.
    /// </summary>
    /// <remarks>
    /// <c>PdfDocument</c> leaves an encrypted file with a null catalog, so without this guard the
    /// inventory answers <c>HasDss = false</c> — which its own documentation defines as "the
    /// document carries no LTV evidence". That is a statement about the file, and it would be
    /// false. The same shape as the Splitter bug this repository records: an encrypted document
    /// that silently looks empty rather than refusing.
    /// </remarks>
    [Fact]
    public void AnEncryptedDocumentIsRefusedRatherThanReportedEmpty()
    {
        byte[] pdf = EncryptedPdf();
        using (var probe = PdfDocument.Open(new MemoryStream(pdf, false)))
            Assert.True(probe.IsEncrypted, "the fixture is not being read as encrypted");

        using var ms = new MemoryStream(pdf, writable: false);
        var ex = Assert.Throws<PdfSigningException>(() => PdfSigningOperations.GetDssInventory(ms));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The same for the signature-field enumeration, for the same reason.</summary>
    [Fact]
    public void AnEncryptedDocumentIsRefusedByTheFieldEnumerationToo()
    {
        using var ms = new MemoryStream(EncryptedPdf(), writable: false);
        var ex = Assert.Throws<PdfSigningException>(
            () => PdfSigningOperations.GetUnsignedSignatureFieldNames(ms));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// /O and /U must be full 32-byte hex strings or the parser does not read the document as
    /// encrypted at all, and the guard under test is never reached.
    /// </summary>
    private static byte[] EncryptedPdf()
    {
        var sb = new StringBuilder();
        var offsets = new int[5];
        void Obj(int n, string body) { offsets[n] = sb.Length; sb.Append(n).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }

        sb.Append("%PDF-1.7\n");
        Obj(1, "<< /Type /Catalog /Pages 2 0 R >>");
        Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>");
        Obj(4, "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 " +
               "/O <0000000000000000000000000000000000000000000000000000000000000000> " +
               "/U <0000000000000000000000000000000000000000000000000000000000000000> >>");

        int count = 5;
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(count).Append("\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++) sb.Append(offsets[i].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(count).Append(" /Root 1 0 R /Encrypt 4 0 R /ID [<01> <01>] >>\n")
          .Append("startxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
    // ------------------------------------------------------------------ OCSP
    //
    // Until these existed ReadOcsp could be deleted whole and this file stayed green: every
    // fixture above is a CRL. The OCSP path is not a copy of the CRL path — it aggregates across
    // SingleResps, applies the opposite no-nextUpdate rule, and reads the certificate status.

    /// <summary>A fresh, good OCSP response is current and reports no issue.</summary>
    [Fact]
    public void AGoodOcspResponseWithinItsNextUpdateIsCurrent()
    {
        var ca = SigningTestCrypto.Ca("CN=Inventory CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        DssInventory inv = Read(PdfWithOcsp(
            Ocsp(ca, leaf.Cert.SerialNumber, null, now.AddHours(-1), now.AddDays(3), now)), now);

        var entry = Assert.Single(inv.OcspResponses);
        Assert.Equal(DssEvidenceKind.OcspResponse, entry.Kind);
        Assert.True(entry.IsCurrent, entry.Issue);
        Assert.True(inv.IsCurrent);
    }

    /// <summary>
    /// The same response, its <c>producedAt</c> a day ahead of the clock: the verifier refuses one
    /// produced in the future, so the inventory, asking the verifier's rule, does not call it current.
    /// The response above, produced now, is the control.
    /// </summary>
    [Fact]
    public void AnOcspResponseProducedInTheFutureIsNotCurrent()
    {
        var ca = SigningTestCrypto.Ca("CN=Inventory CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        DssInventory inv = Read(PdfWithOcsp(
            Ocsp(ca, leaf.Cert.SerialNumber, null, now.AddHours(-1), now.AddDays(3), now.AddDays(1))), now);

        var entry = Assert.Single(inv.OcspResponses);
        Assert.False(entry.IsCurrent);
        Assert.Contains("produced in the future", entry.Issue!, StringComparison.Ordinal);
        Assert.False(inv.IsCurrent);
    }

    /// <summary>
    /// <b>A revoked certificate is not "current evidence" in any useful sense.</b> The response is
    /// perfectly fresh — of the worst possible fact — and an inventory reporting only freshness
    /// would let a caller conclude the document needs nothing done. Refreshing cannot help it.
    /// </summary>
    [Fact]
    public void AnOcspResponseSayingRevokedIsNotReportedAsCurrent()
    {
        var ca = SigningTestCrypto.Ca("CN=Inventory CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        var revoked = new RevokedStatus(now.AddDays(-10), CrlReason.KeyCompromise);

        DssInventory inv = Read(PdfWithOcsp(
            Ocsp(ca, leaf.Cert.SerialNumber, revoked, now.AddHours(-1), now.AddDays(3), now)), now);

        var entry = Assert.Single(inv.OcspResponses);
        Assert.False(entry.IsCurrent);
        Assert.Contains("revoked", entry.Issue!, StringComparison.OrdinalIgnoreCase);
        Assert.False(inv.IsCurrent);
    }

    /// <summary>
    /// The OCSP side of the asymmetry: with no <c>nextUpdate</c>, an old response IS aged out —
    /// the opposite of the CRL rule, and the flag that distinguishes them.
    /// </summary>
    [Fact]
    public void AnOcspResponseWithNoNextUpdateIsAgedOut()
    {
        var ca = SigningTestCrypto.Ca("CN=Inventory CA");
        var leaf = SigningTestCrypto.Leaf(ca);
        DateTime issued = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        DssInventory inv = Read(PdfWithOcsp(
            OcspNoNextUpdate(ca, leaf.Cert.SerialNumber, issued)), issued.AddDays(60));

        var entry = Assert.Single(inv.OcspResponses);
        Assert.Null(entry.NextUpdate);
        Assert.False(entry.IsCurrent);
    }

    /// <summary>The certificate count is read, and the no-clock overload is reachable.</summary>
    [Fact]
    public void CertificatesAreCountedAndTheNoClockOverloadWorks()
    {
        byte[] pdf = PdfWithCerts(2);
        using var ms = new MemoryStream(pdf, writable: false);
        DssInventory inv = PdfSigningOperations.GetDssInventory(ms);

        Assert.True(inv.HasDss);
        Assert.Equal(2, inv.CertificateCount);
    }

    private static byte[] Ocsp(SigningTestCrypto.Issued issuer, BigInteger serial,
                               object status, DateTime thisUpdate, DateTime nextUpdate, DateTime producedAt)
    {
        var gen = new BasicOcspRespGenerator(new RespID(issuer.Cert.SubjectDN));
        var certId = new CertificateID(
            new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), issuer.Cert, serial);
        gen.AddResponse(certId, (CertificateStatus)status, thisUpdate, nextUpdate, null);
        var basic = gen.Generate("SHA256WithRSA", issuer.Private, new[] { issuer.Cert }, producedAt);
        return new OCSPRespGenerator().Generate(OcspRespStatus.Successful, basic).GetEncoded();
    }

    private static byte[] OcspNoNextUpdate(SigningTestCrypto.Issued issuer, BigInteger serial, DateTime thisUpdate)
    {
        var gen = new BasicOcspRespGenerator(new RespID(issuer.Cert.SubjectDN));
        var certId = new CertificateID(
            new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), issuer.Cert, serial);
        // nextUpdate omitted entirely - the case RFC 6960 §4.2.2.1 bounds by age.
        gen.AddResponse(certId, null, thisUpdate, null, null);
        var basic = gen.Generate("SHA256WithRSA", issuer.Private, new[] { issuer.Cert }, thisUpdate);
        return new OCSPRespGenerator().Generate(OcspRespStatus.Successful, basic).GetEncoded();
    }

    /// <summary>
    /// Evidence held under a <c>/VRI</c> key is reported, keyed by that signature.
    /// </summary>
    /// <remarks>
    /// <para>The register recorded this as deliberate: reading <c>/VRI</c> "means deciding which
    /// signature the caller is asking about". That is true of a single flat list and not true of a
    /// list <b>keyed</b> by signature — the <c>/VRI</c> key <i>is</i> the signature's identity, so
    /// reporting every entry decides nothing. The caller still chooses; it now has something to
    /// choose from.</para>
    ///
    /// <para>The document-wide lists are deliberately empty here, which is the shape the gap was
    /// about: before this, such a document reported no evidence at all.</para>
    /// </remarks>
    [Fact]
    public void VriScopedEvidenceIsReportedUnderItsKey()
    {
        var ca = SigningTestCrypto.Ca("CN=VRI CA");
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        DssInventory inv = Read(PdfWithVriCrl("AABBCC", Crl(ca, now.AddDays(-1), now.AddDays(6)), "CRL"), now);

        Assert.Empty(inv.Crls);                         // nothing document-wide
        Assert.Equal(new[] { "AABBCC" }, inv.VriKeys);

        var entry = Assert.Single(inv.VriEvidence);
        Assert.Equal("AABBCC", entry.Key);
        var crl = Assert.Single(entry.Crls);
        Assert.Equal(DssEvidenceKind.Crl, crl.Kind);
        Assert.Equal(now.AddDays(6), crl.NextUpdate!.Value, TimeSpan.FromSeconds(2));
        Assert.True(entry.IsCurrent, crl.Issue);
    }

    /// <summary>
    /// <c>IsCurrent</c> still judges the <b>document-wide</b> lists only, unchanged.
    /// </summary>
    /// <remarks>
    /// It is a published answer, and folding <c>/VRI</c> into it would silently redefine what a
    /// <c>false</c> means for every existing caller. The fixture carries perfectly current
    /// <c>/VRI</c> evidence and still reports <c>false</c> — which is the documented behaviour and
    /// now the actionable one, because <c>VriEvidence</c> says what is actually there.
    /// </remarks>
    [Fact]
    public void IsCurrentStillJudgesTheDocumentWideListsOnly()
    {
        var ca = SigningTestCrypto.Ca("CN=VRI CA");
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        DssInventory inv = Read(PdfWithVriCrl("AABBCC", Crl(ca, now.AddDays(-1), now.AddDays(6)), "CRL"), now);

        Assert.False(inv.IsCurrent);
        Assert.True(Assert.Single(inv.VriEvidence).IsCurrent);
    }

    /// <summary>
    /// <b>A <c>/VRI</c> entry names its arrays in the SINGULAR</b> — <c>/CRL</c>, not <c>/CRLs</c>.
    /// </summary>
    /// <remarks>
    /// The one detail this had to get right to exist at all. Reading a <c>/VRI</c> entry with the
    /// document-wide plural names finds nothing and reports an entry carrying no evidence, which is
    /// indistinguishable from a document that genuinely has none — the same silent-empty failure
    /// the inventory already refuses an encrypted document over. Asserted as a pair, because the
    /// singular case alone passes for an implementation that reads both.
    /// </remarks>
    [Fact]
    public void AVriEntryIsReadWithTheSingularKeyName()
    {
        var ca = SigningTestCrypto.Ca("CN=VRI CA");
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        byte[] crl = Crl(ca, now.AddDays(-1), now.AddDays(6));

        Assert.Single(Assert.Single(Read(PdfWithVriCrl("AABBCC", crl, "CRL"), now).VriEvidence).Crls);

        // The same bytes under the document-wide plural name inside a /VRI entry are not that
        // entry's evidence, and are not silently adopted as if they were.
        var wrong = Assert.Single(Read(PdfWithVriCrl("AABBCC", crl, "CRLs"), now).VriEvidence);
        Assert.Empty(wrong.Crls);
        Assert.False(wrong.IsCurrent);
    }

    /// <summary>A document with no <c>/VRI</c> reports none — the control for the three above.</summary>
    [Fact]
    public void ADocumentWithNoVriReportsNoVriEvidence()
    {
        var ca = SigningTestCrypto.Ca("CN=Inventory CA");
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        DssInventory inv = Read(PdfWithDss(new[] { Crl(ca, now.AddDays(-1), now.AddDays(6)) }, null), now);

        Assert.Empty(inv.VriEvidence);
        Assert.Single(inv.Crls);
        Assert.True(inv.IsCurrent);
    }

    /// <summary>
    /// A <c>/VRI</c> key with an empty entry is reported with no evidence, and the document-wide
    /// list is <b>not</b> attributed to it.
    /// </summary>
    /// <remarks>
    /// Without this, an implementation that fell back to the document-wide arrays when a VRI entry
    /// held nothing would pass every test above — and would tell a caller that signature A is
    /// covered by evidence that belongs to the document as a whole, which is the exact confusion
    /// <c>/VRI</c> exists to prevent.
    /// </remarks>
    [Fact]
    public void AnEmptyVriEntryIsNotFilledFromTheDocumentWideLists()
    {
        var ca = SigningTestCrypto.Ca("CN=Inventory CA");
        DateTime now = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        DssInventory inv = Read(PdfWithDss(new[] { Crl(ca, now.AddDays(-1), now.AddDays(6)) },
                                           new[] { "AABBCC" }), now);

        Assert.Single(inv.Crls);                        // the document-wide one is there
        var entry = Assert.Single(inv.VriEvidence);
        Assert.Empty(entry.Crls);                       // ...and is not this signature's
        Assert.False(entry.IsCurrent);
    }

    /// <summary>
    /// A catalog whose <c>/DSS</c> has <b>no</b> document-wide lists and one <c>/VRI</c> entry
    /// holding <paramref name="crl"/> under <paramref name="arrayKey"/>.
    /// </summary>
    private static byte[] PdfWithVriCrl(string vriKey, byte[] crl, string arrayKey)
    {
        var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(file.Position); Ascii(s); }

        Ascii("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /DSS << /VRI << /" + vriKey
            + " << /" + arrayKey + " [4 0 R] >> >> >> >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        offsets.Add(file.Position);
        Ascii("4 0 obj\n<< /Length " + crl.Length + " >>\nstream\n");
        file.Write(crl, 0, crl.Length);
        Ascii("\nendstream\nendobj\n");

        long xref = file.Position;
        int size = offsets.Count + 1;
        Ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (long o in offsets) Ascii(o.ToString("D10") + " 00000 n \n");
        Ascii($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }

    private static byte[] PdfWithOcsp(byte[] ocsp) => PdfWithStore("OCSPs", new[] { ocsp });

    private static byte[] PdfWithCerts(int n)
    {
        var certs = new byte[n][];
        for (int i = 0; i < n; i++) certs[i] = Encoding.ASCII.GetBytes("cert-" + i);
        return PdfWithStore("Certs", certs);
    }
    // ------------------------------------------------------------------ fixtures

    /// <summary>A catalog whose /DSS holds the given payloads under <paramref name="key"/>.</summary>
    private static byte[] PdfWithStore(string key, byte[][] payloads)
    {
        var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(file.Position); Ascii(s); }

        Ascii("%PDF-1.7\n");
        var refs = new StringBuilder();
        for (int i = 0; i < payloads.Length; i++) refs.Append(5 + i).Append(" 0 R ");

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /DSS 4 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        Obj($"4 0 obj\n<< /Type /DSS /{key} [{refs}] >>\nendobj\n");

        foreach (byte[] data in payloads)
        {
            offsets.Add(file.Position);
            Ascii($"{offsets.Count} 0 obj\n<< /Length {data.Length} >>\nstream\n");
            file.Write(data, 0, data.Length);
            Ascii("\nendstream\nendobj\n");
        }

        long xref = file.Position;
        int size = offsets.Count + 1;
        Ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (long o in offsets) Ascii(o.ToString("D10") + " 00000 n \n");
        Ascii($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }
    private static DssInventory Read(byte[] pdf) => Read(pdf, DateTime.UtcNow);

    private static DssInventory Read(byte[] pdf, DateTime nowUtc)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfSigningOperations.GetDssInventory(ms, nowUtc);
    }

    private static byte[] Crl(SigningTestCrypto.Issued ca, DateTime thisUpdate, DateTime? nextUpdate)
    {
        var gen = new X509V2CrlGenerator();
        gen.SetIssuerDN(ca.Cert.SubjectDN);
        gen.SetThisUpdate(thisUpdate);
        if (nextUpdate.HasValue) gen.SetNextUpdate(nextUpdate.Value);
        gen.AddExtension(X509Extensions.CrlNumber, false,
            new CrlNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _crlNumber))));
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", ca.Private, Random)).GetEncoded();
    }

    /// <summary>
    /// A catalog with a <c>/DSS</c> holding <paramref name="crls"/> as stream objects, or no
    /// <c>/DSS</c> at all when it is null.
    /// </summary>
    private static byte[] PdfWithDss(byte[][] crls, string[] vriKeys)
    {
        var file = new MemoryStream();
        var offsets = new List<long>();
        void Ascii(string s) { byte[] b = Encoding.ASCII.GetBytes(s); file.Write(b, 0, b.Length); }
        void Obj(string s) { offsets.Add(file.Position); Ascii(s); }

        Ascii("%PDF-1.7\n");

        string dss = string.Empty;
        if (crls != null)
        {
            // CRL streams are objects 4..; the /DSS itself is direct in the catalog.
            var refs = new StringBuilder();
            for (int i = 0; i < crls.Length; i++) refs.Append(4 + i).Append(" 0 R ");
            var parts = new StringBuilder("/DSS << /CRLs [").Append(refs).Append(']');
            if (vriKeys != null && vriKeys.Length > 0)
            {
                parts.Append(" /VRI << ");
                foreach (string k in vriKeys) parts.Append('/').Append(k).Append(" << >> ");
                parts.Append(">>");
            }
            parts.Append(" >> ");
            dss = parts.ToString();
        }

        Obj($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R {dss}>>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        if (crls != null)
        {
            foreach (byte[] crl in crls)
            {
                offsets.Add(file.Position);
                Ascii($"{offsets.Count} 0 obj\n<< /Length {crl.Length} >>\nstream\n");
                file.Write(crl, 0, crl.Length);
                Ascii("\nendstream\nendobj\n");
            }
        }

        long xref = file.Position;
        int size = offsets.Count + 1;
        Ascii($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (long o in offsets) Ascii(o.ToString("D10") + " 00000 n \n");
        Ascii($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return file.ToArray();
    }
}