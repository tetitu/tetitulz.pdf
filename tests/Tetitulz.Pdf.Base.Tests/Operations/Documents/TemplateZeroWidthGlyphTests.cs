// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// The template engine measures a glyph by the one width rule (<c>GlyphWidth.Units</c>): an explicit
/// 0 in <c>/Widths</c> is a real width, as the renderers and the extractors read it. Modelled at
/// half an em, the erasure's <c>TJ</c> moved the glyphs after the placeholder by the difference.
/// </summary>
public class TemplateZeroWidthGlyphTests
{
    [Fact]
    public void AnExplicitZeroWidth_LeavesTheGlyphAfterTheErasureWhereTheFontPutsIt()
    {
        using var output = new MemoryStream();
        var result = PdfTemplateEngine.ReplaceText(new MemoryStream(Pdf()), output,
            new Dictionary<string, string> { ["AB"] = "" },
            new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("x") });
        Assert.Contains("AB", result.ReplacedKeys);

        var c = PdfStructuredTextExtractor.Extract(new MemoryStream(output.ToArray()))[0].Chars
            .Single(ch => ch.Char == "C");

        // A is 600 units at 10 pt and B is 0 wide, so C is drawn at 72 + 6 + 0 = 78.
        Assert.InRange(c.X, 77.5f, 78.5f);
    }

    /// <summary>F1 declares A, B, C as 600, 0 and 600 units; the page shows "ABC".</summary>
    private static byte[] Pdf()
    {
        const string content = "BT /F1 10 Tf 72 700 Td (ABC) Tj ET\n";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /TrueType /BaseFont /Helvetica /FirstChar 65 /LastChar 67 /Widths [600 0 600] /Encoding /WinAnsiEncoding >>",
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
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
