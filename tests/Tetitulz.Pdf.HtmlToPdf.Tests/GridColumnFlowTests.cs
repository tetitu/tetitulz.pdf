// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>grid-auto-flow: column</c> (CSS Grid §8.5, batch 98): the auto-placement cursor runs down each
/// column of the explicit grid before the next, so with two explicit rows four items are A over B in
/// the first column and C over D in the second. It needs the explicit grid's rows; with one or none
/// CSS would add implicit columns, which a table of the template's tracks cannot hold, so the items
/// take row flow and the container says so — as it says so when the explicit columns fill up.
/// Before batch 98 column flow was reported and the items placed in row flow.
/// </summary>
public class GridColumnFlowTests
{
    private const string TwoByTwo = "display:grid;grid-template-columns:1fr 1fr;grid-template-rows:auto auto;grid-auto-flow:column";
    private const string Items = "<div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div>";

    [Fact]
    public void TwoRows_FillTheFirstColumnBeforeTheSecond()
    {
        var laid = Lay($"<div style='{TwoByTwo}'>{Items}</div>");
        var rowFlow = Lay($"<div style='{TwoByTwo.Replace("grid-auto-flow:column", "")}'>{Items}</div>");

        Assert.True(laid["BBB"].Y < laid["AAA"].Y - 2.0, "BBB is not under AAA");
        Assert.Equal(laid["AAA"].X, laid["BBB"].X, 1);
        Assert.True(Math.Abs(laid["AAA"].Y - laid["CCC"].Y) < 2.0, "CCC does not share AAA's row");
        Assert.True(laid["CCC"].X > laid["AAA"].X, "CCC is not in the second column");
        Assert.True(Math.Abs(laid["BBB"].Y - laid["DDD"].Y) < 2.0, "DDD does not share BBB's row");
        Assert.Empty(laid.Reports);

        Assert.True(Math.Abs(rowFlow["AAA"].Y - rowFlow["BBB"].Y) < 2.0, "the control's row flow put BBB under AAA");
    }

    /// <summary>The rows may come from <c>grid-template-areas</c> too: two template rows are two explicit rows.</summary>
    [Fact]
    public void TheExplicitRows_MayComeFromTheAreasTemplate()
    {
        var laid = Lay("<div style=\"display:grid;grid-template-columns:1fr 1fr;grid-template-areas:'a b' 'c d';grid-auto-flow:column\">" + Items + "</div>");

        Assert.True(laid["BBB"].Y < laid["AAA"].Y - 2.0, "BBB is not under AAA");
        Assert.True(Math.Abs(laid["AAA"].Y - laid["CCC"].Y) < 2.0, "CCC does not share AAA's row");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// A row span is kept whole in its column: in two rows, B spanning two does not fit under A and
    /// takes the second column whole. The cursor does not go back (not <c>dense</c>), so C takes a
    /// third, implicit column (batch 112) at the top — before batch 112 it fell to a row below the
    /// grid, reported.
    /// </summary>
    [Fact]
    public void ARowSpan_StaysInItsColumn_AndTheNextItemStartsAnImplicitColumn()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:auto auto;grid-auto-flow:column'>" +
                       "<div>AAA</div><div style='grid-row:span 2'>BBB</div><div>CCC</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "BBB did not start at the top of the second column");
        Assert.True(laid["BBB"].X > laid["AAA"].X, "BBB is not in the second column");
        Assert.True(Math.Abs(laid["CCC"].Y - laid["AAA"].Y) < 2.0 && laid["CCC"].X > laid["BBB"].X + 10, "CCC did not start a third column at the top");
        Assert.Empty(laid.Reports);
    }

