// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations.Structure;

/// <summary>
/// <see cref="OutlineItems.Walk"/>: the one bounded walk of an outline, which the action dispatcher,
/// the PDF/UA-1 checker and the PDF/UA-2 checker share.
/// </summary>
public class OutlineItemsTests
{
    /// <summary>An item, then its children, then its next sibling.</summary>
    [Fact]
    public void ItemsAreVisitedDepthFirstInReadingOrder()
    {
        using var doc = Outline(
            "<< /First 3 0 R /Last 6 0 R >>",
            "<< /Title (A) /First 4 0 R /Last 5 0 R /Next 6 0 R >>",
            "<< /Title (A1) /Next 5 0 R >>",
            "<< /Title (A2) >>",
            "<< /Title (B) >>");

        Assert.Equal(new[] { "A", "A1", "A2", "B" }, Titles(doc));
    }

    /// <summary>A /Next that loops back, and a /First naming an ancestor, visit each item once.</summary>
    [Fact]
    public void ACycleVisitsEachItemOnce()
    {
        using var doc = Outline(
            "<< /First 3 0 R >>",
            "<< /Title (A) /First 3 0 R /Next 4 0 R >>",
            "<< /Title (B) /Next 3 0 R >>");

        Assert.Equal(new[] { "A", "B" }, Titles(doc));
    }

    /// <summary>The walk stops when the visitor says so.</summary>
    [Fact]
    public void TheVisitorStopsTheWalk()
    {
        using var doc = Outline(
            "<< /First 3 0 R >>",
            "<< /Title (A) /First 4 0 R /Next 5 0 R >>",
            "<< /Title (A1) >>",
            "<< /Title (B) >>");

        var seen = new List<string>();
        OutlineItems.Walk(doc, doc.Catalog.Dictionary, item => { seen.Add(Title(item)); return seen.Count < 2; });

        Assert.Equal(new[] { "A", "A1" }, seen);
    }

    /// <summary>
    /// A chain deeper than <see cref="PdfLimits.MaxTreeDepth"/> is cut, and the cut is reported once;
    /// a chain within it, and a flat sibling list far longer, are walked whole with no report.
    /// </summary>
    [Theory]
    [InlineData(true, 30)]
    [InlineData(false, 30)]
    public void HostileOutline_ADeepChainIsCutAndReportedOnce_AFlatOneIsNot(bool deep, int extra)
    {
        int count = PdfLimits.MaxTreeDepth + extra;
        var bodies = new List<string> { "<< /First 3 0 R >>" };
        for (int i = 0; i < count; i++)
        {
            string link = i + 1 < count ? (deep ? " /First " : " /Next ") + (i + 4).ToString(CultureInfo.InvariantCulture) + " 0 R" : "";
            bodies.Add("<< /Title (" + i.ToString(CultureInfo.InvariantCulture) + ")" + link + " >>");
        }
        using var doc = Outline(bodies.ToArray());

        var cuts = new List<string>();
        int visited = 0;
        OutlineItems.Walk(doc, doc.Catalog.Dictionary, _ => { visited++; return true; }, cuts.Add);

        Assert.Equal(deep ? PdfLimits.MaxTreeDepth : count, visited);
        Assert.Equal(deep ? 1 : 0, cuts.Count);
    }

    /// <summary>A catalog with no outline is walked as empty.</summary>
    [Fact]
    public void NoOutlineVisitsNothing()
    {
        using var doc = Outline();
        Assert.Empty(Titles(doc));
    }

    // ---------------------------------------------------------------- fixtures

    private static List<string> Titles(PdfDocument doc)
    {
        var titles = new List<string>();
        OutlineItems.Walk(doc, doc.Catalog.Dictionary, item => { titles.Add(Title(item)); return true; });
        return titles;
    }

    private static string Title(PdfDictionary item) => (item[PdfName.Title] as PdfString)?.GetText();

    /// <summary>Catalog 1 (its /Outlines object 2 when <paramref name="outline"/> is given), then the outline's objects from 2.</summary>
    private static PdfDocument Outline(params string[] outline)
    {
        int pages = outline.Length + 2;
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages " + pages.ToString(CultureInfo.InvariantCulture) + " 0 R"
                + (outline.Length > 0 ? " /Outlines 2 0 R" : "") + " >>",
        };
        bodies.AddRange(outline);
        bodies.Add("<< /Type /Pages /Kids [] /Count 0 >>");

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
    }
}
