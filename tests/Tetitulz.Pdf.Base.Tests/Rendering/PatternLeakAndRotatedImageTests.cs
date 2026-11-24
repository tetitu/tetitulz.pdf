// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Regression tests for two rendering bugs surfaced by the Cal/Shufersal credit-card
/// statement (raw/דף פירוט דיגיטלי כאל 02-23.pdf):
///
///   1. A QR code drawn as black/white CMYK rectangles rendered as a SOLID magenta
///      square — the banner's shading <c>/Pattern</c> fill stayed active because the
///      <c>k</c>/<c>g</c>/<c>rg</c> colour operators did not clear <c>FillIsPattern</c>,
///      so every following <c>re f</c> kept painting the leftover pattern (PDF §8.6.8).
///
///   2. Credit-card artwork flattened into many rotated image tiles rendered as a torn
///      mosaic — <see cref="PdfRenderContext.DrawBitmap"/> collapsed each image to an
///      axis-aligned <c>SKRect</c>, dropping the rotation/skew in the CTM.
/// </summary>
public class PatternLeakAndRotatedImageTests
{
    private static byte[] BuildPdf(string content, string pageExtra, params string[] extraObjs)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var off = new List<int>();
        void Obj(string b) { off.Add(sb.Length); sb.Append(b); }
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj($"3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R {pageExtra} >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        foreach (var o in extraObjs) Obj(o);
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(off.Count + 1).Append('\n').Append("0000000000 65535 f \n");
        foreach (int o in off) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(off.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        // Latin1 so raw 8-bit image-stream bytes (e.g. 0xFF) survive 1:1 and the xref
        // byte offsets match the StringBuilder char positions.
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static SKBitmap Render(byte[] pdf)
    {
        byte[] png = PdfPageRenderer.RenderPage(new MemoryStream(pdf), 0, 72);
        Assert.NotNull(png);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// The <c>&lt;path&gt;</c> elements that actually paint a fill — the SVG counterpart of
    /// sampling a pixel. Parsed as XML, never substring-matched: this suite substring-matched SVG
    /// for its whole history and missed an export that was not even well-formed.
    /// </summary>
    private static List<XElement> PaintedPaths(XDocument doc) =>
        doc.Descendants()
           .Where(e => e.Name.LocalName == "path")
           .Where(e => (e.Attribute("fill")?.Value ?? "none") != "none")
           .ToList();

    /// <summary>Every non-<c>none</c> <c>fill</c> value in the document, whatever element carries it.</summary>
    private static List<string> FillValues(XDocument doc) =>
        doc.Descendants()
           .Select(e => e.Attribute("fill")?.Value)
           .Where(v => v != null && v != "none")
           .ToList();

    /// <summary>A <c>/PaintType 1</c> tiling pattern whose single cell covers the page.</summary>
    private static string TilingPatternObj(string cell) =>
        "5 0 obj\n<< /Type /Pattern /PatternType 1 /PaintType 1 /TilingType 1 " +
        "/BBox [0 0 100 100] /XStep 100 /YStep 100 " +
        $"/Resources << >> /Length {cell.Length} >>\nstream\n{cell}\nendstream\nendobj\n";

    private const string RedCell     = "1 0 0 rg 0 0 100 100 re f";
    private const string MagentaCell = "1 0 1 0 k 0 0 100 100 re f";

    /// <summary>
    /// <c>/Pattern cs /P0 scn</c>, then <paramref name="solidColour"/>, then fill the page. Pass
    /// <c>""</c> for the control: with nothing in between, the pattern is what must be painted.
    /// </summary>
    private static byte[] StalePatternPdf(string cell, string solidColour) =>
        BuildPdf($"/Pattern cs /P0 scn {solidColour} 0 0 100 100 re f",
                 "/Resources << /Pattern << /P0 5 0 R >> >>",
                 TilingPatternObj(cell));

    /// <summary>The text counterpart: the same stale name, consumed by a glyph run instead of a path.</summary>
    private static byte[] StalePatternTextPdf(string solidColour) =>
        BuildPdf($"/Pattern cs /P0 scn {solidColour} BT /F1 60 Tf 10 25 Td (H) Tj ET",
                 "/Resources << /Pattern << /P0 5 0 R >>" +
                 " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>",
                 TilingPatternObj(RedCell));

    // ---- Bug 1: a tiling-pattern fill must NOT leak through a later solid-colour fill ----

    [Fact]
    public void SolidColorAfterPatternFill_DoesNotPaintPattern()
    {
        // Select the red pattern as the fill, THEN switch to a solid green via `rg`,
        // and fill the whole page.  The page must be green, not red.
        using var bmp = Render(StalePatternPdf(RedCell, "0 1 0 rg"));
        var c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.True(c.Green > 200 && c.Red < 80,
            $"expected solid green (pattern cleared), got ({c.Red},{c.Green},{c.Blue})");
    }

    [Fact]
    public void CmykBlackAfterPatternFill_DoesNotPaintPattern()
    {
        // Reproduces the QR case: a magenta pattern, then a black CMYK rectangle (`k`).
        using var bmp = Render(StalePatternPdf(MagentaCell, "0 0 0 1 k"));
        var c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        // Black, not magenta: red channel must be low (magenta would be R≈255).
        Assert.True(c.Red < 60 && c.Green < 60 && c.Blue < 60,
            $"expected solid black (pattern cleared), got ({c.Red},{c.Green},{c.Blue})");
    }

    // ---- The SVG half: the same documents, the same leak ----
    //
    // The two tests above render RASTER ONLY. A paint gate asking for the NAME alone — no colour
    // operator clears the name — would export the pattern for these very fixtures, where
    // `FillIsPattern && FillPatternName != null` does not. Both sides go through
    // PatternPaint.SelectedName.

    [Fact]
    public void SolidColorAfterPatternFill_ExportsTheSolidColour_NotThePattern()
    {
        var doc = XDocument.Parse(RenderSvg(StalePatternPdf(RedCell, "0 1 0 rg")));

        Assert.Equal("rgb(0,255,0)", Assert.Single(PaintedPaths(doc)).Attribute("fill")!.Value);
        // …and the pattern was not even defined, so nothing can reference it later.
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "pattern");
    }

    [Fact]
    public void CmykBlackAfterPatternFill_ExportsTheSolidColour_NotThePattern()
    {
        var doc = XDocument.Parse(RenderSvg(StalePatternPdf(MagentaCell, "0 0 0 1 k")));

        Assert.Equal("rgb(0,0,0)", Assert.Single(PaintedPaths(doc)).Attribute("fill")!.Value);
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "pattern");
    }

    /// <summary>
    /// The control, and the reason the two negatives above are not vacuous: with <b>no</b> colour
    /// operator in between, the very same fixture must still export the pattern. Deleting the SVG
    /// pattern support outright would satisfy both negatives and fail this.
    /// </summary>
    [Fact]
    public void PatternFillWithNoInterveningColourOperator_StillExportsThePattern()
    {
        var doc = XDocument.Parse(RenderSvg(StalePatternPdf(RedCell, "")));

        Assert.StartsWith("url(#", Assert.Single(
            PaintedPaths(doc).Where(p => p.Ancestors().All(a => a.Name.LocalName != "pattern")))
            .Attribute("fill")!.Value);
        Assert.Contains(doc.Descendants(), e => e.Name.LocalName == "pattern");
    }

    /// <summary>
    /// The text gate is the same defect on a different operator: <c>Tj</c> filled its glyphs from
    /// <c>PatternNameFill</c> alone, so a run drawn after a device colour operator exported the
    /// stale pattern while the raster path filled it with the colour the file asked for.
    /// </summary>
    [Fact]
    public void SolidColorAfterPatternSelected_TextExportsTheSolidColour_NotThePattern()
    {
        var leaked  = FillValues(XDocument.Parse(RenderSvg(StalePatternTextPdf("0 1 0 rg"))));
        var control = FillValues(XDocument.Parse(RenderSvg(StalePatternTextPdf(""))));

        // The control proves the fixture reaches the text pattern gate at all, so the negative
        // below is a claim about the colour operator and not about text never using a pattern.
        Assert.Contains(control, v => v.StartsWith("url(#", StringComparison.Ordinal));

        Assert.DoesNotContain(leaked, v => v.StartsWith("url(#", StringComparison.Ordinal));
        Assert.Contains(leaked, v => v == "rgb(0,255,0)");
    }

    /// <summary>
    /// The third gate: an <c>/ImageMask</c> stencil takes its ink from the current fill, and the
    /// SVG writer decided "is that a pattern?" from the name alone — so a stencil drawn after a
    /// device colour operator was painted through the stale pattern instead of in the colour the
    /// file set. Asserted on the decoded stencil's pixels, not on the element being present.
    /// </summary>
    [Fact]
    public void SolidColorAfterPatternSelected_ImageMaskUsesTheSolidColour_NotThePattern()
    {
        var leaked  = XDocument.Parse(RenderSvg(StalePatternStencilPdf("0 1 0 rg")));
        var control = XDocument.Parse(RenderSvg(StalePatternStencilPdf("")));

        // The control proves the fixture reaches the stencil-under-a-pattern branch at all.
        Assert.Contains(control.Descendants(), e => e.Name.LocalName == "pattern");
        Assert.Contains(control.Descendants(), e => e.Name.LocalName == "mask");

        Assert.DoesNotContain(leaked.Descendants(), e => e.Name.LocalName == "pattern");
        Assert.DoesNotContain(leaked.Descendants(), e => e.Name.LocalName == "mask");

        // 0xA0 = 1010 0000, default stencil sense: a 0 bit paints. So sample (1,0), which is
        // painted, and require the green the `rg` selected rather than the pattern's red.
        using SKBitmap stencil = DecodeFirstEmbeddedImage(leaked);
        var c = stencil.GetPixel(1, 0);
        Assert.Equal(255, c.Alpha);
        Assert.True(c.Green > 200 && c.Red < 80,
            $"stencil painted in the wrong colour: ({c.Red},{c.Green},{c.Blue})");
    }

    /// <summary>A 4x4 <c>/ImageMask</c> drawn over the page, after the same stale pattern name.</summary>
    private static byte[] StalePatternStencilPdf(string solidColour) =>
        BuildPdf($"/Pattern cs /P0 scn {solidColour} q 100 0 0 100 0 0 cm /Im1 Do Q",
                 "/Resources << /Pattern << /P0 5 0 R >> /XObject << /Im1 6 0 R >> >>",
                 TilingPatternObj(RedCell),
                 "6 0 obj\n<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /ImageMask true" +
                 " /BitsPerComponent 1 /Filter /ASCIIHexDecode /Length 9 >>\n" +
                 "stream\nA0A0A0A0>\nendstream\nendobj\n");

    /// <summary>Decodes the first embedded PNG data URI in the document.</summary>
    private static SKBitmap DecodeFirstEmbeddedImage(XDocument doc)
    {
        var image = doc.Descendants().First(e => e.Name.LocalName == "image");
        string href = image.Attributes().First(a => a.Name.LocalName == "href").Value;
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href);
        var bmp = SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// The <c>sc</c> half of §8.6.8, and the one that needs the graphics state fixed rather than
    /// the paint gate: a component-only <c>sc</c> sets a solid colour, so it leaves the Pattern
    /// space, and both <c>OpSc</c>s clear <c>FillIsPattern</c> for that; were one not to, the flag
    /// <i>and</i> the name would both survive and the stale pattern paint even with the gates agreeing.
    /// </summary>
    /// <remarks>
    /// Asserted as "neither renderer paints the pattern's green" rather than as a pixel match: the
    /// two disagree about what a component-only <c>sc</c> in a Pattern space <i>does</i> paint
    /// (§8.6.8 leaves it undefined — raster paints transparent and reports
    /// <c>PatternOperandNoName</c>, SVG infers DeviceRGB from the operand count), which is a
    /// separate question from whether the stale pattern leaks.
    /// </remarks>
    [Fact]
    public void ComponentOnlyScAfterPatternSelected_PaintsNoPatternInEitherRenderer()
    {
        const string greenCell = "0 1 0 rg 0 0 100 100 re f";
        byte[] pdf = BuildPdf("/Pattern cs /P0 scn 1 0 0 sc 0 0 100 100 re f",
                              "/Resources << /Pattern << /P0 5 0 R >> >>",
                              TilingPatternObj(greenCell));

        using var bmp = Render(pdf);
        var c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
        Assert.False(PixelAssert.Near(c, new SKColor(0, 255, 0)),
            $"raster painted the stale pattern's green, got ({c.Red},{c.Green},{c.Blue})");

        var doc = XDocument.Parse(RenderSvg(pdf));
        Assert.DoesNotContain(doc.Descendants(), e => e.Name.LocalName == "pattern");
        Assert.DoesNotContain(FillValues(doc), v => v.StartsWith("url(#", StringComparison.Ordinal));
    }

    // ---- Bug 2: an image under a rotating CTM must actually rotate ----

    // A 2×1 image: left pixel red, right pixel blue (DeviceRGB, 8 bpc).
    private const string TwoPxImage =
        "5 0 obj\n<< /Type /XObject /Subtype /Image /Width 2 /Height 1 " +
        "/ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 6 >>\nstream\n";

    private static string TwoPxImageObj()
    {
        // bytes: FF0000 (red)  0000FF (blue)
        var raw = new byte[] { 0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF };
        var sb = new StringBuilder(TwoPxImage);
        foreach (var b in raw) sb.Append((char)b);
        sb.Append("\nendstream\nendobj\n");
        return sb.ToString();
    }

    [Fact]
    public void Image_NoRotation_RedLeftBlueRight()
    {
        // Baseline (a=80,d=80, no rotation): red on the left, blue on the right.
        string content = "q 80 0 0 80 10 10 cm /Img Do Q";
        byte[] pdf = BuildPdf(content, "/Resources << /XObject << /Img 5 0 R >> >>", TwoPxImageObj());

        using var bmp = Render(pdf);
        var left  = bmp.GetPixel(25, 50);
        var right = bmp.GetPixel(75, 50);
        Assert.True(left.Red  > 180 && left.Blue  < 80, $"left should be red, got ({left.Red},{left.Green},{left.Blue})");
        Assert.True(right.Blue > 180 && right.Red < 80, $"right should be blue, got ({right.Red},{right.Green},{right.Blue})");
    }

    [Fact]
    public void Image_Rotated90_LeftRightBecomesTopBottom()
    {
        // CTM [0 80 -80 0 90 10] rotates the unit square 90°.  The image's horizontal
        // red→blue split must become a VERTICAL split (top vs bottom differ), proving the
        // rotation is honoured. An image squashed into an axis-aligned box with NO rotation
        // would carry the colours left/right, not top/bottom.
        string content = "q 0 80 -80 0 90 10 cm /Img Do Q";
        byte[] pdf = BuildPdf(content, "/Resources << /XObject << /Img 5 0 R >> >>", TwoPxImageObj());

        using var bmp = Render(pdf);

        // Sample inside the rotated footprint (page x∈[10,90], y∈[10,90]).
        int cx = (int)(50f / 100f * bmp.Width);
        var top    = bmp.GetPixel(cx, (int)(20f / 100f * bmp.Height));
        var bottom = bmp.GetPixel(cx, (int)(80f / 100f * bmp.Height));

        bool topRedBottomBlue = top.Red  > 150 && top.Blue  < 100 && bottom.Blue > 150 && bottom.Red  < 100;
        bool topBlueBottomRed = top.Blue > 150 && top.Red   < 100 && bottom.Red  > 150 && bottom.Blue < 100;
        Assert.True(topRedBottomBlue || topBlueBottomRed,
            $"90° rotation must split the colours top/bottom — top=({top.Red},{top.Green},{top.Blue}) " +
            $"bottom=({bottom.Red},{bottom.Green},{bottom.Blue})");
    }
}
