// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Operations;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// ISO 32000-2 §8.7.3.1: a tiling pattern repeats its cell every <c>/XStep</c> by <c>/YStep</c>,
/// and a <c>/BBox</c> larger than the step makes neighbouring cells overlap. Both renderers build
/// one period from <see cref="TilingLattice"/>: every cell of the lattice that reaches into it.
/// </summary>
/// <remarks>
/// <para><b>The overlap fixture</b>: a cell 20 wide on a step of 10, painting red only at
/// x 12–18. Tiled, cells sit every 10 units, so red lands at 2–8 in every period — all of it spilled
/// in from the cell to the left. Clipping each cell to its own tile leaves nothing; repeating it at
/// its own width (what the raster renderer used to do) puts red at 12–18 every 20 units.</para>
/// </remarks>
public class TilingOverlapTests
{
    [Fact]
    public void Raster_ACellLargerThanItsStep_SpillsIntoTheNeighbouringTile()
    {
        using var bmp = PixelAssert.Render(PatternPage("/BBox [0 0 20 10] /XStep 10 /YStep 10", "1 0 0 rg 12 0 6 10 re f"));

        foreach (float x in new[] { 5f, 15f, 25f, 55f })
            Assert.Equal(new SKColor(255, 0, 0), PixelAssert.At(bmp, x, 50));
        foreach (float x in new[] { 10.5f, 20.5f, 50.5f })
            Assert.Equal(new SKColor(255, 255, 255), PixelAssert.At(bmp, x, 50));
    }

    /// <summary>
    /// The period is the step, not the cell, even where nothing spills: a cell 15 wide on a step of
    /// 10, red at x 0–5, is red at 10–15. Repeating at the cell's width put it at 15–20.
    /// </summary>
    [Fact]
    public void Raster_TheStepIsThePeriodWhenTheCellIsLarger()
    {
        using var bmp = PixelAssert.Render(PatternPage("/BBox [0 0 15 10] /XStep 10 /YStep 10", "1 0 0 rg 0 0 5 10 re f"));

        Assert.Equal(new SKColor(255, 0, 0), PixelAssert.At(bmp, 12.5f, 50));
        Assert.Equal(new SKColor(255, 255, 255), PixelAssert.At(bmp, 17.5f, 50));
    }

    /// <summary>
    /// The same overlap upwards: a cell 15 tall on a step of 10, red at y 11–14, is red at 1–4 in
    /// every period, from the cell below. Anchoring the tile at the cell's top edge rather than its
    /// bottom shifted that by the 5-unit excess.
    /// </summary>
    [Fact]
    public void Raster_ACellTallerThanItsStep_SpillsIntoTheTileAbove()
    {
        using var bmp = PixelAssert.Render(PatternPage("/BBox [0 0 10 15] /XStep 10 /YStep 10", "1 0 0 rg 0 11 10 3 re f"));

        foreach (float y in new[] { 2.5f, 32.5f, 72.5f })
            Assert.Equal(new SKColor(255, 0, 0), PixelAssert.At(bmp, 50, y));
        foreach (float y in new[] { 7.5f, 37.5f, 77.5f })
            Assert.Equal(new SKColor(255, 255, 255), PixelAssert.At(bmp, 50, y));
    }

    /// <summary>
    /// A step that is not a whole number of device pixels keeps its period on both axes: at 100 dpi
    /// a step of 10 is 13.9 pixels, and a tile rounded up to 14 and mapped at the device scale
    /// drifted 0.8 % a period — 59 periods along, the square at 590–595 had moved to 594.7–599.7.
    /// </summary>
    [Fact]
    public void Raster_AFractionalPixelStep_DoesNotDrift()
    {
        const int Dpi = 100;
        using var bmp = PixelAssert.Render(
            PatternPage("/BBox [0 0 10 10] /XStep 10 /YStep 10", "1 0 0 rg 0 0 5 5 re f", width: 600, height: 600), Dpi);

        Assert.Equal(new SKColor(255, 0, 0), PixelAssert.At(bmp, 592.5f, 592.5f, Dpi));
        Assert.Equal(new SKColor(255, 255, 255), PixelAssert.At(bmp, 597.5f, 592.5f, Dpi));
        Assert.Equal(new SKColor(255, 255, 255), PixelAssert.At(bmp, 592.5f, 597.5f, Dpi));
    }

