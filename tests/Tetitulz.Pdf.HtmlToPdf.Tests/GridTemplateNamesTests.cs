// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// Line names in a grid template (CSS Grid §7.2.1, batch 102): <c>[full-start] 1fr [mid] 1fr [full-end]</c>
/// names three lines, and an item's <c>grid-column: full-start / full-end</c> or <c>mid</c> resolves
/// against them — on rows too, and through <c>repeat()</c>, where <c>name N</c> is the Nth line so
/// named. Before batch 102 a name group was read as a track of its own (two named tracks laid out
/// as five) and the names were unresolvable. And a function track — <c>minmax()</c>,
/// <c>fit-content()</c> — is one token sized from its arguments on both axes, where it was split at
/// its space on columns and left the row template unread.
/// </summary>
public class GridTemplateNamesTests
{
    private const string Named = "display:grid;grid-template-columns:[full-start] 1fr [mid] 1fr [full-end]";

    /// <summary>Two named tracks are two tracks: the third item wraps under the first, and nothing is reported.</summary>
    [Fact]
    public void NameGroups_AreNotTracks()
    {
        var laid = Lay($"<div style='{Named}'><div>AAA</div><div>BBB</div><div>CCC</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "AAA and BBB do not share the first row");
        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0, "CCC did not wrap under AAA: the name groups were counted as tracks");
        Assert.Equal(laid["AAA"].X, laid["CCC"].X, 1);
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// An item spanning <c>full-start / full-end</c> takes both tracks (its rule is the full width),
    /// and one at <c>mid</c> starts in the second column of the next row. The sparse cursor does not
    /// step back (§8.5), so the automatic third item follows it into a third row, first column.
    /// </summary>
    [Fact]
    public void NamedLines_ResolvePlacements()
    {
        var laid = Lay($"<div style='border-top:1px solid #000'>REF</div><div style='{Named}'>" +
                       "<div style='grid-column:full-start / full-end;border-top:1px solid #000'>AAA</div><div style='grid-column:mid'>BBB</div><div>CCC</div></div>");

        Assert.Equal(2, laid.Rules.Count);
        Assert.Equal(laid.Rules.Max(), laid.Rules.Min(), 0);
        Assert.True(laid["BBB"].Y < laid["AAA"].Y - 2.0 && laid["BBB"].X > laid["AAA"].X + 50, "BBB is not in the second column of the second row");
        Assert.True(laid["CCC"].Y < laid["BBB"].Y - 2.0 && Math.Abs(laid["CCC"].X - laid["AAA"].X) < 1.0, "CCC did not follow the cursor into the third row's first column");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// <c>repeat(2, [col] 1fr)</c> names the line before each track: <c>col 2</c> is the second
    /// column, so AAA sits right of the first column's x, in the first row; the automatic BBB follows
    /// the sparse cursor into the next row's first column. With <c>dense</c> it fills the hole beside AAA.
    /// </summary>
    [Fact]
    public void NamesInsideARepeat_CountTheirOccurrences()
    {
        var sparse = Lay("<div style='display:grid;grid-template-columns:repeat(2, [col] 1fr)'><div style='grid-column:col 2'>AAA</div><div>BBB</div></div>");
        var dense = Lay("<div style='display:grid;grid-template-columns:repeat(2, [col] 1fr);grid-auto-flow:dense'><div style='grid-column:col 2'>AAA</div><div>BBB</div></div>");

        Assert.True(sparse["AAA"].X > sparse["BBB"].X + 50, "AAA (col 2) is not in the second column");
        Assert.True(sparse["BBB"].Y < sparse["AAA"].Y - 2.0, "the sparse cursor did not carry BBB to the next row");
        Assert.True(Math.Abs(dense["AAA"].Y - dense["BBB"].Y) < 2.0 && dense["AAA"].X > dense["BBB"].X, "dense did not fill the hole beside AAA");
        Assert.Empty(sparse.Reports);
    }

    /// <summary>
    /// A name group may hold several names for one line (<c>[full-start left]</c>): both resolve, and
    /// the group is still no track — two tracks, so the automatic third item wraps under the first.
    /// Split at its space the group would be a stray <c>left]</c> track and a three-column grid, with
    /// the third item on the first row.
    /// </summary>
    [Fact]
    public void ANameGroup_MayHoldSeveralNames()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:[full-start left] 1fr [right] 1fr [full-end]'>" +
                       "<div style='grid-column:left'>AAA</div><div style='grid-column:right'>BBB</div><div>CCC</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "AAA (left) and BBB (right) do not share the first row");
        Assert.True(laid["BBB"].X > laid["AAA"].X + 50, "BBB (line 'right') is not in the second column");
        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0 && Math.Abs(laid["CCC"].X - laid["AAA"].X) < 1.0, "CCC did not wrap under AAA: the two-name group was split into a third track");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// A <c>minmax()</c> column is its max, not its min: <c>minmax(100px, 300px) minmax(300px, 100px)</c>
    /// is a wide first column and a narrow second, so the second item starts past three fifths of the
    /// container's width (its REF rule). Taking the min would put it before two fifths.
    /// </summary>
    [Fact]
    public void AMinmaxColumn_IsItsMax()
    {
        var laid = Lay("<div style='border-top:1px solid #000'>REF</div><div style='display:grid;grid-template-columns:minmax(100px, 300px) minmax(300px, 100px)'>" +
                       "<div>AAA</div><div>BBB</div></div>");

        double width = Assert.Single(laid.Rules);
        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "AAA and BBB do not share the first row");
        Assert.True(laid["BBB"].X - laid["AAA"].X > 0.6 * width, $"BBB starts {laid["BBB"].X - laid["AAA"].X:F0}pt in of {width:F0}pt: the first minmax() column took its min");
        Assert.Empty(laid.Reports);
    }

