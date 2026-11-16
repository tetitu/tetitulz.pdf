// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Xml.Linq;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An inline image whose colour space uses §8.9.7 Table 93's abbreviations <i>inside</i> the
/// <c>/Indexed</c> array — <c>[/I /G 1 &lt;…&gt;]</c> — and the two renderers agreeing about it.
///
/// <para><b>They did not, twice, and both times the raster path was the one that was wrong.</b>
/// <c>ExpandAbbreviatedImageDict</c> expands the <b>top-level</b> <c>/CS</c> name and deliberately
/// leaves an array head alone, so both <c>/I</c> and the base <c>/G</c> arrive as written:</para>
/// <list type="number">
///   <item>the raster recognition test compared against <c>/Indexed</c> only, so
///     <c>[/I …]</c> was not treated as indexed at all and the samples were strided by the base
///     space's component count instead of one index per pixel;</item>
///   <item>the raster palette builder had no <c>/G</c> or <c>/CMYK</c> case, so a grey palette was
///     read three bytes per entry where the file wrote one.</item>
/// </list>
///
/// <para>Each assertion is a decoded colour, because both defects produce a full-size image of
/// entirely plausible pixels — just the wrong ones.</para>
/// </summary>
public sealed class IndexedInlineImageParityTests
{
    /// <summary>
    /// A two-entry grey palette, <c>[/I /G 1 &lt;00FF&gt;]</c>, with the single sample selecting
    /// entry 1 — white. Read with the abbreviations unexpanded, entry 1 starts at byte 3 of a
    /// two-byte table and the palette stays at its initialised black.
    /// </summary>
    [Fact]
    public void AbbreviatedIndexedGrey_SelectsTheRightPaletteEntry_InTheRasterPath()
    {
        using var bmp = PixelAssert.Render(IndexedInlinePdf("/I /G 1 <00FF>", sampleBits: "80"));
        SKColor c = PixelAssert.At(bmp, 45, 45);

        Assert.InRange(c.Red, 247, 255);
        Assert.InRange(c.Green, 247, 255);
        Assert.InRange(c.Blue, 247, 255);
    }

    /// <summary>Entry 0 of the same palette is black — the control that says the index is read.</summary>
    [Fact]
    public void AbbreviatedIndexedGrey_EntryZeroIsBlack()
    {
        using var bmp = PixelAssert.Render(IndexedInlinePdf("/I /G 1 <00FF>", sampleBits: "00"));
        SKColor c = PixelAssert.At(bmp, 45, 45);

        Assert.InRange(c.Red, 0, 8);
    }

    /// <summary>
    /// The unabbreviated spelling of the same image must give the same colour. This is what makes
    /// the two above claims about the <i>abbreviation</i> rather than about indexed images.
    /// </summary>
    [Fact]
    public void AbbreviatedAndUnabbreviatedSpellings_Agree()
    {
        using var abbrev = PixelAssert.Render(IndexedInlinePdf("/I /G 1 <00FF>", "80"));
        using var full   = PixelAssert.Render(IndexedInlinePdf("/Indexed /DeviceGray 1 <00FF>", "80"));

        Assert.Equal(PixelAssert.At(full, 45, 45), PixelAssert.At(abbrev, 45, 45));
    }

    /// <summary>
    /// And the SVG export, which already handled both spellings, still agrees with the raster path
    /// now that it does too.
    /// </summary>
    [Theory]
    [InlineData("80", 255)]
    [InlineData("00", 0)]
    public void RasterAndSvg_AgreeOnTheAbbreviatedPalette(string sampleBits, int expected)
    {
        using var bmp = PixelAssert.Render(IndexedInlinePdf("/I /G 1 <00FF>", sampleBits));
        Assert.InRange(PixelAssert.At(bmp, 45, 45).Red, Math.Max(0, expected - 8),
                       Math.Min(255, expected + 8));

        string svg = RenderSvg(IndexedInlinePdf("/I /G 1 <00FF>", sampleBits));
        Assert.Equal(expected, SvgImageSample(svg));
    }

    /// <summary>
    /// A <c>/CMYK</c> base, the other abbreviation the raster builder was missing: four bytes per
    /// entry, and entry 1 is pure cyan.
    /// </summary>
    [Fact]
    public void AbbreviatedIndexedCmyk_ReadsFourBytesPerEntry()
    {
        // Eight bytes, four per entry: entry 0 = 00 00 00 FF (black),
        // entry 1 = FF 00 00 00 (pure cyan). Read three bytes per entry instead, entry 1
        // starts inside entry 0 and the colour is nothing like cyan.
        using var bmp = PixelAssert.Render(
            IndexedInlinePdf("/I /CMYK 1 <000000FFFF000000>", "80"));

        // Read as 3 bytes per entry the palette lands somewhere else entirely.
        SKColor c = PixelAssert.At(bmp, 45, 45);
        Assert.InRange(c.Red, 0, 8);
        Assert.InRange(c.Green, 247, 255);
        Assert.InRange(c.Blue, 247, 255);
    }

