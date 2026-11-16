// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using Xunit;
using Tetitulz.Pdf.Builder;
using Tetitulz.Pdf.Builder.Export;
using Tetitulz.Pdf.Builder.Model;
using Tetitulz.Pdf.TestHelpers;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// RTL (right-to-left, Hebrew/Arabic) support across all four exporters.
/// The PDF path embeds a Unicode TrueType font + bidi-reorders the text; DOCX/HTML/ODT
/// emit their format's direction markers.
/// </summary>
public class RtlExportTests
{
    private const string Hebrew = "שלום עולם";   // "hello world"

    // Every character HebrewDoc() puts on the page. The injected face below has to cover all of
    // them, not just the paragraph: a run the provider cannot serve falls back to WinAnsi and
    // never reaches the composite-font path this file asserts on.
    private const string HebrewDocCharacters = Hebrew + "שם" + "מחיר" + "מוצר" + "10" + "פריט ראשון";

    private static Document HebrewDoc() =>
        new DocumentBuilder()
            .AddSection(s => s
                .Rtl()
                .AddParagraph(p => p.AddText(Hebrew).FontSize(20))
                .AddTable(t => t
                    .AddRow(r => r.AddCell("שם").AddCell("מחיר"))
                    .AddRow(r => r.AddCell("מוצר").AddCell("10")))
                .AddListItem("פריט ראשון"))
            .Build();

    /// <summary>
    /// A synthetic face covering exactly <see cref="HebrewDocCharacters"/>, handed to the exporter
    /// through <see cref="PdfExportOptions.FontProvider"/> with the host search switched off — the
    /// shape <c>FontResolutionTests</c> uses, so the assertions below hold identically on a
    /// developer's Windows box and on a bare Linux container.
    ///
    /// <para>What this replaces: an <c>if (HasHebrewFont())</c> wrapper around the four assertions,
    /// which on a host with no Hebrew font left the test asserting only that some bytes came back
    /// starting with '%'. That passes with composite-font embedding deleted outright.</para>
    /// </summary>
    private static PdfExportOptions InjectedHebrewFont() => new PdfExportOptions
    {
        AllowSystemFontSubstitution = false,
        FontProvider = _ => MinimalTrueType.Build(HebrewDocCharacters),
    };

    // ---- model / builder API ----

    [Fact]
    public void Rtl_SetsSectionAndParagraphDirection()
    {
        var doc = new DocumentBuilder()
            .AddSection(s => s.Rtl().AddParagraph(p => p.Rtl().AddText("x")))
            .Build();
        Assert.Equal(TextDirection.RightToLeft, doc.Sections[0].Direction);
        var para = (Paragraph)doc.Sections[0].Children[0];
        Assert.Equal(TextDirection.RightToLeft, para.Direction);
    }

    [Fact]
    public void Paragraph_InheritsSectionDirection_WhenUnset()
    {
        var doc = new DocumentBuilder()
            .AddSection(s => s.Rtl().AddParagraph(p => p.AddText("x")))
            .Build();
        var para = (Paragraph)doc.Sections[0].Children[0];
        Assert.Null(para.Direction); // unset → exporters fall back to the section's RTL
    }

    // ---- PDF ----

    [Fact]
    public void Pdf_HebrewRtl_EmbedsCompositeFont()
    {
        byte[] pdf = HebrewDoc().ToPdf(InjectedHebrewFont());

        // The non-WinAnsi text forces the embedded-Type0 path: the font dictionaries
        // are written uncompressed, so they appear verbatim in the file bytes.
        string raw = Encoding.Latin1.GetString(pdf);
        Assert.Contains("Type0", raw);
        Assert.Contains("Identity-H", raw);
        Assert.Contains("CIDFontType2", raw);
        Assert.Contains("FontFile2", raw);   // the program really reached the file
    }

    [Fact]
    public void Pdf_AsciiOnly_DoesNotEmbedFont()
    {
        // No non-WinAnsi characters → no embedded font, standard-14 path only.
        byte[] pdf = new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddText("Hello world")))
            .Build().ToPdf();
        string raw = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("Type0", raw);
        // The face the text is set in: every page carries all twelve standard faces as resources.
        Assert.Equal("Helvetica", PdfContentProbe.BaseFontOf(pdf, "Hello"));
    }

    // ---- DOCX ----

    [Fact]
    public void Docx_HebrewRtl_EmitsBidiAndRtlMarkers()
    {
        string xml = ReadZipEntry(HebrewDoc().ToDocx(), "word/document.xml");
        Assert.Contains("<w:bidi", xml);          // paragraph / section base direction
        Assert.Contains("<w:rtl", xml);           // run complex-script direction
        Assert.Contains("<w:bidiVisual", xml);    // table column mirroring
    }

    // ---- HTML ----

    [Fact]
    public void Html_HebrewRtl_EmitsDirRtl()
    {
        string html = Encoding.UTF8.GetString(HebrewDoc().ToHtml());
        Assert.Contains("dir=\"rtl\"", html);
        Assert.Contains(Hebrew, html);
    }

    [Fact]
    public void Html_Ltr_HasNoDirAttribute()
    {
        string html = Encoding.UTF8.GetString(new DocumentBuilder()
            .AddSection(s => s.AddParagraph(p => p.AddText("Hello")))
            .Build().ToHtml());
        Assert.DoesNotContain("dir=\"rtl\"", html);
    }

    // ---- ODT ----

    [Fact]
    public void Odt_HebrewRtl_EmitsWritingMode()
    {
        string content = ReadZipEntry(HebrewDoc().ToOdt(), "content.xml");
        Assert.Contains("writing-mode=\"rl-tb\"", content);
        Assert.Contains("text:style-name=\"RTLp\"", content);
    }

    // ---- all four still round-trip together ----

    /// <summary>
    /// The per-format tests above each check one exporter's direction markers. What this one adds
    /// is that all four describe the <b>same document</b>: every output must be a well-formed file
    /// of its own kind and must carry the Hebrew text that was put in.
    ///
    /// <para>The predecessor asserted <c>Length > 0</c> four times, which four exporters returning
    /// a header and nothing else would satisfy — and "NoExceptions" was the honest name for what it
    /// actually checked.</para>
    /// </summary>
    [Fact]
    public void AllFormats_HebrewRtl_EachCarriesTheDocumentContent()
    {
        var doc = HebrewDoc();

        // PDF: a real document, not a stub — it opens and has a page.
        byte[] pdf = doc.ToPdf();
        using (var ms = new MemoryStream(pdf))
        using (var opened = Tetitulz.Pdf.Core.Document.PdfDocument.Open(ms))
            Assert.True(opened.Pages.Count >= 1);

        // The three text formats carry the Hebrew verbatim. (The PDF encodes it through an
        // embedded font's glyph ids, so it is not searchable as raw bytes — hence the split.)
        Assert.Contains(Hebrew, ReadZipEntry(doc.ToDocx(), "word/document.xml"));
        Assert.Contains(Hebrew, Encoding.UTF8.GetString(doc.ToHtml()));
        Assert.Contains(Hebrew, ReadZipEntry(doc.ToOdt(), "content.xml"));
    }

    private static string ReadZipEntry(byte[] zipBytes, string entryName)
    {
        using var ms = new MemoryStream(zipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry(entryName);
        Assert.NotNull(entry);
        using var r = new StreamReader(entry!.Open(), Encoding.UTF8);
        return r.ReadToEnd();
    }
}
