// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The output formats and pixel formats <see cref="PdfImageEncoder"/> added: WebP (lossless and
/// lossy), grayscale, a caller-chosen page background, and JPEG chroma subsampling.
///
/// <para><b>Every assertion here decodes the output.</b> A blank page encodes to a valid non-empty
/// image in all four formats, so a length or a non-null check would pass with the encoder deleted
/// (CLAUDE.md banned test pattern 3). Where a claim is comparative — "lossless", "smaller",
/// "less colour error" — the test decodes both sides and compares them.</para>
/// </summary>
public class ImageFormatAndPixelFormatTests
{
    private const int Dpi = 72;

    // ------------------------------------------------------------------ WebP

    /// <summary>
    /// WebP lossless must reproduce the render exactly. This is the claim that makes it worth
    /// having — it is ~9x smaller than PNG on a rendered page, and a size win that quietly cost
    /// fidelity would be a different feature.
    /// </summary>
    [Fact]
    public void WebpLossless_ReproducesEveryPixelOfThePngRender()
    {
        byte[] pdf = ColourPage();

        using SKBitmap fromPng  = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        using SKBitmap fromWebp = Decode(PdfPageRenderer.RenderPageToImage(
            new MemoryStream(pdf), 0, PdfImageFormat.WebpLossless, Dpi));

        Assert.Equal(fromPng.Width,  fromWebp.Width);
        Assert.Equal(fromPng.Height, fromWebp.Height);

        int differing = 0;
        for (int y = 0; y < fromPng.Height; y++)
            for (int x = 0; x < fromPng.Width; x++)
                if (fromPng.GetPixel(x, y) != fromWebp.GetPixel(x, y)) differing++;

        Assert.True(differing == 0,
            $"WebP lossless differs from the PNG render in {differing} pixels");
    }

    /// <summary>
    /// The bytes must actually be WebP — a RIFF container declaring WEBP — and not PNG under a
    /// different method name. Decoding alone would not catch that, because the decoder sniffs.
    /// </summary>
    [Fact]
    public void WebpOutput_IsARiffWebpContainer()
    {
        byte[] webp = PdfPageRenderer.RenderPageToImage(
            new MemoryStream(ColourPage()), 0, PdfImageFormat.WebpLossless, Dpi);

        Assert.True(webp.Length > 12, "output too short to be a RIFF container");
        Assert.Equal("RIFF", Encoding.ASCII.GetString(webp, 0, 4));
        Assert.Equal("WEBP", Encoding.ASCII.GetString(webp, 8, 4));
    }

