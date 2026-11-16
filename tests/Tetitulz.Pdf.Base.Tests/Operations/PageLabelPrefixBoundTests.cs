// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A page-label range's <c>/P</c> prefix is written onto every page of the range, so its length
/// times the page count is a product the file chooses on both sides. The reader cuts the prefix at
/// <see cref="PdfLimits.MaxPageLabelLength"/> and says so; the control, a short prefix, is kept
/// whole with nothing reported.
/// </summary>
public class PageLabelPrefixBoundTests
{
    [Fact]
    public void AdversarialPrefix_IsCutAtTheCeiling_AndSaid()
    {
        int cap = PdfLimits.MaxPageLabelLength;
        string prefix = new string('A', cap + 500);

        var labels = PdfOperations.GetPageLabels(new MemoryStream(ThreePages(prefix)));

        Assert.NotNull(labels);
        Assert.Equal(3, labels!.Count);
        Assert.All(labels, l => Assert.True(l.Length <= cap + 1, $"a label of {l.Length} characters"));
        Assert.All(labels, l => Assert.StartsWith(new string('A', cap), l, StringComparison.Ordinal));
        Assert.Equal(new[] { "1", "2", "3" }, labels.Select(l => l.Substring(cap)).ToArray());
        Assert.Contains("/P prefix", PdfPageLabels.LastReadError ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void AShortPrefix_IsKeptWhole_AndNothingIsSaid()
    {
        var labels = PdfOperations.GetPageLabels(new MemoryStream(ThreePages("A-")));

        Assert.Equal(new[] { "A-1", "A-2", "A-3" }, labels);
        Assert.Null(PdfPageLabels.LastReadError);
    }

    private static byte[] ThreePages(string prefix)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R /PageLabels << /Nums [0 << /S /D /P (" + prefix + ") >>] >> >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R 5 0 R] /Count 3 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>",
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
