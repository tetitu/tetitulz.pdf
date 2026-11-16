// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The pattern name is <b>per paint side</b> (§8.4.1, Table 52): <c>scn</c> selects the fill's and
/// <c>SCN</c> the stroke's.
///
/// <para><b>The raster renderer kept one slot for both.</b> <c>OpSc</c> wrote
/// <c>GraphicsState.CurrentPatternName</c> whatever its <c>stroke</c> flag said, so a content
/// stream that chose a fill pattern and then a stroke pattern painted <i>both</i> with the stroke's
/// — while the SVG writer, which has had <c>PatternNameFill</c> and <c>PatternNameStroke</c> all
/// along, painted them as the file asked. Every other part of the colour state had been kept apart
/// since the beginning; only the pattern name was shared, which is why nothing else looked
/// wrong.</para>
///
/// <para>The fixtures use two tiling patterns of <b>different colours</b>, so the assertions read
/// which pattern reached which side. A test that used one pattern could not tell the two
/// implementations apart at all.</para>
/// </summary>
public sealed class PatternStateParityTests
{
    /// <summary>
    /// <c>/P1 scn</c> then <c>/P2 SCN</c> then <c>B</c>: the interior must be P1's red and the
    /// border P2's blue. Before the fix the interior was blue too.
    /// </summary>
    [Fact]
    public void FillAndStrokePatterns_AreIndependent_InTheRasterPath()
    {
        using var bmp = PixelAssert.Render(TwoPatternPdf());

        SKColor interior = PixelAssert.At(bmp, 50, 50);
        Assert.True(interior.Red > 150 && interior.Blue < 100,
            $"the interior should carry the FILL pattern (red); got {interior}");
    }

    /// <summary>
    /// The stroke keeps its own. Asserted separately so a renderer that swapped the two slots
    /// cannot pass the pair.
    /// </summary>
    /// <summary>
    /// The stroke side reads its own slot: <c>SCN</c> selects P2 and the stroke paints P2's blue.
    /// </summary>
    /// <remarks>
    /// Stroke-only rather than <c>B</c>, so this asserts <i>which slot the stroke side reads</i>
    /// and nothing else. <c>B</c> is covered separately by
    /// <see cref="FillAndStrokePatterns_AreBothPainted_ByTheB_Operator"/>, which was the separate
    /// pre-existing defect this fixture surfaced — the fill pattern consumed the path and the
    /// stroke half drew nothing.
    /// </remarks>
    [Fact]
    public void StrokeKeepsItsOwnPattern_InTheRasterPath()
    {
        // ORDER IS THE POINT: the stroke pattern is chosen FIRST and the fill pattern second, so a
        // single shared slot ends up holding P1 and the stroke would paint red. Written the other
        // way round — SCN last — a shared slot happens to hold the right value and the test cannot
        // fail; the first version of this fixture did exactly that and the mutation survived it.
        using var bmp = PixelAssert.Render(
            PatternPdf("/Pattern CS /P2 SCN /Pattern cs /P1 scn 6 w 10 10 80 80 re S"));

        SKColor border = PixelAssert.At(bmp, 10, 50);
        Assert.True(border.Blue > 150 && border.Red < 100,
            $"the stroke should carry P2 (blue), not the fill's P1; got {border}");
    }

    /// <summary>
    /// The SVG export already behaved this way, and now agrees with the raster path rather than
    /// disagreeing with it. Both paints must reference two <i>different</i> pattern definitions.
    /// </summary>
    [Fact]
    public void FillAndStrokeReferenceDifferentPatterns_InTheSvgExport()
    {
        string svg = RenderSvg(TwoPatternPdf());
        // Not just "a painted path": each <pattern> definition contains the cell's own painted
        // path, so an unqualified query matches three and Single() throws. Only the one in the
        // page's own content is the subject.
        var painted = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "path" &&
                        (e.Attribute("fill")?.Value ?? "none") != "none" &&
                        !e.Ancestors().Any(a => a.Name.LocalName == "pattern"));

        string fill = painted.Attribute("fill")?.Value;
        string stroke = painted.Attribute("stroke")?.Value;

