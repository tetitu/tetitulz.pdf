// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Documents;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Documents;

/// <summary>
/// Page-tree operations against hostile input: <see cref="PdfPageManipulator.DeletePage"/> climbs the
/// <c>/Parent</c> chain under a visited set and the tree depth — a root naming itself as its parent
/// went round for ever, growing a list each turn; and <see cref="PdfNup.CreateNup"/> takes
/// <c>cols × rows</c> in long, where 65536 × 65536 wrapped to 0 and divided by it.
/// </summary>
public sealed class PageTreeHostileInputTests
{
    private static byte[] TwoPages(string rootExtra)
    {
        string[] bodies =
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2" + rootExtra + " >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>",
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
        foreach (int off in offsets) sb.Append(off.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Length + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void AdversarialParentRing_IsRefused_NotClimbedForEver()
    {
        var output = new MemoryStream();
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfException>(() =>
            PdfPageManipulator.DeletePage(new MemoryStream(TwoPages(" /Parent 2 0 R")), output, 0));
        Assert.Contains("ring", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);

        // Control: the same tree without the ring deletes the page.
        var ok = new MemoryStream();
        PdfPageManipulator.DeletePage(new MemoryStream(TwoPages("")), ok, 0);
        Assert.True(ok.Length > 0);
    }

    [Fact]
    public void AdversarialCellCountPastIntRange_IsAnArgumentError_NotADivisionByZero()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfNup.CreateNup(new MemoryStream(TwoPages("")), new MemoryStream(), 65536, 65536));

        var ok = new MemoryStream();
        PdfNup.CreateNup(new MemoryStream(TwoPages("")), ok, 2, 1);
        Assert.True(ok.Length > 0);
    }
}
