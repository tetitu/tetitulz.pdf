// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Accessibility;
using Tetitulz.Pdf.Accessibility.Models;
using Tetitulz.Pdf.Accessibility.Internal;
using Tetitulz.Pdf.Accessibility.Tagging;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// <c>AutoTagOptions.InferTables</c> — consecutive rows whose cells start at the same set of X
/// positions are tagged <c>/Table</c> → <c>/TR</c> → <c>/TH</c>|<c>/TD</c> instead of a run of
/// <c>/P</c>.
/// </summary>
/// <remarks>
/// <para><b>The structural first here is the granularity, not the nesting.</b> Lists needed a
/// parent, which <c>LogicalElement.Parent</c> supplied; tables need something no pass in this
/// feature had ever done — a marked-content sequence <i>finer than a line</i>. A row of three cells
/// drawn in one <c>BT…ET</c> is one line to every other classifier here, and tagging it as one
/// <c>/TD</c> would produce a table of one column.</para>
///
/// <para><b>Most of these are refusals, and the reason is sharper than it was for lists.</b> A
/// table has a required internal shape, and this library's own deep checker reports a ragged
/// one — rows spanning different numbers of columns — as a UA12 <b>Error</b>. So an over-eager
/// inference does not merely mislabel; it manufactures the finding the feature exists to remove.
/// <see cref="AnInferredTablePassesThisLibrarysOwnTableChecks"/> is the test that closes the
/// loop.</para>
///
/// <para>Assertions walk the real structure tree rather than the bytes: <c>"/S /T"</c> would be
/// satisfied by four different roles, and <c>"/S /TD"</c> tells a reader nothing about which
/// <c>/TR</c> the cell is in — which is the half UA12 is about.</para>
/// </remarks>
public sealed class TableInferenceTests
{
    // A grid whose columns are far enough apart that no tolerance question arises, and whose
    // first row is the one the default classifier turns into headers.
    private static readonly string[][] Grid =
    {
        new[] { "Region", "Sales", "Share" },
        new[] { "North",  "1200",  "41%"   },
        new[] { "South",  "900",   "31%"   },
    };

    private static readonly float[] Columns = { 72f, 240f, 400f };

    /// <summary>A three-column grid becomes one table of three rows and nine cells.</summary>
    [Fact]
    public void AnAlignedGridBecomesATable()
    {
        Tree tree = Tag(RowPerTextObject(Grid, Columns), infer: true);

        Assert.Equal(1, tree.Count("Table"));
        Assert.Equal(3, tree.Count("TR"));
        Assert.Equal(3, tree.Count("TH"));
        Assert.Equal(6, tree.Count("TD"));
        Assert.Equal(0, tree.Count("P"));
    }

    /// <summary>The same page without the option is a run of paragraphs.</summary>
    /// <remarks>
    /// <b>The acceptance control for the whole file, and for the default.</b> Existing callers must
    /// see exactly what they saw before, so the opt-in has to be the only thing that changes the
    /// answer — not the presence of a grid on the page.
    /// </remarks>
    [Fact]
    public void TheSamePageWithoutTheOptionIsStillParagraphs()
    {
        Tree tree = Tag(RowPerTextObject(Grid, Columns), infer: false);

        Assert.Equal(0, tree.Count("Table"));
        Assert.Equal(0, tree.Count("TD"));
        Assert.Equal(3, tree.Count("P"));
    }

    /// <summary>
    /// Each cell is its own marked-content sequence, so a three-cell row is three MCIDs rather
    /// than one.
    /// </summary>
    /// <remarks>
    /// <b>Counting roles cannot see this and it is the whole of the change.</b> A tree naming nine
    /// cells that all point at three MCIDs — one per row — has the same census as a correct one,
    /// and is what a line-granular tagger would have produced. The cells would then be
    /// indistinguishable to a screen reader, which reads a cell by reading its marked content.
    /// </remarks>
    [Fact]
    public void EveryCellOwnsItsOwnMarkedContent()
    {
        byte[] pdf = TaggedBytes(RowPerTextObject(Grid, Columns), infer: true);
        Tree tree = new Tree(pdf);

        List<int> mcids = tree.McidsOfEvery("TD").Concat(tree.McidsOfEvery("TH")).ToList();

        Assert.Equal(9, mcids.Count);
        Assert.Equal(9, mcids.Distinct().Count());
    }

