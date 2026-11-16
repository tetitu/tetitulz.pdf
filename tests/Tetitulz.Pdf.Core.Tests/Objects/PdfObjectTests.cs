// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Tests.Objects;

public class PdfObjectTests
{
    [Fact]
    public void PdfName_Equality()
    {
        var name1 = new PdfName("Type");
        var name2 = new PdfName("Type");
        var name3 = new PdfName("Page");

        Assert.Equal(name1, name2);
        Assert.NotEqual(name1, name3);
        Assert.True(name1 == name2);
        Assert.True(name1 != name3);
    }

    [Fact]
    public void PdfName_WellKnownNames()
    {
        Assert.Equal("Type", PdfName.Type.Value);
        Assert.Equal("Page", PdfName.Page.Value);
        Assert.Equal("Catalog", PdfName.Catalog.Value);
    }

    [Fact]
    public void PdfNumber_Integer()
    {
        var num = new PdfNumber(42);
        Assert.True(num.IsInteger);
        Assert.Equal(42, num.IntValue);
        Assert.Equal(42.0, num.DoubleValue);
    }

    [Fact]
    public void PdfNumber_Real()
    {
        var num = new PdfNumber(3.14);
        Assert.False(num.IsInteger);
        Assert.Equal(3.14, num.DoubleValue, 5);
    }

    [Fact]
    public void PdfBoolean_Values()
    {
        Assert.True(PdfBoolean.True.Value);
        Assert.False(PdfBoolean.False.Value);
    }

    [Fact]
    public void PdfNull_Singleton()
    {
        Assert.Same(PdfNull.Instance, PdfNull.Instance);
    }

    [Fact]
    public void PdfString_GetText()
    {
        var str = new PdfString("Hello");
        Assert.Equal("Hello", str.GetText());
    }

    [Fact]
    public void PdfString_HexString()
    {
        var str = new PdfString(new byte[] { 0x48, 0x65, 0x6C, 0x6C, 0x6F }, true);
        Assert.Equal("Hello", str.GetText());
        Assert.True(str.IsHex);
    }

    [Fact]
    public void PdfReference_Equality()
    {
        var ref1 = new PdfReference(10, 0);
        var ref2 = new PdfReference(10, 0);
        var ref3 = new PdfReference(11, 0);

        Assert.Equal(ref1, ref2);
        Assert.NotEqual(ref1, ref3);
    }

    [Fact]
    public void PdfDictionary_SetAndGet()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Page);
        dict.Set(PdfName.Count, new PdfNumber(5));

        Assert.Equal("Page", dict.GetName(PdfName.Type)?.Value);
        Assert.Equal(5, dict.GetInteger(PdfName.Count));
        Assert.Equal(2, dict.Count);
    }

    [Fact]
    public void PdfDictionary_ReturnsNullForMissingKey()
    {
        var dict = new PdfDictionary();
        Assert.Null(dict.GetName(PdfName.Type));
        Assert.Null(dict.GetInteger(PdfName.Count));
    }

    [Fact]
    public void PdfDictionary_ContainsKey()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Page);

        Assert.True(dict.ContainsKey(PdfName.Type));
        Assert.False(dict.ContainsKey(PdfName.Count));
    }

    [Fact]
    public void PdfDictionary_Remove()
    {
        var dict = new PdfDictionary();
        dict.Set(PdfName.Type, PdfName.Page);
        Assert.True(dict.Remove(PdfName.Type));
        Assert.False(dict.ContainsKey(PdfName.Type));
        Assert.Equal(0, dict.Count);
    }

    [Fact]
    public void PdfArray_BasicOperations()
    {
        var array = new PdfArray();
        array.Add(new PdfNumber(1));
        array.Add(new PdfNumber(2));
        array.Add(new PdfNumber(3));

        Assert.Equal(3, array.Count);
        Assert.Equal(1, array.GetInteger(0));
        Assert.Equal(2, array.GetInteger(1));
        Assert.Equal(3, array.GetInteger(2));
    }

    [Fact]
    public void PdfArray_GetTypedValues()
    {
        var array = new PdfArray();
        array.Add(PdfName.Type);
        array.Add(new PdfNumber(42));

        Assert.Equal("Type", array.GetName(0)?.Value);
        Assert.Equal(42, array.GetInteger(1));
    }
}
