// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The bookmark reader reads the outline through <see cref="OutlineItems"/>, the one bounded walk:
/// the tree a viewer shows is rebuilt from the walk's reading order and depths, an item written
/// inline is read like one written as its own object, and a depth cut is reported on
/// <see cref="PdfBookmarksManager.LastTruncation"/>, never silent.
/// </summary>
public class OutlineOneWalkTests
{
    [Fact]
    public void TheTree_IsRebuiltFromTheWalksDepths_SiblingsAndChildrenInPlace()
    {
        // A: [A1: [A1a], A2], B — the items as their own objects, linked by /First, /Next, /Parent.
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Outlines /First 5 0 R /Last 9 0 R /Count 5 >>",
            "<< /Title (A) /Parent 4 0 R /Next 9 0 R /First 6 0 R /Last 8 0 R /Count 3 /Dest [3 0 R /XYZ 0 700 0] >>",
            "<< /Title (A1) /Parent 5 0 R /Next 8 0 R /First 7 0 R /Last 7 0 R /Count -1 >>",
            "<< /Title (A1a) /Parent 6 0 R /Dest [3 0 R /Fit] >>",
            "<< /Title (A2) /Parent 5 0 R /Prev 6 0 R >>",
            "<< /Title (B) /Parent 4 0 R /Prev 5 0 R >>");

        var marks = PdfBookmarksManager.ReadBookmarks(new MemoryStream(pdf));

        Assert.Equal(new[] { "A", "B" }, marks.Select(m => m.Title));
        Assert.Equal(new[] { "A1", "A2" }, marks[0].Children.Select(m => m.Title));
        Assert.Equal(new[] { "A1a" }, marks[0].Children[0].Children.Select(m => m.Title));
        Assert.Empty(marks[0].Children[1].Children);
        Assert.Empty(marks[1].Children);
        Assert.Equal(new[] { 0, 1, 2 }, new[] { marks[0].Depth, marks[0].Children[0].Depth, marks[0].Children[0].Children[0].Depth });
        Assert.True(marks[0].IsOpen);
        Assert.False(marks[0].Children[0].IsOpen);
        Assert.Equal(700f, marks[0].Top);
        Assert.Equal(0, marks[0].Children[0].Children[0].PageNumber);
        Assert.Null(PdfBookmarksManager.LastTruncation);
    }

    /// <summary>An item written inline under <c>/First</c> is an item of the outline (§7.3.10 lets any value be direct).</summary>
    [Fact]
    public void AnItemWrittenInline_IsRead()
    {
        byte[] pdf = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines << /Type /Outlines /First << /Title (Inline) /Dest [3 0 R /Fit] >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");

        var mark = Assert.Single(PdfBookmarksManager.ReadBookmarks(new MemoryStream(pdf)));
        Assert.Equal("Inline", mark.Title);
        Assert.Equal(0, mark.PageNumber);
    }

    /// <summary>
    /// A chain of <c>/First</c> links deeper than the cap is cut there and said so; the items above
    /// the cut are read. A <c>/Next</c> that loops back is walked once (the control: no cut).
    /// </summary>
    [Fact]
    public void AnOutlineDeeperThanTheCap_IsCutAndReported_HostileNesting()
    {
        int depth = PdfLimits.MaxTreeDepth + 5;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Outlines /First 5 0 R >>",
        };
        for (int i = 0; i < depth; i++)
            bodies.Add($"<< /Title (L{i}) " + (i + 1 < depth ? $"/First {6 + i} 0 R " : "") + ">>");
        var marks = PdfBookmarksManager.ReadBookmarks(new MemoryStream(FormPdfFixtures.Build(bodies.ToArray())));

        int read = 0;
        for (var level = marks; level.Count > 0; level = level[0].Children) read++;
        Assert.Equal(PdfLimits.MaxTreeDepth, read);
        Assert.Contains("cut at", PdfBookmarksManager.LastTruncation, System.StringComparison.Ordinal);

        byte[] loop = FormPdfFixtures.Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /Outlines /First 5 0 R >>",
            "<< /Title (One) /Next 6 0 R >>",
            "<< /Title (Two) /Next 5 0 R >>");
        Assert.Equal(new[] { "One", "Two" }, PdfBookmarksManager.ReadBookmarks(new MemoryStream(loop)).Select(m => m.Title));
        Assert.Null(PdfBookmarksManager.LastTruncation);
    }
}
