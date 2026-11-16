// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>PdfRenderOptions.DownsampleImagesToTarget</c> — decoding an encoded image at a reduced size
/// when it is drawn far smaller than its native resolution.
/// <para>
/// Measured motivation: an 8.4 MP source drawn as a 24 pt thumbnail cost <b>85.9 ms and 57 MB</b>
/// against a 14.9 ms floor. The risk is the mirror image of the win — a scaled decode is
/// <b>not</b> bit-identical to a full decode, so the two properties that matter are that the
/// default changes nothing and that switching it on still produces the right picture.
/// </para>
/// </summary>
public class ImageDownsamplingTests
{
    private const int Dpi = 96;

    /// <summary>
    /// Default off, and byte-identical to a render that predates the option. This is the whole
    /// safety argument for shipping it: no existing caller moves.
    /// </summary>
    [Fact]
    public void Default_IsOff_AndOutputIsUnchanged()
    {
        byte[] pdf = LargeJpegDrawnSmall();

        byte[] withDefaults = Render(pdf, null);
        byte[] explicitlyOff = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = false });

        Assert.False(PdfRenderOptions.Default.DownsampleImagesToTarget);
        Assert.Equal(explicitlyOff, withDefaults);
    }

    /// <summary>
    /// With the option on the decode really is reduced — and reduced <i>sensibly</i>: smaller than
    /// the 1024-px source, but still at least the device target it has to cover.
    /// </summary>
    /// <remarks>
    /// Asserting only that a diagnostic fired would pass however absurd the chosen size was, which
    /// is why the reported numbers are parsed. The build guard rejected the weaker form of this
    /// test, correctly.
    /// </remarks>
    [Fact]
    public void WhenOn_TheDecodeIsReducedToASizeThatStillCoversTheTarget()
    {
        // RenderDiagnostics.OnEvent is process-wide, so an unfiltered handler collects events
        // from every test rendering concurrently -- so the assertion below would be about the whole
        // process rather than about this render. The Single-assertion sibling failed exactly once in
        // a whole-solution run, beside a neighbour that legitimately downsampled an image, and
        // passed alone every time; an Empty assertion is the same coupling and the archetype
        // CLAUDE.md names. Filtering to the calling thread removes it; serialising the collection
        // would only hide it.
        int ownThread = Environment.CurrentManagedThreadId;
        var reported = new List<string>();
        void Listen(RenderDiagnosticKind kind, string detail, System.Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.ImageDownsampled) reported.Add(detail);
        }

        RenderDiagnostics.OnEvent += Listen;
        try
        {
            Render(LargeJpegDrawnSmall(), new PdfRenderOptions { DownsampleImagesToTarget = true });
        }
        finally { RenderDiagnostics.OnEvent -= Listen; }

        string detail = Assert.Single(reported);

        // "Image decoded at WxH instead of NxN for a TxT device target."
        var m = System.Text.RegularExpressions.Regex.Match(
            detail, @"decoded at (\d+)x(\d+) instead of (\d+)x(\d+) for a (\d+)x(\d+)");
        Assert.True(m.Success, $"unexpected diagnostic shape: {detail}");

        int decoded = int.Parse(m.Groups[1].Value);
        int native  = int.Parse(m.Groups[3].Value);
        int target  = int.Parse(m.Groups[5].Value);

        Assert.Equal(1024, native);
        Assert.True(decoded < native, $"decoded {decoded} should be below native {native}");
        Assert.True(decoded >= target,
            $"decoded {decoded} must still cover the {target}px device target");
    }

    /// <summary>
    /// A small source is left alone: below the floor the saving is irrelevant and a degraded
    /// thumbnail is not. Pins that the reduction is bounded rather than unconditional.
    /// </summary>
    [Fact]
    public void SmallSource_IsNotDownsampled()
    {
        // RenderDiagnostics.OnEvent is process-wide, so an unfiltered handler collects events
        // from every test rendering concurrently -- so the assertion below would be about the whole
        // process rather than about this render. The Single-assertion sibling failed exactly once in
        // a whole-solution run, beside a neighbour that legitimately downsampled an image, and
        // passed alone every time; an Empty assertion is the same coupling and the archetype
        // CLAUDE.md names. Filtering to the calling thread removes it; serialising the collection
        // would only hide it.
        int ownThread = Environment.CurrentManagedThreadId;
        var reported = new List<string>();
        void Listen(RenderDiagnosticKind kind, string detail, System.Exception ex)
        {
            if (Environment.CurrentManagedThreadId != ownThread) return;
            if (kind == RenderDiagnosticKind.ImageDownsampled) reported.Add(detail);
        }

        RenderDiagnostics.OnEvent += Listen;
        try
        {
            Render(LargeJpegDrawnSmall(imageSize: 64),
                   new PdfRenderOptions { DownsampleImagesToTarget = true });
        }
        finally { RenderDiagnostics.OnEvent -= Listen; }

        Assert.Empty(reported);
    }

    /// <summary>
    /// <b>The one that matters.</b> Downsampling must still draw the right picture — the fixture
    /// is a solid red image, so the drawn area stays red rather than becoming blank, black, or
    /// half-decoded garbage. A reduced decode that produced an empty surface would sail past every
    /// test above.
    /// </summary>
    [Fact]
    public void WhenOn_TheImageStillRendersItsColour()
    {
        byte[] pdf = LargeJpegDrawnSmall();

        using SKBitmap on = Decode(Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));
        using SKBitmap off = Decode(Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = false }));

        // The image is drawn at 40,700 in a 612x792 page, 24pt square.
        SKColor sampledOn = on.GetPixel(X(50), Y(on, 712));
        SKColor sampledOff = off.GetPixel(X(50), Y(off, 712));

        Assert.True(sampledOn.Red > 180 && sampledOn.Green < 80 && sampledOn.Blue < 80,
            $"downsampled render should still be red, was {sampledOn}");
        Assert.True(sampledOff.Red > 180, $"control should be red, was {sampledOff}");
    }

    // ------------------------------------------------------------------
    //  Raw samples (no codec) — decimated before the raw decode branches
    // ------------------------------------------------------------------

    /// <summary>
    /// A raw 8-bpc RGB image drawn far smaller than it is stored must keep its colour when the
    /// samples are decimated. The JPEG path reduces through <c>SKCodec.GetScaledDimensions</c>;
    /// raw samples had no reduction at all until <c>TryDecimateRawSamples</c>.
    /// </summary>
    [Fact]
    public void RawSamples_WhenOn_StillRenderTheirColour()
    {
        byte[] pdf = LargeRawRgbDrawnSmall(512);

        using SKBitmap on  = Decode(Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));
        using SKBitmap off = Decode(Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = false }));

        SKColor sampledOn  = on.GetPixel(X(50), Y(on, 712));
        SKColor sampledOff = off.GetPixel(X(50), Y(off, 712));

        Assert.True(sampledOn.Green > 180 && sampledOn.Red < 80 && sampledOn.Blue < 80,
            $"downsampled raw render should still be green, was {sampledOn}");
        Assert.True(sampledOff.Green > 180, $"control should be green, was {sampledOff}");
    }

    /// <summary>
    /// With the option off, a raw image must be byte-identical to what it was before decimation
    /// existed. The feature is opt-in precisely because nearest-neighbour decimation is visible.
    /// </summary>
    [Fact]
    public void RawSamples_WhenOff_OutputIsUnchanged()
    {
        byte[] pdf = LargeRawRgbDrawnSmall(512);

        byte[] a = Render(pdf, null);
        byte[] b = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = false });

        Assert.Equal(a, b);
    }

    /// <summary>
    /// A source at or below <c>MinDownsampleDimension</c> must not be decimated — the saving is
    /// irrelevant there and the quality loss is not. Compares against the option being off.
    /// </summary>
    [Fact]
    public void RawSamples_SmallSource_IsNotDecimated()
    {
        byte[] pdf = LargeRawRgbDrawnSmall(64);

        byte[] on  = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true });
        byte[] off = Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = false });

        Assert.Equal(off, on);
    }

    /// <summary>
    /// <b>The load-bearing one: decimation must pick existing samples, never average them.</b>
    /// </summary>
    /// <remarks>
    /// <para>Several things downstream read sample <i>values</i> rather than colours — colour-key
    /// <c>/Mask</c> ranges (§8.9.6.4), <c>/Decode</c> remapping, a Separation tint transform, and an
    /// <c>/Indexed</c> palette lookup. An averaging decimator would invent values that were never
    /// in the image.</para>
    /// <para><b>An /Indexed image is what makes that falsifiable.</b> A first attempt used a
    /// colour-key mask over a left-green/right-blue image and <i>the mutation survived</i>:
    /// averaging only differs near the colour boundary, and the test sampled far from it. Here the
    /// image alternates palette index 0 (red) and 2 (blue) every column, so nearest-neighbour at
    /// any even step yields index 0 — pure red — while averaging yields index <b>1</b>, which is
    /// green: a colour present nowhere in the image. Averaging is now impossible to miss.</para>
    /// </remarks>
    [Fact]
    public void RawSamples_DecimationPicksExistingSamples_NeverAverages()
    {
        byte[] pdf = AlternatingIndexedImage(512);

        using SKBitmap on = Decode(Render(pdf, new PdfRenderOptions { DownsampleImagesToTarget = true }));

        SKColor sampled = on.GetPixel(X(50), Y(on, 712));

        Assert.True(sampled.Green < 110,
            $"green is palette index 1, which no pixel uses — decimation averaged indices: {sampled}");
        Assert.True(sampled.Red > 150 && sampled.Blue < 110,
            $"expected palette index 0 (red) at an even decimation step, was {sampled}");
    }

    /// <summary>
    /// An 8-bpc <c>/Indexed</c> image alternating index 0 (red) and index 2 (blue) every column.
    /// Index 1 is green and is deliberately never used, so it can only appear if indices were
    /// averaged. Also exercises the <c>/Indexed</c> stride rule — one index per pixel, whatever
    /// arity the base space has.
    /// </summary>
    private static byte[] AlternatingIndexedImage(int imageSize)
    {
        var samples = new byte[imageSize * imageSize];
        for (int y = 0; y < imageSize; y++)
            for (int x = 0; x < imageSize; x++)
                samples[y * imageSize + x] = (byte)((x % 2 == 0) ? 0 : 2);

        // /Indexed /DeviceRGB 2 <palette>: red, green (unused), blue.
        const string palette = "<FF0000 00FF00 0000FF>";
        return RawImagePdf(samples, imageSize, imageSize,
            $"/ColorSpace [/Indexed /DeviceRGB 2 {palette}] /BitsPerComponent 8",
            "q 24 0 0 24 40 700 cm /Im0 Do Q\n");
    }

    /// <summary>A solid-green raw 8-bpc RGB image, drawn 24pt on the page.</summary>
    private static byte[] LargeRawRgbDrawnSmall(int imageSize)
    {
        var samples = new byte[imageSize * imageSize * 3];
        for (int i = 0; i < samples.Length; i += 3) { samples[i] = 0; samples[i + 1] = 255; samples[i + 2] = 0; }

        return RawImagePdf(samples, imageSize, imageSize,
            "/ColorSpace /DeviceRGB /BitsPerComponent 8",
            "q 24 0 0 24 40 700 cm /Im0 Do Q\n");
    }

    /// <summary>
    /// A raw RGB image whose left half is pure green and right half pure blue, with
    /// <c>/Mask [0 0 255 255 0 0]</c> keying out the green — so the left half is transparent.
    /// </summary>
    private static byte[] HalfMaskedRawImage(int imageSize)
    {
        var samples = new byte[imageSize * imageSize * 3];
        for (int y = 0; y < imageSize; y++)
            for (int x = 0; x < imageSize; x++)
            {
                int i = (y * imageSize + x) * 3;
                bool leftHalf = x < imageSize / 2;
                samples[i]     = 0;
                samples[i + 1] = leftHalf ? (byte)255 : (byte)0;
                samples[i + 2] = leftHalf ? (byte)0   : (byte)255;
            }

        return RawImagePdf(samples, imageSize, imageSize,
            "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Mask [0 0 255 255 0 0]",
            "q 200 0 0 24 40 700 cm /Im0 Do Q\n");
    }

    /// <summary>A one-page PDF whose /Im0 is an uncompressed raw-sample image.</summary>
    private static byte[] RawImagePdf(byte[] samples, int w, int h, string imageExtras, string content)
    {
        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        Emit("%PDF-1.7\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
             + "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObj(5);
        Emit($"<< /Type /XObject /Subtype /Image /Width {w} /Height {h} {imageExtras} "
             + $"/Length {samples.Length} >>\nstream\n");
        acc.AddRange(samples);
        Emit("\nendstream\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }

    // ------------------------------------------------------------------

    private static int X(float userX) => (int)(userX * Dpi / 72f);
    private static int Y(SKBitmap b, float userY) => b.Height - 1 - (int)(userY * Dpi / 72f);

    private static SKBitmap Decode(byte[] png)
    {
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    private static byte[] Render(byte[] pdf, PdfRenderOptions options)
    {
        using var ms = new MemoryStream(pdf);
        return options == null
            ? PdfPageRenderer.RenderPage(ms, 0, Dpi)
            : PdfPageRenderer.RenderPage(ms, 0, Dpi, options);
    }

    /// <summary>
    /// The SVG writer passes a JPEG through as its own bytes — unless a target resolution would
    /// reduce it, when passing it through would embed exactly what the option exists to stop: then
    /// it is decoded for its drawn size and embedded reduced. Off, the bytes go through as before,
    /// and so do a small image's and a wide but short one's, which the shared rule never reduces
    /// (a side of 256 px or less).
    /// </summary>
    [Theory]
    [InlineData(true, 1024, false)]
    [InlineData(false, 1024, true)]
    [InlineData(true, 200, true)]
    [InlineData(true, 1024, true, 200)]   // wide but short: not reducible, as the decoders read it
    public void Svg_AJpegTheTargetWouldReduceIsNotPassedThrough(bool target, int size, bool passedThrough, int height = 0)
    {
        var options = new PdfRenderOptions { DownsampleImagesToTarget = target, SvgImageDpi = target ? Dpi : 0 };
        using var ms = new MemoryStream(LargeJpegDrawnSmall(size, height));
        var doc = System.Xml.Linq.XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0, options));

        string href = null;
        foreach (var e in doc.Descendants())
            if (e.Name.LocalName == "image")
                foreach (var a in e.Attributes())
                    if (a.Name.LocalName == "href") href = a.Value;
        Assert.NotNull(href);

        if (passedThrough)
        {
            Assert.StartsWith("data:image/jpeg;base64,", href, StringComparison.Ordinal);
            return;
        }
        const string png = "data:image/png;base64,";
        Assert.StartsWith(png, href, StringComparison.Ordinal);
        using var embedded = SKBitmap.Decode(Convert.FromBase64String(href.Substring(png.Length)));
        Assert.True(embedded.Width < 1024 && embedded.Width >= 64, $"the JPEG should be decoded for its 32 px target, was {embedded.Width} px");
    }

    /// <summary>A solid-red JPEG of <paramref name="imageSize"/> square, drawn 24pt on the page.</summary>
    private static byte[] LargeJpegDrawnSmall(int imageSize = 1024, int imageHeight = 0)
    {
        if (imageHeight <= 0) imageHeight = imageSize;
        byte[] jpeg = SolidRedJpeg(imageSize, imageHeight);

        const string content = "q 24 0 0 24 40 700 cm /Im0 Do Q\n";

        var acc = new List<byte>();
        var offsets = new Dictionary<int, int>();
        void Emit(string s) => acc.AddRange(Encoding.ASCII.GetBytes(s));
        void BeginObj(int n) { offsets[n] = acc.Count; Emit($"{n} 0 obj\n"); }

        Emit("%PDF-1.4\n");
        BeginObj(1); Emit("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2); Emit("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Emit("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
             + "/Resources << /XObject << /Im0 5 0 R >> >> >>\nendobj\n");
        BeginObj(4);
        Emit($"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        BeginObj(5);
        Emit($"<< /Type /XObject /Subtype /Image /Width {imageSize} /Height {imageHeight} "
             + $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode "
             + $"/Length {jpeg.Length} >>\nstream\n");
        acc.AddRange(jpeg);
        Emit("\nendstream\nendobj\n");

        int xref = acc.Count;
        Emit("xref\n0 6\n0000000000 65535 f \n");
        for (int i = 1; i <= 5; i++) Emit($"{offsets[i]:D10} 00000 n \n");
        Emit($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }

    private static byte[] SolidRedJpeg(int size, int height = 0)
    {
        using var bmp = new SKBitmap(size, height > 0 ? height : size, SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(new SKColor(220, 30, 30));
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }
}
