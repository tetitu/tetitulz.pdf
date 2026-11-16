// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Decoded-pixel helpers for rendering tests.
///
/// <para><b>Why these exist.</b> CLAUDE.md banned test pattern 3: a render test that asserts only
/// <c>Assert.NotNull(png); Assert.NotEmpty(png);</c> passes with the feature under test deleted,
/// because a blank page encodes to a perfectly valid non-empty PNG. Two rendering defects shipped
/// completely non-functional behind six tests of that shape. Assert on what came out of the
/// renderer instead: a colour at a point, ink inside a rect and not outside it, or a difference
/// against a second render.</para>
///
/// <para>Shared rather than copied per file — these had already been written twice.</para>
/// </summary>
internal static class PixelAssert
{
    /// <summary>1 pt = 1 px, so PDF user space and device pixels line up.</summary>
    public const int Dpi = 72;

    public static SKBitmap Render(byte[] pdf, int dpi = Dpi)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: dpi);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>PDF user-space X → device column.</summary>
    public static int X(float userX, int dpi = Dpi) => (int)(userX * dpi / 72f);

    /// <summary>PDF user-space Y (origin bottom-left) → device row (origin top-left).</summary>
    public static int Y(SKBitmap bmp, float userY, int dpi = Dpi) =>
        bmp.Height - 1 - (int)(userY * dpi / 72f);

    /// <summary>The pixel at a PDF user-space point.</summary>
    public static SKColor At(SKBitmap bmp, float userX, float userY, int dpi = Dpi) =>
        bmp.GetPixel(X(userX, dpi), Y(bmp, userY, dpi));

    public static bool IsInk(SKColor c) =>
        c.Alpha > 8 && (c.Red < 240 || c.Green < 240 || c.Blue < 240);

    public static bool IsWhite(SKColor c) => !IsInk(c);

    public static int CountInk(SKBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (IsInk(bmp.GetPixel(x, y))) n++;
        return n;
    }

    public static bool IsBlank(SKBitmap bmp) => CountInk(bmp) == 0;

    /// <summary>Counts pixels matching a colour within <paramref name="tolerance"/> per channel.</summary>
    public static int CountColor(SKBitmap bmp, SKColor want, int tolerance = 24)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (Near(bmp.GetPixel(x, y), want, tolerance)) n++;
        return n;
    }

    public static bool Near(SKColor a, SKColor b, int tolerance = 24) =>
        System.Math.Abs(a.Red   - b.Red)   <= tolerance &&
        System.Math.Abs(a.Green - b.Green) <= tolerance &&
        System.Math.Abs(a.Blue  - b.Blue)  <= tolerance;

    /// <summary>The darkest luminance present, 0 (black) to 255 (white). 255 on a blank page.</summary>
    public static int DarkestLuminance(SKBitmap bmp)
    {
        int darkest = 255;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.Alpha <= 8) continue;
                int lum = (c.Red * 30 + c.Green * 59 + c.Blue * 11) / 100;
                if (lum < darkest) darkest = lum;
            }
        return darkest;
    }

    /// <summary>Distinct grey levels along a horizontal scanline — how many steps an edge has.</summary>
    public static int DistinctLevelsOnRow(SKBitmap bmp, int row)
    {
        var seen = new HashSet<int>();
        for (int x = 0; x < bmp.Width; x++)
        {
            var c = bmp.GetPixel(x, row);
            if (c.Alpha <= 8) continue;
            seen.Add((c.Red * 30 + c.Green * 59 + c.Blue * 11) / 100);
        }
        return seen.Count;
    }

    /// <summary>Bounding box of the inked pixels, or null when the page is blank.</summary>
    public static (int left, int top, int right, int bottom)? InkBounds(SKBitmap bmp)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                if (!IsInk(bmp.GetPixel(x, y))) continue;
                if (x < l) l = x;
                if (x > r) r = x;
                if (y < t) t = y;
                if (y > b) b = y;
            }
        return r < 0 ? null : (l, t, r, b);
    }
}
