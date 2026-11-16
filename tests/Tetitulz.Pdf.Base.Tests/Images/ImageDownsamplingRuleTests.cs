// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Images;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Images;

/// <summary>
/// <see cref="ImageDownsampling"/>: the one policy for how far below its native size an image may
/// be decoded for a device target — the headroom, the floor, and the halvings a JPEG 2000 decode
/// may stop short by.
/// </summary>
public sealed class ImageDownsamplingRuleTests
{
    [Fact]
    public void Wanted_IsTheTargetWithHeadroom_NeverBelowTheFloor()
    {
        Assert.Equal(2f, ImageDownsampling.Headroom);
        Assert.Equal(256, ImageDownsampling.MinDimension);
        Assert.Equal(256f, ImageDownsampling.Wanted(10));    // the floor
        Assert.Equal(256f, ImageDownsampling.Wanted(128));   // exactly at it
        Assert.Equal(400f, ImageDownsampling.Wanted(200));   // 2x above it
    }

    [Theory]
    [InlineData(256, 1024, false)]   // at the floor on one side
    [InlineData(1024, 256, false)]
    [InlineData(257, 257, true)]
    public void IsReducible_NeedsBothSidesAboveTheFloor(long width, long height, bool reducible) =>
        Assert.Equal(reducible, ImageDownsampling.IsReducible(width, height));

    /// <summary>
    /// Each halving is ⌈· / 2⌉ a side, as a resolution level is (T.800 B-14), and stops when the
    /// next would fall below the wanted extent on either side.
    /// </summary>
    [Theory]
    [InlineData(1024, 1024, 32, 32, 2)]     // 512, 256 keep 256; 128 would not
    [InlineData(512, 512, 32, 32, 1)]
    [InlineData(1024, 1024, 200, 200, 1)]   // wanted 400: 512 keeps it, 256 would not
    [InlineData(1024, 1024, 300, 300, 0)]   // wanted 600: 512 already falls short
    [InlineData(4096, 300, 32, 32, 0)]      // the short side: 150 would fall below the floor
    [InlineData(4096, 513, 32, 32, 1)]      // 257 keeps the floor; 129 would not
    [InlineData(1025, 1025, 32, 32, 2)]     // the ceilings: 513, 257 — not 512, 256
    [InlineData(511, 511, 1, 1, 1)]         // ⌈511 / 2⌉ = 256 keeps the floor; ⌊511 / 2⌋ would not
    [InlineData(256, 256, 1, 1, 0)]         // not reducible
    [InlineData(1024, 1024, 0, 32, 0)]      // no target
    [InlineData(1024, 1024, 32, 0, 0)]
    public void HalvingsFor_StopsBeforeTheWantedExtentIsLost(long width, long height, int targetWidth, int targetHeight, int halvings) =>
        Assert.Equal(halvings, ImageDownsampling.HalvingsFor(width, height, targetWidth, targetHeight));

    [Fact]
    public void HalvingsFor_IsBoundedForAnAbsurdSource()
    {
        // A source larger than any real image with a one-pixel target: the count is finite and
        // never past 32, the most levels a codestream can have.
        Assert.Equal(32, ImageDownsampling.HalvingsFor(long.MaxValue / 4, long.MaxValue / 4, 1, 1));
    }
}
