// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// ISO 14289-2 §8.2.5.20 (link annotations that target different locations are in separate Link or Reference
/// elements), §8.9.2.3 (a markup annotation's /RC and /Contents textually equivalent) and §8.10.3.3 (a text
/// field's /RV and /V textually equivalent).
/// </summary>
public sealed class Ua2AnnotationRuleTests
{
    // -------------------------------------------------------------- §8.2.5.20

    /// <summary>
    /// Two link annotations under one Link element: the same target passes, two targets are reported; a
    /// destination is its page, a named one followed to it; a target that cannot be read is not compared.
    /// </summary>
    [Theory]
    [InlineData("/A << /S /URI /URI (https://a.example/) >>", "/A << /S /URI /URI (https://a.example/) >>", "", false)]
    [InlineData("/A << /S /URI /URI (https://a.example/) >>", "/A << /S /URI /URI (https://b.example/) >>", "", true)]
    [InlineData("/Dest [4 0 R /Fit]", "/Dest [4 0 R /XYZ 0 100 0]", "", false)]
    [InlineData("/Dest [4 0 R /Fit]", "/Dest [7 0 R /Fit]", "", true)]
    [InlineData("/Dest /Intro", "/Dest [7 0 R /Fit]", " /Dests << /Intro [7 0 R /Fit] >>", false)]
    [InlineData("/Dest /Intro", "/Dest [7 0 R /Fit]", " /Dests << /Intro [4 0 R /Fit] >>", true)]
    [InlineData("/Dest (intro)", "/Dest [7 0 R /Fit]", " /Names << /Dests << /Names [(intro) [4 0 R /Fit]] >> >>", true)]
    [InlineData("/Dest (intro)", "/Dest [4 0 R /Fit]", " /Names << /Dests << /Names [(intro) [4 0 R /Fit]] >> >>", false)]
    [InlineData("/Dest /Missing", "/Dest [7 0 R /Fit]", "", false)]
    [InlineData("/A << /S /GoTo /D [4 0 R /Fit] >>", "/Dest [4 0 R /FitH 10]", "", false)]
    [InlineData("/A << /S /GoTo /D [4 0 R /Fit] >>", "/A << /S /GoTo /D [7 0 R /Fit] >>", "", true)]
    [InlineData("/A << /S /JavaScript /JS (app.alert(1)) >>", "/A << /S /URI /URI (https://a.example/) >>", "", false)]
    [InlineData("/A << /S /GoToR /F (other.pdf) /D [0 /Fit] >>", "/A << /S /GoToR /F (other.pdf) /D [0 /XYZ 0 0 0] >>", "", false)]
    [InlineData("/A << /S /GoToR /F (other.pdf) /D [0 /Fit] >>", "/A << /S /GoToR /F (other.pdf) /D [1 /Fit] >>", "", true)]
    [InlineData("/A << /S /GoToR /F (other.pdf) /D /Ch1 >>", "/A << /S /GoToR /F << /UF (other.pdf) >> /D /Ch1 >>", "", false)]
    [InlineData("/A << /S /GoToR /F << /UF (a.pdf) >> /D /Ch1 >>", "/A << /S /GoToR /F << /F (b.pdf) >> /D /Ch1 >>", "", true)]
    [InlineData("/Dest /Intro", "/Dest [7 0 R /Fit]", " /Dests << /Intro << /D [4 0 R /Fit] >> >>", true)]
    [InlineData("/Dest /Intro", "/Dest [7 0 R /Fit]", " /Dests << /Intro << /D [7 0 R /Fit] >> >>", false)]
    public void LinkAnnotationsWithDifferentTargets_AreInSeparateElements(string first, string second, string catalogExtra, bool reported)
    {
        var found = Coded(LinkPdf("/S /Link /NS 6 0 R", first, second, catalogExtra), "UA2-8.2.5.20")
            .Where(v => v.Message.Contains("different locations")).ToList();
        Assert.Equal(reported, found.Any());
        if (reported) Assert.Contains("A Link structure element (object 8) encloses link annotations that target 2 different locations", found[0].Message);
    }

    /// <summary>A Reference, and a custom type role mapped to Link, are held to it as a Link is; a P is not a link element.</summary>
    [Theory]
    [InlineData("/S /Reference /NS 6 0 R", "", true)]
    [InlineData("/S /Hyper", " /RoleMap << /Hyper /Link >>", true)]
    [InlineData("/S /Hyper", "", false)]
    public void TheRuleHoldsForEveryLinkElement(string element, string roleMap, bool reported)
    {
        byte[] pdf = LinkPdf(element, "/A << /S /URI /URI (https://a.example/) >>", "/A << /S /URI /URI (https://b.example/) >>", "", roleMap);
        Assert.Equal(reported, Coded(pdf, "UA2-8.2.5.20").Any(v => v.Message.Contains("different locations")));
    }

    // -------------------------------------------------------------- §8.9.2.3

