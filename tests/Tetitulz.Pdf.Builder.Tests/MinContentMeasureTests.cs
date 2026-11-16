// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="PdfExporter.MeasureMinContentWidth(Paragraph, double)"/> (batch 107): a paragraph's min-content width —
/// its longest word, as the layout that draws it breaks words — beside
/// <see cref="PdfExporter.MeasureShrinkToFitWidth(Paragraph, double)"/>, its whole unwrapped line. The HTML mapper's
/// flex items take their automatic minimum size from it.
/// </summary>
public class MinContentMeasureTests
{
    /// <summary>The min-content width of a three-word paragraph is the width of its longest word alone, and less than the whole line.</summary>
    [Fact]
    public void MinContent_IsTheLongestWord()
    {
        var exporter = new PdfExporter();
        var three = Para("tiny Supercalifragilisticexpialidocious tiny");
        var longest = Para("Supercalifragilisticexpialidocious");

        double min = exporter.MeasureMinContentWidth(three, 1000);
        double whole = exporter.MeasureShrinkToFitWidth(three, 1000);
        Assert.Equal(exporter.MeasureShrinkToFitWidth(longest, 1000), min, 2);
        Assert.True(min > 100 && whole > min + 20, $"min-content {min:F1} is not the longest word short of the whole line {whole:F1}");
    }

    /// <summary>The answer is capped at the band, and an empty paragraph measures 0.</summary>
    [Fact]
    public void MinContent_IsCappedAtTheBand_AndZeroForNothing()
    {
        var exporter = new PdfExporter();

        Assert.Equal(50, exporter.MeasureMinContentWidth(Para("Supercalifragilisticexpialidocious"), 50), 6);
        Assert.Equal(0, exporter.MeasureMinContentWidth(new Paragraph(), 1000), 6);
    }

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }
}
