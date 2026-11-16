// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Tests for <see cref="PdfUa2StructureChecker"/> — the PDF/UA-2 structure-tree deep checker's
/// first slice, ISO 14289-2:2024 §8.2.5.2 ("Document and DocumentFragment"): the structure tree
/// root must contain a single Document structure element as its only child, in the PDF 2.0
/// standard structure namespace.
/// </summary>
public class PdfUa2StructureCheckerTests
{
    private const string Pdf2Namespace = "http://iso.org/pdf2/ssn";
    private const string Pdf17Namespace = "http://iso.org/pdf/ssn";

    // =====================================================================
    //  Fixtures
    // =====================================================================

    internal static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
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
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        // Latin-1 round-trips every byte value 0-255 through a char 1:1 (unlike ASCII, which
        // would corrupt anything above 0x7F) — needed so a binary embedded font program can
        // travel through this string-based builder unmodified, matching the same pattern
        // SimpleFontWidthProbeTests.Build/StreamObj already use.
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Wraps raw binary bytes (an embedded font program) as a PDF stream object body.</summary>
    private static string StreamObj(byte[] data, string extraDictEntries = "")
    {
        string raw = Encoding.Latin1.GetString(data);
        return $"<< /Length {data.Length.ToString(CultureInfo.InvariantCulture)}{extraDictEntries} >>\n" +
               $"stream\n{raw}\nendstream";
    }

