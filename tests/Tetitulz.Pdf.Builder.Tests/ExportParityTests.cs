// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Rules that must hold the same way in all four exporters.
///
/// <para><b>Why a suite of its own.</b> One <see cref="Document"/> goes out as PDF, HTML, DOCX and
/// ODT, so a rule wired into one of them is not a partly-delivered feature — it is the same
/// document rendering differently in four files, which no per-exporter test can see. Three rules
/// had drifted exactly that way: <see cref="TextDirection.Auto"/>, the two
/// <see cref="ListItem"/> overrides, and <see cref="TextAlignment.Start"/>/<see cref="TextAlignment.End"/>.</para>
///
/// <para>Assertions read the parsed output — the DOCX part as XML, the HTML as XML, the PDF as its
/// decoded content stream — never a substring of a byte array.</para>
/// </summary>
public class ExportParityTests
{
    private const string Hebrew = "שלום עולם";

    // ---------------------------------------------------------------- fixtures

    private static Document Doc(params IDocElement[] children)
    {
        var doc = new Document();
        var section = new Section();
        foreach (var c in children) section.Children.Add(c);
        doc.Sections.Add(section);
        return doc;
    }

    private static Paragraph Para(string text, TextAlignment align = TextAlignment.Start,
        TextDirection? dir = null)
    {
        var p = new Paragraph { Alignment = align };
        if (dir.HasValue) p.Direction = dir.Value;
        p.Runs.Add(new TextRun { Text = text });
        return p;
    }

