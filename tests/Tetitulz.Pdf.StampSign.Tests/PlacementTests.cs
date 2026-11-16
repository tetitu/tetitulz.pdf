// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using Tetitulz.Pdf.StampSign;

namespace Tetitulz.Pdf.StampSign.Tests;

/// <summary>
/// Page geometry as the user sees it, turned into the page's own coordinates. Every expected value is
/// worked by hand from the page as shown: under <c>/Rotate 90</c> the page turns clockwise, so its
/// left edge is the top, its bottom-left corner the top-left, a step right on screen is +y and a step
/// down is +x.
/// </summary>
public sealed class PlacementTests
{
    private static readonly RectangleF A4 = new(0, 0, 595, 842);

    private const float Tolerance = 0.01f;

    [Theory]
    [InlineData(0, 10, 20, 10, 822)]
    [InlineData(90, 10, 20, 20, 10)]
    [InlineData(180, 10, 20, 585, 20)]
    [InlineData(270, 10, 20, 575, 832)]
    public void A_shown_point_lands_on_the_page_point_under_it(int rotation, double u, double v, float x, float y)
    {
        PointF p = Placement.DisplayToPdf(A4, rotation, u, v);
        Assert.InRange(p.X, x - Tolerance, x + Tolerance);
        Assert.InRange(p.Y, y - Tolerance, y + Tolerance);
    }

    [Theory]
    [InlineData(0, 595, 842)]
    [InlineData(90, 842, 595)]
    [InlineData(180, 595, 842)]
    [InlineData(270, 842, 595)]
    public void A_quarter_turn_swaps_the_shown_size(int rotation, float width, float height)
    {
        SizeF s = Placement.DisplaySize(A4, rotation);
        Assert.Equal(width, s.Width);
        Assert.Equal(height, s.Height);
    }

    [Fact]
    public void The_signature_sits_its_offsets_from_the_bottom_right_corner()
    {
        // 50 x 20 mm, 15 mm in from the right and from the bottom: 141.73 x 56.69 pt, 42.52 pt in.
        var settings = new AppSettings { SignatureWidthMm = 50, SignatureHeightMm = 20, OffsetXMm = 15, OffsetYMm = 15, Anchor = SignatureAnchor.BottomRight };
        RectangleF r = Placement.SignatureRect(settings, A4, 0);
        Assert.InRange(r.X, 410.75f - Tolerance, 410.75f + Tolerance);
        Assert.InRange(r.Y, 42.52f - Tolerance, 42.52f + Tolerance);
        Assert.InRange(r.Width, 141.73f - Tolerance, 141.73f + Tolerance);
        Assert.InRange(r.Height, 56.69f - Tolerance, 56.69f + Tolerance);
    }

    [Fact]
    public void On_a_turned_page_the_top_left_signature_runs_up_the_pages_left_edge()
    {
        // Shown top-left on a page turned clockwise: near the page's own bottom-left corner, its
        // shown width along the page's y.
        var settings = new AppSettings { SignatureWidthMm = 50, SignatureHeightMm = 20, OffsetXMm = 15, OffsetYMm = 15, Anchor = SignatureAnchor.TopLeft };
        RectangleF r = Placement.SignatureRect(settings, A4, 90);
        Assert.InRange(r.X, 42.52f - Tolerance, 42.52f + Tolerance);
        Assert.InRange(r.Y, 42.52f - Tolerance, 42.52f + Tolerance);
        Assert.InRange(r.Width, 56.69f - Tolerance, 56.69f + Tolerance);
        Assert.InRange(r.Height, 141.73f - Tolerance, 141.73f + Tolerance);
    }

    [Theory]
    [InlineData(SignatureAnchor.TopLeft, 42.52f, 742.79f)]
    [InlineData(SignatureAnchor.TopRight, 410.75f, 742.79f)]
    [InlineData(SignatureAnchor.BottomLeft, 42.52f, 42.52f)]
    public void Each_anchor_measures_from_its_own_corner(SignatureAnchor anchor, float x, float y)
    {
        var settings = new AppSettings { SignatureWidthMm = 50, SignatureHeightMm = 20, OffsetXMm = 15, OffsetYMm = 15, Anchor = anchor };
        RectangleF r = Placement.SignatureRect(settings, A4, 0);
        Assert.InRange(r.X, x - Tolerance, x + Tolerance);
        Assert.InRange(r.Y, y - Tolerance, y + Tolerance);
    }
}
