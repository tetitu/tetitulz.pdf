// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The half-em advance fallback at the renderers' <b>call sites</b>, reached from a font that really
/// produces a non-finite advance.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> <c>MatrixGuard.SanitizeAdvance</c> was pinned directly by
/// <c>NonFiniteAdvanceTests</c> — and GAPS.md §9 recorded that <b>every call site survived being
/// bypassed</b>, in both renderers, because nothing in the tree embedded a font whose advance could
/// come out NaN or Infinity. Covering a function is not covering the decision that uses it.</para>
///
/// <para><b>What the fixture does.</b> <c>EmbeddedCffFixture.CorruptAdvanceCffProgram</c> is the
/// ordinary test font with two changes: its glyph's charstring carries no width operand, so
/// <c>defaultWidthX</c> supplies the advance, and its <c>defaultWidthX</c> is the DICT real
/// <c>9E99</c> — finite as the <c>double</c> the parser reads, <c>+Infinity</c> as the <c>float</c>
/// the renderers multiply. Both of those are legal CFF; together they are a font no arithmetic can
/// use, which is the case the guard exists for.</para>
///
/// <para><b>What is asserted is the position of a LATER glyph</b>, which is the only thing that can
/// tell the guard from its absence. Without it the first glyph still paints — the poison enters
/// <c>Tm</c> with the advance — and every glyph after it draws at non-finite coordinates and is
/// silently invisible. An assertion that the page has ink would pass either way.</para>
///
/// <para><b>An overflowing <c>Tf</c> reaches the same multiplication and is deliberately not used
/// here.</b> It can only overflow by making the font size itself astronomical, which makes the whole
/// run's geometry non-finite — so both readings put every glyph off the page and no on-page
/// assertion can separate them.</para>
/// </remarks>
public class NonFiniteAdvanceCallSiteTests
{
    /// <summary>Two glyphs at 24 pt, pen starting at (100, 700).</summary>
    private const string TwoGlyphs = "BT /F1 24 Tf 100 700 Td (AA) Tj ET";

    private const float FontSize = 24f;
    private const float PenX = 100f;

    /// <summary>The glyph's box, in points at <see cref="FontSize"/>: 400/1000 em.</summary>
    private const float BoxPts = FontSize * EmbeddedCffFixture.GlyphBoxEm / 1000f;

    /// <summary>Half an em at <see cref="FontSize"/> — what the fallback substitutes.</summary>
    private const float HalfEmPts = FontSize / 2f;

    // ================================================================
    //  Raster — DrawOtfCffRun / DrawCffRun, whichever the wrap selects
    // ================================================================

    /// <summary>
    /// The inked span's right edge locates the second glyph, and is asserted as a <b>range</b> so
    /// that it excludes all three wrong answers at once: the run stopping at the first glyph
    /// (~109.6), the fallback being applied (121.6, wanted), and a renderer quietly taking
    /// <c>/Widths</c> instead of the font program (128.8).
    /// </summary>
    [Fact]
    public void RasterRender_AFontWithANonFiniteAdvance_PlacesTheSecondGlyphAHalfEmOn()
    {
        using SKBitmap bmp = PixelAssert.Render(
            EmbeddedCffFixture.Page(TwoGlyphs, corruptAdvance: true));

        var ink = PixelAssert.InkBounds(bmp);

        Assert.NotNull(ink);
        int expected = PixelAssert.X(PenX + HalfEmPts + BoxPts);
        Assert.InRange(ink!.Value.right, expected - 1, expected + 1);
    }

    /// <summary>
    /// The control: the same page with the <b>uncorrupted</b> font, which differs in nothing but the
    /// charstring's width operand and the Private DICT. It must paint two glyphs too — otherwise the
    /// test above would be measuring a fixture that never draws a second glyph for reasons of its
    /// own.
    /// </summary>
    [Fact]
    public void RasterRender_TheSameTwoGlyphsWithAUsableAdvance_PlacesTheSecondGlyphToo()
    {
        using SKBitmap bmp = PixelAssert.Render(EmbeddedCffFixture.Page(TwoGlyphs));

        var ink = PixelAssert.InkBounds(bmp);

        Assert.NotNull(ink);
        float advance = FontSize * EmbeddedCffFixture.GlyphAdvanceEm / 1000f;
        Assert.True(ink!.Value.right >= PixelAssert.X(PenX + advance + BoxPts) - 1,
            $"ink ends at x={ink.Value.right} with a perfectly usable advance, so the fixture "
          + "does not draw two glyphs at all and proves nothing about the corrupt one.");
    }

