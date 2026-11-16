// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A page's redaction areas are bucketed on a grid: text redaction makes an area of every match, and
/// each glyph, path, image and annotation was tested against the whole list — glyphs × areas a page.
/// </summary>
public sealed class RedactionAreaIndexTests
{
    /// <summary>
    /// Two thousand glyphs, each under its own area, and one outside them all: every covered glyph is
    /// removed and the outside one kept, with the box tests near the glyph count — the whole list per
    /// glyph was four million.
    /// </summary>
    [Fact]
    public void AdversarialPageOfManyAreas_TestsEachGlyphAgainstTheAreasNearIt()
    {
        const int glyphs = 2_000;
        var content = new StringBuilder();
        var areas = new List<PdfRedactionArea>();
        for (int i = 0; i < glyphs; i++)
        {
            float x = 20 + (i % 50) * 10, y = 700 - (i / 50) * 12;
            content.Append("BT /F1 10 Tf ").Append(x).Append(' ').Append(y).Append(" Td (A) Tj ET\n");
            areas.Add(new PdfRedactionArea { PageIndex = 0, X = x - 1, Y = y - 3, Width = 8, Height = 11 });
        }
        content.Append("BT /F1 10 Tf 560 40 Td (KEPTX) Tj ET\n");

        PdfRedactionEngine.AreaTests = 0;
        byte[] produced = Redact(Pdf(content.ToString()), areas);

        string text = TextOf(produced);
        Assert.DoesNotContain("A", text.Replace("KEPTX", ""), System.StringComparison.Ordinal);
        Assert.Contains("KEPTX", text, System.StringComparison.Ordinal);
        Assert.InRange(PdfRedactionEngine.AreaTests, glyphs, 40L * glyphs);   // was 2 000 × 2 000
    }

    /// <summary>The control: a page of a few areas — under the index's threshold — redacts as the scan did.</summary>
    [Fact]
    public void AFewAreas_RedactAsBefore()
    {
        string content = "BT /F1 10 Tf 100 700 Td (GONEX) Tj ET\nBT /F1 10 Tf 100 600 Td (KEPTX) Tj ET\n";
        byte[] produced = Redact(Pdf(content), new List<PdfRedactionArea>
        {
            new PdfRedactionArea { PageIndex = 0, X = 95, Y = 695, Width = 60, Height = 15 },
        });

        string text = TextOf(produced);
        Assert.DoesNotContain("GONEX", text, System.StringComparison.Ordinal);
        Assert.Contains("KEPTX", text, System.StringComparison.Ordinal);
    }

    private static byte[] Redact(byte[] pdf, IReadOnlyList<PdfRedactionArea> areas)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        PdfRedactionEngine.Redact(input, output, areas);
        return output.ToArray();
    }

    private static string TextOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return PdfTextExtractor.ExtractText(doc);
    }

    private static byte[] Pdf(string content)
    {
        var bodies = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + Encoding.Latin1.GetByteCount(content) + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Length + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
