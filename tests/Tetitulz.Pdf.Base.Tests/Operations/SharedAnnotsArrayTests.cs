// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Base.Tests.TestInfrastructure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The annotation entries a whole-document read examines are bounded together
/// (<see cref="PdfLimits.MaxAnnotationsPerDocument"/>): pages may name one <c>/Annots</c> array, so
/// what the pages name between them is pages × entries — 800 pages over one array of 800 links took
/// the link extractor 1.8 s against 99 ms for 200. Past the ceiling the rest are not examined and the
/// caller is told, by the extractors' <c>LastError</c> and the editor's warnings.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class SharedAnnotsArrayTests
{
    private const int Pages = 20, Links = 10;

    [Fact]
    public void AdversarialPagesSharingOneAnnotsArray_AreReadUpToTheDocumentCeiling_AndSaid()
    {
        byte[] pdf = Document();
        try
        {
            PdfLimits.MaxAnnotationsPerDocument = 95;   // not a multiple of a page, so one page is cut part-way

            var links = PdfLinkExtractor.ExtractLinks(new MemoryStream(pdf));
            Assert.Equal(95, links.Count);
            Assert.Equal(5, links.Count(l => l.PageIndex == 9));
            Assert.Contains("from page 9 on", PdfLinkExtractor.LastError, System.StringComparison.Ordinal);

            var listed = PdfAnnotationExtractor.ListAnnotations(new MemoryStream(pdf));
            Assert.Equal(95, listed.Count);
            Assert.Contains("from page 9 on", PdfAnnotationExtractor.LastError, System.StringComparison.Ordinal);

            var removed = PdfAnnotationEditor.Remove(new MemoryStream(pdf), new MemoryStream(), (_, __) => true);
            Assert.Equal(95, removed.Affected);
            Assert.Contains(removed.Warnings, w => w.Contains("from page 9 on", System.StringComparison.Ordinal));
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }

    /// <summary>The control: under the default ceiling every entry of every page is read, and nothing is said.</summary>
    [Fact]
    public void UnderTheDefaultCeiling_EveryEntryOfEveryPageIsRead()
    {
        byte[] pdf = Document();

        Assert.Equal(Pages * Links, PdfLinkExtractor.ExtractLinks(new MemoryStream(pdf)).Count);
        Assert.Null(PdfLinkExtractor.LastError);
        Assert.Equal(Pages * Links, PdfAnnotationExtractor.ListAnnotations(new MemoryStream(pdf)).Count);
        Assert.Null(PdfAnnotationExtractor.LastError);
        Assert.Equal(Pages * Links, PdfAnnotationEditor.Remove(new MemoryStream(pdf), new MemoryStream(), (_, __) => true).Affected);
    }

    /// <summary><see cref="Pages"/> pages all naming one /Annots array of <see cref="Links"/> URI links.</summary>
    private static byte[] Document()
    {
        var bodies = new List<string>();
        int firstAnnot = 4, firstPage = firstAnnot + Links, content = firstPage + Pages;
        var kids = new StringBuilder();
        for (int p = 0; p < Pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        var refs = new StringBuilder();
        for (int i = 0; i < Links; i++) refs.Append(firstAnnot + i).Append(" 0 R ");
        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {Pages} /MediaBox [0 0 612 792] >>");
        bodies.Add($"[{refs}]");
        for (int i = 0; i < Links; i++)
            bodies.Add($"<< /Type /Annot /Subtype /Link /Rect [{i} 0 {i + 1} 1] /A << /S /URI /URI (http://example.com/{i}) >> >>");
        for (int p = 0; p < Pages; p++) bodies.Add($"<< /Type /Page /Parent 2 0 R /Contents {content} 0 R /Annots 3 0 R >>");
        bodies.Add("<< /Length 0 >>\nstream\n\nendstream");

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