    /// <summary>An item definite on its column takes that column's first free row, before the cursor runs; the cursor fills round it.</summary>
    [Fact]
    public void ADefiniteColumn_IsPlacedFirst_AndTheCursorFillsRoundIt()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:auto auto;grid-auto-flow:column'>" +
                       "<div>AAA</div><div style='grid-column:1'>BBB</div><div>CCC</div></div>");

        // B is definite in column 1 and placed first, at row 0; A then takes (1,0) under it, C the second column's top.
        Assert.True(laid["AAA"].Y < laid["BBB"].Y - 2.0, "AAA did not fall under the definite BBB");
        Assert.Equal(laid["AAA"].X, laid["BBB"].X, 1);
        Assert.True(Math.Abs(laid["CCC"].Y - laid["BBB"].Y) < 2.0 && laid["CCC"].X > laid["BBB"].X, "CCC is not at the top of the second column");
    }

    /// <summary>An item definite on its row moves the cursor to that row, to the next column when the row is behind it.</summary>
    [Fact]
    public void ADefiniteRow_MovesTheCursor()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:auto auto auto;grid-auto-flow:column'>" +
                       "<div>AAA</div><div style='grid-row:3'>BBB</div><div>CCC</div></div>");

        // A (0,0); B jumps to row 3 of the first column (2,0); C continues from there: the first column is spent, so (0,1).
        Assert.True(laid["BBB"].Y < laid["AAA"].Y - 2.0, "BBB is not below AAA");
        Assert.Equal(laid["AAA"].X, laid["BBB"].X, 1);
        Assert.True(Math.Abs(laid["CCC"].Y - laid["AAA"].Y) < 2.0 && laid["CCC"].X > laid["AAA"].X, "CCC did not start the second column");
    }

    /// <summary><c>dense</c> sends the cursor back to the start: the hole a definite row left in the first column is filled.</summary>
    [Fact]
    public void Dense_FillsTheHoleADefiniteRowLeft()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:auto auto auto;grid-auto-flow:column dense'>" +
                       "<div>AAA</div><div style='grid-row:3'>BBB</div><div>CCC</div></div>");

        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0 && laid["CCC"].Y > laid["BBB"].Y + 2.0, "CCC did not fill the hole between AAA and BBB");
        Assert.Equal(laid["AAA"].X, laid["CCC"].X, 1);
    }

    /// <summary>
    /// Six items in a 2×2 explicit grid: the fifth and sixth take a third, implicit column (batch
    /// 112), the fifth at the top beside CCC and the sixth under it, unreported. Before batch 112 they
    /// fell to a row below the grid and the container reported the implicit column it could not add.
    /// </summary>
    [Fact]
    public void ItemsPastTheExplicitColumns_TakeAnImplicitColumn()
    {
        var laid = Lay($"<div style='{TwoByTwo}'>{Items}<div>EEE</div><div>FFF</div></div>");

        Assert.True(Math.Abs(laid["EEE"].Y - laid["AAA"].Y) < 2.0 && laid["EEE"].X > laid["CCC"].X + 10, "EEE did not start a third column at the top");
        Assert.True(Math.Abs(laid["FFF"].Y - laid["BBB"].Y) < 2.0 && Math.Abs(laid["FFF"].X - laid["EEE"].X) < 1.0, "FFF is not under EEE");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// A row template this cannot read leaves the row count unknown: row flow — densely, when asked —
    /// and the report says the template was not read, and whether the packing was dense. (One row or
    /// none is column flow with one implicit row since batch 112: <c>GridImplicitTracksTests</c>.)
    /// </summary>
    [Theory]
    [InlineData("column", ".")]
    [InlineData("column dense", ", densely.")]
    public void AnUnreadRowTemplate_RowFlow_Reported(string flow, string tail)
    {
        var laid = Lay($"<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:clamp(10px, 5%, 100px) auto;grid-auto-flow:{flow}'>{Items}</div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "the items were not placed in row flow");
        string report = Assert.Single(laid.Reports, r => r.StartsWith("grid-auto-flow", StringComparison.Ordinal));
        Assert.Contains("was not read", report, StringComparison.Ordinal);
        Assert.EndsWith("in row flow" + tail, report, StringComparison.Ordinal);
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
