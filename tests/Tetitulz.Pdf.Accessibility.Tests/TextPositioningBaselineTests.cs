// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Artifacts;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The §9.4.2 text-matrix algebra — <c>TL</c>, <c>T*</c> and <c>TD</c> — decides where each line's
/// baseline lands, and therefore what the reading-order analyzer concludes.
/// </summary>
/// <remarks>
/// <b>None of it was asserted.</b> Two content-stream walks in this assembly carried the algebra
/// identically, and flipping the sign of <c>T*</c>'s translate — or of the leading <c>TD</c>
/// derives from its <c>ty</c> — left all 926 Accessibility tests green. Every existing fixture
/// positions its lines with an explicit <c>Td</c> per line, which uses the operands directly and so
/// cannot see either sign.
///
/// <para><b>The reading-order analyzer structurally cannot detect a sign error, and finding that
/// out is what these tests are shaped by.</b> A flipped <c>T*</c> moves a line UP by the leading
/// instead of down — and the line it moves from was itself reached by moving DOWN that same
/// leading, so the flipped line lands exactly back on its predecessor. Every fixture produces a
/// tie, and the analyzer declines on overlapping bands by design ("leave it alone"), so it reports
/// no issue and <c>IsOrdered</c> stays true. Three fixtures were built before that became clear.
/// The analyzer cases below are kept because they pin the ORDERING rule, which is worth having;
/// they are not what kills a sign mutant.</para>
///
/// <para><b>The band tests are.</b> A footer band does not care about order: the line is either
/// within 50pt of the bottom or it is not, and a flipped advance puts it somewhere else entirely.
/// Same shared algebra, reached through <c>PdfArtifactMarker</c> instead.</para>
/// </remarks>
public class TextPositioningBaselineTests
{
    /// <summary>
    /// Three paragraphs laid out top-to-bottom with <c>TL</c> + <c>T*</c>, in structure order.
    /// The analyzer must find nothing wrong.
    /// </summary>
    /// <remarks>
    /// With <c>T*</c>'s translate positive instead of negative the same three lines march up the
    /// page, so structure order 0,1,2 becomes bottom-to-top and the analyzer reports issues.
    /// </remarks>
    [Fact]
    public void LinesAdvancedByTStarReadTopToBottom()
    {
        using var ms = new MemoryStream(ThreeLinePdf(
            "BT /F1 12 Tf 20 160 Td 40 TL "
            + "/P <</MCID 0>> BDC (First) Tj EMC "
            + "T* /P <</MCID 1>> BDC (Second) Tj EMC "
            + "T* /P <</MCID 2>> BDC (Third) Tj EMC ET\n"));

        ReadingOrderAnalysis result = PdfReadingOrderAnalyzer.Analyze(ms);

        Assert.True(result.Analyzed, result.NotAnalyzedReason);
        Assert.Equal(3, result.ElementsPlaced);
        Assert.True(result.IsOrdered,
            "T* should advance DOWN the page, so the three paragraphs are already in reading order");
    }

    /// <summary>
    /// The same three paragraphs with the structure tree deliberately naming them out of order.
    /// </summary>
    /// <remarks>
    /// The control that gives the case above its meaning: without it, an analyzer that reported
    /// <c>IsOrdered</c> unconditionally — or one that placed nothing — would satisfy it.
    /// </remarks>
    [Fact]
    public void TheSameLinesInTheWrongStructureOrderAreReported()
    {
        using var ms = new MemoryStream(ThreeLinePdf(
            "BT /F1 12 Tf 20 160 Td 40 TL "
            + "/P <</MCID 2>> BDC (First) Tj EMC "
            + "T* /P <</MCID 1>> BDC (Second) Tj EMC "
            + "T* /P <</MCID 0>> BDC (Third) Tj EMC ET\n"));

        ReadingOrderAnalysis result = PdfReadingOrderAnalyzer.Analyze(ms);

        Assert.True(result.Analyzed, result.NotAnalyzedReason);
        Assert.Equal(3, result.ElementsPlaced);
        Assert.False(result.IsOrdered, "the structure tree names the paragraphs bottom-to-top");
    }

