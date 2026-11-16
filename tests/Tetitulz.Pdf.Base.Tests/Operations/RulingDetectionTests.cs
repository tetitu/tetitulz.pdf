// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// What becomes a ruling, and what one ruling is — the decisions a sweep of
/// <see cref="PdfGraphicsExtractor"/> found standing.
/// </summary>
/// <remarks>
/// <para><b>Rulings are the whole input to the Lines table strategy.</b> A ruling missed means a
/// bordered table is reported as unbordered or not at all; a ruling invented means a page's
/// underlines and background fills become a grid. The existing fixtures cover the ordinary shapes —
/// a stroked line, a stroked rectangle, a thin filled bar — and stop at the edges: the angle
/// tolerance, the merge arithmetic, and the recursion cap all survived.</para>
///
/// <para><b>The merge step is a second filter, and it masks the first.</b> A segment shorter than
/// <c>MinRulingLength</c> is dropped when it is emitted <i>and</i> again after collinear merging, so
/// deleting the first test leaves every single-segment fixture green. The two are not equivalent —
/// see <see cref="TwoShortCollinearMarksDoNotMergeIntoARuling"/>, which is the input that separates
/// them.</para>
/// </remarks>
public class RulingDetectionTests
{
    private static PdfPageGraphics Graphics(string content)
    {
        using var ms = new MemoryStream(BuildPdf(content), writable: false);
        return PdfGraphicsExtractor.Extract(ms)[0];
    }

    /// <summary>
    /// <b>A diagonal is not a ruling.</b> The axis tolerance is half a point, so anything that
    /// visibly slopes is a line drawing rather than a rule.
    /// </summary>
    /// <remarks>
    /// Widening the tolerance turns every leader line, underline flourish and chart axis on a page
    /// into a table border — and the Lines strategy needs only two of each direction to start
    /// building a grid.
    /// </remarks>
    [Fact]
    public void ADiagonalSegmentIsNotARuling()
    {
        var g = Graphics("50 500 m 250 530 l S");   // 200 across, 30 down

        Assert.Empty(g.HorizontalRulings);
        Assert.Empty(g.VerticalRulings);
    }

    /// <summary>
    /// The control for the row above: the same segment drawn flat <i>is</i> a ruling, so the
    /// assertion there is about the slope and not about the fixture failing to reach the walk.
    /// </summary>
    [Fact]
    public void TheSameSegmentDrawnFlatIsARuling()
    {
        var g = Graphics("50 500 m 250 500 l S");

        Assert.Equal(500f, Assert.Single(g.HorizontalRulings).Y, 3);
    }

    /// <summary>
    /// <b>Two short collinear marks do not become one ruling.</b> This is the input that separates
    /// the length test applied when a segment is emitted from the one applied after merging.
    /// </summary>
    /// <remarks>
    /// <para>Each mark is 3 pt, under the 5 pt <c>MinRulingLength</c>, and they sit 1 pt apart — so
    /// the merge step would join them into a 7 pt run that passes its own length test. Dropping them
    /// at emission is what prevents that, and no single-segment fixture can tell the two filters
    /// apart.</para>
    ///
    /// <para><b>This also means a dashed rule is not detected</b>, which is a consequence of the
    /// rule rather than an oversight: a row of short marks is far more often stippling, a leader, or
    /// noise than a table border. Recorded here so the trade is visible to whoever next wants dashed
    /// borders.</para>
    /// </remarks>
    [Fact]
    public void TwoShortCollinearMarksDoNotMergeIntoARuling()
    {
        var g = Graphics("100 500 m 103 500 l S\n104 500 m 107 500 l S");

        Assert.Empty(g.HorizontalRulings);
    }

