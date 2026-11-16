// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// A Type 3 font's <c>/FontMatrix</c>, glyph space to text space (§9.6.5) — the one reading for
    /// both renderers (through <c>Base.Rendering.Type3Encoding.FontMatrixOf</c>), the redaction
    /// engine and both text extractors, which take a Type 3 glyph's <c>/Widths</c> entry into text
    /// space through its <c>a</c> term.
    /// </summary>
    internal static class Type3FontMatrix
    {
        /// <summary>
        /// The font's <c>/FontMatrix</c>. A missing one is the default 1000-unit glyph space. A corrupt
        /// one (a non-finite entry, a singular 2×2) would give a degenerate glyph transform and NaN
        /// advances that poison the text matrix for the rest of the run, so it is the default too, and
        /// <paramref name="replaced"/> says so; a rotated or sheared matrix has a non-zero determinant
        /// and stands. A new array each call: a caller may keep it.
        /// </summary>
        /// <param name="font">The Type 3 font dictionary.</param>
        /// <param name="resolve">Follows an indirect reference; null reads direct values alone.</param>
        /// <param name="replaced">Whether a corrupt matrix was replaced by the default.</param>
        internal static float[] Of(PdfDictionary font, Func<PdfObject, PdfObject> resolve, out bool replaced)
        {
            replaced = false;
            float[] standard = { PdfUnits.GlyphSpaceScale, 0, 0, PdfUnits.GlyphSpaceScale, 0, 0 };

            PdfObject raw = font?[PdfName.FontMatrix];
            if (raw != null && resolve != null) raw = resolve(raw) ?? raw;
            if (!(raw is PdfArray stored) || stored.Count < 6) return standard;

            var matrix = new float[6];
            bool finite = true;
            for (int i = 0; i < 6; i++)
            {
                PdfObject element = stored[i];
                if (element != null && resolve != null) element = resolve(element) ?? element;
                matrix[i] = element is PdfNumber n ? (float)n.DoubleValue : float.NaN;
                finite &= !float.IsNaN(matrix[i]) && !float.IsInfinity(matrix[i]);
            }

            if (finite && Math.Abs(matrix[0] * matrix[3] - matrix[1] * matrix[2]) >= 1e-12f) return matrix;

            replaced = true;
            return standard;
        }
    }
}
