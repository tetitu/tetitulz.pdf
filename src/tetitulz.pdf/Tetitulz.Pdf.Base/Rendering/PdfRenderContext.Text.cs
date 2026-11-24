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
        /// The pen width a stroked text run is drawn with: the graphics state's line width scaled
        /// by the CTM, exactly as <c>BuildStrokePaint</c> scales it for a path.
        /// </summary>
        /// <remarks>
        /// <para><b>§8.4.3.2 puts the line width in user space</b>, and §9.3.6 says a text stroke
        /// uses the graphics state's line width — so the pen must scale with the CTM and must not
        /// scale with the font size or the text matrix. Every text site here used the raw
        /// <c>_gs.LineWidth</c>, and both of them draw under a transform that is normalised to unit
        /// scale (the screen matrix is divided by the font size; the glyph path is in page-point
        /// space), so under <c>2 0 0 2 0 0 cm</c> a stroked run kept a 1x pen while the path beside
        /// it drew a 2x one. Measured before being changed: doubling the CTM and halving the font
        /// size — identical glyph geometry — left the stroke's ink area at 1.00x, against 2.00x
        /// for a path stroke on the same page.</para>
        /// <para>It was also a raster/SVG divergence: the SVG writer had already been corrected to
        /// scale text strokes by the CTM, and its own tests say so. That is the class of defect
        /// this repository finds most often, and it surfaced here because overprinting a stroked
        /// run through the shared stroke builder would otherwise have changed a pen's width by
        /// whether overprint happened to be on.</para>
        /// </remarks>
        private float TextStrokeWidth => _gs.LineWidth * StrokeCtmScale();

        /// <summary>Draws a glyph string at the current text position.</summary>
        public void DrawText(string text, SKTypeface typeface)
        {
            if (string.IsNullOrEmpty(text) || typeface == null) return;

            float[] textCTM = GraphicsState.Compose(_gs.Tm, _gs.CTM);
            float a = textCTM[0], b = textCTM[1], c = textCTM[2], d = textCTM[3];
            float e = textCTM[4], f = textCTM[5];

            // Font size in PDF points = magnitude of the x-axis vector in the text CTM.
            float sz = (float)Math.Sqrt(a * a + b * b);
            if (sz <= 0) return;

            float s = DpiScale, H = PageHeight;

            // Build a screen-space matrix that maps glyph coords → device pixels.
            // Using ScaleY = d*s/sz > 0 ensures glyph ascenders (gy < 0 in Skia convention)
            // map to screen_y < baseline, i.e., they appear ABOVE the baseline — correct.
            // The canvas.Scale(s,-s) base transform is bypassed via SetMatrix.
            var screenMatrix = new SKMatrix
            {
                ScaleX =  a * s / sz,   SkewX  = -c * s / sz,   TransX =  e * s,
                SkewY  = -b * s / sz,   ScaleY =  d * s / sz,   TransY = (H - f) * s,
                Persp2 = 1f,
            };

            // Size in PDF points; screenMatrix handles the pixel scaling.
            // Edging.SubpixelAntialias is SKFont's LCD rendering.
            using var font = new SKFont(typeface, sz)
            {
                Subpixel      = true,
                Edging        = SKFontEdging.SubpixelAntialias,
                Hinting       = DpiScale > 1.5f ? SKFontHinting.Full : SKFontHinting.Slight,
                LinearMetrics = false,
            };

            // Apply rendering mode. Alpha must be applied to text just as it is to paths
            // (BuildFillPaint calls ApplyAlpha, but text painting bypasses BuildFillPaint), and
            // the stroke half goes through BuildTextStrokePaint so it carries every §8.4.3
            // property rather than only the width.
            var mode = (TextRenderMode)_gs.TextRenderMode;
            if (mode.Fills())
            {
                using var fill = new SKPaint
                {
                    IsAntialias = true,
                    Style       = SKPaintStyle.Fill,
                    Color       = FillPaintColor(),
                };
                ApplyElementBlend(fill, _gs.BlendMode);
                DrawWithOptionalSoftMask(new ScreenTextOp(text, screenMatrix, font, fill));
            }
            if (mode.Strokes())
            {
                using var stroke = BuildTextStrokePaint();
                DrawWithOptionalSoftMask(new ScreenTextOp(text, screenMatrix, font, stroke));
            }
        }

        /// <summary>
        /// Draws glyphs using PDF-derived widths for positioning (not SkiaSharp metrics).
        /// KEY FIX for Hebrew/RTL: the PDF font's /W widths decide placement.
        /// This prevents Hebrew glyphs from drifting to wrong positions.
        /// When <paramref name="unicodeText"/> is supplied, glyphs with GID==0 (missing from
        /// the embedded font subset) are re-resolved via a system-font fallback instead of
        /// rendering as .notdef boxes.
        /// </summary>
        /// <param name="extraSpacing">
        /// Per-glyph text-state spacing (<c>Tc</c>, plus <c>Tw</c> on code 32) to add to each
        /// <b>measured</b> advance, in the same space as <paramref name="pdfWidths"/>. It applies
        /// only where an entry of <paramref name="pdfWidths"/> is <c>NaN</c>: a PDF-derived width
        /// already includes them. SkiaSharp measures a glyph, not a text state, so without this a
        /// system-substituted font would ignore <c>Tc</c> and <c>Tw</c> entirely.
        /// </param>
        /// <param name="glyphIds">Glyph ids to draw, in visual order.</param>
        /// <param name="typeface">Resolved typeface the ids index into.</param>
        /// <param name="pdfWidths">
        /// Per-glyph advance from the font's <c>/Widths</c> or <c>/W</c>, or <c>NaN</c> where the
        /// PDF declares none and the typeface must be measured instead.
        /// </param>
        /// <param name="unicodeText">
        /// The run's text, attached for copy/paste and accessibility only. It never affects glyph
        /// selection — §9.4 has done that by the time this runs.
        /// </param>
        public float DrawGlyphRunWithWidths(
            ushort[] glyphIds, SKTypeface typeface, float[] pdfWidths,
            string unicodeText = null, float[] extraSpacing = null)
        {
            if (glyphIds == null || glyphIds.Length == 0 || typeface == null) return 0f;

            float[] textCTM = GraphicsState.Compose(_gs.Tm, _gs.CTM);
            float a = textCTM[0], b = textCTM[1], c = textCTM[2], d = textCTM[3];
            float e = textCTM[4], f = textCTM[5];

            // The em size is the matrix's VERTICAL axis length, not its horizontal one.
            //
            // Not sqrt(a² + b²) — the x-axis — by which normalising the matrix would divide any
            // horizontal scale straight back out, so `Tz` could never reach the glyph shape: §9.4.4
            // puts Th in Trm's `a` slot. It would also mis-size text under a non-uniform CTM such
            // as `2 0 0 1 cm`, taking the em from the wider axis.
            //
            // Using the y-axis makes `sz` the true em and leaves the residual matrix carrying the
            // horizontal stretch, which is what SKFont + the screen matrix then apply. For a
            // similarity transform (the overwhelming majority) the two are identical, which is why
            // the difference stayed invisible.
            float sz = (float)Math.Sqrt(c * c + d * d);
            if (sz <= 0) return 0f;

            float s = DpiScale, H = PageHeight;

            var screenMatrix = new SKMatrix
            {
                ScaleX =  a * s / sz,   SkewX  = -c * s / sz,   TransX =  e * s,
                SkewY  = -b * s / sz,   ScaleY =  d * s / sz,   TransY = (H - f) * s,
                Persp2 = 1f,
            };

            using var font = new SKFont(typeface, sz)
            {
                LinearMetrics = true,
                Subpixel      = true,
                Hinting       = DpiScale > 1.5f ? SKFontHinting.Full : SKFontHinting.Slight,
            };

            // Phase 1: compute per-glyph x-positions using PDF-derived widths.
            //
            // Any glyph the PDF gives no width for is measured by Skia, in one call for the run — not
            // one glyph at a time, allocating a one-element glyph array AND a one-element width array
            // AND making a marshalled call for each, on a text-heavy page where every glyph takes that
            // branch, which is what a standard-14 font with no
            // /Widths is. Advance widths are per-glyph and context-free, so measuring the whole
            // run in one call returns exactly the same numbers.
            float[] measuredWidths = null;
            for (int i = 0; i < glyphIds.Length; i++)
            {
                if (i < pdfWidths.Length && !float.IsNaN(pdfWidths[i])) continue;
                measuredWidths = new float[glyphIds.Length];
                font.GetGlyphWidths(glyphIds, measuredWidths, System.Span<SKRect>.Empty, null);
                break;
            }

            var xPos = new float[glyphIds.Length];
            float totalAdvance = 0f;
            for (int i = 0; i < glyphIds.Length; i++)
            {
                xPos[i] = totalAdvance;
                // NaN is the "measure this one with Skia" sentinel, not "any negative value",
                // which would silently capture a legitimately negative advance:
                // §9.4.4 folds Tc and Tw into the step, and negative letter- or
                // word-spacing is ordinary typography. Such a glyph took Skia's measured
                // (always positive) width for its POSITION while the caller's Tm advance
                // still accumulated the negative value, so the run's internal pitch and its
                // end position disagreed and every glyph after the first negative one sat
                // too far right.
                if (i < pdfWidths.Length && !float.IsNaN(pdfWidths[i]))
                {
                    // Use PDF-derived width (may be 0 for combining marks like Hebrew nikud).
                    totalAdvance += pdfWidths[i];
                }
                else
                {
                    float measured = measuredWidths != null ? measuredWidths[i] : 0f;
                    // Tc/Tw are text state, not font metrics — Skia cannot know them, so §9.4.4's
                    // "+ Tc + Tw" has to be added here or it is lost for every substituted font.
                    float extra = extraSpacing != null && i < extraSpacing.Length ? extraSpacing[i] : 0f;
                    totalAdvance += (measured > 0 ? measured : 0.5f * sz) + extra;
                }
            }

            // Phase 2: resolve GID==0 (glyph absent from embedded subset) via a system-font
            // fallback so we draw the correct glyph instead of a .notdef box.
            bool[]   isFallback   = null;
            ushort[] fallbackGids = null;
            SKTypeface fallbackTf = null;

            if (unicodeText != null)
            {
                // One probe font for the whole scan, created only when a missing glyph is actually
                // found. SKFont is disposable and this loop runs per glyph, so a per-glyph font
                // would allocate one for every .notdef in a run whose embedded subset is broken -
                // which is precisely the document that reaches this code at all.
                SKFont fallbackProbe = null;
                try
                {
                int ci = 0;
                for (int gi = 0; gi < glyphIds.Length && ci < unicodeText.Length; gi++)
                {
                    int cp = GlyphIdResolver.CodePointAt(unicodeText, ci, out int units);
                    if (glyphIds[gi] == 0)
                    {
                        fallbackTf ??= SKTypeface.Default;
                        // U+FFFD is a ToUnicode-decode failure marker — never substitute it
                        // from a system font (Segoe UI renders it as a diamond ◆).
                        if (cp == 0xFFFD)
                        {
                            if (!_fffdReported)
                            {
                                _fffdReported = true;
                                RenderDiagnostics.Report(RenderDiagnosticKind.FontLoadFailed,
                                    "A glyph decoded to U+FFFD (malformed/absent ToUnicode CMap) and was skipped " +
                                    "(no system-font substitution to avoid a diamond glyph).");
                            }
                            ci += units; continue;
                        }
                        fallbackProbe ??= new SKFont(fallbackTf);
                        ushort fbGid = fallbackProbe.GetGlyph(cp);
                        if (fbGid > 0)
                        {
                            isFallback   ??= new bool[glyphIds.Length];
                            fallbackGids ??= new ushort[glyphIds.Length];
                            isFallback[gi]   = true;
                            fallbackGids[gi] = fbGid;
                        }
                    }
                    ci += units;
                }
                }
                finally { fallbackProbe?.Dispose(); }
            }

            // Pattern-filled text: capture primary + fallback glyph outlines (mirroring the
            // draw phases below) instead of painting; the advance is still returned so the
            // caller's text-matrix step is identical.
            if (IsCapturingGlyphs)
            {
                int pc = 0;
                var pg = new ushort[glyphIds.Length];
                var pp = new SKPoint[glyphIds.Length];
                for (int i = 0; i < glyphIds.Length; i++)
                {
                    if (isFallback != null && isFallback[i]) continue;
                    pg[pc] = glyphIds[i]; pp[pc] = new SKPoint(xPos[i], 0f); pc++;
                }
                if (pc > 0)
                {
                    CaptureGlyphRun(font, screenMatrix, pg.AsSpan(0, pc), pp.AsSpan(0, pc));
                    // A captured run in a clipping mode (4-7) still contributes to the text clip.
                    // This branch returned without doing so, while the glyph-path route adds to the
                    // clip BEFORE it checks for capture — so a pattern-filled or overprinting run
                    // under `4 Tr` clipped what followed on one font path and not on the other.
                    // AddGlyphRunToTextClip is a no-op outside the clipping modes.
                    AddGlyphRunToTextClip(font, screenMatrix, pg.AsSpan(0, pc), pp.AsSpan(0, pc));
                }
                if (isFallback != null && fallbackTf != null)
                {
                    using var fbFont = new SKFont(fallbackTf, sz) { LinearMetrics = true, Subpixel = true };
                    int fc = 0;
                    var fg = new ushort[glyphIds.Length];
                    var fp = new SKPoint[glyphIds.Length];
                    for (int i = 0; i < glyphIds.Length; i++)
                    {
                        if (!isFallback[i]) continue;
                        fg[fc] = fallbackGids[i]; fp[fc] = new SKPoint(xPos[i], 0f); fc++;
                    }
                    if (fc > 0)
                    {
                        CaptureGlyphRun(fbFont, screenMatrix, fg.AsSpan(0, fc), fp.AsSpan(0, fc));
                        AddGlyphRunToTextClip(fbFont, screenMatrix, fg.AsSpan(0, fc), fp.AsSpan(0, fc));
                    }
                }
                return totalAdvance;
            }

            // Phase 3: draw the primary run (skip fallback slots so no .notdef boxes appear).
            int primaryCount = glyphIds.Length;
            if (isFallback != null)
                for (int i = 0; i < isFallback.Length; i++)
                    if (isFallback[i]) primaryCount--;

            bool clipMode = ((TextRenderMode)_gs.TextRenderMode).AddsToClip();
            // The glyphs are kept for the clip, and for a fill-and-stroke run's knockout outline.
            bool keepGlyphs = clipMode || TextFillKnockedOutByStroke();

            if (primaryCount > 0)
            {
                ushort[]  clipGids = keepGlyphs ? new ushort[primaryCount] : null;
                SKPoint[] clipPos  = keepGlyphs ? new SKPoint[primaryCount] : null;

                using var builder = new SKTextBlobBuilder();
                var blobRun   = builder.AllocatePositionedRun(font, primaryCount);
                var glyphSpan = blobRun.Glyphs;
                var posSpan   = blobRun.Positions;
                int k = 0;
                for (int i = 0; i < glyphIds.Length; i++)
                {
                    if (isFallback != null && isFallback[i]) continue;
                    glyphSpan[k] = glyphIds[i];
                    posSpan[k]   = new SKPoint(xPos[i], 0f);
                    if (keepGlyphs) { clipGids[k] = glyphIds[i]; clipPos[k] = posSpan[k]; }
                    k++;
                }
                using var blob = builder.Build();
                if (blob != null) DrawTextBlob(blob, screenMatrix, font, clipGids, clipPos);
                if (clipMode) AddGlyphRunToTextClip(font, screenMatrix, clipGids, clipPos);
            }

            // Phase 4: draw fallback glyphs using the system default typeface.
            if (isFallback != null)
            {
                int fbCount = 0;
                foreach (bool flag in isFallback) if (flag) fbCount++;

                using var fbFont = new SKFont(fallbackTf, sz)
                {
                    LinearMetrics = true,
                    Subpixel      = true,
                    Hinting       = DpiScale > 1.5f ? SKFontHinting.Full : SKFontHinting.Slight,
                };
                ushort[]  clipGids = keepGlyphs ? new ushort[fbCount] : null;
                SKPoint[] clipPos  = keepGlyphs ? new SKPoint[fbCount] : null;

                using var builder = new SKTextBlobBuilder();
                var blobRun   = builder.AllocatePositionedRun(fbFont, fbCount);
                var glyphSpan = blobRun.Glyphs;
                var posSpan   = blobRun.Positions;
                int k = 0;
                for (int i = 0; i < glyphIds.Length; i++)
                {
                    if (!isFallback[i]) continue;
                    glyphSpan[k] = fallbackGids[i];
                    posSpan[k]   = new SKPoint(xPos[i], 0f);
                    if (keepGlyphs) { clipGids[k] = fallbackGids[i]; clipPos[k] = posSpan[k]; }
                    k++;
                }
                using var blob = builder.Build();
                if (blob != null) DrawTextBlob(blob, screenMatrix, fbFont, clipGids, clipPos);
                if (clipMode) AddGlyphRunToTextClip(fbFont, screenMatrix, clipGids, clipPos);
            }

            fallbackTf?.Dispose();
            return totalAdvance;
        }

        // No SKFont parameter: an SKTextBlob carries its own font per run, and SkiaSharp 4.148+
        // removed the SKPaint(SKFont) constructor whose only effect was on the string-drawing
        // paths. The blob draw is unaffected.
        /// <param name="blob">The run.</param>
        /// <param name="screenMatrix">The matrix the run is drawn under.</param>
        /// <param name="font">The run's font, for the knockout outline of a fill-and-stroke run.</param>
        /// <param name="glyphIds">The run's glyphs, or null when not kept (see <see cref="TextFillKnockedOutByStroke"/>).</param>
        /// <param name="positions">Their positions in the blob's space.</param>
        private void DrawTextBlob(SKTextBlob blob, SKMatrix screenMatrix,
                                  SKFont font, ushort[] glyphIds, SKPoint[] positions)
        {
            // Text rendering modes (PDF §9.3.6). Clip accumulation for the clipping modes is
            // handled by the caller via AddGlyphRunToTextClip; here we only emit the
            // fill/stroke paint that each mode also requires.
            var mode = (TextRenderMode)_gs.TextRenderMode;
            bool doFill   = mode.Fills();
            bool doStroke = mode.Strokes();
            if (!doFill && !doStroke) return; // Invisible and Clip paint nothing

            // See PaintBoundsRecorder's and UnscaleByDpi's remarks. blob.Bounds is glyph space;
            // screenMatrix maps it onto real device pixels (it bakes DpiScale in, via `s` a few
            // lines up in DrawGlyphRunWithWidths) — UnscaleByDpi converts that back to the
            // page-point space FillPath/StrokePath/DrawGlyphPath/DrawBitmap all report in. Both of
            // DrawGlyphRunWithWidths's callers (the embedded run, the system-font fallback run)
            // reach painting only through here, so one hook covers both. A stroked run pads by
            // StrokeCullMargin, in page points, exactly as DrawGlyphPath pads a stroked outline:
            // the same glyph reports the same bound whichever route the platform's font manager
            // gives it.
            if (PaintBoundsRecorder != null && HiddenContentDepth == 0)
            {
                SKRect runBounds = UnscaleByDpi(screenMatrix.MapRect(blob.Bounds));
                if (doStroke)
                {
                    float margin = StrokeCullMargin();
                    runBounds.Inflate(margin, margin);
                }
                PaintBoundsRecorder(runBounds);
            }

            // See PaintSuppressed's remarks. DrawGlyphRunWithWidths has already computed and
            // returned the run's text-position advance by the time it calls here, so suppressing
            // this pure paint step changes nothing a later operator in the same BT/ET depends on.
            if (PaintIsSuppressed) return;
            // The run's advance is already computed, so a refused run changes no later position.
            if (!ChargeDevicePaint(screenMatrix.MapRect(blob.Bounds),
                    doStroke ? RenderBudget.StrokePaintWeight : RenderBudget.FillPaintWeight))
                return;

            if (doFill && doStroke && IsOverprintFillStrokeGroup())
                InOverprintFillStrokeGroup(() => PaintTextBlob(blob, screenMatrix, font, glyphIds, positions, true, true));
            else PaintTextBlob(blob, screenMatrix, font, glyphIds, positions, doFill, doStroke);
        }

        /// <summary>A run's fill and stroke halves, as <paramref name="doFill"/> and <paramref name="doStroke"/> ask.</summary>
        private void PaintTextBlob(SKTextBlob blob, SKMatrix screenMatrix, SKFont font,
                                   ushort[] glyphIds, SKPoint[] positions, bool doFill, bool doStroke)
        {
            if (doFill)
            {
                using var fill = new SKPaint
                {
                    IsAntialias = true,
                    Style       = SKPaintStyle.Fill,
                    Color       = FillPaintColor(),
                };
                ApplyElementBlend(fill, _gs.BlendMode);
                // §11.7.4.4: a fill-and-stroke run keeps its fill out from under the stroke, in the
                // blob's own space (the screen matrix), where the stroke paint's width is measured.
                using SKPath glyphs = doStroke && glyphIds != null && TextFillKnockedOutByStroke()
                    ? GlyphRunPath(font, glyphIds, positions) : null;
                using SKPath outline = TextStrokeOutline(glyphs);
                bool clipped = ClipOutStroke(outline, screenMatrix);
                try
                {
                    if (!TryDrawWithMaskShader(fill, screenMatrix, new ScreenTextBlobOp(blob, screenMatrix, fill)))
                        DrawWithOptionalSoftMask(new ScreenTextBlobOp(blob, screenMatrix, fill));
                }
                finally { if (clipped) _canvas.Restore(); }
            }
            if (doStroke)
            {
                using var stroke = BuildTextStrokePaint();
                if (!TryDrawWithMaskShader(stroke, screenMatrix, new ScreenTextBlobOp(blob, screenMatrix, stroke)))
                    DrawWithOptionalSoftMask(new ScreenTextBlobOp(blob, screenMatrix, stroke));
            }
        }

        /// <summary>
        /// The stroke paint for a text run: <see cref="BuildStrokePaint"/>'s, with the pen resized
        /// for the normalised transform the text sites draw under.
        /// </summary>
        /// <remarks>
        /// <para><b>Every §8.4.3 stroke property, from the one builder.</b> An <c>SKPaint</c> built
        /// inline with only the style, the colour and the width would give a stroked text run
        /// Skia's defaults for the line cap, the line join, the miter limit and the dash — butt,
        /// miter, 4 and solid — however the file had set <c>J</c>, <c>j</c>, <c>M</c> or <c>d</c>,
        /// while a path stroke beside it honoured all four.</para>
        /// <para>The width is the one property that cannot come straight from the builder:
        /// <c>BuildStrokePaint</c> scales by the CTM because a path is drawn under the canvas's
        /// base transform, while these sites set an absolute screen matrix normalised by the font
        /// size. <see cref="TextStrokeWidth"/> is that difference, and it is assigned after so the
        /// two never disagree about the other seven properties.</para>
        /// </remarks>
        private SKPaint BuildTextStrokePaint()
        {
            SKPaint paint = BuildStrokePaint();
            paint.Color       = StrokePaintColor();
            paint.StrokeWidth = TextStrokeWidth;
            return paint;
        }

        /// <summary>
        /// Draws a single glyph outline (from a CFF / Type 1 charstring) at the current text
        /// position.  The path is in glyph space; <paramref name="pathScale"/> (the font's
        /// /FontMatrix x-scale, default 0.001 for a 1000-unit em) maps it to text space, then
        /// this method scales it to device space (CFF/Type 1 fallback rendering).
        /// </summary>
        public void DrawGlyphPath(SKPath charspacePath, float fontSize, float pathScale = PdfUnits.GlyphSpaceScale)
        {
            if (charspacePath == null || charspacePath.IsEmpty) return;
            if (fontSize <= 0) fontSize = 12f;
            if (!(pathScale > 0f) || float.IsNaN(pathScale) || float.IsInfinity(pathScale))
                pathScale = PdfUnits.GlyphSpaceScale;

            // Glyph paths are in charspace (1/1000 em units, Y-up).
            // The text rendering matrix (Trm) maps charspace → PDF page space (Y-up).
            // The canvas has only Scale(dpiScale) applied — it has NO Y-flip.
            // We must apply the Y-flip (PDF Y-up → canvas Y-down) explicitly, the same
            // way DrawGlyphRunWithWidths does via TransY = (H - f) * s.
            // Th in the `a` slot (§9.4.4), for the reason DrawStandardRun spells out: it scales
            // the glyph outline, not only the pen step. Unlike that path there is no normalising
            // `sz` here — the outline is transformed by this matrix directly — so omitting Th drew
            // every CFF and Type 1 glyph at full width under `Tz`.
            // Composed into this context's scratch matrices, in the order they always were (the same
            // arithmetic as GraphicsState.Compose, so every pixel is unchanged): five new matrices a
            // glyph were most of what the fallback route allocated (GAPS.md §13b).
            float[] text = _glyphTextMatrix;
            text[0] = fontSize * _gs.HorizScaling; text[3] = fontSize; text[5] = _gs.TextRise;
            PdfMatrix.ComposeInto(text, _gs.Tm, _glyphTrm);
            PdfMatrix.ComposeInto(_glyphTrm, _gs.CTM, _glyphTrm);
            _glyphPathScale[0] = pathScale; _glyphPathScale[3] = pathScale;
            PdfMatrix.ComposeInto(_glyphPathScale, _glyphTrm, _glyphCharToPage);
            var skm = GraphicsState.ToSKMatrix(_glyphCharToPage);

            // Y-flip: PDF page space (Y-up) -> canvas space (Y-down). The canvas applies
            // Scale(dpiScale) on top of this, so dpiScale is not included here.
            var yFlip = PageFlip;

            // Composed and applied ONCE, not charspace -> page into one path and then page -> canvas
            // into a second, which would be a native SKPath allocation and a full outline walk per
            // glyph for an intermediate nothing else reads. Matrix
            // concatenation is associative, so one transform through yFlip*skm is the same
            // outline — and `SKMatrix.Concat(a, b)` applies b first, which is the order the two
            // sequential calls had.
            // The outline is drawn into this context's scratch path, which nothing keeps past this
            // call: the text clip and the pattern capture copy it, and the paint draws it before its
            // own SKPaint is disposed. A glyph drawn while this one paints — a soft mask's content
            // through the same context — takes a path of its own, so the outer one is not overwritten.
            bool ownsScratch = _glyphPathDepth++ == 0;
            SKPath canvasPath = ownsScratch ? (_glyphScratchPath ??= new SKPath()) : new SKPath();
            try
            {
                charspacePath.Transform(SKMatrix.Concat(yFlip, skm), canvasPath);
                PaintGlyphPath(canvasPath);
            }
            finally
            {
                _glyphPathDepth--;
                if (!ownsScratch) canvasPath.Dispose();
            }
        }

        private readonly float[] _glyphTextMatrix = { 0, 0, 0, 0, 0, 0 };
        private readonly float[] _glyphPathScale = { 0, 0, 0, 0, 0, 0 };
        private readonly float[] _glyphTrm = new float[6];
        private readonly float[] _glyphCharToPage = new float[6];
        private SKPath _glyphScratchPath;
        private int _glyphPathDepth;

        /// <summary>A glyph outline already in canvas space, added to the clip, captured or painted as its render mode says.</summary>
        private void PaintGlyphPath(SKPath canvasPath)
        {
            var mode = (TextRenderMode)_gs.TextRenderMode;

            // The clipping modes add glyphs to the text clipping path (PDF §9.3.6).
            bool clipMode = mode.AddsToClip();
            if (clipMode)
            {
                _textClipModeUsed = true; // record even when the glyph outline is empty
                AddGlyphToTextClip(canvasPath);
            }

            // Pattern-filled text: append the outline to the capture accumulator and skip
            // painting (the pattern is painted into the accumulated glyph shape afterwards).
            if (IsCapturingGlyphs) { CaptureGlyphPath(canvasPath); return; }

            // Paint the glyph if the mode calls for drawing. Invisible and Clip paint nothing.
            bool doPaint = mode.Fills() || mode.Strokes();
            if (!doPaint) return;

            bool doFill   = mode.Fills();
            bool doStroke = mode.Strokes();

            // See PaintBoundsRecorder's remarks. canvasPath is the page-point-space glyph outline
            // every branch below paints (built from TransformPoint-derived matrices, so — unlike
            // DrawTextBlob's screenMatrix — it was never multiplied by DpiScale; no UnscaleByDpi
            // needed here). A stroked glyph pads by StrokeCullMargin — StrokePath's own culling
            // margin, not a second formula: TextStrokeWidth IS _gs.LineWidth * StrokeCtmScale(),
            // the same expression StrokeCullMargin already reads, so writing Math.Max(TextStrokeWidth,
            // 1f) here would have been the exact duplication CLAUDE.md's DRY table warns against —
            // two answers to "how much margin does this stroke need" that agree today and could
            // silently drift the next time either one changes (a miter-limit term, the
            // antialiasing pad).
            if (PaintBoundsRecorder != null && HiddenContentDepth == 0)
            {
                SKRect glyphBounds = canvasPath.Bounds;
                if (doStroke)
                {
                    float margin = StrokeCullMargin();
                    glyphBounds.Inflate(margin, margin);
                }
                PaintBoundsRecorder(glyphBounds);
            }

            // See PaintSuppressed's remarks. Text-clip accumulation and pattern-glyph capture
            // already ran above, unconditionally -- this gate covers only the fill/stroke paint
            // from here down.
            if (PaintIsSuppressed) return;
            if (!ChargePaint(canvasPath.Bounds, doStroke ? RenderBudget.StrokePaintWeight : RenderBudget.FillPaintWeight))
                return;

            if (doFill && doStroke && IsOverprintFillStrokeGroup())
            {
                // Both halves opaque in a group at the alpha and blend mode: §11.7.4.4's overprint case.
                InOverprintFillStrokeGroup(() =>
                {
                    PaintGlyphOutline(canvasPath, stroke: false);
                    PaintGlyphOutline(canvasPath, stroke: true);
                });
            }
            else if (doFill && doStroke && TextFillKnockedOutByStroke())
            {
                // §11.7.4.4: the fill kept out from under the stroke, so the stroke composites with
                // the backdrop. The halves then no longer overlap, so each takes its own route —
                // the mask shader included — with nothing composited twice.
                using (SKPath outline = TextStrokeOutline(canvasPath))
                {
                    bool clipped = ClipOutStroke(outline, _canvas.TotalMatrix);
                    try { PaintGlyphOutline(canvasPath, stroke: false); }
                    finally { if (clipped) _canvas.Restore(); }
                }
                PaintGlyphOutline(canvasPath, stroke: true);
            }
            else if (doFill && doStroke)
            {
                // Fill and stroke share ONE paint here because they share one soft-mask layer:
                // they are one element, and masking them separately would composite the shared
                // interior edge twice. The stroke half's geometry therefore has to be copied onto
                // that paint rather than built beside it — see FillThenStrokePathOp.
                using var paint = new SKPaint
                {
                    IsAntialias = true,
                    Style       = SKPaintStyle.Fill,
                    Color       = FillPaintColor(),
                };
                ApplyElementBlend(paint, _gs.BlendMode);
                using SKPaint strokeGeometry = BuildTextStrokePaint();

                // Bounds must cover the stroke too — it is painted inside the same layer.
                SKRect bounds = SoftMaskLayerBounds(canvasPath, paint);
                bounds.Inflate(Math.Max(TextStrokeWidth, 1f), Math.Max(TextStrokeWidth, 1f));
                DrawWithOptionalSoftMask(bounds, new FillThenStrokePathOp(canvasPath, paint, strokeGeometry));
            }
            else PaintGlyphOutline(canvasPath, stroke: doStroke);
        }

        /// <summary>A fill-and-stroke text run whose fill §11.7.4.4 keeps out from under its stroke.</summary>
        /// <remarks>
        /// §11.7.4.4's overprint case is the other split, a group painted at alpha 1 and
        /// composited at the alpha and blend mode (<see cref="InOverprintFillStrokeGroup"/>), which
        /// the run's two paint sites open as the path operators do.
        /// </remarks>
        private bool TextFillKnockedOutByStroke()
        {
            var mode = (TextRenderMode)_gs.TextRenderMode;
            return mode.Fills() && mode.Strokes() && StrokeKnocksOutFill();
        }

        /// <summary>A run's glyph outlines in the blob's space: each glyph at its position.</summary>
        private static SKPath GlyphRunPath(SKFont font, ushort[] glyphIds, SKPoint[] positions)
        {
            if (font == null || glyphIds == null) return null;
            using var builder = new SKPathBuilder { FillType = SKPathFillType.Winding };
            for (int i = 0; i < glyphIds.Length; i++)
            {
                using SKPath gp = font.GetGlyphPath(glyphIds[i]);
                if (gp == null || gp.IsEmpty) continue;
                SKPoint p = i < positions.Length ? positions[i] : SKPoint.Empty;
                builder.AddPath(gp, SKMatrix.CreateTranslation(p.X, p.Y));
            }
            return builder.Snapshot();
        }

        /// <summary>One half of a glyph outline's paint, through the mask shader where a soft mask is set.</summary>
        private void PaintGlyphOutline(SKPath canvasPath, bool stroke)
        {
            using SKPaint paint = stroke ? BuildTextStrokePaint() : new SKPaint
            {
                IsAntialias = true,
                Style       = SKPaintStyle.Fill,
                Color       = FillPaintColor(),
            };
            if (!stroke) ApplyElementBlend(paint, _gs.BlendMode);
            if (!TryDrawPathWithMaskShader(canvasPath, paint))
                DrawWithOptionalSoftMask(SoftMaskLayerBounds(canvasPath, paint),
                                         new PathDrawOp(canvasPath, paint));
        }

        /// <summary>
        /// The area a text stroke covers for <paramref name="glyphs"/> — the outline the fill of a
        /// fill-and-stroke run keeps out of (§11.7.4.4) — stroked with the paint the run's stroke
        /// half is drawn with, in the space it is drawn in.
        /// </summary>
        /// <param name="glyphs">The glyph outlines, in the space the run's stroke paint is drawn in.</param>
        private SKPath TextStrokeOutline(SKPath glyphs)
        {
            if (glyphs == null || glyphs.IsEmpty) return null;
            using SKPaint strokePaint = BuildTextStrokePaint();
            using var builder = new SKPathBuilder();
            return strokePaint.GetFillPath(glyphs, builder) ? builder.Snapshot() : null;
        }

        // ContentStreamInterpreter.DrawOtfCffRun builds one blob for the whole run through
        // DrawGlyphRunWithWidths above; a single-glyph blob helper here would be the thing most
        // likely to be copied back into a
        // hand-rolled batched method next time. Its own history is the argument: its size matrix
        // omitted §9.4.4's Th for a whole release, and the copy that would be made from it is
        // exactly how that returns. The text-clip, glyph-capture, soft-mask and fill/stroke
        // behaviour it carried is all present in DrawGlyphRunWithWidths, which is why the routing
        // was a deletion rather than a merge.

        /// <summary>
        /// Measures a string's advance width using the effective font size
        /// (accounting for the text CTM scale, not just the raw FontSize parameter).
        /// This matches how DrawText computes the actual rendering size.
        /// </summary>
        public float MeasureTextWidth(string text, SKTypeface typeface)
        {
            if (string.IsNullOrEmpty(text) || typeface == null) return 0f;
            // Compute the actual font size from the text matrix + CTM, matching DrawText.
            float[] textCTM = GraphicsState.Compose(
                new float[] { _gs.FontSize, 0, 0, _gs.FontSize, 0, _gs.TextRise }, _gs.Tm);
            float[] full = GraphicsState.Compose(textCTM, _gs.CTM);
            float sz = (float)System.Math.Sqrt(full[0] * full[0] + full[1] * full[1]);
            if (sz <= 0) sz = _gs.FontSize;

            using var font = new SKFont(typeface, sz * DpiScale);
            float measured = font.MeasureText(text);
            // Convert back from pixel space to text-space units.
            return measured / DpiScale;
        }

        // ----------------------------------------------------------------
        //  Image rendering
        // ----------------------------------------------------------------
    }
}
