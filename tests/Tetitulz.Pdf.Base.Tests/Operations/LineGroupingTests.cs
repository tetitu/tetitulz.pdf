// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Which glyphs <see cref="PdfStructuredTextExtractor"/> puts on one line: glyphs on one baseline
/// are a row, and rows join side by side within the band of the smaller glyph — never one above
/// the other, and never on a tall glyph's say alone.
/// </summary>
/// <remarks>
/// <para><b>The rule this replaced</b> compared each glyph, top to bottom, with the one before it,
/// within half the height of the tallest glyph the line held so far, and had no horizontal test.
/// A 19.5 pt title lent its band to the 8 pt lines beside and below it, and a Chromium invoice's
/// header — the supplier's name, the title, the identification line, the logo, "original" and the
/// address — became one line, its characters interleaved when it was sorted by x.</para>
/// <para><b>Glyphs are laid out as Chromium draws them</b> (<see cref="Draw"/>), and go straight to
/// <c>BuildLines</c> with bidi applied, as a document's extraction applies it.</para>
/// </remarks>
public class LineGroupingTests
{
    private static readonly WordAssemblyOptions Bidi = new() { ApplyBidi = true };

    /// <summary>
    /// A header's blocks set side by side at nearby baselines keep their words: the title's two
    /// words whole, the supplier's number on its label's line, and no line holding one block above
    /// another — which the page itself does, the potency of that last check.
    /// </summary>
    [Fact]
    public void AHeadersBlocksKeepTheirWords()
    {
        var page = Header();

        var lines = PdfStructuredTextExtractor.BuildLines(page, Bidi);

        var title = Assert.Single(lines, l => l.Words.Any(w => w.Text == "חשבונית"));
        Assert.Contains(title.Words, w => w.Text == "מס");
        var id = Assert.Single(lines, l => l.Words.Any(w => w.Text == "עוסק"));
        Assert.Contains("עוסק מורשה " + IsraeliInvoiceExtractorTests.Supplier, id.Text, StringComparison.Ordinal);

        Assert.Empty(lines.SelectMany(l => Stacked(Glyphs(l))));
        Assert.True(Stacked(page.Where(g => g.Char != " ").ToList()).Any(),
                    "the header has blocks one above the other, or the check above sees nothing");
    }

    /// <summary>
    /// A line of small print beside a larger field keeps its words: the spaces of the small print
    /// above it are not its spaces. They were, when a line's spaces were those within the band of
    /// its tallest glyph — the field's 10 pt lent the 6 pt line a band that reached the line above.
    /// The line above keeps its own spaces, the control.
    /// </summary>
    [Fact]
    public void TheSpacesOfTheLineAboveDoNotBreakAWord()
    {
        var page = new List<PdfTextChar>();
        Draw(page, "אאא בבב גגג", 128.4f, 678.04f, 6f, rtl: true);
        Draw(page, "דדד ההה ווו", 128.4f, 673.12f, 6f, rtl: true);
        Draw(page, "זזזזז לא חחחחח", 128.4f, 668.14f, 6f, rtl: true);
        Draw(page, "מספר לקוח", 442.4f, 669.82f, 10f, rtl: true);
        Draw(page, "1234567", 352.1f, 669.82f, 10f);

        var lines = PdfStructuredTextExtractor.BuildLines(page, Bidi);

        var sentence = Assert.Single(lines, l => l.Words.Any(w => w.Text.StartsWith("ז", StringComparison.Ordinal)));
        Assert.Equal(new[] { "זזזזז", "לא", "חחחחח" }, sentence.Words.Where(w => w.Height < 7f).Select(w => w.Text));
        var above = Assert.Single(lines, l => l.Words.Any(w => w.Text == "דדד"));
        Assert.Equal(new[] { "דדד", "ההה", "ווו" }, above.Words.Select(w => w.Text));
    }

    /// <summary>
    /// A small caption printed just under a label — a bilingual invoice's English under its Hebrew —
    /// is a line of its own, and its spaces are not the label's: a blank is a row's within the band
    /// of the blank and the row, not the larger band of the row alone, under which the caption's
    /// spaces broke the label's words.
    /// </summary>
    [Fact]
    public void ACaptionsSpacesDoNotBreakTheLabelAboveIt()
    {
        var page = new List<PdfTextChar>();
        Draw(page, "Hello world", 50f, 100f, 10f);
        Draw(page, "tiny print here", 52f, 96f, 6f);

        var lines = PdfStructuredTextExtractor.BuildLines(page, Bidi);

        Assert.Equal(new[] { "Hello world", "tiny print here" }, lines.Select(l => l.Text));
    }

