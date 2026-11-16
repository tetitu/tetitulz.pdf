// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// A page box written from the <b>opposite corner pair</b> — §7.9.5 permits it — read by both
/// renderers.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists, and what it turned out to pin.</b> Three copies of the
/// normalisation existed, and merging them into <c>PageBoxMetrics</c> asked which fixtures reach
/// it: mutating away each <c>Math.Min</c> and each <c>Math.Abs</c> left all 4 379 Base tests green.
/// The tests below were written for those survivors — and <b>they did not kill them either</b>,
/// which is the more useful result. <c>PdfPageRenderer.ClipToMedia</c> normalises <i>both</i>
/// rectangles before intersecting them and emits a canonical <c>[x0 y0 x1 y1]</c>, so every
/// four-element box reaching <c>PageBoxMetrics</c> through <c>ResolveRenderBox</c> — which is every
/// render entry point, raster and SVG alike — <b>is already upright</b>.</para>
///
/// <para>So these assert the <i>pipeline</i>: an inverted box renders correctly end to end, and
/// both renderers agree about it. That is worth pinning on its own, because the normalisation could
/// be lost from <c>ClipToMedia</c> as easily as from anywhere else. What they do not do is cover
/// <c>PageBoxMetrics</c>'s own guard, and saying so is the honest version —
/// <see cref="AThreeElementInvertedBox_IsStillNormalised"/> is the one case that reaches it.</para>
///
/// <para><b>Both renderers had been bitten by this separately.</b> Subtracting the corners blindly
/// gives a <i>negative</i> extent: the raster path turned that into a 1×1-pixel page through
/// <c>Math.Max(1, Ceiling(negative))</c>, and the SVG path emitted a negative <c>width</c> and
/// <c>viewBox</c> extent no viewer will render. Two bug reports, one cause.</para>
///
/// <para><b>The origin and the extent are asserted separately</b>, because they are separate
/// decisions: an extent taken with <c>Math.Abs</c> is right even when the origin is the wrong
/// corner, and a page of the correct size drawn from the wrong origin comes out blank rather than
/// small.</para>
/// </remarks>
public sealed class InvertedPageBoxTests
{
    // ── the extent ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// An inverted <c>/MediaBox</c> still measures 200×100. Without the <c>Math.Abs</c> the extent
    /// is negative and the raster path clamps it to a single pixel.
    /// </summary>
    [Fact]
    public void RasterRender_OfAnInvertedMediaBox_HasTheUprightExtent()
    {
        using SKBitmap upright = Render(PagePdf("[0 0 200 100]"));
        using SKBitmap inverted = Render(PagePdf("[200 100 0 0]"));

        Assert.True(inverted.Width > 1 && inverted.Height > 1,
            $"the inverted box rendered {inverted.Width}x{inverted.Height}; a negative extent " +
            "clamps to 1x1.");
        Assert.Equal(upright.Width, inverted.Width);
        Assert.Equal(upright.Height, inverted.Height);
    }

    /// <summary>The SVG export agrees, and a negative extent there is unrenderable rather than tiny.</summary>
    [Fact]
    public void SvgExport_OfAnInvertedMediaBox_HasTheUprightExtent()
    {
        XElement upright = Svg(PagePdf("[0 0 200 100]"));
        XElement inverted = Svg(PagePdf("[200 100 0 0]"));

        Assert.Equal(Attr(upright, "width"), Attr(inverted, "width"));
        Assert.Equal(Attr(upright, "height"), Attr(inverted, "height"));

        double[] viewBox = Attr(inverted, "viewBox")
            .Split(' ').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        Assert.True(viewBox[2] > 0, $"viewBox width was {viewBox[2]}");
        Assert.True(viewBox[3] > 0, $"viewBox height was {viewBox[3]}");
    }

    // ── the origin ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// An inverted <c>/CropBox</c> whose lower-left corner is <b>not</b> written first: the content
    /// is placed against the minimum corner, so a rectangle covering the box fills the page. Taking
    /// the first corner pair instead shifts the page 200 points in each axis and the fill lands
    /// entirely off the canvas.
    /// </summary>
    [Fact]
    public void RasterRender_OfAnInvertedCropBox_PlacesContentAtTheMinimumCorner()
    {
        // /CropBox [300 400 100 200] normalises to origin (100, 200), extent 200x200, and the
        // content fills exactly that rectangle in user space.
        using SKBitmap bitmap = Render(PagePdf("[0 0 600 600]", cropBox: "[300 400 100 200]",
            content: "0 g 100 200 200 200 re f\n"));

        SKColor centre = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);

