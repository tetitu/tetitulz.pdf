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
/// A right-to-left script outside the BMP — Phoenician, U+10900 upward — laid out inside an
/// otherwise left-to-right paragraph.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> <c>PdfExporter.ContainsMultipleLevels</c> triggers the full
/// UAX #9 reorder on <i>any</i> high surrogate, because a cheap scan cannot tell an astral RTL
/// script from an emoji. Deleting that arm survived the whole suite, and GAPS.md §5 recorded it as
/// <b>unreachable rather than untested</b>: the Standard-14 set is Latin, and the injected-font
/// fixtures built format-4 cmaps, whose segments are 16-bit and therefore cannot carry a
/// supplementary code point at all. The guard was fine; the fixture could not express the input.
/// </para>
///
/// <para><b>What changed is the fixture, not the library.</b> <c>TrueTypeFontProgram</c> has read
/// format 12 all along; <c>MinimalTrueType</c> now emits one as soon as a code point leaves the
/// BMP. Nothing in the exporter moved.</para>
///
/// <para><b>Glyph ids are computed, not read back.</b> The builder assigns them in sorted
/// code-point order from 1 (glyph 0 being <c>.notdef</c>), so each word's ids are known from the
/// fixture's own rule — which also avoids depending on a cmap reader that only parses format 4.
/// </para>
/// </remarks>
public sealed class AstralRightToLeftTests
{
    // Four Phoenician letters: two "words" of two. U+10800–U+10FFF is right-to-left strong, which
    // UnicodeBidi.GetSupplementaryBidiClass answers for the whole range.
    private const string FirstWord = "\U00010900\U00010901";
    private const string SecondWord = "\U00010902\U00010903";

    private const string Hebrew = "אב";
    private const string Hebrew2 = "גד";

    /// <summary>The code points of <paramref name="text"/>, surrogate pairs combined.</summary>
    private static IEnumerable<int> CodePoints(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                yield return char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
                continue;
            }
            yield return text[i];
        }
    }

    /// <summary>
    /// The glyph ids <paramref name="word"/> gets in a font built over <paramref name="cover"/>.
    /// </summary>
    /// <remarks>
    /// <b>Derived from the builder's rule, not hard-coded.</b> <c>MinimalTrueType</c> assigns ids
    /// in sorted code-point order from 1, glyph 0 being <c>.notdef</c> — so every code point in the
    /// cover string shifts the numbering, including the space. Writing the ids out by hand got that
    /// wrong on the first attempt, in a way that failed both the astral case and its BMP control
    /// identically and so said nothing about either.
    /// </remarks>
    private static ushort[] GidsOf(string cover, string word)
    {
        var sorted = CodePoints(cover).Distinct().OrderBy(cp => cp).ToList();
        return CodePoints(word).Select(cp => (ushort)(sorted.IndexOf(cp) + 1)).ToArray();
    }

    private static PdfExportOptions WithFont(string cover) => new PdfExportOptions
    {
        AllowSystemFontSubstitution = false,
        FontProvider = _ => MinimalTrueType.Build(cover),
    };

    private static string Content(string text, string cover)
    {
        var doc = new Document();
        var section = new Section();
        // Left-to-right, so the embedded right-to-left run is the only source of an odd level —
        // which is exactly the case the pre-test arm decides.
        var p = new Paragraph { Alignment = TextAlignment.Start, Direction = TextDirection.LeftToRight };
        p.Runs.Add(new TextRun { Text = text });
        section.Children.Add(p);
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(WithFont(cover)));
    }

    /// <summary>x of the glyph run whose ids are exactly <paramref name="gids"/>.</summary>
    private static double XOfRun(string content, ushort[] gids)
    {
        var want = gids.OrderBy(g => g).ToArray();
        foreach (var run in PdfContentProbe.DrawnGlyphRuns(content))
        {
            var got = run.Gids.OrderBy(g => g).ToArray();
            if (got.Length == want.Length && got.SequenceEqual(want)) return run.X;
        }
        Assert.Fail($"no glyph run with ids [{string.Join(", ", gids)}] was drawn");
        return double.NaN;
    }

    /// <summary>
    /// The two astral words are reordered: the second one logically is drawn to the <b>left</b>,
    /// because UAX #9 L2 reverses the right-to-left run.
    /// </summary>
    [Fact]
    public void AnAstralRightToLeftRun_IsReorderedInsideALeftToRightParagraph()
    {
        string cover = FirstWord + SecondWord + " ";
        string c = Content($"{FirstWord} {SecondWord}", cover);

        Assert.True(XOfRun(c, GidsOf(cover, SecondWord)) < XOfRun(c, GidsOf(cover, FirstWord)),
            "the later word of a right-to-left run must be drawn further left; drawing them in " +
            "logical order means the reorder was skipped.");
    }

    /// <summary>
    /// The control that gives the expectation its shape: a <b>BMP</b> right-to-left run in the same
    /// paragraph reorders the same way. Without it, the assertion above states a rule this file
    /// invented rather than the one the renderer already follows.
    /// </summary>
    [Fact]
    public void ABmpRightToLeftRun_ReordersTheSameWay()
    {
        string cover = Hebrew + Hebrew2 + " ";
        string c = Content($"{Hebrew} {Hebrew2}", cover);

        Assert.True(XOfRun(c, GidsOf(cover, Hebrew2)) < XOfRun(c, GidsOf(cover, Hebrew)),
            "a BMP right-to-left run must reorder, which is the behaviour the astral case has to match.");
    }
}
