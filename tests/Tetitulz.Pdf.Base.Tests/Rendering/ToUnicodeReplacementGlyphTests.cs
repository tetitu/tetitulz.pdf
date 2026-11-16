// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §9.6.6.4: a simple TrueType font's glyph is selected through its encoding, and <c>/ToUnicode</c>
/// takes no part in it. InDesign maps its tab-leader periods to U+FFFD in <c>/ToUnicode</c> so they
/// stay out of extracted text; a renderer selecting through that U+FFFD draws the font's
/// <c>.notdef</c> box for every period of a table of contents' leaders.
/// </summary>
public sealed class ToUnicodeReplacementGlyphTests
{
    private const string Leaders = "BT /F1 48 Tf 40 400 Td (........) Tj ET";
    private const string PeriodToReplacement =
        "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n" +
        "/CMapName /Leaders def /CMapType 2 def\n" +
        "1 begincodespacerange <00> <FF> endcodespacerange\n" +
        "1 beginbfchar <2E> <FFFD> endbfchar\n" +
        "endcmap CMapName currentdict /CMap defineresource pop end end";

    [Fact]
    public void Raster_PeriodMappedToReplacementCharacter_DrawsThePeriodItsEncodingNames()
    {
        byte[] ttf = LoadOpenSans();
        var replaced = Render(BuildPdf(Leaders, ttf, PeriodToReplacement));
        var plain = Render(BuildPdf(Leaders, ttf, toUnicode: null));

        Assert.True(InkedPixels(plain) > 0, "the leaders drew nothing, so the comparison would say nothing");
        Assert.Equal(plain.Pixels, replaced.Pixels);
    }

    [Fact]
    public void Svg_PeriodMappedToReplacementCharacter_DrawsThePeriodItsEncodingNames()
    {
        byte[] ttf = LoadOpenSans();
        string[] replaced = GlyphOutlines(BuildPdf(Leaders, ttf, PeriodToReplacement));
        string[] plain = GlyphOutlines(BuildPdf(Leaders, ttf, toUnicode: null));

        Assert.Single(plain);
        Assert.Equal(plain, replaced);
    }

    private static RenderedPage Render(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return PdfOperations.RenderPageToPixels(ms, 0, 72);
    }

    private static int InkedPixels(RenderedPage page)
    {
        int count = 0;
        for (int y = 0; y < page.Height; y++)
            for (int x = 0; x < page.Width; x++)
                if (page.Pixels[y * page.Stride + x * 4] < 128) count++;
        return count;
    }

    // The outlines of the distinct glyphs the page draws, in definition order.
    private static string[] GlyphOutlines(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var svg = XDocument.Parse(PdfSvgRenderer.RenderPageToSvg(ms, 0));
        return svg.Descendants()
            .Where(e => e.Name.LocalName == "path" && e.Parent?.Name.LocalName == "defs"
                && ((string)e.Attribute("id"))?.StartsWith("g", StringComparison.Ordinal) == true)
            .Select(e => (string)e.Attribute("d"))
            .ToArray();
    }

    // Open Sans 1.10 (Apache 2.0, its licence beside it), under Fixtures/OpenSans: its .notdef is a
    // box and its period a dot, so the two glyphs cannot be mistaken for each other.
    internal static byte[] LoadOpenSans()
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "OpenSans", "OpenSans-Regular.ttf"));

    // One page, a non-symbolic WinAnsi TrueType font embedding the program, and a /ToUnicode
    // stream when one is given.
    internal static byte[] BuildPdf(string content, byte[] ttf, string toUnicode)
    {
        var ms = new MemoryStream();
        var offsets = new int[9];
        int count = toUnicode == null ? 8 : 9;
        void W(string s) { var b = Encoding.ASCII.GetBytes(s); ms.Write(b, 0, b.Length); }
        void Obj(int n) => offsets[n] = (int)ms.Position;

        W("%PDF-1.5\n");
        Obj(1); W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj(2); W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj(3); W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
                  " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj(4); W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj(5); W("5 0 obj\n<< /Type /Font /Subtype /TrueType /BaseFont /OpenSans" +
                  " /Encoding /WinAnsiEncoding /FontDescriptor 6 0 R" +
                  (toUnicode == null ? "" : " /ToUnicode 8 0 R") + " >>\nendobj\n");
        Obj(6); W("6 0 obj\n<< /Type /FontDescriptor /FontName /OpenSans /Flags 32" +
                  " /FontBBox [-500 -300 1300 1100] /ItalicAngle 0 /Ascent 1069 /Descent -293" +
                  " /CapHeight 714 /StemV 80 /FontFile2 7 0 R >>\nendobj\n");
        Obj(7); W($"7 0 obj\n<< /Length {ttf.Length} /Length1 {ttf.Length} >>\nstream\n");
        ms.Write(ttf, 0, ttf.Length);
        W("\nendstream\nendobj\n");
        if (toUnicode != null)
        {
            Obj(8); W($"8 0 obj\n<< /Length {toUnicode.Length} >>\nstream\n{toUnicode}\nendstream\nendobj\n");
        }

        int xref = (int)ms.Position;
        W($"xref\n0 {count}\n0000000000 65535 f \n");
        for (int i = 1; i < count; i++) W($"{offsets[i]:D10} 00000 n \n");
        W($"trailer\n<< /Size {count} /Root 1 0 R >>\n");
        W($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
