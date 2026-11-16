// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Asserts <b>where</b> a <c>/Separation</c> ink lands, which is the half of
/// <see cref="InkAccumulator"/> that <c>InkAccumulatorWiringTests</c> structurally cannot reach.
///
/// <para><b>Why a second file rather than more cases in the first.</b> Every fixture in the wiring
/// tests is a <i>full-page rectangle</i>, and its assertions are of the form "some pixel is
/// non-zero". That shape is blind to an entire class of defect by construction: a full-page
/// rectangle is symmetric under a vertical flip, identical to its own bounding box, and unclipped,
/// so a deposit that mirrors the page, that fills the bounding box instead of the artwork, or that
/// ignores the clip passes all nine of them. Each test below is therefore built around geometry
/// that can tell those apart — a rectangle in one half, a triangle, a clip, a zero-height rule.</para>
///
/// <para>The page is 100 × 100 pt rendered at 72 dpi, so one PDF point is one pixel and the only
/// conversion is the Y flip: <c>deviceRow = 100 - pdfY</c>. Every expected coordinate below is
/// written in device pixels, the space the accumulator is indexed in.</para>
/// </summary>
public class InkCoverageTests
{
    private const string Spot = "Spot1";
    private const int    Side = 100;

    // ------------------------------------------------------------------
    //  Where the ink is
    // ------------------------------------------------------------------

    /// <summary>
    /// Artwork confined to the top half of the page must deposit its ink in the top half of the
    /// map.
    /// </summary>
    /// <remarks>
    /// The deposit used to convert the path's bounds from PDF user space, and the current path is
    /// already in Y-down page-point space — so it applied the page flip twice and every map came
    /// back mirrored about the page's horizontal centre line. Both assertions discriminate: the old
    /// code put ink in the bottom half and none in the top.
    /// </remarks>
    [Fact]
    public void InkLandsInTheHalfOfThePageTheArtworkOccupies()
    {
        // PDF y 50..100 is the TOP half, i.e. device rows 0..50.
        byte[] map = Coverage("/Cs1 cs 1 scn 0 50 100 50 re f");

        Assert.True(At(map, 50, 25) > 0, "the top half of the page carries the artwork's ink");
        Assert.Equal(0, At(map, 50, 75));
    }

    /// <summary>
    /// A triangle inks its interior and not the rest of its bounding box.
    /// </summary>
    /// <remarks>
    /// The bounding-box deposit is exactly right for an axis-aligned rectangle and wrong for
    /// everything else, so the fixture has to be something whose bounds it does not fill. In device
    /// space this triangle's vertices are (10,90), (90,90) and (10,10), so its interior is the half
    /// where <c>y &gt;= x</c>: (20,80) is inside and (80,20) is inside the bounds and outside the
    /// shape.
    /// </remarks>
    [Fact]
    public void ATriangleDoesNotInkTheCornersOfItsBoundingBox()
    {
        byte[] map = Coverage("/Cs1 cs 1 scn 10 10 m 90 10 l 10 90 l h f");

        Assert.True(At(map, 20, 80) > 0, "a point inside the triangle carries ink");
        Assert.Equal(0, At(map, 80, 20));
    }

    /// <summary>A rectangular clip keeps ink out of the area it excludes.</summary>
    /// <remarks>
    /// The clip is PDF (0,0)–(40,40), i.e. device columns 0..40 of rows 60..100. The fill covers
    /// the whole page, so every pixel outside that quarter is ink the file did not lay down. This
    /// asserts a corner sharing rows with inked pixels and a corner sharing columns, so a deposit
    /// that honoured only one axis would still fail.
    /// </remarks>
    [Fact]
    public void ARectangularClipKeepsInkOutOfTheAreaItExcludes()
    {
        byte[] map = Coverage("q 0 0 40 40 re W n /Cs1 cs 1 scn 0 0 100 100 re f Q");

        Assert.True(At(map, 20, 80) > 0, "inside the clip carries ink");
        Assert.Equal(0, At(map, 80, 80));   // same rows, outside the clip's columns
        Assert.Equal(0, At(map, 20, 20));   // same columns, outside the clip's rows
    }

