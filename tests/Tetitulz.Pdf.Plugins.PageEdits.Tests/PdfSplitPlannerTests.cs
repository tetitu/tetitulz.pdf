// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PageEdits;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PageEdits.Tests;

/// <summary>
/// <see cref="PdfSplitPlanner"/> — deciding where a document splits, when the splitter itself only
/// ever took explicit ranges.
/// </summary>
/// <remarks>
/// <para><b>Assertions are on the page numbers in each group</b>, in <c>PdfSplitter</c>'s one-based
/// numbering, because that is what a caller feeds straight back into <c>ExtractPages</c>. A test
/// that only counted groups would pass for a planner that produced the right number of wrong
/// chunks.</para>
///
/// <para><b>The off-by-one is the point of the bookmark cases.</b> <c>PdfBookmark.PageNumber</c> is
/// zero-based and <c>PdfSplitter</c> is one-based; <c>SplitPlugin</c>'s own remarks record that
/// exact confusion shipping — one entry point threw on the first page, another returned the page
/// before the one asked for.</para>
/// </remarks>
public class PdfSplitPlannerTests
{
    // ------------------------------------------------------------------
    //  Every N pages
    // ------------------------------------------------------------------

    [Fact]
    public void EveryNPages_SplitsIntoEqualRuns()
    {
        var plan = PdfSplitPlanner.PlanEveryNPages(new MemoryStream(Pages(6)), 2);

        Assert.Equal(3, plan.Count);
        Assert.Equal(new[] { 1, 2 }, plan[0]);
        Assert.Equal(new[] { 3, 4 }, plan[1]);
        Assert.Equal(new[] { 5, 6 }, plan[2]);
    }

    /// <summary>The last run is short rather than dropped or padded.</summary>
    [Fact]
    public void EveryNPages_LeavesAShortFinalRun()
    {
        var plan = PdfSplitPlanner.PlanEveryNPages(new MemoryStream(Pages(5)), 2);

        Assert.Equal(3, plan.Count);
        Assert.Equal(new[] { 5 }, plan[2]);
    }

    /// <summary>A chunk larger than the document is one chunk of everything, not an error.</summary>
    [Fact]
    public void EveryNPages_LargerThanTheDocument_IsOneChunk()
    {
        var plan = PdfSplitPlanner.PlanEveryNPages(new MemoryStream(Pages(3)), 99);

        Assert.Equal(new[] { 1, 2, 3 }, Assert.Single(plan));
    }

