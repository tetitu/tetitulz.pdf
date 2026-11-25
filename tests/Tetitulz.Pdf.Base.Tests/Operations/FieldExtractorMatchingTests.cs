// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Core.Constants;
using Xunit;
using L = Tetitulz.Pdf.Base.Tests.Operations.InvoiceLayouts;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfFieldExtractor"/> where a line is not plain words read one way: a right-to-left
/// line keyed under its own direction, a value set in the label's own word, ties along one line,
/// an amount query whose line has no amount, glyphs of several characters, words without glyphs,
/// layouts with holes — and the budget that bounds a hostile page.
/// </summary>
public class FieldExtractorMatchingTests
{
    private const char CurlyApostrophe = (char)0x2019;

    private static FieldResult One(PdfPageTextLayout page, FieldQuery q)
        => PdfFieldExtractor.Extract(new[] { page }, new[] { q })[0];

    private static PdfTextLine Line(bool rtl, params PdfTextWord[] words)
    {
        float left = words.Min(w => w.X), right = words.Max(w => w.X + w.Width);
        return new PdfTextLine(string.Join(" ", words.Select(w => w.Text)), left, words[0].Y, right - left,
                               L.Height, words, rtl);
    }

    // A word built without glyphs, as a caller's own layout may hold.
    private static PdfTextWord Bare(string text, float x, float y)
        => new PdfTextWord(text, x, y, L.CharWidth * text.Length, L.Height, Array.Empty<PdfTextChar>());

    // ================================================================
    //  A /ReversedChars run's brackets
    // ================================================================

    /// <summary>
    /// A bracketed label on a Chromium-made Hebrew line — its glyphs from a <c>/ReversedChars</c>
    /// run, each decoding to its logical character — is found: the line is keyed with the brackets
    /// as drawn, so the bidi pass turns them back. Keyed with the logical brackets, the pass turned
    /// them round a second time, the key read <c>)נטו(</c>, and the label never matched.
    /// </summary>
    [Fact]
    public void ABracketedLabelFromAReversedRunMatches()
    {
        // Visual, left to right: "100", then the run ")וטנ(" "םוכס", its glyphs logical.
        PdfTextWord Run(float x, params string[] glyphs)
        {
            var chars = new List<PdfTextChar>();
            for (int i = 0; i < glyphs.Length; i++)
                chars.Add(new PdfTextChar(glyphs[i], x + i * L.CharWidth, 700, L.CharWidth, L.Height,
                                          glyphs[i][0] >= 'א' && glyphs[i][0] <= 'ת') { InReversedRun = true });
            return new PdfTextWord(string.Concat(glyphs), x, 700, L.CharWidth * glyphs.Length, L.Height, chars, isRtl: true);
        }
        var line = Line(true,
            L.VisualWord("100", 50, 700, false, "1", "0", "0"),
            Run(90, ")", "ו", "ט", "נ", "("),
            Run(130, "ם", "ו", "כ", "ס"));

        var r = One(L.Page(0, line), new FieldQuery("net", "סכום (נטו)"));

        Assert.Equal("100", r.Value);
    }

    // ================================================================
    //  A right-to-left line, keyed under its own direction
    // ================================================================

    /// <summary>
    /// A right-to-left line's visual string begins with its value. Resolved from there, a Latin
    /// value — or a curly apostrophe, which the classifier took for a Latin letter — made the line
    /// left to right, the geresh of <c>מס'</c> moved to the far end of its word, and a label
    /// ending in it stopped matching.
    /// </summary>
    [Theory]
    [InlineData('\'', "INV1001")]
    [InlineData(CurlyApostrophe, "1001")]
    public void AGereshEndedLabelMatchesOnARightToLeftLine(char geresh, string value)
    {
        var page = L.Page(0, L.Rtl(700, 500, "חשבונית", "מס" + geresh, value));

        var r = One(page, new FieldQuery("no", "חשבונית מס'"));

        Assert.Equal(value, r.Value);
        Assert.Equal(1f, r.Confidence);
    }

    /// <summary>The control: a left-to-right line is keyed as it always was.</summary>
    [Fact]
    public void ALeftToRightLineIsKeyedLeftToRight()
    {
        var page = L.Page(0, L.Ltr(700, 72, "Invoice", "No", "INV1001"));

        Assert.Equal("INV1001", One(page, new FieldQuery("no", "Invoice No")).Value);
    }

