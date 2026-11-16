// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.StampSign;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.StampSign.Tests;

/// <summary>
/// The stamps drawn into the pages, read back by rendering: each picture where its rectangle says,
/// upright as the page is shown, in one revision, a shared picture written once.
/// </summary>
public sealed class StampWriterTests
{
    // A 2 x 2 picture whose four quadrants differ, so a turn or a mirror shows: red top-left,
    // green top-right, blue bottom-left, black bottom-right, all opaque.
    private static StampPixels Quadrants() => new(2, 2,
        new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 0, 0, 0 },
        new byte[] { 255, 255, 255, 255 });

    private static (byte R, byte G, byte B) Pixel(RenderedPage page, int x, int y)
    {
        int i = y * page.Stride + x * 4;
        return (page.Pixels[i], page.Pixels[i + 1], page.Pixels[i + 2]);
    }

    private static void AssertQuadrants(RenderedPage page, int left, int top, int width, int height)
    {
        int qx = width / 4, qy = height / 4;
        Assert.Equal(((byte)255, (byte)0, (byte)0), Pixel(page, left + qx, top + qy));
        Assert.Equal(((byte)0, (byte)255, (byte)0), Pixel(page, left + 3 * qx, top + qy));
        Assert.Equal(((byte)0, (byte)0, (byte)255), Pixel(page, left + qx, top + 3 * qy));
        Assert.Equal(((byte)0, (byte)0, (byte)0), Pixel(page, left + 3 * qx, top + 3 * qy));
    }

    [Fact]
    public void A_stamp_is_drawn_upright_where_its_rectangle_is()
    {
        byte[] pdf = MinimalPdfBuilder.CreateBlankPdf();   // 612 x 792, white
        // Shown at (100, 100), 200 x 100: in the page's own coordinates y runs 592..692.
        var stamp = new StampPlacement(0, new RectangleF(100, 592, 200, 100), Quadrants());
        byte[] stamped = StampWriter.Apply(pdf, new[] { stamp }, _ => 0);

        RenderedPage page = PdfOperations.RenderPageToPixels(new MemoryStream(stamped), 0, 72);
        AssertQuadrants(page, 100, 100, 200, 100);
        Assert.Equal(((byte)255, (byte)255, (byte)255), Pixel(page, 50, 50));   // outside it, the page
    }

    [Fact]
    public void On_a_turned_page_the_stamp_is_upright_as_shown()
    {
        byte[] pdf = MinimalPdfBuilder.CreateRotatedPdf(90);   // 612 x 792 page turned clockwise, grey
        var box = new RectangleF(0, 0, 612, 792);
        RectangleF rect = Placement.DisplayRectToPdf(box, 90, 100, 100, 200, 100);
        byte[] stamped = StampWriter.Apply(pdf, new[] { new StampPlacement(0, rect, Quadrants()) }, _ => 90);

        RenderedPage page = PdfOperations.RenderPageToPixels(new MemoryStream(stamped), 0, 72);
        Assert.Equal(792, page.Width);                          // shown turned
        AssertQuadrants(page, 100, 100, 200, 100);
    }

    [Fact]
    public void Stamps_on_several_pages_are_one_revision()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);
        var pixels = Quadrants();
        byte[] stamped = StampWriter.Apply(pdf, new[]
        {
            new StampPlacement(0, new RectangleF(10, 10, 50, 50), pixels),
            new StampPlacement(2, new RectangleF(10, 10, 50, 50), pixels),
        }, _ => 0);

        Assert.Equal(Count(pdf, "%%EOF") + 1, Count(stamped, "%%EOF"));
        Assert.True(stamped.AsSpan(0, pdf.Length).SequenceEqual(pdf));   // appended, the original untouched
    }

    [Fact]
    public void A_picture_two_stamps_share_is_written_once()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(2);
        var shared = Quadrants();
        byte[] once = StampWriter.Apply(pdf, new[]
        {
            new StampPlacement(0, new RectangleF(10, 10, 50, 50), shared),
            new StampPlacement(1, new RectangleF(10, 10, 50, 50), shared),
        }, _ => 0);
        // The control: two different pictures are two images.
        var other = new StampPixels(1, 1, new byte[] { 0, 255, 255 }, new byte[] { 255 });
        byte[] twice = StampWriter.Apply(pdf, new[]
        {
            new StampPlacement(0, new RectangleF(10, 10, 50, 50), shared),
            new StampPlacement(1, new RectangleF(10, 10, 50, 50), other),
        }, _ => 0);

        Assert.Equal(1, Count(once, @"/Subtype\s*/Image"));
        Assert.Equal(2, Count(twice, @"/Subtype\s*/Image"));
    }

    [Fact]
    public void No_stamps_returns_the_document_itself()
    {
        byte[] pdf = MinimalPdfBuilder.CreateBlankPdf();
        Assert.Same(pdf, StampWriter.Apply(pdf, Array.Empty<StampPlacement>(), _ => 0));
    }

    [Fact]
    public void Samples_that_do_not_fit_the_size_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new StampPixels(2, 2, new byte[11], new byte[4]));
        Assert.Throws<ArgumentException>(() => new StampPixels(2, 2, new byte[12], new byte[3]));
        Assert.Equal(2, new StampPixels(2, 2, new byte[12], new byte[4]).Width);
    }

    private static int Count(byte[] pdf, string pattern)
        => Regex.Matches(Encoding.Latin1.GetString(pdf), pattern, RegexOptions.None, TimeSpan.FromSeconds(1)).Count;
}
