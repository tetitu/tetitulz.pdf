// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Text;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Text;

/// <summary>
/// Plain extraction reads a gap left by a <c>Tm</c> as a space between words, as it reads a wide
/// <c>TJ</c> kern: a producer that places each word by its own text matrix — an OCR layer, which is
/// how a flattened page keeps its text — leaves no space character and no kern between them.
/// </summary>
/// <remarks>
/// Every glyph is 500 units wide, so at 12 pt "Hello" drawn from x 72 ends at 102 and the gap
/// threshold, a fifth of an em, is 2.4 pt.
/// </remarks>
public sealed class PlainExtractionWordGapTests
{
    [Fact]
    public void AWordPlacedAGapAwayByTm_IsPrecededByASpace()
        => Assert.Equal("Hello World", Extract("BT /F1 12 Tf 1 0 0 1 72 700 Tm (Hello) Tj 1 0 0 1 108 700 Tm (World) Tj ET"));

    /// <summary>Text that already ends in a space gets no second one from the gap after it.</summary>
    [Fact]
    public void TextEndingInASpace_GetsNoSecond()
        => Assert.Equal("Hello World", Extract("BT /F1 12 Tf 1 0 0 1 72 700 Tm (Hello ) Tj 1 0 0 1 114 700 Tm (World) Tj ET"));

    /// <summary>A jump narrower than the threshold is kerning, not a word break.</summary>
    [Fact]
    public void ANarrowJump_IsNoSpace()
        => Assert.Equal("HelloWorld", Extract("BT /F1 12 Tf 1 0 0 1 72 700 Tm (Hello) Tj 1 0 0 1 103 700 Tm (World) Tj ET"));

    /// <summary>
    /// The gap is measured from where the last text ended, which a new text object does not reset:
    /// each word in its own <c>BT</c>, as an OCR layer writes them, is still spaced.
    /// </summary>
    [Fact]
    public void AGapAcrossTextObjects_IsMeasuredFromTheLastTextsEnd()
    {
        Assert.Equal("Hello World", Extract(
            "BT /F1 12 Tf 1 0 0 1 72 700 Tm (Hello) Tj ET\nBT /F1 12 Tf 1 0 0 1 108 700 Tm (World) Tj ET"));
        Assert.Equal("HelloWorld", Extract(
            "BT /F1 12 Tf 1 0 0 1 72 700 Tm (Hello) Tj ET\nBT /F1 12 Tf 1 0 0 1 103 700 Tm (World) Tj ET"));
    }

    /// <summary>Text placed back over or before what came before is no gap at all.</summary>
    [Fact]
    public void AJumpBackwards_IsNoSpace()
        => Assert.Equal("HelloWorld", Extract("BT /F1 12 Tf 1 0 0 1 72 700 Tm (Hello) Tj 1 0 0 1 60 700 Tm (World) Tj ET"));

    /// <summary>
    /// The gap is a fraction of the em the text is drawn at, the text matrix's scale included: 1 pt
    /// type under a twelvefold Tm is 12 pt on the page, so a 1 pt gap is a twelfth of an em — no
    /// space — where the font size alone would make it five ems.
    /// </summary>
    [Fact]
    public void TheThreshold_IsInEmsOfTheTextAsDrawn()
    {
        Assert.Equal("HelloWorld", Extract("BT /F1 1 Tf 12 0 0 12 72 700 Tm (Hello) Tj 12 0 0 12 103 700 Tm (World) Tj ET"));
        Assert.Equal("Hello World", Extract("BT /F1 1 Tf 12 0 0 12 72 700 Tm (Hello) Tj 12 0 0 12 108 700 Tm (World) Tj ET"));
    }

    /// <summary>
    /// A rotated line does not run along x, so this rule reads no gap across it: what the extractor
    /// made of a quarter-turned pair before — a line break, from the move up the page — is what it
    /// makes of it still.
    /// </summary>
    [Fact]
    public void RotatedText_IsLeftAsItWas()
        => Assert.Equal("Hello\nWorld", Extract("BT /F1 12 Tf 0 1 -1 0 72 700 Tm (Hello) Tj 0 1 -1 0 72 736 Tm (World) Tj ET"));

