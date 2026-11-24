// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="PdfAValidator"/>'s action rules, part by part, as the published profile's published validation
/// profiles state them.
/// </summary>
/// <remarks>
/// <para><b>The source.</b> ISO 19005 is not freely available, and the repository holds only its
/// Technical Corrigendum 1. The rules below come from the published profile's profiles (the PDF Association's
/// reference validator). Each test names the ISO clause and the published profile's rule id.</para>
/// <para><b>What a narrower reading misses.</b> A02 looking only for a <c>/Names /JavaScript</c>
/// entry, A05 only at the head of <c>/OpenAction</c>, and A04 only at the catalog's and the pages'
/// <c>/AA</c>, the same for every part, would pass a link's <c>/Launch</c>, a JavaScript action
/// anywhere but those two places, named actions and a widget's actions. In the other direction,
/// PDF/A-4 permits JavaScript actions (6.6.1-1) and PDF/A-1 has no rule about a page's <c>/AA</c>,
/// so neither is reported there.</para>
/// </remarks>
public class PdfAActionRuleTests
{
    // ── forbidden action types: A02 ──────────────────────────────────────────

    /// <summary>ISO 19005-1 6.6.1 (the published profiles 6.6.1-1): Launch is not permitted, wherever it is.</summary>
    [Fact]
    public void Part1_ALinksLaunchActionIsForbidden()
    {
        byte[] pdf = PageWithAnnots("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Launch /F (app.exe) >> >>");

        Assert.Contains(Codes(pdf, PdfAConformancePart.Part1), c => c == "A02");
    }

    /// <summary>ISO 19005-2 6.5.1 (the published profiles 6.5.1-1): SetOCGState is not permitted in PDF/A-2.</summary>
    [Fact]
    public void Part2_ASetOcgStateOpenActionIsForbidden()
    {
        byte[] pdf = Doc("/OpenAction << /S /SetOCGState /State [] >>", "");

        Assert.Contains(Codes(pdf, PdfAConformancePart.Part2), c => c == "A02");
    }

    /// <summary>
    /// A JavaScript action is forbidden in parts 1 to 3 wherever it sits: here on a link, and at
    /// the end of a <c>/Next</c> chain whose head is a harmless GoTo.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part1)]
    [InlineData(PdfAConformancePart.Part2)]
    [InlineData(PdfAConformancePart.Part3)]
    public void AJavaScriptActionAnywhereIsForbiddenInPartsOneToThree(PdfAConformancePart part)
    {
        byte[] onALink = PageWithAnnots("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /JavaScript /JS (x) >> >>");
        byte[] inAChain = Doc("/OpenAction << /S /GoTo /D [3 0 R /Fit] /Next << /S /JavaScript /JS (x) >> >>", "");

        Assert.Contains(Codes(onALink, part), c => c == "A02");
        Assert.Contains(Codes(inAChain, part), c => c == "A02");
    }

    /// <summary>
    /// ISO 19005-4 6.6.1 (the published profiles 6.6.1-1) permits JavaScript actions, and the validator reported
    /// one. Launch is still forbidden in PDF/A-4, which is the control.
    /// </summary>
    [Fact]
    public void Part4_AJavaScriptActionIsPermittedAndALaunchIsNot()
    {
        byte[] js = Doc("/OpenAction << /S /JavaScript /JS (x) >>", "", "%PDF-2.0");
        byte[] launch = Doc("/OpenAction << /S /Launch /F (app.exe) >>", "", "%PDF-2.0");

        Assert.DoesNotContain(Codes(js, PdfAConformancePart.Part4), c => c == "A02");
        Assert.Contains(Codes(launch, PdfAConformancePart.Part4), c => c == "A02");
    }

