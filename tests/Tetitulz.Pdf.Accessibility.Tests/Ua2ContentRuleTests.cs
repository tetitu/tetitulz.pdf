// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Checking.Ua2;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The PDF/UA-2 structure checker against ISO 14289-2's own sentences: §8.2.2 and §8.3.1 (shown text is
/// tagged or an artifact), §8.4.5.5.1's glyph sentence, §8.2.5.29 (a formula's representation), §8.10.2.3
/// (a widget's actions described), §8.6 over element and annotation text strings, and the Note and H
/// prohibitions judged by the standard structure type an element is (§8.2.5.14.1 NOTE 1, §8.2.4).
/// </summary>
public class Ua2ContentRuleTests
{
    private static List<PdfUa2StructureViolation> Coded(byte[] pdf, string code)
        => PdfUa2StructureChecker.Validate(new MemoryStream(pdf)).Violations.Where(v => v.Code == code).ToList();

    private static string Stream(string text)
        => "<< /Length " + text.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + text + "\nendstream";

    // -------------------------------------------------------------- §8.2.2, §8.3.1

    /// <summary>
    /// The PUA fixture (a P element, object 9, claims MCID 0 on page 1) with ordinary text: tagged,
    /// claimed text and an artifact are quiet; text in no MCID-carrying sequence, or in one no element
    /// claims, is reported — invisible text too, since it is content.
    /// </summary>
    [Theory]
    [InlineData("/P <</MCID 0>> BDC BT /F1 12 Tf (B) Tj ET EMC", null)]
    [InlineData("/Artifact BMC BT /F1 12 Tf (B) Tj ET EMC", null)]
    [InlineData("/Artifact <</Type /Pagination>> BDC BT /F1 12 Tf (B) Tj ET EMC", null)]
    [InlineData("BT /F1 12 Tf () Tj ET", null)]                                           // an empty string draws nothing
    [InlineData("BT /F1 12 Tf (B) Tj ET", "text that no marked-content sequence with an /MCID encloses")]
    [InlineData("/Span BMC BT /F1 12 Tf (B) Tj ET EMC", "text that no marked-content sequence with an /MCID encloses")]
    [InlineData("BT 3 Tr /F1 12 Tf (B) Tj ET", "text that no marked-content sequence with an /MCID encloses")]
    [InlineData("/P <</MCID 5>> BDC BT /F1 12 Tf (B) Tj ET EMC", "text in marked content 5, which no structure element claims,")]
    public void Ua222_ShownTextIsTaggedOrAnArtifact(string content, string? expected)
    {
        var found = Coded(PdfUa2StructureCheckerTests.PuaPdf(content), "UA2-8.2.2");
        if (expected == null)
        {
            Assert.Empty(found);
            return;
        }
        string message = Assert.Single(found).Message;
        Assert.StartsWith("Page 1: " + expected, message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A form XObject's text takes its marking from the sequence that invokes it, which the walk does not
    /// see, so it is not judged; the same text on the page itself is.
    /// </summary>
    [Fact]
    public void Ua222_TextInAFormXObject_IsNotJudged()
    {
        byte[] form = PdfUa2StructureCheckerTests.PuaPdf("/Fm0 Do", pageXObjects: "/XObject << /Fm0 10 0 R >>",
            extra: "<< /Type /XObject /Subtype /Form /BBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Length 22 >>\nstream\nBT /F1 12 Tf (B) Tj ET\nendstream");
        Assert.Empty(Coded(form, "UA2-8.2.2"));
        Assert.Single(Coded(PdfUa2StructureCheckerTests.PuaPdf("BT /F1 12 Tf (B) Tj ET"), "UA2-8.2.2"));
    }

    /// <summary>One finding a page and MCID, however often the same untagged run shows text.</summary>
    [Fact]
    public void Ua222_RepeatedUntaggedText_IsReportedOnce()
        => Assert.Single(Coded(PdfUa2StructureCheckerTests.PuaPdf("BT /F1 12 Tf (B) Tj (B) Tj ET BT /F1 12 Tf (B) Tj ET"), "UA2-8.2.2"));

    // -------------------------------------------------------------- §8.4.5.5.1, glyphs

    /// <summary>A Type 1C font whose bare CFF defines glyph A only, code 65 selected through <paramref name="encoding"/>.</summary>
    private static byte[] Type1C(string encoding, string content = "BT /F1 12 Tf (A) Tj ET", int hexLayers = 1)
    {
        byte[] program = MinimalOpenTypeCff.BuildBareCff(MinimalOpenTypeCff.NamedBoxGlyphs("A"));
        string hex = Convert.ToHexString(program) + ">";
        for (int i = 1; i < hexLayers; i++) hex = Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(hex)) + ">";
        string filters = hexLayers == 1 ? "/ASCIIHexDecode" : "[" + string.Concat(Enumerable.Repeat("/ASCIIHexDecode ", hexLayers)) + "]";
        return PdfUa2StructureCheckerTests.BuildPdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 7 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /TestFont /FirstChar 65 /Widths [500] /Encoding " + encoding +
                " /FontDescriptor 5 0 R >>",
            "<< /Type /FontDescriptor /FontName /TestFont /Flags 4 /FontFile3 6 0 R >>",
            "<< /Subtype /Type1C /Filter " + filters + " /Length " + hex.Length.ToString(CultureInfo.InvariantCulture) +
                " >>\nstream\n" + hex + "\nendstream",
            Stream(content),
        });
    }

    /// <summary>
    /// The sentence PDF/UA-1's UA57 and PDF/A's A84 share: a shown code selecting a glyph the embedded
    /// program lacks is reported, one it defines is not.
    /// </summary>
    [Fact]
    public void Ua28455_1_AGlyphTheEmbeddedProgramDoesNotDefine_IsReported()
    {
        var missing = Coded(Type1C("<< /Differences [65 /B] >>"), "UA2-8.4.5.5.1");
        Assert.Contains("A font used for rendering shows a glyph its embedded program does not define",
            Assert.Single(missing).Message, StringComparison.Ordinal);
        Assert.Contains("TestFont", missing[0].Message, StringComparison.Ordinal);
        Assert.Empty(Coded(Type1C("<< /Differences [65 /A] >>"), "UA2-8.4.5.5.1"));
    }

    /// <summary>
    /// What could not be judged is said, never passed: a program that does not decode (seventeen filters,
    /// past PdfLimits.MaxFilterChainLength) leaves its font unjudged, and text shown before any font is
    /// selected leaves the painted glyphs unknown — each a UA2-99, and no verdict.
    /// </summary>
    [Fact]
    public void Ua28455_1_WhatCouldNotBeJudged_IsAUa299()
    {
        byte[] undecodable = Type1C("<< /Differences [65 /B] >>", hexLayers: 17);
        Assert.Empty(Coded(undecodable, "UA2-8.4.5.5.1"));
        Assert.Contains(Coded(undecodable, "UA2-99"), v => v.Message.StartsWith("§8.4.5.5.1's glyph sentence was not judged for a font: TestFont", StringComparison.Ordinal));

        byte[] noFont = Type1C("<< /Differences [65 /B] >>", content: "BT (A) Tj ET");
        Assert.Contains(Coded(noFont, "UA2-99"), v => v.Message.StartsWith("§8.4.5.5.1's glyph sentence was not judged: the glyphs used for rendering could not be established", StringComparison.Ordinal));
        Assert.DoesNotContain(Coded(Type1C("<< /Differences [65 /A] >>"), "UA2-99"), v => v.Message.StartsWith("§8.4.5.5.1", StringComparison.Ordinal));
    }

    // -------------------------------------------------------------- the tree fixture

    /// <summary>
    /// Catalog 1, pages 2, page 3, structure tree root 4 (with <paramref name="rootExtra"/>) holding
    /// element 5, written <paramref name="element"/>; <paramref name="extra"/> from object 6.
    /// </summary>
    private static byte[] Tree(string element, string rootExtra = "", string pageExtra = "", params string[] extra)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200]" + pageExtra + " >>",
            "<< /Type /StructTreeRoot /K [5 0 R]" + rootExtra + " >>",
            "<< /Type /StructElem /P 4 0 R " + element + " >>",
        };
        bodies.AddRange(extra);
        return PdfUa2StructureCheckerTests.BuildPdf(bodies);
    }

    private const string Pdf2Ns = "<< /Type /Namespace /NS (http://iso.org/pdf2/ssn) >>";
    private const string Pdf17Ns = "<< /Type /Namespace /NS (http://iso.org/pdf/ssn) >>";
    private const string MathMlNs = "<< /Type /Namespace /NS (http://www.w3.org/1998/Math/MathML) >>";

    // -------------------------------------------------------------- §8.2.5.14.1, §8.2.5.12

    /// <summary>
    /// The standard Note, by the type an element is: the default namespace's Note, a custom type the
    /// /RoleMap maps to it, the PDF 1.7 namespace's, and a custom namespace's mapped to the bare Note;
    /// not a custom namespace's Note mapped to FENote (NOTE 1).
    /// </summary>
    [Theory]
    [InlineData("/S /Note", "", null, true)]
    [InlineData("/S /Footnote", " /RoleMap << /Footnote /Note >>", null, true)]
    [InlineData("/S /Note /NS 6 0 R", "", Pdf17Ns, true)]
    [InlineData("/S /Note /NS 6 0 R", "", "<< /Type /Namespace /NS (http://example.com/notes) /RoleMapNS << /Note /Note >> >>", true)]
    [InlineData("/S /Note /NS 6 0 R", "", "<< /Type /Namespace /NS (http://example.com/notes) /RoleMapNS << /Note [/FENote 7 0 R] >> >>", false)]
    [InlineData("/S /Footnote", " /RoleMap << /Footnote /FENote >>", null, false)]
    [InlineData("/S /Note /NS 6 0 R", "", Pdf2Ns, false)]                                     // PDF 2.0 defines no Note
    [InlineData("/S /Footnote /NS 6 0 R", "", "<< /Type /Namespace /NS (http://example.com/notes) /RoleMapNS << /Footnote [/Note 8 0 R] >> >>", true)]
    public void Ua2825141_TheNoteStandardType_IsJudgedByItsNamespace(string element, string roleMap, string? ns, bool reported)
    {
        string[] extra = ns == null ? Array.Empty<string>() : new[] { ns, Pdf2Ns, Pdf17Ns };
        Assert.Equal(reported ? 1 : 0, Coded(Tree(element, roleMap, "", extra), "UA2-8.2.5.14.1").Count);
    }

    /// <summary>H is in both standard namespaces, so the PDF 2.0 namespace's is reported too.</summary>
    [Theory]
    [InlineData("/S /H", "", null, true)]
    [InlineData("/S /Heading", " /RoleMap << /Heading /H >>", null, true)]
    [InlineData("/S /H /NS 6 0 R", "", Pdf2Ns, true)]
    [InlineData("/S /H /NS 6 0 R", "", "<< /Type /Namespace /NS (http://example.com/doc) /RoleMapNS << /H [/H1 7 0 R] >> >>", false)]
    [InlineData("/S /H1", "", null, false)]
    public void Ua28212_TheHStandardType_IsJudgedByItsNamespace(string element, string roleMap, string? ns, bool reported)
    {
        string[] extra = ns == null ? Array.Empty<string>() : new[] { ns, Pdf2Ns };
        Assert.Equal(reported ? 1 : 0, Coded(Tree(element, roleMap, "", extra), "UA2-8.2.5.12").Count);
    }

    /// <summary>A /RoleMapNS chain that loops on itself is not the standard type, and ends.</summary>
    [Fact]
    public void Ua2825141_ARoleMapNsChainThatLoops_Ends_HostileInput()
    {
        byte[] pdf = Tree("/S /Note /NS 6 0 R", "", "",
            "<< /Type /Namespace /NS (http://example.com/a) /RoleMapNS << /Note [/Note 7 0 R] >> >>",
            "<< /Type /Namespace /NS (http://example.com/b) /RoleMapNS << /Note [/Note 6 0 R] >> >>");
        Assert.Empty(Coded(pdf, "UA2-8.2.5.14.1"));
        Assert.Single(Coded(Tree("/S /Note"), "UA2-8.2.5.14.1"));
    }

    // -------------------------------------------------------------- §8.2.5.29

    private static string MathMlFile(string relationship)
        => "<< /Type /Filespec /F (f.mml) /UF (f.mml) " + relationship + " /EF << /F 8 0 R >> >>";

    private const string MathMlStream = "<< /Type /EmbeddedFile /Subtype /application#2Fmathml+xml /Length 7 >>\nstream\n<math/>\nendstream";

    /// <summary>
    /// A Formula with MathML — a child in the MathML namespace, or a MathML associated file — or with
    /// /Alt or /ActualText is quiet; one with none of them is reported, role-mapped or not.
    /// </summary>
    [Theory]
    [InlineData("/S /Formula", "", false, true)]
    [InlineData("/S /Equation", " /RoleMap << /Equation /Formula >>", false, true)]
    [InlineData("/S /Formula /Alt (x squared)", "", false, false)]
    [InlineData("/S /Formula /ActualText (x2)", "", false, false)]
    [InlineData("/S /Formula /K [6 0 R]", "", true, false)]
    [InlineData("/S /Formula /AF [7 0 R]", "", false, false)]
    [InlineData("/S /Formula /AF [10 0 R]", "", false, false)]                                // a MIME type in another case is the same type
    public void Ua28529_AFormulaHasMathMlOrAnAlternative(string element, string roleMap, bool mathMlChild, bool reported)
    {
        byte[] pdf = Tree(element, roleMap, "",
            mathMlChild ? "<< /Type /StructElem /S /math /NS 9 0 R /P 5 0 R >>" : "<< >>",
            MathMlFile("/AFRelationship /Supplement"), MathMlStream, MathMlNs,
            "<< /Type /Filespec /F (g.mml) /UF (g.mml) /AFRelationship /Supplement /EF << /F 11 0 R >> >>",
            "<< /Type /EmbeddedFile /Subtype /Application#2FMathML+XML /Length 7 >>\nstream\n<math/>\nendstream");
        var found = Coded(pdf, "UA2-8.2.5.29");
        Assert.Equal(reported ? 1 : 0, found.Count);
        if (reported)
            Assert.StartsWith("A Formula structure element has no MathML", found[0].Message, StringComparison.Ordinal);
    }

    /// <summary>A MathML file a Formula's /AF names has /AFRelationship /Supplement; another value, or none, is reported.</summary>
    [Theory]
    [InlineData("/AFRelationship /Supplement", false)]
    [InlineData("/AFRelationship /Alternative", true)]
    [InlineData("", true)]
    public void Ua285291_AFormulasMathMlFile_IsASupplement(string relationship, bool reported)
    {
        byte[] pdf = Tree("/S /Formula /AF [7 0 R]", "", "", "<< >>", MathMlFile(relationship), MathMlStream);
        Assert.Equal(reported ? 1 : 0, Coded(pdf, "UA2-8.2.5.29.1").Count);
        // A MathML file of any relationship is MathML: the either-or rule is satisfied.
        Assert.Empty(Coded(pdf, "UA2-8.2.5.29"));
    }

    // -------------------------------------------------------------- §8.10.2.3

    private static byte[] Widget(string entries)
        => Tree("/S /Form", "", " /Annots [6 0 R]",
            "<< /Type /Annot /Subtype /Widget /FT /Btn /T (b) /Rect [10 10 50 30] " + entries + " >>");

    [Theory]
    [InlineData("/AA << /E << /S /JavaScript /JS (app.alert\\(1\\)) >> >>", true)]
    [InlineData("/AA << /K << /S /JavaScript /JS (x) >> >>", true)]                             // a merged field's /AA is the widget dictionary's
    [InlineData("/AA << /E << /S /JavaScript /JS (x) >> >> /F 2", true)]                     // Hidden does not make an artifact
    [InlineData("/AA << /E << /S /JavaScript /JS (x) >> >> /Contents (Shows a hint)", false)]
    [InlineData("/AA << /E << /S /JavaScript /JS (x) >> >> /F 1", false)]                    // Invisible: an artifact
    [InlineData("/AA << >>", false)]
    [InlineData("", false)]
    public void Ua281023_AWidgetsActions_AreDescribedInItsContents(string entries, bool reported)
        => Assert.Equal(reported ? 1 : 0, Coded(Widget(entries), "UA2-8.10.2.3").Count);

    /// <summary>§8.10.2.3 is a widget's: another annotation's /AA is not judged by it.</summary>
    [Fact]
    public void Ua281023_AnotherAnnotationsActions_AreNotJudged()
    {
        byte[] screen = Tree("/S /Annot", "", " /Annots [6 0 R]",
            "<< /Type /Annot /Subtype /Screen /Rect [10 10 50 30] /AA << /E << /S /JavaScript /JS (x) >> >> >>");
        Assert.Empty(Coded(screen, "UA2-8.10.2.3"));
        Assert.Single(Coded(Widget("/AA << /E << /S /JavaScript /JS (x) >> >>"), "UA2-8.10.2.3"));
    }

    // -------------------------------------------------------------- §8.6

    /// <summary>U+E000 as a UTF-16BE text string.</summary>
    private const string Pua = "<FEFFE000>";

    [Theory]
    [InlineData("/S /Figure /Alt " + Pua, "/Alt")]
    [InlineData("/S /Span /ActualText " + Pua, "/ActualText")]
    [InlineData("/S /Span /E " + Pua, "/E")]
    [InlineData("/S /Sect /T " + Pua, "/T")]
    [InlineData("/S /Figure /Alt (A chart)", null)]
    [InlineData("/S /Figure /ID " + Pua, null)]                                                // an identifier is not for a person
    public void Ua286_AnElementsHumanReadableTextString_UsesNoPua(string element, string? key)
    {
        var found = Coded(Tree(element), "UA2-8.6");
        if (key == null)
            Assert.Empty(found);
        else
            Assert.Contains("element's " + key + " uses a Unicode Private Use Area codepoint", Assert.Single(found).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/Contents " + Pua, true)]
    [InlineData("/Contents (A note)", false)]
    [InlineData("/NM " + Pua, false)]                                                          // an internal name
    public void Ua286_AnAnnotationsContents_UsesNoPua(string entries, bool reported)
    {
        byte[] pdf = Tree("/S /Annot", "", " /Annots [6 0 R]",
            "<< /Type /Annot /Subtype /Text /Rect [10 10 30 30] " + entries + " >>");
        var found = Coded(pdf, "UA2-8.6");
        Assert.Equal(reported ? 1 : 0, found.Count);
        if (reported) Assert.StartsWith("An annotation's /Contents uses", found[0].Message, StringComparison.Ordinal);
    }
}
