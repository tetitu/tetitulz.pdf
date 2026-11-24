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
/// §12.5.5 (ISO 32000-2, lines 38926–38928 of <c>docs/iso/iso32000-2.txt</c>): an annotation's
/// appearance "shall be composited with a backdrop consisting of the page content along with any
/// previously painted annotations, using the values of the BM, ca and CA entries in the annotation
/// dictionary". Table 166's <c>/BM</c> (38548–38554) is "the blend mode that shall be used when
/// painting the annotation onto the page".
/// </summary>
/// <remarks>
/// <para><b>All three entries are read.</b> Without <c>/BM</c> a <c>/Multiply</c> stamp over text
/// would paint opaque Normal, and <c>/ca</c> is the entry that governs a group composite. Which
/// alpha a group composite takes is decided once, in
/// <c>AnnotationAppearance.CompositeAlpha</c>: §11.6.4.4 (34121–34122) says "the nonstroking alpha
/// constant shall also be applied when painting a transparency group's results onto its backdrop",
/// and Table 166 (38526–38527) makes <c>/CA</c> the nonstroking value when <c>/ca</c> is absent —
/// so the composite alpha is <c>/ca</c>, else <c>/CA</c>, else 1.</para>
///
/// <para>The fixture paints the page black and gives the annotation a yellow appearance. Yellow
/// multiplied onto black is black and yellow painted Normal onto black is yellow, so the pixel can
/// report nothing but the blend mode; at 50 % the green channel lands at 128, which is what the
/// alpha assertions read. The SVG half parses the export and reads the annotation group's
/// attributes.</para>
/// </remarks>
public class AnnotationBlendModeTests
{
    // ------------------------------------------------------------------ appearance stream, raster

    [Fact]
    public void Raster_AMultiplyAppearanceLeavesTheBlackBackdropBlack()
    {
        using var bmp = PixelAssert.Render(WithAppearance("/BM /Multiply"));
        AssertBlack(PixelAssert.At(bmp, 50, 50));
    }

    /// <summary>The control: with no <c>/BM</c> the appearance covers the backdrop.</summary>
    [Fact]
    public void Raster_WithoutABlendModeTheAppearanceCoversTheBackdrop()
    {
        using var bmp = PixelAssert.Render(WithAppearance(""));
        AssertYellow(PixelAssert.At(bmp, 50, 50));
    }

    /// <summary>
    /// Table 166 makes the value a name; an array still goes through the one shared "first
    /// recognised" decision rather than being read as nothing.
    /// </summary>
    [Fact]
    public void Raster_AnArrayValueTakesTheFirstRecognisedName()
    {
        using var bmp = PixelAssert.Render(WithAppearance("/BM [/Foo /Multiply]"));
        AssertBlack(PixelAssert.At(bmp, 50, 50));
    }

    /// <summary>
    /// The composite alpha is <c>/ca</c>, else <c>/CA</c>: yellow over black at 50 % has a green
    /// channel of 128, at 100 % of 255.
    /// </summary>
    [Theory]
    [InlineData("/ca 0.5", 118, 138)]
    [InlineData("/CA 0.5", 118, 138)]
    [InlineData("/CA 0.5 /ca 1", 245, 255)]
    [InlineData("/ca 0.5 /CA 1", 118, 138)]
    public void Raster_TheNonstrokingAlphaGovernsTheComposite(string alphaEntries, int greenLow, int greenHigh)
    {
        using var bmp = PixelAssert.Render(WithAppearance(alphaEntries));
        SKColor c = PixelAssert.At(bmp, 50, 50);
        Assert.InRange(c.Green, greenLow, greenHigh);
        Assert.InRange(c.Blue, 0, 8);
    }

    // ------------------------------------------------------------------ appearance stream, SVG

    [Fact]
    public void Svg_TheAppearanceGroupCarriesTheBlendMode()
        => Assert.Equal("multiply", BlendModeOf(AnnotGroup(WithAppearance("/BM /Multiply"))));

    [Fact]
    public void Svg_WithoutABlendModeTheAppearanceGroupHasNone()
        => Assert.Null(BlendModeOf(AnnotGroup(WithAppearance(""))));