    /// <summary>The permitted types pass: GoTo, URI and a Named NextPage.</summary>
    [Fact]
    public void PermittedActionsPass()
    {
        byte[] pdf = PageWithAnnots(
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /URI /URI (https://example.test) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Named /N /NextPage >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /GoTo /D [3 0 R /Fit] >> >>");

        foreach (var part in new[] { PdfAConformancePart.Part1, PdfAConformancePart.Part2, PdfAConformancePart.Part3 })
            Assert.DoesNotContain(Codes(pdf, part), c => c == "A02" || c == "A22" || c == "A23");
    }

    /// <summary>
    /// The allow-lists differ by part: GoToE is permitted from PDF/A-2 on, not in PDF/A-1, and Hide
    /// is forbidden in PDF/A-1 by Corrigendum 2 (the published profiles 6.6.1-1). RichMediaExecute is PDF/A-4's.
    /// </summary>
    [Fact]
    public void TheAllowListIsThePartsOwn()
    {
        byte[] goToE = Doc("/OpenAction << /S /GoToE /T << /R /C /N (a) >> >>", "");
        byte[] hide = Doc("/OpenAction << /S /Hide /T (f) >>", "");
        byte[] richMedia = Doc("/OpenAction << /S /RichMediaExecute /TA 4 0 R >>", "", "%PDF-2.0", "<< >>");

        Assert.Contains(Codes(goToE, PdfAConformancePart.Part1), c => c == "A02");
        Assert.DoesNotContain(Codes(goToE, PdfAConformancePart.Part2), c => c == "A02");
        Assert.Contains(Codes(hide, PdfAConformancePart.Part1), c => c == "A02");
        Assert.Contains(Codes(richMedia, PdfAConformancePart.Part3), c => c == "A02");
        Assert.DoesNotContain(Codes(richMedia, PdfAConformancePart.Part4), c => c == "A02");
    }

    /// <summary>
    /// An action with no <c>/S</c> fails the published profile's test, which compares <c>S</c> to each permitted
    /// type and so is false for a missing one.
    /// </summary>
    [Fact]
    public void AnActionWithNoTypeIsForbidden()
    {
        byte[] untyped = Doc("/OpenAction << /URI (https://example.test) >>", "");
        byte[] typed = Doc("/OpenAction << /S /URI /URI (https://example.test) >>", "");

        Assert.Contains(Violations(untyped, PdfAConformancePart.Part2), v => v.Code == "A02" && v.Message.Contains("no /S type"));
        Assert.DoesNotContain(Codes(typed, PdfAConformancePart.Part2), c => c == "A02");
    }

    /// <summary>
    /// An outline item's action is an action like any other: the old check never looked at one.
    /// </summary>
    [Fact]
    public void AnOutlineItemsLaunchIsForbidden()
    {
        byte[] pdf = Doc("/Outlines 4 0 R", "", "%PDF-1.7",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            "<< /Title (x) /Parent 4 0 R /A << /S /Launch /F (app.exe) >> >>");

        Assert.Contains(Violations(pdf, PdfAConformancePart.Part2), v => v.Code == "A02" && v.Message.Contains("an outline item"));
    }

    /// <summary>
    /// Many forbidden links are one A02 per type that counts them and names the first, not one
    /// entry each; and the message cites the clause and the published profile's rule id.
    /// </summary>
    [Fact]
    public void RepeatedViolationsAreCountedInOneEntryThatCitesTheRule()
    {
        byte[] pdf = PageWithAnnots(
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Launch /F (a.exe) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Launch /F (b.exe) >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /ImportData /F (c.fdf) >> >>");

        var a02 = Violations(pdf, PdfAConformancePart.Part1).Where(v => v.Code == "A02").ToArray();

        Assert.Equal(2, a02.Length);
        Assert.Contains(a02, v => v.Message.Contains("/Launch action") && v.Message.Contains("2 found, the first on an annotation on page 1"));
        Assert.Contains(a02, v => v.Message.Contains("/ImportData action") && v.Message.Contains("found on an annotation on page 1"));
        Assert.All(a02, v => Assert.Contains("ISO 19005-1 6.6.1; rule 6.6.1-1", v.Message, System.StringComparison.Ordinal));
    }

    /// <summary>
    /// When the dispatcher could not list every action, the type check did not run in full, and
    /// says so: an A99 naming A02 and A22. A chain of 300 GoTo actions passes the per-chain bound
    /// of 256; the same document with a chain of 10 is the control.
    /// </summary>
    [Fact]
    public void AnActionListCutAtItsBoundIsReportedAsPartial()
    {
        var result = PdfAValidator.Validate(new MemoryStream(Doc("/OpenAction 4 0 R", "", "%PDF-1.7", Chain(300))), PdfAConformancePart.Part2);
        var control = PdfAValidator.Validate(new MemoryStream(Doc("/OpenAction 4 0 R", "", "%PDF-1.7", Chain(10))), PdfAConformancePart.Part2);

        Assert.Contains(result.Informational, i => i.Code == "A99" && i.Message.Contains("A02/A22"));
        Assert.DoesNotContain(control.Informational, i => i.Code == "A99" && i.Message.Contains("A02/A22"));
    }

    // ── named actions: A22 ───────────────────────────────────────────────────

    /// <summary>
    /// ISO 19005-1 6.6.1 / 19005-2 6.5.1 / 19005-4 6.6.1 (the published profiles 6.6.1-2, 6.5.1-2): named actions
    /// other than NextPage, PrevPage, FirstPage and LastPage are not permitted.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part1)]
    [InlineData(PdfAConformancePart.Part2)]
    [InlineData(PdfAConformancePart.Part4)]
    public void ANamedActionOtherThanPageNavigationIsForbidden(PdfAConformancePart part)
    {
        byte[] pdf = PageWithAnnots("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Named /N /Print >> >>");

        Assert.Contains(Codes(pdf, part), c => c == "A22");
    }

    /// <summary>A named action with no <c>/N</c> is not one of the four either.</summary>
    [Fact]
    public void ANamedActionWithNoNameIsForbidden()
    {
        byte[] pdf = PageWithAnnots("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Named >> >>");

        Assert.Contains(Violations(pdf, PdfAConformancePart.Part3), v => v.Code == "A22" && v.Message.Contains("missing its /N"));
    }

    // ── a widget's action: A23 ───────────────────────────────────────────────

    /// <summary>
    /// ISO 19005-1 6.6.1 (the published profiles 6.6.1-3), 19005-2/3 6.4.1 (6.4.1-1), 19005-4 6.4.1 (6.4.1-1): a
    /// widget annotation shall not have an <c>/A</c> entry, even a permitted action.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part1)]
    [InlineData(PdfAConformancePart.Part2)]
    [InlineData(PdfAConformancePart.Part3)]
    public void AWidgetWithAnActionIsForbidden(PdfAConformancePart part)
    {
        byte[] pdf = Doc("/AcroForm << /Fields [4 0 R] >>", "/Annots [4 0 R]", "%PDF-1.7",
            "<< /T (btn) /FT /Btn /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /A << /S /URI /URI (https://example.test) >> >>");

        Assert.Contains(Codes(pdf, part), c => c == "A23");
    }

    // ── additional actions: A04 ──────────────────────────────────────────────

    /// <summary>
    /// PDF/A-1 forbids <c>/AA</c> on the catalog (6.6.2-3), a widget (6.6.2-1) and a field (6.6.2-2),
    /// and has no rule about a page's. The validator reported the page's and missed the other two.
    /// </summary>
    [Fact]
    public void Part1_AdditionalActionsOnAWidgetOrFieldAreForbiddenAndOnAPageAreNot()
    {
        byte[] widget = Doc("/AcroForm << /Fields [4 0 R] >>", "/Annots [4 0 R]", "%PDF-1.4",
            "<< /T (f) /FT /Tx /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AA << /K << /S /URI /URI (x) >> >> >>");
        byte[] page = Doc("", "/AA << /O << /S /URI /URI (x) >> >>", "%PDF-1.4");

        Assert.Contains(Codes(widget, PdfAConformancePart.Part1), c => c == "A04");
        Assert.DoesNotContain(Codes(page, PdfAConformancePart.Part1), c => c == "A04");
    }

    /// <summary>
    /// A field whose widget is a separate kid is on no page's <c>/Annots</c>, and its <c>/AA</c>
    /// (where keystroke and format scripts live) is a field's: PDF/A-1 6.6.2-2 and PDF/A-2 6.4.1-2.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part1, "6.6.2-2")]
    [InlineData(PdfAConformancePart.Part2, "6.4.1-2")]
    public void AFieldsAdditionalActionsAreForbiddenWhenItsWidgetIsAKid(PdfAConformancePart part, string rule)
    {
        byte[] pdf = Doc("/AcroForm << /Fields [4 0 R] >>", "/Annots [5 0 R]", "%PDF-1.7",
            "<< /T (f) /FT /Tx /Kids [5 0 R] /AA << /K << /S /URI /URI (x) >> >> >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [0 0 10 10] >>");

        var a04 = Violations(pdf, part).Where(v => v.Code == "A04").ToArray();

        Assert.Contains(a04, v => v.Message.Contains("on a form field") && v.Message.Contains("rule " + rule));
        Assert.DoesNotContain(a04, v => v.Message.Contains("on a widget annotation"));
    }

    /// <summary>
    /// A field tree deeper than the walk's depth cap is examined in part, and the A04 check says so.
    /// A shallow tree is the control.
    /// </summary>
    [Fact]
    public void AFieldTreeCutAtItsDepthCapIsReportedAsPartial()
    {
        var deep = PdfAValidator.Validate(new MemoryStream(NestedFields(AcroFormFieldWalk.MaxFieldDepth + 3)), PdfAConformancePart.Part1);
        var shallow = PdfAValidator.Validate(new MemoryStream(NestedFields(3)), PdfAConformancePart.Part1);

        Assert.Contains(deep.Informational, i => i.Code == "A99" && i.Message.Contains("A04"));
        Assert.DoesNotContain(shallow.Informational, i => i.Code == "A99" && i.Message.Contains("A04"));
    }

    /// <summary>
    /// Parts 1 to 3 have no rule about a non-widget annotation's <c>/AA</c>; PDF/A-4 lets it hold
    /// the six mouse and focus triggers and nothing else, so a page-open trigger is reported there.
    /// </summary>
    [Fact]
    public void ALinksAdditionalActionsAreLimitedOnlyInPartFour()
    {
        byte[] pageOpen = PageWithAnnots("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /AA << /PO << /S /URI /URI (x) >> >> >>");
        byte[] enter = PageWithAnnots("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /AA << /E << /S /URI /URI (x) >> >> >>");

        Assert.DoesNotContain(Codes(pageOpen, PdfAConformancePart.Part2), c => c == "A04");
        Assert.Contains(Violations(pageOpen, PdfAConformancePart.Part4), v => v.Code == "A04" && v.Message.Contains("holds /PO"));
        Assert.DoesNotContain(Codes(enter, PdfAConformancePart.Part4), c => c == "A04");
    }

    /// <summary>
    /// PDF/A-4 puts no limit on a widget's <c>/AA</c> (6.6.3-1 excludes it), and still forbids its
    /// <c>/A</c> (6.4.1-1).
    /// </summary>
    [Fact]
    public void Part4_AWidgetsAdditionalActionsAreFreeAndItsActionIsNot()
    {
        byte[] aa = Doc("/AcroForm << /Fields [4 0 R] >>", "/Annots [4 0 R]", "%PDF-2.0",
            "<< /T (f) /FT /Tx /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AA << /K << /S /URI /URI (x) >> >> >>");
        byte[] a = Doc("/AcroForm << /Fields [4 0 R] >>", "/Annots [4 0 R]", "%PDF-2.0",
            "<< /T (f) /FT /Btn /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /A << /S /URI /URI (x) >> >>");

        Assert.DoesNotContain(Codes(aa, PdfAConformancePart.Part4), c => c == "A04");
        Assert.Contains(Codes(a, PdfAConformancePart.Part4), c => c == "A23");
    }

    /// <summary>
    /// The catalog's <c>/AA</c> is forbidden in parts 1 to 3 (PDF/A-1 6.6.2-3, PDF/A-2/3 6.5.2-1).
    /// </summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part1, "6.6.2-3")]
    [InlineData(PdfAConformancePart.Part2, "6.5.2-1")]
    [InlineData(PdfAConformancePart.Part3, "6.5.2-1")]
    public void TheCatalogsAdditionalActionsAreForbiddenInPartsOneToThree(PdfAConformancePart part, string rule)
    {
        byte[] pdf = Doc("/AA << /WC << /S /URI /URI (x) >> >>", "");

        Assert.Contains(Violations(pdf, part), v => v.Code == "A04"
            && v.Message.Contains("on the document catalog") && v.Message.Contains("rule " + rule));
    }

    /// <summary>
    /// A widget that is a kid of its field is on a page and in no field walk, and its <c>/AA</c> is a
    /// widget's: PDF/A-1 6.6.2-1, PDF/A-2 6.4.1-1. The merged shape cannot tell the two rules apart,
    /// because one dictionary is both.
    /// </summary>
    [Theory]
    [InlineData(PdfAConformancePart.Part1, "6.6.2-1")]
    [InlineData(PdfAConformancePart.Part2, "6.4.1-1")]
    public void AWidgetKidsAdditionalActionsAreAWidgets(PdfAConformancePart part, string rule)
    {
        byte[] pdf = Doc("/AcroForm << /Fields [4 0 R] >>", "/Annots [5 0 R]", "%PDF-1.7",
            "<< /T (f) /FT /Tx /Kids [5 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Parent 4 0 R /Rect [0 0 10 10] /AA << /E << /S /URI /URI (x) >> >> >>");

        var a04 = Violations(pdf, part).Where(v => v.Code == "A04").ToArray();

        Assert.Contains(a04, v => v.Message.Contains("on a widget annotation") && v.Message.Contains("rule " + rule));
        Assert.DoesNotContain(a04, v => v.Message.Contains("on a form field"));
    }

    /// <summary>
    /// An annotation listed twice in <c>/Annots</c> is one annotation, counted once. A second,
    /// distinct widget is the control that the count does move.
    /// </summary>
    [Fact]
    public void AnAnnotationListedTwiceIsCountedOnce()
    {
        const string widget = "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /A << /S /URI /URI (x) >> >>";
        byte[] twice = Doc("", "/Annots [4 0 R 4 0 R]", "%PDF-1.7", widget);
        byte[] two = Doc("", "/Annots [4 0 R 5 0 R]", "%PDF-1.7", widget, widget);

        Assert.Contains(Violations(twice, PdfAConformancePart.Part2), v => v.Code == "A23" && v.Message.Contains("; found on"));
        Assert.Contains(Violations(two, PdfAConformancePart.Part2), v => v.Code == "A23" && v.Message.Contains("; 2 found"));
    }

    /// <summary>PDF/A-2 forbids <c>/AA</c> on a page (6.5.2-2) and on a widget (6.4.1-1).</summary>
    [Fact]
    public void Part2_AdditionalActionsOnAPageOrAWidgetAreForbidden()
    {
        byte[] page = Doc("", "/AA << /O << /S /URI /URI (x) >> >>");
        byte[] widget = Doc("/AcroForm << /Fields [4 0 R] >>", "/Annots [4 0 R]", "%PDF-1.7",
            "<< /T (f) /FT /Tx /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AA << /K << /S /URI /URI (x) >> >> >>");

        Assert.Contains(Codes(page, PdfAConformancePart.Part2), c => c == "A04");
        Assert.Contains(Codes(widget, PdfAConformancePart.Part2), c => c == "A04");
    }

    /// <summary>
    /// PDF/A-4 6.6.3 (the published profiles 6.6.3-1): the catalog's, a page's or a non-widget annotation's
    /// <c>/AA</c> may hold only E, X, D, U, Fo and Bl.
    /// </summary>
    [Fact]
    public void Part4_AdditionalActionsMayHoldOnlyTheMouseAndFocusTriggers()
    {
        byte[] willClose = Doc("/AA << /WC << /S /URI /URI (x) >> >>", "", "%PDF-2.0");
        byte[] mouseUp = Doc("/AA << /U << /S /URI /URI (x) >> >>", "", "%PDF-2.0");

        Assert.Contains(Codes(willClose, PdfAConformancePart.Part4), c => c == "A04");
        Assert.DoesNotContain(Codes(mouseUp, PdfAConformancePart.Part4), c => c == "A04");
    }

    // ── the converter ────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="PdfAConverter"/> validates what its sanitizer leaves and blocks on it. The
    /// sanitizer keeps a named <c>/Print</c>, which is an A22 now, so the document is not stamped
    /// PDF/A; a GoTo link is the control, and is.
    /// </summary>
    [Fact]
    public void TheConverterWithholdsTheIdentifierForANamedPrintAndNotForAGoTo()
    {
        var print = Convert(PageWithAnnots("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /Named /N /Print >> >>"));
        var goTo = Convert(PageWithAnnots("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /A << /S /GoTo /D [3 0 R /Fit] >> >>"));

        Assert.False(print.DeclaredPdfAIdentifier);
        Assert.Contains(print.Blockers, b => b.StartsWith("A22", System.StringComparison.Ordinal));
        Assert.True(goTo.DeclaredPdfAIdentifier, string.Join(" | ", goTo.Blockers));

        static PdfAConversionResult Convert(byte[] pdf)
            => PdfAConverter.ConvertToPdfA1b(new MemoryStream(pdf), new MemoryStream(),
                new PdfAConversionOptions { IccProfile = IccProfileFixtures.Rgb() });
    }

    /// <summary>
    /// PDF/A-4e permits <c>/SetOCGState</c> and <c>/GoTo3DView</c>; PDF/A-4 and PDF/A-4f do not
    /// (the published profile's PDF/A-4e profile, rule 6.6.1-1, against its PDF/A-4 and -4f profiles). The level is
    /// the XMP's <c>pdfaid:conformance</c>; the conformance corpus passes both actions under
    /// <c>PDF_A-4e</c> (6-6-1-t01-pass-a, -b) and fails them under <c>PDF_A-4</c> (-fail-i, -j).
    /// </summary>
    [Theory]
    [InlineData("/SetOCGState /State [/ON 9 0 R]", "E", false)]
    [InlineData("/SetOCGState /State [/ON 9 0 R]", "F", true)]
    [InlineData("/SetOCGState /State [/ON 9 0 R]", null, true)]
    [InlineData("/GoTo3DView /TA 9 0 R /V 0", "E", false)]
    [InlineData("/GoTo3DView /TA 9 0 R /V 0", null, true)]
    public void Part4_TheEngineeringLevelAlonePermitsSetOCGStateAndGoTo3DView(
        string action, string conformance, bool reportsA02)
    {
        byte[] pdf = Doc("/OpenAction << /S " + action + " >> /Metadata 4 0 R", "", "%PDF-2.0",
            XmpStream(conformance));

        var a02 = Violations(pdf, PdfAConformancePart.Part4).Where(v => v.Code == "A02").ToArray();

        Assert.Equal(reportsA02, a02.Length > 0);
        if (reportsA02) Assert.Contains("PDF/A-4 does not permit", a02[0].Message, StringComparison.Ordinal);
    }

    /// <summary>A metadata stream declaring PDF/A-4 rev 2020 with <paramref name="conformance"/>, or none.</summary>
    private static string XmpStream(string conformance)
    {
        string packet = "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + "<pdfaid:part>4</pdfaid:part><pdfaid:rev>2020</pdfaid:rev>"
            + (conformance == null ? "" : "<pdfaid:conformance>" + conformance + "</pdfaid:conformance>")
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return "<< /Type /Metadata /Subtype /XML /Length " + packet.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + packet + "\nendstream";
    }

    // ------------------------------------------------------------------ helpers

    private static string[] Codes(byte[] pdf, PdfAConformancePart part)
        => Violations(pdf, part).Select(v => v.Code).ToArray();

    private static PdfAViolation[] Violations(byte[] pdf, PdfAConformancePart part)
        => PdfAValidator.Validate(new MemoryStream(pdf), part).Violations.ToArray();

    /// <summary>A <c>/Next</c> chain of <paramref name="length"/> GoTo actions, objects 4 onwards.</summary>
    private static string[] Chain(int length)
        => Enumerable.Range(0, length).Select(i =>
            i + 1 < length
                ? $"<< /S /GoTo /D [3 0 R /Fit] /Next {i + 5} 0 R >>"
                : "<< /S /GoTo /D [3 0 R /Fit] >>").ToArray();

    /// <summary>A field tree <paramref name="depth"/> levels deep, each node the one kid of the one above.</summary>
    private static byte[] NestedFields(int depth)
    {
        var fields = Enumerable.Range(0, depth).Select(i =>
            i + 1 < depth
                ? $"<< /T (f{i}) /Kids [{i + 5} 0 R] >>"
                : $"<< /T (f{i}) /FT /Tx >>").ToArray();
        return Doc("/AcroForm << /Fields [4 0 R] >>", "", "%PDF-1.4", fields);
    }

    private static byte[] PageWithAnnots(params string[] annots)
    {
        var refs = string.Join(" ", Enumerable.Range(4, annots.Length).Select(n => n + " 0 R"));
        return Doc("", "/Annots [" + refs + "]", "%PDF-1.7", annots);
    }

    /// <summary>Catalog 1, pages 2, page 3, then <paramref name="objects"/> from 4.</summary>
    private static byte[] Doc(string catalog, string page, string header = "%PDF-1.7", params string[] objects)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R " + catalog + " >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " + page + " >>",
        }.Concat(objects).ToArray();

        var sb = new StringBuilder(header + "\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
