// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §7.3.10 lets a number in a box or a matrix be a reference. Read as it stands, it is 0: a form with
/// a singular matrix draws nothing here while every other reader draws it, which is a page that says
/// one thing in this viewer and another everywhere else.
/// </summary>
public sealed class ReferencedNumbersTests
{
    /// <summary>Object 7 is 2, object 8 is 0, object 9 is 50, object 10 is 100, object 11 is 200.</summary>
    private static byte[] Pdf(string content, string resources = "", string pageExtra = "", string mediaBox = "[0 0 200 200]", params string[] extra)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox {mediaBox} /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> {resources} >> {pageExtra} >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "null", "2", "0", "50", "100", "200",
        };
        bodies.AddRange(extra);
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.Append(offset.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static bool Painted(RenderedPage page, int x, int yFromBottom)
    {
        int bytes = page.Stride / page.Width;
        int at = (page.Height - 1 - yFromBottom) * page.Stride + x * bytes;
        return page.Pixels[at] < 128 && page.Pixels[at + 1] < 128 && page.Pixels[at + 2] < 128;
    }

    private static string S(string body, string dict) => $"<< {dict} /Length {body.Length} >>\nstream\n{body}\nendstream";

    private const string Square = "0 0 10 10 re f";

    [Fact]
    public void A_forms_referenced_matrix_places_it()
    {
        // [2 0 0 2 50 50]: the square from (50,50) to (70,70).
        byte[] pdf = Pdf("/X Do", "/XObject << /X 12 0 R >>", extra: S(Square,
            "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Matrix [7 0 R 8 0 R 8 0 R 7 0 R 9 0 R 9 0 R]"));

        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

        Assert.True(Painted(page, 60, 60));
        Assert.False(Painted(page, 5, 5));
        Assert.False(Painted(page, 80, 80));
    }

    [Fact]
    public void A_forms_referenced_box_clips_it()
    {
        // The box is (0,0) to (50,50), so a fill of the whole page paints only that far.
        byte[] pdf = Pdf("/X Do", "/XObject << /X 12 0 R >>", extra: S("0 0 200 200 re f",
            "/Type /XObject /Subtype /Form /BBox [8 0 R 8 0 R 9 0 R 9 0 R]"));

        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

        Assert.True(Painted(page, 25, 25));
        Assert.False(Painted(page, 80, 80));
    }

    [Fact]
    public void Both_renderers_place_a_form_by_its_referenced_matrix()
    {
        byte[] pdf = Pdf("/X Do", "/XObject << /X 12 0 R >>", extra: S(Square,
            "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Matrix [7 0 R 8 0 R 8 0 R 7 0 R 9 0 R 9 0 R]"));
        byte[] direct = Pdf("/X Do", "/XObject << /X 12 0 R >>", extra: S(Square,
            "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Matrix [2 0 0 2 50 50]"));

        string svg = Svg(pdf);
        Assert.Equal(Svg(direct), svg);
        byte[] elsewhere = Pdf("/X Do", "/XObject << /X 12 0 R >>", extra: S(Square,
            "/Type /XObject /Subtype /Form /BBox [0 0 100 100]"));
        Assert.NotEqual(Svg(elsewhere), svg);
        Assert.NotNull(XDocument.Parse(svg).Root);
    }

    private static string Svg(byte[] pdf) => PdfSvgRenderer.RenderPageToSvg(new MemoryStream(pdf), 0);

    [Theory]
    [InlineData("[8 0 R 8 0 R 11 0 R 10 0 R]", 200, 100)]
    [InlineData("[0 0 11 0 R 200]", 200, 200)]
    public void A_page_box_of_references_sizes_the_page(string box, int width, int height)
    {
        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(Pdf(Square, mediaBox: box)), 0, 72);

        Assert.Equal(width, page.Width);
        Assert.Equal(height, page.Height);
    }

    [Fact]
    public void An_inherited_page_box_of_references_sizes_the_page()
    {
        string text = Encoding.Latin1.GetString(Pdf(Square));
        text = text.Replace("/Kids [3 0 R] /Count 1 >>", "/Kids [3 0 R] /Count 1 /MediaBox [8 0 R 8 0 R 10 0 R 10 0 R] >>", StringComparison.Ordinal);
        text = text.Replace("/MediaBox [0 0 200 200] ", "", StringComparison.Ordinal);
        byte[] pdf = Encoding.Latin1.GetBytes(text);   // the table is off now: the reader rebuilds it

        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

        Assert.Equal(100, page.Width);
        Assert.Equal(100, page.Height);
    }

    [Fact]
    public void An_annotations_referenced_appearance_box_and_matrix_fit_it_to_its_rectangle()
    {
        // /BBox (0,0)-(100,100) under a matrix of 2 is 200 wide; /Rect is 50 wide, at 50: all of the
        // appearance lands there, and nothing past it.
        string ap = S("0 0 100 100 re f", "/Type /XObject /Subtype /Form /BBox [8 0 R 8 0 R 10 0 R 10 0 R] /Matrix [7 0 R 8 0 R 8 0 R 7 0 R 8 0 R 8 0 R]");
        byte[] pdf = Pdf("", pageExtra: "/Annots [13 0 R]",
            extra: new[] { ap, "<< /Type /Annot /Subtype /Stamp /F 4 /Rect [9 0 R 9 0 R 10 0 R 10 0 R] /AP << /N 12 0 R >> >>" });

        RenderedPage page = PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

        Assert.True(Painted(page, 75, 75));
        Assert.False(Painted(page, 25, 25));
        Assert.False(Painted(page, 150, 150));
    }

    [Fact]
    public void Both_renderers_go_on_from_where_the_text_reached_after_a_Q()
    {
        // The B is drawn after the A either way; drawn over it, the second page shows one letter's ink.
        byte[] withQ = Pdf("BT /F1 40 Tf 10 100 Td q (A) Tj Q (B) Tj ET");
        byte[] withoutQ = Pdf("BT /F1 40 Tf 10 100 Td (A) Tj (B) Tj ET");

        Assert.Equal(Svg(withoutQ), Svg(withQ));
        Assert.Equal(PdfPageRenderer.RenderPageToPixels(new MemoryStream(withoutQ), 0, 72).Pixels,
                     PdfPageRenderer.RenderPageToPixels(new MemoryStream(withQ), 0, 72).Pixels);
    }

    [Fact]
    public void The_text_extractor_goes_on_from_where_the_text_reached_after_a_Q()
    {
        // Helvetica's A and B are 667 wide, so C starts 16 points on, wherever the Q falls.
        byte[] pdf = Pdf("BT /F1 12 Tf 10 100 Td q (AB) Tj Q (C) Tj ET");

        PdfPageTextLayout layout = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf)).Single();
        PdfTextChar c = layout.Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).Single(g => g.Char == "C");

        Assert.Equal(26f, c.X, 0);
    }

    [Fact]
    public void The_plain_extractor_goes_on_from_where_the_text_reached_after_a_Q()
    {
        byte[] pdf = Pdf("BT /F1 14 Tf 20 100 Td q (SEC) Tj Q (RET) Tj ET");

        Assert.Contains("SECRET", Tetitulz.Pdf.Base.PdfOperations.ExtractText(new MemoryStream(pdf)), StringComparison.Ordinal);
    }

    [Fact]
    public void The_text_extractor_takes_the_font_size_a_graphics_state_sets()
    {
        // At 30 points the A is 20 wide, so the B stands at 30; at the default 12 it would stand at 18.
        byte[] pdf = Pdf("/G gs BT 10 100 Td (AB) Tj ET", "/ExtGState << /G << /Font [5 0 R 30] >> >>");

        PdfPageTextLayout layout = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf)).Single();
        PdfTextChar b = layout.Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).Single(g => g.Char == "B");

        Assert.Equal(30f, b.X, 0);
        Assert.Equal(30f, b.FontSize, 0);
    }

    [Fact]
    public void Search_and_redact_takes_a_whole_word_set_in_a_font_from_a_graphics_state()
    {
        byte[] pdf = Pdf("/G gs BT 20 100 Td (SECRET) Tj ET", "/ExtGState << /G << /Font [5 0 R 14] >> >>");
        using var output = new MemoryStream();

        Assert.Single(Tetitulz.Pdf.Base.Operations.Documents.PdfTextRedactor.Redact(new MemoryStream(pdf), output, new[] { "SECRET" }));

        string shown = string.Concat(Tetitulz.Pdf.Core.Text.ContentStreamParser
            .Parse(PdfDocument.Open(new MemoryStream(output.ToArray())).Pages[0].GetContentStreamData())
            .Where(op => op.Operator == "TJ" || op.Operator == "Tj")
            .SelectMany(op => op.Operands[0] is Tetitulz.Pdf.Core.Objects.PdfArray a ? a.OfType<Tetitulz.Pdf.Core.Objects.PdfString>() : op.Operands.OfType<Tetitulz.Pdf.Core.Objects.PdfString>())
            .Select(s => Encoding.Latin1.GetString(s.RawBytes)));
        Assert.Empty(shown);
    }

    [Fact]
    public void The_text_extractor_places_a_forms_text_by_its_referenced_matrix()
    {
        byte[] pdf = Pdf("/X Do", "/XObject << /X 12 0 R >>", extra: S("BT /F1 12 Tf 10 10 Td (x) Tj ET",
            "/Type /XObject /Subtype /Form /BBox [0 0 100 100] /Resources << /Font << /F1 5 0 R >> >> /Matrix [7 0 R 8 0 R 8 0 R 7 0 R 9 0 R 9 0 R]"));

        PdfPageTextLayout layout = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf)).Single();
        PdfTextChar glyph = layout.Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).Single();

        Assert.Equal(70f, glyph.X, 1);
        Assert.Equal(70f, glyph.Y, 1);
    }

    // ---- An annotation's numbers, a colour space's parameters, a mask's backdrop, each by reference ----

    private static RenderedPage Render(byte[] pdf) => PdfPageRenderer.RenderPageToPixels(new MemoryStream(pdf), 0, 72);

    /// <summary>The page's pixel as red, green, blue (the buffer is RGBA).</summary>
    private static (byte R, byte G, byte B) Rgb(RenderedPage page, int x, int yFromBottom)
    {
        int bytes = page.Stride / page.Width;
        int at = (page.Height - 1 - yFromBottom) * page.Stride + x * bytes;
        return (page.Pixels[at], page.Pixels[at + 1], page.Pixels[at + 2]);
    }

    private static string Annotated(string annotation) => $"<< /Type /Annot /F 4 {annotation} >>";

    [Fact]
    public void A_highlights_referenced_quad_points_place_it()
    {
        // The quad is (50,50) to (100,100), inside a page-sized /Rect.
        byte[] pdf = Pdf("", pageExtra: "/Annots [12 0 R]", extra: Annotated(
            "/Subtype /Highlight /Rect [0 0 200 200] /C [0 0 0] /QuadPoints [9 0 R 10 0 R 10 0 R 10 0 R 9 0 R 9 0 R 10 0 R 9 0 R]"));

        RenderedPage page = Render(pdf);

        Assert.True(Painted(page, 75, 75));
        Assert.False(Painted(page, 150, 150));
    }

    [Theory]
    [InlineData("/Border [8 0 R 8 0 R 8 0 R]", false)]          // a width of 0, by reference
    [InlineData("/Border [0 0 2]", true)]                       // control: the entry draws a border
    [InlineData("/BS << /W 7 0 R >> /Border [0 0 0]", true)]    // /BS decides over /Border (§12.5.4)
    [InlineData("/BS << /W 8 0 R >> /Border [0 0 2]", false)]
    public void Both_renderers_read_a_links_border_width_through_references_with_BS_deciding(string border, bool drawn)
    {
        byte[] pdf = Pdf("", pageExtra: "/Annots [12 0 R]", extra: Annotated($"/Subtype /Link /Rect [50 50 150 150] /C [0 0 0] {border}"));

        Assert.Equal(drawn, Painted(Render(pdf), 50, 100));
        bool outlined = XDocument.Parse(Svg(pdf)).Descendants()
            .Any(e => e.Name.LocalName == "rect" && (string)e.Attribute("fill") == "none" && e.Attribute("stroke-width") != null);
        Assert.Equal(drawn, outlined);
    }

    [Fact]
    public void An_annotations_referenced_colour_components_colour_it()
    {
        // Object 13 is 1: /IC is red, /C blue.
        byte[] pdf = Pdf("", pageExtra: "/Annots [12 0 R]", extra: new[]
        {
            Annotated("/Subtype /Square /Rect [50 50 150 150] /C [8 0 R 8 0 R 13 0 R] /IC [13 0 R 8 0 R 8 0 R] /BS << /W 4 >>"),
            "1",
        });

        var (r, g, b) = Rgb(Render(pdf), 100, 100);
        Assert.True(r > 200 && g < 60 && b < 60, $"the interior is {r},{g},{b}");
        var (br, bg, bb) = Rgb(Render(pdf), 51, 100);
        Assert.True(bb > 200 && br < 60 && bg < 60, $"the border is {br},{bg},{bb}");
    }

    /// <summary>The longest runs of unpainted and of painted columns along the square's bottom edge.</summary>
    private static (int Unpainted, int Painted) EdgeRuns(RenderedPage page)
    {
        int gap = 0, ink = 0, gapRun = 0, inkRun = 0;
        for (int x = 25; x <= 175; x++)
        {
            bool painted = Enumerable.Range(16, 9).Any(y => Painted(page, x, y));
            gapRun = painted ? 0 : gapRun + 1;
            inkRun = painted ? inkRun + 1 : 0;
            gap = Math.Max(gap, gapRun);
            ink = Math.Max(ink, inkRun);
        }
        return (gap, ink);
    }

    [Theory]
    [InlineData("[9 0 R 9 0 R]", true)]   // 50 on, 50 off, by reference
    [InlineData("[50 50]", true)]         // control: the same dash written directly
    [InlineData("[]", false)]             // control: no dash, a solid border
    public void An_annotations_referenced_dash_dashes_its_border(string dash, bool dashed)
    {
        byte[] pdf = Pdf("", pageExtra: "/Annots [12 0 R]", extra: Annotated($"/Subtype /Square /Rect [20 20 180 180] /C [0 0 0] /BS << /W 2 /D {dash} >>"));

        var (gap, ink) = EdgeRuns(Render(pdf));

        Assert.True(ink >= 30, $"the longest painted run is {ink} px");
        if (dashed) Assert.True(gap >= 30, $"the longest gap is {gap} px");
        else Assert.True(gap < 5, $"the longest gap is {gap} px");
    }

    [Fact]
    public void A_calibrated_spaces_referenced_gamma_is_its_gamma()
    {
        static byte[] Cal(string gamma) => Pdf("/CS0 cs 0.5 0.5 0.5 sc 0 0 200 200 re f",
            $"/ColorSpace << /CS0 [/CalRGB << /WhitePoint [0.9505 1 1.089] /Gamma {gamma} >>] >>");

        var byReference = Rgb(Render(Cal("[7 0 R 7 0 R 7 0 R]")), 100, 100);

        Assert.Equal(Rgb(Render(Cal("[2 2 2]")), 100, 100), byReference);
        Assert.NotEqual(Rgb(Render(Cal("[1 1 1]")), 100, 100), byReference);   // the gamma shows at all
    }

    [Fact]
    public void A_calibrated_spaces_referenced_white_point_and_matrix_are_its_own()
    {
        // Objects 12 to 14 are D50's white point; the default a reader falls back to is D65.
        static byte[] Cal(string whitePoint, string matrix) => Pdf("/CS0 cs 0.3 0.6 0.9 sc 0 0 200 200 re f",
            $"/ColorSpace << /CS0 [/CalRGB << /WhitePoint {whitePoint} /Matrix {matrix} >>] >>",
            extra: new[] { "0.9642", "1", "0.8249" });
        const string Halved = "[0.5 0 0 0 0.5 0 0 0 0.5]";

        // The matrix's diagonal is object 14, 0.8249; a reader that cannot see it falls back to the identity.
        var byReference = Rgb(Render(Cal("[12 0 R 13 0 R 14 0 R]", "[14 0 R 8 0 R 8 0 R 8 0 R 14 0 R 8 0 R 8 0 R 8 0 R 14 0 R]")), 100, 100);
        const string Diagonal = "[0.8249 0 0 0 0.8249 0 0 0 0.8249]";

        Assert.Equal(Rgb(Render(Cal("[0.9642 1 0.8249]", Diagonal)), 100, 100), byReference);
        Assert.NotEqual(Rgb(Render(Cal("[0.9505 1 1.089]", Diagonal)), 100, 100), byReference);   // the white point shows
        Assert.NotEqual(Rgb(Render(Cal("[0.9642 1 0.8249]", "[1 0 0 0 1 0 0 0 1]")), 100, 100), byReference);   // the identity differs
        Assert.NotEqual(Rgb(Render(Cal("[0.9642 1 0.8249]", Halved)), 100, 100), byReference);                    // the matrix shows
    }

    [Fact]
    public void AdversarialDash_PastFloatsRange_LeavesTheBorderSolidInBothRenderers()
    {
        static byte[] Dashed(string dash) => Pdf("", pageExtra: "/Annots [12 0 R]",
            extra: Annotated($"/Subtype /Square /Rect [20 20 180 180] /C [0 0 0] /BS << /W 2 /D {dash} >>"));

        // A real, so the value is past float's range (an integer literal saturates to a long).
        byte[] huge = Dashed("[1000000000000000000000000000000000000000.0 50]");
        var (gap, ink) = EdgeRuns(Render(huge));
        Assert.True(ink >= 30 && gap < 5, $"painted {ink} px, gap {gap} px");
        static bool DashAttribute(byte[] pdf) => XDocument.Parse(Svg(pdf)).Descendants()
            .Any(e => e.Attribute("stroke-dasharray") != null);
        Assert.False(DashAttribute(huge));
        Assert.True(DashAttribute(Dashed("[50 50]")));   // control: a dash the writer can say is written
    }

    [Fact]
    public void A_Lab_spaces_referenced_range_bounds_its_components()
    {
        static byte[] Lab(string range) => Pdf("/CS0 cs 50 80 0 sc 0 0 200 200 re f",
            $"/ColorSpace << /CS0 [/Lab << /WhitePoint [0.9505 1 1.089] /Range {range} >>] >>");

        var byReference = Rgb(Render(Lab("[8 0 R 8 0 R 8 0 R 8 0 R]")), 100, 100);

        Assert.Equal(Rgb(Render(Lab("[0 0 0 0]")), 100, 100), byReference);
        Assert.NotEqual(Rgb(Render(Lab("[-100 100 -100 100]")), 100, 100), byReference);
    }

    [Fact]
    public void An_indexed_Lab_spaces_referenced_range_scales_its_palette()
    {
        // The entry's a is the top of the range and its b the middle: with [0 100 0 100] a reddish
        // colour, with [0 0 0 0] a grey.
        static byte[] Indexed(string range) => Pdf("/CS0 cs 0 sc 0 0 200 200 re f",
            $"/ColorSpace << /CS0 [/Indexed [/Lab << /WhitePoint [0.9505 1 1.089] /Range {range} >>] 0 <80FF80>] >>");

        var byReference = Rgb(Render(Indexed("[8 0 R 10 0 R 8 0 R 10 0 R]")), 100, 100);

        Assert.Equal(Rgb(Render(Indexed("[0 100 0 100]")), 100, 100), byReference);
        Assert.NotEqual(Rgb(Render(Indexed("[0 0 0 0]")), 100, 100), byReference);
    }

    [Fact]
    public void A_soft_masks_referenced_backdrop_is_its_backdrop()
    {
        // A luminosity mask whose group draws nothing: the backdrop alone decides what shows, and
        // object 13 is 1, a white backdrop that shows the fill.
        static byte[] Masked(string backdrop) => Pdf("/G0 gs 0 0 0 rg 0 0 200 200 re f",
            $"/ExtGState << /G0 << /SMask << /Type /Mask /S /Luminosity /G 12 0 R /BC {backdrop} >> >> >>", extra: new[]
            {
                S("", "/Type /XObject /Subtype /Form /BBox [0 0 200 200] /Group << /S /Transparency /CS /DeviceGray >>"),
                "1",
            });

        Assert.True(Painted(Render(Masked("[13 0 R]")), 100, 100));
        Assert.True(Painted(Render(Masked("[1]")), 100, 100));      // control: the same backdrop, direct
        Assert.False(Painted(Render(Masked("[0]")), 100, 100));     // control: a black backdrop hides it
    }

    [Fact]
    public void A_JPEG_2000_images_referenced_decode_is_applied()
    {
        // CMYK and an opacity channel: the one image whose /Decode the JPEG 2000 path reads itself.
        const int w = 16, h = 8;
        var samples = new byte[w * h * 5];
        for (int i = 0; i < w * h; i++)
        {
            samples[i * 5] = (byte)(i * 3); samples[i * 5 + 1] = (byte)(255 - i); samples[i * 5 + 2] = (byte)(i * 5);
            samples[i * 5 + 3] = (byte)(i / 2); samples[i * 5 + 4] = 255;
        }
        byte[] jp2 = Tetitulz.Pdf.Core.Imaging.Jpx.JpxImageEncoding.Encode(samples, w, h, 5, 8,
            new Tetitulz.Pdf.Core.Imaging.Jpx.JpxEncodingOptions { Lossless = true, DecompositionLevels = 1, Jp2Container = true, OpacityChannel = true });
        string data = Encoding.Latin1.GetString(jp2);
        byte[] Image(string decode) => Pdf("q 200 0 0 200 0 0 cm /Im Do Q", "/XObject << /Im 12 0 R >>", extra: new[]
        {
            S(data, $"/Type /XObject /Subtype /Image /Width {w} /Height {h} /Filter /JPXDecode /ColorSpace /DeviceCMYK /BitsPerComponent 8 /SMaskInData 1 {decode}"),
            "1",
        });

        byte[] byReference = Render(Image("/Decode [13 0 R 8 0 R 13 0 R 8 0 R 13 0 R 8 0 R 13 0 R 8 0 R]")).Pixels;
        byte[] direct = Render(Image("/Decode [1 0 1 0 1 0 1 0]")).Pixels;

        Assert.Equal(direct, byReference);
        Assert.NotEqual(Render(Image("")).Pixels, direct);   // control: the array changes the picture
    }
}
