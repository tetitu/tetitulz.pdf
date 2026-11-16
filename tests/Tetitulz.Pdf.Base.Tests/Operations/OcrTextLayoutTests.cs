// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="OcrTextLayout"/>: recognised words — a box and a logical string each — laid out as
/// the page displays them, straightened, and drawn in their visual order, so that a scan reads like
/// a page with a text layer.
/// </summary>
public class OcrTextLayoutTests
{
    private const float CharWidth = 6f, Height = 10f;

    // A word of the displayed page, its right edge at `right` for a Hebrew word, or its left edge
    // at `left` for any other; boxes in points.
    private static PdfOcrWord Rtl(string text, float right, float y) => new PdfOcrWord(text, right - CharWidth * text.Length, y, CharWidth * text.Length, Height);
    private static PdfOcrWord Ltr(string text, float left, float y) => new PdfOcrWord(text, left, y, CharWidth * text.Length, Height);

    /// <summary>
    /// A word mixing Hebrew and digits — <c>ל-12</c>, "for 12" — is drawn in its visual order, so
    /// the line reads back as written; drawn uniformly right to left it came back <c>12-ל</c>. A
    /// label glued to its number, <c>מס'12345</c>, is found and its number read.
    /// </summary>
    [Fact]
    public void AWordMixingHebrewAndDigitsReadsBackAsWritten()
    {
        var page = OcrTextLayout.Build(0, new[]
        {
            Rtl("שירות", 500, 700), Rtl("ל-12", 464, 700), Rtl("חודשים", 434, 700),
            Rtl("מס'12345", 500, 680),
        });

        var line = page.Lines[0];
        Assert.Equal("שירות ל-12 חודשים", line.Text);
        Assert.Equal("ל-12", PdfFieldExtractor.WordReadingOrder(line.Words.Single(w => w.Text == "ל-12")));

        var r = PdfFieldExtractor.Extract(new[] { page }, new[] { new FieldQuery("no", "מס'") })[0];
        Assert.Equal("12345", r.Value);
    }

    // The displayed page's two lines: "Total 118.00" over "VAT 18.00".
    private static readonly (string Text, float X, float Y)[] Displayed =
    {
        ("Total", 72, 700), ("118.00", 120, 700), ("VAT", 72, 680), ("18.00", 120, 680),
    };

    /// <summary>
    /// A page turned a quarter: the engine's words come back in unrotated user space, where a
    /// displayed line is a column. Built with the page's rotation, the two displayed lines are two
    /// lines again, each read in order — clockwise and counter-clockwise.
    /// </summary>
    [Theory]
    [InlineData(90)]
    [InlineData(270)]
    public void AQuarterTurnedPageIsLaidOutAsDisplayed(int rotation)
    {
        const float pageWidth = 612f, pageHeight = 792f;   // the unrotated page
        var words = Displayed.Select(d =>
        {
            float w = CharWidth * d.Text.Length, h = Height;
            // Back from the displayed frame (pageHeight wide, pageWidth tall) into user space.
            return rotation == 90
                ? new PdfOcrWord(d.Text, pageWidth - d.Y - h, d.X, h, w)
                : new PdfOcrWord(d.Text, d.Y, pageHeight - d.X - w, h, w);
        }).ToList();

        var page = OcrTextLayout.Build(0, words, rotation);

        Assert.Equal(new[] { "Total 118.00", "VAT 18.00" }, page.Lines.Select(l => l.Text));
    }

    /// <summary>
    /// A table scanned three degrees askew (<see cref="SkewedTable"/>). Straightened, every row is
    /// one line, and the item table reads each item with its own description and amount; the
    /// description's descender is kept in its row by the line's one baseline.
    /// </summary>
    [Fact]
    public void ASkewedScanIsStraightenedSoRowsStayWhole()
    {
        var page = OcrTextLayout.Build(0, SkewedTable(3));
        var items = IsraeliInvoiceExtractor.Extract(new[] { page }).LineItems;

        Assert.Equal(4, page.Lines.Count);
        Assert.Equal(new[] { "ייעוץ", "פיתוח", "הדרכה" }, items.Select(i => i.Description));
        Assert.Equal(new decimal?[] { 6000m, 4000m, 300m }, items.Select(i => i.Amount));
    }

    /// <summary>
    /// An engine that reports every word twice: a word's copy lies over it, its run nothing, and
    /// gives no slope — taken as one, it was a slope of nothing over nothing, which made the
    /// median, and every straightened line, not a number. The copies' own neighbours measure the
    /// skew, and every row stays one line.
    /// </summary>
    [Fact]
    public void MalformedDuplicateWordBoxesStillLetASkewedScanBeStraightened()
    {
        var words = SkewedTable(3);

        var page = OcrTextLayout.Build(0, words.Concat(words).ToList());

        Assert.Equal(4, page.Lines.Count);
        Assert.All(page.Lines, l => Assert.Equal(8, l.Words.Count));
    }

    // An item table scanned `degrees` askew, four rows a sixteen-point pitch apart. At three
    // degrees each row rises sixteen points across the page, a whole row pitch — its right end
    // level with the next row's left. The description's ink box hangs three and a half points
    // lower than the numbers' — a descender.
    private static List<PdfOcrWord> SkewedTable(double degrees)
    {
        float slope = (float)Math.Tan(degrees * Math.PI / 180);
        float[] columns = { 560f, 440f, 360f, 260f };
        string[][] rows =
        {
            new[] { "תיאור", "כמות", "מחיר", "סה\"כ" },
            new[] { "ייעוץ", "2", "3,000.00", "6,000.00" },
            new[] { "פיתוח", "1", "4,000.00", "4,000.00" },
            new[] { "הדרכה", "3", "100.00", "300.00" },
        };
        const float descender = 3.5f;
        var words = new List<PdfOcrWord>();
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
            {
                string text = rows[r][c];
                float width = CharWidth * text.Length, x = columns[c] - width;
                float y = 640 - r * 16 + (x + width / 2 - 300) * slope;
                float drop = c == 0 ? descender : 0f;
                words.Add(new PdfOcrWord(text, x, y - drop, width, Height + drop));
            }
        return words;
    }
}
