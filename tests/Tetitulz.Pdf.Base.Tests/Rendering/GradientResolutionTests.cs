// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The two rendering-quality constants — <see cref="PdfLimits.SvgGradientStopCount"/> (32, colour
/// resolution of an SVG gradient) and <see cref="PdfLimits.BezierFlattenSteps"/> (16, geometric
/// resolution of a raster patch grid) — are <b>different on purpose</b>, and until this file no
/// test asserted either one.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> CLAUDE.md records that collapsing this pair once silently
/// <em>halved</em> SVG gradient resolution and that no test caught it. Nothing anywhere asserted
/// the emitted count: the suite's gradient assertions are about stop <em>colours</em>
/// (<c>IndirectShadingDomainParityTests</c>, <c>SvgRasterParityTests</c>) or a lower bound
/// (<c>ShadingCieParameterParityTests</c> asserts <c>stops.Count &gt;= 3</c>), and the first and
/// last stop keep their colours whatever the sampling rate in between.</para>
///
/// <para><b>Measured before writing that, and it needed one correction.</b> Setting
/// <c>SvgGradientStopCount = 16</c> and running the rest of <c>Base.Tests</c> reddens exactly
/// <b>one</b> existing test — <c>ShadingCieParameterParityTests.</c>
/// <c>TheSvgExportAgreesWithTheRasterRendererOnACalGrayShading</c> — so the collapse is not quite
/// invisible today. It is worse than invisible: that test compares the stop <em>nearest offset
/// 0.5</em> with the raster midpoint to within ±6, and halving the count moves that stop from
/// 0.484 to 0.467, which on a gamma ramp shifts its colour 2 units past the tolerance. The
/// failure therefore reads <i>"the SVG export disagrees with the raster renderer on a CalGray
/// shading"</i> — pointing at CIE colour handling — and it is two units away from being silent. It
/// is a coincidence of one tolerance and not an assertion about resolution: collapsing to
/// <b>24</b> stops instead leaves that test green (verified), while the tests below fail at any
/// value but 32.</para>
///
/// <para><b>Three assertions, covering three different directions of the same mistake.</b></para>
/// <list type="number">
///   <item><description><b>The emitted stop count</b> (the tests below on axial and radial
///     exports) is the one that observes behaviour. It reads the number of <c>&lt;stop&gt;</c>
///     elements out of the exported document and fails at 16 — i.e. it fails on exactly the
///     collapse that shipped.</description></item>
///   <item><description><b>The measured flattening deviation</b>
///     (<see cref="CoonsPatchBoundary_FollowsTheTrueCubicWithinTheFlatteningTolerance"/>) observes
///     the raster constant, in the only place it reaches pixels: the Coons/tensor patch surface
///     grid. It catches a <em>coarsening</em> — a boundary sampled at 8 steps departs from the
///     true cubic by ~2.3 pt where 16 steps departs by ~0.6 pt.</description></item>
///   <item><description><b>The constants are not equal</b>
///     (<see cref="TheTwoResolutionConstantsAreDeliberatelyDifferentValues"/>). <b>On its own this
///     is very nearly a tautology and would not be worth writing</b> — it restates two literals
///     from one file. It earns its place only beside the two behavioural tests above, because it
///     covers the one direction neither of them can see: raising <c>BezierFlattenSteps</c> to 32
///     makes the raster grid <em>finer</em>, and a finer approximation cannot be distinguished
///     from a correct one by measuring output. So the pair can still be collapsed upward with
///     every pixel and every stop still right — and that is a collapse, since the two values are
///     answers to different questions (colour sampling versus geometry) that happen to be
///     comparable integers. Verified rather than assumed: with
///     <c>BezierFlattenSteps = 32</c> the four behavioural tests here stay green and only this
///     one fails.</description></item>
/// </list>
///
/// <para>The stop-count assertions deliberately name the literal <c>32</c> as well as the
/// constant. Comparing the emitted count only against <c>PdfLimits.SvgGradientStopCount</c> would
/// track any edit to the constant and could never fail for the reason this file exists; comparing
/// only against the literal would not notice the exporter hardcoding its own number beside a
/// constant nobody reads. Both together pin the value <em>and</em> the wiring.</para>
///
/// <para>SVG is parsed as an <see cref="XDocument"/>, never substring-matched: the suite once
/// substring-matched SVG for its whole history and missed an export that was not well-formed
/// XML.</para>
/// </remarks>
public class GradientResolutionTests
{
    /// <summary>The value <see cref="PdfLimits.SvgGradientStopCount"/> is documented to hold.</summary>
    private const int ExpectedSvgStops = 32;

