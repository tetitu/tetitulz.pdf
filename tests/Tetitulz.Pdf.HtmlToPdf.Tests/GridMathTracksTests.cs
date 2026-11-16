// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// Grid tracks sized by <c>min()</c>, <c>max()</c> and <c>clamp()</c> (CSS Values 4 §10.3, batch 128):
/// on columns over lengths and percentages of the container's width, on rows over lengths, and in an
/// <c>auto-fill</c> repeat's count. Before, every such function was reported by name and the column
/// left auto, the row template unread. An <c>fr</c> inside one, or a percentage in a row's, still is.
/// </summary>
/// <remarks>
/// The table shares the band out over the explicit tracks, so a track's size shows as a ratio between
/// columns: with two 100px (75pt) tracks beside the function track, the first two items' distance
/// over the second two's is the function's value over 75pt. Letter page, 28.8pt margins: the band is
/// 554.4pt, so 20% of it is 110.88pt.
/// </remarks>
public class GridMathTracksTests
{
    private const double Band = 612 - 2 * 28.8;

    private static string Grid(string first) =>
        $"<div style='display:grid;grid-template-columns:{first} 100px 100px'><div>AAA</div><div>BBB</div><div>CCC</div></div>";

    private static double Ratio(Laid laid) => (laid["BBB"].X - laid["AAA"].X) / (laid["CCC"].X - laid["BBB"].X);

    /// <summary><c>clamp(100px, 20%, 150px)</c> is its middle value, 20% of the band, between its bounds: 110.88pt against 75pt.</summary>
    [Fact]
    public void Clamp_TakesTheValueBetweenItsBounds()
    {
        var laid = Lay(Grid("clamp(100px, 20%, 150px)"));

        Assert.Equal(0.2 * Band / 75, Ratio(laid), 2);
        Assert.DoesNotContain(laid.Reports, r => r.Contains("not a function", StringComparison.Ordinal));
    }

    /// <summary><c>clamp(120px, 5%, 150px)</c> is its minimum, 90pt, when the value falls under it; <c>clamp(50px, 50%, 100px)</c> its maximum, 75pt, when the value runs over.</summary>
    [Fact]
    public void Clamp_ClampsToItsBounds()
    {
        Assert.Equal(90.0 / 75, Ratio(Lay(Grid("clamp(120px, 5%, 150px)"))), 2);
        Assert.Equal(75.0 / 75, Ratio(Lay(Grid("clamp(50px, 50%, 100px)"))), 2);
    }

    /// <summary><c>min()</c> is the smaller of its arguments and <c>max()</c> the larger: 75pt and 110.88pt of <c>100px</c> and <c>20%</c>.</summary>
    [Fact]
    public void Min_And_Max_TakeTheSmallerAndTheLarger()
    {
        Assert.Equal(1.0, Ratio(Lay(Grid("min(100px, 20%)"))), 2);
        Assert.Equal(0.2 * Band / 75, Ratio(Lay(Grid("max(100px, 20%)"))), 2);
    }

    /// <summary>An <c>fr</c> inside a math function — <c>clamp()</c>'s value, <c>min()</c>'s or <c>max()</c>'s argument — has no definite value here: the track is auto, reported by name — the acceptance control.</summary>
    [Theory]
    [InlineData("clamp(100px, 1fr, 150px)")]
    [InlineData("min(100px, 1fr)")]
    [InlineData("max(1fr, 100px)")]
    public void AnFrInsideAMathFunction_IsAutoAndReported(string track)
    {
        var laid = Lay(Grid(track));

        Assert.Contains(laid.Reports, r => r.Contains("not a function this engine sizes", StringComparison.Ordinal));
        Assert.True(Math.Abs(laid["AAA"].Y - laid["CCC"].Y) < 2.0, "the three items did not share a row");
    }

    /// <summary><c>repeat(auto-fill, clamp(100px, 18%, 150px))</c> counts the clamp's value, 99.79pt: five tracks fit the band, so the sixth item starts a second row.</summary>
    [Fact]
    public void AnAutoRepeat_CountsAMathTrack()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:repeat(auto-fill, clamp(100px, 18%, 150px))'>" +
                       string.Concat(Enumerable.Range(1, 6).Select(i => $"<div>I{i}</div>")) + "</div>");

        Assert.True(Math.Abs(laid["I1"].Y - laid["I5"].Y) < 2.0, "the first five did not share a row");
        Assert.True(laid["I6"].Y < laid["I1"].Y - 2.0, "the sixth item did not start a second row");
        Assert.DoesNotContain(laid.Reports, r => r.Contains("repeat(", StringComparison.Ordinal));
    }

    /// <summary>
    /// A row track of <c>clamp(20px, 40px, 100px)</c> is a least height of 30pt: the second row's text is 30pt
    /// below the first's. One of <c>clamp(20px, 5%, 100px)</c> is of a height the flow does not know, so the
    /// template is reported and not applied, as before (the control).
    /// </summary>
    [Fact]
    public void ARowTrack_TakesAMathFunctionOfLengths()
    {
        var laid = Lay("<div style='display:grid;grid-template-columns:100px 100px;grid-template-rows:clamp(20px, 40px, 100px) auto'>" +
                       "<div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div></div>");
        Assert.Equal(30.0, laid["AAA"].Y - laid["CCC"].Y, 1);
        Assert.DoesNotContain(laid.Reports, r => r.Contains("not a function", StringComparison.Ordinal));

        var percent = Lay("<div style='display:grid;grid-template-columns:100px 100px;grid-template-rows:clamp(20px, 5%, 100px) auto'>" +
                          "<div>AAA</div><div>BBB</div><div>CCC</div><div>DDD</div></div>");
        Assert.Contains(percent.Reports, r => r.Contains("grid-template-rows", StringComparison.Ordinal) && r.Contains("not a function", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------

    private sealed class Laid
    {
        public readonly Dictionary<string, (double X, double Y)> Words = new(StringComparer.Ordinal);
        public readonly List<string> Reports = new();
        public (double X, double Y) this[string word]
        {
            get
            {
                Assert.True(Words.ContainsKey(word), $"'{word}' was not laid out at all");
                return Words[word];
            }
        }
    }

    private static Laid Lay(string html)
    {
        var laid = new Laid();
        using var output = new MemoryStream();
        HtmlToPdfConverter.ConvertHtml(html, output, new HtmlToPdfOptions
        {
            RenderEngine = HtmlRenderEngine.Managed,
            OnDiagnostic = d => { lock (laid.Reports) laid.Reports.Add(d.Detail ?? ""); },
        });
        using var input = new MemoryStream(output.ToArray(), writable: false);
        foreach (var word in PdfOperations.ExtractStructuredText(input).SelectMany(p => p.Lines).SelectMany(l => l.Words))
            if (!laid.Words.ContainsKey(word.Text)) laid.Words[word.Text] = (word.X, word.Y);
        return laid;
    }
}
