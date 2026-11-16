// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// What <see cref="DocxExporter"/> writes for the model properties no other DOCX test reads back:
/// run spacing and scale, non-default sizes, right-to-left sections and contents lists, merged and
/// spanned cells, empty cells, list indents without a definition, the final section's properties,
/// huge page sizes and an empty background image.
/// </summary>
public sealed class DocxExportBoundaryTests
{
    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    // ------------------------------------------------------------------ runs

    /// <summary>
    /// A run's character spacing (points to twips), horizontal scale (percent) and a non-default
    /// size (half-points) are written; a run at the defaults writes none of them.
    /// </summary>
    [Fact]
    public void RunSpacingScaleAndSize_AreWritten_AndDefaultsAreNot()
    {
        var styled = new TextRun { Text = "styled", CharSpacing = 1.5, HorizontalScale = 150, FontSize = 14 };
        var plain = new TextRun { Text = "plain" };
        XDocument xml = DocxXml(Doc(Para(styled), Para(plain)));

        XElement styledProps = RunProps(xml, "styled");
        Assert.Equal("30", styledProps.Element(W + "spacing")!.Attribute(W + "val")!.Value);
        Assert.Equal("150", styledProps.Element(W + "w")!.Attribute(W + "val")!.Value);
        Assert.Equal("28", styledProps.Element(W + "sz")!.Attribute(W + "val")!.Value);

        XElement plainProps = RunProps(xml, "plain");
        Assert.Null(plainProps.Element(W + "spacing"));
        Assert.Null(plainProps.Element(W + "w"));
        Assert.Null(plainProps.Element(W + "sz"));
    }

    // ------------------------------------------------------------------ right to left

