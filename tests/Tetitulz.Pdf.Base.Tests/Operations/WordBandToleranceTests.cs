// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The tolerances that decide what shares a line and where a word ends — the decisions a sweep of
/// <see cref="PdfStructuredTextExtractor"/> found standing, because every existing fixture sits in
/// the middle of their range rather than at an edge.
/// </summary>
/// <remarks>
/// <para><b>A tolerance is only observable at its boundary.</b> Ordinary 12 pt prose clears the line
/// band by a mile and its spaces sit squarely between two glyph centres, so the floor, the band
/// widening and the strict comparison are all free to be wrong. Each test here places one glyph
/// just inside an edge and pairs it with the control that would fire if the rule were merely
/// absent.</para>
///
/// <para><b>The font carries explicit <c>/Widths</c>,</b> so every position below is arithmetic
/// rather than the extractor's 0.6-em fallback for a font it cannot measure.</para>
/// </remarks>
public class WordBandToleranceTests
{
    /// <summary>
    /// <b>The 2 pt floor under the line band.</b> At 3 pt type the proportional tolerance is
    /// 1.5 pt, and two glyphs 1.8 pt apart are one line only because of the floor.
    /// </summary>
    /// <remarks>
    /// Small type is not exotic — footnotes, table footers and legal boilerplate live here, and
    /// without the floor each glyph of a slightly uneven baseline becomes its own line, which is
    /// what every consumer of <c>Lines</c> then reports.
    /// </remarks>
    [Fact]
    public void AtSmallTypeTheLineBandFloorKeepsAnUnevenBaselineTogether()
    {
        // maxH = 3, so the proportional band is 3 × 0.5 = 1.5 and the floor raises it to 2.
        var page = Extract("BT /F1 3 Tf 10 101.8 Td (A) Tj ET BT /F1 3 Tf 11.8 100 Td (B) Tj ET");

        PdfTextLine line = Assert.Single(page.Lines);
        Assert.Equal("AB", string.Concat(line.Words.Select(w => w.Text)));
    }

    /// <summary>
    /// The control for the row above: 2.5 pt apart is outside the floor as well, so it really is
    /// two lines and the test above is pinning the floor rather than an extractor that never splits.
    /// </summary>
    [Fact]
    public void AtSmallTypeAGlyphBeyondTheFloorIsItsOwnLine()
    {
        var page = Extract("BT /F1 3 Tf 10 102.5 Td (A) Tj ET BT /F1 3 Tf 11.8 100 Td (B) Tj ET");

        Assert.Equal(2, page.Lines.Count);
    }

    /// <summary>
    /// <b>A blank glyph slightly off the line's baseline still ends the word</b>, because the band
    /// the blank index searches is widened by the same tolerance the line grouping used.
    /// </summary>
    /// <remarks>
    /// A producer that draws its spaces from a different font, or with a text rise, puts them a
    /// fraction off the baseline of the letters around them. Without the widening the blank is not
    /// considered to be on the line at all and the two words are reported as one.
    /// </remarks>
    [Fact]
    public void ABlankSlightlyOffTheBaselineStillEndsTheWord()
    {
        // 12 pt: the band is ±6, and the blank sits 3 pt above the letters' baseline. The letters
        // are 0.5 pt apart, far inside the gap threshold, so only the blank can split them.
        var page = Extract(
            "BT /F1 12 Tf 10 100 Td (A) Tj ET " +
            "BT /F1 12 Tf 15 103 Td ( ) Tj ET " +
            "BT /F1 12 Tf 16.5 100 Td (B) Tj ET");

        PdfTextLine line = Assert.Single(page.Lines);
        Assert.Equal(new[] { "A", "B" }, line.Words.Select(w => w.Text));
    }

    /// <summary>
    /// <b>The same 2 pt floor, in the blank index.</b> At 3 pt type a blank 1.8 pt off the baseline
    /// is inside the band only because of the floor.
    /// </summary>
    [Fact]
    public void AtSmallTypeTheBlankBandFloorStillFindsTheSpace()
    {
        var page = Extract(
            "BT /F1 3 Tf 10 100 Td (A) Tj ET " +
            "BT /F1 3 Tf 11.2 101.8 Td ( ) Tj ET " +
            "BT /F1 3 Tf 11.8 100 Td (B) Tj ET");

        PdfTextLine line = Assert.Single(page.Lines);
        Assert.Equal(new[] { "A", "B" }, line.Words.Select(w => w.Text));
    }

