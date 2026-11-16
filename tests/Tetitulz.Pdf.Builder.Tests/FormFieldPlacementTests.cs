// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="PdfExporter.PlacedFormFields"/>: where the exporter drew a run that stands for a form
/// field.
/// </summary>
/// <remarks>
/// The placeholder in every fixture is ten no-break spaces in 10&#160;pt Courier, whose every glyph
/// advances 0.6&#160;em — so its width is 60&#160;pt by arithmetic, not by asking the exporter.
/// </remarks>
public class FormFieldPlacementTests
{
    private const int PlaceholderChars = 10;
    private const double FontSizePt = 10;
    private const double PlaceholderWidthPt = PlaceholderChars * 0.6 * FontSizePt;

    private static TextRun FieldRun(string name) => new()
    {
        Text = new string(' ', PlaceholderChars),
        FontName = StandardFonts.Courier,
        FontSize = FontSizePt,
        Highlight = "#EFEFEF",
        FormField = new FormFieldSpec { Name = name },
    };

    private static Document WithParagraphs(params Paragraph[] paragraphs)
    {
        var doc = new Document();
        var section = new Section();
        foreach (var p in paragraphs) section.Children.Add(p);
        doc.Sections.Add(section);
        return doc;
    }

    private static Paragraph Para(params IInlineElement[] runs)
    {
        var p = new Paragraph();
        foreach (var r in runs) p.Runs.Add(r);
        return p;
    }

    private static (PdfExporter Exporter, byte[] Pdf) Export(Document doc, PdfExportOptions? options = null)
    {
        var exporter = new PdfExporter(options);
        using var ms = new MemoryStream();
        exporter.Export(doc, ms);
        return (exporter, ms.ToArray());
    }

    /// <summary>
    /// The placement is the placeholder's shaded box — the rectangle the highlight pass filled —
    /// so a widget laid over it covers exactly what the page shows as the field.
    /// </summary>
    [Fact]
    public void AFieldRunIsPlacedOverExactlyItsShadedBox()
    {
        var run = FieldRun("name");
        var (exporter, pdf) = Export(WithParagraphs(Para(new TextRun { Text = "Name: " }, run)));

        PlacedFormField placed = Assert.Single(exporter.PlacedFormFields);
        Assert.Same(run.FormField, placed.Field);
        Assert.Equal(0, placed.PageIndex);
        Assert.Equal(PlaceholderWidthPt, placed.Width, 3);
        // The glyph box is the face's line metrics (batch 80): Courier's 0.833 above and 0.300 below.
        Assert.Equal(FontSizePt * (0.833 + 0.300), placed.Height, 3);
        Assert.Equal(FontSizePt, placed.FontSize, 3);

        // The page's own highlight rectangle, read back out of the content stream.
        var rects = Regex.Matches(PdfContentProbe.DecodeStreams(pdf),
                                  @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re")
                         .Select(m => Enumerable.Range(1, 4)
                             .Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray())
                         .ToList();
        Assert.Contains(rects, r => Math.Abs(r[0] - placed.X) < 0.01 && Math.Abs(r[1] - placed.Y) < 0.01
                                 && Math.Abs(r[2] - placed.Width) < 0.01 && Math.Abs(r[3] - placed.Height) < 0.01);
    }

    /// <summary>The control: a run that stands for no field is not placed.</summary>
    [Fact]
    public void ARunWithoutAFieldIsNotPlaced()
    {
        var (exporter, _) = Export(WithParagraphs(Para(new TextRun { Text = "Name: ", Highlight = "#EFEFEF" })));

        Assert.Empty(exporter.PlacedFormFields);
    }

    /// <summary>
    /// The rectangle is in <b>default</b> user space, where an annotation's <c>/Rect</c> lives:
    /// the export's scale is applied to it, as the exporter applies it to its own links.
    /// </summary>
    [Fact]
    public void TheExportScaleIsAppliedToThePlacement()
    {
        var (unscaled, _) = Export(WithParagraphs(Para(FieldRun("name"))));
        var (scaled, _) = Export(WithParagraphs(Para(FieldRun("name"))), new PdfExportOptions { Scale = 0.5 });

        PlacedFormField a = Assert.Single(unscaled.PlacedFormFields), b = Assert.Single(scaled.PlacedFormFields);
        Assert.Equal(a.Width * 0.5, b.Width, 3);
        Assert.Equal(a.X * 0.5, b.X, 3);
        Assert.Equal(a.FontSize * 0.5, b.FontSize, 3);
    }

    [Fact]
    public void AFieldAfterAPageBreakIsPlacedOnTheSecondPage()
    {
        var (exporter, _) = Export(WithParagraphs(
            Para(new TextRun { Text = "first page" }, new PageBreak()),
            Para(FieldRun("later"))));

        Assert.Equal(1, Assert.Single(exporter.PlacedFormFields).PageIndex);
    }

    /// <summary>
    /// A footnote's mark borrows the formatting of the run before it. When that run stands for a
    /// field, the mark must not stand for it too.
    /// </summary>
    /// <remarks>
    /// <b>Right-to-left on purpose.</b> Placement is first-wins per field, and in a left-to-right
    /// line the field precedes its mark, so a mark that had inherited the field would be skipped and
    /// nothing could see it. In a right-to-left line the mark comes <i>first</i> in display order,
    /// and an inherited field would be placed over the mark's small box instead of the
    /// placeholder's — which the width then shows.
    /// </remarks>
    [Fact]
    public void AFootnoteMarkDoesNotInheritTheFieldOfTheRunItBorrowsFrom()
    {
        var note = new Footnote();
        note.Runs.Add(new TextRun { Text = "the note" });
        var p = Para(FieldRun("name"), note);
        p.Direction = TextDirection.RightToLeft;
        var (exporter, _) = Export(WithParagraphs(p));

        PlacedFormField placed = Assert.Single(exporter.PlacedFormFields);
        Assert.Equal(PlaceholderWidthPt, placed.Width, 3);
    }

    /// <summary>
    /// A tab leader borrows the formatting of the run before it, like a footnote mark, and must
    /// not borrow its field either. Right-to-left for the same reason as the footnote test: the
    /// leader then comes first in display order, so an inherited field would be placed over the
    /// dots instead of over the placeholder.
    /// </summary>
    [Fact]
    public void ATabLeaderDoesNotInheritTheFieldOfTheRunItBorrowsFrom()
    {
        var p = Para(FieldRun("name"), new TabRun(), new TextRun { Text = "x" });
        p.Direction = TextDirection.RightToLeft;
        p.Tabs.Add(new TabStop { PosPt = 200, Leader = TabLeader.Dot });
        var (exporter, _) = Export(WithParagraphs(p));

        PlacedFormField placed = Assert.Single(exporter.PlacedFormFields);
        Assert.Equal(PlaceholderWidthPt, placed.Width, 3);
    }

    /// <summary>
    /// Each export replaces the list: an exporter reused for a second document must not report
    /// the first one's fields.
    /// </summary>
    [Fact]
    public void ASecondExportReportsOnlyItsOwnFields()
    {
        var exporter = new PdfExporter();
        using (var ms = new MemoryStream()) exporter.Export(WithParagraphs(Para(FieldRun("first"))), ms);
        using (var ms = new MemoryStream()) exporter.Export(WithParagraphs(Para(FieldRun("second"))), ms);

        Assert.Equal("second", Assert.Single(exporter.PlacedFormFields).Field.Name);
    }
}
