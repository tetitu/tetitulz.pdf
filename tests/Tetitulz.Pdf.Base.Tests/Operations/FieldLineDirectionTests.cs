// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;
using L = Tetitulz.Pdf.Base.Tests.Operations.InvoiceLayouts;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A label's line is read in the direction most of its letters go — the side its value lies on
/// and the order its key reads in — whatever the structured extractor, counting digits too, calls
/// the line. Each case beside the same line called right to left, the control.
/// </summary>
public class FieldLineDirectionTests
{
    // A line of words each drawn across the box a page measured for it.
    private static PdfTextLine Measured(float y, bool rtl, params (string Text, float Left, float Right)[] words)
    {
        var laid = new List<PdfTextWord>();
        foreach (var (text, left, right) in words)
        {
            bool rtlWord = L.IsRtlText(text);
            float glyph = (right - left) / text.Length;
            var chars = text.Select((c, i) => new PdfTextChar(c.ToString(), left + (rtlWord ? text.Length - 1 - i : i) * glyph,
                                                              y, glyph, L.Height, rtlWord)).ToList();
            laid.Add(new PdfTextWord(text, left, y, right - left, L.Height, chars, rtlWord));
        }
        float min = laid.Min(w => w.X), max = laid.Max(w => w.X + w.Width);
        return new PdfTextLine(string.Join(" ", words.Select(w => w.Text)), min, y, max - min, L.Height, laid, rtl);
    }

    private static IsraeliInvoice Read(params PdfTextLine[] lines) => IsraeliInvoiceExtractor.Extract(new[] { L.Page(0, lines) });

    /// <summary>
    /// "עוסק מורשה / ח.פ.: 514351287" — fourteen Hebrew letters beside nine digits, which the
    /// extractor calls a left-to-right line — gives the supplier's number, not the slash.
    /// </summary>
    [Fact]
    public void AHebrewLabelBesideALongNumberReadsRightToLeft()
    {
        PdfTextLine Line(bool rtl) => Measured(770, rtl, ("514351287", 440.7f, 483.2f), ("ח.פ.:", 485.6f, 502.4f),
                                               ("/", 504.8f, 507.1f), ("מורשה", 509.5f, 532f), ("עוסק", 534.4f, 550.5f));

        Assert.Equal("514351287", Read(Line(rtl: false)).SupplierId);
        Assert.Equal("514351287", Read(Line(rtl: true)).SupplierId);
    }

    /// <summary>
    /// A title's "מס' חשבונית LS550909" at the left of the supplier's "טל' 1-700-500-500" on one
    /// baseline: the line's first strong character is the number's L, and read from it the
    /// phone was the invoice's number.
    /// </summary>
    [Fact]
    public void AHebrewLineOpeningWithALatinNumberReadsRightToLeft()
    {
        PdfTextLine Line(bool rtl) => Measured(751, rtl, ("LS550909", 44.5f, 93f), ("חשבונית", 95.9f, 130.8f),
                                               ("מס'", 133.7f, 148.1f), ("1-700-500-500", 481.8f, 537.6f), ("טל'", 540f, 550.5f));

        Assert.Equal("LS550909", Read(Line(rtl: false)).InvoiceNumber);
        Assert.Equal("LS550909", Read(Line(rtl: true)).InvoiceNumber);
    }

    /// <summary>
    /// A line its maker calls right to left is read so, whatever its letters: "חשבונית מס'
    /// ABCDEFGHIJK12" — more Latin letters than Hebrew — keeps the geresh of its label in place,
    /// and the number is read.
    /// </summary>
    [Fact]
    public void ALineCalledRightToLeftIsReadSo()
    {
        var invoice = Read(Measured(751, true, ("ABCDEFGHIJK12", 300f, 378f), ("מס'", 382f, 400f), ("חשבונית", 404f, 446f)));

        Assert.Equal("ABCDEFGHIJK12", invoice.InvoiceNumber);
    }

    /// <summary>
    /// A value of two scripts is read in its label's line's direction: "שם הספק: ישראל Dell" on a
    /// line called left to right gives "ישראל Dell", the name's own order — read from its first
    /// strong character, the D, it was "Dell ישראל".
    /// </summary>
    [Fact]
    public void AValueIsReadInItsLinesDirection()
    {
        var query = new FieldQuery("supplier", "שם הספק");
        PdfTextLine Line(bool rtl) => Measured(700, rtl, ("Dell", 300f, 324f), ("ישראל", 328f, 358f), ("הספק:", 400f, 430f), ("שם", 434f, 446f));

        Assert.Equal("ישראל Dell", PdfFieldExtractor.Extract(new[] { L.Page(0, Line(rtl: false)) }, new[] { query })[0].Value);
        Assert.Equal("ישראל Dell", PdfFieldExtractor.Extract(new[] { L.Page(0, Line(rtl: true)) }, new[] { query })[0].Value);
    }

    /// <summary>
    /// The same value on the line under its label is read in that line's direction: "ישראל Dell".
    /// </summary>
    [Fact]
    public void AValueBelowItsLabelIsReadInItsOwnLinesDirection()
    {
        var query = new FieldQuery("supplier", "שם הספק");
        var page = L.Page(0,
            Measured(700, true, ("הספק:", 400f, 430f), ("שם", 434f, 446f)),
            Measured(686, false, ("Dell", 380f, 404f), ("ישראל", 408f, 438f)));

        Assert.Equal("ישראל Dell", PdfFieldExtractor.Extract(new[] { page }, new[] { query })[0].Value);
    }

    /// <summary>
    /// An English line with a Hebrew word at its visual start — "מקור" (original) before "Invoice
    /// No 4471" — reads left to right by its Latin letters: the number is the label's.
    /// </summary>
    [Fact]
    public void ALatinLineOpeningWithAHebrewWordReadsLeftToRight()
    {
        var invoice = Read(Measured(751, false, ("מקור", 44.5f, 68f), ("Invoice", 120f, 156f), ("No", 160f, 172f), ("4471", 176f, 200f)));

        Assert.Equal("4471", invoice.InvoiceNumber);
    }
}