    // ================================================================
    //  A value set in the label's own word
    // ================================================================

    /// <summary>
    /// "Invoice #1001": the label ends inside the word holding the value, whose rest has no
    /// letters, so the rest is the value. Counted as label text, it would let the line below
    /// supply a date as the invoice number.
    /// </summary>
    [Fact]
    public void AValueSetInTheLabelsLastWordIsTheValue()
    {
        var page = L.Page(0,
            L.Ltr(700, 72, "Invoice", "#1001"),
            L.Ltr(686, 72, "Date:", "10/06/2026"));

        var r = One(page, new FieldQuery("no", "Invoice #"));

        Assert.Equal("1001", r.Value);
        Assert.Equal(1f, r.Confidence);
    }

    /// <summary>
    /// A label and its value drawn as one word are divided at the label, the colon between them
    /// left out: left to right, and right to left with the number drawn on the left.
    /// </summary>
    [Fact]
    public void ALabelAndItsValueSetAsOneWordAreDividedAtTheLabel()
    {
        Assert.Equal("118.00", One(L.Page(0, L.Ltr(700, 72, "Total:118.00")), new FieldQuery("t", "Total")).Value);

        var word = L.VisualWord("סה\"כ:1,180.00", 400, 700, rtl: true,
            "1", ",", "1", "8", "0", ".", "0", "0", ":", "כ", "\"", "ה", "ס");

        Assert.Equal("1,180.00", One(L.Page(0, Line(true, word)), new FieldQuery("t", "סה\"כ")).Value);
    }

    /// <summary>
    /// A word the label only ends inside is no label word to the other queries of the call: a
    /// query for "Invoice" alone reads "#1001" as its value instead of stopping before it.
    /// </summary>
    [Fact]
    public void AWordALabelOnlyEndsInsideIsNoLabelToTheOtherQueries()
    {
        var page = L.Page(0, L.Ltr(700, 72, "Invoice", "#1001"));

        var r = PdfFieldExtractor.Extract(new[] { page },
            new[] { new FieldQuery("no", "Invoice #"), new FieldQuery("ref", "Invoice") });

        Assert.Equal("1001", r[0].Value);
        Assert.Equal("#1001", r[1].Value);
    }

    // ================================================================
    //  Which match wins
    // ================================================================

    /// <summary>
    /// The currency printed after the grand total is the value's, not the line's: the total's
    /// line is as specific as the subtotal's, and Last takes it. Counted against the label, the
    /// currency made the earlier subtotal win. The two lines tie, which First shows.
    /// </summary>
    [Fact]
    public void ACurrencyAfterTheAmountDoesNotMakeItsLineLessSpecific()
    {
        var page = L.Page(0,
            L.Rtl(700, 500, "סה\"כ", L.Column, "1,000.00"),
            L.Rtl(680, 500, "סה\"כ", "לתשלום", L.Column, "1,053.00", "ש\"ח"));
        var q = new FieldQuery("total", "סה\"כ", "סה\"כ לתשלום") { ValueKind = FieldValueKind.Amount };

        q.Occurrence = FieldOccurrence.Last;
        Assert.Equal("1,053.00 ש\"ח", One(page, q).Value);

        q.Occurrence = FieldOccurrence.First;
        Assert.Equal("1,000.00", One(page, q).Value);
    }

    /// <summary>
    /// Two labels of one query on one line, equally good: First takes the one read first and Last
    /// the one read last, whichever order the query lists them in.
    /// </summary>
    [Theory]
    [InlineData("Due", "Net")]
    [InlineData("Net", "Due")]
    public void TiesAlongALineAreSettledInReadingOrder(string listedFirst, string listedSecond)
    {
        var page = L.Page(0, L.Ltr(700, 72, "Net", "900.00", L.Column, "Due", "1,053.00"));

        Assert.Equal("900.00", One(page, new FieldQuery("amt", listedFirst, listedSecond)).Value);
        Assert.Equal("1,053.00", One(page, new FieldQuery("amt", listedFirst, listedSecond)
        {
            Occurrence = FieldOccurrence.Last,
        }).Value);
    }

