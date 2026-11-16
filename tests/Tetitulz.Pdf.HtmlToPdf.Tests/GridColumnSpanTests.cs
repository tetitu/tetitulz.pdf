// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// CSS Grid column spanning, which the mapper declined on a premise that was never true.
/// </summary>
/// <remarks>
/// <para><b>The decline said <c>TableCell</c> "has no column span at all".</b> It has carried
/// <c>GridSpan</c> the whole time, and the HTML <i>table</i> mapper in the same assembly has been
/// setting it from <c>colspan</c> — so the feature had somewhere to go, and an item asking for two
/// tracks was getting one for no reason. GAPS.md's §4b row repeated the claim. That is the
/// register's own rule earning its keep: a row is a hypothesis, the code is the authority, and
/// checking cost two greps.</para>
///
/// <para><b>Measured glyph positions, not model structure</b>, for the reason
/// <c>GridLayoutTests</c> records: a grid that fell through to block flow still produces a valid
/// PDF with every item stacked, so "the cell exists" and "the text is there" both pass on output
/// that is wrong. The only thing that separates a spanning item from a non-spanning one is where
/// the <i>next</i> item landed.</para>
///
/// <para><b>Spanning and placement arrive on one property and are not the same feature.</b>
/// <c>grid-column: span 2</c> only spans; <c>grid-column: 2 / 4</c> spans two tracks <i>and</i>
/// asks to start at line 2. Both halves are applied now — the placement half since
/// <c>GridPlacementTests</c> — so neither is reported; what still is, is a line this mapping
/// cannot resolve.</para>
/// </remarks>
public class GridColumnSpanTests
{
    /// <summary>
    /// <b>A spanning item pushes the next one past the tracks it occupies.</b>
    /// </summary>
    /// <remarks>
    /// The assertion that proves spanning happened, rather than that a property was read: in a
    /// four-track grid, B follows a two-track A at the <i>third</i> track. Compared against the
    /// same grid without the span, so the number it is measured against comes from the layout
    /// engine rather than from arithmetic here — a track width this test computed itself would be
    /// asserting its own model of the engine.
    /// </remarks>
    [Fact]
    public void ASpanningItemPushesTheNextItemPastTheTracksItOccupies()
    {
        double spanned = XOf("<div style='grid-column: span 2'>AAA</div><div>BBB</div>", "BBB");
        double plain = XOf("<div>AAA</div><div>BBB</div>", "BBB");

        Assert.True(spanned > plain + 1.0,
            $"BBB is at x={spanned:F1} after a two-track item and x={plain:F1} after a one-track " +
            "item; the span did not widen the first cell");
    }

    /// <summary>Every spelling CSS allows for the same span moves the next item the same way.</summary>
    /// <remarks>
    /// <para>A reader handling only <c>span 2</c> would give an author the wrong layout for the
    /// other spellings and report nothing, because each is valid CSS meaning the same thing.
    /// <c>1 / 3</c> is the one worth stating: line 1 to line 3 crosses <b>two</b> tracks, not
    /// three.</para>
    /// <para><b>It compares against the unspanned baseline as well as against the other
    /// spellings</b>, and the second assertion is not redundant. A mutation that stopped applying
    /// the span at all left the first one green — every spelling then agrees, because none of them
    /// does anything. A relative assertion cannot see a change applied to both of the things it
    /// relates, and this theory was exactly that until the mutation said so.</para>
    /// </remarks>
    [Theory]
    [InlineData("grid-column: span 2")]
    [InlineData("grid-column-end: span 2")]
    [InlineData("grid-column: 1 / 3")]
    [InlineData("grid-column: 1 / span 2")]
    public void EverySpellingOfATwoTrackSpanPlacesTheNextItemAlike(string style)
    {
        double viaThisSpelling = XOf($"<div style='{style}'>AAA</div><div>BBB</div>", "BBB");
        double viaSpanKeyword = XOf("<div style='grid-column: span 2'>AAA</div><div>BBB</div>", "BBB");
        double unspanned = XOf("<div>AAA</div><div>BBB</div>", "BBB");

        Assert.True(Math.Abs(viaThisSpelling - viaSpanKeyword) < 2.0,
            $"'{style}' put BBB at x={viaThisSpelling:F1} where 'span 2' puts it at " +
            $"x={viaSpanKeyword:F1}");

        Assert.True(viaThisSpelling > unspanned + 1.0,
            $"'{style}' left BBB at x={viaThisSpelling:F1}, where an unspanned item puts it at " +
            $"x={unspanned:F1} — the spellings agree because none of them spans");
    }

