// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// The sub-byte TIFF predictor sizes its buffers from the data it was given, never from the
/// dimensions alone.
/// </summary>
/// <remarks>
/// <para>A row buffer allocated before the row loop, from <c>/Columns × /Colors</c>, would let a
/// stream of ten bytes declaring <c>/Colors 32 /BitsPerComponent 4 /Columns 1000000</c> buy 128 MB —
/// 32 M samples as <c>int</c>s — for zero rows, and the dimensions pass <c>RejectDimensions</c>,
/// whose bound is the row's. Reached from any Flate or LZW stream's <c>/DecodeParms</c>.</para>
/// <para>What is asserted is the allocation itself, through
/// <see cref="GC.GetAllocatedBytesForCurrentThread"/>, beside the decoded bytes — never a clock.</para>
/// </remarks>
public sealed class PredictorAllocationTests
{
    [Fact]
    public void TiffSubByte_WithNoWholeRow_AllocatesNothingFromTheDimensions()
    {
        // /Colors 32 /BitsPerComponent 4 /Columns 1000000: a 16 MB row, 32 M samples — over ten bytes.
        byte[] data = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        PredictorHelper.RemoveTiffPredictor(new byte[] { 0x11 }, 2, 1, 4); // warm the path

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] result = PredictorHelper.RemoveTiffPredictor(data, 1_000_000, 32, 4);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(data, result);   // no whole row to decode: the bytes come back as they were…
        Assert.NotSame(data, result); // …as a copy, which is the predictor's contract
        Assert.True(allocated < 64 * 1024, $"{allocated} bytes allocated for a ten-byte stream");
    }

    [Fact]
    public void TiffSubByte_WithARow_AllocatesInProportionToTheData_AndDecodesIt()
    {
        // One row of 250 000 columns × 32 colours × 4 bits = 4 MB, every sample 1.
        const int columns = 250_000, colors = 32;
        byte[] data = new byte[columns * colors / 2];
        for (int i = 0; i < data.Length; i++) data[i] = 0x11;
        PredictorHelper.RemoveTiffPredictor(new byte[] { 0x11 }, 2, 1, 4); // warm the path

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] result = PredictorHelper.RemoveTiffPredictor(data, columns, colors, 4);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        // The running sum over 32 colours: samples 0-31 stay 1, 32-63 become 2, 64-95 become 3, …
        // and samples 480-511 wrap to 0 (16 mod 16). Two samples per byte.
        Assert.Equal(data.Length, result.Length);
        Assert.Equal(0x11, result[0]);
        Assert.Equal(0x22, result[16]);
        Assert.Equal(0x33, result[32]);
        Assert.Equal(0x00, result[240]);
        // The output copy and a byte per sample: under four times the data. An int per sample was nine.
        Assert.True(allocated < 4L * data.Length, $"{allocated} bytes allocated for {data.Length} bytes of data");
    }
}
