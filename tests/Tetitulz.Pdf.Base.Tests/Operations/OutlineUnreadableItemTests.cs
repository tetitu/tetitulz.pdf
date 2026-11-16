// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// An outline item whose <c>/Next</c> names an object the parser cannot read ends the walk as the
/// list's end would, and the bookmark reader says so in <see cref="PdfBookmarksManager.LastTruncation"/>:
/// a caller writing the outline back otherwise replaces the whole with the prefix it read. The
/// control, a readable second item, is read and nothing is reported.
/// </summary>
public class OutlineUnreadableItemTests
{
    [Fact]
    public void AnUnreadableNextItem_EndsTheReadAndIsReported()
    {
        var bookmarks = PdfBookmarksManager.ReadBookmarks(new MemoryStream(Pdf(secondReadable: false)));

        Assert.Single(bookmarks);
        Assert.Equal("One", bookmarks[0].Title);
        Assert.Contains("could not be read", PdfBookmarksManager.LastTruncation ?? "", StringComparison.Ordinal);
        Assert.Contains("/Next", PdfBookmarksManager.LastTruncation ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void AReadableNextItem_IsReadAndNothingIsReported()
    {
        var bookmarks = PdfBookmarksManager.ReadBookmarks(new MemoryStream(Pdf(secondReadable: true)));

        Assert.Equal(2, bookmarks.Count);
        Assert.Equal("Two", bookmarks[1].Title);
        Assert.Null(PdfBookmarksManager.LastTruncation);
    }

    /// <summary>
    /// Outline root 5 with items 6 (One) and 7 (Two); object 7's body is a stray <c>]</c> when it is
    /// to be unreadable — a close with nothing open, which the tolerant parser refuses (an
    /// unterminated dictionary it reads on into the next object and hands back).
    /// </summary>
    private static byte[] Pdf(bool secondReadable)
    {
        string two = secondReadable
            ? "<< /Title (Two) /Parent 5 0 R /Dest [3 0 R /Fit] >>"
            : "]";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Outlines /First 6 0 R /Last 7 0 R /Count 2 >>",
            "<< /Title (One) /Parent 5 0 R /Next 7 0 R /Dest [3 0 R /Fit] >>",
            two,
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
