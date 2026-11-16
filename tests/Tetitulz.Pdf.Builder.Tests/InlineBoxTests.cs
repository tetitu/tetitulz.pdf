// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="InlineBox"/>: a border, padding and background around a stretch of inline text.
/// </summary>
/// <remarks>
/// Asserted on geometry read back from the content stream — the stroked rectangle's corners and the
/// x of the text after the box — because a box that is drawn in the wrong place, or that takes no
/// room, still "draws a rectangle". Pure primaries make the colour operators unambiguous.
/// </remarks>
public class InlineBoxTests
{
    private static readonly Regex StrokedRect = new(
        @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+re\s*\n?S\b", RegexOptions.Compiled);

    private static List<(double X, double Y, double W, double H)> Rects(string content) =>
        StrokedRect.Matches(content).Cast<Match>()
            .Select(m => (D(m.Groups[1].Value), D(m.Groups[2].Value), D(m.Groups[3].Value), D(m.Groups[4].Value)))
            .ToList();

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static InlineBox RedBox(double pad = 6) => new()
    {
        Border = new Border(1, "#FF0000"),
        PaddingLeft = pad, PaddingRight = pad, PaddingTop = 2, PaddingBottom = 2,
    };

    private static string Content(params IInlineElement[] runs) =>
        Content(TextAlignment.Left, runs);

    private static string Content(TextAlignment align, params IInlineElement[] runs)
    {
        var p = new Paragraph { Alignment = align };
        p.Runs.AddRange(runs);
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    [Fact]
    public void ABoxDrawsItsBorderAroundItsText_AndTheControlDrawsNone()
    {
        string boxed = Content(new TextRun { Text = "BOXED", Box = RedBox() }, new TextRun { Text = " AFTER" });
        string plain = Content(new TextRun { Text = "BOXED" }, new TextRun { Text = " AFTER" });

        Assert.Contains("1 0 0 RG", boxed);
        Assert.Empty(Rects(plain));

        var rect = Assert.Single(Rects(boxed));
        double textX = PdfContentProbe.XOf(boxed, "BOXED");
        // The text sits inside the box, one padding plus half the border band in from its left side.
        Assert.True(rect.X < textX && textX - rect.X > 5 && textX - rect.X < 8,
            $"the box's left side at x={rect.X:F1} is not a padding's width left of the text at x={textX:F1}");
    }

    /// <summary>
    /// The horizontal padding and border take room in the line: the text after the box moves right
    /// by both paddings and both borders.
    /// </summary>
    [Fact]
    public void ABoxTakesItsPaddingAndBorderInTheLine()
    {
        string boxed = Content(new TextRun { Text = "BOXED", Box = RedBox(pad: 10) }, new TextRun { Text = " AFTER" });
        string plain = Content(new TextRun { Text = "BOXED" }, new TextRun { Text = " AFTER" });

        double moved = PdfContentProbe.XOf(boxed, "AFTER") - PdfContentProbe.XOf(plain, "AFTER");
        Assert.True(Math.Abs(moved - (10 + 10 + 2 * 1)) < 0.5,
            $"the text after the box moved {moved:F2}pt; two paddings and two borders are 22pt");
    }

    /// <summary>
    /// Vertically the box is the text's glyph box — the face's line metrics, Helvetica's 0.905 of the
    /// size above the baseline and 0.212 below (batch 80; a fixed 0.83 and 0.22 before) — plus the
    /// vertical padding and the border, stroked along the middle of the border band.
    /// </summary>
    /// <remarks>
    /// Nothing pinned the box's height until a mutation dropped the descent from its bottom edge
    /// and the suite stayed green (.claude/mutations-halfleading.ps1, H9).
    /// </remarks>
    [Fact]
    public void ABoxSpansTheGlyphBoxPlusItsVerticalPadding()
    {
        string content = Content(new TextRun { Text = "BOXED", FontSize = 10, Box = RedBox() });
        var rect = Assert.Single(Rects(content));
        double baseline = PdfContentProbe.YOf(content, "BOXED");

        // Bottom: 2.12pt of descent, 2pt of padding, the 1pt border's outer half-band inside it.
        Assert.Equal(baseline - 2.12 - 2 - 0.5, rect.Y, 2);
        // Top: 9.05pt of ascent, 2pt of padding, likewise.
        Assert.Equal(baseline + 9.05 + 2 + 0.5, rect.Y + rect.H, 2);
    }

    /// <summary>
    /// A boxed run with no text is the box alone: it takes its padding and border in the line and
    /// draws its border — an icon drawn as the background of an empty, padded element.
    /// </summary>
    [Fact]
    public void AnEmptyBoxedRunTakesItsRoomAndDrawsItsBox()
    {
        string boxed = Content(new TextRun { Text = "A " }, new TextRun { Text = "", Box = RedBox(pad: 10) },
                               new TextRun { Text = " B" });
        string plain = Content(new TextRun { Text = "A " }, new TextRun { Text = "" }, new TextRun { Text = " B" });

        Assert.Single(Rects(boxed));
        Assert.Empty(Rects(plain));
        Assert.Equal(10 + 10 + 2 * 1, PdfContentProbe.XOf(boxed, "B") - PdfContentProbe.XOf(plain, "B"), 1);
    }

    /// <summary>
    /// An empty box keeps its place when bidi reorders the line. Its piece has no text, so it is
    /// given U+FFFC — a neutral — to be ordered by; a piece with nothing to order by was appended
    /// to the end of the line.
    /// </summary>
    [Fact]
    public void AnEmptyBoxKeepsItsPlaceInARightToLeftLine()
    {
        var p = new Paragraph { Direction = TextDirection.RightToLeft };
        p.Runs.Add(new TextRun { Text = "one " });
        p.Runs.Add(new TextRun { Text = "", Box = RedBox(pad: 10) });
        p.Runs.Add(new TextRun { Text = " two" });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());

        var rect = Assert.Single(Rects(content));
        double one = PdfContentProbe.XOf(content, "one"), two = PdfContentProbe.XOf(content, "two");
        // Latin inside a right-to-left paragraph reads left to right: one, the box, two.
        Assert.True(one < rect.X && rect.X < two, $"the box at x={rect.X:F1} is not between one ({one:F1}) and two ({two:F1})");
    }

    /// <summary>
    /// An empty box's piece draws nothing, so it resolves no font: in a family the exporter would
    /// have to go looking for, it reports nothing — where the same family with text does.
    /// </summary>
    [Fact]
    public void AnEmptyBoxResolvesNoFont()
    {
        static List<string> Diagnostics(string text)
        {
            var seen = new List<string>();
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = text, FontName = "No Such Family", Box = RedBox() });
            var section = new Section();
            section.Children.Add(p);
            var doc = new Document();
            doc.Sections.Add(section);
            doc.ToPdf(new Tetitulz.Pdf.Builder.Export.PdfExportOptions
            {
                AllowSystemFontSubstitution = false,
                OnDiagnostic = d => seen.Add(d.Kind.ToString()),
            });
            return seen;
        }

