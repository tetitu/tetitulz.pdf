// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// The blocks keep decisions measure are budgeted (<see cref="PdfExporter.KeepMeasurePerBlock"/>): a box kept
/// together is measured whole where it opens and again inside every kept box around it, so boxes nested d deep
/// measured d²/2 blocks — half a second at the HTML mapper's 200 levels, repeated by every copy of the tree.
/// The budget's evidence is the count, <see cref="PdfExporter.LastKeepBlocksMeasured"/>, never a clock.
/// </summary>
public class KeepMeasureBudgetTests
{
    private static Paragraph P(string text) => new Paragraph { SpacingAfter = 0, Runs = { new TextRun { Text = text } } };

    /// <summary><paramref name="copies"/> trees of kept boxes nested <paramref name="depth"/> deep, a paragraph in each.</summary>
    private static Document Nested(int copies, int depth)
    {
        var section = new Section();
        for (int c = 0; c < copies; c++)
        {
            for (int i = 0; i < depth; i++) { section.Children.Add(new BoxStart { KeepTogether = true }); section.Children.Add(P("x" + i)); }
            for (int i = 0; i < depth; i++) section.Children.Add(new BoxEnd());
        }
        var doc = new Document();
        doc.Sections.Add(section);
        return doc;
    }

    private static List<BuilderDiagnostic> Export(Document doc)
    {
        var reports = new List<BuilderDiagnostic>();
        doc.ToPdf(new PdfExportOptions { OnDiagnostic = reports.Add });
        return reports;
    }

    /// <summary>
    /// Forty trees 100 deep would measure about 400 000 blocks for their keeps; the export measures no more than
    /// its budget, eight a block of its 12 000, and says it stopped. A hostile document.
    /// </summary>
    [Fact]
    public void DeeplyNestedKeptBoxes_AreMeasuredWithinTheBudget_AndTheCutIsReported()
    {
        Document doc = Nested(copies: 40, depth: 100);
        List<BuilderDiagnostic> reports = Export(doc);

        long budget = PdfExporter.KeepMeasureBudgetFor(doc);
        Assert.Equal(PdfExporter.KeepMeasurePerBlock * 12_000L, budget);
        Assert.InRange(PdfExporter.LastKeepBlocksMeasured, budget / 2, budget);
        Assert.Contains(reports, d => d.Kind == BuilderDiagnosticKind.ContentApproximated && d.Detail.Contains("keep-together"));
    }

    /// <summary>The control: kept boxes nested five deep, twenty times, are measured in full, and nothing is reported.</summary>
    [Fact]
    public void ShallowKeptBoxes_AreMeasuredInFull_AndNothingIsReported()
    {
        List<BuilderDiagnostic> reports = Export(Nested(copies: 20, depth: 5));

        // Each box's keep measures it start to end: 15 + 12 + 9 + 6 + 3 blocks a tree, 45; the budget is the minimum.
        Assert.Equal(20 * 45L, PdfExporter.LastKeepBlocksMeasured);
        Assert.DoesNotContain(reports, d => d.Detail.Contains("keep-together"));
    }

    /// <summary>
    /// A short document keeps the least budget: one tree 40 deep is 120 blocks, eight a block 960, but its keeps
    /// measure 3 × (40 + 39 + … + 1) = 2 460 blocks — in full, under the 20 000 minimum, and nothing is reported.
    /// </summary>
    [Fact]
    public void AShortDocumentsKeeps_AreMeasuredInFull_UnderTheLeastBudget()
    {
        Document doc = Nested(copies: 1, depth: 40);
        List<BuilderDiagnostic> reports = Export(doc);

        Assert.Equal(PdfExporter.MinKeepMeasureBlocks, PdfExporter.KeepMeasureBudgetFor(doc));
        Assert.Equal(2_460L, PdfExporter.LastKeepBlocksMeasured);
        Assert.DoesNotContain(reports, d => d.Detail.Contains("keep-together"));
    }

    /// <summary>A keep-with-next chain is measured once, at its start: a chain of 2 000 paragraphs, 2 000 blocks.</summary>
    [Fact]
    public void AKeepWithNextChain_IsMeasuredOnce()
    {
        var section = new Section();
        for (int i = 0; i < 2000; i++) { var p = P("p" + i); p.KeepWithNext = i < 1999; section.Children.Add(p); }
        var doc = new Document();
        doc.Sections.Add(section);

        List<BuilderDiagnostic> reports = Export(doc);
        Assert.Equal(2000L, PdfExporter.LastKeepBlocksMeasured);
        Assert.DoesNotContain(reports, d => d.Detail.Contains("keep-together"));
    }
}
