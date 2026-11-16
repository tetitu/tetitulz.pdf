// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="ListItem.MarkerFormat"/>: the marker's own style, over the item's, in a list and on a
/// loose item alike — the two paths a marker is drawn by.
/// </summary>
public class MarkerFormatTests
{
    private static ListItem Item()
    {
        var li = new ListItem { IsNumbered = true, Marker = "1.", TextFormat = new TextRun { Color = "#000000" },
                                MarkerFormat = new TextRun { Color = "#FF0000" } };
        li.Runs.Add(new TextRun { Text = "ITEM" });
        return li;
    }

    private static string ColourBefore(string content, string text)
    {
        int at = content.IndexOf("(" + text + ")", System.StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{text}' was not drawn");
        return Regex.Matches(content.Substring(0, at), @"([\d.]+ [\d.]+ [\d.]+) rg").Cast<Match>().Last().Groups[1].Value;
    }

    private static string Render(IDocElement block)
    {
        var section = new Section();
        section.Children.Add(block);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    [Fact]
    public void AListsMarkerTakesItsOwnFormat()
    {
        var list = new ListBlock { IsOrdered = true };
        list.Items.Add(Item());
        string c = Render(list);
        Assert.Equal("1 0 0", ColourBefore(c, "1."));
        Assert.Equal("0 0 0", ColourBefore(c, "ITEM"));
    }

    [Fact]
    public void ALooseItemsMarkerTakesItsOwnFormat()
    {
        string c = Render(Item());
        Assert.Equal("1 0 0", ColourBefore(c, "1."));
        Assert.Equal("0 0 0", ColourBefore(c, "ITEM"));
    }
}