    /// <summary>A clip is honoured as a shape, not as its bounding box.</summary>
    /// <remarks>
    /// <para><b>The rectangular case above cannot see this, and a mutation proved it.</b> Deleting
    /// the tracked clip path from the rasteriser left that test green, because the deposit is
    /// bounded by <c>DeviceClipBounds</c> anyway — and for a rectangular clip the bounds
    /// <i>are</i> the shape. Only a clip whose bounds differ from itself reaches the tracking.</para>
    /// <para>The clip's device vertices are (0,100), (80,100) and (0,20), so its interior is where
    /// <c>y &gt;= x + 20</c>. Device (70,40) is inside the clip's bounding box and outside the clip;
    /// it is the pixel that separates the shape from its box.</para>
    /// </remarks>
    [Fact]
    public void ANonRectangularClipIsHonouredAsAShapeNotAsItsBoundingBox()
    {
        byte[] map = Coverage(
            "q 0 0 m 80 0 l 0 80 l h W n /Cs1 cs 1 scn 0 0 100 100 re f Q");

        Assert.True(At(map, 10, 80) > 0, "inside the triangular clip carries ink");
        Assert.Equal(0, At(map, 70, 40));   // inside the clip's bounds, outside the clip
    }

    /// <summary>A horizontal rule inks the width of its pen, not a single row.</summary>
    /// <remarks>
    /// <para>The old deposit used the path's bounding box and knew nothing of the pen, so a
    /// horizontal rule — whose bounds are one row tall — was recorded one pixel wide however wide
    /// the line was set. It was not dropped: <c>SKRect.IsEmpty</c> is <b>false</b> for a
    /// zero-height rectangle in SkiaSharp, which was checked rather than assumed. Under-reporting
    /// by a factor of the line width is the harder failure to notice, because the rule does appear
    /// on the map.</para>
    /// <para>The pen is 4 pt on a line at device row 50, so rows 48–52 carry ink and row 40 does
    /// not. Row 49 is the discriminating one: it is under the pen and outside the path's bounds.</para>
    /// </remarks>
    [Fact]
    public void AHorizontalRuleInksThePenWidthNotASingleRow()
    {
        byte[] map = Coverage("/Cs1 CS 1 SCN 4 w 10 50 m 90 50 l S");

        Assert.True(At(map, 50, 50) > 0, "the rule's own row is inked");
        Assert.True(At(map, 50, 49) > 0, "a row under the pen but outside the path's bounds is inked");
        Assert.Equal(0, At(map, 50, 40));
    }

    /// <summary>A stroke inks the pen's footprint, not just the path it follows.</summary>
    /// <remarks>
    /// The line is one point wide as a path and ten points wide as a stroke, so the discriminating
    /// pixels are the ones the pen reaches and the path does not. Column 53 is under the pen;
    /// column 70 is well outside it.
    /// </remarks>
    [Fact]
    public void AStrokeInksThePenFootprintAroundThePath()
    {
        byte[] map = Coverage("/Cs1 CS 1 SCN 10 w 50 0 m 50 100 l S");

        Assert.True(At(map, 50, 50) > 0, "the path's own column is inked");
        Assert.True(At(map, 53, 50) > 0, "a column under the pen but off the path is inked");
        Assert.Equal(0, At(map, 70, 50));
    }

    // ------------------------------------------------------------------
    //  Which colour the ink belongs to
    // ------------------------------------------------------------------

    /// <summary>
    /// A device colour set after a spot colour ends the spot colour, so the next fill deposits no
    /// spot ink.
    /// </summary>
    /// <remarks>
    /// The tints used to be interpreter fields rather than graphics-state fields, and nothing
    /// cleared them: once a page had selected any Separation colour, <b>every later fill on that
    /// page</b> deposited that ink. The map named inks the artwork never used, which is the failure
    /// mode a prepress caller is least able to detect — a plate that should not exist looks exactly
    /// like one that should.
    /// </remarks>
    [Fact]
    public void ADeviceColourAfterASpotColourDepositsNoSpotInk()
    {
        byte[] map = Coverage(
            "/Cs1 cs 1 scn 0 50 100 50 re f " +   // top half, in the spot colour
            "0 0 1 rg 0 0 100 50 re f");          // bottom half, in DeviceRGB

        Assert.True(At(map, 50, 25) > 0, "the spot-coloured half is inked");
        Assert.Equal(0, At(map, 50, 75));
    }

    /// <summary>A spot colour selected inside <c>q … Q</c> does not survive the <c>Q</c>.</summary>
    /// <remarks>
    /// The same defect as the test above reached from the other side, and it needs its own fixture
    /// because the second fill here sets <b>no colour operator at all</b> — so a fix that only
    /// cleared the tints in the colour operators would still leak here. §8.4.2 makes the current
    /// colour part of the graphics state, so the colorants it was expressed in have to be restored
    /// with it.
    /// </remarks>
    [Fact]
    public void ASpotTintDoesNotSurviveTheStateRestoreThatEndsIt()
    {
        byte[] map = Coverage(
            "q /Cs1 cs 1 scn 0 50 100 50 re f Q " +   // spot fill, scoped
            "0 0 100 50 re f");                       // bottom half, whatever Q restored

        Assert.True(At(map, 50, 25) > 0, "the fill inside the q…Q is inked");
        Assert.Equal(0, At(map, 50, 75));
    }