    /// <summary>
    /// <c>TD</c> sets the leading to the <b>negated</b> <c>ty</c> (§9.4.2 Table 108), so a
    /// following <c>T*</c> continues down the page by the same step.
    /// </summary>
    /// <remarks>
    /// <b>The <c>T*</c> is what makes this observable.</b> <c>TD</c>'s own movement uses its
    /// operands directly, so flipping the sign of the leading it derives changes nothing until
    /// something reads that leading back — which is why a fixture of <c>TD</c> alone cannot detect
    /// it, and why this one ends with <c>T*</c>.
    /// </remarks>
    [Fact]
    public void TDSetsTheLeadingSoAFollowingTStarContinuesDownward()
    {
        using var ms = new MemoryStream(ThreeLinePdf(
            "BT /F1 12 Tf 20 160 Td "
            + "/P <</MCID 0>> BDC (First) Tj EMC "
            + "0 -40 TD /P <</MCID 1>> BDC (Second) Tj EMC "
            + "T* /P <</MCID 2>> BDC (Third) Tj EMC ET\n"));

        ReadingOrderAnalysis result = PdfReadingOrderAnalyzer.Analyze(ms);

        Assert.True(result.Analyzed, result.NotAnalyzedReason);
        Assert.Equal(3, result.ElementsPlaced);
        Assert.True(result.IsOrdered,
            "TD's leading is -ty, so the following T* should step down 40 like the TD did");
    }

    /// <summary>
    /// A line advanced onto the footer band by <c>T*</c> is marked as a pagination artifact.
    /// </summary>
    /// <remarks>
    /// <b>This is the observable the reading-order analyzer cannot supply.</b> A flipped <c>T*</c>
    /// sends each line UP by the leading instead of down, which always lands the moved line back
    /// on the one before it — and the analyzer DECLINES on overlapping bands by design, so it
    /// reports no issue and <c>IsOrdered</c> stays true. A band test does not care about order: the
    /// line is either within 50pt of the bottom or it is not.
    /// </remarks>
    [Fact]
    public void ALineAdvancedIntoTheFooterBandByTStarIsMarkedAsAnArtifact()
    {
        Assert.Equal(1, FooterArtifacts(
            "BT /F1 12 Tf 20 140 Td 60 TL (Body one) Tj T* (Body two) Tj T* (Page 1) Tj ET\n"));
    }

    /// <summary>
    /// The same, reached through the leading <c>TD</c> derives from its <c>ty</c>.
    /// </summary>
    [Fact]
    public void ALineAdvancedIntoTheFooterBandByTDsLeadingIsMarkedAsAnArtifact()
    {
        Assert.Equal(1, FooterArtifacts(
            "BT /F1 12 Tf 20 140 Td (Body one) Tj 0 -60 TD (Body two) Tj T* (Page 1) Tj ET\n"));
    }

    /// <summary>
    /// The control: with no line in either band nothing is marked, so the two cases above are
    /// about WHERE the line landed and not about the marker running at all.
    /// </summary>
    [Fact]
    public void TextThatStaysInTheBodyIsNotMarked()
    {
        Assert.Equal(0, FooterArtifacts(
            "BT /F1 12 Tf 20 140 Td 20 TL (Body one) Tj T* (Body two) Tj T* (Body three) Tj ET\n"));
    }

    /// <summary>
    /// Artifacts marked on a 200pt-tall page with the default 50pt bands.
    /// </summary>
    /// <remarks>
    /// The first line sits at y=140, deliberately BELOW the header band: at y=160 it was inside it
    /// and every fixture marked two artifacts instead of one, which measures the header rule rather
    /// than the advance under test.
    /// </remarks>
    private static int FooterArtifacts(string content)
    {
        using var input = new MemoryStream(ThreeLinePdf(content));
        using var output = new MemoryStream();
        return PdfArtifactMarker.MarkHeadersAndFooters(input, output).ArtifactsMarked;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Three <c>/P</c> elements over three marked-content sequences on one page, with
    /// <paramref name="content"/> supplying the layout.
    /// </summary>
    private static byte[] ThreeLinePdf(string content) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R"
                + " /Resources << /Font << /F1 9 0 R >> >> /StructParents 0 >>",
            StreamObj(content),
            "<< /Type /StructTreeRoot /K [6 0 R 7 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K << /Type /MCR /MCID 0 >> >>",
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K << /Type /MCR /MCID 1 >> >>",
            "<< /Type /StructElem /S /P /P 5 0 R /Pg 3 0 R /K << /Type /MCR /MCID 2 >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
        + " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);

        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
