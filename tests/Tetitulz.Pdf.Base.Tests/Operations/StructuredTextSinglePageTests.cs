// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The single-page door of the structured extractor extracts its page alone. It extracted every
/// page and returned one: a page of a long document cost the document, and a caller asking page by
/// page paid the square of its length.
/// </summary>
public class StructuredTextSinglePageTests
{
    /// <summary>
    /// Each page answers what the whole-document door answers for it, and the line grouping it
    /// leaves behind is its own — the one-glyph page's work is less than the long page's, where
    /// extracting every page left the last page's work behind whichever page was asked.
    /// </summary>
    [Fact]
    public void APageIsExtractedAlone_AndAnswersAsTheWholeDocumentDoorDoesForIt()
    {
        byte[] pdf = TwoPages();
        var whole = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf));

        var first = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 0);
        int firstWork = PdfStructuredTextExtractor.LastLineGroupingWork;
        var second = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), 1);
        int secondWork = PdfStructuredTextExtractor.LastLineGroupingWork;

        Assert.Equal(Texts(whole[0]), Texts(first));
        Assert.Equal(Texts(whole[1]), Texts(second));
        Assert.Equal("A", Texts(first).Single());
        Assert.True(firstWork < secondWork, $"page 1 left {firstWork} steps, page 2 {secondWork}");
    }

    [Fact]
    public void APageOutsideTheDocument_IsRefusedAsAnArgument()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => PdfStructuredTextExtractor.Extract(new MemoryStream(TwoPages()), 2));
    }

    private static List<string> Texts(PdfPageTextLayout page) => page.Lines.Select(l => l.Text).ToList();

    /// <summary>A page showing one glyph, then a page of forty lines.</summary>
    private static byte[] TwoPages()
    {
        var longText = new StringBuilder("BT /F1 10 Tf 72 720 Td 12 TL");
        for (int i = 0; i < 40; i++) longText.Append(" (Line number ").Append(i).Append(" of the long page) '");
        longText.Append(" ET");
        string shortText = "BT /F1 10 Tf 72 720 Td (A) Tj ET";
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 /MediaBox [0 0 612 792] /Resources << /Font << /F1 7 0 R >> >> >>",
            "<< /Type /Page /Parent 2 0 R /Contents 5 0 R >>",
            "<< /Type /Page /Parent 2 0 R /Contents 6 0 R >>",
            $"<< /Length {shortText.Length} >>\nstream\n{shortText}\nendstream",
            $"<< /Length {longText.Length} >>\nstream\n{longText}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
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
}
