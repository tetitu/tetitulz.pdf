// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Base.Tests.Rendering;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// Tests for gap 28.6 — FontDescriptorMetrics extraction from /FontDescriptor.
/// </summary>
public sealed class FontDescriptorMetricsTests
{
    // ------------------------------------------------------------------
    //  GetFontDescriptorMetrics — read off a real document
    // ------------------------------------------------------------------

    /// <summary>
    /// Every value in the page's /FontDescriptor has to come back out of
    /// <see cref="FontCache.GetFontDescriptorMetrics"/>, in PDF glyph units and with the sign
    /// the file gave (/Descent is negative). The page must also still draw its text where the
    /// content stream put it — an extractor that silently returned zeros renders identically,
    /// which is why "the render did not throw" could never have caught this.
    /// </summary>
    [Fact]
    public void PageWithText_FontHasDescriptor_MetricsAreReadFromTheDescriptor()
    {
        byte[] pdf = BuildPdfWithFontDescriptor(
            ascent: 718, descent: -207, capHeight: 718, xHeight: 523);

        var metrics = MetricsOfFirstPageFont(pdf);
        Assert.False(metrics.IsEmpty);
        Assert.Equal(718f,  metrics.Ascent);
        Assert.Equal(-207f, metrics.Descent);
        Assert.Equal(718f,  metrics.CapHeight);
        Assert.Equal(523f,  metrics.XHeight);

        using var bmp = PixelAssert.Render(pdf);
        AssertTextInkedAtBaseline(bmp, leftUserX: 72f, baselineUserY: 720f);
    }

    /// <summary>
    /// A font with no /FontDescriptor yields the empty struct — <b>not</b> invented defaults —
    /// and the page still renders its text. The empty result is the signal callers branch on,
    /// so it is a real claim, not the absence of one.
    /// </summary>
    [Fact]
    public void PageWithText_FontHasNoDescriptor_MetricsAreEmptyAndTheTextStillRenders()
    {
        byte[] pdf = BuildMinimalTextPdf();

        var metrics = MetricsOfFirstPageFont(pdf);
        Assert.True(metrics.IsEmpty);
        Assert.Equal(0f, metrics.Ascent);
        Assert.Equal(0f, metrics.Descent);
        Assert.Equal(0f, metrics.CapHeight);
        Assert.Equal(0f, metrics.XHeight);

        using var bmp = PixelAssert.Render(pdf);
        AssertTextInkedAtBaseline(bmp, leftUserX: 72f, baselineUserY: 720f);
    }

    /// <summary>
    /// A descriptor value given as an indirect reference (§7.3.10) is read through it. The raw
    /// indexer saw a <c>PdfReference</c>, not a number, and answered 0 — an ascent of nothing.
    /// The direct /Descent beside it is the control.
    /// </summary>
    [Fact]
    public void FontDescriptorMetrics_AnIndirectAscent_IsReadThroughTheReference()
    {
        byte[] pdf = BuildPdfWithFontDescriptor(
            ascent: 718, descent: -207, capHeight: 718, xHeight: 523, indirectAscent: true);

        var metrics = MetricsOfFirstPageFont(pdf);
        Assert.Equal(718f,  metrics.Ascent);
        Assert.Equal(-207f, metrics.Descent);
    }

    // ------------------------------------------------------------------
    //  Shared assertions
    // ------------------------------------------------------------------

    private static FontDescriptorMetrics MetricsOfFirstPageFont(byte[] pdf)
    {
        using var doc   = PdfDocument.Open(new MemoryStream(pdf));
        using var cache = new FontCache();
        return cache.GetFontDescriptorMetrics("F1", doc.Pages[0].Resources, doc);
    }

