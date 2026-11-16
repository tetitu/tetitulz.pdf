// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The render-scoped cache of parsed content-stream operations
/// (<c>PdfRenderContext.GetOrParseOperations</c>), which makes a Form XObject drawn N times
/// tokenise once instead of N times.
/// </summary>
/// <remarks>
/// A parse cache is invisible in the output when it works and invisible when it silently returns
/// the wrong operations for a stream — both look like "the page rendered". So these assert on
/// decoded pixels, and the key case is <see cref="TwoDistinctForms_EachDrawItsOwnContent"/>:
/// a cache keyed carelessly (by length, by content hash, by a shared slot) would serve one form's
/// operations for the other, and every "did it render?" check would still pass.
/// </remarks>
public class ParsedContentCacheTests
{
    private const int Dpi = 72;   // 1 pt == 1 px

    /// <summary>
    /// One form drawn at four positions must paint at all four. If the cache returned an empty or
    /// stale list after the first `Do`, only the first would appear.
    /// </summary>
    [Fact]
    public void FormDrawnFourTimes_PaintsAtEveryPosition()
    {
        using SKBitmap bmp = Render(FormPdf());

        // The form paints a 20x20 red box at its own origin; it is placed at four corners.
        AssertColour(bmp, 30, 30, expectRed: true);
        AssertColour(bmp, 130, 30, expectRed: true);
        AssertColour(bmp, 30, 130, expectRed: true);
        AssertColour(bmp, 130, 130, expectRed: true);

        // And nowhere the form was not placed.
        AssertColour(bmp, 80, 80, expectRed: false);
    }

    /// <summary>
    /// Two different forms on one page. Each must draw its own content — the case a cache with a
    /// colliding key gets wrong while still rendering something plausible everywhere.
    /// </summary>
    [Fact]
    public void TwoDistinctForms_EachDrawItsOwnContent()
    {
        using SKBitmap bmp = Render(TwoFormPdf());

        // Form A is red at (30,30); form B is green at (130,30). A collision would paint one
        // colour in both places.
        SKColor a = bmp.GetPixel(30, bmp.Height - 1 - 30);
        SKColor b = bmp.GetPixel(130, bmp.Height - 1 - 30);

        Assert.True(a.Red > 150 && a.Green < 110, $"form A should be red, was {a}");
        Assert.True(b.Green > 150 && b.Red < 110, $"form B should be green, was {b}");
    }

    /// <summary>
    /// Rendering the same document twice must produce byte-identical pixels. The cache is
    /// render-scoped, so the second render builds its own — a cache leaking across renders, or one
    /// handing out a list a previous render had consumed, shows up here.
    /// </summary>
    [Fact]
    public void RenderingTwice_ProducesIdenticalPixels()
    {
        byte[] pdf = FormPdf();
        using SKBitmap first = Render(pdf);
        using SKBitmap second = Render(pdf);

        Assert.Equal(first.Width, second.Width);
        Assert.Equal(first.Height, second.Height);

        int differing = 0;
        for (int y = 0; y < first.Height; y++)
            for (int x = 0; x < first.Width; x++)
                if (first.GetPixel(x, y) != second.GetPixel(x, y)) differing++;

        Assert.Equal(0, differing);
    }

    /// <summary>
    /// A form whose parsed operations would exceed the cache ceiling must still render correctly —
    /// the budget refuses to <i>cache</i>, never to <i>execute</i>. Uses a deliberately oversized
    /// stream so the over-budget branch is the one taken.
    /// </summary>
    [Fact]
    public void OversizedFormExceedingTheCacheBudget_StillRenders()
    {
        // ~200k operators: comfortably past MaxParsedContentCacheBytes (16 MB) at the ~48 bytes
        // per operation the budget charges, so the entry is executed but not retained.
        var body = new StringBuilder("1 0 0 rg\n");
        for (int i = 0; i < 200_000; i++) body.Append("0 0 1 1 re f\n");
        body.Append("0 0 20 20 re f\n");   // the part that must still be visible

        using SKBitmap bmp = Render(FormPdf(body.ToString()));
        AssertColour(bmp, 30, 30, expectRed: true);
    }

