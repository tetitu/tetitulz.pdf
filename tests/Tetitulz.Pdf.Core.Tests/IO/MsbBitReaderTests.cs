// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Functions;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// The one MSB-first bit read, shared by LZW code reading and Type 0 sampled-function samples.
/// </summary>
/// <remarks>
/// <para>Two copies carried this rule, agreeing on every input either caller could produce and
/// structurally free to stop. They already differed on two: a 32-bit read, where LZW's <c>int</c>
/// accumulator set the sign bit, and an offset starting before the array, where LZW abandoned the
/// read while the function evaluator zero-filled and continued.</para>
///
/// <para><b>The 32-bit case decides the merge direction</b>, which is why it is asserted here
/// through the public function API and not only on the helper: ISO 32000-1 Table 38 lets
/// <c>/BitsPerSample</c> reach 32, so consolidating on the <c>int</c> form would have broken
/// 32-bit sampled functions silently — every sample negative, every output wrong, nothing
/// thrown.</para>
/// </remarks>
public sealed class MsbBitReaderTests
{
    /// <summary>The rule itself: bits come out most-significant first.</summary>
    [Fact]
    public void BitsAreReadMostSignificantFirst()
    {
        // 0b1010_0000 1000_0000 read as 9 bits from 0 is 0b1_0100_0001 = 0x141.
        Assert.Equal(0x141, MsbBitReader.ReadBits(new byte[] { 0xA0, 0x80 }, 0, 9));
    }

    /// <summary>
    /// <b>A 32-bit read is unsigned.</b> The abandoned copy accumulated into an <c>int</c> with
    /// <c>result |= 1 &lt;&lt; (count - 1 - i)</c>, so bit 0 set the sign bit and the answer came
    /// back as <c>-1</c>.
    /// </summary>
    [Fact]
    public void AFullWidthReadIsNotSignExtended()
    {
        Assert.Equal(4294967295L, MsbBitReader.ReadBits(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0, 32));
    }

    /// <summary>
    /// Bits outside the array read as zero and the read <b>continues</b>. The two copies came apart
    /// exactly here: an offset that starts before the array and runs back into it gave 0 from the
    /// abandoning form and 1 from this one.
    /// </summary>
    [Fact]
    public void BitsBeforeTheArrayReadAsZeroAndTheReadContinues()
    {
        Assert.Equal(1, MsbBitReader.ReadBits(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, -8, 9));
    }

    /// <summary>Past the end is the same rule, and is the case both callers actually reach.</summary>
    [Theory]
    [InlineData(-64)]
    [InlineData((long)int.MaxValue + 64)]
    public void BitsPastEitherEndReadAsZero(long bitOffset)
    {
        Assert.Equal(0, MsbBitReader.ReadBits(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, bitOffset, 9));
    }

    /// <summary>
    /// The LZW forwarder narrows to <c>int</c>, and that is safe: its <c>numBits</c> is the code
    /// size, bounded to 12. At 32 it is even byte-identical to the old positional accumulator,
    /// because <c>(int)0xFFFFFFFF</c> is <c>-1</c> — which is what that accumulator produced.
    /// </summary>
    [Fact]
    public void TheLzwForwarderAgreesWithTheSharedReaderOverItsOwnDomain()
    {
        byte[] data = { 0xA0, 0x80, 0x3C, 0x5A };

        for (int numBits = 9; numBits <= 12; numBits++)
            for (long pos = 0; pos + numBits <= data.Length * 8; pos++)
                Assert.Equal(
                    (int)MsbBitReader.ReadBits(data, pos, numBits),
                    LzwDecodeFilter.ReadBits(data, pos, numBits));
    }

    /// <summary>
    /// <b>The consequence, through the public API.</b> A Type 0 sampled function at
    /// <c>/BitsPerSample 32</c> — legal per Table 38 — must interpolate its samples as unsigned.
    /// On the <c>int</c> accumulator every sample read back negative.
    /// </summary>
    [Fact]
    public void ASampledFunctionAtThirtyTwoBitsPerSampleReadsItsSamplesUnsigned()
    {
        // Two samples: 0x00000000 and 0xFFFFFFFF. Domain [0 1] maps onto them, so input 1 must
        // yield the maximum, which /Range clamps to 1.0.
        var samples = new byte[]
        {
            0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF,
        };

        var fn = new PdfStream();
        fn.Set(PdfName.FunctionType, new PdfNumber(0));
        fn.Set(PdfName.Domain, new PdfArray(new PdfObject[] { new PdfNumber(0), new PdfNumber(1) }));
        fn.Set(PdfName.Range, new PdfArray(new PdfObject[] { new PdfNumber(0), new PdfNumber(1) }));
        fn.Set(PdfName.Size, new PdfArray(new PdfObject[] { new PdfNumber(2) }));
        fn.Set(PdfName.BitsPerSample, new PdfNumber(32));
        fn.RawData = samples;

        float[] atZero = PdfFunctionEvaluator.Evaluate(fn, new[] { 0f });
        float[] atOne = PdfFunctionEvaluator.Evaluate(fn, new[] { 1f });

        Assert.NotNull(atZero);
        Assert.NotNull(atOne);
        Assert.Equal(0f, atZero[0], 3);

        // The defect: a negative sample here produced 0, not 1 — indistinguishable from the low
        // end of the ramp, so the function returned the same answer at both ends of its domain.
        Assert.Equal(1f, atOne[0], 3);
        Assert.True(atOne[0] > atZero[0], "the ramp must rise across its domain");
    }
}
