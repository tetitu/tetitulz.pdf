// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §11.6.4's graphics-state soft mask applies to <b>every</b> painting operation, an inline image
/// (<c>BI … ID … EI</c>) included — in both renderers.
///
/// <para><b>The divergence this file guards.</b> <c>SvgContentWriter.RenderImageXObject</c> emits
/// <c>mask="url(#…)"</c> from <c>_gs.SvgMaskId</c> on the element it draws, and <c>OpBi</c>, one file
/// away, must too. The raster path honours the mask on both routes, because both reach
/// <c>PdfRenderContext.DrawBitmap</c> and it goes through <c>DrawWithOptionalSoftMask</c>. Without
/// the attribute one document would render with the image hidden and export with it painted in
/// full — the "one renderer fixed, the other silently disagreeing" class.</para>
///
/// <para><b>Not to be confused with an image's own <c>/SMask</c> entry</b>, which is a different
/// feature that both routes honour. <see cref="ImageSoftMaskParityTests"/> covers the XObject route
/// of the same graphics-state mask; this covers the inline one, and asserts the two routes against
/// each other so neither can change alone.</para>
///
/// <para><b>What is asserted.</b> The raster half reads a decoded pixel off the rendered page. The
/// SVG half parses the export as an <c>XDocument</c>, decodes the embedded PNG to prove the image
/// samples are unchanged by the mask, and then reads the mask reference off the element that carries
/// the placement matrix — resolving it to a <c>&lt;mask&gt;</c> the document actually defines, so a
/// dangling id cannot pass. The control (no <c>gs</c>) must stay <b>unmasked</b> in both, which is
/// what stops the vacuous "always multiply by something" fix.</para>
/// </summary>
public sealed class InlineImageSoftMaskParityTests
{
    /// <summary>The single 8-bpc DeviceGray sample: <c>0x40</c>, which is the printable '@'.</summary>
    private const int Sample = 0x40;

    // ====================================================================
    //  raster — decoded pixels
    // ====================================================================

    /// <summary>
    /// A luminosity soft mask that is black over the whole page evaluates to alpha 0 everywhere, so
    /// the inline image must not reach the page: the centre stays the white background.
    /// </summary>
    [Fact]
    public void RasterRender_AppliesTheExtGStateSoftMaskToAnInlineImage()
    {
        SKColor centre = RasterCentre(InlineImagePdf(applyGs: true));

        Assert.Equal(255, centre.Red);
        Assert.Equal(255, centre.Green);
        Assert.Equal(255, centre.Blue);
    }

    /// <summary>
    /// The control. Without the <c>gs</c> the same inline image paints its sample, so the test above
    /// cannot pass by the image having stopped drawing for some unrelated reason.
    /// </summary>
    [Fact]
    public void RasterRender_WithoutTheSoftMask_StillPaintsTheInlineImage()
    {
        SKColor centre = RasterCentre(InlineImagePdf(applyGs: false));

        Assert.Equal(Sample, centre.Red);
        Assert.Equal(Sample, centre.Green);
        Assert.Equal(Sample, centre.Blue);
    }

    // ====================================================================
    //  SVG — decoded PNG plus the mask reference on the placed element
    // ====================================================================

    /// <summary>
    /// The export must carry the mask on the element it places the inline image with, and that
    /// reference must resolve to a <c>&lt;mask&gt;</c> the document defines. The decoded PNG is
    /// asserted alongside it: the image is masked, not its samples rewritten.
    /// </summary>
    [Fact]
    public void SvgExport_AppliesTheExtGStateSoftMaskToAnInlineImage()
    {
        XElement root = Svg(InlineImagePdf(applyGs: true));

        Assert.Equal(Sample, EmbeddedSample(root));
        Assert.True(DefinesMask(root, MaskIdOnPlacedElement(root)),
            "the inline image's mask reference must resolve to a <mask> in the export");
    }

    /// <summary>
    /// The control, and the reason the rule cannot be "always emit a mask": with no <c>gs</c> in the
    /// content stream there is no soft mask, so the placed element must carry no <c>mask</c>
    /// attribute and the export must define no <c>&lt;mask&gt;</c> at all.
    /// </summary>
    [Fact]
    public void SvgExport_WithoutTheSoftMask_LeavesTheInlineImageUnmasked()
    {
        XElement root = Svg(InlineImagePdf(applyGs: false));

        Assert.Equal(Sample, EmbeddedSample(root));
        Assert.Null(MaskIdOnPlacedElement(root));
        Assert.DoesNotContain(root.Descendants(), e => e.Name.LocalName == "mask");
    }

    // ====================================================================
    //  the two renderers against each other, and the two image routes
    // ====================================================================

    /// <summary>
    /// The parity claim itself: whether the inline image ends up masked must be the same answer in
    /// both renderers. The raster side reads it off a decoded pixel (hidden ⇔ the background shows
    /// through); the SVG side reads it off the resolved mask reference. Both polarities are driven,
    /// so a renderer that always masks and one that never does each fail on one row.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RasterAndSvg_AgreeOnWhetherTheInlineImageIsMasked(bool applyGs)
    {
        byte[] pdf = InlineImagePdf(applyGs);

        bool rasterHid = RasterCentre(pdf).Red == 255;
        XElement root = Svg(pdf);
        string maskId = MaskIdOnPlacedElement(root);
        bool svgMasked = maskId != null && DefinesMask(root, maskId);

        Assert.Equal(applyGs, rasterHid);
        Assert.Equal(rasterHid, svgMasked);
    }

