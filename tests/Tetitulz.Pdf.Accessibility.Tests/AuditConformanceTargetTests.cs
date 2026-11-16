// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfAccessibility.Audit(Stream, AuditOptions)"/> judges a document against the PDF/UA
/// part the caller names — PDF/UA-1 unless told otherwise, so nothing moves — or, under
/// <see cref="PdfUaVersion.Auto"/>, against the part the document itself declares (GAPS.md §22d).
/// </summary>
/// <remarks>
/// <para><b>A UA-2 document judged against UA-1 is wrong twice.</b> It is told its
/// <c>pdfuaid:part</c> must be 1 (UA03), and none of ISO 14289-2's own clauses are checked. Under
/// <see cref="PdfUaVersion.Ua2"/> the audit runs <c>Plugins.Conformance.PdfUa2Checker</c> (Clause 5,
/// <c>UA2-5</c>) and the deep <c>PdfUa2StructureChecker</c> (<c>UA2-8.x</c>) instead, through the
/// same report shape.</para>
///
/// <para>The fixture is a tagged PDF 2.0 document whose structure tree is one <c>Document</c> element
/// in the PDF 2.0 namespace. It violates two UA-2 clauses on purpose — no <c>/Lang</c> (§8.4.4) and
/// no <c>/ViewerPreferences /DisplayDocTitle</c> (§8.11.2) — so a UA-2 audit always has something
/// definite to report; judged against UA-1 the same omissions are UA02 and UA04.</para>
/// </remarks>
public class AuditConformanceTargetTests
{
    private const string Pdf2Namespace = "http://iso.org/pdf2/ssn";

    /// <summary>The PDF/UA-1 codes: <c>UA</c> and two digits, never the <c>UA2-</c> prefix.</summary>
    private static readonly Regex Ua1Code = new Regex(@"^UA\d\d$", RegexOptions.CultureInvariant);

    // =====================================================================
    //  The default, and an explicit target
    // =====================================================================

