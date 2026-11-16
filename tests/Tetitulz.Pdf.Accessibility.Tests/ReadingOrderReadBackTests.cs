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
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.ReadingOrder;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// Read-back tests for <see cref="PdfReadingOrderAnalyzer.Fix"/>.
///
/// <para>
/// <b>What this file exists to close.</b> <c>Fix</c> permutes the <c>/K</c> arrays of structure
/// elements, and every previous test observed only the returned count and that <em>some</em> bytes
/// came out. A reordering function whose reordering is never read back is untested: a <c>Fix</c>
/// that computed the permutation, reported it, and then wrote the original array would have passed
/// all of them. <b>Every assertion here re-opens the produced PDF and walks the parsed structure
/// tree.</b> The input is never asserted on, and a returned count is only ever asserted
/// <em>against</em> what the output actually contains.
/// </para>
///
/// <para>
/// <b>Both halves of the contract are pinned.</b> <c>Fix</c> no longer sorts by Y: it measures the
/// geometry of each entry, applies a semantic-role gate, and <em>declines</em> for any set it
/// cannot prove is a single top-to-bottom column. "Declines correctly, and says why" is therefore
/// as much of the contract as "reorders correctly", and both are asserted from the output file.
/// </para>
/// </summary>
public class ReadingOrderReadBackTests
{
    /// <summary>Object number of the <c>&lt;Document&gt;</c> element in every fixture below.</summary>
    private const int DocumentElement = 7;

    /// <summary>Object number of the first <c>&lt;P&gt;</c> element in every fixture below.</summary>
    private const int FirstParagraph = 8;

    // ===============================================================
    //  1. The reorder itself, read back out of the produced file
    // ===============================================================

    /// <summary>
    /// A single column of three paragraphs tagged in a scrambled order (middle, top, bottom).
    /// The whole point of the pass is that the <c>/K</c> array in the <b>output</b> lists them
    /// top to bottom — so that is what is read back and asserted, both as object numbers and as
    /// the baselines those objects actually point at.
    /// </summary>
    [Fact]
    public void Fix_SingleColumnOutOfOrder_KArrayInOutputIsTopToBottom()
    {
        // mcid 0 → y 660 (middle), mcid 1 → y 700 (top), mcid 2 → y 680 (upper middle).
        float[] baselines = { 660f, 700f, 680f };
        byte[] pdf = SingleColumnPdf(x: 72, baselines);

        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);

        byte[] fixedPdf = output.ToArray();

        // (a) the array read back out of the OUTPUT is in visual top-to-bottom order.
        Assert.Equal(new[] { 700f, 680f, 660f }, KidBaselines(fixedPdf, DocumentElement, baselines));

        // (b) …which for this fixture means exactly this permutation of the /K entries.
        Assert.Equal(new[] { 9, 10, 8 }, KidObjectNumbers(fixedPdf, DocumentElement));

        // (c) a permutation, not a rewrite: same length, same membership, nothing dropped or
        //     invented. Dropping an entry would silently delete tagged content from the tree.
        AssertSameMembership(
            KidObjectNumbers(pdf, DocumentElement),
            KidObjectNumbers(fixedPdf, DocumentElement));

