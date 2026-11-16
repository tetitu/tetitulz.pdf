// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Tests for <see cref="PdfUa2Checker"/> — the PDF/UA-2 (ISO 14289-2:2024) Clause 5 version
/// identification check (<c>pdfuaid:part=2</c> / <c>pdfuaid:rev</c>) and 6.1's file version. The
/// clause-8.2.5.x per-structure-type rules are the Accessibility feature's structure checker's.
///
/// <para>Every PDF is built in memory as a <c>byte[]</c>; nothing is read from disk.</para>
/// </summary>
public class PdfUa2CheckerTests
{
    // ==================================================================
    //  Builders — objects are numbered 1..N in the order supplied.
    // ==================================================================

    private static byte[] BuildPdf(params string[] objects)
        => BuildPdfWithTrailer(string.Empty, objects);

    private static byte[] BuildPdfWithTrailer(string extraTrailerEntries, params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-2.0\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n{1}\nendobj\n", i + 1, objects[i]);
        }

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", objects.Length + 1);
        foreach (int off in offsets)
            sb.AppendFormat("{0:D10} 00000 n \n", off);

        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R{1} >>\n",
            objects.Length + 1, extraTrailerEntries);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length + " >>\nstream\n" + data + "\nendstream";

    /// <summary>A well-formed XMP packet declaring pdfuaid:part and pdfuaid:rev.</summary>
    private static string XmpPacket(string? partValue, string? revValue)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        sb.Append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("  <rdf:Description rdf:about=\"\"");
        sb.Append(" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">\n");
        if (partValue != null) sb.Append("   <pdfuaid:part>" + partValue + "</pdfuaid:part>\n");
        if (revValue != null) sb.Append("   <pdfuaid:rev>" + revValue + "</pdfuaid:rev>\n");
        sb.Append("  </rdf:Description>\n");
        sb.Append(" </rdf:RDF>\n");
        sb.Append("</x:xmpmeta>\n");
        sb.Append("<?xpacket end=\"w\"?>");
        return sb.ToString();
    }

    private static byte[] BuildDocWithMetadata(string? metadataPacket, bool encrypted = false)
    {
        const string catalog = "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>";
        const string pages = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
        const string page = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>";
        string metadata = StreamObject("/Type /Metadata /Subtype /XML", metadataPacket ?? string.Empty);

        return encrypted
            ? BuildPdfWithTrailer(" /Encrypt 5 0 R", catalog, pages, page, metadata, StandardEncryptDict)
            : BuildPdfWithTrailer(string.Empty, catalog, pages, page, metadata);
    }

    private static byte[] BuildDocWithoutMetadata()
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

    /// <summary>
    /// /P -4 sets every permission bit, so this models "streams are ciphertext" without any
    /// real key derivation — the checker only ever reads /Filter, /V, /R and /P elsewhere, and
    /// here it does not even read those; it just needs <c>doc.IsEncrypted</c> to be true.
    /// </summary>
    private const string StandardEncryptDict =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4"
      + " /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>"
      + " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    // ==================================================================
    //  Harness
    // ==================================================================

    private static PdfUa2ValidationResult Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUa2Checker.Validate(ms);
    }

    // ==================================================================
    //  Positive / negative pairs
    // ==================================================================

    [Fact]
    public void Validate_PartTwoAndFourDigitRev_ReportsNoViolations()
    {
        var result = Validate(BuildDocWithMetadata(XmpPacket("2", "2024")));

        Assert.Empty(result.Violations);
        Assert.True(result.IsConforming);
    }

    /// <summary>
    /// XMP Part 1 §7.9.2.2 lets a simple property be written as an attribute of the
    /// <c>rdf:Description</c> rather than as a child element; the identifier is read either way.
    /// </summary>
    [Fact]
    public void Validate_PartAndRevDeclaredAsAttributes_ReportsNoViolations()
    {
        const string packet =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n" +
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n" +
            " <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n" +
            "  <rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"" +
            " pdfuaid:part=\"2\" pdfuaid:rev=\"2024\"/>\n" +
            " </rdf:RDF>\n" +
            "</x:xmpmeta>\n" +
            "<?xpacket end=\"w\"?>";

        var result = Validate(BuildDocWithMetadata(packet));

        Assert.Empty(result.Violations);
    }

    [Fact]
    public void Validate_NoMetadataStream_ReportsUa2_5()
    {
        var result = Validate(BuildDocWithoutMetadata());

        Assert.Contains(result.Violations, v => v.Code == "UA2-5");
    }

    [Fact]
    public void Validate_PartOne_ReportsUa2_5NamingFoundValue()
    {
        var result = Validate(BuildDocWithMetadata(XmpPacket("1", "2024")));

        var violation = Assert.Single(result.Violations, v => v.Code == "UA2-5");
        Assert.Contains("\"1\"", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_MissingRev_ReportsUa2_5()
    {
        var result = Validate(BuildDocWithMetadata(XmpPacket("2", null)));

        var violation = Assert.Single(result.Violations, v => v.Code == "UA2-5");
        Assert.Contains("rev", violation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("24")]      // too short
    [InlineData("20245")]   // too long
    [InlineData("20-4")]    // not all digits
    public void Validate_RevNotAFourDigitYear_ReportsUa2_5(string badRev)
    {
        var result = Validate(BuildDocWithMetadata(XmpPacket("2", badRev)));

        Assert.Contains(result.Violations, v => v.Code == "UA2-5");
    }

    [Fact]
    public void Validate_BothPartAndRevWrong_ReportsTwoSeparateUa2_5Findings()
    {
        var result = Validate(BuildDocWithMetadata(XmpPacket("1", "24")));

        // One for the part, one for the rev — each independently actionable, matching
        // PdfAValidator's A07 precedent of reporting each sub-check of one clause separately.
        var findings = result.Violations;
        Assert.Equal(2, findings.Count);
        Assert.All(findings, v => Assert.Equal("UA2-5", v.Code));
    }

    // ==================================================================
    //  Encryption-aware handling, mirroring PdfUaChecker's UA03/UA99 split
    // ==================================================================

    [Fact]
    public void Validate_EncryptedWithUnreadableXmp_DoesNotAssertUa2_5()
    {
        // Ciphertext is what a checker without the key sees; any non-XML byte run models it.
        var result = Validate(BuildDocWithMetadata("this packet is not XML", encrypted: true));

        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-5");
    }

    [Fact]
    public void Validate_EncryptedWithUnreadableXmp_ReportsUa2_99Unverifiable()
    {
        var result = Validate(BuildDocWithMetadata("this packet is not XML", encrypted: true));

        var unverified = Assert.Single(result.Violations, v => v.Code == "UA2-99");
        Assert.Contains("encrypted", unverified.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_UnencryptedWithUnreadableXmp_ReportsUa2_5Outright()
    {
        var result = Validate(BuildDocWithMetadata("this packet is not XML", encrypted: false));

        Assert.Contains(result.Violations, v => v.Code == "UA2-5");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-99");
    }

    // ==================================================================
    //  Indirect references must resolve
    // ==================================================================

    [Fact]
    public void Validate_IndirectMetadataStream_StillReadsPartAndRev()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            StreamObject("/Type /Metadata /Subtype /XML", XmpPacket("2", "2024")));

        Assert.Empty(Validate(pdf).Violations);
    }

    // ==================================================================
    //  6.1 — the file version is 2.0, in the header or the catalog
    // ==================================================================

    /// <summary>The same header length, so every offset stays right.</summary>
    private static byte[] AsVersion17(byte[] pdf)
    {
        string text = Encoding.ASCII.GetString(pdf);
        Assert.StartsWith("%PDF-2.0", text, StringComparison.Ordinal);
        return Encoding.ASCII.GetBytes("%PDF-1.7" + text.Substring(8));
    }

    [Fact]
    public void Validate_APdf17File_IsRefusedForItsVersionAlone()
    {
        var result = Validate(AsVersion17(BuildDocWithMetadata(XmpPacket("2", "2024"))));

        PdfUa2Violation version = Assert.Single(result.Violations);
        Assert.Equal("UA2-6.1", version.Code);
        Assert.Contains("1.7", version.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_APdf17HeaderWithTheCatalogsVersion20_IsAPdf20File()
    {
        byte[] pdf = AsVersion17(BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R /Version /2.0 >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            StreamObject("/Type /Metadata /Subtype /XML", XmpPacket("2", "2024"))));

        Assert.Empty(Validate(pdf).Violations);
    }

    /// <summary>
    /// The same on an encrypted document: its loaded version is the header's (the load stops at
    /// /Encrypt before the catalog raises it), so a 1.7 header with a catalog /Version /2.0 was reported
    /// as a 1.7 file. Names are not encrypted, and the catalog's /Version is read. A 1.7 file whose
    /// catalog says nothing more is still refused (the control).
    /// </summary>
    [Fact]
    public void Validate_AnEncryptedPdf17HeaderWithTheCatalogsVersion20_IsAPdf20File()
    {
        const string encrypt = " /Encrypt << /Filter /Standard /V 5 /R 6 /Length 256 /P -4 /O <00> /U <00> /OE <00> /UE <00> /Perms <00> >>";
        byte[] raised = AsVersion17(BuildPdfWithTrailer(encrypt,
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R /Version /2.0 >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            StreamObject("/Type /Metadata /Subtype /XML", XmpPacket("2", "2024"))));
        Assert.DoesNotContain(Validate(raised).Violations, v => v.Code == "UA2-6.1");

        byte[] plain = AsVersion17(BuildPdfWithTrailer(encrypt,
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            StreamObject("/Type /Metadata /Subtype /XML", XmpPacket("2", "2024"))));
        Assert.Contains(Validate(plain).Violations, v => v.Code == "UA2-6.1");
    }
}
