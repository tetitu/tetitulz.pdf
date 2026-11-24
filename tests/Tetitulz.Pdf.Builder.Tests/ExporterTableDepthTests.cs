// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// A table cell may hold its own table — a self-reference, or a long chain of distinct tables a caller
/// built. Walking the nesting by C# recursion with no bound would recurse until the thread stack
/// overflowed — which no handler can catch and which takes the host process down.
/// (<c>ModelMeasurements.Validate</c> runs first but is itself depth capped, so it tolerates the cycle
/// rather than catching it.) The DOCX, HTML and ODT exporters share the PDF exporter's ceiling,
/// <see cref="Tetitulz.Pdf.Core.Constants.PdfLimits.MaxTableNestingDepth"/>, and report the truncation
/// instead of overflowing.
/// </summary>
public class ExporterTableDepthTests
{
    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    /// <summary>A document whose one table's single cell contains that same table — an infinite nesting.</summary>
    private static Document CyclicTableDocument()
    {
        var t = new Table();
        var row = new TableRow();
        var cell = new TableCell();
        cell.Blocks.Add(t);            // the cell holds its own table
        row.Cells.Add(cell);
        t.Rows.Add(row);
        var doc = new Document();
        var section = new Section();
        section.Children.Add(t);
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>
    /// Runs an export, collecting the diagnostics it reports on this thread. <see cref="BuilderDiagnostics.OnEvent"/>
    /// fires synchronously on the reporting thread, so the thread-id filter keeps a parallel test's reports out.
    /// </summary>
    private static (byte[] bytes, List<BuilderDiagnostic> reports) Run(IDocumentExporter exporter, Document doc)
    {
        var reports = new List<BuilderDiagnostic>();
        int tid = Environment.CurrentManagedThreadId;
        void Handler(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId == tid) lock (reports) reports.Add(d);
        }
        BuilderDiagnostics.OnEvent += Handler;
        try
        {
            using var ms = new MemoryStream();
            exporter.Export(doc, ms);   // must RETURN — pre-fix this overflowed the stack uncatchably
            return (ms.ToArray(), reports);
        }
        finally { BuilderDiagnostics.OnEvent -= Handler; }
    }

    private static IDocumentExporter ExporterFor(string format) => format switch
    {
        "docx" => new DocxExporter(),
        "html" => new HtmlExporter(),
        _ => new OdtExporter(),
    };

    private static bool TruncationReported(IEnumerable<BuilderDiagnostic> reports) =>
        System.Linq.Enumerable.Any(reports, d => d.Kind == BuilderDiagnosticKind.ContentApproximated
            && d.Detail.Contains("nested more than", StringComparison.Ordinal));

    [Theory]
    [InlineData("docx")]
    [InlineData("html")]
    [InlineData("odt")]
    public void ACyclicNestedTable_IsDepthCapped_NotOverflowed(string format)
    {
        var (bytes, reports) = Run(ExporterFor(format), CyclicTableDocument());

        Assert.NotEmpty(bytes);
        Assert.True(TruncationReported(reports), "the depth cap did not report truncation");
    }

    /// <summary>The control: an ordinary two-level nested table exports its inner text whole, uncapped.</summary>
    [Fact]
    public void AnOrdinaryNestedTable_IsExportedWhole()
    {
        var inner = new Table();
        var ir = new TableRow();
        var ic = new TableCell();
        ic.Blocks.Add(Para("InnerCellText"));
        ir.Cells.Add(ic);
        inner.Rows.Add(ir);

        var outer = new Table();
        var orow = new TableRow();
        var ocell = new TableCell();
        ocell.Blocks.Add(inner);
        orow.Cells.Add(ocell);
        outer.Rows.Add(orow);

        var doc = new Document();
        var section = new Section();
        section.Children.Add(outer);
        doc.Sections.Add(section);

        var (bytes, reports) = Run(new HtmlExporter(), doc);
        string html = Encoding.UTF8.GetString(bytes);

        Assert.Contains("InnerCellText", html, StringComparison.Ordinal);  // the inner cell rendered
        Assert.True(CountOccurrences(html, "<table") >= 2, "the inner table was not written");
        Assert.False(TruncationReported(reports), "an ordinary nesting must not hit the depth cap");
    }

    private static int CountOccurrences(string s, string sub)
    {
        int n = 0, i = 0;
        while ((i = s.IndexOf(sub, i, StringComparison.Ordinal)) >= 0) { n++; i += sub.Length; }
        return n;
    }
}
