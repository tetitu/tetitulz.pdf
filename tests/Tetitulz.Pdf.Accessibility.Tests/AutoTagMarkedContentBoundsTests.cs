// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// What a page's own marked content may make the auto-tagger write: an <c>/MCID</c> past
/// <see cref="PdfLimits.MaxMarkedContentIdentifier"/> is refused (the parent tree's array for the page
/// holds one slot per identifier, and one past <c>int.MaxValue</c> wraps), and a stale
/// <c>/StructParents</c> on a page the pass does not re-mark is cleared, a structure tree present or not.
/// </summary>
public class AutoTagMarkedContentBoundsTests
{
    [Theory]
    [InlineData("5000000", true)]
    [InlineData("2147483647", true)]
    [InlineData("5", false)]
    public void AdversarialMarkedContentIdentifier_PastTheCeiling_LeavesThePageAsItIs(string mcid, bool refused)
    {
        byte[] pdf = OnePage("/P <</MCID " + mcid + ">> BDC EMC BT /F1 12 Tf 72 700 Td (x) Tj ET");

        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(input, output, new AutoTagOptions());

        Assert.Equal(refused, result.SkippedPageReasons.Any(r => r.Contains("MaxMarkedContentIdentifier", StringComparison.Ordinal)));
        Assert.Equal(!refused, result.ContentMarked);
    }

    /// <summary>
    /// Two pages, no structure tree: page 1 carries a producer's <c>/StructParents 0</c> and marked content the
    /// pass cannot adopt (a nonstandard tag), page 2 is re-marked and given key 0. Page 1's stale key is
    /// cleared, or both pages would name one key and page 1's content would resolve to page 2's element.
    /// </summary>
    [Fact]
    public void AStaleStructParentsOnAPageThePassSkips_IsCleared_WhenNoTreeExisted()
    {
        byte[] pdf = TwoPages(
            page1: "/StructParents 0", content1: "/Body <</MCID 0>> BDC BT /F1 12 Tf 72 700 Td (A) Tj ET EMC",
            content2: "BT /F1 12 Tf 72 700 Td (B) Tj ET");

        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        AutoTagResult result = PdfAccessibility.AutoTag(input, output, new AutoTagOptions());
        Assert.True(result.ContentMarked);

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray(), writable: false));
        PdfObject first = doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.StructParents]);
        PdfObject second = doc.ResolveObject(doc.Pages[1].Dictionary[PdfName.StructParents]);
        Assert.NotNull(second);
        Assert.True(first == null || first is PdfNull || !(first is PdfNumber a && second is PdfNumber b && a.LongValue == b.LongValue),
            "both pages name parent-tree key " + (first as PdfNumber)?.LongValue);
    }

    private static byte[] OnePage(string content) => TwoPages(page1: "", content1: content, content2: null);

    private static byte[] TwoPages(string page1, string content1, string content2)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            content2 == null ? "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" : "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " + page1 + " /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            StreamObject(content1),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        if (content2 != null)
        {
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 7 0 R >>");
            bodies.Add(StreamObject(content2));
        }
        return BuildPdf(bodies);
    }

    private static string StreamObject(string content)
        => "<< /Length " + content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n" + content + "\nendstream";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref.ToString(CultureInfo.InvariantCulture)).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
