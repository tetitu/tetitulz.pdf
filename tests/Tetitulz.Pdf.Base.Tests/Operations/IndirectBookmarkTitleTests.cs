// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A bookmark's <c>/Title</c> must be read whichever way it is spelled — and the reason this one
/// matters more than a lossy read is that a <b>round trip destroys it</b>.
/// </summary>
/// <remarks>
/// <para><c>GetStringValue</c> read <c>dict[key]</c> raw, one line above <c>ResolvePageNumber</c>,
/// which resolves <c>/A</c>, <c>/S</c>, <c>/D</c> and <c>/Dest</c> properly. An indirect
/// <c>/Title</c> therefore read as null and <c>ReadBookmarks</c> returned a blank sidebar.</para>
///
/// <para><b>The amplification is what makes it destructive.</b> <c>WriteBookmarks</c> replaces the
/// <i>entire</i> <c>/Outlines</c> tree from the supplied list — so the ordinary read-edit-write
/// cycle (read the outline, change one entry, write it back) writes the blanks back and the titles
/// are gone from the document. No error is reported at any point; the caller sees an outline of
/// empty entries and assumes that is what the file contained.</para>
///
/// <para>The round trip is asserted, not just the read, because that is where the data is lost.</para>
/// </remarks>
public class IndirectBookmarkTitleTests
{
    /// <summary>
    /// The title is read whichever way it is spelled. <c>false</c> is the control.
    /// </summary>
    [Theory]
    [InlineData(false)]  // /Title (Chapter One)
    [InlineData(true)]   // /Title 9 0 R
    public void TheTitleIsRead_WhicheverWayItIsSpelled(bool indirect)
    {
        using var ms = new MemoryStream(OutlinePdf(indirect));
        IReadOnlyList<PdfBookmark> bookmarks = PdfBookmarksManager.ReadBookmarks(ms);

        var bookmark = Assert.Single(bookmarks);
        Assert.Equal("Chapter One", bookmark.Title);
    }

    /// <summary>
    /// A read-edit-write round trip keeps the title — the case where the null is not merely
    /// reported but written back over the document's own data.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARoundTripKeepsTheTitle_WhicheverWayItIsSpelled(bool indirect)
    {
        byte[] original = OutlinePdf(indirect);

        List<PdfBookmark> read;
        using (var input = new MemoryStream(original))
            read = new List<PdfBookmark>(PdfBookmarksManager.ReadBookmarks(input));

        using var rewriteIn = new MemoryStream(original);
        using var rewritten = new MemoryStream();
        PdfBookmarksManager.WriteBookmarks(rewriteIn, rewritten, read);

        rewritten.Position = 0;
        var after = PdfBookmarksManager.ReadBookmarks(rewritten);

        var bookmark = Assert.Single(after);
        Assert.Equal("Chapter One", bookmark.Title);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// A one-entry outline. Object 9 holds the title string when <paramref name="indirect"/> — the
    /// only difference between the two fixtures.
    /// </summary>
    private static byte[] OutlinePdf(bool indirect)
    {
        string title = indirect ? "9 0 R" : "(Chapter One)";

        return Build(
            "<< /Type /Catalog /Pages 2 0 R /Outlines 5 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>",
            "<< /Length 0 >>\nstream\n\nendstream",
            "<< /Type /Outlines /First 6 0 R /Last 6 0 R /Count 1 >>",
            "<< /Title " + title + " /Parent 5 0 R /Dest [3 0 R /Fit] >>",
            "null", "null",
            "(Chapter One)");
    }

    /// <summary>Assembles objects 1..n with a correct xref table. Bodies must be ASCII.</summary>
    private static byte[] Build(params string[] objects)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new List<int>();
        for (int i = 0; i < objects.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Length + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
