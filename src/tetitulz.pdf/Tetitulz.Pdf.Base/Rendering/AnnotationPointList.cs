// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The flat <c>[x y x y …]</c> coordinate arrays two annotation families carry — an Ink
    /// annotation's <c>/InkList</c> strokes (§12.5.6.13) and a Polygon/PolyLine's <c>/Vertices</c>
    /// (§12.5.6.9) — read once for both renderers.
    /// </summary>
    /// <remarks>
    /// <para><b>The sibling of <see cref="AnnotationQuadPoints"/>, and it exists for the same
    /// reason.</b> Two renderers reading <c>/QuadPoints</c> separately disagree in three ways on
    /// real files; <c>/InkList</c> and <c>/Vertices</c> are the same question. Copies that agree
    /// today are not the bar: nothing structural keeps them in step until one of them
    /// changes.</para>
    ///
    /// <para><b>The one rule worth naming: four numbers.</b> A stroke or polyline needs two points
    /// to draw anything, so an entry with fewer than four coordinates is skipped rather than
    /// half-drawn — and an odd trailing coordinate is ignored by the <c>i + 1 &lt; Count</c> step,
    /// not treated as a point with an implied zero. Both renderers already did this; stating it once
    /// means the next reader does not have to infer it twice.</para>
    ///
    /// <para>Painting stays with the caller, because that is where they legitimately differ: the
    /// raster path builds a path on the graphics context, the SVG path builds a <c>d</c> attribute.
    /// What is shared is which strokes exist and which are drawable.</para>
    ///
    /// <para>A leaf by construction — no static fields, so no type initializer and no
    /// initialization edge between the renderers.</para>
    /// </remarks>
    internal static class AnnotationPointList
    {
        /// <summary>
        /// Fewest coordinates that can describe a drawable run: two points, so four numbers.
        /// </summary>
        internal const int MinimumCoordinates = 4;

        /// <summary>
        /// Each drawable stroke of an Ink annotation's <c>/InkList</c>, in order. Yields nothing
        /// when the key is absent, is not an array, or every stroke is too short.
        /// </summary>
        internal static IEnumerable<PdfArray> InkStrokes(PdfDocument doc, PdfDictionary annot)
        {
            // Resolved, and resolved ONCE: PdfResolve already falls back to the direct value, so
            // a `ResolveObject(...) ?? PdfResolve.Array(...)` hedge would have a dead operand. See PdfResolve.Value.
            PdfArray inkList = PdfResolve.Array(doc, annot, PdfName.InkList);
            if (inkList == null) yield break;

            foreach (PdfObject strokeObj in inkList)
            {
                PdfArray points = PdfResolve.Array(doc, strokeObj);
                if (points == null || points.Count < MinimumCoordinates) continue;

                yield return points;
            }
        }

        /// <summary>
        /// A Polygon or PolyLine annotation's <c>/Vertices</c>, or <c>null</c> when it is absent or
        /// too short to draw. Closing the figure is the caller's decision — it depends on the
        /// subtype, not on the geometry.
        /// </summary>
        internal static PdfArray Vertices(PdfDocument doc, PdfDictionary annot)
        {
            PdfArray vertices = PdfResolve.Array(doc, annot, PdfName.Vertices);
            return vertices != null && vertices.Count >= MinimumCoordinates ? vertices : null;
        }
    }
}