    /// <summary>
    /// "Total" inside "Subtotal:" comes first along the line and is as specific as "Total:", but a
    /// match on word boundaries wins.
    /// </summary>
    [Fact]
    public void AMatchOnWordBoundariesBeatsOneInsideAWord()
    {
        var page = L.Page(0, L.Ltr(700, 72, "Subtotal:", "100.00", L.Column, "Total:", "118.00"));

        Assert.Equal("118.00", One(page, new FieldQuery("t", "Total")).Value);
    }

    /// <summary>
    /// <see cref="FieldQuery.WholeWord"/>: "VAT" inside "Private" is no match, and a whole "VAT"
    /// still is. Without it the word inside counts, which is the control.
    /// </summary>
    [Fact]
    public void AWholeWordLabelIsNotMatchedInsideAWord()
    {
        var inside = L.Ltr(700, 72, "Private", "parking", L.Column, "100.00");
        var whole = L.Ltr(686, 72, "VAT", L.Column, "17.00");

        Assert.Equal("parking", One(L.Page(0, inside), new FieldQuery("vat", "VAT")).Value);
        Assert.Equal(-1, One(L.Page(0, inside), new FieldQuery("vat", "VAT") { WholeWord = true }).PageIndex);
        Assert.Equal("17.00", One(L.Page(0, inside, whole), new FieldQuery("vat", "VAT") { WholeWord = true }).Value);

        // Nor is the word a label to the call's other queries: an item's value reads through it.
        var item = L.Page(0, L.Ltr(700, 72, "Item:", "Private", "parking"));
        var r = PdfFieldExtractor.Extract(new[] { item },
            new[] { new FieldQuery("item", "Item"), new FieldQuery("vat", "VAT") { WholeWord = true } });
        Assert.Equal("Private parking", r[0].Value);
    }

    // ================================================================
    //  An amount query whose line holds no amount
    // ================================================================

    /// <summary>
    /// A VAT rate beside its label and the amount on the line below: the amount query reads the
    /// line below and keeps the rate in front of it, rather than take the rate alone, as found.
    /// As text the rate is the value, which is the control.
    /// </summary>
    [Fact]
    public void AnAmountRunWithoutAnAmountGivesWayToTheAmountBelow()
    {
        var page = L.Page(0,
            L.Rtl(700, 500, "מע\"מ", "17%"),
            L.Rtl(686, 500, "170.00"));

        var amount = One(page, new FieldQuery("vat", "מע\"מ") { ValueKind = FieldValueKind.Amount });
        Assert.Equal("17% 170.00", amount.Value);
        Assert.Equal(0.6f, amount.Confidence);

        var text = One(page, new FieldQuery("vat", "מע\"מ"));
        Assert.Equal("17%", text.Value);
        Assert.Equal(1f, text.Confidence);
    }

    /// <summary>
    /// With no amount anywhere near, what the label's line says is still the answer, at a
    /// confidence below every neighbour that holds an amount.
    /// </summary>
    [Fact]
    public void AnAmountQueryWithNoAmountNearbyKeepsItsLineAtLowConfidence()
    {
        var r = One(L.Page(0, L.Rtl(700, 500, "מע\"מ", "17%")),
                    new FieldQuery("vat", "מע\"מ") { ValueKind = FieldValueKind.Amount });

        Assert.Equal("17%", r.Value);
        Assert.Equal(0.3f, r.Confidence);
    }

    // ================================================================
    //  Glyphs of several characters, and words without glyphs
    // ================================================================

    /// <summary>
    /// "الإجمالي" (the total) with its lam-alef drawn as one glyph mapped to both letters: the
    /// glyph is appended reversed, so the line's reversal gives the letters back in order and the
    /// label matches.
    /// </summary>
    [Fact]
    public void ALigatureGlyphInALabelReadsInLogicalOrder()
    {
        var label = L.VisualWord("الإجمالي", 400, 700, rtl: true, "ي", "ل", "ا", "م", "ج", "لإ", "ا");
        var value = L.Word("100.00", 340, 700);

        Assert.Equal("100.00", One(L.Page(0, Line(true, value, label)), new FieldQuery("total", "الإجمالي")).Value);
    }

    /// <summary>A glyph of a letter and its point comes back in a value letter first.</summary>
    [Fact]
    public void AGlyphOfALetterAndItsPointComesBackLetterFirst()
    {
        string betDagesh = "ב" + (char)0x05BC;
        var value = L.VisualWord(betDagesh + "ית", 400, 700, rtl: true, "ת", "י", betDagesh);
        var label = L.Word("שם:", 460, 700);

        Assert.Equal(betDagesh + "ית", One(L.Page(0, Line(true, value, label)), new FieldQuery("name", "שם")).Value);
    }

