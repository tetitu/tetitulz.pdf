// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §12.5.5's algorithm (ISO 32000-2, lines 38886–38903 of <c>docs/iso/iso32000-2.txt</c>) maps an
/// appearance onto <c>/Rect</c> in three steps — <c>/BBox</c> through <c>/Matrix</c>, a fit
/// <c>A</c> onto <c>/Rect</c>, then <c>AA = Matrix × A</c> — and none of them is a rotation. Both
/// renderers must agree on that: the turn a widget asks for through <c>/MK /R</c> is already carried
/// by the <c>/Matrix</c> its producer wrote, and applying it a second time is what these tests
/// forbid.
/// </summary>
/// <remarks>
/// <para><b>Both renderers turned the appearance again.</b> They read <c>/MK /R</c> (or a
/// non-standard <c>/Rotate</c> on the annotation) and rotated the fitted appearance about its
/// centre, inside the §12.5.5 fit. But <c>/MK</c> is "additional information for constructing the
/// annotation's appearance stream" (41198–41200): Table 192's <c>/R</c> (41217–41219) instructs the
/// <i>generator</i>, and Table 166 has no <c>/Rotate</c> key at all. A common viewer and another reader write
/// <c>/MK /R 90</c> beside a <c>/Matrix</c> that already turns the appearance; turned again it came
/// out sideways and spilled outside <c>/Rect</c> — in both renderers, so comparing them showed
/// nothing, and the previous version of this file <i>required</i> the second turn.</para>
///
/// <para><b>The fixture is built the way a producer builds a rotated field.</b> <c>/Rect</c>
/// [50 50 110 90] is 60 wide and 40 tall; the appearance's <c>/BBox</c> [0 0 40 60] is the rotated
/// space and <c>/Matrix</c> [0 1 -1 0 60 0] turns it back, so the transformed box is exactly 60×40
/// and the fit is a pure translation. Painting only the lower 20 units of the BBox puts the ink in
/// the <b>right third</b> of <c>/Rect</c> (x 90–110) when drawn once; drawn twice it became a
/// horizontal band across y 80–100, half of it above <c>/Rect</c>. The raster half reads pixels at
/// those places; the SVG half parses the export and measures the painted geometry.</para>
///
/// <para>The direct-versus-indirect parity case the old file carried is gone with the feature it
/// pinned: neither spelling of the key is read at render time any more, so the two cannot differ.
/// The empty spelling below is the control that the answer is the same with no key at all.</para>
/// </remarks>
public class AnnotationRotationParityTests
{
    public static TheoryData<string> RotationSpellings => new()
    {
        " /MK << /R 90 >>",   // Table 192: the widget's rotation, an input to appearance construction
        " /Rotate 90",        // not a Table 166 key, but producers write it and both renderers read it
        "",                   // the control: no key at all
    };

    // ------------------------------------------------------------------ raster

    [Theory]
    [MemberData(nameof(RotationSpellings))]
    public void Raster_TheMatrixSuppliesTheTurnAndTheRotationKeyIsNotAppliedAgain(string rotationEntry)
    {
        using var bmp = PixelAssert.Render(Pdf(rotationEntry, partial: true));

        AssertBlue(PixelAssert.At(bmp, 100, 70), "the right third of /Rect, where the appearance lands drawn once");
        AssertWhite(PixelAssert.At(bmp, 60, 70), "the left of /Rect, which the appearance leaves empty");
        AssertWhite(PixelAssert.At(bmp, 80, 95), "above /Rect, where a second turn put the appearance");
    }

    [Theory]
    [MemberData(nameof(RotationSpellings))]
    public void Raster_AFullAppearanceFillsExactlyItsRect(string rotationEntry)
    {
        using var bmp = PixelAssert.Render(Pdf(rotationEntry, partial: false));

        AssertBlue(PixelAssert.At(bmp, 55, 70), "inside /Rect");
        AssertBlue(PixelAssert.At(bmp, 105, 85), "inside /Rect");
        AssertWhite(PixelAssert.At(bmp, 80, 20), "well outside /Rect");
        AssertWhite(PixelAssert.At(bmp, 80, 95), "just above /Rect");
        Assert.Equal(0, InkOutside(bmp, 50, 50, 110, 90));
    }