    /// <summary>A value that is not a span leaves the item at one track.</summary>
    /// <remarks>
    /// <c>auto</c> names nothing, and a named line needs <c>grid-template-columns</c> line names this
    /// mapping does not read. One track is what such an item gets; guessing would put the item in a
    /// column the author did not choose. (<c>3 / 1</c> left this list: CSS swaps a backwards pair, so
    /// it IS a two-track span at line 1, and <c>GridPlacementTests</c> pins that. <c>-1 / -3</c> left
    /// it too, once negative lines counted back from the explicit grid's end: see below.)
    /// </remarks>
    [Theory]
    [InlineData("grid-column: auto")]
    [InlineData("grid-column: header-start / header-end")]
    public void AValueThatIsNotASpanLeavesTheItemAtOneTrack(string style)
    {
        double withStyle = XOf($"<div style='{style}'>AAA</div><div>BBB</div>", "BBB");
        double plain = XOf("<div>AAA</div><div>BBB</div>", "BBB");

        Assert.True(Math.Abs(withStyle - plain) < 2.0,
            $"'{style}' moved BBB to x={withStyle:F1} from x={plain:F1}; it is not a span and " +
            "should have changed nothing");
    }

    /// <summary>
    /// A negative pair counts back from the explicit grid's end edge (§8.3): on four tracks
    /// <c>-1 / -3</c> is lines 5 and 3, swapped to <c>3 / 5</c> — a two-track span in the last two
    /// tracks, so BBB opens the next row where <c>3 / 5</c> puts it, not beside AAA.
    /// </summary>
    [Fact]
    public void ANegativePair_IsTheLinesCountedBackFromTheEnd()
    {
        double negative = XOf("<div style='grid-column: -1 / -3'>AAA</div><div>BBB</div>", "AAA");
        double positive = XOf("<div style='grid-column: 3 / 5'>AAA</div><div>BBB</div>", "AAA");
        double unplaced = XOf("<div>AAA</div><div>BBB</div>", "AAA");

        Assert.True(Math.Abs(negative - positive) < 2.0, $"'-1 / -3' put AAA at x={negative:F1} where '3 / 5' puts it at x={positive:F1}");
        Assert.True(negative > unplaced + 10, "'-1 / -3' left AAA in the first track, so the two agree for the wrong reason");
    }

    /// <summary>
    /// <b>Rows wrap on columns used, not on items placed.</b>
    /// </summary>
    /// <remarks>
    /// The bug a span introduces if the loop keeps counting items: a two-track item leaves the next
    /// item one column further along than its index says, so wrapping on the index puts every later
    /// row out of step with the tracks it was sized against. Three items in a three-track grid where
    /// the first spans two are two rows — and CCC belongs on the second, back at the left margin.
    /// </remarks>
    [Fact]
    public void ARowWrapsWhenItsTracksAreFullRatherThanWhenItsItemCountIs()
    {
        var words = Words(Grid(3,
            "<div style='grid-column: span 2'>AAA</div><div>BBB</div><div>CCC</div>"));

        var a = Word(words, "AAA");
        var b = Word(words, "BBB");
        var c = Word(words, "CCC");

        Assert.True(Math.Abs(a.Y - b.Y) < 2.0,
            $"AAA at y={a.Y:F1} and BBB at y={b.Y:F1} are not on the same row");
        Assert.True(c.Y < a.Y - 2.0,
            $"CCC at y={c.Y:F1} did not wrap below AAA at y={a.Y:F1}");
        Assert.True(Math.Abs(c.X - a.X) < 2.0,
            $"CCC at x={c.X:F1} did not return to the first track at x={a.X:F1}");
    }

