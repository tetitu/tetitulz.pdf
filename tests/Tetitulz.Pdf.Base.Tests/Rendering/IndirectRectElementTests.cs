// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// An annotation <c>/Rect</c> whose first element is an indirect reference (§7.3.10) must be read
/// the same way by the renderers and by <see cref="PdfLinkExtractor"/>.
/// </summary>
/// <remarks>
/// <para><b>The divergence.</b> Both renderers read <c>/Rect</c> through
/// <c>Core.Text.ContentOperand.ArrayFloat</c>, which does not follow a reference — correctly, since
/// it reads content-stream operands, which cannot be indirect. A <c>/Rect</c> is a dictionary value,
/// where an indirect element is legal, and an unresolved one reads as <b>0</b>. So for
/// <c>/Rect [12 0 R 700 200 720]</c> with object 12 = 100 the extractor reported a clickable box at
/// x 100..200 while both renderers drew the annotation across x 0..200: <b>the drawn annotation and
/// the reported hit rectangle disagreed by 100 pt.</b></para>
/// <para><b>Why it is asserted as a correlation.</b> Neither answer is detectably wrong on its own —
/// 0 is a perfectly ordinary coordinate — so the test measures the left edge of the drawn
/// annotation out of the decoded pixels and requires it to equal the left edge the extractor
/// reports for the same rectangle in the same file. That is the only form in which this defect is
/// visible at all, and it is why it survived: each component was self-consistent.</para>
/// </remarks>
public sealed class IndirectRectElementTests
{
    private const float ExpectedLeft = 100f;   // object 12
    private const float Band = 710f;           // inside /Rect's y range (700..720)

    [Fact]
    public void RasterAnnotationRect_ResolvesAnIndirectElement_AndAgreesWithTheLinkExtractor()
    {
        byte[] pdf = AnnotationWithIndirectRect();

        using var raster = PixelAssert.Render(pdf);
        float drawnLeft = LeftmostInkX(raster, Band);

        using var input = new MemoryStream(pdf);
        var link = Assert.Single(PdfLinkExtractor.ExtractLinks(input));

        Assert.Equal(ExpectedLeft, link.X, 1);

        // Within a pixel of the border stroke, which straddles the rectangle's edge.
        Assert.InRange(drawnLeft, link.X - 2f, link.X + 2f);

        // And nothing was painted in the strip the unresolved reading would have covered.
        Assert.True(PixelAssert.IsWhite(PixelAssert.At(raster, 40f, Band)),
            "the annotation was drawn from x=0: the /Rect element was read unresolved");
    }

    [Fact]
    public void SvgAnnotationRect_ResolvesAnIndirectElement()
    {
        byte[] pdf = AnnotationWithIndirectRect();

        using var ms = new MemoryStream(pdf);
        string svg = PdfSvgRenderer.RenderPageToSvg(ms, 0);

        var group = System.Xml.Linq.XDocument.Parse(svg).Descendants()
            .Single(e => e.Attribute("data-annot-subtype")?.Value == "Square");
        var rect = Assert.Single(group.Descendants().Where(e => e.Name.LocalName == "rect"));

        Assert.Equal(ExpectedLeft, Attr(rect, "x"), 1);
        Assert.Equal(100f, Attr(rect, "width"), 1);
    }

    // ------------------------------------------------------------------

    private static float Attr(System.Xml.Linq.XElement e, string name) =>
        float.Parse(e.Attribute(name)?.Value ?? "NaN", CultureInfo.InvariantCulture);

    /// <summary>The leftmost inked pixel on the row at PDF user-space <paramref name="userY"/>.</summary>
    private static float LeftmostInkX(SKBitmap bmp, float userY)
    {
        int y = bmp.Height - (int)userY;
        for (int x = 0; x < bmp.Width; x++)
            if (PixelAssert.IsInk(bmp.GetPixel(x, y))) return x;

        Assert.Fail("nothing was drawn on the annotation's row at all");
        return -1f;
    }

    /// <summary>
    /// A page carrying a filled Square annotation and a Link annotation, both with the SAME
    /// <c>/Rect</c> array: <c>[12 0 R 700 200 720]</c>, object 12 = 100.
    /// </summary>
    private static byte[] AnnotationWithIndirectRect()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        int o1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int o2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int o3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                  + " /Contents 4 0 R /Annots [5 0 R 6 0 R] /Resources << >> >>\nendobj\n");

        const string cs = "q Q";
        int o4 = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", cs.Length, cs);

        int o5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Annot /Subtype /Square /Rect [12 0 R 700 200 720]"
                  + " /C [0 0 0] /IC [0 0 0] >>\nendobj\n");

        int o6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Annot /Subtype /Link /Rect [12 0 R 700 200 720]"
                  + " /A << /S /URI /URI (https://example.test) >> >>\nendobj\n");

        // Objects 7..11 are unused; 12 is the coordinate the /Rect refers to.
        int[] filler = new int[5];
        for (int i = 0; i < 5; i++)
        {
            filler[i] = sb.Length;
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0} 0 obj\nnull\nendobj\n", 7 + i);
        }

        int o12 = sb.Length;
        sb.Append("12 0 obj\n100\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 13\n0000000000 65535 f \n");
        foreach (int off in new[] { o1, o2, o3, o4, o5, o6, filler[0], filler[1], filler[2], filler[3], filler[4], o12 })
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 13 /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
