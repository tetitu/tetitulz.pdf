// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Fonts.Cff;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Evaluates a CFF Type 2 charstring and produces an <see cref="SKPath"/>
    /// together with the glyph's advance width — Core's <see cref="Type2Charstrings"/>
    /// drawing into a SkiaSharp path.
    /// Reference: Adobe Technical Note #5177 "Type 2 Charstring Format".
    /// </summary>
    internal static class Type2CharstringEvaluator
    {
        internal struct GlyphOutline
        {
            public SKPath Path;
            /// <summary>Advance width in charspace units (1/1000 em).</summary>
            public float  AdvanceWidth;
            /// <summary>
            /// Horizontal stem zones (y0, height) in charspace units.  Populated
            /// from the charstring's hstem / hstemhm operators for use by the
            /// grid-fitter (Type 1 / TrueType hinting).  Empty list when the
            /// charstring had no horizontal hints.
            /// </summary>
            public IReadOnlyList<(float y0, float h)> HorizontalStems;
            /// <summary>
            /// The page's charstring budget stopped this glyph short (<c>CharstringWork</c>): the outline
            /// is partial, and is not the glyph's.
            /// </summary>
            public bool CutByBudget;
            /// <summary>The charstring steps the evaluation ran.</summary>
            public int Steps;
        }

        /// <summary>
        /// Evaluates the charstring for a given GID and returns the outline.
        /// Returns an empty outline on error.
        /// </summary>
        public static GlyphOutline Evaluate(CffFont font, int gid)
        {
            var builder = new SKPathBuilder();
            // Use EvenOdd fill: CFF spec recommends nonzero-winding, but EvenOdd is safer
            // in practice because some fonts have subtly inconsistent contour winding and
            // because SkiaSharp's winding rule can produce unfilled holes when inner contours
            // happen to wind in the same direction as the outer contour after transformation.
            builder.FillType = SKPathFillType.EvenOdd;

            Type2Charstrings.Result result = Type2Charstrings.Evaluate(font, gid, new SkiaGlyphPathSink(builder));

            // Snapshot rather than Detach: the snapshot is an independent SKPath, so the
            // builder can be disposed here and the caller owns only what it was handed.
            SKPath outline = builder.Snapshot();
            builder.Dispose();

            return new GlyphOutline
            {
                Path = outline,
                AdvanceWidth = result.AdvanceWidth,
                HorizontalStems = result.HorizontalStems,
                CutByBudget = result.CutByBudget,
                Steps = result.Steps,
            };
        }
    }

    /// <summary>A charstring evaluator's path operations onto a SkiaSharp path builder.</summary>
    internal sealed class SkiaGlyphPathSink : Core.Fonts.IGlyphPathSink
    {
        private readonly SKPathBuilder _path;

        public SkiaGlyphPathSink(SKPathBuilder path) => _path = path;

        public void MoveTo(float x, float y) => _path.MoveTo(x, y);
        public void LineTo(float x, float y) => _path.LineTo(x, y);
        public void CubicTo(float x1, float y1, float x2, float y2, float x3, float y3) => _path.CubicTo(x1, y1, x2, y2, x3, y3);
        public void Close() => _path.Close();
    }
}
