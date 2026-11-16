// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// A full-text search keeps at most <see cref="PdfLimits.MaxTextSearchHits"/> hits and writes at most
/// <see cref="PdfLimits.MaxTextSearchContextChars"/> of context, and says so on
/// <see cref="PdfTextSearch.LastTruncation"/>: a 143 KB file of pages sharing one long run of one letter
/// answered a one-letter search with 30 000 000 hits, 26 GB allocated (07's DoS hunt, F9).
/// </summary>
[Collection(Tetitulz.Pdf.Base.Tests.TestInfrastructure.PdfLimitsCollection.Name)]
public sealed class PdfTextSearchLimitTests
{
    /// <summary><paramref name="pages"/> pages sharing one content stream that shows a run of <paramref name="run"/> 'a's.</summary>
    private static byte[] SharedRunPdf(int pages, int run)
    {
        string content = "BT /F1 1 Tf 0 0 Td (" + new string('a', run) + ") Tj ET";
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n"); }
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj($"<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pages).Select(i => (i + 5) + " 0 R"))}] /Count {pages} >>");
        Obj($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        for (int i = 0; i < pages; i++)
            Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 3 0 R /Resources << /Font << /F1 4 0 R >> >> >>");
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>
    /// The hostile file itself under the default ceiling: a million overlapping matches over ten pages, kept
    /// to the ceiling, the truncation said.
    /// </summary>
    [Fact]
    public void ASharedRunBomb_StopsAtTheHitCeiling()
    {
        var hits = PdfTextSearch.Search(new MemoryStream(SharedRunPdf(10, 100_000)), "a");

        Assert.Equal(PdfLimits.DefaultMaxTextSearchHits, hits.Count);
        Assert.Contains("MaxTextSearchHits", PdfTextSearch.LastTruncation, StringComparison.Ordinal);
    }

    /// <summary>Under a lowered ceiling the search stops there; the control, a search with fewer matches, says nothing.</summary>
    [Fact]
    public void TheHitCeiling_StopsTheSearch_AndASearchUnderItSaysNothing()
    {
        try
        {
            PdfLimits.MaxTextSearchHits = 50;
            byte[] pdf = SharedRunPdf(2, 100);

            Assert.Equal(50, PdfTextSearch.Search(new MemoryStream(pdf), "a").Count);
            Assert.NotNull(PdfTextSearch.LastTruncation);

            Assert.Equal(20, PdfTextSearch.Search(new MemoryStream(SharedRunPdf(2, 10)), "a").Count);
            Assert.Null(PdfTextSearch.LastTruncation);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// Past the context budget a hit's snippet is its match alone, and the search says so; the hits before it
    /// keep their context.
    /// </summary>
    [Fact]
    public void PastTheContextBudget_ASnippetIsTheMatchAlone()
    {
        try
        {
            PdfLimits.MaxTextSearchContextChars = 100;
            var hits = PdfTextSearch.Search(new MemoryStream(SharedRunPdf(1, 200)), "a", ignoreCase: true, contextChars: 10);

            Assert.Equal(200, hits.Count);
            Assert.True(hits[0].ContextSnippet.Length > 1, "the first hit lost its context");
            Assert.Equal("a", hits[199].ContextSnippet);
            Assert.Contains("MaxTextSearchContextChars", PdfTextSearch.LastTruncation, StringComparison.Ordinal);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }
}
