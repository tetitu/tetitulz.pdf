// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Conformance.Tests;

/// <summary>
/// Pages that share one <c>/Resources</c> and show no annotation share the walk below their own content
/// (<see cref="ScanReuse.SharedWalk"/>). Each shared scan must be the scan the full walk makes — the same
/// dictionaries, streams, pairings and order, budget and refusal — and the sharing must be live.
/// </summary>
public class SharedResourceWalkTests
{
    /// <summary>
    /// Three pages over one resource dictionary — a form with its own resources and an image, a tiling
    /// pattern — each with content of its own; the fourth page carries an annotation and walks in full.
    /// </summary>
    private static byte[] Document()
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [10 0 R 11 0 R 12 0 R 13 0 R] /Count 4 /MediaBox [0 0 100 100] /Resources 3 0 R >>",
            "<< /XObject << /F1 4 0 R >> /Pattern << /P1 5 0 R >> /ColorSpace << /CS0 [/ICCBased 9 0 R] >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << /XObject << /Im 6 0 R >> >> /Length 9 >>\nstream\nq /Im Do Q\nendstream",
            "<< /PatternType 1 /PaintType 1 /TilingType 1 /BBox [0 0 5 5] /XStep 5 /YStep 5 /Resources << >> /Length 15 >>\nstream\n0 0 1 rg 0 0 5 5\nendstream",
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Length 3 >>\nstream\nabc\nendstream",
            "<< /Length 3 >>\nstream\nxyz\nendstream",
            "<< /Length 3 >>\nstream\nxyz\nendstream",
            "<< /N 3 /Length 3 >>\nstream\nabc\nendstream",
            "<< /Type /Page /Parent 2 0 R /Contents 14 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 15 0 R >>",
            "<< /Type /Page /Parent 2 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 14 0 R /Annots [<< /Type /Annot /Subtype /Text /Rect [0 0 5 5] >>] >>",
            "<< /Length 14 >>\nstream\nq /F1 Do Q 1 g\nendstream",
            "<< /Length 22 >>\nstream\n/Pattern cs /P1 scn 0 0\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static List<PdfXValidator.PageScan> Scans(PdfDocument doc, bool collectContent,
        int maxPageContentBytes = PdfXValidator.DefaultMaxPageContentBytes)
    {
        long documentBytes = 0;
        var scans = new List<PdfXValidator.PageScan>();
        for (int p = 0; p < doc.Pages.Count; p++)
            scans.Add(PdfXValidator.ScanPage(doc, doc.Pages[p], maxPageContentBytes,
                ref documentBytes, PdfXValidator.DefaultMaxDocumentContentBytes, collectContent));
        return scans;
    }

    /// <summary>
    /// Two pages over one form of 28 bytes, their own content 5 and 35 bytes, under a 40-byte page
    /// ceiling: the first page's walk is shared, and the second, whose content leaves too little for the
    /// form, walks in full and refuses it — as it always did — rather than take a walk made for less.
    /// </summary>
    [Fact]
    public void APageWhoseContentLeavesTooLittle_WalksInFull()
    {
        string form = "1 0 0 rg 10 10 100 100 re f";
        string small = "q Q q";
        string large = new string('n', 33) + " Q";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [5 0 R 6 0 R] /Count 2 /MediaBox [0 0 100 100] /Resources 3 0 R >>",
            "<< /XObject << /F1 4 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Length " + form.Length + " >>\nstream\n" + form + "\nendstream",
            "<< /Type /Page /Parent 2 0 R /Contents 7 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 8 0 R >>",
            "<< /Length " + small.Length + " >>\nstream\n" + small + "\nendstream",
            "<< /Length " + large.Length + " >>\nstream\n" + large + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        byte[] pdf = Encoding.ASCII.GetBytes(sb.ToString());

        List<PdfXValidator.PageScan> full;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf))) full = Scans(doc, true, 40);
        List<PdfXValidator.PageScan> shared;
        using (var doc = PdfDocument.Open(new MemoryStream(pdf)))
        using (ScanReuse.Begin()) shared = Scans(doc, true, 40);

        Assert.Null(full[0].Incomplete);                       // the form fits beside five bytes
        Assert.NotNull(full[1].Incomplete);                    // and not beside thirty-five
        Assert.Equal(full[1].Incomplete, shared[1].Incomplete);
        Assert.Equal(full[1].ContentStreams.Count, shared[1].ContentStreams.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ASharedWalk_IsTheFullWalksScan(bool collectContent)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Document()));
        var full = Scans(doc, collectContent);   // outside a scope: every page walked in full

        List<PdfXValidator.PageScan> shared;
        int hits;
        using (ScanReuse.Begin())
        {
            shared = Scans(doc, collectContent);
            hits = ScanReuse.Current!.SharedWalkHits;
        }

        Assert.Equal(2, hits);   // pages two and three take page one's walk; four has an annotation
        for (int p = 0; p < full.Count; p++)
        {
            Assert.Equal(full[p].Resources, shared[p].Resources);
            Assert.Equal(full[p].ContentStreams.Count, shared[p].ContentStreams.Count);
            for (int s = 0; s < full[p].ContentStreams.Count; s++)
                Assert.Equal(full[p].ContentStreams[s], shared[p].ContentStreams[s]);
            Assert.Equal(full[p].ContentStreamResources, shared[p].ContentStreamResources);
            Assert.Equal(full[p].ContentStreamSources, shared[p].ContentStreamSources);
            Assert.Equal(full[p].PageContentIndex, shared[p].PageContentIndex);
            Assert.Equal(full[p].Budget, shared[p].Budget);
            Assert.Equal(full[p].Incomplete, shared[p].Incomplete);
        }
        // Potency: the walk reaches the form's resources, and with content the form and the pattern.
        Assert.True(full[0].Resources.Count >= 2);
        if (collectContent)
        {
            Assert.Contains(full[0].ContentStreamSources, s => s != null && s.ObjectNumber == 4);
            Assert.Contains(full[0].ContentStreamSources, s => s != null && s.ObjectNumber == 5);
            Assert.Equal(0, full[0].PageContentIndex);
            Assert.Equal(-1, full[2].PageContentIndex);   // the page with no content of its own
        }
    }
}
