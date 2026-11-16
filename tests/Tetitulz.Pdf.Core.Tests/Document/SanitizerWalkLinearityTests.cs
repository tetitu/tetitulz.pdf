// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The cleaning walk is linear in the number of distinct reachable dictionaries: one visited set for
/// the whole document, arrays included. Before this, the visited set was recreated for every
/// top-level object and arrays were re-walked each time, so a graph whose dictionaries shared one
/// array cost roughly its cube — 100 nodes ≈ 0.5 s, 200 ≈ 2.6 s, 400 ≈ 17 s, measured.
/// </summary>
/// <remarks>
/// <b>The evidence is the walk's own count, not a clock.</b> <c>PdfSanitizer.LastDictionariesCleaned</c>
/// is the number of dictionaries the walk processed; with one document-wide visited set it equals the
/// count of distinct reachable dictionaries, so it grows linearly with the fixture. A regression to a
/// per-object visited reset would multiply it; a regression to re-walking arrays would square it. The
/// CLAUDE.md rule: a DoS-guard test asserts the guard's evidence, never a wall clock.
/// </remarks>
public class SanitizerWalkLinearityTests
{
    /// <summary>
    /// A page whose <c>/Annots</c> holds N widgets, each referencing one shared dictionary through a
    /// <c>/Parent</c> — a field/widget shape where many nodes point at one. The shared node is
    /// visited once, so the processed count is linear (N widgets + the shared node + the fixed
    /// catalog/pages/page), not a multiple of N for the shared node.
    /// </summary>
    [Theory]
    [InlineData(50)]
    [InlineData(400)]
    public void SharedDictionariesAreVisitedOnce(int n)
    {
        byte[] pdf = SharedParentPdf(n);

        Sanitize(pdf);

        long processed = PdfSanitizer.LastDictionariesCleaned;
        // catalog, pages, page, the shared parent, and N widgets, each once.
        long expected = 4 + n;
        Assert.Equal(expected, processed);
    }

    /// <summary>
    /// The two counts across two sizes confirm linear growth: the delta equals the extra widgets, so
    /// the per-node cost is constant. A quadratic or cubic walk would make the larger run's count
    /// grow faster than the node count.
    /// </summary>
    [Fact]
    public void TheProcessedCountGrowsByExactlyTheNodeCount()
    {
        Sanitize(SharedParentPdf(100));
        long small = PdfSanitizer.LastDictionariesCleaned;

        Sanitize(SharedParentPdf(300));
        long large = PdfSanitizer.LastDictionariesCleaned;

        Assert.Equal(200, large - small);
    }

    /// <summary>
    /// A chain of ten thousand <b>distinct</b> annotation dictionaries, each naming the next in its
    /// own <c>/Annots</c>. The walk is iterative, so nothing overflows the stack and nothing is
    /// clipped at a depth cap: every node is processed and the document comes back whole. (The
    /// recursive predecessor stopped at a depth cap of 100, silently leaving the rest unsanitized.)
    /// </summary>
    [Fact]
    public void ATenThousandDeepChainIsFullyWalkedWithoutOverflow()
    {
        const string marker = "PWNED_DEEP_CHAIN";
        byte[] pdf = DeepAnnotChain(10_000, marker);
        Assert.True(Contains(pdf, marker), "fixture should carry the deep script before sanitizing");

        byte[] cleaned = Sanitize(pdf);

        Assert.False(Contains(cleaned, marker),
            "the JavaScript action 10 000 links down survived — the walk was clipped, not completed");
        Assert.Equal(1, PageCount(cleaned));
    }

    // ================================================================= helpers

    private static byte[] Sanitize(byte[] pdf)
    {
        using var input = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfSanitizer.Sanitize(input, output, new SanitizeOptions());
        return output.ToArray();
    }

    private static int PageCount(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return doc.Pages.Count;
    }

    private static string Latin1(byte[] b) => Encoding.GetEncoding("iso-8859-1").GetString(b);
    private static bool Contains(byte[] pdf, string s) => Latin1(pdf).Contains(s, StringComparison.Ordinal);

    /// <summary>
    /// Objects: 1 catalog, 2 pages, 3 page, 4 shared parent, 5..(4+n) widgets each with
    /// <c>/Parent 4 0 R</c>. The page's /Annots lists every widget.
    /// </summary>
    private static byte[] SharedParentPdf(int n)
    {
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            null, // page, filled below once the widget numbers are known
            "<< /Type /Annot /Subtype /Widget /Rect [0 0 1 1] >>", // shared parent, object 4
        };
        var annotRefs = new StringBuilder();
        for (int i = 0; i < n; i++)
        {
            int num = 5 + i;
            annotRefs.Append(num).Append(" 0 R ");
            objs.Add("<< /Type /Annot /Subtype /Widget /Rect [0 0 1 1] /Parent 4 0 R >>");
        }
        objs[2] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [" + annotRefs + "] >>";
        return Assemble(objs);
    }

    /// <summary>
    /// Objects 4..(3+n), each an annotation naming the next in its own <c>/Annots</c>; the last holds
    /// a JavaScript action so a completed walk is observable by the script's removal.
    /// </summary>
    private static byte[] DeepAnnotChain(int n, string marker)
    {
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Annots [4 0 R] >>",
        };
        for (int i = 0; i < n; i++)
        {
            int self = 4 + i;
            if (i < n - 1)
                objs.Add("<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] /Annots [" + (self + 1) + " 0 R] >>");
            else
                objs.Add("<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] " +
                         "/A << /S /JavaScript /JS (" + marker + ") >> >>");
        }
        return Assemble(objs);
    }

    private static byte[] Assemble(List<string> objs)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offs = new List<int>();
        for (int i = 0; i < objs.Count; i++)
        {
            offs.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        int x = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (var o in offs) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(objs.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n")
          .Append(x).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
