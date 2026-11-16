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
/// §11.6.3 (ISO 32000-2, lines 33999–34001 of <c>docs/iso/iso32000-2.txt</c>): a <c>/BM</c> value
/// may be an array of names, and "if encountered, a PDF processor shall use the first blend mode
/// in the array that it recognizes (or Normal if it recognizes none of them)".
/// </summary>
/// <remarks>
/// <para><b>Both renderers took element 0.</b> <c>[/Foo /Multiply]</c> — a producer-specific mode
/// followed by the standard one, which is exactly what the array form exists to express — painted
/// Normal in the raster render and in the SVG export alike, so a raster-versus-SVG comparison could
/// never show it. The decision now has one implementation, <c>PdfBlendModeEntry.FirstRecognised</c>,
/// reached from both graphics-state readers and from the annotation compositor.</para>
///
/// <para>The raster half asserts a decoded pixel: yellow multiplied onto black is black, and yellow
/// painted Normal onto black is yellow, so the blend mode is the only thing the pixel can report.
/// The SVG half parses the document and reads the painted element's <c>style</c> attribute.</para>
/// </remarks>
public class BlendModeArrayTests
{
    // ------------------------------------------------------------------ raster

    [Fact]
    public void Raster_TheFirstRecognisedNameInTheArrayIsUsed()
    {
        using var bmp = PixelAssert.Render(Pdf("/BM [/Foo /Multiply]"));
        AssertBlack(PixelAssert.At(bmp, 50, 50));
    }

    /// <summary>The control: the same array with Normal as its first recognised name paints over.</summary>
    [Fact]
    public void Raster_ANormalArrayPaintsOverTheBackdrop()
    {
        using var bmp = PixelAssert.Render(Pdf("/BM [/Foo /Normal]"));
        AssertYellow(PixelAssert.At(bmp, 50, 50));
    }

    /// <summary>
    /// An array none of whose names is recognised means Normal — and <i>sets</i> Normal, replacing a
    /// Multiply already in force, rather than leaving the state alone.
    /// </summary>
    [Fact]
    public void Raster_AnArrayWithNoRecognisedNameResetsAnEarlierBlendModeToNormal()
    {
        using var bmp = PixelAssert.Render(Pdf("/BM [/Foo /Bar]", priorGs: "/BM /Multiply"));
        AssertYellow(PixelAssert.At(bmp, 50, 50));
    }

    /// <summary>The fixture's Multiply really multiplies: a name value still reaches the paint.</summary>
    [Fact]
    public void Raster_ANameValueStillMultiplies()
    {
        using var bmp = PixelAssert.Render(Pdf("/BM /Multiply"));
        AssertBlack(PixelAssert.At(bmp, 50, 50));
    }

    // ------------------------------------------------------------------ SVG

    [Fact]
    public void Svg_TheFirstRecognisedNameInTheArrayIsUsed()
        => Assert.Equal("multiply", BlendModeOfYellowPath(Pdf("/BM [/Foo /Multiply]")));

    [Fact]
    public void Svg_ANormalArrayEmitsNoBlendMode()
        => Assert.Null(BlendModeOfYellowPath(Pdf("/BM [/Foo /Normal]")));

    [Fact]
    public void Svg_AnArrayWithNoRecognisedNameResetsAnEarlierBlendModeToNormal()
        => Assert.Null(BlendModeOfYellowPath(Pdf("/BM [/Foo /Bar]", priorGs: "/BM /Multiply")));

    [Fact]
    public void Svg_ANameValueStillEmitsTheBlendMode()
        => Assert.Equal("multiply", BlendModeOfYellowPath(Pdf("/BM /Multiply")));

    // ------------------------------------------------------------------ helpers

    private static void AssertBlack(SKColor c)
        => Assert.True(c.Red < 40 && c.Green < 40 && c.Blue < 40,
            $"expected the black backdrop to survive a Multiply, got {c}");

    private static void AssertYellow(SKColor c)
        => Assert.True(c.Red > 200 && c.Green > 200 && c.Blue < 60,
            $"expected the yellow fill to cover the backdrop, got {c}");

    /// <summary>
    /// The <c>mix-blend-mode</c> the yellow fill's <c>style</c> attribute carries, or <c>null</c>
    /// when it carries none. Parsed, never substring-matched.
    /// </summary>
    private static string? BlendModeOfYellowPath(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        XDocument doc = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));

        XElement yellow = Assert.Single(doc.Descendants().Where(
            e => e.Name.LocalName == "path" && e.Attribute("fill")?.Value == "rgb(255,255,0)"));

        string? style = yellow.Attribute("style")?.Value;
        if (style == null) return null;

        foreach (string declaration in style.Split(';'))
        {
            string[] parts = declaration.Split(':');
            if (parts.Length == 2 && parts[0].Trim() == "mix-blend-mode") return parts[1].Trim();
        }
        return null;
    }

    /// <summary>
    /// A 100×100 page: a black backdrop, then — after <paramref name="priorGs"/> if given, then the
    /// graphics state under test — a yellow square over its middle.
    /// </summary>
    private static byte[] Pdf(string bmEntry, string? priorGs = null)
    {
        string prior = priorGs != null ? "/GSp gs " : "";
        string content = $"0 0 0 rg 0 0 100 100 re f {prior}/GS1 gs 1 1 0 rg 20 20 60 60 re f";
        string extGState = $"/GS1 << {bmEntry} >>" + (priorGs != null ? $" /GSp << {priorGs} >>" : "");

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            $" /Resources << /ExtGState << {extGState} >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
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
