// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A <b>pattern</b>-stroked path draws the same miter spike a solid-coloured stroke draws.
/// </summary>
/// <remarks>
/// <para><b>Skia will not draw a very sharp miter, whatever limit you ask for.</b> It renders them
/// faithfully to a ratio of about 50 and then drops the spike, so this renderer detects the case
/// itself — miter join, a limit above 30, and a join sharper than Skia is willing to draw — and
/// builds the stroke by hand: the body with a bevel join, plus an explicit triangle grafted onto
/// each bevel out to the true apex. That is <c>TryStrokeLargeMiters</c>.</para>
///
/// <para><b>It was reached from the ordinary stroke and from a dead method, and from nothing
/// else.</b> Pattern painting moved to <c>DrawPatternPath</c>, which never called it, while the
/// method that did — <c>StrokePathWithShader</c> — kept its only shader-passing call site and lost
/// all its callers. So the workaround's entire pattern capability, including the soft-mask branch
/// written for it, was live code reachable only from dead code. A `333 M` join drew its spike when
/// stroked in grey and lost it when stroked with a pattern, in the same file.</para>
///
/// <para>The geometry is a very narrow V: two segments meeting at a miter ratio near 300, permitted
/// by a limit of 333 and far past what Skia will draw at a 1-point pen. The spike is the top 131
/// points of the ink, so losing it is a large, unambiguous change in the ink's bounding box — the
/// pattern stroke measured <b>zero</b> points of spike against the solid stroke's 131.</para>
/// </remarks>
public class PatternStrokeMiterTests
{
    private const int Dpi = 72;

    /// <summary>
    /// The V: apex at y=600, a miter ratio near 300, a 333 limit and a <b>1-point</b> pen.
    /// </summary>
    /// <remarks>
    /// <b>The pen width is load-bearing and a wider one made this fixture inert.</b> Whether Skia
    /// gives up on a miter depends on the spike's size in device units, not on the ratio alone, so
    /// a 4-point pen drew a ratio-67 spike natively — both routes agreed, and deleting the entire
    /// workaround left the fixture green. Measured across nine geometries at 1 pt: the solid stroke
    /// spikes at every ratio from 67 to 300, and the pattern stroke at none of them. Ratio 300 is
    /// the largest signal that still fits the page, at 131 points of spike.
    /// </remarks>
    private const string SharpV = "333 M 1 w 299 300 m 300 600 l 301 300 l S";

    /// <summary>A shading pattern that paints solid <b>red</b> everywhere.</summary>
    /// <remarks>
    /// <b>Red rather than black, because a black pattern hides a dropped shader.</b> When the
    /// workaround is handed no shader it falls back to the stroke paint's own colour, which for an
    /// unset pattern stroke is black — so with a black pattern the correct render and the
    /// shader-less one are the same pixels, and the mutation that drops the shader survived. A hue
    /// the fallback cannot produce is what makes that forwarding testable.
    /// </remarks>
    private const string RedPattern =
        "<< /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceRGB " +
        "/Coords [0 0 612 0] /Extend [true true] /Function << /FunctionType 2 " +
        "/Domain [0 1] /C0 [1 0 0] /C1 [1 0 0] /N 1 >> >> >>";

    /// <summary>
    /// The control: a solid-coloured stroke draws the spike, so the fixture is potent.
    /// </summary>
    /// <remarks>
    /// Without this, a pattern stroke that also failed to spike would agree with a broken control
    /// and the comparison below would pass on two wrongs.
    /// </remarks>
    [Fact]
    public void ASolidStrokeDrawsTheLargeMiterSpike()
    {
        var bounds = InkBounds(Render(Pdf($"0 g {SharpV}")));

        Assert.True(bounds.HasValue, "the solid stroke painted nothing at all");
        Assert.True(SpikeHeightPt(bounds!.Value) > 100,
            $"the control drew no spike: its ink rises only {SpikeHeightPt(bounds.Value):0} pt "
          + "above the apex, so this fixture cannot detect a lost one");
    }