    /// <summary>The rows are children of the table, and each row holds only cells.</summary>
    /// <remarks>
    /// <b>UA12's actual requirement.</b> Nine <c>/TD</c> hanging off <c>/Document</c> beside a
    /// <c>/Table</c> has the same census as a correct tree; the containment is the claim.
    /// </remarks>
    [Fact]
    public void TheTableContainsRowsAndEachRowOnlyCells()
    {
        Tree tree = Tag(RowPerTextObject(Grid, Columns), infer: true);

        Assert.Equal(new[] { "TR", "TR", "TR" }, tree.ChildRolesOfFirst("Table"));
        Assert.Equal(new[] { "TH", "TH", "TH" }, tree.ChildRolesOfFirst("TR"));

        // And nothing is reachable twice: an element listed under /Document as well as under its
        // parent has two parents, and its /P agrees with only one of them.
        Assert.Equal(new[] { "Table" }, tree.RootRoles);
    }

    /// <summary>
    /// A row drawn as one text object per cell produces the same table as one drawn as a single
    /// text object.
    /// </summary>
    /// <remarks>
    /// <b>Both shapes are common and neither is detectable by the reading that finds the other.</b>
    /// A word processor emits a <c>BT…ET</c> per cell, which reaches the tagger as three lines
    /// sharing a baseline; a report generator emits one per row, which reaches it as one line with
    /// three shows. Asserting the two produce the same tree is what stops a later change fixing one
    /// and losing the other.
    /// </remarks>
    [Fact]
    public void ARowDrawnAsSeparateTextObjectsGivesTheSameTable()
    {
        Tree perCell = Tag(CellPerTextObject(Grid, Columns), infer: true);
        Tree perRow  = Tag(RowPerTextObject(Grid, Columns), infer: true);

        Assert.Equal(perRow.Count("Table"), perCell.Count("Table"));
        Assert.Equal(perRow.Count("TR"),    perCell.Count("TR"));
        Assert.Equal(perRow.Count("TH"),    perCell.Count("TH"));
        Assert.Equal(perRow.Count("TD"),    perCell.Count("TD"));
        Assert.Equal(1, perCell.Count("Table"));
    }

    /// <summary>A row missing a cell is not a table.</summary>
    /// <remarks>
    /// <b>The refusal that keeps the feature from manufacturing a UA12 Error.</b> Padding the gap
    /// would invent a cell; not padding it makes the rows span different column counts, which the
    /// deep checker reports as a ragged table. Refusing the whole run is the only answer that is
    /// neither.
    /// </remarks>
    [Fact]
    public void ARowWithAMissingCellIsNotATable()
    {
        var ragged = new[]
        {
            new[] { "Region", "Sales", "Share" },
            new[] { "North",  "1200" },
            new[] { "South",  "900",   "31%" },
        };

        Tree tree = Tag(RowPerTextObject(ragged, Columns), infer: true);

        Assert.Equal(0, tree.Count("Table"));
        Assert.Equal(0, tree.Count("TD"));
    }

    /// <summary>A single aligned row is not a table.</summary>
    /// <remarks>
    /// One row of tab stops is a line, not evidence of a grid, and PDF/UA is satisfied by tagging
    /// it as a paragraph.
    /// </remarks>
    [Fact]
    public void ASingleAlignedRowIsNotATable()
    {
        Tree tree = Tag(RowPerTextObject(new[] { new[] { "Region", "Sales", "Share" } }, Columns),
                        infer: true);

        Assert.Equal(0, tree.Count("Table"));
    }