    /// <summary>A span wider than the grid is clipped to the grid and, not fitting its row, starts the next.</summary>
    /// <remarks>
    /// CSS pushes an over-wide item onto the next row, and since placement runs §8.5 so does this;
    /// the span is clipped to the track count, because CSS would grow the implicit grid to nine
    /// columns and a table with a fixed template cannot. A row adding up to more than the track
    /// count is a shape the exporter sizes against the wrong widths. (This asserted the item stayed
    /// on its row while spanning was applied without placement; renamed with the behaviour.)
    /// </remarks>
    [Fact]
    public void ASpanWiderThanTheGridIsClippedToTheGridAndStartsTheNextRow()
    {
        var words = Words(Grid(3, "<div>AAA</div><div style='grid-column: span 9'>BBB</div>" +
                                  "<div>CCC</div>"));

        var a = Word(words, "AAA");
        var b = Word(words, "BBB");
        var c = Word(words, "CCC");

        Assert.True(b.Y < a.Y - 2.0, "the over-wide item did not start a new row");
        Assert.True(Math.Abs(b.X - a.X) < 2.0, $"BBB at x={b.X:F1} did not start its row at the first track, x={a.X:F1}");
        Assert.True(c.Y < b.Y - 2.0, "the item after the full-width span did not take the row after it");
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>A span-only item is fully honoured, so nothing is reported about it.</summary>
    /// <remarks>
    /// The acceptance control for the report below. A channel that fires on input that lost nothing
    /// is one a caller learns to ignore, and then it cannot report the case that matters — the rule
    /// <c>IgnoredParameterReporting.Deviates</c> states on the render side.
    /// </remarks>
    [Fact]
    public void ASpanOnlyItemReportsNoGridColumnApproximation()
    {
        var seen = new List<BuilderDiagnostic>();
        Convert(Grid(3, "<div style='grid-column: span 2'>AAA</div><div>BBB</div>"), seen.Add);

        Assert.DoesNotContain(seen, d => d.Detail.IndexOf("grid-column", StringComparison.Ordinal) >= 0);
    }

    /// <summary>An item that names a start line is placed there, so nothing about it is reported.</summary>
    /// <remarks>
    /// This asserted the opposite while the placement half was dropped. It is applied now
    /// (<c>GridPlacementTests</c>), so the report would fire on input that lost nothing — renamed
    /// rather than re-asserted, because the behaviour changed rather than the claim.
    /// </remarks>
    [Fact]
    public void AnItemNamingAStartLineIsPlacedAndNotReported()
    {
        var seen = new List<BuilderDiagnostic>();
        Convert(Grid(3, "<div style='grid-column: 2 / 4'>AAA</div><div>BBB</div>"), seen.Add);

        Assert.DoesNotContain(seen, d => d.Detail.IndexOf("grid-column", StringComparison.Ordinal) >= 0);
    }

    // ==================================================================

    /// <summary>A four-track grid containing <paramref name="items"/>.</summary>
    private static string Grid(int tracks, string items)
        => "<div style='display:grid;grid-template-columns:" +
           string.Join(" ", Enumerable.Repeat("1fr", tracks)) + "'>" + items + "</div>";

    /// <summary>Where a word landed in a four-track grid of the given items.</summary>
    private static double XOf(string items, string word) => Word(Words(Grid(4, items)), word).X;

    private static List<PdfTextWord> Words(string html)
    {
        byte[] pdf = Convert(html, null);
        Assert.NotEmpty(pdf);

        using var input = new MemoryStream(pdf, writable: false);
        return PdfOperations.ExtractStructuredText(input)
            .SelectMany(p => p.Lines)
            .SelectMany(l => l.Words)
            .ToList();
    }

    private static PdfTextWord Word(List<PdfTextWord> words, string text)
    {
        var hit = words.FirstOrDefault(w => w.Text.Contains(text, StringComparison.Ordinal));
        Assert.True(hit != null, $"'{text}' was not laid out at all");
        return hit!;
    }

    private static byte[] Convert(string html, Action<BuilderDiagnostic> onDiagnostic)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = onDiagnostic,
        });
        return output.ToArray();
    }
}
