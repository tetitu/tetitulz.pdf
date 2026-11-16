// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Accessibility.Artifacts;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <see cref="PdfArtifactMarker"/> at its edges: a line exactly on a band's inner edge, bands that
/// exactly fill the page, a box written top corner first, a page drawing no text, a page tree that
/// names one page twice, the <c>/MarkInfo</c> it writes, and where <c>Plan</c> starts numbering.
/// </summary>
/// <remarks>
/// Pages are 612 × 792 unless a test says otherwise; text is one <c>(x) Tj</c> per line at the
/// given baseline.
/// </remarks>
public class ArtifactMarkerBoundaryTests
{
    // ------------------------------------------------------------------ the bands

    /// <summary>
    /// A line exactly one band height from the top edge is in the header band; half a point lower
    /// it is body text. The same at the bottom edge for the footer.
    /// </summary>
    [Theory]
    [InlineData(742f, 1)]     // 792 - 742 = 50: on the header band's edge
    [InlineData(741.5f, 0)]
    [InlineData(50f, 1)]      // 50 - 0 = 50: on the footer band's edge
    [InlineData(50.5f, 0)]
    public void LineOnABandsInnerEdge_IsInTheBand(float baseline, int expectedArtifacts)
    {
        byte[] pdf = Doc(Page(Text(baseline)));

        ArtifactMarkDetail detail = Mark(pdf, 50f, 50f, out _);

        Assert.Equal(expectedArtifacts, detail.ArtifactsMarked);
    }

    /// <summary>
    /// Bands that exactly fill the page — 50 + 50 on a 100 pt page — leave no body, so the page is
    /// refused with its reason; one point less and it is marked.
    /// </summary>
    [Theory]
    [InlineData(50f, true)]
    [InlineData(49f, false)]
    public void BandsExactlyFillingThePage_AreRefused(float header, bool refused)
    {
        byte[] pdf = Doc(Page(Text(95f), mediaBox: "[0 0 612 100]"));

        ArtifactMarkDetail detail = Mark(pdf, header, 50f, out _);

        Assert.Equal(refused ? 1 : 0, detail.PagesSkipped);
        Assert.Equal(refused, detail.Warnings.Any(w => w.Contains("cover the whole")));
        Assert.Equal(refused ? 0 : 1, detail.ArtifactsMarked);
    }

    /// <summary>
    /// ISO 32000-1 §7.9.5 lets a rectangle name either corner first: a <c>/MediaBox</c> written top
    /// corner first is the same page, and its header is found.
    /// </summary>
    [Fact]
    public void MediaBoxWrittenTopCornerFirst_IsTheSamePage()
    {
        byte[] pdf = Doc(Page(Text(780f), mediaBox: "[0 792 612 0]"));

        ArtifactMarkDetail detail = Mark(pdf, 50f, 50f, out _);

        Assert.Equal(1, detail.ArtifactsMarked);
    }

    // ------------------------------------------------------------------ pages with nothing to mark

    /// <summary>
    /// With both bands at zero there is nothing to look for: a page drawing only a rectangle is
    /// "nothing to mark", with no warning. With bands, the same page reaches the rewriter, which
    /// classifies no line at all — that is reported, and the page counted as skipped.
    /// </summary>
    [Theory]
    [InlineData(0f, false)]
    [InlineData(50f, true)]
    public void PageDrawingNoText_IsNothingWithoutBands_AndReportedWithThem(float margin, bool reported)
    {
        byte[] pdf = Doc(Page("0 0 10 10 re f"));

        ArtifactMarkDetail detail = Mark(pdf, margin, margin, out _);

        Assert.Equal(reported ? 0 : 1, detail.PagesWithNothingToMark);
        Assert.Equal(reported ? 1 : 0, detail.PagesSkipped);
        Assert.Equal(reported, detail.Warnings.Any(w => w.Contains("was left unchanged")));
    }

    /// <summary>
    /// A run that changes nothing makes no claim about <c>/MarkInfo</c>: the warning about withholding
    /// it belongs only to a run that marked something.
    /// </summary>
    [Theory]
    [InlineData(400f, false)]   // nothing in the bands
    [InlineData(780f, true)]
    public void MarkInfoWarning_OnlyWhenSomethingWasMarked(float baseline, bool warned)
    {
        byte[] pdf = Doc(Page(Text(baseline)));

        ArtifactMarkDetail detail = Mark(pdf, 50f, 50f, out _);

        Assert.Equal(warned, detail.Warnings.Any(w => w.Contains("/MarkInfo <</Marked true>> was NOT set")));
    }

    /// <summary>
    /// When the document's structure tree reaches content, marking sets
    /// <c>/MarkInfo &lt;&lt;/Marked true&gt;&gt;</c> on a catalog that had none — true, not merely present.
    /// Page 1 is tagged (and so skipped); page 2's header is marked.
    /// </summary>
    [Fact]
    public void TaggedDocument_GetsMarkedTrue()
    {
        const string font = "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";
        byte[] pdf = ReadingOrderBoundaryTests.BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 8 0 R >>",
            "<< /Type /Pages /Kids [4 0 R 6 0 R] /Count 2 >>",
            font,
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 5 0 R /StructParents 0 "
                + "/Resources << /Font << /F1 3 0 R >> >> >>",
            ReadingOrderBoundaryTests.StreamObject("/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 400 Td (x) Tj ET\nEMC\n"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 7 0 R "
                + "/Resources << /Font << /F1 3 0 R >> >> >>",
            ReadingOrderBoundaryTests.StreamObject(Text(780f)),
            "<< /Type /StructTreeRoot /K [9 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /P /P 8 0 R /Pg 4 0 R /K 0 >>",
            "<< /Nums [0 [9 0 R]] >>",
        });