    /// <summary>
    /// <b>"Strictly between" means strictly.</b> A blank whose centre coincides exactly with a
    /// glyph's centre is drawn <i>on</i> that glyph, not between it and its neighbour, so it
    /// separates nothing.
    /// </summary>
    /// <remarks>
    /// The numbers are exact in binary: at 12 pt the 500/1000 'B' is 6 pt wide from x = 16.5 so its
    /// centre is 19.5, and the 250/1000 blank is 3 pt wide from x = 18 so its centre is 19.5 too.
    /// An inclusive comparison splits "AB" in half on a page that shows one word.
    /// </remarks>
    [Fact]
    public void ABlankCentredExactlyOnAGlyphDoesNotSplitTheWord()
    {
        var page = Extract(
            "BT /F1 12 Tf 10 100 Td (A) Tj ET " +
            "BT /F1 12 Tf 18 100 Td ( ) Tj ET " +
            "BT /F1 12 Tf 16.5 100 Td (B) Tj ET");

        PdfTextLine line = Assert.Single(page.Lines);
        Assert.Equal("AB", Assert.Single(line.Words).Text);
    }

    /// <summary>
    /// <b>Rotation is quantised to the nearest quadrant, not truncated towards zero.</b> Text at
    /// 89° is upright-ish in no sense: it is a quarter turn, and truncation calls it 0°.
    /// </summary>
    /// <remarks>
    /// The value feeds <c>IsUpright</c>, which decides whether a glyph joins the upright line
    /// clusters or the rotated ones — so truncating puts a near-vertical run on the same lines as
    /// the horizontal text around it, and the duplicate-copy rule compares its rotation against
    /// theirs.
    /// </remarks>
    [Fact]
    public void ARotationJustShyOfAQuadrantIsRoundedToIt()
    {
        // cos 89° ≈ 0.017452, sin 89° ≈ 0.999848.
        var page = Extract("BT /F1 12 Tf 0.017452 0.999848 -0.999848 0.017452 100 100 Tm (A) Tj ET");

        PdfTextChar glyph = Assert.Single(page.Chars);
        Assert.Equal(90f, glyph.Rotation);
        Assert.False(glyph.IsUpright, "a quarter-turned glyph is not upright");
    }

    // ------------------------------------------------------------------

    private static PdfPageTextLayout Extract(string content)
    {
        using var ms = new MemoryStream(Pdf(content), writable: false);
        return PdfStructuredTextExtractor.Extract(ms, 0);
    }

    private static byte[] Pdf(string content)
    {
        // /FirstChar 32 .. /LastChar 66: the space, 'A' and 'B' are the only codes used.
        var widths = new int[66 - 32 + 1];
        widths[0] = 250;                  // space
        widths[65 - 32] = 500;            // 'A'
        widths[66 - 32] = 500;            // 'B'
        string w = string.Join(" ", widths);

        return Build(new[]
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 300] /Contents 4 0 R "
                + "/Resources << /Font << /F1 5 0 R >> >> >>\nendobj\n",
            $"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica "
                + $"/FirstChar 32 /LastChar 66 /Widths [{w}] >>\nendobj\n",
        });
    }

    private static byte[] Build(string[] objects)
    {
        var acc = new List<byte>();
        var offsets = new List<int>();
        void Emit(string s) => acc.AddRange(Encoding.Latin1.GetBytes(s));

        Emit("%PDF-1.7\n");
        foreach (var o in objects) { offsets.Add(acc.Count); Emit(o); }

        int xref = acc.Count;
        int size = offsets.Count + 1;
        Emit($"xref\n0 {size}\n0000000000 65535 f \n");
        foreach (int o in offsets) Emit($"{o:D10} 00000 n \n");
        Emit($"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return acc.ToArray();
    }
}
