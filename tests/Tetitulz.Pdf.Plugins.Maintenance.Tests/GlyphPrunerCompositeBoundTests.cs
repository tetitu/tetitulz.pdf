// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.Maintenance;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="TrueTypeGlyphPruner"/> keeps a drawn composite's component and blanks the glyph a walk
/// past the composite's end would name. Its walk was the one of five that stopped at the glyph's end,
/// and it now shares <c>Core.Fonts.CompositeGlyph</c> with the other four; this pins that it still does.
/// </summary>
public class GlyphPrunerCompositeBoundTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheComponentIsKeptAndNothingPastTheCompositeIs(bool malformed)
    {
        byte[] pruned = TrueTypeGlyphPruner.Prune(CompositeTrueTypeFont.Build(malformed),
            new HashSet<int> { CompositeTrueTypeFont.Composite }, out _);

        Assert.True(CompositeTrueTypeFont.GlyphLength(pruned, CompositeTrueTypeFont.Component) > 0);
        Assert.Equal(0, CompositeTrueTypeFont.GlyphLength(pruned, CompositeTrueTypeFont.PastTheEnd));
    }
}