    /// <summary>A run of ordinary left-aligned paragraphs is not a one-column table.</summary>
    /// <remarks>
    /// <b>Every paragraph on the page shares a left edge, which is why one column can never be
    /// evidence.</b> The test exists because the two-column minimum is the only thing standing
    /// between this feature and tagging the body of every document as a table; relaxing it to one
    /// leaves every other assertion in this file green.
    /// </remarks>
    [Fact]
    public void LeftAlignedProseIsNotAOneColumnTable()
    {
        string content =
            "BT /F1 12 Tf 72 700 Td (The first paragraph of the page.) Tj ET\n" +
            "BT /F1 12 Tf 72 670 Td (The second, at the same margin.) Tj ET\n" +
            "BT /F1 12 Tf 72 640 Td (And a third, likewise aligned.) Tj ET\n";

        Tree tree = Tag(content, infer: true);

        Assert.Equal(0, tree.Count("Table"));
        Assert.Equal(0, tree.Count("TD"));
        Assert.Equal(3, tree.Count("P"));
    }

    /// <summary>Two adjacent grids of different widths are two tables.</summary>
    /// <remarks>
    /// <b>Nothing separates them but the change of column count</b> — no paragraph, no gap the
    /// wrapper could key off. So this is the only test that can see whether the grouping honours
    /// the table a cell belongs to or merely runs until it meets something that is not a cell. The
    /// difference matters: joined, the six cells would form one table whose rows span two columns
    /// and then three, which is the ragged-table Error.
    /// </remarks>
    [Fact]
    public void TwoAdjacentGridsOfDifferentWidthsAreTwoTables()
    {
        float[] narrow = { 72f, 240f };

        string content =
            Row(new[] { "Region", "Sales" }, narrow, 700) +
            Row(new[] { "North",  "1200"  }, narrow, 670) +
            Row(new[] { "Region", "Sales", "Share" }, Columns, 640) +
            Row(new[] { "South",  "900",   "31%"   }, Columns, 610);

        Tree tree = Tag(content, infer: true);

        Assert.Equal(2, tree.Count("Table"));
        Assert.Equal(4, tree.Count("TR"));
        Assert.Equal(0, tree.Count("P"));

        // Each table's rows are its own: 2 + 2 cells and 3 + 3 cells, never 5 and 5.
        Assert.Equal(new[] { "TR", "TR" }, tree.ChildRolesOfFirst("Table"));
        Assert.Equal(new[] { "TH", "TH" }, tree.ChildRolesOfFirst("TR"));
    }

    /// <summary>Ordinary prose is not a table.</summary>
    /// <remarks>
    /// <b>The false-positive control the alignment rule exists for.</b> Every line here has several
    /// shows — a styled word mid-sentence is the commonest reason a line has more than one — but
    /// the second show lands at a different X on every line, because it follows text of a different
    /// width. A detector reading only "several shows per line" would call this a table.
    /// </remarks>
    [Fact]
    public void ProseWithMidSentenceStyleChangesIsNotATable()
    {
        string content =
            "BT /F1 12 Tf 72 700 Td (The quick ) Tj 60 0 Td (brown) Tj ET\n" +
            "BT /F1 12 Tf 72 670 Td (fox jumped right over the ) Tj 140 0 Td (lazy) Tj ET\n" +
            "BT /F1 12 Tf 72 640 Td (dog and ) Tj 45 0 Td (kept) Tj ET\n";

        Tree tree = Tag(content, infer: true);

        Assert.Equal(0, tree.Count("Table"));
        Assert.True(tree.Count("P") > 0, "the prose was not tagged at all, so nothing was refused");
    }