    // ==================================================================
    //  1. The emitted stop count — the assertion whose absence let the
    //     collapse through.
    // ==================================================================

    /// <summary>
    /// An axial (type 2) shading exports as a <c>&lt;linearGradient&gt;</c> carrying exactly
    /// <see cref="PdfLimits.SvgGradientStopCount"/> stops.
    /// </summary>
    [Fact]
    public void AxialShadingExportsExactlyThirtyTwoGradientStops()
    {
        var gradient = SingleGradient(AxialShadingPdf(), "linearGradient");
        var stops = gradient.Elements().Where(e => e.Name.LocalName == "stop").ToList();

        Assert.Equal(ExpectedSvgStops, stops.Count);
        Assert.Equal(PdfLimits.SvgGradientStopCount, stops.Count);
    }

    /// <summary>
    /// The radial (type 3) export samples the same function through the same code, so it must
    /// carry the same number of sampled stops — a divergence here would mean two samplers. This
    /// fixture's <c>/Extend [false false]</c> adds a transparent stop at each end, which is not a
    /// sample and is not counted.
    /// </summary>
    [Fact]
    public void RadialShadingSamplesExactlyThirtyTwoGradientStops()
    {
        var gradient = SingleGradient(RadialShadingPdf(), "radialGradient");
        var stops = gradient.Elements()
            .Where(e => e.Name.LocalName == "stop" && e.Attribute("stop-opacity") == null).ToList();

        Assert.Equal(ExpectedSvgStops, stops.Count);
        Assert.Equal(PdfLimits.SvgGradientStopCount, stops.Count);
    }

