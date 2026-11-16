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
using Xunit;
using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A grid container's own box — its margins, border, padding and background — is drawn round its
/// tracks since batch 130, as a flex container's is, and the tracks share the room inside it: a
/// bordered grid card drew no border and ran its tracks over its padding, and its stated width was
/// not read.
/// </summary>
/// <remarks>Letter page, the converter's 28.8pt margins; a pixel is 0.75pt. Read back from the words' positions and the box's fill rectangle.</remarks>
public class GridContainerBoxTests
{
    private const double Margin = 28.8;
    private const double Band = 612 - 2 * Margin;

    /// <summary>
    /// The box is drawn — one fill as wide as the band, enclosing the items' line with the padding
    /// and the border — and the tracks are inside it: A at the left padding edge, 30pt in from the
    /// border, B at the second of two equal tracks of the room the padding leaves.
    /// </summary>
    [Fact]
    public void AGridContainersBox_IsDrawn_AndItsTracksAreInsideIt()
    {
        string html = "<div style='display:grid;grid-template-columns:1fr 1fr;padding:10px 10px 10px 40px;border:2pt solid #000;background:#eeeeee'><div>A</div><div>B</div></div>";
        var laid = Lay(html);
        var fills = Regex.Matches(DecodeStreams(ToPdf(html)), @"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\n")
            .Select(m => (X: D(m.Groups[1].Value), Y: D(m.Groups[2].Value), W: D(m.Groups[3].Value), H: D(m.Groups[4].Value)))
            .Where(r => r.W > Band - 1 && r.W < Band + 1).ToList();

        Assert.Equal(Margin + 2 + 30, laid["A"].X, 1);
        Assert.Equal(Margin + 2 + 30 + (Band - 4 - 37.5) / 2, laid["B"].X, 0);
        var fill = Assert.Single(fills);
        Assert.True(fill.H > 13 + 15 + 4 - 1, $"the box is {fill.H:F1}pt tall, less than a line, the padding and the border");
        Assert.True(fill.Y < laid["A"].Y && fill.Y + fill.H > laid["A"].Y, $"the fill from {fill.Y:F1} to {fill.Y + fill.H:F1} does not enclose the items' line at {laid["A"].Y:F1}");
    }

    /// <summary>A stated width is the room the tracks share: two equal tracks in a 400px container put B 150pt in, not at the band's middle.</summary>
    [Fact]
    public void AGridContainersStatedWidth_IsItsTracksRoom()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:1fr 1fr;width:400px'><div>A</div><div>B</div></div>");

        Assert.Equal(Margin, laid["A"].X, 1);
        Assert.Equal(Margin + 150, laid["B"].X, 0);
    }

    // ------------------------------------------------------------------

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private sealed class Laid
    {
        public readonly List<(string Text, double X, double Y, double Width)> Words = new();

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
        using var input = new MemoryStream(ToPdf(html), writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            laid.Words.Add((word.Text, word.X, word.Y, word.Width));
        return laid;
    }
}