    [Fact]
    public void Audit_DefaultTarget_IsUa1_SoAUa2DocumentIsJudgedAgainstUa1()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(Document(XmpPacket("2", "2024"))));

        Assert.Equal(PdfUaVersion.Ua1, report.ConformanceTarget);
        Assert.Contains(report.Issues, i => i.Code == "UA03");                       // part is not 1
        Assert.DoesNotContain(report.Issues, i => i.Code.StartsWith("UA2-", StringComparison.Ordinal));
    }

    [Fact]
    public void Audit_NullOptions_BehaveAsTheDefault()
    {
        var report = PdfAccessibility.Audit(new MemoryStream(Document(XmpPacket("2", "2024"))), null);

        Assert.Equal(PdfUaVersion.Ua1, report.ConformanceTarget);
        Assert.Contains(report.Issues, i => i.Code == "UA03");
    }

    [Fact]
    public void Audit_Ua2Target_RunsBothUa2CheckersAndNoUa1Check()
    {
        var report = PdfAccessibility.Audit(
            new MemoryStream(Document(XmpPacket("2", "2024"))),
            new AuditOptions { Target = PdfUaVersion.Ua2 });

        Assert.Equal(PdfUaVersion.Ua2, report.ConformanceTarget);

        // The deep structure checker ran: the missing /Lang is §8.4.4, and it blocks conformance.
        var lang = Assert.Single(report.Issues, i => i.Code == "UA2-8.4.4");
        Assert.Equal(IssueSeverity.Error, lang.Severity);
        Assert.Equal(IssueCategory.Language, lang.Category);
        Assert.NotEqual(string.Empty, lang.Remediation);

        // The Clause 5 checker ran and found the identifier well-formed.
        Assert.DoesNotContain(report.Issues, i => i.Code == "UA2-5");

        // And nothing was judged against PDF/UA-1.
        Assert.DoesNotContain(report.Issues, i => Ua1Code.IsMatch(i.Code));
        Assert.False(report.IsConforming);
    }

    /// <summary>
    /// The structure checker lists 25 Sound annotations and sums up the other five as "…and 5 more".
    /// The audit counts that summary as its five instances, so the total is the thirty in the file.
    /// It used to count it as one.
    /// </summary>
    [Fact]
    public void Audit_Ua2Target_CountsTheInstancesASummaryNames()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [" +
                string.Join(" ", Enumerable.Range(4, 30).Select(n => n + " 0 R")) + "] >>",
        };
        for (int i = 0; i < 30; i++) bodies.Add("<< /Type /Annot /Subtype /Sound /Rect [0 0 10 10] >>");

        var report = PdfAccessibility.Audit(
            new MemoryStream(PdfUa2StructureCheckerTests.BuildPdf(bodies)),
            new AuditOptions { Target = PdfUaVersion.Ua2 });

        var sound = report.Issues.Where(i => i.Code == "UA2-8.9.2.4.11").ToList();
        Assert.Equal(26, sound.Count);
        Assert.Equal(5, sound.Single(i => i.InstanceCount.HasValue).InstanceCount);
        Assert.Equal(30, sound.Sum(i => i.InstanceCount ?? 1));
    }

    [Fact]
    public void Audit_Ua2Target_ReportsAPartOneDeclarationAsAClause5Error()
    {
        var report = PdfAccessibility.Audit(
            new MemoryStream(Document(XmpPacket("1", null))),
            new AuditOptions { Target = PdfUaVersion.Ua2 });

        var clause5 = Assert.Single(report.Issues, i => i.Code == "UA2-5" && i.Message.Contains("part"));
        Assert.Equal(IssueSeverity.Error, clause5.Severity);
        Assert.Equal(IssueCategory.Metadata, clause5.Category);
        Assert.DoesNotContain(report.Issues, i => i.Code == "UA03");
    }

    /// <summary>
    /// <c>UA2-99</c> says a check did not run. Like the plugin's UA99 it maps to Info, so an
    /// incomplete report does not condemn the document on its own.
    /// </summary>
    [Fact]
    public void Audit_Ua2Target_MapsTheIncompleteCheckCodeToInfo()
    {
        var report = PdfAccessibility.Audit(
            new MemoryStream(Document("this packet is not XML", encrypted: true)),
            new AuditOptions { Target = PdfUaVersion.Ua2 });

        // Two notices: the unreadable packet, and the structure checks not run on an encrypted file.
        Assert.Contains(report.Issues, i => i.Code == "UA2-99" && i.Message.Contains("encrypted"));
        Assert.All(System.Linq.Enumerable.Where(report.Issues, i => i.Code == "UA2-99"),
                   i => Assert.Equal(IssueSeverity.Info, i.Severity));
    }

    [Fact]
    public void Audit_PathOverload_HonoursTheTarget()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, Document(XmpPacket("2", "2024")));

            var report = PdfAccessibility.Audit(path, new AuditOptions { Target = PdfUaVersion.Ua2 });

            Assert.Equal(PdfUaVersion.Ua2, report.ConformanceTarget);
            Assert.Contains(report.Issues, i => i.Code == "UA2-8.4.4");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // =====================================================================
    //  Auto: the document's own declaration decides
    // =====================================================================

    [Theory]
    [InlineData(false)]   // <pdfuaid:part>2</pdfuaid:part>
    [InlineData(true)]    // pdfuaid:part="2" on the rdf:Description
    public void Audit_AutoTarget_DetectsADeclaredPartTwoAsUa2(bool asAttributes)
    {
        var report = PdfAccessibility.Audit(
            new MemoryStream(Document(XmpPacket("2", "2024", asAttributes))),
            new AuditOptions { Target = PdfUaVersion.Auto });

        Assert.Equal(PdfUaVersion.Ua2, report.ConformanceTarget);
        Assert.Contains(report.Issues, i => i.Code == "UA2-8.4.4");
        Assert.DoesNotContain(report.Issues, i => Ua1Code.IsMatch(i.Code));
    }

    [Fact]
    public void Audit_AutoTarget_JudgesADeclaredPartOneAgainstUa1()
    {
        var report = PdfAccessibility.Audit(
            new MemoryStream(Document(XmpPacket("1", null))),
            new AuditOptions { Target = PdfUaVersion.Auto });

        Assert.Equal(PdfUaVersion.Ua1, report.ConformanceTarget);
        Assert.Contains(report.Issues, i => Ua1Code.IsMatch(i.Code));                // judged by PDF/UA-1's codes
        Assert.DoesNotContain(report.Issues, i => i.Code == "UA03");                 // part is 1
        Assert.DoesNotContain(report.Issues, i => i.Code.StartsWith("UA2-", StringComparison.Ordinal));
    }

    /// <summary>
    /// The declaration is read no further than the XMP packet ceiling (2 MB). The packet was decoded
    /// whole, bounded only by the per-stream ceiling sized for images, so a few kilobytes of flate
    /// were inflated in full — and read: this one, past the ceiling only by its padding, was judged
    /// against the part 2 it declares. Refused, the audit runs against PDF/UA-1.
    /// </summary>
    [Fact]
    public void Audit_AutoTarget_AdversarialPacketPastTheCeiling_IsNotReadForItsDeclaration()
    {
        var report = PdfAccessibility.Audit(
            new MemoryStream(Document(Padded(XmpPacket("2", "2024"), 3 * 1024 * 1024), flated: true)),
            new AuditOptions { Target = PdfUaVersion.Auto });

        Assert.Equal(PdfUaVersion.Ua1, report.ConformanceTarget);
        Assert.Contains(report.Issues, i => i.Code == "UA03"
            && i.Message.Contains("could not be decoded", StringComparison.Ordinal));
    }

    /// <summary>The control: the same packet, flated and padded under the ceiling, is read.</summary>
    [Fact]
    public void Audit_AutoTarget_AFlatedPacketUnderTheCeiling_IsReadForItsDeclaration()
    {
        var report = PdfAccessibility.Audit(
            new MemoryStream(Document(Padded(XmpPacket("2", "2024"), 1024 * 1024), flated: true)),
            new AuditOptions { Target = PdfUaVersion.Auto });

        Assert.Equal(PdfUaVersion.Ua2, report.ConformanceTarget);
    }

    [Fact]
    public void Audit_AutoTarget_JudgesADocumentWithNoDeclarationAgainstUa1()
    {
        var report = PdfAccessibility.Audit(
            new MemoryStream(PdfAccessibilityAuditTests.TaggedPdf()),
            new AuditOptions { Target = PdfUaVersion.Auto });

        Assert.Equal(PdfUaVersion.Ua1, report.ConformanceTarget);
        Assert.Contains(report.Issues, i => i.Code == "UA03");                       // no XMP at all
        Assert.DoesNotContain(report.Issues, i => i.Code.StartsWith("UA2-", StringComparison.Ordinal));
    }

    // =====================================================================
    //  Option defaults
    // =====================================================================

    [Fact]
    public void AuditOptions_DefaultToUa1AndTheDeepCheckersCeilings()
    {
        var options = new AuditOptions();

        Assert.Equal(PdfUaVersion.Ua1, options.Target);
        Assert.Equal(PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes, options.MaxPageContentBytes);
        Assert.Equal(PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes, options.MaxDocumentContentBytes);
    }

    // =====================================================================
    //  Fixtures
    // =====================================================================

    /// <summary>A well-formed XMP packet declaring <c>pdfuaid:part</c> and, when given, <c>pdfuaid:rev</c>.</summary>
    private static string XmpPacket(string part, string rev, bool asAttributes = false)
    {
        var sb = new StringBuilder();
        sb.Append("<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>\n");
        sb.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">\n");
        sb.Append(" <rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">\n");
        sb.Append("  <rdf:Description rdf:about=\"\" xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\"");
        if (asAttributes)
        {
            sb.Append(" pdfuaid:part=\"").Append(part).Append('"');
            if (rev != null) sb.Append(" pdfuaid:rev=\"").Append(rev).Append('"');
            sb.Append("/>\n");
        }
        else
        {
            sb.Append(">\n");
            sb.Append("   <pdfuaid:part>").Append(part).Append("</pdfuaid:part>\n");
            if (rev != null) sb.Append("   <pdfuaid:rev>").Append(rev).Append("</pdfuaid:rev>\n");
            sb.Append("  </rdf:Description>\n");
        }
        sb.Append(" </rdf:RDF>\n");
        sb.Append("</x:xmpmeta>\n");
        sb.Append("<?xpacket end=\"w\"?>");
        return sb.ToString();
    }

    /// <summary><paramref name="xmp"/> with whitespace before its trailer, to <paramref name="length"/> characters.</summary>
    private static string Padded(string xmp, int length)
    {
        int trailer = xmp.LastIndexOf("<?xpacket end", StringComparison.Ordinal);
        return xmp.Insert(trailer, new string(' ', Math.Max(0, length - xmp.Length)));
    }

    /// <summary>
    /// /P -4 sets every permission bit, so this models "streams are ciphertext" without any real key
    /// derivation — the checkers only need <c>doc.IsEncrypted</c> to be true.
    /// </summary>
    private const string EncryptDict =
        "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4"
      + " /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>"
      + " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";

    /// <summary>
    /// Object 1 catalog, 2 struct tree root, 3 pages, 4 page, 5 the Document element, 6 the PDF 2.0
    /// namespace, 7 the XMP stream, and 8 the encryption dictionary when <paramref name="encrypted"/>.
    /// </summary>
    private static byte[] Document(string xmp, bool encrypted = false, bool flated = false)
    {
        string filter = string.Empty;
        if (flated)
        {
            using var output = new MemoryStream();
            using (var z = new System.IO.Compression.ZLibStream(
                       output, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                z.Write(Encoding.ASCII.GetBytes(xmp));
            xmp = Encoding.Latin1.GetString(output.ToArray());
            filter = "/Filter /FlateDecode ";
        }

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /MarkInfo << /Marked true >> /Metadata 7 0 R >>",
            "<< /Type /StructTreeRoot /K [5 0 R] >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 6 0 R >>",
            $"<< /Type /Namespace /NS ({Pdf2Namespace}) >>",
            $"<< /Type /Metadata /Subtype /XML {filter}/Length {xmp.Length.ToString(CultureInfo.InvariantCulture)} >>\n" +
            $"stream\n{xmp}\nendstream",
        };
        if (encrypted) bodies.Add(EncryptDict);

        var sb = new StringBuilder();
        sb.Append("%PDF-2.0\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(encrypted ? " /Encrypt 8 0 R" : string.Empty).Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
