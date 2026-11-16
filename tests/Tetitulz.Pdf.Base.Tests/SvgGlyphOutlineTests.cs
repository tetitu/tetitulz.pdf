// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for the SVG renderer's vector glyph-outline path: embedded fonts are drawn as
/// real glyph &lt;path&gt; outlines (viewer-independent, pixel-identical to the raster
/// renderer) plus an invisible, zero-opacity &lt;text&gt; overlay so the text stays
/// selectable and searchable.  Non-embedded base-14 fonts keep the plain &lt;text&gt; path
/// (the SVG viewer has a matching font), which this also guards against regression.
/// </summary>
public sealed class SvgGlyphOutlineTests
{
    [Fact]
    public void EmbeddedTrueTypeFont_EmitsGlyphOutlinePaths_AndInvisibleSelectableOverlay()
    {
        byte[] ttf = LoadOpenSans();

        const string content = "BT /F1 24 Tf 100 700 Td (Hi) Tj ET";
        byte[] pdf = BuildPdfWithEmbeddedTrueType(content, ttf);

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        // The embedded glyph shapes are emitted as shared vector outlines: each distinct glyph
        // is defined once (<defs><path id="g…">) and instantiated via <use>.
        Assert.Contains("<defs><path id=\"g", svg);
        Assert.Contains("<use xlink:href=\"#g", svg);
        // An invisible, selectable/searchable text overlay carries the Unicode text.
        Assert.Contains("fill-opacity=\"0\"", svg);
        Assert.Contains(">Hi</text>", svg);
    }

    [Fact]
    public void RepeatedGlyph_EmbeddedFont_DefinedOnce_ReferencedManyTimes()
    {
        byte[] ttf = LoadOpenSans();

        // Six identical glyphs ("oooooo") must produce ONE <path> definition and six <use>s.
        const string content = "BT /F1 24 Tf 100 700 Td (oooooo) Tj ET";
        byte[] pdf = BuildPdfWithEmbeddedTrueType(content, ttf);

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        Assert.Equal(1, CountOccurrences(svg, "<defs><path id=\"g"));
        Assert.Equal(6, CountOccurrences(svg, "<use xlink:href=\"#g"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, System.StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    [Fact]
    public void NonEmbeddedBase14Font_KeepsSelectableTextElement_NoInvisibleOverlay()
    {
        const string content = "BT /F1 24 Tf 100 700 Td (Hi) Tj ET";
        byte[] pdf = BuildPdfWithHelvetica(content);

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        // Non-embedded fonts render as a plain <text> (the viewer has Helvetica/Arial) …
        Assert.Contains(">Hi</text>", svg);
        // … and there is no separate invisible outline overlay.
        Assert.DoesNotContain("fill-opacity=\"0\"", svg);
    }

    [Fact]
    public void InvisibleText_EmbeddedFont_EmitsNothingVisible()
    {
        byte[] ttf = LoadOpenSans();

        // Render mode 3 = invisible: no glyph outlines and no overlay should be emitted.
        const string content = "BT /F1 24 Tf 100 700 Td 3 Tr (Hi) Tj ET";
        byte[] pdf = BuildPdfWithEmbeddedTrueType(content, ttf);

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        Assert.DoesNotContain("<use", svg);
        Assert.DoesNotContain("<text", svg);
    }

    // ----------------------------------------------------------------
    //  Helpers
    // ----------------------------------------------------------------

    // Open Sans 1.10 (Apache 2.0, its licence beside it), committed under Fixtures/OpenSans and copied
    // to the output folder: a TrueType program with real outlines for every Latin glyph.
    private static byte[] LoadOpenSans()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenSans", "OpenSans-Regular.ttf"));

    // Builds a minimal single-page PDF whose font embeds the given TrueType program as
    // a /FontFile2.  Offsets are tracked in bytes (the font stream is binary).
    private static byte[] BuildPdfWithEmbeddedTrueType(string content, byte[] ttf)
    {
        var ms = new MemoryStream();
        var offsets = new int[8];
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int n) => offsets[n] = (int)ms.Position;

        W("%PDF-1.5\n");
        Obj(1); W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2); W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj(3); W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj(4); W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj(5); W("5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /OpenSans" +
                  " /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R >>\nendobj\n");
        Obj(6); W("6 0 obj\n<< /Type /FontDescriptor /FontName /OpenSans /Flags 32" +
                  " /FontBBox [-500 -300 1300 1100] /ItalicAngle 0 /Ascent 1069 /Descent -293" +
                  " /CapHeight 714 /StemV 80 /FontFile2 7 0 R >>\nendobj\n");
        Obj(7); W($"7 0 obj\n<< /Length {ttf.Length} /Length1 {ttf.Length} >>\nstream\n");
                ms.Write(ttf, 0, ttf.Length);
                W("\nendstream\nendobj\n");

        int xref = (int)ms.Position;
        W("xref\n0 8\n0000000000 65535 f \n");
        for (int i = 1; i <= 7; i++) W($"{offsets[i]:D10} 00000 n \n");
        W("trailer\n<< /Size 8 /Root 1 0 R >>\n");
        W($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    private static byte[] BuildPdfWithHelvetica(string content)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        int o4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            content.Length, content);
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
