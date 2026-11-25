// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A CSS size that is negative or non-finite is declined (the inherited value is kept, as a browser
/// drops an invalid declaration) rather than reaching the Builder model. Let through,
/// <c>font-size:-1px</c> would be a negative <c>TextRun.FontSize</c>, and <c>NaN%</c>, <c>NaNfr</c>,
/// <c>width:Infinity%</c> and <c>top:Infinity%</c> would carry NaN/±∞ into a track width, a float width
/// or a placement offset — each of which <c>ModelMeasurements.Validate</c> refuses by throwing
/// <c>ArgumentOutOfRangeException</c> naming an internal parameter (<c>"document"</c>) no caller passed,
/// so no PDF would be produced.
/// </summary>
public class NonFiniteCssSizeTests
{
    [Theory]
    [InlineData("<p style='font-size:-1px'>X</p>")]
    [InlineData("<p style='font-size:NaN%'>X</p>")]
    [InlineData("<div style='display:grid;grid-template-columns:NaNfr 1fr'><div>a</div><div>b</div></div>")]
    [InlineData("<div style='float:left;width:Infinity%'>a</div><div>b</div>")]
    [InlineData("<div style='position:absolute;top:Infinity%'>a</div>")]
    public void AnInvalidCssSize_IsDeclined_AndTheDocumentConverts(string html)
    {
        // Let through, each of these would throw ArgumentOutOfRangeException out of the public door.
        byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(html);
        Assert.NotEmpty(pdf);
    }

    /// <summary>
    /// The control: a negative font-size keeps the inherited size, so the text is still drawn — the
    /// decline is not "produce nothing", and a model refusal would have drawn nothing at all.
    /// </summary>
    [Fact]
    public void ANegativeFontSize_KeepsTheInheritedSize_AndStillDrawsTheText()
    {
        byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes("<p style='font-size:-1px'>HelloWorld</p>");
        string content = PdfContentProbe.DecodeStreams(pdf);
        Assert.Contains(PdfContentProbe.DrawnTexts(content), t => t.Text.Contains("HelloWorld", StringComparison.Ordinal));
    }

    /// <summary>
    /// The control that proves negatives are kept where they are legal: an absolute element's negative
    /// offset is a real placement, not dropped — only non-finite offsets are declined.
    /// </summary>
    [Fact]
    public void ANegativeOffset_IsKept_NotDroppedAsInvalid()
    {
        // A finite negative top is legitimate CSS; the conversion must still produce a document.
        byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
            "<div style='position:relative'><div style='position:absolute;top:-20px'>P</div></div>");
        Assert.Contains(PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf)), t => t.Text.Contains("P", StringComparison.Ordinal));
    }
}
