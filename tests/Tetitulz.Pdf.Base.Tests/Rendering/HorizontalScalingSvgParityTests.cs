// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>Tz</c> must reach the SVG export the same way it reaches the raster path: stretching the
/// <b>glyph</b> and the <b>advance</b> alike, each exactly once.
/// </summary>
/// <remarks>
/// <b>Both renderers were wrong here, in opposite directions, and neither could see the other.</b>
/// The raster path scaled an embedded font's advances by <c>Th³</c> (the width scale was read off
/// the text matrix's X axis, which already carries <c>Th</c>, and the draw-time matrix applied it a
/// third time). The SVG path advanced correctly but built its glyph matrix in
/// <c>AppendTypefaceGlyphOutline</c> without <c>Th</c> at all, so every outline drew at full width
/// under a half-width advance — the very defect the 2026-08-07 round had fixed in raster, surviving
/// on the one SVG site that builds its own matrix.
/// <para>Asserting on the emitted matrices rather than on rasterised pixels is what makes this
/// checkable: the <c>&lt;use&gt;</c> transform carries the glyph scale in slot <c>a</c> and the pen
/// position in slot <c>e</c>, so one parse yields both halves of the claim.</para>
/// </remarks>
public class HorizontalScalingSvgParityTests
{
    private static (float scaleX, float x)[] GlyphMatrices(int tzPercent)
    {
        byte[] pdf = FontGapClosureTests.ProbeScaledRunPdf(tzPercent);
        string svg;
        using (var ms = new MemoryStream(pdf)) svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        return XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "use" && e.Attribute("transform") != null)
            .Select(e =>
            {
                var m = Regex.Match(e.Attribute("transform")!.Value, @"matrix\(([^)]*)\)");
                Assert.True(m.Success, "a <use> carried a transform that was not a matrix()");
                float[] v = m.Groups[1].Value.Split(',')
                    .Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
                return (v[0], v[4]);
            })
            .ToArray();
    }

    [Fact]
    public void HorizontalScaling_SvgStretchesGlyphsAndAdvancesAlike()
    {
        var full = GlyphMatrices(100);
        var half = GlyphMatrices(50);

        Assert.Equal(5, full.Length);
        Assert.Equal(5, half.Length);

        // The glyph scale halves — this is what was missing entirely.
        Assert.Equal(100f, full[0].scaleX, 1);
        Assert.Equal(50f, half[0].scaleX, 1);

        // ...and so does the pen step between consecutive glyphs: /Widths 800 at 100 pt = 80 pt.
        Assert.Equal(80f, full[1].x - full[0].x, 1);
        Assert.Equal(40f, half[1].x - half[0].x, 1);
    }
}
