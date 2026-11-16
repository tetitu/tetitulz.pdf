// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The decisions the <c>Base/Rendering</c> sweep's batch 6 (<c>.claude/sweep-rendering-6.ps1</c>) found
/// no test for.
/// </summary>
public class RenderingBatch6DecisionTests
{
    /// <summary>
    /// A <see cref="RenderedPage"/> refuses a buffer shorter than its dimensions describe — for that
    /// reason, naming <c>pixels</c> — and accepts the shortest one that holds them: every row at the
    /// stride but the last, which needs only its pixels.
    /// </summary>
    [Fact]
    public void ARenderedPageRefusesAPixelBufferTooShortForItsRows()
    {
        // 3 × 2 at a 16-byte stride: 16 + 12 = 28 bytes is the minimum.
        var ok = new RenderedPage(3, 2, 16, new byte[28]);
        Assert.Equal(16, ok.Stride);

        var ex = Assert.Throws<ArgumentException>(() => new RenderedPage(3, 2, 16, new byte[27]));
        Assert.Equal("pixels", ex.ParamName);
    }

    /// <summary>
    /// The stride check is made in <c>long</c>: at 600 million pixels <c>width * 4</c> overflows
    /// <c>int</c> to a negative number, which any stride passes.
    /// </summary>
    [Fact]
    public void ARenderedPageChecksTheStrideWithoutOverflow()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new RenderedPage(600_000_000, 1, 0, new byte[4]));
        Assert.Equal("stride", ex.ParamName);
    }
}
