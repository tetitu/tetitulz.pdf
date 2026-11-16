// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.Accessibility.Checking.Wtpdf;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfWtpdfChecker"/> — WTPDF 1.0's declared conformance level (§6.1) over the PDF/UA-2
/// structure checker, whose clause numbers WTPDF shares.
/// </summary>
/// <remarks>
/// The fixture carries three findings of known level: a <c>Figure</c> with no <c>/Alt</c>
/// (§8.2.5.28.2, accessibility only), a catalog with no <c>/ViewerPreferences</c> (§8.11.2,
/// accessibility only) and a bare <c>H</c> heading (§8.2.5.12, a general provision). Declarations are
/// added with the real <see cref="PdfDeclarationsWriter"/>, so the reader is exercised end to end.
/// </remarks>
public class PdfWtpdfCheckerTests
{
    private static byte[] FixtureWithoutDeclarations() => PdfUa2StructureCheckerTests.BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /Lang (en-US) >>",   // no /ViewerPreferences
        "<< /Type /StructTreeRoot /K [5 0 R] >>",
        "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
        "<< /Type /StructElem /S /Document /P 2 0 R /NS 6 0 R /K [7 0 R 8 0 R] >>", // object 5
        "<< /Type /Namespace /NS (http://iso.org/pdf2/ssn) >>",                      // object 6
        "<< /Type /StructElem /S /Figure /P 5 0 R >>",                              // object 7 — no /Alt
        "<< /Type /StructElem /S /H /P 5 0 R >>",                                   // object 8 — bare H
    });

    private static byte[] Declare(byte[] pdf, params string[] uris)
    {
        foreach (string uri in uris)
        {
            using var input = new MemoryStream(pdf);
            using var output = new MemoryStream();
            Assert.True(PdfDeclarationsWriter.AddDeclaration(input, output, uri), PdfDeclarationsWriter.LastError);
            pdf = output.ToArray();
        }
        return pdf;
    }

    private static PdfWtpdfResult Validate(byte[] pdf) => PdfWtpdfChecker.Validate(new MemoryStream(pdf));

    private static bool Has(PdfWtpdfResult result, string code) => result.Violations.Any(v => v.Code == code);

    [Fact]
    public void NoDeclaration_ReportsWtpdf611_AndJudgesAtReuse()
    {
        var result = Validate(FixtureWithoutDeclarations());

        Assert.True(Has(result, "WTPDF-6.1.1"));
        Assert.Empty(result.DeclaredLevels);
        Assert.Equal(WtpdfConformanceLevel.Reuse, result.JudgedLevel);
        Assert.True(Has(result, "UA2-8.2.5.12"));     // general provision still reported
        Assert.False(Has(result, "UA2-8.2.5.28.2"));  // accessibility only
        Assert.False(Has(result, "UA2-8.11.2"));      // accessibility only
    }

    [Fact]
    public void ReuseDeclared_DropsAccessibilityOnlyFindings_KeepsGeneralOnes()
    {
        var result = Validate(Declare(FixtureWithoutDeclarations(), PdfWtpdfChecker.ReuseDeclarationUri));

        Assert.False(Has(result, "WTPDF-6.1.1"));
        Assert.Equal(new[] { WtpdfConformanceLevel.Reuse }, result.DeclaredLevels);
        Assert.Equal(WtpdfConformanceLevel.Reuse, result.JudgedLevel);
        Assert.True(Has(result, "UA2-8.2.5.12"));
        Assert.False(Has(result, "UA2-8.2.5.28.2"));
        Assert.False(Has(result, "UA2-8.11.2"));
    }

    [Fact]
    public void AccessibilityDeclared_KeepsAccessibilityOnlyFindings()
    {
        var result = Validate(Declare(FixtureWithoutDeclarations(), PdfWtpdfChecker.AccessibilityDeclarationUri));

        Assert.False(Has(result, "WTPDF-6.1.1"));
        Assert.Equal(WtpdfConformanceLevel.Accessibility, result.JudgedLevel);
        Assert.True(Has(result, "UA2-8.2.5.12"));
        Assert.True(Has(result, "UA2-8.2.5.28.2"));
        Assert.True(Has(result, "UA2-8.11.2"));
    }

    [Theory]
    [InlineData(PdfWtpdfChecker.ReuseDeclarationUri, false)]
    [InlineData(PdfWtpdfChecker.AccessibilityDeclarationUri, true)]
    public void PuaContentWithoutActualTextOrAlt_IsReportedOnlyAtAccessibility(string declaration, bool reported)
    {
        // WTPDF §8.4.3: "should be present" for reuse, "shall be present" for accessibility.
        byte[] pdf = PdfUa2StructureCheckerTests.PuaPdf("BT /F1 12 Tf (A) Tj ET");
        Assert.Contains(PdfUa2StructureChecker.Validate(new MemoryStream(pdf)).Violations, v => v.Code == "UA2-8.4.3");

        Assert.Equal(reported, Has(Validate(Declare(pdf, declaration)), "UA2-8.4.3"));
    }

    [Fact]
    public void BothLevelsDeclared_JudgedAtAccessibility()
    {
        var result = Validate(Declare(FixtureWithoutDeclarations(),
            PdfWtpdfChecker.ReuseDeclarationUri, PdfWtpdfChecker.AccessibilityDeclarationUri));

        Assert.Equal(new[] { WtpdfConformanceLevel.Reuse, WtpdfConformanceLevel.Accessibility }, result.DeclaredLevels);
        Assert.Equal(WtpdfConformanceLevel.Accessibility, result.JudgedLevel);
        Assert.True(Has(result, "UA2-8.2.5.28.2"));
    }

    [Fact]
    public void OnlyAnUnrelatedDeclaration_ReportsWtpdf611()
    {
        var result = Validate(Declare(FixtureWithoutDeclarations(), "https://example.org/standards/privacy-v1"));

        Assert.True(Has(result, "WTPDF-6.1.1"));
        Assert.Empty(result.DeclaredLevels);
    }

    [Fact]
    public void UnreadableDeclarations_ReportWtpdf99_NotAMissingDeclaration()
    {
        // An encrypted document's XMP is not read; that is "cannot tell", not "declares nothing".
        var pdf = new StringBuilder();
        pdf.Append("%PDF-2.0\n");
        int o1 = pdf.Length; pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = pdf.Length; pdf.Append("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        int o3 = pdf.Length; pdf.Append("3 0 obj\n<< /Filter /Standard /V 2 /R 3 /O <00> /U <00> /P -4 >>\nendobj\n");
        int xref = pdf.Length;
        pdf.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int o in new[] { o1, o2, o3 }) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size 4 /Root 1 0 R /Encrypt 3 0 R /ID [<00> <00>] >>\n");
        pdf.Append("startxref\n").Append(xref).Append("\n%%EOF\n");

        var result = Validate(Encoding.ASCII.GetBytes(pdf.ToString()));

        Assert.True(Has(result, "WTPDF-99"));
        Assert.False(Has(result, "WTPDF-6.1.1"));
    }
}