    /// <summary>
    /// A large initial set on a body line's baseline joins that line alone. On the first line it
    /// lent the old rule its band and merged the three lines; on the last, the control, it never
    /// reached the lines above.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void ALargeInitialKeepsTheLinesApart(int initialLine)
    {
        string[] body = { "Lorem ipsum", "dolor sit amet", "Consectetur elit" };
        var page = new List<PdfTextChar>();
        for (int i = 0; i < body.Length; i++)
        {
            float y = 624f - 12f * i;
            if (i == initialLine)
            {
                page.Add(new PdfTextChar(body[i].Substring(0, 1), 50f, y, 22f, 36f));
                Draw(page, body[i].Substring(1), 75f, y, 12f);
            }
            else Draw(page, body[i], 75f, y, 12f);
        }

        var lines = PdfStructuredTextExtractor.BuildLines(page, Bidi);

        Assert.Equal(body, lines.Select(l => l.Text));
    }

    /// <summary>
    /// A 30 pt word and a 10 pt word beside it, 10 pt lower, are two lines: within the tall word's
    /// band, not the small one's. Three points lower is one line, the control.
    /// </summary>
    [Theory]
    [InlineData(10f, 2)]
    [InlineData(3f, 1)]
    public void ATallGlyphLendsItsBandToNothing(float drop, int expectedLines)
    {
        var page = new List<PdfTextChar>();
        Draw(page, "LOGO", 50f, 700f, 30f);
        Draw(page, "Street", 300f, 700f - drop, 10f);

        Assert.Equal(expectedLines, PdfStructuredTextExtractor.BuildLines(page, Bidi).Count);
    }

    /// <summary>
    /// Two columns of a contract, the second set half a line lower: each of its lines lies within
    /// the band of the first column's lines above and below it. Side by side, a line of each column
    /// joins; one above the other, a column's two lines never do — and a mark or clause number, a
    /// run of its own left of a line, hides neither line from the check. The page itself stacks
    /// lines, the potency of the check that no line does.
    /// </summary>
    [Fact]
    public void StaggeredColumnsDoNotChainIntoOneLine()
    {
        var page = new List<PdfTextChar>();
        Draw(page, "*", 20f, 700f, 12f);
        Draw(page, "Terms of use", 50f, 700f, 12f);
        Draw(page, "Payment terms", 50f, 689f, 12f);
        Draw(page, "2.", 20f, 678f, 12f);
        Draw(page, "Late fees", 50f, 678f, 12f);
        Draw(page, "Contact us", 300f, 694.5f, 12f);
        Draw(page, "By phone", 300f, 683.5f, 12f);
        Draw(page, "By mail", 300f, 672.5f, 12f);

        var lines = PdfStructuredTextExtractor.BuildLines(page, Bidi);

        Assert.Equal(new[] { "* Terms of use Contact us", "Payment terms By phone", "2. Late fees By mail" },
                     lines.Select(l => l.Text));
        Assert.Empty(lines.SelectMany(l => Stacked(Glyphs(l))));
        Assert.True(Stacked(page.Where(g => g.Char != " ").ToList()).Any(), "the columns' lines stack");
    }

    /// <summary>
    /// A wide table row whose cells alternate between two baselines — a larger and a smaller size,
    /// centred — is one line however many cells it has: the check against stacking walks the two
    /// rows' cells together, not every cell of one against every cell of the other.
    /// </summary>
    [Fact]
    public void AWideRowOnTwoBaselinesStaysOneLine()
    {
        var page = new List<PdfTextChar>();
        for (int c = 0; c < 80; c++)
            Draw(page, (c + 1).ToString("D2", CultureInfo.InvariantCulture), 30f * c,
                 c % 2 == 0 ? 500f : 498.5f, c % 2 == 0 ? 12f : 9f);

        var line = Assert.Single(PdfStructuredTextExtractor.BuildLines(page, Bidi));

        Assert.Equal(80, line.Words.Count);
    }

