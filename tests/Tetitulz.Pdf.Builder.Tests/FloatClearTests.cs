// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="Paragraph.ClearFloats"/> on a floated paragraph: it starts below the floats before it
/// (CSS 2.1 §9.5.2 — a float can clear), a second row of floats after a clearfix. Without it the
/// float starts beside them, at the same height (the control).
/// </summary>
public class FloatClearTests
{
    private static Paragraph Floated(string first, bool clear) => new Paragraph
    {
        Float = FloatSide.Left,
        FloatWidth = 100,
        ClearFloats = clear,
        SpacingAfter = 0,
        Runs =
        {
            new TextRun { Text = first }, new LineBreak(),
            new TextRun { Text = first + "2" }, new LineBreak(),
            new TextRun { Text = first + "3" },
        },
    };

    private static string Render(bool clear)
    {
        var section = new Section();
        section.Children.Add(Floated("AAA", clear: false));
        section.Children.Add(Floated("BBB", clear));
        var doc = new Document();
        doc.Sections.Add(section);
        return PdfContentProbe.DecodeStreams(doc.ToPdf());
    }

    [Fact]
    public void AFloatThatClearsStartsBelowTheFloatsBeforeIt()
    {
        string c = Render(clear: true);
        Assert.True(PdfContentProbe.YOf(c, "BBB") < PdfContentProbe.YOf(c, "AAA3"),
            $"BBB {PdfContentProbe.YOf(c, "BBB")}, AAA3 {PdfContentProbe.YOf(c, "AAA3")}");
    }

    [Fact]
    public void AFloatThatDoesNotClearStartsBesideThem()
    {
        string c = Render(clear: false);
        Assert.Equal(PdfContentProbe.YOf(c, "AAA"), PdfContentProbe.YOf(c, "BBB"), 1);
    }
}
