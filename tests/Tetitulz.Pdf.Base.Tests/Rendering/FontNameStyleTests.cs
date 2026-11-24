// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// What a font's name says about weight and slant has one reading, <see cref="FontNameStyle"/>, and
/// three sites that act on it agree: the standard-14 face whose widths are used
/// (<see cref="Standard14Names.Canonical"/>), the face the raster renderer asks the platform for
/// (<c>FontCache.StyleNamedBy</c>) and the SVG writer's CSS weight and slant
/// (<c>SvgFontFace.Describe</c>).
/// </summary>
/// <remarks>
/// Were the sites to disagree on <c>Heavy</c> and <c>Black</c>, <c>Arial,Black</c> would be measured
/// as Helvetica-Bold and drawn as regular Arial — the rows marked below. The assertions are about
/// the style chosen, not a typeface resolved, so the
/// host's installed fonts cannot answer for the code.
/// </remarks>
public class FontNameStyleTests
{
    [Theory]
    [InlineData("Arial",                      false, false, StandardFonts.Helvetica)]
    [InlineData("Arial,Bold",                 true,  false, StandardFonts.HelveticaBold)]
    [InlineData("Arial,Black",                true,  false, StandardFonts.HelveticaBold)]   // the disagreement
    [InlineData("Helvetica-Heavy",            true,  false, StandardFonts.HelveticaBold)]   // the disagreement
    [InlineData("ArialMT,italic",             false, true,  StandardFonts.HelveticaOblique)]
    [InlineData("Helvetica-BoldOblique",      true,  true,  StandardFonts.HelveticaBoldOblique)]
    [InlineData("TimesNewRomanPS-BoldItalicMT", true, true, StandardFonts.TimesBoldItalic)]
    [InlineData("ABCDEF+CourierNewPS-BOLDMT", true,  false, StandardFonts.CourierBold)]
    public void TheWidthsTheDrawnFaceAndTheSvgFaceReadANameAlike(
        string name, bool bold, bool italic, string standard14)
    {
        Assert.Equal(bold, FontNameStyle.IsBold(name));
        Assert.Equal(italic, FontNameStyle.IsItalic(name));

        Assert.Equal(standard14, Standard14Names.Canonical(name));

        SKFontStyle drawn = FontCache.StyleNamedBy(name);
        Assert.Equal(bold ? (int)SKFontStyleWeight.Bold : (int)SKFontStyleWeight.Normal, drawn.Weight);
        Assert.Equal(italic ? SKFontStyleSlant.Italic : SKFontStyleSlant.Upright, drawn.Slant);

        SvgFontFace svg = SvgFontFace.Describe(name, null, null);
        Assert.Equal(bold ? 700 : 400, svg.Weight);
        Assert.Equal(italic, svg.Italic);
    }

    [Fact]
    public void ANullOrStylelessNameSaysNeither()
    {
        Assert.False(FontNameStyle.IsBold(null));
        Assert.False(FontNameStyle.IsItalic(null));
        Assert.False(FontNameStyle.IsBold("Garamond-Regular"));
        Assert.False(FontNameStyle.IsItalic("Garamond-Regular"));
        // Potency: the same family with its style named.
        Assert.True(FontNameStyle.IsBold("Garamond-SemiBold"));
        Assert.True(FontNameStyle.IsItalic("Garamond-Oblique"));
    }
}
