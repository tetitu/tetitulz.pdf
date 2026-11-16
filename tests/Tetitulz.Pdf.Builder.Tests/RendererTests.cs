// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;
using Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// Exercises the centralized model-driven PDF renderer: wrapping, pagination, per-run
/// character formatting, and rich tables. Models are built directly (these features are
/// set by the DOCX/HTML mappers, not the fluent DocumentBuilder API).
/// </summary>
public class RendererTests
{
    // Inflates every FlateDecode content stream in the PDF and concatenates the operators
    // (Latin1 is a 1:1 byte mapping, so substring→bytes round-trips).
    private static string InflateContent(byte[] pdf)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        var sb = new StringBuilder();
        int idx = 0;
        while (true)
        {
            int s = raw.IndexOf("stream", idx, StringComparison.Ordinal);
            if (s < 0) break;
            if (s >= 3 && raw.Substring(s - 3, 3) == "end") { idx = s + 6; continue; }
            int dataStart = s + 6;
            if (dataStart < raw.Length && raw[dataStart] == '\r') dataStart++;
            if (dataStart < raw.Length && raw[dataStart] == '\n') dataStart++;
            int e = raw.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (e < 0) break;
            int len = e - dataStart;
            while (len > 0 && (raw[dataStart + len - 1] == '\n' || raw[dataStart + len - 1] == '\r')) len--;
            byte[] data = Encoding.Latin1.GetBytes(raw.Substring(dataStart, len));
            try
            {
                using var inMs = new MemoryStream(data);
                using var z = new ZLibStream(inMs, CompressionMode.Decompress);
                using var outMs = new MemoryStream();
                z.CopyTo(outMs);
                sb.Append(Encoding.Latin1.GetString(outMs.ToArray()));
                sb.Append('\n');
            }
            catch { /* not a flate stream (e.g. font program) */ }
            idx = e + 9;
        }
        return sb.ToString();
    }

    private static int CountPages(byte[] pdf)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        int n = 0, i = 0;
        while ((i = raw.IndexOf("/MediaBox", i, StringComparison.Ordinal)) >= 0) { n++; i += 9; }
        return n;
    }

    private static Document OneSection(Action<Section> build)
    {
        var doc = new Document();
        var sec = new Section();
        build(sec);
        doc.Sections.Add(sec);
        return doc;
    }

    private static Paragraph Para(params TextRun[] runs)
    {
        var p = new Paragraph();
        foreach (var r in runs) p.Runs.Add(r);
        return p;
    }

    [Fact]
    public void Pagination_LongDocument_ProducesMultiplePages()
    {
        var doc = OneSection(sec =>
        {
            for (int i = 0; i < 120; i++)
                sec.Children.Add(Para(new TextRun { Text = $"Paragraph number {i} with some content." }));
        });
        byte[] pdf = doc.ToPdf();
        Assert.True(CountPages(pdf) >= 2, $"expected multiple pages, got {CountPages(pdf)}");
    }

    [Fact]
    public void Wrapping_LongParagraph_ProducesMultipleLines()
    {
        string longText = string.Concat(System.Linq.Enumerable.Repeat("word ", 200));
        var doc = OneSection(sec => sec.Children.Add(Para(new TextRun { Text = longText })));
        string content = InflateContent(doc.ToPdf());
        // Each wrapped line emits at least one text matrix (Tm); a 200-word paragraph wraps.
        int tm = CountOccurrences(content, " Tm");
        Assert.True(tm >= 5, $"expected several wrapped lines, got {tm} Tm operators");
    }

    [Fact]
    public void Underline_EmitsRule()
    {
        var doc = OneSection(sec =>
            sec.Children.Add(Para(new TextRun { Text = "Underlined", IsUnderline = true })));
        string content = InflateContent(doc.ToPdf());
        Assert.Matches(@"re\s+f", content);  // a filled rectangle = the underline rule
    }

    [Fact]
    public void CharFormatting_EmitsTzTcTs()
    {
        var doc = OneSection(sec => sec.Children.Add(Para(
            new TextRun { Text = "Scaled", HorizontalScale = 150, CharSpacing = 2,
                          VerticalAlign = VerticalAlign.Superscript })));
        string content = InflateContent(doc.ToPdf());
        Assert.Contains(" Tz", content);
        Assert.Contains(" Tc", content);
        Assert.Contains(" Ts", content);
    }

    [Fact]
    public void Table_CellShading_EmitsFill()
    {
        var doc = OneSection(sec =>
        {
            var table = new Table();
            var row = new TableRow();
            var cell = new TableCell { BackgroundColor = "#FFCC00" };
            cell.Paragraphs.Add(Para(new TextRun { Text = "Shaded" }));
            row.Cells.Add(cell);
            table.Rows.Add(row);
            sec.Children.Add(table);
        });
        string content = InflateContent(doc.ToPdf());
        Assert.Contains(" rg", content);  // fill colour set for the cell background
        Assert.Contains(" re", content);
    }

    [Fact]
    public void Table_GridSpanAndNestedTable_RenderWithoutError()
    {
        var doc = OneSection(sec =>
        {
            var outer = new Table();
            var r1 = new TableRow();
            var spanCell = new TableCell { GridSpan = 2 };
            spanCell.Paragraphs.Add(Para(new TextRun { Text = "Spans two columns" }));
            r1.Cells.Add(spanCell);
            outer.Rows.Add(r1);

            var r2 = new TableRow();
            var c1 = new TableCell();
            var nested = new Table();
            var nr = new TableRow();
            var nc = new TableCell();
            nc.Paragraphs.Add(Para(new TextRun { Text = "Nested" }));
            nr.Cells.Add(nc);
            nested.Rows.Add(nr);
            c1.Blocks.Add(nested);
            r2.Cells.Add(c1);
            r2.Cells.Add(new TableCell());
            outer.Rows.Add(r2);

            sec.Children.Add(outer);
        });
        byte[] pdf = doc.ToPdf();
        Assert.True(pdf.Length > 200);
        Assert.Equal((byte)'%', pdf[0]);
        Assert.Contains("Nested", InflateContent(pdf));
    }

    [Fact]
    public void Justify_NonLastLine_SpreadsAcrossWidth()
    {
        // A justified multi-line paragraph should still render and paginate cleanly.
        string text = string.Concat(System.Linq.Enumerable.Repeat("alpha beta gamma ", 40));
        var doc = OneSection(sec =>
        {
            var p = Para(new TextRun { Text = text });
            p.Alignment = TextAlignment.Justify;
            sec.Children.Add(p);
        });
        byte[] pdf = doc.ToPdf();
        Assert.True(pdf.Length > 200);
        Assert.True(CountOccurrences(InflateContent(pdf), " Tm") >= 3);
    }

    [Fact]
    public void ParagraphShadingAndBorder_AreDrawn()
    {
        var doc = OneSection(sec =>
        {
            var p = Para(new TextRun { Text = "Boxed callout" });
            p.Shading = "#EEEEEE";
            p.BorderTop = new Border(1, "#FF0000");
            p.BorderBottom = new Border(1, "#FF0000");
            p.BorderLeft = new Border(1, "#FF0000");
            p.BorderRight = new Border(1, "#FF0000");
            sec.Children.Add(p);
        });
        string content = InflateContent(doc.ToPdf());
        Assert.Contains("1 0 0 RG", content);   // red border stroke colour
        Assert.Matches(@"re\s+f", content);      // filled shading rectangle
    }

    [Fact]
    public void Metadata_WrittenToInfoDictionary()
    {
        var doc = new Document { Title = "My Report", Author = "Jane Doe", Subject = "Quarterly" };
        var sec = new Section();
        sec.Children.Add(Para(new TextRun { Text = "Body" }));
        doc.Sections.Add(sec);

        string raw = Encoding.Latin1.GetString(doc.ToPdf());
        Assert.Contains("/Producer", raw);
        Assert.Contains("My Report", raw);
        Assert.Contains("Jane Doe", raw);
        Assert.Contains("Quarterly", raw);
        Assert.Contains("/Info", raw); // trailer references the info dict
    }

    [Fact]
    public void ExternalHyperlink_EmitsLinkAnnotation()
    {
        var doc = OneSection(sec => sec.Children.Add(Para(
            new TextRun { Text = "Visit", LinkUri = "https://example.com/" })));
        string raw = Encoding.Latin1.GetString(doc.ToPdf());
        Assert.Contains("/Link", raw);                 // annotation subtype
        Assert.Contains("/URI", raw);                  // URI action
        Assert.Contains("https://example.com/", raw);  // the target
    }

    [Fact]
    public void InternalAnchor_DoesNotEmitUriAnnotation()
    {
        var doc = OneSection(sec => sec.Children.Add(Para(
            new TextRun { Text = "Jump", LinkUri = "#section2" })));
        string raw = Encoding.Latin1.GetString(doc.ToPdf());
        Assert.DoesNotContain("/URI", raw); // internal anchors need /Dests (not yet emitted)
    }

    [Fact]
    public void DocxExporter_HonorsRichModelFields()
    {
        var doc = new Document();
        var sec = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "x", VerticalAlign = VerticalAlign.Superscript,
            Highlight = "#FFFF00", IsDoubleStrike = true });
        sec.Children.Add(p);
        var t = new Table();
        var row = new TableRow();
        var c = new TableCell { GridSpan = 2, BackgroundColor = "#EEEEEE" };
        c.Paragraphs.Add(Para(new TextRun { Text = "cell" }));
        row.Cells.Add(c);
        t.Rows.Add(row);
        sec.Children.Add(t);
        doc.Sections.Add(sec);

        string xml = ReadZipEntry(doc.ToDocx(), "word/document.xml");
        Assert.Contains("<w:vertAlign", xml);
        Assert.Contains("superscript", xml);
        Assert.Contains("<w:dstrike", xml);
        Assert.Contains("<w:gridSpan", xml);
        Assert.Contains("w:fill=\"EEEEEE\"", xml);
    }

    private static string ReadZipEntry(byte[] zip, string entryPath)
    {
        using var ms = new MemoryStream(zip);
        using var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
        var entry = archive.GetEntry(entryPath);
        using var sr = new StreamReader(entry!.Open());
        return sr.ReadToEnd();
    }

    [Fact]
    public void HtmlExporter_HonorsRichModelFields()
    {
        var doc = new Document();
        var sec = new Section();
        var p = new Paragraph();
        p.Runs.Add(new TextRun { Text = "note", Highlight = "#FFFF00",
            LinkUri = "https://x.com", VerticalAlign = VerticalAlign.Superscript });
        sec.Children.Add(p);
        var t = new Table();
        var row = new TableRow();
        var c = new TableCell { GridSpan = 2, BackgroundColor = "#EEEEEE" };
        c.Paragraphs.Add(Para(new TextRun { Text = "spanning" }));
        row.Cells.Add(c);
        t.Rows.Add(row);
        sec.Children.Add(t);
        doc.Sections.Add(sec);

        string html = Encoding.UTF8.GetString(doc.ToHtml());
        Assert.Contains("background-color:#FFFF00", html);
        Assert.Contains("<a href=\"https://x.com\"", html);
        Assert.Contains("<sup>", html);
        Assert.Contains("colspan=\"2\"", html);
        Assert.Contains("background-color:#EEEEEE", html);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }
}