    /// <summary>
    /// <b>A thin rectangle painted with <c>B</c> contributes its fill band as well as its strokes</b>,
    /// and the three collinear results merge into one ruling.
    /// </summary>
    /// <remarks>
    /// <para>The rectangle is 2 pt tall, so its stroked edges land at y = 500 and y = 502 and its
    /// fill band at the midpoint 501. Collinear merging groups values within 1 pt of their
    /// neighbour, so 500 → 501 → 502 is one chain and one ruling. Drop the fill and the chain breaks:
    /// 500 and 502 are 2 pt apart and become <b>two</b> rulings — a table row with a doubled border
    /// and a one-point-high phantom cell band between them.</para>
    ///
    /// <para>The same fixture is why the merge compares each value against its <i>neighbour</i>
    /// rather than against the group's first member: against the first, 502 is 2 pt away and splits
    /// off again.</para>
    /// </remarks>
    [Fact]
    public void AThinRectanglePaintedWithFillAndStrokeYieldsOneRuling()
    {
        var g = Graphics("50 500 200 2 re B");

        Assert.Equal(501f, Assert.Single(g.HorizontalRulings).Y, 3);
    }

    /// <summary>
    /// <b>Two collinear rulings a point apart are one ruling.</b>
    /// </summary>
    /// <remarks>
    /// Producers split a rule at a cell boundary, so the same visual line arrives as several
    /// segments that abut or leave a hairline gap. Joining only what overlaps exactly would report
    /// each piece separately, and the Lines strategy would then find a border that spans no cell.
    /// </remarks>
    [Fact]
    public void CollinearRulingsSeparatedByOnePointAreJoined()
    {
        var g = Graphics("50 500 m 150 500 l S\n151 500 m 250 500 l S");

        HRuling r = Assert.Single(g.HorizontalRulings);
        Assert.Equal(50f, r.X1, 3);
        Assert.Equal(250f, r.X2, 3);
    }

    /// <summary>
    /// <b>A merged ruling takes the mean of its parts' positions and the widest of their line
    /// widths.</b>
    /// </summary>
    /// <remarks>
    /// The position is a mean because the parts are measurements of one line and the mean is the
    /// one value inside all of them. The width is the maximum because it decides how much of the
    /// page the rule actually covers, and taking the last part's width instead reports a hairline
    /// for a rule that was drawn heavy.
    /// </remarks>
    [Fact]
    public void AMergedRulingTakesTheMeanPositionAndTheWidestLine()
    {
        var g = Graphics("3 w 50 500 m 150 500 l S\n1 w 50 500.5 m 150 500.5 l S");

        HRuling r = Assert.Single(g.HorizontalRulings);
        Assert.Equal(500.25f, r.Y, 3);
        Assert.Equal(3f, r.LineWidth, 3);
    }

