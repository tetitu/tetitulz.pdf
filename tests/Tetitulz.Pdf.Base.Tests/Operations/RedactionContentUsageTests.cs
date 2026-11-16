// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Which pages share a content stream is read once for a redaction, however many pages it edits.
/// The rewriter walked every page for each page it substituted — the edited pages times the
/// document's, so a redaction on every page of a large document cost its pages squared.
/// </summary>
public sealed class RedactionContentUsageTests
{
    [Fact]
    public void AdversarialRedactionOnEveryPage_ReadsEachPageAFewTimes()
    {
        const int pages = 300;
        byte[] pdf = Pages(pages, sharedBy: 0);

        PdfFullRewriter.ContentUsage.PagesRead = 0;
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(new MemoryStream(pdf), output,
            Enumerable.Range(0, pages).Select(Area).ToList());

        Assert.InRange(PdfFullRewriter.ContentUsage.PagesRead, pages, 4 * pages);
        Assert.DoesNotContain("SECRET", TextOf(output.ToArray(), pages - 1), StringComparison.Ordinal);
    }

    /// <summary>
    /// Three pages share one stream and the first two are redacted: each takes a private copy, or
    /// the last the stream itself once no other page uses it, and the third keeps its text.
    /// </summary>
    [Fact]
    public void PagesSharingAStream_AreRedactedEach_AndTheUnnamedOneKeepsItsText()
    {
        byte[] pdf = Pages(3, sharedBy: 3);

        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(new MemoryStream(pdf), output, new[] { Area(0), Area(1) });

        byte[] redacted = output.ToArray();
        Assert.DoesNotContain("SECRET", TextOf(redacted, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", TextOf(redacted, 1), StringComparison.Ordinal);
        Assert.Contains("SECRET", TextOf(redacted, 2), StringComparison.Ordinal);
    }

    /// <summary>Two pages share one stream and both are redacted: neither keeps the text.</summary>
    [Fact]
    public void BothPagesSharingAStream_AreRedacted()
    {
        byte[] pdf = Pages(2, sharedBy: 2);

        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(new MemoryStream(pdf), output, new[] { Area(0), Area(1) });

        Assert.DoesNotContain("SECRET", TextOf(output.ToArray(), 0), StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", TextOf(output.ToArray(), 1), StringComparison.Ordinal);
    }

    private static PdfRedactionArea Area(int page)
        => new PdfRedactionArea { PageIndex = page, X = 60f, Y = 690f, Width = 200f, Height = 30f };

    private static string TextOf(byte[] pdf, int page)
        => string.Join(" ", PdfStructuredTextExtractor.Extract(new MemoryStream(pdf))[page].Lines.Select(l => l.Text));

    // `count` pages drawing (SECRET) at 72,700 in Helvetica; the first `sharedBy` of them share one
    // content stream, each other page has its own.
    private static byte[] Pages(int count, int sharedBy)
    {
        const string content = "BT /F1 12 Tf 72 700 Td (SECRET) Tj ET";
        // 1 catalog, 2 pages, 3 font, 4 the shared stream, then page i at 5 + 2i, its own stream after it.
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", Enumerable.Range(0, count).Select(i => (5 + 2 * i) + " 0 R")) +
            "] /Count " + count + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        for (int i = 0; i < count; i++)
        {
            int contents = i < sharedBy ? 4 : 6 + 2 * i;
            bodies.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {contents} 0 R " +
                       "/Resources << /Font << /F1 3 0 R >> >> >>");
            bodies.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
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
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
