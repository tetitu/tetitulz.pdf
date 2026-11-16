// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Extraction;

/// <summary>
/// Nested marked-content sections do their reversal and <c>/ActualText</c> work once, in the
/// outermost section that does it: an enclosing <c>/ReversedChars</c> marks the inner glyphs again,
/// and an enclosing <c>/ActualText</c> replaces them. Each <c>EMC</c> re-walked every glyph since its
/// section began, so N nested sections around G glyphs cost N × G — the work asserted here, since
/// the answer is the same either way.
/// </summary>
public sealed class NestedMarkedContentWorkTests
{
    [Fact]
    public void AdversarialNestedReversedChars_MarkTheGlyphsOnce()
    {
        const int depth = 3000, glyphs = 600;
        string content = "BT /F1 1 Tf 72 700 Td " + Repeat("/ReversedChars BMC ", depth) +
                         "(" + new string('a', glyphs) + ") Tj " + Repeat("EMC ", depth) + "ET";

        PdfStructuredTextExtractor.MarkedContentWork = 0;
        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(Page(content)));

        Assert.InRange(PdfStructuredTextExtractor.MarkedContentWork, glyphs, 2L * glyphs);
        Assert.Contains(new string('a', 20), string.Concat(layout[0].Lines.Select(l => l.Text)), StringComparison.Ordinal);
    }

    [Fact]
    public void AdversarialNestedActualText_ReplacesOnce_WithTheOutermostText()
    {
        const int depth = 500;
        string inner = new string('x', 1000);
        var content = new StringBuilder("BT /F1 12 Tf 72 700 Td ");
        content.Append("/Span << /ActualText (Outer) >> BDC ");
        for (int i = 1; i < depth; i++) content.Append("/Span << /ActualText (").Append(inner).Append(") >> BDC ");
        content.Append("(glyphs) Tj ").Append(Repeat("EMC ", depth)).Append("ET");

        PdfStructuredTextExtractor.MarkedContentWork = 0;
        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(Page(content.ToString())));

        Assert.InRange(PdfStructuredTextExtractor.MarkedContentWork, 1, 100);
        Assert.Equal("Outer", string.Concat(layout[0].Lines.Select(l => l.Text)).Trim());
    }

    /// <summary>The control: an inner /ActualText inside an outer /ReversedChars is applied, and marked.</summary>
    [Fact]
    public void AnActualTextInsideReversedChars_IsStillApplied()
    {
        string content = "BT /F1 12 Tf 72 700 Td /ReversedChars BMC /Span << /ActualText (Said) >> BDC (drawn) Tj EMC EMC ET";

        var layout = PdfStructuredTextExtractor.Extract(new MemoryStream(Page(content)));

        string text = string.Concat(layout[0].Lines.Select(l => l.Text));
        Assert.DoesNotContain("drawn", text, StringComparison.Ordinal);
        Assert.True(text.Contains("Said", StringComparison.Ordinal) || text.Contains("diaS", StringComparison.Ordinal),
            $"the /ActualText was not applied: {text}");
    }

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    private static byte[] Page(string content)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
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
