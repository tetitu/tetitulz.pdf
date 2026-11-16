// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Rendering.Halftones;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Halftoning (ISO 32000-2 §10.6): the screens of every halftone type, the device default, the
/// bilevel outputs.
/// </summary>
/// <remarks>
/// Checked against the reference renderer (<c>pbmraw</c>, 72 dpi) while it was written: a Type 6 array placed every
/// threshold on the pixel the reference renderer placed it, a Type 10 pair of squares and a Type 16 pair of rectangles tiled as
/// it did (pinned below), and a Type 1 Line screen at 30° ran the same way at the same slope.
/// The two differ in how a level is quantised — the reference renderer screens its 15-bit internal colour and whitens one
/// threshold more than ISO 32000-2's rule for levels from 129/255 up — so the tests below pin the rule, not that
/// implementation.
/// </remarks>
[Collection(PdfLimitsCollection.Name)]
public partial class HalftoneTests
{
    // ------------------------------------------------------------------ threshold arrays

    [Fact]
    public void ThresholdArray_InksExactlyWhereTheLevelIsBelowTheThreshold()
    {
        // Thresholds 8, 24, … 248 (none a multiple of 16) under bands of every sixteenth grey: the rule decides
        // each pixel, and one level more or less in either would move a whole threshold's pixels.
        var thresholds = new byte[] { 8, 136, 40, 168, 200, 72, 232, 104, 56, 184, 24, 152, 248, 120, 216, 88 };
        byte[] pdf = PageWithHalftone(64, GreyBands(64, 4, Enumerable.Range(0, 16).Select(i => i * 16 / 255.0)),
            Stream("/Type /Halftone /HalftoneType 6 /Width 4 /Height 4", thresholds));

        var page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72);