    /// <summary>
    /// A right-to-left section says so in its section properties, and its contents list's entries
    /// are right-to-left paragraphs; a left-to-right section's are not.
    /// </summary>
    [Theory]
    [InlineData(TextDirection.RightToLeft, true)]
    [InlineData(TextDirection.LeftToRight, false)]
    public void RightToLeftSection_MarksItsSectionAndItsContentsList(TextDirection direction, bool rtl)
    {
        var heading = Para(new TextRun { Text = "Chapter" });
        heading.Role = "H1";
        Document doc = Doc(new TocBlock { Title = null }, heading);
        doc.Sections[0].Direction = direction;

        XDocument xml = DocxXml(doc);

        XElement sectPr = xml.Root!.Element(W + "body")!.Element(W + "sectPr")!;
        Assert.Equal(rtl, sectPr.Element(W + "bidi") != null);

        XElement tocEntry = xml.Descendants(W + "p").Single(p =>
            (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") == "TOC1");
        Assert.Equal(rtl, tocEntry.Element(W + "pPr")!.Element(W + "bidi") != null);
    }

    /// <summary>
    /// A contents list with no title has no heading paragraph; with a title it has one carrying it.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Contents")]
    public void ContentsListTitle_IsWrittenOnlyWhenThereIsOne(string? title)
    {
        var heading = Para(new TextRun { Text = "Chapter" });
        heading.Role = "H1";
        XDocument xml = DocxXml(Doc(new TocBlock { Title = title }, heading));

        var headings = xml.Descendants(W + "p").Where(p =>
            (string?)p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val") == "TOCHeading").ToList();

        if (string.IsNullOrEmpty(title))
            Assert.Empty(headings);
        else
            Assert.Equal(title, Assert.Single(headings).Value);
    }

    // ------------------------------------------------------------------ tables

    /// <summary>
    /// A cell spanning two columns says so and a single-column cell writes no span; a continuing
    /// vertical merge is written as one; an empty cell still holds the one paragraph OOXML
    /// requires; and the table's six borders are single half-point lines.
    /// </summary>
    [Fact]
    public void TableCells_SpanMergeAndEmptiness_AreWritten()
    {
        var table = new Table();
        var top = new TableRow();
        top.Cells.Add(Cell("wide", gridSpan: 2, merge: VerticalMerge.Restart));
        top.Cells.Add(Cell("single"));
        var bottom = new TableRow();
        bottom.Cells.Add(new TableCell { GridSpan = 2, VMerge = VerticalMerge.Continue });
        bottom.Cells.Add(Cell("last"));
        table.Rows.Add(top);
        table.Rows.Add(bottom);

        XDocument xml = DocxXml(Doc(table));
        var cells = xml.Descendants(W + "tc").ToList();

        Assert.Equal("2", cells[0].Element(W + "tcPr")!.Element(W + "gridSpan")!.Attribute(W + "val")!.Value);
        Assert.Null(cells[1].Element(W + "tcPr")?.Element(W + "gridSpan"));
        Assert.Equal("continue", cells[2].Element(W + "tcPr")!.Element(W + "vMerge")!.Attribute(W + "val")!.Value);
        Assert.Single(cells[2].Elements(W + "p"));

        XElement borders = xml.Descendants(W + "tblBorders").Single();
        foreach (string edge in new[] { "top", "bottom", "left", "right", "insideH", "insideV" })
        {
            XElement border = borders.Element(W + edge)!;
            Assert.Equal("single", border.Attribute(W + "val")!.Value);
            Assert.Equal("4", border.Attribute(W + "sz")!.Value);
        }
    }

    // ------------------------------------------------------------------ lists without a definition

    /// <summary>
    /// A loose list item (no list around it, so no numbering definition) is indented 18 pt per
    /// level — 360 twips a level — and a level-0 item carries no indent at all.
    /// </summary>
    [Theory]
    [InlineData(0, null)]
    [InlineData(2, "720")]
    public void LooseListItem_IsIndentedEighteenPointsPerLevel(int level, string? expectedLeft)
    {
        XDocument xml = DocxXml(Doc(new ListItem { Text = "item", Level = level }));

        XElement? ind = xml.Descendants(W + "p").Single().Element(W + "pPr")?.Element(W + "ind");
        Assert.Equal(expectedLeft, (string?)ind?.Attribute(W + "left"));
    }

    // ------------------------------------------------------------------ section properties

    /// <summary>
    /// The final section's properties are the body's last child (WordprocessingML allows that
    /// position to no other section); an earlier section's ride in its last paragraph.
    /// </summary>
    [Fact]
    public void FinalSectionProperties_AreTheBodysLastChild()
    {
        var doc = new Document();
        foreach (string text in new[] { "first", "second" })
        {
            var section = new Section();
            section.Children.Add(Para(new TextRun { Text = text }));
            doc.Sections.Add(section);
        }

        XElement body = DocxXml(doc).Root!.Element(W + "body")!;

        Assert.Equal(W + "sectPr", body.Elements().Last().Name);
        Assert.Single(body.Elements(W + "sectPr"));
        Assert.Single(body.Elements(W + "p").Where(p => p.Element(W + "pPr")?.Element(W + "sectPr") != null));
    }

    /// <summary>
    /// A page width past what a twip count can hold is clamped to the largest one, not wrapped
    /// into a small number.
    /// </summary>
    [Fact]
    public void HugePageWidth_IsClampedNotWrapped()
    {
        Document doc = Doc(Para(new TextRun { Text = "x" }));
        doc.Sections[0].PageWidthPt = 1e12;

        XElement pgSz = DocxXml(doc).Descendants(W + "pgSz").Single();

        Assert.Equal(uint.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
            pgSz.Attribute(W + "w")!.Value);
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>
    /// A paragraph background image with no bytes is no image, so nothing is reported as dropped;
    /// one with bytes is reported.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(4, true)]
    public void EmptyBackgroundImage_IsNotReportedAsDropped(int bytes, bool reported)
    {
        var para = Para(new TextRun { Text = "x" });
        para.BackgroundImage = new BackgroundImage { Data = new byte[bytes] };

        var seen = CaptureOnThisThread(() => DocxXml(Doc(para)));

        Assert.Equal(reported, seen.Any(d => d.Detail.Contains("background image")));
    }

    // ------------------------------------------------------------------ fixtures

    private static Paragraph Para(TextRun run)
    {
        var p = new Paragraph();
        p.Runs.Add(run);
        return p;
    }

    private static TableCell Cell(string text, int gridSpan = 1, VerticalMerge merge = VerticalMerge.None)
    {
        var cell = new TableCell { GridSpan = gridSpan, VMerge = merge };
        cell.Paragraphs.Add(Para(new TextRun { Text = text }));
        return cell;
    }

    private static XElement RunProps(XDocument xml, string text)
        => xml.Descendants(W + "r").Single(r => r.Element(W + "t")?.Value == text).Element(W + "rPr")!;

    private static Document Doc(params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        foreach (IDocElement child in children) section.Children.Add(child);
        doc.Sections.Add(section);
        return doc;
    }

    private static XDocument DocxXml(Document doc)
    {
        using var ms = new MemoryStream();
        new DocxExporter().Export(doc, ms);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using Stream part = zip.GetEntry("word/document.xml")!.Open();
        return XDocument.Load(part);
    }

    private static List<BuilderDiagnostic> CaptureOnThisThread(Action body)
    {
        var seen = new List<BuilderDiagnostic>();
        int mine = Environment.CurrentManagedThreadId;
        void Capture(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId != mine) return;
            lock (seen) seen.Add(d);
        }

        BuilderDiagnostics.OnEvent += Capture;
        try { body(); }
        finally { BuilderDiagnostics.OnEvent -= Capture; }
        return seen;
    }
}
