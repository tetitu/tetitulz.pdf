// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// The text-position step a <c>TJ</c> number makes (§9.4.3, §9.4.4) — one implementation for both
    /// renderers, which each had the horizontal formula written out.
    /// </summary>
    /// <remarks>
    /// §9.4.4: <c>tx = (w0 − Tj/1000)·Tfs·Th</c> in horizontal writing and <c>ty = (w1 − Tj/1000)·Tfs</c>
    /// in vertical, so a number moves the position along the writing direction — and in vertical
    /// writing that is <i>down</i>, with no horizontal scaling. Both renderers applied every number
    /// horizontally, so a vertical CJK run spaced with <c>TJ</c> slid sideways instead.
    /// </remarks>
    internal static class TextAdjustment
    {
        /// <summary>The translation to compose onto <c>Tm</c> for a <c>TJ</c> number.</summary>
        internal static float[] Of(double adjustment, float fontSize, float horizScale, bool vertical)
        {
            float d = -(float)adjustment / (float)PdfUnits.GlyphSpaceUnitsPerEm * fontSize;
            return vertical
                ? new float[] { 1, 0, 0, 1, 0, d }
                : new float[] { 1, 0, 0, 1, d * horizScale, 0 };
        }
    }
}
