// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility.Checking;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The five accessibility defects raised by the 2026 assembly scan (plan 10, HIGH), each pinned by
/// the guard's own evidence rather than by a wall clock.
/// </summary>
/// <remarks>
/// <para>Grouped in one file because they share fixtures and because all five are the same shape of
/// mistake: a bound that was written down and then not charged, or a verdict stated more strongly
/// than the evidence supports.</para>
/// </remarks>
public class AssemblyScanFixesTests
{
    // =====================================================================
    //  1. The document-wide decoded-content budget is charged for a REFUSED page
    // =====================================================================

    /// <summary>
    /// Pages whose <c>/Contents</c> cannot be decoded spend the document-wide content budget, so a
    /// run of them exhausts it and later pages are refused with the budget as the stated reason.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>LoadPageContent</c> returned on the <c>content == null</c> path
    /// <i>before</i> <c>ctx.ContentByteBudget -= content.Length</c>, so a refused page cost zero.
    /// <c>ConcatenatePageContent</c>'s bounded decode still inflates up to the per-page ceiling
    /// before refusing — a filtered stream that declares no <c>/DL</c> cannot be measured any other
    /// way — so ten thousand pages all pointing at one Flate bomb bought ten thousand ceilings of
    /// decompression against a budget that never moved. That is exactly the "same attack with an
    /// extra loop" <c>DefaultMaxDocumentContentBytes</c> was written to stop, and the comment three
    /// lines below the early return claimed the opposite ("a page that fails to scan must not be
    /// free to repeat").</para>
    /// <para><b>The evidence asserted</b> is the refusal message the budget produces, which cannot
    /// appear unless the budget was actually decremented by the failing pages. Not elapsed time: a
    /// guard test that finishes fast has quite possibly not run.</para>
    /// </remarks>
    [Fact]
    public void Audit_PagesWhoseContentWillNotDecode_SpendTheDocumentWideBudget()
    {
        // Four pages, each /Contents a Flate stream of garbage: WouldExceedCeiling cannot predict
        // its decoded size, so each page is authorised to inflate up to the per-page ceiling and
        // then refused.
        byte[] pdf = PagesWithUndecodableContent(4);

        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf, writable: false),
                maxPageContentBytes: 1_000, maxDocumentContentBytes: 2_500);

        Assert.Contains(issues, i =>
            i.Message.IndexOf("already spent by earlier pages", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// The same document under an ample budget refuses <b>no</b> page for want of budget: the
    /// charge is proportional to the work, not a flat penalty per failure.
    /// </summary>
    /// <remarks>
    /// Without this, "charge the whole ceiling for every failure" would pass the test above while
    /// declaring an ordinary damaged document out of budget after sixteen broken pages — a
    /// limitation report on a file that has none.
    /// </remarks>
    [Fact]
    public void Audit_TheSamePagesUnderAnAmpleBudget_AreNeverRefusedForWantOfBudget()
    {
        byte[] pdf = PagesWithUndecodableContent(4);

        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(pdf, writable: false),
                PdfDeepAccessibilityChecker.DefaultMaxPageContentBytes,
                PdfDeepAccessibilityChecker.DefaultMaxDocumentContentBytes);

        Assert.DoesNotContain(issues, i =>
            i.Message.IndexOf("already spent by earlier pages", StringComparison.Ordinal) >= 0);
    }

    // =====================================================================
    //  2. A truncated /ParentTree does not accuse the document
    // =====================================================================

    /// <summary>
    /// When the <c>/ParentTree</c> could only be read in part, a page whose <c>/StructParents</c>
    /// key was never reached is reported as <b>Info</b>, not as a UA14 Error.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>Truncated</c>'s doc comment says the partial read makes
    /// <c>CheckParentTreeCoverage</c> "downgrade everything that rests on the partial map"; the code
    /// added an Info issue and then fell straight through to four <c>EmitOccurrences</c> calls that
    /// emit Errors. A well-formed 10 000-page report with more <c>/ParentTree</c> keys than the
    /// ceiling therefore had tens of thousands of Errors raised against it, and
    /// <c>IsConforming =&gt; ErrorCount == 0</c> declared a valid document non-conforming for a
    /// defect it does not have.</para>
    /// <para>The fixture reaches the same state through the <b>depth</b> ceiling rather than the
    /// entry ceiling, so it is 34 tiny objects instead of 200 001 — the two set the same
    /// <c>ParentTreeError</c> and take the same downgrade.</para>
    /// </remarks>
    [Fact]
    public void Audit_TruncatedParentTree_ReportsMissingCoverageAsInfoRatherThanError()
    {
        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(TooDeepParentTreePdf(), writable: false));

        List<AccessibilityIssue> missing = issues
            .Where(i => i.Message.IndexOf("the /ParentTree has no entry for that key",
                                          StringComparison.Ordinal) >= 0)
            .ToList();

        // The finding is still made — silence would be the other dishonesty — but it is not an
        // accusation, and it says why.
        Assert.NotEmpty(missing);
        Assert.All(missing, i => Assert.Equal(IssueSeverity.Info, i.Severity));
        Assert.All(missing, i => Assert.Contains("never reached", i.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// A document whose <c>/ParentTree</c> was read in <b>full</b> still gets the Error: the
    /// downgrade is conditional on the truncation, not a blanket softening of UA14.
    /// </summary>
    [Fact]
    public void Audit_CompleteParentTreeMissingAKey_IsStillAnError()
    {
        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(EmptyParentTreePdf(), writable: false));

        Assert.Contains(issues, i =>
            i.Severity == IssueSeverity.Error &&
            i.Message.IndexOf("the /ParentTree has no entry for that key", StringComparison.Ordinal) >= 0);
    }

    // =====================================================================
    //  3. UA12's table grid walk is bounded, and says when it gave up
    // =====================================================================

    /// <summary>
    /// A table whose declared <c>/ColSpan</c>/<c>/RowSpan</c> grid is larger than the per-table work
    /// budget is abandoned, reported through UA99, and — the important half — is <b>not</b> declared
    /// ragged on the strength of the half-built grid.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> The <c>carry</c> map holds one entry per occupied column, and both
    /// the per-cell skip loop and the per-row expiry sweep walk it linearly. <c>MaxTableRows</c>
    /// caps the rows and <c>GetSpan</c> caps one span at 1000, but nothing capped their product: a
    /// first row of cells at <c>/ColSpan 1000</c> followed by ordinary rows re-sweeps the whole
    /// column space once per row. <c>RunCheck</c> catches exceptions, not time, so the audit did not
    /// fail — it hung.</para>
    /// <para>The assertion is the guard's own evidence: the UA99 Info naming the budget, plus the
    /// absence of a raggedness Error the truncated grid would have manufactured.</para>
    /// </remarks>
    [Fact]
    public void Audit_TableWithAnOversizedSpanGrid_IsAbandonedAndSaysSoInsteadOfHanging()
    {
        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(OversizedSpanGridTablePdf(), writable: false));

        Assert.Contains(issues, i =>
            i.Severity == IssueSeverity.Info && i.Code == "UA99" &&
            i.Message.IndexOf("column regularity was not checked", StringComparison.Ordinal) >= 0);

        Assert.DoesNotContain(issues, i =>
            i.Message.IndexOf("Table is not regular", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// An ordinary two-column table with a real merge is <b>not</b> touched by the budget: a
    /// diagnostic that fires on conformant input is one callers learn to ignore.
    /// </summary>
    [Fact]
    public void Audit_OrdinaryMergedTable_IsNeverReportedAsOverBudget()
    {
        IReadOnlyList<AccessibilityIssue> issues =
            PdfDeepAccessibilityChecker.Check(new MemoryStream(SmallRegularTablePdf(), writable: false));

        Assert.DoesNotContain(issues, i =>
            i.Message.IndexOf("column regularity was not checked", StringComparison.Ordinal) >= 0);
        Assert.DoesNotContain(issues, i =>
            i.Message.IndexOf("Table is not regular", StringComparison.Ordinal) >= 0);
    }

    // =====================================================================
    //  4. A Form XObject's subtree is expanded once per page, not once per name
    // =====================================================================

    /// <summary>
    /// One page reaching one form under two hundred resource names inventories that form's own
    /// invocations <b>once</b>, not two hundred times.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>Walk</c> guarded recursion with <c>onThisPath</c>, which it
    /// removed again at the end of every iteration, so the guard stopped self-reference and nothing
    /// else. A form was fully re-tokenised — a whole <c>ContentStreamScanner.ScanDetailed</c> over
    /// its decoded bytes — once for every name that reached it and once for every path through the
    /// form graph, and <c>MaxTotalScanBytes</c> never saw any of it because it charges the one-time
    /// decode in <c>Measure</c>. A hundred thousand names on one 3 MiB form is ~300 GB of
    /// scanning.</para>
    /// <para><b>The evidence is a count, not a clock.</b> The form invokes one name that resolves to
    /// nothing, and every expansion records that as an <c>Unreadable</c> entry which
    /// <c>TagForms</c> turns into a warning. One expansion, one warning; two hundred expansions,
    /// two hundred warnings. The assertion is exact.</para>
    /// </remarks>
    [Fact]
    public void AutoTag_OneFormReachedUnderManyNames_ExpandsItsSubtreeOnlyOnce()
    {
        (AutoTagResult result, _) = AutoTag(OneFormUnderManyNamesPdf(200));

        List<string> unresolved = result.Warnings
            .Where(w => w.IndexOf("/Missing does not resolve", StringComparison.Ordinal) >= 0)
            .ToList();

        Assert.Single(unresolved);
    }

    // =====================================================================
    //  5. The duplication budget cannot be talked out of firing by an overflow
    // =====================================================================

    /// <summary>
    /// A form whose duplication cost overflows a 32-bit product is refused by the byte budget, with
    /// the budget named as the reason.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>BudgetCost</c> was <c>(form.Content?.Length ?? 0) *
    /// (targets.Count - 1)</c> in an unchecked <c>int</c> context. <c>Content.Length</c> is bounded
    /// only by <c>AutoTagOptions.MaxPageContentBytes</c> (4 MiB) and <c>targets.Count</c> only by
    /// the page count, so the product wraps negative once (pages − 1) ≥ 512 for a 4 MiB form. A
    /// negative cost passes <c>&gt; remainingBudget</c>, and <c>TagForms</c>' <c>duplicationBudget
    /// -= BudgetCost</c> then <i>raises</i> the budget, so every later form is unbounded too: 600
    /// pages × 4 MiB wrote 2.4 GB of output from a 60 KB input while the 8 MiB ceiling the type's
    /// own remarks call mandatory never fired.</para>
    /// <para>The fixture straddles the overflow exactly — 4 MiB × 599 = 2 512 388 096, which wraps
    /// to −1 782 579 200 — so it is green only for a computation done in <c>long</c>.</para>
    /// </remarks>
    [Fact]
    public void AutoTag_DuplicationCostThatOverflowsAnInt_IsStillRefusedByTheByteBudget()
    {
        (AutoTagResult result, byte[] tagged) = AutoTag(OverflowingDuplicationPdf());

        Assert.Contains(result.Warnings,
            w => w.IndexOf("budget for duplicated form content", StringComparison.Ordinal) >= 0);
        Assert.Equal(0, result.SharedFormsDuplicated);

        // And the form really was left alone, which is what the budget is protecting.
        using PdfDocument doc = PdfDocument.Open(new MemoryStream(tagged));
        Assert.Null(FormOnPage(doc, 0, "Fm0")[PdfName.StructParents]);
    }

    // =====================================================================
    //  6. A form the inventory refused is reported, not dropped
    // =====================================================================

    /// <summary>
    /// A Form XObject holding real text that the inventory refused — here because it already carries
    /// its own marked content — is reported and counted against
    /// <see cref="AutoTagResult.PagesPartiallyTagged"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>The defect.</b> <c>FormXObjectInventory.Measure</c> set <c>HasText</c> on its last
    /// line only, so every refusal path left it false, and <c>TagForms</c> tested
    /// <c>if (!form.HasText) continue;</c> <i>before</i> <c>if (form.SkipReason != null)</c>. The
    /// reporting branch was unreachable dead code: a form full of untagged text produced no warning,
    /// no <c>PagesPartiallyTagged</c> increment and a clean-looking report, while the catalog went on
    /// to assert <c>/MarkInfo /Marked true</c>.</para>
    /// </remarks>
    [Fact]
    public void AutoTag_TextBearingFormTheInventoryRefused_IsReportedRatherThanDroppedSilently()
    {
        (AutoTagResult result, _) = AutoTag(AlreadyMarkedFormPdf());

        Assert.Equal(1, result.PagesPartiallyTagged);
        Assert.Contains(result.Warnings,
            w => w.IndexOf("already carries marked content", StringComparison.Ordinal) >= 0);
    }

    /// <summary>
    /// A form with <b>no content at all</b> stays silent: "it has no content" is a fact about the
    /// file, not a coverage gap, and a channel that fires on conformant input is worse than none.
    /// </summary>
    [Fact]
    public void AutoTag_EmptyForm_IsNotReportedAsUntaggedText()
    {
        (AutoTagResult result, _) = AutoTag(EmptyFormPdf());

        Assert.Equal(0, result.PagesPartiallyTagged);
        Assert.DoesNotContain(result.Warnings,
            w => w.IndexOf("it has no content", StringComparison.Ordinal) >= 0);
    }

    // =====================================================================
    //  Fixtures
    // =====================================================================

    private const string Font =
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>";

    private const string FormText = "BT /F1 10 Tf 10 10 Td (text inside the form) Tj ET\n";
    private const string PageText =
        "BT /F1 10 Tf 72 400 Td (page level text) Tj ET\nq 1 0 0 1 0 0 cm /Fm0 Do Q\n";

    /// <summary>
    /// <paramref name="pageCount"/> pages whose <c>/Contents</c> declares <c>/FlateDecode</c> over
    /// bytes that are not zlib, so the decoder is authorised to inflate and then fails.
    /// </summary>
    private static byte[] PagesWithUndecodableContent(int pageCount)
    {
        var bodies = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };

        var kids = new StringBuilder();
        for (int i = 0; i < pageCount; i++) kids.Append(3 + i).Append(" 0 R ");
        bodies.Add("<< /Type /Pages /Kids [" + kids.ToString().Trim() + "] /Count " +
                   pageCount.ToString(CultureInfo.InvariantCulture) + " >>");

        int firstContent = 3 + pageCount;
        for (int i = 0; i < pageCount; i++)
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents " +
                       (firstContent + i).ToString(CultureInfo.InvariantCulture) + " 0 R >>");

        // Deliberately not zlib. No /DL either, so nothing about the decoded size is knowable in
        // advance — the shape TryGetKnownDecodedLength documents as unpredictable.
        string garbage = new string('ÿ', 64);
        for (int i = 0; i < pageCount; i++)
            bodies.Add(StreamObject("/Filter /FlateDecode", garbage));

        return BuildPdf(bodies);
    }

    /// <summary>
    /// A tagged one-page document whose <c>/ParentTree</c> hides its <c>/Nums</c> below a
    /// <c>/Kids</c> chain deeper than the walk's depth ceiling, so the map comes back empty and
    /// flagged as partial.
    /// </summary>
    private static byte[] TooDeepParentTreePdf() => TaggedOnePagePdf(DeepParentTreeObjects(40));

    /// <summary>The same document with a complete but empty <c>/ParentTree</c>.</summary>
    private static byte[] EmptyParentTreePdf() => TaggedOnePagePdf(new List<string> { "<< /Nums [] >>" });

    /// <summary>
    /// A <c>/Kids</c> chain <paramref name="levels"/> deep whose leaf holds the real
    /// <c>/Nums</c> — object 6 is the root, and the chain follows it.
    /// </summary>
    private static List<string> DeepParentTreeObjects(int levels)
    {
        var objects = new List<string>();
        for (int i = 0; i < levels; i++)
        {
            // Object 6 is the /ParentTree root; the chain occupies 7 … 6+levels-1.
            int nextObject = 7 + i;
            objects.Add(i == levels - 1
                ? "<< /Nums [0 [5 0 R]] >>"
                : "<< /Kids [" + nextObject.ToString(CultureInfo.InvariantCulture) + " 0 R] >>");
        }
        return objects;
    }

    /// <summary>
    /// One tagged page declaring <c>/StructParents 0</c>. Object 6 is the <c>/ParentTree</c> root
    /// and <paramref name="parentTreeObjects"/> supplies it plus anything below it.
    /// </summary>
    private static byte[] TaggedOnePagePdf(IReadOnlyList<string> parentTreeObjects)
    {
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 >>",
            "<< /Type /StructTreeRoot /K [5 0 R] /ParentTree 6 0 R >>",
            "<< /Type /StructElem /S /P /P 4 0 R /Pg 3 0 R /K [0] >>",
        };
        bodies.AddRange(parentTreeObjects);
        return BuildPdf(bodies);
    }

    /// <summary>
    /// A table whose first row declares five cells at <c>/ColSpan 1000 /RowSpan 1000</c>, followed by
    /// five hundred ordinary one-cell rows: five thousand occupied columns re-swept per row, which
    /// is past the per-table budget.
    /// </summary>
    private static byte[] OversizedSpanGridTablePdf()
    {
        const int WideCells = 5;
        const int TailRows  = 500;

        // 1 catalog, 2 pages, 3 page, 4 struct root, 5 table, 6 parent tree, 7… the rows and cells.
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R /MarkInfo << /Marked true >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 >>",
            "<< /Type /StructTreeRoot /K [5 0 R] /ParentTree 6 0 R >>",
        };

        int firstRow = 7;
        var tableKids = new StringBuilder();
        for (int r = 0; r < TailRows + 1; r++)
            tableKids.Append(firstRow + r).Append(" 0 R ");

        bodies.Add("<< /Type /StructElem /S /Table /P 4 0 R /K [" + tableKids.ToString().Trim() + "] >>");
        bodies.Add("<< /Nums [] >>");

        int firstCell = firstRow + TailRows + 1;

        // Row 0: the wide cells.
        var wideKids = new StringBuilder();
        for (int c = 0; c < WideCells; c++) wideKids.Append(firstCell + c).Append(" 0 R ");
        bodies.Add("<< /Type /StructElem /S /TR /K [" + wideKids.ToString().Trim() + "] >>");

        // Rows 1…TailRows: one ordinary cell each.
        for (int r = 0; r < TailRows; r++)
            bodies.Add("<< /Type /StructElem /S /TR /K [" +
                       (firstCell + WideCells + r).ToString(CultureInfo.InvariantCulture) + " 0 R] >>");

        for (int c = 0; c < WideCells; c++)
            bodies.Add("<< /Type /StructElem /S /TH /K [] "
                     + "/A << /O /Table /ColSpan 1000 /RowSpan 1000 /Scope /Column >> >>");

        for (int r = 0; r < TailRows; r++)
            bodies.Add("<< /Type /StructElem /S /TD /K [] >>");

        return BuildPdf(bodies);
    }

    /// <summary>A two-row, two-column table with one real vertical merge.</summary>
    private static byte[] SmallRegularTablePdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R /StructTreeRoot 4 0 R /MarkInfo << /Marked true >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /StructParents 0 >>",
        "<< /Type /StructTreeRoot /K [5 0 R] /ParentTree 6 0 R >>",
        "<< /Type /StructElem /S /Table /P 4 0 R /K [7 0 R 8 0 R] >>",
        "<< /Nums [] >>",
        "<< /Type /StructElem /S /TR /K [9 0 R 10 0 R] >>",
        "<< /Type /StructElem /S /TR /K [11 0 R] >>",
        "<< /Type /StructElem /S /TH /K [] /A << /O /Table /RowSpan 2 /Scope /Row >> >>",
        "<< /Type /StructElem /S /TD /K [] >>",
        "<< /Type /StructElem /S /TD /K [] >>",
    });

    /// <summary>
    /// One page whose <c>/XObject</c> holds <paramref name="names"/> distinct names, all pointing at
    /// one Form XObject that itself invokes a name resolving to nothing.
    /// </summary>
    private static byte[] OneFormUnderManyNamesPdf(int names)
    {
        var content = new StringBuilder("BT /F1 10 Tf 72 400 Td (page level text) Tj ET\n");
        var xobjects = new StringBuilder();
        for (int i = 0; i < names; i++)
        {
            content.Append("q /N").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" Do Q\n");
            xobjects.Append("/N").Append(i.ToString(CultureInfo.InvariantCulture)).Append(" 6 0 R ");
        }

        return BuildPdf(new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << " + xobjects.ToString().Trim() + " >> >> >>",
            StreamObject("", content.ToString()),
            Font,

            // The form draws its own text AND invokes a name its resources do not define; the
            // unresolved invocation is what makes each expansion of this subtree observable.
            StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
                + "/Resources << /Font << /F1 5 0 R >> /XObject << /Other 5 0 R >> >>",
                FormText + "q /Missing Do Q\n"),
        });
    }

    /// <summary>
    /// 600 pages sharing one form whose decoded content is exactly 4 MiB, so the duplication cost
    /// (4 194 304 × 599) overflows a 32-bit product.
    /// </summary>
    private static byte[] OverflowingDuplicationPdf()
    {
        const int PageCount = 600;
        const int FormBytes = 4 * 1024 * 1024;   // AutoTagOptions.MaxPageContentBytes exactly

        string bulky = FormText + new string(' ', FormBytes - FormText.Length);

        var bodies = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>" };

        var kids = new StringBuilder();
        for (int i = 0; i < PageCount; i++) kids.Append(3 + i).Append(" 0 R ");
        bodies.Add("<< /Type /Pages /Kids [" + kids.ToString().Trim() + "] /Count " +
                   PageCount.ToString(CultureInfo.InvariantCulture) + " >>");

        int firstContent = 3 + PageCount;
        int fontNumber   = firstContent + PageCount;
        int formNumber   = fontNumber + 1;

        for (int i = 0; i < PageCount; i++)
            bodies.Add("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents "
                     + (firstContent + i).ToString(CultureInfo.InvariantCulture) + " 0 R "
                     + "/Resources << /Font << /F1 " + fontNumber.ToString(CultureInfo.InvariantCulture)
                     + " 0 R >> /XObject << /Fm0 " + formNumber.ToString(CultureInfo.InvariantCulture)
                     + " 0 R >> >> >>");

        for (int i = 0; i < PageCount; i++) bodies.Add(StreamObject("", PageText));

        bodies.Add(Font);
        bodies.Add(StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
            + "/Resources << /Font << /F1 " + fontNumber.ToString(CultureInfo.InvariantCulture)
            + " 0 R >> >>", bulky));

        return BuildPdf(bodies);
    }

    /// <summary>One page drawing a form whose text is already inside its own marked-content pair.</summary>
    private static byte[] AlreadyMarkedFormPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
        StreamObject("", PageText),
        Font,
        StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
            + "/Resources << /Font << /F1 5 0 R >> >>",
            "/Artifact BMC\n" + FormText + "EMC\n"),
    });

    /// <summary>One page drawing a form with an empty content stream.</summary>
    private static byte[] EmptyFormPdf() => BuildPdf(new List<string>
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> >>",
        StreamObject("", PageText),
        Font,
        StreamObject("/Type /XObject /Subtype /Form /BBox [0 0 200 50] "
            + "/Resources << /Font << /F1 5 0 R >> >>", ""),
    });

    // =====================================================================
    //  Helpers
    // =====================================================================

    private static (AutoTagResult Result, byte[] Bytes) AutoTag(byte[] pdf)
    {
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();

        AutoTagResult result = PdfAutoTagger.AutoTag(input, output,
            new AutoTagOptions { CompressRewrittenStreams = false });

        return (result, output.ToArray());
    }

    private static PdfStream FormOnPage(PdfDocument doc, int pageIndex, string name)
    {
        PdfDictionary resources = doc.Pages[pageIndex].Resources;
        Assert.NotNull(resources);

        var xobjects = doc.ResolveObject(resources[PdfName.XObject]) as PdfDictionary;
        Assert.NotNull(xobjects);

        var form = doc.ResolveObject(xobjects[new PdfName(name)]) as PdfStream;
        Assert.NotNull(form);
        return form;
    }

    /// <summary>Assembles a classic-xref PDF; bodies[0] is object 1 and is always the catalog.</summary>
    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder(64 * bodies.Count + 256);
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

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string StreamObject(string dictEntries, string data)
        => "<< " + dictEntries + " /Length " + data.Length.ToString(CultureInfo.InvariantCulture)
         + " >>\nstream\n" + data + "\nendstream";
}