    // ------------------------------------------------------------------
    //  How much ink
    // ------------------------------------------------------------------

    /// <summary>A half tint over a fully covered pixel records half the ink.</summary>
    /// <remarks>
    /// The wiring suite asserts only that some pixel is "&gt; 0 and &lt; 255", which a great many
    /// wrong answers satisfy. The value is the measurement, so it is asserted as one — through
    /// <c>PdfColor.Channel</c>'s rounding, the same quantisation every other 8-bit conversion in the
    /// renderer uses.
    /// </remarks>
    [Fact]
    public void AHalfTintRecordsHalfTheInk()
    {
        byte[] map = Coverage("/Cs1 cs 0.5 scn 0 0 100 100 re f");

        // 0.5 x full coverage, rounded to 8 bits.
        Assert.InRange(At(map, 50, 50), 127, 128);
    }

    /// <summary>Two overlapping paints of one ink record the greater tint, not their sum.</summary>
    /// <remarks>
    /// The accumulator's model is what the plate carries: re-inking an already-covered area does
    /// not lay down twice the ink, so deposits combine by maximum. Asserting it here rather than on
    /// the accumulator alone is what ties the rule to the render path — the unit test proves
    /// <c>Deposit</c> takes the maximum, and this proves a page reaches it that way.
    /// </remarks>
    [Fact]
    public void OverlappingPaintsOfOneInkRecordTheGreaterTint()
    {
        byte[] map = Coverage(
            "/Cs1 cs 1 scn 0 0 100 100 re f " +     // full tint everywhere
            "0.25 scn 0 0 100 100 re f");           // a lighter tint over all of it

        Assert.Equal(255, At(map, 50, 50));
    }

    // ------------------------------------------------------------------
    //  Text
    // ------------------------------------------------------------------

    /// <summary>Spot-coloured text deposits ink.</summary>
    /// <remarks>
    /// <para>Text used to deposit nothing while still <i>registering</i> its colorant, so a page
    /// whose only spot content was type reported the channel as present and every pixel of it zero
    /// — which reads as "this ink is selected and never used" rather than as "not measured".</para>
    /// <para><b>Two assertions, because either alone is weak.</b> That some pixel is inked kills
    /// the old behaviour; that the inked pixels are a small minority of the page kills the obvious
    /// wrong fix of depositing the text's bounding box, which for a 36 pt line is a large band.</para>
    /// <para>Host-gated: this needs a font that resolves to real outlines, and a host with no font
    /// manager at all cannot observe the property. The Windows CI job runs the full solution and
    /// has Arial, so the killer runs there.</para>
    /// </remarks>
    [SystemFontFact("Arial")]
    public void SpotColouredTextDepositsInkOverItsGlyphsOnly()
    {
        byte[] map = Coverage("/Cs1 cs 1 scn BT /F1 36 Tf 8 40 Td (IIII) Tj ET", withFont: true);

        int inked = 0;
        foreach (byte b in map) if (b > 0) inked++;

        Assert.True(inked > 0, "spot-coloured type deposits ink");
        Assert.True(inked < Side * Side / 5,
            $"glyph outlines ink a small part of the page, not a band; {inked} of {Side * Side}");
    }

    /// <summary>
    /// Glyphs drawn over one another ink as their union: the captured outlines combine by the
    /// non-zero winding rule (§9.3.6 NOTE 2). Two I's at one position (Tc cancels the 278-unit
    /// advance) ink what one does; even-odd would cancel the overlap to nothing.
    /// </summary>
    [SystemFontFact("Arial")]
    public void CoincidentGlyphsInkAsTheirUnion()
    {
        int Inked(byte[] map) { int n = 0; foreach (byte b in map) if (b > 0) n++; return n; }

        int single = Inked(Coverage("/Cs1 cs 1 scn BT /F1 36 Tf 8 40 Td (I) Tj ET", withFont: true));
        int pair = Inked(Coverage("/Cs1 cs 1 scn BT /F1 36 Tf -10.008 Tc 8 40 Td (II) Tj ET", withFont: true));

        Assert.True(single > 0, "one glyph inks something");
        Assert.True(pair >= single * 0.8, $"two coincident glyphs inked {pair} pixels against one's {single}");
    }

