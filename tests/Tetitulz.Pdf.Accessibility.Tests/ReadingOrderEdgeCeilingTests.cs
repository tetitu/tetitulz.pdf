// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The reading-order fix examines a structure tree's <c>/K</c> slots under the walker's edge
/// ceiling, <see cref="PdfLimits.MaxStructTreeEdges"/>. It carried node and depth caps only, and
/// the visited sets bound how often an element is expanded, not the cost of expanding one: N
/// elements naming one shared N-entry <c>/K</c> array were N² slots, each classified and probed.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class ReadingOrderEdgeCeilingTests
{
    private const int Elements = 200;

    [Fact]
    public void AdversarialElementsSharingOneWideKArray_StopAtTheEdgeCeiling()
    {
        using var doc = PdfDocument.Open(new MemoryStream(SharedKids(Elements), writable: false));
        try
        {
            PdfLimits.MaxStructTreeEdges = 5_000;
            PdfReadingOrderAnalyzer.ReorderEdgesExamined = 0;

            PdfReadingOrderAnalyzer.Plan(doc, 100);

            // The root's 200 slots and 24 elements' 200 each; the 25th would pass the ceiling.
            Assert.Equal(5_000L, PdfReadingOrderAnalyzer.ReorderEdgesExamined);
            Assert.Contains("5000-entry ceiling", PdfReadingOrderAnalyzer.LastError ?? "");
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>The control: under the default ceiling every element's slots are examined.</summary>
    [Fact]
    public void ElementsSharingOneKArray_UnderTheCeiling_AreAllExamined()
    {
        using var doc = PdfDocument.Open(new MemoryStream(SharedKids(Elements), writable: false));
        PdfReadingOrderAnalyzer.ReorderEdgesExamined = 0;

        PdfReadingOrderAnalyzer.Plan(doc, 100);

        Assert.Equal((long)Elements + (long)Elements * Elements, PdfReadingOrderAnalyzer.ReorderEdgesExamined);
        Assert.DoesNotContain("-entry ceiling", PdfReadingOrderAnalyzer.LastError ?? "");
    }

    /// <summary>
    /// Object 1 catalog, 2 pages, 3 page, 4 struct tree root, 5 the shared <c>/K</c> array naming
    /// every element, 6 onward the elements, each with <c>/K 5 0 R</c>.
    /// </summary>
    private static byte[] SharedKids(int elements)
    {
        string refs = string.Join(" ", Enumerable.Range(6, elements).Select(n => n.ToString(CultureInfo.InvariantCulture) + " 0 R"));
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
            "<< /Type /StructTreeRoot /K [" + refs + "] >>",
            "[" + refs + "]",
        };
        for (int i = 0; i < elements; i++) bodies.Add("<< /Type /StructElem /S /P /P 4 0 R /Pg 3 0 R /K 5 0 R >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int size = bodies.Count + 1;
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
