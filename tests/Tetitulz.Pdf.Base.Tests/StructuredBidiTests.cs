// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// P1 — opt-in bidi in the structured extractor.  Verifies that a mixed RTL line reads in logical
/// order with embedded numbers preserved when ApplyBidi is on, that the default output is unchanged,
/// and the edge cases (pure RTL, pure LTR, single char).
/// </summary>
public class StructuredBidiTests
{
    private const int Alef = 0x05D0, Bet = 0x05D1, Gimel = 0x05D2;

    // A line with Hebrew "ABC" laid out visually RTL on the right (alef right-most) and the number
    // "12" to its left — logical order is "ABC 12".
    private static byte[] MixedHebrewNumberPdf()
        => new InvoicePdfBuilder()
            .HebRunRtl(InvoicePdfBuilder.Hb(Alef, Bet, Gimel), xRight: 200, y: 700)
            .AsciiRun("12", x: 100, y: 700)
            .Build();

    private static PdfTextLine FirstLine(byte[] pdf, bool applyBidi)
    {
        using var ms = new MemoryStream(pdf);
        var pages = PdfStructuredTextExtractor.Extract(ms, new WordAssemblyOptions { ApplyBidi = applyBidi });
        return pages[0].Lines.First();
    }

    [Fact]
    public void ApplyBidi_MixedLine_NumbersNotReversed_AndHebrewLogical()
    {
        var line = FirstLine(MixedHebrewNumberPdf(), applyBidi: true);

        Assert.Contains("12", line.Text);          // digits kept in order
        Assert.DoesNotContain("21", line.Text);    // not reversed
        Assert.StartsWith(InvoicePdfBuilder.Hb(Alef), line.Text); // Hebrew in logical order (alef first)
    }

    [Fact]
    public void DefaultNoBidi_MixedRtlLine_ReversesEmbeddedNumber_RegressionLock()
    {
        // Locks the legacy geometric behaviour: a majority-RTL line sorted descending by X reverses
        // the embedded number to "21".  This is exactly what ApplyBidi fixes above.
        var line = FirstLine(MixedHebrewNumberPdf(), applyBidi: false);
        Assert.Contains("21", line.Text);
    }

    /// <summary>
    /// An invoice row drawn the way Chromium draws one — the amount and its ₪ at the visual left,
    /// the Hebrew description at the right — reads back description first. Core's classifier typed
    /// ₪ strong L, so the line resolved left to right and came back amount first, as every item
    /// row of an outside producer's invoices did (GAPS.md §4a). (An integer amount: the builder
    /// steps every character 6 pt, which opens a word gap after Helvetica's narrow period.)
    /// </summary>
    [Fact]
    public void ApplyBidi_ARowWithTheShekelAtItsLeft_ReadsDescriptionFirst()
    {
        byte[] pdf = new InvoicePdfBuilder()
            .Shekel(100, 700)
            .AsciiRun("600", x: 110, y: 700)
            .HebRunRtl(InvoicePdfBuilder.Hb(Alef, Bet, Gimel), xRight: 200, y: 700)
            .Build();

        var line = FirstLine(pdf, applyBidi: true);

        Assert.Equal(InvoicePdfBuilder.Hb(Alef, Bet, Gimel) + " 600 " + (char)0x20AA, line.Text);
    }

    // "(אבג)" the way Chromium draws it: one /ReversedChars run, drawn left to right, each glyph
    // decoding to its logical character — the closing bracket at the visual left, the opening one at
    // the right.
    private static byte[] BracketedReversedRunPdf()
        => new InvoicePdfBuilder()
            .ReversedChars(b => b
                .Ascii(')', 113, 700)
                .HebRun(InvoicePdfBuilder.Hb(Gimel, Bet, Alef), x: 116, y: 700)
                .Ascii('(', 137, 700))
            .Build();

    /// <summary>
    /// Brackets in a <c>/ReversedChars</c> run read the right way round, with ApplyBidi and without.
    /// Under ApplyBidi the bidi algorithm's L4 turned the logical brackets round a second time and
    /// they faced out; read in place, right to left, they were and are right.
    /// </summary>
    [Fact]
    public void ReversedChars_BracketsReadTheRightWayRound_WithAndWithoutBidi()
    {
        byte[] pdf = BracketedReversedRunPdf();
        string expected = "(" + InvoicePdfBuilder.Hb(Alef, Bet, Gimel) + ")";

        Assert.Equal(expected, FirstLine(pdf, applyBidi: true).Text.Replace(" ", ""));
        Assert.Equal(expected, FirstLine(pdf, applyBidi: false).Text.Replace(" ", ""));
    }

    /// <summary>
    /// The plain text extractor reads the same run with its brackets the right way round: it
    /// always reorders a line through the bidi algorithm, and turned them round as the structured
    /// extractor did.
    /// </summary>
    [Fact]
    public void ReversedChars_ThePlainExtractorReadsTheBracketsTheRightWayRound()
    {
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(BracketedReversedRunPdf()));

        string text = Tetitulz.Pdf.Core.Text.PdfTextExtractor.ExtractText(doc);

        Assert.Equal("(" + InvoicePdfBuilder.Hb(Alef, Bet, Gimel) + ")", text.Replace(" ", "").Trim());
    }

    [Fact]
    public void ApplyBidi_PureHebrew_StaysInLogicalOrder()
    {
        byte[] pdf = new InvoicePdfBuilder()
            .HebRunRtl(InvoicePdfBuilder.Hb(Alef, Bet, Gimel), xRight: 200, y: 700)
            .Build();
        var line = FirstLine(pdf, applyBidi: true);
        Assert.StartsWith(InvoicePdfBuilder.Hb(Alef), line.Text);
        Assert.Equal(InvoicePdfBuilder.Hb(Alef, Bet, Gimel), line.Text);
    }

    [Fact]
    public void ApplyBidi_PureLatin_Unchanged()
    {
        byte[] pdf = new InvoicePdfBuilder().AsciiRun("Hi", x: 50, y: 700).Build();
        var line = FirstLine(pdf, applyBidi: true);
        Assert.Equal("Hi", line.Text);
    }

    [Fact]
    public void ApplyBidi_SingleHebrewChar_NoException()
    {
        byte[] pdf = new InvoicePdfBuilder().Heb((char)Alef, x: 100, y: 700).Build();
        var line = FirstLine(pdf, applyBidi: true);
        Assert.Equal(InvoicePdfBuilder.Hb(Alef), line.Text);
    }
}
