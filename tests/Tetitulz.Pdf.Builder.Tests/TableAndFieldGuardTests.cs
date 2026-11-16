// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Limits the PDF exporter tolerates, and the diagnostics that say so.
///
/// <para><b>Every case asserts the guard's own evidence</b> — the reported
/// <see cref="BuilderDiagnosticKind"/>, or the geometry in the finished content stream — never a
/// wall clock and never "the call returned". A dropped table subtree and an empty table look
/// identical in the output bytes; the diagnostic is the only thing that tells them apart, which is
/// exactly why its absence was the defect.</para>
/// </summary>
public class TableAndFieldGuardTests
{
    /// <summary>Diagnostics raised during <paramref name="export"/>, filtered to this thread's own.</summary>
    private static List<BuilderDiagnostic> Capture(Action export)
    {
        var seen = new List<BuilderDiagnostic>();
        int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
        void Handler(BuilderDiagnostic d)
        {
            // Filtered to the calling thread: the sink is process-wide, so an unfiltered handler
            // would collect a neighbouring test's report and make this one order-dependent.
            if (System.Threading.Thread.CurrentThread.ManagedThreadId == thread) seen.Add(d);
        }
        BuilderDiagnostics.OnEvent += Handler;
        try { export(); }
        finally { BuilderDiagnostics.OnEvent -= Handler; }
        return seen;
    }

    private static Document OneSection(params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        foreach (var c in children) section.Children.Add(c);
        doc.Sections.Add(section);
        return doc;
    }

    private static TableCell Cell(string text, int gridSpan = 1,
        VerticalMerge merge = VerticalMerge.None)
    {
        var cell = new TableCell { GridSpan = gridSpan, VMerge = merge };
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        cell.Blocks.Add(p);
        return cell;
    }

    // ================================================================ nesting depth

    /// <summary>
    /// Table nesting past the cap is <b>reported</b>, not silently discarded.
    /// </summary>
    /// <remarks>
    /// Both recursion entry points returned early with nothing written to the sink — and
    /// <c>Measure</c> returning 0 additionally sized the surrounding cell as if the dropped
    /// content did not exist. A batch converter had no way to flag the document for review.
    /// </remarks>
    [Fact]
    public void AdversarialNesting_PastTheDepthCap_IsReported()
    {
        var deepest = new Table();
        var innerRow = new TableRow();
        innerRow.Cells.Add(Cell("deep"));
        deepest.Rows.Add(innerRow);

        Table current = deepest;
        for (int i = 0; i < 40; i++)
        {
            var outer = new Table();
            var row = new TableRow();
            var cell = new TableCell();
            cell.Blocks.Add(current);
            row.Cells.Add(cell);
            outer.Rows.Add(row);
            current = outer;
        }

        var doc = OneSection(current);
        var seen = Capture(() => doc.ToPdf());

        var depth = seen.Where(d => d.Detail.Contains("table nesting past")).ToList();
        Assert.NotEmpty(depth);
        Assert.All(depth, d => Assert.Equal(BuilderDiagnosticKind.ContentApproximated, d.Kind));
        // One report per export: a model that is wide as well as deep must not flood the sink.
        Assert.Single(depth);
    }

    [Fact]
    public void ShallowNesting_ReportsNothing()
    {
        var inner = new Table();
        var innerRow = new TableRow();
        innerRow.Cells.Add(Cell("inner"));
        inner.Rows.Add(innerRow);

        var outer = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(inner);
        row.Cells.Add(cell);
        outer.Rows.Add(row);

        var seen = Capture(() => OneSection(outer).ToPdf());

        Assert.DoesNotContain(seen, d => d.Detail.Contains("table nesting past"));
    }

    // ================================================================ column count

    /// <summary>
    /// A file-controlled <see cref="TableCell.GridSpan"/> cannot size an allocation.
    /// </summary>
    /// <remarks>
    /// <c>ColumnCount</c> summed the spans in <see cref="int"/> with no cap and the total sized
    /// <c>new double[cols]</c> plus three O(cols) loops. The DOCX mapper copies the value straight
    /// out of the input, so <c>&lt;w:gridSpan w:val="200000000"/&gt;</c> asked for a 1.6 GB array —
    /// and a value near <see cref="int.MaxValue"/> wrapped the sum negative instead.
    /// </remarks>
    [Theory]
    [InlineData(200_000_000)]
    [InlineData(int.MaxValue)]
    public void AdversarialGridSpan_IsClampedAndReported(int span)
    {
        var table = new Table();
        var row = new TableRow();
        row.Cells.Add(Cell("a", gridSpan: span));
        row.Cells.Add(Cell("b", gridSpan: span));
        table.Rows.Add(row);

        byte[] pdf = null;
        var seen = Capture(() => pdf = OneSection(table).ToPdf());

        Assert.NotNull(pdf);
        Assert.Equal((byte)'%', pdf[0]);
        var clamp = seen.Where(d => d.Detail.Contains("columns")).ToList();
        Assert.NotEmpty(clamp);
        Assert.All(clamp, d => Assert.Equal(BuilderDiagnosticKind.ContentApproximated, d.Kind));
    }

