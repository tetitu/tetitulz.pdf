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
/// <see cref="TextDirection.Auto"/> — a paragraph whose base direction is taken from its first
/// strong character rather than declared.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> <c>PdfExporter.ResolveBaseDirection</c> exists because three
/// sites each open-coded <c>== TextDirection.RightToLeft</c>, which silently answers
/// "left-to-right" for <see cref="TextDirection.Auto"/>; its doc comment says so. A mutation sweep
/// then rewrote the consolidated helper's own <c>Auto</c> arm to
/// <c>BaseDirection.LeftToRight</c> — reinstating exactly the defect the merge was performed to
/// remove — and <b>nothing reddened</b>. That is CLAUDE.md's rule 5: a merge whose survivor no
/// test reaches is a deletion, not a consolidation, so the surviving arm has to be pinned
/// directly.</para>
///
/// <para><b>These assert on geometry, never on extracted text</b>, for the reason
/// <see cref="BidiLineOrderTests"/> records: <c>PdfTextExtractor</c> re-applies bidi per line, so a
/// document drawn in the wrong visual order still extracts to the right logical string and an
/// assertion built on it passes with the renderer's bidi deleted.</para>
///
/// <para><b>Both directions are asserted, and that pairing is the point.</b> A Hebrew-first test
/// alone passes on an implementation that always resolves right-to-left; a Latin-first test alone
/// passes on the mutation. Only the two together say that the base direction <i>follows the
/// text</i>.</para>
///
/// <para>Host-independent: the covering font is injected, so these hold on a bare container with
/// no Hebrew font installed.</para>
/// </remarks>
public class AutoDirectionTests
{
    private const string Shalom = "שלום";
    private const string Cover = Shalom + "JohnSmithabcdefgHelo .";

    private static PdfExportOptions WithFont() => new PdfExportOptions
    {
        AllowSystemFontSubstitution = false,
        FontProvider = _ => MinimalTrueType.Build(Cover),
    };

    private static readonly Lazy<Dictionary<int, int>> Cmap =
        new(() => MinimalTrueType.ReadCmap(MinimalTrueType.Build(Cover)));

    /// <summary>A one-paragraph document whose direction is <paramref name="dir"/>.</summary>
    private static string Content(string text, TextDirection dir)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph { Alignment = TextAlignment.Start, Direction = dir };
        p.Runs.Add(new TextRun { Text = text });
        section.Children.Add(p);
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(WithFont()));
    }

    private static double XOfWord(string content, string word)
    {
        double x = PdfContentProbe.XOf(content, word);
        Assert.False(double.IsNaN(x), $"'{word}' was never drawn");
        return x;
    }

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
    /// Hebrew first: <see cref="TextDirection.Auto"/> resolves right-to-left, so the Latin run is
    /// drawn to the <b>left</b> of the Hebrew. Under a resolver that reads <c>Auto</c> as
    /// left-to-right the two swap sides.
    /// </summary>
    [Fact]
    public void Auto_HebrewFirst_LaysOutRightToLeft()
    {
        string c = Content($"{Shalom} John", TextDirection.Auto);

        Assert.True(XOfHebrew(c, Shalom) > XOfWord(c, "John"),
            "a paragraph whose first strong character is Hebrew must resolve right-to-left, " +
            "putting the Hebrew to the right of the embedded Latin run.");
    }

    /// <summary>
    /// Latin first: <see cref="TextDirection.Auto"/> resolves left-to-right, so the Hebrew sits to
    /// the <b>right</b>. This is the arm the mutation cannot fail, and it is what stops the test
    /// above from passing on "always right-to-left".
    /// </summary>
    [Fact]
    public void Auto_LatinFirst_LaysOutLeftToRight()
    {
        string c = Content($"John {Shalom}", TextDirection.Auto);

        Assert.True(XOfHebrew(c, Shalom) > XOfWord(c, "John"),
            "a paragraph whose first strong character is Latin must resolve left-to-right, " +
            "leaving the Hebrew to the right of the Latin.");
    }

    /// <summary>
    /// The contrast that gives <see cref="Auto_HebrewFirst_LaysOutRightToLeft"/> its meaning: the
    /// same Hebrew-first text declared <see cref="TextDirection.LeftToRight"/> puts the Hebrew on
    /// the left. <c>Auto</c> and an explicit direction therefore disagree about one string, which
    /// is only possible if <c>Auto</c> is resolved rather than folded into the default.
    /// </summary>
    [Fact]
    public void ExplicitLeftToRight_HebrewFirst_PutsTheHebrewOnTheLeft()
    {
        string c = Content($"{Shalom} John", TextDirection.LeftToRight);

        Assert.True(XOfHebrew(c, Shalom) < XOfWord(c, "John"),
            "an explicitly left-to-right paragraph must keep its logical order, " +
            "drawing the leading Hebrew word first.");
    }
}
