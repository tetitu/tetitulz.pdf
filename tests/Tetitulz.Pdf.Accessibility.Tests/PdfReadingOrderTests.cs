// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>Tests for PdfAccessibility.AnalyzeReadingOrder and FixReadingOrder.</summary>
public class PdfReadingOrderTests
{
    // ---------------------------------------------------------------
    //  AnalyzeReadingOrder
    // ---------------------------------------------------------------

    [Fact]
    public void AnalyzeReadingOrder_NullPath_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.AnalyzeReadingOrder((string)null));
    }

    [Fact]
    public void AnalyzeReadingOrder_NullStream_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.AnalyzeReadingOrder((Stream)null));
    }

    /// <summary>
    /// An untagged document has no reading order to examine, and the analysis says so through
    /// <see cref="ReadingOrderAnalysis.Analyzed"/> — <b>while still reporting
    /// <c>IsOrdered == true</c></b>.
    ///
    /// <para>That pairing is the whole point and is asserted here deliberately: <c>IsOrdered</c>
    /// alone is <b>not</b> evidence of a good reading order, it is the default answer when nothing
    /// was looked at. GAPS.md 5c says "check <c>Analyzed</c> and <c>IsPartial</c> before trusting
    /// <c>IsOrdered</c>"; this test is what makes that contract enforceable, and a test asserting
    /// only <c>IsOrdered</c> would pass on a document the analyzer never opened.</para>
    /// </summary>
    [Fact]
    public void AnalyzeReadingOrder_UntaggedDocument_ReportsNotAnalyzedDespiteIsOrderedBeingTrue()
    {
        var analysis = PdfAccessibility.AnalyzeReadingOrder(
            new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()));

        Assert.False(analysis.Analyzed);
        Assert.Equal(0, analysis.ElementsTotal);
        Assert.Equal(0, analysis.ElementsPlaced);
        Assert.Empty(analysis.Issues);

        // The trap, pinned: "ordered" here means "nothing was examined".
        Assert.True(analysis.IsOrdered);
    }

    [Fact]
    public void AnalyzeReadingOrder_MinimalUntaggedPdf_IsOrdered()
    {
        // No structure tree → nothing to analyze → considered ordered (no issues found)
        var analysis = PdfAccessibility.AnalyzeReadingOrder(
            new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()));
        Assert.True(analysis.IsOrdered,
            "Minimal PDF with no structure tree should report no ordering issues.");
    }

    /// <summary>
    /// A document that *has* a structure tree but whose elements cannot be located on a page
    /// (no resolvable MCID, no matching <c>/ActualText</c>) must also report
    /// <c>Analyzed == false</c> rather than silently pronouncing the order fine — and the
    /// diagnostic must say which of the two it was.
    /// </summary>
    [Fact]
    public void AnalyzeReadingOrder_TaggedButUnplaceableElements_ReportsNotAnalyzedAndWhy()
    {
        var analysis = PdfAccessibility.AnalyzeReadingOrder(
            new MemoryStream(PdfAccessibilityAuditTests.TaggedPdf()));

        Assert.False(analysis.Analyzed);
        Assert.Equal(0, analysis.ElementsPlaced);

        Assert.Contains("could be located", PdfAccessibility.LastDiagnostic ?? "");
    }

    // ---------------------------------------------------------------
    //  FixReadingOrder
    // ---------------------------------------------------------------

    [Fact]
    public void FixReadingOrder_NullInput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.FixReadingOrder(null, new MemoryStream()));
    }

    [Fact]
    public void FixReadingOrder_NullOutput_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PdfAccessibility.FixReadingOrder(
                new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf()), null));
    }

    /// <summary>
    /// With no structure tree there is nothing to repair, so the document must come back
    /// <b>byte for byte</b> and the result must claim nothing. The invariant this pins is the one
    /// stated across the feature: <i>a reported count above zero implies the output differs from
    /// the input</i> — so zero must mean the input survived untouched, not that an empty revision
    /// was appended.
    /// </summary>
    [Fact]
    public void FixReadingOrder_NothingToRepair_CopiesTheDocumentThroughUnchanged()
    {
        byte[] source = PdfAccessibilityAuditTests.MinimalPdf();

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfAccessibility.FixReadingOrder(input, output);

        Assert.Equal(0, result.ElementsReordered);
        Assert.False(result.Modified);
        Assert.Equal(source, output.ToArray());
        Assert.Contains("copied through unchanged", PdfAccessibility.LastDiagnostic ?? "");
    }

    [Fact]
    public void FixReadingOrder_MinimalPdf_ZeroElementsReordered()
    {
        // Minimal PDF has no structure tree to reorder
        using var input  = new MemoryStream(PdfAccessibilityAuditTests.MinimalPdf());
        using var output = new MemoryStream();
        var result = PdfAccessibility.FixReadingOrder(input, output);
        Assert.Equal(0, result.ElementsReordered);
        Assert.False(result.Modified);
    }

    /// <summary>
    /// Same invariant on a document that *does* carry a structure tree: when the analyzer cannot
    /// prove an ordering it declines to permute, and declining must leave the bytes alone.
    /// Permuting on insufficient evidence would manufacture a defect in a file that was correct —
    /// which is why "produced some output" was never an adequate assertion here.
    /// </summary>
    [Fact]
    public void FixReadingOrder_TaggedDocumentItCannotJudge_LeavesTheBytesAlone()
    {
        byte[] source = PdfAccessibilityAuditTests.TaggedPdf();

        using var input  = new MemoryStream(source);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfAccessibility.FixReadingOrder(input, output);

        Assert.Equal(0, result.ElementsReordered);
        Assert.False(result.Modified);
        Assert.Equal(source, output.ToArray());
    }

    // ---------------------------------------------------------------
    //  ReadingOrderAnalysis / ReadingOrderIssue model
    // ---------------------------------------------------------------

    [Fact]
    public void ReadingOrderAnalysis_EmptyIssues_IsOrdered()
    {
        var analysis = new ReadingOrderAnalysis(Array.Empty<ReadingOrderIssue>());
        Assert.True(analysis.IsOrdered);
        Assert.Empty(analysis.Issues);
    }

    [Fact]
    public void ReadingOrderAnalysis_WithIssues_IsNotOrdered()
    {
        var issues = new[]
        {
            new ReadingOrderIssue("P", pageIndex: 0, treePos: 3, expectedPos: 0)
        };
        var analysis = new ReadingOrderAnalysis(issues);
        Assert.False(analysis.IsOrdered);
        Assert.Single(analysis.Issues);
    }

    [Fact]
    public void ReadingOrderIssue_Constructor_SetsProperties()
    {
        var issue = new ReadingOrderIssue("H1", 1, 4, 1);
        Assert.Equal("H1", issue.ElementType);
        Assert.Equal(1,     issue.PageIndex);
        Assert.Equal(4,     issue.TreePosition);
        Assert.Equal(1,     issue.ExpectedVisualPosition);
    }

    [Fact]
    public void ReadingOrderIssue_ToString_ContainsElementType()
    {
        var issue = new ReadingOrderIssue("H2", 0, 2, 0);
        Assert.Contains("H2", issue.ToString());
    }

    [Fact]
    public void ReadingOrderFixResult_Modified_WhenElementsReordered()
    {
        var r = new ReadingOrderFixResult(5);
        Assert.Equal(5, r.ElementsReordered);
        Assert.True(r.Modified);
    }

    [Fact]
    public void ReadingOrderFixResult_NotModified_WhenZero()
    {
        var r = new ReadingOrderFixResult(0);
        Assert.False(r.Modified);
    }

    // ===============================================================
    //  Multi-column and table layouts: the ranking must not invent one
    // ===============================================================
    //
    //  The defect these pin: /K was permuted by baseline Y alone, with no horizontal coordinate
    //  anywhere in the pass. That model only holds for a single top-to-bottom column. Given a
    //  two-column page whose /K order was RIGHT, sorting by Y interleaves the columns and the
    //  result is WRITTEN — a screen reader then announces left-para-1, right-para-1,
    //  left-para-2, right-para-2. Given a table row whose cells do not share an exact baseline,
    //  the cells are re-dealt in Y order, reversing column order and breaking every header/data
    //  association a PDF/UA checker validates. Remediation manufacturing a defect in a correct
    //  file is worse than declining to repair one, so the pass now has to prove the set it is
    //  about to rank is a single column.

    /// <summary>
    /// Two columns of three paragraphs each, in correct document order (left column top to
    /// bottom, then right column). Ranking by Y alone yields L1, R1, L2, R2, L3, R3 — the two
    /// columns interleaved — and the old pass wrote exactly that. The correct outcome is that
    /// nothing moves.
    /// </summary>
    [Fact]
    public void FixReadingOrder_TwoColumnPage_LeavesTheCorrectOrderAlone()
    {
        byte[] pdf = TwoColumnPdf();
        IReadOnlyList<int> before = DocumentKidNumbers(pdf);
        Assert.Equal(new[] { 8, 9, 10, 11, 12, 13 }, before);   // guards the fixture itself

        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);

        Assert.Equal(0, result.ElementsReordered);
        Assert.False(result.Modified);

        // The order actually in the file is what matters — ElementsReordered is only a report.
        Assert.Equal(before, DocumentKidNumbers(output.ToArray()));

        // Declining silently would be its own defect: the caller must be able to learn why.
        Assert.NotNull(PdfReadingOrderAnalyzer.LastError);
        Assert.Contains("column", PdfReadingOrderAnalyzer.LastError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same page through the analyzer: a correctly ordered two-column document must not be
    /// reported as out of order. Y-only ranking put four of its six paragraphs more than the
    /// ±1 tolerance away from their "expected" position.
    /// </summary>
    [Fact]
    public void AnalyzeReadingOrder_TwoColumnPage_ReportsNoIssues()
    {
        using var input = new MemoryStream(TwoColumnPdf(), writable: false);
        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(input);

        Assert.True(analysis.Analyzed, analysis.NotAnalyzedReason);
        Assert.True(analysis.IsOrdered,
            "a correctly ordered two-column page must produce no findings, got: " +
            string.Join("; ", Describe(analysis)));
    }

    /// <summary>
    /// A table row whose two cells sit on different baselines — the ordinary case of a
    /// vertically centred cell, or a cell set in a larger face. Y ranking swaps them, which
    /// reverses the row's columns; the cells' order is semantic, so it must survive untouched.
    /// </summary>
    [Fact]
    public void FixReadingOrder_TableRowWithStaggeredCells_KeepsColumnOrder()
    {
        byte[] pdf = TableRowPdf();
        Assert.Equal(new[] { 9, 10 }, RowKidNumbers(pdf));      // guards the fixture itself

        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);

        Assert.Equal(0, result.ElementsReordered);
        Assert.Equal(new[] { 9, 10 }, RowKidNumbers(output.ToArray()));
    }

    /// <summary>
    /// The control that stops the gates from becoming a blanket "never repair anything": a
    /// single column whose two paragraphs are genuinely in the wrong order is still repaired.
    /// </summary>
    [Fact]
    public void FixReadingOrder_SingleColumnOutOfOrder_IsStillRepaired()
    {
        byte[] pdf = ScrambledSingleColumnPdf();
        Assert.Equal(new[] { 8, 9 }, DocumentKidNumbers(pdf));

        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);

        Assert.Equal(2, result.ElementsReordered);
        Assert.Equal(new[] { 9, 8 }, DocumentKidNumbers(output.ToArray()));
    }

    /// <summary>
    /// The analyzer's control: a single column in reverse order must still be reported, or the
    /// "no issues" assertion on the two-column page above would pass vacuously.
    /// </summary>
    [Fact]
    public void AnalyzeReadingOrder_SingleColumnInReverseOrder_ReportsIssues()
    {
        using var input = new MemoryStream(ReversedSingleColumnPdf(), writable: false);
        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(input);

        Assert.True(analysis.Analyzed, analysis.NotAnalyzedReason);
        Assert.False(analysis.IsOrdered, "a reversed single column is a real reading-order defect");
    }

    /// <summary>
    /// Adversarial input for the <c>/RoleMap</c> hop the gates added: a map that sends
    /// <c>/Alpha → /Beta → /Alpha</c>. Resolving a role through it must stop at the hop cap
    /// instead of looping forever, and the pass must still do its job.
    /// </summary>
    [Fact]
    public void FixReadingOrder_CyclicRoleMap_TerminatesAndStillRepairs()
    {
        byte[] pdf = CyclicRoleMapPdf();

        (ReadingOrderFixResult Result, byte[] Bytes) run = RunWithinBudget(
            () =>
            {
                using var input  = new MemoryStream(pdf, writable: false);
                using var output = new MemoryStream();
                ReadingOrderFixResult r = PdfReadingOrderAnalyzer.Fix(input, output);
                return (Result: r, Bytes: output.ToArray());
            },
            "a /RoleMap cycle must be bounded, not followed until the process gives up");

        // Neither /Alpha nor /Beta is a role whose child order is semantic, so the two
        // paragraphs — one column, tagged bottom first — are still put right.
        Assert.Equal(2, run.Result.ElementsReordered);
        Assert.Equal(new[] { 9, 8 }, DocumentKidNumbers(run.Bytes));
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a worker thread and fails if it has not finished inside
    /// <paramref name="budgetMs"/>. An unguarded traversal of a cyclic /RoleMap does not return, so
    /// a wall-clock ceiling is the only way to say "must terminate" here.
    ///
    /// <para><b>A dedicated <see cref="Thread"/>, not <c>Task.Run</c>.</b> A pool work item is not
    /// scheduled promptly when the whole solution's test assemblies share the same cores, so the
    /// elapsed time would measure the machine's backlog rather than the walk. Blocking on
    /// <see cref="Thread.Join(int)"/> also cannot deadlock the test the way <c>Task.Wait</c> can —
    /// the same move <c>HollowSubtreeTests.RunWithinBudget</c> already makes.</para>
    /// </summary>
    private static T RunWithinBudget<T>(Func<T> operation, string what, int budgetMs = 30_000)
    {
        T result = default;
        Exception failure = null;

        var worker = new Thread(() =>
        {
            try { result = operation(); }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true, Name = "reading-order-guard" };

        worker.Start();
        Assert.True(worker.Join(budgetMs),
            what + " (it did not finish within " +
            budgetMs.ToString(CultureInfo.InvariantCulture) + " ms)");

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    // ---------------------------------------------------------------
    //  Fixtures — every one an in-memory byte[]; nothing touches disk
    // ---------------------------------------------------------------

    /// <summary>
    /// One column of two paragraphs tagged bottom-first, under an element whose <c>/S</c> is a
    /// producer role caught in a <c>/RoleMap</c> cycle.
    /// </summary>
    private static byte[] CyclicRoleMapPdf()
    {
        var content = new StringBuilder();
        AppendParagraph(content, mcid: 0, x: 72, y: 600, text: "This paragraph is lower on the page.");
        AppendParagraph(content, mcid: 1, x: 72, y: 700, text: "This paragraph is higher on the page.");

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(content.ToString()),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 "
                + "/RoleMap << /Alpha /Beta /Beta /Alpha >> >>",
            "<< /Type /StructElem /S /Alpha /P 6 0 R /Pg 3 0 R /K [8 0 R 9 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 1 >>",
            "<< /Nums [0 [8 0 R 9 0 R]] >>",
        });
    }

    /// <summary>
    /// Six paragraphs in two columns of three, tagged in the correct reading order:
    /// left column at x = 72 (y = 700, 680, 660), right column at x = 320 (the same three
    /// baselines). Ranking by Y alone cannot tell this apart from one column of six.
    /// </summary>
    private static byte[] TwoColumnPdf()
    {
        var content = new StringBuilder();
        AppendParagraph(content, mcid: 0, x: 72,  y: 700, text: "Left column paragraph one.");
        AppendParagraph(content, mcid: 1, x: 72,  y: 680, text: "Left column paragraph two.");
        AppendParagraph(content, mcid: 2, x: 72,  y: 660, text: "Left column paragraph three.");
        AppendParagraph(content, mcid: 3, x: 320, y: 700, text: "Right column paragraph one.");
        AppendParagraph(content, mcid: 4, x: 320, y: 680, text: "Right column paragraph two.");
        AppendParagraph(content, mcid: 5, x: 320, y: 660, text: "Right column paragraph three.");

        return TaggedDocumentPdf(content.ToString(), paragraphCount: 6);
    }

    /// <summary>Two paragraphs tagged bottom-first: a real, repairable single-column defect.</summary>
    private static byte[] ScrambledSingleColumnPdf()
    {
        var content = new StringBuilder();
        AppendParagraph(content, mcid: 0, x: 72, y: 600, text: "This paragraph is lower on the page.");
        AppendParagraph(content, mcid: 1, x: 72, y: 700, text: "This paragraph is higher on the page.");

        return TaggedDocumentPdf(content.ToString(), paragraphCount: 2);
    }

    /// <summary>Four paragraphs of one column, tagged bottom to top.</summary>
    private static byte[] ReversedSingleColumnPdf()
    {
        var content = new StringBuilder();
        AppendParagraph(content, mcid: 0, x: 72, y: 640, text: "Fourth on the page, first in the tree.");
        AppendParagraph(content, mcid: 1, x: 72, y: 660, text: "Third on the page.");
        AppendParagraph(content, mcid: 2, x: 72, y: 680, text: "Second on the page.");
        AppendParagraph(content, mcid: 3, x: 72, y: 700, text: "First on the page, last in the tree.");

        return TaggedDocumentPdf(content.ToString(), paragraphCount: 4);
    }

    private static void AppendParagraph(StringBuilder content, int mcid, int x, int y, string text)
    {
        content.Append("/P <</MCID ").Append(mcid.ToString(CultureInfo.InvariantCulture)).Append(">> BDC\n")
               .Append("BT /F1 12 Tf ").Append(x.ToString(CultureInfo.InvariantCulture)).Append(' ')
               .Append(y.ToString(CultureInfo.InvariantCulture)).Append(" Td (").Append(text).Append(") Tj ET\n")
               .Append("EMC\n");
    }

    /// <summary>
    /// A tagged single-page document: object 6 is the structure tree root, object 7 a
    /// <c>&lt;Document&gt;</c> whose <c>/K</c> lists <paramref name="paragraphCount"/>
    /// <c>&lt;P&gt;</c> elements starting at object 8, each pointing at the matching MCID.
    /// </summary>
    private static byte[] TaggedDocumentPdf(string pageContent, int paragraphCount)
    {
        int firstParagraph = 8;
        int parentTree     = firstParagraph + paragraphCount;

        var kids = new StringBuilder("[");
        for (int i = 0; i < paragraphCount; i++)
            kids.Append((firstParagraph + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
        kids.Append(']');

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(pageContent),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree "
                + parentTree.ToString(CultureInfo.InvariantCulture) + " 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K " + kids + " >>",
        };

        for (int i = 0; i < paragraphCount; i++)
            bodies.Add("<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K "
                       + i.ToString(CultureInfo.InvariantCulture) + " >>");

        var nums = new StringBuilder("<< /Nums [0 [");
        for (int i = 0; i < paragraphCount; i++)
            nums.Append((firstParagraph + i).ToString(CultureInfo.InvariantCulture)).Append(" 0 R ");
        nums.Append("]] >>");
        bodies.Add(nums.ToString());

        return BuildPdf(bodies);
    }

    /// <summary>
    /// A one-row table whose two cells sit on different baselines — cell A's single line at
    /// y = 700, cell B's at y = 706, which is what a vertically centred or larger-set cell
    /// looks like. Object 7 is the table, 8 the row, 9 and 10 its cells.
    /// </summary>
    private static byte[] TableRowPdf()
    {
        var content = new StringBuilder();
        content.Append("/TD <</MCID 0>> BDC\nBT /F1 12 Tf 72 700 Td (Name) Tj ET\nEMC\n")
               .Append("/TD <</MCID 1>> BDC\nBT /F1 14 Tf 320 706 Td (Value) Tj ET\nEMC\n");

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(content.ToString()),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 11 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Table /P 6 0 R /Pg 3 0 R /K [8 0 R] >>",
            "<< /Type /StructElem /S /TR /P 7 0 R /Pg 3 0 R /K [9 0 R 10 0 R] >>",
            "<< /Type /StructElem /S /TD /P 8 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /TD /P 8 0 R /Pg 3 0 R /K 1 >>",
            "<< /Nums [0 [9 0 R 10 0 R]] >>",
        });
    }

    // ---------------------------------------------------------------
    //  Read-back helpers
    // ---------------------------------------------------------------

    /// <summary>Object numbers listed by the <c>&lt;Document&gt;</c> element's <c>/K</c>.</summary>
    private static IReadOnlyList<int> DocumentKidNumbers(byte[] pdf) => KidNumbers(pdf, elementObjectNumber: 7);

    /// <summary>Object numbers listed by the <c>&lt;TR&gt;</c> element's <c>/K</c>.</summary>
    private static IReadOnlyList<int> RowKidNumbers(byte[] pdf) => KidNumbers(pdf, elementObjectNumber: 8);

    private static IReadOnlyList<int> KidNumbers(byte[] pdf, int elementObjectNumber)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var element = doc.GetObject(elementObjectNumber) as PdfDictionary;
        Assert.NotNull(element);

        var kids = doc.ResolveObject(element[new PdfName("K")]) as PdfArray;
        Assert.NotNull(kids);

        var numbers = new List<int>(kids.Count);
        for (int i = 0; i < kids.Count; i++)
        {
            var reference = kids[i] as PdfReference;
            Assert.NotNull(reference);
            numbers.Add(reference.ReferenceObjectNumber);
        }

        return numbers;
    }

    private static IEnumerable<string> Describe(ReadingOrderAnalysis analysis)
    {
        foreach (ReadingOrderIssue issue in analysis.Issues) yield return issue.ToString();
    }

    // ---------------------------------------------------------------
    //  Minimal classic-xref PDF assembly
    // ---------------------------------------------------------------

    private static string StreamObject(string data)
        => "<< /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(256 + (64 * bodies.Count));
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
