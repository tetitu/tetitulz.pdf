// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Imaging;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Imaging;

/// <summary>
/// <see cref="RasterImageInfo.ExifOrientation"/>: tag 274 of a JPEG's APP1 Exif segment, read by
/// the one JPEG marker walk.
/// </summary>
/// <remarks>
/// A phone stores an upright photo sideways and records orientation 6; a browser turns it by
/// default. Every offset in the segment is the file's to choose, so the malformed cases are here
/// as much as the eight values.
/// </remarks>
public class ExifOrientationTests
{
    [Theory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)] [InlineData(4, false)]
    [InlineData(5, true)] [InlineData(6, true)] [InlineData(7, true)] [InlineData(8, true)]
    public void EachOrientationIsRead_InEitherByteOrder(int orientation, bool swaps)
    {
        foreach (bool little in new[] { false, true })
        {
            Assert.True(RasterImageInspector.TryInspect(ExifJpeg.Build(40, 30, orientation, little), out var info));
            Assert.Equal(orientation, info.ExifOrientation);
            Assert.Equal(swaps, info.ExifSwapsAxes);
            // The stored size is reported as stored; turning it is the caller's.
            Assert.Equal(40, info.Width);
            Assert.Equal(30, info.Height);
        }
    }

    [Fact]
    public void NoExifSegmentIsOrientationOne()
    {
        Assert.True(RasterImageInspector.TryInspect(ExifJpeg.Build(40, 30, 6, exif: false), out var info));
        Assert.Equal(1, info.ExifOrientation);
        Assert.False(info.ExifSwapsAxes);
    }

    /// <summary>A value outside 1–8, or the tag stored as another type, is no orientation.</summary>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(9, 3)]
    [InlineData(6, 4)]   // LONG, not SHORT
    public void AnInvalidValueIsOrientationOne(int value, int type)
    {
        Assert.True(RasterImageInspector.TryInspect(ExifJpeg.Build(40, 30, value, valueType: type), out var info));
        Assert.Equal(1, info.ExifOrientation);
    }

    /// <summary>A segment whose TIFF header is not one (the magic is not 42) states no orientation.</summary>
    [Fact]
    public void ANonTiffHeaderIsOrientationOne()
    {
        Assert.True(RasterImageInspector.TryInspect(ExifJpeg.Build(40, 30, 6, magic: 43), out var info));
        Assert.Equal(1, info.ExifOrientation);
    }

    /// <summary>
    /// An Exif segment cut off by the end of the file — its header and nothing after it. The walk
    /// reads no byte past the file, and reports no image, since there is no frame.
    /// </summary>
    [Fact]
    public void AnExifSegmentCutOffByTheEndOfTheFileIsNotReadPastIt()
    {
        byte[] truncated = { 0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x08, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00 };

        Assert.False(RasterImageInspector.TryInspect(truncated, out _));
    }

    /// <summary>
    /// Hostile offsets: a directory past the segment's end, one inside the file but past the
    /// segment, and an entry count far beyond what the segment holds — with the one real entry
    /// unusable, so the walk goes on looking until the segment ends. None throws, and the frame is
    /// still read.
    /// </summary>
    [Theory]
    [InlineData(0xFFFFFFF0u, 1, 3)]
    [InlineData(200u, 1, 3)]
    [InlineData(8u, 65535, 4)]
    public void AHostileDirectoryIsOrientationOne_AndTheFrameIsStillRead(uint ifdOffset, int entries, int type)
    {
        Assert.True(RasterImageInspector.TryInspect(
            ExifJpeg.Build(40, 30, 6, ifdOffset: ifdOffset, entryCount: entries, valueType: type), out var info));

        Assert.Equal(1, info.ExifOrientation);
        Assert.Equal(40, info.Width);
        Assert.Equal(30, info.Height);
    }
}
