// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Plain extraction drops text drawn again at nearly the same place <b>glyph by glyph</b>, so a copy
/// that is split into different show operators is recognised too (GAPS.md §21).
/// </summary>
/// <remarks>
/// <para><b>The operator-level rule missed the common case.</b>
/// <see cref="PlainExtractionDuplicateTextTests"/> pins a later show operator that repeats an earlier
/// one. But an OCR layer draws its words one operator each over lines the visible layer drew whole,
/// so neither operator's text equals the other's and both came back. Structured extraction already
/// compared per glyph; this extractor advances the text matrix per glyph from the font's widths, so
/// it can index each glyph's position by the same rule, whatever operator boundaries the copy
/// has.</para>
/// <para>Every glyph in these fixtures is 500 units wide, so a 12 pt glyph advances 6 pt and the
/// tolerance is 2 pt — the geometry the operator-level tests use. "Hello World" drawn whole at
/// (72, 700) puts H at 72, the space at 102 and W at 108; drawn word by word, <c>36 0 Td</c> after
/// "Hello" puts W at 108 too, because <c>Td</c> moves the line matrix, not the text matrix.</para>
/// <para>Line endings are normalised in the harness: the extractor writes
/// <c>Environment.NewLine</c>, and these assertions are about which glyphs survive, not about the
/// platform.</para>
/// </remarks>
public sealed class PlainExtractionGlyphDuplicateTests
{
    private const string WholeLine  = "BT /F1 12 Tf 72 700 Td (Hello World) Tj ET";
    private const string WordByWord = "BT /F1 12 Tf 72 700 Td (Hello) Tj 36 0 Td (World) Tj ET";

    /// <summary>
    /// A simple font naming none of the standard 14, with no <c>/Widths</c>: the extractor's
    /// 500-unit fallback advance rather than its width table.
    /// </summary>
    private const string WidthlessFont = "<< /Type /Font /Subtype /Type1 /BaseFont /Wingdings-Regular >>";

    /// <summary>
    /// Identity-H — two bytes per code, every CID 500 wide, no <c>/ToUnicode</c>, so each code is
    /// its own character. Object 5 is the Type 0 wrapper, object 6 its descendant.
    /// </summary>
    private static readonly string[] CompositeFont =
    {
        "<< /Type /Font /Subtype /Type0 /BaseFont /Test /Encoding /Identity-H /DescendantFonts [6 0 R] >>",
        "<< /Type /Font /Subtype /CIDFontType2 /BaseFont /Test /DW 500 >>",
    };

    // ---------------------------------------------------------------------------------------------
    //  A copy split into different operators
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void ALineDrawnWholeThenRedrawnWordByWord_IsExtractedOnce()
        => Assert.Equal("Hello World", Extract(WholeLine + "\n" + WordByWord));

    [Fact]
    public void ALineDrawnWordByWordThenRedrawnWhole_IsExtractedOnce()
        => Assert.Equal("Hello World", Extract(WordByWord + "\n" + WholeLine));

    [Fact]
    public void ARotatedLineDrawnWholeThenWordByWord_IsExtractedOnce()
    {
        // A quarter turn puts every advance on the page's Y axis; the glyph positions and the
        // tolerance are measured in device space, so the copy is still found.
        string text = Extract("q 0 1 -1 0 300 100 cm\n" + WholeLine + "\n" + WordByWord + "\nQ");

        Assert.Equal("Hello World", text);
    }

    [Fact]
    public void ARotatedTextMatrixLineDrawnWholeThenWordByWord_IsExtractedOnce()
    {
        // The quarter turn is in Tm itself this time, so the advance has to be carried through the
        // text matrix (§9.4.4: Tm ← [1 0 0 1 tx 0] × Tm) to climb the page: W lands at y = 736.
        // Tm to a different y reads as a new line, which the trailing trim removes.
        string text = Extract(
            "BT /F1 1 Tf 0 12 -12 0 72 700 Tm (Hello World) Tj ET\n" +
            "BT /F1 1 Tf 0 12 -12 0 72 700 Tm (Hello) Tj 0 12 -12 0 72 736 Tm (World) Tj ET");

        Assert.Equal("Hello World", text);
    }

    [Fact]
    public void AScaledTextMatrixLineDrawnWholeThenWordByWord_IsExtractedOnce()
    {
        // 1 pt type scaled twelvefold by Tm: the same eleven device positions as WholeLine, reached
        // through the text matrix rather than the font size. Tm on the same line inserts nothing.
        string text = Extract(
            "BT /F1 1 Tf 12 0 0 12 72 700 Tm (Hello World) Tj ET\n" +
            "BT /F1 1 Tf 12 0 0 12 72 700 Tm (Hello) Tj 12 0 0 12 108 700 Tm (World) Tj ET");

        Assert.Equal("Hello World", text);
    }

