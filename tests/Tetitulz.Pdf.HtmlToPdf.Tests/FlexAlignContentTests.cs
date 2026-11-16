// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

using static Tetitulz.Pdf.HtmlToPdf.Tests.ManagedTestSupport;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// <c>align-content</c> (Flexbox §8.4) on a wrapping container with a definite height, and
/// <c>align-items: baseline</c> (§8.3) across a flex line — read from the drawn baselines.
/// </summary>
public class FlexAlignContentTests
{
    private static string Container(string alignContent, string wrap = "wrap") =>
        $"<div style='display:flex;flex-wrap:{wrap};height:400px;{alignContent}'>" +
        "<div style='width:60%'>LINEONE</div><div style='width:60%'>LINETWO</div></div><p>AFTERWARDS</p>";

    private static (double One, double Two, string[] Reports) Lines(string alignContent, string wrap = "wrap")
    {
        var reports = new System.Collections.Generic.List<string>();
        var options = Managed();
        options.OnDiagnostic = d => { lock (reports) reports.Add(d.Detail ?? ""); };
        var drawn = DrawnTexts(DecodeStreams(ToPdf(Container(alignContent, wrap), options)));
        return (drawn.First(t => t.Text == "LINEONE").Y, drawn.First(t => t.Text == "LINETWO").Y, reports.ToArray());
    }

    /// <summary>
    /// Every value against <c>flex-start</c>, in units of half the free space (the shift <c>center</c> gives
    /// both lines): <c>flex-end</c> shifts both by two, <c>space-between</c> the second by two,
    /// <c>stretch</c> (the initial value) the second by one, <c>space-around</c> the first by a half and the
    /// second by one and a half, <c>space-evenly</c> two thirds and four thirds.
    /// </summary>
    [Fact]
    public void EachValueSharesTheFreeSpaceAsTheSpecificationSays()
    {
        var start = Lines("align-content:flex-start");
        var center = Lines("align-content:center");
        double half = start.One - center.One;
        Assert.True(half > 20, $"center moved the lines by {half:F1}");
        Assert.Equal(half, start.Two - center.Two, 1);

        void Shifts(string value, double one, double two)
        {
            var got = Lines(value);
            Assert.Equal(one * half, start.One - got.One, 1);
            Assert.Equal(two * half, start.Two - got.Two, 1);
            Assert.Empty(got.Reports);
        }
        Shifts("align-content:flex-end", 2, 2);
        Shifts("align-content:space-between", 0, 2);
        Shifts("", 0, 1);                                  // stretch, the initial value
        Shifts("align-content:stretch", 0, 1);
        Shifts("align-content:space-around", 0.5, 1.5);
        Shifts("align-content:space-evenly", 2.0 / 3, 4.0 / 3);
    }

    /// <summary><c>wrap-reverse</c> stacks the lines from the bottom: its <c>flex-start</c> packs them at the bottom, its <c>flex-end</c> at the top.</summary>
    [Fact]
    public void WrapReverseSwapsStartAndEnd()
    {
        var start = Lines("align-content:flex-start");
        var reversedStart = Lines("align-content:flex-start", "wrap-reverse");
        var reversedEnd = Lines("align-content:flex-end", "wrap-reverse");
        // Reversed, LINETWO's line is drawn first (on top); flex-end packs at the top, where flex-start packed before.
        Assert.Equal(start.One, reversedEnd.Two, 1);
        Assert.True(reversedStart.Two < reversedEnd.Two - 20, "wrap-reverse's flex-start did not pack the lines at the bottom");
    }

    /// <summary>
    /// A wrapping container holding one line still places it: <c>flex-end</c> puts the line at the bottom of the
    /// container's height, <c>flex-start</c> at the top.
    /// </summary>
    [Fact]
    public void AWrappingContainersOneLineIsPlacedToo()
    {
        static double Y(string alignContent) => DrawnTexts(DecodeStreams(ToPdf(
            $"<div style='display:flex;flex-wrap:wrap;height:300px;{alignContent}'><div style='width:40%'>ONLYLINE</div></div>")))
            .First(t => t.Text == "ONLYLINE").Y;
        Assert.True(Y("align-content:flex-start") - Y("align-content:flex-end") > 150, "flex-end did not move the one line to the bottom");
    }

    /// <summary>A single-line container (nowrap) is not affected by <c>align-content</c>.</summary>
    [Fact]
    public void NowrapIgnoresAlignContent()
    {
        var start = Lines("align-content:flex-start", "nowrap");
        var end = Lines("align-content:flex-end", "nowrap");
        Assert.Equal(start.One, end.One, 1);
    }

    /// <summary>A value this does not place is reported, the lines left at the start.</summary>
    [Fact]
    public void AnUnplacedValueIsReported()
    {
        var start = Lines("align-content:flex-start");
        var other = Lines("align-content:last baseline");
        Assert.Equal(start.One, other.One, 1);
        Assert.Contains(other.Reports, r => r.Contains("align-content: last baseline", System.StringComparison.Ordinal));
    }

    private static (double Big, double Small, double Padded) Baselines(string alignItems)
    {
        string html = $"<div style='display:flex;{alignItems}'>" +
                      "<div style='font-size:36px'>BIGWORD</div>" +
                      "<div style='font-size:10px'>SMALLWORD</div>" +
                      "<div style='font-size:10px;padding-top:30px;background:#eee'>PADDEDWORD</div></div>";
        var drawn = DrawnTexts(DecodeStreams(ToPdf(html)));
        return (drawn.First(t => t.Text == "BIGWORD").Y, drawn.First(t => t.Text == "SMALLWORD").Y, drawn.First(t => t.Text == "PADDEDWORD").Y);
    }

