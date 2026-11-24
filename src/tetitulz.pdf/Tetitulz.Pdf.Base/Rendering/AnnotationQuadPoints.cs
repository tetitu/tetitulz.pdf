// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Text;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The <c>/QuadPoints</c> geometry of a text-markup annotation (§12.5.6.10 — Highlight,
    /// Underline, StrikeOut, Squiggly), read once for both renderers.
    /// </summary>
    /// <remarks>
    /// <para>Both renderers read the array here; read separately, they could disagree about it in
    /// three ways, each visible on a real file:</para>
    /// <list type="number">
    ///   <item><b>The sentinel guard.</b> A quad whose corners exceed <see cref="MaxCoordinate"/>
    ///     is skipped — <c>[-32768 … 32768]</c> is a producer's "unset" marker, not a rectangle, and
    ///     painting it covers the page. Guarded on one side only, the identical file would render
    ///     clean and export as one enormous filled path.</item>
    ///   <item><b>The extents.</b> Min/max over all four corners, never only the two corners
    ///     assumed to be the bottom edge. §12.5.6.10's corner order has been written
    ///     inconsistently by real producers for years — widely used viewers' own output has swapped
    ///     the last two — so a quad in another winding would give a rule of the wrong length, in
    ///     the wrong place, or both.</item>
    ///   <item><b>The absent case.</b> "No quads" is reported, so Highlight can fall back to
    ///     filling the <c>/Rect</c> rather than silently drawing nothing.</item>
    /// </list>
    /// <para>A leaf by construction — no static fields, so no type initializer and no
    /// initialization edge between the renderers.</para>
    /// </remarks>
    internal static class AnnotationQuadPoints
    {
        /// <summary>
        /// Largest coordinate magnitude a real quad may have. Beyond this the values are a
        /// sentinel rather than geometry; a PDF user-space page is at most 14 400 units
        /// (§14.11.2's 200-inch limit), so nothing conforming comes near.
        /// </summary>
        internal const float MaxCoordinate = 20000f;

        /// <summary>One quad, with the axis-aligned extents both renderers derive from it.</summary>
        internal readonly struct Quad
        {
            public readonly float X0, Y0, X1, Y1, X2, Y2, X3, Y3;

            /// <summary>Extents over <b>all four</b> corners, not the two the spec nominates.</summary>
            public readonly float Left, Right, Bottom, Top;

            internal Quad(float x0, float y0, float x1, float y1,
                          float x2, float y2, float x3, float y3)
            {
                X0 = x0; Y0 = y0; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; X3 = x3; Y3 = y3;
                Left   = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
                Right  = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
                Bottom = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
                Top    = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
            }

            /// <summary>
            /// The y of a horizontal rule across this quad: 0 = bottom (Underline, Squiggly),
            /// 0.5 = centre (StrikeOut), 1 = top.
            /// </summary>
            public float RuleY(float factor) => Bottom + (Top - Bottom) * factor;
        }

        /// <summary>
        /// Reads the annotation's quads, skipping sentinels.
        /// </summary>
        /// <param name="present">
        /// Whether a usable <c>/QuadPoints</c> array was <b>declared</b> — independently of how many
        /// quads survived the sentinel filter.
        /// </param>
        /// <remarks>
        /// <b>"Present but all skipped" is not "absent", and collapsing the two paints the page.</b>
        /// Highlight falls back to filling its <c>/Rect</c> when no quads were declared. An
        /// all-sentinel array is declared, so it must NOT take that fallback: such an annotation
        /// routinely carries <c>/Rect [0 0 612 792]</c>, and the fallback would paint the
        /// page-covering wash the sentinel guard exists to prevent. Returning the count alone loses
        /// exactly this distinction — which is how consolidating the two readers first went wrong.
        /// </remarks>
        /// <param name="doc">The document, for resolving an indirect <c>/QuadPoints</c>.</param>
        /// <param name="annot">The annotation dictionary to read.</param>
        internal static List<Quad> Read(PdfDocument doc, PdfDictionary annot, out bool present)
        {
            present = false;
            var quads = new List<Quad>();
            if (annot == null) return quads;

            var arr = PdfResolve.Array(doc, annot, PdfName.QuadPoints);
            if (arr == null || arr.Count < 8) return quads;
            present = true;

            for (int i = 0; i + 7 < arr.Count; i += 8)
            {
                float x0 = PdfResolve.ArrayFloat(doc, arr, i),     y0 = PdfResolve.ArrayFloat(doc, arr, i + 1);
                float x1 = PdfResolve.ArrayFloat(doc, arr, i + 2), y1 = PdfResolve.ArrayFloat(doc, arr, i + 3);
                float x2 = PdfResolve.ArrayFloat(doc, arr, i + 4), y2 = PdfResolve.ArrayFloat(doc, arr, i + 5);
                float x3 = PdfResolve.ArrayFloat(doc, arr, i + 6), y3 = PdfResolve.ArrayFloat(doc, arr, i + 7);

                if (IsSentinel(x0, y0) || IsSentinel(x1, y1) ||
                    IsSentinel(x2, y2) || IsSentinel(x3, y3)) continue;

                quads.Add(new Quad(x0, y0, x1, y1, x2, y2, x3, y3));
            }
            return quads;
        }

        private static bool IsSentinel(float x, float y)
            => !(Math.Abs(x) <= MaxCoordinate) || !(Math.Abs(y) <= MaxCoordinate);
    }
}
