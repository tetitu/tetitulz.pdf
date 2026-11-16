// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using SkiaSharp;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Interprets a PDF content stream, executing drawing operators against a
    /// <see cref="PdfRenderContext"/> backed by SkiaSharp.
    ///
    /// <para>Supported operator families:</para>
    /// <list type="bullet">
    ///   <item>Path construction: m l c v y h re</item>
    ///   <item>Path painting: S s F f f* B B* b b* n</item>
    ///   <item>Color: g G rg RG k K cs CS sc SC scn SCN</item>
    ///   <item>Graphics state: q Q cm w J j M d ri i gs</item>
    ///   <item>Text: BT ET Tf Td TD Tm T* Tj TJ ' " Tc Tw Tz TL Tr Ts</item>
    ///   <item>XObject: Do (Form XObjects recursed; Image XObjects rasterized)</item>
    ///   <item>Clipping: W W*</item>
    /// </list>
    /// </summary>
    internal sealed partial class ContentStreamInterpreter
    {
        // ---- Path construction ----
        private void OpM(ContentStreamOperation op)
        {
            _curX = GetFloat(op, 0); _curY = GetFloat(op, 1);
            _ctx.MoveTo(_curX, _curY);
        }

        private void OpL(ContentStreamOperation op)
        {
            _curX = GetFloat(op, 0); _curY = GetFloat(op, 1);
            _ctx.LineTo(_curX, _curY);
        }

        private void OpC(ContentStreamOperation op)
        {
            _curX = GetFloat(op, 4); _curY = GetFloat(op, 5);
            _ctx.CurveTo(GetFloat(op,0), GetFloat(op,1),
                         GetFloat(op,2), GetFloat(op,3),
                         _curX, _curY);
        }

        private void OpV(ContentStreamOperation op)
        {
            // v: first control point = current point (per PDF spec)
            float x3 = GetFloat(op, 2), y3 = GetFloat(op, 3);
            _ctx.CurveTo(_curX, _curY,
                         GetFloat(op, 0), GetFloat(op, 1),
                         x3, y3);
            _curX = x3; _curY = y3;
        }

        private void OpY(ContentStreamOperation op)
        {
            // y: second control point = endpoint (per PDF spec)
            float x3 = GetFloat(op, 2), y3 = GetFloat(op, 3);
            _ctx.CurveTo(GetFloat(op, 0), GetFloat(op, 1),
                         x3, y3,
                         x3, y3);
            _curX = x3; _curY = y3;
        }

        private void OpRe(ContentStreamOperation op)
        {
            float x = GetFloat(op,0), y = GetFloat(op,1),
                  w = GetFloat(op,2), h = GetFloat(op,3);
            _ctx.AppendRect(x, y, w, h);
        }

        // ---- Path painting helpers ----

        private void ApplyPendingClip()
        {
            if (!_pendingClip) return;
            _pendingClip = false;
            _ctx.ClipPath(_pendingClipEvenOdd);
        }

        private void PaintStroke()
        {
            ApplyPendingClip();
            // Tiling pattern stroke
            string strokePat = SelectedPatternName(stroke: true);
            if (strokePat != null)
            {
                // A pattern paints its own cells, so the tints on the graphics state describe an
                // uncoloured pattern's underlying colour rather than what reaches the page.
                if (_ctx.GS.StrokeSepTints != null) _ctx.ReportInkNotMeasured("a pattern stroke");
                RenderTilingPattern(strokePat, stroke: true);
                // The selection is NOT cleared here. §8.6.8 gives painting operators no say over
                // the current colour: a pattern stays selected until a colour operator replaces
                // it. Clearing it left the graphics state's fill colour — black by default — as
                // the paint, so a second shape under one `scn` came out solid black while the SVG
                // writer, which only clears at colour operators, painted both. Measured before
                // this change: first square red, second #ff000000.
                return;
            }
            if (PatternSelectedNothing(stroke: true)) { _ctx.NewPath(); return; }
            DepositInkCoverage(fill: false, stroke: true);
            _ctx.StrokePath();
        }

        private void PaintFill(bool evenOdd)
        {
            ApplyPendingClip();
            // Tiling pattern fill
            string fillPat = SelectedPatternName(stroke: false);
            if (fillPat != null)
            {
                if (_ctx.GS.FillSepTints != null) _ctx.ReportInkNotMeasured("a pattern fill");
                RenderTilingPattern(fillPat, stroke: false, evenOdd: evenOdd);
                // Not cleared — see PaintStroke above for why, and §8.6.8.
                return;
            }
            if (PatternSelectedNothing(stroke: false)) { _ctx.NewPath(); return; }
            DepositInkCoverage(fill: true, stroke: false, evenOdd: evenOdd);
            _ctx.FillPath(evenOdd);
        }

        private void PaintFillStroke(bool evenOdd)
        {
            ApplyPendingClip();
            // Patterns handled per component
            string fillPatName   = SelectedPatternName(stroke: false);
            string strokePatName = SelectedPatternName(stroke: true);
            bool fillPattern   = fillPatName   != null;
            bool strokePattern = strokePatName != null;
            bool fillNothing   = PatternSelectedNothing(stroke: false);
            bool strokeNothing = PatternSelectedNothing(stroke: true);
            DepositInkCoverage(fill: !fillPattern && !fillNothing,
                               stroke: !strokePattern && !strokeNothing, evenOdd: evenOdd);
            // A pattern paint consumes the path — every exit from RenderTilingPattern ends in
            // NewPath() — so the fill half must be run against a copy, or the stroke half has no
            // geometry left. §8.5.3.1: B fills and strokes ONE path, as the SVG export emits both
            // paints on a single element.
            Action paintFill = fillPattern
                ? () => _ctx.PaintPreservingPath(() => RenderTilingPattern(fillPatName, stroke: false, evenOdd: evenOdd))
                : () => _ctx.FillPath(evenOdd, clearAfter: false);
            Action paintStroke = strokePattern
                ? () => RenderTilingPattern(strokePatName, stroke: true)
                : () => _ctx.StrokePath(clearAfter: true);

            if (!fillNothing && !strokeNothing)
            {
                // §11.7.4.4: one object — the stroke knocks out the fill, or the overprint case's group.
                _ctx.FillThenStroke(paintFill, paintStroke);
                return;
            }
            if (!fillNothing) paintFill();
            if (!strokeNothing) paintStroke();
            else _ctx.NewPath();
        }

        /// <summary>
        /// The pattern name selected for one paint side — the raster counterpart of the SVG
        /// writer's <c>stroke ? _gs.PatternNameStroke : _gs.PatternNameFill</c>.
        /// </summary>
        /// <remarks>
        /// The stroke's own slot, not a shared one, so a page that selected a fill pattern and then
        /// entered <c>/Pattern CS</c> without an <c>SCN</c> does not conclude a stroke pattern had been
        /// selected — the SVG side's answer too.
        /// </remarks>
        private string PatternNameFor(bool stroke)
            => stroke ? _ctx.GS.StrokePatternName : _ctx.GS.FillPatternName;

        /// <summary>
        /// The pattern this paint side must actually paint, or <c>null</c> — the <b>one</b> place
        /// the flag and the name are combined, shared with the SVG writer through
        /// <see cref="PatternPaint.SelectedName"/>.
        /// </summary>
        /// <remarks>
        /// Every raster pattern gate was already this conjunction, written out four times; the SVG
        /// writer's five gates read the name alone, so a stale name exported a pattern the renderer
        /// had correctly dropped. Routing both through one leaf is what stops that recurring.
        /// </remarks>
        private string SelectedPatternName(bool stroke)
            => PatternPaint.SelectedName(
                stroke ? _ctx.GS.StrokeIsPattern : _ctx.GS.FillIsPattern,
                PatternNameFor(stroke));

        /// <summary>
        /// True when the current colour is a Pattern space's <b>initial</b> colour — §8.6.8
        /// Table 74: "a pattern object that causes nothing to be painted". Set by <c>cs</c>/<c>CS</c>
        /// and cleared by the <c>scn</c> that names a pattern.
        /// </summary>
        /// <remarks>
        /// Painting had no notion of this: <c>FillIsPattern</c> with no pattern name simply fell
        /// through to the solid <see cref="GraphicsState.FillColor"/>, so <c>/Pattern cs … f</c>
        /// painted an opaque black rectangle where the spec paints nothing at all. Reported rather
        /// than silently skipped — a file that reaches here is malformed, and an invisible fill is
        /// exactly the kind of omission that stays invisible.
        /// </remarks>
        private bool PatternSelectedNothing(bool stroke)
        {
            bool isPattern = stroke ? _ctx.GS.StrokeIsPattern : _ctx.GS.FillIsPattern;
            if (!PatternPaint.SelectedNothing(isPattern, PatternNameFor(stroke))) return false;
            if (!_unselectedPatternReported)
            {
                _unselectedPatternReported = true;
                RenderDiagnostics.Report(RenderDiagnosticKind.PatternOperandNoName,
                    "Paint operator in a /Pattern colour space with no pattern selected by scn/SCN; " +
                    "nothing painted (§8.6.8 Table 74).");
            }
            return true;
        }

        // One-shot guard for the diagnostic above.
        private bool _unselectedPatternReported;

        /// <summary>
        /// Records the <c>/Separation</c> and <c>/DeviceN</c> ink the paint just issued lays down,
        /// for a host that attached an <see cref="InkAccumulator"/>.
        /// </summary>
        /// <remarks>
        /// The geometry lives in <see cref="PdfRenderContext"/> — it owns the path, the clip, the
        /// stroke pen and the page transform — so this is the dispatch, not the arithmetic. It used
        /// to be the arithmetic, and re-deriving the page transform here is what let it drift: the
        /// current path is already in <b>Y-down page-point space</b>, and this method flipped it a
        /// second time, so every map came out mirrored about the page's horizontal centre line.
        /// </remarks>
        private void DepositInkCoverage(bool fill, bool stroke, bool evenOdd = false)
        {
            if (_ctx.InkAccumulator == null) return;
            _ctx.DepositPathInk(fill, stroke, evenOdd);
        }

        // ---- Color ----
    }
}
