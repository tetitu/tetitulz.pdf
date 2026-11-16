// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Tests for PDF/A-4 validation (GAP: Feature 6).
/// All tests build minimal in-memory PDFs — no disk files required.
/// </summary>
public class PdfA4ValidatorTests
{
    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal PDF with the given version header and optional XMP stream.
    /// Returns a byte array that PdfDocument.Open can parse.
    /// </summary>
    private static byte[] BuildPdf(
        string versionHeader = "%PDF-2.0",
        string xmpContent = null,
        bool addNeedsRendering = false,
        bool addRequirements = false,
        bool addEmbeddedFileWithoutAf = false,
        string catalogExtra = null,
        string contentExtra = null)
    {
        var sb = new StringBuilder();
        sb.Append(versionHeader + "\n").Append("%\u00E2\u00E3\u00CF\u00D3\n");   // with ISO 19005 6.1.2's binary comment

        // Objects layout:
        //   1 = catalog
        //   2 = pages
        //   3 = page
        //   4 = content stream
        //   5 = font (Type1)
        //   6 = font descriptor (with FontFile3)
        //   7 = fake CFF stream (satisfies A06)
        //   8 = XMP metadata (optional)
        //   9 = AcroForm (optional, for NeedsRendering test)
        //  10 = EmbeddedFiles spec (optional)

        int nextObj = 8;
        int metaObjNum = -1;
        int acroFormObjNum = -1;
        int efSpecObjNum = -1;

        if (xmpContent != null) metaObjNum = nextObj++;
        if (addNeedsRendering) acroFormObjNum = nextObj++;
        if (addEmbeddedFileWithoutAf) efSpecObjNum = nextObj++;

        var offsets = new System.Collections.Generic.Dictionary<int, int>();

        // obj 1 — catalog
        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<<\n /Type /Catalog\n /Pages 2 0 R\n");
        if (metaObjNum > 0)   sb.AppendFormat(" /Metadata {0} 0 R\n", metaObjNum);
        if (acroFormObjNum > 0) sb.AppendFormat(" /AcroForm {0} 0 R\n", acroFormObjNum);
        if (addRequirements)  sb.Append(" /Requirements []\n");
        if (catalogExtra != null) sb.Append(' ').Append(catalogExtra).Append('\n');
        if (efSpecObjNum > 0)
            sb.AppendFormat(
                " /Names << /EmbeddedFiles << /Names [(test.txt) {0} 0 R] >> >>\n",
                efSpecObjNum);
        sb.Append(">>\nendobj\n");

        // obj 2 — pages
        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // obj 3 — page
        offsets[3] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >>" +
                  " /ColorSpace << /CS0 [/CalRGB << /WhitePoint [0.9505 1 1.089] >>] >> >> >>\nendobj\n");

