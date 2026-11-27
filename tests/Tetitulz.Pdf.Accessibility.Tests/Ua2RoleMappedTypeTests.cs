// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// ISO 14289-2 §8.2.4: a custom type "role mapped, either directly or transitively, to standard structure
/// types" conforms "to the requirements for the standard structure types to which they are role mapped,
/// including the containment rules" of ISO/TS 32005 — through the classic <c>/RoleMap</c> in the default
/// namespace and a namespace's <c>/RoleMapNS</c> otherwise. Each rule below is asked of a mapped type, with
/// the same type unmapped as its control.
/// </summary>
public sealed class Ua2RoleMappedTypeTests
{
    // -------------------------------------------------------------- Figure (§8.2.5.28.2)

    /// <summary>A figure by any route needs /Alt or /ActualText; an unmapped custom type is no figure.</summary>
    [Theory]
    [InlineData("/S /Pic", " /RoleMap << /Pic /Figure >>", true)]                 // classic /RoleMap
    [InlineData("/S /Pic", " /RoleMap << /Pic /Art /Art /Figure >>", false)]       // stops at the first standard type
    [InlineData("/S /Pic /NS 7 0 R", "", true)]                                    // /RoleMapNS to the PDF 2.0 Figure
    [InlineData("/S /Figure", " /RoleMap << /Figure /Div >>", true)]               // a standard type is not renamed
    [InlineData("/S /Pic", "", false)]                                             // unmapped: not a figure
    [InlineData("/S /Pic /Alt (A chart)", " /RoleMap << /Pic /Figure >>", false)]  // mapped, and described
    public void AMappedFigure_NeedsItsDescription(string element, string roleMap, bool reported)
    {
        byte[] pdf = Pdf("[8 0 R]", roleMap, "", $"<< /Type /StructElem {element} /P 5 0 R >>");
        Assert.Equal(reported, Coded(pdf, "UA2-8.2.5.28.2").Any());
    }

    // -------------------------------------------------------------- containment (§6.2, ISO/TS 32005)

    /// <summary>
    /// A type mapped to Span may not hold a Table, as Span may not (nor stand directly under Document);
    /// mapped to P it may do both.
    /// </summary>
    [Theory]
    [InlineData(" /RoleMap << /Wrapper /Span >>", true)]
    [InlineData(" /RoleMap << /Wrapper /P >>", false)]
    [InlineData("", false)]
    public void AMappedParent_IsHeldToItsTypesContainmentRules(string roleMap, bool reported)
    {
        byte[] pdf = Pdf("[8 0 R]", roleMap, "",
            "<< /Type /StructElem /S /Wrapper /P 5 0 R /K [9 0 R] >>",
            "<< /Type /StructElem /S /Table /NS 6 0 R /P 8 0 R >>");
        var found = Coded(pdf, "UA2-6.2");
        Assert.Equal(reported, found.Any(v => v.Message.Contains("A Table structure element is a child of a Wrapper (role mapped to Span)")));
        Assert.Equal(reported, found.Any(v => v.Message.Contains("A Wrapper (role mapped to Span) structure element is a child of a Document")));
    }

    // -------------------------------------------------------------- Document (§8.2.5.2)

    /// <summary>A root element mapped to the PDF 2.0 Document is the Document; unmapped it is not.</summary>
    [Theory]
    [InlineData("/Root", false)]
    [InlineData("/Unmapped", true)]
    public void ARootMappedToThePdf2Document_IsTheDocument(string type, bool reported)
    {
        byte[] pdf = PdfWithRootKid("8 0 R", "[]", "", "", $"<< /Type /StructElem /S {type} /NS 7 0 R /P 2 0 R >>");
        Assert.Equal(reported, Coded(pdf, "UA2-8.2.5.2").Any(v => v.Message.Contains("must be a Document")));
    }

    // -------------------------------------------------------------- Ruby (§8.2.5.23)

    /// <summary>A Ruby's base and ruby text counted by the types they are mapped to.</summary>
    [Theory]
    [InlineData(" /RoleMap << /Base /RB /Gloss /RT >>", false)]
    [InlineData("", true)]
    public void ARubysMappedChildren_CountAsRbAndRt(string roleMap, bool reported)
    {
        byte[] pdf = Pdf("[8 0 R]", roleMap, "",
            "<< /Type /StructElem /S /Ruby /NS 6 0 R /P 5 0 R /K [9 0 R 10 0 R] >>",
            "<< /Type /StructElem /S /Base /P 8 0 R >>",
            "<< /Type /StructElem /S /Gloss /P 8 0 R >>");
        Assert.Equal(reported, Coded(pdf, "UA2-8.2.5.23").Any());
    }

    // -------------------------------------------------------------- Link (§8.2.5.20)