    /// <summary>
    /// A tiling cell is rendered offscreen, into a context of its own, once per paint that uses it.
    /// That context shares the page's parsed operations: the cell's content is parsed into the same
    /// cache as the page's, so a cell painted many times — or nested, as fuzzing found, to the depth
    /// cap — is tokenised once. Before, each offscreen context began with an empty cache. The output
    /// is the same either way, so the cache's entries are the evidence, beside the paint.
    /// </summary>
    [Fact]
    public void ATilingCellRenderedOffscreen_ParsesIntoThePagesCache()
    {
        byte[] pdf = Build(
            "/Pattern cs /P1 scn 20 20 60 60 re f 120 120 60 60 re f\n",
            "/Pattern << /P1 5 0 R >>",
            new[] { ("5", "/Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 10 10] " +
                          "/XStep 10 /YStep 10 /Resources << >>", "1 0 0 rg 0 0 10 10 re f\n") });

        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
        var page = doc.Pages[0];
        using var bitmap = new SKBitmap(200, 200, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        var cache = new Tetitulz.Pdf.Base.Rendering.ContentOperationCache();
        using (var ctx = new Tetitulz.Pdf.Base.Rendering.PdfRenderContext(canvas, 200, 200, 1f) { Operations = cache })
        using (var fonts = new Tetitulz.Pdf.Base.Rendering.FontCache())
        {
            var interpreter = new Tetitulz.Pdf.Base.Rendering.ContentStreamInterpreter(ctx, doc, page.Resources, fonts)
                { FormBudget = new Tetitulz.Pdf.Base.Rendering.RenderBudget() };
            interpreter.Execute(page.GetContentStreamData());
        }

        Assert.Equal(2, cache.Count);   // the page's content and the cell's, in one cache
        AssertColour(bitmap, 50, 50, expectRed: true);
        AssertColour(bitmap, 150, 150, expectRed: true);
        AssertColour(bitmap, 100, 100, expectRed: false);
    }

    // ------------------------------------------------------------------

    private static void AssertColour(SKBitmap bmp, int userX, int userY, bool expectRed)
    {
        SKColor c = bmp.GetPixel(userX, bmp.Height - 1 - userY);
        bool isRed = c.Red > 150 && c.Green < 110 && c.Blue < 110;
        Assert.True(isRed == expectRed,
            $"at PDF ({userX},{userY}) expected {(expectRed ? "red" : "no red")}, got {c}");
    }

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfOperations.RenderPageToPng(ms, 0, dpi: Dpi);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>A 200x200 page drawing one Form XObject at four corners.</summary>
    private static byte[] FormPdf(string formBody = null)
    {
        formBody ??= "1 0 0 rg 0 0 20 20 re f\n";
        string content =
            "q 1 0 0 1 20 20 cm /Fx Do Q\n" +
            "q 1 0 0 1 120 20 cm /Fx Do Q\n" +
            "q 1 0 0 1 20 120 cm /Fx Do Q\n" +
            "q 1 0 0 1 120 120 cm /Fx Do Q\n";

        return Build(content, "/XObject << /Fx 5 0 R >>",
                     new[] { ("5", "/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >>", formBody) });
    }

    /// <summary>A 200x200 page with two different forms, one red and one green.</summary>
    private static byte[] TwoFormPdf()
    {
        string content =
            "q 1 0 0 1 20 20 cm /Fa Do Q\n" +
            "q 1 0 0 1 120 20 cm /Fb Do Q\n";

        return Build(content, "/XObject << /Fa 5 0 R /Fb 6 0 R >>", new[]
        {
            ("5", "/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >>", "1 0 0 rg 0 0 20 20 re f\n"),
            ("6", "/Type /XObject /Subtype /Form /BBox [0 0 20 20] /Resources << >>", "0 1 0 rg 0 0 20 20 re f\n"),
        });
    }

    private static byte[] Build(string content, string resources,
                                (string num, string dict, string body)[] streams)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
             + $"/Resources << {resources} >> /Contents 4 0 R >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");

        foreach (var (num, dict, body) in streams)
        {
            BeginObj(int.Parse(num));
            Emit($"<< {dict} /Length {body.Length} >>\nstream\n{body}\nendstream\nendobj\n");
        }

        int count = 5 + streams.Length;
        int xref = acc.Count;
        Emit($"xref\n0 {count}\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
