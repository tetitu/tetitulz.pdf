// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Tests.Constants;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// The duplicate-text rule's comparisons are a page's budget
/// (<see cref="PdfLimits.MaxOverlappingTextComparisons"/>): a column of one glyph at one X — here
/// 300 lines of <c>a</c> — reads every earlier drawing per glyph, quadratic in the page. Past the
/// budget the rule stops and <see cref="PdfTextExtractor.LastFormXObjectError"/> says so; under the
/// default the same page extracts with nothing said. Both extract every line.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class OverlapComparisonBudgetTests
{
    [Fact]
    public void AColumnOfOneGlyph_PastTheComparisonBudget_IsReportedAndStillExtracted()
    {
        byte[] pdf = ColumnPdf(300);
        try
        {
            PdfLimits.MaxOverlappingTextComparisons = 1000;
            string stopped = Extract(pdf, out string stoppedError);
            Assert.Contains("MaxOverlappingTextComparisons", stoppedError, System.StringComparison.Ordinal);
            Assert.Equal(300, stopped.Count(c => c == 'a'));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }

        string whole = Extract(pdf, out string wholeError);
        Assert.Null(wholeError);
        Assert.Equal(300, whole.Count(c => c == 'a'));
    }

    /// <summary>
    /// The budget is the page's, whatever draws on it: two forms each a 300-line column (44 850
    /// comparisons apiece) spend one budget of 50 000 between them and pass it, where each alone
    /// fits — and a document of two such pages renews it per page, so neither page is said to.
    /// </summary>
    [Fact]
    public void TheBudget_IsSharedByAPagesFormsAndRenewedPerPage()
    {
        try
        {
            PdfLimits.MaxOverlappingTextComparisons = 50_000;

            Extract(TwoFormsPdf(300), out string forms);
            Assert.Contains("MaxOverlappingTextComparisons", forms, System.StringComparison.Ordinal);

            using var ms = new MemoryStream(ColumnPdf(300, pages: 2));
            using var doc = PdfDocument.Open(ms);
            string text = PdfTextExtractor.ExtractText(doc);
            Assert.Null(PdfTextExtractor.LastFormXObjectError);
            Assert.Equal(600, text.Count(c => c == 'a'));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    private const string FontResources = "<< /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >>";

    private static string Column(int lines)
    {
        var content = new StringBuilder("BT /F1 12 Tf 100 1600 Td ");
        for (int i = 0; i < lines; i++) content.Append("(a) Tj 0 -5 Td ");
        return content.Append("ET").ToString();
    }

    private static byte[] TwoFormsPdf(int lines)
    {
        string column = Column(lines);
        string form = $"<< /Type /XObject /Subtype /Form /BBox [0 0 612 1700] /Resources {FontResources} " +
                      $"/Length {column.Length} >>\nstream\n{column}\nendstream";
        const string invoke = "/A Do /B Do";
        return Assemble(new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 1700] /Contents 4 0 R " +
                "/Resources << /XObject << /A 5 0 R /B 6 0 R >> >> >>",
            $"<< /Length {invoke.Length} >>\nstream\n{invoke}\nendstream",
            form,
            form,
        });
    }

    private static string Extract(byte[] pdf, out string error)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);
        error = PdfTextExtractor.LastFormXObjectError;
        return text;
    }

    /// <summary>Pages of <paramref name="lines"/> lines of a single <c>a</c>, 5 points apart, at one X.</summary>
    private static byte[] ColumnPdf(int lines, int pages = 1)
    {
        string body = Column(lines);
        string kids = string.Join(" ", Enumerable.Range(0, pages).Select(p => $"{3 + 2 * p} 0 R"));
        var objects = new System.Collections.Generic.List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{kids}] /Count {pages} >>",
        };
        for (int p = 0; p < pages; p++)
        {
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 1700] /Contents {4 + 2 * p} 0 R /Resources {FontResources} >>");
            objects.Add($"<< /Length {body.Length} >>\nstream\n{body}\nendstream");
        }
        return Assemble(objects.ToArray());
    }

    private static byte[] Assemble(string[] objects)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new long[objects.Length];
        for (int i = 0; i < objects.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        long xref = sb.Length;
        sb.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (long off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
