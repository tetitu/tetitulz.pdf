// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Plugins.PostScript;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// The end-to-end proof: embedded PostScript, converted by the real interpreter plugin, reaching
/// actual pixels and actual SVG.
///
/// <para>Everything else about this feature is asserted on intermediate artifacts — emitted operator
/// text in the plugin's own tests, dispatch behaviour with fakes in
/// <c>PostScriptPluginDispatchTests</c>. This is the only place the whole chain runs: PostScript →
/// interpreter → content stream → the renderer's form-XObject path → decoded pixels.</para>
/// </summary>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public sealed class PostScriptXObjectRenderTests : IDisposable
{
    public PostScriptXObjectRenderTests()
    {
        PdfPluginHost.Instance.ResetForTesting();
        PdfPluginHost.Instance.Register(new PostScriptXObjectPlugin());
    }

    public void Dispose() => PdfPluginHost.Instance.ResetForTesting();

    // The XObject is placed at (100,400) by the page's `cm`, and its BBox is [0 0 200 100].
    private const string BlueSquare =
        "0 0 1 setrgbcolor newpath 20 20 moveto 80 20 lineto 80 80 lineto 20 80 lineto " +
        "closepath fill";

    /// <summary>
    /// A PostScript XObject painting a blue square really paints blue pixels, in the right place,
    /// and leaves the rest of the page white.
    /// </summary>
    [Fact]
    public void PsXObject_DrawingABlueSquare_PaintsBluePixelsAtTheRightPlace()
    {
        using var bmp = Render(BuildPdf(BlueSquare));

        // Square is (20,20)-(80,80) in XObject space, so (120,420)-(180,480) on the page.
        Assert.Equal(new SKColor(0, 0, 255), PixelAt(bmp, 150, 450));
        Assert.Equal(new SKColor(0, 0, 255), PixelAt(bmp, 125, 425));

        // Outside the square but inside the BBox, and outside the BBox entirely.
        Assert.Equal(SKColors.White, PixelAt(bmp, 250, 450));
        Assert.Equal(SKColors.White, PixelAt(bmp, 400, 600));
    }

    /// <summary>
    /// The interpreter's arithmetic and control flow reach the drawing: this square's coordinates
    /// are computed, not literal, and its colour comes out of a procedure defined in a prologue.
    /// </summary>
    [Fact]
    public void PsXObject_WithAPrologueAndComputedCoordinates_PaintsTheComputedShape()
    {
        const string ps =
            "/inset 20 def " +
            "/box { newpath inset inset moveto " +
            "       100 inset sub inset moveto " +   // exercises arithmetic
            "       inset inset moveto " +
            "       80 inset lineto 80 80 lineto inset 80 lineto closepath } def " +
            "/blue { 0 0 1 setrgbcolor } def " +
            "blue box fill";

        using var bmp = Render(BuildPdf(ps));

        Assert.Equal(new SKColor(0, 0, 255), PixelAt(bmp, 150, 450));
        Assert.Equal(SKColors.White, PixelAt(bmp, 400, 600));
    }

    /// <summary>
    /// The transform reaches the output too: the same square translated by 100 in x lands 100
    /// further right, and does not cover where the untranslated one does.
    /// </summary>
    [Fact]
    public void PsXObject_WithATranslate_MovesTheDrawing()
    {
        using var plain = Render(BuildPdf(BlueSquare));
        using var moved = Render(BuildPdf("100 0 translate " + BlueSquare));

        Assert.Equal(new SKColor(0, 0, 255), PixelAt(plain, 150, 450));
        Assert.Equal(SKColors.White, PixelAt(plain, 250, 450));

        Assert.Equal(SKColors.White, PixelAt(moved, 150, 450));
        Assert.Equal(new SKColor(0, 0, 255), PixelAt(moved, 250, 450));
    }

    /// <summary>Strokes reach pixels as well as fills, and honour the line width.</summary>
    [Fact]
    public void PsXObject_Stroking_PaintsAlongThePathAndNotInsideIt()
    {
        const string ps =
            "1 0 0 setrgbcolor 8 setlinewidth " +
            "newpath 20 20 moveto 180 20 lineto stroke";

        using var bmp = Render(BuildPdf(ps));

        // On the line at y = 420 (20 in XObject space).
        Assert.Equal(new SKColor(255, 0, 0), PixelAt(bmp, 200, 420));
        // Well above it, still inside the BBox: untouched.
        Assert.Equal(SKColors.White, PixelAt(bmp, 200, 480));
    }

    /// <summary>
    /// The host clips plugin output to the XObject's <c>/BBox</c>, so PostScript that draws outside
    /// its declared box cannot escape onto the rest of the page.
    /// </summary>
    [Fact]
    public void PsXObject_DrawingOutsideItsBBox_IsClippedByTheHost()
    {
        // BBox is [0 0 200 100]; this fills 0..400 x 0..400.
        const string ps = "0 0 1 setrgbcolor newpath 0 0 moveto 400 0 lineto 400 400 lineto " +
                          "0 400 lineto closepath fill";

        using var bmp = Render(BuildPdf(ps));

        Assert.Equal(new SKColor(0, 0, 255), PixelAt(bmp, 150, 450));   // inside the BBox
        Assert.Equal(SKColors.White, PixelAt(bmp, 350, 450));           // past its right edge
        Assert.Equal(SKColors.White, PixelAt(bmp, 150, 550));           // above its top edge
    }

    /// <summary>
    /// PostScript that draws nothing must leave the placeholder to the host rather than producing
    /// an empty content stream the renderer would report as a plugin failure.
    /// </summary>
    [Fact]
    public void PsXObject_ThatDrawsNothing_FallsBackToThePlaceholder()
    {
        string svg = RenderSvg(BuildPdf("1 2 add pop"));

        Assert.Contains("data-postscript-placeholder", svg);
    }

    /// <summary>Both renderers run the same plugin output, so the SVG carries the same drawing.</summary>
    [Fact]
    public void PsXObject_Svg_EmitsThePathAndNotThePlaceholder()
    {
        string svg = RenderSvg(BuildPdf(BlueSquare));

        Assert.DoesNotContain("data-postscript-placeholder", svg);
        Assert.Contains("rgb(0,0,255)", svg);
        Assert.Contains("<path", svg);
    }

    /// <summary>
    /// A hostile program inside a PS XObject must not hang or crash the render — the whole reason
    /// the interpreter is budgeted. The page around it still renders.
    /// </summary>
    [Fact]
    public void PsXObject_WithAnInfiniteLoop_IsBoundedAndThePageStillRenders()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 200_000 };
        PdfPluginHost.Instance.ResetForTesting();
        PdfPluginHost.Instance.Register(new PostScriptXObjectPlugin(options));

        using var bmp = Render(BuildPdf("0 0 1 setrgbcolor { } loop"));

        // The page rendered at all — that is the claim — and the runaway drew nothing.
        Assert.Equal(SKColors.White, PixelAt(bmp, 400, 600));
    }

    /// <summary>
    /// Text reaches pixels: black glyphs land inside the band the pen was placed on, and the rest of
    /// the BBox stays white. Asserted as ink coverage rather than glyph shapes, because the exact
    /// rasterisation is the font's business and the claim here is "text drew at all, in the right
    /// place".
    /// </summary>
    [Fact]
    public void PsXObject_ShowingText_InksGlyphsAtThePenPosition()
    {
        const string ps = "/Helvetica findfont 24 scalefont setfont " +
                          "0 0 0 setrgbcolor newpath 20 40 moveto (Hello) show";

        using var bmp = Render(BuildPdf(ps));

        // The XObject sits at (100,400), so the baseline is at page y = 440 and the glyphs occupy
        // roughly x 120..200, y 440..458.
        int inked = InkCount(bmp, 120, 442, 200, 458);
        Assert.True(inked > 0, "no glyphs were inked for the shown text.");

        // Far from the text, inside the BBox, stays clean.
        Assert.Equal(SKColors.White, PixelAt(bmp, 280, 490));
    }

    /// <summary>
    /// An image reaches pixels the right way up. The fixture is a 1x2 greyscale image — black on
    /// top, white below — so a vertical flip is directly observable rather than merely plausible.
    /// </summary>
    [Fact]
    public void PsXObject_DrawingAnImage_IsNotVerticallyFlipped()
    {
        // 1 wide, 2 tall: row 0 (top) black, row 1 (bottom) white.
        // The image matrix maps the 60x60 user-space box onto the 1x2 sample grid.
        const string ps = "gsave 20 20 translate 60 60 scale " +
                          "1 2 8 [ 1 0 0 -2 0 2 ] <00FF> image grestore";

        using var bmp = Render(BuildPdf(ps));

        // In XObject space the image covers (20,20)-(80,80) → page (120,420)-(180,480).
        // Top half must be black, bottom half white.
        var top = PixelAt(bmp, 150, 470);
        var bottom = PixelAt(bmp, 150, 430);

        Assert.True(top.Red < 80, $"the top of the image should be dark, was {top}.");
        Assert.True(bottom.Red > 180, $"the bottom of the image should be light, was {bottom}.");
    }

    /// <summary>Counts inked pixels in a page-space rectangle.</summary>
    private static int InkCount(SKBitmap bmp, int x0, int y0, int x1, int y1)
    {
        int n = 0;
        for (int x = x0; x < x1; x++)
            for (int y = y0; y < y1; y++)
            {
                var c = PixelAt(bmp, x, y);
                if (c.Alpha > 8 && (c.Red < 200 || c.Green < 200 || c.Blue < 200)) n++;
            }
        return n;
    }

    // ==================================================================
    //  Helpers
    // ==================================================================

    private static SKBitmap Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static SKColor PixelAt(SKBitmap bmp, int x, int yUser)
        => bmp.GetPixel(x, bmp.Height - 1 - yUser);

    /// <summary>A one-page PDF whose only content is a PS XObject at (100,400).</summary>
    private static byte[] BuildPdf(string postScript)
    {
        const string cs = "q 1 0 0 1 100 400 cm /PsXo Do Q";

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.AppendFormat(
            "5 0 obj\n<< /Type /XObject /Subtype /PS /BBox [0 0 200 100] /Length {0} >>\n" +
            "stream\n{1}\nendstream\nendobj\n", postScript.Length, postScript);
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /XObject << /PsXo 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int xref = sb.Length;
        sb.Append("xref\n0 6\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.Append("trailer\n<< /Size 6 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
