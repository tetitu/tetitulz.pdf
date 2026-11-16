// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
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
/// An annotation's <c>/F</c> flags written as an <b>indirect reference</b>, in both renderers.
/// </summary>
/// <remarks>
/// <para><b>The null falls the unsafe way.</b> Unresolved, <c>/F</c> reads as absent and the flag
/// word is never examined — so <c>/F 2</c> (Hidden, §12.5.3) never reaches
/// <c>PdfAnnotationFlags.IsHiddenOnScreen</c> and <b>the annotation the file asks to hide is
/// drawn</b>. Not a degraded rendering: content appears that the document says should not.</para>
///
/// <para><b>Both renderers had it, identically</b>, so they agreed while both were wrong — which is
/// why this is one parity test rather than two. <c>PdfAnnotationFlags.IsHiddenOnScreen</c> is
/// already a shared decision point; the <i>read</i> feeding it was not shared, and neither copy
/// resolved.</para>
///
/// <para>Asserted on decoded pixels and on the emitted SVG. A hidden annotation that is wrongly
/// drawn produces a perfectly valid page — so the assertion has to be that the ink is absent, with
/// a visible control proving the fixture can put ink there at all.</para>
/// </remarks>
public class IndirectAnnotationFlagParityTests
{
    /// <summary>
    /// A Hidden annotation contributes no ink, whichever way <c>/F</c> is spelled.
    /// </summary>
    [Theory]
    [InlineData(true)]   // /F 8 0 R
    [InlineData(false)]  // /F 2
    public void RasterDoesNotDrawAHiddenAnnotation(bool indirect)
    {
        using var ms = new MemoryStream(AnnotationPdf(indirect, hidden: true));
        using var bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));
        Assert.NotNull(bmp);

        Assert.False(HasInk(bmp), "a Hidden annotation was drawn");
    }

    /// <summary>
    /// The control: the same annotation without the Hidden bit <b>is</b> drawn, so the assertion
    /// above is about the flag rather than about a fixture that never draws anything.
    /// </summary>
    [Fact]
    public void RasterDrawsAVisibleAnnotation()
    {
        using var ms = new MemoryStream(AnnotationPdf(indirect: false, hidden: false));
        using var bmp = SKBitmap.Decode(PdfPageRenderer.RenderPage(ms, 0, 72));

        Assert.True(HasInk(bmp), "the fixture drew nothing even when visible");
    }

    /// <summary>
    /// The SVG export makes the same decision from the same flag word: a Hidden annotation adds
    /// nothing beyond what a page with no annotation at all emits.
    /// </summary>
    /// <remarks>
    /// <b>Measured against a no-annotation baseline rather than against zero.</b> The exporter
    /// always emits a page-background <c>rect</c>, so "no shape elements" is false for every
    /// document — the first version of this test asserted it and failed on its own control. The
    /// difference between the two exports is the only thing that isolates the annotation.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SvgDoesNotEmitAHiddenAnnotation(bool indirect)
    {
        int baseline = SvgShapeCount(AnnotationPdf(indirect, hidden: true, annotated: false));
        int hidden = SvgShapeCount(AnnotationPdf(indirect, hidden: true));

        Assert.Equal(baseline, hidden);
    }

    /// <summary>The SVG control, for the same reason as the raster one.</summary>
    [Fact]
    public void SvgEmitsAVisibleAnnotation()
    {
        int baseline = SvgShapeCount(AnnotationPdf(indirect: false, hidden: false, annotated: false));
        int visible = SvgShapeCount(AnnotationPdf(indirect: false, hidden: false));

        Assert.True(visible > baseline,
            $"the fixture emitted nothing for a visible annotation (baseline {baseline}, visible {visible})");
    }

    private static int SvgShapeCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        return ShapeElements(PdfSvgRenderer.RenderPageToSvg(ms, 0)).Count;
    }

    // ------------------------------------------------------------------

    /// <summary>Any non-white pixel in the region the annotation occupies.</summary>
    private static bool HasInk(SKBitmap bmp)
    {
        for (int y = 20; y < 60; y++)
            for (int x = 20; x < 60; x++)
            {
                SKColor c = bmp.GetPixel(x, y);
                if (c.Red < 200 || c.Green < 200 || c.Blue < 200) return true;
            }
        return false;
    }

    /// <summary>Drawing elements in the export, which is what an annotation would add.</summary>
    private static System.Collections.Generic.List<XElement> ShapeElements(string svg) =>
        XDocument.Parse(svg).Descendants()
            .Where(e => e.Name.LocalName is "rect" or "path" or "image")
            .ToList();

    /// <summary>
    /// A page whose only content is a square annotation with an appearance stream. Object 8 carries
    /// the flag word when <paramref name="indirect"/>.
    /// </summary>
    private static byte[] AnnotationPdf(bool indirect, bool hidden, bool annotated = true)
    {
        string flagValue = hidden ? "2" : "4";        // 2 = Hidden, 4 = Print
        string f = indirect ? "8 0 R" : flagValue;

        return Build(
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R" +
            (annotated ? " /Annots [5 0 R] >>" : " >>"),
            StreamObj("", "q Q\n"),
            "<< /Type /Annot /Subtype /Square /Rect [20 40 60 80] /F " + f + " /AP << /N 6 0 R >> >>",
            StreamObj("/Type /XObject /Subtype /Form /BBox [0 0 40 40]", "0 0 0 rg 0 0 40 40 re f\n"),
            "null",
            flagValue);
    }

    private static string StreamObj(string dictEntries, string data) =>
        "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "endstream";

    private static byte[] Build(params string[] bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
