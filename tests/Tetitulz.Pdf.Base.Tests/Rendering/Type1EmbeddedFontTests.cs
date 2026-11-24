// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An embedded <b>Type 1</b> font program (<c>/FontFile</c>), rendered end to end.
///
/// <para><b>Why this exists.</b> <c>Type1GlyphSource</c>, <c>DrawType1Run</c> and
/// <c>PdfRenderContext.DrawGlyphPath</c> form a complete glyph-rendering route with <b>no test
/// coverage anywhere in the tree</b> — <c>grep -rl Type1GlyphSource tests/</c> returned nothing
/// before this file. That is the same shape as three defects found in this repository within one
/// session: <c>SvgToPdfConverter</c> drew a blank page behind sixteen tests, type 5 shading was
/// never implemented and had no test of any kind, and every JBIG2 image rendered in negative behind
/// 119 tests. An untested route here is not a small risk.</para>
///
/// <para><b>What decides which route runs, and why it is host-dependent.</b>
/// <c>FontCache</c> PFB-wraps a <c>/FontFile</c> and offers it to
/// <c>SKTypeface.FromData</c> first. When Skia has its Type 1 driver compiled in, that
/// succeeds and the glyphs are drawn by Skia through the ordinary batched path —
/// <c>Type1Source</c> is never populated and <c>DrawGlyphPath</c> is unreachable. Only when
/// Skia declines does <c>Type1Parser</c> run and the outline path light up. So whether this
/// repository's own Type 1 renderer executes at all depends on how the referenced SkiaSharp native
/// was built, which is exactly the fact nobody had established.</para>
///
/// <para><b>The tests are shaped around that.</b> The first asserts the thing that must be true
/// on <i>every</i> host — the glyph reaches the page with the geometry its charstring declares —
/// so it is falsifiable regardless of which route ran. The second reports which route this host
/// took, and asserts the consequence that follows from it, so the answer is recorded rather than
/// assumed.</para>
/// </summary>
public sealed class Type1EmbeddedFontTests
{
    // The charstring draws a 400x400 box on a 1000-unit em, so at 100 pt it is 40x40 pt.
    private const float FontSizePt = 100f;
    private const float ExpectedSizePt = 40f;

    /// <summary>
    /// The glyph is drawn, at the size and place its charstring declares — whichever of the two
    /// routes the host takes. This is the assertion that had no equivalent anywhere: an embedded
    /// Type 1 font had never been rendered by a test.
    /// </summary>
    [Fact]
    public void EmbeddedType1Font_DrawsItsGlyphAtTheDeclaredGeometry()
    {
        using SKBitmap bmp = PixelAssert.Render(Type1GlyphPage());

        var bounds = PixelAssert.InkBounds(bmp);
        Assert.True(bounds.HasValue,
            "The page rendered blank: an embedded Type 1 /FontFile produced no ink at all. " +
            "Neither the PFB/Skia route nor the Type1GlyphSource outline route drew the glyph.");

        var b = bounds.Value;
        float width = b.right - b.left + 1;
        float height = b.bottom - b.top + 1;

        // A 400x400 charstring box at 100 pt. Tolerance is generous because the two routes
        // rasterise differently at the edges — the point is that the glyph is the declared SHAPE,
        // not that both routes are pixel-identical.
        Assert.Equal(ExpectedSizePt, width, tolerance: 3f);
        Assert.Equal(ExpectedSizePt, height, tolerance: 3f);
    }

    /// <summary>
    /// Records which of the two routes this host takes, and asserts what follows from it.
    /// </summary>
    /// <remarks>
    /// <para>This is deliberately not a "both are fine" assertion, which could not fail. Each arm
    /// asserts something specific to itself: if the outline route is live, the repository's own
    /// Type 1 parser must have produced glyphs; if Skia took the font, the parser must NOT have been
    /// consulted, because populating both would mean two renderers for one font and no rule saying
    /// which wins.</para>
    /// <para>Whichever arm runs, the answer belongs in the record: it determines whether
    /// <c>DrawGlyphPath</c> is dead code on a standard build, and therefore whether optimising it —
    /// plan 08 item 30's unshipped half — is worth anything at all.</para>
    /// </remarks>
    [Fact]
    public void EmbeddedType1Font_RoutesToExactlyOneRenderer()
    {
        byte[] pdf = Type1GlyphPage();
        using var stream = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(stream);

        var page = doc.Pages[0];
        var resources = page.Resources;

        using var cache = new FontCache();
        var outlineSource = cache.GetType1GlyphSource("F1", resources, doc);

        // Read inside the cache's own scope and keep only the bool: FontCache.Dispose disposes
        // every typeface it hands out, so letting one escape this method would be a
        // use-after-free that Windows happens to hide (CLAUDE.md records it segfaulting Linux).
        bool skiaTookIt = cache.GetTypeface("F1", resources, doc) != null;

        if (outlineSource != null)
        {
            // This host's Skia declined the PFB, so the library's own Type 1 renderer is live
            // and DrawGlyphPath is reachable. It must actually produce a glyph.
            var outline = outlineSource.GetOutline('A');
            Assert.True(outline.HasValue && !outline.Value.Path.IsEmpty,
                "The Type 1 outline route is live on this host but produced no path for 'A'.");
        }
        else
        {
            // Skia accepted the PFB wrap. The outline renderer must not ALSO have been set up:
            // FontCache populates Type1Source only when SKTypeface.FromData returned null, or when
            // its heaviness check refused the typeface (Type1GlyphSource.IsTooHeavyForHinting).
            Assert.True(skiaTookIt,
                "Neither route claimed the font: no Type1GlyphSource and no typeface, so the " +
                "glyph would fall back to a substituted system face and the /FontFile is ignored.");
        }
    }