    /// <summary>
    /// Words built without glyphs are read by their text — beside the label, below it, and in a
    /// run with a word that has glyphs. They were found as labels and read as empty values.
    /// </summary>
    [Fact]
    public void WordsWithoutGlyphsAreReadByTheirText()
    {
        var sameLine = new PdfTextLine("Total 118.00", 72, 700, 84, L.Height,
            new[] { Bare("Total", 72, 700), Bare("118.00", 120, 700) });
        var r = One(L.Page(0, sameLine), new FieldQuery("t", "Total"));
        Assert.Equal("118.00", r.Value);
        Assert.Equal(1f, r.Confidence);

        var label = new PdfTextLine("Total", 72, 700, 30, L.Height, new[] { Bare("Total", 72, 700) });
        var below = new PdfTextLine("118.00", 72, 686, 36, L.Height, new[] { Bare("118.00", 72, 686) });
        var r2 = One(L.Page(0, label, below), new FieldQuery("t", "Total") { Side = FieldValueSide.Below });
        Assert.Equal("118.00", r2.Value);
        Assert.Equal(0.6f, r2.Confidence);

        var mixed = new PdfTextLine("Total 118.00 ILS", 72, 700, 106, L.Height,
            new[] { Bare("Total", 72, 700), Bare("118.00", 120, 700), L.Word("ILS", 160, 700) });
        Assert.Equal("118.00 ILS", One(L.Page(0, mixed), new FieldQuery("t", "Total")).Value);
    }

    // ================================================================
    //  Layouts with holes
    // ================================================================

    /// <summary>
    /// A null line, a line without words, a null word and a null glyph are passed over — the
    /// spatial fallback included, which walks the whole page — and the value below the label is
    /// still found.
    /// </summary>
    [Fact]
    public void MalformedLayoutsWithNullLinesWordsAndGlyphsAreSkipped()
    {
        var label = L.Ltr(700, 72, "Total:");
        var noWords = new PdfTextLine("", 0, 0, 0, 0, null);
        var nullWord = new PdfTextLine("x", 300, 650, 6, L.Height, new[] { null, L.Word("x", 300, 650) });
        var glyphs = new List<PdfTextChar> { null };
        glyphs.AddRange(L.Word("118.00", 72, 686).Chars);
        var value = new PdfTextWord("118.00", 72, 686, 36, L.Height, glyphs);
        var below = new PdfTextLine("118.00", 72, 686, 36, L.Height, new[] { value });
        var page = new PdfPageTextLayout(0, new[] { label, null, noWords, nullWord, below });

        var r = One(page, new FieldQuery("t", "Total"));

        Assert.Equal("118.00", r.Value);
        Assert.Equal(0.6f, r.Confidence);
    }

    /// <summary>The public spatial queries pass over the same holes.</summary>
    [Fact]
    public void SpatialQueriesSkipMalformedLinesAndWords()
    {
        var anchor = L.Word("Total", 72, 700);
        var value = L.Word("118.00", 120, 700);
        var page = new PdfPageTextLayout(0, new[]
        {
            null,
            new PdfTextLine("", 0, 0, 0, 0, null),
            new PdfTextLine("Total 118.00", 72, 700, 84, L.Height, new[] { anchor, null, value }),
        });

        Assert.Same(value, PdfSpatialQuery.WordToTheRightOf(page, anchor));
        Assert.Single(PdfSpatialQuery.LinesInRect(page, 0, 690, 600, 30));
    }

    // ================================================================
    //  The work budget
    // ================================================================

    /// <summary>
    /// A page of label words with no values: every occurrence searches the page for a neighbour.
    /// A budget worth a few occurrences stops the call part-way and says so; the default finishes
    /// the same page, which is the control.
    /// </summary>
    [Fact]
    public void AHostilePageOfLabelsStopsAtTheWorkBudget()
    {
        var lines = Enumerable.Range(0, 300).Select(i => L.Ltr(10000 - i * 30, 72, "Total")).ToArray();
        var pages = new[] { L.Page(0, lines) };
        var q = new FieldQuery("t", "Total");

        var small = PdfFieldExtractor.Prepare(pages, new[] { q }, workUnits: 5000);
        small.Best(q);
        Assert.True(small.Incomplete);

        var ample = PdfFieldExtractor.Prepare(pages, new[] { q }, workUnits: PdfLimits.DefaultMaxFieldExtractionWork);
        ample.Best(q);
        Assert.False(ample.Incomplete);
    }

