// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Tests for fixes from the final comprehensive code review.
/// </summary>
public class FinalReviewTests
{
    // ================================================================
    //  PdfString: UTF-16 LE BOM
    // ================================================================

    [Fact]
    public void PdfString_Utf16LE_DecodedCorrectly()
    {
        // UTF-16 LE BOM: FF FE, then "Hi" in UTF-16 LE
        byte[] data = new byte[] { 0xFF, 0xFE, 0x48, 0x00, 0x69, 0x00 };
        var str = new PdfString(data);
        Assert.Equal("Hi", str.GetText());
    }

    [Fact]
    public void PdfString_Utf16BE_StillWorks()
    {
        // UTF-16 BE BOM: FE FF, then "Hi" in UTF-16 BE
        byte[] data = new byte[] { 0xFE, 0xFF, 0x00, 0x48, 0x00, 0x69 };
        var str = new PdfString(data);
        Assert.Equal("Hi", str.GetText());
    }

    [Fact]
    public void PdfString_Utf8BOM_DecodedCorrectly()
    {
        byte[] data = new byte[] { 0xEF, 0xBB, 0xBF, 0x48, 0x65, 0x6C, 0x6C, 0x6F };
        var str = new PdfString(data);
        Assert.Equal("Hello", str.GetText());
    }

    [Fact]
    public void PdfString_PdfDocEncoding_DefaultFallback()
    {
        byte[] data = Encoding.ASCII.GetBytes("Hello");
        var str = new PdfString(data);
        Assert.Equal("Hello", str.GetText());
    }

    // ================================================================
    //  PdfVersion: comparison operators
    // ================================================================

    [Fact]
    public void PdfVersion_CompareGreater()
    {
        var v14 = new PdfVersion(1, 4);
        var v17 = new PdfVersion(1, 7);
        Assert.True(v17 > v14);
        Assert.True(v14 < v17);
    }

    [Fact]
    public void PdfVersion_CompareEqual()
    {
        var a = new PdfVersion(2, 0);
        var b = new PdfVersion(2, 0);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a, b);
    }

    [Fact]
    public void PdfVersion_CompareMajorDifference()
    {
        var v1 = new PdfVersion(1, 9);
        var v2 = new PdfVersion(2, 0);
        Assert.True(v2 > v1);
    }

    [Fact]
    public void PdfVersion_GetHashCode_Equal()
    {
        var a = new PdfVersion(1, 7);
        var b = new PdfVersion(1, 7);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void PdfVersion_ToString()
    {
        Assert.Equal("1.4", new PdfVersion(1, 4).ToString());
        Assert.Equal("2.0", new PdfVersion(2, 0).ToString());
    }

    // ================================================================
    //  PdfMetadata: date parsing
    // ================================================================

    [Fact]
    public void PdfMetadata_ParseDate_FullFormat()
    {
        var result = PdfMetadata.ParsePdfDate("D:20230615120000+02'00'");
        Assert.NotNull(result);
        Assert.Equal(2023, result.Value.Year);
        Assert.Equal(6, result.Value.Month);
        Assert.Equal(15, result.Value.Day);
        Assert.Equal(12, result.Value.Hour);
        Assert.Equal(2, result.Value.Offset.Hours);
    }

    [Fact]
    public void PdfMetadata_ParseDate_YearOnly()
    {
        var result = PdfMetadata.ParsePdfDate("D:2023");
        Assert.NotNull(result);
        Assert.Equal(2023, result.Value.Year);
        Assert.Equal(1, result.Value.Month);
        Assert.Equal(1, result.Value.Day);
    }

    [Fact]
    public void PdfMetadata_ParseDate_NegativeTimezone()
    {
        var result = PdfMetadata.ParsePdfDate("D:20230615120000-05'30'");
        Assert.NotNull(result);
        Assert.Equal(-5, result.Value.Offset.Hours);
        Assert.Equal(-30, result.Value.Offset.Minutes);
    }

    [Fact]
    public void PdfMetadata_ParseDate_Null_ReturnsNull()
    {
        Assert.Null(PdfMetadata.ParsePdfDate(null));
        Assert.Null(PdfMetadata.ParsePdfDate(""));
        Assert.Null(PdfMetadata.ParsePdfDate("xyz"));
    }

    [Fact]
    public void PdfMetadata_ParseDate_NoPrefixStillWorks()
    {
        var result = PdfMetadata.ParsePdfDate("20230101000000");
        Assert.NotNull(result);
        Assert.Equal(2023, result.Value.Year);
    }

    // ================================================================
    //  PdfArray: bounds checking
    // ================================================================

    [Fact]
    public void PdfArray_GetName_OutOfBounds_ReturnsNull()
    {
        var arr = new PdfArray();
        Assert.Null(arr.GetName(0));
        Assert.Null(arr.GetName(-1));
        Assert.Null(arr.GetName(999));
    }

    [Fact]
    public void PdfArray_GetDictionary_OutOfBounds_ReturnsNull()
    {
        var arr = new PdfArray();
        Assert.Null(arr.GetDictionary(0));
    }

    [Fact]
    public void PdfArray_GetInteger_OutOfBounds_ThrowsArgumentOutOfRange()
    {
        var arr = new PdfArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => arr.GetInteger(0));
    }

    [Fact]
    public void PdfArray_GetInteger_ValidIndex_Works()
    {
        var arr = new PdfArray();
        arr.Add(new PdfNumber(42));
        Assert.Equal(42, arr.GetInteger(0));
    }

    // ================================================================
    //  PdfWriter: WriteXrefTable doesn't mutate caller's list
    // ================================================================

    [Fact]
    public void PdfWriter_WriteXrefTable_DoesNotMutateInput()
    {
        var entries = new System.Collections.Generic.List<(int objNum, long offset)>
        {
            (3, 300), (1, 100), (2, 200)
        };
        int originalCount = entries.Count;
        var originalFirst = entries[0];

        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteXrefTable(entries);

        // Caller's list should not be modified
        Assert.Equal(originalCount, entries.Count);
        Assert.Equal(originalFirst, entries[0]);
    }

    // ================================================================
    //  Integration: PdfDocumentBuilder + PdfVersion
    // ================================================================

    [Fact]
    public void DocumentBuilder_Version17_ParsedCorrectly()
    {
        var builder = new PdfDocumentBuilder().WithVersion(new PdfVersion(1, 7));
        var page = builder.AddPage(612, 792);
        page.AddStandardFont("Helvetica");
        page.BeginText().SetFont("F1", 12).SetTextPosition(72, 700).ShowText("v17").EndText();

        using var ms = new MemoryStream();
        builder.Build(ms);
        ms.Position = 0;
        using var doc = PdfDocument.Open(ms);
        Assert.Equal(new PdfVersion(1, 7), doc.Version);
    }
}
