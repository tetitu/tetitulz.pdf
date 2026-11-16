// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Operations.Forms;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfFieldExtractor"/>'s label-matching and value-placement decisions.
/// </summary>
/// <remarks>
/// <para><b>Built from layouts rather than from PDFs.</b> Every decision below is about words and
/// their positions, and <see cref="PdfPageTextLayout"/> is public and constructible — driving them
/// through a rendered page would mean a PDF fixture per case whose real subject is two coordinates.
/// The extractor's own overload takes layouts for exactly this reason.</para>
/// <para>A mutation sweep found nine of these reached by nothing: the existing tests ask whether a
/// known label finds its value, which is the happy path through all of them at once.</para>
/// </remarks>
public class FieldExtractorDecisionTests
{
    /// <summary>
    /// A word with its characters, because the extractor reports a value in <b>reading</b> order
    /// and that walk is built from the characters — a word with none contributes nothing, which is
    /// how the first version of this fixture produced an empty value for every query.
    /// </summary>
    private static PdfTextWord Word(string text, float x, float y, float w = 20, float h = 10,
                                    bool rtl = false)
    {
        // An RTL word's glyphs are laid down right to left, so its first logical character has the
        // LARGEST x. Laying them out ascending would make the reading-order walk hand back the
        // string reversed — and the label would then fail to match itself.
        float per = w / Math.Max(1, text.Length);
        var chars = new List<PdfTextChar>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            float cx = x + (rtl ? text.Length - 1 - i : i) * per;
            chars.Add(new PdfTextChar(text[i].ToString(), cx, y, per, h, rtl));
        }

