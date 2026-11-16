// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;
using F = Tetitulz.Pdf.TestHelpers.MarkAttachmentFont;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The Builder's exporter writes a run's mark attachment (GAPS.md §19c): with
/// <see cref="F"/>, a bet with qamats at 20 points is the qamats under a text rise of −1 (−50/1000
/// em), moved by <c>[-150 &lt;0002&gt; 450] TJ</c>, then the bet. It drew <c>&lt;00020001&gt; Tj</c>.
/// </summary>
public class MarkAttachmentTests
{
    [Fact]
    public void AMark_IsPlacedByItsAnchor()
    {
        Assert.Contains("-1 Ts\n[-150 <0002> 450] TJ\n0 Ts\n<0001> Tj", Content(new TextRun { Text = $"{F.Bet}{F.Qamats}", FontSize = 20 }));
    }

    /// <summary>A run already raised keeps its rise: the mark's is relative to it, and it is restored after.</summary>
    [Fact]
    public void AMarkInARaisedRun_IsPlacedRelativeToTheRunsRise()
    {
        string content = Content(new TextRun { Text = $"{F.Bet}{F.Qamats}", FontSize = 20, BaselineShift = 5 });
        Assert.Contains("4 Ts\n[-150 <0002> 450] TJ\n5 Ts\n<0001> Tj", content);
    }

    /// <summary>
    /// Letter spacing follows the letter with its point: under 2 points of character spacing the
    /// qamats takes the spacing back (100 thousandths at 20 points), and a right-aligned bet starts
    /// where it does without its point — the point adds neither width nor a gap. Counting a gap per
    /// glyph made the pointed word 2 points wider.
    /// </summary>
    [Fact]
    public void LetterSpacing_FollowsTheLetterWithItsMark()
    {
        string pointed = Content(new TextRun { Text = $"{F.Bet}{F.Qamats}", FontSize = 20, CharSpacing = 2 }, TextAlignment.Right);
        string bare = Content(new TextRun { Text = $"{F.Bet}", FontSize = 20, CharSpacing = 2 }, TextAlignment.Right);

        Assert.Contains("-1 Ts\n[-150 <0002> 550] TJ\n0 Ts\n<0001> Tj", pointed);
        Assert.Equal(TmX(bare), TmX(pointed));
    }

    /// <summary>The x of the first text matrix.</summary>
    private static string TmX(string content)
    {
        var m = System.Text.RegularExpressions.Regex.Match(content, @"1 0 0 1 (\S+) \S+ Tm");
        Assert.True(m.Success, "no text matrix was written");
        return m.Groups[1].Value;
    }

    private static string Content(TextRun run, TextAlignment alignment = TextAlignment.Left)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph { Alignment = alignment };
        p.Runs.Add(run);
        section.Children.Add(p);
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => F.Build(),
        }));
    }
}
