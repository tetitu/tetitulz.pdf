// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The path-construction operators Type 1 and Type 2 charstrings share — <c>rlineto</c>,
    /// <c>hlineto</c>/<c>vlineto</c>, <c>rrcurveto</c>, and <c>vhcurveto</c>/<c>hvcurveto</c>.
    ///
    /// <para>Both evaluators implemented all five, identically apart from the name of their state
    /// class. The interpreters themselves stay separate — Type 1 and Type 2 differ in almost
    /// everything else, from operand encoding to hinting to subroutine biasing — but these five
    /// draw the same geometry from the same operands in both — Base's Type 1 evaluator and Core's
    /// <see cref="Cff.Type2Charstrings"/> — into an <see cref="IGlyphPathSink"/>.</para>
    /// </summary>
    /// <remarks>
    /// <b>Pure statics over the pieces of state they touch</b>, because the two evaluators' state
    /// classes are private and sealed and cannot be shared. <c>X</c> and <c>Y</c> are fields on
    /// both, so they pass by <c>ref</c> directly.
    ///
    /// <para><b>No static fields, so no type initializer</b> — a leaf by construction, like
    /// <c>Type3Encoding</c> and <c>SvgSyntax</c>. Pointing one evaluator at the other's static
    /// would have been the peer-to-peer edge that crashed the Base test host and silently dropped
    /// ~190 tests.</para>
    ///
    /// <para><b>The extra-argument branch is unreachable from Type 1 and is kept anyway.</b> TN
    /// #5015 gives <c>vhcurveto</c>/<c>hvcurveto</c> exactly four arguments, so <c>n % 4 != 0</c>
    /// is false and the branch never fires for a Type 1 charstring; Type 2 (TN #5177 §4.4) allows
    /// the trailing endpoint correction. That is why the two bodies could be merged without a
    /// separating input, and it is stated here rather than left for the next reader to re-derive.
    /// The Type 1 arity restriction is enforced in NEITHER copy today, before or after this merge;
    /// if it is ever wanted, this is the one place it can live.</para>
    /// </remarks>
    internal static class CharstringPath
    {
        /// <summary>
        /// <c>rlineto</c> — relative line segments, two operands each.
        /// </summary>
        internal static void RLineto(IGlyphPathSink path, ref float x, ref float y, List<float> stack)
        {
            int n = stack.Count;
            for (int i = 0; i + 1 < n; i += 2)
            {
                x += stack[i];
                y += stack[i + 1];
                path.LineTo(x, y);
            }
        }

        /// <summary>
        /// <c>hlineto</c> / <c>vlineto</c> — alternating horizontal and vertical segments, one
        /// operand each, starting on the axis <paramref name="horizontalFirst"/> names.
        /// </summary>
        /// <remarks>
        /// Type 1 had this as two methods with a plain alternating loop; Type 2 had one
        /// parameterised method whose loop carried a dead <c>if</c> and a toggle guard that could
        /// not change the result, since the loop exits immediately after. The plain form is kept.
        /// </remarks>
        internal static void AlternatingLineto(
            IGlyphPathSink path, ref float x, ref float y, List<float> stack, bool horizontalFirst)
        {
            bool horizontal = horizontalFirst;
            int n = stack.Count;
            for (int i = 0; i < n; i++)
            {
                if (horizontal) x += stack[i];
                else            y += stack[i];
                path.LineTo(x, y);
                horizontal = !horizontal;
            }
        }

        /// <summary>
        /// <c>rrcurveto</c> — relative cubic curves, six operands each.
        /// </summary>
        internal static void RRCurveto(IGlyphPathSink path, ref float x, ref float y, List<float> stack)
        {
            int n = stack.Count;
            for (int i = 0; i + 5 < n; i += 6)
            {
                float x1 = x + stack[i];
                float y1 = y + stack[i + 1];
                float x2 = x1 + stack[i + 2];
                float y2 = y1 + stack[i + 3];
                x = x2 + stack[i + 4];
                y = y2 + stack[i + 5];
                path.CubicTo(x1, y1, x2, y2, x, y);
            }
        }

        /// <summary>
        /// <c>vhcurveto</c> / <c>hvcurveto</c> — cubic curves whose start and end tangents
        /// alternate between vertical and horizontal, four operands each.
        /// </summary>
        /// <remarks>
        /// A vert-then-horiz curve departs vertically and arrives horizontally, so its final
        /// operand is a <c>dx</c>; horiz-then-vert is the mirror. The last curve may carry ONE
        /// extra operand correcting the other axis of its endpoint, present exactly when
        /// <c>n % 4 != 0</c>.
        /// <para><b>Not <c>n % 8</c></b>: that fires for n = 4, 12, 20 … and reads
        /// <c>stack[i + 4]</c> out of bounds. The condition is recorded because it is the one that
        /// was got wrong.</para>
        /// </remarks>
        internal static void AlternatingCurveto(
            IGlyphPathSink path, ref float x, ref float y, List<float> stack, bool verticalFirst)
        {
            int n = stack.Count;
            bool vertical = verticalFirst;
            int i = 0;

            while (i + 3 < n)
            {
                bool isLast = i + 4 >= n - 1;
                bool hasExtra = isLast && n % 4 != 0;
                float x1, y1, x2, y2;

                if (vertical)
                {
                    // Departure vertical, arrival horizontal: end X takes stack[i+3], the extra
                    // operand (when present) corrects Y.
                    x1 = x;                y1 = y + stack[i];
                    x2 = x1 + stack[i + 1]; y2 = y1 + stack[i + 2];
                    x = x2 + stack[i + 3];
                    y = y2 + (hasExtra ? stack[i + 4] : 0);
                }
                else
                {
                    // Departure horizontal, arrival vertical: the mirror of the above.
                    x1 = x + stack[i];      y1 = y;
                    x2 = x1 + stack[i + 1]; y2 = y1 + stack[i + 2];
                    x = x2 + (hasExtra ? stack[i + 4] : 0);
                    y = y2 + stack[i + 3];
                }

                i += 4 + (hasExtra ? 1 : 0);
                path.CubicTo(x1, y1, x2, y2, x, y);
                vertical = !vertical;
            }
        }
    }
}
