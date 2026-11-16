// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The edges of <see cref="PdfReadingOrderAnalyzer"/>'s single-column proof, its tolerances and its
/// ceilings — each pinned on the side the code puts it, with a control on the other side.
/// </summary>
/// <remarks>
/// Every fixture is one page: object 7 is a <c>&lt;Document&gt;</c> whose <c>/K</c> lists its
/// <c>&lt;P&gt;</c> children (objects 8 onward) in the tree order the test chooses, and each
/// <c>&lt;P&gt;</c> owns one marked-content sequence per <see cref="Mark"/> — a show origin. An
/// element's ordering key is its first mark's baseline; its band is all of them.
/// </remarks>
public class ReadingOrderBoundaryTests
{
    // ---------------------------------------------------------------
    //  Bands: what shares vertical space
    // ---------------------------------------------------------------

    /// <summary>
    /// Two one-line blocks whose baselines are 0.75 pt apart are one line as far as the band
    /// test goes (the tolerance is 1 pt), so ranking them makes no claim about side-by-side
    /// content and the out-of-order pair is still repaired.
    /// </summary>
    [Fact]
    public void Fix_BaselinesWithinOnePoint_AreOneBand_AndStillRanked()
    {
        byte[] pdf = Page(new[] { 0, 1 }, new[] { M(72, 700f) }, new[] { M(72, 700.75f) });

        (int moved, IReadOnlyList<int> kids) = FixAndRead(pdf);

        Assert.Equal(2, moved);
        Assert.Equal(new[] { 9, 8 }, kids);
    }

    /// <summary>
    /// A block reaching up to exactly one tolerance below another's floor still shares its
    /// vertical space — the join test is strict — so two blocks on different baselines are beside
    /// each other and are left alone.
    /// </summary>
    [Theory]
    [InlineData(700f, 0)]     // top exactly 1 pt below the floor at 701: joins, declined
    [InlineData(699.5f, 2)]   // control: 1.5 pt below: a separate band, repaired
    public void Fix_BandOneToleranceBelowAnothersFloor_SharesItsVerticalSpace(float lowerY, int expectedMoved)
    {
        // Element 0: one line at 705 and one at 701 (band 701..705). Element 1: one line.
        byte[] pdf = Page(new[] { 1, 0 }, new[] { M(72, 705f), M(72, 701f) }, new[] { M(72, lowerY) });

        (int moved, IReadOnlyList<int> kids) = FixAndRead(pdf);

        Assert.Equal(expectedMoved, moved);
        Assert.Equal(expectedMoved == 0 ? new[] { 9, 8 } : new[] { 8, 9 }, kids);
        if (expectedMoved == 0)
            Assert.Contains("share vertical space", PdfReadingOrderAnalyzer.LastError ?? "");
    }

    /// <summary>
    /// A group's floor is the lowest <em>bottom</em> any member reached. Block B joins A's line
    /// and reaches down to 690; block C at 695 sits inside B's band, on a different baseline, so
    /// the set is not a single column and nothing moves.
    /// </summary>
    [Fact]
    public void Fix_GroupFloorIsTheLowestBottom_NotTheLastTop()
    {
        byte[] pdf = Page(new[] { 2, 0, 1 },
            new[] { M(72, 710f) },
            new[] { M(72, 709.5f), M(72, 690f) },
            new[] { M(72, 695f) });

        (int moved, IReadOnlyList<int> kids) = FixAndRead(pdf);

        Assert.Equal(0, moved);
        Assert.Equal(new[] { 10, 8, 9 }, kids);
        Assert.Contains("share vertical space", PdfReadingOrderAnalyzer.LastError ?? "");
    }

