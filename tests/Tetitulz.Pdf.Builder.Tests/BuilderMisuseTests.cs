// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Builder;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Chained formatting styles the <b>preceding</b> run. Calling it when there is no current run is
/// a programming error, and it now says so instead of producing unstyled output.
/// </summary>
/// <remarks>
/// These were all silent no-ops (<c>if (_lastRun != null) …</c>). The first case below is the one
/// that matters: <c>Bold().AddText("x")</c> is the natural reading order — "make this bold, here
/// is the text" — and it compiled, ran, and produced plain text with nothing to indicate the call
/// had been ignored.
/// </remarks>
public class BuilderMisuseTests
{
    [Fact]
    public void FormattingBeforeAnyText_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DocumentBuilder().AddSection(s => s.AddParagraph(p => p.Bold().AddText("x"))));

        Assert.Contains("Bold()", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddText", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormattingAfterAnImage_Throws()
    {
        // AddImage ends the current run, so formatting cannot follow it — the failure the message
        // calls out explicitly, because it is not obvious from the call chain.
        Assert.Throws<InvalidOperationException>(() =>
            new DocumentBuilder().AddSection(s => s.AddParagraph(p => p
                .AddText("caption")
                .AddImage(new byte[] { 1, 2, 3 }, 10, 10)
                .Italic())));
    }

    [Fact]
    public void FormattingAfterAPageBreak_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new DocumentBuilder().AddSection(s => s.AddParagraph(p => p
                .AddText("before")
                .AddPageBreak()
                .FontSize(14))));
    }

    [Fact]
    public void CellFormattingBeforeAnyCell_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DocumentBuilder().AddSection(s => s.AddTable(t => t.AddRow(r => r.Bold().AddCell("x")))));

        Assert.Contains("AddCell", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every chained run-styling method, including the ones added when the fa&#231;ade was widened.
    /// </summary>
    /// <remarks>
    /// Enumerated rather than sampled: each of these is an independent call to
    /// <c>CurrentRun(nameof(...))</c>, so one written as <c>if (_lastRun != null)</c> would be
    /// invisible to a test that only exercised its neighbours — which is exactly how the original
    /// four came to be silent no-ops.
    /// </remarks>
    [Theory]
    [InlineData("DoubleStrike")]
    [InlineData("Highlight")]
    [InlineData("Superscript")]
    [InlineData("Subscript")]
    [InlineData("BaselineShift")]
    [InlineData("CharSpacing")]
    [InlineData("HorizontalScale")]
    [InlineData("SmallCaps")]
    [InlineData("AllCaps")]
    [InlineData("Link")]
    [InlineData("TextOpacity")]
    public void NewRunFormattingBeforeAnyText_Throws(string method)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DocumentBuilder().AddSection(s => s.AddParagraph(p => Apply(p, method).AddText("x"))));

        Assert.Contains(method + "()", ex.Message, StringComparison.Ordinal);
    }

    private static ParagraphBuilder Apply(ParagraphBuilder p, string method) => method switch
    {
        "DoubleStrike" => p.DoubleStrike(),
        "Highlight" => p.Highlight("#FFFF00"),
        "Superscript" => p.Superscript(),
        "Subscript" => p.Subscript(),
        "BaselineShift" => p.BaselineShift(2),
        "CharSpacing" => p.CharSpacing(1),
        "HorizontalScale" => p.HorizontalScale(80),
        "SmallCaps" => p.SmallCaps(),
        "AllCaps" => p.AllCaps(),
        "Link" => p.Link("https://example.com"),
        "TextOpacity" => p.TextOpacity(0.5),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "unmapped"),
    };

    [Theory]
    [InlineData("Underline")]
    [InlineData("Strikethrough")]
    [InlineData("FontName")]
    public void NewCellFormattingBeforeAnyCell_Throws(string method)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DocumentBuilder().AddSection(s => s.AddTable(t => t.AddRow(r =>
                ApplyCell(r, method).AddCell("x")))));

        Assert.Contains(method + "()", ex.Message, StringComparison.Ordinal);
    }

    private static RowBuilder ApplyCell(RowBuilder r, string method) => method switch
    {
        "Underline" => r.Underline(),
        "Strikethrough" => r.Strikethrough(),
        "FontName" => r.FontName("Courier"),
        _ => throw new ArgumentOutOfRangeException(nameof(method), method, "unmapped"),
    };

    [Fact]
    public void CellFormattingAfterABlockCell_Throws()
    {
        // The AddCell(Action<CellBuilder>) overload ends the current cell run, exactly as
        // AddImage ends the current text run: its content is built by a paragraph builder, which
        // has its own — and richer — formatting chain.
        Assert.Throws<InvalidOperationException>(() =>
            new DocumentBuilder().AddSection(s => s.AddTable(t => t.AddRow(r => r
                .AddCell("plain")
                .AddCell(c => c.AddParagraph(p => p.AddText("block")))
                .Bold()))));
    }

    [Fact]
    public void ListItemFormattingBeforeAnyText_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new DocumentBuilder().AddSection(s => s.AddListItem(l => l.Bold().AddText("x"))));

        Assert.Contains("Bold()", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: correct order still works, and styles only the run it follows. Without this
    /// the tests above would pass if the methods had simply been made to always throw.
    /// </summary>
    [Fact]
    public void FormattingAfterText_AppliesToThatRunOnly()
    {
        var doc = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p
                .AddText("plain ")
                .AddText("bold").Bold().FontSize(18)))
            .Build();

        var paragraph = Assert.IsType<Model.Paragraph>(doc.Sections[0].Children[0]);
        var runs = paragraph.Runs;
        Assert.Equal(2, runs.Count);

        var first = Assert.IsType<Model.TextRun>(runs[0]);
        var second = Assert.IsType<Model.TextRun>(runs[1]);

        Assert.False(first.IsBold);
        Assert.True(second.IsBold);
        Assert.Equal(18, second.FontSize);
    }
}
