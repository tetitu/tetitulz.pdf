// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// When a list draws markers, and when its items are a line rather than a column: the
/// <c>list-style</c> shorthand, an item that is not <c>display: list-item</c>, and inline items.
/// </summary>
/// <remarks>
/// Bootstrap turns markers off with the shorthand (<c>list-style: none</c> on
/// <c>.list-unstyled</c>, <c>.breadcrumb</c>, <c>.nav</c>) and with <c>display: block</c> items
/// (<c>.list-group-item</c>), and lays breadcrumbs and pagers out with inline items. Only the
/// <c>list-style-type</c> longhand was read, so every one of those drew bullets, one item per line.
/// </remarks>
public class ListMarkerTests
{
    private const string Bullet = "•";

    private static List<PdfTextWord> Words(string html)
    {
        using var input = new MemoryStream(ToPdf(html), writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }

    private static int Bullets(string html) =>
        Words(html).Count(w => w.Text.Contains(Bullet, StringComparison.Ordinal));

    [Fact]
    public void APlainListDrawsItsBullets()
    {
        Assert.Equal(2, Bullets("<ul><li>ONE</li><li>TWO</li></ul>"));
    }

    [Theory]
    [InlineData("<ul style='list-style:none'><li>ONE</li><li>TWO</li></ul>")]
    [InlineData("<ul style='list-style-type:none'><li>ONE</li><li>TWO</li></ul>")]
    [InlineData("<ul style='list-style:none inside'><li>ONE</li><li>TWO</li></ul>")]
    [InlineData("<style>.unstyled{padding-left:0;list-style:none}</style><ul class='unstyled'><li>ONE</li><li>TWO</li></ul>")]
    public void ListStyleNone_InEitherSpelling_DrawsNoBullets(string html)
    {
        var words = Words(html);
        Assert.DoesNotContain(words, w => w.Text.Contains(Bullet, StringComparison.Ordinal));
        Assert.Contains(words, w => w.Text.Contains("TWO", StringComparison.Ordinal));
    }

    /// <summary>The item's own <c>list-style</c> overrides the list's, which it would otherwise inherit.</summary>
    [Fact]
    public void AnItemsOwnListStyleNone_RemovesOnlyItsBullet()
    {
        Assert.Equal(1, Bullets("<ul><li style='list-style:none'>ONE</li><li>TWO</li></ul>"));
    }

    /// <summary>
    /// The other direction: an item's own type wins over a list that says <c>none</c> — the item
    /// is consulted first, the list only when the item says nothing.
    /// </summary>
    [Fact]
    public void AnItemsOwnListStyle_OverridesAListThatSaysNone()
    {
        Assert.Equal(1, Bullets("<ul style='list-style:none'><li style='list-style:disc'>ONE</li><li>TWO</li></ul>"));
    }

    /// <summary>
    /// An item that is not a list item draws no marker — Bootstrap's <c>.list-group-item</c> is
    /// <c>display: block</c> — while <c>display: list-item</c> keeps it.
    /// </summary>
    [Fact]
    public void AnItemThatIsNotAListItem_DrawsNoMarker()
    {
        Assert.Equal(0, Bullets("<ul><li style='display:block'>ONE</li><li style='display:block'>TWO</li></ul>"));
        Assert.Equal(2, Bullets("<ul><li style='display:list-item'>ONE</li><li style='display:list-item'>TWO</li></ul>"));
    }

    /// <summary>
    /// A breadcrumb: inline items, one line, in order, with the <c>li + li:before</c> separator and
    /// no bullets.
    /// </summary>
    [Fact]
    public void InlineItems_AreOneLineInOrder_WithTheirGeneratedSeparators()
    {
        const string Html =
            "<style>.bc{list-style:none} .bc>li{display:inline-block} .bc>li+li:before{content:'/ '}</style>" +
            "<ol class='bc'><li>HOME</li><li>LIBRARY</li><li>DATA</li></ol>";
        var words = Words(Html);

        PdfTextWord Find(string t) => words.First(w => w.Text.Contains(t, StringComparison.Ordinal));
        var home = Find("HOME"); var library = Find("LIBRARY"); var data = Find("DATA");

        Assert.True(Math.Abs(home.Y - library.Y) < 2.0 && Math.Abs(library.Y - data.Y) < 2.0,
            "the inline items did not share a line");
        Assert.True(home.X < library.X && library.X < data.X, "the items are out of order");
        Assert.Contains(words, w => w.Text.Contains("/", StringComparison.Ordinal));
        // An inline list is a paragraph, not a numbered list: no "1." markers either.
        Assert.DoesNotContain(words, w => w.Text == "1.");
    }

    /// <summary>The control: the same list with block items is a column.</summary>
    [Fact]
    public void BlockItems_StayAColumn()
    {
        var words = Words("<ul style='list-style:none'><li>HOME</li><li>LIBRARY</li></ul>");
        var home = words.First(w => w.Text.Contains("HOME", StringComparison.Ordinal));
        var library = words.First(w => w.Text.Contains("LIBRARY", StringComparison.Ordinal));
        Assert.True(library.Y < home.Y - 2.0, "block items shared a line");
    }

    /// <summary>A row of left-floated items (Bootstrap's tabs) is a line too.</summary>
    [Fact]
    public void LeftFloatedItems_AreOneLine()
    {
        var words = Words("<ul style='list-style:none'><li style='float:left'>TABA</li><li style='float:left'>TABB</li></ul>");
        var a = words.First(w => w.Text.Contains("TABA", StringComparison.Ordinal));
        var b = words.First(w => w.Text.Contains("TABB", StringComparison.Ordinal));
        Assert.True(Math.Abs(a.Y - b.Y) < 2.0 && a.X < b.X, "the floated tabs are not one line in order");
    }
}