    /// <summary>
    /// Two blocks sharing vertical space whose ordering keys are 2 pt apart are beside each other.
    /// The spread is measured between ordering keys (702 and 700), not against the second block's
    /// bottom (700.5), which would put them within tolerance.
    /// </summary>
    /// <remarks>
    /// Element 0's own <c>/K</c> lists its lower line first, so the pass re-deals that array either
    /// way; what is asserted is the <c>&lt;Document&gt;</c>'s order.
    /// </remarks>
    [Theory]
    [InlineData(702f, false)]     // keys 2 pt apart: declined
    [InlineData(700.75f, true)]   // control: keys 0.75 pt apart: one line, repaired
    public void Fix_SpreadIsBetweenOrderingKeys_NotABottom(float secondKey, bool repaired)
    {
        // Element 0: key 700, band 700..710. Element 1: key secondKey, bottom 700.5.
        byte[] pdf = Page(new[] { 0, 1 },
            new[] { M(72, 700f), M(72, 710f) },
            new[] { M(72, secondKey), M(72, 700.5f) });

        (_, IReadOnlyList<int> kids) = FixAndRead(pdf);

        Assert.Equal(repaired ? new[] { 9, 8 } : new[] { 8, 9 }, kids);
    }

    /// <summary>
    /// Two blocks side by side — one spanning 680..700, the other a line at 690 to its right — are
    /// judged even though they are only two: the run check starts at two elements.
    /// </summary>
    [Theory]
    [InlineData(690f, 0)]     // beside the block: declined
    [InlineData(670f, 2)]     // control: below it: a stack, repaired
    public void Fix_TwoBlocksSideBySide_AreLeftInTheirOrder(float rightY, int expectedMoved)
    {
        byte[] pdf = Page(new[] { 1, 0 }, new[] { M(72, 700f), M(72, 680f) }, new[] { M(320, rightY) });

        (int moved, IReadOnlyList<int> kids) = FixAndRead(pdf);

        Assert.Equal(expectedMoved, moved);
        Assert.Equal(expectedMoved == 0 ? new[] { 9, 8 } : new[] { 8, 9 }, kids);
    }

    // ---------------------------------------------------------------
    //  The column signature
    // ---------------------------------------------------------------

    /// <summary>
    /// The signature is "moved left, then stepped back right". A block whose right edge is within
    /// 1 pt of the left edge of the block above <em>touches</em> it, which is not a move left — so
    /// a third block further right is no evidence of columns. 1.5 pt clear is.
    /// </summary>
    [Theory]
    [InlineData(299f, 2)]      // exactly one tolerance short: touching, repaired
    [InlineData(299.25f, 2)]   // within tolerance: touching, repaired
    [InlineData(298.5f, 0)]    // control: clear of it: moved left, then right: declined
    public void Fix_BlockWithinOnePointLeftOfTheOneAbove_TouchesIt(float middleX, int expectedMoved)
    {
        byte[] pdf = Page(new[] { 1, 0, 2 },
            new[] { M(300, 700f) },
            new[] { M(middleX, 680f) },
            new[] { M(310, 660f) });

        (int moved, _) = FixAndRead(pdf);

        Assert.Equal(expectedMoved, moved);
        if (expectedMoved == 0)
            Assert.Contains("steps back to the right", PdfReadingOrderAnalyzer.LastError ?? "");
    }

    // ---------------------------------------------------------------
    //  The extent probe cap
    // ---------------------------------------------------------------

    /// <summary>
    /// An element's band is measured from its first 64 <c>/K</c> entries. With the 64th entry
    /// (index 63) reaching down to 680 the element spans 680..700 and a line at 690 beside it
    /// blocks the repair; the same entry at index 64 is past the cap, the band stays at 700, and
    /// the pair is repaired.
    /// </summary>
    /// <remarks>
    /// The block's own <c>/K</c> is re-dealt either way (its low entry moves to the end); what is
    /// asserted is the <c>&lt;Document&gt;</c>'s order.
    /// </remarks>
    [Theory]
    [InlineData(63, false)]
    [InlineData(64, true)]
    public void Fix_ExtentIsMeasuredFromTheFirst64Entries(int outlierIndex, bool repaired)
    {
        var block = new Mark[65];
        for (int i = 0; i < block.Length; i++) block[i] = M(72, i == outlierIndex ? 680f : 700f);

        byte[] pdf = Page(new[] { 1, 0 }, block, new[] { M(320, 690f) });

        (_, IReadOnlyList<int> kids) = FixAndRead(pdf);

        Assert.Equal(repaired ? new[] { 8, 9 } : new[] { 9, 8 }, kids);
    }

