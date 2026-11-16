// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Overprint simulation as the page's separation plates recombined (<see cref="OverprintPreview"/>):
/// which pages take it, how the inks recombine, and that a tile agrees with the page.
/// </summary>
public class OverprintPreviewTests
{
    private const string Overprint = "<< /OP true /op true /OPM 1 >>";

    // ------------------------------------------------------------------ which pages

    /// <summary>
    /// A page overprints when an ExtGState turning it on is reachable by any route — the page, a
    /// form, a tiling pattern's cell, a shading pattern, a Type 3 font, an annotation appearance, a
    /// soft mask's group — and not otherwise.
    /// </summary>
    [Theory]
    [InlineData("page", true)]
    [InlineData("fillOnly", true)]
    [InlineData("form", true)]
    [InlineData("tiling", true)]
    [InlineData("shadingPattern", true)]
    [InlineData("type3", true)]
    [InlineData("annotation", true)]
    [InlineData("annotationStates", true)]
    [InlineData("softMaskGroup", true)]
    [InlineData("none", false)]
    [InlineData("overprintFalse", false)]
    public void APageOverprints_ByEveryRouteAnExtGStateIsReached(string route, bool expected)
    {
        using var doc = PdfDocument.Open(new MemoryStream(RoutePdf(route)));
        Assert.Equal(expected, OverprintPreview.PageOverprints(doc.Pages[0], doc));
    }

