// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="Pdf20DeprecationChecker"/> — the producer-hygiene advisory for constructs
/// ISO 32000-2:2020 §0.3 deprecates. Every positive case is paired with a negative control, since a
/// checker that always finds something is as useless as one that never does.
/// </summary>
public class Pdf20DeprecationCheckerTests
{
    // ==================================================================
    //  Builder — same shape as PdfUaCheckerHardeningTests's, objects numbered 1..N in order.
    // ==================================================================

    // Two DISTINCT names, not overloads of one name: BuildPdf(a, b) with a params-array second
    // overload is an overload-resolution trap — the compiler silently prefers absorbing the first
    // argument into a fixed string parameter over expanding both into the params array, so a plain
    // two-object call would route through the trailer-taking overload instead, swallowing the
    // catalog object into "extra trailer text" and corrupting every fixture built that way. Matches
    // PdfUaCheckerHardeningTests's own two-name pattern for the same reason.
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

    private static byte[] BuildPdf(params string[] objects) => BuildPdfWithTrailer(string.Empty, objects);

    private static byte[] OnePageWithAnnotation(string annotExtras)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
            "<< /Type /Annot /Rect [0 0 100 20] " + annotExtras + " >>");

    private static byte[] OnePageWithResources(string resourceExtras)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << " + resourceExtras + " >> >>");

    private static byte[] SignedDocument(string subFilter)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
            "<< /FT /Sig /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /T (Sig1) "
                + "/V << /Type /Sig /Filter /Adobe.PPKLite /SubFilter /" + subFilter + " >> >>");

    /// <summary>A page whose <c>/Resources</c> names one Form XObject (object 4), which in turn
    /// carries its OWN <paramref name="nestedResourceExtras"/> as its <c>/Resources</c> — the shape
    /// that proves a check reads <see cref="Tetitulz.Pdf.Plugins.Conformance.PdfXValidator.ScanPage"/>'s
    /// full reachable-resource walk rather than <c>page.Resources</c> alone.</summary>
    private static byte[] OnePageWithNestedFormResources(string nestedResourceExtras)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Fm1 4 0 R >> >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
                + "/Resources << " + nestedResourceExtras + " >> /Length 0 >>\nstream\n\nendstream");

    private static byte[] OnePageWithFontResource(string fontDictBody)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 4 0 R >> >> >>",
            fontDictBody);

    // ==================================================================
    //  XFA
    // ==================================================================

    [Fact]
    public void Check_AcroFormWithXfa_ReportsXfa()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA (dummy) >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.Xfa);
    }

    [Fact]
    public void Check_AcroFormWithoutXfa_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.Xfa);
    }

    // ==================================================================
    //  Movie / Sound / TrapNet annotations
    // ==================================================================

    [Theory]
    [InlineData("/Subtype /Movie")]
    [InlineData("/Subtype /Sound")]
    [InlineData("/Subtype /TrapNet")]
    public void Check_DeprecatedAnnotationSubtype_IsReported(string subtypeEntry)
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithAnnotation(subtypeEntry)));

        var finding = Assert.Single(findings, f => f.Construct == Pdf20DeprecatedConstruct.MovieSoundOrTrapNetAnnotation);
        Assert.Equal(0, finding.PageIndex);
    }

    /// <summary>
    /// Pages sharing one <c>/Annots</c> array that names one sound annotation many times report it
    /// once, at the first page: read once a page, 200 pages × 200 entries made 40 000 findings (and
    /// 10 000 × 10 000 runs out of memory). A second, distinct sound annotation on a later page is
    /// reported there (the control).
    /// </summary>
    [Fact]
    public void AdversarialSharedAnnotsArray_ReportsEachAnnotationOnce()
    {
        const int pages = 200;
        var kids = string.Join(" ", Enumerable.Range(0, pages).Select(i => (6 + i) + " 0 R"));
        var objects = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + kids + " " + (6 + pages) + " 0 R] /Count " + (pages + 1) + " >>",
            "[" + string.Join(" ", Enumerable.Repeat("4 0 R", pages)) + "]",
            "<< /Type /Annot /Subtype /Sound /Rect [0 0 1 1] >>",
            "<< /Type /Annot /Subtype /Sound /Rect [0 0 1 1] >>",
        };
        for (int i = 0; i < pages; i++)
            objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots 3 0 R >>");
        objects.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Annots [5 0 R] >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(BuildPdf(objects.ToArray())))
            .Where(f => f.Construct == Pdf20DeprecatedConstruct.MovieSoundOrTrapNetAnnotation).ToList();

        Assert.Equal(new[] { 0, pages }, findings.Select(f => f.PageIndex).ToArray());
    }

    [Fact]
    public void Check_OrdinaryWidgetAnnotation_ReportsNothing()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithAnnotation("/Subtype /Widget")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.MovieSoundOrTrapNetAnnotation);
    }

    // ==================================================================
    //  /Info dictionary
    // ==================================================================

    [Fact]
    public void Check_TrailerWithInfo_ReportsInfoDictionary()
    {
        byte[] pdf = BuildPdfWithTrailer(
            " /Info 3 0 R",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Title (test) >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.InfoDictionary);
    }

    [Fact]
    public void Check_TrailerWithoutInfo_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.InfoDictionary);
    }

    // ==================================================================
    //  /ProcSet
    // ==================================================================

    [Fact]
    public void Check_ResourcesWithProcSet_ReportsProcSet()
    {
        var findings = Pdf20DeprecationChecker.Check(
            new MemoryStream(OnePageWithResources("/ProcSet [/PDF /Text]")));

        var finding = Assert.Single(findings, f => f.Construct == Pdf20DeprecatedConstruct.ProcSet);
        Assert.Equal(0, finding.PageIndex);
    }

    [Fact]
    public void Check_ResourcesWithoutProcSet_ReportsNothing()
    {
        var findings = Pdf20DeprecationChecker.Check(
            new MemoryStream(OnePageWithResources("/Font << >>")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.ProcSet);
    }

    // ==================================================================
    //  Legacy signature SubFilter
    // ==================================================================

    [Theory]
    [InlineData("adbe.pkcs7.sha1")]
    [InlineData("adbe.x509.rsa_sha1")]
    public void Check_LegacySubFilter_IsReported(string subFilter)
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(SignedDocument(subFilter)));

        var finding = Assert.Single(findings, f => f.Construct == Pdf20DeprecatedConstruct.LegacySignatureSubFilter);
        Assert.Contains(subFilter, finding.Detail);
    }

    [Fact]
    public void Check_ModernSubFilter_ReportsNothing()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(SignedDocument("adbe.pkcs7.detached")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.LegacySignatureSubFilter);
    }

    // ==================================================================
    //  Transfer functions
    // ==================================================================

    [Fact]
    public void Check_NonDefaultTransferFunction_IsReported()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithResources(
            "/ExtGState << /GS1 << /TR /Identity >> >>")));

        var finding = Assert.Single(findings, f => f.Construct == Pdf20DeprecatedConstruct.TransferFunctionInGraphicsState);
        Assert.Equal(0, finding.PageIndex);
    }

    /// <summary>Presence is not deviation: /TR /Default names the behaviour every reader already has.</summary>
    [Fact]
    public void Check_DefaultTransferFunction_ReportsNothing()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithResources(
            "/ExtGState << /GS1 << /TR /Default >> >>")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.TransferFunctionInGraphicsState);
    }

    [Fact]
    public void Check_NoExtGState_ReportsNothing()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithResources("/Font << >>")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.TransferFunctionInGraphicsState);
    }

    // ==================================================================
    //  /AcroForm /NeedAppearances
    // ==================================================================

    [Fact]
    public void Check_NeedAppearancesTrue_IsReported()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /NeedAppearances true >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.NeedAppearances);
    }

    /// <summary>Presence is not deviation: /NeedAppearances false names the key's own default.</summary>
    [Fact]
    public void Check_NeedAppearancesFalse_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /NeedAppearances false >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.NeedAppearances);
    }

    [Fact]
    public void Check_NoAcroForm_ReportsNothingForNeedAppearances()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.NeedAppearances);
    }

    // ==================================================================
    //  /MarkInfo /Suspects
    // ==================================================================

    [Fact]
    public void Check_MarkInfoWithSuspects_IsReported()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true /Suspects false >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.SuspectsFlag);
    }

    [Fact]
    public void Check_MarkInfoWithoutSuspects_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.SuspectsFlag);
    }

    [Fact]
    public void Check_NoMarkInfo_ReportsNothingForSuspects()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.SuspectsFlag);
    }

    // ==================================================================
    //  Prepress /ViewerPreferences
    // ==================================================================

    [Theory]
    [InlineData("/ViewArea [0 0 100 100]")]
    [InlineData("/ViewClip [0 0 100 100]")]
    [InlineData("/PrintArea [0 0 100 100]")]
    [InlineData("/PrintClip [0 0 100 100]")]
    public void Check_PrepressViewerPreference_IsReported(string prefEntry)
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences << " + prefEntry + " >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.PrepressViewerPreference);
    }

    [Fact]
    public void Check_OrdinaryViewerPreferences_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences << /DisplayDocTitle true >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.PrepressViewerPreference);
    }

    [Fact]
    public void Check_NoViewerPreferences_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.PrepressViewerPreference);
    }

    // ==================================================================
    //  /BM as an array
    // ==================================================================

    [Fact]
    public void Check_BlendModeArray_IsReported()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithResources(
            "/ExtGState << /GS1 << /BM [/Multiply /Normal] >> >>")));

        var finding = Assert.Single(findings, f => f.Construct == Pdf20DeprecatedConstruct.BlendModeArray);
        Assert.Equal(0, finding.PageIndex);
    }

    [Fact]
    public void Check_BlendModeSingleName_ReportsNothing()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithResources(
            "/ExtGState << /GS1 << /BM /Multiply >> >>")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.BlendModeArray);
    }

    [Fact]
    public void Check_NoBlendMode_ReportsNothingForBlendModeArray()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithResources(
            "/ExtGState << /GS1 << /CA 0.5 >> >>")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.BlendModeArray);
    }

    // ==================================================================
    //  /OPI
    // ==================================================================

    [Fact]
    public void Check_ImageXObjectWithOpi_ReportsOpi()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Im1 4 0 R >> >> >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /OPI << /2.0 << >> >> /Length 1 >>\nstream\n\x00\nendstream");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        var finding = Assert.Single(findings, f => f.Construct == Pdf20DeprecatedConstruct.Opi);
        Assert.Equal(0, finding.PageIndex);
    }

    [Fact]
    public void Check_ImageXObjectWithoutOpi_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Im1 4 0 R >> >> >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Length 1 >>\nstream\n\x00\nendstream");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.Opi);
    }

    /// <summary>The regression test for the <c>ScanPage</c> retrofit: an <c>/OPI</c>-bearing image
    /// reachable only through a Form XObject's own <c>/Resources</c>, invisible to a
    /// <c>page.Resources</c>-only read.</summary>
    [Fact]
    public void Check_OpiInsideNestedFormXObject_ReportsOpi()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Fm1 4 0 R >> >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
                + "/Resources << /XObject << /Im1 5 0 R >> >> /Length 0 >>\nstream\n\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /OPI << /2.0 << >> >> /Length 1 >>\nstream\n\x00\nendstream");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.Opi);
    }

    // ==================================================================
    //  Nested-resource regressions for the ScanPage retrofit (ProcSet, BlendModeArray)
    // ==================================================================

    [Fact]
    public void Check_ProcSetInsideNestedFormXObject_ReportsProcSet()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(
            OnePageWithNestedFormResources("/ProcSet [/PDF /Text]")));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.ProcSet);
    }

    [Fact]
    public void Check_BlendModeArrayInsideNestedFormXObject_ReportsBlendModeArray()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(
            OnePageWithNestedFormResources("/ExtGState << /GS1 << /BM [/Multiply /Normal] >> >>")));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.BlendModeArray);
    }

    // ==================================================================
    //  /CharSet (Type1) and /CIDSet (CIDFont)
    // ==================================================================

    [Fact]
    public void Check_Type1FontWithCharSet_ReportsCharSet()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithFontResource(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times "
            + "/FontDescriptor << /Type /FontDescriptor /CharSet (/A/B) >> >>")));

        var finding = Assert.Single(findings, f => f.Construct == Pdf20DeprecatedConstruct.CharSet);
        Assert.Equal(0, finding.PageIndex);
    }

    [Fact]
    public void Check_Type1FontWithoutCharSet_ReportsNothing()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithFontResource(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times "
            + "/FontDescriptor << /Type /FontDescriptor >> >>")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.CharSet);
    }

    /// <summary>Cross-check: a Type1 font's descriptor carrying <c>/CIDSet</c> instead (a
    /// malformed but conceivable file) must not report <c>CharSet</c> — proves the finding is keyed
    /// off the actual key present, not "any descriptor key at all".</summary>
    [Fact]
    public void Check_Type1FontWithCidSetInsteadOfCharSet_ReportsNothing()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithFontResource(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Times "
            + "/FontDescriptor << /Type /FontDescriptor /CIDSet (dummy) >> >>")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.CharSet);
        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.CidSet);
    }

    [Fact]
    public void Check_CidFontWithCidSet_ReportsCidSet()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H "
                + "/DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor << /Type /FontDescriptor /CIDSet (dummy) >> >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        var finding = Assert.Single(findings, f => f.Construct == Pdf20DeprecatedConstruct.CidSet);
        Assert.Equal(0, finding.PageIndex);
    }

    [Fact]
    public void Check_CidFontWithoutCidSet_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H "
                + "/DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor << /Type /FontDescriptor >> >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.CidSet);
    }

    /// <summary>Cross-check, the other direction: a CIDFont descriptor carrying <c>/CharSet</c>
    /// instead must not report <c>CidSet</c> (or <c>CharSet</c> — the subtype gate for
    /// <c>CharSet</c> requires <c>/Subtype /Type1</c>, which this is not).</summary>
    [Fact]
    public void Check_CidFontWithCharSetInsteadOfCidSet_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H "
                + "/DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo "
                + "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> "
                + "/FontDescriptor << /Type /FontDescriptor /CharSet (/A/B) >> >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.CidSet);
        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.CharSet);
    }

    // ==================================================================
    //  /Names /AlternatePresentations
    // ==================================================================

    [Fact]
    public void Check_NamesWithAlternatePresentations_IsReported()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /AlternatePresentations 3 0 R >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Names [] >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.AlternatePresentations);
    }

    [Fact]
    public void Check_NamesWithoutAlternatePresentations_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /JavaScript 3 0 R >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Names [] >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.AlternatePresentations);
    }

    [Fact]
    public void Check_NoNames_ReportsNothingForAlternatePresentations()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.AlternatePresentations);
    }

    // ==================================================================
    //  /Perms /UR3
    // ==================================================================

    [Fact]
    public void Check_PermsWithUR3_IsReported()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Perms << /UR3 3 0 R >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Sig >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.UsageRights);
    }

    [Fact]
    public void Check_PermsWithoutUR3_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Perms << /DocMDP 3 0 R >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Sig >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.UsageRights);
    }

    [Fact]
    public void Check_NoPerms_ReportsNothingForUsageRights()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.UsageRights);
    }

    // ==================================================================
    //  /Encrypt /P bit 10 (extract for accessibility)
    // ==================================================================

    [Fact]
    public void Check_EncryptPClearsAccessibilityBit_IsReported()
    {
        byte[] pdf = BuildPdfWithTrailer(
            " /Encrypt 3 0 R",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Filter /Standard /V 1 /R 2 /P 0 >>"); // bit 10 (512) is clear

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.AssistiveTechnologyDrmRestriction);
    }

    [Fact]
    public void Check_EncryptPSetsAccessibilityBit_ReportsNothing()
    {
        byte[] pdf = BuildPdfWithTrailer(
            " /Encrypt 3 0 R",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Filter /Standard /V 1 /R 2 /P 512 >>"); // bit 10 set

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.AssistiveTechnologyDrmRestriction);
    }

    [Fact]
    public void Check_NoEncrypt_ReportsNothingForAccessibilityRestriction()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.AssistiveTechnologyDrmRestriction);
    }

    // ==================================================================
    //  Named XObjects/Fonts (/Name directly on the resource dictionary)
    // ==================================================================

    [Fact]
    public void Check_ImageXObjectWithName_IsReported()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Im1 4 0 R >> >> >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Name /Im1 /Length 1 >>\nstream\n\x00\nendstream");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.NamedResource);
    }

    [Fact]
    public void Check_ImageXObjectWithoutName_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /XObject << /Im1 4 0 R >> >> >>",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Length 1 >>\nstream\n\x00\nendstream");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.NamedResource);
    }

    [Fact]
    public void Check_FontWithName_IsReported()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithFontResource(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Name /F1 >>")));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.NamedResource);
    }

    [Fact]
    public void Check_FontWithoutName_ReportsNothing()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(OnePageWithFontResource(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.NamedResource);
    }

    // ==================================================================
    //  OS-specific file specifications (/DOS, /Mac, /Unix)
    // ==================================================================

    [Theory]
    [InlineData("/DOS (C:\\\\TEST.TXT)")]
    [InlineData("/Mac (TEST.TXT)")]
    [InlineData("/Unix (/tmp/test.txt)")]
    public void Check_AFFilespecWithOsEntry_IsReported(string osEntry)
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AF [3 0 R] >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Filespec /F (test.txt) " + osEntry + " >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificFileSpecification);
    }

    [Fact]
    public void Check_AFFilespecWithoutOsEntry_ReportsNothing()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AF [3 0 R] >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Type /Filespec /F (test.txt) >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificFileSpecification);
    }

    /// <summary>The other reachable location, walked via the shared name-tree reader rather than a
    /// second walk.</summary>
    [Fact]
    public void Check_EmbeddedFilesFilespecWithOsEntry_IsReported()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles 3 0 R >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Names [(file1) 4 0 R] >>",
            "<< /Type /Filespec /F (test.txt) /Mac (TEST.TXT) >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificFileSpecification);
    }

    [Fact]
    public void Check_NoAFOrEmbeddedFiles_ReportsNothingForOsSpecificFileSpecification()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificFileSpecification);
    }

    // ==================================================================
    //  Movie/Sound actions and OS-specific Launch-action entries — both reached via
    //  PdfActionDispatcher.ExtractActions(PdfDocument), not a second hand-rolled walk.
    // ==================================================================

    private static byte[] WithOpenAction(string actionDictBody)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /OpenAction << " + actionDictBody + " >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>");

    /// <summary>A field-level action (§12.6.3's other reachable location), proving the check rides
    /// the dispatcher's full walk rather than only /OpenAction.</summary>
    private static byte[] WithFieldAction(string actionDictBody)
        => BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [4 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R] >>",
            "<< /FT /Btn /Type /Annot /Subtype /Widget /Rect [0 0 100 20] /T (Btn1) "
                + "/A << " + actionDictBody + " >> >>");

    /// <summary>An indirect <c>/S</c> (§7.3.10) is still a Movie action.</summary>
    /// <remarks>
    /// Read raw, <c>/S 3 0 R</c> is not a name, so the deprecated construct went unreported. The
    /// catalog's page tree is object 2, so the name sits at object 3.
    /// </remarks>
    [Fact]
    public void Check_OpenActionWithAnIndirectMovieSubtype_ReportsMovieOrSoundAction()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /OpenAction << /S 3 0 R >> >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "/Movie");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.MovieOrSoundAction);
    }

    [Fact]
    public void Check_OpenActionIsSound_ReportsMovieOrSoundAction()
    {
        byte[] pdf = WithOpenAction("/S /Sound");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.MovieOrSoundAction);
    }

    [Fact]
    public void Check_OpenActionIsMovie_ReportsMovieOrSoundAction()
    {
        byte[] pdf = WithOpenAction("/S /Movie");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.MovieOrSoundAction);
    }

    [Fact]
    public void Check_FieldActionIsSound_ReportsMovieOrSoundAction()
    {
        byte[] pdf = WithFieldAction("/S /Sound");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.MovieOrSoundAction);
    }

    [Fact]
    public void Check_OpenActionIsGoTo_ReportsNothingForMovieOrSoundAction()
    {
        byte[] pdf = WithOpenAction("/S /GoTo /D [3 0 R /Fit]");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.MovieOrSoundAction);
    }

    [Fact]
    public void Check_LaunchActionWithWinEntry_ReportsOsSpecificLaunchAction()
    {
        byte[] pdf = WithOpenAction("/S /Launch /F (app.exe) /Win << /F (app.exe) >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificLaunchAction);
    }

    [Fact]
    public void Check_LaunchActionWithMacEntry_ReportsOsSpecificLaunchAction()
    {
        byte[] pdf = WithOpenAction("/S /Launch /Mac (TestApp)");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificLaunchAction);
    }

    [Fact]
    public void Check_LaunchActionWithUnixEntry_ReportsOsSpecificLaunchAction()
    {
        byte[] pdf = WithOpenAction("/S /Launch /Unix (testapp)");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificLaunchAction);
    }

    /// <summary>The control: <c>/F</c> alone is the platform-neutral, non-deprecated shape.</summary>
    [Fact]
    public void Check_LaunchActionWithOnlyF_ReportsNothingForOsSpecificLaunchAction()
    {
        byte[] pdf = WithOpenAction("/S /Launch /F (app.exe)");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificLaunchAction);
    }

    /// <summary>A <c>/Win</c> entry on a non-Launch action must not be misread as this construct —
    /// the key only means "deprecated OS-specific launch parameters" inside a Launch action.</summary>
    [Fact]
    public void Check_GoToActionCannotTriggerOsSpecificLaunchAction()
    {
        byte[] pdf = WithOpenAction("/S /GoTo /D [3 0 R /Fit]");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.DoesNotContain(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificLaunchAction);
    }

    /// <summary>
    /// A link's Movie action, an annotation's page-open Sound action and an outline item's
    /// <c>/Win</c> launch are each reported, a link's and an annotation's on their page.
    /// </summary>
    /// <remarks>
    /// The check rides <c>PdfActionDispatcher.ExtractActions</c>, which walked the catalog, the
    /// pages' <c>/AA</c> and the form fields and nothing else. A link is where a Movie action
    /// usually is, and none of these three was reported.
    /// </remarks>
    [Fact]
    public void Check_AnnotationAndOutlineActions_AreReported()
    {
        byte[] pdf = BuildPdf(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Annots [4 0 R 5 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Movie >> >>",
            "<< /Type /Annot /Subtype /Screen /Rect [0 0 10 10] /AA << /PO << /S /Sound >> >> >>",
            "<< /Type /Outlines /First 7 0 R /Last 7 0 R >>",
            "<< /Title (item) /Parent 6 0 R /A << /S /Launch /Win << /F (app.exe) >> >> >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        var media = findings.Where(f => f.Construct == Pdf20DeprecatedConstruct.MovieOrSoundAction).ToList();
        Assert.Equal(2, media.Count);
        Assert.All(media, f => Assert.Equal(0, f.PageIndex));
        Assert.Contains(findings, f => f.Construct == Pdf20DeprecatedConstruct.OsSpecificLaunchAction);
    }

    // ==================================================================
    //  AssistiveTechnologyDrmRestriction — /P is a flag word (Wave 17, R9.2)
    // ==================================================================

    /// <summary>
    /// Producers write <c>/P</c> in its unsigned spelling as often as its signed one; the two name
    /// the same 32 bits (ISO 32000-1 Table 22). Read through <c>IntValue</c>, the unsigned form
    /// saturated to <c>int.MaxValue</c> — every bit set — so a document denying extraction for
    /// assistive technology was reported in one spelling and passed in the other. Both PDF/UA
    /// checkers read <c>/P</c> through <c>PdfPermissionValue</c>; this one now does too.
    /// </summary>
    [Theory]
    [InlineData("-3904", true)]         // 0xFFFFF0C0 — bit 10 clear
    [InlineData("4294963392", true)]    // the same bits, unsigned
    [InlineData("-3392", false)]        // 0xFFFFF2C0 — bit 10 set
    [InlineData("4294963904", false)]   // the same bits, unsigned
    public void Check_EncryptPermissionsDenyingAssistiveExtraction_AreReportedInEitherSpelling(
        string p, bool reported)
    {
        byte[] pdf = BuildPdfWithTrailer(" /Encrypt 4 0 R",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Filter /Standard /V 2 /R 3 /Length 128 /P " + p
            + " /O <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20>"
            + " /U <0102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F20> >>");

        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(pdf));

        Assert.Equal(reported,
            findings.Any(f => f.Construct == Pdf20DeprecatedConstruct.AssistiveTechnologyDrmRestriction));
    }
}
