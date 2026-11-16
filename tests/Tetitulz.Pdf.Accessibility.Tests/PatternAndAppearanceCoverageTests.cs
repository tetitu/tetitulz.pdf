// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Where the deep checker's traversal reaches, established by measurement rather than by reading
/// the code.
///
/// <para>
/// <b>Why this file exists.</b> The deep checker descends into form XObjects. It was assumed —
/// including in this repository's own notes — that stopping there was harmless for PDF/UA, on the
/// reasoning that a tiling pattern is decorative and an annotation appearance is represented in
/// the structure tree by <c>/OBJR</c> rather than by MCIDs. Half of that turned out to be right.
/// Running the two fixtures below found that a document whose <b>only</b> font and text live in a
/// tiling pattern produced <b>no issues at all</b>, while the identical text drawn on the page
/// produced three.
/// </para>
///
/// <para>
/// The unambiguous half is the font: UA21 grants no exemption based on where a font is drawn
/// from, and <c>Fonts.PdfFontEmbeddingRemediator</c> — the fixer for that very violation — already
/// walked tiling patterns. So the fixer would embed a font the checker never reported. That is the
/// checker/fixer drift <c>AcroFormFieldWalk</c> exists to prevent elsewhere, and it is what these
/// tests now hold closed.
/// </para>
/// </summary>
public class PatternAndAppearanceCoverageTests
{
    /// <summary>
    /// The finding. A font reachable only through a tiling pattern is still a font the document
    /// renders with; before the traversal was widened this document reported nothing whatsoever.
    /// </summary>
    [Fact]
    public void FontUsedOnlyInsideATilingPattern_IsReportedAsNotEmbedded()
    {
        var issues = Check(TextOnlyInsideTilingPattern());

        Assert.Contains(issues, i => i.Code == "UA21");
    }

    /// <summary>
    /// A font inside a Type 3 glyph procedure's own resources, for the same reason. Type 3
    /// <c>/CharProcs</c> are content streams painted with the font's <c>/Resources</c>.
    /// </summary>
    [Fact]
    public void FontUsedOnlyInsideAType3FontResources_IsReportedAsNotEmbedded()
    {
        var issues = Check(FontOnlyInsideType3Resources());

        Assert.Contains(issues, i => i.Code == "UA21" && i.Message.Contains("Helvetica"));
    }

    /// <summary>
    /// The control that gives the two above their meaning: a document whose fonts are all embedded
    /// reports no UA21 wherever they live. Without it, "widen the walk" could degenerate into
    /// "report every font".
    /// </summary>
    [Fact]
    public void EmbeddedFontInsideATilingPattern_IsNotReported()
    {
        var issues = Check(TextOnlyInsideTilingPattern(embedFont: true));

        Assert.DoesNotContain(issues, i => i.Code == "UA21");
    }

    /// <summary>
    /// Text inside an annotation's appearance stream is <b>not</b> a coverage gap, and this
    /// records why rather than leaving the absence to look like one. PDF/UA represents an
    /// annotation in the structure tree through an <c>/OBJR</c>, not through MCIDs inside its
    /// <c>/AP</c> — so the rule that catches it is UA23, which fires here.
    /// </summary>
    [Fact]
    public void TextInsideAnAnnotationAppearance_IsCoveredByTheAnnotationRule()
    {
        var issues = Check(TextOnlyInsideAnnotationAppearance());

        Assert.Contains(issues, i => i.Code == "UA23");
    }

    /// <summary>
    /// The baseline the two pattern fixtures are measured against: the same text drawn directly on
    /// an untagged page is caught three separate ways.
    /// </summary>
    [Fact]
    public void SameTextDrawnOnThePage_IsReportedByTheContentChecks()
    {
        var issues = Check(UntaggedTextOnPage());

        Assert.Contains(issues, i => i.Code == "UA17");   // text outside marked content
        Assert.Contains(issues, i => i.Code == "UA21");   // font not embedded
    }

    // ------------------------------------------------------------------