    /// <summary>
    /// <b>A chain of nested Form XObjects deeper than the cap is refused, and says so.</b>
    /// </summary>
    /// <remarks>
    /// <para>Neither of this walk's other guards bounds it: the visited set refuses a <i>repeat</i>,
    /// which a chain of distinct forms never is, and the per-page budget allows far more invocations
    /// than a stack has frames. The identical gap existed in <c>PdfStructuredTextExtractor</c> and
    /// was closed in the same sweep — depth caps go untested in both extractors because every
    /// fixture is about repetition rather than length.</para>
    ///
    /// <para>Unlike that one, this cap already reported its refusal; the assertion below is the
    /// guard's own evidence rather than a clock.</para>
    /// </remarks>
    [Fact]
    public void AFormChainDeeperThanTheCapIsRefusedAndReported()
    {
        // Ten nested forms; the cap is 8. The innermost draws the only ruling on the page, so its
        // absence is what proves the walk stopped — and the reported reason proves why.
        var g = GraphicsOf(FormChain(10));

        Assert.Empty(g.HorizontalRulings);
        Assert.Contains("depth limit", PdfGraphicsExtractor.LastError ?? string.Empty,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: the same chain within the cap draws its ruling, so the test above is pinning the
    /// cap rather than a walk that never descends.
    /// </summary>
    [Fact]
    public void AFormChainInsideTheCapReachesItsRuling()
    {
        var g = GraphicsOf(FormChain(3));

        Assert.Equal(300f, Assert.Single(g.HorizontalRulings).Y, 3);
    }

    /// <summary>
    /// The vertical half of <see cref="TwoShortCollinearMarksDoNotMergeIntoARuling"/>.
    /// </summary>
    /// <remarks>
    /// <b>Written because the horizontal test did not cover it.</b> The two branches are separate
    /// comparisons on separate axes, and a mutation of the vertical one survived the horizontal
    /// fixture — the same finding the PDF MAC work produced for its two placements. Symmetry in the
    /// code is not symmetry in the tests.
    /// </remarks>
    [Fact]
    public void TwoShortCollinearVerticalMarksDoNotMergeIntoARuling()
    {
        var g = Graphics("500 100 m 500 103 l S\n500 104 m 500 107 l S");

        Assert.Empty(g.VerticalRulings);
    }

    /// <summary>
    /// <b>An image drawn under a degenerate matrix contributes no box.</b>
    /// </summary>
    /// <remarks>
    /// A zero-scale CTM maps the image's unit square to a point, so its bounding box has no area.
    /// Reporting it would hand a caller a rectangle that cannot contain anything — and
    /// <c>PdfPageGraphics.Images</c> is consumed by layout analysis, which divides by those extents.
    /// </remarks>
    [Fact]
    public void AnImageUnderADegenerateMatrixIsNotReported()
    {
        var g = GraphicsOf(ImagePdf("q 0 0 0 0 100 100 cm /Im0 Do Q"));

        Assert.Empty(g.Images);
    }

    /// <summary>
    /// The control: the same image under an ordinary matrix is reported, so the assertion above is
    /// about the degenerate extent rather than about the image never being reached.
    /// </summary>
    [Fact]
    public void AnImageUnderAnOrdinaryMatrixIsReported()
    {
        var g = GraphicsOf(ImagePdf("q 200 0 0 100 50 600 cm /Im0 Do Q"));

        ImageBbox box = Assert.Single(g.Images);
        Assert.Equal(200f, box.Width, 3);
        Assert.Equal(100f, box.Height, 3);
    }

    // ------------------------------------------------------------------

    /// <summary>A one-page PDF with a 1×1 image XObject named /Im0.</summary>
    private static byte[] ImagePdf(string contentStream)
    {
        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /Im0 5 0 R >> >> >>",
            $"<< /Length {contentStream.Length} >>\nstream\n{contentStream}\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray " +
                "/BitsPerComponent 8 /Length 1 >>\nstream\n\u0000\nendstream",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new System.Collections.Generic.List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static PdfPageGraphics GraphicsOf(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        return PdfGraphicsExtractor.Extract(ms)[0];
    }

    /// <summary>Page draws /F0; form k draws /F(k+1); the innermost strokes one ruling.</summary>
    private static byte[] FormChain(int levels)
    {
        var bodies = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /XObject << /F0 5 0 R >> >> >>",
            "<< /Length 7 >>\nstream\n/F0 Do\nendstream",
        };

        for (int k = 0; k < levels; k++)
        {
            bool last = k == levels - 1;
            string content = last ? "2 w 100 300 m 400 300 l S" : $"/F{k + 1} Do";
            string res = last ? "" : $"/XObject << /F{k + 1} {6 + k} 0 R >>";
            bodies.Add($"<< /Type /XObject /Subtype /Form /BBox [0 0 612 792] /Resources << {res} >> " +
                       $"/Length {content.Length} >>\nstream\n{content}\nendstream");
        }

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildPdf(string contentStream)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[4];

        offsets[0] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        offsets[1] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        offsets[2] = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        offsets[3] = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(contentStream), contentStream);

        int xref = sb.Length;
        sb.Append("xref\n0 5\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
