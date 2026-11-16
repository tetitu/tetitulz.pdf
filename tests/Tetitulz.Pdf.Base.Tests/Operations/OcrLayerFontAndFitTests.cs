// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Structure;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The invisible OCR text layer: its composite font embeds a glyphless program — so a layered page
/// can be PDF/A, which embeds every font — and each word is fitted to its box, so the words come back
/// from extraction as words.
/// </summary>
public class OcrLayerFontAndFitTests
{
    /// <summary>The glyphless program is a TrueType font of two empty glyphs, each a full em wide.</summary>
    [Fact]
    public void TheGlyphlessProgram_IsTwoEmptyGlyphsOfOneEm()
    {
        var program = TrueTypeFontProgram.Parse(OcrTextLayerFont.GlyphlessProgram());

        Assert.Equal(2, program.GlyphCount);
        Assert.Equal(1000, program.UnitsPerEm);
        Assert.Equal(1000, program.GetAdvance(1));
    }

    /// <summary>
    /// The composite font embeds the program as <c>/FontFile2</c>, and every code reaches its empty
    /// glyph through <c>/CIDToGIDMap</c>.
    /// </summary>
    [Fact]
    public void TheCompositeFont_EmbedsTheProgram_AndMapsEveryCodeToItsGlyph()
    {
        using var doc = PdfDocument.Open(new MemoryStream(Layered("שלום", "עולם")));
        var font = LayerFont(doc);
        Assert.Equal(PdfName.Type0, font[PdfName.Subtype]);

        var descendant = (PdfDictionary)doc.ResolveObject(((PdfArray)font[PdfName.DescendantFonts])[0]);
        var descriptor = (PdfDictionary)doc.ResolveObject(descendant[PdfName.FontDescriptor]);
        Assert.IsType<PdfStream>(doc.ResolveObject(descriptor[PdfName.FontFile2]));

        byte[] map = ((PdfStream)doc.ResolveObject(descendant[PdfName.CIDToGIDMap])).GetDecodedData();
        Assert.Equal(2 * 65536, map.Length);
        Assert.Equal(new byte[] { 0, 1 }, map.Skip(2 * 0x05E9).Take(2).ToArray());   // shin
    }

    /// <summary>
    /// Each word is fitted to its box along the baseline, so the gap between two boxes is the gap
    /// between the words — and extraction spaces them, through the WinAnsi font and the composite one
    /// alike. In the composite font, whose every glyph is an em wide, a word scaled by its height ran
    /// far past its box into the next.
    /// </summary>
    [Theory]
    [InlineData("Archived", "invoice")]
    [InlineData("שלום", "עולם")]
    public void AnOcrLayersWords_ExtractAsWords(string first, string second)
    {
        using var doc = PdfDocument.Open(new MemoryStream(Layered(first, second)));
        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);

        Assert.Contains(first, text);
        Assert.Contains(second, text);
        Assert.Contains(" ", text.Trim());
    }

    /// <summary>
    /// A second layer on a page that has one keeps the first one's text: each layer's font maps only
    /// the codes it shows, and the second takes a resource name the page does not use — named as the
    /// first one's, it left the first layer's words mapped through its own codes.
    /// </summary>
    [Fact]
    public void ASecondLayer_LeavesTheFirstLayersTextAsItWas()
    {
        byte[] once = Layered("שלום", "עולם");
        using var twice = new MemoryStream();
        PdfOcrLayerWriter.AddLayer(new MemoryStream(once), 0, new List<PdfOcrWord> { new PdfOcrWord("דלק", 10, 20, 40, 12) }, twice);

        using var doc = PdfDocument.Open(new MemoryStream(twice.ToArray()));
        string text = PdfTextExtractor.ExtractText(doc.Pages[0]);
        Assert.Contains("שלום", text);
        Assert.Contains("עולם", text);
        Assert.Contains("דלק", text);
        var fonts = (PdfDictionary)doc.ResolveObject(((PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]))[PdfName.Font]);
        Assert.Equal(new[] { "OcrF1", "OcrF2" }, fonts.Select(kv => kv.Key.Value).Order());
    }

    /// <summary>A blank page with two words laid over it, 40 points wide and 20 points apart.</summary>
    private static byte[] Layered(string first, string second)
    {
        const string blank = "%PDF-1.4\n1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] >>\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n";
        using var output = new MemoryStream();
        PdfOcrLayerWriter.AddLayer(new MemoryStream(Encoding.Latin1.GetBytes(blank)), 0,
            new List<PdfOcrWord> { new PdfOcrWord(first, 10, 50, 40, 12), new PdfOcrWord(second, 70, 50, 40, 12) },
            output);
        return output.ToArray();
    }

    private static PdfDictionary LayerFont(PdfDocument doc)
    {
        var resources = (PdfDictionary)doc.ResolveObject(doc.Pages[0].Dictionary[PdfName.Resources]);
        var fonts = (PdfDictionary)doc.ResolveObject(resources[PdfName.Font]);
        return (PdfDictionary)doc.ResolveObject(fonts.First().Value);
    }
}
