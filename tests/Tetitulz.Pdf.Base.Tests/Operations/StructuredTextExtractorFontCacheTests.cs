// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// <c>PdfStructuredTextExtractor.EmitString</c> takes its width metrics from the per-page
/// <c>FontCache</c> the walk already carries, instead of building a fresh <c>PdfFont</c> for
/// every shown string. These tests pin the output that must not move across that change:
/// the decoded text, and the page-space coordinate of every character.
///
/// <para>
/// Three properties are separable, and each needs its own fixture, because a wrong shared
/// font still produces a perfectly well-formed layout:
/// </para>
/// <list type="number">
///   <item><description>Each font resource keeps <b>its own</b> <c>/Widths</c>. Two fonts on one
///   page whose widths differ by 4x place their second glyph 7.5 pt apart; a cache that handed
///   one font's metrics to the other would still emit both strings, at the wrong X.</description></item>
///   <item><description>A resource name the page's <c>/Font</c> dictionary does not define
///   still emits its text, at the 0.6-em approximation — <b>not</b> at some other font's
///   widths, and not at a <c>FallbackFont</c>'s.</description></item>
///   <item><description>A Form XObject whose own <c>/Resources</c> maps the <b>same name</b>
///   <c>/F1</c> to a <b>different</b> font gets the form's widths. This is the property the
///   change actually put at risk: the fresh-per-string construction could not get it wrong,
///   whereas a cache can — it is why <c>FontCache.MakeKey</c> mixes the resource dictionary's
///   identity into the key.</description></item>
/// </list>
/// </summary>
public class StructuredTextExtractorFontCacheTests
{
    // Every fixture below shows "AB" at 10 pt, so each font's second glyph sits exactly
    // (width/1000 * 10) points right of its first. The three widths are chosen far enough
    // apart that no rounding tolerance can confuse them.
    private const float FontSize = 10f;
    private const float WideAdvance   = 10f;   // /Widths 1000 -> 1000/1000 * 10
    private const float NarrowAdvance = 2.5f;  // /Widths  250 ->  250/1000 * 10
    private const float NoFontAdvance = 6f;    // no font dict -> 0.6f * 10 (EmitString's approximation)

    // ------------------------------------------------------------------
    //  Fixture: one page, two page-level fonts, one undefined font name,
    //  and a Form XObject that redefines /F1 to a third font.
    // ------------------------------------------------------------------