        Assert.True(centre.Red < 64 && centre.Green < 64 && centre.Blue < 64,
            $"the page centre was {centre}; the fill covers the whole normalised CropBox, so a " +
            "page placed at the wrong corner leaves it blank.");
    }

    /// <summary>
    /// The upright spelling of that same CropBox is the control: it must draw the identical page,
    /// so the test above is pinning the normalisation rather than "a big rectangle covers the
    /// middle whatever the origin".
    /// </summary>
    [Fact]
    public void TheUprightCropBoxSpellingDrawsTheSamePage()
    {
        using SKBitmap inverted = Render(PagePdf("[0 0 600 600]", cropBox: "[300 400 100 200]",
            content: "0 g 100 200 200 200 re f\n"));
        using SKBitmap upright = Render(PagePdf("[0 0 600 600]", cropBox: "[100 200 300 400]",
            content: "0 g 100 200 200 200 re f\n"));

        Assert.Equal(upright.Width, inverted.Width);
        Assert.Equal(upright.Height, inverted.Height);
        Assert.Equal(0, DifferingPixels(upright, inverted));
    }

    // ── the element-count gate ──────────────────────────────────────────────────────

    /// <summary>
    /// <b>The one input that reaches <c>PageBoxMetrics</c>' own normalisation.</b> A box with fewer
    /// than four elements is handed back by <c>ClipToMedia</c> <i>unchanged</i> — there is nothing
    /// to intersect — so a three-element box written from the far corner arrives inverted, and the
    /// <c>Math.Min</c> and <c>Math.Abs</c> are what stop it becoming a 1-pixel page.
    /// </summary>
    /// <remarks>
    /// <para><b>This also explains why the vertical pair cannot be tested, and is not a gap.</b>
    /// The only way to bypass <c>ClipToMedia</c>'s normalisation is to have fewer than four
    /// elements — and a box that short has no fourth element to invert, so
    /// <c>PageBoxMetrics</c>' vertical <c>Math.Min</c>/<c>Math.Abs</c> can never see an inverted
    /// input through any render entry point. That is structural unreachability, not coverage debt,
    /// and it is recorded at the code rather than chased with a contrived fixture.</para>
    /// </remarks>
    [Fact]
    public void AThreeElementInvertedBox_IsStillNormalised()
    {
        // [200 0 0]: x runs from 200 down to 0. Width must be 200, not -200, and the origin must
        // be 0, not 200. Height falls back, there being no fourth element. The square sits at the
        // user-space origin, which is the page's bottom-left corner once the box is normalised.
        using SKBitmap bitmap = Render(PagePdf("[200 0 0]", content: "0 g 0 0 20 20 re f\n"));

        // The extent: Math.Abs. A negative width clamps to one pixel.
        Assert.True(bitmap.Width > 1,
            $"the three-element inverted box rendered {bitmap.Width} pixels wide; a negative " +
            "extent clamps to 1.");
        Assert.Equal(200, bitmap.Width);

        // The origin: Math.Min. Taking the first corner puts the page 200 points to the right of
        // where it belongs, so the square falls off the left edge and the corner comes out white.
        SKColor corner = bitmap.GetPixel(10, bitmap.Height - 10);
        Assert.True(corner.Red < 64 && corner.Green < 64 && corner.Blue < 64,
            $"the bottom-left corner was {corner}; the square is drawn at the user-space origin, " +
            "so a page placed at the far corner leaves it off the canvas.");
    }

    /// <summary>
    /// A box with too few elements is not a box: the page falls back to the default size rather
    /// than reading past the end of the array.
    /// </summary>
    [Fact]
    public void ATwoElementBox_FallsBackToTheDefaultPageSize()
    {
        using SKBitmap bitmap = Render(PagePdf("[0 0]"));

        // PdfPageSizes.DefaultWidth/Height at 72 dpi, i.e. US Letter — and emphatically not 1x1.
        Assert.True(bitmap.Width > 100 && bitmap.Height > 100,
            $"a malformed two-element box rendered {bitmap.Width}x{bitmap.Height}.");
    }

    // ---------------------------------------------------------------- helpers

    private static int DifferingPixels(SKBitmap a, SKBitmap b)
    {
        int differing = 0;
        for (int y = 0; y < a.Height; y++)
            for (int x = 0; x < a.Width; x++)
                if (a.GetPixel(x, y) != b.GetPixel(x, y)) differing++;
        return differing;
    }

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        SKBitmap bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        return bitmap;
    }

    private static XElement Svg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0)).Root;
    }

    private static string Attr(XElement svg, string name) => svg.Attribute(name)?.Value ?? "";

    private static byte[] PagePdf(
        string mediaBox, string cropBox = null, string content = "0 g 0 0 10 10 re f\n")
    {
        string crop = cropBox == null ? "" : " /CropBox " + cropBox;
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox " + mediaBox + crop +
                " /Contents 4 0 R /Resources << >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
    }

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.Append($"startxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
