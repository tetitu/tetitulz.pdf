// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="PdfExporter.MeasureShrinkToFitWidth(Paragraph, double)"/>: the width a paragraph takes when it shrinks
/// to its content — CSS shrink-to-fit, min(max-content, available).
/// </summary>
/// <remarks>
/// <para><b>Public because <c>HtmlToPdf</c> needs it and cannot see this assembly's internals.</b>
/// A <c>float</c> with no declared width needs CSS shrink-to-fit's measurement pass, without which
/// it would fall back to a two-cell band; this is that pass, answered
/// by the layout that will draw the float, so the reserved width holds exactly the lines that were
/// measured.</para>
///
/// <para><b>Asserted against the exporter's own advance widths</b>, not against numbers typed here:
/// the claim is that the measure IS the layout's, and a constant would pin a value rather than
/// the relationship.</para>
/// </remarks>
public sealed class ShrinkToFitMeasureTests
{
    /// <summary>One line of text measures its advance width, plus the documented slack.</summary>
    [Fact]
    public void ALineMeasuresItsAdvanceWidthPlusTheSlack()
    {
        double advance = PdfExporter.AdvanceWidth("LOGO", bold: false, italic: false, size: 12);

        double measured = new PdfExporter().MeasureShrinkToFitWidth(Para("LOGO"), 400);

        Assert.Equal(advance + PdfExporter.ShrinkToFitSlackPt, measured, 3);
    }

    /// <summary>The widest of several forced lines wins — max-content, not the first line.</summary>
    [Fact]
    public void TheWidestForcedLineWins()
    {
        Paragraph para = Para("short");
        para.Runs.Add(new LineBreak());
        para.Runs.Add(new TextRun { Text = "a considerably longer second line" });

        double longer = PdfExporter.AdvanceWidth("a considerably longer second line", false, false, 12);

        Assert.Equal(longer + PdfExporter.ShrinkToFitSlackPt,
            new PdfExporter().MeasureShrinkToFitWidth(para, 400), 3);
    }

    /// <summary>Text wider than the band answers the band: CSS caps shrink-to-fit at the available width.</summary>
    [Fact]
    public void ALineWiderThanTheBandAnswersTheBand()
    {
        Paragraph para = Para(string.Join(" ", System.Linq.Enumerable.Repeat("running text", 40)));

        Assert.Equal(200, new PdfExporter().MeasureShrinkToFitWidth(para, 200));
    }

    /// <summary>The answer is the box: indents are added, because the float renderer takes them off again.</summary>
    [Fact]
    public void TheParagraphsIndentsAreAdded()
    {
        Paragraph para = Para("LOGO");
        para.IndentLeft = 10;
        para.IndentRight = 5;

        double plain = new PdfExporter().MeasureShrinkToFitWidth(Para("LOGO"), 400);

        Assert.Equal(plain + 15, new PdfExporter().MeasureShrinkToFitWidth(para, 400), 3);
    }

    /// <summary>A bordered paragraph is drawn wider than its band by the box inset on each side.</summary>
    [Fact]
    public void ABorderedParagraphAddsTheBoxInset()
    {
        Paragraph para = Para("LOGO");
        para.BorderLeft = new Border(1, "#000000");

        double plain = new PdfExporter().MeasureShrinkToFitWidth(Para("LOGO"), 400);

        // Two points a side, the inset the paragraph renderer paints its box with.
        Assert.Equal(plain + 4, new PdfExporter().MeasureShrinkToFitWidth(para, 400), 3);
    }

    /// <summary>An image run counts at its own width, the way the block image renderer draws it.</summary>
    [Fact]
    public void AnImageRunReservesItsOwnWidth()
    {
        var para = new Paragraph();
        para.Runs.Add(new ImageRun { Data = new byte[] { 1, 2, 3 }, WidthPt = 80, HeightPt = 20 });

        Assert.Equal(80 + PdfExporter.ShrinkToFitSlackPt,
            new PdfExporter().MeasureShrinkToFitWidth(para, 400), 3);
    }

    /// <summary>Nothing to measure answers zero — the caller's cue to fall back rather than reserve nothing.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyParagraphMeasuresZero(string text)
        => Assert.Equal(0, new PdfExporter().MeasureShrinkToFitWidth(Para(text), 400));

    /// <summary>A trailing space before a forced break is not part of the width, as it is not of the drawn line.</summary>
    [Fact]
    public void ATrailingSpaceBeforeABreakIsNotCounted()
    {
        Paragraph trailing = Para("LOGO ");
        trailing.Runs.Add(new LineBreak());
        trailing.Runs.Add(new TextRun { Text = "x" });

        Assert.Equal(new PdfExporter().MeasureShrinkToFitWidth(Para("LOGO"), 400),
            new PdfExporter().MeasureShrinkToFitWidth(trailing, 400), 3);
    }

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }
}
