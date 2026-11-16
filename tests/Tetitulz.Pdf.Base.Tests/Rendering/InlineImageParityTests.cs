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
/// The inline-image (<c>BI … ID … EI</c>) path, against its image-XObject twin and against the SVG
/// export.
///
/// <para>Three duplications, found together because they sit on one code path.</para>
/// <list type="number">
///   <item><b>The raster inline path was the one image-sample decode in either renderer that
///     bypassed <c>ContentStreamInterpreter.DecodeImageStream</c></b>, calling
///     <c>PdfStream.GetDecodedData()</c> directly — so it alone lost the shared
///     <c>/Columns</c>-from-<c>/Width</c> repair. <b>Live defect.</b></item>
///   <item><b><c>ContentStreamParser</c> had three copies of its inline value switch</b> and the
///     inline-image one had no case for a dictionary, so <c>/DP</c> was stored as
///     <c>PdfNull</c> and its keys leaked into the image dictionary. <b>Live defect</b>, and the
///     one that made the fixture for (1) misleading until it was fixed — see
///     <c>ContentStreamParserTests</c>.</item>
///   <item><b>The SVG writer carried a private copy of the §8.9.7 Table 93 abbreviation
///     expander</b> that never renamed <c>/D</c> to <c>/Decode</c>. <b>Latent, not live</b>: every
///     SVG image-decode read is written <c>Decode ?? D</c> where the raster ones read
///     <c>Decode</c> alone, so each consumer compensated for what the expander dropped. Removing
///     the copy is still right — it was one new call site from being live — but the tests below
///     that turn red when the rename is removed are the <i>raster</i> ones.</item>
/// </list>
///
/// <para>Every assertion here reads a <b>decoded sample value</b>, because none of these changes
/// the shape of the output: an inverted image and a correct one produce the same elements, the same
/// element count and the same attributes.</para>
/// </summary>
public sealed class InlineImageParityTests
{
    /// <summary>The single sample in the grey fixtures: mid-dark, 8 bpc.</summary>
    private const int Sample = 0x40;               // 64
    private const int Inverted = 0xFF - Sample;    // 191 — what /Decode [1 0] must produce

    // ====================================================================
    //  /D  —  the abbreviation the SVG copy had lost
    // ====================================================================

    /// <summary>
    /// <c>/D</c> is Table 93's abbreviation for <c>/Decode</c>. Neither value is the white page
    /// background, so this cannot pass with the image dropped: 64 and 191 are both ink.
    /// </summary>
    [Fact]
    public void InlineImage_DecodeAbbreviation_IsHonouredByTheRasterPath()
    {
        Assert.Equal(Sample,   RasterSample(InlineImagePdf(decodeEntry: "")));
        Assert.Equal(Inverted, RasterSample(InlineImagePdf(decodeEntry: "/D [1 0] ")));
    }

    /// <summary>
    /// And by the SVG export. The exported <c>&lt;image&gt;</c> carries the <b>decoded</b> samples
    /// as a PNG, so this reads the same number back out of it.
    /// </summary>
    [Fact]
    public void InlineImage_DecodeAbbreviation_IsHonouredByTheSvgExport()
    {
        Assert.Equal(Sample,   SvgSample(InlineImagePdf(decodeEntry: "")));
        Assert.Equal(Inverted, SvgSample(InlineImagePdf(decodeEntry: "/D [1 0] ")));
    }

    /// <summary>
    /// The unabbreviated spelling must reach the same place — the control that says the two above
    /// measure the <i>rename</i> rather than decode-array support in general.
    /// </summary>
    [Fact]
    public void InlineImage_UnabbreviatedDecodeKey_AgreesWithTheAbbreviation()
    {
        Assert.Equal(RasterSample(InlineImagePdf("/D [1 0] ")),
                     RasterSample(InlineImagePdf("/Decode [1 0] ")));
        Assert.Equal(SvgSample(InlineImagePdf("/D [1 0] ")),
                     SvgSample(InlineImagePdf("/Decode [1 0] ")));
    }

