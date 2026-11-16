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
/// <see cref="SvgToPdfConverter"/> applies <c>clip-path</c> (SVG 1.1 §14.3.5): the referenced
/// <c>&lt;clipPath&gt;</c>'s shapes, as paths only, become the element's clip.
/// </summary>
/// <remarks>
/// It was reported and the element drawn unclipped: a photo frame, a masked avatar or a chart clipped
/// to its plot area spilled over. The clip is the clipPath's shapes' paths, in the element's user
/// space, then <c>W n</c> (<c>W* n</c> for <c>clip-rule="evenodd"</c>), inside a <c>q</c>/<c>Q</c> that
/// closes with the element.
/// </remarks>
public class SvgClipPathTests
{
    private const string Clip5 = @"<defs><clipPath id=""c""><rect width=""5"" height=""5""/></clipPath></defs>";

    [Fact]
    public void AClipPathClipsTheElement()
    {
        string[] lines = Lines(Content(Clip5 + @"<rect width=""10"" height=""10"" clip-path=""url(#c)""/>"));

        int w = Array.IndexOf(lines, "W");
        Assert.True(w > 0, "no clip was set");
        Assert.Equal(new[] { 0f, 0, 5, 5 }, Numbers(lines[w - 1], "re"));
        Assert.Equal("n", lines[w + 1]);
        Assert.Equal("q", lines[w - 2]);
        int element = Array.FindIndex(lines, w, l => l.EndsWith(" re", StringComparison.Ordinal));
        Assert.Equal(new[] { 0f, 0, 10, 10 }, Numbers(lines[element], "re"));
        Assert.Equal("f", lines[element + 1]);   // the element itself is painted, outside the clip's path mode
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
        Assert.Null(SvgToPdfConverter.LastError);
    }

    /// <summary>The clipPath's own transform applies to its shapes: translate(5, 0) moves the clip.</summary>
    [Fact]
    public void TheClipPathsTransformApplies()
    {
        string[] lines = Lines(Content(
            @"<defs><clipPath id=""c"" transform=""translate(5, 0)""><rect width=""5"" height=""5""/></clipPath></defs>" +
            @"<rect width=""10"" height=""10"" clip-path=""url(#c)""/>"));

        Assert.Equal(new[] { 5f, 0, 5, 5 }, Numbers(lines[Array.IndexOf(lines, "W") - 1], "re"));
    }

    /// <summary>A clip has no opacity: a translucent shape inside a clipPath sets no graphics state.</summary>
    [Fact]
    public void OpacityInsideAClipPathSetsNoState()
    {
        string[] lines = Lines(Content(
            @"<defs><clipPath id=""c""><rect width=""5"" height=""5"" opacity=""0.5""/></clipPath></defs>" +
            @"<rect width=""10"" height=""10"" clip-path=""url(#c)""/>"));

        Assert.Contains("W", lines);
        Assert.DoesNotContain(lines, l => l.EndsWith(" gs", StringComparison.Ordinal));
    }

    /// <summary>The clip is in the element's user space: its transform moves the clip with it.</summary>
    [Fact]
    public void TheClipIsInTheElementsUserSpace()
    {
        string[] lines = Lines(Content(Clip5 + @"<rect width=""10"" height=""10"" transform=""translate(10, 0)"" clip-path=""url(#c)""/>"));

        Assert.Equal(new[] { 10f, 0, 5, 5 }, Numbers(lines[Array.IndexOf(lines, "W") - 1], "re"));
    }

    /// <summary>The clip's shapes write paths only: no colour and no painting operator before the <c>W</c>.</summary>
    [Fact]
    public void TheClipsShapesAreNotPainted()
    {
        string[] lines = Lines(Content(
            @"<defs><clipPath id=""c""><circle cx=""5"" cy=""5"" r=""5"" fill=""red"" stroke=""blue""/><rect x=""20"" width=""5"" height=""5""/></clipPath></defs>" +
            @"<rect width=""30"" height=""10"" clip-path=""url(#c)""/>"));

        int q = Array.IndexOf(lines, "q"), w = Array.IndexOf(lines, "W");
        string[] clip = lines.Skip(q + 1).Take(w - q - 1).ToArray();
        Assert.Equal(4, clip.Count(l => l.EndsWith(" c", StringComparison.Ordinal)));   // the circle's curves
        Assert.Contains(clip, l => l.EndsWith(" re", StringComparison.Ordinal));      // and the rect, one path
        Assert.DoesNotContain(clip, l => l is "f" or "S" or "B" or "n" || l.EndsWith(" rg", StringComparison.Ordinal) || l.EndsWith(" RG", StringComparison.Ordinal));
    }

