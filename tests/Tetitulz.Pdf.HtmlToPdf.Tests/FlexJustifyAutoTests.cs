// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A non-wrapping flex line of auto-width items that grow nothing keeps the items at their measured
/// widths: the free space is <c>justify-content</c>'s to place — at the end for <c>flex-start</c> —
/// not stretched across the items with the value reported. A line the items overflow still shares
/// the width out, and a growing item still grows.
/// </summary>
public class FlexJustifyAutoTests
{
    /// <summary><c>space-between</c> on two auto items puts the second at the right edge, where a right-aligned paragraph puts its text (within a point: the measured width rounds up); nothing is reported.</summary>
    [Fact]
    public void SpaceBetween_PutsTheSecondAutoItemAtTheRightEdge()
    {
        var laid = Lay("<div style='display:flex;justify-content:space-between'><div>AAA</div><div>BBB</div></div>");
        var right = Lay("<p style='text-align:right;margin:0'>BBB</p>");
        var left = Lay("<p style='margin:0'>AAA</p>");

        Assert.InRange(Math.Abs(right["BBB"].X - laid["BBB"].X), 0, 1.0);
        Assert.Equal(left["AAA"].X, laid["AAA"].X, 1);
        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "the two items did not share the row");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// <c>center</c> leaves the same free space on each side: the first item's distance from the left
    /// margin is the second's right edge's from the right. The items are gapped, as every abutting
    /// fixture here is, or extraction reads two content-width items as one word.
    /// </summary>
    [Fact]
    public void Center_LeavesEqualSpaceOnEachSide()
    {
        var laid = Lay("<div style='display:flex;justify-content:center;column-gap:10px'><div>AAA</div><div>BBB</div></div>");
        var left = Lay("<p style='margin:0'>AAA</p>");
        var right = Lay("<p style='text-align:right;margin:0'>BBB</p>");

        double leftGap = laid["AAA"].X - left["AAA"].X;
        double rightGap = right["BBB"].X - laid["BBB"].X;
        Assert.True(leftGap > 100, $"the items were not moved in: left gap {leftGap:F1}");
        Assert.InRange(Math.Abs(leftGap - rightGap), 0, 1.0);
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// With nothing stated the items keep their content widths and the free space follows: BBB starts
    /// just after AAA's three letters and the 10px gap, where two 50% items put it half the band apart.
    /// </summary>
    [Fact]
    public void FlexStart_KeepsAutoItemsAtTheirContentWidths()
    {
        var laid = Lay("<div style='display:flex;column-gap:10px'><div>AAA</div><div>BBB</div></div>");
        var halves = Lay("<div style='display:flex;column-gap:10px'><div style='width:50%'>AAA</div><div style='width:50%'>BBB</div></div>");

        double step = laid["BBB"].X - laid["AAA"].X;
        Assert.True(step > 20 && step < 50, $"BBB is {step:F1}pt after AAA, not AAA's content width and the gap");
        Assert.True(halves["BBB"].X - halves["AAA"].X > 200, "the 50% control did not put BBB half the band across");
        Assert.Empty(laid.Reports);
    }

    /// <summary>Auto items whose content overflows the line keep the table's share-out: both on one row, the second well across; with <c>space-between</c> the overflow is reported.</summary>
    [Fact]
    public void AutoItemsOverflowingTheLine_StillShareTheWidth_AndTheValueIsReported()
    {
        const string Long = "alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike november oscar papa";
        var laid = Lay($"<div style='display:flex;justify-content:space-between;column-gap:10px'><div>AAA {Long}</div><div>BBB {Long}</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "the two items did not share the row");
        Assert.True(laid["BBB"].X - laid["AAA"].X > 150, $"BBB is only {laid["BBB"].X - laid["AAA"].X:F1}pt across");
        string report = Assert.Single(laid.Reports, r => r.StartsWith("justify-content", StringComparison.Ordinal));
        Assert.Contains("overflow the line", report, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gaps count in the fit: two items whose widths alone fit the line but not with a 100px gap
    /// between them overflow it, so they keep the table's share-out and the value is reported as an
    /// overflow; the same items with no gap fit, are placed, and nothing is reported.
    /// </summary>
    [Fact]
    public void TheGaps_CountInTheFit()
    {
        string w = new string('W', 23), m = new string('M', 23);
        var gapped = Lay($"<div style='display:flex;justify-content:space-between;column-gap:100px'><div>{w}</div><div>{m}</div></div>");
        var tight = Lay($"<div style='display:flex;justify-content:space-between'><div>{w}</div><div>{m}</div></div>");

        Assert.True(Math.Abs(gapped[w].Y - gapped[m].Y) < 2.0, "the gapped items did not share the row");
        string report = Assert.Single(gapped.Reports, r => r.StartsWith("justify-content", StringComparison.Ordinal));
        Assert.Contains("overflow the line", report, StringComparison.Ordinal);
        Assert.Empty(tight.Reports);
    }

    /// <summary>A list in a flex header is measured too: a logo and a horizontal nav list under <c>space-between</c> put the list's last item at the right.</summary>
    [Fact]
    public void ANavList_InASpaceBetweenHeader_SitsAtTheRight()
    {
        var laid = Lay("<div style='display:flex;justify-content:space-between'><div>Logo</div><ul style='display:flex;list-style:none;margin:0;padding:0;column-gap:10px'><li>One</li><li>Two</li></ul></div>");
        var right = Lay("<p style='text-align:right;margin:0'>Two</p>");

        Assert.True(Math.Abs(laid["Logo"].Y - laid["Two"].Y) < 3.0, "the nav did not share the logo's row");
        Assert.True(laid["Two"].X > laid["Logo"].X + 300, $"Two at {laid["Two"].X:F1} is not at the right of the header");
        Assert.True(right["Two"].X - laid["Two"].X < 12, $"Two at {laid["Two"].X:F1} is short of the right edge {right["Two"].X:F1}");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// A list displayed as a flex container lays its items out as the container does — One then Two
    /// on one row, a gap apart — where it was a column of items whatever its display said. The
    /// block-list control stacks them.
    /// </summary>
    [Fact]
    public void AFlexList_LaysItsItemsInARow()
    {
        var flex = Lay("<ul style='display:flex;list-style:none;margin:0;padding:0;column-gap:10px'><li>One</li><li>Two</li></ul>");
        var block = Lay("<ul style='list-style:none;margin:0;padding:0'><li>One</li><li>Two</li></ul>");

        Assert.True(Math.Abs(flex["One"].Y - flex["Two"].Y) < 2.0, "the flex list stacked its items");
        Assert.True(flex["Two"].X > flex["One"].X + 20, $"Two at {flex["Two"].X:F1} is not after One at {flex["One"].X:F1}");
        Assert.True(block["Two"].Y < block["One"].Y - 2.0, "the block list did not stack its items");
    }

    // ------------------------------------------------------------------

    private sealed class Laid
    {
        public readonly Dictionary<string, (double X, double Y)> Words = new(StringComparer.Ordinal);
        public readonly List<string> Reports = new();
        public (double X, double Y) this[string word]
        {
            get
            {
                Assert.True(Words.ContainsKey(word), $"'{word}' was not laid out at all");
                return Words[word];
            }
        }
    }

    private static Laid Lay(string html)
    {
        var laid = new Laid();
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d => { lock (laid.Reports) laid.Reports.Add(d.Detail ?? ""); },
        });
        using var input = new MemoryStream(output.ToArray(), writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            if (!laid.Words.ContainsKey(word.Text)) laid.Words[word.Text] = (word.X, word.Y);
        return laid;
    }
}
