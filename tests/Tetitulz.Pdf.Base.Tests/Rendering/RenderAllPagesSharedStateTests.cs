// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>PdfPageRenderer.RenderAllPages(Stream)</c> — the sequential overload, which now renders every
/// page from <b>one</b> open <see cref="Tetitulz.Pdf.Core.Document.PdfDocument"/> with one
/// <see cref="FontCache"/>, one <see cref="ContentOperationCache"/> and one hidden-OCG set.
///
/// <para><b>What sharing can break, and therefore what is asserted here.</b> It used to open and
/// close a fresh document per page, so every page started from nothing. Sharing state across pages
/// is exactly the change that produces <i>nearly</i> the same output — a font resolved for page 1
/// reused for page 2, a parsed operation list handed out twice, a graphics state that outlived its
/// page — so the load-bearing test is a pixel comparison of each page against the same page
/// rendered on its own.</para>
///
/// <para>The parallel overloads deliberately keep a document per thread and are unchanged: the
/// parser's object cache and this shared state are plain dictionaries.</para>
/// </summary>
public class RenderAllPagesSharedStateTests
{
    private const int Dpi = 72;
    private const int PageCount = 4;

    /// <summary>
    /// Every page of the batch must match the same page rendered by itself, pixel for pixel.
    /// </summary>
    /// <remarks>
    /// Page 0 of the fixture ends with an unbalanced <c>q</c> and a <c>cm</c>, so a renderer that
    /// let one page's graphics state reach the next would draw page 1 at twice the scale. That
    /// state lives on <c>PdfRenderContext</c>, which is still built per page — this is the test
    /// that says so.
    /// </remarks>
    [Fact]
    public void RenderAllPages_IsPixelIdenticalToRenderingEachPageAlone()
    {
        byte[] pdf = MultiPagePdf();

        byte[][] batch = PdfPageRenderer.RenderAllPages(new MemoryStream(pdf), Dpi);
        Assert.Equal(PageCount, batch.Length);

        for (int i = 0; i < PageCount; i++)
        {
            using SKBitmap fromBatch = Decode(batch[i]);
            using SKBitmap alone     = Decode(PdfPageRenderer.RenderPage(new MemoryStream(pdf), i, Dpi));

            Assert.Equal(alone.Width, fromBatch.Width);
            Assert.Equal(alone.Height, fromBatch.Height);

            int differing = 0;
            for (int y = 0; y < alone.Height; y++)
                for (int x = 0; x < alone.Width; x++)
                    if (alone.GetPixel(x, y) != fromBatch.GetPixel(x, y)) differing++;

            Assert.True(differing == 0, $"page {i} differs from its standalone render in {differing} pixels");
        }
    }

    /// <summary>
    /// The control for the comparison above: the pages must genuinely differ from one another, or
    /// it would pass with every page rendering the same thing — or nothing.
    /// </summary>
    [Fact]
    public void RenderAllPages_EachPageDrawsItsOwnContent()
    {
        byte[][] batch = PdfPageRenderer.RenderAllPages(new MemoryStream(MultiPagePdf()), Dpi);

        for (int i = 0; i < PageCount; i++)
        {
            using SKBitmap bmp = Decode(batch[i]);

            // Page i paints its square i*20 units to the right of the last one.
            SKColor onSquare  = bmp.GetPixel(20 + i * 20, 150);
            SKColor offSquare = bmp.GetPixel(20 + i * 20, 20);

            Assert.True(onSquare.Red > 200 && onSquare.Green < 80,
                $"page {i} should paint its square at x={20 + i * 20}, found {onSquare}");
            Assert.True(offSquare.Red > 200 && offSquare.Green > 200,
                $"page {i} should be white above its square, found {offSquare}");
        }
    }

    // ------------------------------------------------------------------

    private static SKBitmap Decode(byte[] png)
    {
        var bmp = SKBitmap.Decode(png);
        Assert.NotNull(bmp);
        return bmp!;
    }

    /// <summary>
    /// Four 200x200 pages, each painting a red square 20 units further right than the last and
    /// showing text, all sharing one indirect <c>/Resources</c> dictionary naming one font object.
    /// Page 0 deliberately ends with an unbalanced <c>q</c> plus a <c>cm</c>.
    /// </summary>
    /// <remarks>
    /// The shared <c>/Resources</c> and the text are what make this fixture reach the change under
    /// test: with a font declared but never shown, the batch's shared <see cref="FontCache"/> is
    /// never consulted and the parity assertion above holds however the caches are wired. A font
    /// resolved for page 0 and reused for page 3 is the reuse the change exists for, and also the
    /// thing that could plausibly render them differently.
    /// </remarks>
    private static byte[] MultiPagePdf()
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        // 1 catalog, 2 page tree, 3 shared resources, then a (page, contents) pair per page.
        var kids = new StringBuilder();
        for (int i = 0; i < PageCount; i++) kids.Append(4 + i * 2).Append(" 0 R ");

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj($"2 0 obj\n<< /Type /Pages /Kids [{kids.ToString().Trim()}] /Count {PageCount} >>\nendobj\n");
        Obj("3 0 obj\n<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>\nendobj\n");

        for (int i = 0; i < PageCount; i++)
        {
            int pageObj = 4 + i * 2, contentObj = 5 + i * 2;
            string content = $"1 0 0 rg {20 + i * 20} 20 15 40 re f\n"
                           + $"BT /F1 18 Tf 0 g 20 120 Td (Page {i}) Tj ET\n"
                           + (i == 0 ? "q 2 0 0 2 0 0 cm\n" : string.Empty);

            Obj($"{pageObj} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] "
                + $"/Resources 3 0 R /Contents {contentObj} 0 R >>\nendobj\n");
            Obj($"{contentObj} 0 obj\n<< /Length {content.Length} >>\n"
                + $"stream\n{content}\nendstream\nendobj\n");
        }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");

        return acc.ToArray();
    }
}