        ArtifactMarkDetail detail = Mark(pdf, 50f, 50f, out byte[] output);
        Assert.Equal(1, detail.ArtifactsMarked);

        using var doc = PdfDocument.Open(new MemoryStream(output));
        var markInfo = doc.ResolveObject(doc.Catalog.Dictionary[PdfName.MarkInfo]) as PdfDictionary;
        Assert.NotNull(markInfo);
        var marked = doc.ResolveObject(markInfo[PdfName.Marked]) as PdfBoolean;
        Assert.NotNull(marked);
        Assert.True(marked.Value);
    }

    // ------------------------------------------------------------------ Plan

    /// <summary>
    /// <c>Plan</c> numbers what it creates from exactly <c>firstFreeObjectNumber</c>: the new
    /// content stream is that number, and nothing between the document's own ceiling and it is used.
    /// </summary>
    [Fact]
    public void Plan_NumbersFromExactlyTheFirstFreeObject()
    {
        byte[] pdf = Doc(Page(Text(780f)));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        int ceiling = doc.CrossReferenceTable.GetMaxObjectNumber();

        ArtifactMarkPlan plan = PdfArtifactMarker.Plan(doc, 40);

        Assert.Contains(plan.Objects, o => o.ObjectNumber == 40);
        Assert.DoesNotContain(plan.Objects, o => o.ObjectNumber > ceiling && o.ObjectNumber < 40);
    }

    /// <summary>
    /// One is the lowest legal first free object (zero is the free-list head): it is accepted,
    /// and the plan then numbers above the document's own ceiling.
    /// </summary>
    [Fact]
    public void Plan_AcceptsOneAsTheFirstFreeObject()
    {
        byte[] pdf = Doc(Page(Text(780f)));
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        int ceiling = doc.CrossReferenceTable.GetMaxObjectNumber();

        ArtifactMarkPlan plan = PdfArtifactMarker.Plan(doc, 1);

        Assert.Contains(plan.Objects, o => o.ObjectNumber == ceiling + 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfArtifactMarker.Plan(doc, 0));
    }

    // ------------------------------------------------------------------ the result

    /// <summary><see cref="ArtifactMarkDetail.Warnings"/> is never null, even when constructed with null.</summary>
    [Fact]
    public void Detail_WithNullWarnings_HasAnEmptyList()
    {
        var detail = new ArtifactMarkDetail(0, 0, 0, 0, 0, null);

        Assert.NotNull(detail.Warnings);
        Assert.Empty(detail.Warnings);
    }

    // ------------------------------------------------------------------ fixtures

    private static ArtifactMarkDetail Mark(byte[] pdf, float header, float footer, out byte[] output)
    {
        using var input = new MemoryStream(pdf, writable: false);
        using var sink = new MemoryStream();
        ArtifactMarkDetail detail = PdfArtifactMarker.MarkHeadersAndFootersDetailed(input, sink, header, footer);
        output = sink.ToArray();
        return detail;
    }

    private static string Text(float baseline)
        => "BT /F1 12 Tf 72 " + baseline.ToString("0.###", CultureInfo.InvariantCulture) + " Td (x) Tj ET\n";

    private readonly struct PageSpec
    {
        internal readonly string Content;
        internal readonly string MediaBox;
        internal PageSpec(string content, string mediaBox) { Content = content; MediaBox = mediaBox; }
    }

    private static PageSpec Page(string content, string mediaBox = "[0 0 612 792]") => new PageSpec(content, mediaBox);

    private static byte[] Doc(PageSpec page) => Doc(new[] { page }, null, 1);

    /// <summary>
    /// Object 1 catalog, 2 page tree, 3 font; page <c>i</c> is object <c>4 + 2i</c> and its content
    /// <c>5 + 2i</c>. <paramref name="kids"/> overrides the page tree's <c>/Kids</c>.
    /// </summary>
    private static byte[] Doc(PageSpec[] pages, string kids, int count)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids " + (kids ?? "[" + string.Join(" ", pages.Select((_, i) =>
                (4 + 2 * i).ToString(CultureInfo.InvariantCulture) + " 0 R")) + "]")
                + " /Count " + count.ToString(CultureInfo.InvariantCulture) + " >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
        };
        for (int i = 0; i < pages.Length; i++)
        {
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox " + pages[i].MediaBox + " /Contents "
                       + (5 + 2 * i).ToString(CultureInfo.InvariantCulture) + " 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            bodies.Add(ReadingOrderBoundaryTests.StreamObject(pages[i].Content));
        }
        return ReadingOrderBoundaryTests.BuildPdf(bodies);
    }
}