    // ---------------------------------------------------------------
    //  Fix traversal ceilings
    // ---------------------------------------------------------------

    /// <summary>
    /// The node ceiling counts visits, the root included: at one node only the root is visited and
    /// the <c>&lt;Document&gt;</c> below it is never reached, so its scrambled children stay put
    /// and the stop is reported; at two the <c>&lt;Document&gt;</c> is visited and repaired (its
    /// <c>&lt;P&gt;</c> children are the nodes past the ceiling then).
    /// </summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    public void Plan_NodeCeiling_StopsBeforeTheNodePastIt(int maxNodes, int expectedMoved)
    {
        byte[] pdf = Page(new[] { 0, 1 }, new[] { M(72, 600f) }, new[] { M(72, 700f) });
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        ReadingOrderPlan plan = PdfReadingOrderAnalyzer.Plan(doc, 100, maxNodes);

        Assert.Equal(expectedMoved, plan.Describe(revisionWasAppended: true).ElementsReordered);
        Assert.Contains(maxNodes.ToString(CultureInfo.InvariantCulture) + "-node ceiling",
            PdfReadingOrderAnalyzer.LastError ?? "");
    }

    /// <summary>
    /// The depth cap is 100: an element at depth 100 has its <c>/K</c> repaired, one at depth 101
    /// is never visited and keeps its order, with the cap reported.
    /// </summary>
    [Theory]
    [InlineData(100, 2)]
    [InlineData(101, 0)]
    public void Fix_DepthCap_LeavesTheElementPastItAlone(int leafDepth, int expectedMoved)
    {
        byte[] pdf = Chain(leafDepth);
        int leaf = 6 + leafDepth;

        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);

