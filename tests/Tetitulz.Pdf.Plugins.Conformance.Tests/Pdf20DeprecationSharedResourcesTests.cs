// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.Conformance;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// <see cref="Pdf20DeprecationChecker"/> reads each resource sub-dictionary once, however many pages
/// share it, and reports what it finds at the first page that reaches it — as its <c>/CharSet</c>
/// check already did for a font. Read once a page, a dictionary the pages share cost pages × entries
/// and reported each construct once a page: 800 pages over 800 entries took 2.6 s.
/// </summary>
public class Pdf20DeprecationSharedResourcesTests
{
    private const int Pages = 40;

    /// <summary>Every construct a shared dictionary holds is one finding, on the first page.</summary>
    [Fact]
    public void AdversarialPagesSharingResources_ReportEachConstructOnce_AtTheFirstPage()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(Document(Pages, shared: true)));

        foreach (var construct in new[]
                 {
                     Pdf20DeprecatedConstruct.BlendModeArray, Pdf20DeprecatedConstruct.TransferFunctionInGraphicsState,
                     Pdf20DeprecatedConstruct.Opi,
                 })
        {
            var one = Assert.Single(findings, f => f.Construct == construct);
            Assert.Equal(0, one.PageIndex);
        }
        var named = findings.Where(f => f.Construct == Pdf20DeprecatedConstruct.NamedResource).ToList();
        Assert.Equal(2, named.Count);                         // the XObject's /Name and the font's
        Assert.All(named, f => Assert.Equal(0, f.PageIndex));

        // The evidence the reads were skipped, counted only inside the scope the check opens: six
        // resource checks, each passing the shared sub-dictionary by on every page after the first.
        Assert.Equal(6 * (Pages - 1), ScanReuse.ClosedResourceRevisitsSkipped);
    }

    /// <summary>The control: pages with dictionaries of their own each report theirs.</summary>
    [Fact]
    public void PagesWithTheirOwnResources_EachReportTheirs()
    {
        var findings = Pdf20DeprecationChecker.Check(new MemoryStream(Document(3, shared: false)));

        Assert.Equal(new[] { 0, 1, 2 },
            findings.Where(f => f.Construct == Pdf20DeprecatedConstruct.BlendModeArray).Select(f => f.PageIndex).OrderBy(p => p));
        Assert.Equal(new[] { 0, 1, 2 },
            findings.Where(f => f.Construct == Pdf20DeprecatedConstruct.Opi).Select(f => f.PageIndex).OrderBy(p => p));
    }

    /// <summary>
    /// <paramref name="pages"/> pages whose resources hold an /ExtGState with a /BM array and a /TR,
    /// an image with /OPI and /Name, and a font with /Name — one dictionary their /Pages node names,
    /// or, without <paramref name="shared"/>, each page its own copies.
    /// </summary>
    private static byte[] Document(int pages, bool shared)
    {
        var bodies = new List<string>();
        int firstPage = 3, copies = shared ? 1 : pages, firstSet = firstPage + pages, content = firstSet + 4 * copies;
        var kids = new StringBuilder();
        for (int p = 0; p < pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792]"
                   + (shared ? $" /Resources {firstSet} 0 R" : "") + " >>");
        for (int p = 0; p < pages; p++)
            bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R"
                       + (shared ? "" : $" /Resources {firstSet + 4 * p} 0 R") + " >>");
        for (int k = 0; k < copies; k++)
        {
            int set = firstSet + 4 * k;
            bodies.Add($"<< /ExtGState << /GS0 {set + 1} 0 R >> /XObject << /Im0 {set + 2} 0 R >> /Font << /F0 {set + 3} 0 R >> >>");
            bodies.Add("<< /Type /ExtGState /BM [/Multiply /Normal] /TR [0 1] >>");
            bodies.Add("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceGray /BitsPerComponent 8 /OPI << /1.3 1 >> /Name /Im0 /Length 1 >>\nstream\na\nendstream");
            bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Name /F0 >>");
        }
        bodies.Add("<< /Length 7 >>\nstream\n/Im0 Do\nendstream");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
