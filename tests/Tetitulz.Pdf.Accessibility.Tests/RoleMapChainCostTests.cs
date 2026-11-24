// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The deep checker follows <c>/RoleMap</c> chains with no hop cap, and walks each chain once: a chain asked from
/// each of its entries, and from every element of its first type, is answered from the roles it passed.
/// </summary>
public class RoleMapChainCostTests
{
    private const int Entries = 2_000;

    /// <summary>
    /// One chain R0 → R1 → … of two thousand entries, with two hundred elements of type R0: walked from each entry and
    /// each element it is about 2 million steps; walked once it is a few thousand. A chain ending at a standard type
    /// is resolved (no UA13); the control, ending at a type the map does not name, is reported.
    /// </summary>
    [Theory]
    [InlineData("/P", false)]
    [InlineData("/Custom", true)]
    public void AdversarialLongRoleMapChain_IsWalkedOnce(string end, bool unresolved)
    {
        long before = PdfDeepAccessibilityChecker.RoleChainSteps;
        var report = PdfAccessibility.Audit(new MemoryStream(ChainPdf(end, elements: 200)));
        long steps = PdfDeepAccessibilityChecker.RoleChainSteps - before;

        Assert.InRange(steps, Entries, 5L * Entries);
        Assert.Equal(unresolved, report.Issues.Any(i => i.Code == "UA13"));
    }

    private static byte[] ChainPdf(string end, int elements)
    {
        var rm = new StringBuilder("<<");
        for (int i = 0; i < Entries; i++) rm.Append($" /R{i} ").Append(i == Entries - 1 ? end : $"/R{i + 1}");
        rm.Append(" >>");
        var kids = new StringBuilder("[");
        for (int i = 0; i < elements; i++) kids.Append(" << /Type /StructElem /S /R0 /P 5 0 R >>");
        kids.Append(" ]");

        var pdf = new MemoryStream();
        void W(string s) => pdf.Write(Encoding.ASCII.GetBytes(s));
        var off = new long[5];
        W("%PDF-1.7\n");
        off[0] = pdf.Position; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 5 0 R /MarkInfo << /Marked true >> >>\nendobj\n");
        off[1] = pdf.Position; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        off[2] = pdf.Position; W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R >>\nendobj\n");
        off[3] = pdf.Position; W("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");
        off[4] = pdf.Position; W($"5 0 obj\n<< /Type /StructTreeRoot /RoleMap {rm} /K {kids} >>\nendobj\n");
        long x = pdf.Position;
        W("xref\n0 6\n0000000000 65535 f \n");
        foreach (long o in off) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{x}\n%%EOF\n");
        return pdf.ToArray();
    }
}