    /// <summary>
    /// Clips nest: the second <c>W n</c> intersects the first (§8.5.4), so ink lands only where both
    /// allow. Triangles rather than rectangles, because the deposit is already cut to the canvas
    /// clip's bounding box — two rectangles intersect to a rectangle and cannot tell. Here the
    /// clips are x + y &lt; 100 and x &gt; y: (50, 20) is in both, (90, 40) only in the second.
    /// </summary>
    [Fact]
    public void NestedClipsInkOnlyTheirIntersection()
    {
        byte[] map = Coverage(
            "0 0 m 100 0 l 0 100 l h W n 0 0 m 100 0 l 100 100 l h W n /Cs1 cs 1 scn 0 0 100 100 re f");

        Assert.True(At(map, 50, 80) > 0, "inside both clips is inked");
        Assert.Equal(0, At(map, 90, 60));
    }

    /// <summary>
    /// A pixel the path half covers gets half the tint: the coverage is the anti-aliased raster, not
    /// a hit test. A square starting at x 10.5 covers column 10 by half and column 20 fully.
    /// </summary>
    [Fact]
    public void APartlyCoveredPixelTakesItsShareOfTheTint()
    {
        byte[] map = Coverage("/Cs1 cs 1 scn 10.5 10 20 20 re f");

        Assert.InRange(At(map, 10, 80), 100, 160);
        Assert.Equal(255, At(map, 20, 80));
    }

    /// <summary>
    /// At 144 dpi the map is twice the size and the square lands at twice the coordinates: x 10–30
    /// is columns 20–60, and column 50 is inked — at one pixel per point it would not be.
    /// </summary>
    [Fact]
    public void TheCoverageMapFollowsTheResolution()
    {
        InkAccumulator acc = Extract("/Cs1 cs 1 scn 10 10 20 20 re f", dpi: 144f);
        byte[] map = acc.Channels[Spot];

        Assert.Equal(200, acc.Width);
        Assert.True(map[160 * acc.Width + 50] > 0, "column 50 is inside the doubled square");
        Assert.Equal(0, map[160 * acc.Width + 70]);
    }

    // ------------------------------------------------------------------
    //  Acceptance control
    // ------------------------------------------------------------------

    /// <summary>
    /// A page whose spot colour is selected and never painted keeps the channel and inks nothing.
    /// </summary>
    /// <remarks>
    /// The control for every refusal above: these tests assert a great many zeroes, and a deposit
    /// that had simply stopped working would satisfy all of them. This one fixes the other end —
    /// the channel must still be registered, because "selected, never used" is a real answer about
    /// the document and not the same as "not measured".
    /// </remarks>
    [Fact]
    public void ASpotColourSelectedAndNeverPaintedRegistersTheChannelAndInksNothing()
    {
        InkAccumulator acc = Extract("/Cs1 cs 1 scn");

        Assert.Contains(Spot, acc.Channels.Keys);
        foreach (byte b in acc.Channels[Spot]) Assert.Equal(0, b);
    }

    // ------------------------------------------------------------------
    //  What is deliberately not measured says so
    // ------------------------------------------------------------------

    /// <summary>
    /// A <c>/Separation</c> image reports that its region is unmeasured rather than leaving the map
    /// to read as no ink.
    /// </summary>
    /// <remarks>
    /// An image's tints vary per sample and the samples are converted to sRGB at decode time, so
    /// measuring one needs the device layer (GAPS §11a). What matters for a caller estimating ink
    /// is the difference between "no spot ink is laid down here" and "this was not measured", and
    /// nothing in a coverage map of zeroes distinguishes them.
    /// </remarks>
    [Fact]
    public void ASeparationImageReportsThatItsInkIsNotMeasured()
    {
        var seen = Diagnostics(() => Extract("q 100 0 0 100 0 0 cm /Im1 Do Q", withImage: true));

        Assert.Contains(seen, e => e.Kind == RenderDiagnosticKind.InkCoverageIncomplete);
    }

    /// <summary>
    /// The report is once per kind of unmeasured paint, not once per paint: two runs of spot-stroked
    /// text (mode 1 — the stroke's ink is not measured) on one page say it once. (Two draws of one
    /// image would not show it: the decode, where an image reports, is cached.)
    /// </summary>
    [Fact]
    public void AnUnmeasuredPaintIsReportedOncePerPage()
    {
        var seen = Diagnostics(() => Extract(
            "/Cs1 CS 1 SCN BT /F1 20 Tf 1 Tr 10 10 Td (I) Tj 0 40 Td (I) Tj ET", withFont: true));

        Assert.Single(seen, e => e.Kind == RenderDiagnosticKind.InkCoverageIncomplete);
    }