    /// <summary>
    /// The cell is drawn at the tile's scale, not the page's. At 150 dpi a step of 2 is 4.17 pixels,
    /// rounded up to a tile of 5, and a cell painted at the page's scale filled 4.17 of those 5 rows
    /// and columns — so a cell that fills its step, a solid blue, tiled with a white seam a sixth of
    /// the period wide; at a one-point step (a tile of 3 pixels for a cell drawn 2.08 high) the tile
    /// was the cell's empty top rows and the page stayed white. Found by the coverage-guided fuzzer's
    /// control fixture (GAPS.md §7c). The two steps round apart — 3 by 2 is a tile of 7 by 5, scales
    /// 2.33 and 2.5 — so the cell's two axes take two scales; drawn at one, its rows fall short.
    /// </summary>
    [Theory]
    [InlineData(2f, 2f)]
    [InlineData(1f, 1f)]
    [InlineData(3f, 2f)]
    public void Raster_TheCellIsDrawnAtTheTilesScale_SoAFillingCellLeavesNoSeam(float xStep, float yStep)
    {
        const int Dpi = 150;
        string sx = xStep.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string sy = yStep.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var bmp = PixelAssert.Render(
            PatternPage($"/BBox [0 0 {sx} {sy}] /XStep {sx} /YStep {sy}", $"0 0 1 rg 0 0 {sx} {sy} re f"), Dpi);

        // Every interior device pixel: a seam anywhere in the tiling is a white pixel somewhere here.
        for (int y = 2; y < bmp.Height - 2; y++)
            for (int x = 2; x < bmp.Width - 2; x++)
                if (bmp.GetPixel(x, y) != new SKColor(0, 0, 255))
                    Assert.Fail($"pixel ({x}, {y}) is {bmp.GetPixel(x, y)}, not the cell's blue");
    }

    /// <summary>
    /// The SVG export of the overlap fixture: the cell once, clipped to its <c>/BBox</c>, and a
    /// <c>&lt;use&gt;</c> of it per lattice copy, in lattice order — the left neighbour first — and
    /// nothing reported. A pattern whose cell fits its step is emitted as it was, with no copies.
    /// </summary>
    [Fact]
    public void Svg_ACellLargerThanItsStep_IsTiledFromCopiesOfTheClippedCell()
    {
        byte[] pdf = PatternPage("/BBox [0 0 20 10] /XStep 10 /YStep 10", "1 0 0 rg 12 0 6 10 re f");
        List<string> reports = Reports(() => PdfOperations.RenderPageToSvg(new MemoryStream(pdf), 0));
        XDocument svg = Svg(pdf);

        XElement pattern = Assert.Single(svg.Descendants(SvgNs + "pattern"));
        Assert.Equal("10", (string)pattern.Attribute("width"));
        XElement clipRect = Assert.Single(pattern.Descendants(SvgNs + "clipPath")).Element(SvgNs + "rect");
        Assert.Equal("20", (string)clipRect.Attribute("width"));
        Assert.Equal("10", (string)clipRect.Attribute("height"));

        List<XElement> uses = pattern.Elements(SvgNs + "use").ToList();
        Assert.Equal(new[] { "translate(-10,0)", "translate(0,0)" }, uses.Select(u => (string)u.Attribute("transform")));
        XNamespace xlink = "http://www.w3.org/1999/xlink";
        XElement cell = pattern.Descendants(SvgNs + "g").Single(g => g.Attribute("clip-path") != null);
        Assert.All(uses, u => Assert.Equal("#" + (string)cell.Attribute("id"), (string)u.Attribute(xlink + "href")));
        Assert.Single(cell.Descendants(SvgNs + "path"));
        Assert.Empty(reports);

        XDocument tall = Svg(PatternPage("/BBox [0 0 10 15] /XStep 10 /YStep 10", "1 0 0 rg 0 11 10 3 re f"));
        Assert.Equal(new[] { "translate(0,-10)", "translate(0,0)" },
            Assert.Single(tall.Descendants(SvgNs + "pattern")).Elements(SvgNs + "use").Select(u => (string)u.Attribute("transform")));

        XDocument fits = Svg(PatternPage("/BBox [0 0 10 10] /XStep 10 /YStep 10", "1 0 0 rg 2 0 6 10 re f"));
        XElement fitting = Assert.Single(fits.Descendants(SvgNs + "pattern"));
        Assert.Empty(fitting.Elements(SvgNs + "use"));
        Assert.Single(fitting.Descendants(SvgNs + "path"));
    }