    // ====================================================================
    //  inline vs XObject  —  the same image by two routes
    // ====================================================================

    /// <summary>
    /// The same samples, colour space and decode array, drawn once as an inline image and once as
    /// an image XObject. The XObject path is the reference: it has always gone through
    /// <c>DecodeImageStream</c>.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("[1 0]")]
    public void InlineImage_AndImageXObject_ProduceTheSameSample(string decode)
    {
        string inlineEntry  = decode.Length == 0 ? "" : $"/D {decode} ";
        string xobjectEntry = decode.Length == 0 ? "" : $"/Decode {decode} ";

        Assert.Equal(RasterSample(XObjectImagePdf(xobjectEntry)),
                     RasterSample(InlineImagePdf(inlineEntry)));
    }

    /// <summary>And the two renderers agree with each other on the inline route.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("/D [1 0] ")]
    public void InlineImage_RasterAndSvg_Agree(string decodeEntry)
    {
        Assert.Equal(RasterSample(InlineImagePdf(decodeEntry)),
                     SvgSample(InlineImagePdf(decodeEntry)));
    }

    // ====================================================================
    //  /Columns  —  the shared repair the raster inline path used to skip
    // ====================================================================

    /// <summary>
    /// A CCITTFaxDecode inline image whose <c>/DecodeParms</c> omits <c>/Columns</c>. The filter's
    /// own default is <b>1728</b> (§7.4.6, Table 11), not the image width, so decoding it as
    /// written fails — and <c>DecodeImageStream</c> is the one place that supplies <c>/Columns</c>
    /// from <c>/Width</c>.
    ///
    /// <para>The failure is not an exception: <c>CcittFaxDecodeFilter</c> hands back its
    /// <b>compressed input</b>, which the 1-bpc path then paints as samples. The fixture is an
    /// all-white image, so any dark pixel inside its rectangle is the compressed bytes showing
    /// through.</para>
    /// </summary>
    [Fact]
    public void InlineImage_CcittWithoutColumns_DecodesLikeTheXObjectRoute()
    {
        Assert.Equal(0, DarkPixelsInImageRect(CcittXObjectImagePdf(columnsEntry: "")));
        Assert.Equal(0, DarkPixelsInImageRect(CcittInlineImagePdf(columnsEntry: "")));
    }

    /// <summary>
    /// The control: with <c>/Columns 8</c> written out, both routes were already correct. If this
    /// one ever fails the fixture is wrong, not the renderer.
    /// </summary>
    [Fact]
    public void InlineImage_CcittWithExplicitColumns_IsTheSameImage()
    {
        Assert.Equal(0, DarkPixelsInImageRect(CcittInlineImagePdf(columnsEntry: "/Columns 8 ")));
        Assert.Equal(0, DarkPixelsInImageRect(CcittXObjectImagePdf(columnsEntry: "/Columns 8 ")));
    }

    // ====================================================================
    //  fixtures
    // ====================================================================

