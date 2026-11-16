// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary><see cref="PdfOutlineJson"/>'s hand-written JSON reader.</summary>
public class OutlineJsonTests
{
    /// <summary>
    /// <b>A literal is recognised in place, not by copying the rest of the input.</b> The test was
    /// <c>Substring(_i).StartsWith(…)</c>, three copies per <c>null</c>, so an array of N literals
    /// cost O(N²) — the 100 kB of JSON here allocated about 6 GB. Allocation is the evidence, not a
    /// clock; the bookmark ahead of the literals shows the import really ran to the end.
    /// </summary>
    [Fact]
    public void AnArrayOfLiteralsIsReadWithoutCopyingTheRestOfTheInputPerLiteral()
    {
        const int literals = 20_000;
        var json = new StringBuilder("[{\"title\":\"Kept\",\"page\":0}");
        for (int i = 0; i < literals; i++) json.Append(",null");
        json.Append(']');
        string text = json.ToString();
        byte[] pdf = OnePagePdf();

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] result = Import(pdf, text);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 64L * 1024 * 1024,
            $"{allocated:N0} bytes were allocated to read {text.Length:N0} characters of JSON");
        var kept = Assert.Single(PdfBookmarksManager.ReadBookmarks(new MemoryStream(result)));
        Assert.Equal("Kept", kept.Title);
        Assert.Equal(0, kept.PageNumber);
    }

    /// <summary>The acceptance control: each literal is read as its value wherever it stands.</summary>
    [Fact]
    public void LiteralsAreReadAsValues_AndANullPageIsNoPage()
    {
        byte[] result = Import(OnePagePdf(),
            "[{\"title\":\"A\",\"page\":null,\"open\":true,\"bold\":false},{\"title\":\"B\",\"page\":0}]");

        var marks = PdfBookmarksManager.ReadBookmarks(new MemoryStream(result));
        Assert.Equal(new[] { "A", "B" }, marks.Select(m => m.Title));
        Assert.Null(marks[0].PageNumber);
        Assert.Equal(0, marks[1].PageNumber);
    }

    /// <summary>
    /// A literal cut short by the end of the input, or misspelled, is not one — the in-place compare
    /// must not read past the input or accept a prefix.
    /// </summary>
    [Theory]
    [InlineData("[tru")]
    [InlineData("[nul]")]
    [InlineData("[falsy]")]
    public void ALiteralCutShortOrMisspelledIsRefusedAsUnexpected(string json)
    {
        var ex = Assert.Throws<FormatException>(() => Import(OnePagePdf(), json));
        Assert.Contains("unexpected char", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An item's position, open state, colour and style round-trip through the JSON: imported, they
    /// are written to the outline; exported, they come back as members — and an item at its defaults
    /// carries none of them (the control).
    /// </summary>
    [Fact]
    public void ThePositionOpenStateColourAndStyle_RoundTrip()
    {
        byte[] result = Import(OnePagePdf(),
            "[{\"title\":\"Styled\",\"page\":0,\"top\":640.5,\"open\":true,\"color\":[1,0.5,0],\"bold\":true,\"italic\":true," +
            "\"children\":[{\"title\":\"Plain\",\"page\":0}]}]");

        var styled = Assert.Single(PdfBookmarksManager.ReadBookmarks(new MemoryStream(result)));
        Assert.Equal(640.5f, styled.Top);
        Assert.True(styled.IsOpen);
        Assert.Equal(new[] { 1f, 0.5f, 0f }, styled.Color);
        Assert.True(styled.IsBold);
        Assert.True(styled.IsItalic);
        var plain = Assert.Single(styled.Children);
        Assert.Null(plain.Top);
        Assert.False(plain.IsOpen);
        Assert.Null(plain.Color);
        Assert.False(plain.IsBold);

        string json = PdfOutlineJson.Export(new MemoryStream(result));
        Assert.Contains("\"top\": 640.5", json, StringComparison.Ordinal);
        Assert.Contains("\"open\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"color\": [1, 0.5, 0]", json, StringComparison.Ordinal);
        Assert.Contains("\"bold\": true", json, StringComparison.Ordinal);
        Assert.Contains("\"italic\": true", json, StringComparison.Ordinal);
        int plainAt = json.IndexOf("\"Plain\"", StringComparison.Ordinal);
        Assert.DoesNotContain("\"top\"", json.Substring(plainAt), StringComparison.Ordinal);
        Assert.DoesNotContain("\"color\"", json.Substring(plainAt), StringComparison.Ordinal);
    }

    /// <summary>
    /// A hostile value — a top past a float's range, a colour that is not three numbers, a page past
    /// an int — reads as its default, never as a refusal or a wrapped number.
    /// </summary>
    [Theory]
    [InlineData("\"top\":1e400")]
    [InlineData("\"top\":1e39")]
    [InlineData("\"color\":[2,\"x\",0]")]
    [InlineData("\"color\":[1,0]")]
    [InlineData("\"page\":1e300")]
    public void AHostileMember_ReadsAsItsDefault(string member)
    {
        byte[] result = Import(OnePagePdf(), "[{\"title\":\"A\",\"page\":0," + member + "}]");
        var mark = Assert.Single(PdfBookmarksManager.ReadBookmarks(new MemoryStream(result)));
        Assert.Equal("A", mark.Title);
        Assert.Null(mark.Top);
        Assert.Null(mark.Color);
    }

    private static byte[] Import(byte[] pdf, string json)
    {
        using var output = new MemoryStream();
        PdfOutlineJson.Import(new MemoryStream(pdf), output, json);
        return output.ToArray();
    }

    private static byte[] OnePagePdf() => FormPdfFixtures.Build(
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
}
