// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Tab leaders — the dotted run that fills the gap a tab opens, as a table of contents draws it.
///
/// <para><b>What was wrong.</b> <see cref="TabStop.Leader"/> has existed on the model, and been
/// settable through <c>DocumentBuilder.AddTabStop</c>, for as long as tab stops have. The PDF
/// exporter never read it: <c>NextTab</c> returned a bare <c>double</c>, so by the time the tab had
/// been turned into a piece the <see cref="TabStop"/> it matched — and with it the leader and the
/// alignment — was gone. A caller could set the property, get no error, and get no dots.</para>
///
/// <para><b>What these assert.</b> The drawn content stream, not the model. A leader is real
/// characters on the page: they have to appear in the output, in the right quantity, and only
/// where a leader was asked for. Asserting on the model would pass with the exporter change
/// reverted, which is precisely the failure this file exists to rule out.</para>
/// </summary>
public class TabLeaderTests
{
    /// <summary>
    /// The claim, with its control: the identical paragraph with no leader draws no dots, so the
    /// assertion cannot be satisfied by punctuation from elsewhere in the document.
    /// </summary>
    [Fact]
    public void ADotLeader_FillsTheGapWithRealCharacters()
    {
        string withLeader = Content(TabLeader.Dot);
        string without    = Content(TabLeader.None);

        Assert.Contains("....", DrawnText(withLeader));
        Assert.DoesNotContain("..", DrawnText(without));
    }

    /// <summary>
    /// Each leader kind emits its own character and only its own. A single "fill it with
    /// something" implementation would satisfy a test written for <c>Dot</c> alone.
    /// </summary>
    [Theory]
    [InlineData(TabLeader.Dot, '.')]
    [InlineData(TabLeader.Hyphen, '-')]
    [InlineData(TabLeader.Underscore, '_')]
    public void EachLeaderKind_EmitsItsOwnCharacter(TabLeader leader, char expected)
    {
        Assert.Contains(new string(expected, 4), DrawnText(Content(leader)));
    }

    /// <summary>
    /// The leader must not move the text after it. One that over-filled would push the following
    /// word past the tab stop, which is worse than no leader at all — reaching the stop is the one
    /// thing a tab exists to guarantee.
    ///
    /// <para>Asserted on the drawn x of the word after the tab, which is a layout fact the model
    /// cannot state.</para>
    /// </summary>
    [Fact]
    public void TheLeaderDoesNotMoveTheTextAfterIt()
    {
        double withLeader = PdfContentProbe.XOf(Content(TabLeader.Dot), "7");
        double without    = PdfContentProbe.XOf(Content(TabLeader.None), "7");

        Assert.False(double.IsNaN(withLeader), "the text after the tab was never drawn");
        Assert.Equal(without, withLeader, 3);
    }

    /// <summary>
    /// The leader ends flush against the stop rather than starting flush against the text: the
    /// unfilled remainder goes first. A leader stopping short of the stop reads as a mistake; one
    /// starting a hair late does not.
    /// </summary>
    [Fact]
    public void TheLeaderEndsAtTheTabStop_NotAtTheTextBeforeIt()
    {
        string content = Content(TabLeader.Dot);

        // Located by its SHAPE rather than by an exact literal: the run is however many dots fit,
        // which is a function of the font metrics and the stop, so pinning a count here would make
        // the test a metrics assertion by accident.
        var leaderRun = PdfContentProbe.DrawnTexts(content)
            .FirstOrDefault(t => t.Text.Length > 1 && t.Text.All(c => c == '.'));
        Assert.NotNull(leaderRun);
        double dots = leaderRun!.X;
        double after = PdfContentProbe.XOf(content, "7");

        // The dots start after the left-hand text ends and finish where the following text begins.
        // "Chapter", not "Chapter One": the exporter tokenises a run into words, so a two-word
        // literal is never drawn as one and XOf would answer NaN — which compares false against
        // everything and would have made this assertion unfailable in the other direction.
        Assert.True(dots > PdfContentProbe.XOf(content, "Chapter"),
            "the leader was drawn before the text it should follow");
        Assert.True(dots < after, "the leader was drawn past the tab stop");
    }

