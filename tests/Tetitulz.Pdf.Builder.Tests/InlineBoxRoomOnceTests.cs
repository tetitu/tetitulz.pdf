// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// An inline box's padding and border take room on a line once, however many pieces of the box fall
/// on it (Builder-2:3).
/// </summary>
/// <remarks>
/// <c>BoxRoomOnLine</c> answers "is this the box's first piece on the line?" from a per-line set of the
/// boxes added so far, kept in step with the pieces by <c>AddTracked</c>, not by scanning every piece
/// already on the line — O(n²) over a line of many boxed pieces. If that set drifted from
/// the pieces, a box's room would be charged once per piece and everything after the box would slide
/// right. The same box around one run and around several runs that share the box instance must leave
/// the marker after it at the same x — the box content is identical, so only a miscount could move it.
/// </remarks>
public class InlineBoxRoomOnceTests
{
    private static InlineBox Box() =>
        new InlineBox { Border = new Border(1, "#FF0000"), PaddingLeft = 6, PaddingRight = 6 };

    private static double MarkerX(params IInlineElement[] runs)
    {
        var p = new Paragraph();
        p.Runs.AddRange(runs);
        var section = new Section();
        section.Children.Add(p);
        var doc = new Document();
        doc.Sections.Add(section);
        string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
        return PdfContentProbe.DrawnTexts(content).First(d => d.Text == "Z").X;
    }

    [Fact]
    public void ABoxSpanningSeveralPieces_ChargesItsRoomOnce()
    {
        InlineBox one = Box();
        double xOne = MarkerX(new TextRun { Text = "AAAA", Box = one }, new TextRun { Text = "Z" });

        InlineBox many = Box();
        double xMany = MarkerX(
            new TextRun { Text = "A", Box = many }, new TextRun { Text = "A", Box = many },
            new TextRun { Text = "A", Box = many }, new TextRun { Text = "A", Box = many },
            new TextRun { Text = "Z" });

        // Same box, same four A's, so the marker after must land at one place. A per-piece miscount
        // would push xMany right by the box's room three more times.
        Assert.Equal(xOne, xMany, 3);
    }
}