        Assert.Equal(expectedMoved, result.ElementsReordered);
        Assert.Equal(expectedMoved == 0 ? new[] { 0, 1 } : new[] { 1, 0 }, LeafMcids(output.ToArray(), leaf));
        if (expectedMoved == 0)
            Assert.Contains("100-level cap", PdfReadingOrderAnalyzer.LastError ?? "");
    }

    // ---------------------------------------------------------------
    //  Analyze
    // ---------------------------------------------------------------

    /// <summary>
    /// A clean page whose every element is placed leaves <see cref="PdfReadingOrderAnalyzer.LastError"/>
    /// empty: neither the partial-result notice nor the scanner notice fires. The controls below
    /// show each one does fire when it should.
    /// </summary>
    [Fact]
    public void Analyze_FullyPlacedCleanPage_ReportsNothing()
    {
        byte[] pdf = FlatPage(new[] { 0, 1 }, new[] { M(72, 700f) }, new[] { M(72, 600f) });

        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(new MemoryStream(pdf, writable: false));

        Assert.True(analysis.Analyzed, analysis.NotAnalyzedReason);
        Assert.Equal(2, analysis.ElementsPlaced);
        Assert.Equal(2, analysis.ElementsTotal);
        Assert.Null(PdfReadingOrderAnalyzer.LastError);
    }

    /// <summary>Control: an element whose MCID is on no page makes the result partial, and says so.</summary>
    [Fact]
    public void Analyze_UnplacedElement_ReportsThePartialResult()
    {
        byte[] pdf = FlatPageWith(new[] { 0, 1 }, new[] { M(72, 700f) }, new[] { M(72, 600f) },
            extraElementK: "99");

        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(new MemoryStream(pdf, writable: false));

        Assert.True(analysis.Analyzed, analysis.NotAnalyzedReason);
        Assert.Equal(2, analysis.ElementsPlaced);
        Assert.Equal(3, analysis.ElementsTotal);
        Assert.Contains("only 2 of 3", PdfReadingOrderAnalyzer.LastError ?? "");
    }

    /// <summary>Control: a content stream the scanner cannot finish is reported.</summary>
    [Fact]
    public void Analyze_ContentStreamCutShort_ReportsTheScanner()
    {
        byte[] pdf = FlatPageWith(new[] { 0, 1 }, new[] { M(72, 700f) }, new[] { M(72, 600f) },
            contentTail: "(never closed");

        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(new MemoryStream(pdf, writable: false));

        Assert.Equal(2, analysis.ElementsPlaced);
        Assert.Contains("could not be scanned to the end", PdfReadingOrderAnalyzer.LastError ?? "");
    }

    /// <summary>
    /// A single adjacent transposition is within the ±1 position tolerance and is not reported;
    /// three blocks in reverse put the outer two two places out, and exactly those are.
    /// </summary>
    [Fact]
    public void Analyze_AdjacentTransposition_IsWithinTolerance()
    {
        byte[] swapped = FlatPage(new[] { 1, 0 }, new[] { M(72, 700f) }, new[] { M(72, 600f) });
        ReadingOrderAnalysis pair = PdfReadingOrderAnalyzer.Analyze(new MemoryStream(swapped, writable: false));

        Assert.True(pair.Analyzed, pair.NotAnalyzedReason);
        Assert.Empty(pair.Issues);

        byte[] reversed = FlatPage(new[] { 2, 1, 0 }, new[] { M(72, 700f) }, new[] { M(72, 650f) }, new[] { M(72, 600f) });
        ReadingOrderAnalysis three = PdfReadingOrderAnalyzer.Analyze(new MemoryStream(reversed, writable: false));

        Assert.Equal(2, three.Issues.Count);
        Assert.Equal(0, three.Issues[0].TreePosition);
        Assert.Equal(2, three.Issues[0].ExpectedVisualPosition);
        Assert.Equal(2, three.Issues[1].TreePosition);
        Assert.Equal(0, three.Issues[1].ExpectedVisualPosition);
    }

    /// <summary>
    /// A page of only two elements is still checked for layout risk: two blocks side by side make
    /// the analyzer decline the page and say why; the same two stacked raise nothing.
    /// </summary>
    [Theory]
    [InlineData(690f, true)]
    [InlineData(670f, false)]
    public void Analyze_TwoElementPage_IsCheckedForLayoutRisk(float rightY, bool declined)
    {
        byte[] pdf = FlatPage(new[] { 0, 1 }, new[] { M(72, 700f), M(72, 680f) }, new[] { M(320, rightY) });

        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(new MemoryStream(pdf, writable: false));

        Assert.True(analysis.Analyzed, analysis.NotAnalyzedReason);
        if (declined)
            Assert.Contains("could not be shown to be a single top-to-bottom column",
                PdfReadingOrderAnalyzer.LastError ?? "");
        else
            Assert.Null(PdfReadingOrderAnalyzer.LastError);
    }

    /// <summary>
    /// Elements with no marked content are placed by their <c>/ActualText</c> against the
    /// extracted lines — and a line shorter than the 20-character prefix is indexed too.
    /// </summary>
    [Fact]
    public void Analyze_ActualTextFallback_PlacesShortLines()
    {
        const string content =
            "BT /F1 12 Tf 72 700 Td (Short) Tj ET\n" +
            "BT /F1 12 Tf 72 600 Td (Tiny) Tj ET\n";

        byte[] pdf = BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [8 0 R 9 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /ActualText (Tiny) >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /ActualText (Short) >>",
        });

        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(new MemoryStream(pdf, writable: false));

        Assert.True(analysis.Analyzed, analysis.NotAnalyzedReason);
        Assert.Equal(2, analysis.ElementsPlaced);
    }

    // ---------------------------------------------------------------
    //  Fixtures
    // ---------------------------------------------------------------

    internal readonly struct Mark
    {
        internal readonly float X;
        internal readonly float Y;
        internal Mark(float x, float y) { X = x; Y = y; }
    }

    internal static Mark M(float x, float y) => new Mark(x, y);

    internal static (int Moved, IReadOnlyList<int> Kids) FixAndRead(byte[] pdf)
    {
        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);
        return (result.ElementsReordered, KidNumbers(output.ToArray()));
    }

    /// <summary>
    /// One page; element <c>e</c> (object <c>8 + e</c>) owns one marked-content sequence per mark,
    /// and the <c>&lt;Document&gt;</c> lists the elements in <paramref name="treeOrder"/>.
    /// <paramref name="extraElementK"/> appends one more element with that <c>/K</c>.
    /// </summary>
    internal static byte[] Page(int[] treeOrder, params Mark[][] elements)
        => Page(treeOrder, elements, null, null, flat: false);

    /// <summary>
    /// <see cref="Page(int[], Mark[][])"/> with the elements directly under the structure tree
    /// root, so every element the analyzer counts has marked content (a <c>&lt;Document&gt;</c>
    /// container is itself an element nothing places, which makes every result partial).
    /// </summary>
    internal static byte[] FlatPage(int[] treeOrder, params Mark[][] elements)
        => Page(treeOrder, elements, null, null, flat: true);

    internal static byte[] FlatPageWith(int[] treeOrder, Mark[] first, Mark[] second,
        string extraElementK = null, string contentTail = null)
        => Page(treeOrder, new[] { first, second }, extraElementK, contentTail, flat: true);

    private static byte[] Page(int[] treeOrder, Mark[][] elements, string extraElementK, string contentTail,
        bool flat)
    {
        var content = new StringBuilder();
        var elementKids = new List<string>();
        int mcid = 0;
        foreach (Mark[] marks in elements)
        {
            var k = new StringBuilder("[");
            foreach (Mark mark in marks)
            {
                content.Append("/P <</MCID ").Append(N(mcid)).Append(">> BDC\nBT /F1 12 Tf ")
                       .Append(N(mark.X)).Append(' ').Append(N(mark.Y)).Append(" Td (x) Tj ET\nEMC\n");
                k.Append(N(mcid)).Append(' ');
                mcid++;
            }
            elementKids.Add(k.Append(']').ToString());
        }
        if (contentTail != null) content.Append(contentTail);

        int count = elements.Length + (extraElementK != null ? 1 : 0);
        var docKids = new StringBuilder("[");
        foreach (int e in treeOrder) docKids.Append(N(8 + e)).Append(" 0 R ");
        if (extraElementK != null) docKids.Append(N(8 + elements.Length)).Append(" 0 R ");
        docKids.Append(']');

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(content.ToString()),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            flat ? "<< /Type /StructTreeRoot /K " + docKids + " >>" : "<< /Type /StructTreeRoot /K [7 0 R] >>",
            flat ? "<< >>" : "<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K " + docKids + " >>",
        };
        string parent = flat ? "6" : "7";
        foreach (string k in elementKids)
            bodies.Add("<< /Type /StructElem /S /P /P " + parent + " 0 R /Pg 3 0 R /K " + k + " >>");
        if (extraElementK != null)
            bodies.Add("<< /Type /StructElem /S /P /P " + parent + " 0 R /Pg 3 0 R /K " + extraElementK + " >>");

        Assert.Equal(7 + count, bodies.Count);
        return BuildPdf(bodies);
    }

    /// <summary>
    /// A chain of <c>&lt;Sect&gt;</c> elements: object 7 at depth 1, object <c>6 + d</c> at depth
    /// <c>d</c>, the last at <paramref name="leafDepth"/> holding MCIDs 0 (y = 600) and 1 (y = 700)
    /// — bottom first.
    /// </summary>
    private static byte[] Chain(int leafDepth)
    {
        string content =
            "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 600 Td (x) Tj ET\nEMC\n" +
            "/P <</MCID 1>> BDC\nBT /F1 12 Tf 72 700 Td (x) Tj ET\nEMC\n";

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] >>",
        };
        for (int depth = 1; depth <= leafDepth; depth++)
        {
            int self = 6 + depth;
            string k = depth == leafDepth ? "[0 1]" : "[" + N(self + 1) + " 0 R]";
            bodies.Add("<< /Type /StructElem /S /Sect /P " + N(self - 1) + " 0 R /Pg 3 0 R /K " + k + " >>");
        }
        return BuildPdf(bodies);
    }

    private static IReadOnlyList<int> KidNumbers(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var kids = doc.ResolveObject(((PdfDictionary)doc.GetObject(7))[PdfName.K]) as PdfArray;
        Assert.NotNull(kids);

        var numbers = new List<int>(kids.Count);
        for (int i = 0; i < kids.Count; i++) numbers.Add(((PdfReference)kids[i]).ReferenceObjectNumber);
        return numbers;
    }

    private static IReadOnlyList<int> LeafMcids(byte[] pdf, int leaf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        var kids = doc.ResolveObject(((PdfDictionary)doc.GetObject(leaf))[PdfName.K]) as PdfArray;
        Assert.NotNull(kids);

        var mcids = new List<int>(kids.Count);
        for (int i = 0; i < kids.Count; i++) mcids.Add(((PdfNumber)kids[i]).IntValue);
        return mcids;
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string N(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    internal static string StreamObject(string data)
        => "<< /Length " + N(data.Length) + " >>\nstream\n" + data + "\nendstream";

    internal static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(N(i + 1)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(N(bodies.Count + 1)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(N(bodies.Count + 1)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}

/// <summary>
/// The marked-content scan's graphics-state stack ceiling (<see cref="PdfLimits.MaxGraphicsStateStackDepth"/>),
/// tested as a parameter.
/// </summary>
[Collection(PdfLimitsCollection.Name)]
public class ReadingOrderStackCeilingTests
{
    /// <summary>
    /// A <c>q</c> past the ceiling saves nothing, so its <c>Q</c> restores nothing and a
    /// <c>cm</c> between them stays in force: with the ceiling at 2, the third <c>q</c>'s
    /// translation by −100 moves element A from 700 to 600, below B at 650, and the tree order
    /// B, A is already right. Under the default ceiling the same <c>Q</c> undoes it, A is back at
    /// 700, and the pair is repaired.
    /// </summary>
    [Theory]
    [InlineData(2, 0)]
    [InlineData(-1, 2)]   // -1: the default ceiling
    public void Fix_SaveBeyondTheStackCeiling_IsNotRestored(int ceiling, int expectedMoved)
    {
        const string content =
            "q q q 1 0 0 1 0 -100 cm Q\n" +
            "/P <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (A) Tj ET\nEMC\n" +
            "Q Q\n" +
            "/P <</MCID 1>> BDC\nBT /F1 12 Tf 72 650 Td (B) Tj ET\nEMC\n";

        byte[] pdf = ReadingOrderBoundaryTests.BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /MarkInfo << /Marked true >> /StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            ReadingOrderBoundaryTests.StreamObject(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [9 0 R 8 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 1 >>",
        });

        try
        {
            if (ceiling >= 0) PdfLimits.MaxGraphicsStateStackDepth = ceiling;

            (int moved, IReadOnlyList<int> kids) = ReadingOrderBoundaryTests.FixAndRead(pdf);

            Assert.Equal(expectedMoved, moved);
            Assert.Equal(expectedMoved == 0 ? new[] { 9, 8 } : new[] { 8, 9 }, kids);
        }
        finally
        {
            PdfLimits.ResetToDefaults();
        }
    }
}