    /// <summary>
    /// A markup annotation's /RC against its /Contents: the same text passes however it is marked up and wherever
    /// its paragraphs break, different text is reported; an annotation that is not markup is not judged.
    /// </summary>
    [Theory]
    [InlineData("/Text", "(<p>Hello world</p>)", "(Hello world)", false)]
    [InlineData("/FreeText", "(<?xml version=\"1.0\"?><body xmlns=\"http://www.w3.org/1999/xhtml\"><p>Hello <b>world</b></p></body>)", "(Hello world)", false)]
    [InlineData("/Square", "(<body><p>Hello</p><p>world</p></body>)", "(Hello\\rworld)", false)]
    [InlineData("/Text", "(<p>Hello &amp; welcome</p>)", "(Hello & welcome)", false)]
    [InlineData("/Text", "(<p>Hello</p>)", "(Goodbye)", true)]
    [InlineData("/Highlight", "(<p>Hello</p><p>world</p>)", "(Helloworld)", true)]
    [InlineData("/Link", "(<p>Hello</p>)", "(Goodbye)", false)]
    public void AMarkupAnnotationsRichText_IsTextuallyEquivalentToItsContents(string subtype, string rc, string contents, bool reported)
    {
        byte[] pdf = AnnotationPdf($"<< /Type /Annot /Subtype {subtype} /Rect [0 0 10 10] /RC {rc} /Contents {contents} >>");
        var found = Coded(pdf, "UA2-8.9.2.3").Where(v => v.Message.Contains("not textually equivalent")).ToList();
        Assert.Equal(reported, found.Any());
    }

    /// <summary>A rich text stream is read as a string is; one that will not decode is said not judged.</summary>
    [Fact]
    public void ARichTextStream_IsJudged_AndOneThatWillNotDecodeIsSaid()
    {
        byte[] judged = AnnotationPdf("<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /RC 9 0 R /Contents (Goodbye) >>",
            Stream("<p>Hello</p>"));
        Assert.Contains(Coded(judged, "UA2-8.9.2.3"), v => v.Message.Contains("shows \"Hello\" and /Contents says \"Goodbye\""));

        byte[] tooLong = AnnotationPdf("<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /RC 9 0 R /Contents (Goodbye) >>",
            Stream(new string('a', Core.Text.RichText.MaxStreamBytes + 1)));
        Assert.DoesNotContain(Coded(tooLong, "UA2-8.9.2.3"), v => v.Message.Contains("not textually equivalent"));
        Assert.Contains(Coded(tooLong, "UA2-99"), v => v.Message.StartsWith("§8.9.2.3's textual equivalence was not judged for 1", System.StringComparison.Ordinal));
    }

    // -------------------------------------------------------------- §8.10.3.3

    /// <summary>A text field's /RV against its /V: the same text passes, different text is reported, a /V stream is read.</summary>
    [Theory]
    [InlineData("(<p>Jane Doe</p>)", "(Jane Doe)", false)]
    [InlineData("(<body><p>Jane <i>Doe</i></p></body>)", "(Jane Doe)", false)]
    [InlineData("(<p>Jane Doe</p>)", "(John Doe)", true)]
    [InlineData("(<p>Jane Doe</p>)", "9 0 R", false)]
    public void ATextFieldsRichValue_IsTextuallyEquivalentToItsValue(string rv, string value, bool reported)
    {
        byte[] pdf = AnnotationPdf($"<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [0 0 10 10] /RV {rv} /V {value} >>",
            Stream("Jane Doe"));
        var found = Coded(pdf, "UA2-8.10.3.3").Where(v => v.Message.Contains("not textually equivalent")).ToList();
        Assert.Equal(reported, found.Any());
        if (reported) Assert.Contains("shows \"Jane Doe\" and /V is \"John Doe\"", found[0].Message);
    }

    /// <summary>A field that is not a text field is not held to /RV; the same entries on a text field are.</summary>
    [Theory]
    [InlineData("/Ch", false)]
    [InlineData("/Tx", true)]
    public void OnlyATextFieldsRichValue_IsJudged(string fieldType, bool reported)
    {
        byte[] pdf = AnnotationPdf($"<< /Type /Annot /Subtype /Widget /FT {fieldType} /T (name) /Rect [0 0 10 10] /RV (<p>Jane</p>) /V (John) >>");
        Assert.Equal(reported, Coded(pdf, "UA2-8.10.3.3").Any(v => v.Message.Contains("not textually equivalent")));
    }

    // -------------------------------------------------------------- §8.13.3

    /// <summary>
    /// A URI action with /IsMap true — on the annotation's /A or along its /Next chain — is an advisory, not a
    /// violation: IsConforming does not count it; without /IsMap there is none.
    /// </summary>
    [Theory]
    [InlineData("/A << /S /URI /URI (https://a.example/map) /IsMap true >>", true)]
    [InlineData("/A << /S /GoTo /D [4 0 R /Fit] /Next << /S /URI /URI (https://a.example/map) /IsMap true >> >>", true)]
    [InlineData("/A << /S /URI /URI (https://a.example/map) /IsMap false >>", false)]
    [InlineData("/A << /S /URI /URI (https://a.example/map) >>", false)]
    public void AUriActionWithIsMap_IsAnAdvisory(string action, bool advised)
    {
        PdfUa2StructureResult result = PdfUa2StructureChecker.Validate(new MemoryStream(
            AnnotationPdf($"<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Contents (Map) {action} >>")));
        Assert.Equal(advised, result.Advisories.Any(a => a.Code == "UA2-8.13.3" && a.Message.Contains("/IsMap true")));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.13.3");
    }

