// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.HtmlToPdf.Tests;

/// <summary>
/// A CSS Grid <c>repeat(n, …)</c> count is clamped so a huge one cannot run away. <c>ExpandRepeats</c>
/// left the count unclamped on purpose — a lazy consumer stops at its own track cap — but a pattern of
/// only <c>[name]</c> groups never reaches a track cap, so <c>repeat(2147483647, [a])</c> was pulled 2^31
/// times (a multi-gigabyte <c>List</c>, minutes of CPU, then <c>OutOfMemoryException</c> out of the public
/// door), a nested <c>repeat(1, repeat(2147483647, 1px))</c> built a 2^31-entry list, and
/// <c>repeat(auto-fill, repeat(2147483647, 10px))</c> walked the pattern 2^31 times. The count is now
/// clamped to one past the largest consumer cap, which also bounds the nested and auto-fill paths (2026-10-23).
/// </summary>
public class GridRepeatBoundTests
{
    [Theory]
    [InlineData("<div style='display:grid;grid-template-columns:repeat(2147483647,[a]) 1fr'><p>x</p></div>")]
    [InlineData("<div style='display:grid;grid-template-columns:repeat(1,repeat(2147483647,1px))'><p>x</p></div>")]
    [InlineData("<div style='display:grid;grid-template-columns:repeat(auto-fill,repeat(2147483647,10px))'><p>x</p></div>")]
    [InlineData("<div style='display:grid;grid-template-rows:repeat(2147483647,10px)'><p>x</p></div>")]
    [InlineData("<div style='display:grid;grid-auto-columns:repeat(2147483647,10px)'><p>x</p></div>")]
    public void AHugeRepeatCount_IsClamped_NotRunAway(string html)
    {
        // Pre-fix each of these ran for minutes and/or threw OutOfMemoryException out of the public door.
        byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(html);
        Assert.NotEmpty(pdf);
    }

    /// <summary>The control: an ordinary small repeat still lays out its tracks (the clamp does not drop a real template).</summary>
    [Fact]
    public void AnOrdinaryRepeat_StillLaysOutItsColumns()
    {
        byte[] pdf = HtmlToPdfConverter.ConvertHtmlToBytes(
            "<div style='display:grid;grid-template-columns:repeat(3,100px)'><p>aCell</p><p>bCell</p><p>cCell</p></div>");
        var texts = PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(pdf));
        Assert.Contains(texts, t => t.Text.Contains("aCell", StringComparison.Ordinal));
        Assert.Contains(texts, t => t.Text.Contains("cCell", StringComparison.Ordinal));
    }
}
