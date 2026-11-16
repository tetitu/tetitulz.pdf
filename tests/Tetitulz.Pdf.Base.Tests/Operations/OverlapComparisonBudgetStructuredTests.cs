// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Structured extraction spends the page's duplicate-text comparisons through the same index as
/// plain extraction (<see cref="PdfLimits.MaxOverlappingTextComparisons"/>): 300 lines of one glyph at
/// one X are 44 850 comparisons, so under a budget of 1 000 the rule stops and
/// <see cref="PdfStructuredTextExtractor.LastError"/> says so, and every glyph is still extracted;
/// under the default nothing is said.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class OverlapComparisonBudgetStructuredTests
{
    [Fact]
    public void AColumnOfOneGlyph_PastTheComparisonBudget_IsReportedAndStillExtracted()
    {
        byte[] pdf = ColumnPdf(300);
        try
        {
            PdfLimits.MaxOverlappingTextComparisons = 1000;
            PdfPageTextLayout stopped = Extract(pdf, out string stoppedError);
            Assert.Contains("MaxOverlappingTextComparisons", stoppedError, System.StringComparison.Ordinal);
            Assert.Equal(300, stopped.Chars.Count(c => c.Char == "a"));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }

        PdfPageTextLayout whole = Extract(pdf, out string wholeError);
        Assert.Null(wholeError);
        Assert.Equal(300, whole.Chars.Count(c => c.Char == "a"));
    }

    /// <summary>
    /// The budget is a page's: two pages of 44 850 comparisons each are extracted under 50 000 with
    /// nothing said, where one budget for both would have passed it on the second.
    /// </summary>
    [Fact]
    public void TheBudget_IsRenewedForEachPage()
    {
        try
        {
            PdfLimits.MaxOverlappingTextComparisons = 50_000;
            using var ms = new MemoryStream(ColumnPdf(300, pages: 2), writable: false);
            var pages = PdfStructuredTextExtractor.Extract(ms);
            Assert.Null(PdfStructuredTextExtractor.LastError);
            Assert.Equal(new[] { 300, 300 }, pages.Select(p => p.Chars.Count(c => c.Char == "a")).ToArray());
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    private static PdfPageTextLayout Extract(byte[] pdf, out string error)
    {
        using var ms = new MemoryStream(pdf, writable: false);
        PdfPageTextLayout page = PdfStructuredTextExtractor.Extract(ms)[0];
        error = PdfStructuredTextExtractor.LastError;
        return page;
    }

    private static byte[] ColumnPdf(int lines, int pages = 1)
    {
        var content = new StringBuilder("BT /F1 12 Tf 100 1600 Td ");
        for (int i = 0; i < lines; i++) content.Append("(a) Tj 0 -5 Td ");
        content.Append("ET");
        string body = content.ToString();
        string kids = string.Join(" ", Enumerable.Range(0, pages).Select(p => $"{3 + 2 * p} 0 R"));
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>",
        };
        for (int p = 0; p < pages; p++)
        {
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 1700] /Contents {4 + 2 * p} 0 R " +
                "/Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>");
            bodies.Add($"<< /Length {body.Length} >>\nstream\n{body}\nendstream");
        }

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat("{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