    /// <summary>
    /// A page of ordinary lines takes a step a glyph and one a line: each line, out of reach once
    /// the next is read, is let go rather than tried again by every line below it.
    /// </summary>
    [Fact]
    public void AnOrdinaryPageTakesAStepAGlyphAndOneALine()
    {
        var page = new List<PdfTextChar>();
        for (int i = 0; i < 50; i++)
            Draw(page, "Lorem ipsum dolor sit", 50f, 700f - 14f * i, 12f);

        var lines = PdfStructuredTextExtractor.BuildLines(page, Bidi);

        Assert.Equal(50, lines.Count);
        Assert.Equal(page.Count(g => g.Char != " ") + 49, PdfStructuredTextExtractor.LastLineGroupingWork);
    }

    /// <summary>
    /// Rotated glyphs keep the old rule: a glyph joins the line of the one before it within half the
    /// tallest glyph the line holds so far, the tallest kept as the line grows. A glyph 8 pt below a
    /// 20 pt one joins; 8 pt below 10 pt glyphs alone, it starts a line of its own.
    /// </summary>
    [Theory]
    [InlineData(20f, 1)]
    [InlineData(10f, 2)]
    public void RotatedGlyphsKeepTheTallestGlyphsBand(float second, int expectedLines)
    {
        var page = new List<PdfTextChar> { Rotated("a", 500f, 10f), Rotated("b", 497f, second), Rotated("c", 489f, 10f) };

        Assert.Equal(expectedLines, PdfStructuredTextExtractor.BuildLines(page, Bidi).Count);
    }

    /// <summary>
    /// Rows a page means as one line stay one: a centred amount beside its smaller label, a
    /// recognised row skewed 0.3°, a value raised beside its label, a superscript, a total and a
    /// larger amount on one baseline, cells a few hundredths of a point apart, a cascading Arabic
    /// word, a point drawn between two letters.
    /// </summary>
    [Theory]
    [InlineData("centred")]
    [InlineData("skewed")]
    [InlineData("raised")]
    [InlineData("superscript")]
    [InlineData("sizes")]
    [InlineData("cells")]
    [InlineData("cascade")]
    [InlineData("point")]
    public void RowsMeantAsOneLineStayOne(string layout)
    {
        var page = Layout(layout);

        var line = Assert.Single(PdfStructuredTextExtractor.BuildLines(page, Bidi));

        Assert.Equal(page.Count(g => g.Char != " "), line.Words.Sum(w => w.Chars.Count));
    }

    /// <summary>
    /// A hundred thousand glyphs on one baseline are one row, a step each: the old rule looked
    /// through the line for its tallest glyph at every glyph, quadratic work. Rotated glyphs, which
    /// keep the old rule, keep the tallest as the line grows — a step each as well.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdversarialOneBaseline_IsGroupedInLinearSteps(bool rotated)
    {
        const int n = 100_000;
        var page = new List<PdfTextChar>(n);
        for (int i = 0; i < n; i++)
            page.Add(new PdfTextChar(((char)('a' + i % 26)).ToString(), 6f * i, 500f, 6f, 10f)
                     { IsUpright = !rotated, Rotation = rotated ? 180f : 0f });

        var line = Assert.Single(PdfStructuredTextExtractor.BuildLines(page, Bidi));

        Assert.Equal(n, line.Words.Sum(w => w.Chars.Count));
        Assert.Equal(rotated ? n - 1 : n, PdfStructuredTextExtractor.LastLineGroupingWork);
    }

