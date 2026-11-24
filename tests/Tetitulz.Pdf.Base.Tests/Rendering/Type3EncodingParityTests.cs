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
/// Type 3 char code → glyph name (§9.6.6.1) and the missing-glyph rule (§9.6.6.2), where the two
/// renderers were <b>each right about one half and wrong about the other</b>.
///
/// <list type="number">
///   <item><b>Duplicate codes in <c>/Differences</c>.</b> The array is processed sequentially and a
///     later entry replaces an earlier one, so <c>[65 /A 65 /B]</c> maps 65 to <c>B</c>. The raster
///     path returned on the <i>first</i> match and drew <c>A</c>; the SVG writer builds a
///     dictionary, where the last write wins, and drew <c>B</c>. <b>SVG was right.</b></item>
///   <item><b>A code with no <c>/CharProcs</c> entry.</b> A Type 3 font has no glyph but the
///     CharProcs the file supplies, so an undefined code draws nothing. The SVG writer emitted a
///     hairline box "so the missing-glyph slot is visible", putting ink on the page for a glyph the
///     document never defined. <b>Raster was right.</b></item>
/// </list>
///
/// <para>Each test paints in a colour the other outcome cannot produce, so the assertion is which
/// CharProc ran rather than that something was drawn.</para>
/// </summary>
public sealed class Type3EncodingParityTests
{
    /// <summary>
    /// <c>/Differences [65 /A 65 /B]</c> with CharProc <c>A</c> red and <c>B</c> blue: the glyph
    /// must be blue, not red.
    /// </summary>
    [Fact]
    public void DuplicateDifferencesCode_TheLastEntryWins_InTheRasterPath()
    {
        using var bmp = PixelAssert.Render(Type3Pdf("[65 /A 65 /B]"));
        SKColor c = PixelAssert.At(bmp, 30, 30);

        Assert.True(c.Blue > 150 && c.Red < 100,
            $"/Differences [65 /A 65 /B] must draw CharProc B (blue); got {c}");
    }

    /// <summary>
    /// The control: with no duplicate, code 65 draws <c>A</c>. Without it the test above would pass
    /// for a renderer that always drew the last CharProc in the dictionary.
    /// </summary>
    [Fact]
    public void SingleDifferencesCode_DrawsThatGlyph()
    {
        using var bmp = PixelAssert.Render(Type3Pdf("[65 /A]"));
        SKColor c = PixelAssert.At(bmp, 30, 30);

        Assert.True(c.Red > 150 && c.Blue < 100,
            $"/Differences [65 /A] must draw CharProc A (red); got {c}");
    }

    /// <summary>And the SVG export reaches the same glyph — it always did, and must keep doing.</summary>
    [Fact]
    public void DuplicateDifferencesCode_TheLastEntryWins_InTheSvgExport()
    {
        string svg = RenderSvg(Type3Pdf("[65 /A 65 /B]"));
        var fills = XDocument.Parse(svg).Descendants()
            .Select(e => e.Attribute("fill")?.Value)
            .Where(v => v != null)
            .ToList();

        Assert.Contains("rgb(0,0,255)", fills);
        Assert.DoesNotContain("rgb(255,0,0)", fills);
    }

    // ── the missing-glyph half ───────────────────────────────────────────

    /// <summary>
    /// A code whose name is absent from <c>/CharProcs</c> draws nothing. The SVG export used to
    /// stroke a hairline box for it — ink for a glyph the file never defined.
    /// </summary>
    [Fact]
    public void UndefinedGlyph_DrawsNothing_InTheSvgExport()
    {
        // /Differences maps 66 to /Missing, which /CharProcs does not carry.
        string svg = RenderSvg(Type3Pdf("[66 /Missing]", showByte: 'B'));

        Assert.DoesNotContain(XDocument.Parse(svg).Descendants(),
            e => e.Name.LocalName == "rect" && e.Attribute("stroke") != null);
    }

    /// <summary>The raster path already drew nothing, and must keep doing so.</summary>
    [Fact]
    public void UndefinedGlyph_DrawsNothing_InTheRasterPath()
    {
        using var bmp = PixelAssert.Render(Type3Pdf("[66 /Missing]", showByte: 'B'));
        Assert.True(PixelAssert.IsBlank(bmp),
            "a Type 3 code with no /CharProcs entry must paint nothing (§9.6.6.2)");
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A Type 3 font whose CharProcs <c>A</c> and <c>B</c> paint a red and a blue square, with the
    /// supplied <c>/Differences</c> array, showing one byte.
    /// </summary>
    private static byte[] Type3Pdf(string differences, char showByte = 'A') =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            " /Resources << /Font << /T3 5 0 R >> >> >>",
            Stream("", $"BT /T3 50 Tf 10 10 Td ({showByte}) Tj ET\n"),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1 1]" +
            " /FontMatrix [0.001 0 0 0.001 0 0]" +
            " /CharProcs << /A 6 0 R /B 7 0 R >>" +
            $" /Encoding << /Type /Encoding /Differences {differences} >>" +
            " /FirstChar 65 /LastChar 66 /Widths [1000 1000] /Resources << >> >>",
            Glyph("1 0 0 rg"),
            Glyph("0 0 1 rg"));

    /// <summary>A CharProc that declares its width and paints a filled square.</summary>
    private static string Glyph(string colourOp)
    {
        string body = "1000 0 d0\n" + colourOp + " 0 0 1000 1000 re f\n";
        return $"<< /Length {body.Length} >>\nstream\n{body}endstream";
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
