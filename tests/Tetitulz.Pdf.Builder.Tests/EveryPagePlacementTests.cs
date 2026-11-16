// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="AbsolutePlacement.EveryPage"/>: a positioned paragraph drawn at its point on every
/// page its section produces — on each page the flow leaves as it breaks, and on the last with the
/// other positioned blocks — in z order, wherever in the section it stands.
/// </summary>
/// <remarks>
/// Asserted on the decoded content streams, which the probe joins in page order: a paragraph drawn
/// on a page shows its text there, so the count of its draws against the page count says whether it
/// repeated, and the order of two texts says which painted over which. Before batch 87 every
/// placement drew once, on the section's last page.
/// </remarks>
public class EveryPagePlacementTests
{
    private static Paragraph Placed(string text, int z, bool everyPage)
    {
        var p = new Paragraph { Placement = new AbsolutePlacement { LeftPt = 100, TopPt = 100, ZIndex = z, EveryPage = everyPage } };
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    /// <summary>One line of flow: a single word, so it is one <c>(Flow)</c> show operator in the content.</summary>
    private static Paragraph Line()
    {
        var p = new Paragraph { SpacingAfter = 2 };
        p.Runs.Add(new TextRun { Text = "Flow" });
        return p;
    }

    private const int FlowLines = 120;

    /// <summary>A section of the placed paragraphs followed by 120 lines of flow — three pages or so.</summary>
    private static byte[] Export(params IDocElement[] placed)
    {
        var section = new Section();
        section.Children.AddRange(placed);
        for (int i = 0; i < FlowLines; i++) section.Children.Add(Line());
        var doc = new Document();
        doc.Sections.Add(section);
        return doc.ToPdf();
    }

    private static int Draws(string content, string text) => Regex.Matches(content, Regex.Escape("(" + text + ")")).Count;

    /// <summary>
    /// A paragraph placed on every page shows on each of the section's pages; one placed once shows
    /// on the last page alone, after the flow's last line — where before batch 87 both showed once.
    /// </summary>
    [Fact]
    public void AnEveryPagePlacementIsDrawnOnEachPage_AnOrdinaryOneOnTheLast()
    {
        byte[] pdf = Export(Placed("Stamp", 0, everyPage: true), Placed("Once", 0, everyPage: false));
        string all = PdfContentProbe.DecodeStreams(pdf);
        int pages = PdfContentProbe.CountPages(pdf);

        Assert.True(pages >= 2, $"the flow made {pages} page(s); a repeat needs two");
        Assert.Equal(FlowLines, Draws(all, "Flow"));   // the potency control: the flow is in the content as counted
        Assert.Equal(pages, Draws(all, "Stamp"));
        Assert.Equal(1, Draws(all, "Once"));
        Assert.True(all.LastIndexOf("(Once)", System.StringComparison.Ordinal) > all.LastIndexOf("(Flow)", System.StringComparison.Ordinal),
            "the once-placed paragraph was not drawn on the section's last page");
    }

    /// <summary>
    /// On a page the flow leaves, the every-page blocks paint after its flow and in z order — the
    /// lower z first, and before the next page's flow — and on the last page in the same order among
    /// the other positioned blocks: a once-placed block at z 1 paints between an every-page block at
    /// 0 and one at 2, whatever order the three stand in.
    /// </summary>
    [Fact]
    public void EveryPageBlocksPaintOverTheFlowInZOrder()
    {
        byte[] pdf = Export(Placed("High", 2, everyPage: true), Placed("Mid", 1, everyPage: false), Placed("Low", 0, everyPage: true));
        string all = PdfContentProbe.DecodeStreams(pdf);
        int pages = PdfContentProbe.CountPages(pdf);
        int At(string text) => all.IndexOf("(" + text + ")", System.StringComparison.Ordinal);
        int Last(string text) => all.LastIndexOf("(" + text + ")", System.StringComparison.Ordinal);
        int FlowBefore(int index) => Draws(all.Substring(0, index), "Flow");

        Assert.True(pages >= 3, $"the flow made {pages} page(s); three are needed");
        Assert.True(At("Flow") < At("Low") && At("Low") < At("High"), $"on the first page the order is flow {At("Flow")}, Low {At("Low")}, High {At("High")}");
        // The first page's blocks are drawn on it: after its own lines, before the second page's.
        int firstPageLines = FlowBefore(At("High"));
        Assert.InRange(firstPageLines, 1, FlowLines / 2);
        Assert.Equal(firstPageLines, FlowBefore(At("Low")));
        Assert.True(Last("Flow") < Last("Low") && Last("Low") < At("Mid") && At("Mid") < Last("High"),
            $"on the last page the order is flow {Last("Flow")}, Low {Last("Low")}, Mid {At("Mid")}, High {Last("High")}");
    }
}