    /// <summary>
    /// A tab landing on one of the implicit half-inch defaults has no <see cref="TabStop"/> and so
    /// no leader — the case <c>NextTabStop</c> returns null for. Without this, "always draw dots"
    /// would pass every case above.
    /// </summary>
    [Fact]
    public void ATabWithNoDeclaredStop_GetsNoLeader()
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "Left" });
        p.Runs.Add(new TabRun());
        p.Runs.Add(new TextRun { Text = "Right" });
        section.Children.Add(p);
        doc.Sections.Add(section);

        Assert.DoesNotContain("..", DrawnText(PdfContentProbe.DecodeStreams(doc.ToPdf())));
    }

    // ---------------------------- alignment ----------------------------
    //
    // Surfaced by the leader work rather than previously recorded: NextTab returned a bare double,
    // so the matched TabStop -- and with it BOTH its leader and its alignment -- was gone by the
    // time the tab became a piece. Every stop behaved as Left.

    /// <summary>
    /// A right-aligned stop puts the <b>end</b> of the following text on the stop. Asserted as
    /// "the text ends at 300" rather than "starts at 300", which is the entire difference between
    /// Right and Left and the only thing that distinguishes them in the output.
    /// </summary>
    [Fact]
    public void ARightAlignedStop_EndsTheFollowingTextAtTheStop()
    {
        string content = Content(TabAlignment.Right, TabLeader.None, tail: "7");

        var tail = PdfContentProbe.DrawnTexts(content).First(t => t.Text == "7");
        double endsAt = tail.X + WidthOf("7");

        Assert.InRange(endsAt, BandLeft() + 300 - 2, BandLeft() + 300 + 2);
    }

    /// <summary>
    /// A centred stop straddles it. Kept separate from Right so an implementation that subtracted
    /// the full segment width in both cases could not pass.
    /// </summary>
    [Fact]
    public void ACentredStop_StraddlesTheStop()
    {
        string content = Content(TabAlignment.Center, TabLeader.None, tail: "1234");

        var tail = PdfContentProbe.DrawnTexts(content).First(t => t.Text == "1234");
        double width = WidthOf("1234");
        double stop = BandLeft() + 300;
        double centre = tail.X + width / 2;

        Assert.InRange(centre, stop - 2, stop + 2);
        // And it really is centred rather than merely right-aligned: the start sits left of the
        // stop by about half the width, not the whole of it.
        Assert.True(tail.X < stop - width / 4, "the segment was not moved left at all");
        Assert.True(tail.X > stop - width, "the segment was right-aligned, not centred");
    }

    /// <summary>
    /// A left-aligned stop is unchanged — the behaviour every stop had, and the one that must not
    /// regress now that the others are honoured.
    /// </summary>
    [Fact]
    public void ALeftAlignedStop_StillStartsTheTextAtTheStop()
    {
        string content = Content(TabAlignment.Left, TabLeader.None, tail: "7");

        Assert.InRange(PdfContentProbe.XOf(content, "7"), BandLeft() + 300 - 2, BandLeft() + 300 + 2);
    }

    /// <summary>
    /// A decimal stop aligns on the separator, so two rows with different digit counts either side
    /// put their points in the same column. Asserted across two rows, because a single row cannot
    /// tell decimal alignment from right alignment.
    /// </summary>
    [Fact]
    public void ADecimalStop_LinesUpTheSeparatorAcrossRows()
    {
        double first = DecimalColumnOf("1.5");
        double second = DecimalColumnOf("1234.75");

        Assert.InRange(second - first, -2, 2);
    }

    /// <summary>
    /// Right alignment would put those two rows' <i>ends</i> together instead, so this is the
    /// control that stops the case above passing for a right-aligned implementation.
    /// </summary>
    [Fact]
    public void RightAlignmentWouldNotLineUpTheSeparator()
    {
        string a = Content(TabAlignment.Right, TabLeader.None, tail: "1.5");
        string b = Content(TabAlignment.Right, TabLeader.None, tail: "1234.75");

        double sepA = PdfContentProbe.XOf(a, "1.5") + WidthOf("1");
        double sepB = PdfContentProbe.XOf(b, "1234.75") + WidthOf("1234");

        Assert.True(Math.Abs(sepB - sepA) > 4,
            "right alignment happened to line the separators up too, so the decimal case above " +
            "is not distinguishing anything");
    }

    /// <summary>
    /// Alignment and a leader together. The leader is emitted in its place in the logical text —
    /// the reason the tab's width is measured by looking ahead rather than by widening a
    /// placeholder afterwards — so a contents page extracts as "Chapter One....7" and not as
    /// "Chapter One 7 ....".
    /// </summary>
    [Fact]
    public void ARightAlignedStopWithALeader_KeepsTheDotsBeforeTheNumber()
    {
        string content = Content(TabAlignment.Right, TabLeader.Dot, tail: "7");

        string drawn = DrawnText(content);
        int dots = drawn.IndexOf("....", StringComparison.Ordinal);
        int number = drawn.LastIndexOf('7');

        Assert.True(dots >= 0, "no leader was drawn for an aligned stop");
        Assert.True(dots < number, $"the leader was emitted after the number ({dots} vs {number})");
    }

    /// <summary>
    /// Text that already runs past the stop cannot be aligned to it. The tab collapses to a small
    /// visible gap rather than to nothing: a zero-width tab reads as a missing space.
    /// </summary>
    [Fact]
    public void TextOverrunningTheStop_CollapsesToAVisibleGap()
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Tabs.Add(new TabStop { PosPt = 40, Align = TabAlignment.Right });
        p.Runs.Add(new TextRun { Text = "AAAAAAAAAAAAAAAAAAAA" });
        p.Runs.Add(new TabRun());
        p.Runs.Add(new TextRun { Text = "BBBBBBBBBBBBBBBBBBBB" });
        section.Children.Add(p);
        doc.Sections.Add(section);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());

        double first = PdfContentProbe.XOf(content, "AAAAAAAAAAAAAAAAAAAA");
        double second = PdfContentProbe.XOf(content, "BBBBBBBBBBBBBBBBBBBB");
        Assert.True(second > first + WidthOf("AAAAAAAAAAAAAAAAAAAA"),
            "the overrunning segment was drawn on top of the text before it");
    }

    /// <summary>x where the decimal separator of <paramref name="number"/> falls.</summary>
    private static double DecimalColumnOf(string number)
    {
        string content = Content(TabAlignment.Decimal, TabLeader.None, tail: number);
        int at = number.IndexOf('.');
        return PdfContentProbe.XOf(content, number) + WidthOf(number.Substring(0, at));
    }

    /// <summary>
    /// The x a paragraph's text starts at with no tab involved — the band's left edge in page
    /// coordinates.
    /// </summary>
    /// <remarks>
    /// A tab stop's position is measured from the band, while a drawn glyph's x is absolute, so
    /// every assertion here is <c>BandLeft() + stop</c>. Writing the page margin in as a literal
    /// would make these tests fail the moment a default changed, and — worse — would look like an
    /// alignment bug when it happened.
    /// </remarks>
    private static double BandLeft()
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "I" });
        section.Children.Add(p);
        doc.Sections.Add(section);

        return PdfContentProbe.XOf(PdfContentProbe.DecodeStreams(doc.ToPdf()), "I");
    }

    /// <summary>
    /// Advance width of <paramref name="text"/> in the document's default face, from a
    /// right-aligned stop: the text ends at the stop, so its width is the stop minus its start.
    /// </summary>
    private static double WidthOf(string text)
    {
        const double Stop = 400;
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Tabs.Add(new TabStop { PosPt = Stop, Align = TabAlignment.Right });
        p.Runs.Add(new TextRun { Text = "x" });
        p.Runs.Add(new TabRun());
        p.Runs.Add(new TextRun { Text = text });
        section.Children.Add(p);
        doc.Sections.Add(section);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        return (BandLeft() + Stop) - PdfContentProbe.XOf(content, text);
    }

    // ----------------------------------------------------------------

    /// <summary>
    /// A leader is capped at 4 096 characters, however small its font: at 0.2pt a dot leader is the cap's length,
    /// and a two-character <see cref="TabStop.LeaderText"/> (2026-10-19) 2 048 repetitions, where the gap holds some
    /// 2 068 — the cap counts
    /// characters, not repetitions. Its control, a leader the gap holds well under the cap, is the gap's.
    /// </summary>
    [Fact]
    public void ALeader_IsCappedInCharacters()
    {
        static string Leader(TabStop stop, double size)
        {
            var doc = new Document();
            var section = new Section();
            var p = new Paragraph();
            p.Tabs.Add(stop);
            p.Runs.Add(new TabRun());
            p.Runs.Add(new TextRun { Text = "7", FontSize = size });
            section.Children.Add(p);
            doc.Sections.Add(section);
            // The leader is the longest literal drawn; the "7" after it is one character.
            return PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf())).Select(t => t.Text).OrderByDescending(t => t.Length).First();
        }

        // 460pt of the 468pt band; the smallest size drawn makes a dot about 0.11pt, so the gap holds over 4 000.
        Assert.Equal(4096, Leader(new TabStop { PosPt = 460, Leader = TabLeader.Dot }, 0.2).Length);
        string pairs = Leader(new TabStop { PosPt = 460, LeaderText = ".," }, 0.2);
        Assert.Equal(4096, pairs.Length);
        Assert.StartsWith(".,.,", pairs, StringComparison.Ordinal);
        Assert.InRange(Leader(new TabStop { PosPt = 460, LeaderText = ".," }, 10).Length, 20, 400);
    }

    private static string Content(TabLeader leader)
        => Content(TabAlignment.Left, leader, "7");

    private static string Content(TabAlignment align, TabLeader leader, string tail)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph();
        p.Tabs.Add(new TabStop { PosPt = 300, Align = align, Leader = leader });
        p.Runs.Add(new TextRun { Text = "Chapter One" });
        p.Runs.Add(new TabRun());
        p.Runs.Add(new TextRun { Text = tail });
        section.Children.Add(p);
        doc.Sections.Add(section);

        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    /// <summary>Every literal this page draws, concatenated — the text as it reaches the page.</summary>
    private static string DrawnText(string decodedContent) =>
        string.Concat(PdfContentProbe.DrawnTexts(decodedContent).Select(t => t.Text));
}
