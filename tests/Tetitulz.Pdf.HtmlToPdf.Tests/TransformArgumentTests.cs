// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A <c>transform</c> argument is read by the function it belongs to, in any CSS length unit, and a
/// non-finite result drops the transform rather than reaching the Builder model. A length was read only
/// in px or pt and a percentage as zero, so <c>scale(50%)</c> became a zero matrix that drew the block
/// nowhere and <c>translateX(1em)</c> did nothing; and <c>scale(1e200) scale(1e200)</c> composed an
/// infinite matrix that <c>ModelMeasurements</c> refused at export with an exception naming an argument
/// the caller never passed (2026-10-22).
/// </summary>
public class TransformArgumentTests
{
    private static string Content(string html, List<string>? reports = null)
    {
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = reports == null ? null : d => { lock (reports) reports.Add(d.Detail ?? ""); },
        });
        byte[] pdf = output.ToArray();
        Assert.NotEmpty(pdf);
        return PdfContentProbe.DecodeStreams(pdf);
    }

    private static double[] MatrixOf(string content)
    {
        var m = Regex.Match(content, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) cm");
        Assert.True(m.Success, "no cm operator was emitted");
        return Enumerable.Range(1, 6).Select(g => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture)).ToArray();
    }

    private static bool Dropped(List<string> reports) =>
        reports.Any(r => r.Contains("composes no others", StringComparison.Ordinal));

    // ------------------------------------------------------------------ the arguments read

    /// <summary><c>scale(50%)</c> is a half scale, not a zero matrix that draws the block nowhere.</summary>
    [Fact]
    public void ScaleAsAPercentage_IsAFraction()
    {
        var reports = new List<string>();
        double[] m = MatrixOf(Content("<p style='transform:scale(50%)'>X</p>", reports));

        Assert.InRange(m[0], 0.49, 0.51);
        Assert.InRange(m[3], 0.49, 0.51);
        Assert.False(Dropped(reports));
    }

    /// <summary>A translate length is read in any unit, not only px or pt: <c>translateX(1em)</c> shifts by the em.</summary>
    [Theory]
    [InlineData("translateX(1em)", 12)]
    [InlineData("translateX(2cm)", 56.7)]
    [InlineData("translateX(1in)", 72)]
    public void ATranslateLength_IsReadInAnyUnit(string transform, double expected)
    {
        double[] m = MatrixOf(Content($"<p style='transform:{transform}'>X</p>"));

        // The matrix is written about the block's origin, so the e carries the origin's share too; the
        // point is that the shift is near the length, not the zero the px/pt-only reader produced.
        Assert.InRange(Math.Abs(m[4]), expected * 0.5, expected * 2);
    }

    // ------------------------------------------------------------------ the non-finite drop

    /// <summary>
    /// A transform whose composed matrix overflows to infinity is dropped and reported, and the
    /// conversion returns instead of throwing an exception naming the document.
    /// </summary>
    [Theory]
    [InlineData("scale(1e200) scale(1e200)")]
    [InlineData("rotate(NaN)")]
    [InlineData("scale(1e309)")]
    public void ANonFiniteTransform_IsDroppedAndReported_NotThrown(string transform)
    {
        var reports = new List<string>();
        string content = Content($"<p style='transform:{transform}'>X</p>", reports);

        Assert.DoesNotContain(" cm\n", content);
        Assert.True(Dropped(reports));
    }

    /// <summary>A percentage translate, whose box is not known at parse time, is dropped and reported — not a silent zero shift.</summary>
    [Fact]
    public void APercentageTranslate_IsDroppedAndReported()
    {
        var reports = new List<string>();
        string content = Content("<p style='transform:translate(-50%,-50%)'>X</p>", reports);

        Assert.DoesNotContain(" cm\n", content);
        Assert.True(Dropped(reports));
    }

    /// <summary>
    /// The control: an ordinary transform in points is applied and reports nothing, so the drops above
    /// are not a rule that always fires.
    /// </summary>
    [Fact]
    public void AnOrdinaryTransform_IsApplied_AndReportsNothing()
    {
        var reports = new List<string>();
        string content = Content("<p style='transform:translateX(10pt)'>X</p>", reports);

        Assert.Contains(" cm\n", content);
        Assert.False(Dropped(reports));
    }

    // ------------------------------------------------------------------ flex non-finite

    /// <summary>A <c>flex-grow</c> that overflows the track to infinity does not reach the model: the conversion returns.</summary>
    [Theory]
    [InlineData("flex-grow:1e306")]
    [InlineData("flex:1e309")]
    public void AnOverflowingFlexGrow_IsDroppedNotThrown(string style)
    {
        byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
            $"<div style='display:flex'><div style='{style}'>a</div><div>b</div></div>");

        Assert.NotEmpty(pdf);
    }
}
