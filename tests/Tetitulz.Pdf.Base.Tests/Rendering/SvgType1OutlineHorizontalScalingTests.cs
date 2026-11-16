// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The SVG export of a Type 1 (charstring-outlined) glyph puts the horizontal scaling in the
/// glyph matrix's <c>a</c> slot (§9.4.4), as the typeface path does: under <c>50 Tz</c> each glyph
/// is half as wide and the pen steps at half pitch, where the outline drew full width on a scaled
/// pitch.
/// </summary>
public class SvgType1OutlineHorizontalScalingTests
{
    [Fact]
    public void AType1Outline_UnderHorizontalScaling_IsHalfAsWide()
    {
        var full = GlyphMatrices(100);
        var half = GlyphMatrices(50);

        Assert.Equal(2, full.Count);
        Assert.Equal(2, half.Count);
        Assert.Equal(full[0].a / 2, half[0].a, 4);            // the glyph is stretched by Tz
        Assert.Equal(full[0].d, half[0].d, 4);                // its height is not
        Assert.Equal((full[1].e - full[0].e) / 2, half[1].e - half[0].e, 2);   // the pitch halves too
    }

    private static System.Collections.Generic.List<(double a, double d, double e)> GlyphMatrices(int tz)
    {
        byte[] pdf = EmbeddedType1Fixture.Page($"BT /F1 100 Tf {tz} Tz 10 10 Td (AA) Tj ET");
        string svg = PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);

        var uses = XDocument.Parse(svg).Descendants().Where(e => e.Name.LocalName == "use").ToList();
        Assert.True(uses.Count >= 2, "the export drew no glyph outlines: " + svg);
        return uses.Take(2).Select(u =>
        {
            var n = Regex.Matches(u.Attribute("transform")!.Value, @"-?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?")
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(6, n.Length);
            return (n[0], n[3], n[4]);
        }).ToList();
    }
}
