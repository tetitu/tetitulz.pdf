// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Writing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Writing;

/// <summary>
/// <see cref="TransformMatrix"/> (batch 104): PDF's row-vector composition, a matrix taken about a
/// point, and the bounds of a rectangle's turned corners — the arithmetic a producer's
/// <c>transform-origin</c> and the exporter's annotation remapping both rest on.
/// </summary>
public class TransformMatrixTests
{
    private static readonly double[] Scale2 = { 2, 0, 0, 2, 0, 0 };
    /// <summary>A quarter turn anticlockwise: (x, y) → (−y, x).</summary>
    private static readonly double[] Quarter = { 0, 1, -1, 0, 0, 0 };

    /// <summary><c>Multiply(m, n)</c> applies m first: a translation then a scale scales the translation; the other way round it does not.</summary>
    [Fact]
    public void Multiply_AppliesTheLeftFactorFirst()
    {
        double[] translateThenScale = TransformMatrix.Multiply(TransformMatrix.Translation(10, 0), Scale2);
        double[] scaleThenTranslate = TransformMatrix.Multiply(Scale2, TransformMatrix.Translation(10, 0));

        Assert.Equal((20, 0), TransformMatrix.Apply(translateThenScale, 0, 0));
        Assert.Equal((10, 0), TransformMatrix.Apply(scaleThenTranslate, 0, 0));
    }

    /// <summary>A matrix about a point leaves that point where it is and turns the rest round it: 10 to the right of the origin becomes 10 above it.</summary>
    [Fact]
    public void About_FixesThePoint_AndTurnsRoundIt()
    {
        double[] about = TransformMatrix.About(Quarter, 30, 40);

        var fixedPoint = TransformMatrix.Apply(about, 30, 40);
        var turned = TransformMatrix.Apply(about, 40, 40);
        Assert.Equal(30, fixedPoint.X, 9); Assert.Equal(40, fixedPoint.Y, 9);
        Assert.Equal(30, turned.X, 9); Assert.Equal(50, turned.Y, 9);
        // The control: about the origin the same point goes to (−40, 40).
        var atOrigin = TransformMatrix.Apply(Quarter, 40, 40);
        Assert.Equal(-40, atOrigin.X, 9); Assert.Equal(40, atOrigin.Y, 9);
    }

    /// <summary>A scale about a point grows from it: the point 10 right of the origin is 20 right after <c>scale(2)</c>, wherever the origin is.</summary>
    [Fact]
    public void About_AScaleGrowsFromThePoint()
    {
        double[] about = TransformMatrix.About(Scale2, 100, 200);

        var p = TransformMatrix.Apply(about, 110, 200);
        Assert.Equal(120, p.X, 9); Assert.Equal(200, p.Y, 9);
        Assert.Equal(-100, about[4], 9); Assert.Equal(-200, about[5], 9);
    }

    /// <summary>A translation is the same about any point.</summary>
    [Fact]
    public void About_LeavesATranslationAlone()
    {
        double[] about = TransformMatrix.About(TransformMatrix.Translation(5, 7), 100, 200);

        Assert.Equal(TransformMatrix.Translation(5, 7), about);
    }

    /// <summary>The bounds of a turned rectangle: 10 wide and 20 tall, a quarter turn about the origin, is 20 wide and 10 tall to the left of the axis.</summary>
    [Fact]
    public void BoundsOf_HoldsTheTurnedCorners()
    {
        var r = TransformMatrix.BoundsOf(Quarter, 0, 0, 10, 20);

        Assert.Equal(-20, r.X0, 9); Assert.Equal(0, r.Y0, 9);
        Assert.Equal(0, r.X1, 9); Assert.Equal(10, r.Y1, 9);
        // The control: a translation moves the rectangle whole.
        var moved = TransformMatrix.BoundsOf(TransformMatrix.Translation(3, 4), 0, 0, 10, 20);
        Assert.Equal((3, 4, 13, 24), moved);
    }
}