    /// <summary>A pattern-stroked path draws the same spike.</summary>
    [Fact]
    public void APatternStrokeDrawsTheSameLargeMiterSpike()
    {
        var solid   = InkBounds(Render(Pdf($"0 g {SharpV}")));
        var pattern = InkBounds(Render(Pdf($"/Pattern CS /P1 SCN {SharpV}")));

        Assert.True(pattern.HasValue, "the pattern stroke painted nothing at all");

        float solidSpike   = SpikeHeightPt(solid!.Value);
        float patternSpike = SpikeHeightPt(pattern!.Value);

        Assert.True(patternSpike > solidSpike - 4,
            $"the pattern stroke lost the miter spike: it rises {patternSpike:0} pt above the apex "
          + $"against the solid stroke's {solidSpike:0} pt");
    }

    /// <summary>
    /// The pattern stroke's spike is subject to <c>/ca</c>, like the rest of the stroke.
    /// </summary>
    /// <remarks>
    /// <para>The hand-built stroke is painted as a <i>fill</i> of a unioned outline, and a fill
    /// carrying a shader takes its alpha from the paint's colour — which that branch leaves at
    /// opaque black unless the caller's alpha is carried across. Nothing about the spike's
    /// <i>shape</i> can show this, which is why it needs its own test.</para>
    /// <para><b>Measured as total darkness, not as one pixel.</b> At a ratio near 300 the spike is
    /// a needle well under a pixel wide for most of its length, so every pixel in it is a partial
    /// coverage value — the darkest was 196 of 255, and a threshold written for a solid black
    /// sample failed against a perfectly correct render. Summing coverage over the whole region
    /// above the apex is the measure the geometry actually supports.</para>
    /// </remarks>
    [Fact]
    public void APatternStrokesSpikeHonoursTheStrokeAlpha()
    {
        SKBitmap opaque = Render(Pdf($"/Pattern CS /P1 SCN {SharpV}"));
        SKBitmap half   = Render(Pdf($"/GSa gs /Pattern CS /P1 SCN {SharpV}",
                                     extGState: "/GSa << /CA 0.5 >>"));

        long opaqueInk = DarknessAboveApex(opaque);
        long halfInk   = DarknessAboveApex(half);

        Assert.True(opaqueInk > 0, "the opaque spike laid down no ink to compare against");
        Assert.True(halfInk < opaqueInk * 0.75,
            $"the spike ignored CA 0.5: darkness {halfInk} against the opaque {opaqueInk}");
    }

    /// <summary>The spike is painted by the <b>pattern</b>, not by the fallback colour.</summary>
    /// <remarks>
    /// The workaround paints the hand-built outline as a fill, and takes its colour from the
    /// shader when one is passed and from the stroke paint otherwise. Both produce a spike of the
    /// same shape, so only its hue can tell them apart — see <see cref="RedPattern"/>.
    /// </remarks>
    [Fact]
    public void TheSpikeIsPaintedByThePatternAndNotTheFallbackColour()
    {
        SKBitmap bmp = Render(Pdf($"/Pattern CS /P1 SCN {SharpV}"));

        (long red, long other) = SpikeHue(bmp);

        Assert.True(red + other > 0, "the spike laid down no ink at all");
        Assert.True(red > other * 2,
            $"the spike is not the pattern's red: red weight {red}, non-red weight {other}");
    }