    /// <summary>
    /// Rows packed a fiftieth of a point apart, side by side, every other one a giant glyph's:
    /// within the giants' band every row of a line is near, and a row looking through its lines
    /// for the nearest, then checking its join, would walk them all — work growing with the square
    /// of the rows. A row takes at most <see cref="PdfStructuredTextExtractor.MaxStepsPerRow"/>.
    /// </summary>
    /// <remarks>
    /// Measured on this fixture: about 550 steps a row within the budget, 11 million in all; with
    /// the budget lifted, one line of every row and 301 million steps — eight seconds where the
    /// bounded grouping takes a third of one. The total's bound below is the budget's, so a lifted
    /// budget fails it by a factor of fourteen. The total alone cannot see one search left
    /// unbounded: a line can grow no longer than one stacking check walks, so the rows' average
    /// stays under the budget; the busiest row is asserted to reach it and stop there.
    /// </remarks>
    [Fact]
    public void AdversarialPackedRows_TakeABoundedNumberOfStepsEach()
    {
        const int n = 20_000;
        var page = new List<PdfTextChar>(n);
        for (int i = 0; i < n; i++)
            page.Add(new PdfTextChar("x", 2f * i, 1000f - 0.02f * i, 1f, i % 2 == 0 ? 1000f : 0.001f));

        var lines = PdfStructuredTextExtractor.BuildLines(page, Bidi);

        Assert.Equal(n, lines.Sum(l => l.Words.Sum(w => w.Chars.Count)));
        // A step per glyph into its row, at most the budget per row, and one per line let go.
        Assert.InRange((long)PdfStructuredTextExtractor.LastLineGroupingWork,
                       n, n + (long)n * PdfStructuredTextExtractor.MaxStepsPerRow + n);
        Assert.Equal(PdfStructuredTextExtractor.MaxStepsPerRow, PdfStructuredTextExtractor.LastLineGroupingRowSteps);
    }

