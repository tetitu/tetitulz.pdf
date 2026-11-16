// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Where a <see cref="TextAlignment.Justify"/> line starts when it is <b>not</b> being stretched —
/// the last line of a paragraph, or one with no spaces to distribute.
/// </summary>
/// <remarks>
/// <para><b>Why this file exists.</b> A mutation sweep over the PDF exporter swapped the arms of
/// <c>TextAlignment.Justify =&gt; rtl ? Right : Left</c>, so an unstretched justified line fell to
/// the <i>trailing</i> edge, and <b>all 438 Builder tests stayed green</b>. Justification is
/// covered for the lines that <i>are</i> stretched; the fallback that every paragraph's final line
/// takes was not.</para>
///
/// <para><b>What it looks like when wrong.</b> Every justified paragraph in the document ends with
/// its last line pushed to the opposite margin — a ragged-right block with one right-aligned line
/// under it. Nothing throws, the text is all present, and a test asserting that the paragraph was
/// drawn passes.</para>
///
/// <para><b>Asserted against the explicit alignments rather than against a coordinate.</b> Pinning
/// x = 72 would pin the default page margin as much as the fallback. Saying "an unstretched
/// justified line starts exactly where a Start-aligned one does, and not where an End-aligned one
/// does" survives any change to the page setup and still fails under the swap.</para>
/// </remarks>
public class JustifiedFallbackAlignmentTests
{
    /// <summary>Short enough that it can never fill the band, so it is never stretched.</summary>
    private const string Short = "Xy";

    private static double XOfLine(TextAlignment align, TextDirection dir)
    {
        var doc = new Document();
        var section = new Section();
        var p = new Paragraph { Alignment = align, Direction = dir };
        p.Runs.Add(new TextRun { Text = Short });
        section.Children.Add(p);
        doc.Sections.Add(section);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        double x = PdfContentProbe.XOf(content, Short);
        Assert.False(double.IsNaN(x), $"'{Short}' was never drawn");
        return x;
    }

    /// <summary>
    /// Left to right: the fallback is the <b>leading</b> edge, which is where Start puts it.
    /// </summary>
    [Fact]
    public void Ltr_AnUnstretchedJustifiedLine_StartsAtTheLeadingEdge()
    {
        double justify = XOfLine(TextAlignment.Justify, TextDirection.LeftToRight);
        double start = XOfLine(TextAlignment.Start, TextDirection.LeftToRight);
        double end = XOfLine(TextAlignment.End, TextDirection.LeftToRight);

        // The control: the two explicit alignments really do differ, so the comparison below
        // is capable of failing.
        Assert.True(end > start,
            $"End ({end}) must differ from Start ({start}) for this assertion to mean anything.");

        Assert.Equal(start, justify, precision: 3);
    }

    /// <summary>
    /// Right to left: the leading edge is the right one, and the fallback follows the direction
    /// rather than being hard-wired. Without this arm the test above passes on "Justify is always
    /// Left".
    /// </summary>
    [Fact]
    public void Rtl_AnUnstretchedJustifiedLine_StartsAtTheLeadingEdge()
    {
        double justify = XOfLine(TextAlignment.Justify, TextDirection.RightToLeft);
        double start = XOfLine(TextAlignment.Start, TextDirection.RightToLeft);
        double end = XOfLine(TextAlignment.End, TextDirection.RightToLeft);

        Assert.True(end < start,
            $"in a right-to-left paragraph End ({end}) must sit left of Start ({start}).");

        Assert.Equal(start, justify, precision: 3);
    }

    /// <summary>
    /// And the two directions disagree about where a justified line starts — the evidence that the
    /// fallback is resolved against the direction rather than being one fixed edge.
    /// </summary>
    [Fact]
    public void TheJustifiedFallbackFollowsTheParagraphDirection()
    {
        double ltr = XOfLine(TextAlignment.Justify, TextDirection.LeftToRight);
        double rtl = XOfLine(TextAlignment.Justify, TextDirection.RightToLeft);

        Assert.True(rtl > ltr,
            $"a right-to-left justified line ({rtl}) must start to the right of a " +
            $"left-to-right one ({ltr}).");
    }
}
