// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// A file-supplied integer past the <c>int</c> range is read as out of range — like
/// <see cref="int.MaxValue"/> — never by its low 32 bits. <c>(int)LongValue</c> read
/// 2^32 + k as k, so an invalid value arrived as a valid, different one.
/// </summary>
/// <remarks>
/// Each test holds three readings side by side: 2^32 + k must read as int.MaxValue does (the
/// claim), and k must read differently from int.MaxValue (the potency — without it the first
/// assertion could pass with the wrap still in place).
/// </remarks>
public sealed class OutOfRangeIntegerReadTests
{
    private const long TwoTo32 = 1L << 32;

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void ContentOperandInt_ClampsPastTheIntRange_NeverKeepsTheLow32Bits(int k)
    {
        Assert.Equal(k, ContentOperand.Int(Op(k), 0));
        Assert.Equal(int.MaxValue, ContentOperand.Int(Op(TwoTo32 + k), 0));    // `Tr 4294967299` is not mode 3
        Assert.Equal(int.MinValue, ContentOperand.Int(Op(-TwoTo32 + k), 0));

        static ContentStreamOperation Op(long v) => new("Tr", new PdfObject[] { new PdfNumber(v) });
    }

    /// <summary>A <c>/W</c> entry's first CID past the int range widens no small CID (format 1).</summary>
    [Fact]
    public void CidWidths_AFirstCidPastTheIntRange_ReadsAsOutOfRange()
    {
        static Dictionary<int, double> Widths(long c1)
        {
            var widths = new Dictionary<int, double>();
            PdfType0Font.ParseWArray(new PdfArray(new PdfObject[] { new PdfNumber(c1), PdfArray.OfNumbers(500) }), widths);
            return widths;
        }

        Assert.Equal(Widths(int.MaxValue), Widths(TwoTo32 + 65));
        Assert.NotEqual(Widths(int.MaxValue), Widths(65));
        Assert.Equal(500, Widths(65)[65]);
    }

    /// <summary>A <c>/W</c> range's last CID past the int range reads as out of range (format 2).</summary>
    [Fact]
    public void CidWidths_ALastCidPastTheIntRange_ReadsAsOutOfRange()
    {
        static Dictionary<int, double> Widths(long c2)
        {
            var widths = new Dictionary<int, double>();
            PdfType0Font.ParseWArray(new PdfArray(new PdfObject[]
                { new PdfNumber(70), new PdfNumber(c2), new PdfNumber(600) }), widths);
            return widths;
        }

        Assert.Equal(Widths(int.MaxValue), Widths(TwoTo32 + 72));
        Assert.NotEqual(Widths(int.MaxValue), Widths(72));
    }

    [Fact]
    public void FunctionType_PastTheIntRange_IsNotTypeTwo()
    {
        static float[] At(long type) => PdfFunctionEvaluator.Evaluate(new PdfDictionary
        {
            [PdfName.FunctionType] = new PdfNumber(type),
            [PdfName.Domain] = PdfArray.OfNumbers(0, 1),
            [PdfName.C0] = PdfArray.OfNumbers(0),
            [PdfName.C1] = PdfArray.OfNumbers(1),
            [PdfName.N] = new PdfNumber(1),
        }, new[] { 0.5f }).ToArray();

        Assert.Equal(At(int.MaxValue), At(TwoTo32 + 2));
        Assert.NotEqual(At(int.MaxValue), At(2));
    }

    [Fact]
    public void SampledFunction_OrderPastTheIntRange_IsNotCubic()
    {
        Assert.Equal(Sampled(order: int.MaxValue), Sampled(order: TwoTo32 + 3));
        Assert.NotEqual(Sampled(order: int.MaxValue), Sampled(order: 3));
    }

    [Fact]
    public void SampledFunction_BitsPerSamplePastTheIntRange_IsNotEightBits()
    {
        Assert.Equal(Sampled(bitsPerSample: int.MaxValue), Sampled(bitsPerSample: TwoTo32 + 8));
        Assert.NotEqual(Sampled(bitsPerSample: int.MaxValue), Sampled(bitsPerSample: 8));
    }

    [Fact]
    public void SampledFunction_SizePastTheIntRange_IsNotASmallSize()
    {
        Assert.Equal(Sampled(size: int.MaxValue), Sampled(size: TwoTo32 + 4));
        Assert.NotEqual(Sampled(size: int.MaxValue), Sampled(size: 4));
    }

    /// <summary>
    /// A one-input sampled function over the samples 0, 255, 0, 255 evaluated at 0.4 — a point
    /// between samples, where linear and cubic interpolation part.
    /// </summary>
    private static float[] Sampled(long order = 1, long bitsPerSample = 8, long size = 4)
    {
        var fn = new PdfStream { RawData = new byte[] { 0, 255, 0, 255 } };
        fn.Set(PdfName.FunctionType, new PdfNumber(0));
        fn.Set(PdfName.Domain, PdfArray.OfNumbers(0, 1));
        fn.Set(PdfName.Range, PdfArray.OfNumbers(0, 1));
        fn.Set(PdfName.Size, new PdfArray(new PdfObject[] { new PdfNumber(size) }));
        fn.Set(PdfName.BitsPerSample, new PdfNumber(bitsPerSample));
        fn.Set(PdfName.Order, new PdfNumber(order));
        return PdfFunctionEvaluator.Evaluate(fn, new[] { 0.4f }).ToArray();
    }
}