    private static IReadOnlyList<AccessibilityIssue> Check(byte[] pdf)
        => PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf));

    private static string StreamObject(string content, string extra = "")
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " " + extra
           + " >>\nstream\n" + content + "endstream";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>(bodies.Count);
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        int size = bodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private const string Catalog =
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 6 0 R /MarkInfo << /Marked true >> "
        + "/Lang (en-US) /ViewerPreferences << /DisplayDocTitle true >> >>";

    /// <summary>
    /// A font descriptor carrying a <c>/FontFile2</c>, so the font counts as embedded. The stream
    /// content is irrelevant to UA21, which is a dictionary-level fact.
    /// </summary>
    private const string EmbeddedFontObjects =
        "<< /Type /Font /Subtype /TrueType /BaseFont /Embedded /FontDescriptor 10 0 R >>";

    /// <summary>
    /// The page's own content is correctly tagged and draws no text; everything is in the pattern.
    /// </summary>
    private static byte[] TextOnlyInsideTilingPattern(bool embedFont = false) => BuildPdf(new List<string>
    {
        Catalog,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /StructParents 0 "
        + "/Tabs /S /Resources << /Pattern << /P1 9 0 R >> >> >>",
        StreamObject("/P <</MCID 0>> BDC\n/Pattern cs /P1 scn 0 0 200 200 re f\nEMC\n"),
        embedFont
            ? EmbeddedFontObjects
            : "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K [0] >>",
        "<< /Nums [0 [7 0 R]] >>",
        StreamObject("BT /F1 12 Tf 2 2 Td (Text inside the pattern) Tj ET\n",
            "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 200 200] "
            + "/XStep 200 /YStep 200 /Resources << /Font << /F1 5 0 R >> >>"),
        "<< /Type /FontDescriptor /FontName /Embedded /Flags 32 /FontFile2 11 0 R >>",
        StreamObject("stub"),
    });

    /// <summary>A Type 3 font whose own /Resources name a further, non-embedded font.</summary>
    private static byte[] FontOnlyInsideType3Resources() => BuildPdf(new List<string>
    {
        Catalog,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /StructParents 0 "
        + "/Tabs /S /Resources << /Font << /T3 9 0 R >> >> >>",
        StreamObject("/P <</MCID 0>> BDC\nBT /T3 12 Tf 2 2 Td (A) Tj ET\nEMC\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K [0] >>",
        "<< /Nums [0 [7 0 R]] >>",
        "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 10 10] /FontMatrix [0.001 0 0 0.001 0 0] "
        + "/CharProcs << /A 10 0 R >> /Encoding << /Differences [65 /A] >> /FirstChar 65 "
        + "/LastChar 65 /Widths [10] /Resources << /Font << /F1 5 0 R >> >> >>",
        // The glyph procedure draws with the font: ISO 14289-1 7.21.4.1 asks about a font that is
        // used, and one a resource dictionary merely names is not.
        StreamObject("10 0 0 0 0 0 d1\nBT /F1 1 Tf (x) Tj ET\n"),
    });

    /// <summary>The page is tagged and draws no text; the text is in an annotation's /AP.</summary>
    private static byte[] TextOnlyInsideAnnotationAppearance() => BuildPdf(new List<string>
    {
        Catalog,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /StructParents 0 "
        + "/Tabs /S /Annots [9 0 R] /Resources << >> >>",
        StreamObject("/P <</MCID 0>> BDC\n0 0 0 rg 0 0 10 10 re f\nEMC\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K [0] >>",
        "<< /Nums [0 [7 0 R]] >>",
        "<< /Type /Annot /Subtype /Stamp /Rect [20 20 180 60] /F 4 /AP << /N 10 0 R >> >>",
        StreamObject("BT /F1 12 Tf 2 2 Td (Text inside the appearance) Tj ET\n",
            "/Type /XObject /Subtype /Form /BBox [0 0 160 40] "
            + "/Resources << /Font << /F1 5 0 R >> >>"),
    });

    /// <summary>Control: the same text drawn straight on the page, untagged.</summary>
    private static byte[] UntaggedTextOnPage() => BuildPdf(new List<string>
    {
        Catalog,
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /StructParents 0 "
        + "/Tabs /S /Resources << /Font << /F1 5 0 R >> >> >>",
        StreamObject("BT /F1 12 Tf 2 2 Td (Untagged text) Tj ET\n"),
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 8 0 R >>",
        "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K [0] >>",
        "<< /Nums [0 [7 0 R]] >>",
    });
}