    /// <summary>A link annotation inside a custom element mapped to Link is enclosed in a Link.</summary>
    [Theory]
    [InlineData("/S /Hyper /NS 7 0 R", false)]
    [InlineData("/S /Hyper", true)]
    public void ALinkInAnElementMappedToLink_IsEnclosed(string element, bool reported)
    {
        byte[] pdf = Pdf("[8 0 R]", "", " /Annots [9 0 R]",
            $"<< /Type /StructElem {element} /P 5 0 R /Pg 4 0 R /K << /Type /OBJR /Obj 9 0 R /Pg 4 0 R >> >>",
            "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /Contents (Example) /StructParent 0 /A << /S /URI /URI (https://example.com/) >> >>");
        Assert.Equal(reported, Coded(pdf, "UA2-8.2.5.20").Any(v => v.Message.Contains("is not enclosed within a Link")));
    }

    // -------------------------------------------------------------- an element with no namespace (§8.2.4)

    /// <summary>
    /// An element with no explicit namespace is, after its /RoleMap chain, one of the unique PDF 1.7 types (ISO/TS
    /// 32005 Tables 2 and 3): Quote, or a type mapped to it, passes; P — which the PDF 2.0 namespace defines too —
    /// and a type mapped to P or to nothing are reported; Note passes here, §8.2.5.14.1 reporting it; an element
    /// in a namespace is not this sentence's.
    /// </summary>
    [Theory]
    [InlineData("/S /Quote", "", null)]
    [InlineData("/S /BibEntry", "", null)]
    [InlineData("/S /MyQuote", " /RoleMap << /MyQuote /Quote >>", null)]
    [InlineData("/S /Note", "", null)]
    [InlineData("/S /P /NS 6 0 R", "", null)]
    [InlineData("/S /P", "", "P is defined in the PDF 2.0 namespace too")]
    [InlineData("/S /MyPara", " /RoleMap << /MyPara /P >>", "MyPara (role mapped to P) structure element has no explicit namespace, and P is defined")]
    [InlineData("/S /Aside", "", "its type is no standard PDF 1.7 type")]
    public void AnElementWithNoNamespace_IsAUniquePdf17Type(string element, string roleMap, string? expected)
    {
        byte[] pdf = Pdf("[8 0 R]", roleMap, "", $"<< /Type /StructElem {element} /P 5 0 R >>");
        var found = Coded(pdf, "UA2-8.2.4").Where(v => v.Message.Contains("has no explicit namespace")).ToList();
        if (expected == null) { Assert.Empty(found); return; }
        Assert.Contains(expected, Assert.Single(found).Message);
    }

    /// <summary>The structure tree root's own child is §8.2.5.2's, not reported by this sentence a second time.</summary>
    [Fact]
    public void TheRootsChildWithNoNamespace_IsLeftToTheDocumentRule()
    {
        byte[] pdf = PdfWithRootKid("8 0 R", "[]", "", "", "<< /Type /StructElem /S /Document /P 2 0 R >>");
        Assert.DoesNotContain(Coded(pdf, "UA2-8.2.4"), v => v.Message.Contains("has no explicit namespace"));
        Assert.Contains(Coded(pdf, "UA2-8.2.5.2"), v => v.Message.Contains("PDF 2.0"));
    }

    // -------------------------------------------------------------- fixture

    /// <summary>
    /// 1 catalog, 2 structure tree root (<paramref name="roleMap"/> appended), 3 pages, 4 the page, 5 the PDF 2.0
    /// Document with <paramref name="documentKids"/>, 6 the PDF 2.0 namespace, 7 a custom namespace whose
    /// <c>/RoleMapNS</c> maps <c>Pic</c> and <c>Root</c> to the PDF 2.0 Figure and Document and <c>Hyper</c> to
    /// the default namespace's Link; <paramref name="from8"/> from object 8.
    /// </summary>
    private static byte[] Pdf(string documentKids, string roleMap, string pageExtra, params string[] from8)
        => PdfWithRootKid("5 0 R", documentKids, roleMap, pageExtra, from8);

    private static byte[] PdfWithRootKid(string rootKid, string documentKids, string roleMap, string pageExtra, params string[] from8)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 3 0 R /StructTreeRoot 2 0 R /MarkInfo << /Marked true >> >>",
            $"<< /Type /StructTreeRoot /K {rootKid} /Namespaces [6 0 R 7 0 R]{roleMap} >>",
            "<< /Type /Pages /Kids [4 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200]{pageExtra} >>",
            $"<< /Type /StructElem /S /Document /NS 6 0 R /P 2 0 R /K {documentKids} >>",
            "<< /Type /Namespace /NS (http://iso.org/pdf2/ssn) >>",
            "<< /Type /Namespace /NS (http://example.com/custom) /RoleMapNS << /Pic [/Figure 6 0 R] /Root [/Document 6 0 R] /Hyper /Link >> >>",
        };
        bodies.AddRange(from8);
        return PdfUa2StructureCheckerTests.BuildPdf(bodies);
    }

    private static List<PdfUa2StructureViolation> Coded(byte[] pdf, string code)
        => PdfUa2StructureChecker.Validate(new MemoryStream(pdf)).Violations.Where(v => v.Code == code).ToList();
}