    /// <summary>
    /// A 1×1, 8-bpc DeviceGray inline image drawn over 200×200 user-space units at (100, 100).
    /// The sample byte is <c>0x40</c>, which is <c>'@'</c> — printable, so the inline data needs no
    /// escaping and the <c>EI</c> scan is unambiguous.
    /// </summary>
    private static byte[] InlineImagePdf(string decodeEntry) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << >> >>",
            Stream("", "q 200 0 0 200 100 100 cm\n" +
                       $"BI /W 1 /H 1 /BPC 8 /CS /G {decodeEntry}ID @\nEI\nQ\n"));

    /// <summary>The same image as an image XObject.</summary>
    private static byte[] XObjectImagePdf(string decodeEntry) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im0 5 0 R >> >> >>",
            Stream("", "q 200 0 0 200 100 100 cm /Im0 Do Q\n"),
            Stream("/Type /XObject /Subtype /Image /Width 8 /Height 1" +
                   " /ColorSpace /DeviceGray /BitsPerComponent 8 " + decodeEntry, "@@@@@@@@"));

    private static byte[] CcittInlineImagePdf(string columnsEntry) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << >> >>",
            Stream("", "q 200 0 0 200 100 100 cm\n" +
                       "BI /W 8 /H 2 /BPC 1 /CS /G /F /CCF" +
                       $" /DP << /K 0 {columnsEntry}/Rows 2 /EndOfBlock false >>\n" +
                       "ID " + WhiteRowsG3 + "\nEI\nQ\n"));

    private static byte[] CcittXObjectImagePdf(string columnsEntry) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R" +
            " /Resources << /XObject << /Im0 5 0 R >> >> >>",
            Stream("", "q 200 0 0 200 100 100 cm /Im0 Do Q\n"),
            Stream("/Type /XObject /Subtype /Image /Width 8 /Height 2 /ColorSpace /DeviceGray" +
                   " /BitsPerComponent 1 /Filter /CCITTFaxDecode" +
                   $" /DecodeParms << /K 0 {columnsEntry}/Rows 2 /EndOfBlock false >>",
                   WhiteRowsG3));

    /// <summary>
    /// Two Group 3 one-dimensional rows of eight white pixels. T.4's white terminating code for a
    /// run of 8 is <c>10011</c>, so the payload is <c>10011 10011</c> zero-padded to a byte
    /// boundary: <c>0x9C 0xC0</c>.
    ///
    /// <para>Those two bytes are also what the broken path painted <i>as samples</i>
    /// (<c>10011100</c>, <c>11000000</c> — four black pixels in the first row), which is what makes
    /// "no dark pixel" a falsifiable assertion rather than a restatement of the page background.
    /// </para>
    /// </summary>
    private static readonly string WhiteRowsG3 = new string(new[] { (char)0x9C, (char)0xC0 });

    // ====================================================================
    //  measurement
    // ====================================================================

    /// <summary>The grey level the raster renderer painted at the centre of the drawn image.</summary>
    private static int RasterSample(byte[] pdf)
    {
        using var bmp = RenderRaster(pdf);

        // 72 dpi ⇒ one device pixel per point. The image covers x 100..300 and, y-down, 492..692.
        SKColor c = bmp.GetPixel(200, 792 - 200);
        Assert.Equal(c.Red, c.Green);           // grey in, grey out
        Assert.Equal(c.Red, c.Blue);
        return c.Red;
    }

    /// <summary>
    /// How many pixels inside the image's rectangle are darker than mid-grey. The CCITT fixtures
    /// are all-white, so the correct answer is zero and the compressed-bytes-as-samples failure is
    /// a large number.
    /// </summary>
    private static int DarkPixelsInImageRect(byte[] pdf)
    {
        using var bmp = RenderRaster(pdf);

        int dark = 0;
        for (int x = 105; x < 295; x++)
            for (int y = 792 - 295; y < 792 - 105; y++)
                if (bmp.GetPixel(x, y).Red < 128) dark++;
        return dark;
    }

    private static SKBitmap RenderRaster(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        byte[] png = PdfPageRenderer.RenderPage(ms, 0, 72);
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp;
    }

    /// <summary>
    /// The grey level in the PNG the SVG export embedded. Reading the sample back out of the data
    /// URI is what makes this a pixel assertion rather than a structural one.
    /// </summary>
    private static int SvgSample(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        XElement image = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "image");
        string href = image.Attributes()
            .Single(a => a.Name.LocalName == "href").Value;

        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href, StringComparison.Ordinal);

        using var decoded = SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(decoded);
        SKColor c = decoded.GetPixel(0, 0);
        Assert.Equal(c.Red, c.Green);
        Assert.Equal(c.Red, c.Blue);
        return c.Red;
    }

    private static string Stream(string dictEntries, string data) =>
        $"<< {dictEntries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be Latin-1.</summary>
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
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
