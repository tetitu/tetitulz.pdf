// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Unit tests for <see cref="ContentOperand"/> — the shared operand-coercion helper
/// that the content-stream interpreter, SVG renderer, and graphics extractor all
/// delegate to. Verifies the out-of-range / type-mismatch defaulting rules that the
/// previously-duplicated per-file helpers relied on.
/// </summary>
public sealed class ContentOperandTests
{
    private static ContentStreamOperation Op(params PdfObject[] operands)
        => new ContentStreamOperation("x", operands);

    [Fact]
    public void Float_ReturnsValue_ForNumberOperand()
    {
        var op = Op(new PdfNumber(1.5), new PdfNumber(-2.0));
        Assert.Equal(1.5f, ContentOperand.Float(op, 0));
        Assert.Equal(-2.0f, ContentOperand.Float(op, 1));
    }

    [Theory]
    [InlineData(2)]   // index == length
    [InlineData(5)]   // index > length
    [InlineData(-1)]  // negative index
    public void Float_ReturnsZero_ForOutOfRangeIndex(int idx)
    {
        var op = Op(new PdfNumber(1.0), new PdfNumber(2.0));
        Assert.Equal(0f, ContentOperand.Float(op, idx));
    }

    [Fact]
    public void Float_ReturnsZero_ForNonNumberOperand()
    {
        var op = Op(new PdfName("Foo"));
        Assert.Equal(0f, ContentOperand.Float(op, 0));
    }

    [Fact]
    public void Int_And_Long_TruncateTowardZero()
    {
        var op = Op(new PdfNumber(3.9), new PdfNumber(-3.9));
        Assert.Equal(3, ContentOperand.Int(op, 0));
        Assert.Equal(-3, ContentOperand.Int(op, 1));
        Assert.Equal(3L, ContentOperand.Long(op, 0));
        Assert.Equal(-3L, ContentOperand.Long(op, 1));
    }

    [Fact]
    public void Int_And_Long_ReturnZero_ForOutOfRange()
    {
        var op = Op(new PdfNumber(1));
        Assert.Equal(0, ContentOperand.Int(op, 9));
        Assert.Equal(0L, ContentOperand.Long(op, 9));
    }

    [Fact]
    public void Name_ReturnsValue_OrNull()
    {
        var op = Op(new PdfName("DeviceRGB"), new PdfNumber(1));
        Assert.Equal("DeviceRGB", ContentOperand.Name(op, 0));
        Assert.Null(ContentOperand.Name(op, 1));   // not a name
        Assert.Null(ContentOperand.Name(op, 9));   // out of range
    }

    [Fact]
    public void StringBytes_ReturnsRawBytes_OrEmpty()
    {
        var bytes = new byte[] { 1, 2, 3 };
        Assert.Equal(bytes, ContentOperand.StringBytes(new PdfString(bytes)));
        Assert.Empty(ContentOperand.StringBytes(new PdfNumber(1)));
        Assert.Empty(ContentOperand.StringBytes(null));
    }

    [Fact]
    public void ArrayFloat_ReturnsValue_OrZero()
    {
        var arr = new PdfArray();
        arr.Add(new PdfNumber(4.25));
        arr.Add(new PdfName("NotANumber"));
        Assert.Equal(4.25f, ContentOperand.ArrayFloat(arr, 0));
        Assert.Equal(0f, ContentOperand.ArrayFloat(arr, 1));   // not a number
        Assert.Equal(0f, ContentOperand.ArrayFloat(arr, 9));   // out of range
        Assert.Equal(0f, ContentOperand.ArrayFloat(null, 0));  // null array
    }
}