    [Fact]
    public void ACompositeFontLineDrawnWholeThenWordByWord_IsExtractedOnce()
    {
        // Identity-H: the per-glyph text has to be cut at the CMap's two-byte code boundaries.
        const string hello = "00480065006C006C006F", space = "0020", world = "0057006F0072006C0064";
        string text = Extract(
            $"BT /F1 12 Tf 72 700 Td <{hello}{space}{world}> Tj ET\n" +
            $"BT /F1 12 Tf 72 700 Td <{hello}> Tj 36 0 Td <{world}> Tj ET",
            CompositeFont);

        Assert.Equal("Hello World", text);
    }

    [Fact]
    public void AFontWithoutWidthsDrawnWholeThenWordByWord_IsExtractedOnce()
        => Assert.Equal("Hello World", Extract(WholeLine + "\n" + WordByWord, WidthlessFont));

    // ---------------------------------------------------------------------------------------------
    //  Controls: the same words genuinely repeated are kept
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void TheSameWordsDrawnAgainOnTheNextLine_AreKept()
        => Assert.Equal("Hello World\nHello World",
            Extract(WholeLine + "\nBT /F1 12 Tf 72 680 Td (Hello) Tj 36 0 Td (World) Tj ET"));

    [Fact]
    public void TheSameWordDrawnAgainFurtherAlongTheLine_IsKept()
    {
        // "Hello" lands on its copy; "World" lands 42 pt past where its copy would sit. The space
        // between them is the Td word gap, which is emitted whether or not the glyphs survive.
        string text = Extract(WholeLine + "\nBT /F1 12 Tf 72 700 Td (Hello) Tj 78 0 Td (World) Tj ET");

        Assert.Equal("Hello World\n World", text);
    }

    // ---------------------------------------------------------------------------------------------
    //  The rule's parameters, per glyph
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("73.9", "PEC_CH00")]            // 1.9 pt aside: within a third of a 6 pt glyph
    [InlineData("74.1", "PEC_CH00PEC_CH00")]    // 2.1 pt aside: kept, and Tm inserts no separator
    public void AGlyphsToleranceIsMeasuredInDeviceSpace(string secondX, string expected)
    {
        // 1 pt type under a twelvefold Tm: a tolerance taken from the text-space advance would be
        // a sixth of a point and find no copy at all.
        string text = Extract(
            "BT /F1 1 Tf 12 0 0 12 72 700 Tm (PEC_CH00) Tj ET\n" +
            $"BT /F1 1 Tf 12 0 0 12 {secondX} 700 Tm (PEC_CH00) Tj ET");

        Assert.Equal(expected, text);
    }

    [Fact]
    public void AKernSpaceInsideACopiedArray_IsDroppedWithTheGlyphBeforeIt()
    {
        // The word space a TJ kern stands for belongs to the glyph before it: kept with a kept
        // glyph, dropped with a dropped one. "X" follows the copy on the same line so that a space
        // wrongly kept cannot hide in the trailing whitespace the extractor trims.
        string text = Extract(
            "BT /F1 12 Tf 72 700 Td [(Hello) -300 (World)] TJ ET\n" +
            "BT /F1 12 Tf 72 700 Td [(Hello) -300 (World)] TJ (X) Tj ET");

        Assert.Equal("Hello World\nX", text);
    }

    // ---------------------------------------------------------------------------------------------
    //  Harness
    // ---------------------------------------------------------------------------------------------

    private static string HelveticaWithEveryGlyph500Wide()
    {
        var widths = new StringBuilder();
        for (int c = 32; c <= 126; c++) widths.Append("500 ");
        return "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding " +
               $"/FirstChar 32 /LastChar 126 /Widths [{widths}] >>";
    }

    /// <summary>
    /// One page drawing <paramref name="content"/> with <c>/F1</c> bound to object 5, which is the
    /// first of <paramref name="fontObjects"/> (Helvetica, every glyph 500 wide, when none is given).
    /// </summary>
    private static string Extract(string content, params string[] fontObjects)
    {
        if (fontObjects.Length == 0) fontObjects = new[] { HelveticaWithEveryGlyph500Wide() };

        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R " +
            "/Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
        };
        bodies.AddRange(fontObjects);

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[bodies.Count + 1];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i + 1] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        for (int i = 1; i <= bodies.Count; i++) sb.AppendFormat("{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\n")
          .AppendFormat("startxref\n{0}\n%%EOF\n", xref);

        using var doc = PdfDocument.Open(new MemoryStream(Encoding.ASCII.GetBytes(sb.ToString())));
        return PdfTextExtractor.ExtractText(doc.Pages[0]).Replace("\r\n", "\n");
    }
}
