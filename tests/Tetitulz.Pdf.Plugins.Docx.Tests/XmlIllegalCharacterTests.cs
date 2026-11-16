// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using Tetitulz.Pdf.Plugins.Docx;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Docx.Tests;

/// <summary>
/// Extracted text containing a character XML 1.0 forbids does not abort the conversion.
///
/// <para><b>Why this exists.</b> Page text was handed to OpenXml verbatim. XML 1.0 §2.2 forbids
/// almost every C0 control character, and <c>DocumentFormat.OpenXml</c> serialises through an
/// <c>XmlWriter</c> with <c>CheckCharacters</c> on — so one such byte aborted the conversion with
/// <c>ArgumentException: '', hexadecimal value 0x01, is an invalid character</c>, thrown from the
/// package's <c>Dispose</c>, i.e. after the <c>using</c> block, so nothing was returned at all. It
/// reached callers through <c>DocxExportPlugin.Export</c> and
/// <c>PdfOperations.Export(input, output, "docx")</c>.</para>
///
/// <para>The route in is ordinary: a font dictionary with no <c>/Subtype</c> falls back to
/// <c>Core.Fonts.FallbackFont</c>, whose decode is a straight CP1252 mapping, so
/// <c>(\001A\002) Tj</c> extracts as U+0001 'A' U+0002. A <c>/ToUnicode</c> CMap mapping a code to
/// <c>&lt;0001&gt;</c> gets there on a perfectly well-formed file too.</para>
/// </summary>
public class XmlIllegalCharacterTests
{
    /// <summary>The marker sits between the two illegal characters, so it can only survive if the
    /// sanitiser removed them rather than the whole run.</summary>
    private const string Marker = "SurvivorText";

    [Fact]
    public void DocxConvert_WithControlCharactersInTheText_ProducesAReadableDocumentKeepingTheText()
    {
        byte[] docx = PdfToDocxConverter.Convert(PdfWithControlCharacters());

        string text = DocxBodyText(docx);
        Assert.Contains(Marker, text, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x01, text);
        Assert.DoesNotContain((char)0x02, text);
    }

    [Fact]
    public void XlsxConvert_WithControlCharactersInTheText_DoesNotAbort()
    {
        byte[] xlsx = PdfToExcelConverter.Convert(PdfWithControlCharacters());

        using var ms = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(ms, false);
        string text = doc.WorkbookPart!.WorksheetParts.First().Worksheet.InnerText;

        Assert.Contains(Marker, text, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x01, text);
    }

    /// <summary>
    /// Text a spreadsheet would decode — <c>_x0031_</c> is "1" in a cell's string — is escaped by
    /// the PDF-to-XLSX converter as by the invoice workbook, so it shows as written. Read from the
    /// part's XML: the SDK's text decodes it too.
    /// </summary>
    [Fact]
    public void XlsxConvert_EscapesTextThatLooksLikeASpreadsheetEscape()
    {
        byte[] xlsx = PdfToExcelConverter.Convert(Pdf("BT /F1 12 Tf 72 700 Td (AB_x0031_) Tj ET"));

        using var ms = new MemoryStream(xlsx);
        using var doc = SpreadsheetDocument.Open(ms, false);
        string xml = doc.WorkbookPart!.WorksheetParts.First().Worksheet.OuterXml;

        Assert.Contains("AB_x005F_x0031_", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void PptxConvert_WithControlCharactersInTheText_DoesNotAbort()
    {
        byte[] pptx = PdfToPptxConverter.Convert(PdfWithControlCharacters());

        using var ms = new MemoryStream(pptx);
        using var doc = PresentationDocument.Open(ms, false);
        string text = doc.PresentationPart!.SlideParts.First().Slide.InnerText;

        Assert.Contains(Marker, text, StringComparison.Ordinal);
        Assert.DoesNotContain((char)0x01, text);
    }

    /// <summary>
    /// The control: an ordinary two-line page still becomes two paragraphs, so the three
    /// assertions above cannot pass by the sanitiser having started to eat everything.
    /// </summary>
    /// <remarks>
    /// <b>This does not pin the tab/LF/CR exemption</b>, and the name used to imply it did. All
    /// three converters split their text on <c>\n</c> <i>before</i> sanitizing, so no line ever
    /// carries one and a mutation removing <c>IsLegalScalar</c>'s whitespace exemption survived this
    /// test untouched. <see cref="XmlTextSanitizerTests"/> pins it at the unit that owns it.
    /// </remarks>
    [Fact]
    public void DocxConvert_OfAnOrdinaryTwoLinePage_StillProducesTwoParagraphs()
    {
        byte[] docx = PdfToDocxConverter.Convert(PdfShowingTwoLines());

        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        var paragraphs = doc.MainDocumentPart!.Document.Body!
            .Elements<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .Select(p => p.InnerText)
            .Where(t => t.Length > 0)
            .ToList();

        Assert.Contains("LineOne", paragraphs);
        Assert.Contains("LineTwo", paragraphs);
    }

    private static string DocxBodyText(byte[] docx)
    {
        using var ms = new MemoryStream(docx);
        using var doc = WordprocessingDocument.Open(ms, false);
        return doc.MainDocumentPart!.Document.Body!.InnerText;
    }

    // The font has no /Subtype, so Core falls back to a CP1252 decode and the octal escapes come
    // through as U+0001 / U+0002.
    private static byte[] PdfWithControlCharacters() =>
        Pdf($"BT /F1 12 Tf 72 700 Td (\\001{Marker}\\002) Tj ET");

    private static byte[] PdfShowingTwoLines() =>
        Pdf("BT /F1 12 Tf 72 700 Td (LineOne) Tj 0 -20 Td (LineTwo) Tj ET");

    private static byte[] Pdf(string content)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));
        void Obj(string body) { offsets.Add(acc.Count); Emit(body); }

        Emit("%PDF-1.7\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
            + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        Obj("5 0 obj\n<< /Type /Font /BaseFont /Helvetica >>\nendobj\n");   // no /Subtype

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
