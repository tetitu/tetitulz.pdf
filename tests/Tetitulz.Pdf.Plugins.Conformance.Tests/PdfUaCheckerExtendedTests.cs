// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Plugins.Conformance;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Tests for the document-level PDF/UA checks: UA01 (tagging), UA06 (encryption
/// permissions), UA08 (form field tooltips), UA09 (tab order), UA25 (font embedding),
/// UA26 (Unicode mapping) and UA27 (annotation descriptions).
/// </summary>
public class PdfUaCheckerExtendedTests
{
    // ------------------------------------------------------------------
    //  Generic builder — objects are numbered 1..N in the order supplied.
    // ------------------------------------------------------------------

    private static byte[] BuildPdf(params string[] objects)
        => BuildPdfWithTrailer(string.Empty, objects);

    private static byte[] BuildPdfWithTrailer(string extraTrailerEntries, params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

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

    private static PdfUaValidationResult Validate(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfUaChecker.Validate(ms);
    }

    private static byte[] BuildPdfWithAnnotatedPage()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int obj3 = sb.Length;
        // Page with an annotation but no /Tabs
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Annots [4 0 R] >>\nendobj\n");
        int obj4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// ISO 32000-1 Table 30 names the page tab-order key <c>/Tabs</c>. (<c>/TabOrder</c>,
    /// which this checker used to demand, is not a PDF key at all.)
    /// </summary>
    private static byte[] BuildPdfWithAnnotatedPageAndTabs(string tabKey = "/Tabs")
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int obj3 = sb.Length;
        sb.AppendFormat("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                        " /Annots [4 0 R] {0} /S >>\nendobj\n", tabKey);
        int obj4 = sb.Length;
        sb.Append("4 0 obj\n<< /Type /Annot /Subtype /Widget /Rect [0 0 100 20] >>\nendobj\n");
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdfWithFormField(bool withTooltip)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>\nendobj\n");
        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        int obj4 = sb.Length;
        string tuEntry = withTooltip ? " /TU (My Field)" : "";
        sb.AppendFormat("4 0 obj\n<< /FT /Tx /T (field1){0} >>\nendobj\n", tuEntry);
        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ------------------------------------------------------------------
    //  UA08 — form field tooltips
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_FormFieldMissingTooltip_UA08Violation()
    {
        byte[] pdf = BuildPdfWithFormField(withTooltip: false);
        using var ms = new MemoryStream(pdf);
        var result = PdfUaChecker.Validate(ms);
        Assert.Contains(result.Violations, v => v.Code == "UA08");
    }

    [Fact]
    public void Validate_FormFieldWithTooltip_NoUA08Violation()
    {
        byte[] pdf = BuildPdfWithFormField(withTooltip: true);
        using var ms = new MemoryStream(pdf);
        var result = PdfUaChecker.Validate(ms);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA08");
    }

    [Fact]
    public void Validate_NoAcroForm_NoUA08Violation()
    {
        // PDF without any form — UA08 should not fire
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");
        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA08");
    }

    /// <summary>
    /// /Fields (like every other dictionary value) may be stored indirectly. A bare cast
    /// silently skipped the whole form and reported a clean bill of health.
    /// </summary>
    [Fact]
    public void Validate_IndirectFieldsArray_StillDetectsMissingTooltip()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Fields 5 0 R >>",
            "[6 0 R]",
            "<< /FT /Tx /T (field1) >>");
        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA08");
    }

    // ------------------------------------------------------------------
    //  UA09 — tab order (/Tabs, ISO 32000-1 Table 30)
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_AnnotatedPageWithoutTabs_UA09Violation()
    {
        byte[] pdf = BuildPdfWithAnnotatedPage();
        using var ms = new MemoryStream(pdf);
        var result = PdfUaChecker.Validate(ms);
        Assert.Contains(result.Violations, v => v.Code == "UA09");
    }

    [Fact]
    public void Validate_AnnotatedPageWithTabsS_NoUA09Violation()
    {
        byte[] pdf = BuildPdfWithAnnotatedPageAndTabs("/Tabs");
        using var ms = new MemoryStream(pdf);
        var result = PdfUaChecker.Validate(ms);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA09");
    }