    /// <summary>
    /// The same image, the same soft mask, drawn once inline and once as an image XObject. The
    /// XObject route is the reference — it emits the mask — so this is what stops the two image
    /// routes drifting apart inside the SVG writer.
    ///
    /// <para>The two routes do not emit the same <i>element</i>: an XObject is de-duplicated into
    /// <c>&lt;defs&gt;&lt;image&gt;</c> plus a <c>&lt;use&gt;</c>, an inline image is a single
    /// <c>&lt;image&gt;</c>. The invariant is about the element that carries the placement matrix,
    /// which is why the helper looks for <c>transform</c> rather than for a tag name.</para>
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InlineImageAndImageXObject_AreMaskedAlikeInBothRenderers(bool applyGs)
    {
        XElement inlineRoot  = Svg(InlineImagePdf(applyGs));
        XElement xobjectRoot = Svg(XObjectImagePdf(applyGs));

        Assert.Equal(MaskIdOnPlacedElement(xobjectRoot) != null,
                     MaskIdOnPlacedElement(inlineRoot) != null);

        // …and the raster route agrees with both, on the same file pair.
        Assert.Equal(RasterCentre(XObjectImagePdf(applyGs)).Red,
                     RasterCentre(InlineImagePdf(applyGs)).Red);
    }

    // ====================================================================
    //  measurement
    // ====================================================================

    /// <summary>The pixel at the centre of the 72×72 page, which the image covers entirely.</summary>
    private static SKColor RasterCentre(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using SKBitmap bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));
        Assert.NotNull(bmp);
        return bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
    }

    private static XElement Svg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0)).Root;
    }

    /// <summary>
    /// The grey level in the PNG the export embedded for the image. There is exactly one
    /// <c>&lt;image&gt;</c> in these fixtures — the soft-mask group paints a rectangle, not an
    /// image — so finding it needs no disambiguation.
    /// </summary>
    private static int EmbeddedSample(XElement root)
    {
        XElement image = root.Descendants().Single(e => e.Name.LocalName == "image");
        string href = image.Attributes().Single(a => a.Name.LocalName == "href").Value;

        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href, StringComparison.Ordinal);

        using SKBitmap decoded = SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(decoded);
        SKColor c = decoded.GetPixel(0, 0);
        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Red, c.Blue);
        return c.Red;
    }

    /// <summary>
    /// The id inside the <c>mask="url(#…)"</c> of the element that places the image — the one
    /// carrying the <c>transform</c> matrix, which is the <c>&lt;image&gt;</c> for the inline route
    /// and the <c>&lt;use&gt;</c> for the de-duplicated XObject route. <c>null</c> when that element
    /// carries no mask.
    /// </summary>
    private static string MaskIdOnPlacedElement(XElement root)
    {
        XElement placed = root.Descendants()
            .Single(e => (e.Name.LocalName == "image" || e.Name.LocalName == "use")
                         && e.Attribute("transform") != null);

        string mask = placed.Attribute("mask")?.Value;
        if (mask == null) return null;

        Assert.StartsWith("url(#", mask, StringComparison.Ordinal);
        Assert.EndsWith(")", mask, StringComparison.Ordinal);
        return mask.Substring(5, mask.Length - 6);
    }

    private static bool DefinesMask(XElement root, string id) =>
        id != null && root.Descendants()
            .Any(e => e.Name.LocalName == "mask" && e.Attribute("id")?.Value == id);

    // ====================================================================
    //  fixtures
    // ====================================================================

    /// <summary>
    /// A 72×72 page whose whole area is covered by a 1×1 8-bpc DeviceGray inline image, optionally
    /// under an ExtGState whose <c>/SMask</c> is a luminosity mask painting black — alpha 0 — over
    /// the entire page.
    /// </summary>
    private static byte[] InlineImagePdf(bool applyGs)
    {
        string gs = applyGs ? "/GS0 gs " : "";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GS0 5 0 R >> >> >>",
            Stream("", $"q {gs}72 0 0 72 0 0 cm\nBI /W 1 /H 1 /BPC 8 /CS /G ID @\nEI\nQ\n"),
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 6 0 R /BC [0] >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 72 72]" +
                   " /Group << /Type /Group /S /Transparency /CS /DeviceGray >> /Resources << >>",
                   BlackMask));
    }

    /// <summary>The same image and the same mask, drawn through <c>Do</c> instead.</summary>
    private static byte[] XObjectImagePdf(bool applyGs)
    {
        string gs = applyGs ? "/GS0 gs " : "";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 72 72] /Contents 4 0 R" +
            " /Resources << /ExtGState << /GS0 5 0 R >> /XObject << /Im0 7 0 R >> >> >>",
            Stream("", $"q {gs}72 0 0 72 0 0 cm /Im0 Do Q\n"),
            "<< /Type /ExtGState /SMask << /Type /Mask /S /Luminosity /G 6 0 R /BC [0] >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 72 72]" +
                   " /Group << /Type /Group /S /Transparency /CS /DeviceGray >> /Resources << >>",
                   BlackMask),
            Stream("/Type /XObject /Subtype /Image /Width 1 /Height 1" +
                   " /ColorSpace /DeviceGray /BitsPerComponent 8", "@"));
    }

    /// <summary>
    /// The mask group: black over the whole page. A luminosity mask reads its children's luminance,
    /// so this is alpha 0 everywhere and hides whatever it is applied to. <c>/BC [0]</c> makes the
    /// area outside the group's <c>/BBox</c> read the same, so nothing leaks in at the edges.
    /// </summary>
    private const string BlackMask = "0 g 0 0 72 72 re f\n";

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be Latin-1.</summary>
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
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
