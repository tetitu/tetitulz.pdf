// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Forms;
using Tetitulz.Pdf.Base.Operations.Metadata;
using Xunit;
using Ex = Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The decisions in the small option and value types of <c>Base/Operations</c> that a sweep of the
/// files no earlier sweep named found nothing asserting.
/// </summary>
public class StragglerDecisionTests
{
    /// <summary>
    /// What a caller who sets nothing gets from form filling: Helvetica, the field's own
    /// <c>/DA</c> honoured, appearances generated, the direction found from the text.
    /// </summary>
    [Fact]
    public void FormFillOptions_Defaults()
    {
        var options = new FormFillOptions();
        Assert.Equal("Helvetica", options.FontName);
        Assert.True(options.HonorDefaultAppearance);
        Assert.True(options.GenerateAppearances);
        Assert.Equal(Core.Text.BaseDirection.Auto, options.BaseDirection);
    }

    /// <summary>What an output intent written from a bare spec says: a PDF/A-1 intent, condition "Custom".</summary>
    [Fact]
    public void OutputIntentSpec_Defaults()
    {
        var spec = new OutputIntentSpec();
        Assert.Equal("GTS_PDFA1", spec.Subtype);
        Assert.Equal("Custom", spec.OutputConditionIdentifier);
    }

    /// <summary>An annotation with no subtype is no annotation: null and empty are refused, a real one kept.</summary>
    [Fact]
    public void FormDataAnnotation_NeedsASubtype()
    {
        Assert.Throws<ArgumentNullException>(() => new FormDataAnnotation(null));
        Assert.Throws<ArgumentException>(() => new FormDataAnnotation(""));
        Assert.Equal("Highlight", new FormDataAnnotation("Highlight").Subtype);
    }

    /// <summary>A line "ab cd": glyphs 10 wide, 12 tall, 'a' at x 100 on baseline-box y 200.</summary>
    private static (string Text, Ex.PdfTextChar[] Map) TwoWords()
    {
        Ex.PdfTextChar Glyph(string c, float x) => new Ex.PdfTextChar(c, x, 200, 10, 12);
        var ab = new[] { Glyph("a", 100), Glyph("b", 110) };
        var cd = new[] { Glyph("c", 130), Glyph("d", 140) };
        var line = new Ex.PdfTextLine("ab cd", 100, 200, 50, 12, new[]
        {
            new Ex.PdfTextWord("ab", 100, 200, 20, 12, ab),
            new Ex.PdfTextWord("cd", 130, 200, 20, 12, cd),
        });
        return Ex.TextLineGlyphMap.Build(line);
    }

    /// <summary>
    /// The box of a span is the union of its glyphs' boxes, right and top edges included; a span
    /// running past the line's end is cut at the end; a span of the separator alone has no box.
    /// </summary>
    [Fact]
    public void TextLineGlyphMap_SpanBoxes()
    {
        var (text, map) = TwoWords();
        Assert.Equal("ab cd", text);

        Assert.True(Ex.TextLineGlyphMap.TryUnionBox(map, 1, 3, out float x0, out float y0, out float x1, out float y1));
        Assert.Equal((110f, 200f, 140f, 212f), (x0, y0, x1, y1));

        Assert.True(Ex.TextLineGlyphMap.TryUnionBox(map, 3, 50, out x0, out _, out x1, out _));
        Assert.Equal((130f, 150f), (x0, x1));

        Assert.False(Ex.TextLineGlyphMap.TryUnionBox(map, 2, 1, out _, out _, out _, out _));
    }

    /// <summary>
    /// A linearization state nobody has set yet is "not yet available" — not a claim either way
    /// about the file.
    /// </summary>
    [Fact]
    public void PdfLinearizationState_DefaultIsNotYetAvailable()
        => Assert.Equal(PdfLinearizationState.NotYetAvailable, default(PdfLinearizationState));
}
