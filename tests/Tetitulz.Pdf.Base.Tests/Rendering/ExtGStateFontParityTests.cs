// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
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
/// ISO 32000-1 §8.4.5 Table 58 — the ExtGState <c>/Font</c> entry, asserted in <b>both</b>
/// renderers over one fixture.
///
/// <para><b>What these were written for.</b> The two renderers read the entry in
/// <i>mutually exclusive</i> shapes, so exactly one of them could ever select the font for any
/// given file. Table 58 defines <c>/Font</c> as <c>[fontDictionaryRef size]</c> — an
/// <b>indirect reference to a font dictionary</b>, not a <c>/Resources /Font</c> key.
/// <c>SvgContentWriter</c> read that shape; <c>ContentStreamInterpreter</c> accepted only a
/// <see cref="Tetitulz.Pdf.Core.Objects.PdfName"/> at index 0, so a conformant
/// <c>/Font [6 0 R 24]</c> applied the <i>size</i> and left the <i>face</i> at whatever the last
/// <c>Tf</c> had chosen — the page rendered in one font and exported in another. Both now read the
/// entry through the one shared <c>ExtGStateFont</c>.</para>
///
/// <para><b>Why the fixture uses a Type 3 font for the ExtGState's font.</b> The assertion has to
/// separate "the right font was selected" from "no font was selected" and from "the <c>Tf</c> font
/// stayed in force", on both renderers, without depending on which faces the host has installed.
/// A Type 3 glyph is drawn by executing its <c>/CharProcs</c> stream, so the fixture's <c>/F2</c>
/// paints a solid 24 pt square where <c>/F1</c> paints a 24 pt <c>'A'</c> — a difference in
/// decoded pixels on one side and in element <i>kind</i> on the other, with the advance
/// (24 pt against 6 pt) pinning which font's <c>/Widths</c> were used.</para>
/// </summary>
public sealed class ExtGStateFontParityTests
{
    // ====================================================================
    //  The spec shape: /Font [<font dictionary reference> size]
    // ====================================================================

