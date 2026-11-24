// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Tetitulz.Pdf.DocxToPdf.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests;

/// <summary>
/// A table style's conditional entries are read once a style, not once a cell: rebuilding the
/// style chain and scanning every <c>tblStylePr</c> for each conditional type a cell takes, in the
/// shading and the border resolver both, would make 100 000 entries under a 100 x 100 table some
/// 4x10^9 visits. The header row's shading still comes from its entry.
/// </summary>
public sealed class TableStyleConditionalsTests
{
    private const int Entries = 2000;
    private const int Side = 10;

    private static byte[] Docx()
    {
        string w = HostileDocxInputTests.WordNamespace;
        var styles = new StringBuilder($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:styles xmlns:w=\"{w}\">")
            .Append("<w:style w:type=\"table\" w:styleId=\"T\"><w:name w:val=\"T\"/>")
            .Append("<w:tblStylePr w:type=\"firstRow\"><w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"FF0000\"/></w:tcPr></w:tblStylePr>");
        for (int i = 0; i < Entries - 2; i++) styles.Append("<w:tblStylePr w:type=\"seCell\"/>");
        // A second entry of the same type: the first in the chain is the one a cell takes.
        styles.Append("<w:tblStylePr w:type=\"firstRow\"><w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"0000FF\"/></w:tcPr></w:tblStylePr>");
        styles.Append("</w:style></w:styles>");

        var rows = new StringBuilder();
        for (int r = 0; r < Side; r++)
        {
            rows.Append("<w:tr>");
            for (int c = 0; c < Side; c++) rows.Append("<w:tc><w:p><w:r><w:t>x</w:t></w:r></w:p></w:tc>");
            rows.Append("</w:tr>");
        }
        string document = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{w}\"><w:body>"
            + "<w:tbl><w:tblPr><w:tblStyle w:val=\"T\"/><w:tblW w:w=\"5000\" w:type=\"pct\"/></w:tblPr>"
            + rows + "</w:tbl><w:p/></w:body></w:document>";

        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            using (var part = new MemoryStream(Encoding.UTF8.GetBytes(document))) main.FeedData(part);
            var stylePart = main.AddNewPart<StyleDefinitionsPart>();
            using (var part = new MemoryStream(Encoding.UTF8.GetBytes(styles.ToString()))) stylePart.FeedData(part);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// A 2 x 2 table of "Head"/"Body" cells, styled "H" or not; H's header row is red with bold green text, its first
    /// column blue.
    /// </summary>
    internal static byte[] HeaderStyledDocx(bool styled)
    {
        string w = HostileDocxInputTests.WordNamespace;
        string styles = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:styles xmlns:w=\"{w}\">"
            + "<w:style w:type=\"table\" w:styleId=\"H\"><w:name w:val=\"H\"/>"
            + "<w:tblStylePr w:type=\"firstRow\"><w:rPr><w:b/><w:color w:val=\"00FF00\"/></w:rPr>"
            + "<w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"FF0000\"/></w:tcPr></w:tblStylePr>"
            + "<w:tblStylePr w:type=\"firstCol\"><w:tcPr><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"0000FF\"/></w:tcPr></w:tblStylePr>"
            + "</w:style></w:styles>";
        string cell(string t) => $"<w:tc><w:p><w:r><w:t>{t}</w:t></w:r></w:p></w:tc>";
        string document = $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document xmlns:w=\"{w}\"><w:body>"
            + "<w:tbl><w:tblPr>" + (styled ? "<w:tblStyle w:val=\"H\"/>" : "") + "<w:tblW w:w=\"5000\" w:type=\"pct\"/></w:tblPr>"
            + "<w:tblGrid><w:gridCol w:w=\"4000\"/><w:gridCol w:w=\"4000\"/></w:tblGrid>"
            + "<w:tr>" + cell("Head") + cell("Head") + "</w:tr><w:tr>" + cell("Body") + cell("Body") + "</w:tr>"
            + "</w:tbl><w:p/></w:body></w:document>";

        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            using (var part = new MemoryStream(Encoding.UTF8.GetBytes(document))) main.FeedData(part);
            var stylePart = main.AddNewPart<StyleDefinitionsPart>();
            using (var part = new MemoryStream(Encoding.UTF8.GetBytes(styles))) stylePart.FeedData(part);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// The Builder path applies a table style's conditionals too: the header row's red fill, its bold face and
    /// its green text, which it applied none of. The control, the same table unstyled, has none of the three.
    /// </summary>
    [Fact]
    public void TheBuilderPath_AppliesTheHeaderRowsConditionalFormatting()
    {
        string Content(bool styled)
        {
            byte[] pdf = DocxToPdfConverter.ConvertToBytes(HeaderStyledDocx(styled), new DocxToPdfOptions { UseLegacyRenderer = false });
            using var doc = Tetitulz.Pdf.Core.Document.PdfDocument.Open(new MemoryStream(pdf));
            return Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
        }

        // Each text's fill and font resource, read from its text object: "<fill> rg\n/<font> <size> Tf ... (<text>) Tj".
        static (string Fill, string Font) TextState(string content, string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(content,
                @"([\d. ]+) rg\n/(\w+) [\d.]+ Tf\n[^\n]*\n\(" + text + @"\) Tj");
            Assert.True(m.Success, $"no text object for {text}");
            return (m.Groups[1].Value.Trim(), m.Groups[2].Value);
        }

        string styled = Content(true), plain = Content(false);

        var head = TextState(styled, "Head");
        var body = TextState(styled, "Body");
        Assert.Contains("1 0 0 rg\n", styled, StringComparison.Ordinal);    // the header row's red fill
        Assert.Equal("0 1 0", head.Fill);                                   // its green text
        Assert.NotEqual("0 1 0", body.Fill);
        Assert.NotEqual(body.Font, head.Font);                               // its bold face
        // The first column's blue on the body row's first cell alone: each cell is placed by its own column.
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(styled, "0 0 1 rg\n").Count);

        Assert.DoesNotContain("1 0 0 rg\n", plain, StringComparison.Ordinal);
        Assert.Equal(TextState(plain, "Body").Font, TextState(plain, "Head").Font);
        Assert.NotEqual("0 1 0", TextState(plain, "Head").Fill);
    }

    /// <summary>
    /// The legacy renderer, whose two resolvers read a table style's conditionals.
    /// </summary>
    [Fact]
    public void AStylesConditionalEntries_AreReadOnceAStyle_NotOnceACell()
    {
        long before = StyleResolver.ConditionalEntriesRead;
        byte[] pdf = DocxToPdfConverter.ConvertToBytes(Docx(),
            new DocxToPdfOptions { CompressStreams = false, UseLegacyRenderer = true });
        long read = StyleResolver.ConditionalEntriesRead - before;

        // Once for the style; once a cell, in two resolvers, would be 200 times this.
        Assert.Equal(Entries, read);
        // The header row's entry is the one a cell takes: its red fill is painted.
        string text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("1.000 0.000 0.000 rg", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("0.000 0.000 1.000 rg", text, System.StringComparison.Ordinal);
    }
}