    /// <summary>A line in a clip sets no stroke either: it set its colour and width outside the clip's paint check.</summary>
    [Fact]
    public void ALineInAClipSetsNoStroke()
    {
        string[] lines = Lines(Content(
            @"<defs><clipPath id=""c""><line x1=""0"" y1=""0"" x2=""5"" y2=""5"" stroke=""black""/><rect width=""5"" height=""5""/></clipPath></defs>" +
            @"<rect width=""10"" height=""10"" clip-path=""url(#c)""/>"));

        int w = Array.IndexOf(lines, "W");
        Assert.True(w > 0, "no clip was set");
        Assert.DoesNotContain(lines.Take(w), l => l.EndsWith(" RG", StringComparison.Ordinal) || l.EndsWith(" w", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEvenOddClipRuleIsWStar()
    {
        string[] lines = Lines(Content(
            @"<defs><clipPath id=""c"" clip-rule=""evenodd""><rect width=""5"" height=""5""/></clipPath></defs>" +
            @"<rect width=""10"" height=""10"" clip-path=""url(#c)""/>"));

        Assert.Contains("W*", lines);
        Assert.DoesNotContain("W", lines);
    }

    /// <summary>A group's clip closes after all the group draws, before its next sibling.</summary>
    [Fact]
    public void AGroupIsClippedAsAWhole()
    {
        string[] lines = Lines(Content(Clip5 +
            @"<g clip-path=""url(#c)""><rect width=""10"" height=""10""/><rect x=""20"" width=""10"" height=""10""/></g>" +
            @"<rect x=""100"" width=""1"" height=""1""/>"));

        int w = Array.IndexOf(lines, "W");
        int[] rects = lines.Select((l, i) => (l, i)).Where(p => p.l.EndsWith(" re", StringComparison.Ordinal)).Select(p => p.i).ToArray();
        Assert.Equal(4, rects.Length);                     // the clip's, the group's two, the sibling
        int closeAfterGroup = Array.LastIndexOf(lines, "Q", rects[3]);
        Assert.True(closeAfterGroup > rects[2] && w < rects[1], "the clip must be set before the group and end before its sibling");
        Assert.Equal(lines.Count(l => l == "q"), lines.Count(l => l == "Q"));
    }

    /// <summary>An empty clipPath clips everything away; a zero-size path stands for it.</summary>
    [Fact]
    public void AnEmptyClipPathClipsEverything()
    {
        string[] lines = Lines(Content(
            @"<defs><clipPath id=""c""/></defs><rect width=""10"" height=""10"" clip-path=""url(#c)""/>"));

        Assert.Equal(new[] { 0f, 0, 0, 0 }, Numbers(lines[Array.IndexOf(lines, "W") - 1], "re"));
    }

    /// <summary>
    /// What cannot be applied is reported, and the element drawn unclipped. (A clip in
    /// <c>objectBoundingBox</c> units is applied to a shape since batch 46: <c>SvgBoxUnitsTests</c>.)
    /// </summary>
    [Theory]
    [InlineData("", "url(#nothing)", "no clipPath")]
    [InlineData(Clip5, "star(5px)", "not a basic shape")]
    public void AClipThatCannotBeAppliedIsReported(string defs, string clipPath, string reason)
    {
        string[] lines = Lines(Content(defs + $@"<rect width=""10"" height=""10"" clip-path=""{clipPath}""/>"));

        Assert.DoesNotContain("W", lines);
        Assert.Contains(lines, l => l.EndsWith(" re", StringComparison.Ordinal));
        Assert.Contains("'clip-path'", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
        Assert.Contains(reason, SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
    }

    /// <summary>A clipPath is only ever referenced: on its own it draws nothing and is not reported.</summary>
    [Fact]
    public void AClipPathOnItsOwnIsNotDrawn()
    {
        string[] lines = Lines(Content(@"<clipPath id=""c""><rect width=""5"" height=""5""/></clipPath><rect x=""50"" width=""1"" height=""1""/>"));

        Assert.Single(lines, l => l.EndsWith(" re", StringComparison.Ordinal));   // the control rect alone
        Assert.Null(SvgToPdfConverter.LastError);
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
