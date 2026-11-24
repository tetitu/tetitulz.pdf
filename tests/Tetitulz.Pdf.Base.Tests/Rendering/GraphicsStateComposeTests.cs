// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Tests for <see cref="GraphicsState.Compose"/> / <see cref="GraphicsState.Transform"/>,
/// the single matrix-composition implementation shared by the interpreter, the SVG
/// renderer, and the graphics extractor.
/// </summary>
public sealed class GraphicsStateComposeTests
{
    private static readonly float[] Identity = { 1, 0, 0, 1, 0, 0 };

    [Fact]
    public void Compose_WithIdentity_IsNoOp()
    {
        float[] m = { 2, 0, 0, 3, 5, 7 };
        Assert.Equal(m, GraphicsState.Compose(Identity, m));
        Assert.Equal(m, GraphicsState.Compose(m, Identity));
    }

    [Fact]
    public void Compose_AppliesAThenB()
    {
        // A = scale(2,3); B = translate(10,20). Compose(A,B) applies A first, then B.
        float[] a = { 2, 0, 0, 3, 0, 0 };
        float[] b = { 1, 0, 0, 1, 10, 20 };
        float[] ab = GraphicsState.Compose(a, b);
        // Point (1,1): scale → (2,3); translate → (12,23).
        var (x, y) = GraphicsState.Transform(ab, 1, 1);
        Assert.Equal(12f, x, 3);
        Assert.Equal(23f, y, 3);
    }

    [Fact]
    public void Compose_IsAssociative()
    {
        float[] a = { 1, 2, 3, 4, 5, 6 };
        float[] b = { 2, 0, 1, 2, 1, 1 };
        float[] c = { 0, 1, 1, 0, 2, 3 };
        float[] left  = GraphicsState.Compose(GraphicsState.Compose(a, b), c);
        float[] right = GraphicsState.Compose(a, GraphicsState.Compose(b, c));
        for (int i = 0; i < 6; i++)
            Assert.Equal(left[i], right[i], 3);
    }
}
