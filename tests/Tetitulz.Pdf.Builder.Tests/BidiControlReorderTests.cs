// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A line of pure Latin text that a <b>bidi control</b> forces into right-to-left order — the one
/// case no script test can see.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> <c>ContainsMultipleLevels</c> is the cheap pre-test that
/// decides whether a left-to-right line needs the full UAX #9 reorder. Its own comment says it
/// "must recognise EVERY way an odd level can arise, not just RTL scripts", because "a wrong 'no'
/// here silently skips the reorder, and the fast path then looks like correct left-to-right output
/// rather than like a missing feature". A mutation sweep deleted its
/// <c>UnicodeBidi.IsBidiControl</c> arm and <b>all 438 Builder tests stayed green</b>.</para>
///
/// <para><b>Why the other arms do not cover it.</b> The script-range checks fire on Hebrew and
/// Arabic, and there is a separate arm for RLM/LRM. <c>IsBidiControl</c> covers U+202A–U+202E and
/// the U+2066–U+2069 isolates — the overrides and embeddings that impose a direction on text which
/// carries no directional character at all. Latin text inside an override is exactly the input for
/// which every other arm answers "no".</para>
///
/// <para><b>These read geometry, not extracted text</b>, for the reason
/// <see cref="BidiLineOrderTests"/> records: extraction re-applies bidi, so a document drawn in the
/// wrong order still extracts to the right string.</para>
/// </remarks>
public class BidiControlReorderTests
{
    /// <summary>U+202E RIGHT-TO-LEFT OVERRIDE … U+202C POP DIRECTIONAL FORMATTING.</summary>
    private const string Rlo = "‮";
    private const string Pop = "‬";
    /// <summary>U+2067 RIGHT-TO-LEFT ISOLATE … U+2069 POP DIRECTIONAL ISOLATE: an odd level whose Latin letters stay left-to-right (level 2), its own isolating run sequence (X10).</summary>
    private const string Rli = "⁧";
    private const string Pdi = "⁩";

    private static string Content(string text)
    {
        var doc = new Document();
        var section = new Section();
        // Left-to-right throughout: the override is the ONLY thing that can produce an odd level,
        // which is what puts the pre-test's bidi-control arm on the path.
        var p = new Paragraph { Alignment = TextAlignment.Start, Direction = TextDirection.LeftToRight };
        p.Runs.Add(new TextRun { Text = text });
        section.Children.Add(p);
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    private static double XOf(string content, string word)
    {
        double x = PdfContentProbe.XOf(content, word);
        Assert.False(double.IsNaN(x), $"'{word}' was never drawn");
        return x;
    }

    /// <summary>
    /// Under a right-to-left override the two words are drawn in reverse visual order, even though
    /// every character is Latin and the paragraph is left-to-right — and each word's letters too
    /// (L2 reverses characters, as <c>&lt;bdo dir="rtl"&gt;</c> does in a browser): <c>agemo</c> left
    /// of <c>ahpla</c>. The letters were left in order while the override character sat inside the
    /// word's piece; cut off at its level change, it no longer shields them.
    /// </summary>
    [Fact]
    public void AnOverrideReversesPureLatinWords()
    {
        string c = Content($"{Rlo}alpha omega{Pop}");

        Assert.True(XOf(c, "agemo") < XOf(c, "ahpla"),
            "inside a right-to-left override the later word must be drawn further left; " +
            "drawing them in logical order means the reorder was skipped.");
    }

    /// <summary>
    /// The acceptance control: the identical text without the override keeps reading order. Without
    /// this, the test above would also pass on an exporter that reversed every line.
    /// </summary>
    [Fact]
    public void WithoutTheOverride_TheSameWordsKeepReadingOrder()
    {
        string c = Content("alpha omega");

        Assert.True(XOf(c, "alpha") < XOf(c, "omega"),
            "with no override the words must be drawn in reading order.");
    }

    /// <summary>
    /// The control characters themselves are never drawn: they are zero-width, no font has a glyph
    /// for them, and a substitute would land in <c>/ToUnicode</c> and break copy-paste and search.
    /// </summary>
    /// <remarks>
    /// <b>The comparison must be ordinal.</b> <c>Assert.DoesNotContain(string, string)</c> defaults
    /// to a culture-sensitive search, which treats a bidi control as an <i>ignorable</i> character —
    /// so it reports a match inside any string at all and this test fails against correct output.
    /// A culture-sensitive search for a format character is never the question being asked.
    /// </remarks>
    [Fact]
    public void TheControlCharactersAreNotDrawn()
    {
        string c = Content($"{Rlo}alpha omega{Pop}");

        Assert.DoesNotContain(Rlo, c, StringComparison.Ordinal);
        Assert.DoesNotContain(Pop, c, StringComparison.Ordinal);
    }

    /// <summary>
    /// A left-to-right paragraph can hold a word of two levels too: inside a right-to-left
    /// isolate, <c>one two,</c> has its letters at level 2 and its comma — between them and the
    /// isolate's end, whose eos is R — at level 1 (N2), displayed left of the Latin run. (An
    /// embedding would not show it: the resolver keeps an embedding inside its run sequence, GAPS.md §4a.) Cut at that change,
    /// <c>two</c> stays right of <c>one</c>; whole, the piece <c>two,</c> was placed where its comma
    /// is displayed, left of <c>one</c>. Latin and controls only, so no Hebrew font is needed.
    /// </summary>
    [Fact]
    public void AWordOfTwoLevelsIsCutInALeftToRightParagraphToo()
    {
        string c = Content($"start {Rli}one two,{Pdi} end");
        Assert.True(XOf(c, "one") < XOf(c, "two"), "the Latin run inside the embedding lost its order");
        Assert.True(XOf(c, ",") < XOf(c, "one"), "the comma is not at the run's left, on the embedding's side");
        Assert.True(XOf(c, "start") < XOf(c, ",") && XOf(c, "two") < XOf(c, "end"));
    }
}