    // ================================================================
    //  SVG — the mirrored call site in SvgContentWriter.Text
    // ================================================================

    /// <summary>
    /// The export places each glyph with its own <c>&lt;use transform="matrix(…)"&gt;</c>, so the
    /// pen positions are readable exactly — no pixel tolerance, and the half-em is pinned as a
    /// value rather than as a bound.
    /// </summary>
    [Fact]
    public void SvgExport_AFontWithANonFiniteAdvance_PlacesTheSecondGlyphAHalfEmOn()
    {
        float[] pens = GlyphPenPositions(
            EmbeddedCffFixture.Page(TwoGlyphs, corruptAdvance: true));

        Assert.Equal(new[] { PenX, PenX + HalfEmPts }, pens);
    }

    /// <summary>
    /// The same export must carry no non-finite number anywhere — the failure mode that made this
    /// rule shared in the first place was <c>d="MInfinity,NaN …"</c>, markup no viewer renders.
    /// </summary>
    [Fact]
    public void SvgExport_AFontWithANonFiniteAdvance_EmitsNoNonFiniteCoordinate()
    {
        XDocument svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(
            new MemoryStream(EmbeddedCffFixture.Page(TwoGlyphs, corruptAdvance: true)), 0));

        string[] offenders = svg.Descendants().SelectMany(e => e.Attributes())
            .Select(a => a.Value)
            .Where(v => v.IndexOf("Infinity", StringComparison.Ordinal) >= 0
                     || v.IndexOf("NaN", StringComparison.Ordinal) >= 0)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>The SVG control, for the same reason the raster one exists.</summary>
    [Fact]
    public void SvgExport_TheSameTwoGlyphsWithAUsableAdvance_PlacesTheSecondGlyphToo()
    {
        float[] pens = GlyphPenPositions(EmbeddedCffFixture.Page(TwoGlyphs));

        float advance = FontSize * EmbeddedCffFixture.GlyphAdvanceEm / 1000f;
        Assert.Equal(new[] { PenX, PenX + advance }, pens);
    }

    // ================================================================
    //  Fixture
    // ================================================================

    /// <summary>
    /// The pen X of every glyph the export placed, in document order: the <c>e</c> component of each
    /// <c>&lt;use&gt;</c>'s <c>transform="matrix(a,b,c,d,e,f)"</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>The outlines themselves carry no position.</b> The writer emits one
    /// <c>&lt;path&gt;</c> per distinct glyph into <c>&lt;defs&gt;</c>, in glyph space, and places
    /// each occurrence with a <c>&lt;use&gt;</c> — so reading coordinates out of the path data finds
    /// the same 0.4-unit box whatever the advance did, which is a fixture that cannot fail.</para>
    /// <para>Parsed as an <see cref="XDocument"/> rather than matched as a substring, per CLAUDE.md,
    /// so a malformed export fails here rather than silently missing a match.</para>
    /// </remarks>
    private static float[] GlyphPenPositions(byte[] pdf)
    {
        XDocument svg = XDocument.Parse(
            PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0));

        var pens = new List<float>();
        foreach (XElement use in svg.Descendants().Where(e => e.Name.LocalName == "use"))
        {
            string transform = (string)use.Attribute("transform");
            if (string.IsNullOrEmpty(transform)) continue;

            Match m = Regex.Match(transform, @"matrix\(([^)]*)\)");
            Assert.True(m.Success, $"a <use> carried a transform this test cannot read: {transform}");

            float[] parts = m.Groups[1].Value.Split(',')
                .Select(p => float.Parse(p.Trim(), CultureInfo.InvariantCulture))
                .ToArray();
            Assert.Equal(6, parts.Length);
            pens.Add(parts[4]);
        }
        return pens.ToArray();
    }
}
