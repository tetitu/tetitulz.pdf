// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Fonts;

/// <summary>
/// <see cref="PdfFontSubsetter.Subset(byte[], string)"/> on a composite glyph: its component comes
/// along, is renumbered, and nothing past the glyph's end is read as a component.
/// </summary>
/// <remarks>
/// The subsetter's composite handling had no test: the shared test fonts carried no composite glyph.
/// <see cref="CompositeTrueTypeFont"/> has one, and a malformed variant whose record keeps
/// <c>MORE_COMPONENTS</c> set so that the next glyph's bytes name glyph 5. The walk read on into them
/// until it was bounded by the glyph's end (<c>Core.Fonts.CompositeGlyph</c>).
/// </remarks>
public class SubsetCompositeGlyphTests
{
    [Fact]
    public void ACompositesComponentIsKeptAndRenumbered()
    {
        byte[] subset = PdfFontSubsetter.Subset(CompositeTrueTypeFont.Build(malformed: false), "A");

        // Glyphs 0, 3 and 4 survive, as 0, 1 and 2; the composite (new 1) names its component (new 2).
        Assert.Equal(3, CompositeTrueTypeFont.NumGlyphs(subset));
        Assert.Equal(2, CompositeTrueTypeFont.ComponentOf(subset, 1));
        Assert.True(CompositeTrueTypeFont.GlyphLength(subset, 2) > 0, "the component's outline was dropped");
    }

    [Fact]
    public void AMalformedCompositeDoesNotPullInTheNextGlyphsBytes()
    {
        byte[] subset = PdfFontSubsetter.Subset(CompositeTrueTypeFont.Build(malformed: true), "A");

        Assert.Equal(3, CompositeTrueTypeFont.NumGlyphs(subset));
        Assert.Equal(2, CompositeTrueTypeFont.ComponentOf(subset, 1));
    }
}
