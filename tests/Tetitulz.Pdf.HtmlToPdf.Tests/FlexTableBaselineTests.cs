// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>align-items: baseline</c> for a flex item that starts with a table or a list: a
/// table's baseline is its first row's (CSS Tables 3 §3.10.2) — the baseline its
/// <c>vertical-align: baseline</c> cells share, else the bottom content edge of its lowest cell — and a
/// list's is its first item's. Each expectation is what Edge draws for the same markup: a middle-aligned
/// cell's word 2.25pt above the large word's baseline (its line's depth below the baseline), a
/// baseline-aligned cell's and a list item's word on it.
/// </summary>
public class FlexTableBaselineTests
{
    private const string Big = "<div style='font-size:36px'>BIGWORD</div>";

    private static (Dictionary<string, double> Y, string[] Reports) Lay(string item)
    {
        var reports = new List<string>();
        var options = Managed();
        options.OnDiagnostic = d => { lock (reports) reports.Add(d.Detail ?? ""); };
        var drawn = DrawnTexts(DecodeStreams(ToPdf($"<div style='display:flex;align-items:baseline'>{Big}{item}</div>", options)));
        var y = new Dictionary<string, double>();
        foreach (var t in drawn) y.TryAdd(t.Text, t.Y);
        return (y, reports.ToArray());
    }

    /// <summary>How far above the large word's baseline <paramref name="word"/> sits, with no baseline report.</summary>
    private static double Above(string item, string word)
    {
        var laid = Lay(item);
        Assert.DoesNotContain(laid.Reports, r => r.Contains("align-items: baseline", StringComparison.Ordinal));
        return laid.Y[word] - laid.Y["BIGWORD"];
    }

    /// <summary>
    /// A one-cell table, its cell middle-aligned (HTML's default): the cell's content bottom is on the
    /// baseline, so its word sits its line's depth above it — Edge's 2.25pt — where the same cell
    /// <c>vertical-align: baseline</c>, the control, puts the word on it.
    /// </summary>
    [Fact]
    public void AMiddleCellsContentBottom_IsTheTablesBaseline()
    {
        double middle = Above("<table><tr><td style='font-size:12px'>CELLWORD</td></tr></table>", "CELLWORD");
        double baseline = Above("<table><tr><td style='font-size:12px;vertical-align:baseline'>CELLWORD</td></tr></table>", "CELLWORD");

        Assert.Equal(0, baseline, 0.5);
        Assert.InRange(middle, 1.5, 3.5);
    }

    /// <summary>
    /// A row whose second cell is three lines tall: the lowest cell's content bottom is the baseline, so
    /// that cell's last word sits as the one-cell table's did and the short middle cell's word above it.
    /// </summary>
    [Fact]
    public void TheLowestCellDecides()
    {
        double one = Above("<table><tr><td style='font-size:12px'>CELLWORD</td></tr></table>", "CELLWORD");
        var laid = Lay("<table><tr><td style='font-size:12px'>SHORT</td><td style='font-size:12px'>X<br>Y<br>LASTLINE</td></tr></table>");

        Assert.Equal(one, laid.Y["LASTLINE"] - laid.Y["BIGWORD"], 0.5);
        Assert.True(laid.Y["SHORT"] > laid.Y["LASTLINE"] + 5, "the middle cell's word is not above the tall cell's last line");
    }

    /// <summary>Baseline-aligned cells: the row's baseline is their first lines', on the large word's; a tall one does not move it.</summary>
    [Fact]
    public void BaselineCells_PutTheirFirstLinesOnIt()
    {
        var laid = Lay("<table><tr><td style='font-size:12px;vertical-align:baseline'>FIRSTCELL</td>" +
                       "<td style='font-size:12px;vertical-align:baseline'>TOPLINE<br>Y<br>Z</td></tr></table>");

        Assert.Equal(laid.Y["BIGWORD"], laid.Y["FIRSTCELL"], 0.5);
        Assert.Equal(laid.Y["BIGWORD"], laid.Y["TOPLINE"], 0.5);
    }

    /// <summary>A table's border, padding and margin are above its first row: the cell's content bottom stays on the baseline.</summary>
    [Fact]
    public void ATablesBorderAndPadding_AreAboveTheRow()
    {
        double plain = Above("<table><tr><td style='font-size:12px'>CELLWORD</td></tr></table>", "CELLWORD");
        double framed = Above("<table style='border:3px solid black;margin-top:10px' cellpadding='8'><tr><td style='font-size:12px'>CELLWORD</td></tr></table>", "CELLWORD");

        Assert.Equal(plain, framed, 0.5);
    }

    /// <summary>A list's baseline is its first item's first line.</summary>
    [Fact]
    public void AListsFirstItem_IsOnTheBaseline()
    {
        double above = Above("<ul style='margin:0'><li style='font-size:12px'>LISTWORD</li><li>two</li></ul>", "LISTWORD");

        Assert.Equal(0, above, 0.5);
    }

    /// <summary>A table under a padded box: the box's padding is above the table, the row's baseline unmoved by it.</summary>
    [Fact]
    public void ATableInsideAPaddedBox_KeepsItsBaseline()
    {
        double plain = Above("<table><tr><td style='font-size:12px'>CELLWORD</td></tr></table>", "CELLWORD");
        double boxed = Above("<div style='padding-top:12px'><table><tr><td style='font-size:12px'>CELLWORD</td></tr></table></div>", "CELLWORD");

        Assert.Equal(plain, boxed, 0.5);
    }

    /// <summary>
    /// An item opening with a floated box of blocks has no first baseline this measures (the in-flow
    /// content after the float would give CSS's): it stays at the top, reported — and the same item
    /// without the float, the control, is not reported.
    /// </summary>
    [Fact]
    public void AnItemOpeningWithAFloatedBox_IsReported()
    {
        const string Floated = "<div><div style='float:left;width:60px'><p>ONE</p><p>TWO</p></div><p>AFTER</p></div>";
        var laid = Lay(Floated);
        Assert.Contains(laid.Reports, r => r.Contains("align-items: baseline", StringComparison.Ordinal) && r.Contains("floated box", StringComparison.Ordinal));

        var control = Lay(Floated.Replace("float:left;", "", StringComparison.Ordinal));
        Assert.DoesNotContain(control.Reports, r => r.Contains("align-items: baseline", StringComparison.Ordinal));
    }
}