    /// <summary>
    /// A bracket that never closes sends the search for a label's qualifier to the line's end, and
    /// the search is paid for word by word: a budget the value's run alone fits in stops the call.
    /// The same line without the bracket finishes under it, the control.
    /// </summary>
    [Fact]
    public void AnUnclosedQualifiersSearchIsPaidForWordByWord()
    {
        var q = new FieldQuery("t", "Total");
        PdfFieldExtractor.Prepared Searched(string first)
        {
            var tokens = new[] { "Total", first }.Concat(Enumerable.Repeat("x", 3000)).ToArray();
            var prepared = PdfFieldExtractor.Prepare(new[] { L.Page(0, L.Ltr(700, 72, tokens)) }, new[] { q }, workUnits: 4500);
            prepared.Best(q);
            return prepared;
        }

        Assert.True(Searched("(x").Incomplete);
        Assert.False(Searched("x").Incomplete);
    }

    /// <summary>
    /// Checking a value read below its label for a column under it visits the page, and is paid
    /// for. Each label here costs two visits to read — the neighbour beside it, then below — and
    /// the check a third: a budget of two and a half reads every label and stops the call that
    /// checks them, as the caller that knows the value's use does.
    /// </summary>
    [Fact]
    public void CheckingAValueBelowForAColumnIsChargedToTheBudget()
    {
        const int labels = 150;
        var lines = Enumerable.Range(0, labels).SelectMany(i => new[]
        {
            L.Ltr(10000 - i * 40, 72, "Date"),
            L.Ltr(10000 - i * 40 - 13, 72, "01/01/2026"),
        }).ToArray();
        var pages = new[] { L.Page(0, lines) };
        var q = new FieldQuery("d", "Date");
        int budget = labels * lines.Length * 5 / 2;

        var read = PdfFieldExtractor.Prepare(pages, new[] { q }, budget);
        Assert.Equal(labels, read.All(q).Count(c => c.Result.Value == "01/01/2026"));
        Assert.False(read.Incomplete);

        var checkedToo = PdfFieldExtractor.Prepare(pages, new[] { q }, budget);
        checkedToo.Best(q, r => InvoiceValueParser.TryFindDate(r.Value, out _));
        Assert.True(checkedToo.Incomplete);
    }

    /// <summary>
    /// A label repeated inside one word is as many occurrences, and every one reads the same
    /// value: they are one candidate, plus the two at the word's ends, whose boundaries differ.
    /// </summary>
    [Fact]
    public void ALabelRepeatedInsideOneWordIsBoundedToFewCandidates()
    {
        var word = L.Word(string.Concat(Enumerable.Repeat("Total", 2000)), 72, 700);
        var pages = new[] { L.Page(0, Line(false, word)) };
        var q = new FieldQuery("t", "Total");

        var prepared = PdfFieldExtractor.Prepare(pages, new[] { q });

        Assert.InRange(prepared.All(q).Count, 1, 3);
        Assert.False(prepared.Incomplete);
    }
}

/// <summary>
/// <see cref="PdfLimits.MaxFieldExtractionWork"/> reaches the public call, which then says it
/// stopped short.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class FieldExtractorBudgetLimitTests
{
    [Fact]
    public void TheWorkBudgetLimitIsReadByThePublicCall()
    {
        var lines = Enumerable.Range(0, 300).Select(i => L.Ltr(10000 - i * 30, 72, "Total")).ToArray();
        var pages = new[] { L.Page(0, lines) };
        var queries = new[] { new FieldQuery("t", "Total") };
        try
        {
            PdfLimits.MaxFieldExtractionWork = 5000;
            PdfFieldExtractor.Extract(pages, queries);
            Assert.Contains("MaxFieldExtractionWork", PdfFieldExtractor.LastError, StringComparison.Ordinal);

            PdfLimits.ResetToDefaults();
            PdfFieldExtractor.Extract(pages, queries);
            Assert.Null(PdfFieldExtractor.LastError);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }
}
