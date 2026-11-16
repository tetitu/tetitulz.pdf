// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A ruled cell holding two lines of text reads line by line, top to bottom: its words ordered by
/// x alone interleaved the lines ("Wireless with USB keyboard receiver"). The control, a one-line
/// cell, reads as before.
/// </summary>
public class TableCellLinesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATwoLineCell_ReadsLineByLine(bool bidi)
    {
        using var ms = new MemoryStream(Grid(), writable: false);
        var table = Assert.Single(PdfTableExtractor.Extract(ms,
            new TableExtractionOptions { Strategy = TableStrategy.Lines, ApplyBidi = bidi }));

        Assert.Equal("Wireless keyboard with USB receiver", table.Rows[0].Cells[0].Text);
        Assert.Equal("B", table.Rows[0].Cells[1].Text);
        Assert.Equal("C", table.Rows[1].Cells[0].Text);
    }

    /// <summary>
    /// A 2×2 grid — columns at x = 50/200/350, rows at y = 700/672/644 — whose top-left cell draws
    /// two lines in Helvetica 10.
    /// </summary>
    private static byte[] Grid()
    {
        const string content =
            "50 700 m 350 700 l S\n50 672 m 350 672 l S\n50 644 m 350 644 l S\n" +
            "50 700 m 50 644 l S\n200 700 m 200 644 l S\n350 700 m 350 644 l S\n" +
            "BT /F1 10 Tf 55 690 Td (Wireless keyboard) Tj ET\n" +
            "BT /F1 10 Tf 55 678 Td (with USB receiver) Tj ET\n" +
            "BT /F1 10 Tf 205 686 Td (B) Tj ET\n" +
            "BT /F1 10 Tf 55 656 Td (C) Tj ET\n" +
            "BT /F1 10 Tf 205 656 Td (D) Tj ET\n";
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new StringBuilder("%PDF-1.4\n");
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