    // ==================================================================
    //  §8.9.5.2 Table 89 — an Indexed image's /BitsPerComponent is 1, 2, 4 or 8
    // ==================================================================

    /// <summary>
    /// A 16-bpc <c>/Indexed</c> image is malformed. Both renderers must refuse it and say so.
    /// </summary>
    /// <remarks>
    /// The raster path reported and dropped the image; the SVG path silently clamped to
    /// <c>bpc = 8</c> and carried on, computing the row stride from the clamped depth against data
    /// with twice as many bytes per row — confidently wrong exported pixels, no diagnostic. The
    /// assertion is that <b>both</b> report, because "the raster one reports" was already true.
    /// </remarks>
    [Fact]
    public void IndexedImageWithSixteenBitsPerComponent_IsRefusedByBothRenderers()
    {
        byte[] pdf = IndexedXObjectPdf(bpc: 16);

        var raster = Capture(() => { using var ms = new MemoryStream(pdf); PdfOperations.RenderPageToPng(ms, 0); });
        var svg    = Capture(() => { using var ms = new MemoryStream(pdf); PdfSvgRenderer.RenderPageToSvg(ms, 0); });

        Assert.Contains(raster, d => d != null && d.Contains("/BitsPerComponent 16"));
        Assert.Contains(svg,    d => d != null && d.Contains("/BitsPerComponent 16"));
    }

    /// <summary>
    /// The control: a legal depth must be refused by neither, or the test above would pass for a
    /// renderer that rejected every Indexed image.
    /// </summary>
    [Fact]
    public void IndexedImageWithALegalDepth_IsRefusedByNeitherRenderer()
    {
        byte[] pdf = IndexedXObjectPdf(bpc: 8);

        var raster = Capture(() => { using var ms = new MemoryStream(pdf); PdfOperations.RenderPageToPng(ms, 0); });
        var svg    = Capture(() => { using var ms = new MemoryStream(pdf); PdfSvgRenderer.RenderPageToSvg(ms, 0); });

        Assert.DoesNotContain(raster, d => d != null && d.Contains("must be 1, 2, 4 or 8"));
        Assert.DoesNotContain(svg,    d => d != null && d.Contains("must be 1, 2, 4 or 8"));
    }

    /// <summary>
    /// Render diagnostics raised on the calling thread only — <c>RenderDiagnostics.OnEvent</c> is
    /// process-wide, so an unfiltered handler makes any assertion a claim about the whole run.
    /// </summary>
    private static List<string> Capture(Action act)
    {
        int thread = Environment.CurrentManagedThreadId;
        var seen = new List<string>();
        Action<RenderDiagnosticKind, string, Exception> handler = (_, detail, __) =>
        {
            if (Environment.CurrentManagedThreadId != thread) return;
            lock (seen) seen.Add(detail);
        };

        RenderDiagnostics.OnEvent += handler;
        try { act(); } finally { RenderDiagnostics.OnEvent -= handler; }
        return seen;
    }

    /// <summary>A 2x1 Indexed image XObject at <paramref name="bpc"/> bits per component.</summary>
    private static byte[] IndexedXObjectPdf(int bpc) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            " /Resources << /XObject << /Im0 5 0 R >> >> >>",
            Stream("", "q 70 0 0 70 10 10 cm /Im0 Do Q\n"),
            Stream("/Type /XObject /Subtype /Image /Width 2 /Height 1" +
                   " /ColorSpace [/Indexed /DeviceRGB 1 <000000FFFFFF>]" +
                   $" /BitsPerComponent {bpc}", "\x00\x01\x00\x01"));

    // ------------------------------------------------------------------

    /// <summary>
    /// A 1×1, 1-bpc inline image drawn over 200×200 units, with <paramref name="colorSpace"/> as
    /// the body of its <c>/CS</c> array and <paramref name="sampleBits"/> as the hex sample byte.
    /// </summary>
    private static byte[] IndexedInlinePdf(string colorSpace, string sampleBits) =>
        Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 90 90] /Contents 4 0 R" +
            " /Resources << >> >>",
            Stream("", "q 70 0 0 70 10 10 cm\n" +
                       $"BI /W 1 /H 1 /BPC 1 /CS [{colorSpace}] /F /AHx ID {sampleBits}>\nEI\nQ\n"));

    /// <summary>The grey level of the single pixel in the PNG the SVG export embedded.</summary>
    private static int SvgImageSample(string svg)
    {
        XElement image = XDocument.Parse(svg).Descendants()
            .Single(e => e.Name.LocalName == "image");
        string href = image.Attributes().Single(a => a.Name.LocalName == "href").Value;

        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, href, StringComparison.Ordinal);
        using var bmp = SKBitmap.Decode(Convert.FromBase64String(href.Substring(prefix.Length)));
        Assert.NotNull(bmp);
        return bmp.GetPixel(0, 0).Red;
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
