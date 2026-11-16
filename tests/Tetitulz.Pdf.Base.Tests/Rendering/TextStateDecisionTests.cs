// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SkiaSharp;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// Text-state operators the <c>Base/Rendering</c> sweep's batch 10 found no test for, read off where
/// a glyph lands. The font is a Type 3 font whose <c>/a</c> (code 65) is a filled square one em wide
/// and whose code 32 is an empty glyph of the same width, so every position is exact.
/// </summary>
public class TextStateDecisionTests
{
    private static bool Ink(SKBitmap bmp, float x, float y) => PixelAssert.IsInk(PixelAssert.At(bmp, x, y));

    /// <summary><c>TD</c> sets the leading to −ty, so the following <c>T*</c> steps the same distance again.</summary>
    [Fact]
    public void TDSetsTheLeadingTStarUses()
    {
        using var bmp = Render("BT /T3 20 Tf 10 80 Td 0 -30 TD (A) Tj T* (A) Tj ET");

        Assert.True(Ink(bmp, 15, 55), "the first line, at y 50");
        Assert.True(Ink(bmp, 15, 25), "the second line, one leading (30) lower");
    }

    /// <summary><c>T*</c> moves down by <c>TL</c>.</summary>
    [Fact]
    public void TStarMovesDownByTheLeading()
    {
        using var bmp = Render("BT /T3 20 Tf 30 TL 10 80 Td (A) Tj T* (A) Tj ET");

        Assert.True(Ink(bmp, 15, 85), "the first line, at y 80");
        Assert.True(Ink(bmp, 15, 55), "the second line, 30 lower");
    }

    /// <summary>
    /// <c>aw ac string "</c> sets the word spacing before it shows: the glyph after the space lands
    /// <c>Tw</c> = 50 further on, at x 100.
    /// </summary>
    [Fact]
    public void TheDoubleQuoteOperatorSetsTheWordSpacing()
    {
        using var bmp = Render("BT /T3 20 Tf 30 TL 10 80 Td 50 0 (A A) \" ET");

        Assert.True(Ink(bmp, 105, 55));
        Assert.False(Ink(bmp, 55, 55));
    }

    /// <summary>
    /// <c>Tw</c> applies to the single-byte code 32 only (§9.3.3): two A's with no space between stay
    /// side by side whatever the word spacing.
    /// </summary>
    [Fact]
    public void WordSpacingSkipsCodesOtherThanTheSpace()
    {
        using var bmp = Render("BT /T3 20 Tf 10 50 Td 50 Tw (AA) Tj ET");

        Assert.True(Ink(bmp, 35, 55));
        Assert.False(Ink(bmp, 85, 55));
    }

    /// <summary>
    /// The same rule on the per-glyph advance the invisible, CFF and Type 1 runs share (AdvanceTm):
    /// two invisible A's in a Helvetica 600 units wide leave the pen at 10 + 24 = 34 under 50 Tw,
    /// where the visible Type 3 glyph then lands — not at 134.
    /// </summary>
    [Fact]
    public void WordSpacingSkipsOtherCodesOnTheGlyphAdvancePath()
    {
        using var bmp = Render("BT /F1 20 Tf 50 Tw 3 Tr 10 50 Td (AA) Tj /T3 20 Tf 0 Tr (A) Tj ET");

        Assert.True(Ink(bmp, 40, 55));
        Assert.False(Ink(bmp, 140, 55));
    }

    /// <summary><c>BT</c> starts the text matrix afresh: the second object's glyph is at the origin.</summary>
    [Fact]
    public void BTResetsTheTextMatrix()
    {
        using var bmp = Render("BT /T3 20 Tf 10 50 Td (A) Tj ET BT /T3 20 Tf (A) Tj ET");

        Assert.True(Ink(bmp, 5, 5), "the second text object starts at the origin");
    }

    /// <summary>
    /// A <c>TJ</c> number is scaled by <c>Tz</c> like the glyph advance (§9.4.4): at 50 % the
    /// −1000 moves 10, so the second A spans 30 to 40, not 40 to 50.
    /// </summary>
    [Fact]
    public void ATjNumberIsScaledByTheHorizontalScaling()
    {
        using var bmp = Render("BT /T3 20 Tf 50 Tz 10 50 Td [(A) -1000 (A)] TJ ET");

        Assert.True(Ink(bmp, 33, 55));
        Assert.False(Ink(bmp, 45, 55));
    }

    // ---------------------------------------------------------------- fixture

    private static SKBitmap Render(string content)
    {
        string widths = string.Join(" ", Enumerable.Range(32, 34).Select(c => c == 32 || c == 65 ? "100" : "0"));
        const string glyph = "100 0 0 0 100 100 d1 0 0 100 100 re f";
        const string space = "100 0 0 0 0 0 d1";
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents 4 0 R /Resources << /Font << /T3 5 0 R /F1 8 0 R >> >> >>",
            Stream(content),
            "<< /Type /Font /Subtype /Type3 /FontBBox [0 0 100 100] /FontMatrix [0.01 0 0 0.01 0 0]"
                + " /CharProcs << /a 6 0 R /space 7 0 R >> /Encoding << /Type /Encoding /Differences [32 /space 65 /a] >>"
                + " /FirstChar 32 /LastChar 65 /Widths [" + widths + "] >>",
            Stream(glyph),
            Stream(space),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 65 /LastChar 65 /Widths [600] >>",
        };

        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new int[objects.Count];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i + 1).Append(" 0 obj\n").Append(objects[i]).Append("\nendobj\n");
        }
        int xref = sb.Length;
        sb.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
        foreach (int off in offsets) sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);
        return PixelAssert.Render(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    private static string Stream(string data) =>
        string.Format(CultureInfo.InvariantCulture, "<< /Length {0} >>\nstream\n{1}\nendstream", data.Length, data);
}