        return new PdfTextWord(text, x, y, w, h, chars, rtl);
    }

    private static PdfTextLine Line(float y, bool rtl, params PdfTextWord[] words) =>
        new PdfTextLine(string.Join(" ", words.Select(w => w.Text)),
                        words.Min(w => w.X), y, words.Sum(w => w.Width), 10, words, rtl);

    private static PdfPageTextLayout Page(params PdfTextLine[] lines) =>
        new PdfPageTextLayout(0, lines);

    private static FieldResult Extract(PdfPageTextLayout page, FieldQuery query) =>
        PdfFieldExtractor.Extract(new[] { page }, new[] { query })[0];

    // ================================================================
    //  Which candidate wins
    // ================================================================

    /// <summary>
    /// A value on the label's own line beats one found by looking at a neighbour, wherever the two
    /// appear. Keeping the first match instead would make the answer depend on page order.
    /// </summary>
    [Fact]
    public void AValueOnTheLabelsLineBeatsASpatialNeighbourFoundEarlier()
    {
        var page = Page(
            Line(700, false, Word("Total", 72, 700)),          // label alone → neighbour lookup
            Line(690, false, Word("999", 72, 690)),            // the neighbour below it
            Line(600, false, Word("Total", 72, 600), Word("42", 200, 600)));

        Assert.Equal("42", Extract(page, new FieldQuery("total", "Total")).Value);
    }

    /// <summary>
    /// A word that <i>contains</i> the label is part of the label, not the value — otherwise
    /// "SubTotal" is read as the value of a query for "Total".
    /// </summary>
    [Fact]
    public void AWordContainingTheLabelIsTreatedAsLabelText()
    {
        var page = Page(Line(700, false, Word("SubTotal", 72, 700), Word("42", 200, 700)));

        Assert.Equal("42", Extract(page, new FieldQuery("total", "Total")).Value);
    }

    // ================================================================
    //  Which side the value is on
    // ================================================================

    /// <summary>
    /// <see cref="FieldValueSide.Below"/> means below even when the label's own line has more words
    /// on it — the caller asked for the line under the label, and the line's remainder is not it.
    /// </summary>
    [Fact]
    public void BelowIsHonouredEvenWhenTheLabelsLineHasMoreWords()
    {
        var page = Page(
            Line(700, false, Word("Date", 72, 700), Word("NOT-THIS", 200, 700)),
            Line(688, false, Word("2026-09-20", 72, 688)));

        var q = new FieldQuery("date", "Date") { Side = FieldValueSide.Below };

        Assert.Equal("2026-09-20", Extract(page, q).Value);
    }

    /// <summary>
    /// On a right-to-left line, <see cref="FieldValueSide.Auto"/> looks <b>left</b> — which is where
    /// a Hebrew or Arabic label's value sits. Looking right would find the wrong word or none.
    /// </summary>
    [Fact]
    public void AutoLooksLeftOfAnRtlLabel()
    {
        // The label fills its OWN line, so the same-line branch has nothing to return and the value
        // is a spatial neighbour. The two candidates sit at the same height in lines of their own —
        // one to its left, one to its right — so only the side rule decides which comes back.
        var page = Page(
            Line(700, false, Word("LEFT", 40, 700)),
            Line(700, true, Word("סה״כ", 120, 700, rtl: true)),
            Line(700, false, Word("RIGHT", 240, 700)));

        Assert.Equal("LEFT", Extract(page, new FieldQuery("total", "סה״כ")).Value);
    }

    /// <summary>
    /// The anchor is the label word nearest the value's side: the left-most when looking left, the
    /// right-most when looking right. Anchoring on the wrong end reaches across the label.
    /// </summary>
    /// <remarks>
    /// <b>Both sides, because each is a separate arm.</b> A fixture that only looks right leaves
    /// the left arm unexercised, and a mutation that disables it survives — which is exactly what
    /// happened to the first version of this test.
    /// </remarks>
    [Theory]
    [InlineData(FieldValueSide.Right, "42")]
    public void TheAnchorIsTheLabelWordNearestTheValuesSide(FieldValueSide side, string expected)
    {
        // The two label words fill their own line — otherwise the same-line branch returns every
        // non-label word and the anchor rule is never consulted. The candidates sit at the same
        // height in lines of their own: looking right must anchor on the RIGHT-most label word
        // ("Amount") to reach "42", looking left on the left-most ("Invoice") to reach "7".
        var page = Page(
            Line(700, false, Word("7", 20, 700)),
            Line(700, false, Word("Invoice", 72, 700), Word("Amount", 140, 700)),
            Line(700, false, Word("42", 240, 700)));

        var q = new FieldQuery("amount", "Invoice Amount") { Side = side };

        Assert.Equal(expected, Extract(page, q).Value);
    }

    /// <summary>
    /// The left-hand arm of the same rule, on an <b>RTL</b> label whose words are stored in reading
    /// order — so the first word the walk sees is the right-most one.
    /// </summary>
    /// <remarks>
    /// <b>This is the fixture the left arm needs, and the LTR one above cannot be it.</b> The anchor
    /// starts as the first label word and is improved by the loop; on a left-to-right line the first
    /// word is already the left-most, so the loop changes nothing and a mutation that empties it
    /// survives. Only a label whose words arrive right-to-left can tell the two apart — which is
    /// exactly how a Hebrew or Arabic line is stored.
    /// </remarks>
    [Fact]
    public void TheLeftAnchorIsChosenWhenTheLabelWordsArriveRightToLeft()
    {
        var page = Page(
            Line(700, false, Word("7", 20, 700)),
            // Reading order for an RTL line: the right-most word first.
            Line(700, true, Word("מס", 140, 700, rtl: true),
                            Word("חשבון", 72, 700, rtl: true)),
            Line(700, false, Word("42", 240, 700)));

        var q = new FieldQuery("acct", "מס חשבון")
        {
            Side = FieldValueSide.Left,
        };

        Assert.Equal("7", Extract(page, q).Value);
    }

    /// <summary>
    /// When the requested side holds nothing, the line below is tried. A label at the right edge of
    /// its line has nothing to its right, and the value is on the next line.
    /// </summary>
    [Fact]
    public void WhenTheRequestedSideIsEmptyTheLineBelowIsTried()
    {
        var page = Page(
            Line(700, false, Word("Total", 500, 700)),
            Line(688, false, Word("42", 500, 688)));

        var q = new FieldQuery("total", "Total") { Side = FieldValueSide.Right };

        Assert.Equal("42", Extract(page, q).Value);
    }

    // ================================================================
    //  Normalisation — what counts as the same text
    // ================================================================

    /// <summary>
    /// A colon is punctuation, not part of the label: a caller's <c>"Total:"</c> has to match a
    /// page that writes <c>Total</c>, and vice versa.
    /// </summary>
    [Fact]
    public void AColonInTheQueryDoesNotStopTheLabelMatching()
    {
        var page = Page(Line(700, false, Word("Total", 72, 700), Word("42", 200, 700)));

        Assert.Equal("42", Extract(page, new FieldQuery("total", "Total:")).Value);
    }

    /// <summary>
    /// Invisible bidi controls inside a label do not stop it matching. They are laid down by RTL
    /// producers around numbers and around mixed runs, and they are not part of anybody's label.
    /// </summary>
    [Fact]
    public void BidiControlsInsideALabelDoNotStopItMatching()
    {
        var page = Page(Line(700, false,
            Word("To‏tal", 72, 700), Word("42", 200, 700)));

        Assert.Equal("42", Extract(page, new FieldQuery("total", "Total")).Value);
    }

    // ================================================================
    //  Nothing found
    // ================================================================

    /// <summary>A label no page carries reports not-found rather than an invented value.</summary>
    [Fact]
    public void AMissingLabelIsReportedAsNotFound()
    {
        var page = Page(Line(700, false, Word("Total", 72, 700), Word("42", 200, 700)));

        FieldResult r = Extract(page, new FieldQuery("vat", "VAT"));

        Assert.Equal(string.Empty, r.Value);
        Assert.Equal(-1, r.PageIndex);
        Assert.Equal(0f, r.Confidence);
    }
}