    /// <summary>
    /// The stops span the whole parametric range: the first sits at offset 0 and the last at
    /// offset 1, so the count above is a sampling <em>rate</em> and not a truncated ramp.
    /// </summary>
    [Fact]
    public void GradientStopsSpanTheWholeRangeAtEvenSpacing()
    {
        var gradient = SingleGradient(AxialShadingPdf(), "linearGradient");
        var offsets = gradient.Elements()
            .Where(e => e.Name.LocalName == "stop")
            .Select(e => float.Parse(e.Attribute("offset")!.Value, CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(0f, offsets[0], 3);
        Assert.Equal(1f, offsets[offsets.Count - 1], 3);

        // Even spacing: 1/(N-1) between neighbours. At N = 16 the step would be 0.0667.
        float step = 1f / (ExpectedSvgStops - 1);
        for (int i = 1; i < offsets.Count; i++)
            Assert.Equal(step, offsets[i] - offsets[i - 1], 3);
    }

    // ==================================================================
    //  2. The raster constant, measured against the curve it approximates.
    // ==================================================================

    /// <summary>
    /// A Coons patch (type 6) whose lower boundary is a strongly curved cubic is rendered by
    /// flattening that boundary into <see cref="PdfLimits.BezierFlattenSteps"/> chords. The
    /// painted edge must therefore follow the true cubic to within the sagitta of one chord.
    /// </summary>
    /// <remarks>
    /// <para><b>The arithmetic that fixes the tolerance.</b> The fixture's boundary is the cubic
    /// through (30,40) → (30,240) → (270,240) → (270,40), for which
    /// <c>y''(t) = -1200</c> everywhere, so a chord spanning parameter width <c>h</c> lies below
    /// the curve by <c>h²·1200/8 = 150h²</c> points at its midpoint:</para>
    /// <list type="bullet">
    ///   <item><description><c>N = 32</c> → 0.15 pt</description></item>
    ///   <item><description><c>N = 16</c> (shipped) → <b>0.59 pt</b></description></item>
    ///   <item><description><c>N = 8</c> → 2.34 pt</description></item>
    ///   <item><description><c>N = 4</c> → 9.4 pt</description></item>
    /// </list>
    /// <para><b>Measured, not predicted.</b> Reading the boundary back out of a raster adds a
    /// roughly constant ~0.35 pt on top of that arithmetic — antialiased triangle edges plus the
    /// quantisation of "the lowest inked row in this column" — so the shipped 16 steps measures
    /// <b>0.95 pt</b> here, and 8 steps would measure ~2.7. The 1.5 pt tolerance sits between
    /// them with about a 1.6× margin each way. Rendering at 288 dpi rather than 72 is what keeps
    /// the quantisation part of that overhead at 0.125 pt.</para>
    /// <para>The measured band is x ∈ [122, 178] — the flat middle of the arch. Near its ends the
    /// boundary is close to vertical (x'(0) = x'(1) = 0), where "the lowest inked pixel in this
    /// column" is not a measurement of the boundary at all, and the slope multiplies the chord's
    /// horizontal error into the vertical one. A worst-case chord midpoint of both 16 and 8 steps
    /// falls inside the band, which is what makes the two separable.</para>
    /// <para><b>What it cannot see, by construction:</b> a <em>finer</em> grid. That direction is
    /// what the constants-differ test below is for.</para>
    /// </remarks>
    [Fact]
    public void CoonsPatchBoundary_FollowsTheTrueCubicWithinTheFlatteningTolerance()
    {
        const int dpi = 288;
        const float scale = dpi / 72f;

        using var bmp = PixelAssert.Render(CoonsArchPdf(), dpi);

        // Only the flat middle of the arch — see the remarks for why the ends cannot be
        // measured this way, and why this band still contains a worst-case chord of both the
        // shipped 16 steps and the 8 the tolerance has to reject.
        float worst = -1f;
        float worstX = 0f, worstTrue = 0f, worstInk = 0f;
        int columns = 0;

        for (int px = (int)(122 * scale); px <= (int)(178 * scale); px++)
        {
            float userX = (px + 0.5f) / scale;

            int lowestInkRow = -1;
            for (int row = bmp.Height - 1; row >= 0; row--)
            {
                if (!PixelAssert.IsInk(bmp.GetPixel(px, row))) continue;
                lowestInkRow = row;
                break;
            }
            if (lowestInkRow < 0) continue;   // no ink in this column at all

            columns++;
            float inkBottomUserY = (bmp.Height - lowestInkRow - 0.5f) / scale;
            float trueY = ArchY(userX);
            float deviation = trueY - inkBottomUserY;   // > 0: ink spilled below the true curve
            if (deviation > worst)
            {
                worst = deviation;
                worstX = userX; worstTrue = trueY; worstInk = inkBottomUserY;
            }
        }

        // The fixture must actually have painted the band being measured; a blank page would
        // otherwise report a deviation of -1 and "pass".
        Assert.True(columns > 200,
            $"the arch band was not painted: only {columns} inked columns of ~225 expected");

        Assert.True(worst < 1.5f,
            $"the flattened boundary departs from the true cubic by {worst:F2} pt at x = {worstX:F1} " +
            $"(curve y = {worstTrue:F2}, lowest ink y = {worstInk:F2}); " +
            $"16 chords give ~0.59 pt, 8 chords ~2.34 pt");
    }

    // ==================================================================
    //  3. The direction no output can show.
    // ==================================================================

    /// <summary>
    /// The two constants must not become one value. See this class's remarks for why an assertion
    /// this close to a tautology is kept: it is the only one of the three that fails when the pair
    /// is collapsed <em>upward</em>, which leaves every pixel and every stop correct.
    /// </summary>
    [Fact]
    public void TheTwoResolutionConstantsAreDeliberatelyDifferentValues()
    {
        // Colour resolution of a resolution-independent SVG gradient — banding stays visible at
        // any zoom, so it is sampled twice as finely as geometry that is about to be rasterised.
        Assert.Equal(ExpectedSvgStops, PdfLimits.SvgGradientStopCount);

        // Geometric resolution of a patch grid whose result is immediately sampled to pixels.
        Assert.Equal(16, PdfLimits.BezierFlattenSteps);

        Assert.NotEqual(PdfLimits.BezierFlattenSteps, PdfLimits.SvgGradientStopCount);
    }

    // ==================================================================
    //  Fixtures
    // ==================================================================

    /// <summary>Exports page 0 to SVG and returns its one gradient element of the given name.</summary>
    private static XElement SingleGradient(byte[] pdf, string localName)
    {
        using var ms = new System.IO.MemoryStream(pdf);
        string svg = Tetitulz.Pdf.Base.Rendering.PdfSvgRenderer.RenderPageToSvg(ms, 0);

        var doc = XDocument.Parse(svg);
        var gradients = doc.Descendants().Where(e => e.Name.LocalName == localName).ToList();
        Assert.Single(gradients);
        return gradients[0];
    }

    /// <summary>
    /// The fixture arch: the cubic through (30,40) → (30,240) → (270,240) → (270,40), evaluated
    /// at the parameter whose x is <paramref name="x"/>.
    /// </summary>
    private static float ArchY(float x)
    {
        // x(t) = 30 + 240·(3t² − 2t³) is monotone on [0,1]; invert it by bisection.
        double target = (x - 30.0) / 240.0;
        double lo = 0, hi = 1;
        for (int i = 0; i < 60; i++)
        {
            double mid = (lo + hi) / 2;
            double v = 3 * mid * mid - 2 * mid * mid * mid;
            if (v < target) lo = mid; else hi = mid;
        }
        double t = (lo + hi) / 2;
        // y(t) = 40[(1−t)³ + t³] + 720·t(1−t)
        double u = 1 - t;
        return (float)(40 * (u * u * u + t * t * t) + 720 * t * u);
    }

    /// <summary>
    /// A type-6 patch bounded below by that arch and above by a straight edge at y = 280, in one
    /// flat colour so the ink test measures geometry only.
    /// </summary>
    private static byte[] CoonsArchPdf()
    {
        // §8.7.4.5.7 boundary order as the interpreter reads it: corner, two controls, corner, …
        string boundary =
            Pt(30, 40) + Pt(30, 240) + Pt(270, 240) +   // the arch: A, its two controls…
            Pt(270, 40) +                               // …to B
            Pt(270, 120) + Pt(270, 200) + Pt(270, 280) + // B → C, straight
            Pt(190, 280) + Pt(110, 280) + Pt(30, 280) +  // C → D, straight
            Pt(30, 200) + Pt(30, 120);                   // D → A, straight
        const string corners = "FF0000" + "FF0000" + "FF0000" + "FF0000";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh1 5 0 R >> >> >>",
            StreamObj("", "/Sh1 sh"),
            StreamObj("/ShadingType 6 /ColorSpace /DeviceRGB /BitsPerCoordinate 16" +
                      " /BitsPerComponent 8 /BitsPerFlag 8 /Decode [0 300 0 300 0 1 0 1 0 1]" +
                      " /Filter /ASCIIHexDecode",
                      "00" + boundary + corners + ">"));
    }

    /// <summary>A 16-bit patch coordinate pair, hex, over the fixture's <c>/Decode [0 300]</c>.</summary>
    private static string Pt(float x, float y) => Coord(x) + Coord(y);

    private static string Coord(float user) =>
        ((int)Math.Round(user / 300.0 * 65535.0)).ToString("X4", CultureInfo.InvariantCulture);

    /// <summary>A black→white axial ramp painted through <c>sh</c>.</summary>
    private static byte[] AxialShadingPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh0 5 0 R >> >> >>",
            StreamObj("", "q 10 40 80 20 re W n /Sh0 sh Q"),
            "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [10 0 90 0]" +
            " /Extend [true true] /Function 6 0 R >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >>");

    /// <summary>The same ramp as a radial shading, so the second gradient element is exercised.</summary>
    private static byte[] RadialShadingPdf() =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Shading << /Sh0 5 0 R >> >> >>",
            StreamObj("", "q 10 10 80 80 re W n /Sh0 sh Q"),
            "<< /ShadingType 3 /ColorSpace /DeviceRGB /Coords [50 50 0 50 50 40]" +
            " /Extend [false false] /Function 6 0 R >>",
            "<< /FunctionType 2 /Domain [0 1] /C0 [0 0 0] /C1 [1 1 1] /N 1 >>");

    private static string StreamObj(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " +
        data.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((objects.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append((objects.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
