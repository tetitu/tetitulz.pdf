// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using Tetitulz.Pdf.Plugins.PostScript.Emit;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The Adobe-Japan1 CMaps named with no collection marker are Japanese. H, V, 78-H, 78-V and the
/// kana and symbol sets fell through the marker table to Identity, so the composite font declared
/// Identity and a reader took the shown codes as glyph indices.
/// </summary>
public sealed class CidCollectionNameTests
{
    [Theory]
    [InlineData("H")]
    [InlineData("V")]
    [InlineData("78-H")]
    [InlineData("78-V")]
    [InlineData("Hankaku")]
    [InlineData("Hiragana")]
    [InlineData("Katakana")]
    [InlineData("Roman")]
    [InlineData("WP-Symbol")]
    public void AnUnmarkedJapan1CMap_IsJapan1(string cmap)
        => Assert.Equal(("Adobe", "Japan1", 7), PsCidFonts.CollectionFor(cmap));

    /// <summary>
    /// Identity-H is a CMap though no file holds it: a font named for it is split there, not at its
    /// trailing H, which is a JIS X 0208 encoding and now — rightly — Japan1.
    /// </summary>
    [Theory]
    [InlineData("UniFace-Identity-H", "UniFace", "Identity-H")]
    [InlineData("Some-Face-Identity-V", "Some-Face", "Identity-V")]
    public void AFontNamedForAnIdentityCMap_IsSplitAtIt(string font, string face, string cmap)
    {
        Assert.True(PsCidFonts.TrySplitCompositeName(font, out string f, out string c));
        Assert.Equal(face, f);
        Assert.Equal(cmap, c);
    }

    /// <summary>The controls: a marked name keeps its collection, and an unknown one stays Identity — the match is whole.</summary>
    [Theory]
    [InlineData("UniCNS-UCS2-H", "CNS1")]
    [InlineData("UniJIS-UTF16-H", "Japan1")]
    [InlineData("Identity-H", "Identity")]
    [InlineData("Foo-H", "Identity")]
    [InlineData("HV", "Identity")]
    public void OtherNames_KeepTheirCollection(string cmap, string ordering)
        => Assert.Equal(ordering, PsCidFonts.CollectionFor(cmap).Ordering);
}
