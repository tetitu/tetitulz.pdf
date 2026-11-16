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
/// The graphics-state transfer function (<c>/TR</c>, <c>/TR2</c> — §10.4) applied to <b>paint</b>:
/// path fills, path strokes and text.
///
/// <para><b>It was raster-only.</b> Both writers read <c>/TR2 ?? /TR</c> off the ExtGState and both
/// remapped image samples with it; only the raster path also remapped fill and stroke colours
/// (<c>PdfRenderContext.ApplyAlpha</c>). So a page that installed an inverting transfer function and
/// filled a red rectangle <i>rendered cyan and exported red</i> — same elements, same geometry,
/// opposite colour, no diagnostic. Both now build the table through
/// <c>TransferFunctionLut</c>.</para>
///
/// <para>Every assertion is a colour: an inverting function is the one shape that cannot be
/// mistaken for the identity, and red → cyan cannot be reached by dropping the fill either.</para>
/// </summary>
public sealed class TransferFunctionPaintParityTests
{
    /// <summary><c>out = 1 − t</c> as a Type 2 exponential, applied to all three channels.</summary>
    private const string Invert = "<< /FunctionType 2 /Domain [0 1] /C0 [1] /C1 [0] /N 1 >>";

    // ====================================================================
    //  path fill and stroke
    // ====================================================================

    /// <summary>Red under an inverting transfer function is cyan, in the raster path.</summary>
    [Fact]
    public void InvertingTransferFunction_RemapsAPathFill_InTheRasterPath()
    {
        using var bmp = PixelAssert.Render(TransferPdf(Invert, "1 0 0 rg 10 10 70 70 re f"));
        SKColor c = PixelAssert.At(bmp, 45, 45);

        Assert.InRange(c.Red, 0, 8);
        Assert.InRange(c.Green, 247, 255);
        Assert.InRange(c.Blue, 247, 255);
    }

    /// <summary>
    /// And in the SVG export, which is the half that was missing. The fill attribute is read out of
    /// the parsed document, never substring-matched.
    /// </summary>
    [Fact]
    public void InvertingTransferFunction_RemapsAPathFill_InTheSvgExport()
    {
        string svg = RenderSvg(TransferPdf(Invert, "1 0 0 rg 10 10 70 70 re f"));

        var painted = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Select(e => e.Attribute("fill")?.Value)
            .Where(v => v != null && v != "none")
            .ToList();

        Assert.Equal(new[] { "rgb(0,255,255)" }, painted);
    }