    /// <summary>
    /// A page whose spot content is entirely measurable reports nothing on that channel.
    /// </summary>
    /// <remarks>
    /// The acceptance control the repository's "presence is not deviation" rule asks for. A channel
    /// that also fired on the content it <i>does</i> measure would be one a prepress caller learns
    /// to filter out, which costs the report above all of its value.
    /// </remarks>
    [Fact]
    public void AMeasurableSpotFillReportsNothingOnTheIncompleteChannel()
    {
        var seen = Diagnostics(() => Extract("/Cs1 cs 1 scn 0 0 100 100 re f"));

        Assert.DoesNotContain(seen, e => e.Kind == RenderDiagnosticKind.InkCoverageIncomplete);
    }

    // ------------------------------------------------------------------
    //  Fixture
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs <paramref name="act"/> and collects the render diagnostics <b>this thread</b> raised.
    /// </summary>
    /// <remarks>
    /// Filtered to the calling thread rather than serialised: <c>RenderDiagnostics.OnEvent</c> is a
    /// process-wide multicast, so an unfiltered collector asserts something about whatever else the
    /// run happens to be doing. That has produced three separate intermittents in this repository.
    /// </remarks>
    private static List<(RenderDiagnosticKind Kind, string Detail)> Diagnostics(Action act)
    {
        var seen = new List<(RenderDiagnosticKind, string)>();
        int ownThread = Environment.CurrentManagedThreadId;

        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId == ownThread)
                seen.Add((kind, detail ?? string.Empty));
        }

        RenderDiagnostics.OnEvent += Sink;
        try { act(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        return seen;
    }

    private static byte At(byte[] map, int x, int y) => map[y * Side + x];

    private static byte[] Coverage(string content, bool withFont = false)
    {
        InkAccumulator acc = Extract(content, withFont);
        Assert.Contains(Spot, acc.Channels.Keys);
        return acc.Channels[Spot];
    }

    private static InkAccumulator Extract(string content, bool withFont = false,
                                          bool withImage = false, float dpi = 72f)
    {
        using var ms = new MemoryStream(BuildPdf(content, withFont, withImage));
        // 72 dpi over a 100 pt page: one point is one pixel, so the only transform between the
        // content stream's coordinates and the map's is the Y flip.
        return PdfOperations.ExtractInkCoverage(ms, 0, dpi: dpi);
    }

    /// <summary>
    /// A 100 × 100 pt page carrying <paramref name="content"/>, with <c>/Cs1</c> bound to a
    /// <c>/Separation</c> over an identity tint transform into DeviceGray.
    /// </summary>
    /// <remarks>
    /// The image, when asked for, is a 2 × 2 8-bit raster in that same Separation space. Its four
    /// samples are all below 0x80 so the whole file stays ASCII, which is what lets this fixture be
    /// a string rather than a byte-splicing exercise.
    /// </remarks>
    private static byte[] BuildPdf(string content, bool withFont, bool withImage = false)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R /Resources "
              + "<< /ColorSpace << /Cs1 [/Separation /" + Spot + " /DeviceGray 5 0 R] >> "
              + (withFont  ? "/Font << /F1 6 0 R >> " : string.Empty)
              + (withImage ? "/XObject << /Im1 6 0 R >> " : string.Empty)
              + ">> >>",
            "STREAM",
            "<< /FunctionType 2 /Domain [0 1] /Range [0 1] /N 1 /C0 [0] /C1 [1] >>",
        };
        if (withFont)
            objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        if (withImage)
            objects.Add("IMAGE");

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n");
            if (objects[i] == "STREAM")
            {
                int len = Encoding.ASCII.GetByteCount(content);
                sb.Append("<< /Length ").Append(len.ToString(CultureInfo.InvariantCulture))
                  .Append(" >>\nstream\n").Append(content).Append("\nendstream\n");
            }
            else if (objects[i] == "IMAGE")
            {
                const string samples = "0123";   // 2 x 2, one component, four ASCII bytes
                sb.Append("<< /Type /XObject /Subtype /Image /Width 2 /Height 2 ")
                  .Append("/ColorSpace [/Separation /").Append(Spot).Append(" /DeviceGray 5 0 R] ")
                  .Append("/BitsPerComponent 8 /Length 4 >>\nstream\n")
                  .Append(samples).Append("\nendstream\n");
            }
            else sb.Append(objects[i]).Append('\n');
            sb.Append("endobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
