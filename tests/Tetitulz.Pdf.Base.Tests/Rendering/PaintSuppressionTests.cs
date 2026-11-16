// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="PdfRenderContext.PaintSuppressed"/>, the gate a
/// future increment wires <see cref="PdfRenderSession.RenderPageBands"/> up to. Tested here in
/// isolation, against a real render (not <see cref="ContentStreamInterpreter.BuildDisplayList"/>'s
/// throwaway pass) — the claim under test is about actual pixels and actual state, which a
/// bounds-only pass cannot exercise.
/// </summary>
public sealed class PaintSuppressionTests
{
    private const float PageSize = DisplayListTests.PageSize;

    [Fact]
    public void APatternFilledRect_WithPaintAlwaysSuppressed_PaintsNoPixels()
    {
        byte[] pixelsSuppressed = Render(
            "/Pattern cs\n/P0 scn\n10 10 20 20 re\nf\n",
            DisplayListTests.FixtureResources.Pattern,
            suppressed: () => true);

        // The shading pattern (a black-to-white axial gradient, DisplayListTests' fixture) always
        // paints something other than the SKBitmap default of fully transparent black wherever it
        // is not suppressed -- so "every byte is 0" is the right potency check for "nothing was
        // painted", not just "the two renders differ".
        Assert.All(pixelsSuppressed, b => Assert.Equal(0, b));
    }

    [Fact]
    public void APatternFilledRect_WithPaintNeverSuppressed_PaintsSomePixels()
    {
        // The control for the test above: the same content, same fixture, PaintSuppressed left
        // null (its default), proves the fixture actually paints when not gated -- otherwise
        // "every byte is 0" above would be true for a reason that has nothing to do with the gate.
        byte[] pixelsPainted = Render(
            "/Pattern cs\n/P0 scn\n10 10 20 20 re\nf\n",
            DisplayListTests.FixtureResources.Pattern,
            suppressed: null);

        Assert.Contains(pixelsPainted, b => b != 0);
    }

    [Fact]
    public void ASuppressedPatternFill_StillClearsThePath_SoALaterUnrelatedFillIsNotUnioned()
    {
        // op0 "/Pattern cs", op1 "/P0 scn", op2 "10 10 20 20 re", op3 "f" (suppressed: the
        // pattern-filled rect at (10,10)-(30,30)), op4 "0 1 0 rg" (green), op5 "1 1 8 8 re" (a
        // second, disjoint, much smaller rect at (1,1)-(9,9)), op6 "f" (an ordinary green fill,
        // never suppressed). If DrawPatternPath's NewPath() did not run on the suppressed branch,
        // _currentPath at op6 would still hold the first rect's geometry too, and the ordinary
        // fill would paint green across BOTH rects -- exactly the corruption "suppress the paint, not the
        // dispatch" exists to prevent.
        int suppressedOpIndex = 3;
        byte[] pixels = Render(
            "/Pattern cs\n/P0 scn\n10 10 20 20 re\nf\n0 1 0 rg\n1 1 8 8 re\nf\n",
            DisplayListTests.FixtureResources.Pattern,
            suppressed: null,
            opIndexToSuppress: suppressedOpIndex,
            out int width, out int height, out int stride);

        // Green, opaque, premultiplied: R=0 G=255 B=0 A=255 (RGBA8888 byte order). The canvas
        // flips PDF's Y-up to device Y-down internally (PdfRenderContext's own class remarks), so
        // PDF point (x, y) reads back at device pixel row (PageSize - y).
        bool GreenAt(int pdfX, int pdfY)
        {
            int py = (int)(PageSize - pdfY);
            int i = (py * stride) + (pdfX * 4);
            return pixels[i] == 0 && pixels[i + 1] == 255 && pixels[i + 2] == 0 && pixels[i + 3] == 255;
        }

        // Inside the small rect (1,1)-(9,9): must be green -- the fill that was never suppressed.
        Assert.True(GreenAt(5, 5), "the ordinary fill did not paint its own rect");

        // Inside the first rect but well outside the second (PDF point (20,20)): must be
        // untouched (fully transparent), not green -- the leak this test exists to catch.
        int py20 = (int)(PageSize - 20);
        int i20 = (py20 * stride) + (20 * 4);
        Assert.Equal(0, pixels[i20 + 3]); // alpha 0: nothing painted here at all
    }