    /// <summary>
    /// Object 1: catalog with /StructTreeRoot 2 0 R (or none, when <paramref name="omitStructTreeRoot"/>).
    /// Object 2: struct tree root with /K set from <paramref name="rootKids"/>.
    /// Object 3: /Pages, object 4: the one page. Objects 5+: whatever
    /// <paramref name="extraObjects"/> supplies, in order.
    /// </summary>
    private static byte[] Pdf(string rootKids, bool omitStructTreeRoot, params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            omitStructTreeRoot
                ? "<< /Type /Catalog /Pages 3 0 R /Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>"
                : "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /Lang (en-US) " +
                  "/ViewerPreferences << /DisplayDocTitle true >> >>",
            $"<< /Type /StructTreeRoot /K {rootKids} >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
        };
        bodies.AddRange(extraObjects);
        return BuildPdf(bodies);
    }

    /// <summary>
    /// <see cref="Pdf"/>'s exact object layout, with a <c>/ClassMap</c> on the struct tree root —
    /// the second place ISO 32000-2 §14.7.6.2 lets a producer declare an attribute, which
    /// <see cref="Pdf"/>'s fixed struct tree root body has no hook for.
    /// </summary>
    private static byte[] PdfWithClassMap(string classMap, string rootKids, params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /Lang (en-US) " +
            "/ViewerPreferences << /DisplayDocTitle true >> >>",
            $"<< /Type /StructTreeRoot /K {rootKids} /ClassMap {classMap} >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
        };
        bodies.AddRange(extraObjects);
        return BuildPdf(bodies);
    }

    private static string DocumentElement(int nsObjectNumber)
        => $"<< /Type /StructElem /S /Document /P 2 0 R /NS {nsObjectNumber} 0 R >>";

    private static string DocumentElementNoNs()
        => "<< /Type /StructElem /S /Document /P 2 0 R >>";

    private static string DocumentElementWithChild(int nsObjectNumber, int childObjectNumber)
        => $"<< /Type /StructElem /S /Document /P 2 0 R /NS {nsObjectNumber} 0 R " +
           $"/K {childObjectNumber} 0 R >>";

    private static string ChildElement(string role, int parentObjectNumber)
        => $"<< /Type /StructElem /S /{role} /P {parentObjectNumber} 0 R >>";

    private static string FigureElement(int parentObjectNumber, string extraEntries = "")
        => $"<< /Type /StructElem /S /Figure /P {parentObjectNumber} 0 R {extraEntries} >>";

    private static string ContainerElement(string role, int parentObjectNumber, params int[] childObjectNumbers)
        => $"<< /Type /StructElem /S /{role} /P {parentObjectNumber} 0 R " +
           $"/K [{string.Join(" ", childObjectNumbers.Select(n => n + " 0 R"))}] >>";

    private static string NamespaceDict(string nsName)
        => $"<< /Type /Namespace /NS ({nsName}) >>";

    // =====================================================================
    //  The conforming shape
    // =====================================================================

    [Fact]
    public void SingleDocumentChild_InPdf2Namespace_ReportsNoViolation()
    {
        // Bypasses Pdf() (whose fixed catalog has no /Metadata hook) so this stays a genuinely
        // fully-conforming fixture, including a valid dc:title, for the Assert.Empty below.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /Lang (en-US) " +
            "/ViewerPreferences << /DisplayDocTitle true >> /Metadata 7 0 R >>",
            // /Namespaces declares what the Document element names: ISO 32000-2 Table 354 makes it
            // the array of namespace dictionaries the document uses, so a fixture that claims to be
            // fully conforming has to carry it. It did not until CheckNamespacesAreDeclared existed.
            "<< /Type /StructTreeRoot /K [5 0 R] /Namespaces [6 0 R] >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
            DocumentElement(6), // object 5
            NamespaceDict(Pdf2Namespace), // object 6
            $"<< /Type /Metadata /Subtype /XML /Length {XmpWithTitle.Length} >>\n" +
            $"stream\n{XmpWithTitle}\nendstream", // object 7
        });

        var result = Validate(pdf);
        Assert.Empty(result.Violations);
        Assert.True(result.IsConforming);
    }

    // =====================================================================
    //  Missing structure tree root
    // =====================================================================

    [Fact]
    public void NoStructTreeRoot_ReportsUa2825()
    {
        byte[] pdf = Pdf("[]", omitStructTreeRoot: true);

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.2");
    }

    // =====================================================================
    //  Child count
    // =====================================================================

    [Fact]
    public void NoChildren_ReportsUa2825()
    {
        byte[] pdf = Pdf("[]", omitStructTreeRoot: false);

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.2" && v.Message.Contains("exactly one"));
    }

    [Fact]
    public void TwoChildren_ReportsUa2825()
    {
        byte[] pdf = Pdf("[5 0 R 7 0 R]", omitStructTreeRoot: false,
            DocumentElement(6),        // object 5
            NamespaceDict(Pdf2Namespace), // object 6
            DocumentElement(6));       // object 7 — a second Document, reusing the same namespace object

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.2" && v.Message.Contains("exactly one"));
    }

    // =====================================================================
    //  Role
    // =====================================================================

    [Fact]
    public void ChildIsNotDocument_ReportsUa2825()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            "<< /Type /StructElem /S /Part /P 2 0 R >>"); // object 5

        var result = Validate(pdf);
        Assert.Contains(result.Violations,
            v => v.Code == "UA2-8.2.5.2" && v.Message.Contains("Document structure element"));
    }

    // =====================================================================
    //  Namespace
    // =====================================================================

    [Fact]
    public void DocumentWithNoNsEntry_ReportsUa2825()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementNoNs()); // object 5

        var result = Validate(pdf);
        Assert.Contains(result.Violations,
            v => v.Code == "UA2-8.2.5.2" && v.Message.Contains("namespace"));
    }

    [Fact]
    public void DocumentInPdf17Namespace_ReportsUa2825()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElement(6),         // object 5
            NamespaceDict(Pdf17Namespace)); // object 6 — the WRONG (PDF 1.7) namespace

        var result = Validate(pdf);
        Assert.Contains(result.Violations,
            v => v.Code == "UA2-8.2.5.2" && v.Message.Contains("namespace"));
    }

    // =====================================================================
    //  /K shapes other than a one-element array
    // =====================================================================

    /// <summary>A single (non-array) /K value pointing directly at the Document element is the
    /// same "one child" shape as a one-element array and must be accepted identically.</summary>
    [Fact]
    public void SingleDirectKReference_NotAnArray_IsAccepted()
    {
        // Bypasses Pdf() (whose fixed catalog has no /Metadata hook) so this stays a genuinely
        // fully-conforming fixture, including a valid dc:title, for the Assert.Empty below.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /Lang (en-US) " +
            "/ViewerPreferences << /DisplayDocTitle true >> /Metadata 7 0 R >>",
            // /K is a direct reference rather than an array here — that is this test's subject —
            // and /Namespaces is present for the same reason as in the fixture above.
            "<< /Type /StructTreeRoot /K 5 0 R /Namespaces [6 0 R] >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
            DocumentElement(6),
            NamespaceDict(Pdf2Namespace),
            $"<< /Type /Metadata /Subtype /XML /Length {XmpWithTitle.Length} >>\n" +
            $"stream\n{XmpWithTitle}\nendstream",
        });

        var result = Validate(pdf);
        Assert.Empty(result.Violations);
    }

    // =====================================================================
    //  Clause 8.2.5.12 — the H structure type is prohibited
    // =====================================================================

    [Fact]
    public void HStructureType_Anywhere_ReportsUa28212()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),  // object 5
            ChildElement("H", 5),            // object 6
            NamespaceDict(Pdf2Namespace));   // object 7

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.12");
    }

    [Fact]
    public void NumberedHeading_H1_DoesNotReportUa28212()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),
            ChildElement("H1", 5),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.12");
    }

    // =====================================================================
    //  Clause 8.2.5.14.1 — the Note structure type is prohibited
    // =====================================================================

    [Fact]
    public void NoteStructureType_Anywhere_ReportsUa2825141()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),
            ChildElement("Note", 5),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.14.1");
    }

    [Fact]
    public void FENote_DoesNotReportUa2825141()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),
            ChildElement("FENote", 5),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.14.1");
    }

    // =====================================================================
    //  Clause 8.2.5.14.1 — a citation's /Ref to an FENote must be named back
    // =====================================================================
    //
    //  Layout: 5=Document (K [7 8]), 6=namespace, 7=the citing element, 8=the FENote.

    private static byte[] CitationAndNote(string citation, string note)
        => Pdf("[5 0 R]", omitStructTreeRoot: false,
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 6 0 R /K [7 0 R 8 0 R] >>", // object 5
            NamespaceDict(Pdf2Namespace),                                              // object 6
            citation,                                                                  // object 7
            note);                                                                     // object 8

    private static bool IsFENoteRefFinding(PdfUa2StructureViolation v)
        => v.Code == "UA2-8.2.5.14.1" && v.Message.Contains("cites the FENote");

    [Fact]
    public void CitationAndFENoteNameEachOther_DoesNotReportUa825141()
    {
        var result = Validate(CitationAndNote(
            "<< /Type /StructElem /S /P /P 5 0 R /Ref [8 0 R] >>",
            "<< /Type /StructElem /S /FENote /P 5 0 R /Ref [7 0 R] >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.14.1");
    }

    [Fact]
    public void CitationRefToFENoteWithNoBackRef_ReportsUa825141()
    {
        var result = Validate(CitationAndNote(
            "<< /Type /StructElem /S /P /P 5 0 R /Ref [8 0 R] >>",
            "<< /Type /StructElem /S /FENote /P 5 0 R >>"));
        var finding = Assert.Single(result.Violations, IsFENoteRefFinding);
        Assert.Contains("object 7", finding.Message);
        Assert.Contains("object 8", finding.Message);
    }

    [Fact]
    public void FENoteRefNamesSomethingElse_ReportsUa825141()
    {
        // The FENote has a /Ref, but it names the Document, not this citation.
        var result = Validate(CitationAndNote(
            "<< /Type /StructElem /S /P /P 5 0 R /Ref [8 0 R] >>",
            "<< /Type /StructElem /S /FENote /P 5 0 R /Ref [5 0 R] >>"));
        Assert.Contains(result.Violations, IsFENoteRefFinding);
    }

    [Fact]
    public void CitationRefToANonFENote_DoesNotReportUa825141()
    {
        var result = Validate(CitationAndNote(
            "<< /Type /StructElem /S /P /P 5 0 R /Ref [8 0 R] >>",
            "<< /Type /StructElem /S /Aside /P 5 0 R >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.14.1");
    }

    [Fact]
    public void FENoteNamingAnElementWithNoRef_IsNotCheckedConversely_DoesNotReportUa825141()
    {
        // Only a /Ref the citing element itself carries makes it a citation; an FENote's /Ref can
        // name something that is not one.
        var result = Validate(CitationAndNote(
            "<< /Type /StructElem /S /P /P 5 0 R >>",
            "<< /Type /StructElem /S /FENote /P 5 0 R /Ref [7 0 R] >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.14.1");
    }

    [Fact]
    public void FENoteNamingAnotherFENoteOneSided_ReportsUa825141()
    {
        var result = Validate(CitationAndNote(
            "<< /Type /StructElem /S /FENote /P 5 0 R /Ref [8 0 R] >>",
            "<< /Type /StructElem /S /FENote /P 5 0 R >>"));
        Assert.Contains(result.Violations, IsFENoteRefFinding);
    }

    [Fact]
    public void CitationNamingTheSameFENoteTwice_ReportsOnce_Ua825141()
    {
        var result = Validate(CitationAndNote(
            "<< /Type /StructElem /S /P /P 5 0 R /Ref [8 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /FENote /P 5 0 R >>"));
        Assert.Single(result.Violations, IsFENoteRefFinding);
    }

    // =====================================================================
    //  Clause 8.2.5.14.2 — an FENote-owned NoteType must be Footnote, Endnote or None
    // =====================================================================

    private static byte[] NoteWithAttributes(string noteExtraEntries)
        => Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),                                          // object 5
            NamespaceDict(Pdf2Namespace),                                            // object 6
            $"<< /Type /StructElem /S /FENote /P 5 0 R {noteExtraEntries} >>");      // object 7

    [Theory]
    [InlineData("Footnote")]
    [InlineData("Endnote")]
    [InlineData("None")]
    public void FENoteTypeInTheValueSet_DoesNotReportUa825142(string noteType)
    {
        var result = Validate(NoteWithAttributes($"/A << /O /FENote /NoteType /{noteType} >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.14.2");
    }

    [Fact]
    public void FENoteTypeOutsideTheValueSet_ReportsUa825142()
    {
        var result = Validate(NoteWithAttributes("/A << /O /FENote /NoteType /Sidenote >>"));
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.14.2");
    }

    [Fact]
    public void FENoteTypeThatIsNotAName_ReportsUa825142()
    {
        var result = Validate(NoteWithAttributes("/A << /O /FENote /NoteType (Footnote) >>"));
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.14.2");
    }

    [Fact]
    public void NoteTypeUnderAnotherOwner_IsNotJudged_DoesNotReportUa825142()
    {
        var result = Validate(NoteWithAttributes("/A << /O /Layout /NoteType /Sidenote >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.14.2");
    }

    [Fact]
    public void FENoteTypeThroughClassMap_OutsideTheValueSet_ReportsUa825142()
    {
        byte[] pdf = PdfWithClassMap("<< /Side << /O /FENote /NoteType /Sidenote >> >>", "[5 0 R]",
            DocumentElementWithChild(6, 7),                               // object 5
            NamespaceDict(Pdf2Namespace),                                 // object 6
            "<< /Type /StructElem /S /FENote /P 5 0 R /C /Side >>");      // object 7

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.14.2");
    }

    // =====================================================================
    //  Clause 8.2.5.23 — Ruby must contain exactly one RB and one RT
    // =====================================================================

    [Fact]
    public void RubyWithOneRbAndOneRt_DoesNotReportUa282523()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(9, 6),   // object 5
            ContainerElement("Ruby", 5, 7, 8), // object 6
            ChildElement("RB", 6),             // object 7
            ChildElement("RT", 6),             // object 8
            NamespaceDict(Pdf2Namespace));     // object 9

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.23");
    }

    /// <summary>The four-element parenthetical form (RB, RP, RT, RP) still has exactly one RB
    /// and one RT, and must not be reported — this check deliberately does not enforce the
    /// four-element order/count, only the universal "one RB, one RT" half.</summary>
    [Fact]
    public void RubyWithRpDelimiters_DoesNotReportUa282523()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(11, 6),
            ContainerElement("Ruby", 5, 7, 8, 9, 10),
            ChildElement("RB", 6),
            ChildElement("RP", 6),
            ChildElement("RT", 6),
            ChildElement("RP", 6),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.23");
    }

    [Fact]
    public void RubyMissingRt_ReportsUa282523()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(8, 6),
            ContainerElement("Ruby", 5, 7),
            ChildElement("RB", 6),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.23");
    }

    [Fact]
    public void RubyWithTwoRb_ReportsUa282523()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(10, 6),
            ContainerElement("Ruby", 5, 7, 8, 9),
            ChildElement("RB", 6),
            ChildElement("RB", 6),
            ChildElement("RT", 6),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.23");
    }

    // =====================================================================
    //  Clause 8.2.5.24 — Warichu must be exactly [WP, WT, WP]
    // =====================================================================

    [Fact]
    public void WarichuWithCorrectSequence_DoesNotReportUa282524()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(10, 6),
            ContainerElement("Warichu", 5, 7, 8, 9),
            ChildElement("WP", 6),
            ChildElement("WT", 6),
            ChildElement("WP", 6),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.24");
    }

    [Fact]
    public void WarichuWithWrongOrder_ReportsUa282524()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(10, 6),
            ContainerElement("Warichu", 5, 7, 8, 9),
            ChildElement("WT", 6),  // WT first — wrong order
            ChildElement("WP", 6),
            ChildElement("WP", 6),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.24");
    }

    [Fact]
    public void WarichuWithMissingClosingWp_ReportsUa282524()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(9, 6),
            ContainerElement("Warichu", 5, 7, 8),
            ChildElement("WP", 6),
            ChildElement("WT", 6),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.24");
    }

    // =====================================================================
    //  Clause 8.2.5.27 — Caption must be first or last among its siblings
    // =====================================================================

    [Fact]
    public void CaptionFirst_DoesNotReportUa28527()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(9, 6),
            ContainerElement("Sect", 5, 7, 8),
            ChildElement("Caption", 6),  // object 7 — first
            ChildElement("P", 6),        // object 8
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.27");
    }

    [Fact]
    public void CaptionLast_DoesNotReportUa28527()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(9, 6),
            ContainerElement("Sect", 5, 7, 8),
            ChildElement("P", 6),         // object 7
            ChildElement("Caption", 6),   // object 8 — last
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.27");
    }

    [Fact]
    public void CaptionInTheMiddle_ReportsUa28527()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(10, 6),
            ContainerElement("Sect", 5, 7, 8, 9),
            ChildElement("P", 6),         // object 7
            ChildElement("Caption", 6),   // object 8 — in the middle
            ChildElement("P", 6),         // object 9
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.27");
    }

    /// <summary>The sole child of its parent is trivially both first and last — must not report.</summary>
    [Fact]
    public void CaptionAsOnlyChild_DoesNotReportUa28527()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(8, 6),
            ContainerElement("Sect", 5, 7),
            ChildElement("Caption", 6),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.27");
    }

    // =====================================================================
    //  Clause 8.2.5.28.2 — Figure must have /Alt or /ActualText
    // =====================================================================

    [Fact]
    public void FigureWithAlt_DoesNotReportUa2852852()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),
            FigureElement(5, "/Alt (A chart showing quarterly revenue.)"),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.28.2");
    }

    [Fact]
    public void FigureWithActualText_DoesNotReportUa2852852()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),
            FigureElement(5, "/ActualText (Q3 2026)"),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.28.2");
    }

    [Fact]
    public void FigureWithNeitherAltNorActualText_ReportsUa2852852()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),
            FigureElement(5),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.28.2");
    }

    [Fact]
    public void FigureWithEmptyAlt_ReportsUa2852852()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),
            FigureElement(5, "/Alt ()"),
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.28.2");
    }

    // =====================================================================
    //  Clause 8.2.5.25 — LI's direct children must be wrapped in Lbl/LBody
    // =====================================================================

    [Fact]
    public void LiWithLblAndLBody_DoesNotReportUa28525()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(9, 6),
            ContainerElement("LI", 5, 7, 8),
            ChildElement("Lbl", 6),   // object 7
            ChildElement("LBody", 6), // object 8
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    [Fact]
    public void LiWithLBodyOnly_DoesNotReportUa28525()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(8, 6),
            ContainerElement("LI", 5, 7),
            ChildElement("LBody", 6), // object 7
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    [Fact]
    public void LiWithWrongRoleDirectChild_ReportsUa28525()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(8, 6),
            ContainerElement("LI", 5, 7),
            ChildElement("P", 6), // object 7 — real content's structure element, not wrapped in LBody
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    /// <summary>A bare MCID integer directly under LI is real content with no wrapping element at all.</summary>
    [Fact]
    public void LiWithBareMcidDirectChild_ReportsUa28525()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),
            "<< /Type /StructElem /S /LI /P 5 0 R /K 0 >>", // object 6 — bare MCID, no LBody
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    // =====================================================================
    //  Clause 8.2.5.25 — ListNumbering when labels are present; ContinuedList
    // =====================================================================
    //
    //  Layout: 5=Document, 6=namespace, 7=L, 8=LI, then the LI's kids. The two attribute checks
    //  share UA2-8.2.5.25 with the LI-wrapping check above, so a "reports" test here also names
    //  the attribute in the message — the wrapping check can never be what satisfied it.

    private static byte[] LabelledList(string listExtraEntries)
        => Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),                                         // object 5
            NamespaceDict(Pdf2Namespace),                                           // object 6
            $"<< /Type /StructElem /S /L /P 5 0 R /K [8 0 R] {listExtraEntries} >>", // object 7
            ContainerElement("LI", 7, 9, 10),                                       // object 8
            ChildElement("Lbl", 8),                                                 // object 9
            ChildElement("LBody", 8));                                              // object 10

    private static bool IsListNumberingFinding(PdfUa2StructureViolation v)
        => v.Code == "UA2-8.2.5.25" && v.Message.Contains("ListNumbering");

    private static bool IsContinuedListFinding(PdfUa2StructureViolation v)
        => v.Code == "UA2-8.2.5.25" && v.Message.Contains("ContinuedFrom");

    [Fact]
    public void LabelledListWithListNumbering_DoesNotReportUa825025()
    {
        var result = Validate(LabelledList("/A << /O /List /ListNumbering /Decimal >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    [Fact]
    public void LabelledListWithoutListNumbering_ReportsUa825025()
    {
        var result = Validate(LabelledList(""));
        Assert.Contains(result.Violations, IsListNumberingFinding);
    }

    [Fact]
    public void LabelledListWithListNumberingNone_ReportsUa825025()
    {
        var result = Validate(LabelledList("/A << /O /List /ListNumbering /None >>"));
        Assert.Contains(result.Violations, IsListNumberingFinding);
    }

    [Fact]
    public void ListNumberingUnderTheLayoutOwner_IsNotAListAttribute_ReportsUa825025()
    {
        // §14.8.5.5: List attributes may be owned by List or any owner EXCEPT Layout, Table,
        // PrintField and Artifact.
        var result = Validate(LabelledList("/A << /O /Layout /ListNumbering /Decimal >>"));
        Assert.Contains(result.Violations, IsListNumberingFinding);
    }

    [Fact]
    public void UnlabelledListWithoutListNumbering_DoesNotReportUa825025()
    {
        // The requirement is conditional on labels: LI holds only an LBody here.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),                         // object 5
            NamespaceDict(Pdf2Namespace),                           // object 6
            "<< /Type /StructElem /S /L /P 5 0 R /K [8 0 R] >>",    // object 7
            ContainerElement("LI", 7, 9),                           // object 8
            ChildElement("LBody", 8));                              // object 9

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    [Fact]
    public void ListNumberingThroughClassMap_DoesNotReportUa825025()
    {
        byte[] pdf = PdfWithClassMap("<< /Numbered << /O /List /ListNumbering /Decimal >> >>", "[5 0 R]",
            DocumentElementWithChild(6, 7),                                    // object 5
            NamespaceDict(Pdf2Namespace),                                      // object 6
            "<< /Type /StructElem /S /L /P 5 0 R /K [8 0 R] /C /Numbered >>",  // object 7
            ContainerElement("LI", 7, 9, 10),                                  // object 8
            ChildElement("Lbl", 8),                                            // object 9
            ChildElement("LBody", 8));                                         // object 10

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    [Fact]
    public void NestedLabelledListInheritingListNumbering_DoesNotReportUa825025()
    {
        // ListNumbering is inheritable (ISO 32000-2 Table 382), and §14.8.5.3: "there is no
        // semantic distinction between attributes that are specified explicitly and ones that are
        // inherited". The inner list declares nothing and inherits /Decimal from the outer one.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),                                                    // object 5
            NamespaceDict(Pdf2Namespace),                                                      // object 6
            "<< /Type /StructElem /S /L /P 5 0 R /K [8 0 R] /A << /O /List /ListNumbering /Decimal >> >>", // object 7 — outer
            ContainerElement("LI", 7, 9, 10),                                                  // object 8
            ChildElement("Lbl", 8),                                                            // object 9
            ContainerElement("LBody", 8, 11),                                                  // object 10
            ContainerElement("L", 10, 12),                                                     // object 11 — inner, no attribute
            ContainerElement("LI", 11, 13, 14),                                                // object 12
            ChildElement("Lbl", 12),                                                           // object 13
            ChildElement("LBody", 12));                                                        // object 14

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    [Fact]
    public void NestedLabelledListWithNothingToInherit_ReportsOnlyTheInnerList_Ua825025()
    {
        // The outer list has no labels (LI → LBody only) and no ListNumbering; the inner list has
        // labels. Only the inner L is "the respective L" — exactly one finding, naming object 10.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),                         // object 5
            NamespaceDict(Pdf2Namespace),                           // object 6
            "<< /Type /StructElem /S /L /P 5 0 R /K [8 0 R] >>",    // object 7 — outer, unlabelled
            ContainerElement("LI", 7, 9),                           // object 8
            ContainerElement("LBody", 8, 10),                       // object 9
            ContainerElement("L", 9, 11),                           // object 10 — inner, labelled
            ContainerElement("LI", 10, 12, 13),                     // object 11
            ChildElement("Lbl", 11),                                // object 12
            ChildElement("LBody", 11));                             // object 13

        var result = Validate(pdf);
        var finding = Assert.Single(result.Violations, IsListNumberingFinding);
        Assert.Contains("Object 10.", finding.Message);
    }

    private static byte[] ContinuingList(string listAttributes)
        => Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),                                               // object 5
            NamespaceDict(Pdf2Namespace),                                                 // object 6
            $"<< /Type /StructElem /S /L /P 5 0 R /K [8 0 R] /A << /O /List {listAttributes} >> >>", // object 7
            ContainerElement("LI", 7, 9),                                                 // object 8 — unlabelled
            ChildElement("LBody", 8));                                                    // object 9

    [Fact]
    public void ContinuedFromWithContinuedListTrue_DoesNotReportUa825025()
    {
        var result = Validate(ContinuingList("/ContinuedFrom (list-1) /ContinuedList true"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    [Fact]
    public void ContinuedFromWithoutContinuedList_ReportsUa825025()
    {
        var result = Validate(ContinuingList("/ContinuedFrom (list-1)"));
        Assert.Contains(result.Violations, IsContinuedListFinding);
    }

    [Fact]
    public void ContinuedFromWithContinuedListFalse_ReportsUa825025()
    {
        var result = Validate(ContinuingList("/ContinuedFrom (list-1) /ContinuedList false"));
        Assert.Contains(result.Violations, IsContinuedListFinding);
    }

    [Fact]
    public void ContinuedListTrueWithoutContinuedFrom_DoesNotReportUa825025()
    {
        // ContinuedFrom is optional even for a continuing list (Table 382: without it, the list
        // continues the preceding one at the same level) — only a declared ContinuedFrom is read.
        var result = Validate(ContinuingList("/ContinuedList true"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.25");
    }

    // =====================================================================
    //  Clause 8.2.5.26 — Table regularity
    // =====================================================================

    [Fact]
    public void RegularTable_DoesNotReportUa825026()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),      // object 5
            NamespaceDict(Pdf2Namespace),        // object 6
            ContainerElement("Table", 5, 8, 9),  // object 7
            ContainerElement("TR", 7, 10, 11),   // object 8 — row 1: 2 cells
            ContainerElement("TR", 7, 12, 13),   // object 9 — row 2: 2 cells
            ChildElement("TH", 8),               // object 10
            ChildElement("TD", 8),               // object 11
            ChildElement("TD", 9),               // object 12
            ChildElement("TD", 9));              // object 13

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Fact]
    public void IrregularTable_ReportsUa825026()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),      // object 5
            NamespaceDict(Pdf2Namespace),        // object 6
            ContainerElement("Table", 5, 8, 9),  // object 7
            ContainerElement("TR", 7, 10, 11),   // object 8 — row 1: 2 cells
            ContainerElement("TR", 7, 12),       // object 9 — row 2: 1 cell
            ChildElement("TD", 8),               // object 10
            ChildElement("TD", 8),               // object 11
            ChildElement("TD", 9));              // object 12

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Fact]
    public void RegularTableWithRowSpan_DoesNotReportUa825026()
    {
        // Row 1: a RowSpan=2 cell (reaches into row 2) plus one ordinary cell — width 2.
        // Row 2: one ordinary cell of its own, plus 1 inherited from the RowSpan cell — width 2.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),      // object 5
            NamespaceDict(Pdf2Namespace),        // object 6
            ContainerElement("Table", 5, 8, 9),  // object 7
            ContainerElement("TR", 7, 10, 11),   // object 8 — row 1
            ContainerElement("TR", 7, 12),       // object 9 — row 2
            "<< /Type /StructElem /S /TD /P 8 0 R /A << /O /Table /RowSpan 2 >> >>", // object 10
            ChildElement("TD", 8),               // object 11
            ChildElement("TD", 9));              // object 12

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Fact]
    public void IrregularTableWithMismatchedRowSpan_ReportsUa825026()
    {
        // Row 1's RowSpan=2 cell reaches into row 2, giving row 2 a total width of 2 (1 own + 1
        // inherited) against row 1's own width of 2 — but row 3 has only 1 cell and nothing
        // reaching it, so it is irregular against the other two.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),          // object 5
            NamespaceDict(Pdf2Namespace),            // object 6
            ContainerElement("Table", 5, 8, 9, 13),  // object 7
            ContainerElement("TR", 7, 10, 11),       // object 8 — row 1
            ContainerElement("TR", 7, 12),           // object 9 — row 2
            "<< /Type /StructElem /S /TD /P 8 0 R /A << /O /Table /RowSpan 2 >> >>", // object 10
            ChildElement("TD", 8),                   // object 11
            ChildElement("TD", 9),                   // object 12
            ContainerElement("TR", 7, 14),           // object 13 — row 3: 1 cell, nothing inherited
            ChildElement("TD", 13));                 // object 14

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    // /RowSpan and /ColSpan are Table-owner ATTRIBUTES (ISO 32000-2 §14.8.5.7), declared in a
    // cell's /A or through /C + /ClassMap — never entries on the structure element dictionary
    // itself, which Table 355 gives no such key. The four tests below pin where a span is read
    // from. Layout for each: row 1 is ONE cell whose span, if read, makes it two columns wide;
    // row 2 is two ordinary cells. The table is regular exactly when the span is honoured.

    [Fact]
    public void RegularTableWithColSpanInTableAttributeObject_DoesNotReportUa825026()
    {
        // The shape a conforming producer writes — and the one this library's own Builder writes
        // (TableRenderer.BuildCellAttributes: /A << /O /Table /ColSpan n >>).
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),      // object 5
            NamespaceDict(Pdf2Namespace),        // object 6
            ContainerElement("Table", 5, 8, 9),  // object 7
            ContainerElement("TR", 7, 10),       // object 8 — row 1: one cell spanning two columns
            ContainerElement("TR", 7, 11, 12),   // object 9 — row 2: two cells
            "<< /Type /StructElem /S /TH /P 8 0 R /A << /O /Table /ColSpan 2 >> >>", // object 10
            ChildElement("TD", 9),               // object 11
            ChildElement("TD", 9));              // object 12

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Fact]
    public void RegularTableWithColSpanThroughClassMap_DoesNotReportUa825026()
    {
        // §14.7.6.2's class form: the cell names a class, and the struct tree root's /ClassMap
        // supplies the Table attribute object.
        byte[] pdf = PdfWithClassMap("<< /Span2 << /O /Table /ColSpan 2 >> >>", "[5 0 R]",
            DocumentElementWithChild(6, 7),      // object 5
            NamespaceDict(Pdf2Namespace),        // object 6
            ContainerElement("Table", 5, 8, 9),  // object 7
            ContainerElement("TR", 7, 10),       // object 8 — row 1: one cell spanning two columns
            ContainerElement("TR", 7, 11, 12),   // object 9 — row 2: two cells
            "<< /Type /StructElem /S /TH /P 8 0 R /C /Span2 >>", // object 10
            ChildElement("TD", 9),               // object 11
            ChildElement("TD", 9));              // object 12

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Fact]
    public void ColSpanWrittenOnTheElementDictionary_IsNotASpan_ReportsUa825026()
    {
        // A producer that writes /ColSpan straight onto the structure element has declared no
        // span at all, so row 1 is one column wide against row 2's two.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),      // object 5
            NamespaceDict(Pdf2Namespace),        // object 6
            ContainerElement("Table", 5, 8, 9),  // object 7
            ContainerElement("TR", 7, 10),       // object 8 — row 1
            ContainerElement("TR", 7, 11, 12),   // object 9 — row 2: two cells
            "<< /Type /StructElem /S /TH /P 8 0 R /ColSpan 2 >>", // object 10
            ChildElement("TD", 9),               // object 11
            ChildElement("TD", 9));              // object 12

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2147483647)]
    public void OutOfRangeColSpan_IsReadAsOne_DoesNotReportUa825026(int span)
    {
        // A span outside 1..1000 is not trusted and reads as the attribute's default of 1 — the
        // clamp PDF/UA-1's UA12 applies, shared through TableAttributes. Untrusted, 0 would make
        // row 1 zero columns wide, and 2^31-1 would be summed into the row width as-is. Row 1 is
        // one such cell, row 2 one ordinary cell: regular only if the span reads as 1.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),      // object 5
            NamespaceDict(Pdf2Namespace),        // object 6
            ContainerElement("Table", 5, 8, 9),  // object 7
            ContainerElement("TR", 7, 10),       // object 8 — row 1: the out-of-range cell
            ContainerElement("TR", 7, 11),       // object 9 — row 2: one ordinary cell
            $"<< /Type /StructElem /S /TD /P 8 0 R /A << /O /Table /ColSpan {span} >> >>", // object 10
            ChildElement("TD", 9));              // object 11

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Fact]
    public void ColSpanUnderANonTableOwner_IsNotASpan_ReportsUa825026()
    {
        // The key is the Table owner's; a /Layout attribute object that happens to carry one
        // declares no table span.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),      // object 5
            NamespaceDict(Pdf2Namespace),        // object 6
            ContainerElement("Table", 5, 8, 9),  // object 7
            ContainerElement("TR", 7, 10),       // object 8 — row 1
            ContainerElement("TR", 7, 11, 12),   // object 9 — row 2: two cells
            "<< /Type /StructElem /S /TH /P 8 0 R /A << /O /Layout /ColSpan 2 >> >>", // object 10
            ChildElement("TD", 9),               // object 11
            ChildElement("TD", 9));              // object 12

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Fact]
    public void RowsInsideRowGroups_AreCollected_ReportsUa825026()
    {
        // Rows nested inside THead/TBody are read the same as direct Table children — this
        // fixture is irregular (2 cells vs. 1) but only detectable if the walk descends into
        // the row groups rather than stopping at them.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),      // object 5
            NamespaceDict(Pdf2Namespace),        // object 6
            ContainerElement("Table", 5, 8, 9),  // object 7 — K=[THead(8), TBody(9)]
            ContainerElement("THead", 7, 10),    // object 8
            ContainerElement("TBody", 7, 13),    // object 9
            ContainerElement("TR", 8, 11, 12),   // object 10 — 2 cells
            ChildElement("TH", 10),              // object 11
            ChildElement("TH", 10),              // object 12
            ContainerElement("TR", 9, 14),       // object 13 — 1 cell
            ChildElement("TD", 13));             // object 14

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Fact]
    public void NestedTableDirectlyInOuterK_DoesNotLeakIntoOuterRegularity_DoesNotReportUa825026()
    {
        // A malformed producer nests a second Table role directly as a sibling in the outer
        // Table's own /K, skipping the TR level entirely. Both tables are individually regular
        // on their own (outer: two 1-cell rows; inner: two 2-cell rows) — but if the inner
        // table's rows leaked into the outer table's row list, the combined widths [1, 1, 2, 2]
        // would be irregular, so this fixture only stays fully clean if the walk correctly stops
        // at the nested Table role rather than descending into it.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),          // object 5
            NamespaceDict(Pdf2Namespace),            // object 6
            ContainerElement("Table", 5, 8, 9, 12),  // object 7 — outer table
            ContainerElement("TR", 7, 10),           // object 8 — outer row 1: 1 cell
            ContainerElement("TR", 7, 11),           // object 9 — outer row 2: 1 cell
            ChildElement("TD", 8),                   // object 10
            ChildElement("TD", 9),                   // object 11
            ContainerElement("Table", 7, 13, 16),    // object 12 — nested table, direct sibling
            ContainerElement("TR", 12, 14, 15),      // object 13 — inner row 1: 2 cells
            ChildElement("TD", 13),                  // object 14
            ChildElement("TD", 13),                  // object 15
            ContainerElement("TR", 12, 17, 18),      // object 16 — inner row 2: 2 cells
            ChildElement("TD", 16),                  // object 17
            ChildElement("TD", 16));                 // object 18

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.26");
    }

    [Fact]
    public void CyclicRowGroup_DoesNotHang_DoesNotThrow()
    {
        // object 7's THead references itself in its own /K — an adversarial cyclic row group.
        // The depth cap must terminate the descent rather than recursing unbounded.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),  // object 5
            NamespaceDict(Pdf2Namespace),    // object 6
            ContainerElement("Table", 5, 8), // object 7
            ContainerElement("THead", 7, 8)); // object 8 — references itself

        var ex = Record.Exception(() => Validate(pdf));
        Assert.Null(ex);
    }

    // =====================================================================
    //  Clause 8.2.5.29.1 — math must be a child of Formula
    // =====================================================================

    [Fact]
    public void MathAsChildOfFormula_DoesNotReportUa2852901()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(8, 6),
            ContainerElement("Formula", 5, 7),
            ChildElement("math", 6), // object 7
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.29.1");
    }

    [Fact]
    public void MathAsChildOfNonFormula_ReportsUa2852901()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(8, 6),
            ContainerElement("P", 5, 7),
            ChildElement("math", 6), // object 7 — parent is P, not Formula
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.29.1");
    }

    [Fact]
    public void MathAsRootChild_ReportsUa2852901()
    {
        // math directly under the Document element — no Formula ancestor at all.
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(7, 6),
            ChildElement("math", 5), // object 6
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.29.1");
    }

    // =====================================================================
    //  Clause 8.2.5.8 — TOCI must identify its target via /Ref
    // =====================================================================

    [Fact]
    public void TociWithOwnRef_DoesNotReportUa8258()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /TOCI /P 5 0 R /Ref [8 0 R] >>", // object 7
            "<< /Type /StructElem /S /Part /P 2 0 R >>"); // object 8 — arbitrary Ref target

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.8");
    }

    [Fact]
    public void TociWithRefOnNestedReferenceChild_DoesNotReportUa8258()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            ContainerElement("TOCI", 5, 8), // object 7 — no /Ref of its own
            "<< /Type /StructElem /S /Reference /P 7 0 R /Ref [9 0 R] >>", // object 8 — nested child
            "<< /Type /StructElem /S /Part /P 2 0 R >>"); // object 9 — arbitrary Ref target

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.8");
    }

    [Fact]
    public void TociWithNoRefAnywhere_ReportsUa8258()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            ChildElement("TOCI", 5)); // object 7 — no /Ref at all

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.8");
    }

    [Fact]
    public void TociWithEmptyRefArray_ReportsUa8258()
    {
        byte[] pdf = Pdf("[5 0 R]", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /TOCI /P 5 0 R /Ref [] >>"); // object 7 — empty array

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.8");
    }

    // =====================================================================
    //  Clause 8.2.5.21 / §8.9.2.2 — hidden annotations must be artifacts
    // =====================================================================
    //
    //  These bypass the Pdf() helper (whose fixed page body carries no /Annots) and build the
    //  object list directly: object1=catalog, object2=StructTreeRoot, object3=Pages,
    //  object4=Page with /Annots, object5=Document element, object6=annotation, object7=namespace,
    //  object8=/OBJR, and (where needed) object9=an Artifact wrapper.

    [Fact]
    public void InvisibleAnnotationNotInStructTree_DoesNotReportUa8922()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            $"<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>", // no /K at all
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /F 1 >>", // Invisible
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.2");
    }

    [Fact]
    public void InvisibleAnnotationEnclosedInArtifact_DoesNotReportUa8922()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /F 1 >>", // Invisible
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Artifact /P 5 0 R /K 9 0 R >>", // object 8 — Artifact wrapper
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 9 — inside the Artifact
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.2");
    }

    [Fact]
    public void InvisibleAnnotationNotEnclosedInArtifact_ReportsUa8922()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /F 1 >>", // Invisible
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 8 — directly under Document, not Artifact
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.2");
    }

    [Fact]
    public void NoViewWithToggleNoView_IsNotRequiredToBeArtifact_DoesNotReportUa8922()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            // NoView (bit 6, value 32) + ToggleNoView (bit 9, value 256) both set: not required.
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /F 288 >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 8 — not in an Artifact, but that's fine
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.2");
    }

    // =====================================================================
    //  Clause 8.9.3.3 — Tab order
    // =====================================================================
    //
    //  A page-dictionary check with no structure tree needed: object1=catalog, object2=Pages,
    //  object3=Page (with /Annots and, where tested, /Tabs), object4=the annotation.

    [Fact]
    public void PageWithAnnotationAndValidTabs_DoesNotReportUa8933()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] /Tabs /S >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.3.3");
    }

    [Fact]
    public void PageWithAnnotationAndNoTabs_ReportsUa8933()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.3.3");
    }

    [Fact]
    public void PageWithAnnotationAndInvalidTabsValue_ReportsUa8933()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] /Tabs /Z >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.3.3");
    }

    [Fact]
    public void PageWithNoAnnotations_NoTabsNeeded_DoesNotReportUa8933()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.3.3");
    }

    // =====================================================================
    //  Clause 8.10.1 — no XFA
    // =====================================================================

    [Fact]
    public void AcroFormWithXfa_ReportsUa8101()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /AcroForm 2 0 R >>",
            "<< /Type /AcroForm /Fields [] /XFA [] >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.10.1");
    }

    [Fact]
    public void AcroFormWithoutXfa_DoesNotReportUa8101()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /AcroForm 2 0 R >>",
            "<< /Type /AcroForm /Fields [] >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.1");
    }

    [Fact]
    public void NoAcroForm_DoesNotReportUa8101()
    {
        byte[] pdf = Pdf("[]", omitStructTreeRoot: false);

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.1");
    }

    // =====================================================================
    //  Clause 8.10.1 — a Form structure element encloses at most one widget
    // =====================================================================

    [Fact]
    public void FormWithTwoWidgets_ReportsUa8101()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K 9 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Form /P 5 0 R /K [10 0 R 11 0 R] >>", // object 9
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 10
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>", // object 11
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.10.1");
    }

    [Fact]
    public void FormWithOneWidget_DoesNotReportUa8101()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Form /P 5 0 R /K 9 0 R >>", // object 8
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 9
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.1");
    }

    [Fact]
    public void TwoFormsEachWithOneWidget_DoesNotReportUa8101()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K [9 0 R 11 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Form /P 5 0 R /K 10 0 R >>", // object 9
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 10
            "<< /Type /StructElem /S /Form /P 5 0 R /K 12 0 R >>", // object 11
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>", // object 12
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.1");
    }

    // =====================================================================
    //  Clause 8.10.2.4 — multi-widget fields: a label or Contents per widget
    // =====================================================================
    //
    //  Object layout common to these fixtures: 1=catalog (with /AcroForm), 2=StructTreeRoot,
    //  3=Pages, 4=Page (with /Annots [6 7]), 5=Document, 6/7=the two widget annotations,
    //  8=namespace, 9/11=one Form structure element per widget, 10/12=that Form's /OBJR to the
    //  widget, 13/14=a Lbl structure element as a direct child of the Form (when present),
    //  15=the AcroForm dictionary, 16=the field dictionary naming both widgets as /Kids.

    [Fact]
    public void MultiWidgetField_BothWidgetsHaveDirectFormLbl_DoesNotReportUa810_2_4()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /AcroForm 15 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K [9 0 R 11 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>", // 6
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>", // 7
            NamespaceDict(Pdf2Namespace), // 8
            "<< /Type /StructElem /S /Form /P 5 0 R /K [13 0 R 10 0 R] >>", // 9
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // 10
            "<< /Type /StructElem /S /Form /P 5 0 R /K [14 0 R 12 0 R] >>", // 11
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>", // 12
            "<< /Type /StructElem /S /Lbl /P 9 0 R >>", // 13
            "<< /Type /StructElem /S /Lbl /P 11 0 R >>", // 14
            "<< /Type /AcroForm /Fields [16 0 R] >>", // 15
            "<< /Type /Annot /FT /Btn /Kids [6 0 R 7 0 R] >>", // 16
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.2.4");
    }

    [Fact]
    public void MultiWidgetField_OneWidgetMissingLabelAndContents_ReportsUa810_2_4()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /AcroForm 14 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K [9 0 R 11 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>", // 6 — has a label
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>", // 7 — missing both
            NamespaceDict(Pdf2Namespace), // 8
            "<< /Type /StructElem /S /Form /P 5 0 R /K [13 0 R 10 0 R] >>", // 9 — with Lbl
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // 10
            "<< /Type /StructElem /S /Form /P 5 0 R /K 12 0 R >>", // 11 — no Lbl
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>", // 12
            "<< /Type /StructElem /S /Lbl /P 9 0 R >>", // 13
            "<< /Type /AcroForm /Fields [15 0 R] >>", // 14
            "<< /Type /Annot /FT /Btn /Kids [6 0 R 7 0 R] >>", // 15
        });

        var result = Validate(pdf);
        var violation = Assert.Single(result.Violations, v => v.Code == "UA2-8.10.2.4");
        Assert.Contains("object 7", violation.Message);
    }

    [Fact]
    public void MultiWidgetField_WidgetHasContentsInsteadOfLabel_DoesNotReportUa810_2_4()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /AcroForm 14 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K [9 0 R 11 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>", // 6
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /Contents (Chocolate flavour) >>", // 7
            NamespaceDict(Pdf2Namespace), // 8
            "<< /Type /StructElem /S /Form /P 5 0 R /K [13 0 R 10 0 R] >>", // 9
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // 10
            "<< /Type /StructElem /S /Form /P 5 0 R /K 12 0 R >>", // 11 — no Lbl, relies on /Contents
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>", // 12
            "<< /Type /StructElem /S /Lbl /P 9 0 R >>", // 13
            "<< /Type /AcroForm /Fields [15 0 R] >>", // 14
            "<< /Type /Annot /FT /Btn /Kids [6 0 R 7 0 R] >>", // 15
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.2.4");
    }

    [Fact]
    public void SingleWidgetField_NoLabelOrContents_DoesNotReportUa810_2_4()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /AcroForm 10 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>", // 6 — no label, no Contents
            NamespaceDict(Pdf2Namespace), // 7
            "<< /Type /StructElem /S /Form /P 5 0 R /K 9 0 R >>", // 8 — no Lbl
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // 9
            "<< /Type /AcroForm /Fields [11 0 R] >>", // 10
            "<< /Type /Annot /FT /Btn /Kids [6 0 R] >>", // 11 — a single-widget field
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.2.4");
    }

    [Fact]
    public void MultiWidgetField_NoStructureTreeAtAll_OneWidgetMissingContents_ReportsUa810_2_4()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /Contents (Vanilla flavour) >>", // 4 — has Contents
            "<< /Type /AcroForm /Fields [6 0 R] >>", // 5
            "<< /Type /Annot /FT /Btn /Kids [4 0 R 7 0 R] >>", // 6
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>", // 7 — no Contents, no structure tree
        });

        var result = Validate(pdf);
        var violation = Assert.Single(result.Violations, v => v.Code == "UA2-8.10.2.4");
        Assert.Contains("object 7", violation.Message);
    }

    // =====================================================================
    //  Clause 8.9.4.2 — Alt properties must match Contents
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=StructTreeRoot, 3=Pages, 4=Page (with /Annots),
    //  5=Document, 6=the Alt-carrying element (/S /Annot), 7=the annotation, 8=namespace,
    //  9=the /OBJR linking 6's /K to the annotation.

    [Fact]
    public void MatchingAltAndContents_DoesNotReportUa8942()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K 6 0 R >>",
            "<< /Type /StructElem /S /Annot /P 5 0 R /Alt (Photo of a cat) /K 9 0 R >>",
            "<< /Type /Annot /Subtype /Text /Contents (Photo of a cat) /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.4.2");
    }

    [Fact]
    public void MismatchedAltAndContents_ReportsUa8942()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K 6 0 R >>",
            "<< /Type /StructElem /S /Annot /P 5 0 R /Alt (Photo of a cat) /K 9 0 R >>",
            "<< /Type /Annot /Subtype /Text /Contents (A dog running) /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.4.2");
    }

    [Fact]
    public void AltWithNoAnnotationContents_DoesNotReportUa8942()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K 6 0 R >>",
            "<< /Type /StructElem /S /Annot /P 5 0 R /Alt (Photo of a cat) /K 9 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>", // no /Contents
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.4.2");
    }

    [Fact]
    public void ContentsWithNoElementAlt_DoesNotReportUa8942()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 8 0 R /K 6 0 R >>",
            "<< /Type /StructElem /S /Annot /P 5 0 R /K 9 0 R >>", // no /Alt
            "<< /Type /Annot /Subtype /Text /Contents (A dog running) /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 7 0 R /Pg 4 0 R >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.4.2");
    }

    // =====================================================================
    //  Clause 8.9.2.4.11 — Sound and Movie annotations shall not be present
    // =====================================================================

    [Fact]
    public void SoundAnnotationPresent_ReportsUa8924011()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Sound /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.11");
    }

    /// <summary>
    /// Past the individual limit (25) a per-annotation check lists the first 25 and counts the rest.
    /// The scan used to stop one past the limit, so its summary read "…and 1 more" for any number.
    /// </summary>
    [Fact]
    public void ThirtySoundAnnotations_ListTwentyFive_AndCountTheOtherFive()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [" +
                string.Join(" ", Enumerable.Range(4, 30).Select(n => n + " 0 R")) + "] >>",
        };
        for (int i = 0; i < 30; i++) bodies.Add("<< /Type /Annot /Subtype /Sound /Rect [0 0 10 10] >>");

        var found = Validate(BuildPdf(bodies)).Violations.Where(v => v.Code == "UA2-8.9.2.4.11").ToList();

        Assert.Equal(26, found.Count);
        Assert.Equal(25, found.Count(v => v.Message.Contains(" Object ", System.StringComparison.Ordinal)));
        Assert.Equal("…and 5 more, not listed individually.", found.Last().Message);
    }

    /// <summary>
    /// A check that stops looking once past the limit knows only that there is more. The page
    /// tab-order check stops, and says "at least one more" rather than the "1 more" it used to count.
    /// </summary>
    [Fact]
    public void ThirtyPagesWithoutTabs_ListTwentyFive_AndSayAtLeastOneMore()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, 30).Select(i => (3 + 2 * i) + " 0 R")) + "] /Count 30 >>",
        };
        for (int i = 0; i < 30; i++)
        {
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [{4 + 2 * i} 0 R] >>");
            bodies.Add("<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] >>");
        }

        var found = Validate(BuildPdf(bodies)).Violations.Where(v => v.Code == "UA2-8.9.3.3").ToList();

        Assert.Equal(26, found.Count);
        Assert.StartsWith("…and at least one more page(s)", found.Last().Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// An annotation that will not parse is not an absent one: the checks pass over it as before, and
    /// the report ends with one UA2-99 notice that an object could not be read. The control is the
    /// same page with the annotation readable.
    /// </summary>
    [Theory]
    [InlineData("]", true)]
    [InlineData("<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] >>", false)]
    public void AnUnreadableAnnotation_IsReportedAsAnIncompleteCheck(string annotation, bool unreadable)
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            annotation,
        });

        var result = Validate(pdf);

        Assert.NotEmpty(result.Violations);
        Assert.Equal(unreadable, result.Violations.Any(v =>
            v.Code == "UA2-99" && v.Message.Contains("could not be read", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void MovieAnnotationPresent_ReportsUa8924011()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Movie /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.11");
    }

    [Fact]
    public void NoSoundOrMovieAnnotation_DoesNotReportUa8924011()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.11");
    }

    // =====================================================================
    //  Clause 8.9.2.4.15 — TrapNet annotations shall not be present
    // =====================================================================

    [Fact]
    public void TrapNetAnnotationPresent_ReportsUa8924015()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /TrapNet /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.15");
    }

    [Fact]
    public void NoTrapNetAnnotation_DoesNotReportUa8924015()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.15");
    }

    // =====================================================================
    //  Clause 8.9.2.4.9 — Popup annotations shall not be present in the structure tree
    // =====================================================================

    [Fact]
    public void PopupReferencedFromStructureTree_ReportsUa892409()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Popup /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 8 — Popup referenced from the tree
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.9");
    }

    [Fact]
    public void PopupNotReferencedFromStructureTree_DoesNotReportUa892409()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>", // no /K at all
            "<< /Type /Annot /Subtype /Popup /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.9");
    }

    // =====================================================================
    //  Clause 8.9.2.4.14 — a printer's mark annotation shall be an artifact
    // =====================================================================

    [Fact]
    public void PrinterMarkNotInStructureTree_DoesNotReportUa8924014()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>", // no /K at all
            "<< /Type /Annot /Subtype /PrinterMark /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.14");
    }

    [Fact]
    public void PrinterMarkEnclosedInArtifact_DoesNotReportUa8924014()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /PrinterMark /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Artifact /P 5 0 R /K 9 0 R >>", // object 8
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 9
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.14");
    }

    [Fact]
    public void PrinterMarkNotEnclosedInArtifact_ReportsUa8924014()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /PrinterMark /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 8 — directly under Document, not Artifact
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.14");
    }

    // =====================================================================
    //  Clauses 8.9.2.4.12 / 8.9.2.4.19 — annotation subtypes requiring /Contents
    // =====================================================================

    [Fact]
    public void ScreenWithContents_DoesNotReportUa8924012()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Screen /Contents (A video of a factory tour) /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.12");
    }

    [Fact]
    public void ScreenWithNoContents_ReportsUa8924012()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Screen /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.12");
    }

    [Fact]
    public void ScreenWithEmptyContents_ReportsUa8924012()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Screen /Contents () /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.12");
    }

    [Fact]
    public void ThreeDWithContents_DoesNotReportUa8924019()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /3D /Contents (A rotatable model of a turbine) /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.19");
    }

    [Fact]
    public void RichMediaWithNoContents_ReportsUa8924019()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /RichMedia /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.19");
    }

    // =====================================================================
    //  Clause 8.9.2.4.13 — a zero-size widget annotation shall be an artifact
    // =====================================================================

    [Fact]
    public void ZeroSizeWidgetNotEnclosedInArtifact_ReportsUa8924013()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Rect [10 10 10 10] >>", // zero width and height
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 8 — directly under Document, not Artifact
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.13");
    }

    [Fact]
    public void ZeroSizeWidgetEnclosedInArtifact_DoesNotReportUa8924013()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Rect [10 10 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Artifact /P 5 0 R /K 9 0 R >>", // object 8
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 9
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.13");
    }

    [Fact]
    public void NonZeroSizeWidgetNotEnclosedInArtifact_DoesNotReportUa8924013()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 40 20] >>", // not zero-size
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 8
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.13");
    }

    // =====================================================================
    //  Clause 8.9.2.4.10 — File attachment's /FS dictionary must have /AFRelationship
    // =====================================================================

    [Fact]
    public void FileAttachmentWithAFRelationship_DoesNotReportUa8924010()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /FileAttachment /FS 5 0 R /Rect [0 0 10 10] >>",
            "<< /Type /Filespec /F (data.csv) /AFRelationship /Data >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.10");
    }

    [Fact]
    public void FileAttachmentWithoutAFRelationship_ReportsUa8924010()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /FileAttachment /FS 5 0 R /Rect [0 0 10 10] >>",
            "<< /Type /Filespec /F (data.csv) >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.10");
    }

    /// <summary>The clause is about file attachment annotations: an /FS on a Square annotation is not one.</summary>
    [Fact]
    public void ANonFileAttachmentCarryingAFileSpecification_DoesNotReportUa8924010()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Square /FS 5 0 R /Rect [0 0 10 10] >>",
            "<< /Type /Filespec /F (data.csv) >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.10");
    }

    [Fact]
    public void FileAttachmentWithStringFormFS_DoesNotReportUa8924010()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /FileAttachment /FS (data.csv) /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.10");
    }

    // =====================================================================
    //  Clause 8.9.2.4.8 — Ink annotations require a Contents entry
    // =====================================================================

    [Fact]
    public void InkWithContents_DoesNotReportUa892408()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Ink /Contents (the signature of John Doe) /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.4.8");
    }

    [Fact]
    public void InkWithNoContents_ReportsUa892408()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Ink /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.4.8");
    }

    // =====================================================================
    //  Clause 8.10.3.3 — a text field's RV entry requires a V entry
    // =====================================================================

    [Fact]
    public void TextFieldWithRVAndV_DoesNotReportUa810303()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /V (Hello) /RV (Hello) /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.3.3");
    }

    [Fact]
    public void TextFieldWithRVButNoV_ReportsUa810303()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /RV (Hello) /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.10.3.3");
    }

    /// <summary>The clause is about text fields: the same entries on a Square annotation are not one.</summary>
    [Fact]
    public void ANonWidgetCarryingTextFieldEntries_DoesNotReportUa810303()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Square /FT /Tx /RV (Hello) /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.3.3");
    }

    [Fact]
    public void TextFieldWithNeitherRVNorV_DoesNotReportUa810303()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.3.3");
    }

    [Fact]
    public void NonTextFieldWithRVButNoV_DoesNotReportUa810303()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /RV (Hello) /Rect [0 0 10 10] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.10.3.3");
    }

    // =====================================================================
    //  Clause 8.4.4 — the default natural language must be declared
    // =====================================================================

    [Fact]
    public void CatalogWithLang_DoesNotReportUa844()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.4");
    }

    [Fact]
    public void CatalogWithNoLang_ReportsUa844()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.4");
    }

    [Fact]
    public void CatalogWithEmptyLang_ReportsUa844()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang () >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.4");
    }

    // =====================================================================
    //  Clause 8.7 — no /AS on any OC configuration dictionary
    // =====================================================================

    [Fact]
    public void DefaultOcConfigWithAs_ReportsUa87()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /OCGs [] /D << /AS [] >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.7");
    }

    [Fact]
    public void DefaultOcConfigWithoutAs_DoesNotReportUa87()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /OCGs [] /D << >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.7");
    }

    [Fact]
    public void NoOcProperties_DoesNotReportUa87()
    {
        byte[] pdf = Pdf("[]", omitStructTreeRoot: false);

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.7");
    }

    // =====================================================================
    //  Clause 8.7 — OC configs need /Name once /Configs is present
    // =====================================================================

    [Fact]
    public void ConfigsPresent_DefaultConfigHasNoName_ReportsUa87()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /OCGs [] /D << >> /Configs [5 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Name (Alternate) >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.7");
    }

    [Fact]
    public void ConfigsPresent_AllConfigsHaveNames_DoesNotReportUa87()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /OCGs [] /D << /Name (Default) >> /Configs [5 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Name (Alternate) >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.7");
    }

    [Fact]
    public void EmptyConfigsArray_DefaultConfigHasNoName_DoesNotReportUa87()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OCProperties 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /OCGs [] /D << >> /Configs [] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.7");
    }

    // =====================================================================
    //  Clause 8.11.2 — ViewerPreferences /DisplayDocTitle must be true
    // =====================================================================

    [Fact]
    public void DisplayDocTitleTrue_DoesNotReportUa8112()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences << /DisplayDocTitle true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.11.2");
    }

    [Fact]
    public void NoViewerPreferences_ReportsUa8112()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.11.2");
    }

    [Fact]
    public void DisplayDocTitleFalse_ReportsUa8112()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences << /DisplayDocTitle false >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.11.2");
    }

    [Fact]
    public void ViewerPreferencesWithNoDisplayDocTitle_ReportsUa8112()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /ViewerPreferences << >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.11.2");
    }

    // =====================================================================
    //  Clause 8.14.1 — embedded files require /Desc
    // =====================================================================

    [Fact]
    public void EmbeddedFileWithDesc_DoesNotReportUa8141()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.txt) 3 0 R] >> >> >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Filespec /F (a.txt) /Desc (Supplementary data.) >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.14.1");
    }

    [Fact]
    public void EmbeddedFileWithoutDesc_ReportsUa8141()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.txt) 3 0 R] >> >> >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Filespec /F (a.txt) >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.14.1");
    }

    [Fact]
    public void NoEmbeddedFiles_DoesNotReportUa8141()
    {
        byte[] pdf = Pdf("[]", omitStructTreeRoot: false);

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.14.1");
    }

    // =====================================================================
    //  Clause 8.11.1 — XMP metadata must include dc:title
    // =====================================================================

    private const string XmpWithTitle =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
        "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
        "<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">A Report</rdf:li></rdf:Alt></dc:title>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    private const string XmpWithoutTitle =
        "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
        "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
        "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
        "<rdf:Description xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
        "<dc:creator><rdf:Seq><rdf:li>An Author</rdf:li></rdf:Seq></dc:creator>" +
        "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";

    [Fact]
    public void XmpWithDcTitle_DoesNotReportUa8111()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            $"<< /Type /Metadata /Subtype /XML /Length {XmpWithTitle.Length} >>\n" +
            $"stream\n{XmpWithTitle}\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.11.1");
    }

    [Fact]
    public void XmpWithoutDcTitle_ReportsUa8111()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Metadata 3 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            $"<< /Type /Metadata /Subtype /XML /Length {XmpWithoutTitle.Length} >>\n" +
            $"stream\n{XmpWithoutTitle}\nendstream",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.11.1");
    }

    [Fact]
    public void NoMetadataStream_ReportsUa8111()
    {
        byte[] pdf = Pdf("[]", omitStructTreeRoot: false);

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.11.1");
    }

    // =====================================================================
    //  Clause 8.4.5.3.2 — embedded CIDFontType2 fonts require /CIDToGIDMap
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (with /Resources /Font /F1 4 0 R),
    //  4=Type0 font, 5=CIDFontType2 descendant, 6=FontDescriptor (with /FontFile2),
    //  7=the embedded font program stream.

    [Fact]
    public void CidToGidMapIdentity_DoesNotReportUa84532()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo /CIDToGIDMap /Identity " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 /FontFile2 7 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.3.2");
    }

    [Fact]
    public void CidToGidMapStream_DoesNotReportUa84532()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo /CIDToGIDMap 8 0 R " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 /FontFile2 7 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Length 0 >>\nstream\n\nendstream",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.3.2");
    }

    [Fact]
    public void CidToGidMapMissing_ReportsUa84532()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 /FontFile2 7 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.3.2");
    }

    [Fact]
    public void CidToGidMapInvalidValue_ReportsUa84532()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo /CIDToGIDMap /Bogus " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 /FontFile2 7 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.3.2");
    }

    [Fact]
    public void CidToGidMapMissingButNotEmbedded_DoesNotReportUa84532()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> /FontDescriptor 6 0 R >>",
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>", // no /FontFile2 — not embedded
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.3.2");
    }

    // =====================================================================
    //  Clause 8.4.5.5.1 — fonts used for rendering (outside text rendering
    //  mode 3) must be embedded
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (with /Contents 5 0 R,
    //  /Resources /Font << /F1 4 0 R >>), 4=the simple TrueType font,
    //  5=the page content stream, 6=its /FontDescriptor, 7+=an embedded
    //  /FontFile2 stream where the fixture needs one.

    private static byte[] SimpleFontEmbeddingPdf(
        string content, string fontDescriptorEntries, params string[] extraObjects)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R " +
                "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FontDescriptor 6 0 R >>",
            $"<< /Length 0 >>\nstream\n{content}\nendstream",
            $"<< /Type /FontDescriptor /FontName /Foo /Flags 32{fontDescriptorEntries} >>",
        };
        bodies.AddRange(extraObjects);
        return BuildPdf(bodies);
    }

    [Fact]
    public void UnembeddedFontShownOutsideMode3_ReportsUa84551()
    {
        byte[] pdf = SimpleFontEmbeddingPdf("BT /F1 12 Tf (Hi) Tj ET", "");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.5.1");
    }

    [Fact]
    public void EmbeddedFontShownOutsideMode3_DoesNotReportUa84551()
    {
        byte[] pdf = SimpleFontEmbeddingPdf("BT /F1 12 Tf (Hi) Tj ET", " /FontFile2 7 0 R",
            "<< /Length 0 >>\nstream\n\nendstream");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.5.1");
    }

    [Fact]
    public void UnembeddedFontShownOnlyInRenderMode3_DoesNotReportUa84551()
    {
        // Text rendering mode 3 draws nothing (§9.3.6) — not "used for rendering", so the
        // clause's own exemption applies and an unembedded font here is compliant.
        byte[] pdf = SimpleFontEmbeddingPdf("BT /F1 12 Tf 3 Tr (Hi) Tj ET", "");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.5.1");
    }

    [Fact]
    public void UnembeddedFontResourceNeverShown_DoesNotReportUa84551()
    {
        // /F1 is a resource but no text-showing operator ever appears — not "referenced from
        // one or more content streams" by an actual glyph, so out of the clause's own scope.
        byte[] pdf = SimpleFontEmbeddingPdf("BT /F1 12 Tf ET", "");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.5.1");
    }

    [Fact]
    public void RenderModeRestoredAfterQAndReports_Ua84551()
    {
        // Tr is set to 3 (exempt) only inside q/Q; if Restore ("Q") failed to pop it, the mode
        // would wrongly stay 3 after Q and this would NOT report — proving the stack, not just
        // that mode 3 alone is exempt.
        byte[] pdf = SimpleFontEmbeddingPdf("BT /F1 12 Tf ET q 3 Tr Q BT (Hi) Tj ET", "");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.5.1");
    }

    [Fact]
    public void FontNameRestoredAfterQAndReports_Ua84551()
    {
        // /F2 is never declared in /Resources /Font. If Restore ("Q") failed to pop the font
        // name stack, the current font after Q would stay "F2" (unresolvable) and this would
        // wrongly NOT report — proving the q/Q stack, not just that some font resolves.
        byte[] pdf = SimpleFontEmbeddingPdf("BT /F1 12 Tf ET q /F2 10 Tf Q BT (Hi) Tj ET", "");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.5.1");
    }

    [Fact]
    public void Type0DescendantWithNoFontDescriptor_ReportsUa84551()
    {
        // Reuses the §8.4.5.9 CidFontPdf fixture: its CIDFontType2 descendant carries no
        // /FontDescriptor at all, so embedding cannot be confirmed — reported, not skipped
        // (FontEmbeddingProbe's own "cannot confirm is not conforms" rule).
        byte[] pdf = CidFontPdf("BT /F1 12 Tf <0041> Tj ET");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.5.1");
    }

    [Fact]
    public void Type0DescendantEmbedded_DoesNotReportUa84551()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 7 0 R " +
                "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding 6 0 R " +
                "/DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo " +
                "/CIDSystemInfo << /Registry (Test) /Ordering (Test) /Supplement 0 >> " +
                "/FontDescriptor 8 0 R >>",
            $"<< /Length 0 >>\nstream\n{CidCMapPrologue}{CidCMapEpilogue}\nendstream",
            "<< /Length 0 >>\nstream\nBT /F1 12 Tf <0041> Tj ET\nendstream",
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 /FontFile2 9 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.5.1");
    }

    // =====================================================================
    //  Clause 8.4.5.7 — a symbolic TrueType font must not carry /Encoding
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (with /Resources /Font /F1 4 0 R),
    //  4=TrueType font, 5=FontDescriptor (with /Flags and, for embedded cases, /FontFile2),
    //  6=the embedded font program stream.

    [Fact]
    public void SymbolicTrueTypeWithEncoding_ReportsUa8457()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /Encoding /WinAnsiEncoding " +
            "/FirstChar 32 /LastChar 32 /Widths [500] /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>", // bit 3 (value 4) = Symbolic
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Fact]
    public void SymbolicTrueTypeWithoutEncoding_DoesNotReportUa8457()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo " +
            "/FirstChar 32 /LastChar 32 /Widths [500] /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Fact]
    public void NonSymbolicTrueTypeWithEncoding_DoesNotReportUa8457()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /Encoding /WinAnsiEncoding " +
            "/FirstChar 32 /LastChar 32 /Widths [500] /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /Foo /Flags 32 >>", // bit 6 (value 32) = Nonsymbolic
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Fact]
    public void SymbolicType1WithEncoding_DoesNotReportUa8457()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Foo /Encoding /WinAnsiEncoding " +
            "/FirstChar 32 /LastChar 32 /Widths [500] /FontDescriptor 5 0 R >>", // not TrueType
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    // §8.4.5.7's other sentences. Layout: 1=catalog, 2=Pages, 3=Page (/F1 4 0 R, /Contents 7 0 R),
    // 4=TrueType font, 5=descriptor, 6=program (or an unused placeholder), 7=content.

    private static byte[] TrueTypeRulesPdf(string fontEntries, string flags, byte[] program,
        string content = "BT /F1 12 Tf (A) Tj ET")
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 65 /Widths [600] " +
                $"/FontDescriptor 5 0 R {fontEntries} >>",
            $"<< /Type /FontDescriptor /FontName /TestFont {flags} " +
                (program != null ? "/FontFile2 6 0 R" : "") + " >>",
            program != null ? StreamObj(program) : "<< >>",
            ContentObj(content),
        });

    private static bool IsFinding(PdfUa2StructureViolation v, string code, string text)
        => v.Code == code && v.Message.Contains(text);

    [Theory]
    [InlineData("/Encoding /WinAnsiEncoding")]
    [InlineData("/Encoding /MacRomanEncoding")]
    [InlineData("/Encoding << /BaseEncoding /WinAnsiEncoding >>")]
    public void NonSymbolicTrueTypeWithAPermittedEncoding_DoesNotReportUa8457(string encoding)
    {
        var result = Validate(TrueTypeRulesPdf(encoding, "/Flags 32", MinimalTrueType.Build("A")));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Theory]
    [InlineData("")]
    [InlineData("/Encoding /MacExpertEncoding")]
    [InlineData("/Encoding << /Differences [65 /A] >>")]
    public void NonSymbolicTrueTypeWithoutAPermittedEncoding_ReportsUa8457(string encoding)
    {
        var result = Validate(TrueTypeRulesPdf(encoding, "/Flags 32", MinimalTrueType.Build("A")));
        Assert.Contains(result.Violations, v => IsFinding(v, "UA2-8.4.5.7", "MacRomanEncoding or WinAnsiEncoding"));
    }

    [Fact]
    public void TrueTypeWhoseDescriptorDeclaresNoFlags_IsNotJudgedNonSymbolic_DoesNotReportUa8457()
    {
        // No /Flags: IsSymbolic would answer "not symbolic" by default, which the file never said.
        var result = Validate(TrueTypeRulesPdf("", "", MinimalTrueType.Build("A", 0, 3)));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(1, 0)]
    public void RenderedNonSymbolicTrueTypeWithARequiredCmapSubtable_DoesNotReportUa8457(int platform, int encoding)
    {
        var result = Validate(TrueTypeRulesPdf("/Encoding /WinAnsiEncoding", "/Flags 32",
            MinimalTrueType.Build("A", platform, encoding)));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Fact]
    public void RenderedNonSymbolicTrueTypeWithOnlyAUnicodePlatformSubtable_ReportsUa8457()
    {
        // (0,3) is a perfectly good Unicode subtable, but not one of the two §8.4.5.7 names.
        var result = Validate(TrueTypeRulesPdf("/Encoding /WinAnsiEncoding", "/Flags 32",
            MinimalTrueType.Build("A", 0, 3)));
        Assert.Contains(result.Violations, v => IsFinding(v, "UA2-8.4.5.7", "(3,1) Microsoft Unicode nor a (1,0)"));
    }

    [Fact]
    public void UnrenderedNonSymbolicTrueTypeWithOnlyAUnicodePlatformSubtable_DoesNotReportUa8457()
    {
        // The non-symbolic cmap sentence covers fonts "used for rendering"; this one is never shown.
        var result = Validate(TrueTypeRulesPdf("/Encoding /WinAnsiEncoding", "/Flags 32",
            MinimalTrueType.Build("A", 0, 3), content: "0 0 10 10 re f"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Fact]
    public void NonSymbolicTrueTypeShownOnlyInRenderingMode3_IsNotUsedForRendering_DoesNotReportUa8457()
    {
        // A font shown only in mode 3 draws nothing (§8.4.5.5.1: "not rendered"). Unlike a font never
        // shown at all, it IS reached by the content walk, so only the rendering gate keeps it quiet.
        var result = Validate(TrueTypeRulesPdf("/Encoding /WinAnsiEncoding", "/Flags 32",
            MinimalTrueType.Build("A", 0, 3), content: "BT /F1 12 Tf 3 Tr (A) Tj ET"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Fact]
    public void SymbolicTrueTypeWithA30Subtable_DoesNotReportUa8457()
    {
        var result = Validate(TrueTypeRulesPdf("", "/Flags 4", MinimalTrueType.BuildSymbolic("A")));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Fact]
    public void SymbolicTrueTypeWithOnlyA31Subtable_ReportsUa8457()
    {
        var result = Validate(TrueTypeRulesPdf("", "/Flags 4", MinimalTrueType.Build("A")));
        Assert.Contains(result.Violations, v => IsFinding(v, "UA2-8.4.5.7", "(3,0) Microsoft Symbol nor a (1,0)"));
    }

    [Fact]
    public void NonSymbolicDifferencesWithAglNamesAndA31Subtable_DoesNotReportUa8457()
    {
        var result = Validate(TrueTypeRulesPdf(
            "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /Alpha /eacute] >>", "/Flags 32",
            MinimalTrueType.Build("A")));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.7");
    }

    [Fact]
    public void NonSymbolicDifferencesWithANonAglName_ReportsUa8457()
    {
        var result = Validate(TrueTypeRulesPdf(
            "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /Alpha /notAnAglGlyphName] >>", "/Flags 32",
            MinimalTrueType.Build("A")));
        Assert.Contains(result.Violations, v => IsFinding(v, "UA2-8.4.5.7", "/notAnAglGlyphName"));
    }

    [Fact]
    public void NonSymbolicDifferencesWithoutA31Subtable_ReportsUa8457()
    {
        // (1,0) satisfies the general cmap sentence, but a /Differences array needs (3,1) itself.
        var result = Validate(TrueTypeRulesPdf(
            "/Encoding << /BaseEncoding /WinAnsiEncoding /Differences [65 /Alpha] >>", "/Flags 32",
            MinimalTrueType.Build("A", 1, 0)));
        Assert.Contains(result.Violations, v => IsFinding(v, "UA2-8.4.5.7", "no (3,1) Microsoft Unicode"));
    }

    // =====================================================================
    //  Clause 8.4.5.3.1 — Type 0 font / descendant CIDSystemInfo relationship
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (with /Resources /Font /F1 4 0 R),
    //  4=Type0 font (with /Encoding as an embedded CMap stream, object 6), 5=CIDFontType0
    //  descendant, 6=the embedded CMap stream (with its own /CIDSystemInfo).

    [Fact]
    public void MatchingRegistryOrderingAndSufficientSupplement_DoesNotReportUa845031()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding 6 0 R /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Foo " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 5 >> >>",
            "<< /Type /CMap /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 5 >> " +
            "/Length 0 >>\nstream\n\nendstream",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.3.1");
    }

    [Fact]
    public void MismatchedOrdering_ReportsUa845031()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding 6 0 R /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Foo " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 5 >> >>",
            "<< /Type /CMap /CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 5 >> " +
            "/Length 0 >>\nstream\n\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.3.1");
    }

    [Fact]
    public void InsufficientSupplement_ReportsUa845031()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding 6 0 R /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Foo " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 2 >> >>",
            "<< /Type /CMap /CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 5 >> " +
            "/Length 0 >>\nstream\n\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.3.1");
    }

    [Fact]
    public void IdentityHEncoding_DoesNotReportUa845031()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.3.1");
    }

    [Fact]
    public void NamedPredefinedCMapEncoding_DoesNotReportUa845031()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /UniGB-UCS2-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Foo " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (GB1) /Supplement 0 >> >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.3.1");
    }

    // =====================================================================
    //  Clause 8.4.5.4 — an embedded CMap's dictionary /WMode must match its program
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (with /Resources /Font /F1 4 0 R),
    //  4=Type0 font (with /Encoding as an embedded CMap stream, object 5),
    //  5=CIDFontType0 descendant, 6=the embedded CMap stream.

    private static string CMapStreamWithWMode(int? dictWMode, string programBody)
    {
        string wModeEntry = dictWMode.HasValue
            ? $" /WMode {dictWMode.Value.ToString(CultureInfo.InvariantCulture)}"
            : "";
        return $"<< /Type /CMap{wModeEntry} /Length {programBody.Length} >>\n" +
               $"stream\n{programBody}\nendstream";
    }

    private static byte[] CMapWModePdf(int? dictWMode, string programBody, string encoding = "6 0 R")
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            $"<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding {encoding} /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Foo " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Japan1) /Supplement 5 >> >>",
            CMapStreamWithWMode(dictWMode, programBody),
        });

    [Fact]
    public void MatchingDictAndProgramWMode_DoesNotReportUa8454()
    {
        byte[] pdf = CMapWModePdf(dictWMode: 0, programBody: "/WMode 0 def");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.4");
    }

    [Fact]
    public void MismatchedDictAndProgramWMode_ReportsUa8454()
    {
        byte[] pdf = CMapWModePdf(dictWMode: 0, programBody: "/WMode 1 def");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.4");
    }

    [Fact]
    public void AbsentDictWModeDefaultsToZero_MismatchReportsUa8454()
    {
        byte[] pdf = CMapWModePdf(dictWMode: null, programBody: "/WMode 1 def");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.4");
    }

    [Fact]
    public void NoWModeDeclarationInProgram_DoesNotReportUa8454()
    {
        byte[] pdf = CMapWModePdf(dictWMode: 1, programBody: "");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.4");
    }

    [Fact]
    public void PredefinedNameEncoding_DoesNotReportUa8454()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo " +
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.4");
    }

    // =====================================================================
    //  Clause 8.4.5.8 — ToUnicode CMap values must not be 0, U+FEFF or U+FFFE
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (with /Resources /Font /F1 4 0 R),
    //  4=simple font (with /ToUnicode 5 0 R), 5=the ToUnicode CMap stream.

    private const string ToUnicodeCMapPrologue =
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
        "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n";
    private const string ToUnicodeCMapEpilogue = "endcmap\nend\nend";

    [Fact]
    public void ToUnicodeCMapWithOrdinaryValues_DoesNotReportUa8458()
    {
        string cmap = ToUnicodeCMapPrologue +
            "1 beginbfchar\n<0041> <0042>\nendbfchar\n" + ToUnicodeCMapEpilogue;
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Foo /ToUnicode 5 0 R >>",
            $"<< /Length 0 >>\nstream\n{cmap}\nendstream",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    [Fact]
    public void ToUnicodeCMapMappingToFeff_ReportsUa8458()
    {
        string cmap = ToUnicodeCMapPrologue +
            "1 beginbfchar\n<0041> <FEFF>\nendbfchar\n" + ToUnicodeCMapEpilogue;
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Foo /ToUnicode 5 0 R >>",
            $"<< /Length 0 >>\nstream\n{cmap}\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    [Fact]
    public void ToUnicodeCMapMappingToFffe_ReportsUa8458()
    {
        string cmap = ToUnicodeCMapPrologue +
            "1 beginbfchar\n<0041> <FFFE>\nendbfchar\n" + ToUnicodeCMapEpilogue;
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Foo /ToUnicode 5 0 R >>",
            $"<< /Length 0 >>\nstream\n{cmap}\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    [Fact]
    public void ToUnicodeCMapMappingToZero_ReportsUa8458()
    {
        string cmap = ToUnicodeCMapPrologue +
            "1 beginbfchar\n<0041> <0000>\nendbfchar\n" + ToUnicodeCMapEpilogue;
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Foo /ToUnicode 5 0 R >>",
            $"<< /Length 0 >>\nstream\n{cmap}\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    [Fact]
    public void UnshownFontWithNoToUnicodeEntry_DoesNotReportUa8458()
    {
        // The presence rule asks for a CMap mapping "at least all referenced glyphs"; a font that is
        // listed in resources but never shown references none. (Shown fonts are covered below.)
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Foo >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    [Fact]
    public void ToUnicodeCMapOnType3Font_IsChecked_ReportsUa8458()
    {
        // WalkAllTopLevelFonts must reach Type 3, which WalkAllFontDescriptors skips outright.
        string cmap = ToUnicodeCMapPrologue +
            "1 beginbfchar\n<0041> <FEFF>\nendbfchar\n" + ToUnicodeCMapEpilogue;
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type3 /BaseFont /Foo /FontBBox [0 0 1 1] " +
            "/FontMatrix [0.001 0 0 0.001 0 0] /CharProcs 6 0 R /Encoding 7 0 R /ToUnicode 5 0 R >>",
            $"<< /Length 0 >>\nstream\n{cmap}\nendstream",
            "<< >>",
            "<< /Type /Encoding /Differences [65 /a] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    // §8.4.5.8's presence rule and its four exceptions. Layout: 1=catalog, 2=Pages, 3=Page (/F1 4 0 R,
    // /Contents 6 0 R), 4=the font, 5=a /ToUnicode stream or an unused placeholder, 6=content,
    // 7=a descriptor or descendant CIDFont when the font needs one.

    private static byte[] ToUnicodePresencePdf(string fontBody, string content, string toUnicodeCMap = null,
        string seventh = "<< >>")
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R >>",
            fontBody,
            toUnicodeCMap != null ? $"<< /Length 0 >>\nstream\n{toUnicodeCMap}\nendstream" : "<< >>",
            ContentObj(content),
            seventh,
        });

    private static bool IsPresenceFinding(PdfUa2StructureViolation v)
        => v.Code == "UA2-8.4.5.8" && v.Message.Contains("has no /ToUnicode CMap");

    [Fact]
    public void ShownSymbolicTrueTypeWithoutToUnicode_ReportsUa8458()
    {
        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FontDescriptor 7 0 R >>",
            "BT /F1 12 Tf (A) Tj ET",
            seventh: "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>"));
        Assert.Contains(result.Violations, IsPresenceFinding);
    }

    [Fact]
    public void ShownNonSymbolicTrueTypeWithoutToUnicode_IsExempt_DoesNotReportUa8458()
    {
        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FontDescriptor 7 0 R >>",
            "BT /F1 12 Tf (A) Tj ET",
            seventh: "<< /Type /FontDescriptor /FontName /Foo /Flags 32 >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    [Fact]
    public void ShownFontWithAPredefinedEncodingWithoutToUnicode_IsExempt_DoesNotReportUa8458()
    {
        // A symbolic TrueType font, so no OTHER exception can apply: only the predefined encoding
        // keeps it quiet. (A Type 1 font here would also pass through the glyph-name exception.)
        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /Encoding /MacExpertEncoding /FontDescriptor 7 0 R >>",
            "BT /F1 12 Tf (A) Tj ET",
            seventh: "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    [Fact]
    public void ShownType1WhoseShownGlyphNamesAreAllInTheAgl_IsExempt_DoesNotReportUa8458()
    {
        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Foo /Encoding << /Differences [65 /Alpha] >> >>",
            "BT /F1 12 Tf (A) Tj ET"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    [Fact]
    public void ShownType1WithANonAglGlyphNameWithoutToUnicode_ReportsUa8458()
    {
        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Foo /Encoding << /Differences [65 /notAnAglGlyphName] >> >>",
            "BT /F1 12 Tf (A) Tj ET"));
        Assert.Contains(result.Violations, IsPresenceFinding);
    }

    [Fact]
    public void ShownType3WithANonAglGlyphNameWithoutToUnicode_ReportsUa8458()
    {
        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [0.001 0 0 0.001 0 0] " +
                "/CharProcs << >> /Encoding << /Differences [65 /notAnAglGlyphName] >> >>",
            "BT /F1 12 Tf (A) Tj ET"));
        Assert.Contains(result.Violations, IsPresenceFinding);
    }

    [Fact]
    public void ShownSymbolicType1WithAGlyphOutsideDifferences_CannotBeDetermined_DoesNotReportUa8458()
    {
        // B (66) has no /Differences name and a symbolic Type 1 font's base is its built-in encoding,
        // so its name cannot be known here — skipped, not guessed at.
        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /Type1 /BaseFont /Foo /Encoding << /Differences [65 /Alpha] >> " +
                "/FontDescriptor 7 0 R >>",
            "BT /F1 12 Tf (AB) Tj ET",
            seventh: "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    [Theory]
    [InlineData("Japan1", false)]
    [InlineData("KR", false)]
    [InlineData("Identity", true)]
    public void ShownType0WithoutToUnicode_IsExemptOnlyForAnAdobeCjkCollection_Ua8458(string ordering, bool reports)
    {
        byte[] pdf = ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding /Identity-H /DescendantFonts [7 0 R] >>",
            "BT /F1 12 Tf <0001> Tj ET",
            seventh: "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo " +
                $"/CIDSystemInfo << /Registry (Adobe) /Ordering ({ordering}) /Supplement 0 >> >>");

        var result = Validate(pdf);
        if (reports) Assert.Contains(result.Violations, IsPresenceFinding);
        else Assert.DoesNotContain(result.Violations, IsPresenceFinding);
    }

    private const string OneByteToUnicodePrologue =
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
        "1 begincodespacerange\n<00> <FF>\nendcodespacerange\n";

    [Fact]
    public void ToUnicodeThatMissesAShownCode_ReportsUa8458()
    {
        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FontDescriptor 7 0 R /ToUnicode 5 0 R >>",
            "BT /F1 12 Tf (AB) Tj ET",
            toUnicodeCMap: OneByteToUnicodePrologue + "1 beginbfchar\n<41> <0041>\nendbfchar\n" + ToUnicodeCMapEpilogue,
            seventh: "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>"));
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.8" && v.Message.Contains("code 66"));
    }

    [Fact]
    public void OversizedToUnicodeTruncatedAtItsBudget_IsNotReadAsMissingCodes_DoesNotReportUa8458()
    {
        // 129 ranges of 65 280 codes each exceed PdfLimits.MaxCMapMappingWrites (8 MiB), so the parser
        // stops before the final range that maps the shown codes and returns a PARTIAL map. A partial
        // map says nothing about which codes the CMap really maps, so it must not be reported as
        // missing them.
        var cmap = new StringBuilder(OneByteToUnicodePrologue.Replace("<00> <FF>", "<0000> <FFFF>"));
        cmap.Append("129 beginbfrange\n");
        for (int i = 0; i < 129; i++) cmap.Append("<0100> <FFFF> <0100>\n");
        cmap.Append("endbfrange\n1 beginbfrange\n<0041> <0042> <0041>\nendbfrange\n").Append(ToUnicodeCMapEpilogue);

        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FontDescriptor 7 0 R /ToUnicode 5 0 R >>",
            "BT /F1 12 Tf (A) Tj ET",
            toUnicodeCMap: cmap.ToString(),
            seventh: "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.8" && v.Message.Contains("does not map"));
    }

    [Fact]
    public void ToUnicodeThatMapsEveryShownCode_DoesNotReportUa8458()
    {
        var result = Validate(ToUnicodePresencePdf(
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FontDescriptor 7 0 R /ToUnicode 5 0 R >>",
            "BT /F1 12 Tf (AB) Tj ET",
            toUnicodeCMap: OneByteToUnicodePrologue + "2 beginbfchar\n<41> <0041>\n<42> <0042>\nendbfchar\n" + ToUnicodeCMapEpilogue,
            seventh: "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.8");
    }

    // =====================================================================
    //  Clause 8.4.3 — real content mapped to the Unicode PUA needs ActualText or Alt
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (/F1 4 0 R, /Contents 6 0 R, /Fm0 10 0 R when a
    //  form is supplied), 4=a symbolic TrueType font whose /ToUnicode maps A (0x41) to U+E000 and
    //  B (0x42) to U+0042, 5=that CMap, 6=content, 7=its descriptor, 8=StructTreeRoot, 9=a P element
    //  on the page, 10+=whatever `extra` supplies.

    internal static byte[] PuaPdf(string content, string element = "/K 0", string rootKids = "9 0 R",
        string pageXObjects = "", params string[] extra)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 8 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                $"/Resources << /Font << /F1 4 0 R >> {pageXObjects} >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo /FontDescriptor 7 0 R /ToUnicode 5 0 R >>",
            "<< /Length 0 >>\nstream\n" + OneByteToUnicodePrologue +
                "2 beginbfchar\n<41> <E000>\n<42> <0042>\nendbfchar\n" + ToUnicodeCMapEpilogue + "\nendstream",
            ContentObj(content),
            "<< /Type /FontDescriptor /FontName /Foo /Flags 4 >>",
            $"<< /Type /StructTreeRoot /K {rootKids} >>",
            $"<< /Type /StructElem /S /P /P 8 0 R /Pg 3 0 R {element} >>",
        };
        bodies.AddRange(extra);
        return BuildPdf(bodies);
    }

    private static List<PdfUa2StructureViolation> PuaFindings(byte[] pdf)
        => Validate(pdf).Violations.Where(v => v.Code == "UA2-8.4.3").ToList();

    [Fact]
    public void UntaggedTextMappedToThePua_ReportsUa843()
    {
        var finding = Assert.Single(PuaFindings(PuaPdf("BT /F1 12 Tf (A) Tj ET")));
        Assert.Contains("Page 1", finding.Message);
        Assert.Contains("neither marked as an artifact", finding.Message);
    }

    [Fact]
    public void UntaggedTextNotMappedToThePua_DoesNotReportUa843()
    {
        // The control: the same font and layout, a code whose /ToUnicode value is ordinary.
        Assert.Empty(PuaFindings(PuaPdf("BT /F1 12 Tf (B) Tj ET")));
    }

    [Fact]
    public void RepeatedPuaTextUnderOneMarkedContentKey_ReportsUa843Once()
    {
        Assert.Single(PuaFindings(PuaPdf("BT /F1 12 Tf (A) Tj (A) Tj ET")));
    }

    [Fact]
    public void PuaTextInsideAnArtifact_DoesNotReportUa843()
    {
        Assert.Empty(PuaFindings(PuaPdf("/Artifact BMC BT /F1 12 Tf (A) Tj ET EMC")));
    }

    [Fact]
    public void PuaTextInsideMarkedContentWithActualText_DoesNotReportUa843()
    {
        Assert.Empty(PuaFindings(PuaPdf("/Span << /ActualText (x) >> BDC BT /F1 12 Tf (A) Tj ET EMC")));
    }

    [Fact]
    public void PuaTextAfterItsActualTextSequenceHasEnded_ReportsUa843()
    {
        // The marked-content /ActualText covers what it encloses and nothing after its EMC.
        Assert.Single(PuaFindings(PuaPdf(
            "/Span << /ActualText (x) >> BDC BT /F1 12 Tf (B) Tj ET EMC BT /F1 12 Tf (A) Tj ET")));
    }

    [Fact]
    public void TaggedPuaTextWhoseElementHasNeitherEntry_ReportsUa843()
    {
        var finding = Assert.Single(PuaFindings(PuaPdf("/P << /MCID 0 >> BDC BT /F1 12 Tf (A) Tj ET EMC")));
        Assert.Contains("marked content 0", finding.Message);
    }

    [Theory]
    [InlineData("/Alt (x)")]
    [InlineData("/ActualText (x)")]
    [InlineData("/ActualText ()")]
    public void TaggedPuaTextWhoseElementCarriesAnEntry_DoesNotReportUa843(string entry)
    {
        Assert.Empty(PuaFindings(PuaPdf("/P << /MCID 0 >> BDC BT /F1 12 Tf (A) Tj ET EMC",
            element: "/K 0 " + entry)));
    }

    [Fact]
    public void TaggedPuaTextWhoseAncestorCarriesActualText_DoesNotReportUa843()
    {
        Assert.Empty(PuaFindings(PuaPdf("/P << /MCID 0 >> BDC BT /F1 12 Tf (A) Tj ET EMC",
            rootKids: "10 0 R",
            extra: "<< /Type /StructElem /S /Span /P 8 0 R /ActualText (x) /K [9 0 R] >>")));
    }

    [Theory]
    [InlineData("/Alt (x)", false)]
    [InlineData("", true)]
    public void TaggedPuaTextOwnedThroughAMarkedContentReference_IsJudgedByThatElement_Ua843(string entry, bool reports)
    {
        var findings = PuaFindings(PuaPdf("/P << /MCID 0 >> BDC BT /F1 12 Tf (A) Tj ET EMC",
            element: "/K << /Type /MCR /Pg 3 0 R /MCID 0 >> " + entry));
        Assert.Equal(reports ? 1 : 0, findings.Count);
    }

    [Fact]
    public void TaggedPuaTextOwnedThroughAReferenceCarryingTheOnlyPage_ReportsUa843()
    {
        // Element 10 has no /Pg of its own and none to inherit; only its /MCR kid's /Pg places the
        // MCID on the page. (Element 9 owns an unrelated MCID.)
        Assert.Single(PuaFindings(PuaPdf("/P << /MCID 0 >> BDC BT /F1 12 Tf (A) Tj ET EMC",
            element: "/K 5", rootKids: "[9 0 R 10 0 R]",
            extra: "<< /Type /StructElem /S /P /P 8 0 R /K << /Type /MCR /Pg 3 0 R /MCID 0 >> >>")));
    }

    [Fact]
    public void TaggedPuaTextThatNoElementOwns_IsNotJudged_DoesNotReportUa843()
    {
        // MCID 0 is claimed by no element (the only one owns MCID 5), so there is no element whose
        // entries could be read — skipped, not guessed at.
        Assert.Empty(PuaFindings(PuaPdf("/P << /MCID 0 >> BDC BT /F1 12 Tf (A) Tj ET EMC", element: "/K 5")));
    }

    [Fact]
    public void TaggedPuaTextClaimedOnlyByAReferenceIntoAFormStream_IsNotJudged_DoesNotReportUa843()
    {
        // An /MCR with /Stm names MCID 0 of a form XObject, not of the page, so it does not own the
        // page's MCID 0. With /Stm removed the same element owns it and the finding appears.
        Assert.Empty(PuaFindings(PuaPdf("/P << /MCID 0 >> BDC BT /F1 12 Tf (A) Tj ET EMC",
            element: "/K << /Type /MCR /Pg 3 0 R /Stm 10 0 R /MCID 0 >>",
            extra: "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length 0 >>\nstream\n\nendstream")));
    }

    [Fact]
    public void UntaggedPuaTextInAFormXObject_IsNotJudged_DoesNotReportUa843()
    {
        string form = "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> " +
                      "/Length 22 >>\nstream\nBT /F1 12 Tf (A) Tj ET\nendstream";
        Assert.Empty(PuaFindings(PuaPdf("/Fm0 Do", pageXObjects: "/XObject << /Fm0 10 0 R >>", extra: form)));
    }

    // =====================================================================
    //  Clause 8.9.2.3 — markup annotations must be enclosed within Annot
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=StructTreeRoot, 3=Pages, 4=Page (with /Annots),
    //  5=Document element, 6=the annotation, 7=namespace, and (where needed) 8=an Annot
    //  wrapper structure element and 9=the /OBJR inside it.

    [Fact]
    public void MarkupAnnotationEnclosedInAnnot_DoesNotReportUa8923()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Annot /P 5 0 R /K 9 0 R >>", // object 8 — the Annot wrapper
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 9 — inside the Annot wrapper
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.3");
    }

    [Fact]
    public void MarkupAnnotationAbsentFromStructTree_ReportsUa8923()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>", // no /K at all
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.3");
    }

    [Fact]
    public void MarkupAnnotationReferencedButNotInAnnot_ReportsUa8923()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 8 — directly under Document, not Annot
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.3");
    }

    [Fact]
    public void InvisibleMarkupAnnotationAbsentFromStructTree_DoesNotReportUa8923()
    {
        // §8.9.2.2 requires an Invisible-flagged annotation to be an artifact instead — the
        // opposite of this clause's "enclosed within Annot" — so it is exempt here.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /F 1 >>", // Invisible
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.3");
    }

    [Fact]
    public void WatermarkAnnotationAbsentFromStructTree_DoesNotReportUa8923()
    {
        // Watermark's own subclause (§8.9.2.4.16) makes §8.9.2.3 apply only "when used as real
        // content" — a semantic judgment this check does not attempt, so Watermark is excluded
        // from the unconditional subtype set entirely.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>",
            "<< /Type /Annot /Subtype /Watermark /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.3");
    }

    // =====================================================================
    //  Clause 8.2.5.20 — link annotations must be enclosed in Link or Reference
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=StructTreeRoot, 3=Pages, 4=Page (with /Annots),
    //  5=Document element, 6=the Link annotation, 7=namespace, and (where needed) 8=a Link or
    //  Reference wrapper structure element and 9=the /OBJR inside it.

    [Fact]
    public void LinkAnnotationEnclosedInLink_DoesNotReportUa825020()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Link /P 5 0 R /K 9 0 R >>", // object 8 — the Link wrapper
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 9 — inside the Link wrapper
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.20");
    }

    [Fact]
    public void LinkAnnotationEnclosedInReference_DoesNotReportUa825020()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Reference /P 5 0 R /K 9 0 R >>", // object 8 — the Reference wrapper
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 9 — inside the Reference wrapper
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.20");
    }

    [Fact]
    public void LinkAnnotationAbsentFromStructTree_ReportsUa825020()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>", // no /K at all
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.20");
    }

    [Fact]
    public void LinkAnnotationReferencedButNotInLinkOrReference_ReportsUa825020()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>", // object 8 — directly under Document, not Link/Reference
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.5.20");
    }

    [Fact]
    public void InvisibleLinkAnnotationAbsentFromStructTree_DoesNotReportUa825020()
    {
        // §8.9.2.2 requires an Invisible-flagged annotation to be an artifact instead — the
        // opposite of this clause's "enclosed within Link/Reference" — so it is exempt here.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /F 1 >>", // Invisible
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.5.20");
    }

    // =====================================================================
    //  Clause 8.6 — outline (bookmark) /Title must not use the Unicode PUA
    // =====================================================================
    //
    //  Object layout: 1=catalog (with /Outlines), 2=Pages, 3=Page, 4=Outlines dict
    //  (/First 5 0 R), 5+=outline items.

    private static byte[] OutlinePdf(params string[] outlineItems)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Outlines /First 5 0 R /Count " +
            outlineItems.Length.ToString(CultureInfo.InvariantCulture) + " >>",
        };
        bodies.AddRange(outlineItems);
        return BuildPdf(bodies);
    }

    [Fact]
    public void OutlineTitleWithPua_ReportsUa86()
    {
        // <FEFFE000> = UTF-16BE BOM + U+E000, the first BMP Private Use Area codepoint.
        byte[] pdf = OutlinePdf("<< /Type /Outlines /Parent 4 0 R /Title <FEFFE000> >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.6");
    }

    [Fact]
    public void OutlineTitleWithoutPua_DoesNotReportUa86()
    {
        byte[] pdf = OutlinePdf("<< /Type /Outlines /Parent 4 0 R /Title (Chapter One) >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.6");
    }

    [Fact]
    public void NoOutlines_DoesNotReportUa86()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.6");
    }

    [Fact]
    public void NestedOutlineChildWithPua_ReportsUa86()
    {
        byte[] pdf = OutlinePdf(
            "<< /Type /Outlines /Parent 4 0 R /Title (Chapter One) /First 6 0 R >>", // object 5
            "<< /Type /Outlines /Parent 5 0 R /Title <FEFFE000> >>"); // object 6 — child via /First

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.6");
    }

    [Fact]
    public void SecondSiblingWithPua_ReportsUa86()
    {
        byte[] pdf = OutlinePdf(
            "<< /Type /Outlines /Parent 4 0 R /Title (Chapter One) /Next 6 0 R >>", // object 5
            "<< /Type /Outlines /Parent 4 0 R /Title <FEFFE000> >>"); // object 6 — sibling via /Next

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.6");
    }

    [Fact]
    public void SelfReferencingNextCycle_ReportsExactlyOnceAndDoesNotHangUa86()
    {
        // A malformed /Next pointing back to the same item — the cycle guard must stop the walk
        // after visiting it once, not loop forever or double-count the one real violation.
        byte[] pdf = OutlinePdf(
            "<< /Type /Outlines /Parent 4 0 R /Title <FEFFE000> /Next 5 0 R >>"); // object 5

        var result = Validate(pdf);
        Assert.Single(result.Violations, v => v.Code == "UA2-8.6");
    }

    // =====================================================================
    //  Clause 8.9.2.1 — annotations must be included in the structure tree
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=StructTreeRoot, 3=Pages, 4=Page (with /Annots),
    //  5=Document, 6=the annotation, 7=namespace, 8=an /Annot wrapper + /OBJR (when present).

    [Fact]
    public void ScreenAnnotationEnclosedSomewhere_DoesNotReportUa8921()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R /K 8 0 R >>",
            "<< /Type /Annot /Subtype /Screen /Contents (A video) /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Annot /P 5 0 R /K 9 0 R >>",
            "<< /Type /OBJR /Obj 6 0 R /Pg 4 0 R >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    [Fact]
    public void ScreenAnnotationAbsentFromTree_ReportsUa8921()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>",
            "<< /Type /Annot /Subtype /Screen /Contents (A video) /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    [Fact]
    public void PopupAnnotationAbsentFromTree_DoesNotReportUa8921()
    {
        // Popup is excluded from this clause's candidate set — §8.9.2.4.9 requires the opposite:
        // absence from the structure tree, not presence.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>",
            "<< /Type /Annot /Subtype /Popup /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    [Fact]
    public void WidgetAnnotationAbsentFromTree_DoesNotReportUa8921()
    {
        // Widgets are out of this clause's scope entirely — its own General text carves out forms
        // ("except for forms (see 8.10)").
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    [Fact]
    public void InvisibleFlaggedAnnotationAbsentFromTree_DoesNotReportUa8921()
    {
        // §8.9.2.2 requires an Invisible-flagged annotation to be an artifact instead of being
        // included in the tree — the opposite requirement, so it is exempt here.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>",
            "<< /Type /Annot /Subtype /Screen /Contents (A video) /Rect [0 0 10 10] /F 1 >>", // Invisible
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    [Fact]
    public void PrinterMarkAbsentFromTree_DoesNotReportUa8921()
    {
        // §8.9.2.4.14 requires a printer's mark to be an artifact unconditionally, so it is
        // excluded here regardless of flags.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R >>",
            "<< /Type /StructTreeRoot /K 5 0 R >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /StructElem /S /Document /P 2 0 R /NS 7 0 R >>",
            "<< /Type /Annot /Subtype /PrinterMark /Rect [0 0 10 10] >>",
            NamespaceDict(Pdf2Namespace),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    // =====================================================================
    //  Clause 8.9.2.1 — annotation appearance streams must not use marked
    //  content to substructure themselves (a second, independent sentence
    //  sharing the same UA2-8.9.2.1 code as the tree-inclusion checks above)
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (with /Annots), 4=the
    //  annotation, 5+=its /AP /N appearance stream(s).

    [Fact]
    public void SimpleMarkedContentInAppearanceStream_ReportsUa8921()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] /AP << /N 5 0 R >> >>",
            "<< /Length 0 >>\nstream\nBMC q 0 0 10 10 re f Q EMC\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    [Fact]
    public void PropertyMarkedContentInAppearanceStream_ReportsUa8921()
    {
        // BDC (marked content with a properties operand), not just bare BMC.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] /AP << /N 5 0 R >> >>",
            "<< /Length 0 >>\nstream\n/Span BDC q 0 0 10 10 re f Q EMC\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    [Fact]
    public void AppearanceStreamWithoutMarkedContent_DoesNotReportUa8921()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] /AP << /N 5 0 R >> >>",
            "<< /Length 0 >>\nstream\nq 0 0 10 10 re f Q\nendstream",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    [Fact]
    public void NonSelectedApStateWithMarkedContent_ReportsUa8921()
    {
        // /AS selects "Off", but the marked content sits only in the "On" state's stream — the
        // clause names the file's appearance streams in general, not "the currently active one,"
        // so every named state must be scanned.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AS /Off " +
                "/AP << /N << /Off 5 0 R /On 6 0 R >> >> >>",
            "<< /Length 0 >>\nstream\nq 0 0 10 10 re f Q\nendstream",
            "<< /Length 0 >>\nstream\nBMC q 0 0 10 10 re f Q EMC\nendstream",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    [Fact]
    public void SharedAppearanceStreamDeduplicatedAcrossPages_ReportsOnce()
    {
        // Two annotations on two DIFFERENT pages reference the identical appearance stream
        // object — must be reported exactly once, not once per referencing annotation. Two
        // annotations on the SAME page would already be deduplicated by PdfXValidator.ScanPage's
        // own internal, per-call seenForms set, which would pass even without this check's own
        // document-wide dedup — proven by a mutation that removed this check's own `seen` guard
        // and survived exactly that way against a same-page version of this fixture.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [5 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [6 0 R] >>",
            "<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] /AP << /N 7 0 R >> >>",
            "<< /Type /Annot /Subtype /Square /Rect [20 20 30 30] /AP << /N 7 0 R >> >>",
            "<< /Length 0 >>\nstream\nBMC q 0 0 10 10 re f Q EMC\nendstream",
        });

        var result = Validate(pdf);
        Assert.Single(result.Violations, v => v.Code == "UA2-8.9.2.1");
    }

    // =====================================================================
    //  Clause 8.4.5.6 — simple-font declared vs. embedded glyph widths
    // =====================================================================
    //
    //  Object layout common to these fixtures: 1=catalog, 2=Pages, 3=Page (with
    //  /Resources /Font /F1 4 0 R and /Contents 7 0 R), 4=the simple font, 5=its
    //  /FontDescriptor, 6=the embedded font program (/FontFile2 for TrueType, /FontFile3 for
    //  CFF), 7=the page content. The clause covers fonts "referenced for rendering", so a
    //  fixture that means to be judged must actually show its font.

    private static string ContentObj(string operators)
        => StreamObj(Encoding.ASCII.GetBytes(operators));

    private static byte[] TrueTypeWidthPdf(byte[] fontProgram, int declaredWidthOfA, int flags,
        string content = "BT /F1 12 Tf (A) Tj ET")
        => SimpleTrueTypeWidthPdf(fontProgram, declaredWidthOfA.ToString(CultureInfo.InvariantCulture), flags, content);

    private static byte[] SimpleTrueTypeWidthPdf(byte[] fontProgram, string widths, int flags, string content)
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 65 " +
                $"/Widths [{widths}] /FontDescriptor 5 0 R >>",
            $"<< /Type /FontDescriptor /FontName /TestFont /Flags {flags} /FontFile2 6 0 R >>",
            StreamObj(fontProgram),
            ContentObj(content),
        });

    [Fact]
    public void MatchingTrueTypeWidth_DoesNotReportUa8456()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("A");
        byte[] pdf = TrueTypeWidthPdf(font, MinimalTrueType.MappedGlyphAdvance, flags: 4); // Symbolic

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void MismatchedTrueTypeWidth_ReportsUa8456()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("A");
        byte[] pdf = TrueTypeWidthPdf(font, MinimalTrueType.MappedGlyphAdvance + 90, flags: 4);

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void TrueTypeWidthOffByExactlyOneUnit_DoesNotReportUa8456()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("A");
        byte[] pdf = TrueTypeWidthPdf(font, MinimalTrueType.MappedGlyphAdvance + 1, flags: 4);

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void NonsymbolicTrueTypeWithNoDifferences_DoesNotReportUa8456()
    {
        // Ambiguous code — no unambiguous glyph-id route exists, so this must be skipped, not
        // guessed at, even with a wildly mismatched declared width.
        byte[] font = MinimalTrueType.Build("A");
        byte[] pdf = TrueTypeWidthPdf(font, declaredWidthOfA: 999999, flags: 32); // Nonsymbolic

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void HugeDeclaredLastChar_DoesNotHangUa8456()
    {
        // /LastChar is attacker-controlled and unrelated to the real /Widths array length; the
        // iteration must be bounded by the physical array, not the declared range.
        byte[] font = MinimalTrueType.BuildSymbolic("A");
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /TestFont /FirstChar 65 " +
                "/LastChar 2147483647 /Widths [700] /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 4 /FontFile2 6 0 R >>",
            StreamObj(font),
            ContentObj("BT /F1 12 Tf (A) Tj ET"),
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.6"); // the one real, in-range mismatch
    }

    [Fact]
    public void UnreferencedFontWithMismatchedWidth_DoesNotReportUa8456()
    {
        // The clause covers fonts "referenced for rendering": listed in /Resources, never shown.
        byte[] font = MinimalTrueType.BuildSymbolic("A");
        byte[] pdf = TrueTypeWidthPdf(font, MinimalTrueType.MappedGlyphAdvance + 90, flags: 4,
            content: "0 0 10 10 re f");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    // "AB" program: A = MappedGlyphAdvance, B = MappedGlyphAdvance + 10. /Widths below give A a
    // wrong width and B its right one, so only A can ever be reported.
    private static string WrongAThenRightB => string.Format(CultureInfo.InvariantCulture, "{0} {1}",
        MinimalTrueType.MappedGlyphAdvance + 90, MinimalTrueType.MappedGlyphAdvance + 10);

    [Fact]
    public void FontShownOnlyInRenderingMode3_DoesNotReportUa8456()
    {
        // Only B is shown, and only in mode 3, so the font is not "referenced for rendering" at
        // all. A is never shown and has a wrong width: the per-glyph mode-3 exemption does not
        // cover it (it was not shown in mode 3 either), so only the font-level scope keeps it quiet.
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        byte[] pdf = SimpleTrueTypeWidthPdf(font, WrongAThenRightB, flags: 4,
            content: "BT /F1 12 Tf 3 Tr (B) Tj ET");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void GlyphShownOnlyInRenderingMode3_IsExempt_DoesNotReportUa8456()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        byte[] pdf = SimpleTrueTypeWidthPdf(font, WrongAThenRightB, flags: 4,
            content: "BT /F1 12 Tf (B) Tj 3 Tr (A) Tj ET");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void GlyphShownInRenderingMode3AndVisibly_IsNotExempt_ReportsUa8456()
    {
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        byte[] pdf = SimpleTrueTypeWidthPdf(font, WrongAThenRightB, flags: 4,
            content: "BT /F1 12 Tf 3 Tr (A) Tj 0 Tr (A) Tj ET");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.6" && v.Message.Contains("code 65"));
    }

    [Fact]
    public void UnshownGlyphOfARenderedFont_IsStillJudged_ReportsUa8456()
    {
        // Only B is shown, but the font is rendered and the clause says "every glyph".
        byte[] font = MinimalTrueType.BuildSymbolic("AB");
        byte[] pdf = SimpleTrueTypeWidthPdf(font, WrongAThenRightB, flags: 4,
            content: "BT /F1 12 Tf (B) Tj ET");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.6" && v.Message.Contains("code 65"));
    }

    [Fact]
    public void CompositeFontWithNoEmbeddedProgram_DoesNotReportUa8456()
    {
        // Composite fonts are judged only against an embedded CIDFontType2 program; this one
        // carries none, so there is nothing to compare /W with.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /TestType0 /Encoding /Identity-H " +
                "/DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestType0 /W [1 [999999]] >>",
            ContentObj("BT /F1 12 Tf <0001> Tj ET"),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void CffEmbeddedSimpleFontWithNoDifferencesName_IsNotJudged_DoesNotReportUa8456()
    {
        // A CFF-embedded simple font is judged only for a code whose glyph a /Differences name
        // selects (SimpleFontWidthProbe's remarks). This one has no /Encoding at all, so code 65
        // reaches its glyph through the program's built-in encoding and is declined, whatever its
        // width. The font IS shown, so the skip is for the encoding, not for being unreferenced.
        var glyphs = MinimalOpenTypeCff.NamedBoxGlyphs("A");
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(glyphs);
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /TestCff /FirstChar 65 " +
                "/Widths [999999] /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestCff /Flags 4 /FontFile3 6 0 R >>",
            StreamObj(cff, " /Subtype /Type1C"),
            ContentObj("BT /F1 12 Tf (A) Tj ET"),
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    // Type 1 family simple fonts over an embedded program. Layout: 1=catalog, 2=Pages, 3=Page (/F1 4 0 R,
    // /Contents 7 0 R), 4=a Type1 font showing code 65 through `encoding`, 5=its descriptor, 6=the
    // program under `fontFileKey`, 7=content.

    private static byte[] Type1FamilyPdf(byte[] program, string fontFileKey, string streamEntries,
        int declaredWidthOfA, string encoding = "<< /Differences [65 /A] >>")
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /TestFont /FirstChar 65 " +
                $"/Widths [{declaredWidthOfA}] /Encoding {encoding} /FontDescriptor 5 0 R >>",
            $"<< /Type /FontDescriptor /FontName /TestFont /Flags 4 /{fontFileKey} 6 0 R >>",
            StreamObj(program, streamEntries),
            ContentObj("BT /F1 12 Tf (A) Tj ET"),
        });

    /// <summary>A bare CFF whose glyph A (GID 1) advances 500.</summary>
    private static byte[] Type1CPdf(int declaredWidthOfA, string encoding = "<< /Differences [65 /A] >>")
        => Type1FamilyPdf(MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("A")),
            "FontFile3", " /Subtype /Type1C", declaredWidthOfA, encoding);

    /// <summary>A Type 1 program whose glyph A advances 610; <paramref name="extraCharStrings"/> is appended raw.</summary>
    private static byte[] Type1ProgramPdf(int declaredWidthOfA, string encoding = "<< /Differences [65 /A] >>",
        string extraCharStrings = null)
    {
        var (program, length1, length2) = MinimalType1.Build(
            new[] { (".notdef", MinimalType1.AdvanceOnly(0)), ("A", MinimalType1.AdvanceOnly(610)) },
            extraCharStringsText: extraCharStrings);
        return Type1FamilyPdf(program, "FontFile",
            $" /Length1 {length1.ToString(CultureInfo.InvariantCulture)} /Length2 {length2.ToString(CultureInfo.InvariantCulture)}",
            declaredWidthOfA, encoding);
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(501, false)]
    [InlineData(590, true)]
    public void Type1CWidthAgainstTheCffAdvanceOfTheGlyphItsDifferencesNameSelects_Ua8456(int declared, bool reports)
    {
        var findings = Validate(Type1CPdf(declared)).Violations.Where(v => v.Code == "UA2-8.4.5.6").ToList();
        if (reports) Assert.Contains(findings, v => v.Message.Contains("code 65") && v.Message.Contains("(500)"));
        else Assert.Empty(findings);
    }

    [Theory]
    [InlineData(610, false)]
    [InlineData(700, true)]
    public void Type1ProgramWidthAgainstTheHsbwAdvanceOfTheGlyphItsDifferencesNameSelects_Ua8456(int declared, bool reports)
    {
        var findings = Validate(Type1ProgramPdf(declared)).Violations.Where(v => v.Code == "UA2-8.4.5.6").ToList();
        if (reports) Assert.Contains(findings, v => v.Message.Contains("code 65") && v.Message.Contains("(610)"));
        else Assert.Empty(findings);
    }

    // Composite fonts over CFF (Type 0 / CIDFontType0). Layout: 1=catalog, 2=Pages, 3=Page (/F1 4 0 R,
    // /Contents 7 0 R), 4=Type0 (Identity-H), 5=CIDFontType0, 6=its descriptor, 7=content, 8=the program.

    private static byte[] CompositeCffWidthPdf(byte[] cff, string widths, string content)
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /TestCid /Encoding /Identity-H /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /TestCid " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/FontDescriptor 6 0 R {widths} >>",
            "<< /Type /FontDescriptor /FontName /TestCid /Flags 4 /FontFile3 8 0 R >>",
            ContentObj(content),
            StreamObj(cff, " /Subtype /CIDFontType0C"),
        });

    [Theory]
    [InlineData(500, false)]
    [InlineData(590, true)]
    public void CompositeWidthOverANonCidKeyedCff_ComparesTheGlyphTheCidIndexes_Ua8456(int declared, bool reports)
    {
        byte[] cff = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("A"));
        var findings = Validate(CompositeCffWidthPdf(cff, W(1, declared), "BT /F1 12 Tf <0001> Tj ET"))
            .Violations.Where(v => v.Code == "UA2-8.4.5.6").ToList();
        if (reports) Assert.Contains(findings, v => v.Message.Contains("CID 1") && v.Message.Contains("glyph 1"));
        else Assert.Empty(findings);
    }

    [Theory]
    [InlineData(500, false)]
    [InlineData(590, true)]
    public void CompositeWidthOverACidKeyedCff_ComparesTheGlyphTheCharsetMapsTheCidTo_Ua8456(int declared, bool reports)
    {
        // CID 20 is GID 2 through the charset; read as a glyph index it is out of range and skipped.
        byte[] cff = MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20 });
        var findings = Validate(CompositeCffWidthPdf(cff, W(20, declared), "BT /F1 12 Tf <0014> Tj ET"))
            .Violations.Where(v => v.Code == "UA2-8.4.5.6").ToList();
        if (reports) Assert.Contains(findings, v => v.Message.Contains("CID 20") && v.Message.Contains("glyph 2"));
        else Assert.Empty(findings);
    }

    // Vertical metrics (§8.4.5.6's second paragraph). Layout: 1=catalog, 2=Pages, 3=Page (/F1 4 0 R,
    // /Contents 7 0 R), 4=Type0 over `encoding`, 5=CIDFontType2 (/CIDToGIDMap /Identity), 6=descriptor,
    // 7=content, 8=the program. CID 1 is glyph 1.

    private static byte[] VerticalPdf(byte[] program, string cidFontEntries, string encoding = "Identity-V",
        string content = "BT /F1 12 Tf <0001> Tj ET", params string[] extraObjects)
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            $"<< /Type /Font /Subtype /Type0 /BaseFont /TestVert /Encoding /{encoding} /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestVert " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/FontDescriptor 6 0 R /CIDToGIDMap /Identity {cidFontEntries} >>",
            "<< /Type /FontDescriptor /FontName /TestVert /Flags 4 /FontFile2 8 0 R >>",
            ContentObj(content),
            StreamObj(program),
        }.Concat(extraObjects).ToArray());   // extras are numbered from 9

    private static List<PdfUa2StructureViolation> VerticalFindings(byte[] pdf)
        => Validate(pdf).Violations.Where(v => v.Code == "UA2-8.4.5.6" && v.Message.Contains("vertical")).ToList();

    /// <summary>
    /// Wave 17 R13.4: a displacement written as an indirect reference (object 9 holds −900 against
    /// a <c>vmtx</c> of 1000) is resolved before it is judged — in a <c>/W2</c> entry and in
    /// <c>/DW2</c> alike. Read as stored, the reference is not a number: the entry fell to Table
    /// 117's −1000, which matches the program, and the inconsistent font passed.
    /// </summary>
    [Theory]
    [InlineData("/W2 [1 [9 0 R 305 880]]", "from /W2")]
    [InlineData("/DW2 [880 9 0 R]", "from /DW2")]
    public void VerticalMetricsWrittenAsReferences_AreResolvedBeforeTheyAreJudged_Ua8456(string entries, string source)
    {
        var findings = VerticalFindings(VerticalPdf(
            MinimalTrueType.BuildSymbolicWithVerticalMetrics("AB", 1000), entries, extraObjects: "-900"));
        Assert.Contains(findings, v => v.Message.Contains("CID 1") && v.Message.Contains(source));
    }

    [Theory]
    [InlineData("/W2 [1 [-1000 305 880]]", false)]
    [InlineData("/W2 [1 [-900 305 880]]", true)]
    [InlineData("/W2 [1 1 -1000 305 880]", false)]
    [InlineData("/W2 [1 1 -900 305 880]", true)]
    public void W2DisplacementAgainstTheProgramsVmtx_Ua8456(string w2, bool reports)
    {
        var findings = VerticalFindings(VerticalPdf(MinimalTrueType.BuildSymbolicWithVerticalMetrics("AB", 1000), w2));
        if (reports) Assert.Contains(findings, v => v.Message.Contains("CID 1") && v.Message.Contains("from /W2"));
        else Assert.Empty(findings);
    }

    [Theory]
    [InlineData("/DW2 [880 -1000]", 1000, false)]
    [InlineData("/DW2 [880 -900]", 1000, true)]
    [InlineData("", 1000, false)]   // no /DW2: Table 117's default -1000
    [InlineData("", 900, true)]
    public void DW2DisplacementForACidWithNoW2Entry_Ua8456(string dw2, int advanceHeight, bool reports)
    {
        var findings = VerticalFindings(VerticalPdf(MinimalTrueType.BuildSymbolicWithVerticalMetrics("AB", advanceHeight), dw2));
        if (reports) Assert.Contains(findings, v => v.Message.Contains("from /DW2"));
        else Assert.Empty(findings);
    }

    /// <summary>The same layout over a <c>CIDFontType0</c> whose <c>/FontFile3</c> (object 8) has <paramref name="subtype"/>.</summary>
    private static byte[] VerticalCffPdf(byte[] program, string cidFontEntries, string subtype, string content,
        string encoding = "Identity-V")
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            $"<< /Type /Font /Subtype /Type0 /BaseFont /TestVert /Encoding /{encoding} /DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /TestVert " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/FontDescriptor 6 0 R {cidFontEntries} >>",
            "<< /Type /FontDescriptor /FontName /TestVert /Flags 4 /FontFile3 8 0 R >>",
            ContentObj(content),
            StreamObj(program, $" /Subtype /{subtype}"),
        });

    [Theory]
    [InlineData("/W2 [20 [-1000 250 880]]", false)]
    [InlineData("/W2 [20 [-900 250 880]]", true)]
    public void OpenTypeWrappedCffVerticalMetrics_AreComparedForTheGlyphTheCharsetSelects_Ua8456(string w2, bool reports)
    {
        // CID 20 is glyph 2 of a CID-keyed CFF; the OpenType wrapper's vmtx gives every glyph 1000.
        byte[] otf = MinimalOpenTypeCff.WrapInOtto(MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20 }), "AB",
            verticalAdvance: 1000);
        var findings = VerticalFindings(VerticalCffPdf(otf, w2, "OpenType", "BT /F1 12 Tf <0014> Tj ET"));

        if (reports) Assert.Contains(findings, v => v.Message.Contains("CID 20") && v.Message.Contains("glyph 2"));
        else Assert.Empty(findings);
    }

    /// <summary>
    /// A three-glyph CFF2 table: header, a Top DICT naming the CharStrings at offset 9, an empty global
    /// subroutine INDEX, and three empty charstrings. CFF2 has no charset, so a CID is its glyph.
    /// </summary>
    private static readonly byte[] ThreeGlyphCff2 =
    {
        0x02, 0x00, 0x05, 0x00, 0x02,       // header: major 2, minor 0, hdrSize 5, topDictLength 2
        0x94, 0x11,                         // Top DICT: 9 CharStrings
        0x00, 0x00,                         // GlobalSubr INDEX: empty
        0x00, 0x03, 0x01, 0x01, 0x02, 0x03, 0x04, 0x0E, 0x0E, 0x0E,
    };

    [Theory]
    [InlineData(500, false)]
    [InlineData(590, true)]
    public void Cff2CompositeWidth_IsComparedWithTheOpenTypeWrappersHmtx_Ua8456(int declared, bool reports)
    {
        // WrapInOtto gives every glyph an hmtx advance of 500; CFF2 charstrings carry no width at all.
        byte[] otf = MinimalOpenTypeCff.WrapInOtto(ThreeGlyphCff2, "AB");
        byte[] pdf = VerticalCffPdf(otf, W(1, declared), "OpenType", "BT /F1 12 Tf <0001> Tj ET", "Identity-H");
        var findings = Validate(pdf).Violations.Where(v => v.Code == "UA2-8.4.5.6").ToList();

        if (reports) Assert.Contains(findings, v => v.Message.Contains("CID 1") && v.Message.Contains("(500)"));
        else Assert.Empty(findings);
    }

    [Fact]
    public void Cff2CompositeWidthForACidPastTheProgramsGlyphs_IsNotComparedWithAFallbackGlyph()
    {
        // CID 5 selects glyph 5 of a three-glyph program. hmtx's lookup falls back to glyph 0 for an id
        // it does not have, and comparing /W against that would judge a glyph that was never selected.
        byte[] otf = MinimalOpenTypeCff.WrapInOtto(ThreeGlyphCff2, "AB");
        byte[] pdf = VerticalCffPdf(otf, W(5, 590), "OpenType", "BT /F1 12 Tf <0005> Tj ET", "Identity-H");
        Assert.DoesNotContain(Validate(pdf).Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void ABareCffWithNoOpenTypeWrapper_IsNotJudgedOnVerticalMetrics()
        => Assert.Empty(VerticalFindings(VerticalCffPdf(MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20 }),
            "/W2 [20 [-900 250 880]]", "CIDFontType0C", "BT /F1 12 Tf <0014> Tj ET")));

    [Fact]
    public void AFontUsedInHorizontalWritingMode_IsNotJudgedOnVerticalMetrics()
        => Assert.Empty(VerticalFindings(VerticalPdf(MinimalTrueType.BuildSymbolicWithVerticalMetrics("AB", 1000),
            "/W2 [1 [-900 305 880]]", encoding: "Identity-H")));

    [Fact]
    public void AProgramWithoutVerticalMetrics_IsNotJudged()
        => Assert.Empty(VerticalFindings(VerticalPdf(MinimalTrueType.BuildSymbolic("AB"), "/W2 [1 [-900 305 880]]")));

    [Fact]
    public void AGlyphShownOnlyInRenderingMode3_IsNotJudgedOnVerticalMetrics()
    {
        // CID 2 is rendered normally, so the font IS referenced for rendering and reaches the check;
        // only CID 1 — whose /W2 entry is wrong — is confined to mode 3. (With every glyph in mode 3
        // the whole font is skipped earlier, and the exclusion inside the check is never reached.)
        byte[] pdf = VerticalPdf(MinimalTrueType.BuildSymbolicWithVerticalMetrics("AB", 1000),
            "/W2 [1 [-900 305 880]]", content: "BT /F1 12 Tf <0002> Tj 3 Tr <0001> Tj ET");
        Assert.Empty(VerticalFindings(pdf));
    }

    // Composite (Type 0 / CIDFontType2) widths. Layout: 1=catalog, 2=Pages, 3=Page (/F1 4 0 R,
    // /Contents 8 0 R), 4=Type0 (Identity-H), 5=CIDFontType2, 6=its descriptor, 7=the "AB" program
    // (glyph 1 = A, glyph 2 = B), 8=content, 9=an optional extra object (a /CIDToGIDMap stream).

    private static byte[] CompositeWidthPdf(string cidFontEntries, string content, params string[] extra)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] " +
                "/Resources << /Font << /F1 4 0 R >> >> /Contents 8 0 R >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /TestCid /Encoding /Identity-H " +
                "/DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /TestCid " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/FontDescriptor 6 0 R {cidFontEntries} >>",
            "<< /Type /FontDescriptor /FontName /TestCid /Flags 4 /FontFile2 7 0 R >>",
            StreamObj(MinimalTrueType.BuildSymbolic("AB")),
            ContentObj(content),
        };
        bodies.AddRange(extra);
        return BuildPdf(bodies);
    }

    private static string W(int cid, params int[] widths)
        => $"/W [{cid} [{string.Join(" ", widths)}]]";

    [Fact]
    public void CompositeWidthsMatchingTheProgram_DoesNotReportUa8456()
    {
        byte[] pdf = CompositeWidthPdf(
            "/CIDToGIDMap /Identity " + W(1, MinimalTrueType.MappedGlyphAdvance, MinimalTrueType.MappedGlyphAdvance + 10),
            "BT /F1 12 Tf <0001> Tj ET");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void CompositeWidthMismatchedWithTheProgram_ReportsUa8456()
    {
        byte[] pdf = CompositeWidthPdf(
            "/CIDToGIDMap /Identity " + W(1, MinimalTrueType.MappedGlyphAdvance + 90),
            "BT /F1 12 Tf <0001> Tj ET");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.6" && v.Message.Contains("CID 1"));
    }

    [Fact]
    public void CompositeWidthThroughACidToGidMapStream_ComparesTheMappedGlyph_ReportsUa8456()
    {
        // CID 5 maps to glyph 1 (A) through the stream. Its /W width is B's advance — right for
        // glyph 2, wrong for the glyph actually selected — so reading CID 5 as glyph 5 (out of
        // range, skipped) or ignoring the map would stay silent.
        byte[] map = new byte[12];
        map[10] = 0x00; map[11] = 0x01;   // CID 5 → GID 1
        byte[] pdf = CompositeWidthPdf(
            "/CIDToGIDMap 9 0 R " + W(5, MinimalTrueType.MappedGlyphAdvance + 10),
            "BT /F1 12 Tf <0005> Tj ET",
            StreamObj(map));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.6" && v.Message.Contains("glyph 1"));
    }

    [Fact]
    public void CompositeCidLeftToDW_IsNotJudged_DoesNotReportUa8456()
    {
        // No /W: every CID takes /DW, a fallback like a simple font's /MissingWidth, not an
        // explicit width.
        byte[] pdf = CompositeWidthPdf("/CIDToGIDMap /Identity /DW 999", "BT /F1 12 Tf <0001> Tj ET");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    [Fact]
    public void CompositeGlyphShownOnlyInRenderingMode3_IsExempt_DoesNotReportUa8456()
    {
        // CID 1's width is wrong but it is shown only in mode 3; CID 2 is shown visibly and right.
        byte[] pdf = CompositeWidthPdf(
            "/CIDToGIDMap /Identity " + W(1, MinimalTrueType.MappedGlyphAdvance + 90, MinimalTrueType.MappedGlyphAdvance + 10),
            "BT /F1 12 Tf <0002> Tj 3 Tr <0001> Tj ET");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.6");
    }

    // =====================================================================
    //  Clause 8.8 — Link destinations must be structure destinations
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=the page (with /Annots), 4=the page again
    //  (target of a page-based destination — same object as 3, referenced a second time
    //  so the destination array can name it explicitly), 5=a structure element (target of
    //  a structure destination).

    [Fact]
    public void DirectDestTargetingPage_ReportsUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [3 0 R /Fit] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.8");
    }

    [Fact]
    public void DirectDestTargetingStructElem_DoesNotReportUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [5 0 R /Fit] >>",
            "<< /Type /StructElem /S /P /P 3 0 R >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.8");
    }

    [Fact]
    public void GoToActionDestTargetingPage_ReportsUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] " +
                "/A << /S /GoTo /D [3 0 R /Fit] >> >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.8");
    }

    [Fact]
    public void GoToRAction_DoesNotReportUa88()
    {
        // Cross-document: /S /GoToR is excluded outright. /D's first element is deliberately a
        // real page reference here (unrealistic for genuine GoToR, whose target lives in another
        // file) specifically so this fixture would trip the page-type check if the /S /GoTo gate
        // were ever removed or loosened — a weaker fixture (e.g. a page-index integer) would pass
        // this test even without the gate, proving nothing.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] " +
                "/A << /S /GoToR /F (other.pdf) /D [3 0 R /Fit] >> >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.8");
    }

    [Fact]
    public void NonLinkAnnotationWithPageDest_DoesNotReportUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Text /Rect [0 0 10 10] /Dest [3 0 R /Fit] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.8");
    }

    [Fact]
    public void DestFirstElementUnresolvable_DoesNotReportUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest [42 /Fit] >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.8");
    }

    [Fact]
    public void GoToActionWithStructureDestinationInSD_DoesNotReportUa88()
    {
        // ISO 32000-2 Table 202: a GoTo action's /D is REQUIRED, and /SD (PDF 2.0) is the structure
        // destination that takes precedence over it. A PDF 2.0 file linking by structure keeps a
        // page /D for older readers — the action jumps to its /SD.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] " +
                "/A << /S /GoTo /D [3 0 R /Fit] /SD [5 0 R /Fit] >> >>",
            "<< /Type /StructElem /S /P /P 3 0 R >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.8");
    }

    // §8.8 names "all destinations (see ISO 32000-2:2020, 12.3.2)" — outline items, named
    // destinations and /OpenAction hold them too. Layout below: 1=catalog, 2=Pages, 3=the page,
    // then whatever each test adds from object 4.

    private static bool IsDestinationFinding(PdfUa2StructureViolation v, string holder)
        => v.Code == "UA2-8.8" && v.Message.Contains(holder);

    [Fact]
    public void OutlineItemDestTargetingPage_ReportsUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            "<< /Title (One) /Parent 4 0 R /Dest [3 0 R /Fit] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => IsDestinationFinding(v, "outline"));
    }

    [Fact]
    public void OutlineItemDestTargetingStructElem_DoesNotReportUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 1 >>",
            "<< /Title (One) /Parent 4 0 R /Dest [6 0 R /Fit] >>",
            "<< /Type /StructElem /S /H1 /P 3 0 R >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.8");
    }

    [Fact]
    public void NestedOutlineItemGoToTargetingPage_ReportsUa88()
    {
        // The offending item is a child (reached through /First) and uses a GoTo action, not /Dest.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Outlines /First 5 0 R /Last 5 0 R /Count 2 >>",
            "<< /Title (Parent) /Parent 4 0 R /First 6 0 R /Last 6 0 R /Count 1 >>",
            "<< /Title (Child) /Parent 5 0 R /A << /S /GoTo /D [3 0 R /XYZ 0 200 0] >> >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => IsDestinationFinding(v, "outline"));
    }

    [Fact]
    public void NamedDestinationInNameTreeTargetingPage_ReportsUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 4 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Names [(chap1) [3 0 R /Fit]] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => IsDestinationFinding(v, "\"chap1\""));
    }

    [Fact]
    public void NamedDestinationDictionaryFormInLegacyDests_ReportsUa88()
    {
        // The PDF 1.1 catalog /Dests dictionary, with the << /D [...] >> value form §12.3.2.3 allows.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Dests << /chap1 << /D [3 0 R /Fit] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => IsDestinationFinding(v, "\"chap1\""));
    }

    [Fact]
    public void NamedDestinationTargetingStructElem_DoesNotReportUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 4 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Names [(chap1) [5 0 R /Fit]] >>",
            "<< /Type /StructElem /S /H1 /P 3 0 R >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.8");
    }

    [Fact]
    public void LinkNamingANamedDestination_IsJudgedOnceAtTheDefinition_Ua88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /Names << /Dests 5 0 R >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Dest (chap1) >>",
            "<< /Names [(chap1) [3 0 R /Fit]] >>",
        });

        var result = Validate(pdf);
        var finding = Assert.Single(result.Violations, v => v.Code == "UA2-8.8");
        Assert.Contains("\"chap1\"", finding.Message);
    }

    [Fact]
    public void OpenActionTargetingPage_ReportsUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction [3 0 R /Fit] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
        });

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => IsDestinationFinding(v, "/OpenAction"));
    }

    [Fact]
    public void OpenActionGoToWithStructureDestinationInSD_DoesNotReportUa88()
    {
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /GoTo /D [3 0 R /Fit] /SD [4 0 R /Fit] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /StructElem /S /Document /P 3 0 R >>",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.8");
    }

    // =====================================================================
    //  Clause 8.4.5.9 — the .notdef glyph (Type0/CID fonts) must not be shown
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (with /Contents 7 0 R, /Resources
    //  /Font << /F1 4 0 R >>), 4=Type0 font (/Encoding 6 0 R, /DescendantFonts [5 0 R]),
    //  5=CIDFontType2 descendant, 6=the embedded CID CMap stream, 7=the page content
    //  stream. The CMap maps code <0041> to CID 100 (ordinary) and code <0042> to CID 0
    //  (.notdef).

    private const string CidCMapPrologue =
        "/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n" +
        "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
        "1 begincidrange\n<0041> <0041> 100\nendcidrange\n" +
        "1 begincidchar\n<0042> 0\nendcidchar\n";
    private const string CidCMapEpilogue = "endcmap\nend\nend";

    private static byte[] CidFontPdf(string content, string fontResources = "/F1 4 0 R")
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 7 0 R " +
                $"/Resources << /Font << {fontResources} >> >> >>",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Foo /Encoding 6 0 R " +
                "/DescendantFonts [5 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Foo " +
                "/CIDSystemInfo << /Registry (Test) /Ordering (Test) /Supplement 0 >> >>",
            $"<< /Length 0 >>\nstream\n{CidCMapPrologue}{CidCMapEpilogue}\nendstream",
            $"<< /Length 0 >>\nstream\n{content}\nendstream",
        });

    [Fact]
    public void ShowTextWithNotdefCid_ReportsUa8459()
    {
        byte[] pdf = CidFontPdf("BT /F1 12 Tf <00410042> Tj ET");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void ShowTextArrayWithNotdefCid_ReportsUa8459()
    {
        // The -100 array element is a position adjustment, not a string — proving TJ's numbers
        // are skipped rather than mistaken for shown text.
        byte[] pdf = CidFontPdf("BT /F1 12 Tf [<0041> -100 <0042>] TJ ET");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void NextLineShowTextWithNotdefCid_ReportsUa8459()
    {
        byte[] pdf = CidFontPdf("BT /F1 12 Tf <0042> ' ET");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void NextLineSetSpacingShowTextWithNotdefCid_ReportsUa8459()
    {
        byte[] pdf = CidFontPdf("BT /F1 12 Tf 0 0 <0042> \" ET");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void ShowTextWithoutNotdefCid_DoesNotReportUa8459()
    {
        byte[] pdf = CidFontPdf("BT /F1 12 Tf <0041> Tj ET");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    /// <summary>
    /// A code no codespace range admits references <c>.notdef</c> (ISO 32000-2 §9.7.6.3: "a
    /// substitute glyph is chosen"): <c>&lt;004141&gt;</c> under a two-byte codespace ends in one
    /// byte, 0x41, which matches nothing — and whose own value is not 0, so only the codespace
    /// reading catches it. The leading <c>&lt;0041&gt;</c> alone is clean (the control above).
    /// </summary>
    [Fact]
    public void ShowTextWithACodeNoCodespaceAdmits_ReportsUa8459()
    {
        var result = Validate(CidFontPdf("BT /F1 12 Tf <004141> Tj ET"));

        var violation = Assert.Single(result.Violations, v => v.Code == "UA2-8.4.5.9");
        Assert.Contains("matches no codespace range", violation.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A valid code no character mapping covers references <c>.notdef</c> too (ISO 32000-2 §9.7.6.3: "If
    /// the CMap does not contain either a character mapping or a notdef mapping for the code, … the glyph
    /// for CID 0 shall be substituted"): <c>&lt;0043&gt;</c> is inside the codespace and in no
    /// <c>cidrange</c> or <c>cidchar</c>. The codec keeps its own value for the renderer, which hid it.
    /// </summary>
    [Fact]
    public void ShowTextWithAValidCodeNoMappingCovers_ReportsUa8459()
    {
        var violation = Assert.Single(Validate(CidFontPdf("BT /F1 12 Tf <00410043> Tj ET")).Violations, v => v.Code == "UA2-8.4.5.9");
        Assert.Contains("character code 67", violation.Message, System.StringComparison.Ordinal);
        Assert.Contains("no character mapping of its CMap covers", violation.Message, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// A simple font under a named encoding: WinAnsi names no glyph for code 0, so showing it references
    /// <c>.notdef</c> — the reading the published profile's 6.2.11.8 test files make; a named code does not.
    /// </summary>
    [Fact]
    public void SimpleFontShowingACodeItsEncodingLeavesUnnamed_ReportsUa8459()
    {
        byte[] Pdf(string shown) => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R /Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            $"<< /Length 0 >>\nstream\nBT /F1 12 Tf {shown} Tj ET\nendstream",
        });
        var violation = Assert.Single(Validate(Pdf("<0041>")).Violations, v => v.Code == "UA2-8.4.5.9");
        Assert.Contains("character code 0 of a /Type1 font, whose encoding names no glyph for it", violation.Message, System.StringComparison.Ordinal);
        Assert.DoesNotContain(Validate(Pdf("(AB)")).Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void SimpleFontShowingArbitraryCode_DoesNotReportUa8459()
    {
        // Out of scope: simple (non-Type0) fonts are not checked at all, per this clause's own
        // deferral — regardless of what code is shown.
        byte[] pdf = BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 5 0 R " +
                "/Resources << /Font << /F1 4 0 R >> >> >>",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo >>",
            "<< /Length 0 >>\nstream\nBT /F1 12 Tf ( ) Tj ET\nendstream",
        });

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void FontRestoredAfterQStillResolvesAndReports_Ua8459()
    {
        // /F2 is never declared in /Resources /Font. If Restore ("Q") failed to pop the font
        // name stack, the current font after Q would stay "F2" (unresolvable) and this would
        // wrongly NOT report — proving the q/Q stack, not just that some font resolves.
        byte[] pdf = CidFontPdf("BT /F1 12 Tf ET q /F2 10 Tf Q BT <0042> Tj ET");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void NoTfBeforeShowText_DoesNotReportUa8459()
    {
        byte[] pdf = CidFontPdf("BT <0042> Tj ET");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    // =====================================================================
    //  Clause 8.4.5.9 (continued) — simple TrueType fonts, via /Differences
    //  resolving through the glyph-index-name convention (g0/index0/cid0) —
    //  no embedded /FontFile2 needed, since EnsureProgram degrades gracefully
    //  to that convention when there is no post table to consult.
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page (with /Contents 4 0 R,
    //  /Resources /Font << /F1 5 0 R >>), 4=the page content stream,
    //  5=the TrueType font (/Encoding /Differences).

    private static byte[] TrueTypeDifferencesPdf(string content, string differencesArray)
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length 0 >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Foo " +
                $"/Encoding << /Differences [{differencesArray}] >> >>",
        });

    [Fact]
    public void TrueTypeDifferenceNamesGlyphIndexZero_ReportsUa8459()
    {
        // "g0" is the glyph-index-name convention TryParseGlyphIndexName recognises — the file's
        // own author directly asserting "draw glyph index 0," unambiguous even with no embedded
        // font program to consult.
        byte[] pdf = TrueTypeDifferencesPdf("BT /F1 12 Tf (A) Tj ET", "65 /g0");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void TrueTypeDifferenceNamesRealGlyph_DoesNotReportUa8459()
    {
        byte[] pdf = TrueTypeDifferencesPdf("BT /F1 12 Tf (A) Tj ET", "65 /g5");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void TrueTypeDifferencesEntryForOtherCodeOnly_DoesNotReportUa8459()
    {
        // /Differences maps code 66 ('B') to glyph 0, but the shown code is 65 ('A'), which has
        // no /Differences entry at all — proving the lookup is per-code, not "any entry present."
        byte[] pdf = TrueTypeDifferencesPdf("BT /F1 12 Tf (A) Tj ET", "66 /g0");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void TrueTypeDifferenceNamesGlyphIndexZeroViaTJArray_ReportsUa8459()
    {
        // The -100 array element is a position adjustment, not a string — proving TJ's numbers
        // are skipped for the TrueType path too, the same way §8.4.5.9's Type0 tests already
        // prove it for CID fonts.
        byte[] pdf = TrueTypeDifferencesPdf("BT /F1 12 Tf [(A) -100 (B)] TJ ET", "65 /g0 66 /g7");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void TrueTypeDifferenceNamingNotdefExplicitly_ReportsUa8459()
    {
        // No post table and no gNN convention — the name itself is the glyph's name.
        byte[] pdf = TrueTypeDifferencesPdf("BT /F1 12 Tf (A) Tj ET", "65 /.notdef");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    // Type 1 and Type 3: a /Differences name .notdef selects that glyph by name (ISO 32000-2
    // §9.6.5.2). Layout as above, with the font at object 5.

    private static byte[] NamedFontDifferencesPdf(string subtype, string content, string differencesArray)
        => BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length 0 >>\nstream\n{content}\nendstream",
            $"<< /Type /Font /Subtype /{subtype} /BaseFont /Foo " +
                (subtype == "Type3" ? "/FontBBox [0 0 1 1] /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs << >> " : "") +
                $"/Encoding << /Differences [{differencesArray}] >> >>",
        });

    [Theory]
    [InlineData("Type1")]
    [InlineData("MMType1")]
    [InlineData("Type3")]
    public void NamedFontShowingACodeWhoseDifferenceIsNotdef_ReportsUa8459(string subtype)
    {
        byte[] pdf = NamedFontDifferencesPdf(subtype, "BT /F1 12 Tf (A) Tj ET", "65 /.notdef 66 /B");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9" && v.Message.Contains($"/{subtype} font"));
    }

    [Fact]
    public void Type1ShowingOnlyCodesWithRealNames_DoesNotReportUa8459()
    {
        // 65 is .notdef but only 66 (B) is shown — the check is per shown code. The font embeds no
        // program, so B's existence cannot be judged either.
        byte[] pdf = NamedFontDifferencesPdf("Type1", "BT /F1 12 Tf (B) Tj ET", "65 /.notdef 66 /B");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void Type1CShowingANameItsCffDoesNotDefine_ReportsUa8459()
    {
        var result = Validate(Type1CPdf(500, encoding: "<< /Differences [65 /B] >>"));
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9" && v.Message.Contains("names /B"));
    }

    [Fact]
    public void Type1CShowingANameItsCffDefines_DoesNotReportUa8459()
        => Assert.DoesNotContain(Validate(Type1CPdf(500)).Violations, v => v.Code == "UA2-8.4.5.9");

    [Fact]
    public void Type1ProgramShowingANameItDoesNotDefine_ReportsUa8459()
    {
        var result = Validate(Type1ProgramPdf(610, encoding: "<< /Differences [65 /B] >>"));
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.4.5.9" && v.Message.Contains("names /B"));
    }

    [Fact]
    public void Type1CWhoseProgramCannotBeDecoded_IsSkippedWithoutAbortingEitherCheck_Ua8456AndUa8459()
    {
        // Seventeen filters exceed PdfLimits.MaxFilterChainLength, so decoding the program throws.
        // Neither the width check nor the undefined-name check may turn that into UA2-99.
        string filters = " /Filter [" + string.Concat(Enumerable.Repeat("/ASCIIHexDecode ", 17)) + "]";
        byte[] pdf = Type1FamilyPdf(MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("A")),
            "FontFile3", " /Subtype /Type1C" + filters, 590, "<< /Differences [65 /B] >>");

        var violations = Validate(pdf).Violations;
        Assert.DoesNotContain(violations, v => v.Code == "UA2-99");
        Assert.DoesNotContain(violations, v => v.Code == "UA2-8.4.5.6" || v.Code == "UA2-8.4.5.9");
    }

    [Fact]
    public void Type1ProgramWhoseCharStringsWereNotAllRead_IsNotJudgedOnUndefinedNames_DoesNotReportUa8459()
    {
        // A skipped /CharStrings entry means B's absence may be the scan's, not the font's.
        var result = Validate(Type1ProgramPdf(610, encoding: "<< /Differences [65 /B] >>",
            extraCharStrings: "/B notALength RD ND\n"));
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.4.5.9");
    }

    // =====================================================================
    //  Clause 6.2 — ISO/TS 32005 structure containment rules
    // =====================================================================
    //
    //  Confirmed directly against PdfStructureNamespaceInclusion.CanContain: "StructTreeRoot" ->
    //  "DocumentFragment" and "Span" -> "Table" are both Forbidden; "P" -> "Table" is Optional
    //  (allowed).

    [Fact]
    public void RootChildIsDocumentFragment_ReportsUa62()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            ChildElement("DocumentFragment", 2));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-6.2");
    }

    [Fact]
    public void RootChildIsDocument_DoesNotReportUa62()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElement(6), NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-6.2");
    }

    [Fact]
    public void NestedSpanContainingTable_ReportsUa62()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            ContainerElement("Span", 5, 8), ChildElement("Table", 7));

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-6.2");
    }

    [Fact]
    public void NestedPContainingTable_DoesNotReportUa62()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            ContainerElement("P", 5, 8), ChildElement("Table", 7));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-6.2");
    }

    [Fact]
    public void UnknownCustomChildRole_DoesNotReportUa62()
    {
        // A custom (unmapped) role name is not in PdfStructureNamespaceInclusion's fixed
        // standard-type lists, so CanContain answers Unknown, not Forbidden — must not report.
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            ContainerElement("CustomXyzRole", 5, 8), ChildElement("Table", 7));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-6.2");
    }

    // =====================================================================
    //  Clause 8.2.4 — a namespace's own /RoleMapNS must not target itself
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=Pages, 3=Page, 4=StructTreeRoot (with /Namespaces),
    //  5+=namespace dictionaries.

    private const string CustomNamespace = "http://example.com/custom-ns";

    private static byte[] RoleMapNsPdf(string namespacesArray, params string[] namespaceDicts)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            $"<< /Type /StructTreeRoot /Namespaces [{namespacesArray}] >>",
        };
        bodies.AddRange(namespaceDicts);
        return BuildPdf(bodies);
    }

    [Fact]
    public void BareNameRoleMapNsToDefaultNamespace_SameNamespace_ReportsUa824()
    {
        // The namespace IS the PDF 1.7 namespace, and a bare-Name /RoleMapNS value targets the
        // default standard structure namespace, which is PDF 1.7 — same namespace, a violation.
        byte[] pdf = RoleMapNsPdf("5 0 R",
            $"<< /Type /Namespace /NS ({Pdf17Namespace}) /RoleMapNS << /CustomType /P >> >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void BareNameRoleMapNsToDefaultNamespace_DifferentNamespace_DoesNotReportUa824()
    {
        byte[] pdf = RoleMapNsPdf("5 0 R",
            $"<< /Type /Namespace /NS ({CustomNamespace}) /RoleMapNS << /CustomType /P >> >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void ArrayRoleMapNsToSameNamespaceDictionary_ReportsUa824()
    {
        byte[] pdf = RoleMapNsPdf("5 0 R",
            $"<< /Type /Namespace /NS ({Pdf17Namespace}) /RoleMapNS << /CustomType [/P 5 0 R] >> >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void ArrayRoleMapNsToDifferentNamespaceDictionary_DoesNotReportUa824()
    {
        byte[] pdf = RoleMapNsPdf("5 0 R 6 0 R",
            $"<< /Type /Namespace /NS ({Pdf17Namespace}) /RoleMapNS << /CustomType [/P 6 0 R] >> >>",
            $"<< /Type /Namespace /NS ({Pdf2Namespace}) >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void NoRoleMapNs_DoesNotReportUa824()
    {
        byte[] pdf = RoleMapNsPdf("5 0 R",
            $"<< /Type /Namespace /NS ({Pdf17Namespace}) >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    // =====================================================================
    //  Clause 8.2.4 — every element must belong to a standard namespace,
    //  possibly through role mapping (the clause's opening sentence)
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=StructTreeRoot, 3=Pages, 4=Page,
    //  5=Document (NS=6, standard, K=7), 6=Pdf2 namespace dict,
    //  7=the element under test (role "CustomType"), 8+=its own /NS chain.

    private const string MathMlNamespaceConst = "http://www.w3.org/1998/Math/MathML";

    [Fact]
    public void BareNameRoleMapReachesStandardNamespace_DoesNotReportUa824()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R /NS 8 0 R >>",
            $"<< /Type /Namespace /NS ({CustomNamespace}) /RoleMapNS << /CustomType /P >> >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void NoRoleMapNsAtAllOnCustomNamespace_ReportsUa824()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R /NS 8 0 R >>",
            $"<< /Type /Namespace /NS ({CustomNamespace}) >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void RoleMapNsPresentButNoEntryForThisRole_ReportsUa824()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R /NS 8 0 R >>",
            $"<< /Type /Namespace /NS ({CustomNamespace}) /RoleMapNS << /OtherType /P >> >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void ArrayRoleMapReachesStandardNamespaceDirectly_DoesNotReportUa824()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R /NS 8 0 R >>",
            $"<< /Type /Namespace /NS ({CustomNamespace}) /RoleMapNS << /CustomType [/Y 9 0 R] >> >>",
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void ArrayRoleMapChainNeverReachesStandardNamespace_ReportsUa824()
    {
        // Object 9's own namespace is custom too, and it has no /RoleMapNS of its own — the
        // chain dead-ends without ever reaching PDF 1.7, PDF 2.0 or MathML.
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R /NS 8 0 R >>",
            $"<< /Type /Namespace /NS ({CustomNamespace}) /RoleMapNS << /CustomType [/Y 9 0 R] >> >>",
            "<< /Type /Namespace /NS (http://example.com/custom-ns-dead-end) >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void MultiHopRoleMapChainReachesStandardNamespace_DoesNotReportUa824()
    {
        // Proves the recursion actually chases beyond one hop: custom NS A's /RoleMapNS points
        // to custom NS B, whose own /RoleMapNS is the one that finally reaches PDF 2.0.
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R /NS 8 0 R >>",
            "<< /Type /Namespace /NS (http://example.com/custom-ns-a) " +
                "/RoleMapNS << /CustomType [/Y 9 0 R] >> >>",
            "<< /Type /Namespace /NS (http://example.com/custom-ns-b) " +
                "/RoleMapNS << /Y [/Z 10 0 R] >> >>",
            NamespaceDict(Pdf2Namespace));

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void ExplicitMathMlNamespaceOnElement_DoesNotReportUa824()
    {
        // §8.2.4's own text: elements in the namespaces it names (PDF 1.7, PDF 2.0, MathML) need
        // no /RoleMapNS entry — standard outright, with no role-map chase needed.
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R /NS 8 0 R >>",
            $"<< /Type /Namespace /NS ({MathMlNamespaceConst}) >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void AbsentNsOnElement_DoesNotReportUa824()
    {
        // ISO 32000-2 §14.8.6.1: an absent /NS defaults to the PDF 1.7 namespace — compliant by
        // definition, and this element is never even walked into ResolveNamespaceMembership.
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void SelfReferencingRoleMapNsCycle_DoesNotHangUa824()
    {
        // The namespace's own /RoleMapNS maps its role back to itself, through the array form —
        // an unbounded loop without the hop cap. PdfRoleMap.MaxHops (16) must terminate the chase
        // and answer CannotDetermine, never a violation and never a hang.
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R /NS 8 0 R >>",
            $"<< /Type /Namespace /NS ({CustomNamespace}) " +
                "/RoleMapNS << /CustomType [/CustomType 8 0 R] >> >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    [Fact]
    public void MalformedArrayRoleMapNsTarget_DoesNotReportUa824()
    {
        // The array's second element (the target namespace reference) does not resolve to any
        // object at all — unresolvable, so the fail-safe answer is CannotDetermine, never a
        // violation.
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7),
            NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /CustomType /P 5 0 R /NS 8 0 R >>",
            $"<< /Type /Namespace /NS ({CustomNamespace}) " +
                "/RoleMapNS << /CustomType [/Y 99 0 R] >> >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.4");
    }

    // =====================================================================
    //  Clause 8.2.6.4 — ARIA/DPUB-ARIA /role must use the ARIA-1.1 owner
    // =====================================================================
    //
    //  Object layout: 1=catalog, 2=StructTreeRoot, 3=Pages, 4=Page, 5=Document, 6=namespace,
    //  7=the element carrying /A.

    [Fact]
    public void CorrectAriaOwner_DoesNotReportUa8264()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Part /P 5 0 R " +
                "/A << /O /ARIA-1.1 /role /doc-glossary >> >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.6.4");
    }

    [Fact]
    public void WrongAriaOwner_ReportsUa8264()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Part /P 5 0 R " +
                "/A << /O /Layout /role /doc-glossary >> >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.6.4");
    }

    [Fact]
    public void NoOwnerAtAllWithRole_ReportsUa8264()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Part /P 5 0 R /A << /role /doc-glossary >> >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.6.4");
    }

    [Fact]
    public void ArrayFormAttributeWithRevisionNumber_ReportsUa8264()
    {
        // The trailing "3" is a revision number (Table 355), not an attribute object — proving
        // the array walk skips PdfNumber elements rather than choking on them.
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Part /P 5 0 R " +
                "/A [ << /O /Layout /role /doc-glossary >> 3 ] >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.6.4");
    }

    [Fact]
    public void AttributeObjectWithNoRoleKey_DoesNotReportUa8264()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Part /P 5 0 R " +
                "/A << /O /Layout /Placement /Block >> >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.6.4");
    }

    [Fact]
    public void NoAttributeEntryAtAll_DoesNotReportUa8264()
    {
        byte[] pdf = Pdf("5 0 R", omitStructTreeRoot: false,
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Part /P 5 0 R >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.6.4");
    }

    [Fact]
    public void WrongAriaOwnerThroughClassMap_ReportsUa8264()
    {
        // §14.7.6.2's class form: the element names a class whose /ClassMap attribute object
        // carries the /role. A read that consults only /A never sees it.
        byte[] pdf = PdfWithClassMap("<< /Gloss << /O /Layout /role /doc-glossary >> >>", "5 0 R",
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Part /P 5 0 R /C /Gloss >>");

        var result = Validate(pdf);
        Assert.Contains(result.Violations, v => v.Code == "UA2-8.2.6.4");
    }

    [Fact]
    public void CorrectAriaOwnerThroughClassMap_DoesNotReportUa8264()
    {
        byte[] pdf = PdfWithClassMap("<< /Gloss << /O /ARIA-1.1 /role /doc-glossary >> >>", "5 0 R",
            DocumentElementWithChild(6, 7), NamespaceDict(Pdf2Namespace),
            "<< /Type /StructElem /S /Part /P 5 0 R /C /Gloss >>");

        var result = Validate(pdf);
        Assert.DoesNotContain(result.Violations, v => v.Code == "UA2-8.2.6.4");
    }

    private static PdfUa2StructureResult Validate(byte[] pdf)
        => PdfUa2StructureChecker.Validate(new MemoryStream(pdf));
}