    [Fact]
    public void OrdinaryGridSpan_IsNotClamped()
    {
        var table = new Table();
        var row = new TableRow();
        row.Cells.Add(Cell("a", gridSpan: 2));
        row.Cells.Add(Cell("b"));
        table.Rows.Add(row);

        var seen = Capture(() => OneSection(table).ToPdf());

        Assert.DoesNotContain(seen, d => d.Detail.Contains("columns"));
    }

    // ================================================================ merged cell pagination

    /// <summary>
    /// A vertically merged cell that spans a page break keeps its ink on the page.
    /// </summary>
    /// <remarks>
    /// <c>DrawRow</c> summed a <c>Restart</c> cell's height over its continuation rows with no
    /// knowledge of where pass B broke the page, so the cell's border and background were painted
    /// over the footer and off the sheet while the rows it should have continued into rendered at
    /// the top of the next page. Asserted as the geometric invariant — no filled rectangle below
    /// the bottom margin — plus the report that says the continuation was approximated.
    /// </remarks>
    [Fact]
    public void MergedCellSpanningAPageBreak_StaysAboveTheBottomMargin()
    {
        var doc = OneSection(TallTableWithMerge());
        byte[] pdf = null;
        var seen = Capture(() => pdf = doc.ToPdf());

        double bottom = doc.Sections[0].MarginBottom;
        var below = FilledRects(PdfContentProbe.DecodeStreams(pdf))
            .Where(r => r.Y < bottom - 1.0)
            .ToList();

        Assert.True(below.Count == 0,
            $"{below.Count} filled rectangle(s) reach below the bottom margin ({bottom} pt): " +
            string.Join(", ", below.Take(4).Select(r => $"y={r.Y:0.#} h={r.H:0.#}")));
        Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.ContentApproximated
                                   && d.Detail.Contains("vertically merged cell"));
    }

    [Fact]
    public void MergedCellThatFitsOnOnePage_IsNotClamped()
    {
        var table = new Table();
        for (int r = 0; r < 3; r++)
        {
            var row = new TableRow();
            row.Cells.Add(Cell(r == 0 ? "merged" : "",
                merge: r == 0 ? VerticalMerge.Restart : VerticalMerge.Continue));
            row.Cells.Add(Cell("row " + r.ToString(CultureInfo.InvariantCulture)));
            table.Rows.Add(row);
        }

        var seen = Capture(() => OneSection(table).ToPdf());

        Assert.DoesNotContain(seen, d => d.Detail.Contains("vertically merged cell"));
    }

    /// <summary>A table long enough to paginate, with a merge across the break.</summary>
    private static Table TallTableWithMerge()
    {
        var table = new Table();
        const int rows = 60;
        for (int r = 0; r < rows; r++)
        {
            var row = new TableRow();
            // A long merged run in the middle, guaranteed to straddle whatever row the break
            // lands on for a 60-row table on one page's worth of space.
            VerticalMerge merge =
                r == 15 ? VerticalMerge.Restart :
                (r > 15 && r < 45) ? VerticalMerge.Continue : VerticalMerge.None;
            row.Cells.Add(Cell(merge == VerticalMerge.Restart ? "span" : "", merge: merge));
            row.Cells.Add(Cell("row " + r.ToString(CultureInfo.InvariantCulture)));
            table.Rows.Add(row);
        }
        return table;
    }

    private static readonly Regex ReF = new Regex(
        @"(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s+re\s*\n?f",
        RegexOptions.Singleline);

    private static List<(double X, double Y, double W, double H)> FilledRects(string content)
        => ReF.Matches(content).Cast<Match>()
              .Select(m => (D(m.Groups[1].Value), D(m.Groups[2].Value),
                            D(m.Groups[3].Value), D(m.Groups[4].Value)))
              .ToList();

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    // ================================================================ the NUMPAGES field

    /// <summary>
    /// The <c>{{NUMPAGES}}</c> field is substituted even when the model glues it to non-Latin
    /// text.
    /// </summary>
    /// <remarks>
    /// The back-fill is a literal ASCII search over the finished content stream, so it can only
    /// reach a piece drawn through the simple-font path. Glued to Hebrew, the whole token needed a
    /// composite font and was written as glyph ids — the search never matched and the reader saw
    /// the literal <c>{{NUMPAGES}}</c> on the page. Splitting the field into its own token puts it
    /// back on the path the substitution can reach.
    /// </remarks>
    [Fact]
    public void NumPagesField_GluedToHebrew_IsStillSubstituted()
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "עמוד" + PdfExporter.NumPagesPlaceholder });
        var doc = OneSection(p);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build("עמוד 1234567890"),
        }));

        Assert.DoesNotContain("NUMPAGES", content);
        Assert.Contains(PdfContentProbe.DrawnTexts(content), d => d.Text == "1");
    }

    [Fact]
    public void NumPagesField_OnItsOwn_IsSubstituted()
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "of " + PdfExporter.NumPagesPlaceholder });

        string content = PdfContentProbe.DecodeStreams(OneSection(p).ToPdf());

        Assert.DoesNotContain("NUMPAGES", content);
        Assert.Contains(PdfContentProbe.DrawnTexts(content), d => d.Text == "1");
    }

    /// <summary>
    /// The substitution happens after layout, so the line was measured against the 12-character
    /// placeholder — content that IS drawn but not as described, which is what
    /// <see cref="BuilderDiagnosticKind.ContentApproximated"/> exists to say.
    /// </summary>
    [Fact]
    public void NumPagesField_ReportsThatItsLineWasMeasuredAgainstThePlaceholder()
    {
        var p = new Paragraph { Alignment = TextAlignment.Center };
        p.Runs.Add(new TextRun { Text = "Page " + PdfExporter.PagePlaceholder + " of " +
                                        PdfExporter.NumPagesPlaceholder + " — Confidential" });

        var seen = Capture(() => OneSection(p).ToPdf());

        Assert.Contains(seen, d => d.Kind == BuilderDiagnosticKind.ContentApproximated
                                   && d.Detail.Contains("NUMPAGES"));
    }

    [Fact]
    public void DocumentWithoutTheField_ReportsNothing()
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "Page 1 of 1" });

        Assert.Empty(Capture(() => OneSection(p).ToPdf()));
    }

    // ================================================================ alt text on a tagged page

    /// <summary>
    /// An undecodable image's alt text is drawn inside the marked-content sequence of the
    /// <c>/Figure</c> it stands in for.
    /// </summary>
    /// <remarks>
    /// It was drawn from the <c>else</c> branch, outside the scope that marks the successful path,
    /// so on a tagged document the substitute was unmarked ink — a PDF/UA violation in its own
    /// right — and the model's alt text never reached the structure tree.
    /// </remarks>
    [Fact]
    public void UndecodableImageOnATaggedPage_DrawsItsAltTextInsideAMarkedSequence()
    {
        var p = new Paragraph();
        p.Runs.Add(new ImageRun
        {
            Data = new byte[] { 1, 2, 3, 4 }, WidthPt = 120, HeightPt = 60, Alt = "sales chart",
        });
        var doc = OneSection(p);
        doc.Tagged = true;

        byte[] pdf = doc.ToPdf();
        string content = PdfContentProbe.DecodeStreams(pdf);

        int at = content.IndexOf("(sales chart)", StringComparison.Ordinal);
        Assert.True(at >= 0, "the alt text was never drawn");
        Assert.True(IsInsideMarkedContent(content, at),
            "the alt text must be inside a BDC…EMC sequence on a tagged page");

        // …and the /Figure carrying it reaches the structure tree.
        Assert.Contains("/Figure", PdfContentProbe.Raw(pdf));
        Assert.Contains("sales chart", PdfContentProbe.Raw(pdf));
    }

    /// <summary>Whether <paramref name="index"/> falls inside an open BDC…EMC pair.</summary>
    private static bool IsInsideMarkedContent(string content, int index)
    {
        int depth = 0;
        foreach (Match m in Regex.Matches(content.Substring(0, index), @"\bBDC\b|\bBMC\b|\bEMC\b"))
            depth += m.Value == "EMC" ? -1 : 1;
        return depth > 0;
    }
}
