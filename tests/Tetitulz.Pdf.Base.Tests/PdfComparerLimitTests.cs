// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// A text comparison fills at most <see cref="PdfLimits.MaxTextCompareCells"/> line-table cells across all its
/// pages, and a page past them is compared line by line in order, said on
/// <see cref="PdfComparer.LastApproximation"/>; a cell compares two line numbers, not two strings (07's DoS
/// hunt, F5: 4 999 lines of 20 000 characters sharing a prefix took 79 s a page to fill the table).
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class PdfComparerLimitTests
{
    /// <summary>A document of pages each showing <paramref name="lines"/>, one line per baseline.</summary>
    private static byte[] Pdf(int pages, IReadOnlyList<string> lines)
    {
        var content = new StringBuilder("BT /F1 10 Tf\n");
        for (int i = 0; i < lines.Count; i++)
            content.Append("1 0 0 1 10 ").Append(780 - i * 14).Append(" Tm (").Append(lines[i]).Append(") Tj\n");
        content.Append("ET");
        string cs = content.ToString();
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => (i + 5) + " 0 R"))}] /Count {pages} >>");
        Obj($"<< /Length {cs.Length} >>\nstream\n{cs}\nendstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (int i = 0; i < pages; i++)
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 3 0 R /Resources << /Font << /F1 4 0 R >> >> >>");
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static readonly string[] Left = { "alpha", "bravo", "charlie", "delta" };

    /// <summary>The right side: a line inserted at the top, so an exact diff keeps every left line unchanged.</summary>
    private static readonly string[] Right = { "zulu", "alpha", "bravo", "charlie", "delta" };

    /// <summary>
    /// Under a ceiling that admits one page's table (5 × 6 = 30 cells), the first page is diffed exactly and
    /// the second line by line in order, which reports the shifted lines as replaced; the comparison says so.
    /// </summary>
    [Fact]
    public void PastTheCellCeiling_APageIsComparedInOrder_AndTheComparisonSaysSo()
    {
        try
        {
            PdfLimits.MaxTextCompareCells = 30;
            var result = PdfComparer.CompareText(new MemoryStream(Pdf(2, Left)), new MemoryStream(Pdf(2, Right)));

            var first = result.TextDiffs.Where(d => d.PageIndex == 0).ToList();
            Assert.Single(first, d => d.Type == PdfDiffType.Inserted);
            Assert.Equal(4, first.Count(d => d.Type == PdfDiffType.Unchanged));

            var second = result.TextDiffs.Where(d => d.PageIndex == 1).ToList();
            Assert.Equal(0, second.Count(d => d.Type == PdfDiffType.Unchanged));
            Assert.Equal(4, second.Count(d => d.Type == PdfDiffType.Deleted));

            Assert.Contains("MaxTextCompareCells", PdfComparer.LastApproximation, StringComparison.Ordinal);
            Assert.Contains("the first page 2", PdfComparer.LastApproximation, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The control: under the default ceiling both pages are diffed exactly and nothing is said.</summary>
    [Fact]
    public void UnderTheCellCeiling_EveryPageIsDiffedExactly()
    {
        var result = PdfComparer.CompareText(new MemoryStream(Pdf(2, Left)), new MemoryStream(Pdf(2, Right)));

        Assert.Equal(2, result.TextDiffs.Count(d => d.Type == PdfDiffType.Inserted && d.Text == "zulu"));
        Assert.Equal(8, result.TextDiffs.Count(d => d.Type == PdfDiffType.Unchanged));
        Assert.Null(PdfComparer.LastApproximation);
    }

    /// <summary>
    /// Lines that differ only in their last character still diff by their text: a cell compares the two lines'
    /// numbers, one number per distinct line, so a line repeated on the other side matches and a near-copy
    /// does not.
    /// </summary>
    [Fact]
    public void LinesSharingAPrefix_MatchOnlyWhenEqual()
    {
        string p = new string('a', 80);
        var result = PdfComparer.CompareText(
            new MemoryStream(Pdf(1, new[] { p + "1", p + "2", p + "3" })),
            new MemoryStream(Pdf(1, new[] { p + "1", p + "X", p + "3" })));

        Assert.Equal(new[] { PdfDiffType.Unchanged, PdfDiffType.Deleted, PdfDiffType.Inserted, PdfDiffType.Unchanged },
            result.TextDiffs.Select(d => d.Type).ToArray());
        Assert.Equal(p + "X", result.TextDiffs[2].Text);
        Assert.True(PdfComparer.CompareText(new MemoryStream(Pdf(1, new[] { p + "1" })), new MemoryStream(Pdf(1, new[] { p + "1" }))).AreTextEqual);
    }
}