    [Theory]
    [InlineData("/CA 0.5 /ca 0.25", 0.25f)]
    [InlineData("/CA 0.5", 0.5f)]
    [InlineData("/ca 0.5 /CA 1", 0.5f)]
    public void Svg_TheAppearanceGroupOpacityIsTheNonstrokingAlpha(string alphaEntries, float expected)
    {
        XElement group = AnnotGroup(WithAppearance(alphaEntries));
        Assert.Equal(expected, float.Parse(group.Attribute("opacity")!.Value, CultureInfo.InvariantCulture), 3);
    }

    [Fact]
    public void Svg_AnOpaqueAppearanceGroupCarriesNoOpacity()
        => Assert.Null(AnnotGroup(WithAppearance("/CA 0.5 /ca 1")).Attribute("opacity"));

    // ------------------------------------------------------------------ synthesised appearance

    /// <summary>
    /// Table 166's <c>/BM</c> is the mode for "painting the annotation onto the page", so the
    /// synthesised no-<c>/AP</c> drawing composites with it too.
    /// </summary>
    [Fact]
    public void Raster_AMultiplyBlendAppliesToASynthesisedAppearanceToo()
    {
        using var multiplied = PixelAssert.Render(WithoutAppearance("/BM /Multiply"));
        using var normal = PixelAssert.Render(WithoutAppearance(""));

        AssertBlack(PixelAssert.At(multiplied, 50, 50));
        AssertYellow(PixelAssert.At(normal, 50, 50));
    }

    [Fact]
    public void Svg_TheSynthesisedGroupCarriesTheBlendMode()
    {
        Assert.Equal("multiply", BlendModeOf(AnnotGroup(WithoutAppearance("/BM /Multiply"))));
        Assert.Null(BlendModeOf(AnnotGroup(WithoutAppearance(""))));
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertBlack(SKColor c)
        => Assert.True(c.Red < 40 && c.Green < 40 && c.Blue < 40,
            $"expected the black backdrop to survive a Multiply, got {c}");

    private static void AssertYellow(SKColor c)
        => Assert.True(c.Red > 200 && c.Green > 200 && c.Blue < 60,
            $"expected the yellow appearance to cover the backdrop, got {c}");

    /// <summary>The <c>&lt;g data-annot-subtype="Square"&gt;</c> the one annotation exported into.</summary>
    private static XElement AnnotGroup(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        XDocument doc = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        return Assert.Single(doc.Descendants().Where(
            e => e.Attribute("data-annot-subtype")?.Value == "Square"));
    }

    /// <summary>The <c>mix-blend-mode</c> declared in an element's <c>style</c>, or <c>null</c>.</summary>
    private static string? BlendModeOf(XElement e)
    {
        string? style = e.Attribute("style")?.Value;
        if (style == null) return null;
        foreach (string declaration in style.Split(';'))
        {
            string[] parts = declaration.Split(':');
            if (parts.Length == 2 && parts[0].Trim() == "mix-blend-mode") return parts[1].Trim();
        }
        return null;
    }

    /// <summary>
    /// A 100×100 page painted black, with one Square annotation whose <c>/AP</c> fills the whole
    /// page yellow and which carries <paramref name="annotExtras"/>.
    /// </summary>
    private static byte[] WithAppearance(string annotExtras)
    {
        const string ap = "1 1 0 rg 0 0 100 100 re f";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << >> /Annots [5 0 R] >>",
            Stream("", "0 0 0 rg 0 0 100 100 re f"),
            $"<< /Type /Annot /Subtype /Square /Rect [0 0 100 100] /F 4 {annotExtras}" +
            " /AP << /N 6 0 R >> >>",
            Stream("/Type /XObject /Subtype /Form /BBox [0 0 100 100]", ap));
    }

    /// <summary>
    /// The same page with a Square annotation that has no <c>/AP</c> and a yellow interior, so the
    /// synthesised drawing is what lands on the backdrop.
    /// </summary>
    private static byte[] WithoutAppearance(string annotExtras) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << >> /Annots [5 0 R] >>",
            Stream("", "0 0 0 rg 0 0 100 100 re f"),
            "<< /Type /Annot /Subtype /Square /Rect [10 10 90 90] /F 4 /C [1 1 0] /IC [1 1 0]" +
            $" {annotExtras} >>");

    private static string Stream(string dictExtras, string content) =>
        $"<< {dictExtras} /Length {content.Length} >>\nstream\n{content}\nendstream";

    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets)
            sb.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