    /// <summary>
    /// The pair's other half: a <b>line-to-curve</b> miter spike also survives a pattern stroke.
    /// </summary>
    /// <remarks>
    /// <para>Skia computes a miter from its <i>flattened</i> approximation of an adjoining Bézier,
    /// not from the analytic tangent, so an acute line-to-curve join is clamped far below the
    /// requested limit. The renderer pre-flattens curves itself when a miter join is active
    /// (<c>GetStrokePathForJoin</c>), and the pattern route did not.</para>
    /// <para><b>This is not the test that covers the pre-flattening, though it was written to
    /// be.</b> At a ratio near 300 the manual spike route claims the join and returns before the
    /// flattener is consulted, so bypassing the flattener leaves this green — it covers the
    /// <i>spike</i> route over a curve, which is worth having and is what its name now says.
    /// <see cref="APatternStrokeWithJoinsStillDistinguishesTheJoinStyle"/> is the one that pins
    /// the flattener.</para>
    /// </remarks>
    [Fact]
    public void APatternStrokeDrawsTheLineToCurveMiterSpike()
    {
        // Left arm a line, right arm a cubic whose initial tangent is near-vertical: the same
        // ~300 miter ratio as SharpV, but formed at a line-to-curve join.
        const string curveV =
            "333 M 1 w 299 300 m 300 600 l 300.33 500 300.67 400 301 300 c S";

        var solid   = InkBounds(Render(Pdf($"0 g {curveV}")));
        var pattern = InkBounds(Render(Pdf($"/Pattern CS /P1 SCN {curveV}")));

        Assert.True(solid.HasValue && pattern.HasValue, "a fixture painted nothing at all");

        float solidSpike   = SpikeHeightPt(solid!.Value);
        float patternSpike = SpikeHeightPt(pattern!.Value);

        Assert.True(solidSpike > 50,
            $"the control drew no line-to-curve spike ({solidSpike:0} pt), so this cannot detect a lost one");
        Assert.True(patternSpike > solidSpike - 4,
            $"the pattern stroke lost the line-to-curve spike: {patternSpike:0} pt against {solidSpike:0} pt");
    }

    /// <summary>
    /// A pattern stroke over a curve-to-curve join still tells the join styles apart, which it can
    /// only do if the Bézier pre-flattening runs on this route too.
    /// </summary>
    /// <remarks>
    /// <para><b>The other half of §8.4.3.5, and the large-limit tests above cannot reach it.</b>
    /// Those joins are claimed by the manual spike route, which returns before the flattener is
    /// consulted. The flattener matters at an <i>ordinary</i> limit, where Skia draws the miter
    /// itself but derives it from its flattened approximation of the curve rather than the true
    /// tangent.</para>
    /// <para><b>The geometry and the assertion are borrowed deliberately</b> from
    /// <c>JoinlessStrokeCapTests.APathWithJoinsStillDistinguishesTheJoinStyle</c>, which is the one
    /// test in the tree whose failure proves the flattening did something: bypassing
    /// <c>GetStrokePathForJoin</c> across the whole Base suite reddens that test and nothing else.
    /// Two fixtures of my own devising — a line-to-curve spike, and a route-parity comparison at
    /// limit 10 — both failed to detect the bypass, so the shape that is known to work is the one
    /// used here rather than a third guess.</para>
    /// <para>Without the flattening the two join styles render identically, so this is
    /// <c>NotEqual</c> on a hash of the whole page.</para>
    /// </remarks>
    [Fact]
    public void APatternStrokeWithJoinsStillDistinguishesTheJoinStyle()
    {
        // 8 w: wide enough that a fractional tangent change moves pixels. 2 J: projecting square
        // caps, which carry the tangent's direction. 10 M: the DEFAULT limit, so the manual spike
        // route declines and the flattener is what is under test.
        const string ops = "8 w 2 J 10 M /Pattern CS /P1 SCN "
                         + "40 40 m 120 200 200 -40 280 80 c 360 200 420 -20 500 80 c S";

        string miter = RenderHash(Pdf(ops));
        string round = RenderHash(Pdf("1 j " + ops));

        Assert.NotEqual(round, miter);
    }

    /// <summary>
    /// The vector half: the SVG export of the same pattern-stroked join carries the miter limit,
    /// so a viewer draws the spike the raster path now draws by hand.
    /// </summary>
    /// <remarks>
    /// <para><b>The two renderers reach the same answer by opposite routes, and only this pins
    /// that.</b> Skia will not draw the join, so the raster path builds it; SVG has a native
    /// <c>stroke-miterlimit</c>, so the exporter states the limit and the viewer draws it. Every
    /// other test in this file measures the raster side, which would leave the export free to omit
    /// the attribute and lose the spike in a browser while the render kept it.</para>
    /// <para>A pattern stroke is the case worth asserting rather than a solid one: it resolves its
    /// paint through a different branch before building the stroke attribute, so "the pattern
    /// branch forgot the geometry" is a defect the solid-stroke tests cannot see.</para>
    /// </remarks>
    [Fact]
    public void TheSvgExportOfAPatternStroke_CarriesTheMiterLimit()
    {
        using var ms = new MemoryStream(Pdf($"/Pattern CS /P1 SCN {SharpV}"));
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        XElement stroked = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .FirstOrDefault(e => (e.Attribute("stroke")?.Value ?? "none") != "none");

        Assert.True(stroked != null, "the export drew no stroked <path>:" + Environment.NewLine + svg);

        string raw = stroked!.Attribute("stroke-miterlimit")?.Value;
        Assert.True(raw != null,
            "the pattern-stroked <path> carries no stroke-miterlimit, so it exports at SVG's "
          + "default of 4 and a viewer bevels the join the raster renderer spikes");
        Assert.Equal(333f, float.Parse(raw, CultureInfo.InvariantCulture), 3);
    }

