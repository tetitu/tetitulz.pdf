// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A line's trailing space is not part of the width it is aligned by, in either direction.
/// </summary>
/// <remarks>
/// In a right-to-left line bidi puts the trailing space at the visual left, so it is drawn first.
/// It was left out of the aligned width and still drawn from the line's start, which pushed every
/// word one space past the right edge: a form's <c>text&#160;&lt;br&gt;</c> lines stood out of their
/// column by a space.
/// </remarks>
public class TrailingSpaceAlignmentTests
{
    private static double XOfWord(TextDirection direction, string text)
    {
        var p = new Paragraph { Direction = direction };
        p.Runs.Add(new TextRun { Text = text });
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.XOf(PdfContentProbe.DecodeStreams(doc.ToPdf()), "Word");
    }

    [Fact]
    public void ARightToLeftLineEndsAtTheSameEdge_WithOrWithoutATrailingSpace()
    {
        double bare = XOfWord(TextDirection.RightToLeft, "Word");
        Assert.False(double.IsNaN(bare), "the word was not found");

        Assert.Equal(bare, XOfWord(TextDirection.RightToLeft, "Word "), 2);
        Assert.Equal(bare, XOfWord(TextDirection.RightToLeft, "Word   "), 2);
    }

    /// <summary>The control: left-to-right, where the space hangs off the right and never moved anything.</summary>
    [Fact]
    public void ALeftToRightLineIsUnchanged()
    {
        double bare = XOfWord(TextDirection.LeftToRight, "Word");

        Assert.Equal(bare, XOfWord(TextDirection.LeftToRight, "Word "), 2);
        // Potency: the two directions really do put the word at different edges.
        Assert.True(XOfWord(TextDirection.RightToLeft, "Word") > bare + 100, "the RTL line was not right-aligned");
    }
}
