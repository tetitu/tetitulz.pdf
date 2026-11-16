// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// §9.4.4: a vertical font's run moves the text matrix down, by <c>w1</c> from <c>/W2</c> or
/// <c>/DW2</c> (else one em) with <c>Tc</c> shortening the step, and not along x — the renderers'
/// step, which the extractor took along x by <c>/W</c>. The horizontal control moves along x alone.
/// </summary>
public sealed class VerticalAdvanceTests
{
    [Theory]
    [InlineData("/Identity-V", "", 0, 0, -20)]
    [InlineData("/Identity-V", "/DW2 [880 -500]", 0, 0, -10)]
    [InlineData("/Identity-V", "/W2 [1 [-700 500 880]]", 0, 0, -17)]
    [InlineData("/Identity-V", "", 2, 0, -16)]
    [InlineData("/Identity-H", "", 0, 10, 0)]
    public void TwoGlyphs_MoveTheMatrixAsTheFontWrites(string encoding, string vertical, double charSpacing, double expectedX, double expectedY)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Pdf(encoding, vertical)));
        var fonts = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Resources[PdfName.Font]);
        PdfFont font = PdfFont.Create((PdfDictionary)doc.ResolveObject(fonts[new PdfName("F0")]), doc);

        var state = new TextState { FontSize = 10, CharacterSpacing = charSpacing };
        PdfTextExtractor.AdvanceTextMatrixWithWidths(state, font.SplitCodes(new byte[] { 0, 1, 0, 2 }), font);

        Assert.Equal(expectedX, state.TextMatrix.E, 6);
        Assert.Equal(expectedY, state.TextMatrix.F, 6);
    }

    private static byte[] Pdf(string encoding, string vertical)
    {
        const string content = "BT /F0 10 Tf 50 700 Td <00010002> Tj ET";
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 800] /Contents 4 0 R /Resources << /Font << /F0 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding " + encoding + " /DescendantFonts [6 0 R] >>",
            "<< /Type /Font /Subtype /CIDFontType0 /BaseFont /Test /CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
            "/DW 500 " + vertical + " >>",
        };
        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[objs.Count];
        for (int i = 0; i < objs.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objs[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objs.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(objs.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