    /// <summary>A stroke is remapped too — a separate code path in both writers.</summary>
    [Fact]
    public void InvertingTransferFunction_RemapsAPathStroke_InBothRenderers()
    {
        const string content = "1 0 0 RG 8 w 10 45 m 80 45 l S";

        using var bmp = PixelAssert.Render(TransferPdf(Invert, content));
        SKColor raster = PixelAssert.At(bmp, 45, 45);
        Assert.InRange(raster.Red, 0, 8);
        Assert.InRange(raster.Green, 247, 255);

        string svg = RenderSvg(TransferPdf(Invert, content));
        var stroked = XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName == "path")
            .Select(e => e.Attribute("stroke")?.Value)
            .Where(v => v != null && v != "none")
            .ToList();
        Assert.Equal(new[] { "rgb(0,255,255)" }, stroked);
    }

    // ====================================================================
    //  order: the function may arrive after the colour
    // ====================================================================

    /// <summary>
    /// §10.4 transfers at paint time, so a <c>gs</c> that installs the function <b>after</b> the
    /// colour operator still changes what is painted. This is why the remap cannot live where the
    /// colour is stored, and it is the ordering a real file uses — <c>gs</c> typically opens a
    /// block, but nothing requires it to.
    /// </summary>
    [Fact]
    public void TransferFunctionSetAfterTheColour_StillAppliesToTheFill()
    {
        // 1 0 0 rg comes first; /GT gs second.
        byte[] pdf = TransferPdf(Invert, "1 0 0 rg /GT gs 10 10 70 70 re f", inlineGs: false);

        using var bmp = PixelAssert.Render(pdf);
        Assert.InRange(PixelAssert.At(bmp, 45, 45).Red, 0, 8);

        string svg = RenderSvg(pdf);
        Assert.Contains("rgb(0,255,255)", XDocument.Parse(svg).Descendants()
            .Select(e => e.Attribute("fill")?.Value).Where(v => v != null));
    }

    // ====================================================================
    //  /Identity clears, and the two renderers agree that it does
    // ====================================================================

    /// <summary>
    /// <c>/TR /Identity</c> clears any active function rather than installing one, so the fill is
    /// the colour the file asked for. Without this the "inverted" assertions above would also pass
    /// for a writer that inverted unconditionally.
    ///
    /// <para><b>This one is a control, and the mutation run says so.</b> Disabling the
    /// <c>IsIdentityName</c> check in the ExtGState handler leaves it green, because
    /// <c>TransferFunctionLut.Build</c> guards the same case a second time and a <c>PdfName</c> is
    /// neither a function dictionary nor an array, so three independent routes all end at "no
    /// remap". It is kept for what it pins in the other direction — that the cyan above comes from
    /// the function rather than from a writer that inverts regardless — not as a claim any single
    /// edit can falsify.</para>
    /// </summary>
    [Fact]
    public void IdentityTransferFunction_LeavesTheColourAlone()
    {
        byte[] pdf = TransferPdf("/Identity", "1 0 0 rg 10 10 70 70 re f");

        using var bmp = PixelAssert.Render(pdf);
        SKColor c = PixelAssert.At(bmp, 45, 45);
        Assert.InRange(c.Red, 247, 255);
        Assert.InRange(c.Green, 0, 8);

        Assert.Contains("rgb(255,0,0)", XDocument.Parse(RenderSvg(pdf)).Descendants()
            .Select(e => e.Attribute("fill")?.Value).Where(v => v != null));
    }

    // ====================================================================
    //  text
    // ====================================================================

    /// <summary>
    /// Text is painted with the fill colour, so it transfers with it. Asserted by ink colour rather
    /// than by glyph presence — a run drawn in the wrong colour has exactly the same shape.
    /// </summary>
    [Fact]
    public void InvertingTransferFunction_RemapsTextFill_InBothRenderers()
    {
        const string content = "1 0 0 rg BT /F1 48 Tf 10 30 Td (III) Tj ET";
        byte[] pdf = TransferPdf(Invert, content, withFont: true);

        using var bmp = PixelAssert.Render(pdf);
        SKColor ink = DarkestPixel(bmp);
        Assert.True(ink.Green > ink.Red + 100,
            $"text should paint cyan under an inverting /TR; darkest ink was {ink}");

        var fills = XDocument.Parse(RenderSvg(pdf)).Descendants()
            .Where(e => e.Name.LocalName == "text" || e.Name.LocalName == "g")
            .Select(e => e.Attribute("fill")?.Value)
            .Where(v => v != null && v != "none")
            .ToList();
        Assert.Contains("rgb(0,255,255)", fills);
    }

    // ====================================================================
    //  helpers
    // ====================================================================

    /// <summary>The pixel with the most non-white ink — where the glyphs are.</summary>
    private static SKColor DarkestPixel(SKBitmap bmp)
    {
        SKColor best = SKColors.White;
        int bestSum = int.MaxValue;
        for (int y = 0; y < bmp.Height; y++)
            for (int x = 0; x < bmp.Width; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                int sum = c.Red + c.Green + c.Blue;
                if (sum < bestSum) { bestSum = sum; best = c; }
            }
        return best;
    }

    /// <summary>
    /// A 90×90 page whose content is <paramref name="content"/>, with <c>/GT</c> in the ExtGState
    /// resources holding <paramref name="trObject"/> as its <c>/TR</c>.
    /// </summary>
    private static byte[] TransferPdf(string trObject, string content,
                                      bool inlineGs = true, bool withFont = false)
    {
        string body = inlineGs ? "/GT gs " + content : content;
        string fontRes = withFont
            ? " /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >>"
            : "";

        bool trIsName = trObject.StartsWith("/", StringComparison.Ordinal);
        string gsDict = trIsName
            ? $"<< /Type /ExtGState /TR {trObject} >>"
            : "<< /Type /ExtGState /TR 6 0 R >>";

        return trIsName
            ? Build(
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
                $" /Resources << /ExtGState << /GT 5 0 R >>{fontRes} >> >>",
                Stream("", body),
                gsDict)
            : Build(
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
                $" /Resources << /ExtGState << /GT 5 0 R >>{fontRes} >> >>",
                Stream("", body),
                gsDict,
                trObject);
    }

    private static string RenderSvg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

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
