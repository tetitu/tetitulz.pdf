// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Fonts;

/// <summary>
/// An embedded font's <c>/BaseFont</c> and <c>/FontName</c> come from the <b>English</b>
/// <c>name</c> record, not from whichever record happens to be listed first.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> A <c>name</c> table carries one record per language, and the
/// conventional sort is platform, then encoding, then language — which puts <b>Macintosh records
/// first</b>. Taking the first usable record and accepting any Macintosh language would embed a
/// font whose Mac records are localised and whose English name lives on the Windows platform under
/// its localised name. Reading <i>any</i> Macintosh encoding as ASCII would be wrong too: Core
/// rejects every one but Roman because the rest are legacy CJK code pages that decode to mojibake
/// — and a mojibake <c>/BaseFont</c> is a PDF name object full of bytes needing <c>#xx</c>
/// escaping, which is what readers match on when substituting a missing font.</para>
///
/// <para>Core's own reading has <c>TrueTypeNameRecordSelectionTests</c>, with the same fixture;
/// this is the embedder's.</para>
///
/// <para>These assert on the <b>PDF that comes out</b>, not on the metrics object, because
/// <c>/BaseFont</c> is what a reader acts on and the metrics are an intermediate.</para>
/// </remarks>
public sealed class EmbeddedFontNameRecordTests
{
    private const string Sample = "HeloZ";

    /// <summary>
    /// The Windows English name wins over a Macintosh record listed before it — whether that
    /// Macintosh record is localised (11) or itself English (0).
    /// </summary>
    [Theory]
    [InlineData(11)]  // a localised Macintosh name, listed first
    [InlineData(0)]   // a Macintosh English name, listed first
    public void TheEnglishNameIsEmbedded_NotTheFirstRecordListed(int macLanguageId)
    {
        byte[] font = MinimalTrueType.BuildWithLocalisedNameTable(
            Sample, macFamilyName: "LocalisedFamily", windowsFamilyName: "EnglishFamily",
            macLanguageId: macLanguageId);

        (string baseFont, string fontName) = EmbedAndReadNames(font);

        Assert.Equal("EnglishFamily-PS", baseFont);
        Assert.Equal("EnglishFamily-PS", fontName);
    }

    /// <summary>
    /// The loser must be <b>absent</b>, not merely outranked. Asserting only that the English name
    /// won would still pass if the two were concatenated, or if the localised string leaked into
    /// the other field — the same reason Core's own suite carries this check.
    /// </summary>
    [Fact]
    public void TheLocalisedNameDoesNotAppearInTheEmbeddedFontAtAll()
    {
        byte[] font = MinimalTrueType.BuildWithLocalisedNameTable(
            Sample, macFamilyName: "LocalisedFamily", windowsFamilyName: "EnglishFamily");

        (string baseFont, string fontName) = EmbedAndReadNames(font);

        Assert.DoesNotContain("Localised", baseFont, StringComparison.Ordinal);
        Assert.DoesNotContain("Localised", fontName, StringComparison.Ordinal);
    }

    /// <summary>
    /// The fixture-validity control: an ordinary single-record font is embedded under exactly the
    /// name it declares. Without this, a change that emitted a constant would satisfy both
    /// assertions above.
    /// </summary>
    [Fact]
    public void AnOrdinarySingleRecordFontKeepsItsOwnName()
    {
        byte[] font = MinimalTrueType.BuildNamed(Sample, "PlainFamily");

        (string baseFont, string fontName) = EmbedAndReadNames(font);

        Assert.Equal("PlainFamily", baseFont);
        Assert.Equal("PlainFamily", fontName);
    }

    // ================================================================ helper

    /// <summary>The embedded font's <c>/BaseFont</c> and its descriptor's <c>/FontName</c>.</summary>
    private static (string baseFont, string fontName) EmbedAndReadNames(byte[] font)
    {
        using var input = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();
        PdfFontEmbedder.EmbedFont(input, output, font, "F9");

        using var doc = PdfDocument.Open(new MemoryStream(output.ToArray()));
        var fonts = doc.ResolveObject(doc.Pages[0].Resources?[PdfName.Font]) as PdfDictionary;
        Assert.NotNull(fonts);

        var fontDict = doc.ResolveObject(fonts![new PdfName("F9")]) as PdfDictionary;
        Assert.NotNull(fontDict);

        var descriptor = doc.ResolveObject(fontDict![PdfName.FontDescriptor]) as PdfDictionary;
        Assert.NotNull(descriptor);

        return ((doc.ResolveObject(fontDict[PdfName.BaseFont]) as PdfName)?.Value,
                (doc.ResolveObject(descriptor![PdfName.FontName]) as PdfName)?.Value);
    }

    private static byte[] MinimalPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new System.Collections.Generic.List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << >> >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat("xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets) sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.AppendFormat("trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n",
            offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