    // ------------------------------------------------------------------ SVG

    [Theory]
    [MemberData(nameof(RotationSpellings))]
    public void Svg_TheAppearanceGeometryLandsInTheRightThirdOfTheRect(string rotationEntry)
    {
        var (minX, minY, maxX, maxY) = BlueExtent(ExportSvg(Pdf(rotationEntry, partial: true)));

        Assert.Equal(90f, minX, 2);
        Assert.Equal(110f, maxX, 2);
        Assert.Equal(50f, minY, 2);
        Assert.Equal(90f, maxY, 2);
    }

    [Theory]
    [MemberData(nameof(RotationSpellings))]
    public void Svg_AFullAppearanceCoversExactlyTheRect(string rotationEntry)
    {
        var (minX, minY, maxX, maxY) = BlueExtent(ExportSvg(Pdf(rotationEntry, partial: false)));

        Assert.Equal(50f, minX, 2);
        Assert.Equal(110f, maxX, 2);
        Assert.Equal(50f, minY, 2);
        Assert.Equal(90f, maxY, 2);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertBlue(SKColor c, string where)
        => Assert.True(c.Blue > 200 && c.Red < 60 && c.Green < 60, $"expected blue at {where}, got {c}");

    private static void AssertWhite(SKColor c, string where)
        => Assert.True(!PixelAssert.IsInk(c), $"expected nothing painted at {where}, got {c}");

    /// <summary>Inked pixels outside a user-space rectangle, with a one-point margin for anti-aliasing.</summary>
    private static int InkOutside(SKBitmap bmp, float x0, float y0, float x1, float y1)
    {
        int n = 0;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                float ux = x, uy = bmp.Height - 1 - y;
                bool inside = ux >= x0 - 1 && ux <= x1 + 1 && uy >= y0 - 1 && uy <= y1 + 1;
                if (!inside && PixelAssert.IsInk(bmp.GetPixel(x, y))) n++;
            }
        return n;
    }

    private static string ExportSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0, new PdfRenderOptions());
    }

    /// <summary>
    /// The bounding box of the blue appearance fill, from the coordinates in its <c>d</c> attribute.
    /// The writer folds the appearance's CTM into the emitted coordinates, so the numbers are page
    /// user space and a wrong transform shows up here as a wrong extent.
    /// </summary>
    private static (float minX, float minY, float maxX, float maxY) BlueExtent(string svg)
    {
        XDocument doc = XDocument.Parse(svg);
        XElement blue = Assert.Single(doc.Descendants().Where(
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(0,0,255)"));

        float[] numbers = Regex.Matches(blue.Attribute("d")!.Value, @"-?\d+(?:\.\d+)?")
            .Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToArray();
        Assert.True(numbers.Length >= 6 && numbers.Length % 2 == 0,
            $"the fill should be a closed polygon, got d=\"{blue.Attribute("d")!.Value}\"");

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < numbers.Length; i += 2)
        {
            minX = System.Math.Min(minX, numbers[i]);     maxX = System.Math.Max(maxX, numbers[i]);
            minY = System.Math.Min(minY, numbers[i + 1]); maxY = System.Math.Max(maxY, numbers[i + 1]);
        }
        return (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// A 200×200 page carrying one widget with an appearance whose <c>/Matrix</c> carries a 90°
    /// turn, plus <paramref name="rotationEntry"/> on the annotation dictionary. With
    /// <paramref name="partial"/> the appearance paints the lower third of its BBox — the right
    /// third of <c>/Rect</c> once the matrix has turned it; otherwise it fills the BBox.
    /// </summary>
    private static byte[] Pdf(string rotationEntry, bool partial)
    {
        string ap = partial ? "0 0 1 rg 0 0 40 20 re f" : "0 0 1 rg 0 0 40 60 re f";
        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Annots [5 0 R] >>",
            "<< /Length 3 >>\nstream\nq Q\nendstream",
            "<< /Type /Annot /Subtype /Widget /Rect [50 50 110 90] /F 4 /AP << /N 6 0 R >>"
                + rotationEntry + " >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 40 60] /Matrix [0 1 -1 0 60 0]"
                + $" /Length {ap.Length} >>\nstream\n{ap}\nendstream");
    }

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
