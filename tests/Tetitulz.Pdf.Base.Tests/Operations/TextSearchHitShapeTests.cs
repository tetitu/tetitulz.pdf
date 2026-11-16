// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The shape of a search hit: how many there are, and what the snippet around one looks like.
/// </summary>
/// <remarks>
/// <b>Search drives redaction targeting and the viewer's find box</b>, so a missed occurrence is a
/// secret left on a page that was reported as cleaned. The existing tests cover the folding path and
/// the offsets; the enumeration and the snippet's own edges were unasserted.
/// </remarks>
public class TextSearchHitShapeTests
{
    private static byte[] PageShowing(string text)
    {
        var hex = new StringBuilder();
        foreach (char c in text) hex.Append(((int)c).ToString("X2"));
        string content = "BT /F1 12 Tf 50 700 Td <" + hex + "> Tj ET";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
                "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// <b>Overlapping occurrences are all found.</b> The scan resumes one character past the match's
    /// start, not past its end.
    /// </summary>
    /// <remarks>
    /// "aaaa" contains two overlapping "aaa", at offsets 0 and 1 — advancing by the match length
    /// would report only the first.
    /// For a redactor that is the difference between covering a secret and covering part of it; the
    /// remaining occurrence stays on a page reported as redacted.
    /// </remarks>
    [Fact]
    public void OverlappingOccurrencesAreAllReported()
    {
        using var pdf = new MemoryStream(PageShowing("aaaa"));

        var hits = PdfTextSearch.Search(pdf, "aaa");

        Assert.Equal(2, hits.Count);
        Assert.Equal(new[] { 0, 1 }, hits.Select(h => h.CharOffset).ToArray());
    }

    /// <summary>
    /// <b>The snippet's ellipses mark real truncation and nothing else.</b> A match at the very
    /// start of the text has no elision before it, and one at the very end none after.
    /// </summary>
    /// <remarks>
    /// An ellipsis that is always written says the page continues where it does not — which a
    /// caller displaying the snippet shows to a person as fact.
    /// </remarks>
    [Fact]
    public void NoEllipsisIsWrittenWhereNothingWasElided()
    {
        using var pdf = new MemoryStream(PageShowing("needle"));

        var hit = Assert.Single(PdfTextSearch.Search(pdf, "needle", ignoreCase: true, contextChars: 40));

        Assert.Equal("needle", hit.ContextSnippet);
        Assert.DoesNotContain("…", hit.ContextSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control: when text really is elided on both sides, both ellipses appear — so the test
    /// above pins the condition rather than an implementation that never writes one.
    /// </summary>
    [Fact]
    public void EllipsesAppearOnBothSidesWhenTextIsElided()
    {
        using var pdf = new MemoryStream(PageShowing("aaaaaneedlebbbbb"));

        var hit = Assert.Single(PdfTextSearch.Search(pdf, "needle", ignoreCase: true, contextChars: 2));

        Assert.StartsWith("…", hit.ContextSnippet, StringComparison.Ordinal);
        Assert.EndsWith("…", hit.ContextSnippet, StringComparison.Ordinal);
        Assert.Contains("needle", hit.ContextSnippet, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>A negative <c>ContextChars</c> is refused by name</b> rather than producing a snippet
    /// whose bounds cross.
    /// </summary>
    [Fact]
    public void ANegativeContextCharsIsRefused()
    {
        using var pdf = new MemoryStream(PageShowing("needle"));

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => PdfTextSearch.Search(pdf, "needle", new TextSearchOptions { ContextChars = -1 }));

        Assert.Contains("ContextChars", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b><c>Contains</c> honours <c>IgnoreCase</c>.</b>
    /// </summary>
    /// <remarks>
    /// The options overload reads the flag into its comparison, and nothing asserted that it did —
    /// so a case-sensitive search could have matched case-insensitively, which is the direction that
    /// reports a hit the caller did not ask for.
    /// </remarks>
    [Fact]
    public void ContainsHonoursCaseSensitivity()
    {
        using var insensitive = new MemoryStream(PageShowing("Needle"));
        using var sensitive = new MemoryStream(PageShowing("Needle"));

        Assert.True(PdfTextSearch.Contains(insensitive, "needle",
            new TextSearchOptions { IgnoreCase = true }));
        Assert.False(PdfTextSearch.Contains(sensitive, "needle",
            new TextSearchOptions { IgnoreCase = false }));
    }
}
