// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Constants
{
    /// <summary>
    /// Text rendering mode, set by the <c>Tr</c> operator (PDF 32000-1:2008, §9.3.6 "Text Rendering Mode").
    ///
    /// <para>An enum rather than a bare <c>int</c> compared against raw literals — <c>&gt;= 4
    /// &amp;&amp; &lt;= 7</c> to mean "adds to the clip path" is unreadable without the spec table
    /// open.</para>
    /// </summary>
    public enum TextRenderMode
    {
        /// <summary>0 — fill the glyphs.</summary>
        Fill = 0,
        /// <summary>1 — stroke the glyph outlines.</summary>
        Stroke = 1,
        /// <summary>2 — fill, then stroke.</summary>
        FillThenStroke = 2,
        /// <summary>3 — draw nothing. Used for the invisible text layer over scanned pages.</summary>
        Invisible = 3,
        /// <summary>4 — fill and add the glyphs to the clip path.</summary>
        FillAndClip = 4,
        /// <summary>5 — stroke and add the glyphs to the clip path.</summary>
        StrokeAndClip = 5,
        /// <summary>6 — fill, stroke, and add the glyphs to the clip path.</summary>
        FillStrokeAndClip = 6,
        /// <summary>7 — add the glyphs to the clip path without painting them.</summary>
        Clip = 7,
    }

    /// <summary>Line cap style, set by the <c>J</c> operator (PDF 32000-1:2008, §8.4.3 "Details of Graphics State Parameters").</summary>
    public enum PdfLineCap
    {
        /// <summary>0 — the stroke stops squarely at the endpoint.</summary>
        Butt = 0,
        /// <summary>1 — a semicircular cap centred on the endpoint.</summary>
        Round = 1,
        /// <summary>2 — a square cap projecting half the line width past the endpoint.</summary>
        ProjectingSquare = 2,
    }

    /// <summary>Line join style, set by the <c>j</c> operator (PDF 32000-1:2008, §8.4.3 "Details of Graphics State Parameters").</summary>
    public enum PdfLineJoin
    {
        /// <summary>0 — extend the outer edges until they meet, subject to the miter limit.</summary>
        Miter = 0,
        /// <summary>1 — round off the corner with an arc.</summary>
        Round = 1,
        /// <summary>2 — cut the corner off with a straight edge.</summary>
        Bevel = 2,
    }

    /// <summary>Helpers for interpreting the graphics-state enums stored as raw integers.</summary>
    public static class PdfGraphicsEnumExtensions
    {
        /// <summary>True when the mode paints glyph interiors.</summary>
        public static bool Fills(this TextRenderMode mode) =>
            mode == TextRenderMode.Fill ||
            mode == TextRenderMode.FillThenStroke ||
            mode == TextRenderMode.FillAndClip ||
            mode == TextRenderMode.FillStrokeAndClip;

        /// <summary>True when the mode paints glyph outlines.</summary>
        public static bool Strokes(this TextRenderMode mode) =>
            mode == TextRenderMode.Stroke ||
            mode == TextRenderMode.FillThenStroke ||
            mode == TextRenderMode.StrokeAndClip ||
            mode == TextRenderMode.FillStrokeAndClip;

        /// <summary>
        /// True when the mode contributes the glyphs to the clip path — modes 4 through 7.
        /// </summary>
        public static bool AddsToClip(this TextRenderMode mode) =>
            mode >= TextRenderMode.FillAndClip && mode <= TextRenderMode.Clip;

        /// <summary>
        /// True when the mode paints nothing at all. Note this is <see cref="TextRenderMode.Invisible"/>
        /// only: mode 7 also paints nothing but still alters the clip, so it is not "invisible".
        /// </summary>
        public static bool IsInvisible(this TextRenderMode mode) => mode == TextRenderMode.Invisible;
    }
}
