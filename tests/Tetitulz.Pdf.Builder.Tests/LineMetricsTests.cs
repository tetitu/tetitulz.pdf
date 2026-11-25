// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A piece's glyph box — the box a highlight fills, an inline box surrounds and the half-leading
/// centres — is its font's line metrics: for a standard face, the <c>hhea</c> ascender and
/// descender of the metric-compatible Liberation font a viewer puts under it (Arial's, Times New
/// Roman's, Courier New's); for an embedded program, its own.
/// </summary>
/// <remarks>
/// A box of a fixed 0.83 of the size above the baseline and 0.22 below for every face would put
/// Arial text 4% of its size higher in a line than a browser does. Read from the highlight's
/// rectangle, whose height is the box and whose bottom is the
/// descent below the baseline.
/// </remarks>
public class LineMetricsTests
{
    /// <summary>The first filled rectangle — the highlight — as its bottom and its height.</summary>
    private static (double Y, double Height) HighlightRect(string content)
    {
        var m = Regex.Matches(content, @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+re\s*\n?f\b").Cast<Match>().First();
        return (double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), double.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>The highlight's height, and how far its bottom is below the baseline of <paramref name="word"/> (a WinAnsi run).</summary>
    private static (double Height, double Descent) HighlightBox(string content, string word)
    {
        var (y, h) = HighlightRect(content);
        return (h, PdfContentProbe.YOf(content, word) - y);
    }

    private static string Content(TextRun run, PdfExportOptions options = null)
    {
        var p = new Paragraph();
        p.Runs.Add(run);
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(options ?? new PdfExportOptions()));
    }

    /// <summary>
    /// Helvetica takes Liberation Sans's 0.905 and 0.212 of the size, Times Liberation Serif's 0.891
    /// and 0.216, Courier Liberation Mono's 0.833 and 0.300: three faces, three boxes.
    /// </summary>
    [Theory]
    [InlineData(null, 11.17, 2.12)]
    [InlineData("serif", 11.07, 2.16)]
    [InlineData("monospace", 11.33, 3.00)]
    public void AHighlightSpansTheFacesLineMetrics(string family, double height, double descent)
    {
        var run = new TextRun { Text = "Hi", FontSize = 10, Highlight = "#0000FF" };
        if (family != null) run.FontName = family;
        var (h, d) = HighlightBox(Content(run), "Hi");

        Assert.Equal(height, h, 2);
        Assert.Equal(descent, d, 2);
    }

    /// <summary>
    /// An embedded program's box is its own <c>hhea</c> metrics: the fixture font's 800 and −200 of a
    /// 1000 em make a 10pt run's box 10 tall with 2 below the baseline, where Helvetica's is 11.17
    /// with 2.12. The Hebrew run is shown as glyph ids, so its baseline is read through the control:
    /// one run each in one line of the same height, the baselines coincide, and the highlights'
    /// bottoms differ by the descents' 0.12.
    /// </summary>
    [Fact]
    public void AnEmbeddedFontsBoxIsItsOwnMetrics()
    {
        const string Hebrew = "שלום";
        var options = new PdfExportOptions { FontProvider = _ => MinimalTrueType.Build(Hebrew) };
        var (y, h) = HighlightRect(Content(new TextRun { Text = Hebrew, FontSize = 10, Highlight = "#0000FF" }, options));
        string control = Content(new TextRun { Text = "Hi", FontSize = 10, Highlight = "#0000FF" }, options);
        var (yc, hc) = HighlightRect(control);
        var (_, dc) = HighlightBox(control, "Hi");

        Assert.Equal(10.0, h, 2);
        Assert.Equal(11.17, hc, 2);
        Assert.Equal(2.12, dc, 2);
        Assert.Equal(0.12, y - yc, 2);   // 2.12 − 2.0: the fixture's descent is 0.2 of the size
    }

    /// <summary>The line's box is the tallest ascent and the deepest descent among its pieces, each at its own font.</summary>
    [Fact]
    public void ALinesBoxIsTheTallestAscentAndTheDeepestDescent()
    {
        // Courier's descent (0.300) is deeper than Helvetica's (0.212) at the same size, and
        // Helvetica's ascent (0.905) taller than Courier's (0.833): the half-leading baseline
        // takes both — (40 − (9.05 + 3.00)) / 2 + 3.00 above the line's bottom. The deeper descent
        // comes first and the taller ascent last, so neither "the first piece's" nor "the last
        // piece's" gives the answer.
        var p = new Paragraph { LineSpacingPt = 40, SpacingBefore = 0, SpacingAfter = 0 };
        p.Runs.Add(new TextRun { Text = "mono ", FontSize = 10, FontName = "monospace" });
        p.Runs.Add(new TextRun { Text = "sans", FontSize = 10 });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);

        double bottom = PdfContentProbe.YOf(PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions())), "sans");
        double baseline = PdfContentProbe.YOf(PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions { HalfLeading = true })), "sans");

        Assert.Equal(bottom + (40 - (9.05 + 3.00)) / 2 + 3.00, baseline, 2);
    }
}
