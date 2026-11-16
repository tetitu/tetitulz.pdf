// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The give-way rule: margins stand while they leave at least <see cref="PageContentBox.MinContentPt"/>
/// of content on an axis, and drop to zero when they would leave less.
/// </summary>
public class PageContentBoxTests
{
    [Fact]
    public void MarginsLeavingAmpleRoom_Stand()
    {
        var (near, far) = PageContentBox.Fit(extent: 792, near: 72, far: 72, out bool gaveWay);
        Assert.False(gaveWay);
        Assert.Equal(72, near);
        Assert.Equal(72, far);
    }

    [Fact]
    public void MarginsThatSwallowThePage_GiveWayToZero()
    {
        var (near, far) = PageContentBox.Fit(extent: 792, near: 400, far: 400, out bool gaveWay);
        Assert.True(gaveWay);
        Assert.Equal(0, near);
        Assert.Equal(0, far);
    }

    [Fact]
    public void ExactlyTheFloor_Stands_JustUnderIt_GivesWay()
    {
        // extent - near - far == MinContentPt: enough, so the margins stand (the control for the
        // boundary). One point tighter leaves less than the floor, so they give way — a single test
        // pinning the comparison is at (>=), not (>).
        double extent = 100, min = PageContentBox.MinContentPt;

        var (n1, f1) = PageContentBox.Fit(extent, near: (extent - min) / 2, far: (extent - min) / 2, out bool atFloor);
        Assert.False(atFloor);
        Assert.Equal((extent - min) / 2, n1);
        Assert.Equal((extent - min) / 2, f1);

        var (n2, f2) = PageContentBox.Fit(extent, near: (extent - min) / 2 + 0.5, far: (extent - min) / 2 + 0.5, out bool justUnder);
        Assert.True(justUnder);
        Assert.Equal(0, n2);
        Assert.Equal(0, f2);
    }
}