        Assert.Empty(Diagnostics(""));
        // The control: the family really is one the exporter reports on.
        Assert.NotEmpty(Diagnostics("X"));
    }

    /// <summary>
    /// An inline box paints its background image inside itself, at its position — centred here —
    /// over the fill and under the border.
    /// </summary>
    [Fact]
    public void ABoxPaintsItsBackgroundImageInsideItself()
    {
        var box = RedBox(pad: 20);
        box.BackgroundImage = new BackgroundImage
        {
            Data = ParagraphBoxTests.MakeRgbPng(8, 8), Repeat = BackgroundRepeat.NoRepeat, PositionX = 0.5,
        };
        string content = Content(new TextRun { Text = "", Box = box });

        var rect = Assert.Single(Rects(content));
        var draw = Assert.Single(ParagraphBoxTests.ImageDraws(content));
        // The box's outer left edge is half a border band outside the stroke; the tile is 8px, 6pt.
        double outerLeft = rect.X - 0.5, outerWidth = rect.W + 1;
        Assert.Equal(outerLeft + (outerWidth - 6) / 2, draw.x, 2);
        Assert.Equal(6, draw.w, 2);
    }

    [Fact]
    public void ABoxBackgroundFillsTheBox()
    {
        var box = RedBox();
        box.Border = null;
        box.Background = "#0000FF";
        string content = Content(new TextRun { Text = "FILLED", Box = box });

        Assert.Contains("0 0 1 rg", content);
        Assert.DoesNotContain("0 0 1 rg", Content(new TextRun { Text = "FILLED" }));
    }

    /// <summary>
    /// Layout counts the box's room once per line, so alignment places the BOX: right-aligned, its
    /// right side meets the right margin — not overshooting it by the padding, and not short of it
    /// by a padding counted again for the box's second word.
    /// </summary>
    [Fact]
    public void ARightAlignedBoxEndsAtTheMargin()
    {
        string content = Content(TextAlignment.Right, new TextRun { Text = "RIGHT SIDE", Box = RedBox() });
        var rect = Assert.Single(Rects(content));

        // The default section's right margin, read rather than assumed (it is A4). The stroke runs
        // half a border band inside the box's outer edge.
        var section = new Section();
        double margin = section.PageWidthPt - section.MarginRight;
        double outerRight = rect.X + rect.W + 0.5;
        Assert.True(Math.Abs(outerRight - margin) < 1.0,
            $"the box's right edge is at x={outerRight:F1}, not the margin at {margin:F1}");
    }

    /// <summary>A box that wraps is drawn once per line it lands on.</summary>
    [Fact]
    public void AWrappedBoxIsDrawnOncePerLine()
    {
        string longText = string.Join(" ", Enumerable.Repeat("wrapping", 60));
        string content = Content(new TextRun { Text = longText, Box = RedBox() });

        int lines = PdfContentProbe.DrawnTexts(content).Select(t => Math.Round(t.Y)).Distinct().Count();
        Assert.True(lines >= 3, $"the fixture should wrap onto several lines, got {lines}");
        Assert.Equal(lines, Rects(content).Count);
    }
}