        Assert.Equal((64, 64), (page.Width, page.Height));
        int inked = 0;
        for (int y = 0; y < 64; y++)
            for (int x = 0; x < 64; x++)
            {
                int level = 16 * (x / 4);
                bool expected = level < thresholds[(y % 4) * 4 + x % 4];
                Assert.True(expected == page.IsInk(x, y), $"pixel ({x}, {y}) at level {level}");
                if (expected) inked++;
            }
        // Each band of 16 levels holds the thresholds above its level: 16 + 15 + … + 1, sixteen rows of cells.
        Assert.Equal(136 * 16, inked);
    }

    [Fact]
    public void ThresholdArray_ReadsAZeroAsOne()
    {
        var zero = HalftoneScreen.FromThresholdArray(1, 1, 0, 0, new[] { 0 }, 255);
        var one = HalftoneScreen.FromThresholdArray(1, 1, 0, 0, new[] { 1 }, 255);
        var two = HalftoneScreen.FromThresholdArray(1, 1, 0, 0, new[] { 2 }, 255);

        // Threshold 1: only level 0 — full ink — is below it.
        Assert.Equal(255, one.MinInkAt(0, 0));
        Assert.Equal(one.MinInkAt(0, 0), zero.MinInkAt(0, 0));
        Assert.Equal(254, two.MinInkAt(0, 0));
    }

    [Fact]
    public void Type16_SixteenBitThresholdsSplitTheLevelsWhereTheyFall()
    {
        // 32 768 / 65 535 of the way: level 127/255 (0.498) is below it, 128/255 (0.502) is not. As eight bits
        // the threshold would be 128, and level 127 alone below it too — so the second page tells the two apart.
        byte[] pdf = PageWithHalftone(16, GreyBands(16, 8, new[] { 127 / 255.0, 128 / 255.0 }),
            Stream("/Type /Halftone /HalftoneType 16 /Width 1 /Height 1", new byte[] { 0x80, 0x00 }));
        var page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72);
        Assert.True(Enumerable.Range(0, 8).All(x => page.IsInk(x, 5)));
        Assert.True(Enumerable.Range(8, 8).All(x => !page.IsInk(x, 5)));

        // 32 639 / 65 535 is exactly 127/255: level 127 is not below it, 126 is.
        byte[] lower = PageWithHalftone(16, GreyBands(16, 8, new[] { 126 / 255.0, 127 / 255.0 }),
            Stream("/Type /Halftone /HalftoneType 16 /Width 1 /Height 1", new byte[] { 0x7F, 0x7F }));
        var page2 = PdfHalftoning.RenderPage(new MemoryStream(lower), 0, 72);
        Assert.True(page2.IsInk(3, 3));
        Assert.False(page2.IsInk(11, 3));
    }

    [Theory]
    [InlineData(3, 3, 2, 2)]
    [InlineData(45, 45, 8, 8)]   // 2 089 pixels a cell, prime: a period of 2 089 × 2 089, past the tile — placed pixel by pixel
    [InlineData(4, 2, 2, 3)]     // Type 16's rectangles
    [InlineData(2, 3, 5, 1)]     // the second wider than the first
    [InlineData(5, 4, 0, 0)]     // one rectangle
    public void TwoRectangles_ArePlacedAsTheLatticeRepeatsThem(int w, int h, int w2, int h2)
    {
        int n = w * h + w2 * h2;
        var values = Enumerable.Range(0, n).Select(i => 1 + (i * 7) % 255).ToArray();
        var screen = HalftoneScreen.FromThresholdArray(w, h, w2, h2, values, 255);

        // The forward placement, stamped cell by cell: the first rectangle at the cell's corner, the second below
        // it, each cell (a, b) at a·(W, −H₂) + b·(W₂, H), y downward — or (W, 0) and (0, H) for one. The screen
        // looks each pixel up the other way.
        int t1x = w, t1y = -h2, t2x = w2, t2y = h;
        if (w2 == 0) { t2x = 0; t2y = h; }
        const int window = 64;
        var expected = new int?[window, window];
        int reach = 3 * window;
        for (int a = -reach; a <= reach; a++)
            for (int b = -reach; b <= reach; b++)
            {
                int ox = a * t1x + b * t2x, oy = a * t1y + b * t2y;
                if (ox < -window || oy < -window || ox > window || oy > window) continue;
                for (int i = 0; i < n; i++)
                {
                    int qx = i < w * h ? i % w : (i - w * h) % w2;
                    int qy = i < w * h ? i / w : h + (i - w * h) / w2;
                    int px = ox + qx, py = oy + qy;
                    if (px < 0 || py < 0 || px >= window || py >= window) continue;
                    Assert.Null(expected[px, py]);   // no pixel in two cells
                    expected[px, py] = values[i];
                }
            }
        for (int y = 0; y < window; y++)
            for (int x = 0; x < window; x++)
            {
                Assert.True(expected[x, y].HasValue, $"pixel ({x}, {y}) in no stamped cell");
                Assert.Equal(256 - expected[x, y]!.Value, screen.MinInkAt(x, y));
            }

        // Which path answered: a small lattice keeps a tile, the prime one does not.
        if (n == 2089) Assert.Equal(0, screen.PeriodX);
        else Assert.True(screen.PeriodX > 0);
    }

    [Fact]
    public void Type16_MatchesTheReferenceScreen()
    {
        // A 2 × 3 rectangle over a 5 × 1 one — the second wider than the first — rendered by the reference renderer
        // (pbmraw, 72 dpi), its first eight rows.
        string[] reference =
        {
            ".#######.##.#######.##.#.##.##.##.#.##.#",
            "######.##.#######.##.#.##.##.##.#.##.##.",
            "####.##.#######.##.#.##.##.##.#.##.##.##",
            "##.##.#######.##.####.##.##.#.##.##.##.#",
            ".##.#######.##.#######.##.#.##.##.##.#.#",
            "#.#######.##.#######.##.#.##.##.##.#.##.",
            "#######.##.#######.##.#.##.##.##.#.##.##",
            "#####.##.#######.##.#.##.##.##.#.##.##.#",
        };
        const int n = 2 * 3 + 5 * 1;
        var data = new byte[2 * n];
        for (int i = 0; i < n; i++)
        {
            int v = (int)((i * 7 % n + 0.5) * 65535 / n);
            data[2 * i] = (byte)(v >> 8);
            data[2 * i + 1] = (byte)v;
        }
        byte[] pdf = PageWithHalftone(40, GreyBands(40, 20, new[] { 40 / 255.0, 100 / 255.0 }),
            Stream("/Type /Halftone /HalftoneType 16 /Width 2 /Height 3 /Width2 5 /Height2 1", data));
        var page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72);
        for (int y = 0; y < reference.Length; y++)
            Assert.Equal(reference[y], new string(Enumerable.Range(0, 40).Select(x => page.IsInk(x, y) ? '#' : '.').ToArray()));
    }

    [Fact]
    public void Tile_AgreesWithThePixelByPixelPlacementOverAPeriod()
    {
        var values = Enumerable.Range(1, 13).Select(i => i * 19).ToArray();
        var screen = HalftoneScreen.FromThresholdArray(3, 3, 2, 2, values, 255);
        Assert.Equal(13, screen.PeriodX);   // (3, −2), (2, 3): det 13, prime
        Assert.Equal(13, screen.PeriodY);
        for (int y = -13; y < 26; y++)
            for (int x = -13; x < 26; x++)
                Assert.Equal(screen.MinInkUntiled(x, y), screen.MinInkAt(x, y));
    }

    [Fact]
    public void Type10_MatchesTheReferenceScreen()
    {
        // The reference renderer's pbmraw at 72 dpi of this page, its first ten rows ('#' ink): an implementation of
        // PLRM §7.4's Type 10 this library did not write. Levels 64 and 100, below the 129 where its quantisation
        // parts from the rule's.
        string[] reference =
        {
            ".#.######.###.#.######.###.#...####.###.",
            "#.###.#.######.###.#...####.###.#...####",
            "######.###.#.######.###.#...####.###.#..",
            "##.#.######.###.#.######.###.#...####.##",
            "###.###.#.######.###.#...####.###.#...##",
            "#.######.###.#.######.###.#...####.###.#",
            ".###.#.######.###.#...####.###.#...####.",
            "#####.###.#.######.###.#...####.###.#...",
            "#.#.######.###.#.######.###.#...####.###",
            "##.###.#.######.###.#...####.###.#...###",
        };
        var values = new byte[] { 10, 200, 30, 220, 50, 240, 70, 90, 110, 130, 150, 170, 190 };
        byte[] pdf = PageWithHalftone(40, GreyBands(40, 20, new[] { 64 / 255.0, 100 / 255.0 }),
            Stream("/Type /Halftone /HalftoneType 10 /Xsquare 3 /Ysquare 2", values));
        var page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72);
        for (int y = 0; y < reference.Length; y++)
            Assert.Equal(reference[y], new string(Enumerable.Range(0, 40).Select(x => page.IsInk(x, y) ? '#' : '.').ToArray()));
    }

    [Fact]
    public void Type10_ThroughTheDocument_MatchesTheScreen()
    {
        var values = new byte[] { 10, 200, 30, 220, 50, 240, 70, 90, 110, 130, 150, 170, 190 };
        byte[] pdf = PageWithHalftone(32, "/G0 gs 0.5 g 0 0 32 32 re f",
            Stream("/Type /Halftone /HalftoneType 10 /Xsquare 3 /Ysquare 2", values));
        var page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72);
        var screen = HalftoneScreen.FromThresholdArray(3, 3, 2, 2, values.Select(v => (int)v).ToArray(), 255);

        // Grey 0.5 is level 128: ink where the threshold is above it.
        int inked = 0;
        for (int y = 0; y < 32; y++)
            for (int x = 0; x < 32; x++)
            {
                bool expected = 127 >= screen.MinInkAt(x, y);
                Assert.Equal(expected, page.IsInk(x, y));
                if (expected) inked++;
            }
        Assert.InRange(inked, 1, 32 * 32 - 1);
    }

    // ------------------------------------------------------------------ spot functions

    [Theory]
    [InlineData("Round", 0)]
    [InlineData("Round", 15)]
    [InlineData("Round", 45)]
    [InlineData("Round", 75)]
    [InlineData("Ellipse", 30)]
    [InlineData("Line", -20)]
    public void SpotFunction_InksExactlyItsShareOfEveryCell(string spot, double angle)
    {
        var screen = HalftoneScreen.FromSpotFunction(50, angle, 300, SpotFunctions.Named(spot), PdfLimits.MaxHalftoneCellPixels);
        long n = screen.CellPixels;
        int px = screen.PeriodX, py = screen.PeriodY;
        Assert.True(px > 0 && py > 0);
        long cells = (long)px * py / n;
        Assert.Equal((long)px * py, cells * n);

        // A cell of n pixels at level L whitens floor(L·n/255) of them.
        for (int tint = 0; tint <= 255; tint++)
        {
            long inked = 0;
            for (int y = 0; y < py; y++)
                for (int x = 0; x < px; x++)
                    if (tint > 0 && tint >= screen.MinInkAt(x, y)) inked++;
            Assert.Equal(cells * (n - (255 - tint) * n / 255), inked);
        }
    }

    [Fact]
    public void SpotFunction_WhitensTheLowestValueFirst()
    {
        // SimpleDot is highest at the cell's centre: those pixels are whitened last, so they take ink first —
        // a dot screen (§10.6.3's example). Angle 0, side 8: the cell is (0 … 7, 0 … 7).
        var screen = HalftoneScreen.FromSpotFunction(9, 0, 72, SpotFunctions.Named("SimpleDot"), PdfLimits.MaxHalftoneCellPixels);
        Assert.Equal((8, 0), screen.CellVector);
        var byInk = (from y in Enumerable.Range(0, 8) from x in Enumerable.Range(0, 8)
                     orderby screen.MinInkAt(x, y), y, x select (x, y)).ToList();
        Assert.Subset(new HashSet<(int, int)> { (3, 3), (4, 3), (3, 4), (4, 4) }, new HashSet<(int, int)>(byInk.Take(4)));
        Assert.Subset(new HashSet<(int, int)> { (0, 0), (7, 0), (0, 7), (7, 7) }, new HashSet<(int, int)>(byInk.Skip(60)));
    }

    [Theory]
    [InlineData(30, 10, 6)]
    [InlineData(0, 12, 0)]
    [InlineData(90, 12, 0)]     // a square cell turned a quarter is the same cell
    [InlineData(120, 10, 6)]
    [InlineData(-60, 10, 6)]
    [InlineData(75, 3, 12)]
    public void SpotFunction_CellTurnsByTheAngle(double angle, int x, int y)
    {
        // 12 pixels a side. Device y runs down the page, and the cell's side turns from +x toward +y — the way
        // the reference renderer's Line screen at 30° ran, its lines descending to the right.
        var screen = HalftoneScreen.FromSpotFunction(6, angle, 72, SpotFunctions.Named("Line"), PdfLimits.MaxHalftoneCellPixels);
        Assert.Equal((x, y), screen.CellVector);
        Assert.Equal(x * x + y * y, screen.CellPixels);
    }

    [Fact]
    public void PredefinedSpotFunctions_RunTheTablesPrograms()
    {
        Assert.Equal(21, SpotFunctions.Names.Count());
        foreach (string name in SpotFunctions.Names)
        {
            var f = SpotFunctions.Named(name);
            Assert.NotNull(f);
            double v = f(0.3, -0.6);
            Assert.True(v >= -4 && v <= 4, $"{name} answered {v}");
            Assert.NotNull(HalftoneScreen.FromSpotFunction(50, 15, 300, f, PdfLimits.MaxHalftoneCellPixels));
        }

        // The formulas of Table 126, at points in each branch.
        Assert.Equal(0.75, SpotFunctions.Named("SimpleDot")(0.3, 0.4), 5);
        Assert.Equal(0.6875, SpotFunctions.Named("Round")(0.5, 0.25), 5);
        Assert.Equal(-0.875, SpotFunctions.Named("Round")(0.75, 0.75), 5);
        Assert.Equal(-0.4, SpotFunctions.Named("Line")(0.9, 0.4), 5);
        // Diamond's middle branch, and its last — the one whose printed "du mul" is read as "dup mul".
        Assert.Equal(1 - (0.85 * 0.5 + 0.5), SpotFunctions.Named("Diamond")(0.5, 0.5), 5);
        Assert.Equal(-0.98, SpotFunctions.Named("Diamond")(0.9, 0.9), 5);
        Assert.Null(SpotFunctions.Named("Bogus"));
    }

    [Fact]
    public void Type1_TakesTheFirstKnownNameOfAnArray_AndAFunction()
    {
        string bands = GreyBands(48, 12, new[] { 0.2, 0.4, 0.6, 0.8 });
        PdfBilevelImage Render(string spot) => PdfHalftoning.RenderPage(new MemoryStream(
            PageWithHalftone(48, bands, $"<< /Type /Halftone /HalftoneType 1 /Frequency 12 /Angle 0 /SpotFunction {spot} >>",
                Stream("/FunctionType 4 /Domain [-1 1 -1 1] /Range [-1 1]", Encoding.ASCII.GetBytes("{ pop }")))), 0, 72);

        var named = Render("/LineX");
        var array = Render("[/NotAFunction /LineX /Round]");
        var function = Render("6 0 R");
        var round = Render("/Round");
        Assert.Equal(named.Bits, array.Bits);
        Assert.Equal(named.Bits, function.Bits);
        Assert.NotEqual(named.Bits, round.Bits);
    }

    // ------------------------------------------------------------------ Type 5, transfer, the default

    [Fact]
    public void Type5_EachColorantTakesItsOwnScreen_AndTheRestTheDefault()
    {
        // Cyan through a two-pixel array (64, 192), black through the /Default (128), both at tint 0.5 (level 127).
        byte[] pdf = PageWithHalftone(16, "/G0 gs 0.5 0 0 0.5 k 0 0 16 16 re f",
            "<< /Type /Halftone /HalftoneType 5 /Cyan 6 0 R /Default 7 0 R >>",
            Stream("/Type /Halftone /HalftoneType 6 /Width 2 /Height 1", new byte[] { 64, 192 }),
            Stream("/Type /Halftone /HalftoneType 6 /Width 1 /Height 1", new byte[] { 128 }));

        var separations = PdfHalftoning.RenderSeparations(new MemoryStream(pdf), 0, 72);
        Assert.Equal(separations.Plates.Select(p => p.Bits),
            PdfOperations.RenderPageSeparationsHalftoned(new MemoryStream(pdf), 0, 72).Plates.Select(p => p.Bits));

        var cyan = separations[PdfColorSpaceNames.CyanColorant];
        var black = separations[PdfColorSpaceNames.BlackColorant];
        var magenta = separations[PdfColorSpaceNames.MagentaColorant];
        for (int y = 0; y < 16; y++)
            for (int x = 0; x < 16; x++)
            {
                Assert.Equal(x % 2 == 1, cyan.IsInk(x, y));
                Assert.True(black.IsInk(x, y));
                Assert.False(magenta.IsInk(x, y));
            }
    }

    /// <summary>
    /// A Type 5 halftone's components are read when a colorant asks for one: two hundred colorant keys
    /// naming one 256 × 256 threshold array were two hundred arrays of 256 KB read before a grey page,
    /// which asks only for <c>Gray</c> and gets the <c>/Default</c>. The potency: the same page screened
    /// with the <c>/Default</c> alone is the same bits.
    /// </summary>
    [Fact]
    public void Type5_ReadsAComponentOnlyWhenItsColorantAsks()
    {
        string keys = string.Concat(Enumerable.Range(0, 200).Select(i => $"/K{i} 6 0 R "));
        string content = "/G0 gs 0.5 g 0 0 16 16 re f";
        byte[] many = PageWithHalftone(16, content,
            $"<< /Type /Halftone /HalftoneType 5 {keys}/Default 7 0 R >>",
            Stream("/Type /Halftone /HalftoneType 6 /Width 256 /Height 256", new byte[256 * 256]),
            Stream("/Type /Halftone /HalftoneType 6 /Width 2 /Height 1", new byte[] { 64, 192 }));
        byte[] alone = PageWithHalftone(16, content,
            "<< /Type /Halftone /HalftoneType 5 /Default 6 0 R >>",
            Stream("/Type /Halftone /HalftoneType 6 /Width 2 /Height 1", new byte[] { 64, 192 }));

        PdfHalftoning.RenderPage(new MemoryStream(many), 0, 72);   // warm the paths
        long before = GC.GetAllocatedBytesForCurrentThread();
        var page = PdfHalftoning.RenderPage(new MemoryStream(many), 0, 72);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(PdfHalftoning.RenderPage(new MemoryStream(alone), 0, 72).Bits, page.Bits);
        Assert.InRange(allocated, 0, 16L << 20);   // 200 arrays of 256 KB were 51 MB
    }

    /// <summary>
    /// A spot function runs once a cell pixel, and spends one function budget for the screen: a Type 4 spot
    /// function looping two hundred times an evaluation, over a cell of 400 pixels, passes a budget of 5 000 —
    /// reported, and the device's default serves; under the default budget it is the page's own screen.
    /// </summary>
    [Fact]
    public void Type1_ALoopingSpotFunction_SpendsTheScreensFunctionBudget()
    {
        byte[] pdf = PageWithHalftone(72, "/G0 gs 0.5 g 0 0 72 72 re f",
            "<< /Type /Halftone /HalftoneType 1 /Frequency 3.6 /Angle 0 /SpotFunction 6 0 R >>",
            Stream("/FunctionType 4 /Domain [-1 1 -1 1] /Range [-1 1]", Encoding.ASCII.GetBytes("{ 0 1 199 { pop } for pop }")));
        try
        {
            PdfLimits.MaxFunctionWorkPerPage = 5000;
            PdfBilevelImage page = null!;
            var reports = Reports(() => page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72));
            Assert.Contains(reports, r => r.Contains("MaxFunctionWorkPerPage", StringComparison.Ordinal));
            Assert.Equal(81 * 2 * 16, Count(page));   // the device default's 16 of 32 a cell
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }

        var own = Reports(() => PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72));
        Assert.DoesNotContain(own, r => r.Contains("spot function", StringComparison.Ordinal));
    }

    [Fact]
    public void TransferFunction_MapsTheLevelBeforeTheScreen()
    {
        // One threshold of 128 under full black: inked everywhere — unless the halftone's transfer function lifts
        // every level to white.
        string Halftone(string transfer) => Stream("/Type /Halftone /HalftoneType 6 /Width 1 /Height 1 " + transfer, new byte[] { 128 });
        string content = "/G0 gs 0 g 0 0 8 8 re f";
        var plain = PdfHalftoning.RenderPage(new MemoryStream(PageWithHalftone(8, content, Halftone(""))), 0, 72);
        var identity = PdfHalftoning.RenderPage(new MemoryStream(PageWithHalftone(8, content, Halftone("/TransferFunction /Identity"))), 0, 72);
        var lifted = PdfHalftoning.RenderPage(new MemoryStream(PageWithHalftone(8, content,
            Halftone("/TransferFunction << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [1] /N 1 >>"))), 0, 72);

        Assert.True(plain.IsInk(4, 4));
        Assert.True(identity.IsInk(4, 4));
        Assert.False(lifted.IsInk(4, 4));

        // The function maps the additive level, not the tint: grey 0.2 (level 51) through √ is level 114. Under
        // thresholds 100 and 150 that is paper then ink; untransferred it would be ink twice, and the tint
        // (0.8 → 0.894, level 228) paper twice.
        string sqrt = "/TransferFunction << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 0.5 >>";
        var two = Stream("/Type /Halftone /HalftoneType 6 /Width 2 /Height 1 " + sqrt, new byte[] { 100, 150 });
        var root = PdfHalftoning.RenderPage(new MemoryStream(PageWithHalftone(8, "/G0 gs 0.2 g 0 0 8 8 re f", two)), 0, 72);
        Assert.Equal((false, true), (root.IsInk(4, 4), root.IsInk(5, 4)));

        // The separations take the same transfer: black at 0.8 is level 51 too.
        var plates = PdfHalftoning.RenderSeparations(new MemoryStream(PageWithHalftone(8, "/G0 gs 0 0 0 0.8 k 0 0 8 8 re f", two)), 0, 72);
        Assert.Equal((false, true), (plates[PdfColorSpaceNames.BlackColorant].IsInk(4, 4), plates[PdfColorSpaceNames.BlackColorant].IsInk(5, 4)));
    }

    [Fact]
    public void DefaultScreen_ServesAPageWithoutAHalftone_AndOneWhoseHalftoneIsIgnored()
    {
        // 72 dpi: a sixth is 12 cells per inch, a 6-pixel side at 45° rounded to (4, 4) — 32 pixels, a period of
        // 8 × 8 holding two cells. Grey 0.5 is level 128: floor(128·32/255) = 16 whitened, 16 inked a cell.
        byte[] none = HalftoneTests.Pdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R >>", Stream("", Encoding.ASCII.GetBytes("0.5 g 0 0 72 72 re f")));
        var page = PdfHalftoning.RenderPage(new MemoryStream(none), 0, 72);
        Assert.Equal(81 * 2 * 16, Count(page));

        // The page's own halftone inks everything at that level; told to ignore it, the device's screen serves.
        byte[] own = PageWithHalftone(72, "/G0 gs 0.5 g 0 0 72 72 re f",
            Stream("/Type /Halftone /HalftoneType 6 /Width 1 /Height 1", new byte[] { 255 }));
        Assert.Equal(72 * 72, Count(PdfHalftoning.RenderPage(new MemoryStream(own), 0, 72)));
        Assert.Equal(page.Bits, PdfHalftoning.RenderPage(new MemoryStream(own), 0, 72, new PdfHalftoneOptions { UseDocumentHalftones = false }).Bits);
        Assert.Equal(page.Bits, PdfOperations.RenderPageHalftoned(new MemoryStream(own), 0, 72, new PdfHalftoneOptions { UseDocumentHalftones = false }).Bits);
    }

    [Fact]
    public void DefaultScreen_TakesTheCallersFrequencyAndSpotFunction()
    {
        byte[] none = HalftoneTests.Pdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R >>", Stream("", Encoding.ASCII.GetBytes("0.5 g 0 0 72 72 re f")));
        var standard = PdfHalftoning.RenderPage(new MemoryStream(none), 0, 72);
        var coarse = PdfHalftoning.RenderPage(new MemoryStream(none), 0, 72, new PdfHalftoneOptions { Frequency = 8 });
        var line = PdfHalftoning.RenderPage(new MemoryStream(none), 0, 72, new PdfHalftoneOptions { SpotFunction = "Line" });
        Assert.NotEqual(standard.Bits, coarse.Bits);
        Assert.NotEqual(standard.Bits, line.Bits);
        Assert.Equal(standard.Bits, PdfHalftoning.RenderPage(new MemoryStream(none), 0, 72, new PdfHalftoneOptions { Frequency = 12 }).Bits);
    }

    [Fact]
    public void Options_RefuseWhatIsNotAScreen()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfHalftoneOptions { Frequency = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfHalftoneOptions { Frequency = double.PositiveInfinity });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfHalftoneOptions { Frequency = -1 });
        Assert.Throws<ArgumentException>(() => new PdfHalftoneOptions { SpotFunction = "Bogus" });
        Assert.Throws<ArgumentException>(() => new PdfHalftoneOptions { SpotFunction = null! });
        Assert.Equal(0, new PdfHalftoneOptions { Frequency = 0 }.Frequency);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfHalftoning.RenderPage(new MemoryStream(new byte[1]), 0, 0));
        Assert.Throws<ArgumentNullException>(() => PdfHalftoning.RenderSeparations(null!, 0));
    }

    // ------------------------------------------------------------------ what is approximated, reported

    [Fact]
    public void Reports_EachDepartureFromThePage()
    {
        string twoStates = "<< /Type /Catalog /Pages 2 0 R >>";
        byte[] pdf = HalftoneTests.Pdf(twoStates, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 8 8] /Resources << /ExtGState << "
            + "/G0 << /HT 5 0 R /HTO [1 1] >> /G1 << /HT << /Type /Halftone /HalftoneType 1 /Frequency 9 /Angle 0 /SpotFunction /Round /AccurateScreens true >> >> >> >> /Contents 4 0 R >>",
            Stream("", Encoding.ASCII.GetBytes("/G0 gs 0 g 0 0 8 8 re f /G1 gs")),
            "<< /Type /Halftone /HalftoneType 1 /Frequency 9 /Angle 0 /SpotFunction /Round /AccurateScreens true >>");

        var reports = Reports(() => PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72));
        Assert.Contains(reports, r => r.Contains("sets 2 halftones", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.Contains("/HTO", StringComparison.Ordinal));
        Assert.Contains(reports, r => r.Contains("/AccurateScreens", StringComparison.Ordinal));

        // A page with one plain halftone reports nothing.
        byte[] plain = PageWithHalftone(8, "/G0 gs 0 g 0 0 8 8 re f", Stream("/Type /Halftone /HalftoneType 6 /Width 1 /Height 1", new byte[] { 9 }));
        Assert.Empty(Reports(() => PdfHalftoning.RenderPage(new MemoryStream(plain), 0, 72)));
    }

    [Theory]
    [InlineData("/Custom", "named /Custom")]
    [InlineData("<< /HalftoneType 6 /Width 4 /Height 4 /Length 3 >>\nstream\nabc\nendstream", "fewer than its 16")]
    [InlineData("<< /HalftoneType 6 /Width 0 /Height 4 /Length 0 >>\nstream\n\nendstream", "no area")]
    [InlineData("<< /HalftoneType 1 /Frequency 9 /Angle 0 /SpotFunction /Bogus >>", "spot function")]
    [InlineData("<< /HalftoneType 1 /Angle 0 /SpotFunction /Round >>", "/Frequency")]
    [InlineData("<< /HalftoneType 7 >>", "type 7")]
    [InlineData("<< /HalftoneType 5 /Default << /HalftoneType 5 >> >>", "inside a Type 5")]
    public void Reports_AHalftoneItCannotUse_AndScreensWithTheDefault(string halftone, string reason)
    {
        byte[] pdf = PageWithHalftone(72, "/G0 gs 0.5 g 0 0 72 72 re f", halftone);
        PdfBilevelImage page = null!;
        var reports = Reports(() => page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72));
        Assert.Contains(reports, r => r.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(81 * 2 * 16, Count(page));   // the device default's 16 of 32 a cell
    }

    [Fact]
    public void HostileHalftone_TooLargeACell_IsRefusedBeforeItIsBuilt()
    {
        // A threshold array of ten billion samples, and a screen of a thousandth of a cell per inch (a cell of
        // 72 000² pixels at 72 dpi): each would allocate past memory before a pixel was placed.
        foreach (string halftone in new[]
                 {
                     Stream("/HalftoneType 6 /Width 100000 /Height 100000", new byte[] { 1 }),
                     "<< /HalftoneType 1 /Frequency 0.001 /Angle 0 /SpotFunction /Round >>",
                 })
        {
            byte[] pdf = PageWithHalftone(72, "/G0 gs 0.5 g 0 0 72 72 re f", halftone);
            PdfBilevelImage page = null!;
            var reports = Reports(() => page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72));
            Assert.Contains(reports, r => r.Contains("PdfLimits.MaxHalftoneCellPixels", StringComparison.Ordinal));
            Assert.Equal(81 * 2 * 16, Count(page));
        }
    }

    [Fact]
    public void MaxHalftoneCellPixels_IsTheCeilingAsked()
    {
        // An 8 × 8 array: 64 samples, refused under a ceiling of 63 and used under 64.
        byte[] pdf = PageWithHalftone(8, "/G0 gs 0 g 0 0 8 8 re f",
            Stream("/Type /Halftone /HalftoneType 6 /Width 8 /Height 8", Enumerable.Repeat((byte)0, 64).ToArray()));
        try
        {
            PdfLimits.MaxHalftoneCellPixels = 63;
            Assert.Contains(Reports(() => PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72)), r => r.Contains("MaxHalftoneCellPixels", StringComparison.Ordinal));
            PdfLimits.MaxHalftoneCellPixels = 64;
            Assert.Empty(Reports(() => PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    [Fact]
    public void HostileDefaultFrequency_IsReportedAndReplaced()
    {
        byte[] none = HalftoneTests.Pdf("<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R >>", Stream("", Encoding.ASCII.GetBytes("0.5 g 0 0 72 72 re f")));
        PdfBilevelImage page = null!;
        var reports = Reports(() => page = PdfHalftoning.RenderPage(new MemoryStream(none), 0, 72, new PdfHalftoneOptions { Frequency = 0.0001 }));
        Assert.Contains(reports, r => r.Contains("a sixth of the resolution", StringComparison.Ordinal));
        Assert.Equal(81 * 2 * 16, Count(page));
    }

    // ------------------------------------------------------------------ the outputs

    [Fact]
    public void Pbm_IsTheBitsUnderAP4Header()
    {
        byte[] pdf = PageWithHalftone(10, "/G0 gs 0 g 0 0 5 10 re f", Stream("/Type /Halftone /HalftoneType 6 /Width 1 /Height 1", new byte[] { 128 }));
        var page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72);
        byte[] pbm = page.ToPbm();
        byte[] header = Encoding.ASCII.GetBytes("P4\n10 10\n");
        Assert.Equal(header, pbm.Take(header.Length).ToArray());
        Assert.Equal(page.Bits, pbm.Skip(header.Length).ToArray());
        // Ink in the left five columns: 0b11111000, then 0b00000000.
        Assert.Equal(new byte[] { 0xF8, 0x00 }, page.Bits.Take(2).ToArray());
    }

    [Fact]
    public void Tiff_CarriesEachPlateAsANamedNumberedGroup4Page()
    {
        byte[] pdf = PageWithHalftone(24, "/G0 gs 0.3 0 0 0.6 k 0 0 24 24 re f", Stream("/Type /Halftone /HalftoneType 6 /Width 2 /Height 2", new byte[] { 50, 100, 150, 200 }));
        var separations = PdfHalftoning.RenderSeparations(new MemoryStream(pdf), 0, 72);
        byte[] tiff = separations.ToTiff();
        var pages = TiffPages(tiff);

        Assert.Equal(separations.Plates.Count, pages.Count);
        for (int i = 0; i < pages.Count; i++)
        {
            var tags = pages[i];
            Assert.Equal(24u, tags[256]);
            Assert.Equal(24u, tags[257]);
            Assert.Equal(1u, tags[258]);
            Assert.Equal(4u, tags[259]);    // Group 4
            Assert.Equal(0u, tags[262]);    // WhiteIsZero: a 1 bit is ink
            Assert.Equal(2u, tags[296]);    // inch
            Assert.Equal((uint)i | ((uint)pages.Count << 16), tags[297]);
            Assert.Equal(separations.Plates[i].Name, TiffString(tiff, pages[i], 285));
        }
    }

    [Fact]
    public void Tiff_PastTheCcittBudget_IsPackBits()
    {
        byte[] pdf = PageWithHalftone(20, "/G0 gs 0.5 g 0 0 20 20 re f", Stream("/Type /Halftone /HalftoneType 6 /Width 3 /Height 1", new byte[] { 40, 140, 240 }));
        var page = PdfHalftoning.RenderPage(new MemoryStream(pdf), 0, 72);
        byte[] tiff;
        try
        {
            PdfLimits.MaxCcittColumns = 19;
            tiff = page.ToTiff();
        }
        finally { PdfLimits.ResetToDefaults(); }

        var tags = TiffPages(tiff).Single();
        Assert.Equal(32773u, tags[259]);
        Assert.False(tags.ContainsKey(293));
        // PackBits (TIFF 6.0 §9): n ≥ 0 copies n + 1 bytes, n in −127 … −1 repeats the next byte 1 − n times.
        var unpacked = new List<byte>();
        int at = (int)tags[273], end = at + (int)tags[279];
        while (at < end)
        {
            sbyte n = (sbyte)tiff[at++];
            if (n >= 0) { unpacked.AddRange(tiff.Skip(at).Take(n + 1)); at += n + 1; }
            else if (n != -128) { unpacked.AddRange(Enumerable.Repeat(tiff[at], 1 - n)); at++; }
        }
        Assert.Equal(page.Bits, unpacked.ToArray());
        Assert.Contains(page.Bits, b => b != 0);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A PDF of the given objects (1 the catalog, 2 the pages, 3 the page), its cross-reference table built.</summary>
    internal static byte[] Pdf(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>A stream object's text: its dictionary entries, then its bytes as Latin-1 (one character a byte).</summary>
    internal static string Stream(string entries, byte[] data)
        => $"<< {entries} /Length {data.Length} >>\nstream\n{Encoding.Latin1.GetString(data)}\nendstream";

    /// <summary>A page <paramref name="size"/> points square whose ExtGState <c>/G0</c> sets halftone object 5, painted by <paramref name="content"/>.</summary>
    internal static byte[] PageWithHalftone(int size, string content, string halftoneObject, params string[] more)
        => Pdf(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {size} {size}] /Resources << /ExtGState << /G0 << /HT 5 0 R >> >> >> /Contents 4 0 R >>",
            Stream("", Encoding.Latin1.GetBytes(content)),
            halftoneObject,
        }.Concat(more).ToArray());

    /// <summary>Vertical bands of grey across a page <paramref name="size"/> points square, each <paramref name="band"/> points wide, after <c>/G0 gs</c>.</summary>
    internal static string GreyBands(int size, int band, IEnumerable<double> greys)
    {
        var content = new StringBuilder("/G0 gs\n");
        int x = 0;
        foreach (double g in greys)
        {
            content.Append(FormattableString.Invariant($"{g:0.######} g {x} 0 {band} {size} re f\n"));
            x += band;
        }
        return content.ToString();
    }

    private static int Count(PdfBilevelImage page)
    {
        int n = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
                if (page.IsInk(x, y)) n++;
        return n;
    }

    /// <summary>The <see cref="RenderDiagnosticKind.HalftoneApproximated"/> reports <paramref name="action"/> makes on this thread.</summary>
    private static List<string> Reports(Action action)
    {
        var reports = new List<string>();
        int ownThread = Environment.CurrentManagedThreadId;
        void Sink(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId == ownThread && kind == RenderDiagnosticKind.HalftoneApproximated)
                lock (reports) reports.Add(detail ?? "");
        }
        RenderDiagnostics.OnEvent += Sink;
        try { action(); }
        finally { RenderDiagnostics.OnEvent -= Sink; }
        lock (reports) return new List<string>(reports);
    }

    /// <summary>Every IFD of a little-endian TIFF, read from the bytes: tag → its value field (a one-value tag's value, else its offset).</summary>
    private static List<Dictionary<int, uint>> TiffPages(byte[] tiff)
    {
        Assert.Equal(new byte[] { (byte)'I', (byte)'I', 42, 0 }, tiff.Take(4).ToArray());
        var pages = new List<Dictionary<int, uint>>();
        uint ifd = BitConverter.ToUInt32(tiff, 4);
        while (ifd != 0)
        {
            int count = BitConverter.ToUInt16(tiff, (int)ifd);
            var tags = new Dictionary<int, uint>();
            int previous = -1;
            for (int i = 0; i < count; i++)
            {
                int at = (int)ifd + 2 + 12 * i;
                int tag = BitConverter.ToUInt16(tiff, at);
                Assert.True(tag > previous, "TIFF 6.0 §2: an IFD's entries ascend by tag");
                previous = tag;
                int type = BitConverter.ToUInt16(tiff, at + 2);
                uint n = BitConverter.ToUInt32(tiff, at + 4);
                tags[tag] = type == 3 && n == 1 ? BitConverter.ToUInt16(tiff, at + 8) : BitConverter.ToUInt32(tiff, at + 8);
                tags[-tag] = n;   // the count, under the tag's negation
            }
            pages.Add(tags);
            ifd = BitConverter.ToUInt32(tiff, (int)ifd + 2 + 12 * count);
        }
        return pages;
    }

    /// <summary>An ASCII tag's text (TIFF 6.0 §2: in the value field when four bytes or fewer), without its terminating NUL.</summary>
    private static string TiffString(byte[] tiff, Dictionary<int, uint> tags, int tag)
    {
        int count = (int)tags[-tag];
        byte[] bytes = count <= 4 ? BitConverter.GetBytes(tags[tag]).Take(count).ToArray() : tiff.Skip((int)tags[tag]).Take(count).ToArray();
        Assert.Equal(0, bytes[count - 1]);
        return Encoding.ASCII.GetString(bytes, 0, count - 1);
    }
}