    [Fact]
    public void EveryNPages_RefusesANonPositiveChunkSize()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            PdfSplitPlanner.PlanEveryNPages(new MemoryStream(Pages(3)), 0));
    }

    /// <summary>Every page appears exactly once, in order — the invariant the caller relies on.</summary>
    [Fact]
    public void EveryNPages_CoversEveryPageExactlyOnce()
    {
        var plan = PdfSplitPlanner.PlanEveryNPages(new MemoryStream(Pages(7)), 3);

        Assert.Equal(Enumerable.Range(1, 7), plan.SelectMany(g => g));
    }

    // ------------------------------------------------------------------
    //  At bookmarks
    // ------------------------------------------------------------------

    /// <summary>
    /// <b>The off-by-one case.</b> Bookmarks point at pages 2 and 4 (zero-based 1 and 3); the chunks
    /// must start at one-based 2 and 4, with pages before the first bookmark leading.
    /// </summary>
    [Fact]
    public void AtBookmarks_ConvertsZeroBasedBookmarkPagesToOneBasedChunks()
    {
        var plan = PdfSplitPlanner.PlanAtBookmarks(new MemoryStream(BookmarkedFivePages()));

        Assert.Equal(3, plan.Count);
        Assert.Equal(new[] { 1 }, plan[0]);          // the title page, before the first bookmark
        Assert.Equal(new[] { 2, 3 }, plan[1]);       // bookmark on page 2
        Assert.Equal(new[] { 4, 5 }, plan[2]);       // bookmark on page 4
    }

    /// <summary>
    /// Pages before the first bookmark are a chunk of their own. Folding a title page into chapter 1
    /// is a decision the caller did not make.
    /// </summary>
    [Fact]
    public void AtBookmarks_PagesBeforeTheFirstBookmarkAreTheirOwnChunk()
    {
        var plan = PdfSplitPlanner.PlanAtBookmarks(new MemoryStream(BookmarkedFivePages()));

        Assert.Equal(new[] { 1 }, plan[0]);
    }

    /// <summary>
    /// A document with no bookmarks is one chunk, and says so — an empty plan would silently drop
    /// the whole document.
    /// </summary>
    [Fact]
    public void AtBookmarks_WithNoBookmarks_IsOneChunkAndWarns()
    {
        var plan = PdfSplitPlanner.PlanAtBookmarks(new MemoryStream(Pages(3)));

        Assert.Equal(new[] { 1, 2, 3 }, Assert.Single(plan));
        Assert.NotNull(PdfSplitPlanner.LastPlanWarning);
    }

    /// <summary>
    /// <b>A bookmark whose destination does not resolve starts no chunk.</b>
    /// <c>PdfBookmark.PageNumber</c> is <c>int?</c> and a named destination the document does not
    /// define leaves it null — a <c>?? 0</c> would silently put every such bookmark at page 1,
    /// creating a boundary the document never asked for.
    /// </summary>
    /// <remarks>
    /// Written because a mutation removing the null guard left all fifteen tests green: every
    /// fixture's bookmarks resolved, so the branch was never reached.
    /// </remarks>
    [Fact]
    public void AtBookmarks_ABookmarkWithAnUnresolvableDestination_StartsNoChunk()
    {
        var plan = PdfSplitPlanner.PlanAtBookmarks(new MemoryStream(BookmarkedWithBrokenDestination()));

        // Only the resolvable bookmark (page 3) is a boundary; the broken one contributes nothing.
        Assert.Equal(2, plan.Count);
        Assert.Equal(new[] { 1, 2 }, plan[0]);
        Assert.Equal(new[] { 3, 4 }, plan[1]);
    }

    /// <summary>The warning is cleared by a plan that has nothing to report.</summary>
    [Fact]
    public void TheWarningIsClearedByACleanPlan()
    {
        PdfSplitPlanner.PlanAtBookmarks(new MemoryStream(Pages(3)));
        Assert.NotNull(PdfSplitPlanner.LastPlanWarning);

        PdfSplitPlanner.PlanEveryNPages(new MemoryStream(Pages(3)), 1);
        Assert.Null(PdfSplitPlanner.LastPlanWarning);
    }

    // ------------------------------------------------------------------
    //  Byte ceiling
    // ------------------------------------------------------------------

    /// <summary>
    /// Every chunk is at or below the ceiling — asserted by extracting each one and measuring, which
    /// is the only claim the planner actually makes.
    /// </summary>
    [Fact]
    public void ByteCeiling_EveryChunkFitsUnderTheCeiling()
    {
        byte[] source = Pages(8);
        long onePage = SizeOf(source, new[] { 1 });
        long ceiling = onePage * 3;

        var plan = PdfSplitPlanner.PlanUnderByteCeiling(new MemoryStream(source), ceiling);

        Assert.NotEmpty(plan);
        foreach (var chunk in plan)
            Assert.True(SizeOf(source, chunk) <= ceiling,
                $"chunk [{string.Join(",", chunk)}] is {SizeOf(source, chunk)} bytes, over {ceiling}");
    }

    /// <summary>…and every page still appears exactly once, in order.</summary>
    [Fact]
    public void ByteCeiling_CoversEveryPageExactlyOnce()
    {
        byte[] source = Pages(8);
        var plan = PdfSplitPlanner.PlanUnderByteCeiling(
            new MemoryStream(source), SizeOf(source, new[] { 1 }) * 3);

        Assert.Equal(Enumerable.Range(1, 8), plan.SelectMany(g => g));
    }

    /// <summary>
    /// <b>A page that exceeds the ceiling on its own ships alone and is reported.</b> A page cannot
    /// be split, so the alternatives are an empty plan or an infinite loop; being told is the only
    /// useful outcome.
    /// </summary>
    [Fact]
    public void ByteCeiling_APageTooLargeOnItsOwn_ShipsAloneAndIsReported()
    {
        byte[] source = Pages(3);

        var plan = PdfSplitPlanner.PlanUnderByteCeiling(new MemoryStream(source), 1);

        Assert.Equal(3, plan.Count);
        Assert.Equal(Enumerable.Range(1, 3), plan.SelectMany(g => g));
        Assert.Contains("cannot be split", PdfSplitPlanner.LastPlanWarning);
    }

    /// <summary>A generous ceiling puts the whole document in one chunk.</summary>
    [Fact]
    public void ByteCeiling_AGenerousCeilingIsOneChunk()
    {
        byte[] source = Pages(4);
        var plan = PdfSplitPlanner.PlanUnderByteCeiling(new MemoryStream(source), 10_000_000);

        Assert.Equal(new[] { 1, 2, 3, 4 }, Assert.Single(plan));
        Assert.Null(PdfSplitPlanner.LastPlanWarning);
    }

    /// <summary>
    /// <b>Each chunk's end is found by doubling and bisection, not one page at a time</b> (Wave 17
    /// R11.1). The plan cannot show this — both searches find the same chunks — so the evidence is
    /// the trial count: forty pages under a generous ceiling are one chunk after the doubling probes
    /// 1, 2, 4, 8, 16, 32 and 40 pages, seven extracts, where growing the run a page at a time made
    /// forty extracts of 1, 2, … 40 pages. The bound leaves room for a bisection that never runs
    /// here; the one-page-at-a-time search cannot get under it.
    /// </summary>
    [Fact]
    public void ByteCeiling_FindsAChunksEndByDoublingAndBisection_NotOnePageAtATime()
    {
        byte[] source = Pages(40);

        var plan = PdfSplitPlanner.PlanUnderByteCeiling(new MemoryStream(source), 100_000_000);

        Assert.Equal(Enumerable.Range(1, 40), Assert.Single(plan));
        Assert.InRange(PdfSplitPlanner.LastTrialExtractions, 1, 12);
    }

    /// <summary>
    /// …and the bisection lands on the same boundary the one-page search did: with a ceiling of
    /// three pages' worth, eight pages split 3 / 3 / 2, the last fit before each break — asserted
    /// on the pages, since a bisection that stopped a page early would still "fit".
    /// </summary>
    [Fact]
    public void ByteCeiling_BisectionFindsTheLastFitBeforeTheBreak()
    {
        byte[] source = Pages(8);
        long threePages = SizeOf(source, new[] { 1, 2, 3 });

        var plan = PdfSplitPlanner.PlanUnderByteCeiling(new MemoryStream(source), threePages);

        Assert.Equal(new[] { 1, 2, 3 }, plan[0]);
        Assert.Equal(new[] { 4, 5, 6 }, plan[1]);
        Assert.Equal(new[] { 7, 8 }, plan[2]);
        Assert.Equal(3, plan.Count);
    }

    /// <summary>
    /// <b>The claim the whole design rests on: sizes do not sum.</b> The splitter rebuilds one
    /// shared object graph per chunk, so four pages sharing a font are <i>one</i> copy together and
    /// <i>four</i> apart — the joint extract is smaller than the sum of the single-page ones. A
    /// planner that added up per-page sizes would therefore split earlier than it needs to, wasting
    /// the ceiling and producing more files than asked for.
    /// </summary>
    /// <remarks>
    /// Written because the mutation that replaced the joint measurement with a sum left all fifteen
    /// tests green: "every chunk fits under the ceiling" is satisfied by chunks that are too small.
    /// The discriminating assertion is that the plan is as <b>large</b> as the ceiling allows.
    /// </remarks>
    [Fact]
    public void ByteCeiling_MeasuresTheJointExtract_NotTheSumOfSinglePages()
    {
        byte[] source = Pages(4);

        long joint = SizeOf(source, new[] { 1, 2, 3, 4 });
        long sum = Enumerable.Range(1, 4).Sum(p => SizeOf(source, new[] { p }));

        // The precondition the claim rests on. If this ever fails, the splitter stopped sharing
        // objects across a chunk and this test is measuring nothing.
        Assert.True(joint < sum,
            $"joint extract was {joint} bytes and the per-page sum {sum}; the fixture's pages must " +
            "share objects for this test to discriminate");

        // A ceiling the joint extract exactly meets and the naive sum does not.
        var plan = PdfSplitPlanner.PlanUnderByteCeiling(new MemoryStream(source), joint);

        Assert.Equal(new[] { 1, 2, 3, 4 }, Assert.Single(plan));
    }

    [Fact]
    public void ByteCeiling_RefusesANonPositiveCeiling()
    {
        Assert.Throws<System.ArgumentOutOfRangeException>(() =>
            PdfSplitPlanner.PlanUnderByteCeiling(new MemoryStream(Pages(2)), 0));
    }

    // ------------------------------------------------------------------
    //  The plan is usable
    // ------------------------------------------------------------------

    /// <summary>
    /// The whole point: the groups go straight into <c>PdfSplitter.ExtractPages</c> and produce
    /// documents with the expected page counts. A planner whose numbering were off by one would
    /// throw here, which is exactly how the original defect surfaced.
    /// </summary>
    [Fact]
    public void ThePlanFeedsStraightIntoTheSplitter()
    {
        byte[] source = Pages(5);
        var plan = PdfSplitPlanner.PlanEveryNPages(new MemoryStream(source), 2);

        var counts = plan.Select(chunk => PageCountOf(source, chunk)).ToArray();
        Assert.Equal(new[] { 2, 2, 1 }, counts);
    }

    // ------------------------------------------------------------------

    private static long SizeOf(byte[] source, System.Collections.Generic.IReadOnlyList<int> pages)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfSplitter.ExtractPages(input, output, pages);
        return output.Length;
    }

    private static int PageCountOf(byte[] source, System.Collections.Generic.IReadOnlyList<int> pages)
    {
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        PdfSplitter.ExtractPages(input, output, pages);
        output.Position = 0;
        using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(output);
        return doc.Pages.Count;
    }

    /// <summary>An n-page document, each page with a little distinct content.</summary>
    private static byte[] Pages(int count)
    {
        var bodies = new System.Collections.Generic.List<string>();
        var kids = new StringBuilder();
        for (int i = 0; i < count; i++) kids.Append(4 + i * 2).Append(" 0 R ");

        bodies.Add("<< /Type /Catalog /Pages 2 0 R >>");
        bodies.Add("<< /Type /Pages /Kids [" + kids.ToString().Trim() + "] /Count " + count + " >>");
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (int i = 0; i < count; i++)
        {
            int pageObj = 4 + i * 2;
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents "
                       + (pageObj + 1) + " 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            bodies.Add(StreamObj("BT /F1 24 Tf 72 700 Td (Page " + (i + 1) + ") Tj ET"));
        }

        return Build(bodies.ToArray(), null);
    }

    /// <summary>Five pages with top-level bookmarks on pages 2 and 4 (zero-based 1 and 3).</summary>
    private static byte[] BookmarkedFivePages()
    {
        var bodies = new System.Collections.Generic.List<string>();
        var kids = new StringBuilder();
        for (int i = 0; i < 5; i++) kids.Append(4 + i * 2).Append(" 0 R ");

        bodies.Add("<< /Type /Catalog /Pages 2 0 R /Outlines 14 0 R >>");
        bodies.Add("<< /Type /Pages /Kids [" + kids.ToString().Trim() + "] /Count 5 >>");
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (int i = 0; i < 5; i++)
        {
            int pageObj = 4 + i * 2;
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents "
                       + (pageObj + 1) + " 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            bodies.Add(StreamObj("BT /F1 24 Tf 72 700 Td (Page " + (i + 1) + ") Tj ET"));
        }

        // 14 = outline root, 15/16 = the two items. Pages 2 and 4 are objects 6 and 10.
        bodies.Add("<< /Type /Outlines /First 15 0 R /Last 16 0 R /Count 2 >>");
        bodies.Add("<< /Title (Chapter 1) /Parent 14 0 R /Next 16 0 R /Dest [6 0 R /XYZ null null null] >>");
        bodies.Add("<< /Title (Chapter 2) /Parent 14 0 R /Prev 15 0 R /Dest [10 0 R /XYZ null null null] >>");

        return Build(bodies.ToArray(), null);
    }

    /// <summary>
    /// Four pages; the first bookmark names a destination the document does not define (so its
    /// <c>PageNumber</c> is null) and the second points at page 3.
    /// </summary>
    private static byte[] BookmarkedWithBrokenDestination()
    {
        var bodies = new System.Collections.Generic.List<string>();
        var kids = new StringBuilder();
        for (int i = 0; i < 4; i++) kids.Append(4 + i * 2).Append(" 0 R ");

        bodies.Add("<< /Type /Catalog /Pages 2 0 R /Outlines 12 0 R >>");
        bodies.Add("<< /Type /Pages /Kids [" + kids.ToString().Trim() + "] /Count 4 >>");
        bodies.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (int i = 0; i < 4; i++)
        {
            int pageObj = 4 + i * 2;
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents "
                       + (pageObj + 1) + " 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            bodies.Add(StreamObj("BT /F1 24 Tf 72 700 Td (Page " + (i + 1) + ") Tj ET"));
        }

        // 12 = outline root, 13 names a destination nothing defines, 14 points at page 3 (object 8).
        bodies.Add("<< /Type /Outlines /First 13 0 R /Last 14 0 R /Count 2 >>");
        bodies.Add("<< /Title (Broken) /Parent 12 0 R /Next 14 0 R /Dest /NoSuchNamedDestination >>");
        bodies.Add("<< /Title (Chapter 2) /Parent 12 0 R /Prev 13 0 R /Dest [8 0 R /XYZ null null null] >>");

        return Build(bodies.ToArray(), null);
    }

    private static string StreamObj(string data) =>
        "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture) +
        " >>\nstream\n" + data + "\nendstream";

    private static byte[] Build(string[] bodies, string extraTrailer)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new int[bodies.Length];
        for (int i = 0; i < bodies.Length; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append((bodies.Length + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R").Append(extraTrailer ?? "").Append(" >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