    /// <summary>A cell drawn with two show operators breaks the run.</summary>
    /// <remarks>
    /// <b>A documented limit pinned so it stays a limit rather than becoming a surprise.</b> The
    /// second show of a cell sits at the first one's X plus the width of its text, so it lands
    /// somewhere different on every row and the column set stops matching. The refusal is the
    /// designed behaviour; if a later change makes these tables detectable, this test is the one
    /// that must be rewritten deliberately.
    /// </remarks>
    [Fact]
    public void ACellDrawnWithTwoShowOperatorsIsNotDetectedYet()
    {
        // Column 2's content is split in two, and the split lands at a different offset per row
        // exactly as a real one would.
        string content =
            "BT /F1 12 Tf 72 700 Td (Region) Tj 168 0 Td (Sa) Tj 14 0 Td (les) Tj ET\n" +
            "BT /F1 12 Tf 72 670 Td (North) Tj 168 0 Td (12) Tj 22 0 Td (00) Tj ET\n" +
            "BT /F1 12 Tf 72 640 Td (South) Tj 168 0 Td (9) Tj 9 0 Td (00) Tj ET\n";

        Tree tree = Tag(content, infer: true);

        Assert.Equal(0, tree.Count("Table"));
    }

    /// <summary>The header cells carry a <c>/Scope</c> a data cell can be associated with.</summary>
    /// <remarks>
    /// <b>A <c>/TH</c> with neither <c>/Scope</c> nor an <c>/ID</c> some cell references is a UA12
    /// finding in its own right</b> — a header nothing is associated with. Emitting the header
    /// without the scope would trade one finding for another, so the attribute is part of the
    /// claim, not decoration. It must be owner-qualified (§14.7.5): a bare <c>/Scope</c> on the
    /// element is read by nothing.
    /// </remarks>
    [Fact]
    public void EachHeaderCellCarriesAnOwnerQualifiedColumnScope()
    {
        byte[] pdf = TaggedBytes(RowPerTextObject(Grid, Columns), infer: true);
        var tree = new Tree(pdf);

        List<(string Owner, string Scope)> scopes = tree.TableAttributesOfEvery("TH");

        Assert.Equal(3, scopes.Count);
        Assert.All(scopes, s => Assert.Equal("Table", s.Owner));
        Assert.All(scopes, s => Assert.Equal("Column", s.Scope));

        // The data cells get none: /Scope says what a cell heads, and a data cell heads nothing.
        Assert.Empty(tree.TableAttributesOfEvery("TD"));
    }