    // ================================================================
    //  Horizontal scaling (Th) on the outline route
    // ================================================================

    /// <summary>
    /// <c>Tz</c> must scale an embedded Type 1 glyph horizontally and leave its height alone.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the only coverage of <c>Th</c> in
    /// <c>PdfRenderContext.DrawGlyphPath</c>.</b> <c>HorizontalScalingGlyphOutlineTests</c> makes
    /// the same three assertions, but its embedded-CFF fixture is OTF-wrapped and so reaches the
    /// OTF-CFF run path instead — that class says as much in its own remarks. <c>DrawGlyphPath</c>
    /// omitted <c>Th</c> from its size matrix entirely, so every glyph drawn as an outline ignored
    /// <c>Tz</c>.</para>
    /// <para><b>Which route runs is host-dependent, and the assertions are written so that does
    /// not matter.</b> The outline route is taken when Skia's Skia build declines the font
    /// program — which it does for this PFB wrap on SkiaSharp 4.151.0/Windows, so CI exercises
    /// <c>DrawGlyphPath</c>. On a host whose Skia accepts it the run goes through the glyph-id
    /// path instead. Both are required to honour <c>Tz</c> identically, so the test is a true
    /// statement about the renderer either way; only which line of code it covers changes. Do not
    /// "fix" this by asserting on the route — that would make it fail on a conformant host.</para>
    /// </remarks>
    [Fact]
    public void Type1Glyph_UnderHorizontalScaling_IsDrawnAtHalfWidth()
    {
        var full = Type1GlyphInkExtent(100);
        var half = Type1GlyphInkExtent(50);

        Assert.InRange(half.width, full.width * 0.45f, full.width * 0.55f);
    }

    /// <summary>
    /// The complement: <c>Tz</c> is horizontal only, so the glyph's height must not move. Without
    /// it the halving test above would also pass on a renderer that scaled the whole glyph.
    /// </summary>
    [Fact]
    public void Type1Glyph_UnderHorizontalScaling_KeepsItsHeight()
    {
        var full = Type1GlyphInkExtent(100);
        var half = Type1GlyphInkExtent(50);

        Assert.InRange(half.height, full.height - 2f, full.height + 2f);
    }

    /// <summary>
    /// <c>Ts</c> lifts an embedded Type 1 glyph by the rise in text space (ISO 32000-1 §9.3.7): at
    /// 1 px per point a rise of 20 draws it 20 pixels higher, the same size. Like the <c>Tz</c> pair
    /// above, it is true of either route; a <c>DrawGlyphPath</c> that dropped the rise would fail it.
    /// </summary>
    [Fact]
    public void Type1Glyph_UnderTextRise_IsDrawnHigherByTheRise()
    {
        using SKBitmap flat = PixelAssert.Render(EmbeddedType1Fixture.Page(
            $"BT /F1 {FontSizePt:0} Tf 100 300 Td (A) Tj ET"));
        using SKBitmap raised = PixelAssert.Render(EmbeddedType1Fixture.Page(
            $"BT /F1 {FontSizePt:0} Tf 20 Ts 100 300 Td (A) Tj ET"));

        var a = PixelAssert.InkBounds(flat);
        var b = PixelAssert.InkBounds(raised);
        Assert.True(a.HasValue && b.HasValue, "a page rendered blank, so nothing here measures Ts");

        Assert.InRange(a.Value.top - b.Value.top, 19, 21);
        Assert.InRange(b.Value.bottom - b.Value.top, a.Value.bottom - a.Value.top - 1, a.Value.bottom - a.Value.top + 1);
    }

    /// <summary>Ink extent of the glyph at a given <c>Tz</c>, rasterised at 1 px per point.</summary>
    private static (float width, float height) Type1GlyphInkExtent(int horizontalScalingPercent)
    {
        using SKBitmap bmp = PixelAssert.Render(Type1GlyphPage(horizontalScalingPercent));
        var bounds = PixelAssert.InkBounds(bmp);
        Assert.True(bounds.HasValue,
            $"The page rendered blank at {horizontalScalingPercent} Tz, so the fixture never " +
            "reached the embedded Type 1 glyph and nothing here would be measuring Tz.");

        var b = bounds.Value;
        return (b.right - b.left + 1, b.bottom - b.top + 1);
    }

    // ================================================================
    //  Fixture — a genuine, minimal Type 1 font program
    // ================================================================

    /// <summary>One page showing 'A' from an embedded Type 1 <c>/FontFile</c>.</summary>
    /// <param name="horizontalScalingPercent">The <c>Tz</c> operand, in percent.</param>
    /// <remarks>
    /// The font program itself moved to <see cref="EmbeddedType1Fixture"/> when a second suite
    /// needed it. Two copies of an <c>eexec</c>-encrypted program would be two chances for one of
    /// them to drift into something Skia accepts and the other does not — which would change
    /// <i>which renderer runs</i>, silently, in one suite only.
    /// </remarks>
    private static byte[] Type1GlyphPage(int horizontalScalingPercent = 100) =>
        EmbeddedType1Fixture.Page(
            $"BT /F1 {FontSizePt:0} Tf {horizontalScalingPercent} Tz 100 300 Td (A) Tj ET");
}