    /// <summary>
    /// <c>align-items: baseline</c> puts the items' first baselines on one line — a large word, a small one,
    /// and a small one under top padding; at <c>flex-start</c>, the control, they differ.
    /// </summary>
    [Fact]
    public void BaselineAlignsTheFirstBaselines()
    {
        var aligned = Baselines("align-items:baseline");
        Assert.Equal(aligned.Big, aligned.Small, 1);
        Assert.Equal(aligned.Big, aligned.Padded, 1);

        var top = Baselines("align-items:flex-start");
        Assert.True(top.Small > top.Big + 5, "at flex-start the small word should sit higher than the large one's baseline");
    }

    /// <summary>An item that is a box — a border, padding, two paragraphs — has its baseline under its top padding and border.</summary>
    [Fact]
    public void ABoxedItemsBaselineIsUnderItsPadding()
    {
        string html = "<div style='display:flex;align-items:baseline'>" +
                      "<div style='font-size:36px'>BIGWORD</div>" +
                      "<div style='font-size:10px;padding-top:30px;border:2px solid #000'><p style='margin:0'>BOXEDWORD</p><p style='margin:0'>second</p></div></div>";
        var drawn = DrawnTexts(DecodeStreams(ToPdf(html)));
        Assert.Equal(drawn.First(t => t.Text == "BIGWORD").Y, drawn.First(t => t.Text == "BOXEDWORD").Y, 1);
    }

    /// <summary>
    /// An item with no line of its own — an empty, sized box — takes the baseline its border box synthesizes
    /// (Flexbox §8.5): its bottom edge sits on the large word's baseline, where at <c>flex-start</c>, the
    /// control, it does not. (An item opening with a table or a list: <c>FlexTableBaselineTests</c>.)
    /// </summary>
    [Fact]
    public void AnItemWithNoLine_SitsOnTheBaselineByItsBottomEdge()
    {
        (double Word, double BoxBottom, string[] Reports) Lay(string align, string second)
        {
            var reports = new System.Collections.Generic.List<string>();
            var options = Managed();
            options.OnDiagnostic = d => { lock (reports) reports.Add(d.Detail ?? ""); };
            string html = $"<div style='display:flex;{align}'><div style='font-size:36px'>BIGWORD</div>{second}</div>";
            string content = DecodeStreams(ToPdf(html, options));
            double word = DrawnTexts(content).First(t => t.Text == "BIGWORD").Y;
            var rect = System.Text.RegularExpressions.Regex.Match(content, @"1 0 0 rg\s+(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re");
            double bottom = double.NaN;
            if (rect.Success)
            {
                double y = double.Parse(rect.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                double h = double.Parse(rect.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture);
                bottom = System.Math.Min(y, y + h);
            }
            return (word, bottom, reports.ToArray());
        }
        const string Empty = "<div style='width:40px;height:20px;background:#ff0000'></div>";

        var aligned = Lay("align-items:baseline", Empty);
        Assert.False(double.IsNaN(aligned.BoxBottom), "the box is drawn");
        Assert.Equal(aligned.Word, aligned.BoxBottom, 1);
        Assert.DoesNotContain(aligned.Reports, r => r.Contains("align-items: baseline", System.StringComparison.Ordinal));
        var top = Lay("align-items:flex-start", Empty);
        Assert.True(System.Math.Abs(top.Word - top.BoxBottom) > 5, "at flex-start the box's bottom is not on the baseline");

        // The border box's bottom, not the margin's: a bottom margin leaves the edge on the baseline.
        var margined = Lay("align-items:baseline", Empty.Replace("background:", "margin-bottom:12px;background:"));
        Assert.Equal(margined.Word, margined.BoxBottom, 1);
    }

    /// <summary>A box inside a box: the baseline is under both boxes' top padding and border.</summary>
    [Fact]
    public void ANestedBoxedItemsBaselineIsUnderBothPaddings()
    {
        string html = "<div style='display:flex;align-items:baseline'>" +
                      "<div style='font-size:36px'>BIGWORD</div>" +
                      "<div style='font-size:10px;padding-top:20px;border:2px solid #000'><div style='padding-top:15px;border:1px solid #000'><p style='margin:0'>NESTEDWORD</p></div></div></div>";
        var drawn = DrawnTexts(DecodeStreams(ToPdf(html)));
        Assert.Equal(drawn.First(t => t.Text == "BIGWORD").Y, drawn.First(t => t.Text == "NESTEDWORD").Y, 1);
    }

    /// <summary><c>align-self: baseline</c> takes one item onto the baseline of the others that are on it.</summary>
    [Fact]
    public void AlignSelfBaselineJoinsTheLinesBaseline()
    {
        string html = "<div style='display:flex'>" +
                      "<div style='font-size:36px;align-self:baseline'>BIGWORD</div>" +
                      "<div style='font-size:10px;align-self:baseline'>SMALLWORD</div>" +
                      "<div style='font-size:10px'>TOPWORD</div></div>";
        var drawn = DrawnTexts(DecodeStreams(ToPdf(html)));
        double big = drawn.First(t => t.Text == "BIGWORD").Y, small = drawn.First(t => t.Text == "SMALLWORD").Y, topWord = drawn.First(t => t.Text == "TOPWORD").Y;
        Assert.Equal(big, small, 1);
        Assert.True(topWord > small + 5, "the item not on the baseline moved with them");
    }
}
