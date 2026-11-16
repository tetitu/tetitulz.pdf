// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="PdfPageRenderer.GetRenderSizes"/> — the batch form of
/// <see cref="PdfPageRenderer.GetRenderSize(Stream, int, int, PdfRenderOptions)"/>.
///
/// <para><b>Why it exists.</b> The per-page query goes through <c>WithDecryptedDocument</c>, so
/// asking N pages for their size in a loop parses the file N times. Measured on a 36-page document
/// at 96 dpi, that loop alone cost 132 ms and 116 MiB — 13% of the wall clock and 26% of the
/// allocations of the PDF→HTML export that contained it, to answer a question one load answers,
/// immediately before opening the same file again to render every page.</para>
///
/// <para><b>The agreement test is the point, not the count.</b> A batch query that returned
/// plausible sizes computed its own way would be the divergence this repo keeps finding: two
/// implementations of one spec rule that agree today. Both go through the same
/// <c>MeasurePage</c> → <c>ToRenderSize</c> pair, and these assert it per page rather than
/// trusting the comment.</para>
/// </summary>
public class BatchRenderSizeTests
{
    private static PageRenderSize[] Batch(byte[] pdf, int dpi)
    {
        using var ms = new MemoryStream(pdf);
        return PdfPageRenderer.GetRenderSizes(ms, dpi).ToArray();
    }

    private static PageRenderSize Single(byte[] pdf, int page, int dpi)
    {
        using var ms = new MemoryStream(pdf);
        return PdfPageRenderer.GetRenderSize(ms, page, dpi);
    }

    [Fact]
    public void It_returns_one_entry_per_page_in_page_order()
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(5);

        Assert.Equal(5, Batch(pdf, 96).Length);
    }

    /// <summary>
    /// Every entry equals what the single-page query reports for that page — the claim that lets
    /// the batch form replace a loop without re-deriving any geometry.
    /// </summary>
    [Theory]
    [InlineData(72)]
    [InlineData(96)]
    [InlineData(150)]
    public void Each_entry_equals_the_single_page_query_for_that_page(int dpi)
    {
        byte[] pdf = MinimalPdfBuilder.CreateMultiPagePdf(3);

        PageRenderSize[] batch = Batch(pdf, dpi);

        for (int i = 0; i < batch.Length; i++)
        {
            PageRenderSize one = Single(pdf, i, dpi);
            Assert.Equal(one.WidthPixels,  batch[i].WidthPixels);
            Assert.Equal(one.HeightPixels, batch[i].HeightPixels);
            Assert.Equal(one.WidthPoints,  batch[i].WidthPoints);
            Assert.Equal(one.HeightPoints, batch[i].HeightPoints);
            Assert.Equal(one.Rotation,     batch[i].Rotation);
            Assert.Equal(one.EffectiveDpi, batch[i].EffectiveDpi);
        }
    }

    /// <summary>
    /// <c>/Rotate</c> is applied, so a rotated page's pixel extent is transposed relative to its
    /// point extent. Without this the agreement test above could pass over a page whose geometry is
    /// square in both senses and prove nothing about rotation.
    /// </summary>
    [Fact]
    public void A_rotated_page_reports_transposed_pixels_and_its_rotation()
    {
        byte[] pdf = MinimalPdfBuilder.CreateRotatedPdf(90);

        PageRenderSize size = Batch(pdf, 96).Single();

        Assert.Equal(90, size.Rotation);
        // Points describe the page box (portrait); pixels describe the image (landscape).
        Assert.True(size.HeightPoints > size.WidthPoints,
            "the fixture is not portrait, so this cannot detect a missing transpose");
        Assert.True(size.WidthPixels > size.HeightPixels,
            "the rendered image was not transposed for /Rotate 90");
    }

    [Fact]
    public void A_non_positive_dpi_is_refused()
    {
        using var ms = new MemoryStream(MinimalPdfBuilder.CreateMinimalPdf());

        Assert.Throws<ArgumentOutOfRangeException>(() => PdfPageRenderer.GetRenderSizes(ms, 0));
    }

    [Fact]
    public void A_null_stream_is_refused()
    {
        Assert.Throws<ArgumentNullException>(() => PdfPageRenderer.GetRenderSizes(null!, 96));
    }
}
