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
/// <see cref="PdfExportOptions.HalfLeading"/>: the leading split half above and half below each
/// line's text (CSS 2.1 §10.8.1), instead of the baseline on the line's bottom edge.
/// </summary>
/// <remarks>
/// A tall line makes the difference large enough to read: 30pt lines of 10pt text. The glyph box
/// the exporter centres is the face's line metrics — for Helvetica, Liberation Sans's (Arial's)
/// 0.905 of the size above the baseline and 0.212 below (batch 80; a fixed 0.83 and 0.22 before) —
/// the box a highlight fills — so the claim is checked as CSS states it: equal space above and
/// below that box. 10pt is below the document's default size on purpose: the box centred is the
/// text's, not the default's.
/// </remarks>
public class HalfLeadingTests
{
    private const double Line = 30, Size = 10;
    /// <summary>Helvetica's line metrics: Liberation Sans's hhea ascender and descender over the em.</summary>
    private const double Ascent = 0.905, Descent = 0.212;

    private static string Content(bool halfLeading, string highlight = "")
    {
        var section = new Section();
        foreach (string word in new[] { "First", "Second" })
        {
            // No paragraph spacing, so a line's top is where its paragraph starts and two
            // paragraphs' lines are exactly one line apart.
            var p = new Paragraph { LineSpacingPt = Line, SpacingBefore = 0, SpacingAfter = 0 };
            p.Runs.Add(new TextRun { Text = word, FontSize = Size, Highlight = highlight });
            section.Children.Add(p);
        }
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions { HalfLeading = halfLeading }));
    }

    [Fact]
    public void TheLeadingIsSplitEvenlyAboveAndBelowTheText()
    {
        // Without the option the first baseline IS the first line's bottom — that is the default's
        // contract, and it is what locates the line box here.
        double bottom = PdfContentProbe.YOf(Content(halfLeading: false), "First");
        double top = bottom + Line;
        double baseline = PdfContentProbe.YOf(Content(halfLeading: true), "First");

        double above = top - (baseline + Ascent * Size);
        double below = (baseline - Descent * Size) - bottom;
        Assert.Equal(above, below, 3);
        Assert.Equal((Line - (Ascent + Descent) * Size) / 2, below, 3);
    }

    /// <summary>
    /// A line of mixed sizes centres its largest text, wherever on the line that text is; the
    /// smaller text shares the baseline.
    /// </summary>
    [Fact]
    public void ALineCentresItsLargestText()
    {
        static string Mixed(bool halfLeading)
        {
            var p = new Paragraph { LineSpacingPt = 40, SpacingBefore = 0, SpacingAfter = 0 };
            p.Runs.Add(new TextRun { Text = "small ", FontSize = 8 });
            p.Runs.Add(new TextRun { Text = "BIG", FontSize = 20 });
            var section = new Section();
            section.Children.Add(p);
            var doc = new Document();
            doc.Sections.Add(section);
            return PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions { HalfLeading = halfLeading }));
        }

        double bottom = PdfContentProbe.YOf(Mixed(halfLeading: false), "BIG");
        string on = Mixed(halfLeading: true);
        double baseline = PdfContentProbe.YOf(on, "BIG");

        Assert.Equal(bottom + (40 - (Ascent + Descent) * 20) / 2 + Descent * 20, baseline, 3);
        Assert.Equal(baseline, PdfContentProbe.YOf(on, "small"), 3);
    }

    /// <summary>Only the text's place in the line moves: the lines are as far apart as before.</summary>
    [Fact]
    public void TheLinesKeepTheirHeight()
    {
        string off = Content(halfLeading: false), on = Content(halfLeading: true);

        double pitchOff = PdfContentProbe.YOf(off, "First") - PdfContentProbe.YOf(off, "Second");
        double pitchOn = PdfContentProbe.YOf(on, "First") - PdfContentProbe.YOf(on, "Second");
        Assert.Equal(Line, pitchOff, 3);
        Assert.Equal(pitchOff, pitchOn, 3);
    }

    /// <summary>What is drawn around the text — a highlight here — moves with it.</summary>
    [Fact]
    public void AHighlightMovesWithItsText()
    {
        static double HighlightY(string content)
        {
            var m = Regex.Matches(content, @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+re\s*\n?f\b")
                .Cast<Match>().First();
            return double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        }

        string off = Content(halfLeading: false, "#0000FF"), on = Content(halfLeading: true, "#0000FF");
        double textShift = PdfContentProbe.YOf(on, "First") - PdfContentProbe.YOf(off, "First");

        Assert.True(textShift > 5, $"the text moved {textShift:F2}pt");
        Assert.Equal(textShift, HighlightY(on) - HighlightY(off), 2);
    }
}
