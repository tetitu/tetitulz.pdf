// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <see cref="PdfTemplateEngine.ReplaceRun"/> says what it did not do: a page the document does
/// not have is refused, a page with no content reports the elements as not found, and an id among
/// several that names no text is named in the warnings while the rest are replaced.
/// </summary>
public class ReplaceRunScopeTests
{
    [Fact]
    public void APagePastTheEnd_IsRefused()
    {
        using var output = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfTemplateEngine.ReplaceRun(new MemoryStream(Pdf("BT /F1 12 Tf 20 100 Td (Total) Tj ET")), output, 5, new[] { 0 }, "x", Options()));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void AnEmptyPage_ReportsTheElementsNotFound()
    {
        using var output = new MemoryStream();
        var result = PdfTemplateEngine.ReplaceRun(new MemoryStream(Pdf("")), output, 0, new[] { 0 }, "x", Options());

        Assert.False(result.ChangedAnything);
        Assert.Contains("element 0 of page 1", Assert.Single(result.NotFoundKeys), StringComparison.Ordinal);
        Assert.Contains(result.Warnings, w => w.Contains("no content", StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdThatNamesNoText_IsPassedOverAndSaid_WhileTheRestIsReplaced()
    {
        byte[] pdf = Pdf("BT /F1 12 Tf 20 100 Td (Total) Tj ET");
        var run = PdfPageElements.List(new MemoryStream(pdf), 0).Single(e => e.Text == "Total");

        using var output = new MemoryStream();
        var result = PdfTemplateEngine.ReplaceRun(new MemoryStream(pdf), output, 0, new[] { run.Id, 999 }, "Sum", Options());

        Assert.True(result.ChangedAnything, string.Join("; ", result.Warnings));
        Assert.Equal("Total", Assert.Single(result.ReplacedKeys));
        Assert.Contains(result.Warnings, w => w.Contains("999", StringComparison.Ordinal) && w.Contains("passed over", StringComparison.Ordinal));
    }

    private static TemplateReplaceOptions Options() =>
        new TemplateReplaceOptions { FontProvider = _ => MinimalTrueType.Build("Sumx") };

    private static byte[] Pdf(string content)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            "<< /Length " + content.Length + " >>\nstream\n" + content + "\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
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