    private static byte[] BuildPdf()
    {
        const string formContent = "BT /F1 10 Tf 1 0 0 1 300 500 Tm (AB) Tj ET\n";

        string content =
            "BT /F1 10 Tf 1 0 0 1 50 700 Tm (AB) Tj ET\n" +   // page /F1  — widths 1000
            "BT /F2 10 Tf 1 0 0 1 50 650 Tm (AB) Tj ET\n" +   // page /F2  — widths 250
            "BT /F3 10 Tf 1 0 0 1 50 600 Tm (AB) Tj ET\n" +   // /F3 is not in /Font at all
            "BT /F1 10 Tf 1 0 0 1 50 550 Tm (AB) Tj ET\n" +   // page /F1 again — second cache hit
            "q /Fm1 Do Q\n";                                  // form /F1 — widths 250, /Courier

        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");

        int obj1 = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        int obj2 = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        int obj3 = sb.Length;
        sb.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792]"
                + " /Contents 4 0 R /Resources << /Font << /F1 5 0 R /F2 6 0 R >>"
                + " /XObject << /Fm1 7 0 R >> >> >>\nendobj\n");

        int obj4 = sb.Length;
        sb.AppendFormat("4 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(content), content);

        int obj5 = sb.Length;
        sb.Append("5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica"
                + " /FirstChar 65 /LastChar 66 /Widths [1000 1000] >>\nendobj\n");

        int obj6 = sb.Length;
        sb.Append("6 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman"
                + " /FirstChar 65 /LastChar 66 /Widths [250 250] >>\nendobj\n");

        int obj7 = sb.Length;
        sb.AppendFormat("7 0 obj\n<< /Type /XObject /Subtype /Form /BBox [0 0 612 792]"
                      + " /Resources << /Font << /F1 8 0 R >> >> /Length {0} >>\n"
                      + "stream\n{1}\nendstream\nendobj\n",
            Encoding.ASCII.GetByteCount(formContent), formContent);

        int obj8 = sb.Length;
        sb.Append("8 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Courier"
                + " /FirstChar 65 /LastChar 66 /Widths [250 250] >>\nendobj\n");

        int xref = sb.Length;
        sb.Append("xref\n0 9\n0000000000 65535 f \n");
        foreach (int off in new[] { obj1, obj2, obj3, obj4, obj5, obj6, obj7, obj8 })
            sb.AppendFormat("{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size 9 /Root 1 0 R >>\n");
        sb.AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static IReadOnlyList<PdfTextChar> AllChars()
    {
        using var ms = new MemoryStream(BuildPdf());
        var page = PdfStructuredTextExtractor.Extract(ms)[0];
        return page.Lines.SelectMany(l => l.Words).SelectMany(w => w.Chars).ToList();
    }

    /// <summary>The two characters shown at the given baseline Y, ordered left to right.</summary>
    private static IReadOnlyList<PdfTextChar> AtBaseline(float y)
        => AllChars().Where(c => System.Math.Abs(c.Y - y) < 0.5f)
                     .OrderBy(c => c.X).ToList();

    // ==================================================================
    //  1. Every font on the page keeps its own /Widths
    // ==================================================================

    [Fact]
    public void TwoFontsOnOnePage_EachPlacesItsSecondGlyphAtItsOwnAdvance()
    {
        var wide   = AtBaseline(700f);   // /F1, /Widths 1000
        var narrow = AtBaseline(650f);   // /F2, /Widths 250

        Assert.Equal(new[] { "A", "B" }, wide.Select(c => c.Char));
        Assert.Equal(new[] { "A", "B" }, narrow.Select(c => c.Char));

        // Both strings start at x = 50; only the advance differs, and it is the font's own.
        Assert.Equal(50f,                 wide[0].X,   precision: 3);
        Assert.Equal(50f + WideAdvance,   wide[1].X,   precision: 3);
        Assert.Equal(50f,                 narrow[0].X, precision: 3);
        Assert.Equal(50f + NarrowAdvance, narrow[1].X, precision: 3);

        // The reported box is the same width as the advance the metrics gave.
        Assert.Equal(WideAdvance,   wide[0].Width,   precision: 3);
        Assert.Equal(NarrowAdvance, narrow[0].Width, precision: 3);

        // And each character is attributed to its own /BaseFont.
        Assert.Equal("Helvetica",   wide[0].FontName);
        Assert.Equal("Times-Roman", narrow[0].FontName);
    }

    // ==================================================================
    //  2. A font resource the page never defines
    // ==================================================================

    [Fact]
    public void UndefinedFontResourceName_PlacesGlyphsAtTheSixTenthsEmApproximation()
    {
        // /F3 appears in no /Font dictionary, so EmitString has no PdfFont at all and falls
        // back to 0.6 em. The value must be that approximation — not 0, not another font's
        // width, and not the 0 that FontCache's FallbackFont would report for an
        // unresolvable dictionary.
        var chars = AtBaseline(600f);

        Assert.Equal(new[] { "A", "B" }, chars.Select(c => c.Char));
        Assert.Equal(50f,                 chars[0].X, precision: 3);
        Assert.Equal(50f + NoFontAdvance, chars[1].X, precision: 3);
        Assert.Equal(NoFontAdvance,       chars[0].Width, precision: 3);

        // With no dictionary to read /BaseFont from, the resource key is reported instead.
        Assert.Equal("F3", chars[0].FontName);
    }

    // ==================================================================
    //  3. A Form XObject that redefines the same resource name
    // ==================================================================

    [Fact]
    public void FormXObjectRedefiningF1_UsesTheFormsWidthsNotThePages()
    {
        // The page showed /F1 (widths 1000) four lines earlier, so the cache is already
        // populated for the name "F1". The form's /Resources map "F1" to a 250-wide Courier;
        // a cache keyed on the name alone would place the second glyph at 310.
        var chars = AtBaseline(500f);

        Assert.Equal(new[] { "A", "B" }, chars.Select(c => c.Char));
        Assert.Equal(300f,                 chars[0].X, precision: 3);
        Assert.Equal(300f + NarrowAdvance, chars[1].X, precision: 3);
        Assert.Equal("Courier",            chars[0].FontName);

        // Stated as the inequality it is really about: the page's /F1 advance must not leak in.
        Assert.NotEqual(300f + WideAdvance, chars[1].X);
    }

    // ==================================================================
    //  4. The second use of a cached font is identical to the first
    // ==================================================================

    [Fact]
    public void SameFontShownTwiceOnOnePage_ProducesIdenticalGeometryBothTimes()
    {
        var first  = AtBaseline(700f);
        var second = AtBaseline(550f);   // the same /F1, now served from the cache entry

        Assert.Equal(first.Select(c => c.Char),     second.Select(c => c.Char));
        Assert.Equal(first[1].X - first[0].X,       second[1].X - second[0].X, precision: 3);
        Assert.Equal(first[0].Width,                second[0].Width,           precision: 3);
        Assert.Equal(first[0].FontName,             second[0].FontName);
        Assert.Equal(WideAdvance,                   second[1].X - second[0].X, precision: 3);
    }

    // ==================================================================
    //  5. The whole page at once — text and line order unchanged
    // ==================================================================

    [Fact]
    public void WholePage_ReportsFiveLinesOfABInTopToBottomOrder()
    {
        using var ms = new MemoryStream(BuildPdf());
        var page = PdfStructuredTextExtractor.Extract(ms)[0];

        Assert.Equal(new[] { "AB", "AB", "AB", "AB", "AB" }, page.Lines.Select(l => l.Text));

        float[] expectedY = { 700f, 650f, 600f, 550f, 500f };
        Assert.Equal(expectedY.Length, page.Lines.Count);
        for (int i = 0; i < expectedY.Length; i++)
            Assert.Equal(expectedY[i], page.Lines[i].Y, precision: 3);

        Assert.Null(PdfStructuredTextExtractor.LastError);
    }
}
