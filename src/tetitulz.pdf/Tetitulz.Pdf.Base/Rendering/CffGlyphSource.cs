// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using SkiaSharp;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Fonts.Cff;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Provides per-glyph <see cref="SKPath"/> outlines derived from a parsed
    /// CFF font program.  Used as a fallback when SkiaSharp cannot load the
    /// embedded font via <c>SKTypeface.FromData()</c>.
    /// </summary>
    internal sealed class CffGlyphSource : System.IDisposable
    {
        private readonly CffFont _font;
        // GID → cached outline
        private readonly Dictionary<int, Type2CharstringEvaluator.GlyphOutline> _cache
            = new Dictionary<int, Type2CharstringEvaluator.GlyphOutline>();

        public CffGlyphSource(CffFont font) { _font = font; }

        /// <summary>
        /// Glyph-space → text-space scale from the CFF /FontMatrix (default 0.001).
        /// The renderer passes this to <c>DrawGlyphPath</c> so outlines from a non-standard
        /// FontMatrix are scaled correctly instead of being drawn at a fixed 1/1000.
        /// </summary>
        public float FontMatrixScale => _font.FontMatrixScale;

        /// <summary>
        /// Returns the glyph outline for the given character code, or
        /// <c>null</c> if no outline is available.
        /// </summary>
        public Type2CharstringEvaluator.GlyphOutline? GetOutline(int charCode)
        {
            if (!_font.CharCodeToGid.TryGetValue(charCode, out int gid))
                gid = 0; // .notdef
            return GetOutlineForGid(gid);
        }

        /// <summary>
        /// Returns the outline of glyph <paramref name="gid"/>, or <c>null</c> when the page's charstring
        /// budget cut it short — the glyph ids Skia draws for a program it loaded itself.
        /// </summary>
        public Type2CharstringEvaluator.GlyphOutline? GetOutlineForGid(int gid)
        {
            if (_cache.TryGetValue(gid, out var cached)) return cached;

            var outline = Type2CharstringEvaluator.Evaluate(_font, gid);
            if (outline.CutByBudget || outline.Steps > PdfLimits.MaxCharstringStepsForHinting) _heavy = true;
            if (outline.CutByBudget)
            {
                // Not the glyph's outline, so neither drawn nor kept: a later page may draw it whole.
                outline.Path?.Dispose();
                RenderBudget.ReportCharstringRefusal();
                return null;
            }
            _cache[gid] = outline;
            return outline;
        }

        private bool _heavy;
        private bool _heavyReported;

        /// <summary>
        /// Whether this program is too heavy to hand to Skia through its OTF wrapper: a glyph of
        /// <paramref name="codes"/>, or one evaluated before, ran more than
        /// <see cref="PdfLimits.MaxCharstringStepsForHinting"/> steps or was cut short by the page's budget.
        /// The run's outlines are evaluated here — the draw evaluates them for their advances anyway, and
        /// caches them — so this costs a real font nothing. Once heavy, always heavy: the font's runs are
        /// drawn from these outlines, reported once.
        /// </summary>
        /// <remarks>
        /// Skia has no budget of ours: it ran a subroutine bomb a few hundred bytes long at about
        /// 300 ms a glyph, so a 20-glyph page took 7.5 s through the wrapper and 1.4 s without it (07's
        /// DoS hunt, F15).
        /// </remarks>
        public bool IsTooHeavyForHinting(IReadOnlyList<CMapCodec.Code> codes)
        {
            for (int i = 0; i < codes.Count && !_heavy; i++) GetOutline(codes[i].Cid);
            return HeavyReported();
        }

        /// <summary>
        /// The same question for a program Skia loaded itself, asked of the glyph ids it will draw
        /// (<paramref name="gids"/>, from the typeface's own lookup): the ids are what Skia runs, so
        /// they are what is evaluated.
        /// </summary>
        public bool IsTooHeavyForHinting(IReadOnlyList<ushort> gids)
        {
            for (int i = 0; i < gids.Count && !_heavy; i++) GetOutlineForGid(gids[i]);
            return HeavyReported();
        }

        private bool HeavyReported()
        {
            if (_heavy && !_heavyReported)
            {
                _heavyReported = true;
                RenderDiagnostics.Report(RenderDiagnosticKind.FontHintingSkipped,
                    "An embedded CFF program's glyphs ran more than " +
                    PdfLimits.MaxCharstringStepsForHinting.ToString("N0", CultureInfo.InvariantCulture) +
                    " charstring steps each (PdfLimits.MaxCharstringStepsForHinting), or past the page's " +
                    "budget, so the font is drawn from the library's own outlines, without Skia's hinting.");
            }
            return _heavy;
        }

        /// <summary>Returns the GID for the given character code (0 = .notdef if not mapped).</summary>
        public ushort GetGid(int charCode)
        {
            if (!_font.CharCodeToGid.TryGetValue(charCode, out int gid))
                gid = 0;
            return (ushort)Math.Min(gid, ushort.MaxValue);
        }

        /// <summary>Advance width in 1/1000 em units for the given character code.</summary>
        public float GetAdvanceWidth(int charCode)
        {
            var outline = GetOutline(charCode);
            float raw = outline.HasValue ? outline.Value.AdvanceWidth : _font.DefaultWidthX;
            // Charstring widths are in glyph space; map to the renderer's 1000-unit em
            // (GlyphSpaceScale is the default /FontMatrix x-scale, 0.001).  For a font with
            // that default matrix the ratio is exactly 1, so this is a no-op there.
            return raw * (_font.FontMatrixScale / PdfUnits.GlyphSpaceScale);
        }

        public void Dispose()
        {
            foreach (var outline in _cache.Values)
                outline.Path?.Dispose();
            _cache.Clear();
        }
    }
}