    /// <summary>The audit reports the advisory as a Warning, beside the document's errors.</summary>
    [Fact]
    public void TheAudit_ReportsTheIsMapAdvisoryAsAWarning()
    {
        byte[] pdf = AnnotationPdf("<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Contents (Map) " +
                                   "/A << /S /URI /URI (https://a.example/map) /IsMap true >> >>");
        var report = PdfAccessibility.Audit(new MemoryStream(pdf), new AuditOptions { Target = PdfUaVersion.Ua2 });
        var issue = Assert.Single(report.Issues, i => i.Code == "UA2-8.13.3");
        Assert.Equal(IssueSeverity.Warning, issue.Severity);
        Assert.Contains(report.Issues, i => i.Severity == IssueSeverity.Error);
    }

    // -------------------------------------------------------------- fixtures

    /// <summary>
    /// 1 catalog (<paramref name="catalogExtra"/>), 2 structure tree root (<paramref name="roleMap"/>), 3 pages,
    /// 4 and 7 the pages, 5 the Document, 6 the PDF 2.0 namespace, 8 the link element (<paramref name="element"/>)
    /// enclosing the link annotations 10 and 11 by /OBJR, 9 spare.
    /// </summary>
    private static byte[] LinkPdf(string element, string first, string second, string catalogExtra, string roleMap = "")
        => PdfUa2StructureCheckerTests.BuildPdf(new List<string>
        {
            $"<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R{catalogExtra} >>",
            $"<< /Type /StructTreeRoot /K 5 0 R /Namespaces [6 0 R]{roleMap} >>",
            "<< /Type /Pages /Kids [4 0 R 7 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [10 0 R 11 0 R] >>",
            "<< /Type /StructElem /S /Document /NS 6 0 R /P 2 0 R /K [8 0 R] >>",
            "<< /Type /Namespace /NS (http://iso.org/pdf2/ssn) >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
            $"<< /Type /StructElem {element} /P 5 0 R /Pg 4 0 R /K [<< /Type /OBJR /Obj 10 0 R >> << /Type /OBJR /Obj 11 0 R >>] >>",
            "<< >>",
            $"<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Contents (one) /StructParent 0 {first} >>",
            $"<< /Type /Annot /Subtype /Link /Rect [0 20 10 30] /Contents (two) /StructParent 1 {second} >>",
        });

    /// <summary>1 catalog, 2 pages, 3 the page listing annotation 4 (<paramref name="annotation"/>), then <paramref name="extra"/> from 5 — 9 by padding.</summary>
    internal static byte[] AnnotationPdf(string annotation, string extra = "<< >>")
        => PdfUa2StructureCheckerTests.BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            annotation,
            "<< >>", "<< >>", "<< >>", "<< >>",
            extra,
        });

    private static string Stream(string text)
        => "<< /Length " + text.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + text + "\nendstream";

    private static List<PdfUa2StructureViolation> Coded(byte[] pdf, string code)
        => PdfUa2StructureChecker.Validate(new MemoryStream(pdf)).Violations.Where(v => v.Code == code).ToList();
}

/// <summary>
/// §8.13.3's reading of an action chain under <see cref="Core.Constants.PdfLimits.MaxActionChainDepth"/>: a chain
/// longer than it is read to the ceiling, and a UA2-99 says the rest was not.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class Ua2IsMapChainLimitTests
{
    /// <summary>Under the default the /IsMap action at the chain's end is found; past a ceiling of two it is not read, and said.</summary>
    [Fact]
    public void AChainPastTheCeiling_IsCutAndSaid_HostileInput()
    {
        byte[] pdf = Ua2AnnotationRuleTests.AnnotationPdf(
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Contents (Map) /A << /S /GoTo /D [3 0 R /Fit] /Next " +
            "<< /S /GoTo /D [3 0 R /Fit] /Next << /S /URI /URI (https://a.example/map) /IsMap true >> >> >> >>");

        PdfUa2StructureResult result = PdfUa2StructureChecker.Validate(new MemoryStream(pdf));
        Assert.Contains(result.Advisories, a => a.Code == "UA2-8.13.3");
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-99" && v.Message.StartsWith("§8.13.3 stopped", System.StringComparison.Ordinal));

        try
        {
            Core.Constants.PdfLimits.MaxActionChainDepth = 2;
            result = PdfUa2StructureChecker.Validate(new MemoryStream(pdf));
            Assert.DoesNotContain(result.Advisories, a => a.Code == "UA2-8.13.3");
            Assert.Contains(result.Violations, v => v.Code == "UA2-99" && v.Message.StartsWith("§8.13.3 stopped", System.StringComparison.Ordinal));
        }
        finally
        {
            Core.Constants.PdfLimits.ResetToDefaults();
        }
    }
}