    /// <summary>An inferred table is reported clean by this library's own table checks.</summary>
    /// <remarks>
    /// <b>The test the whole feature is measured by.</b> Every other assertion here says the tree
    /// has the shape this pass intended; this one says the shape is the one the standard wants,
    /// judged by the checker that would report it — raggedness, a row child that is not a cell, an
    /// unaddressable header. Remediation that manufactures a finding is worse than remediation that
    /// does nothing, and only running the checker can tell the two apart.
    /// </remarks>
    [Fact]
    public void AnInferredTablePassesThisLibrarysOwnTableChecks()
    {
        byte[] pdf = TaggedBytes(RowPerTextObject(Grid, Columns), infer: true);

        using var ms = new MemoryStream(pdf);
        AccessibilityAuditReport report = PdfAccessibility.Audit(ms);

        List<AccessibilityIssue> tableIssues = report.Issues
            .Where(i => string.Equals(i.Code, "UA12", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(tableIssues);
    }

    /// <summary>The result counts what it inferred.</summary>
    /// <remarks>
    /// The number a caller checks before trusting the option on their corpus. It is 0 without the
    /// option even on a page full of grids, which is the half that makes it a measurement rather
    /// than a constant.
    /// </remarks>
    [Fact]
    public void TheResultReportsHowManyTablesWereInferred()
    {
        Assert.Equal(1, Result(RowPerTextObject(Grid, Columns), infer: true).TablesInferred);
        Assert.Equal(0, Result(RowPerTextObject(Grid, Columns), infer: false).TablesInferred);
    }

    /// <summary>Two grids separated by a paragraph become two tables, not one.</summary>
    /// <remarks>
    /// The run is broken by the paragraph between them. Joining them would produce a table whose
    /// middle row is prose — a UA12 "row child that is not a cell" — and would claim a relationship
    /// between two grids that nothing on the page states.
    /// </remarks>
    [Fact]
    public void AParagraphBetweenTwoGridsSeparatesThem()
    {
        string content =
            Row(new[] { "Region", "Sales" }, Columns, 700) +
            Row(new[] { "North",  "1200"  }, Columns, 670) +
            "BT /F1 12 Tf 72 640 Td (An explanatory sentence between the two grids.) Tj ET\n" +
            Row(new[] { "Region", "Share" }, Columns, 610) +
            Row(new[] { "South",  "31%"   }, Columns, 580);

        Tree tree = Tag(content, infer: true);

        Assert.Equal(2, tree.Count("Table"));
        Assert.Equal(4, tree.Count("TR"));
        Assert.Equal(1, tree.Count("P"));
    }

    /// <summary>A table whose rows sit in the header and footer bands loses them as artifacts.</summary>
    /// <remarks>
    /// <b>Not a defect — a consequence, pinned because it is surprising.</b> The artifact bands are
    /// decided before the cell role is, and deliberately: a running head that happens to be
    /// two-column is still a running head, and demoting the band would put pagination into the
    /// structure tree. The rows that survive still form a table.
    /// </remarks>
    [Fact]
    public void RowsInsideThePaginationBandsStayArtifacts()
    {
        // 792 - 20 = 772 is inside the 50 pt header band; the other two rows are not.
        string content =
            Row(new[] { "Region", "Sales" }, Columns, 772) +
            Row(new[] { "North",  "1200"  }, Columns, 700) +
            Row(new[] { "South",  "900"   }, Columns, 670);

        Tree tree = Tag(content, infer: true);

        Assert.Equal(1, tree.Count("Table"));
        Assert.Equal(2, tree.Count("TR"));
    }

    /// <summary>The classifier is handed each cell's place in the grid.</summary>
    /// <remarks>
    /// <b>The escape hatch the "row 0 is the header" heuristic depends on.</b> Nothing the tagger
    /// records distinguishes a header row from a data row, so the guess has to be overridable by a
    /// caller who knows their corpus — and that is only writable if the row, the column and the
    /// column count reach the classifier. Exercised through the tagger's own seam because
    /// <c>AutoTagOptions</c> deliberately exposes no classifier; the facade's own answer is pinned
    /// by <see cref="AnAlignedGridBecomesATable"/>.
    /// </remarks>
    [Fact]
    public void TheClassifierIsHandedEachCellsPlaceInTheGrid()
    {
        var seen = new List<(int Row, int Column, int Columns, string Role)>();

        var options = new PageTagOptions
        {
            InferTables = true,
            PageHeight  = 792f,
            Classifier  = c =>
            {
                RunClassification answer = ContentStreamTagger.DefaultClassifier(c);
                if (c.IsTableCell)
                    seen.Add((c.TableRowIndex, c.TableColumnIndex, c.TableColumnCount, answer.Role));

                // The override itself: every cell is data, including the first row's.
                return c.IsTableCell
                    ? RunClassification.Content(StructureTypes.TableDataCell)
                    : answer;
            },
        };

        PageTagResult result = ContentStreamTagger.TagPage(
            Encoding.Latin1.GetBytes(RowPerTextObject(Grid, Columns)), options);

        Assert.Null(result.SkipReason);
        Assert.Equal(9, seen.Count);
        Assert.All(seen, s => Assert.Equal(3, s.Columns));

        // Row 0 spans the three columns, and is what the default classifier would have made headers.
        Assert.Equal(new[] { 0, 1, 2 }, seen.Where(s => s.Row == 0).Select(s => s.Column).ToArray());
        Assert.All(seen.Where(s => s.Row == 0), s => Assert.Equal("TH", s.Role));
        Assert.All(seen.Where(s => s.Row > 0),  s => Assert.Equal("TD", s.Role));

        // And the override held: nothing the tagger emitted is a header.
        Assert.DoesNotContain(result.Runs, r => r.Role == "TH");
        Assert.Equal(9, result.Runs.Count(r => r.Role == "TD"));
    }

    /// <summary>A content stream whose matrices overflow produces no table and no hang.</summary>
    /// <remarks>
    /// A <c>cm</c> that overflows a <c>float</c> makes every derived coordinate NaN, and every
    /// comparison against NaN is false in both directions — so a rule written one way round and the
    /// same rule written the other take opposite branches on it. The detector must reach the same
    /// answer either way, which for adversarial geometry is "no grid".
    /// </remarks>
    [Fact]
    public void AdversarialNonFiniteGeometryYieldsNoTable()
    {
        // 1e39 in the only notation a content-stream number may use — plain digits, because
        // ContentStreamScanner.ReadNumber accepts no exponent. Written as "1e39" the operator gets
        // tokens it cannot use, the cm is ignored, and the fixture quietly tests nothing: it
        // produced a perfectly ordinary three-row table until this line was corrected.
        string huge = "1" + new string('0', 39);

        string content =
            huge + " 0 0 " + huge + " 0 0 cm\n" +
            Row(new[] { "Region", "Sales" }, Columns, 700) +
            Row(new[] { "North",  "1200"  }, Columns, 670);

        Tree tree = Tag(content, infer: true);

        Assert.Equal(0, tree.Count("Table"));
    }

    // ================================================================
    //  Fixtures
    // ================================================================

    /// <summary>One text object per row: a <c>Td</c> and a <c>Tj</c> per cell.</summary>
    private static string RowPerTextObject(string[][] rows, float[] columns)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < rows.Length; r++) sb.Append(Row(rows[r], columns, 700 - r * 30));
        return sb.ToString();
    }

