// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts.Type1;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Provides per-glyph <see cref="SKPath"/> outlines derived from a parsed
    /// Type 1 PostScript font program.  Used when SkiaSharp cannot load the
    /// embedded /FontFile data via <c>SKTypeface.FromData()</c>.
    /// </summary>
    internal sealed class Type1GlyphSource : IDisposable
    {
        // Advance returned for a char code with no usable outline: half an em in the
        // renderer's 1/1000-em space, the same fallback the interpreters use.
        private const float DefaultGlyphWidth = 500f;

        private readonly Type1Font _font;
        // charCode → cached outline (null means .notdef / no glyph)
        private readonly Dictionary<int, Type1CharstringEvaluator.GlyphOutline?> _cache
            = new Dictionary<int, Type1CharstringEvaluator.GlyphOutline?>();

        /// <summary>
        /// FontMatrix scale factor (typically 0.001): converts glyph-space units
        /// to 1/1000-em space used by the renderer for advance widths.
        /// </summary>
        public float FontMatrixScale => _font.FontMatrixScale;

        public Type1GlyphSource(Type1Font font) { _font = font; }

        /// <summary>
        /// Returns the glyph name for the given character code using the font's /Encoding.
        /// Returns ".notdef" when the code is unmapped.
        /// </summary>
        public string GetGlyphName(int charCode)
        {
            if (charCode < 0 || charCode >= _font.Encoding.Length) return ".notdef";
            return _font.Encoding[charCode] ?? ".notdef";
        }

        /// <summary>
        /// Returns the glyph outline for the given character code, or <c>null</c>
        /// when no outline is available.
        /// </summary>
        public Type1CharstringEvaluator.GlyphOutline? GetOutline(int charCode)
        {
            if (_cache.TryGetValue(charCode, out var cached)) return cached;

            string name = GetGlyphName(charCode);
            if (name == ".notdef" && !_font.CharStrings.ContainsKey(".notdef"))
            {
                _cache[charCode] = null;
                return null;
            }

            var outline = Type1CharstringEvaluator.Evaluate(_font, name);
            if (outline.CutByBudget)
            {
                // Not the glyph's outline, so neither drawn nor kept: a later page may draw it whole.
                outline.Path?.Dispose();
                RenderBudget.ReportCharstringRefusal();
                return null;
            }
            _cache[charCode] = outline;
            return outline;
        }

        /// <summary>
        /// Advance width in 1/1000-em units for the given character code.
        /// Type 1 charstrings express widths in glyph space (1 unit = FontMatrix[0] em),
        /// so the raw width is multiplied by FontMatrixScale * 1000 to convert to 1/1000-em.
        /// </summary>
        public float GetAdvanceWidth(int charCode)
        {
            var outline = GetOutline(charCode);
            if (!outline.HasValue) return DefaultGlyphWidth;
            // Type 1 glyph-space width × FontMatrixScale × 1000 = 1/1000-em units
            return outline.Value.AdvanceWidth * _font.FontMatrixScale
                   * (float)PdfUnits.GlyphSpaceUnitsPerEm;
        }

        /// <summary>
        /// Whether a Type 1 program is too heavy to hand to Skia: one of its glyphs runs more than
        /// <see cref="PdfLimits.MaxCharstringStepsForHinting"/> steps, or the glyphs together run past
        /// <see cref="PdfLimits.MaxCharstringStepsPerFont"/> (or the open page's budget, which the check
        /// also spends). Every glyph is asked, once, because the glyph ids Skia draws cannot be mapped
        /// back to names here; a real program of a few hundred glyphs costs a few milliseconds.
        /// </summary>
        /// <remarks>
        /// The rule the CFF programs follow (<see cref="CffGlyphSource.IsTooHeavyForHinting(IReadOnlyList{ushort})"/>),
        /// applied where SkiaSharp's font engine accepts the PFB wrap — Linux; on Windows the wrap is declined
        /// and this font is drawn from <see cref="Type1GlyphSource"/> already.
        /// </remarks>
        internal static bool IsTooHeavyForHinting(Type1Font font)
        {
            var work = new Core.Util.WorkBudget(PdfLimits.MaxCharstringStepsPerFont,
                "a Type 1 program's glyphs (PdfLimits.MaxCharstringStepsPerFont)", false,
                Core.Fonts.CharstringWork.CurrentPage);
            try
            {
                using (Core.Fonts.CharstringWork.SpendFrom(work))
                    foreach (string name in font.CharStrings.Keys)
                    {
                        var outline = Type1CharstringEvaluator.Evaluate(font, name);
                        outline.Path?.Dispose();
                        if (Core.Fonts.CharstringWork.TooHeavyForSkia(outline.Steps, outline.CutByBudget)) return true;
                    }
            }
            catch (Core.Util.WorkBudgetExceededException) { return true; }
            return false;
        }

        public void Dispose()
        {
            foreach (var kv in _cache)
                kv.Value?.Path?.Dispose();
            _cache.Clear();
        }
    }
}