    /// <summary>
    /// A Chromium invoice's header, drawn as Chromium draws it — each block at its own size, the
    /// right-to-left runs in <c>/ReversedChars</c> — reads as a tax invoice with its supplier's
    /// number: its title and identification line are no longer interleaved with their neighbours.
    /// </summary>
    [Fact]
    public void AChromiumHeaderReadsItsTitleAndSupplier()
    {
        byte[] pdf = new InvoicePdfBuilder()
            .Size(13).ReversedChars(b => b.Rtl("אופק שירותים", 506, 800, step: 8, space: 12))
            .Size(20).ReversedChars(b => b.Rtl("חשבונית מס", 128, 798, step: 12, space: 17))
            .Size(8).ReversedChars(b => b.Rtl("עוסק מורשה:", 506, 789, step: 5, space: 7))
            .AsciiRun(IsraeliInvoiceExtractorTests.Supplier, 400, 789, step: 5)
            .Size(16).ReversedChars(b => b.Rtl("לפ", 548, 787, step: 10, space: 14))
            .Size(8).ReversedChars(b => b.Rtl("מקור", 91, 784, step: 5, space: 7))
            .ReversedChars(b => b.Rtl("רחוב הרצל תל אביב", 506, 778, step: 5, space: 7))
            .ReversedChars(b => b.Rtl("טלפון", 506, 768, step: 5, space: 7))
            .Build();

        var inv = IsraeliInvoiceExtractor.Extract(new MemoryStream(pdf));

        Assert.Equal(IsraeliDocumentType.TaxInvoice, inv.DocumentType);
        Assert.Equal(IsraeliInvoiceExtractorTests.Supplier, inv.SupplierId);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// Lays <paramref name="text"/> out as Chromium draws it: each glyph at its own origin, a letter
    /// 0.6 of the size wide and a space 0.25 — a blank glyph of its own, the only thing that ends a
    /// word at that spacing — from <paramref name="edge"/>: its left edge, or for a right-to-left
    /// run its right edge, the first letter right-most.
    /// </summary>
    private static void Draw(List<PdfTextChar> page, string text, float edge, float y, float size, bool rtl = false)
    {
        float x = edge;
        foreach (char c in text)
        {
            float w = (c == ' ' ? 0.25f : 0.6f) * size;
            if (rtl) x -= w;
            page.Add(new PdfTextChar(c.ToString(), x, y, w, size, rtl && c != ' '));
            if (!rtl) x += w;
        }
    }

    // An invoice header at a Chromium invoice's geometry: the supplier's name, the title, the
    // identification line, the logo, "original", the address and the phone — each block's text,
    // edge, baseline and size.
    private static List<PdfTextChar> Header()
    {
        var page = new List<PdfTextChar>();
        Draw(page, "אופק שירותים", 515f, 800.13f, 12.75f, rtl: true);
        Draw(page, "חשבונית מס", 138.8f, 797.88f, 19.5f, rtl: true);
        Draw(page, "עוסק מורשה", 515f, 788.88f, 8.25f, rtl: true);
        Draw(page, IsraeliInvoiceExtractorTests.Supplier, 409.7f, 788.88f, 8.25f);
        Draw(page, "לפ", 556f, 786.63f, 16.5f, rtl: true);
        Draw(page, "מקור", 91.2f, 784.38f, 8.25f, rtl: true);
        Draw(page, "רחוב הרצל תל אביב", 515f, 778.38f, 8.25f, rtl: true);
        Draw(page, "טלפון", 515f, 767.88f, 8.25f, rtl: true);
        return page;
    }

    private static List<PdfTextChar> Layout(string name)
    {
        var page = new List<PdfTextChar>();
        switch (name)
        {
            case "centred":       // a 13 px label and a 21 px total in one table row, middle-aligned
                Draw(page, "Total", 60f, 565.17f, 9.75f);
                Draw(page, "3,953.00", 440f, 563.67f, 15.75f);
                break;
            case "skewed":        // an OCR layer's row, each glyph placed along a 0.3° slope
                float slope = (float)Math.Tan(0.3 * Math.PI / 180);
                foreach (var (text, x) in new[] { ("Subtotal", 60f), ("1,000.00", 480f) })
                    for (int i = 0; i < text.Length; i++)
                    {
                        float gx = x + 6f * i;
                        page.Add(new PdfTextChar(text[i].ToString(), gx, 500f + (gx - 60f) * slope, 6f, 10f));
                    }
                break;
            case "raised":        // a value raised 1.5 pt, 12 pt from its label
                Draw(page, "Date:", 60f, 400f, 12f);
                Draw(page, "01/02/2026", 60f + 5 * 7.2f + 12f, 401.5f, 12f);
                break;
            case "superscript":   // a 7 pt figure raised 3 pt
                Draw(page, "E=mc", 60f, 700f, 12f);
                Draw(page, "2", 60f + 4 * 7.2f, 703f, 7f);
                break;
            case "sizes":         // a total and a larger amount on one baseline
                Draw(page, "Total", 50f, 300f, 10f);
                Draw(page, "1,234.00", 400f, 300f, 16f);
                break;
            case "cells":         // three cells, each on its own rounding of the row's baseline
                Draw(page, "Item", 50f, 220.04f, 11f);
                Draw(page, "2", 200f, 220.00f, 11f);
                Draw(page, "15.00", 350f, 220.02f, 11f);
                break;
            case "cascade":       // each letter of a word 1.5 pt below the one before it
                for (int i = 0; i < 5; i++)
                    page.Add(new PdfTextChar(((char)(0x0645 + i)).ToString(), 200f - 7.2f * (i + 1),
                                             500f - 1.5f * i, 7.2f, 12f, isRtl: true));
                break;
            case "point":         // a qamats drawn 2 pt low between two letters, over neither
                page.Add(new PdfTextChar("ב", 110f, 100f, 7.2f, 12f, isRtl: true));
                page.Add(new PdfTextChar(((char)0x05B8).ToString(), 108.2f, 98f, 1.2f, 12f, isRtl: true));
                page.Add(new PdfTextChar("ג", 100f, 100f, 7.2f, 12f, isRtl: true));
                break;
            default:
                throw new ArgumentException(name, nameof(name));
        }
        return page;
    }

    private static PdfTextChar Rotated(string ch, float y, float size)
        => new PdfTextChar(ch, 100f, y, 0.6f * size, size) { IsUpright = false, Rotation = 90f };

    private static List<PdfTextChar> Glyphs(PdfTextLine line) => line.Words.SelectMany(w => w.Chars).ToList();

    // Pairs of glyphs one above the other — overlapping horizontally — whose baselines are further
    // apart than the band of the smaller: what no line may hold.
    private static IEnumerable<(PdfTextChar, PdfTextChar)> Stacked(IReadOnlyList<PdfTextChar> glyphs)
    {
        for (int i = 0; i < glyphs.Count; i++)
            for (int j = i + 1; j < glyphs.Count; j++)
            {
                PdfTextChar a = glyphs[i], b = glyphs[j];
                bool overlap = a.X < b.X + b.Width && b.X < a.X + a.Width;
                float band = Math.Max(Math.Min(a.Height, b.Height) * 0.5f, 2f);
                if (overlap && Math.Abs(a.Y - b.Y) > band) yield return (a, b);
            }
    }
}
