// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Page layouts built word by word, for the invoice tests — the shape the structured extractor
/// hands over, without a PDF and a font per case.
/// </summary>
/// <remarks>
/// <para><b>Glyphs are laid where the page draws them.</b> The extractor reads reading order from
/// glyph positions, so each word carries its characters: a Hebrew word right to left (its first
/// letter right-most), a number or a Latin word left to right — inside a right-to-left line too,
/// as a number is drawn there.</para>
/// <para><b>A line is a list of tokens.</b> Words follow one another with <see cref="WordGap"/>
/// between them; the token <see cref="Column"/> puts <see cref="ColumnGap"/> there instead, which
/// is the space between two fields or two table columns.</para>
/// </remarks>
internal static class InvoiceLayouts
{
    /// <summary>The token that separates two columns.</summary>
    internal const string Column = "|";

    internal const float CharWidth = 6f;
    internal const float Height = 10f;
    internal const float WordGap = 4f;
    internal const float ColumnGap = 48f;

    // Drawn right to left: a word with Hebrew letters and no Latin letter or digit — "ח.פ." and
    // "מס'" included, whose punctuation is half their characters.
    internal static bool IsRtlText(string text)
        => text.Any(c => c >= 'א' && c <= 'ת') && !text.Any(c => c < 0x80 && char.IsLetterOrDigit(c));

    /// <summary>A word whose glyphs sit where a page draws them.</summary>
    internal static PdfTextWord Word(string text, float x, float y)
    {
        bool rtl = IsRtlText(text);
        float width = CharWidth * text.Length;
        var chars = new List<PdfTextChar>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            float cx = x + (rtl ? text.Length - 1 - i : i) * CharWidth;
            chars.Add(new PdfTextChar(text[i].ToString(), cx, y, CharWidth, Height, rtl));
        }
        return new PdfTextWord(text, x, y, width, Height, chars, rtl);
    }

    /// <summary>
    /// A word whose glyphs are given in visual order, left to right: a word mixing directions, or
    /// with a glyph of several characters, which <see cref="Word"/> cannot lay out.
    /// </summary>
    internal static PdfTextWord VisualWord(string text, float x, float y, bool rtl, params string[] glyphsLeftToRight)
    {
        var chars = new List<PdfTextChar>();
        for (int i = 0; i < glyphsLeftToRight.Length; i++)
            chars.Add(new PdfTextChar(glyphsLeftToRight[i], x + i * CharWidth, y, CharWidth, Height, rtl));
        return new PdfTextWord(text, x, y, CharWidth * glyphsLeftToRight.Length, Height, chars, rtl);
    }

    /// <summary>
    /// <paramref name="line"/> with each word that mixes Hebrew letters and digits — <c>ב-17%</c> —
    /// drawn as a right-to-left line draws it: its characters in the bidi algorithm's visual order,
    /// <c>17%-ב</c>. <see cref="Word"/> lays such a word out as written, and a reader keys that as
    /// <c>17%-ב</c>.
    /// </summary>
    internal static PdfTextLine Drawn(PdfTextLine line)
    {
        var words = line.Words.Select(w =>
            w.Text.Any(c => c >= 'א' && c <= 'ת') && w.Text.Any(char.IsDigit)
                ? VisualWord(w.Text, w.X, w.Y, rtl: true,
                             UnicodeBidi.Reorder(w.Text, BaseDirection.RightToLeft).Select(c => c.ToString()).ToArray())
                : w).ToList();
        return new PdfTextLine(line.Text, line.X, line.Y, line.Width, line.Height, words, line.IsRtl);
    }

    /// <summary>A right-to-left line whose first token's right edge is <paramref name="right"/>.</summary>
    internal static PdfTextLine Rtl(float y, float right, params string[] tokens)
    {
        var words = new List<PdfTextWord>();
        float edge = right;
        bool gapPending = false;
        foreach (string token in tokens)
        {
            if (token == Column) { edge -= ColumnGap; gapPending = false; continue; }
            if (gapPending) edge -= WordGap;
            float width = CharWidth * token.Length;
            words.Add(Word(token, edge - width, y));
            edge -= width;
            gapPending = true;
        }
        return new PdfTextLine(string.Join(" ", tokens.Where(t => t != Column)),
                               words.Min(w => w.X), y, right - words.Min(w => w.X), Height, words, isRtl: true);
    }

    /// <summary>A left-to-right line whose first token's left edge is <paramref name="left"/>.</summary>
    internal static PdfTextLine Ltr(float y, float left, params string[] tokens)
    {
        var words = new List<PdfTextWord>();
        float edge = left;
        bool gapPending = false;
        foreach (string token in tokens)
        {
            if (token == Column) { edge += ColumnGap; gapPending = false; continue; }
            if (gapPending) edge += WordGap;
            words.Add(Word(token, edge, y));
            edge += CharWidth * token.Length;
            gapPending = true;
        }
        return new PdfTextLine(string.Join(" ", tokens.Where(t => t != Column)),
                               left, y, edge - left, Height, words, isRtl: false);
    }

    /// <summary>
    /// A right-to-left table row: cell <c>i</c>'s words end at <paramref name="rightEdges"/>[i],
    /// so the columns line up from row to row as a table's do.
    /// </summary>
    internal static PdfTextLine RtlRow(float y, float[] rightEdges, params string[] cells)
    {
        var words = new List<PdfTextWord>();
        for (int c = 0; c < cells.Length; c++)
        {
            float edge = rightEdges[c];
            bool first = true;
            foreach (string token in cells[c].Split(' '))
            {
                if (token.Length == 0) continue;
                if (!first) edge -= WordGap;
                float width = CharWidth * token.Length;
                words.Add(Word(token, edge - width, y));
                edge -= width;
                first = false;
            }
        }
        float left = words.Min(w => w.X), right = words.Max(w => w.X + w.Width);
        return new PdfTextLine(string.Join(" ", cells), left, y, right - left, Height, words, isRtl: true);
    }

    /// <summary>
    /// A left-to-right table row: cell <c>i</c>'s words start at <paramref name="leftEdges"/>[i],
    /// the English twin of <see cref="RtlRow"/>.
    /// </summary>
    internal static PdfTextLine LtrRow(float y, float[] leftEdges, params string[] cells)
    {
        var words = new List<PdfTextWord>();
        for (int c = 0; c < cells.Length; c++)
        {
            float edge = leftEdges[c];
            bool first = true;
            foreach (string token in cells[c].Split(' '))
            {
                if (token.Length == 0) continue;
                if (!first) edge += WordGap;
                words.Add(Word(token, edge, y));
                edge += CharWidth * token.Length;
                first = false;
            }
        }
        float left = words.Min(w => w.X), right = words.Max(w => w.X + w.Width);
        return new PdfTextLine(string.Join(" ", cells), left, y, right - left, Height, words, isRtl: false);
    }

    internal static PdfPageTextLayout Page(int number, params PdfTextLine[] lines)
        => new PdfPageTextLayout(number, lines);

    internal static IReadOnlyList<PdfPageTextLayout> Pages(params PdfPageTextLayout[] pages) => pages;
}