    /// <summary>The DOCX's <c>word/document.xml</c>, parsed.</summary>
    private static XDocument DocxXml(Document doc)
    {
        using var ms = new MemoryStream();
        doc.SaveAsDocx(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml");
        Assert.NotNull(entry);
        using var s = entry!.Open();
        return XDocument.Load(s);
    }

    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static string Html(Document doc) => Encoding.UTF8.GetString(doc.ToHtml());

    private static string Odt(Document doc)
    {
        using var ms = new MemoryStream();
        doc.SaveAsOdt(ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("content.xml");
        Assert.NotNull(entry);
        using var s = entry!.Open();
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    // ================================================================ alignment

    /// <summary>
    /// The default alignment writes no <c>w:jc</c> at all.
    /// </summary>
    /// <remarks>
    /// The switch predated <see cref="TextAlignment.Start"/> and fell through to
    /// <c>_ =&gt; Left</c>; because Start is the model's default, the old <c>!= Left</c> guard
    /// stopped suppressing anything, so EVERY paragraph got an explicit left justification that
    /// overrides the right alignment <c>&lt;w:bidi/&gt;</c> gives an RTL paragraph.
    /// </remarks>
    [Fact]
    public void Docx_DefaultAlignment_WritesNoJustification()
    {
        var xml = DocxXml(Doc(Para("hello")));

        Assert.Empty(xml.Descendants(W + "jc"));
    }

    [Fact]
    public void Docx_StartAlignmentUnderRtl_WritesNoJustification()
    {
        var xml = DocxXml(Doc(Para(Hebrew, TextAlignment.Start, TextDirection.RightToLeft)));

        Assert.Empty(xml.Descendants(W + "jc"));
        Assert.NotEmpty(xml.Descendants(W + "bidi"));    // the direction still carries the alignment
    }

    /// <summary><c>End</c> is the trailing edge — right under LTR, left under RTL.</summary>
    [Theory]
    [InlineData(false, "right")]
    [InlineData(true, "left")]
    public void Docx_EndAlignment_ResolvesAgainstTheDirection(bool rtl, string expected)
    {
        var xml = DocxXml(Doc(Para("total", TextAlignment.End,
            rtl ? TextDirection.RightToLeft : TextDirection.LeftToRight)));

        var jc = Assert.Single(xml.Descendants(W + "jc"));
        Assert.Equal(expected, jc.Attribute(W + "val")!.Value);
    }

    /// <summary>An explicit physical <c>Left</c> must survive inside an RTL paragraph.</summary>
    [Fact]
    public void Docx_ExplicitLeftUnderRtl_IsWritten()
    {
        var xml = DocxXml(Doc(Para(Hebrew, TextAlignment.Left, TextDirection.RightToLeft)));

        var jc = Assert.Single(xml.Descendants(W + "jc"));
        Assert.Equal("left", jc.Attribute(W + "val")!.Value);
    }

    /// <summary>
    /// HTML emitted CSS only for Center/Right/Justify, so <c>End</c> was always wrong and an
    /// explicit <c>Left</c> was lost inside <c>dir="rtl"</c> — the exact distinction
    /// <see cref="TextAlignment.Start"/> was added to preserve.
    /// </summary>
    [Fact]
    public void Html_EndAlignmentUnderLtr_IsRightAligned()
        => Assert.Contains("text-align:right;", ParagraphStyle(Html(Doc(Para("t", TextAlignment.End)))));

    [Fact]
    public void Html_ExplicitLeftUnderRtl_IsLeftAligned()
        => Assert.Contains("text-align:left;", ParagraphStyle(
            Html(Doc(Para(Hebrew, TextAlignment.Left, TextDirection.RightToLeft)))));

    [Fact]
    public void Html_DefaultAlignment_EmitsNoTextAlign()
        => Assert.DoesNotContain("text-align", ParagraphStyle(Html(Doc(Para("t")))));

    private static string ParagraphStyle(string html)
    {
        int at = html.IndexOf("<p", StringComparison.Ordinal);
        Assert.True(at >= 0, "no paragraph in the export");
        int end = html.IndexOf("</p>", at, StringComparison.Ordinal);
        var p = XElement.Parse(html.Substring(at, end - at + 4));
        return p.Attribute("style")?.Value ?? "";
    }

    // ================================================================ lang

    /// <summary>
    /// The HTML export declares the document's language, or none — never <c>en</c> by default.
    /// </summary>
    /// <remarks>
    /// A wrong language is worse than no language: a screen reader pronounces the Hebrew with
    /// English phonemes while an automated check reports a clean declaration. The model documents
    /// exactly that, and the PDF path already followed it.
    /// </remarks>
    [Fact]
    public void Html_Language_MirrorsTheDocumentAndThePdf()
    {
        var doc = Doc(Para(Hebrew));
        doc.Language = "he-IL";
        doc.Tagged = true;                 // the PDF catalog carries /Lang on the tagged path

        string html = Html(doc);
        Assert.Contains("<html lang=\"he-IL\">", html);
        Assert.Contains("/Lang (he-IL)", PdfContentProbe.Raw(doc.ToPdf()));
    }

    [Fact]
    public void Html_NoLanguage_DeclaresNone()
    {
        string html = Html(Doc(Para("plain")));

        Assert.Contains("<html>", html);
        Assert.DoesNotContain("lang=", html);
    }

    // ================================================================ TextDirection.Auto

    /// <summary>
    /// An <see cref="TextDirection.Auto"/> Hebrew paragraph resolves right-to-left in every
    /// exporter, not only in the PDF.
    /// </summary>
    /// <remarks>
    /// <para>Auto was wired through <c>PdfExporter.ResolveBaseDirection</c> while the other three
    /// still tested <c>== RightToLeft</c>, so the same document read right-to-left in one file and
    /// left-to-right in three.</para>
    /// <para><b>Mixed or alignment-based assertions only.</b> Pure Hebrew word order is correct
    /// under either base direction — it is one run that reverses as a unit — so these assert on the
    /// direction MARKUP each format writes, which is falsifiable, rather than on word order, which
    /// is not.</para>
    /// </remarks>
    [Fact]
    public void Auto_HebrewSection_IsRightToLeftInDocx()
    {
        // Differential, not "non-empty": the same builder, the same export, one word of Hebrew
        // against one word of Latin. A bare NotEmpty here would pass if Auto emitted w:bidi
        // unconditionally — which is a different bug in the same line of code, and the one an
        // "is it RTL?" test is least likely to notice.
        var rtl = new Document();
        var rtlSection = new Section { Direction = TextDirection.Auto };
        rtlSection.Children.Add(Para(Hebrew));
        rtl.Sections.Add(rtlSection);

        var ltr = new Document();
        var ltrSection = new Section { Direction = TextDirection.Auto };
        ltrSection.Children.Add(Para("plain latin"));
        ltr.Sections.Add(ltrSection);

        int hebrew = DocxXml(rtl).Descendants(W + "bidi").Count();
        int latin  = DocxXml(ltr).Descendants(W + "bidi").Count();

        Assert.True(hebrew > 0 && latin == 0,
            $"Auto must resolve Hebrew to RTL and Latin to LTR; got w:bidi x{hebrew} for Hebrew " +
            $"and x{latin} for Latin.");
    }

    [Fact]
    public void Auto_LatinSection_StaysLeftToRightInDocx()
    {
        var doc = new Document();
        var section = new Section { Direction = TextDirection.Auto };
        section.Children.Add(Para("plain latin"));
        doc.Sections.Add(section);

        Assert.Empty(DocxXml(doc).Descendants(W + "bidi"));
    }

    /// <summary>HTML is the one format that can express Auto verbatim, and could not say it.</summary>
    [Fact]
    public void Auto_Paragraph_EmitsDirAutoInHtml()
    {
        string html = Html(Doc(Para(Hebrew, TextAlignment.Start, TextDirection.Auto)));

        int at = html.IndexOf("<p", StringComparison.Ordinal);
        int end = html.IndexOf("</p>", at, StringComparison.Ordinal);
        var p = XElement.Parse(html.Substring(at, end - at + 4));

        Assert.Equal("auto", p.Attribute("dir")!.Value);
    }

    [Fact]
    public void Auto_HebrewSection_IsRightToLeftInOdt()
        => Assert.Contains("RTLp", Odt(AutoSection(Hebrew)));

    [Fact]
    public void Auto_LatinSection_StaysLeftToRightInOdt()
        => Assert.DoesNotContain("text:style-name=\"RTLp\"", Odt(AutoSection("plain latin")));

    private static Document AutoSection(string text)
    {
        var doc = new Document();
        var section = new Section { Direction = TextDirection.Auto };
        section.Children.Add(Para(text));
        doc.Sections.Add(section);
        return doc;
    }

    /// <summary>
    /// An Auto Hebrew section mirrors its table columns, the way an explicitly RTL one does.
    /// </summary>
    /// <remarks>
    /// <c>IsSectionRtl</c> answered <c>false</c> for Auto while <c>ResolveBaseDirection</c>
    /// answered right-to-left, so one section produced RTL paragraphs and LTR table columns — the
    /// same document disagreeing with itself inside one exporter.
    /// </remarks>
    [Fact]
    public void Auto_HebrewSection_MirrorsTableColumnsInThePdf()
    {
        double autoFirstCellX = FirstCellX(TextDirection.Auto);
        double rtlFirstCellX = FirstCellX(TextDirection.RightToLeft);
        double ltrFirstCellX = FirstCellX(TextDirection.LeftToRight);

        // Column MIRRORING is the thing under test, so the assertion is about which half of the
        // band cell 0 lands in. It is deliberately not `auto == rtl`: the paragraph INSIDE the cell
        // resolves its own direction, and an Auto section holding Latin cell text legitimately
        // left-aligns that text within its (mirrored) cell where an explicit RTL section
        // right-aligns it.
        double mid = (ltrFirstCellX + rtlFirstCellX) / 2;
        Assert.True(rtlFirstCellX > ltrFirstCellX + 100,
            $"fixture must show a visible mirror (ltr={ltrFirstCellX}, rtl={rtlFirstCellX})");
        Assert.True(autoFirstCellX > mid,
            $"an Auto Hebrew section must mirror its columns (auto={autoFirstCellX}, ltr={ltrFirstCellX}, rtl={rtlFirstCellX})");
    }

    /// <summary>x of the word in the first cell of a two-column table in a Hebrew section.</summary>
    private static double FirstCellX(TextDirection direction)
    {
        var doc = new Document();
        var section = new Section { Direction = direction };
        var table = new Table();
        var row = new TableRow();
        foreach (string t in new[] { "aaa", "bbb" })
        {
            var cell = new TableCell();
            var p = new Paragraph();
            p.Runs.Add(new TextRun { Text = t });
            cell.Blocks.Add(p);
            row.Cells.Add(cell);
        }
        table.Rows.Add(row);
        section.Children.Add(table);
        // Hebrew elsewhere in the section, so Auto has something strong to detect.
        section.Children.Add(Para(Hebrew));
        doc.Sections.Add(section);

        string content = PdfContentProbe.DecodeStreams(doc.ToPdf(new PdfExportOptions
        {
            AllowSystemFontSubstitution = false,
            FontProvider = _ => MinimalTrueType.Build(Hebrew + "abc "),
        }));
        double x = PdfContentProbe.XOf(content, "aaa");
        Assert.False(double.IsNaN(x), "the first cell's text was never drawn");
        return x;
    }

    // ================================================================ list items

    /// <summary>
    /// <see cref="ListItem.Runs"/> and <see cref="ListItem.Marker"/> — both documented as
    /// overriding their plain counterparts — reach every exporter.
    /// </summary>
    /// <remarks>
    /// DOCX and ODT wrote only <c>Text</c> behind a marker derived from <c>IsNumbered</c>, so an
    /// item produced by <c>HtmlToBuilderMapper</c> (formatted fragments in <c>Runs</c>, empty
    /// <c>Text</c>, an explicit <c>"1.2."</c> marker) exported as a bare "1. " with its entire text
    /// missing — and no diagnostic said so.
    /// </remarks>
    [Fact]
    public void Docx_ListItem_HonoursMarkerAndRuns()
    {
        var xml = DocxXml(Doc(RichListItem()));
        string text = string.Concat(xml.Descendants(W + "t").Select(t => t.Value));

        Assert.Contains("1.2.", text);
        Assert.Contains("bold bit", text);
        Assert.DoesNotContain("1. ", text);      // the derived number must not appear
    }

    [Fact]
    public void Odt_ListItem_HonoursMarkerAndRuns()
    {
        string xml = Odt(Doc(RichListItem()));

        Assert.Contains("1.2.", xml);
        Assert.Contains("bold bit", xml);
    }

    [Fact]
    public void Html_ListItem_HonoursMarkerAndRuns()
    {
        string html = Html(Doc(RichListItem()));

        Assert.Contains("1.2.", html);
        Assert.Contains("bold bit", html);
    }

    [Fact]
    public void Pdf_ListItem_HonoursMarkerAndRuns()
    {
        string content = PdfContentProbe.DecodeStreams(Doc(RichListItem()).ToPdf());
        var drawn = PdfContentProbe.DrawnTexts(content).Select(d => d.Text).ToList();

        Assert.Contains("1.2.", drawn);
        Assert.Contains("bold", drawn);
    }

    /// <summary>
    /// A list item whose content is in <see cref="ListItem.Text"/> reaches every exporter — the
    /// other half of the pair, and the half that had no test.
    /// </summary>
    /// <remarks>
    /// <para><b>The other branch.</b> The four tests above all build <see cref="RichListItem"/>, whose
    /// content is in <c>Runs</c> and whose <c>Text</c> is empty — so without this one, the branch that
    /// adds <c>Text</c> would be exercised by no assertion in the repository, on the overload
    /// (<c>AddListItem("first")</c>) that most callers use, and an exporter rendering the marker and
    /// dropping the text would still compile (<c>if (…) foreach (…) …;</c> is legal with no
    /// <c>else</c>). A fixture that covers one side of a documented either/or is a fixture that hides
    /// the other.</para>
    /// </remarks>
    [Theory]
    [InlineData("pdf")]
    [InlineData("html")]
    [InlineData("docx")]
    [InlineData("odt")]
    public void EveryExporter_RendersAListItemWhoseContentIsInText(string format)
    {
        Document doc = Doc(new ListItem { Text = "plain item text" });

        string rendered;
        switch (format)
        {
            case "pdf":
                string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
                rendered = string.Concat(PdfContentProbe.DrawnTexts(content).Select(d => d.Text));
                break;
            case "html": rendered = Html(doc); break;
            case "docx":
                rendered = string.Concat(DocxXml(doc).Descendants(W + "t").Select(t => t.Value));
                break;
            default: rendered = Odt(doc); break;
        }

        Assert.Contains("plain item text", rendered, StringComparison.Ordinal);

    }

    /// <summary>
    /// A placed paragraph's <see cref="AbsolutePlacement.Following"/> paragraphs reach every
    /// exporter, at the top level and in a table cell: the PDF draws them under the placed one, and
    /// the exporters that draw no placement write them in the flow after it — never dropped.
    /// </summary>
    [Theory]
    [InlineData("pdf", false)]
    [InlineData("html", false)]
    [InlineData("docx", false)]
    [InlineData("odt", false)]
    [InlineData("pdf", true)]
    [InlineData("html", true)]
    [InlineData("docx", true)]
    [InlineData("odt", true)]
    public void EveryExporter_WritesAPlacedParagraphsFollowingParagraphs(string format, bool inCell)
    {
        Paragraph placed = Para("placed first");
        placed.Placement = new AbsolutePlacement { LeftPt = 100, TopPt = 100 };
        placed.Placement.Following.Add(Para("second block"));

        Document doc;
        if (inCell)
        {
            var cell = new TableCell();
            cell.Blocks.Add(placed);
            var row = new TableRow();
            row.Cells.Add(cell);
            var table = new Table();
            table.Rows.Add(row);
            doc = Doc(table);
        }
        else
        {
            doc = Doc(placed);
        }

        string rendered;
        switch (format)
        {
            case "pdf":
                string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
                rendered = string.Concat(PdfContentProbe.DrawnTexts(content).Select(d => d.Text));
                break;
            case "html": rendered = Html(doc); break;
            case "docx":
                rendered = string.Concat(DocxXml(doc).Descendants(W + "t").Select(t => t.Value));
                break;
            default: rendered = Odt(doc); break;
        }

        Assert.Contains("placed first", rendered, StringComparison.Ordinal);
        Assert.Contains("second block", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A table among a placement's <see cref="AbsolutePlacement.FollowingBlocks"/> reaches every
    /// exporter, at the top level and in a table cell, after the placed paragraph and its
    /// <see cref="AbsolutePlacement.Following"/> one: the PDF draws it under them, the others write it
    /// in the flow.
    /// </summary>
    [Theory]
    [InlineData("pdf", false)]
    [InlineData("html", false)]
    [InlineData("docx", false)]
    [InlineData("odt", false)]
    [InlineData("pdf", true)]
    [InlineData("html", true)]
    [InlineData("docx", true)]
    [InlineData("odt", true)]
    public void EveryExporter_WritesAPlacedParagraphsFollowingTable(string format, bool inCell)
    {
        Paragraph placed = Para("placed first");
        placed.Placement = new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 };
        placed.Placement.Following.Add(Para("second block"));
        var inner = new TableCell();
        inner.Paragraphs.Add(Para("third in a table"));
        var innerRow = new TableRow();
        innerRow.Cells.Add(inner);
        var followingTable = new Table();
        followingTable.Rows.Add(innerRow);
        placed.Placement.FollowingBlocks.Add(followingTable);

        Document doc;
        if (inCell)
        {
            var cell = new TableCell();
            cell.Blocks.Add(placed);
            var row = new TableRow();
            row.Cells.Add(cell);
            var table = new Table();
            table.Rows.Add(row);
            doc = Doc(table);
        }
        else
        {
            doc = Doc(placed);
        }

        string rendered = format switch
        {
            "pdf" => string.Concat(PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf())).Select(d => d.Text)),
            "html" => Html(doc),
            "docx" => string.Concat(DocxXml(doc).Descendants(W + "t").Select(t => t.Value)),
            _ => Odt(doc),
        };

        int first = rendered.IndexOf("placed first", StringComparison.Ordinal);
        int second = rendered.IndexOf("second block", StringComparison.Ordinal);
        int third = rendered.IndexOf("third in a table", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first && third > second, $"{format}: the blocks are not all written, in order: {first}, {second}, {third}");
    }

    /// <summary>
    /// A list among a placement's <see cref="AbsolutePlacement.FollowingBlocks"/> reaches every
    /// exporter after the placed paragraph — what a table cell holds follows a placed paragraph.
    /// </summary>
    [Theory]
    [InlineData("pdf")]
    [InlineData("html")]
    [InlineData("docx")]
    [InlineData("odt")]
    public void EveryExporter_WritesAPlacedParagraphsFollowingList(string format)
    {
        Paragraph placed = Para("placed first");
        placed.Placement = new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 };
        var list = new ListBlock();
        list.Items.Add(new ListItem { Text = "a listed item" });
        placed.Placement.FollowingBlocks.Add(list);
        Document doc = Doc(placed);

        string rendered = format switch
        {
            "pdf" => string.Concat(PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf())).Select(d => d.Text)),
            "html" => Html(doc),
            "docx" => string.Concat(DocxXml(doc).Descendants(W + "t").Select(t => t.Value)),
            _ => Odt(doc),
        };
        int first = rendered.IndexOf("placed first", StringComparison.Ordinal);
        int item = rendered.IndexOf("a listed item", StringComparison.Ordinal);
        Assert.True(first >= 0 && item > first, $"{format}: the list is not written after the placed paragraph: {first}, {item}");
    }

    /// <summary>
    /// A block no exporter places after a paragraph — a paragraph placed itself — is reported by every
    /// exporter, and the table beside it is still written: the report is about the one block.
    /// </summary>
    [Theory]
    [InlineData("pdf")]
    [InlineData("html")]
    [InlineData("docx")]
    [InlineData("odt")]
    public void EveryExporter_ReportsAFollowingBlockItCannotPlace(string format)
    {
        Paragraph placed = Para("placed first");
        placed.Placement = new AbsolutePlacement { LeftPt = 100, TopPt = 100, WidthPt = 200 };
        Paragraph nested = Para("a nested placed paragraph");
        nested.Placement = new AbsolutePlacement { LeftPt = 300, TopPt = 300 };
        placed.Placement.FollowingBlocks.Add(nested);
        var cell = new TableCell();
        cell.Paragraphs.Add(Para("table after the nested one"));
        var row = new TableRow();
        row.Cells.Add(cell);
        var table = new Table();
        table.Rows.Add(row);
        placed.Placement.FollowingBlocks.Add(table);
        Document doc = Doc(placed);

        // The sink is process-wide: only this thread's export is this test's.
        var reports = new System.Collections.Generic.List<Export.BuilderDiagnostic>();
        int thread = Environment.CurrentManagedThreadId;
        void Collect(Export.BuilderDiagnostic d) { if (Environment.CurrentManagedThreadId == thread) lock (reports) reports.Add(d); }
        Export.BuilderDiagnostics.OnEvent += Collect;
        string rendered;
        try
        {
            rendered = format switch
            {
                "pdf" => string.Concat(PdfContentProbe.DrawnTexts(PdfContentProbe.DecodeStreams(doc.ToPdf())).Select(d => d.Text)),
                "html" => Html(doc),
                "docx" => string.Concat(DocxXml(doc).Descendants(W + "t").Select(t => t.Value)),
                _ => Odt(doc),
            };
        }
        finally { Export.BuilderDiagnostics.OnEvent -= Collect; }

        Assert.Contains(reports, r => r.Kind == Export.BuilderDiagnosticKind.UnsupportedByExportFormat
                                      && r.Detail.Contains("FollowingBlocks holds a placed paragraph", StringComparison.Ordinal));
        Assert.Contains("table after the nested one", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("a nested placed paragraph", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// A list item in a <b>table cell</b> reaches every exporter.
    /// </summary>
    /// <remarks>
    /// <para><b><c>CellBuilder.AddListItem</c> is public, in the API snapshot.</b> A cell-block loop
    /// handling <c>Paragraph</c> and <c>Table</c> and nothing else would drop the item with no
    /// diagnostic — and in the PDF exporter twice over, if the code-point walker that decides which
    /// glyphs to embed skipped it too.</para>
    ///
    /// <para><b>A branch that exists in one loop and not its sibling is the shape that costs
    /// content.</b> Section children and cell blocks are both <c>IReadOnlyList&lt;IDocElement&gt;</c>,
    /// so the model says the two are the same thing, and the exporters must agree.</para>
    /// </remarks>
    [Theory]
    [InlineData("pdf")]
    [InlineData("html")]
    [InlineData("docx")]
    [InlineData("odt")]
    public void EveryExporter_RendersAListItemInsideATableCell(string format)
    {
        var cell = new TableCell();
        cell.Blocks.Add(new ListItem { Text = "item in a cell" });

        var row = new TableRow();
        row.Cells.Add(cell);

        var table = new Table();
        table.Rows.Add(row);

        Document doc = Doc(table);

        string rendered;
        switch (format)
        {
            case "pdf":
                string content = PdfContentProbe.DecodeStreams(doc.ToPdf());
                rendered = string.Concat(PdfContentProbe.DrawnTexts(content).Select(d => d.Text));
                break;
            case "html": rendered = Html(doc); break;
            case "docx":
                rendered = string.Concat(DocxXml(doc).Descendants(W + "t").Select(t => t.Value));
                break;
            default: rendered = Odt(doc); break;
        }

        Assert.Contains("item in a cell", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row a cell of list items sits in is <b>measured</b> from them, not just drawn with them.
    /// </summary>
    /// <remarks>
    /// <para><b>Drawing and measuring are two code paths and a table needs them to agree.</b>
    /// <c>TableRenderer</c> sizes every row by measuring its cells and then fills it by drawing
    /// them; a block the measure loop does not know about produces a row sized as if the cell were
    /// empty, and the content then spills past its own borders and over whatever follows.</para>
    ///
    /// <para><b>Asserted through the position of a later element, because nothing else can see
    /// it.</b> The item is drawn either way — a wrongly measured row still receives its ink — so a
    /// test that asks whether the text appears passes on both. Deleting the measure arm survives
    /// every such assertion and is caught only by the paragraph <i>after</i> the table moving up
    /// the page. That is the same blind spot a pixel-comparing parity suite has about an advance.</para>
    /// </remarks>
    [Fact]
    public void AListItemInACellIsMeasuredIntoItsRowHeight()
    {
        double withItems = FollowingParagraphY(3);
        double empty = FollowingParagraphY(0);

        // PDF Y grows upward, so a taller table pushes what follows DOWN — to a smaller Y.
        Assert.True(withItems < empty - 20,
            $"the row was not sized from its list items: the paragraph after the table sits at "
            + $"{withItems:F1} with three items and {empty:F1} with none, a difference of "
            + $"{empty - withItems:F1} pt");
    }

    /// <summary>Y of a paragraph placed after a one-cell table holding <paramref name="items"/> list items.</summary>
    private static double FollowingParagraphY(int items)
    {
        var cell = new TableCell();
        for (int i = 0; i < items; i++)
            cell.Blocks.Add(new ListItem { Text = "cell item " + i.ToString(CultureInfo.InvariantCulture) });

        var row = new TableRow();
        row.Cells.Add(cell);

        var table = new Table();
        table.Rows.Add(row);

        var after = new Paragraph();
        after.Runs.Add(new TextRun { Text = "AFTERTABLE" });

        string content = PdfContentProbe.DecodeStreams(Doc(table, after).ToPdf());
        return PdfContentProbe.YOf(content, "AFTERTABLE");
    }

    private static ListItem RichListItem()
    {
        var li = new ListItem { IsNumbered = true, Marker = "1.2. ", Text = "" };
        li.Runs.Add(new TextRun { Text = "bold bit", IsBold = true });
        return li;
    }

    // ================================================================ dropped content

    /// <summary>
    /// An inline image the DOCX and ODT exporters cannot carry is REPORTED, not dropped in
    /// silence — the paragraph <b>background</b> image, a far smaller loss, already was.
    /// </summary>
    [Fact]
    public void Docx_InlineImage_IsReportedAsUnsupported()
    {
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "Figure 1" });
        p.Runs.Add(new ImageRun { Data = new byte[] { 1, 2, 3, 4 }, WidthPt = 20, HeightPt = 20, Alt = "chart" });

        using var ms = new MemoryStream();
        Doc(p).SaveAsDocx(ms);

        Assert.NotNull(BuilderDiagnostics.LastError);
        Assert.Equal(BuilderDiagnosticKind.UnsupportedByExportFormat, BuilderDiagnostics.LastError.Kind);
        Assert.Contains("chart", BuilderDiagnostics.LastError.Detail);
    }

    [Fact]
    public void Odt_InlineImage_IsReportedAsUnsupported()
    {
        var p = new Paragraph();
        p.Runs.Add(new ImageRun { Data = new byte[] { 1, 2, 3, 4 }, WidthPt = 20, HeightPt = 20 });

        using var ms = new MemoryStream();
        Doc(p).SaveAsOdt(ms);

        Assert.NotNull(BuilderDiagnostics.LastError);
        Assert.Equal(BuilderDiagnosticKind.UnsupportedByExportFormat, BuilderDiagnostics.LastError.Kind);
    }

    [Fact]
    public void Docx_PlainParagraph_ReportsNothing()
    {
        using var ms = new MemoryStream();
        Doc(Para("plain")).SaveAsDocx(ms);

        Assert.Null(BuilderDiagnostics.LastError);
    }
}
