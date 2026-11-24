// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.Maintenance.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// What the linearizer's closure walks cost is the references of every object they reach. Read again by
/// each walk, pages sharing an array of fifty thousand references to one object would examine fifty
/// thousand a page and collect one (2 000 pages: 24 s), and pages sharing an array naming every page
/// would examine pages² and collect nothing (4 000 pages: 4.7 s). Each object's references are read once
/// and de-duplicated (0.24 s and 0.26 s), and the references examined are charged to
/// <see cref="PdfLimits.MaxLinearizationEdges"/>.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public sealed class LinearizerEdgeCostTests
{
    /// <summary>
    /// Two hundred pages sharing ten thousand references to one object examine a handful of references a
    /// page — the page's <c>/Resources</c>, its <c>/Extra</c>, the one distinct target — not ten thousand.
    /// </summary>
    [Fact]
    public void ARepeatedReference_IsExaminedOnceAPage_NotOnceAnOccurrence()
    {
        LinearizationParts parts = Build(SharedArray(200, 10_000, toPages: false));

        Assert.Equal(200, 1 + parts.Part7.Count);
        Assert.True(parts.EdgesExamined < 20_000, "examined " + parts.EdgesExamined + " references");
    }

    /// <summary>
    /// Three hundred pages sharing an array that names every page examine some ninety thousand references:
    /// under a ceiling of fifty thousand they are refused for that reason, and the control, a ceiling of a
    /// million, linearizes the same document.
    /// </summary>
    [Fact]
    public void PagesExaminingMoreReferencesThanTheCeiling_AreRefused_AndUnderItTheyAreNot()
    {
        byte[] pdf = SharedArray(300, 0, toPages: true);
        try
        {
            PdfLimits.MaxLinearizationEdges = 50_000;
            using var refused = new MemoryStream();
            var ex = Assert.Throws<PdfException>(() => PdfLinearizer.Linearize(new MemoryStream(pdf), refused));
            Assert.Contains("MaxLinearizationEdges", ex.Message);
            Assert.Equal(0, refused.Length);

            PdfLimits.MaxLinearizationEdges = 1_000_000;
            using var written = new MemoryStream();
            PdfLinearizer.Linearize(new MemoryStream(pdf), written);
            Assert.Contains("/Linearized", Encoding.ASCII.GetString(written.ToArray(), 0, (int)System.Math.Min(written.Length, 1024)));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    private static LinearizationParts Build(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        var all = new Dictionary<int, PdfObject>();
        foreach (var kv in doc.Parser.EnumerateAllObjects()) all[kv.Key] = kv.Value;
        return LinearizationParts.Build(doc, all);
    }

    /// <summary>
    /// <paramref name="pages"/> pages sharing one <c>/Resources</c> whose <c>/Extra</c> is an array (object 4):
    /// with <paramref name="toPages"/> false, <paramref name="refs"/> references to one small dictionary
    /// (object 5); with it true, a reference to every page — objects a page closure may not enter.
    /// </summary>
    private static byte[] SharedArray(int pages, int refs, bool toPages)
    {
        int firstPage = 6;
        var bodies = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };
        var kids = new StringBuilder();
        for (int p = 0; p < pages; p++) kids.Append(firstPage + p).Append(" 0 R ");
        bodies.Add($"<< /Type /Pages /Kids [{kids}] /Count {pages} /MediaBox [0 0 612 792] >>");
        bodies.Add("<< /Extra 4 0 R >>");
        var array = new StringBuilder("[");
        if (toPages) for (int p = 0; p < pages; p++) array.Append(firstPage + p).Append(" 0 R ");
        else for (int i = 0; i < refs; i++) array.Append("5 0 R ");
        bodies.Add(array.Append(']').ToString());
        bodies.Add("<< /K 1 >>");
        for (int p = 0; p < pages; p++) bodies.Add("<< /Type /Page /Parent 2 0 R /Resources 3 0 R >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++) { offsets.Add(sb.Length); sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n"); }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
