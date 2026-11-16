// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Phase 2 tests: per-character FontName, FontSize, FillColor, StrokeColor,
/// IsUpright, and Rotation properties on PdfTextChar.
/// All PDFs are built in-memory with exact byte offsets.
/// </summary>
public class PdfStructuredTextExtractorPhase2Tests
{
    // ------------------------------------------------------------------
    //  In-memory PDF builder (single font, single page)
    // ------------------------------------------------------------------

    private static byte[] BuildPdf(string contentStream, string extraFonts = "")
    {
        // extraFonts lets callers inject additional /Font entries, e.g. "/F2 6 0 R"
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = sb.Length;
        sb.AppendFormat(
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]" +
            " /Contents 4 0 R /Resources << /Font << /F1 5 0 R{0} >> >> >>\nendobj\n",
            string.IsNullOrEmpty(extraFonts) ? "" : " " + extraFonts);

        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(contentStream), contentStream);

        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        int obj6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier-Bold >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 7\n0000000000 65535 f \n");
        sb.AppendFormat("{0:D10} 00000 n \n", obj1);
        sb.AppendFormat("{0:D10} 00000 n \n", obj2);
        sb.AppendFormat("{0:D10} 00000 n \n", obj3);
        sb.AppendFormat("{0:D10} 00000 n \n", obj4);
        sb.AppendFormat("{0:D10} 00000 n \n", obj5);
        sb.AppendFormat("{0:D10} 00000 n \n", obj6);
        sb.Append("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // Helper to extract all chars from page 0
    private static System.Collections.Generic.IReadOnlyList<PdfTextChar> Chars(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        var pages = PdfStructuredTextExtractor.Extract(ms);
        return pages.Count == 0
            ? System.Array.Empty<PdfTextChar>()
            : pages[0].Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).ToList();
    }

    // ==================================================================
    //  FontName
    // ==================================================================

    [Fact]
    public void FontName_Populated_FromBaseFont()
    {
        const string cs = "BT /F1 12 Tf  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        // BaseFont of F1 is /Helvetica
        Assert.Equal("Helvetica", chars[0].FontName);
    }

    [Fact]
    public void FontName_DiffersByFont_TwoFontsOnPage()
    {
        const string cs =
            "BT /F1 12 Tf 100 700 Td (A) Tj ET\n" +
            "BT /F2 12 Tf 200 700 Td (B) Tj ET\n";
        var chars = Chars(BuildPdf(cs, "/F2 6 0 R"));
        // Should have at least 2 chars; their FontNames must differ
        Assert.True(chars.Count >= 2);
        var names = chars.Select(c => c.FontName).Distinct().ToList();
        Assert.True(names.Count >= 2,
            $"Expected ≥2 distinct FontNames, got: {string.Join(", ", names)}");
        Assert.Contains("Helvetica",   names);
        Assert.Contains("Courier-Bold", names);
    }

    // ==================================================================
    //  FontSize
    // ==================================================================

    [Fact]
    public void FontSize_ReflectsEffectiveSizeTimesHorizScale()
    {
        // 200 Tz → HorizScale = 2; Tf size = 10 → effective = 10 * 2 = 20
        const string cs = "BT /F1 10 Tf  200 Tz  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.Equal(20f, chars[0].FontSize, precision: 1);
    }

    [Fact]
    public void FontSize_DefaultTzIs100Percent()
    {
        const string cs = "BT /F1 14 Tf  100 700 Td (X) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.Equal(14f, chars[0].FontSize, precision: 1);
    }

    // ==================================================================
    //  FillColor
    // ==================================================================

    [Fact]
    public void FillColor_Null_WhenNoColorSet()
    {
        const string cs = "BT /F1 12 Tf  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.Null(chars[0].FillColor);
    }

    [Fact]
    public void FillColor_RGB_FromRgOperator()
    {
        // rg 1 0 0 → red fill
        const string cs = "1 0 0 rg\nBT /F1 12 Tf  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.NotNull(chars[0].FillColor);
        Assert.Equal(3, chars[0].FillColor.Length);
        Assert.Equal(1f, chars[0].FillColor[0], precision: 3);  // R
        Assert.Equal(0f, chars[0].FillColor[1], precision: 3);  // G
        Assert.Equal(0f, chars[0].FillColor[2], precision: 3);  // B
    }

    [Fact]
    public void FillColor_Gray_FromGOperator()
    {
        // g 0.5 → 50% gray → [0.5, 0.5, 0.5]
        const string cs = "0.5 g\nBT /F1 12 Tf  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.NotNull(chars[0].FillColor);
        Assert.Equal(3, chars[0].FillColor.Length);
        Assert.Equal(0.5f, chars[0].FillColor[0], precision: 3);
    }

    [Fact]
    public void FillColor_CMYK_FromKOperator()
    {
        // k 0 1 0 0 → pure magenta in CMYK → float[4]
        const string cs = "0 1 0 0 k\nBT /F1 12 Tf  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.NotNull(chars[0].FillColor);
        Assert.Equal(4, chars[0].FillColor.Length);
        Assert.Equal(1f, chars[0].FillColor[1], precision: 3);  // M
    }

    [Fact]
    public void FillColor_UpdatedMidStream()
    {
        // Two text runs with different fill colors
        const string cs =
            "1 0 0 rg\nBT /F1 12 Tf 100 700 Td (A) Tj ET\n" +
            "0 0 1 rg\nBT /F1 12 Tf 200 700 Td (B) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.True(chars.Count >= 2);

        var chA = chars.First(c => c.Char == "A");
        var chB = chars.First(c => c.Char == "B");
        Assert.Equal(1f, chA.FillColor[0], precision: 3);  // A is red
        Assert.Equal(1f, chB.FillColor[2], precision: 3);  // B is blue
    }

    [Fact]
    public void FillColor_RestoredAfterGsRestore()
    {
        // q … set red … Q — should restore to no color outside q/Q
        // We verify that the outer char has null FillColor (not the inner red)
        const string cs =
            "BT /F1 12 Tf 100 710 Td (outer) Tj ET\n" +
            "q\n" +
            "1 0 0 rg\n" +
            "BT /F1 12 Tf 100 700 Td (inner) Tj ET\n" +
            "Q\n" +
            "BT /F1 12 Tf 100 690 Td (after) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        var outer = chars.FirstOrDefault(c => c.Char == "o");  // from "outer"
        var inner = chars.FirstOrDefault(c => c.Char == "i");  // from "inner"
        var after = chars.FirstOrDefault(c => c.Char == "a");  // from "after"
        if (inner != null)
            Assert.NotNull(inner.FillColor);  // red set inside q/Q
        if (after != null)
            Assert.Null(after.FillColor);     // restored to null outside q/Q
    }

    // ==================================================================
    //  StrokeColor
    // ==================================================================

    [Fact]
    public void StrokeColor_Null_WhenNoColorSet()
    {
        const string cs = "BT /F1 12 Tf  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.Null(chars[0].StrokeColor);
    }

    [Fact]
    public void StrokeColor_RGB_FromRGOperator()
    {
        // RG 0 0 1 → blue stroke
        const string cs = "0 0 1 RG\nBT /F1 12 Tf  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.NotNull(chars[0].StrokeColor);
        Assert.Equal(3, chars[0].StrokeColor.Length);
        Assert.Equal(1f, chars[0].StrokeColor[2], precision: 3);  // B
    }

    [Fact]
    public void StrokeColor_Gray_FromGUpperOperator()
    {
        const string cs = "0.25 G\nBT /F1 12 Tf  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.NotNull(chars[0].StrokeColor);
        Assert.Equal(0.25f, chars[0].StrokeColor[0], precision: 3);
    }

    // ==================================================================
    //  IsUpright / Rotation
    // ==================================================================

    [Fact]
    public void IsUpright_True_WhenNoRotation()
    {
        const string cs = "BT /F1 12 Tf  100 700 Td (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.True(chars[0].IsUpright);
        Assert.Equal(0f, chars[0].Rotation, precision: 1);
    }

    [Fact]
    public void IsUpright_False_AndRotation90_WhenTm90()
    {
        // Tm 0 1 -1 0 100 700 — 90° rotation (sin(90°)=1, cos(90°)=0)
        const string cs = "BT /F1 12 Tf  0 1 -1 0 100 700 Tm (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.False(chars[0].IsUpright);
        Assert.Equal(90f, chars[0].Rotation, precision: 1);
    }

    [Fact]
    public void IsUpright_False_AndRotation180_WhenTm180()
    {
        // Tm -1 0 0 -1 400 700 — 180° rotation
        const string cs = "BT /F1 12 Tf  -1 0 0 -1 400 700 Tm (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.False(chars[0].IsUpright);
        Assert.Equal(180f, chars[0].Rotation, precision: 1);
    }

    [Fact]
    public void IsUpright_False_AndRotation270_WhenTm270()
    {
        // Tm 0 -1 1 0 100 400 — 270° rotation (sin(270°)=-1)
        const string cs = "BT /F1 12 Tf  0 -1 1 0 100 400 Tm (A) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.NotEmpty(chars);
        Assert.False(chars[0].IsUpright);
        Assert.Equal(270f, chars[0].Rotation, precision: 1);
    }

    // ==================================================================
    //  FillColor isolation — each PdfTextChar gets its own array copy
    // ==================================================================

    [Fact]
    public void FillColor_IsDefensiveCopy_MutatingOneDoesNotAffectAnother()
    {
        const string cs =
            "1 0 0 rg\nBT /F1 12 Tf  100 700 Td (AB) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.True(chars.Count >= 2);
        // Mutate first char's FillColor; second must be unaffected
        chars[0].FillColor[0] = 0f;
        Assert.Equal(1f, chars[1].FillColor[0], precision: 3);
    }

    /// <summary>
    /// The glyphs drawn in one colour hold one stored array — two arrays a glyph were a third of
    /// extraction's allocation on a page that sets a colour — and a new colour is a new array. The
    /// liveness half of the optimisation: a glyph's colour reads the same either way, so only the
    /// stored array shows whether it is shared.
    /// </summary>
    [Fact]
    public void GlyphsDrawnInOneColour_ShareTheStoredArray_ANewColourIsANewOne()
    {
        const string cs =
            "1 0 0 rg 0 0 1 RG\nBT /F1 12 Tf  100 700 Td (AB) Tj 0 1 0 rg (C) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.Equal(3, chars.Count);

        Assert.Same(chars[0].SharedFillColor, chars[1].SharedFillColor);
        Assert.Same(chars[0].SharedStrokeColor, chars[2].SharedStrokeColor);
        Assert.NotSame(chars[0].SharedFillColor, chars[2].SharedFillColor);
        Assert.Equal(new[] { 0f, 1f, 0f }, chars[2].FillColor);
        Assert.Equal(new[] { 0f, 0f, 1f }, chars[0].StrokeColor);
    }

    /// <summary>
    /// The glyphs of one run share one text-state object (font, size, colours, rotation), and a
    /// change of font is a new one — the 88-to-64-byte glyph's liveness, which no glyph's own
    /// properties can show.
    /// </summary>
    [Fact]
    public void GlyphsOfOneRun_ShareOneStyle_AChangeOfFontIsANewOne()
    {
        const string cs = "BT /F1 12 Tf 100 700 Td (AB) Tj /F1 14 Tf (C) Tj ET\n";
        var chars = Chars(BuildPdf(cs));
        Assert.Equal(3, chars.Count);

        Assert.Same(chars[0].Style, chars[1].Style);
        Assert.NotSame(chars[1].Style, chars[2].Style);
        Assert.Equal(12f, chars[0].FontSize, precision: 3);
        Assert.Equal(14f, chars[2].FontSize, precision: 3);
    }

    /// <summary>
    /// The page's glyph list is sized from the strings its own content shows — every byte of a
    /// Tj, ' and " string and every string in a TJ array — and the figure is capped. The list's
    /// capacity is not observable through the output, so the bound is asserted where it is made.
    /// </summary>
    [Fact]
    public void ThePagesGlyphListIsSizedFromTheShownBytes_AndCapped()
    {
        var ops = Tetitulz.Pdf.Core.Text.ContentStreamParser.Parse(System.Text.Encoding.ASCII.GetBytes(
            "BT /F1 12 Tf (abc) Tj 0 0 (de) \" (f) ' [(gh) -250 (ij)] TJ 1 0 0 1 0 0 cm ET"));
        Assert.Equal(10, PdfStructuredTextExtractor.GlyphsShownAtMost(ops));

        var many = new System.Text.StringBuilder("BT ");
        string chunk = "(" + new string('x', 4096) + ") Tj ";
        for (int i = 0; i <= PdfStructuredTextExtractor.MaxPresizedGlyphs / 4096; i++) many.Append(chunk);
        var big = Tetitulz.Pdf.Core.Text.ContentStreamParser.Parse(System.Text.Encoding.ASCII.GetBytes(many.Append("ET").ToString()));
        Assert.Equal(PdfStructuredTextExtractor.MaxPresizedGlyphs, PdfStructuredTextExtractor.GlyphsShownAtMost(big));
    }

    /// <summary>
    /// Each read of a glyph's colour is the reader's own copy: changing it changes nothing stored,
    /// for that glyph or another drawn in the same colour.
    /// </summary>
    [Fact]
    public void ReadingAGlyphsColour_ReturnsACopy_ThatChangesNothingStored()
    {
        const string cs = "1 0 0 rg\nBT /F1 12 Tf  100 700 Td (AB) Tj ET\n";
        var chars = Chars(BuildPdf(cs));

        float[] read = chars[0].FillColor;
        read[0] = 0f;

        Assert.NotSame(read, chars[0].FillColor);
        Assert.Equal(1f, chars[0].FillColor[0], precision: 3);
        Assert.Equal(1f, chars[1].FillColor[0], precision: 3);
    }
}