    /// <summary>
    /// The page's only mark is one short run of text drawn at <c>Td</c>'s position, so its ink
    /// box pins down both that the glyphs were drawn and where.
    /// </summary>
    private static void AssertTextInkedAtBaseline(SKBitmap bmp, float leftUserX, float baselineUserY)
    {
        var box = PixelAssert.InkBounds(bmp);
        Assert.NotNull(box);

        // Starts at the pen position and runs to the right of it, a few glyphs wide.
        Assert.InRange(box!.Value.left,  PixelAssert.X(leftUserX - 2f), PixelAssert.X(leftUserX + 8f));
        Assert.InRange(box.Value.right,  PixelAssert.X(leftUserX + 8f), PixelAssert.X(leftUserX + 60f));

        // Sits on the baseline: no ink below it beyond a descender, none above the ascender.
        Assert.InRange(box.Value.bottom, PixelAssert.Y(bmp, baselineUserY + 5f),
                                         PixelAssert.Y(bmp, baselineUserY - 5f));
        Assert.InRange(box.Value.top,    PixelAssert.Y(bmp, baselineUserY + 14f),
                                         PixelAssert.Y(bmp, baselineUserY + 1f));
    }

    // ------------------------------------------------------------------
    //  FontDescriptorMetrics struct
    // ------------------------------------------------------------------

    [Fact]
    public void FontDescriptorMetrics_DefaultIsEmpty()
    {
        var m = default(FontDescriptorMetrics);
        Assert.True(m.IsEmpty);
        Assert.Equal(0f, m.Ascent);
        Assert.Equal(0f, m.Descent);
        Assert.Equal(0f, m.CapHeight);
        Assert.Equal(0f, m.XHeight);
    }

    [Fact]
    public void FontDescriptorMetrics_PopulatedIsNotEmpty()
    {
        var m = new FontDescriptorMetrics(718f, -207f, 718f, 523f);
        Assert.False(m.IsEmpty);
        Assert.Equal(718f,  m.Ascent);
        Assert.Equal(-207f, m.Descent);
        Assert.Equal(718f,  m.CapHeight);
        Assert.Equal(523f,  m.XHeight);
    }

    // ------------------------------------------------------------------
    //  Helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds a minimal PDF whose font dict contains a /FontDescriptor with
    /// the specified metric values.
    /// </summary>
    private static byte[] BuildPdfWithFontDescriptor(
        float ascent, float descent, float capHeight, float xHeight, bool indirectAscent = false)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        // FontDescriptor (obj 6)
        int o6 = sb.Length;
        sb.AppendFormat(
            "6 0 obj\n<< /Type /FontDescriptor /FontName /TestFont" +
            " /Ascent {0} /Descent {1} /CapHeight {2} /XHeight {3}" +
            " /Flags 32 /ItalicAngle 0 /StemV 80 /FontBBox [-166 -225 1000 931] >>\nendobj\n",
            indirectAscent ? "7 0 R" : ascent.ToString(System.Globalization.CultureInfo.InvariantCulture),
            descent, capHeight, xHeight);

        // The ascent as its own object (obj 7), when the descriptor refers to it.
        int o7 = sb.Length;
        if (indirectAscent)
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, "7 0 obj\n{0}\nendobj\n", ascent);

        // Font (obj 5) references the descriptor
        int o5 = sb.Length;
        sb.Append(
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica" +
            " /FontDescriptor 6 0 R >>\nendobj\n");

        // Content stream: draw some text (obj 4)
        const string cs = "BT /F1 12 Tf 72 720 Td (Hello) Tj ET";
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");

        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);

        int xref = sb.Length;
        sb.Append(indirectAscent ? "xref\n0 8\n0000000000 65535 f \n" : "xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", o1);
        sb.AppendFormat("{0:D10} 00000 n \n", o2);
        sb.AppendFormat("{0:D10} 00000 n \n", o3);
        sb.AppendFormat("{0:D10} 00000 n \n", o4);
        sb.AppendFormat("{0:D10} 00000 n \n", o5);
        sb.AppendFormat("{0:D10} 00000 n \n", o6);
        if (indirectAscent) sb.AppendFormat("{0:D10} 00000 n \n", o7);
        sb.Append(indirectAscent ? "trailer\n<< /Size 8 /Root 1 0 R >>\n" : "trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] BuildMinimalTextPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int o3 = sb.Length;
        sb.Append(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        const string cs = "BT /F1 12 Tf 72 720 Td (Test) Tj ET";
        int o4 = sb.Length;
        sb.AppendFormat(
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            cs.Length, cs);
        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");
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
