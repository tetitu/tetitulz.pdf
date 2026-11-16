// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Fonts
{
    /// <summary>
    /// The single answer to "how wide is this glyph, in glyph-space units?" — §9.4.4's <c>w₀</c>.
    /// </summary>
    /// <remarks>
    /// <para>Four sites across the two renderers asked this, and three of them agreed:
    /// <b>a width of zero means "no data" only when the font does not declare one.</b> A Type 0
    /// font may carry an explicit <c>0</c> in its <c>/W</c> array — the Hebrew-nikud and
    /// combining-mark case — and that zero is the correct advance, not a missing one.</para>
    ///
    /// <para><b>The fourth site was the raster renderer's invisible-text path</b>
    /// (<c>DrawInvisibleRun</c>, render mode 3 — the OCR-overlay mode), which coerced any
    /// non-positive width to the 500-unit default with no escape. So for a glyph with an explicit
    /// zero width the raster renderer advanced <b>6 pt</b> at 12 pt type where the SVG exporter
    /// advanced <b>0 pt</b>, accumulating along the run — and the raster renderer disagreed
    /// <i>with itself</i>, since the same glyph under render mode 0 took the guarded path and
    /// advanced 0. The searchable overlay drifted out of alignment with the glyphs it exists to
    /// overlay, by one default advance per combining mark.</para>
    ///
    /// <para><b>A defect in an advance is invisible to a pixel-comparing parity suite</b> — every
    /// glyph stays internally correct and only the positions after it move — so a sweep comparing
    /// the two renderers' output cannot see it.</para>
    ///
    /// <para><b>In Core, because the extractor needs it.</b> A private
    /// <c>if (w &lt;= 0) w = 500;</c>, with no <c>HasExplicitWidth</c> escape, would advance an
    /// explicit <c>/W 0</c> <b>500 units in extraction and 0 in rendering</b>, so extracted text
    /// positions would drift from the glyphs they describe by one default advance per combining
    /// mark. Core cannot reference Base, so the rule sits down here.</para>
    /// </remarks>
    internal static class GlyphWidth
    {
        /// <summary>
        /// The width used when the font supplies none: §9.7.4.3's <c>/DW</c> default, and the same
        /// value the three private copies of this constant carried.
        /// </summary>
        internal const double DefaultUnits = 500.0;

        /// <summary>
        /// <paramref name="font"/>'s advance width for <paramref name="charCode"/>, in glyph-space
        /// units, falling back to <see cref="DefaultUnits"/> only when the font declares nothing.
        /// </summary>
        internal static double Units(PdfFont font, int charCode)
        {
            double w = font?.GetWidth(charCode) ?? 0;

            // Zero is "no data" UNLESS the font says otherwise. An explicit /W entry of 0 is a real
            // width — see the remarks above for what substituting the default costs.
            if (w <= 0 && font?.HasExplicitWidth(charCode) != true) w = DefaultUnits;

            return w;
        }

        /// <summary>
        /// <paramref name="font"/>'s advance for <paramref name="charCode"/> in text space at a
        /// font size of 1: <see cref="Units"/> over 1000 for every font but a Type 3, whose
        /// <c>/Widths</c> are in its own glyph space and go through its <c>/FontMatrix</c>'s
        /// <c>a</c> term (§9.6.5) — a code it declares no width for advances nothing. Both text
        /// extractors measure through this, as the renderers do.
        /// </summary>
        internal static double TextSpace(PdfFont font, int charCode)
        {
            if (font != null && font.IsType3)
            {
                if (!font.HasExplicitWidth(charCode)) return 0;
                float a = font.Type3Matrix != null && font.Type3Matrix.Length >= 1 ? font.Type3Matrix[0] : Constants.PdfUnits.GlyphSpaceScale;
                return font.GetWidth(charCode) * a;
            }
            return Units(font, charCode) / Constants.PdfUnits.GlyphSpaceUnitsPerEm;
        }
    }
}
