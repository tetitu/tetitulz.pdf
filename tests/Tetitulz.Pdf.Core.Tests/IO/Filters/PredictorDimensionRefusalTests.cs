// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The dimension guard both predictors share: <c>/Columns</c>, <c>/Colors</c> and
/// <c>/BitsPerComponent</c> must be positive and must not be large enough for their product to
/// overflow the row arithmetic that sizes the buffers.
/// </summary>
/// <remarks>
/// <para><b>Why these exist.</b> The ladder was written twice — once for PNG, once for TIFF,
/// differing only in the word in the message — and merging them onto <c>RejectDimensions</c> turned
/// "which fixtures reach this?" into one question. Nothing reached it: deleting the non-positive
/// refusal left the whole Core suite green. A DoS guard with no adversarial-input test is the case
/// CLAUDE.md names explicitly.</para>
///
/// <para><b>What is asserted is the guard's own evidence</b> — the reason it recorded and the data
/// coming back unchanged — never a wall clock. The point of the guard is that the hostile
/// dimensions are refused <i>before</i> anything is allocated from them, so there is nothing slow
/// to time.</para>
///
/// <para>Each refusal is paired with an acceptance control on ordinary dimensions, so a guard that
/// started refusing everything would not pass as a guard that refuses the right things.</para>
/// </remarks>
public sealed class PredictorDimensionRefusalTests
{
    /// <summary>Four rows of two bytes, each with a PNG filter byte in front.</summary>
    private static byte[] PngRows() => new byte[]
    {
        0, 0x10, 0x20,
        0, 0x30, 0x40,
        0, 0x50, 0x60,
        0, 0x70, 0x80,
    };

    [Theory]
    [InlineData(0, 1, 8)]          // /Columns 0
    [InlineData(-1, 1, 8)]         // negative
    [InlineData(2, 0, 8)]          // /Colors 0
    [InlineData(2, 1, 0)]          // /BitsPerComponent 0
    public void ANonPositiveDimension_IsRefusedAndSaysWhy_Png(int columns, int colors, int bpc)
    {
        byte[] data = PngRows();

        byte[] result = PredictorHelper.RemovePngPredictor(data, columns, colors, bpc);

        Assert.Equal(data, result);   // handed straight back, nothing allocated from the dimensions
        Assert.Contains("must all be positive", PredictorHelper.LastPredictorSkipReason);
        Assert.Contains("PNG", PredictorHelper.LastPredictorSkipReason);
    }

    [Theory]
    [InlineData(0, 1, 8)]
    [InlineData(2, 0, 8)]
    [InlineData(2, 1, 0)]
    public void ANonPositiveDimension_IsRefusedAndSaysWhy_Tiff(int columns, int colors, int bpc)
    {
        byte[] data = PngRows();

        byte[] result = PredictorHelper.RemoveTiffPredictor(data, columns, colors, bpc);

        Assert.Equal(data, result);
        Assert.Contains("must all be positive", PredictorHelper.LastPredictorSkipReason);
        Assert.Contains("TIFF", PredictorHelper.LastPredictorSkipReason);
    }

    /// <summary>
    /// An oversized dimension is refused for a different reason, and says a different thing — the
    /// product of the three is what would overflow the row arithmetic and size a wrong buffer.
    /// </summary>
    [Theory]
    [InlineData(2_000_000, 1, 8)]      // /Columns past its bound
    [InlineData(2, 64, 8)]             // /Colors past its bound
    [InlineData(2, 1, 64)]             // /BitsPerComponent past its bound
    public void AnOversizedDimension_IsRefusedAndSaysWhy_Png(int columns, int colors, int bpc)
    {
        byte[] data = PngRows();

        byte[] result = PredictorHelper.RemovePngPredictor(data, columns, colors, bpc);

        Assert.Equal(data, result);
        Assert.Contains("is out of range", PredictorHelper.LastPredictorSkipReason);
    }

    [Theory]
    [InlineData(2_000_000, 1, 8)]
    [InlineData(2, 64, 8)]
    [InlineData(2, 1, 64)]
    public void AnOversizedDimension_IsRefusedAndSaysWhy_Tiff(int columns, int colors, int bpc)
    {
        byte[] data = PngRows();

        byte[] result = PredictorHelper.RemoveTiffPredictor(data, columns, colors, bpc);

        Assert.Equal(data, result);
        Assert.Contains("is out of range", PredictorHelper.LastPredictorSkipReason);
    }

    /// <summary>
    /// The acceptance control. Ordinary dimensions are accepted, the filter bytes are stripped, and
    /// nothing is recorded as skipped — so the refusals above are about the dimensions rather than
    /// about a predictor that refuses everything.
    /// </summary>
    [Fact]
    public void OrdinaryDimensions_AreAccepted_AndNothingIsRecordedAsSkipped()
    {
        byte[] result = PredictorHelper.RemovePngPredictor(PngRows(), columns: 2, colors: 1, bitsPerComponent: 8);

        Assert.Null(PredictorHelper.LastPredictorSkipReason);
        Assert.Equal(8, result.Length);        // four rows of two bytes, filter bytes removed
        Assert.Equal(0x10, result[0]);         // filter 0 (None) leaves the first row as written
        Assert.Equal(0x20, result[1]);
    }

    /// <summary>The TIFF control, for the same reason.</summary>
    [Fact]
    public void OrdinaryDimensions_AreAcceptedByTheTiffPredictorToo()
    {
        // TIFF predictor 2 has no per-row filter byte: two rows of two 8-bit samples.
        byte[] data = { 0x10, 0x05, 0x20, 0x03 };

        byte[] result = PredictorHelper.RemoveTiffPredictor(data, columns: 2, colors: 1, bitsPerComponent: 8);

        Assert.Null(PredictorHelper.LastPredictorSkipReason);
        Assert.Equal(4, result.Length);
        Assert.Equal(0x10, result[0]);
        Assert.Equal(0x15, result[1]);         // horizontal differencing undone: 0x10 + 0x05
    }
}