    /// <summary>
    /// The raster path reads this shape too. The control render — the same page with
    /// the <c>gs</c> removed — is what makes the treatment's assertion mean "F2 was selected"
    /// rather than "something was drawn".
    /// </summary>
    [Fact]
    public void SpecShapedFontArray_SelectsTheReferencedFontDictionary_InRasterRender()
    {
        using SKBitmap withGs = PixelAssert.Render(Pdf(gs: true));
        using SKBitmap without = PixelAssert.Render(Pdf(gs: false));

        // /F2's only glyph is a filled 1000x1000 glyph-space square: 24 pt on a side at the
        // ExtGState's size, lower-left corner on the pen at (50, 50).
        AssertSolidSquare(withGs, x: 50, y: 50, side: 24);

        // Without the gs the page draws /F1's 'A', which reaches neither x = 70 nor y = 70 …
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(without, 70, 70)));
        // … and it does draw something, so the fixture reaches the text machinery either way.
        Assert.True(PixelAssert.CountInk(without) > 0);
    }

    /// <summary>
    /// The SVG exporter already read this shape; the test exists so the pair is asserted together
    /// and neither side can regress alone.
    /// </summary>
    [Fact]
    public void SpecShapedFontArray_SelectsTheReferencedFontDictionary_InSvgExport()
    {
        XDocument withGs = XDocument.Parse(Svg(Pdf(gs: true)));
        XDocument without = XDocument.Parse(Svg(Pdf(gs: false)));

        // A Type 3 glyph is executed, so it exports as its own painted geometry …
        (float x0, float y0, float x1, float y1) = PathBounds(Assert.Single(Elements(withGs, "path")));
        Assert.Equal(50f, x0, 1);
        Assert.Equal(50f, y0, 1);
        Assert.Equal(74f, x1, 1);
        Assert.Equal(74f, y1, 1);
        Assert.Empty(Elements(withGs, "text"));

        // … while /F1 is a non-embedded base-14 font, which exports as a <text> the viewer renders.
        Assert.Equal("A", Assert.Single(Elements(without, "text")).Value);
        Assert.Empty(Elements(without, "path"));
    }

    // ====================================================================
    //  The advance — a defect in which font is selected moves every later glyph
    // ====================================================================

    /// <summary>
    /// The selected font supplies the advance as well as the glyph, so the second glyph's position
    /// is an independent measurement of which font <c>gs</c> chose: /F2 advances 1000/1000 em
    /// (24 pt), /F1 advances 250/1000 (6 pt).
    /// </summary>
    [Fact]
    public void SpecShapedFontArray_AdvancesByTheReferencedFontsWidths_InRasterRender()
    {
        using SKBitmap withGs = PixelAssert.Render(Pdf(gs: true, tail: "(A) Tj "));

        // The second square occupies x 74..98. At /F1's 6 pt advance it would sit at 56..80, so
        // x = 96 is ink only if the 24 pt advance was used …
        Assert.True(PixelAssert.IsInk(PixelAssert.At(withGs, 96, 70)));
        // … and x = 99 is past its right edge, which rules out "the glyph was simply drawn wider".
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(withGs, 99, 70)));
    }

    /// <summary>
    /// The same measurement on the export, against the control's 6 pt. A wrong advance leaves the
    /// glyph that produced it perfectly correct and displaces everything after it, so this is the
    /// assertion a pixel-comparing parity suite structurally cannot make.
    /// </summary>
    [Fact]
    public void SpecShapedFontArray_AdvancesByTheReferencedFontsWidths_InSvgExport()
    {
        List<XElement> glyphs = Elements(XDocument.Parse(Svg(Pdf(gs: true, tail: "(A) Tj "))), "path");
        Assert.Equal(2, glyphs.Count);
        Assert.Equal(24f, PathBounds(glyphs[1]).x0 - PathBounds(glyphs[0]).x0, 1);

        List<XElement> control = Elements(XDocument.Parse(Svg(Pdf(gs: false, tail: "(A) Tj "))), "text");
        Assert.Equal(2, control.Count);
        Assert.Equal(6f, TransformMatrix(control[1])[4] - TransformMatrix(control[0])[4], 1);
    }

    // ====================================================================
    //  The non-conformant shape both renderers tolerate
    // ====================================================================

    /// <summary>
    /// <c>/Font [/F2 24]</c> — a <c>/Resources /Font</c> key where Table 58 calls for a font
    /// dictionary reference. It is not what the spec defines, but it can only mean a resource name,
    /// so both the raster path and the SVG exporter tolerate it.
    /// </summary>
    [Fact]
    public void ResourceNameShapedFontArray_IsStillHonoured_InBothRenderers()
    {
        using SKBitmap raster = PixelAssert.Render(Pdf(gs: true, nameShape: true));
        AssertSolidSquare(raster, x: 50, y: 50, side: 24);

        XDocument svg = XDocument.Parse(Svg(Pdf(gs: true, nameShape: true)));
        Assert.Equal(50f, PathBounds(Assert.Single(Elements(svg, "path"))).x0, 1);
        Assert.Empty(Elements(svg, "text"));
    }

    // ====================================================================
    //  A font that is not a resource of the page — legal, and unreachable here
    // ====================================================================

    /// <summary>
    /// §8.4.5 permits the entry to name a font that is not among the page's font resources, which
    /// is the whole reason the entry exists. Everything downstream of this — the font cache, every
    /// width, decode and glyph query in both renderers — is keyed by resource name, so such a font
    /// cannot be selected. It must then leave the font <b>in force</b> rather than clear it (the
    /// entry sets a font, it never unsets one) while still applying the size, and say so through
    /// <c>RenderDiagnostics</c>.
    /// </summary>
    [Fact]
    public void FontArrayNamingAFontOutsideTheResources_KeepsTheFaceAndAppliesTheSize_InBothRenderers()
    {
        // The `Tf` names the Type 3 font here, so "the face in force survived" and "the face was
        // cleared" are a square and a letter — a difference both renderers can show. A 48 pt
        // square says both halves at once: /F2 is still selected, at the ExtGState's size and not
        // the Tf's 24.
        //
        // Written the other way round — Tf naming the base-14 /F1 — this passed with the face
        // cleared, because a null font resource falls back to the same substitute face /F1
        // resolves to and the pixels are identical. That version was a surviving mutation.
        byte[] pdf = Pdf(gs: true, unreachable: true, tf: "F2");

        using SKBitmap raster = PixelAssert.Render(pdf);
        AssertSolidSquare(raster, x: 50, y: 50, side: 48);

        (float x0, float y0, float x1, float y1) = PathBounds(
            Assert.Single(Elements(XDocument.Parse(Svg(pdf)), "path")));
        Assert.Equal(50f, x0, 1);
        Assert.Equal(50f, y0, 1);
        Assert.Equal(98f, x1, 1);
        Assert.Equal(98f, y1, 1);
    }

    // ====================================================================
    //  Fixture and measurement helpers
    // ====================================================================

    /// <summary>
    /// Requires the inked pixels to be exactly the <paramref name="side"/>-point square whose
    /// lower-left corner is (<paramref name="x"/>, <paramref name="y"/>), and to be <i>solid</i> —
    /// an 'A' shares neither property.
    /// </summary>
    private static void AssertSolidSquare(SKBitmap bmp, float x, float y, float side)
    {
        (int left, int top, int right, int bottom) = PixelAssert.InkBounds(bmp)
            ?? throw new Xunit.Sdk.XunitException("the page is blank");

        Assert.Equal(PixelAssert.X(x), left);
        Assert.Equal(PixelAssert.X(x + side) - 1, right);
        Assert.Equal(PixelAssert.Y(bmp, y), bottom);
        Assert.Equal(PixelAssert.Y(bmp, y + side) + 1, top);

        for (int py = top + 2; py <= bottom - 2; py++)
            for (int px = left + 2; px <= right - 2; px++)
                Assert.True(PixelAssert.IsInk(bmp.GetPixel(px, py)),
                            $"({px},{py}) is not inked, so the glyph is not a solid square");
    }

    /// <summary>Painted elements of one kind, parsed as XML — never substring-matched.</summary>
    private static List<XElement> Elements(XDocument svg, string localName) =>
        svg.Descendants().Where(e => e.Name.LocalName == localName).ToList();

    /// <summary>The six numbers of a <c>transform="matrix(a,b,c,d,e,f)"</c>.</summary>
    private static float[] TransformMatrix(XElement element)
    {
        string transform = element.Attribute("transform")?.Value
                           ?? throw new Xunit.Sdk.XunitException("element carries no transform");
        float[] m = Numbers(transform);
        Assert.Equal(6, m.Length);
        return m;
    }

    /// <summary>The bounding box of a path's <c>d</c>, whose coordinates are already page space.</summary>
    private static (float x0, float y0, float x1, float y1) PathBounds(XElement path)
    {
        float[] n = Numbers(path.Attribute("d")?.Value
                            ?? throw new Xunit.Sdk.XunitException("path carries no d"));
        Assert.True(n.Length >= 2 && n.Length % 2 == 0, "d did not decompose into coordinate pairs");

        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int i = 0; i < n.Length; i += 2)
        {
            x0 = Math.Min(x0, n[i]);     x1 = Math.Max(x1, n[i]);
            y0 = Math.Min(y0, n[i + 1]); y1 = Math.Max(y1, n[i + 1]);
        }
        return (x0, y0, x1, y1);
    }

    private static float[] Numbers(string s) =>
        Regex.Matches(s, @"-?\d+(?:\.\d+)?")
             .Select(m => float.Parse(m.Value, CultureInfo.InvariantCulture))
             .ToArray();

    private static string Svg(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfSvgRenderer.RenderPageToSvg(ms, 0);
    }

    /// <summary>
    /// One page, two fonts and one ExtGState. <c>/F1</c> is the only font a <c>Tf</c> ever names;
    /// <c>/F2</c> is a Type 3 font whose single glyph is a filled em square, so which font the
    /// <c>gs</c> selected is visible in the output of either renderer.
    /// </summary>
    private static byte[] Pdf(bool gs, bool nameShape = false, bool unreachable = false,
                              string tail = "", string tf = "F1") =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R" +
            " /Resources << /Font << /F1 5 0 R /F2 6 0 R >>" +
            " /ExtGState << /GS1 9 0 R >> >> >>",
            Stream("", $"BT /{tf} 24 Tf 1 0 0 1 50 50 Tm " + (gs ? "/GS1 gs " : "") + "(A) Tj " + tail + "ET"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica" +
            " /FirstChar 65 /LastChar 65 /Widths [250] >>",
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 1000 1000]" +
            " /FontMatrix [0.001 0 0 0.001 0 0] /CharProcs 7 0 R" +
            " /Encoding << /Type /Encoding /Differences [65 /square] >>" +
            " /FirstChar 65 /LastChar 65 /Widths [1000] /Resources << >> >>",
            "<< /square 8 0 R >>",
            Stream("", "1000 0 0 0 1000 1000 d1\n0 0 1000 1000 re f"),
            nameShape     ? "<< /Type /ExtGState /Font [/F2 24] >>"
            : unreachable ? "<< /Type /ExtGState /Font [10 0 R 48] >>"
            : "<< /Type /ExtGState /Font [6 0 R 24] >>",
            // Object 10: a font dictionary the ExtGState entry can reach and nothing else can.
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier >>");

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
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
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