    /// <summary>
    /// A <c>Td</c> moves from the start of the line, so its operand counts the glyphs already
    /// shown. A producer that writes a kerning pair as a move — a browser's PDF does, for every
    /// pair it kerns — lands the rest of the word where the first letter ended, a point short of
    /// it here: one word, where the operand alone (5 pt, past three tenths of the font size)
    /// reads a space into the middle of it.
    /// </summary>
    [Fact]
    public void AKerningPairWrittenAsATd_IsOneWord()
        => Assert.Equal("PAYROLL", Extract("BT /F1 12 Tf 72 700 Td (P) Tj 5 0 Td (AYROLL) Tj ET"));

    /// <summary>A move that lands exactly where the last glyph ended continues the word.</summary>
    [Fact]
    public void ATdLandingWhereTheLastGlyphEnded_IsNoSpace()
        => Assert.Equal("HelloWorld", Extract("BT /F1 12 Tf 72 700 Td (Hello) Tj 30 0 Td (World) Tj ET"));

    /// <summary>The control: a move that leaves a real gap after the last glyph is still a space.</summary>
    [Fact]
    public void AWordPlacedAGapAwayByTd_IsPrecededByASpace()
        => Assert.Equal("Hello World", Extract("BT /F1 12 Tf 72 700 Td (Hello) Tj 36 0 Td (World) Tj ET"));

    /// <summary>A drawn space followed by a move to its end is one space, not two.</summary>
    [Fact]
    public void ATdAfterADrawnSpace_AddsNoSecond()
        => Assert.Equal("Hello World", Extract("BT /F1 12 Tf 72 700 Td (Hello ) Tj 36 0 Td (World) Tj ET"));

    /// <summary>
    /// A move that lands far back over the line — more than an em before where the last glyph
    /// ended, which no kern does — is not a continuation of the word, and its operand decides as
    /// it does where nothing can be measured.
    /// </summary>
    [Fact]
    public void ATdFarBackOverTheLine_IsReadByItsOperand()
        => Assert.Equal("Hello World Again X", Extract("BT /F1 12 Tf 72 700 Td (Hello World Again) Tj 10 0 Td (X) Tj ET"));

    /// <summary>
    /// A rotated line does not run along x, so the gap is not measured across it and the operand
    /// decides: a move of six glyph widths past five glyphs reads as a space.
    /// </summary>
    [Fact]
    public void ATdOnARotatedLine_IsReadByItsOperand()
        => Assert.Equal("Hello World", Extract("BT /F1 12 Tf 0 1 -1 0 72 700 Tm (Hello) Tj 36 0 Td (World) Tj ET"));

    /// <summary>
    /// A move on another line than the last glyph's is not measured against it either: the x of
    /// where a line above ended says nothing about a gap on this one.
    /// </summary>
    [Fact]
    public void ATdOnAnotherLineThanTheLastGlyph_IsReadByItsOperand()
        => Assert.Equal("Hello\n World", Extract(
            "BT /F1 12 Tf 1 0 0 1 72 700 Tm (Hello) Tj ET\nBT /F1 12 Tf 1 0 0 1 72 650 Tm 30 0 Td (World) Tj ET"));

    private static string Extract(string content)
    {
        string widths = string.Join(" ", Enumerable.Repeat("500", 95));
        var bodies = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream",
            $"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 126 /Widths [{widths}] >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets.Add(sb.Length);
            sb.Append(i + 1).Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(bodies.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int o in offsets) sb.AppendFormat("{0:D10} 00000 n \n", o);
        sb.Append("trailer\n<< /Size ").Append(bodies.Count + 1).Append(" /Root 1 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");

        using var doc = PdfDocument.Open(new MemoryStream(Encoding.Latin1.GetBytes(sb.ToString())));
        return PdfTextExtractor.ExtractText(doc.Pages[0]).Replace(Environment.NewLine, "\n").Trim();
    }
}