    /// <summary>One text object per cell — the shape a word processor emits.</summary>
    private static string CellPerTextObject(string[][] rows, float[] columns)
    {
        var sb = new StringBuilder();
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
                sb.Append("BT /F1 12 Tf ")
                  .Append(F(columns[c])).Append(' ').Append(F(700 - r * 30))
                  .Append(" Td (").Append(Escape(rows[r][c])).Append(") Tj ET\n");
        return sb.ToString();
    }

    /// <summary>
    /// One row as a single text object. Cells are placed with a <c>Td</c> relative to the previous
    /// one, so the fixture's X positions are the ones a real producer would write rather than
    /// absolute values the tagger could not have derived.
    /// </summary>
    private static string Row(string[] cells, float[] columns, float y)
    {
        var sb = new StringBuilder("BT /F1 12 Tf ");
        float at = 0f;
        for (int c = 0; c < cells.Length; c++)
        {
            float dx = columns[c] - at;
            float dy = c == 0 ? y : 0f;
            at = columns[c];
            sb.Append(F(dx)).Append(' ').Append(F(dy)).Append(" Td (")
              .Append(Escape(cells[c])).Append(") Tj ");
        }
        return sb.Append("ET\n").ToString();
    }

    private static string F(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static Tree Tag(string content, bool infer) => new Tree(TaggedBytes(content, infer));

    private static AutoTagResult Result(string content, bool infer)
    {
        using var input = new MemoryStream(Page(content));
        using var output = new MemoryStream();
        return PdfAutoTagger.AutoTag(input, output, new AutoTagOptions { InferTables = infer });
    }

    private static byte[] TaggedBytes(string content, bool infer)
    {
        using var input = new MemoryStream(Page(content));
        using var output = new MemoryStream();

        AutoTagResult result = PdfAutoTagger.AutoTag(
            input, output, new AutoTagOptions { InferTables = infer });

        Assert.True(result.ContentMarked,
            "the tagger marked nothing, so the tree under test was never built"
            + (result.SkippedPageReasons.Count > 0
                   ? ": " + result.SkippedPageReasons[0]
                   : string.Empty));

        return output.ToArray();
    }

    /// <summary>One page carrying the given content stream verbatim.</summary>
    private static byte[] Page(string content)
    {
        byte[] bytes = Encoding.Latin1.GetBytes(content);

        var sb = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();

        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(offsets.Count).Append(" 0 obj\n").Append(body).Append("\nendobj\n");
        }

        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R"
            + " /Resources << /Font << /F1 5 0 R >> >> >>");
        Obj("<< /Length " + bytes.Length.ToString(CultureInfo.InvariantCulture)
            + " >>\nstream\n" + Encoding.Latin1.GetString(bytes) + "\nendstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(offsets.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offset);

        sb.Append("trailer\n<< /Size ").Append(offsets.Count + 1)
          .Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    private static string Escape(string text)
        => text.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");

    /// <summary>The structure tree of a tagged document, read back.</summary>
    private sealed class Tree
    {
        private readonly PdfDocument _doc;
        private readonly List<string> _roles = new List<string>();
        private readonly Dictionary<string, List<string>> _firstChildren =
            new Dictionary<string, List<string>>(StringComparer.Ordinal);
        private readonly List<(string Role, PdfDictionary Dict)> _elements =
            new List<(string, PdfDictionary)>();

        internal List<string> RootRoles { get; } = new List<string>();

        internal Tree(byte[] pdf)
        {
            var ms = new MemoryStream(pdf);
            _doc = PdfDocument.Open(ms);

            var root = _doc.ResolveObject(_doc.Catalog.Dictionary.Get(PdfName.StructTreeRoot))
                       as PdfDictionary;
            if (root == null) return;

            void Visit(PdfObject node, int depth)
            {
                if (depth > 40) return;
                if (_doc.ResolveObject(node) is not PdfDictionary dict) return;

                string role = RoleOf(node);
                List<PdfObject> kids = KidsOf(dict);

                if (role != null)
                {
                    _roles.Add(role);
                    _elements.Add((role, dict));
                    if (!_firstChildren.ContainsKey(role))
                        _firstChildren[role] = kids.Select(RoleOf).Where(r => r != null).ToList();
                }

                foreach (PdfObject kid in kids) Visit(kid, depth + 1);
            }

            foreach (PdfObject documentElement in KidsOf(root))
            {
                if (_doc.ResolveObject(documentElement) is not PdfDictionary document) continue;

                foreach (PdfObject kid in KidsOf(document))
                {
                    string role = RoleOf(kid);
                    if (role != null) RootRoles.Add(role);
                    Visit(kid, 0);
                }
            }
        }

        private string RoleOf(PdfObject node)
            => _doc.ResolveObject(node) is PdfDictionary d
               && _doc.ResolveObject(d.Get(PdfName.S)) is PdfName s ? s.Value : null;

        private List<PdfObject> KidsOf(PdfDictionary dict)
        {
            PdfObject k = _doc.ResolveObject(dict.Get(PdfName.K));
            if (k is PdfArray array) return array.ToList();
            return k == null ? new List<PdfObject>() : new List<PdfObject> { k };
        }

        internal int Count(string role) => _roles.Count(r => r == role);

        internal List<string> ChildRolesOfFirst(string role)
        {
            Assert.True(_firstChildren.ContainsKey(role), $"no /{role} element in the tree");
            return _firstChildren[role];
        }

        /// <summary>Every MCID reachable from every element of one role.</summary>
        internal List<int> McidsOfEvery(string role)
        {
            var mcids = new List<int>();
            foreach ((string r, PdfDictionary dict) in _elements)
            {
                if (r != role) continue;
                foreach (PdfObject kid in KidsOf(dict))
                    if (_doc.ResolveObject(kid) is PdfNumber n) mcids.Add((int)n.DoubleValue);
            }
            return mcids;
        }

        /// <summary>The <c>/A</c> owner and <c>/Scope</c> of every element of one role that has one.</summary>
        internal List<(string Owner, string Scope)> TableAttributesOfEvery(string role)
        {
            var found = new List<(string, string)>();
            foreach ((string r, PdfDictionary dict) in _elements)
            {
                if (r != role) continue;
                if (_doc.ResolveObject(dict.Get(PdfName.A)) is not PdfDictionary attributes) continue;

                string owner = (_doc.ResolveObject(attributes.Get(PdfName.O)) as PdfName)?.Value;
                string scope = (_doc.ResolveObject(attributes.Get(PdfName.Scope)) as PdfName)?.Value;
                found.Add((owner, scope));
            }
            return found;
        }
    }
}
