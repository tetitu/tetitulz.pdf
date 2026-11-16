// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A run of floats with nothing beside them whose floats declare no width: each is as wide as its
/// content since batch 126 (CSS shrink-to-fit, measured by the layout that draws it), so the next
/// float starts where the previous one's text ends and a float that does not fit starts a new line.
/// Before, the auto-width floats shared the line's remaining width equally, reported. A float
/// nothing can measure still shares, and still reports (<c>FloatLayoutTests</c>).
/// </summary>
/// <remarks>
/// Read back from the words' positions; the converter's page is Letter with 28.8pt margins, so the
/// band is 554.4pt from x = 28.8. A measured float ends exactly where its text ends, so the next
/// float's text touches it and the extractor reads the two as one word; each float here ends in a
/// no-break space — part of its measured width, a drawn space the extractor breaks at — so the gap
/// between two floats is one space, about 3pt, and never more.
/// </remarks>
public class FloatRunMeasureTests
{
    private const double Margin = 28.8;
    private const double Band = 612 - 2 * Margin;
    private const double Space = 3.3;   // a 12pt Helvetica space, 0.278em

    /// <summary>
    /// Two left floats with no width: the second starts where the first's two words end — not at half
    /// the measure — and the first's width is both its words, not its longest one.
    /// </summary>
    [Fact]
    public void AutoWidthFloats_AreAsWideAsTheirContent()
    {
        var laid = Lay("<div><div style='float:left'>SHORT ONE&nbsp;</div><div style='float:left'>LONGERFLOATTEXT HERE</div></div>");

        double firstEnd = laid["ONE"].X + laid["ONE"].Width;
        Assert.True(Math.Abs(laid["SHORT"].Y - laid["LONGERFLOATTEXT"].Y) < 2.0, "the floats did not share a line");
        Assert.InRange(laid["LONGERFLOATTEXT"].X - firstEnd, 0, Space + 1);
        Assert.True(laid["LONGERFLOATTEXT"].X < Margin + Band / 4, $"the second float at x={laid["LONGERFLOATTEXT"].X:F1} was given a share of the line, not the first's width");
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// Right floats the same way, mirrored: the first is at the right edge, as wide as its text, and
    /// the second ends where the first begins.
    /// </summary>
    [Fact]
    public void AutoWidthRightFloats_LineUpInwardByTheirWidths()
    {
        var laid = Lay("<div><div style='float:right'>FIRSTRIGHT</div><div style='float:right'>SECOND&nbsp;</div></div>");

        var first = laid["FIRSTRIGHT"];
        var second = laid["SECOND"];
        Assert.True(Math.Abs(first.Y - second.Y) < 2.0, "the floats did not share a line");
        Assert.Equal(Margin + Band, first.X + first.Width, 0);
        Assert.InRange(first.X - (second.X + second.Width), 0, Space + 1);
        Assert.Empty(laid.Reports);
    }

    /// <summary>
    /// A measured float that does not fit beside the ones before it starts a new line at the float
    /// edge, as a declared width does: three floats of about a third of the band each are two lines.
    /// </summary>
    [Fact]
    public void AMeasuredFloatThatDoesNotFit_StartsANewLine()
    {
        string wide = string.Join(" ", Enumerable.Repeat("WIDE", 6)) + "&nbsp;";   // ~ 37% of the band
        var laid = Lay($"<div><div style='float:left'>AAA {wide}</div><div style='float:left'>BBB {wide}</div><div style='float:left'>CCC {wide}</div></div>");

        Assert.True(Math.Abs(laid["AAA"].Y - laid["BBB"].Y) < 2.0, "the first two did not share a line");
        Assert.True(laid["CCC"].Y < laid["AAA"].Y - 2.0, $"CCC at y={laid["CCC"].Y:F1} did not drop below AAA at y={laid["AAA"].Y:F1}");
        Assert.Equal(laid["AAA"].X, laid["CCC"].X, 1);
        Assert.Empty(laid.Reports);
    }

    /// <summary>A declared width and a measured one on one line: the declared float keeps its half, and the measured one starts right after it, as wide as its text.</summary>
    [Fact]
    public void ADeclaredAndAMeasuredFloat_ShareALineAtTheirOwnWidths()
    {
        var laid = Lay("<div><div style='float:left;width:50%'>HALF</div><div style='float:left'>MEASURED TEXT&nbsp;</div><div style='float:left'>NEXT</div></div>");

        Assert.Equal(Margin + Band / 2, laid["MEASURED"].X, 0);
        Assert.InRange(laid["NEXT"].X - (laid["TEXT"].X + laid["TEXT"].Width), 0, Space + 1);
        Assert.True(Math.Abs(laid["HALF"].Y - laid["NEXT"].Y) < 2.0, "the three did not share a line");
        Assert.Empty(laid.Reports);
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
                Assert.True(found.Text != null, $"'{word}' was not laid out at all: {string.Join("; ", Words.Select(w => $"{w.Text}@({w.X:F1},{w.Y:F1})"))}");
                return found;
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
            OnDiagnostic = d => { if (d.Kind == BuilderDiagnosticKind.ContentApproximated) lock (laid.Reports) laid.Reports.Add(d.Detail ?? ""); },
        });
        using var input = new MemoryStream(output.ToArray(), writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            laid.Words.Add((word.Text, word.X, word.Y, word.Width));
        return laid;
    }
}
