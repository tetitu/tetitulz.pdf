// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// How far one text-showing operation moved the pen, in text space, as the raster renderer ran
    /// it — every glyph's width, the character and word spacing, a <c>TJ</c>'s own numbers — and the
    /// font size and horizontal scaling it ran under.
    /// </summary>
    /// <remarks>
    /// This is what lets a show operation be taken out of a content stream with the text after it
    /// left where it was: a <c>TJ</c> holding one number moves the pen by the same amount
    /// (§9.4.3: <c>tx = −n/1000 × Tfs × Th</c>, or <c>ty = −n/1000 × Tfs</c> down a vertical
    /// line) and touches neither the text line matrix nor anything a reader extracts.
    /// </remarks>
    internal readonly struct ShownTextAdvance
    {
        private ShownTextAdvance(bool known, float tx, float ty, float fontSize, float horizontalScaling)
        {
            Known = known;
            Tx = tx;
            Ty = ty;
            FontSize = fontSize;
            HorizontalScaling = horizontalScaling;
        }

        /// <summary>False when the text matrix had no inverse or a value was not finite: the pen's move cannot be said.</summary>
        internal bool Known { get; }

        /// <summary>The move along the text line, in text space.</summary>
        internal float Tx { get; }

        /// <summary>The move across it — down a vertical line — in text space.</summary>
        internal float Ty { get; }

        internal float FontSize { get; }

        /// <summary>The horizontal scaling as a fraction (100% is 1).</summary>
        internal float HorizontalScaling { get; }

        /// <summary>
        /// The translation, in text space, that takes the text matrix <paramref name="start"/> to
        /// <paramref name="end"/>: <c>end = T(tx, ty) × start</c>.
        /// </summary>
        internal static ShownTextAdvance Between(float[] start, float[] end, float fontSize, float horizontalScaling)
        {
            double a = start[0], b = start[1], c = start[2], d = start[3];
            double dx = (double)end[4] - start[4], dy = (double)end[5] - start[5];
            double det = a * d - b * c;
            double tx = (dx * d - dy * c) / det, ty = (a * dy - b * dx) / det;
            bool known = det != 0 && IsFinite(tx) && IsFinite(ty) && Math.Abs(tx) < float.MaxValue && Math.Abs(ty) < float.MaxValue;
            return known
                ? new ShownTextAdvance(true, (float)tx, (float)ty, fontSize, horizontalScaling)
                : new ShownTextAdvance(false, 0, 0, fontSize, horizontalScaling);
        }

        /// <summary>
        /// This advance with <paramref name="along"/> taken off its move along the line: what is
        /// left of a move the renderer made by a substituted face's measure once the part beyond the
        /// font's own widths is taken away. A vertical line's move is left as it is.
        /// </summary>
        internal ShownTextAdvance Less(float along)
            => !Known || along == 0f || Math.Abs(Ty) > Math.Abs(Tx) || float.IsNaN(along) || float.IsInfinity(along)
                ? this
                : new ShownTextAdvance(true, Tx - along, Ty, FontSize, HorizontalScaling);

        /// <summary>
        /// The number a <c>TJ</c> array holds to move the pen as this operation did, or null when
        /// none does: an unknown move, no move, or a font size or scaling of zero, under which a
        /// number moves nothing.
        /// </summary>
        internal double? AsShowArrayNumber()
        {
            if (!Known) return null;
            // A horizontal line moves along x and a vertical one along y; the larger is the line's.
            bool vertical = Math.Abs(Ty) > Math.Abs(Tx);
            double move = vertical ? Ty : Tx;
            double unit = vertical ? FontSize : (double)FontSize * HorizontalScaling;
            if (move == 0 || unit == 0) return null;
            double n = -move * 1000.0 / unit;
            return IsFinite(n) ? n : (double?)null;
        }

        private static bool IsFinite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
    }
}
