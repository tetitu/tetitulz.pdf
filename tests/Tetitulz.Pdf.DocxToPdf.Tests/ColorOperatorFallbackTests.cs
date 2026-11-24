// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// The colour operator a chart or equation emits for an OOXML hex value.
/// </summary>
/// <remarks>
/// <b>An unparseable value emits black, never nothing</b>: nothing is worse than the wrong
/// colour, since with no <c>rg</c> of its own the shape inherits whatever the content stream
/// painted last, so a chart series would draw in the PREVIOUS series' colour and look like a
/// legitimate chart with two bars the same. A guard against null alone
/// (<c>hex ?? "000000"</c>) that skips the append when the parse fails does exactly that.
///
/// <para><b>It is reachable through the theme palette.</b> <c>StyleResolver.LoadThemeColors</c>
/// stores the accent value out of <c>theme1.xml</c> without validating it and
/// <c>ChartLayout.PaletteAt</c> hands it straight to the fill, so a theme part whose accent is not
/// six hex digits is all it takes. Black is what the null guard intends and what OOXML
/// "automatic" means.</para>
///
/// <para>Asserted on the emitted operator text, which is what the defect is about — the absence of
/// an operator, not a wrong number in one.</para>
/// </remarks>
public class ColorOperatorFallbackTests
{
    [Theory]
    [InlineData(null)]        // the only case the old null guard covered
    [InlineData("")]
    [InlineData("auto")]      // OOXML's literal "automatic" value
    [InlineData("FF00")]      // too short
    [InlineData("ZZZZZZ")]    // right length, not hex
    [InlineData("accent1")]   // a theme name that reached the palette unresolved
    public void AnUnparseableFillColourStillEmitsAnOperatorAndItIsBlack(string? hex)
    {
        var ops = new StringBuilder();
        RunRenderer.AppendFill(ops, hex);

        Assert.Equal("0.000 0.000 0.000 rg\n", ops.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("auto")]
    [InlineData("ZZZZZZ")]
    public void AnUnparseableStrokeColourStillEmitsAnOperatorAndItIsBlack(string? hex)
    {
        var ops = new StringBuilder();
        RunRenderer.AppendStroke(ops, hex, 1.5);

        Assert.Equal("0.000 0.000 0.000 RG\n1.50 w\n", ops.ToString());
    }

    /// <summary>
    /// The control: a well-formed value is unchanged by the fallback, so the theories above are
    /// about unparseable input rather than about the conversion.
    /// </summary>
    [Fact]
    public void AWellFormedColourIsUnaffected()
    {
        var fill = new StringBuilder();
        RunRenderer.AppendFill(fill, "FF8000");
        Assert.Equal("1.000 0.502 0.000 rg\n", fill.ToString());

        var stroke = new StringBuilder();
        RunRenderer.AppendStroke(stroke, "0000FF", 0.75);
        Assert.Equal("0.000 0.000 1.000 RG\n0.75 w\n", stroke.ToString());
    }

    /// <summary>
    /// The failure the old code produced, stated as a property rather than as a story: whatever the
    /// input, an operator is emitted, so a shape can never inherit the previous one's colour.
    /// </summary>
    [Theory]
    [InlineData("auto")]
    [InlineData("112233")]
    public void AnOperatorIsAlwaysEmittedSoNoShapeInheritsThePreviousColour(string hex)
    {
        var ops = new StringBuilder();
        ops.Append("1.000 0.000 0.000 rg\n");   // a previous series, painted red
        RunRenderer.AppendFill(ops, hex);

        // Two operators, and the second one is the one that governs.
        Assert.EndsWith(" rg\n", ops.ToString());
        Assert.Equal(2, ops.ToString().Split(" rg\n").Length - 1);
    }
}