    /// <summary>
    /// A hostile overlap — a cell a million units wide on a step of 1 — would need a million copies
    /// per tile; both renderers draw the cell once per step instead and report it.
    /// </summary>
    [Fact]
    public void HostileOverlap_IsBoundedAndReported_ByBothRenderers()
    {
        byte[] pdf = PatternPage("/BBox [0 0 1000000 1000000] /XStep 1 /YStep 1", "1 0 0 rg 0 0 1 1 re f");

        Assert.Contains(Reports(() => PixelAssert.Render(pdf).Dispose()), d => d.Contains("overlap"));
        Assert.Contains(Reports(() => PdfOperations.RenderPageToSvg(new MemoryStream(pdf), 0)), d => d.Contains("overlap"));
        Assert.DoesNotContain(Reports(() => PixelAssert.Render(
            PatternPage("/BBox [0 0 20 10] /XStep 10 /YStep 10", "1 0 0 rg 12 0 6 10 re f")).Dispose()), d => d.Contains("overlap"));
    }

    [Theory]
    [InlineData(20f, 10f, 10f, 10f, new[] { -10f, 0f, 0f, 0f })]
    [InlineData(15f, 25f, 10f, 10f, new[] { -10f, -20f, 0f, -20f, -10f, -10f, 0f, -10f, -10f, 0f, 0f, 0f })]
    [InlineData(10f, 10f, 10f, 10f, new[] { 0f, 0f })]
    [InlineData(10.00001f, 10f, 10f, 10f, new[] { 0f, 0f })]
    [InlineData(5f, 5f, 10f, 10f, new[] { 0f, 0f })]
    [InlineData(20f, 10f, -10f, 10f, new[] { -10f, 0f, 0f, 0f })]
    public void Lattice_HoldsEveryCellReachingIntoOnePeriod_InLatticeOrder(
        float cellW, float cellH, float xStep, float yStep, float[] offsets)
    {
        TilingLattice lattice = TilingLattice.For(cellW, cellH, xStep, yStep);

        Assert.Equal(offsets, lattice.Copies.SelectMany(c => new[] { c.Dx, c.Dy }));
        Assert.Equal(Math.Abs(xStep), lattice.StepX);
        Assert.False(lattice.Approximated);
    }

    [Fact]
    public void Lattice_PastItsBound_HoldsTheCellOnceAndSaysSo()
    {
        TilingLattice lattice = TilingLattice.For(9f, 8f, 1f, 1f);   // 72 copies
        Assert.True(lattice.Approximated);
        Assert.Equal(new[] { (0f, 0f) }, lattice.Copies);

        TilingLattice atBound = TilingLattice.For(8f, 8f, 1f, 1f);   // 64 copies
        Assert.False(atBound.Approximated);
        Assert.Equal(TilingLattice.MaxCopies, atBound.Copies.Count);
    }

    private static readonly XNamespace SvgNs = "http://www.w3.org/2000/svg";

    private static XDocument Svg(byte[] pdf) => XDocument.Parse(PdfOperations.RenderPageToSvg(new MemoryStream(pdf), 0));

    /// <summary>The <c>PatternApproximated</c> details one render reports, on this thread.</summary>
    private static List<string> Reports(Action render)
    {
        var reports = new List<string>();
        int own = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != own) return;
            if (kind == RenderDiagnosticKind.PatternApproximated)
                lock (reports) reports.Add(detail ?? "");
        }
        RenderDiagnostics.OnEvent += Sink;
        try { render(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        lock (reports) return new List<string>(reports);
    }

    /// <summary>A page filled wholly with a coloured tiling pattern of <paramref name="geometry"/> painting <paramref name="cell"/>.</summary>
    private static byte[] PatternPage(string geometry, string cell, int width = 100, int height = 100) =>
        FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 {width} {height}] >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents 4 0 R " +
            "/Resources << /Pattern << /P0 5 0 R >> >> >>",
            FormPdfFixtures.StreamBody(string.Empty, $"/Pattern cs /P0 scn 0 0 {width} {height} re f"),
            FormPdfFixtures.StreamBody(
                $"/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 {geometry} /Resources << >>", cell));
}