    // ====================================================================
    //  Fixtures
    // ====================================================================

    /// <summary>A hash of every pixel, for an assertion about "the same rendering".</summary>
    private static string RenderHash(byte[] pdf)
    {
        using SKBitmap bmp = Render(pdf, 150);
        return Convert.ToHexString(SHA256.HashData(bmp.GetPixelSpan().ToArray()));
    }

    /// <summary>
    /// Ink above the apex, split into the red the pattern paints and everything else.
    /// </summary>
    private static (long red, long other) SpikeHue(SKBitmap bmp)
    {
        int apexRow = RowFor(bmp, userY: 600);
        long red = 0, other = 0;
        for (int y = 0; y < apexRow; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha <= 8) continue;
                int coverage = 255 - Math.Min(c.Red, Math.Min(c.Green, c.Blue));
                if (coverage <= 0) continue;
                // Red ink keeps its red channel high while the others fall away.
                if (c.Red > c.Green + 40 && c.Red > c.Blue + 40) red += coverage;
                else                                             other += coverage;
            }
        return (red, other);
    }

    /// <summary>How far the ink rises above the V's apex at y=600, in points.</summary>
    private static float SpikeHeightPt((int left, int top, int right, int bottom) b)
    {
        // InkBounds is in device rows from the top; the page is 792 pt tall at 72 dpi.
        float topUserY = 792f - b.top;
        return topUserY - 600f;
    }

    private static int RowFor(SKBitmap bmp, float userY) =>
        bmp.Height - 1 - (int)(userY * Dpi / 72f);

    /// <summary>
    /// Total ink coverage strictly above the V's apex — the region only the grafted spike paints.
    /// </summary>
    private static long DarknessAboveApex(SKBitmap bmp)
    {
        int apexRow = RowFor(bmp, userY: 600);
        long sum = 0;
        for (int y = 0; y < apexRow; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                // Minimum channel, not the red one: the pattern IS red, so its red channel
                // stays at 255 and a red-channel measure would score a solid spike as blank.
                if (c.Alpha > 8) sum += 255 - Math.Min(c.Red, Math.Min(c.Green, c.Blue));
            }
        return sum;
    }

    private static (int left, int top, int right, int bottom)? InkBounds(SKBitmap bmp)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Alpha <= 8 || (c.Red >= 240 && c.Green >= 240 && c.Blue >= 240)) continue;
                if (x < l) l = x;
                if (x > r) r = x;
                if (y < t) t = y;
                if (y > b) b = y;
            }
        return r < 0 ? null : (l, t, r, b);
    }

    private static SKBitmap Render(byte[] pdf, int dpi = Dpi)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: dpi);
        SKBitmap bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    private static byte[] Pdf(string content, string extGState = null)
    {
        string gsEntry = extGState == null ? string.Empty : $"/ExtGState << {extGState} >> ";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            $"/Resources << /Pattern << /P1 5 0 R >> {gsEntry}>> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            RedPattern,
        };

        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Length);
            pdf.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = pdf.Length;
        pdf.Append("xref\n0 ").Append(objects.Count + 1).Append('\n')
           .Append("0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(o.ToString("D10")).Append(" 00000 n \n");
        pdf.Append("trailer\n<< /Size ").Append(objects.Count + 1)
           .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
