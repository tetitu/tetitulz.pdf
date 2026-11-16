// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Builder.Export;
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A float whose content is not one paragraph — several paragraphs, a list, a table, a bordered box
/// — is a floated box since batch 125 (<c>BoxStart.Float</c>): laid out at the band's edge in its
/// width, the content after it flowing beside it and back to the full measure past its bottom, as a
/// floated paragraph has done since the exact float. Before, such a float fell back to the two-cell
/// band — the text beside it kept its column to the end of the block — and reported the approximation.
/// The band is left for a float whose width nothing says, which still reports.
/// </summary>
/// <remarks>
/// Read back from the words' positions in the produced PDF. The page is Letter with the converter's
/// default 0.4in (28.8pt) margins, so the band is 554.4pt wide from x = 28.8; a pixel is 0.75pt. The
/// first word of each body line is its start: a line beside the float starts clear of it, a line
/// below it at the margin, and only both together tell a float from a band (<c>FloatReflowTests</c>).
/// </remarks>
public class FloatedBlockContentTests
{
    private const double Margin = 28.8;
    private const double Band = 612 - 2 * Margin;
    private static readonly string Body = string.Join(" ", Enumerable.Repeat("BODY", 90));

    /// <summary>
    /// Two paragraphs in a 150px (112.5pt) left float: the body's first line is beside them, clear of
    /// the float, a later line is back at the margin, and nothing is reported as approximated.
    /// </summary>
    [Fact]
    public void AFloatOfSeveralParagraphs_IsAFloatedBox_AndTheTextReflowsPastIt()
    {
        var laid = Lay($"<div style='float:left;width:150px'><div>SIDEONE</div><div>SIDETWO</div></div><div>{Body}</div>");

        double[] lefts = laid.LineStarts("BODY");
        Assert.True(lefts.Length >= 6, $"expected several body lines, got {lefts.Length}: {laid.Dump()}");
        Assert.True(lefts.Any(x => x > Margin + 112.5 - 2), $"no body line starts beside the float: {laid.Dump()}");
        Assert.True(lefts.Any(x => x < Margin + 2), $"no body line returns to the margin: {laid.Dump()}");
        Assert.True(Math.Abs(laid["SIDEONE"].Y - laid.Words.Where(w => w.Text == "BODY").Max(w => w.Y)) < 2.0, $"the body's first line is not beside the float: {laid.Dump()}");
        Assert.True(laid.Words.Any(w => w.Text == "SIDETWO"), "the float's second paragraph was lost");
        Assert.DoesNotContain(laid.Reports, r => r.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A table floated right with a width sits at the band's right edge, every body line starts at the
    /// margin, the first is shorter than the lines below the table, and nothing is reported.
    /// </summary>
    [Fact]
    public void AFloatedTable_SitsAtTheRightEdge_AndShortensTheLinesBesideIt()
    {
        var laid = Lay($"<table style='float:right;width:150px'><tr><td>CELL</td></tr></table><p>{Body}</p>");

        Assert.True(laid["CELL"].X > Margin + Band - 112.5 - 2, $"the table at x={laid["CELL"].X:F1} is not at the right edge");
        var lines = laid.Lines("BODY");
        Assert.True(lines.Count >= 6);
        Assert.All(lines, l => Assert.True(l.Left < Margin + 2, $"a right float moved a line's start to {l.Left:F1}"));
        Assert.True(lines[0].Right < lines.Skip(2).Max(l => l.Right) - 60, "the first line, beside the table, was not shortened");
        Assert.DoesNotContain(laid.Reports, r => r.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A list floated left with no width shrinks to its content: the body's first line starts just past
    /// the widest item, a later line at the margin, nothing reported.
    /// </summary>
    [Fact]
    public void AFloatedListWithNoWidth_ShrinksToItsItems()
    {
        var laid = Lay($"<ul style='float:left'><li>ITEMONE</li><li>ITEMTWOLONGER</li></ul><p>{Body}</p>");

        // The body's first line is beside the first item and starts where the longer second item
        // ends — past the first item's end by more than the two items' difference, and well short of
        // a wide float; a later line is back at the margin. (The second item and the body word beside
        // it touch, as CSS has them touch, and extract as one word.)
        var lines = laid.Lines("BODY");
        Assert.True(lines.Count >= 4, $"expected several body lines, got {lines.Count}: {laid.Dump()}");
        var itemOne = laid.Words.First(w => w.Text.EndsWith("ITEMONE", StringComparison.Ordinal));
        Assert.True(lines[0].Left > itemOne.X + itemOne.Width + 20 && lines[0].Left < Margin + 150,
            $"the first body line at x={lines[0].Left:F1} is not just past the list's widest item, ITEMONE ending at {itemOne.X + itemOne.Width:F1}: {laid.Dump()}");
        Assert.True(lines.Any(l => l.Left < Margin + 2), $"no body line returns to the margin: {laid.Dump()}");
        Assert.DoesNotContain(laid.Reports, r => r.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A bordered, padded float of two paragraphs with <c>width: 40%</c> keeps that width once: its
    /// background rectangle is 40% of the band wide (187.2pt), not 40% of a 40% float, and the body
    /// starts clear of it.
    /// </summary>
    [Fact]
    public void ABoxedFloat_KeepsItsStatedWidthOnce()
    {
        string html = $"<div style='float:left;width:40%;border:1px solid #000;padding:4px;background:#eeeeee'><p>SIDEONE</p><p>SIDETWO</p></div><p>{Body}</p>";
        string content = DecodeStreams(ToPdf(html));
        var widths = Regex.Matches(content, @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\n")
            .Select(m => double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)).ToList();
        Assert.True(widths.Any(w => Math.Abs(w - 0.4 * Band) < 1.0), $"no rectangle 40% of the band wide; the rectangles are {string.Join(", ", widths.Select(w => w.ToString("F1", CultureInfo.InvariantCulture)))}");
        Assert.DoesNotContain(widths, w => Math.Abs(w - 0.16 * Band) < 1.0);

        var laid = Lay(html);
        Assert.True(laid.LineStarts("BODY").Any(x => x > Margin + 0.4 * Band - 2), $"no body line starts beside the float: {laid.Dump()}");
    }

    /// <summary>
    /// A declared width is the float's border box and its margins lie outside it (CSS 2.1 §9.5.1): a
    /// 150px float with a 20px right margin fills 112.5pt and the body starts 15pt past its rectangle —
    /// which is why the element's own box carries the float; a box wrapped round it would put the
    /// margin inside the 112.5pt and draw the fill 15pt narrower.
    /// </summary>
    [Fact]
    public void ADeclaredWidthsMargins_LieOutsideIt()
    {
        string html = $"<div style='float:left;width:150px;margin-right:20px;background:#eeeeee'><div>SIDEONE</div><div>SIDETWO</div></div><div>{Body}</div>";
        string content = DecodeStreams(ToPdf(html));
        var rect = Regex.Matches(content, @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\n")
            .Select(m => (X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), W: double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)))
            .Single(r => r.W < 200);
        var laid = Lay(html);

        Assert.Equal(112.5, rect.W, 1);
        Assert.Equal(Margin, rect.X, 1);
        Assert.Equal(Margin + 112.5 + 15, laid.Lines("BODY")[0].Left, 1);
    }

    /// <summary>
    /// A measured float's margins lie outside its width: a padded, bordered float with no width and a
    /// 20px right margin is as wide as its text and padding — its background rectangle — and the body
    /// starts 15pt (20px) past the rectangle's edge. Counting the margins into the width would widen
    /// the box by them.
    /// </summary>
    [Fact]
    public void AMeasuredFloatsMargins_LieOutsideItsWidth()
    {
        string html = $"<div style='float:left;margin-right:20px;padding:4px;background:#eeeeee'><div>SIDEONE</div><div>SIDETWO</div></div><div>{Body}</div>";
        string content = DecodeStreams(ToPdf(html));
        var rect = Regex.Matches(content, @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\n")
            .Select(m => (X: double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), W: double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)))
            .Single(r => r.W < 200);
        var laid = Lay(html);

        double text = laid["SIDETWO"].Width;
        Assert.InRange(rect.W, text + 6 - 0.5, text + 6 + 1.0);   // 4px of padding a side is 3pt; the measure has half a point of slack
        Assert.Equal(rect.X + rect.W + 15, laid.Lines("BODY")[0].Left, 1);
        Assert.DoesNotContain(laid.Reports, r => r.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A float whose width nothing says — no width, and a table nested past the layout's cap — still
    /// takes the band and says so: the acceptance control for the fallback.
    /// </summary>
    [Fact]
    public void AFloatWhoseWidthCannotBeKnown_StillTakesTheBand_Reported()
    {
        var laid = Lay($"<div style='float:left'><p>LOGO</p>{ManagedTestSupport.TablesPastTheNestingCap("")}</div><p>{Body}</p>");

        Assert.Contains(laid.Reports, r => r.StartsWith("float:", StringComparison.Ordinal) && r.Contains("fixed column", StringComparison.Ordinal));
        Assert.True(laid.Words.Any(w => w.Text == "LOGO"), "the float's content was lost");
    }

    /// <summary>
    /// Inside a table cell: a floated box of two paragraphs sits at the cell's edge, the cell's text
    /// flows beside it and back to the cell's full width past it.
    /// </summary>
    [Fact]
    public void AFloatedBoxInACell_LetsTheCellsTextFlowBesideIt()
    {
        var laid = Lay($"<table style='width:400px'><tr><td><div style='float:left;width:100px'><p>SIDEONE</p><p>SIDETWO</p></div><p>{Body}</p></td></tr></table>");

        double side = laid["SIDEONE"].X;
        double[] lefts = laid.LineStarts("BODY");
        Assert.True(lefts.Length >= 6, $"expected several body lines, got {lefts.Length}");
        Assert.Contains(lefts, x => x > side + 75 - 2);
        Assert.Contains(lefts, x => Math.Abs(x - side) < 2);
        Assert.DoesNotContain(laid.Reports, r => r.Contains("float", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// <c>clear</c> on an element whose content is several blocks — a bordered box — is carried by the
    /// box (<c>BoxStart.ClearFloats</c>): it starts below a float taller than the text before it, at
    /// the margin; so does a box after an empty clearing element (the clearfix), whose clear was left
    /// pending for the next block. Without the clear the same box sits beside the float (the
    /// control). Before batch 125 a box could not carry the clear, and it was dropped.
    /// </summary>
    [Theory]
    [InlineData("<div style='clear:both;background:#eeeeee'><p>BELOW</p><p>TWO</p></div>")]
    [InlineData("<div style='clear:both'></div><div style='background:#eeeeee'><p>BELOW</p><p>TWO</p></div>")]
    [InlineData("<div style='clear:both'></div><div style='display:flex;gap:20px;background:#eeeeee'><div>BELOW</div><div>TWO</div></div>")]
    public void ClearOnABoxOfBlocks_StartsItBelowTheFloat(string clearing)
    {
        // Unkerned: kerned, four TALLs fill the 120pt float and the last touches ADDR, which reads as one word.
        string tall = string.Join(" ", Enumerable.Repeat("TALL", 40));
        string html = $"<div style='float:left;width:120pt;font-kerning:none'>{tall}</div><div>ADDR</div>{clearing}";
        var cleared = Lay(html);
        var beside = Lay($"<div style='float:left;width:120pt;font-kerning:none'>{tall}</div><div>ADDR</div><div style='border:1px solid #000'><p>BELOW</p><p>TWO</p></div>");

        double floatBottom = cleared.Words.Where(w => w.Text == "TALL").Min(w => w.Y);
        Assert.True(cleared["BELOW"].Y < floatBottom, $"BELOW at y={cleared["BELOW"].Y:F1} is not below the float ending at y={floatBottom:F1}");
        Assert.True(cleared["BELOW"].X < Margin + 10, $"BELOW at x={cleared["BELOW"].X:F1} is not back at the margin");
        // The box's own frame starts below the float too — its fill, the band wide — not beside it
        // with only its first paragraph dropped below.
        var fill = Regex.Matches(DecodeStreams(ToPdf(html)), @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\n")
            .Select(m => (Y: double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), W: double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), H: double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)))
            .Single(r => r.W > 500);
        Assert.True(fill.Y + fill.H < floatBottom + 1, $"the box's fill starts at y={fill.Y + fill.H:F1}, beside the float ending at y={floatBottom:F1}");
        Assert.True(beside["BELOW"].X > Margin + 100, $"without the clear BELOW at x={beside["BELOW"].X:F1} should be beside the float: {beside.Dump()}");
    }

    /// <summary>
    /// A floated box after an empty clearing element carries the pending clear as the first float of
    /// its run: it starts below the float before it — a real float, with ADDR beside it — at the
    /// margin, where without the clearfix it would sit inward of that float on its line.
    /// </summary>
    [Fact]
    public void AFloatedBoxAfterAClearfix_StartsBelowTheFloatBeforeIt()
    {
        // Unkerned: kerned, four TALLs fill the 120pt float and the last touches ADDR, which reads as one word.
        string tall = string.Join(" ", Enumerable.Repeat("TALL", 40));
        const string Box = "<div style='float:left;width:100px'><div>SIDEONE</div><div>SIDETWO</div></div>";
        var cleared = Lay($"<div style='float:left;width:120pt;font-kerning:none'>{tall}</div><div>ADDR</div><div style='clear:both'></div>{Box}<div>{Body}</div>");
        var beside = Lay($"<div style='float:left;width:120pt;font-kerning:none'>{tall}</div><div>ADDR</div>{Box}<div>{Body}</div>");

        double floatBottom = cleared.Words.Where(w => w.Text == "TALL").Min(w => w.Y);
        Assert.True(cleared["SIDEONE"].Y < floatBottom, $"the box at y={cleared["SIDEONE"].Y:F1} is not below the float ending at y={floatBottom:F1}: {cleared.Dump()}");
        Assert.Equal(Margin, cleared["SIDEONE"].X, 1);
        Assert.True(beside["SIDEONE"].X > Margin + 100, $"without the clearfix the box at x={beside["SIDEONE"].X:F1} should sit beside the float: {beside.Dump()}");
    }

    // ------------------------------------------------------------------

    private sealed class Laid
    {
        public readonly List<(string Text, double X, double Y, double Width)> Words = new();
        public readonly List<string> Reports = new();

        public (string Text, double X, double Y, double Width) this[string word]
        {
            get
            {
                var found = Words.FirstOrDefault(w => w.Text == word);
                Assert.True(found.Text != null, $"'{word}' was not laid out at all");
                return found;
            }
        }

        /// <summary>The body's lines, top first: each line's first word's x and its last word's right edge.</summary>
        public List<(double Left, double Right)> Lines(string word)
            => Words.Where(w => w.Text == word).GroupBy(w => Math.Round(w.Y, 1)).OrderByDescending(g => g.Key)
                    .Select(g => (g.Min(w => w.X), g.Max(w => w.X + w.Width))).ToList();

        public double[] LineStarts(string word) => Lines(word).Select(l => l.Left).ToArray();

        /// <summary>Every word with its position, for a failure message.</summary>
        public string Dump()
            => string.Join("; ", Words.OrderByDescending(w => w.Y).ThenBy(w => w.X)
                                      .Select(w => $"{w.Text}@({w.X:F1},{w.Y:F1})"));
    }

    private static Laid Lay(string html)
    {
        var laid = new Laid();
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d => { if (d.Kind == BuilderDiagnosticKind.ContentApproximated) lock (laid.Reports) laid.Reports.Add(d.Detail ?? ""); },
        });
        using var input = new MemoryStream(output.ToArray(), writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            laid.Words.Add((word.Text, word.X, word.Y, word.Width));
        return laid;
    }
}
