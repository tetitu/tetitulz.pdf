// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>The kind of one <see cref="GlyphOutlineSegment"/>.</summary>
    public enum GlyphOutlineSegmentKind
    {
        /// <summary>Starts a subpath at the end point.</summary>
        MoveTo,

        /// <summary>A straight line to the end point.</summary>
        LineTo,

        /// <summary>A cubic Bézier through the two control points to the end point.</summary>
        CurveTo,

        /// <summary>Closes the subpath back to its start.</summary>
        ClosePath,
    }

    /// <summary>One segment of a glyph's outline, in the font's glyph space.</summary>
    public readonly struct GlyphOutlineSegment
    {
        /// <summary>A segment of <paramref name="kind"/>; a move or a line uses the end point (x3, y3) alone.</summary>
        public GlyphOutlineSegment(GlyphOutlineSegmentKind kind, double x1, double y1, double x2, double y2, double x3, double y3)
        {
            Kind = kind; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; X3 = x3; Y3 = y3;
        }

        /// <summary>What the segment draws.</summary>
        public GlyphOutlineSegmentKind Kind { get; }

        /// <summary>A curve's first control point.</summary>
        public double X1 { get; }

        /// <summary>A curve's first control point.</summary>
        public double Y1 { get; }

        /// <summary>A curve's second control point.</summary>
        public double X2 { get; }

        /// <summary>A curve's second control point.</summary>
        public double Y2 { get; }

        /// <summary>The end point.</summary>
        public double X3 { get; }

        /// <summary>The end point.</summary>
        public double Y3 { get; }
    }

    /// <summary>
    /// A glyph's outline and its advance, in the font's glyph space — what
    /// <see cref="Type1.Type1Outlines"/> and <see cref="TrueTypeOutlines"/> both answer.
    /// </summary>
    public sealed class GlyphOutline
    {
        /// <summary>A glyph of <paramref name="segments"/> advancing by <paramref name="advance"/>.</summary>
        public GlyphOutline(IReadOnlyList<GlyphOutlineSegment> segments, double advance)
        {
            Segments = segments;
            Advance = advance;
        }

        /// <summary>The outline, every subpath closed.</summary>
        public IReadOnlyList<GlyphOutlineSegment> Segments { get; }

        /// <summary>The advance along the baseline, in glyph space.</summary>
        public double Advance { get; }
    }
}