    /// <summary>Row templates name lines too: <c>grid-row: bottom</c> puts the item in the second row.</summary>
    [Fact]
    public void RowLineNames_ResolveTheRow()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:[top] auto [bottom] auto'>" +
                       "<div style='grid-row:bottom'>AAA</div><div>BBB</div><div>CCC</div></div>");

        Assert.True(laid["AAA"].Y < laid["BBB"].Y - 2.0, "AAA (row 'bottom') is not below BBB");
        Assert.True(Math.Abs(laid["BBB"].Y - laid["CCC"].Y) < 2.0, "BBB and CCC do not fill the first row");
        Assert.Empty(laid.Reports);
    }

    /// <summary>A template's explicit name wins over an area's implicit <c>name-start</c>: the two agree here, and an explicit <c>sidebar-start</c> at line 2 is where the item goes.</summary>
    [Fact]
    public void AnExplicitName_WinsOverAnAreasImplicitOne()
    {
        var laid = Lay("<div style=\"display:grid;grid-template-columns:1fr [sidebar-start] 1fr;grid-template-areas:'sidebar main'\">" +
                       "<div style='grid-column:sidebar-start'>AAA</div><div>BBB</div></div>");

        // The area 'sidebar' would start at line 1; the explicit [sidebar-start] names line 2.
        Assert.True(laid["AAA"].X > laid["BBB"].X, "AAA did not go to the explicit sidebar-start line");
    }

    /// <summary>
    /// A <c>minmax()</c> column is one track at its max: two tracks, the third item under the first —
    /// and <c>fit-content()</c> one track too (three of them hold three items on one row and wrap the
    /// fourth). The width a fixed track ends up with beside <c>fr</c> tracks is the standing
    /// approximation (GAPS §4b) and is not asserted here.
    /// </summary>
    [Fact]
    public void FunctionColumns_AreOneTrackEach()
    {
        var two = Lay("<div style='display:grid;grid-template-columns:minmax(100px, 1fr) 1fr'><div>AAA</div><div>BBB</div><div>CCC</div></div>");
        var fit = Lay("<div style='display:grid;grid-template-columns:fit-content(200px) fit-content(200px) 1fr'><div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div></div>");

        Assert.True(Math.Abs(two["AAA"].Y - two["BBB"].Y) < 2.0, "AAA and BBB do not share the first row");
        Assert.True(two["CCC"].Y < two["AAA"].Y - 2.0 && Math.Abs(two["CCC"].X - two["AAA"].X) < 1.0, "CCC did not wrap under AAA: minmax was split into two tracks");
        Assert.Empty(two.Reports);
        Assert.True(Math.Abs(fit["AAA"].Y - fit["CCC"].Y) < 2.0, "three fit-content/fr tracks did not hold three items on one row");
        Assert.True(fit["DDD"].Y < fit["AAA"].Y - 2.0 && Math.Abs(fit["DDD"].X - fit["AAA"].X) < 1.0, "the fourth item did not wrap under the first");
        Assert.Empty(fit.Reports);
    }

    /// <summary>A <c>minmax()</c> row is at least its min: 60px (45pt) under a one-line item, where the auto row is the line.</summary>
    [Fact]
    public void AMinmaxRow_IsAtLeastItsMin()
    {
        var floor = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:minmax(60px, auto) auto'><div>AAA</div><div>BBB</div><div>CCC</div></div>");
        var auto = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:auto auto'><div>AAA</div><div>BBB</div><div>CCC</div></div>");

        Assert.True(auto["AAA"].Y - auto["CCC"].Y < 40, "the control's auto row is not a single line");
        Assert.True(floor["AAA"].Y - floor["CCC"].Y >= 44, $"the minmax row is {floor["AAA"].Y - floor["CCC"].Y:F1}pt tall, under its 45pt floor");
        Assert.DoesNotContain(floor.Reports, r => r.Contains("not applied", StringComparison.Ordinal));
    }

    /// <summary>
    /// A function this engine does not size stays reported, naming the function: <c>calc()</c> on a
    /// column leaves it auto; <c>clamp()</c> with a percentage on a row — of a height the flow does not
    /// know — leaves the row template unread (<c>clamp()</c> of lengths and of a column's percentages
    /// is sized since batch 128: <c>GridMathTracksTests</c>).
    /// </summary>
    [Fact]
    public void AnUnknownFunction_IsReported()
    {
        var col = Lay("<div style='display:grid;grid-template-columns:calc(10px + 5%) 1fr'><div>AAA</div><div>BBB</div></div>");
        var row = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;grid-template-rows:clamp(10px, 5%, 100px)'><div>AAA</div><div>BBB</div></div>");

        Assert.Contains(col.Reports, r => r.Contains("calc(10px + 5%) is not a function this engine sizes", StringComparison.Ordinal));
        Assert.Contains(row.Reports, r => r.Contains("grid-template-rows", StringComparison.Ordinal) && r.Contains("clamp(", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------

    private sealed class Laid
    {
        public readonly Dictionary<string, (double X, double Y)> Words = new(StringComparer.Ordinal);
        public readonly List<string> Reports = new();
        public readonly List<double> Rules = new();
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
        byte[] pdf = output.ToArray();
        using var input = new MemoryStream(pdf, writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            if (!laid.Words.ContainsKey(word.Text)) laid.Words[word.Text] = (word.X, word.Y);
        foreach (var rule in ManagedTestSupport.StrokedSegments(ManagedTestSupport.DecodeStreams(pdf)).Where(s => s.IsHorizontal))
            laid.Rules.Add(Math.Abs(rule.X1 - rule.X0));
        return laid;
    }
}
