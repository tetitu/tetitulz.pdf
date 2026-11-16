// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The content filter, taking painting text out, keeps the pen where that text left it when a
/// clipping run follows in the same text object: the removed run is written in mode 3, so the kept
/// clip is set where the page set it, where it moved by the removed run's advance. The control, a
/// run with no clipping text after it, is dropped outright.
/// </summary>
public class ContentFilterClipAdvanceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARemovedRunBeforeAClippingRun_KeepsThePenWhereItWas(bool clipFollows)
    {
        string content = clipFollows
            ? "BT /F1 24 Tf 50 400 Td (AB) Tj 7 Tr (C) Tj ET 0 0 1 rg 0 0 612 792 re f"
            : "BT /F1 24 Tf 50 400 Td (AB) Tj ET 0 0 1 rg 0 0 612 792 re f";
        using var output = new MemoryStream();
        PdfContentFilter.Remove(new MemoryStream(Pdf(content)), output, PdfContentClass.Text);

        List<ContentStreamOperation> ops = Ops(output.ToArray());
        int ab = ops.FindIndex(o => o.Operator == PdfOperators.ShowText && Shown(o) == "AB");
        int c = ops.FindIndex(o => o.Operator == PdfOperators.ShowText && Shown(o) == "C");

        if (clipFollows)
        {
            Assert.True(ab >= 0, "the removed run is gone, and the clip after it is set where its advance was not made");
            Assert.True(c > ab);
            Assert.Equal("3", ModeBefore(ops, ab));
            Assert.Equal("7", ModeBefore(ops, c));
        }
        else
        {
            Assert.Equal(-1, ab);
        }
    }

    private static string ModeBefore(List<ContentStreamOperation> ops, int at)
    {
        for (int i = at - 1; i >= 0; i--)
            if (ops[i].Operator == PdfOperators.SetTextRenderMode)
                return ((PdfNumber)ops[i].Operands[0]).IntValue.ToString();
        return "none";
    }

    private static string Shown(ContentStreamOperation op) => (op.Operands[0] as PdfString)?.GetText();

    private static List<ContentStreamOperation> Ops(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        return ContentStreamParser.Parse(doc.Pages[0].GetContentStreamData());
    }

    private static byte[] Pdf(string content)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var sb = new System.Text.StringBuilder("%PDF-1.7\n");
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
        return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
    }
}
