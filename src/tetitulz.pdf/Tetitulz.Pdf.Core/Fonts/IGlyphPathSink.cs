// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// Where a charstring evaluator draws a glyph: the four path operations its operators reduce to,
    /// in the charstring's own glyph space. The raster renderer hands a SkiaSharp path builder through
    /// one (Base's <c>SkiaGlyphPathSink</c>); <see cref="GlyphOutlineSink"/> collects a
    /// <see cref="GlyphOutline"/> for a reader outside the renderers.
    /// </summary>
    internal interface IGlyphPathSink
    {
        void MoveTo(float x, float y);
        void LineTo(float x, float y);
        void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3);
        void Close();
    }

    /// <summary>
    /// A <see cref="GlyphOutline"/> collected from a charstring evaluator, each point scaled by one
    /// factor into the glyph space the outline is wanted in.
    /// </summary>
    internal sealed class GlyphOutlineSink : IGlyphPathSink
    {
        private readonly System.Collections.Generic.List<GlyphOutlineSegment> _segments = new System.Collections.Generic.List<GlyphOutlineSegment>();
        private readonly double _scale;

        /// <summary>A sink whose points are multiplied by <paramref name="scale"/>.</summary>
        public GlyphOutlineSink(double scale) => _scale = scale;

        public void MoveTo(float x, float y)
            => _segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.MoveTo, 0, 0, 0, 0, x * _scale, y * _scale));

        public void LineTo(float x, float y)
            => _segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.LineTo, 0, 0, 0, 0, x * _scale, y * _scale));

        public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3)
            => _segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.CurveTo,
                                                     x1 * _scale, y1 * _scale, x2 * _scale, y2 * _scale, x3 * _scale, y3 * _scale));

        public void Close()
            => _segments.Add(new GlyphOutlineSegment(GlyphOutlineSegmentKind.ClosePath, 0, 0, 0, 0, 0, 0));

        /// <summary>The segments collected, with <paramref name="advance"/>.</summary>
        public GlyphOutline ToOutline(double advance) => new GlyphOutline(_segments, advance);
    }
}
