// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Base.Operations.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Fonts;

/// <summary>
/// Embedding a font takes its advance widths from whichever <c>cmap</c> subtable the font actually
/// ships — not only from Windows Unicode BMP.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>PdfFontEmbedder</c> carried its own <c>cmap</c> reader that
/// recognised <c>(3,1)</c>, <c>(0,3)</c> and <c>(0,4)</c>, took the first match, and parsed format 4
/// alone. For anything else it silently mapped nothing — and <c>GetWidth</c>'s fallback for an
/// unmapped code point is <c>.notdef</c>'s advance. So the font embedded, the PDF was valid, the
/// glyphs drew, and <b>every advance in <c>/Widths</c> was wrong</b>. Nothing throws, nothing warns,
/// and the page simply lays out with the wrong metrics.</para>
///
/// <para><b>The three unsupported kinds are all ordinary fonts.</b> <c>(3,0)</c> Microsoft Symbol
/// is what marlett, symbol, wingdings, webdings and most icon webfonts ship; <c>(1,0)</c> Macintosh
/// Roman is common in older faces; <c>(3,10)</c> Windows UCS-4 is normal for anything with astral
/// coverage. <c>Core.Fonts.TrueTypeFontProgram</c> had already been taught all of them, each with a
/// comment describing this exact damage — the fix never reached the copy one folder away, which is
/// the "landed one level short" pattern CLAUDE.md records.</para>
///
/// <para><b>Why the obvious fixture cannot witness this.</b> <c>MinimalTrueType.Build(string)</c>
/// gives every glyph the same 600-unit advance, so a reader that found the subtable and one that
/// found nothing produce identical <c>/Widths</c> — the test passes either way. These use
/// <c>BuildWithDistinctWidths</c>, where <c>.notdef</c> is 100 and glyph <i>i</i> is 610 + 10(i−1),
/// so the array says which glyph was looked up. That trap is named in
/// <c>MinimalTrueType.NotdefAdvance</c>'s own remarks and it is the reason this went unseen.</para>
/// </remarks>
public sealed class EmbeddedCmapSubtableTests
{
    private const string Sample = "HeloZ";

    /// <summary>
    /// Every cmap kind the font may ship yields the <b>same</b> widths, because they all describe
    /// the same glyphs. <c>(3,1)</c> is the control — it worked before and must still.
    /// </summary>
    [Theory]
    [InlineData(3, 1)]    // Windows Unicode BMP — the only kind that used to work
    [InlineData(3, 0)]    // Microsoft Symbol — marlett, symbol, wingdings, webdings, icon fonts
    [InlineData(1, 0)]    // Macintosh Roman
    [InlineData(3, 10)]   // Windows UCS-4 — normal for astral coverage
    public void AFontsAdvancesAreReadWhicheverCmapSubtableItShips(int platformId, int encodingId)
    {
        int[] widths = EmbedAndReadWidths(
            MinimalTrueType.BuildWithDistinctWidths(Sample, platformId, encodingId));

        // /Widths starts at char 32. The fixture assigns glyph i the advance 610 + 10*(i-1) in
        // ascending CODE POINT order — H(72), Z(90), e(101), l(108), o(111) — not in the order the
        // characters were passed. A reader that found no subtable leaves every one of these at
        // .notdef's 100, so each assertion below is a separate witness that the lookup happened.
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance, WidthOf(widths, 'H'));
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance + 10, WidthOf(widths, 'Z'));
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance + 20, WidthOf(widths, 'e'));
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance + 30, WidthOf(widths, 'l'));
        Assert.Equal(MinimalTrueType.MappedGlyphAdvance + 40, WidthOf(widths, 'o'));
    }

    /// <summary>
    /// The floor, and what makes the theory above a claim about <i>lookup</i> rather than about
    /// widths in general: a character the font does not map still gets <c>.notdef</c>'s advance.
    /// Without this, a change that returned <see cref="MinimalTrueType.MappedGlyphAdvance"/> for
    /// everything would satisfy every assertion above.
    /// </summary>
    [Fact]
    public void AnUnmappedCharacterStillFallsBackToNotdef()
    {
        int[] widths = EmbedAndReadWidths(
            MinimalTrueType.BuildWithDistinctWidths(Sample, 3, 0));

        Assert.Equal(MinimalTrueType.NotdefAdvance, WidthOf(widths, 'Q'));   // not in "HeloZ"
        Assert.Equal(MinimalTrueType.NotdefAdvance, WidthOf(widths, '#'));
    }

    // ================================================================ helpers

    /// <summary>The <c>/Widths</c> entry for <paramref name="c"/>; the array starts at char 32.</summary>
    private static int WidthOf(int[] widths, char c)
    {
        int index = c - 32;
        Assert.InRange(index, 0, widths.Length - 1);
        return widths[index];
    }

    private static int[] EmbedAndReadWidths(byte[] font)
    {
        using var input = new MemoryStream(MinimalPdf());
        using var output = new MemoryStream();
        PdfFontEmbedder.EmbedFont(input, output, font, "F9");

        string text = Encoding.ASCII.GetString(output.ToArray());
        Match m = Regex.Match(text, @"/Widths\s*\[([^\]]*)\]");
        Assert.True(m.Success, "the embedded font carries no /Widths array");

        return m.Groups[1].Value
                .Split(new[] { ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.Parse(s, CultureInfo.InvariantCulture))
                .ToArray();
    }

    private static byte[] MinimalPdf()
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append(body); }

        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R "
          + "/Resources << >> >>\nendobj\n");
        Obj("4 0 obj\n<< /Length 0 >>\nstream\n\nendstream\nendobj\n");

        int xref = sb.Length;
        sb.AppendFormat(CultureInfo.InvariantCulture, "xref\n0 {0}\n0000000000 65535 f \n", offsets.Count + 1);
        foreach (int off in offsets)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", off);
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "trailer\n<< /Size {0} /Root 1 0 R >>\nstartxref\n{1}\n%%EOF\n", offsets.Count + 1, xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
