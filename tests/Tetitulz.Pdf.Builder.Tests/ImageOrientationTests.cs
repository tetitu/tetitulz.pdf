// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="ImageRun.Orientation"/>: the image is drawn turned or mirrored into its box, by one
/// <c>cm</c> that sends the stored image's corners where the orientation puts them.
/// </summary>
/// <remarks>
/// Read back as the six numbers of the image's <c>cm</c>, relative to where an unturned image of the
/// same box is placed. The box is 30 × 40pt — the shown shape — so a matrix that confuses width
/// with height, or a corner with its neighbour, lands on a different number.
/// </remarks>
public class ImageOrientationTests
{
    private const double W = 30, H = 40;

    private static double[] Placement(ImageOrientation orientation)
    {
        var p = new Paragraph();
        p.Runs.Add(new ImageRun
        {
            Data = ExifJpeg.Build(8, 8, 1), WidthPt = W, HeightPt = H, Orientation = orientation,
        });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());

        var m = Regex.Match(content,
            @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+cm\s*/\w+\s+Do\b");
        Assert.True(m.Success, "no image was drawn");
        return Enumerable.Range(1, 6).Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
    }

    [Theory]
    [InlineData(ImageOrientation.Normal, W, 0, 0, H, 0, 0)]
    [InlineData(ImageOrientation.FlipHorizontal, -W, 0, 0, H, W, 0)]
    [InlineData(ImageOrientation.Rotate180, -W, 0, 0, -H, W, H)]
    [InlineData(ImageOrientation.FlipVertical, W, 0, 0, -H, 0, H)]
    [InlineData(ImageOrientation.Transpose, 0, -H, -W, 0, W, H)]
    [InlineData(ImageOrientation.Rotate90Clockwise, 0, -H, W, 0, 0, H)]
    [InlineData(ImageOrientation.Transverse, 0, H, W, 0, 0, 0)]
    [InlineData(ImageOrientation.Rotate90Anticlockwise, 0, H, -W, 0, W, 0)]
    public void EachOrientationMapsTheImageIntoItsBox(ImageOrientation orientation,
        double a, double b, double c, double d, double e, double f)
    {
        double[] normal = Placement(ImageOrientation.Normal);
        double x = normal[4], y = normal[5];

        double[] m = Placement(orientation);

        Assert.Equal(new[] { a, b, c, d, x + e, y + f }, m, new Tolerance());
    }

    /// <summary>
    /// The stored top-left corner lands where Exif says it is shown: for a quarter turn clockwise
    /// (6), at the box's top right — checked by applying the matrix, not by reading it.
    /// </summary>
    [Fact]
    public void AQuarterTurnClockwisePutsTheStoredTopLeftAtTheTopRight()
    {
        double[] normal = Placement(ImageOrientation.Normal);
        double left = normal[4], bottom = normal[5];
        double[] m = Placement(ImageOrientation.Rotate90Clockwise);

        // The image's top-left is unit-square (0, 1).
        double px = m[0] * 0 + m[2] * 1 + m[4], py = m[1] * 0 + m[3] * 1 + m[5];

        Assert.Equal(left + W, px, 2);
        Assert.Equal(bottom + H, py, 2);
    }

    private sealed class Tolerance : System.Collections.Generic.IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => System.Math.Abs(x - y) < 0.01;
        public int GetHashCode(double obj) => 0;
    }
}