        Assert.StartsWith("url(#", fill, StringComparison.Ordinal);
        Assert.StartsWith("url(#", stroke, StringComparison.Ordinal);
        Assert.NotEqual(fill, stroke);
    }

    /// <summary>
    /// <c>B</c> paints <b>both</b> sides — §8.5.3.1: it fills and strokes the <i>same</i> path.
    /// </summary>
    /// <remarks>
    /// The raster path painted the fill pattern and then stroked nothing: every exit from
    /// <c>RenderTilingPattern</c> ends in <c>NewPath()</c>, so by the time the stroke half ran
    /// there was no geometry left. Measured white at x = 8 — where the stroke covers and the fill
    /// does not — while the SVG export emitted both paints on one element. The assertion point
    /// matters: anywhere inside the rectangle both sides overlap, so the fill's red alone would
    /// satisfy a test that sampled the interior.
    /// </remarks>
    [Fact]
    public void FillAndStrokePatterns_AreBothPainted_ByTheB_Operator()
    {
        using var bmp = PixelAssert.Render(TwoPatternPdf());

        // The 6-wide stroke is centred on the rectangle's edge at x = 10, so it covers 7..13;
        // the fill covers 10..90. x = 8 is stroke-only.
        SKColor strokeOnly = PixelAssert.At(bmp, 8, 50);
        Assert.True(strokeOnly.Blue > 150 && strokeOnly.Red < 100,
            $"`B` must paint the STROKE pattern (blue) too, not only the fill's; got {strokeOnly}");
    }

    /// <summary>
    /// The same path with an ordinary <b>solid</b> stroke — the branch a real file is far more
    /// likely to take, and the one that shows the defect was in the fill half, not the stroke half.
    /// </summary>
    [Fact]
    public void PatternFillWithSolidStroke_StillPaintsTheStroke()
    {
        using var bmp = PixelAssert.Render(
            PatternPdf("/Pattern cs /P1 scn 0 0 1 RG 6 w 10 10 80 80 re B"));

        SKColor strokeOnly = PixelAssert.At(bmp, 8, 50);
        Assert.True(strokeOnly.Blue > 150 && strokeOnly.Red < 100,
            $"a solid stroke must survive the fill pattern consuming the path; got {strokeOnly}");
    }

    /// <summary>
    /// <c>/Pattern CS</c> with no <c>SCN</c> must not consume the fill pattern a preceding
    /// <c>scn</c> selected — the two operators address different sides.
    /// </summary>
    [Fact]
    public void EnteringPatternSpaceForStroke_DoesNotClearTheFillPattern()
    {
        // /P1 scn selects the fill pattern; /Pattern CS then enters pattern space for the STROKE
        // side only, and the fill must still paint P1.
        using var bmp = PixelAssert.Render(
            PatternPdf("/Pattern cs /P1 scn /Pattern CS 10 10 80 80 re f"));

        SKColor interior = PixelAssert.At(bmp, 50, 50);
        Assert.True(interior.Red > 150,
            $"the fill pattern must survive `/Pattern CS`; got {interior}");
    }

    /// <summary>
    /// The control for §8.6.8 Table 74: entering pattern space and painting without ever selecting
    /// a pattern paints <b>nothing</b>. Without this, the test above would pass for a renderer that
    /// had simply stopped clearing the name at all.
    /// </summary>
    [Fact]
    public void PatternSpaceWithNoPatternSelected_PaintsNothing()
    {
        using var bmp = PixelAssert.Render(PatternPdf("/Pattern cs 10 10 80 80 re f"));

        Assert.True(PixelAssert.IsWhite(PixelAssert.At(bmp, 50, 50)),
            "a /Pattern cs fill with no scn must paint nothing (§8.6.8 Table 74)");
    }

    // ------------------------------------------------------------------

    /// <summary>Red fill pattern P1, blue stroke pattern P2, filled and stroked together.</summary>
    private static byte[] TwoPatternPdf() =>
        PatternPdf("/Pattern cs /P1 scn /Pattern CS /P2 SCN 6 w 10 10 80 80 re B");

    /// <summary>A 100×100 page with two solid tiling patterns available as /P1 (red) and /P2 (blue).</summary>
    private static byte[] PatternPdf(string content) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Pattern << /P1 5 0 R /P2 6 0 R >> >> >>",
            Stream("", content + "\n"),
            Tile("1 0 0 rg"),
            Tile("0 0 1 rg"));

    /// <summary>A tiling pattern whose whole 8×8 cell is one colour.</summary>
    private static string Tile(string colourOp)
    {
        string cell = colourOp + " 0 0 8 8 re f\n";
        return "<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1" +
               " /BBox [0 0 8 8] /XStep 8 /YStep 8 /Resources << >>" +
               $" /Length {cell.Length} >>\nstream\n{cell}endstream";
    }

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
