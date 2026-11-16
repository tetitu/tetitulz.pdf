// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Visual ordering of bidirectional lines — where each word is actually <b>drawn</b>.
///
/// <para><b>These assert on geometry, never on extracted text.</b>
/// <c>Core.Text.PdfTextExtractor.ExtractText</c> re-applies bidi per line, so a document drawn in
/// the wrong visual order extracts to the right logical string: an ordering assertion built on it
/// passes with the renderer's bidi deleted. Every case here reads the <c>Tm</c> coordinates the
/// renderer emitted.</para>
///
/// <para>Host-independent: the covering font is injected, so the same assertions hold on a bare
/// container with no Hebrew font installed.</para>
/// </summary>
public class BidiLineOrderTests
{
    private const string Shalom = "שלום";
    private const string Olam = "עולם";

    // Covers the Hebrew used below plus the Latin, so one program serves the whole document.
    private static PdfExportOptions WithFont() => new PdfExportOptions
    {
        AllowSystemFontSubstitution = false,
        FontProvider = _ => MinimalTrueType.Build(Shalom + Olam + "JohnSmithabcdefgHelo ."),
    };

    private static Document Doc(string text, TextDirection? dir = null,
        TextAlignment align = TextAlignment.Start)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph { Alignment = align };
        if (dir.HasValue) p.Direction = dir.Value;
        p.Runs.Add(new TextRun { Text = text });
        section.Children.Add(p);
        doc.Sections.Add(section);
        return doc;
    }

    private static string Content(Document doc) =>
        PdfContentProbe.DecodeStreams(doc.ToPdf(WithFont()));

    /// <summary>x of a Latin word, which draws through the simple-font path as a literal.</summary>
    private static double XOfWord(string content, string word)
    {
        double x = PdfContentProbe.XOf(content, word);
        Assert.False(double.IsNaN(x), $"'{word}' was never drawn");
        return x;
    }

    // The injected font's Unicode → glyph-id map, so a Hebrew word can be IDENTIFIED in the
    // output rather than assumed to be at a particular stream position. Glyph runs are emitted in
    // VISUAL order now, so "the first run in the stream" is the leftmost piece — which is exactly
    // the thing under test and therefore useless as a way to find a specific word.
    private static readonly Lazy<Dictionary<int, int>> Cmap = new(() =>
        MinimalTrueType.ReadCmap(MinimalTrueType.Build(Shalom + Olam + "JohnSmithabcdefgHelo .")));

    /// <summary>x of the glyph run drawing <paramref name="word"/>, found by its glyph ids.</summary>
    private static double XOfHebrew(string content, string word)
    {
        var want = word.Select(ch => (ushort)Cmap.Value[ch]).OrderBy(g => g).ToArray();
        foreach (var run in PdfContentProbe.DrawnGlyphRuns(content))
        {
            var got = run.Gids.OrderBy(g => g).ToArray();
            if (got.Length == want.Length && got.SequenceEqual(want)) return run.X;
        }
        Assert.Fail($"'{word}' was never drawn as a glyph run");
        return double.NaN;
    }

    /// <summary>
    /// A run whose own text opens an override — the control glued to the word, as a caller of the model
    /// may write it — draws the word in the override's direction: Hebrew under a left-to-right override
    /// has its letters left to right in logical order. The control is the same word with no override,
    /// drawn reversed, as right-to-left text is.
    /// </summary>
    [Fact]
    public void AnOverrideOpeningARunsOwnText_HoldsTheWordInItsDirection()
    {
        ushort[] Drawn(string text) => PdfContentProbe.DrawnGlyphRuns(Content(Doc(text))).Single().Gids;
        ushort[] logical = Shalom.Select(ch => (ushort)Cmap.Value[ch]).ToArray();

        Assert.Equal(logical, Drawn((char)0x202D + Shalom + (char)0x202C));
        Assert.Equal(logical.Reverse().ToArray(), Drawn(Shalom));
    }

    /// <summary>x of the n-th composite glyph run in stream (= visual, left-to-right) order.</summary>
    private static double XOfGlyphRun(string content, int index)
    {
        var runs = PdfContentProbe.DrawnGlyphRuns(content);
        Assert.True(runs.Count > index, $"expected at least {index + 1} glyph runs, found {runs.Count}");
        return runs[index].X;
    }

    // ================================================================ the headline defect

    /// <summary>
    /// A multi-word Latin phrase inside a right-to-left paragraph is <b>one</b> left-to-right
    /// run: its words keep reading order, and only the run as a whole sits left of the Hebrew.
    ///
    /// <para>This is the case blanket reversal gets wrong. Placing tokens right-to-left in
    /// logical order performs the level-1 reversal — which is why pure Hebrew and Hebrew plus a
    /// <i>single</i> Latin word came out right, and why the defect stayed invisible — but the
    /// embedded level-2 run must not be reversed, and "John Smith" was emitted as
    /// "Smith John".</para>
    /// </summary>
    [Fact]
    public void Rtl_MultiWordLatinRun_KeepsItsWordsInReadingOrder()
    {
        string c = Content(Doc($"{Shalom} John Smith", TextDirection.RightToLeft));

        Assert.True(XOfWord(c, "John") < XOfWord(c, "Smith"),
            "the embedded Latin run must not be word-reversed");
    }

    [Fact]
    public void Rtl_MultiWordLatinRun_SitsLeftOfTheHebrew()
    {
        string c = Content(Doc($"{Shalom} John Smith", TextDirection.RightToLeft));

        // The paragraph's first word belongs at the right in an RTL paragraph.
        Assert.True(XOfWord(c, "Smith") < XOfHebrew(c, Shalom),
            "the Hebrew that opens the paragraph belongs to the right of the Latin run");
    }

    [Fact]
    public void Rtl_ThreeWordLatinRun_KeepsAllThreeInOrder()
    {
        string c = Content(Doc($"{Shalom} abc def g", TextDirection.RightToLeft));

        double a = XOfWord(c, "abc"), d = XOfWord(c, "def"), g = XOfWord(c, "g");
        Assert.True(a < d && d < g, $"expected abc < def < g, got {a} / {d} / {g}");
    }

    // ================================================================ the cases that were right

    /// <summary>
    /// Pure Hebrew already rendered correctly, so this pins it: the rewrite must not "fix" what
    /// was not broken. The paragraph's first word is drawn rightmost.
    /// </summary>
    [Fact]
    public void Rtl_PureHebrew_FirstWordIsRightmost()
    {
        string c = Content(Doc($"{Shalom} {Olam}", TextDirection.RightToLeft));

        // Identified by glyph id, not by stream position: runs are emitted in visual order, so
        // "the first run" IS the leftmost piece and asserting on it would be circular.
        double first = XOfHebrew(c, Shalom), second = XOfHebrew(c, Olam);
        Assert.True(first > second,
            $"the paragraph's first Hebrew word belongs at the right ({first} vs {second})");
    }

    /// <summary>
    /// The other case that was accidentally correct: Hebrew plus a <i>single</i> Latin word. One
    /// embedded word has no internal order to get wrong, so token reversal happened to match
    /// UAX #9. Pinned so the rewrite cannot move it.
    /// </summary>
    [Fact]
    public void Rtl_HebrewPlusOneLatinWord_LatinSitsOnTheLeft()
    {
        string c = Content(Doc($"{Shalom} Helo", TextDirection.RightToLeft));

        Assert.True(XOfWord(c, "Helo") < XOfHebrew(c, Shalom),
            "a single embedded Latin word sits left of the Hebrew that precedes it");
    }

    // ================================================================ base direction

    /// <summary>
    /// Hebrew with no declared direction. Under <see cref="TextDirection.Auto"/> the first strong
    /// character decides (UAX #9 P2/P3), so the paragraph resolves right-to-left and its first
    /// word is drawn rightmost — where a left-to-right resolution would put it leftmost.
    /// </summary>
    [Fact]
    public void Auto_HebrewContent_ResolvesRightToLeft()
    {
        string c = Content(Doc($"{Shalom} {Olam}", TextDirection.Auto));

        Assert.True(XOfHebrew(c, Shalom) > XOfHebrew(c, Olam),
            "auto-detected Hebrew must read right-to-left");
    }

    [Fact]
    public void Auto_LatinContent_ResolvesLeftToRight()
    {
        // The control: Auto must not turn a Latin paragraph around.
        string c = Content(Doc("abc def", TextDirection.Auto));

        Assert.True(XOfWord(c, "abc") < XOfWord(c, "def"));
    }

    [Fact]
    public void SectionDirectionDefault_IsUnchanged_SoLatinDocumentsDoNotMove()
    {
        // Section.Direction still defaults to LeftToRight; only paragraphs that opt into Auto
        // get content detection. A Hebrew document with no direction at all keeps its previous
        // behaviour rather than silently changing for every existing caller.
        var doc = new Document();
        doc.Sections.Add(new Section());
        Assert.Equal(TextDirection.LeftToRight, doc.Sections[0].Direction);
    }

    // ================================================================ alignment

    [Fact]
    public void Rtl_DefaultAlignment_IsTheRightEdge()
    {
        string c = Content(Doc(Shalom, TextDirection.RightToLeft));
        double x = XOfHebrew(c, Shalom);

        // A short line right-aligned in a 595 pt page with 72 pt margins starts well right of centre.
        Assert.True(x > 300, $"expected a right-aligned start, got x={x}");
    }

    /// <summary>
    /// An explicit <c>Left</c> under RTL must be honoured. It could not be before: RTL forced
    /// <c>Right</c> for everything except <c>Center</c>, and the default was <c>Left</c>, so
    /// "the author asked for the left edge" and "nobody said" were the same value.
    /// </summary>
    [Fact]
    public void Rtl_ExplicitLeftAlignment_IsHonoured()
    {
        string c = Content(Doc(Shalom, TextDirection.RightToLeft, TextAlignment.Left));
        double x = XOfHebrew(c, Shalom);

        Assert.True(x < 100, $"expected the left margin, got x={x}");
    }

    [Fact]
    public void Rtl_EndAlignment_IsTheLeftEdge()
    {
        string c = Content(Doc(Shalom, TextDirection.RightToLeft, TextAlignment.End));
        double x = XOfHebrew(c, Shalom);

        Assert.True(x < 100, $"End under RTL is the left edge, got x={x}");
    }

    [Fact]
    public void Ltr_StartAlignment_IsTheLeftEdge()
    {
        string c = Content(Doc("abc", TextDirection.LeftToRight, TextAlignment.Start));
        Assert.True(XOfWord(c, "abc") < 100);
    }

    // ================================================================ justification

    /// <summary>
    /// Justification used to be disabled outright for right-to-left text (<c>!rtl &amp;&amp; …</c>).
    /// With placement running left-to-right in both directions the special case disappeared, so
    /// this is a feature that arrived by deleting a condition.
    /// </summary>
    [Fact]
    public void Rtl_Justified_StretchesTheLineToTheFullBand()
    {
        // Enough words to wrap, so the first line is a justifiable non-last line.
        string many = string.Join(" ", Enumerable.Repeat($"{Shalom} {Olam}", 14));
        var doc = Doc(many, TextDirection.RightToLeft, TextAlignment.Justify);
        string c = PdfContentProbe.DecodeStreams(doc.ToPdf(WithFont()));

        var runs = PdfContentProbe.DrawnGlyphRuns(c);
        Assert.True(runs.Count > 4, "fixture must wrap for justification to be observable");

        double firstBaseline = runs[0].Y;
        var firstLine = runs.Where(r => Math.Abs(r.Y - firstBaseline) < 0.01).ToList();
        Assert.True(firstLine.Count > 1, "the first line must hold several words");

        // A justified line reaches the left margin: its leftmost word starts at the band left.
        double leftmost = firstLine.Min(r => r.X);
        Assert.True(leftmost < 80, $"a justified RTL line should reach the left margin, got {leftmost}");
    }

    // ================================================================ wrapping

    /// <summary>
    /// The word that <b>opens a wrapped line</b> is drawn at that line's start, not at its end.
    ///
    /// <para>Layout appended the wrapping token to the logical text <i>before</i> closing the line,
    /// so the closed line's <c>CharEnd</c> swallowed it and the new line's <c>CharStart</c> began
    /// after it. <c>VisualPieceOrder</c> then resolved the new line's order over a range that
    /// excluded the very piece opening it; the piece was not found in <c>order</c> and fell through
    /// to the "there should be none" tail, which appends at the END of the display order — so the
    /// first word of line 2 was drawn at the far right of the band instead of at the left
    /// margin.</para>
    ///
    /// <para><b>Mixed content is mandatory here.</b> Pure Hebrew wrapping is accidentally
    /// unaffected — appending the first logical word rightmost is what an RTL line wants — which is
    /// exactly why <see cref="Rtl_Justified_StretchesTheLineToTheFullBand"/>, the only other
    /// wrapping bidi case, stayed green through the whole life of the defect.</para>
    /// </summary>
    [Fact]
    public void WrappedLine_FirstWordOpensTheLine_NotEndsIt()
    {
        // Latin filler long enough that "headline" no longer fits, then a mixed second line.
        string filler = string.Join(" ", Enumerable.Repeat("abcdefg", 9));
        string c = Content(Doc($"{filler} headline {Shalom} {Olam} then"));

        double headline = XOfWord(c, "headline"), then = XOfWord(c, "then");
        double shalom = XOfHebrew(c, Shalom), olam = XOfHebrew(c, Olam);

        // The fixture must actually have wrapped with all four on one line, or it proves nothing.
        double y = PdfContentProbe.YOf(c, "headline");
        Assert.True(Math.Abs(PdfContentProbe.YOf(c, "then") - y) < 0.01,
            "fixture must put 'headline' and 'then' on the same line");
        Assert.True(y < PdfContentProbe.YOf(c, "abcdefg"),
            "fixture must wrap: 'headline' belongs on a line below the filler");

        // An LTR line reading "headline <hebrew phrase> then" displays as
        // headline | עולם | שלום | then — the Hebrew run reverses, the Latin around it does not.
        Assert.True(headline < olam,
            $"'headline' opens the line, so it is leftmost (headline={headline}, olam={olam})");
        Assert.True(olam < shalom, $"the Hebrew run reverses (olam={olam}, shalom={shalom})");
        Assert.True(shalom < then, $"'then' closes the line (shalom={shalom}, then={then})");
    }

    // ================================================================ compatibility

    /// <summary>
    /// The emit rewrite touches every PDF the Builder produces, DocxToPdf included. A pure-Latin
    /// document must come out byte-identical (modulo the random trailer /ID) whichever path it
    /// takes, or the "fix" quietly moved every existing customer's text.
    /// </summary>
    [Fact]
    public void LtrDocument_IsUnaffectedByDirectionResolution()
    {
        var a = Doc("The quick brown fox jumps over the lazy dog", TextDirection.LeftToRight);
        var b = Doc("The quick brown fox jumps over the lazy dog");   // Start default

        Assert.Equal(PdfContentProbe.NormalizedRaw(a.ToPdf()), PdfContentProbe.NormalizedRaw(b.ToPdf()));
    }

    [Fact]
    public void PureLatinDocument_NeverResolvesBidi()
    {
        // Bidi resolution is lazy precisely so a Latin-only document does not pay for it. The
        // observable proxy: the output is identical with and without a direction that would
        // require resolution.
        var ltr = Doc("Plain latin text here");
        var auto = Doc("Plain latin text here", TextDirection.Auto);

        Assert.Equal(PdfContentProbe.NormalizedRaw(ltr.ToPdf()),
                     PdfContentProbe.NormalizedRaw(auto.ToPdf()));
    }
}
