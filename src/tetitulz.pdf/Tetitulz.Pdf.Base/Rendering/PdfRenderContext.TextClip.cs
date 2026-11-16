// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Wraps an <see cref="SKCanvas"/> and manages the PDF graphics-state stack.
    /// Coordinate-system note: PDF origin is bottom-left (Y-up); SkiaSharp origin
    /// is top-left (Y-down).  The initial canvas transform flips Y so that all
    /// subsequent drawing uses PDF point coordinates directly.
    /// </summary>
    internal sealed partial class PdfRenderContext : IDisposable
    {
        /// <summary>
        /// Initialises the text clipping path accumulator at BT.
        /// </summary>
        public void BeginTextClip()
        {
            _textClipPath?.Dispose();
            _textClipPath = null;
            _textClipModeUsed = false;
        }

        /// <summary>
        /// At ET, intersects the accumulated text clip path with the current clip.
        /// A null/empty accumulator means no clip glyphs were drawn — nothing to do.
        /// </summary>
        public void EndTextClip()
        {
            bool clipRequested = _textClipModeUsed;
            _textClipModeUsed = false;

            SKPath accumulated = _textClipPath?.Snapshot();
            if (accumulated == null || accumulated.IsEmpty)
            {
                accumulated?.Dispose();
                _textClipPath?.Dispose();
                _textClipPath = null;
                // PDF §9.4.3: when a text-clipping rendering mode (4-7) was used but the
                // accumulated glyph outline is empty (e.g. the only glyphs drawn were
                // blank/space glyphs), the resulting clip region is empty — subsequent
                // marks must be clipped out entirely.  Intersecting with an empty rect
                // achieves that.  Without this, content drawn after ET (commonly a
                // gradient image masked by the text) leaks through unclipped as a solid
                // rectangle (observed on Cal credit-card statements: gradient "boxes"
                // where blank clip glyphs sat between the gradient-filled letters).
                if (clipRequested)
                    _canvas.ClipRect(SKRect.Empty, SKClipOperation.Intersect);
                return;
            }
            _canvas.ClipPath(accumulated, SKClipOperation.Intersect, antialias: true);
            accumulated.Dispose();
            _textClipPath.Dispose();
            _textClipPath = null;
        }

        /// <summary>
        /// Adds a transformed glyph path (page-point, Y-down — the canvas applies DpiScale) to the text clip accumulator.
        /// Called from <see cref="DrawGlyphPath"/> for the modes where
        /// <see cref="PdfGraphicsEnumExtensions.AddsToClip"/> holds.
        /// </summary>
        public void AddGlyphToTextClip(SKPath devicePath)
        {
            if (devicePath == null || devicePath.IsEmpty) return;
            if (_textClipPath == null)
            {
                // PDF §9.3.6: accumulated glyph outlines are combined into a single path
                // using the non-zero winding number rule (NOT even-odd). NOTE 2 of that
                // subclause: with overlapping glyphs the winding rule is what fills the
                // intersection instead of punching a hole — see OverlappingGlyphClipping.
                _textClipPath = new SKPathBuilder { FillType = SKPathFillType.Winding };
            }
            _textClipPath.AddPath(devicePath);
        }

        /// <summary>
        /// Accumulates the outlines of a positioned glyph run into the text-clipping path
        /// (PDF §9.3.6, text rendering modes 4-7). <paramref name="screenMatrix"/> maps the
        /// SKFont's glyph coordinates to device pixels (it bakes in DpiScale); we divide that
        /// factor back out so the outline lands in the page-point space that
        /// <see cref="EndTextClip"/> intersects against the canvas — whose base transform
        /// already carries the DpiScale.  No-op unless the current mode is a clipping mode.
        /// </summary>
        public void AddGlyphRunToTextClip(
            SKFont font, SKMatrix screenMatrix,
            ReadOnlySpan<ushort> glyphIds, ReadOnlySpan<SKPoint> positions)
        {
            var mode = (TextRenderMode)_gs.TextRenderMode;
            if (!mode.AddsToClip()) return;
            if (font == null || glyphIds.Length == 0) return;

            // Record that a text-clipping mode was actually exercised in this text object,
            // even if every glyph outline turns out empty.  EndTextClip uses this to apply
            // an empty clip (rather than no clip) when the accumulated path is empty.
            _textClipModeUsed = true;

            AddGlyphRunOutlines(font, screenMatrix, glyphIds, positions, toTextClip: true);
        }

        /// <summary>
        /// Each glyph of a positioned run as an outline in page-point space — glyph-local at the font
        /// size, then device pixels, then ÷ <see cref="DpiScale"/>, the space <c>_currentPath</c> lives
        /// in — added to the text clip, or with <paramref name="toTextClip"/> false to the
        /// pattern-text capture.
        /// </summary>
        private void AddGlyphRunOutlines(SKFont font, SKMatrix screenMatrix,
            ReadOnlySpan<ushort> glyphIds, ReadOnlySpan<SKPoint> positions, bool toTextClip)
        {
            float s = DpiScale > 0f ? DpiScale : 1f;
            var toPage = SKMatrix.Concat(SKMatrix.CreateScale(1f / s, 1f / s), screenMatrix);
            for (int i = 0; i < glyphIds.Length; i++)
            {
                using SKPath gp = font.GetGlyphPath(glyphIds[i]);
                if (gp == null || gp.IsEmpty) continue;
                SKPoint p = i < positions.Length ? positions[i] : SKPoint.Empty;
                gp.Transform(SKMatrix.Concat(toPage, SKMatrix.CreateTranslation(p.X, p.Y)));
                if (toTextClip) AddGlyphToTextClip(gp);
                else CaptureGlyphPath(gp);
            }
        }

        // ----------------------------------------------------------------
        //  Pattern-filled text: glyph-outline capture + pattern paint
        // ----------------------------------------------------------------

        /// <summary>True while glyph outlines are being captured (for pattern-filled text).</summary>
        public bool IsCapturingGlyphs => _glyphCapturePath != null;

        /// <summary>Begins capturing glyph outlines instead of painting them.</summary>
        public void BeginGlyphCapture()
        {
            _glyphCapturePath?.Dispose();
            // PDF §9.3.6 NOTE 2: combine glyph outlines with the non-zero winding rule.
            _glyphCapturePath = new SKPathBuilder { FillType = SKPathFillType.Winding };
        }

        /// <summary>Stops capture and hands the accumulated outline to the caller (ownership transfers).</summary>
        public SKPath EndGlyphCapture()
        {
            // Ownership transfers as an SKPath, which is what every caller expects and
            // disposes; the builder itself never leaves this class.
            SKPathBuilder b = _glyphCapturePath;
            _glyphCapturePath = null;
            if (b == null) return null;
            SKPath captured = b.Snapshot();
            b.Dispose();
            return captured;
        }

        /// <summary>Appends a page-point/Y-down glyph outline to the capture accumulator.</summary>
        private void CaptureGlyphPath(SKPath pagePointPath)
        {
            if (pagePointPath == null || pagePointPath.IsEmpty) return;
            _glyphCapturePath.AddPath(pagePointPath);
        }

        /// <summary>
        /// Captures a positioned glyph run into the accumulator, in the same page-point space as
        /// <c>_currentPath</c> (<see cref="AddGlyphRunOutlines"/>).
        /// </summary>
        private void CaptureGlyphRun(SKFont font, SKMatrix screenMatrix,
            ReadOnlySpan<ushort> glyphIds, ReadOnlySpan<SKPoint> positions)
        {
            if (font == null) return;
            AddGlyphRunOutlines(font, screenMatrix, glyphIds, positions, toTextClip: false);
        }

        /// <summary>
        /// Installs <paramref name="glyphPath"/> (page-point/Y-down space) as the current path and
        /// invokes <paramref name="paintCurrentPath"/> (which paints the fill pattern via the
        /// interpreter's RenderTilingPattern, filling <c>_currentPath</c>). Wrapped in
        /// PushState/PopState and a save/restore of <c>_currentPath</c> so the real current path,
        /// clip and graphics state are left untouched. The live canvas clip still constrains the
        /// paint, and only the glyph shape is filled — the background stays unpainted.
        /// </summary>
        public void FillGlyphsWithPattern(SKPath glyphPath, Action paintCurrentPath)
        {
            if (glyphPath == null || glyphPath.IsEmpty || paintCurrentPath == null) return;
            PushState();
            var saved = _currentPath;
            _currentPath = new SKPath(glyphPath) { FillType = SKPathFillType.Winding };
            try { paintCurrentPath(); }
            finally
            {
                // RenderTilingPattern's terminal NewPath() left a fresh empty path; drop it
                // and restore the caller's real current path.
                _currentPath?.Dispose();
                _currentPath = saved;
                PopState();
            }
        }

        /// <summary>
        /// Paints the fill pattern through a stencil mask (§8.9.6.2: a stencil's painted samples
        /// take the fill): the pattern is painted into the unit square under the CTM inside a layer
        /// of its own, which the stencil's alpha then keeps in — the colour the stencil was decoded
        /// in is immaterial, its alpha is the shape. <paramref name="paintCurrentPath"/> paints the
        /// pattern into the current path, which is the unit square for the call; the real current
        /// path, clip and state are left as they were.
        /// </summary>
        public void FillStencilWithPattern(SKBitmap stencil, bool interpolate, Action paintCurrentPath)
        {
            if (stencil == null || paintCurrentPath == null) return;
            if (!TryBuildImageMatrix(stencil.Width, stencil.Height, 0, 0, 1, 1, out SKMatrix m)) return;
            if (PaintIsSuppressed) return;

            PushState();
            var saved = _currentPath;
            _currentPath = new SKPath();
            try
            {
                AppendRect(0, 0, 1, 1);
                // The layer is sized to the square, in the canvas's own space, as a soft-mask layer is.
                _canvas.SaveLayer(_currentPath.Bounds, null);
                _layerDepth++;
                try
                {
                    paintCurrentPath();
                    using var image = SKImage.FromBitmap(stencil);
                    using var keep = new SKPaint { BlendMode = SKBlendMode.DstIn };
                    _canvas.Save();
                    _canvas.SetMatrix(m);
                    _canvas.DrawImage(image, 0, 0, SamplingFor(interpolate), keep);
                    _canvas.Restore();
                }
                finally
                {
                    _layerDepth--;
                    _canvas.Restore();
                }
            }
            finally
            {
                _currentPath?.Dispose();
                _currentPath = saved;
                PopState();
            }
        }

        // ----------------------------------------------------------------
        //  Current Transformation Matrix
        // ----------------------------------------------------------------
    }
}