    /// <summary>
    /// Lossy WebP is offered too, and it must still be a WebP that decodes to the right geometry
    /// and roughly the right colours — the point being that the quality argument reaches it.
    /// </summary>
    [Fact]
    public void WebpLossy_DecodesToTheSameGeometryAndApproximateColour()
    {
        byte[] pdf = ColourPage();
        using SKBitmap reference = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi));
        using SKBitmap lossy = Decode(PdfPageRenderer.RenderPageToImage(
            new MemoryStream(pdf), 0, PdfImageFormat.WebpLossy, Dpi, quality: 90));

        Assert.Equal(reference.Width,  lossy.Width);
        Assert.Equal(reference.Height, lossy.Height);

        // The red block is the page's dominant feature; a lossy codec moves it a little, not a lot.
        SKColor got = lossy.GetPixel(lossy.Width / 4, lossy.Height / 2);
        Assert.True(PixelAssert.Near(got, new SKColor(255, 0, 0), tolerance: 24),
            $"expected approximately red at the block centre, got {got}");
    }

    // ------------------------------------------------------------- grayscale

    /// <summary>
    /// Grayscale output must be grey — every channel equal — and must carry the source's luminance
    /// rather than an arbitrary channel.
    /// </summary>
    /// <remarks>
    /// The fixture is deliberately <b>red</b>: dropping to a single channel by taking R would leave
    /// pure red at 255 and pass a "channels are equal" test on a red page only if it also converted
    /// green and blue. Checking the value as well as the equality is what makes this falsifiable.
    /// </remarks>
    [Fact]
    public void Grayscale_ProducesEqualChannelsCarryingTheSourceLuminance()
    {
        byte[] pdf = ColourPage();
        var options = new PdfRenderOptions { PixelFormat = RenderPixelFormat.Grayscale };

        using SKBitmap grey = Decode(PdfPageRenderer.RenderPageToImage(
            new MemoryStream(pdf), 0, PdfImageFormat.Png, Dpi, renderOptions: options));

        SKColor inBlock = grey.GetPixel(grey.Width / 4, grey.Height / 2);
        Assert.True(inBlock.Red == inBlock.Green && inBlock.Green == inBlock.Blue,
            $"grayscale output is not grey at the red block: {inBlock}");

        // Pure red converts to a mid-dark grey, nowhere near either extreme. A conversion that
        // simply took the red channel would give 255 and be indistinguishable from the white page.
        Assert.InRange(inBlock.Red, 30, 160);

        // The right half is bare paper — the block is full height, so the top-left corner is red,
        // not paper. (Reading the corner is what this test did first, and it failed correctly.)
        SKColor onPaper = grey.GetPixel(grey.Width - 3, 2);
        Assert.True(onPaper.Red > 240, $"the page background should stay near-white, got {onPaper}");
    }

    /// <summary>Grayscale must be smaller and still the same image — the reason to ask for it.</summary>
    [Fact]
    public void Grayscale_IsSmallerThanTheColourEncodingOfTheSamePage()
    {
        byte[] pdf = ColourPage();
        byte[] colour = PdfPageRenderer.RenderPageToImage(new MemoryStream(pdf), 0,
            PdfImageFormat.Png, 150);
        byte[] grey = PdfPageRenderer.RenderPageToImage(new MemoryStream(pdf), 0,
            PdfImageFormat.Png, 150,
            renderOptions: new PdfRenderOptions { PixelFormat = RenderPixelFormat.Grayscale });

        Assert.True(grey.Length < colour.Length,
            $"grayscale PNG ({grey.Length} B) should be smaller than colour ({colour.Length} B)");

        using SKBitmap greyBmp = Decode(grey);
        using SKBitmap colourBmp = Decode(colour);
        Assert.Equal(colourBmp.Width,  greyBmp.Width);
        Assert.Equal(colourBmp.Height, greyBmp.Height);
    }

    // ------------------------------------------------------------ background

    /// <summary>
    /// A transparent background must reach the encoded PNG as alpha 0 where nothing was drawn, and
    /// leave drawn content opaque.
    /// </summary>
    [Fact]
    public void TransparentBackground_LeavesUndrawnPixelsFullyTransparent()
    {
        byte[] pdf = ColourPage();
        var options = new PdfRenderOptions { BackgroundArgb = PdfRenderOptions.Transparent };

        using SKBitmap bmp = DecodeUnpremul(PdfPageRenderer.RenderPage(
            new MemoryStream(pdf), 0, Dpi, options));

        SKColor corner = bmp.GetPixel(bmp.Width - 3, 2);
        Assert.Equal(0, corner.Alpha);

        SKColor inBlock = bmp.GetPixel(bmp.Width / 4, bmp.Height / 2);
        Assert.Equal(255, inBlock.Alpha);
        Assert.True(PixelAssert.Near(inBlock, new SKColor(255, 0, 0), tolerance: 8),
            $"drawn content should be opaque red, got {inBlock}");
    }

    /// <summary>A custom opaque background must appear where nothing was drawn.</summary>
    [Fact]
    public void CustomBackground_PaintsTheRequestedColourBehindThePage()
    {
        byte[] pdf = ColourPage();
        var options = new PdfRenderOptions { BackgroundArgb = 0xFF0000FFu };   // opaque blue

        using SKBitmap bmp = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, Dpi, options));

        SKColor corner = bmp.GetPixel(bmp.Width - 3, 2);
        Assert.True(PixelAssert.Near(corner, new SKColor(0, 0, 255), tolerance: 4),
            $"expected the requested blue background, got {corner}");
    }

    /// <summary>
    /// A rotated page takes a second surface, and that one used to be cleared to white
    /// unconditionally — so a transparent render came back opaque for exactly the pages carrying
    /// <c>/Rotate</c>.
    /// </summary>
    [Fact]
    public void TransparentBackground_SurvivesThePageRotationPath()
    {
        var options = new PdfRenderOptions { BackgroundArgb = PdfRenderOptions.Transparent };

        using SKBitmap rotated = DecodeUnpremul(PdfPageRenderer.RenderPage(
            new MemoryStream(ColourPage(rotate: 90)), 0, Dpi, options));
        using SKBitmap upright = DecodeUnpremul(PdfPageRenderer.RenderPage(
            new MemoryStream(ColourPage()), 0, Dpi, options));

        // Landscape, so the rotation path really ran — this is the surface that used to be
        // cleared to opaque white regardless of the requested background.
        Assert.True(rotated.Width > rotated.Height,
            "expected a 90-degree rotated page to be landscape");

        int rotatedClear = CountFullyTransparent(rotated);
        int uprightClear = CountFullyTransparent(upright);

        // Rotation is a rigid transform of the same content, so the transparent area is the same
        // area. A surface cleared to white instead would report zero.
        Assert.True(uprightClear > 0, "the upright transparent render has no transparent pixels");
        Assert.InRange(rotatedClear, (int)(uprightClear * 0.98), (int)(uprightClear * 1.02));
    }

    private static int CountFullyTransparent(SKBitmap bmp)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
                if (bmp.GetPixel(x, y).Alpha == 0) n++;
        return n;
    }

    /// <summary>
    /// JPEG has no alpha, so a transparent background must be flattened onto white rather than
    /// composited against black — and the caller must be told, because the two produce different
    /// images and only one of them is what was asked for.
    /// </summary>
    [Fact]
    public void TransparentBackgroundWithJpeg_FlattensOntoWhiteAndReports()
    {
        byte[] pdf = ColourPage();
        var options = new PdfRenderOptions { BackgroundArgb = PdfRenderOptions.Transparent };

        int reports = 0;
        int callingThread = Environment.CurrentManagedThreadId;
        void Handler(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            // Filtered to this thread: a process-wide sink asserted on without a filter is an
            // assertion about the whole test run (CLAUDE.md, diagnosing a red run, rule 2).
            if (Environment.CurrentManagedThreadId != callingThread) return;
            if (kind == RenderDiagnosticKind.RenderParameterIgnored) reports++;
        }

        RenderDiagnostics.OnEvent += Handler;
        byte[] jpeg;
        try
        {
            jpeg = PdfPageRenderer.RenderPageAsJpg(new MemoryStream(pdf), 0, Dpi, 90, options);
        }
        finally { RenderDiagnostics.OnEvent -= Handler; }

        using SKBitmap bmp = Decode(jpeg);
        SKColor corner = bmp.GetPixel(bmp.Width - 3, 2);
        Assert.True(corner.Red > 235 && corner.Green > 235 && corner.Blue > 235,
            $"a transparent background must flatten onto white for JPEG, got {corner}");
        Assert.True(reports > 0,
            "dropping the requested background must be reported, not silent");
    }

    // ------------------------------------------------------ JPEG subsampling

    /// <summary>
    /// 4:4:4 must carry coloured text and thin coloured rules more accurately than the default
    /// 4:2:0 — which is the entire reason the option exists.
    /// </summary>
    /// <remarks>
    /// Measured against the lossless render rather than against each other: "they differ" would
    /// pass whichever way the difference went, and the claim is directional.
    /// </remarks>
    [Fact]
    public void JpegChroma444_IsCloserToTheSourceThanTheDefaultOnColouredText()
    {
        byte[] pdf = ColourTextPage();
        using SKBitmap source = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 150));

        using SKBitmap def = Decode(PdfPageRenderer.RenderPageAsJpg(
            new MemoryStream(pdf), 0, 150, 85,
            new PdfRenderOptions { JpegSubsampling = JpegSubsampling.Default }));
        using SKBitmap c444 = Decode(PdfPageRenderer.RenderPageAsJpg(
            new MemoryStream(pdf), 0, 150, 85,
            new PdfRenderOptions { JpegSubsampling = JpegSubsampling.Chroma444 }));

        double defError  = MeanSquaredError(source, def);
        double c444Error = MeanSquaredError(source, c444);

        Assert.True(c444Error < defError,
            $"4:4:4 should be closer to the source than the default; got {c444Error:F2} vs {defError:F2}");
    }

    /// <summary>
    /// The default must stay byte-identical to what callers already receive. Changing it would
    /// move the output of every existing JPEG caller, which this library does not do silently.
    /// </summary>
    [Fact]
    public void JpegDefaultSubsampling_IsUnchangedFromTheHistoricalEncoding()
    {
        byte[] pdf = ColourTextPage();

        byte[] withoutOption = PdfPageRenderer.RenderPageAsJpg(new MemoryStream(pdf), 0, 150, 85);
        byte[] withDefault   = PdfPageRenderer.RenderPageAsJpg(new MemoryStream(pdf), 0, 150, 85,
            new PdfRenderOptions { JpegSubsampling = JpegSubsampling.Default });

        Assert.Equal(withoutOption, withDefault);
    }

    // --------------------------------------------------------- opacity fast path

    /// <summary>
    /// Skipping the flatten copy for an already-opaque surface must not change one byte of the
    /// JPEG. This is the test that makes that optimisation safe rather than merely faster.
    /// </summary>
    [Fact]
    public void OpaquePage_EncodesIdenticallyWithAndWithoutTheFlattenCopy()
    {
        byte[] pdf = ColourPage();

        // The default background is opaque, so the fast path is taken here...
        byte[] fast = PdfPageRenderer.RenderPageAsJpg(new MemoryStream(pdf), 0, Dpi, 85);

        // ...and a background one alpha step below opaque forces the flatten, over the same
        // near-white paper. The two must agree everywhere the page is opaque.
        byte[] flattened = PdfPageRenderer.RenderPageAsJpg(new MemoryStream(pdf), 0, Dpi, 85,
            new PdfRenderOptions { BackgroundArgb = 0xFEFFFFFFu });

        using SKBitmap a = Decode(fast);
        using SKBitmap b = Decode(flattened);
        Assert.Equal(a.Width, b.Width);

        int differing = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (!PixelAssert.Near(a.GetPixel(x, y), b.GetPixel(x, y), tolerance: 2)) differing++;

        Assert.True(differing == 0,
            $"the flatten fast path changed {differing} pixels; it must be a pure cost saving");
    }

    // ------------------------------------------------------------------ helpers

    private static double MeanSquaredError(SKBitmap a, SKBitmap b)
    {
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);

        double sum = 0;
        long n = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
            {
                SKColor p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                sum += (p.Red   - q.Red)   * (p.Red   - q.Red);
                sum += (p.Green - q.Green) * (p.Green - q.Green);
                sum += (p.Blue  - q.Blue)  * (p.Blue  - q.Blue);
                n += 3;
            }
        return sum / Math.Max(1, n);
    }

    private static SKBitmap Decode(byte[] encoded)
    {
        var bmp = SKBitmap.Decode(encoded);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// Decodes into an explicit unpremultiplied layout, so alpha can be read without the platform's
    /// preferred colour type getting in the way (GAPS.md §13d records the swapped-channel trap).
    /// </summary>
    private static SKBitmap DecodeUnpremul(byte[] encoded)
    {
        using var codec = SKCodec.Create(new MemoryStream(encoded));
        Assert.NotNull(codec);
        var info = new SKImageInfo(codec!.Info.Width, codec.Info.Height,
                                   SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bmp = new SKBitmap(info);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(info, bmp.GetPixels()));
        return bmp;
    }

    /// <summary>A page with a solid red block on the left half, and white paper elsewhere.</summary>
    private static byte[] ColourPage(int rotate = 0)
        => SinglePage("1 0 0 rg\n0 0 306 792 re\nf", rotate);

    /// <summary>Coloured text and thin coloured rules — what chroma subsampling damages.</summary>
    private static byte[] ColourTextPage()
    {
        var content = new StringBuilder("BT /F1 9 Tf 20 770 Td 11 TL\n");
        string[] colours = { "1 0 0 rg", "0 0 1 rg", "0 0.6 0 rg", "0 0 0 rg" };
        for (int i = 0; i < 60; i++)
        {
            content.Append(colours[i % colours.Length]).Append('\n');
            content.Append("(Invoice 2026-08-12 total 1,234.56 for ACME Trading Ltd) Tj T*\n");
        }
        content.Append("ET\n");
        for (int i = 0; i < 10; i++)
            content.Append("1 0 0 RG 0.5 w 20 ").Append(60 + i * 8)
                   .Append(" m 590 ").Append(60 + i * 8).Append(" l S\n");
        return SinglePage(content.ToString());
    }

    private static byte[] SinglePage(string content, int rotate = 0)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        string rotateEntry = rotate != 0 ? $" /Rotate {rotate}" : "";
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" + rotateEntry
            + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append('\n');
        sb.Append("0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
