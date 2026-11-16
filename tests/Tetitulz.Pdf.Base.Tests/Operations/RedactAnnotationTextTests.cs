// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Search-and-redact reads what a reader sees, the text an annotation's appearance draws included:
/// a filled field whose appearance says TOPSECRET is found, its area lies in the widget's rectangle,
/// the engine drops the widget, and the result's appearances say nothing of it. The page's own
/// content, which never said it, is kept. A search that read the content stream alone found no
/// match and copied the document, telling the caller "0 matches" of a word still on the page.
/// </summary>
public sealed class RedactAnnotationTextTests
{
    [Fact]
    public void TextAnAppearanceDraws_IsFoundAndRemoved()
    {
        byte[] pdf = Pdf();

        var found = PdfTextRedactor.FindMatches(new MemoryStream(pdf), new[] { "TOPSECRET" });
        var match = Assert.Single(found);
        Assert.Equal(0, match.PageIndex);
        Assert.InRange(match.Area.X, 100, 300);
        Assert.InRange(match.Area.Y, 100, 130);

        using var output = new MemoryStream();
        var removed = PdfTextRedactor.Redact(new MemoryStream(pdf), output, "TOPSECRET");
        Assert.Single(removed);

        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(output.ToArray()),
            new WordAssemblyOptions { IncludeAnnotationAppearances = true });
        string text = string.Join("\n", layout.SelectMany(p => p.Lines).Select(l => l.Text));
        Assert.DoesNotContain("TOPSECRET", text);
        Assert.Contains("Name:", text);
    }

    /// <summary>The control: with the appearances left out, the extractor reads the page's content alone.</summary>
    [Fact]
    public void TheExtractor_ReadsAppearancesOnlyWhenAsked()
    {
        byte[] pdf = Pdf();
        string without = string.Join("\n", PdfStructuredTextExtractor.Extract(new MemoryStream(pdf)).SelectMany(p => p.Lines).Select(l => l.Text));
        string with = string.Join("\n", PdfStructuredTextExtractor.Extract(new MemoryStream(pdf),
            new WordAssemblyOptions { IncludeAnnotationAppearances = true }).SelectMany(p => p.Lines).Select(l => l.Text));
        Assert.DoesNotContain("TOPSECRET", without);
        Assert.Contains("TOPSECRET", with);
        // Placed where the widget stands: inside its /Rect, through the appearance's /BBox fit.
        var word = PdfStructuredTextExtractor.Extract(new MemoryStream(pdf), new WordAssemblyOptions { IncludeAnnotationAppearances = true })[0]
            .Lines.SelectMany(l => l.Words).Single(w => w.Text == "TOPSECRET");
        Assert.InRange(word.X, 100, 300);
        Assert.InRange(word.Y, 100, 130);
    }

    /// <summary>A page saying "Name:" and a filled text widget whose appearance draws TOPSECRET in [100 100 300 130].</summary>
    private static byte[] Pdf()
    {
        const string content = "BT /F1 12 Tf 50 200 Td (Name:) Tj ET";
        const string ap = "/Tx BMC BT /F1 12 Tf 2 4 Td (TOPSECRET) Tj ET EMC";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /DR << /Font << /F1 6 0 R >> >> >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 300] /Contents 4 0 R /Resources << /Font << /F1 6 0 R >> >> /Annots [5 0 R] >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (secret) /V (TOPSECRET) /Rect [100 100 300 130] /F 4 /AP << /N 7 0 R >> >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 200 30] /Resources << /Font << /F1 6 0 R >> >> /Length " + ap.Length + " >>\nstream\n" + ap + "\nendstream",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