    /// <summary>
    /// A page may carry the non-standard /TabOrder key instead of /Tabs. Accepting it on read would
    /// make this checker the one component in the stack that calls the page conformant: /TabOrder
    /// is not in ISO 32000-1 Table 30, no reader honours it, an independent checker and the
    /// published profiles fail the page under ISO 14289-1 §7.18.3, and the library's own
    /// SetStructureTabOrder / FixTabs both treat it as still needing the real key.
    /// </summary>
    [Fact]
    public void Validate_AnnotatedPageWithLegacyTabOrderS_StillReportsUA09()
    {
        byte[] pdf = BuildPdfWithAnnotatedPageAndTabs("/TabOrder");
        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA09");
    }

    [Fact]
    public void Validate_PageWithoutAnnotations_NoUA09Violation()
    {
        // Page has no /Annots — UA09 should not fire
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA09");
    }

    // ------------------------------------------------------------------
    //  UA01 — /MarkInfo may be an indirect reference
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_IndirectMarkInfoMarkedTrue_NoUA01Violation()
    {
        // `/MarkInfo 4 0 R` is entirely legal and extremely common. Cast without resolving, it
        // would be reported "not a dictionary" for every such file.
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Marked true >>");
        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA01");
    }

    [Fact]
    public void Validate_DirectMarkInfoMarkedTrue_NoUA01Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA01");
    }

    [Fact]
    public void Validate_IndirectMarkInfoMarkedFalse_UA01Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Marked false >>");
        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA01");
    }

    // ------------------------------------------------------------------
    //  UA06 — encryption is allowed; denying accessibility extraction is not
    // ------------------------------------------------------------------

    private static byte[] BuildEncryptedPdf(int permissions, int revision)
    {
        string encrypt =
            $"<< /Filter /Standard /V 2 /R {revision} /Length 128 /P {permissions} " +
            "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
            "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";
        return BuildPdfWithTrailer(" /Encrypt 4 0 R",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            encrypt);
    }

    [Fact]
    public void Validate_UnencryptedDocument_NoUA06Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA06");
    }

    /// <summary>
    /// PDF/UA-1 does not forbid encryption. An encrypted document whose /P grants bit 10
    /// (extract for accessibility, value 512) must not be reported under UA06.
    /// </summary>
    [Fact]
    public void Validate_EncryptedButAccessibilityPermitted_NoUA06Violation()
    {
        byte[] pdf = BuildEncryptedPdf(permissions: -4, revision: 3); // all permission bits set
        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA06");
    }

    [Fact]
    public void Validate_EncryptedWithAccessibilityDenied_UA06Violation()
    {
        // -516 == -4 with bit 10 (0x200) cleared.
        byte[] pdf = BuildEncryptedPdf(permissions: -516, revision: 3);
        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA06");
    }

    /// <summary>
    /// The old checker returned immediately on any encrypted document, so an encrypted file
    /// produced a one-line report and nothing else. Dictionary structure is never encrypted,
    /// so the structural checks must still run.
    /// </summary>
    [Fact]
    public void Validate_EncryptedDocument_StillRunsRemainingChecks()
    {
        byte[] pdf = BuildEncryptedPdf(permissions: -4, revision: 3);
        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA01"); // not tagged
        Assert.Contains(result.Violations, v => v.Code == "UA05"); // no structure tree
    }

    /// <summary>
    /// An encrypted document's content is ciphertext to this checker, so which fonts it paints
    /// cannot be read — and every non-embedded font is reported, as before UA25 asked about use.
    /// Were the content readable it would draw Helvetica only invisibly, and go unreported.
    /// </summary>
    [Fact]
    public void Validate_EncryptedDocument_ReportsEveryNonEmbeddedFont()
    {
        const string content = "BT /F1 12 Tf 3 Tr (x) Tj ET";
        string encrypt =
            "<< /Filter /Standard /V 2 /R 3 /Length 128 /P -4 " +
            "/O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> " +
            "/U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>";
        byte[] pdf = BuildPdfWithTrailer(" /Encrypt 4 0 R",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            encrypt,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA25" && v.Message.Contains("Helvetica"));
    }

    /// <summary>
    /// Revision 6 (AES-256 / PDF 2.0) deprecates /P bit 10 — readers must treat it as set,
    /// so a cleared bit is not a violation there.
    /// </summary>
    [Fact]
    public void Validate_EncryptedRevision6_AccessibilityBitIgnored()
    {
        byte[] pdf = BuildEncryptedPdf(permissions: -516, revision: 6);
        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA06");
    }

    // ------------------------------------------------------------------
    //  UA25 — fonts must be embedded
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_UnembeddedFont_UA25Violation()
    {
        // The page draws with the font: UA25 asks about fonts used for rendering (ISO 14289-1
        // 7.21.4.1), and one a resource dictionary merely names is not.
        const string content = "BT /F1 12 Tf 72 700 Td (x) Tj ET";
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R " +
            "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA25");
        Assert.Contains(result.Violations, v => v.Code == "UA25" && v.Message.Contains("Helvetica"));
    }

    [Fact]
    public void Validate_EmbeddedFont_NoUA25Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /ABCDEF+Arial " +
            "/Encoding /WinAnsiEncoding /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Arial /Flags 32 /FontFile2 6 0 R >>",
            "<< /Length 0 >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA25");
    }

    [Fact]
    public void Validate_PageWithoutFonts_NoUA25OrUA26Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA25");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA26");
    }

    // ------------------------------------------------------------------
    //  UA26 — fonts must be mappable to Unicode
    // ------------------------------------------------------------------

    /// <summary>
    /// Identity-H maps character codes straight to glyph indices. Without /ToUnicode the
    /// text cannot be extracted or read aloud at all — the classic accessibility failure.
    /// </summary>
    [Fact]
    public void Validate_IdentityHWithoutToUnicode_UA26Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+Arial /Encoding /Identity-H " +
            "/DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+Arial /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Arial /Flags 4 /FontFile2 7 0 R >>",
            "<< /Length 0 >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA26");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA25"); // it *is* embedded
    }

    [Fact]
    public void Validate_IdentityHWithToUnicode_NoUA26Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /ABCDEF+Arial /Encoding /Identity-H " +
            "/DescendantFonts [5 0 R] /ToUnicode 8 0 R >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /ABCDEF+Arial /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /ABCDEF+Arial /Flags 4 /FontFile2 7 0 R >>",
            "<< /Length 0 >>",
            "<< /Length 0 >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA26");
    }

    [Fact]
    public void Validate_WinAnsiEncodedFont_NoUA26Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " +
            "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA26");
    }

    // ------------------------------------------------------------------
    //  UA27 — annotations need an accessible description
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_AnnotationWithoutContents_UA27Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] >>");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    [Fact]
    public void Validate_AnnotationWithContents_NoUA27Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /Contents (A reviewer note) >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    [Fact]
    public void Validate_HiddenAnnotationWithoutContents_NoUA27Violation()
    {
        // /F bit 2 (value 2) = Hidden: nothing is presented, so nothing needs describing.
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /F 2 >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// <c>/F</c> is read as a flag word (<c>PdfAnnotationFlags.FromNumber</c>), not through
    /// <c>IntValue</c>. That used to saturate both fixtures to every named flag — Hidden among them —
    /// so an undescribed annotation a reader shows was skipped as hidden. <c>6.5</c> is not a flag
    /// word and reads as 0; <c>2147483652</c> is Print plus bit 32 in the unsigned spelling.
    /// </summary>
    [Theory]
    [InlineData("6.5")]
    [InlineData("2147483652")]
    public void Validate_AnnotationWhoseFlagsOnlyLookedHidden_UA27Violation(string flags)
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 20 20] /F " + flags + " >>");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    [Fact]
    public void Validate_WidgetAnnotationWithoutContents_NoUA27Violation()
    {
        // Widgets are covered by UA08 (/TU on the field) — reporting them again under UA27
        // would double-count the same defect.
        byte[] pdf = BuildPdfWithAnnotatedPage();
        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// ISO 14289-1 treats printer's marks as artifacts and exempts them from the /Contents
    /// description requirement (Matterhorn 28-004 names Widget, Link and PrinterMark together).
    /// PdfDeepAccessibilityChecker.EnumerateAnnotations and PdfAutoTagger.BelongsInStructureTree
    /// both exempt the subtype, and so does this checker: otherwise a press-ready file with
    /// registration marks would be reported non-conforming here while the deep checker passed the
    /// very same bytes.
    /// </summary>
    [Fact]
    public void Validate_PrinterMarkAnnotationWithoutContents_NoUA27Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /PrinterMark /Rect [0 0 20 20] /F 4 >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// The exemption must be subtype-specific, not a blanket "any annotation with /F 4 is fine":
    /// a visible /Stamp with no /Contents on the same kind of page must still be reported, and the
    /// count in the message must not include the printer's mark sitting beside it.
    /// </summary>
    [Fact]
    public void Validate_PrinterMarkBesideUndescribedStamp_ReportsOnlyTheStamp()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /PrinterMark /Rect [0 0 20 20] /F 4 >>",
            "<< /Type /Annot /Subtype /Stamp /Rect [30 30 60 60] /F 4 >>");

        var violation = Assert.Single(Validate(pdf).Violations, v => v.Code == "UA27");
        Assert.Contains("1 annotation(s) have neither a /Contents", violation.Message);
    }

    [Fact]
    public void Validate_LinkAnnotationWithoutDescription_UA27Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 20 20] >>");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    /// <summary>
    /// ISO 14289-1 §7.18.5 lets the enclosing &lt;Link&gt; structure element supply the
    /// alternate description instead of the annotation's /Contents.
    /// </summary>
    [Fact]
    public void Validate_LinkAnnotationNestedInLinkStructWithAlt_NoUA27Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 20 20] >>",
            "<< /Type /StructTreeRoot /K 6 0 R >>",
            "<< /Type /StructElem /S /Link /Alt (Link to the annual report) /K 7 0 R >>",
            "<< /Type /OBJR /Obj 4 0 R >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA27");
    }

    // ------------------------------------------------------------------
    //  UA07 — figures need a text alternative, /RoleMap aware
    // ------------------------------------------------------------------

    [Fact]
    public void Validate_FigureWithoutAlt_UA07Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /StructElem /S /Figure >>");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA07");
    }

    [Fact]
    public void Validate_FigureWithActualText_NoUA07Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /StructElem /S /Figure /ActualText (Q3 revenue chart) >>");

        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA07");
    }

    /// <summary>A document-specific role mapped to /Figure must be checked as a figure.</summary>
    [Fact]
    public void Validate_RoleMappedFigureWithoutAlt_UA07Violation()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K 5 0 R /RoleMap << /MyPicture /Figure >> >>",
            "<< /Type /StructElem /S /MyPicture >>");

        Assert.Contains(Validate(pdf).Violations, v => v.Code == "UA07");
    }

    /// <summary>
    /// Adversarial input: a /K graph that points back at an ancestor. Before the cycle guard
    /// this recursed until the depth cap at every branch, costing branching^depth visits.
    /// </summary>
    [Fact]
    public void Validate_CyclicStructureTree_Terminates()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /StructElem /S /Sect /K [6 0 R 6 0 R 6 0 R] >>",
            "<< /Type /StructElem /S /Sect /K [5 0 R 5 0 R 5 0 R] >>");

        var result = Validate(pdf);
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
    }

    /// <summary>Adversarial input: a /Kids cycle in the AcroForm field tree.</summary>
    [Fact]
    public void Validate_CyclicFormFieldTree_Terminates()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /T (a) /Kids [5 0 R 5 0 R] >>",
            "<< /T (b) /Kids [4 0 R 4 0 R] >>");

        var result = Validate(pdf);
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA99");
    }
}
