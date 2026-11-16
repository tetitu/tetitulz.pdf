// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="TrueTypeSubsetter"/> (the composite-font embedder's subsetter) keeps and renumbers a
/// composite glyph's component, and reads no component past the glyph's end.
/// </summary>
public class TrueTypeSubsetterCompositeTests
{
    [Fact]
    public void TheComponentIsKeptAndRenumbered()
    {
        var (subset, oldToNew) = TrueTypeSubsetter.Subset(
            CompositeTrueTypeFont.Build(malformed: false), new ushort[] { CompositeTrueTypeFont.Composite });

        Assert.Equal(new[] { 0, 3, 4 }, oldToNew.Keys.OrderBy(k => k));
        Assert.Equal(oldToNew[CompositeTrueTypeFont.Component],
            CompositeTrueTypeFont.ComponentOf(subset, oldToNew[CompositeTrueTypeFont.Composite]));
    }

    /// <summary>
    /// The malformed composite keeps <c>MORE_COMPONENTS</c> set; the bytes after it name glyph 5, which
    /// the walk added to the subset until it stopped at the glyph's end.
    /// </summary>
    [Fact]
    public void AMalformedCompositeAddsNoGlyphFromThePastItsEnd()
    {
        var (_, oldToNew) = TrueTypeSubsetter.Subset(
            CompositeTrueTypeFont.Build(malformed: true), new ushort[] { CompositeTrueTypeFont.Composite });

        Assert.Equal(new[] { 0, 3, 4 }, oldToNew.Keys.OrderBy(k => k));
    }
}
