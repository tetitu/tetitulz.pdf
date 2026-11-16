// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;

namespace Tetitulz.Pdf.Base.Operations.Extraction
{
    /// <summary>
    /// Joins a line's <b>text</b> to the <b>glyphs</b> that produced it, so a match found in the
    /// string can be turned back into a page-space rectangle.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation, because two would drift exactly where it matters.</b> This was
    /// private inside <c>PdfTextRedactor</c> and typed to redaction; the autolinker needs the same
    /// join, and re-writing it is this repository's named recurring defect. The map is subtle in two
    /// places, and both are where a re-implementation goes wrong:</para>
    /// <list type="number">
    ///   <item>A glyph may carry a <b>multi-character cluster</b>, so every string position it
    ///   occupies must map back to the same glyph — otherwise a match's start index names the wrong
    ///   glyph from the first ligature onwards.</item>
    ///   <item>The synthetic inter-word separator maps to <c>null</c>, and a caller unioning boxes
    ///   must <b>skip</b> it rather than treat it as a zero-width box at the origin, which stretches
    ///   every multi-word match back to the bottom-left corner of the page.</item>
    /// </list>
    ///
    /// <para><b>The box is baseline→ascender, with no descender.</b>
    /// <c>PdfTextChar.Y</c> is the baseline origin, so that is the convention both callers get —
    /// which is the point of sharing rather than each choosing.</para>
    /// </remarks>
    internal static class TextLineGlyphMap
    {
        /// <summary>
        /// A line's text together with, for each character position, the glyph that produced it —
        /// <c>null</c> for the space this inserts between words.
        /// </summary>
        /// <param name="line">The extracted line.</param>
        /// <remarks>
        /// Built here rather than read from <c>PdfTextLine.Text</c>, because the map is the whole
        /// point: a match's position in the string has to name the glyphs it covers, and a string
        /// alone cannot say which. The separator matches what the extractor puts in <c>Text</c>, so
        /// a caller searching the extracted text finds the same matches here.
        /// </remarks>
        internal static (string Text, PdfTextChar[] Map) Build(PdfTextLine line)
        {
            var sb = new StringBuilder();
            var map = new List<PdfTextChar>();
            if (line?.Words == null) return (string.Empty, new PdfTextChar[0]);

            for (int w = 0; w < line.Words.Count; w++)
            {
                if (w > 0) { sb.Append(' '); map.Add(null); }

                var word = line.Words[w];
                for (int c = 0; c < word.Chars.Count; c++)
                {
                    var glyph = word.Chars[c];
                    string s = glyph.Char ?? string.Empty;

                    for (int i = 0; i < s.Length; i++) map.Add(glyph);
                    sb.Append(s);
                }
            }

            return (sb.ToString(), map.ToArray());
        }

        /// <summary>
        /// The union of the boxes of the glyphs a match covers, in page space.
        /// </summary>
        /// <param name="map">From <see cref="Build"/>.</param>
        /// <param name="start">Character index of the match.</param>
        /// <param name="length">Character length of the match.</param>
        /// <param name="x0">Left edge.</param>
        /// <param name="y0">Bottom edge (the baseline).</param>
        /// <param name="x1">Right edge.</param>
        /// <param name="y1">Top edge (the ascender).</param>
        /// <returns>False when the range covers no glyph at all, in which case there is no box.</returns>
        internal static bool TryUnionBox(
            PdfTextChar[] map, int start, int length,
            out float x0, out float y0, out float x1, out float y1)
        {
            x0 = float.MaxValue; y0 = float.MaxValue;
            x1 = float.MinValue; y1 = float.MinValue;
            bool any = false;
            if (map == null) return false;

            for (int i = start; i < start + length && i < map.Length; i++)
            {
                PdfTextChar glyph = i >= 0 ? map[i] : null;
                if (glyph == null) continue;      // the synthetic word separator — see the remarks

                any = true;
                if (glyph.X < x0) x0 = glyph.X;
                if (glyph.Y < y0) y0 = glyph.Y;
                if (glyph.X + glyph.Width > x1) x1 = glyph.X + glyph.Width;
                if (glyph.Y + glyph.Height > y1) y1 = glyph.Y + glyph.Height;
            }

            return any;
        }
    }
}