    [Fact]
    public void AShadingFill_WithPaintSuppressed_PaintsNoPixels()
    {
        byte[] suppressed = Render("/Sh0 sh\n", DisplayListTests.FixtureResources.Shading,
            suppressed: () => true);
        byte[] painted = Render("/Sh0 sh\n", DisplayListTests.FixtureResources.Shading,
            suppressed: null);

        Assert.All(suppressed, b => Assert.Equal(0, b));
        Assert.Contains(painted, b => b != 0); // control: the fixture does paint when not gated
    }

    [Fact]
    public void AnImageDraw_WithPaintSuppressed_PaintsNoPixels()
    {
        // Placed to cover the whole 40x40 page, so the control assertion below cannot pass by
        // missing the fixture's 1x1 red pixel entirely.
        const string content = "q 40 0 0 40 0 0 cm /Im0 Do Q\n";
        byte[] suppressed = Render(content, DisplayListTests.FixtureResources.Image,
            suppressed: () => true);
        byte[] painted = Render(content, DisplayListTests.FixtureResources.Image,
            suppressed: null);

        Assert.All(suppressed, b => Assert.Equal(0, b));
        Assert.Contains(painted, b => b != 0);
    }

    [Fact]
    public void AnEmbeddedType1GlyphOutline_WithPaintSuppressed_PaintsNoPixels()
    {
        // EmbeddedType1Fixture's glyph ('A', code 65) is a real Type 1 charstring outline drawn
        // through DrawGlyphPath, not DrawTextBlob -- the same fixture
        // AnEmbeddedType1GlyphsOutline_ReportsOneEntry_AtTheTjOperatorsIndex (DisplayListTests) uses.
        const string content = "BT\n/F1 100 Tf\n100 376 Td\n(A) Tj\nET\n";
        byte[] pdf = EmbeddedType1Fixture.Page(content);

        byte[] suppressed = RenderEmbeddedType1(pdf, content, suppressed: () => true);
        byte[] painted = RenderEmbeddedType1(pdf, content, suppressed: null);

        Assert.All(suppressed, b => Assert.Equal(0, b));
        Assert.Contains(painted, b => b != 0);
    }

    [Fact]
    public void AStandardFontTextRun_WithPaintSuppressed_PaintsNoPixels()
    {
        // Helvetica has no embedded outline, so this run reaches DrawTextBlob (the system/fallback
        // typeface path), not DrawGlyphPath -- covering the fifth and last gated method.
        const string content = "BT\n/F1 20 Tf\n2 15 Td\n(A) Tj\nET\n";
        byte[] suppressed = Render(content, DisplayListTests.FixtureResources.Font,
            suppressed: () => true);
        byte[] painted = Render(content, DisplayListTests.FixtureResources.Font,
            suppressed: null);

        Assert.All(suppressed, b => Assert.Equal(0, b));
        Assert.Contains(painted, b => b != 0);
    }

    // ================================================================

    private static byte[] RenderEmbeddedType1(byte[] pdf, string content, Func<bool>? suppressed)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        using var bmp = new SKBitmap(612, 792, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        using var ctx = new PdfRenderContext(canvas, 612f, 792f, 1f);
        using var fonts = new FontCache();

        var interp = new ContentStreamInterpreter(ctx, doc, DisplayListTests.Resources(doc), fonts);
        ctx.PaintSuppressed = suppressed;

        var ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes(content));
        interp.Execute(ops);
        return bmp.Bytes;
    }

    private static byte[] Render(string content, DisplayListTests.FixtureResources resources,
        Func<bool>? suppressed)
        => Render(content, resources, suppressed, opIndexToSuppress: -1,
                  out _, out _, out _);

    private static byte[] Render(string content, DisplayListTests.FixtureResources resources,
        Func<bool>? suppressed, int opIndexToSuppress,
        out int width, out int height, out int stride)
    {
        using var doc = DisplayListTests.Open(resources);
        int pixels = (int)PageSize;
        using var bmp = new SKBitmap(pixels, pixels, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bmp);
        using var ctx = new PdfRenderContext(canvas, PageSize, PageSize, 1f);
        using var fonts = new FontCache();

        var interp = new ContentStreamInterpreter(ctx, doc, DisplayListTests.Resources(doc), fonts);

        if (opIndexToSuppress >= 0)
            ctx.PaintSuppressed = () => interp.CurrentOpIndex == opIndexToSuppress;
        else
            ctx.PaintSuppressed = suppressed;

        var ops = ContentStreamParser.Parse(Encoding.ASCII.GetBytes(content));
        interp.Execute(ops);

        width  = bmp.Width;
        height = bmp.Height;
        stride = bmp.RowBytes;
        return bmp.Bytes;
    }
}
