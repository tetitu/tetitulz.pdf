// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BoxStart.IndexOfEnd"/> and <see cref="BoxEnd.IndexOfStart"/> (2026-10-19): the one walk every reader of a
/// list of blocks finds a box's other edge by — the boxes nested inside counted, both ways — which had three copies.
/// </summary>
public class BoxMatchingTests
{
    // 0 outer start, 1 paragraph, 2 inner start, 3 paragraph, 4 inner end, 5 paragraph, 6 outer end, 7 paragraph
    private static readonly IDocElement[] Nested =
    {
        new BoxStart(), new Paragraph(), new BoxStart(), new Paragraph(), new BoxEnd(), new Paragraph(), new BoxEnd(), new Paragraph(),
    };

    [Fact]
    public void ABoxsEnd_IsFoundPastTheBoxesInside()
    {
        Assert.Equal(6, BoxStart.IndexOfEnd(Nested, 0));
        Assert.Equal(4, BoxStart.IndexOfEnd(Nested, 2));
        // None before the bound: the bound, or the list's count by default.
        Assert.Equal(5, BoxStart.IndexOfEnd(Nested, 0, 5));
        Assert.Equal(3, BoxStart.IndexOfEnd(new IDocElement[] { new BoxStart(), new Paragraph(), new Paragraph() }, 0));
    }

    [Fact]
    public void ABoxsStart_IsFoundPastTheBoxesInside()
    {
        Assert.Equal(0, BoxEnd.IndexOfStart(Nested, 6));
        Assert.Equal(2, BoxEnd.IndexOfStart(Nested, 4));
        Assert.Equal(-1, BoxEnd.IndexOfStart(new IDocElement[] { new Paragraph(), new BoxEnd() }, 1));
    }
}