    /// <summary>
    /// A resource graph too large to finish is answered yes, after a bounded number of
    /// dictionaries — a preview of a page that does not overprint costs passes, not correctness.
    /// The adversarial fixture: five thousand distinct forms, none overprinting.
    /// </summary>
    [Fact]
    public void AHugeResourceGraph_IsAnsweredYes_WithoutWalkingAllOfIt_Hostile()
    {
        var objs = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            null,
            "<< /Length 0 >>\nstream\n\nendstream",
        };
        var xobjects = new StringBuilder();
        for (int i = 0; i < 5000; i++)
        {
            int num = objs.Count + 1;
            objs.Add("<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /ExtGState << /G << /OP false >> >> >> /Length 0 >>\nstream\n\nendstream");
            xobjects.Append("/X").Append(i).Append(' ').Append(num).Append(" 0 R ");
        }
        objs[2] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << " + xobjects + ">> >> /Contents 4 0 R >>";
        using var doc = PdfDocument.Open(new MemoryStream(Build(objs.ToArray())));
        Assert.True(OverprintPreview.PageOverprints(doc.Pages[0], doc));
    }

    /// <summary>A page that does not overprint renders identically with simulation on and off: it never takes the plates.</summary>
    [Fact]
    public void APageThatDoesNotOverprint_IsRenderedAsUsual()
    {
        byte[] pdf = Pdf("1 0 0 rg 0 0 50 40 re f 0.2 0.4 0.6 0.1 k 50 0 50 40 re f", "");
        var on = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72, new PdfRenderOptions { SimulateOverprint = true });
        var off = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);
        Assert.Equal(off.Pixels, on.Pixels);
    }

    // ------------------------------------------------------------------ the recombination

    /// <summary>
    /// Process inks: cyan, then magenta overprinting it — blue where they meet, as
    /// <see cref="PdfColor.CmykToRgb"/> converts C+M; each alone elsewhere.
    /// </summary>
    [Fact]
    public void ProcessInks_RecombineThroughTheCmykConversion()
    {
        var px = Pixels(Pdf("1 0 0 0 k 0 0 60 40 re f /GS gs 0 1 0 0 k 30 0 70 40 re f", "/ExtGState << /GS " + Overprint + " >>"));
        Assert.Equal((0, 255, 255), px(15));
        Assert.Equal(Rgb(PdfColor.CmykToRgb(1, 1, 0, 0)), px(45));
        Assert.Equal((255, 0, 255), px(80));
    }

    /// <summary>
    /// A spot ink multiplies its appearance — its tint transform through the alternate space — into
    /// what is beneath: a red spot (M+Y alternate) over cyan is black, over paper it is red. In a
    /// <c>/DeviceN</c> space the spot's appearance is the transform with it alone at the tint.
    /// </summary>
    [Theory]
    [InlineData("[/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>]", "1")]
    [InlineData("[/DeviceN [/Black /Spot] /DeviceCMYK 5 0 R]", "0 1")]
    public void ASpotInk_MultipliesItsAppearanceIntoTheBackdrop(string space, string tints)
    {
        const string fn = "{ 0 3 1 roll dup 3 -1 roll }";   // (k, s) -> (0, s, s, k)
        string res = "/ExtGState << /GS " + Overprint + " >> /ColorSpace << /CS0 " + space + " >>";
        var px = Pixels(Pdf($"1 0 0 0 k 0 0 60 40 re f /GS gs /CS0 cs {tints} sc 30 0 70 40 re f", res,
            "<< /FunctionType 4 /Domain [0 1 0 1] /Range [0 1 0 1 0 1 0 1] /Length " + fn.Length + " >>\nstream\n" + fn + "\nendstream"));
        AssertNear((0, 255, 255), px(15), "cyan alone");
        AssertNear((0, 0, 0), px(45), "spot over cyan");
        AssertNear((255, 0, 0), px(80), "spot over paper");
    }

    /// <summary>
    /// An overprinting spot shading over a 50 % black page keeps the black — the case the per-paint
    /// compositor declined: half black times the spot's ramp.
    /// </summary>
    [Fact]
    public void AnOverprintingSpotShading_KeepsTheBlackBeneathIt()
    {
        const string spot = "[/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>]";
        string res = "/ExtGState << /GS " + Overprint + " >> /Shading << /Sh << /ShadingType 2 /ColorSpace " + spot +
                     " /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [1] /N 1 >> /Extend [true true] >> >>";
        var px = Pixels(Pdf("0 0 0 0.5 k 0 0 100 40 re f /GS gs /Sh sh", res));
        // Black at 0.5 is grey 127/128; the spot at full tint is red: (128, 0, 0).
        AssertNear(Rgb(PdfColor.CmykToRgb(0, 0, 0, 0.5f)) with { Item2 = 0, Item3 = 0 }, px(50), "spot over half black");
    }

    /// <summary>
    /// A spot ink reached only through an uncoloured pattern (<c>[/Pattern /CS0]</c>), an image's
    /// own space, or a stencil mask painted in it is recorded with its space too, so its appearance
    /// is its own red over the paper rather than the grey a spot with no space gets.
    /// </summary>
    [Theory]
    [InlineData("pattern")]
    [InlineData("image")]
    [InlineData("stencil")]
    public void ASpotReachedByAnyPaint_KeepsItsOwnAppearance(string paint)
    {
        const string spot = "[/Separation /Spot /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [0 1 1 0] /N 1 >>]";
        string res = "/ExtGState << /GS " + Overprint + " >> /ColorSpace << /CS0 " + spot + " /PCS [/Pattern /CS0] >> " +
                     "/Pattern << /P 5 0 R >> /XObject << /Im 6 0 R /Mk 7 0 R >>";
        string content = paint switch
        {
            "pattern" => "/GS gs /PCS cs 1 /P scn 0 0 100 40 re f",
            "image" => "/GS gs q 100 0 0 40 0 0 cm /Im Do Q",
            _ => "/GS gs /CS0 cs 1 sc q 100 0 0 40 0 0 cm /Mk Do Q",
        };
        var px = Pixels(Pdf(content, res,
            "<< /PatternType 1 /PaintType 2 /TilingType 1 /BBox [0 0 10 10] /XStep 10 /YStep 10 /Resources << >> /Length 14 >>\nstream\n0 0 10 10 re f\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace " + spot + " /BitsPerComponent 8 /Length 1 >>\nstream\nÿ\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true /Length 1 >>\nstream\n\u0000\nendstream"));
        AssertNear((255, 0, 0), px(50), paint);
    }

    // ------------------------------------------------------------------ tiles

    /// <summary>A tile of an overprinting page, simulated, is the page's own pixels for that region — the mosaic agrees.</summary>
    [Fact]
    public void ATile_AgreesWithTheWholePage()
    {
        byte[] pdf = Pdf("1 0 0 0 k 0 0 60 40 re f /GS gs 0 1 0 0 k 30 0 70 40 re f", "/ExtGState << /GS " + Overprint + " >>");
        var options = new PdfRenderOptions { SimulateOverprint = true };
        var page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72, options);
        using var session = PdfRenderSession.Open(new MemoryStream(pdf), options);
        var tile = session.RenderPageTileToPixels(0, 40, 10, 20, 20, 72);
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 20; x++)
            {
                int t = y * tile.Stride + x * 4, p = (y + 10) * page.Stride + (x + 40) * 4;
                Assert.True(tile.Pixels[t] == page.Pixels[p] && tile.Pixels[t + 1] == page.Pixels[p + 1] && tile.Pixels[t + 2] == page.Pixels[p + 2],
                    $"tile ({x},{y}) differs from the page");
            }
        Assert.Equal(Rgb(PdfColor.CmykToRgb(1, 1, 0, 0)), (tile.Pixels[0], tile.Pixels[1], tile.Pixels[2]));   // blue: the tile is the overlap
    }

    // ------------------------------------------------------------------ helpers

    private static (int, int, int) Rgb(SkiaSharp.SKColor c) => (c.Red, c.Green, c.Blue);

    private static void AssertNear((int, int, int) expected, (int, int, int) actual, string what) =>
        Assert.True(Math.Abs(expected.Item1 - actual.Item1) <= 2 && Math.Abs(expected.Item2 - actual.Item2) <= 2
                    && Math.Abs(expected.Item3 - actual.Item3) <= 2, $"{what}: {actual}, expected {expected}");

    /// <summary>The simulated render's pixel at x across the middle row.</summary>
    private static Func<int, (int, int, int)> Pixels(byte[] pdf)
    {
        var page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72, new PdfRenderOptions { SimulateOverprint = true });
        return x => { int i = 20 * page.Stride + x * 4; return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]); };
    }

    private static byte[] Pdf(string content, string resources, params string[] extra) => Build(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 40] /Resources << " + resources + " >> /Contents 4 0 R >>",
        "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
    }.Concat(extra).ToArray());

    /// <summary>A page whose only overprinting ExtGState is reached by <paramref name="route"/>.</summary>
    private static byte[] RoutePdf(string route)
    {
        string page = route switch
        {
            "page" => "/ExtGState << /G " + Overprint + " >>",
            "fillOnly" => "/ExtGState << /G << /op true >> >>",
            "overprintFalse" => "/ExtGState << /G << /OP false /op false >> >>",
            "form" => "/XObject << /X 5 0 R >>",
            "tiling" => "/Pattern << /P 6 0 R >>",
            "shadingPattern" => "/Pattern << /P << /PatternType 2 /Shading << /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 1 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >> /ExtGState " + Overprint + " >> >>",
            "type3" => "/Font << /F << /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1] /FontMatrix [1 0 0 1 0 0] /CharProcs << >> /Encoding << /Differences [] >> /FirstChar 0 /LastChar 0 /Widths [0] /Resources << /ExtGState << /G " + Overprint + " >> >> >> >>",
            "softMaskGroup" => "/ExtGState << /M << /SMask << /S /Luminosity /G 7 0 R >> >> >>",
            _ => "",
        };
        string annots = route switch
        {
            "annotation" => " /Annots [<< /Type /Annot /Subtype /Square /Rect [0 0 10 10] /AP << /N 5 0 R >> >>]",
            "annotationStates" => " /Annots [<< /Type /Annot /Subtype /Widget /Rect [0 0 10 10] /AS /On /AP << /N << /On 5 0 R /Off 8 0 R >> >> >>]",
            _ => "",
        };
        return Build(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << " + page + " >>" + annots + " /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources << /ExtGState << /G " + Overprint + " >> >> /Length 0 >>\nstream\n\nendstream",
            "<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 1 1] /XStep 1 /YStep 1 /Resources << /ExtGState << /G " + Overprint + " >> >> /Length 0 >>\nstream\n\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Group << /S /Transparency >> /Resources << /ExtGState << /G " + Overprint + " >> >> /Length 0 >>\nstream\n\nendstream",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Length 0 >>\nstream\n\nendstream",
        });
    }

    private static byte[] Build(string[] objs)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new int[objs.Length];
        for (int i = 0; i < objs.Length; i++) { offs[i] = sb.Length; sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n"); }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(objs.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
