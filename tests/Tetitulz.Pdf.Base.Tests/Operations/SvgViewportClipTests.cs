// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="SvgToPdfConverter"/> clips a nested <c>&lt;svg&gt;</c> and a used
/// <c>&lt;symbol&gt;</c> to their viewport (SVG 1.1 §14.3.3): the initial <c>overflow</c> is hidden.
/// </summary>
/// <remarks>
/// Batch 23 drew the viewport's content without a clip, so a <c>slice</c> — which fills the viewport
/// by overflowing it — or any content past the viewBox spilled over the drawing around it. The clip
/// is a <c>W n</c> path inside a <c>q</c>/<c>Q</c> pair that closes with the viewport.
/// </remarks>
public class SvgViewportClipTests
{
    [Fact]
    public void ANestedSvgClipsToItsViewport()
    {
        string[] lines = Lines(Content(@"<svg x=""10"" y=""20"" width=""40"" height=""40"" viewBox=""0 0 20 20""><rect width=""20"" height=""20""/></svg>"));

        int w = Array.IndexOf(lines, "W");
        Assert.True(w > 0, "no clip was set");
        Assert.Equal(new[] { 10f, 20, 40, 40 }, Numbers(lines[w - 1], "re"));
        Assert.Equal("n", lines[w + 1]);
        Assert.Equal("q", lines[w - 2]);
    }

    /// <summary>A symbol's clip is the use's viewport, 48 × 48 at (10, 10), not the viewBox's area inside it.</summary>
    [Fact]
    public void ASymbolClipsToTheUsesViewport()
    {
        string[] lines = Lines(Content(
            @"<defs><symbol id=""i"" viewBox=""0 0 24 12""><rect width=""24"" height=""12""/></symbol></defs>" +
            @"<use href=""#i"" x=""10"" y=""10"" width=""48"" height=""48""/>"));

        int w = Array.IndexOf(lines, "W");
        Assert.Equal(new[] { 10f, 10, 48, 48 }, Numbers(lines[w - 1], "re"));
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));   // and it is closed
    }

    /// <summary>
    /// Without a width and height the viewport is 100% of the enclosing one, whatever its viewBox
    /// (batch 50): 200 × 200 at (5, 5). The viewBox's size, 30 × 20, was taken for it.
    /// </summary>
    [Fact]
    public void AViewBoxAloneDoesNotSizeTheClip()
    {
        string[] lines = Lines(Content(@"<svg x=""5"" y=""5"" viewBox=""0 0 30 20""><rect width=""1"" height=""1""/></svg>"));

        int w = Array.IndexOf(lines, "W");
        Assert.True(w > 0, "no clip was set");
        Assert.Equal(new[] { 5f, 5, 200, 200 }, Numbers(lines[w - 1], "re"));
    }

    [Theory]
    [InlineData(@"overflow=""visible""")]
    [InlineData(@"style=""overflow: auto""")]
    public void AVisibleOverflowIsNotClipped(string overflow)
    {
        string[] lines = Lines(Content($@"<svg x=""10"" y=""20"" width=""40"" height=""40"" viewBox=""0 0 20 20"" {overflow}><rect width=""20"" height=""20""/></svg>"));

        Assert.DoesNotContain("W", lines);
        Assert.Contains(lines, l => l.EndsWith(" re", StringComparison.Ordinal));   // still drawn
    }

    /// <summary>
    /// A viewport with no width, height or viewBox is 100% of the enclosing one (batch 50), and clipped
    /// to it: 200 × 200 at (5, 5). Its size unknown, it was not clipped.
    /// </summary>
    [Fact]
    public void AViewportWithoutASizeIsClippedToAllOfTheEnclosingOne()
    {
        string[] lines = Lines(Content(@"<svg x=""5"" y=""5""><rect width=""10"" height=""10""/></svg>"));

        int w = Array.IndexOf(lines, "W");
        Assert.True(w > 0, "no clip was set");
        Assert.Equal(new[] { 5f, 5, 200, 200 }, Numbers(lines[w - 1], "re"));
    }

    /// <summary>
    /// Under a rotation the viewport is not axis-aligned: the clip is its four corners, mapped.
    /// rotate(90) takes (10, 0) to (0, 10) and (10, 20) to (−20, 10).
    /// </summary>
    [Fact]
    public void ARotatedViewportIsClippedByItsCorners()
    {
        string[] lines = Lines(Content(
            @"<g transform=""rotate(90)""><svg width=""10"" height=""20"" viewBox=""0 0 10 20""><rect width=""1"" height=""1""/></svg></g>"));

        int w = Array.IndexOf(lines, "W");
        Assert.Equal("h", lines[w - 1]);
        float[][] corners = { Numbers(lines[w - 5], "m"), Numbers(lines[w - 4], "l"), Numbers(lines[w - 3], "l"), Numbers(lines[w - 2], "l") };
        float[][] expected = { new[] { 0f, 0 }, new[] { 0f, 10 }, new[] { -20f, 10 }, new[] { -20f, 0 } };
        for (int i = 0; i < 4; i++)
        {
            Assert.Equal(expected[i][0], corners[i][0], 3);
            Assert.Equal(expected[i][1], corners[i][1], 3);
        }
    }

    /// <summary>The clip ends with the viewport: its Q comes before the next sibling draws, and every q has its Q.</summary>
    [Fact]
    public void TheClipEndsWithTheViewport()
    {
        string[] lines = Lines(Content(
            @"<svg x=""10"" y=""20"" width=""40"" height=""40"" viewBox=""0 0 20 20""><rect width=""20"" height=""20""/></svg>" +
            @"<rect x=""100"" width=""5"" height=""5""/>"));

        int w = Array.IndexOf(lines, "W");
        int close = Array.IndexOf(lines, "Q", w);
        int sibling = Array.FindIndex(lines, l => l.StartsWith("100.0000 ", StringComparison.Ordinal) && l.EndsWith(" re", StringComparison.Ordinal));
        Assert.True(close > w && close < sibling, "the viewport's clip must end before its sibling draws");
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
    }

    // ------------------------------------------------------------------ helpers

    private static float[] Numbers(string line, string op)
    {
        Assert.EndsWith(" " + op, line, StringComparison.Ordinal);
        return line.Substring(0, line.Length - op.Length - 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => float.Parse(p, CultureInfo.InvariantCulture)).ToArray();
    }

    private static string Content(string body)
    {
        byte[] pdf = SvgToPdfConverter.Convert(
            @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + body + "</svg>",
            new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
    }

    private static string[] Lines(string content) => content.Split('\n').Select(l => l.Trim()).ToArray();
}