        // obj 4 — content stream. The text is painted in a calibrated space: in the initial DeviceGray,
        // with no output intent, it would not conform (ISO 19005-4 6.2.4.3).
        string cs = "/CS0 cs 0 0 0 sc BT /F1 12 Tf 100 700 Td (Hi) Tj ET" + (contentExtra ?? "");
        offsets[4] = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        // obj 5 — font with FontDescriptor (satisfies A06)
        offsets[5] = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica" +
                  " /FontDescriptor 6 0 R >>\nendobj\n");

        // obj 6 — font descriptor pointing to FontFile3
        offsets[6] = sb.Length;
        sb.Append("6 0 obj\n<< /Type /FontDescriptor /FontName /Helvetica" +
                  " /Flags 32 /FontFile3 7 0 R >>\nendobj\n");

        // obj 7 — minimal fake font data stream (just 1 byte)
        offsets[7] = sb.Length;
        sb.Append("7 0 obj\n<< /Length 1 /Subtype /Type1C >>\nstream\n \nendstream\nendobj\n");

        // obj 8+ — XMP metadata
        if (metaObjNum > 0 && xmpContent != null)
        {
            offsets[metaObjNum] = sb.Length;
            sb.AppendFormat(
                "{0} 0 obj\n<< /Type /Metadata /Subtype /XML /Length {1} >>\nstream\n",
                metaObjNum, xmpContent.Length);
            sb.Append(xmpContent);
            sb.Append("\nendstream\nendobj\n");
        }

        // obj — AcroForm with NeedsRendering
        if (acroFormObjNum > 0)
        {
            offsets[acroFormObjNum] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n<< /Fields [] /NeedsRendering true >>\nendobj\n",
                acroFormObjNum);
        }

        // obj — embedded file spec without /AFRelationship (A4-05 trigger)
        if (efSpecObjNum > 0)
        {
            offsets[efSpecObjNum] = sb.Length;
            sb.AppendFormat("{0} 0 obj\n<< /Type /Filespec /F (test.txt) >>\nendobj\n",
                efSpecObjNum);
        }

        // Build xref + trailer
        int totalObjs = nextObj - 1;
        int xrefOffset = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n", totalObjs + 1);
        sb.Append("0000000000 65535 f \n");
        for (int i = 1; i <= totalObjs; i++)
        {
            if (offsets.TryGetValue(i, out int off))
                sb.AppendFormat("{0:D10} 00000 n \n", off);
            else
                sb.Append("0000000000 65535 f \n");
        }

        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R /ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n", totalObjs + 1);
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xrefOffset);

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string MakeXmp(string part, string conformance, string rev = null)
    {
        var revAttr = rev != null ? $"\n    <pdfaid:rev>{rev}</pdfaid:rev>" : "";
        return $@"<?xpacket begin="""" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/"">
  <rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
    <rdf:Description xmlns:pdfaid=""http://www.aiim.org/pdfa/ns/id/"">
      <pdfaid:part>{part}</pdfaid:part>
      {(conformance == null ? "" : $"<pdfaid:conformance>{conformance}</pdfaid:conformance>")}{revAttr}
    </rdf:Description>
  </rdf:RDF>
</x:xmpmeta>
<?xpacket end=""w""?>";
    }

    // ------------------------------------------------------------------
    //  PDF/A-4: valid document
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_Part4_ValidDocument_NoViolations()
    {
        // Plain PDF/A-4: a document declaring PDF/A-4f embeds a file (A67, the published profiles rule 6.9-5).
        string xmp = MakeXmp("4", null, "2020");
        byte[] pdf = BuildPdf("%PDF-2.0", xmp);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.True(result.IsConformant, string.Join("; ", result.Violations));
    }

    // ------------------------------------------------------------------
    //  A4-06 — a name's bytes are UTF-8 (6.1.7)
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>/Rectangle#c2</c> ends on a lone UTF-8 lead byte, as the published profile's 6-1-7-t01-fail-d has it: A4-06,
    /// naming the name as the file spelled it. The control, <c>/Caf#C3#A9</c>, is UTF-8 for "Café".
    /// </summary>
    [Fact]
    public void Validate_Part4_ANameWhoseBytesAreNotUtf8_A406Violation()
    {
        string xmp = MakeXmp("4", null, "2020");
        var bad = PdfAValidator.Validate(new MemoryStream(BuildPdf("%PDF-2.0", xmp, catalogExtra: "/Probe /Rectangle#c2")), PdfAConformancePart.Part4);
        var good = PdfAValidator.Validate(new MemoryStream(BuildPdf("%PDF-2.0", xmp, catalogExtra: "/Probe /Caf#C3#A9")), PdfAConformancePart.Part4);

        var violation = Assert.Single(bad.Violations, v => v.Code == "A4-06");
        Assert.Contains("/Rectangle#C2", violation.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(good.Violations, v => v.Code == "A4-06");
        Assert.True(good.IsConformant, string.Join("; ", good.Violations));
    }

    /// <summary>The report spells the name as the file did: a space and a delimiter are written <c>#XX</c> as well.</summary>
    [Fact]
    public void Validate_Part4_TheReportSpellsTheNameAsTheFileDid()
    {
        string xmp = MakeXmp("4", null, "2020");
        var result = PdfAValidator.Validate(new MemoryStream(BuildPdf("%PDF-2.0", xmp, catalogExtra: "/Probe /A#20#28#E9")), PdfAConformancePart.Part4);

        Assert.Contains(result.Violations, v => v.Code == "A4-06" && v.Message.Contains("/A#20#28#E9", StringComparison.Ordinal));
    }

    /// <summary>A key is a name too: <c>/Na#E9me</c> as a dictionary key is reported.</summary>
    [Fact]
    public void Validate_Part4_AKeyWhoseBytesAreNotUtf8_A406Violation()
    {
        string xmp = MakeXmp("4", null, "2020");
        var result = PdfAValidator.Validate(new MemoryStream(BuildPdf("%PDF-2.0", xmp, catalogExtra: "/Na#E9me 1")), PdfAConformancePart.Part4);

        Assert.Contains(result.Violations, v => v.Code == "A4-06" && v.Message.Contains("/Na#E9me", StringComparison.Ordinal));
    }

    /// <summary>A drawn operand is a name too: a marked-content tag <c>/T#FF</c> in the page's content.</summary>
    [Fact]
    public void Validate_Part4_AnOperandWhoseBytesAreNotUtf8_A406Violation()
    {
        string xmp = MakeXmp("4", null, "2020");
        var result = PdfAValidator.Validate(new MemoryStream(BuildPdf("%PDF-2.0", xmp, contentExtra: " /T#FF BMC EMC")), PdfAConformancePart.Part4);

        Assert.Contains(result.Violations, v => v.Code == "A4-06" && v.Message.Contains("/T#FF", StringComparison.Ordinal));
    }

    /// <summary>The rule is PDF/A-4's: the same file declared PDF/A-2 is not judged by it.</summary>
    [Fact]
    public void Validate_Part2_ANameWhoseBytesAreNotUtf8_IsNotA406()
    {
        string xmp = MakeXmp("2", "B");
        var result = PdfAValidator.Validate(new MemoryStream(BuildPdf("%PDF-1.7", xmp, catalogExtra: "/Probe /Rectangle#c2")), PdfAConformancePart.Part2);

        Assert.DoesNotContain(result.Violations, v => v.Code == "A4-06");
    }

    // ------------------------------------------------------------------
    //  A4-01 — PDF version must be 2.0
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_Part4_WrongVersion_A401Violation()
    {
        string xmp = MakeXmp("4", "F", "2020");
        byte[] pdf = BuildPdf("%PDF-1.7", xmp);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.Contains(result.Violations, v => v.Code == "A4-01");
    }

    [Fact]
    public void Validate_Part4_Version14_A401Violation()
    {
        string xmp = MakeXmp("4", "F", "2020");
        byte[] pdf = BuildPdf("%PDF-1.4", xmp);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.Contains(result.Violations, v => v.Code == "A4-01");
    }

    // ------------------------------------------------------------------
    //  A4-02 — /NeedsRendering must not be present
    // ------------------------------------------------------------------

    /// <summary>
    /// The catalog's <c>/NeedsRendering true</c> (the published profiles rule 6.4.2-2, tested as <c>NeedsRendering == false</c>): a
    /// violation; <c>false</c> is not one, nor is the key in the AcroForm dictionary, where the check looked until
    /// 2026-10-20 and ISO 32000 puts no such key.
    /// </summary>
    [Theory]
    [InlineData("/NeedsRendering true", false, true)]
    [InlineData("/NeedsRendering false", false, false)]
    [InlineData(null, true, false)]
    public void Validate_Part4_NeedsRendering_A402Violation(string catalogExtra, bool inAcroForm, bool violates)
    {
        string xmp = MakeXmp("4", "F", "2020");
        byte[] pdf = BuildPdf("%PDF-2.0", xmp, addNeedsRendering: inAcroForm, catalogExtra: catalogExtra);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.Equal(violates, result.Violations.Any(v => v.Code == "A4-02"));
    }

    // ------------------------------------------------------------------
    //  A4-03 — /Requirements must not be present
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_Part4_Requirements_A403Violation()
    {
        string xmp = MakeXmp("4", "F", "2020");
        byte[] pdf = BuildPdf("%PDF-2.0", xmp, addRequirements: true);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.Contains(result.Violations, v => v.Code == "A4-03");
    }

    // ------------------------------------------------------------------
    //  A4-04 — pdfaid:rev="2020" required
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_Part4_MissingRev_A404Violation()
    {
        string xmp = MakeXmp("4", "F"); // no rev
        byte[] pdf = BuildPdf("%PDF-2.0", xmp);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.Contains(result.Violations, v => v.Code == "A4-04");
    }

    [Fact]
    public void Validate_Part4_WrongRev_A404Violation()
    {
        string xmp = MakeXmp("4", "F", "2017");
        byte[] pdf = BuildPdf("%PDF-2.0", xmp);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.Contains(result.Violations, v => v.Code == "A4-04");
    }

    /// <summary>
    /// PDF/A-4's <c>pdfaid:conformance</c> is absent, <c>E</c> or <c>F</c> (the published profiles rule 6.7.3-3 in
    /// the PDF/A-4, -4e and -4f profiles); any other letter is an A07 — the conformance corpus fails one
    /// (6-7-3-t01-fail-i), and the validator passed it.
    /// </summary>
    [Theory]
    [InlineData(null, false)]
    [InlineData("E", false)]
    [InlineData("F", false)]
    [InlineData("B", true)]
    [InlineData("Z", true)]
    public void Validate_Part4_ConformanceLetterIsEOrF(string conformance, bool reportsA07)
    {
        byte[] pdf = BuildPdf("%PDF-2.0", MakeXmp("4", conformance, "2020"));
        var a07 = PdfAValidator.Validate(new MemoryStream(pdf), PdfAConformancePart.Part4)
            .Violations.Where(v => v.Code == "A07").ToArray();

        Assert.Equal(reportsA07, a07.Length > 0);
        if (reportsA07) Assert.Contains("not valid for PDF/A-4", a07[0].Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------
    //  A07 — XMP must declare correct part
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_Part4_WrongXmpPart_A07Violation()
    {
        string xmp = MakeXmp("3", "B", "2020"); // wrong part
        byte[] pdf = BuildPdf("%PDF-2.0", xmp);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.Contains(result.Violations, v => v.Code == "A07");
    }

    [Fact]
    public void Validate_Part4_MissingXmp_A07Violation()
    {
        byte[] pdf = BuildPdf("%PDF-2.0", xmpContent: null);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.Contains(result.Violations, v => v.Code == "A07");
    }

    // ------------------------------------------------------------------
    //  PDF/A-4 allows embedded files (unlike PDF/A-1/2) — no A03 raised
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_Part4_EmbeddedFileAllowed_NoA03Violation()
    {
        // PDF/A-4 allows embedded files; only A4-05 is raised if /AFRelationship is missing.
        // A03 must NOT be raised for Part4.
        string xmp = MakeXmp("4", "F", "2020");
        byte[] pdf = BuildPdf("%PDF-2.0", xmp);
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part4);
        Assert.DoesNotContain(result.Violations, v => v.Code == "A03");
    }

    // ------------------------------------------------------------------
    //  Enum values
    // ------------------------------------------------------------------

    [Fact]
    public void PdfAConformancePart_Part4_HasValue4()
    {
        Assert.Equal(4, (int)PdfAConformancePart.Part4);
    }

    // ------------------------------------------------------------------
    //  Backwards-compatibility: Part1/2/3 still work after adding Part4
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_Part1_StillDetectsJavaScript()
    {
        // A document with a /Names /JavaScript entry should fail Part1 validation.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R" +
                  " /Names << /JavaScript << /Names [(script) 3 0 R] >> >> >>\nendobj\n");
        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /S /JavaScript /JS (app.alert('x');) >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 4\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.Append("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part1);
        Assert.Contains(result.Violations, v => v.Code == "A02");
    }

    [Fact]
    public void Validate_Part3_NoA03ForEmbeddedFiles()
    {
        // Part3 allows embedded files — A03 must not fire.
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R" +
                  " /Names << /EmbeddedFiles << /Names [] >> >> >>\nendobj\n");
        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 3\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.Append("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());
        using var ms = new MemoryStream(pdf);
        var result = PdfAValidator.Validate(ms, PdfAConformancePart.Part3);
        Assert.DoesNotContain(result.Violations, v => v.Code == "A03");
    }
}