        // (d) only now the report — and it is checked against the output, not trusted.
        Assert.True(result.Modified);
        Assert.Equal(
            ChangedSlots(KidObjectNumbers(pdf, DocumentElement), KidObjectNumbers(fixedPdf, DocumentElement)).Count,
            result.ElementsReordered);
    }

    // ===============================================================
    //  2. The count is the number that moved, not the number touched
    // ===============================================================

    /// <summary>
    /// Four paragraphs of one column, of which only the last two are transposed. An earlier
    /// defect reported every child of every touched parent, which for this document is 4; the
    /// honest answer is 2, and the output is what decides it.
    /// </summary>
    [Fact]
    public void Fix_CountsOnlyTheEntriesWhoseSlotOccupantChanged()
    {
        // Tree order 700, 680, 640, 660 — the first two are already right, the last two swap.
        float[] baselines = { 700f, 680f, 640f, 660f };
        byte[] pdf = SingleColumnPdf(x: 72, baselines);

        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);

        byte[] fixedPdf = output.ToArray();

        IReadOnlyList<int> before = KidObjectNumbers(pdf, DocumentElement);
        IReadOnlyList<int> after  = KidObjectNumbers(fixedPdf, DocumentElement);

        Assert.Equal(new[] { 700f, 680f, 660f, 640f }, KidBaselines(fixedPdf, DocumentElement, baselines));
        Assert.Equal(new[] { 8, 9, 11, 10 }, after);

        IReadOnlyList<int> changed = ChangedSlots(before, after);
        Assert.Equal(new[] { 2, 3 }, changed);

        // 2, not 4: the array has four entries and all four have a positioned occupant, so a
        // count of "every child of the touched parent" is arithmetically distinguishable here.
        Assert.Equal(2, result.ElementsReordered);
        Assert.Equal(4, after.Count);
    }

    // ===============================================================
    //  3. Already correct → untouched, and provably so
    // ===============================================================

    /// <summary>
    /// A document already in visual order must come out byte-identical: no permutation, no
    /// appended revision, nothing reported. A pass that "reorders" an ordered array into itself
    /// but still appends a revision has grown the file and re-signed nothing for no reason.
    /// </summary>
    [Fact]
    public void Fix_DocumentAlreadyInOrder_IsCopiedThroughByteIdentical()
    {
        float[] baselines = { 700f, 680f, 660f };
        byte[] pdf = SingleColumnPdf(x: 72, baselines);

        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);

        byte[] fixedPdf = output.ToArray();

        Assert.Equal(0, result.ElementsReordered);
        Assert.False(result.Modified);

        // No appended revision: an incremental update would add a second startxref/%%EOF pair.
        Assert.Equal(1, Occurrences(fixedPdf, "startxref"));
        Assert.Equal(1, Occurrences(fixedPdf, "%%EOF"));
        Assert.Equal(pdf, fixedPdf);

        // The tree in the output still reads top to bottom, i.e. "unchanged" here is "there was
        // nothing to do" — not "unchanged and still wrong".
        Assert.Equal(new[] { 700f, 680f, 660f }, KidBaselines(fixedPdf, DocumentElement, baselines));

        // …and specifically not "unchanged because the pass gave up": every decline path reports
        // one of these two phrases, and none of them ran.
        string diagnostic = PdfReadingOrderAnalyzer.LastError ?? string.Empty;
        Assert.DoesNotContain("original order", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("left unchanged", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    // ===============================================================
    //  4. A genuine multi-column page is declined — and says so
    // ===============================================================

    /// <summary>
    /// Six paragraphs in two columns of three, tagged in the correct reading order (whole left
    /// column, then whole right column). Ranking by baseline alone interleaves them into
    /// L1 R1 L2 R2 L3 R3 and <c>Fix</c> would write that, so the pass must decline. Declining
    /// silently would be its own defect: the caller has to be able to learn that a repair was
    /// refused rather than that the document was clean.
    /// </summary>
    [Fact]
    public void Fix_TwoColumnDocument_IsDeclinedAndReportsWhy()
    {
        byte[] pdf = TwoColumnPdf();

        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);

        byte[] fixedPdf = output.ToArray();

        // The /K array read back from the output is the one that went in — entry for entry.
        Assert.Equal(new[] { 8, 9, 10, 11, 12, 13 }, KidObjectNumbers(fixedPdf, DocumentElement));
        Assert.Equal(KidObjectNumbers(pdf, DocumentElement), KidObjectNumbers(fixedPdf, DocumentElement));
        Assert.Equal(0, result.ElementsReordered);
        Assert.False(result.Modified);

        // …and the pass says why it declined, rather than being indistinguishable from a
        // document it found nothing wrong with.
        string reason = PdfReadingOrderAnalyzer.LastError;
        Assert.False(string.IsNullOrEmpty(reason));
        Assert.Contains("column", reason, StringComparison.OrdinalIgnoreCase);

        // Control: the very same code path DOES repair a single column, so the assertion above
        // is not just "this pass never touches anything".
        using var control       = new MemoryStream(SingleColumnPdf(x: 72, new[] { 600f, 700f }), writable: false);
        using var controlOutput = new MemoryStream();
        Assert.Equal(2, PdfReadingOrderAnalyzer.Fix(control, controlOutput).ElementsReordered);
        Assert.Equal(new[] { 9, 8 }, KidObjectNumbers(controlOutput.ToArray(), DocumentElement));
    }

    // ===============================================================
    //  5. "Not analysed" is not "no issues"
    // ===============================================================

    /// <summary>
    /// A structure tree whose elements point at marked-content identifiers that the page's
    /// content stream never opens: nothing can be positioned, so nothing can be compared. The
    /// old code returned an empty issue list for this, i.e. reported an analysis failure as a
    /// clean bill of health. The distinction is the whole reason
    /// <see cref="ReadingOrderAnalysis.Analyzed"/> exists, so it is asserted explicitly — and
    /// against a measurable control, so it cannot pass by the analyzer simply never working.
    /// </summary>
    [Fact]
    public void Analyze_UnmeasurableDocument_ReportsNotAnalyzedRatherThanNoIssues()
    {
        using var input = new MemoryStream(UnmeasurablePdf(), writable: false);
        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(input);

        Assert.False(analysis.Analyzed);
        Assert.False(string.IsNullOrEmpty(analysis.NotAnalyzedReason));
        Assert.Empty(analysis.Issues);

        // IsOrdered is true here — an empty issue list always is. That is exactly why it may not
        // be read on its own: with Analyzed false it means "nothing was examined", and a caller
        // that trusted it would be told an unexaminable document is correctly ordered.
        Assert.True(analysis.IsOrdered);
        Assert.Contains("not analyzed", analysis.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrEmpty(PdfReadingOrderAnalyzer.LastError));

        // Control: a document whose elements CAN be measured is analysed, so the assertions
        // above pin a distinction rather than a uniformly broken analyzer.
        using var measurable = new MemoryStream(SingleColumnPdf(x: 72, new[] { 700f, 680f, 660f }), writable: false);
        ReadingOrderAnalysis ok = PdfReadingOrderAnalyzer.Analyze(measurable);
        Assert.True(ok.Analyzed, ok.NotAnalyzedReason);
        Assert.Null(ok.NotAnalyzedReason);
        Assert.True(ok.ElementsPlaced >= 3);
    }

    // ===============================================================
    //  6. Analyze and Fix agree about the same document
    // ===============================================================

    /// <summary>
    /// The producer/checker-consistency property: the elements <c>Analyze</c> reports as sitting
    /// away from their visual position are exactly the <c>/K</c> slots whose occupant
    /// <c>Fix</c> changes. A checker that flags what the repairer will not touch (or a repairer
    /// that moves what the checker calls fine) leaves a document that fails its own audit after
    /// remediation.
    ///
    /// <para>
    /// Five paragraphs of one column tagged bottom-to-top. <c>Analyze</c>'s ±1 tolerance leaves
    /// the middle element unreported, and the ranking leaves that same middle slot's occupant
    /// unchanged — so the two sets coincide, which is the strongest form the property takes.
    /// </para>
    /// </summary>
    [Fact]
    public void AnalyzeAndFix_AgreeOnWhichElementsAreOutOfOrder()
    {
        float[] baselines = { 620f, 640f, 660f, 680f, 700f };   // tagged bottom-first
        byte[] pdf = SingleColumnPdf(x: 72, baselines);

        using var toAnalyze = new MemoryStream(pdf, writable: false);
        ReadingOrderAnalysis analysis = PdfReadingOrderAnalyzer.Analyze(toAnalyze);
        Assert.True(analysis.Analyzed, analysis.NotAnalyzedReason);
        Assert.False(analysis.IsOrdered, "five paragraphs tagged bottom-to-top are out of order");

        var reported = new List<int>();
        foreach (ReadingOrderIssue issue in analysis.Issues) reported.Add(issue.TreePosition);
        reported.Sort();

        using var input  = new MemoryStream(pdf, writable: false);
        using var output = new MemoryStream();
        ReadingOrderFixResult result = PdfReadingOrderAnalyzer.Fix(input, output);

        byte[] fixedPdf = output.ToArray();
        IReadOnlyList<int> moved = ChangedSlots(
            KidObjectNumbers(pdf, DocumentElement), KidObjectNumbers(fixedPdf, DocumentElement));

        Assert.Equal(reported, moved);
        Assert.Equal(moved.Count, result.ElementsReordered);

        // …and the repair the two agreed on is the right one: fully reversed, top to bottom.
        Assert.Equal(new[] { 700f, 680f, 660f, 640f, 620f }, KidBaselines(fixedPdf, DocumentElement, baselines));

        // The other direction of the same property: a document Analyze calls ordered is one Fix
        // leaves alone. Agreement that only holds for broken documents is half a property.
        byte[] ordered = SingleColumnPdf(x: 72, new[] { 700f, 680f, 660f });
        using var orderedToAnalyze = new MemoryStream(ordered, writable: false);
        Assert.True(PdfReadingOrderAnalyzer.Analyze(orderedToAnalyze).IsOrdered);

        using var orderedInput  = new MemoryStream(ordered, writable: false);
        using var orderedOutput = new MemoryStream();
        Assert.Equal(0, PdfReadingOrderAnalyzer.Fix(orderedInput, orderedOutput).ElementsReordered);
        Assert.Empty(ChangedSlots(
            KidObjectNumbers(ordered, DocumentElement),
            KidObjectNumbers(orderedOutput.ToArray(), DocumentElement)));
    }

    // ===============================================================
    //  7. Adversarial: a cyclic /K graph
    // ===============================================================

    /// <summary>
    /// A <c>/K</c> graph with a back edge (a <c>Sect</c> whose <c>/K</c> lists its own ancestor).
    /// An unguarded walk revisits the cycle until the process gives up; the traversal must
    /// terminate promptly <em>and</em> still hand back a usable document — so this asserts both
    /// the bound and the repair, read back from the output.
    /// </summary>
    [Fact]
    public void Fix_CyclicKidGraph_TerminatesAndStillProducesAUsableResult()
    {
        byte[] pdf = CyclicKidsPdf();

        (ReadingOrderFixResult Result, byte[] Bytes) run = RunWithinBudget(
            () =>
            {
                using var input  = new MemoryStream(pdf, writable: false);
                using var output = new MemoryStream();
                ReadingOrderFixResult r = PdfReadingOrderAnalyzer.Fix(input, output);
                return (Result: r, Bytes: output.ToArray());
            },
            "a /K cycle must be bounded by the visited set, not followed until the process gives up");

        byte[] fixedPdf = run.Bytes;

        // The two positioned paragraphs are put right; the unpositionable <Sect> — its own /K
        // reaches only structure elements — keeps the slot it occupied, so the array's length and
        // membership survive.
        Assert.Equal(new[] { 9, 8, 10 }, KidObjectNumbers(fixedPdf, DocumentElement));
        Assert.Equal(2, run.Result.ElementsReordered);
        AssertSameMembership(KidObjectNumbers(pdf, DocumentElement), KidObjectNumbers(fixedPdf, DocumentElement));

        // The cycle's own array is intact too, and the output is still a parseable document whose
        // back edge is preserved rather than broken by the edit.
        Assert.Equal(new[] { 11, DocumentElement }, KidObjectNumbers(fixedPdf, 10));
    }

    /// <summary>
    /// Runs <paramref name="operation"/> on a worker thread and fails if it has not finished inside
    /// <paramref name="budgetMs"/>. An unguarded traversal of a cyclic /K graph does not return, so
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
        { IsBackground = true, Name = "reading-order-readback-guard" };

        worker.Start();
        Assert.True(worker.Join(budgetMs),
            what + " (it did not finish within " +
            budgetMs.ToString(CultureInfo.InvariantCulture) + " ms)");

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return result;
    }

    // ---------------------------------------------------------------
    //  Read-back helpers — every one of them opens the produced bytes
    // ---------------------------------------------------------------

    /// <summary>
    /// Object numbers listed by an element's <c>/K</c>, read out of a parsed PDF. Fails the test
    /// (rather than returning a defaulted list) if anything is not a reference, so a rewritten
    /// array that lost an entry cannot be mistaken for a shorter correct one.
    /// </summary>
    private static IReadOnlyList<int> KidObjectNumbers(byte[] pdf, int elementObjectNumber)
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

    /// <summary>
    /// The baselines an element's <c>/K</c> entries actually point at, in the order the array
    /// lists them: each child is resolved, its marked-content identifier read, and that identifier
    /// mapped through the fixture's mcid → Y table. This is the assertion that makes the test
    /// about <em>visual</em> order rather than about object numbers.
    /// </summary>
    private static IReadOnlyList<float> KidBaselines(byte[] pdf, int elementObjectNumber, float[] baselinesByMcid)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));

        var element = doc.GetObject(elementObjectNumber) as PdfDictionary;
        Assert.NotNull(element);

        var kids = doc.ResolveObject(element[new PdfName("K")]) as PdfArray;
        Assert.NotNull(kids);

        var result = new List<float>(kids.Count);
        for (int i = 0; i < kids.Count; i++)
        {
            var child = doc.ResolveObject(kids[i]) as PdfDictionary;
            Assert.NotNull(child);

            var mcid = child[new PdfName("K")] as PdfNumber;
            Assert.NotNull(mcid);
            Assert.InRange(mcid.IntValue, 0, baselinesByMcid.Length - 1);

            result.Add(baselinesByMcid[mcid.IntValue]);
        }

        return result;
    }

    /// <summary>Indices at which two <c>/K</c> readings disagree — the slots whose occupant changed.</summary>
    private static IReadOnlyList<int> ChangedSlots(IReadOnlyList<int> before, IReadOnlyList<int> after)
    {
        Assert.Equal(before.Count, after.Count);

        var changed = new List<int>();
        for (int i = 0; i < before.Count; i++)
            if (before[i] != after[i]) changed.Add(i);

        return changed;
    }

    private static void AssertSameMembership(IReadOnlyList<int> before, IReadOnlyList<int> after)
    {
        var a = new List<int>(before);
        var b = new List<int>(after);
        a.Sort();
        b.Sort();
        Assert.Equal(a, b);
    }

    private static int Occurrences(byte[] pdf, string needle)
    {
        string text = Encoding.ASCII.GetString(pdf);
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    // ---------------------------------------------------------------
    //  Fixtures — in-memory byte[] only; nothing here touches disk
    // ---------------------------------------------------------------

    /// <summary>
    /// A tagged one-page document: object 6 is the structure tree root, object 7 a
    /// <c>&lt;Document&gt;</c> whose <c>/K</c> lists one <c>&lt;P&gt;</c> per baseline starting at
    /// object 8, in the order given. Paragraph <c>i</c> owns marked-content identifier <c>i</c>
    /// and is drawn at <paramref name="x"/>, <c>baselines[i]</c> — so the tree order is the array
    /// order and the visual order is the sorted-descending order.
    /// </summary>
    private static byte[] SingleColumnPdf(int x, float[] baselines)
    {
        var content = new StringBuilder();
        for (int i = 0; i < baselines.Length; i++)
            AppendParagraph(content, i, x, baselines[i], "Paragraph " + Num(i + 1) + " of the fixture.");

        return TaggedDocumentPdf(content.ToString(), baselines.Length);
    }

    /// <summary>
    /// Two columns of three paragraphs, tagged in the correct reading order: the left column at
    /// x = 72 (y = 700, 680, 660) then the right column at x = 320 on the same three baselines.
    /// The columns share baselines exactly, so only the horizontal step-back gives them away.
    /// </summary>
    private static byte[] TwoColumnPdf()
    {
        var content = new StringBuilder();
        AppendParagraph(content, 0, 72,  700f, "Left column paragraph one.");
        AppendParagraph(content, 1, 72,  680f, "Left column paragraph two.");
        AppendParagraph(content, 2, 72,  660f, "Left column paragraph three.");
        AppendParagraph(content, 3, 320, 700f, "Right column paragraph one.");
        AppendParagraph(content, 4, 320, 680f, "Right column paragraph two.");
        AppendParagraph(content, 5, 320, 660f, "Right column paragraph three.");

        return TaggedDocumentPdf(content.ToString(), paragraphCount: 6);
    }

    /// <summary>
    /// A structure tree that claims marked content the page never opens: the content stream draws
    /// its text with no <c>BDC</c>/<c>EMC</c> at all, so no <c>/MCID</c> resolves, and no element
    /// carries <c>/ActualText</c> for the fallback to match. Nothing can be positioned, so nothing
    /// can be compared — which must be reported as such, not as "no issues".
    /// </summary>
    private static byte[] UnmeasurablePdf()
    {
        const string content =
            "BT /F1 12 Tf 72 700 Td (Untagged first line.) Tj ET\n" +
            "BT /F1 12 Tf 72 680 Td (Untagged second line.) Tj ET\n";

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(content),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 10 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [8 0 R 9 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 1 >>",
            "<< /Nums [0 [8 0 R 9 0 R]] >>",
        });
    }

    /// <summary>
    /// One column of two paragraphs tagged bottom-first, plus a <c>&lt;Sect&gt;</c> (object 10)
    /// whose <c>/K</c> lists its own ancestor, object 7 — a genuine cycle in the <c>/K</c> graph.
    /// The <c>&lt;Sect&gt;</c> reaches no marked content of its own, so it is unpositionable and
    /// must stay pinned in the slot it occupies while its two siblings are put right.
    /// </summary>
    private static byte[] CyclicKidsPdf()
    {
        var content = new StringBuilder();
        AppendParagraph(content, 0, 72, 620f, "Lower paragraph, tagged first.");
        AppendParagraph(content, 1, 72, 660f, "Higher paragraph, tagged second.");
        AppendParagraph(content, 2, 72, 700f, "Paragraph inside the cyclic section.");

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /Lang (en-US) /MarkInfo << /Marked true >> "
                + "/StructTreeRoot 6 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/StructParents 0 /Resources << /Font << /F1 5 0 R >> >> >>",
            StreamObject(content.ToString()),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree 12 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K [8 0 R 9 0 R 10 0 R] >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 0 >>",
            "<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K 1 >>",
            "<< /Type /StructElem /S /Sect /P 7 0 R /Pg 3 0 R /K [11 0 R 7 0 R] >>",
            "<< /Type /StructElem /S /P /P 10 0 R /Pg 3 0 R /K 2 >>",
            "<< /Nums [0 [8 0 R 9 0 R 11 0 R]] >>",
        });
    }

    private static void AppendParagraph(StringBuilder content, int mcid, int x, float y, string text)
    {
        content.Append("/P <</MCID ").Append(Num(mcid)).Append(">> BDC\n")
               .Append("BT /F1 12 Tf ").Append(Num(x)).Append(' ').Append(Num(y))
               .Append(" Td (").Append(text).Append(") Tj ET\n")
               .Append("EMC\n");
    }

    private static byte[] TaggedDocumentPdf(string pageContent, int paragraphCount)
    {
        int parentTree = FirstParagraph + paragraphCount;

        var kids = new StringBuilder("[");
        for (int i = 0; i < paragraphCount; i++) kids.Append(Num(FirstParagraph + i)).Append(" 0 R ");
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
            "<< /Type /StructTreeRoot /K [7 0 R] /ParentTree " + Num(parentTree)
                + " 0 R /ParentTreeNextKey 1 >>",
            "<< /Type /StructElem /S /Document /P 6 0 R /Pg 3 0 R /K " + kids + " >>",
        };

        for (int i = 0; i < paragraphCount; i++)
            bodies.Add("<< /Type /StructElem /S /P /P 7 0 R /Pg 3 0 R /K " + Num(i) + " >>");

        var nums = new StringBuilder("<< /Nums [0 [");
        for (int i = 0; i < paragraphCount; i++) nums.Append(Num(FirstParagraph + i)).Append(" 0 R ");
        nums.Append("]] >>");
        bodies.Add(nums.ToString());

        return BuildPdf(bodies);
    }

    // ---------------------------------------------------------------
    //  Minimal classic-xref PDF assembly
    // ---------------------------------------------------------------

    private static string Num(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Num(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string StreamObject(string data)
        => "<< /Length " + Num(data.Length) + " >>\nstream\n" + data + "\nendstream";

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(256 + (64 * bodies.Count));
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(Num(i + 1)).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(Num(bodies.Count + 1)).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(Num(bodies.Count + 1)).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
