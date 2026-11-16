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
        private readonly PdfRenderContext    _ctx;
        private readonly PdfDocument         _doc;
        private readonly PdfDictionary       _resources;
        private readonly FontCache           _fontCache;

        // Current point in PDF user space (tracked for v/y curve operators).
        private float _curX, _curY;

        // Pending clip operator (W / W*): applied on the next painting operator.
        private bool _pendingClip;
        private bool _pendingClipEvenOdd;

        // One-shot guards for the parsed-but-not-honoured graphics-state parameters. Each may be
        // set on every `gs` in a stream, and reporting each occurrence would drown a host's
        // telemetry the way the inline-PS operator would.
        //
        // One flags field, mirrored by the SVG writer, and the gate plus the wording live
        // in IgnoredParameterReporting so the two paths cannot describe one gap two ways.
        private IgnoredGraphicsStateParameter _ignoredParametersReported;

        // NChannel keeps its own flag: it is a colour-space attribute, not a graphics-state
        // parameter, and it is reported from ContentStreamInterpreter.Color.
        private bool _nChannelReported;

        // Optional Content Group visibility: set of OCG object numbers that are HIDDEN.
        // When null or empty, all content is visible (default).
        internal System.Collections.Generic.HashSet<int> HiddenOcgs;

        // When > 0, we are inside one or more BDC /OC blocks whose OCG is hidden.
        // All operators are silently skipped until the matching EMC(s) bring it back to 0.
        private int _ocSkipDepth;

        // Type 3 CharProc: d0/d1 set the advance width; NaN = not yet set.
        private float _type3AdvanceWidth = float.NaN;
        private bool  _inType3CharProc;
        // Set by d1 inside a CharProc: the glyph is uncoloured (Type3GlyphColour).
        private bool  _type3Uncoloured;


        // ----------------------------------------------------------------
        //  Constructor
        // ----------------------------------------------------------------

        public ContentStreamInterpreter(
            PdfRenderContext ctx,
            PdfDocument doc,
            PdfDictionary resources,
            FontCache fontCache)
        {
            _ctx       = ctx;
            _doc       = doc;
            _resources = resources;
            _fontCache = fontCache;
        }

        /// <summary>
        /// Nesting depth of this interpreter relative to the top-level page
        /// content stream. Child interpreters created for Form XObjects, Type 3
        /// CharProcs, tiling patterns, and soft-mask groups carry the parent's
        /// depth + 1. Used to bound recursion: a form/pattern/Type3 glyph whose
        /// stream draws itself (or a mutual cycle) would otherwise recurse
        /// without limit and raise an uncatchable <see cref="System.StackOverflowException"/>.
        /// </summary>
        internal int RenderDepth { get; set; }

        /// <summary>
        /// The page's aggregate form-XObject invocation budget, shared by every nested interpreter
        /// this one creates. Null means unbudgeted, which is what a caller constructing an
        /// interpreter directly gets; every entry point in <see cref="PdfPageRenderer"/> supplies
        /// one.
        /// </summary>
        /// <remarks>
        /// <b><see cref="RenderDepth"/> bounds one branch and this bounds the number of branches</b>,
        /// which is why a depth cap alone left a 2^24 fan-out reachable from a few kilobytes of PDF.
        /// It cannot live on <see cref="PdfRenderContext"/>: a fresh context is built for every
        /// offscreen, tiling-pattern and soft-mask render, so the budget would reset in exactly the
        /// places the attack recurses through.
        /// </remarks>
        internal RenderBudget FormBudget { get; set; }

        // Bound image allocations the same way the page renderer bounds its bitmap: a
        // malformed /Width /Height (or a decode bomb) must not force a multi-GB SKColor[]
        // / SKBitmap.  Per-axis cap matches the page ceiling; the area cap mirrors
        // PdfOptimizer.LoadBitmapFromRawPixels so a crafted image can't pin a core or OOM.
        internal static int  MaxImageDimension => PdfLimits.MaxImageDimension;
        internal static long MaxImagePixelArea => PdfLimits.MaxImagePixelArea;

        // Maximum value of an 8-bit colour channel — the divisor that normalises a byte
        // sample to [0,1] and the scale that maps a normalised value back to a byte.
        private const int   MaxChannelValue  = 255;
        private const float MaxChannelValueF = 255f;

        // Number of distinct values an 8-bit channel can take — the length of every
        // per-channel lookup table built in this file.
        private const int ChannelValueCount = MaxChannelValue + 1;

        // Maximum value of a 16-bit colour channel (see Scale16To8).
        private const int   MaxChannelValue16  = 65535;
        private const float MaxChannelValue16F = 65535f;

        // ITU-R BT.601 luma weights, used to reduce a /Luminosity soft-mask group to a
        // single greyscale mask value (PDF §11.6.5.3). The values live in Core.Constants. Aliased rather than
        // inlined at the use site because the names are what make the sum readable.
        private const float LumaRedWeight   = PdfColorSpaceNames.LumaRedF;
        private const float LumaGreenWeight = PdfColorSpaceNames.LumaGreenF;
        private const float LumaBlueWeight  = PdfColorSpaceNames.LumaBlueF;

        // Bits in a byte, for the packed-sample readers.
        private const int BitsPerByte = 8;

        // PDF 32000-1:2008 defaults when a shading or image dictionary omits the
        // sample bit-widths (§8.7.4.5 and §8.9.5.1 respectively).
        private const int DefaultBitsPerCoordinate = 16;
        private const int DefaultBitsPerComponent  = 8;
        private const int DefaultBitsPerFlag       = 8;

        // Widest bit-field a mesh/patch shading may declare for a coordinate, colour
        // component or flag (PDF §8.7.4.5).  Also the widest `1L << n` mask that can be
        // formed without the shift count being masked to 6 bits, so an attacker-supplied
        // /BitsPerCoordinate of 64 cannot silently degenerate into `(1L << 0) - 1 == 0`.
        private const int MaxSampleBitWidth = 32;

        // The Tz operand is a percentage of normal horizontal scaling (PDF §9.3.4).
        private const float HorizontalScalingPercent = 100f;

        // The 500-unit fallback is Rendering/GlyphWidth.cs's, which both renderers call.

        // Gradient stops sampled from an axial/radial shading's colour function for the
        // RASTER path.  Unrelated to PdfLimits.SvgGradientStopCount: an SKShader gradient
        // is rasterised immediately at the current DPI, so more stops than this buy nothing.
        private const int RasterGradientStopCount = 64;

        // Alpha below (1 - this) is treated as genuinely translucent; the tolerance keeps a
        // ca/CA of 0.99999 from forcing a compositing layer for no visible benefit.
        private const float OpacityEpsilon = 1e-4f;

        /// <summary>
        /// Aborts the content-stream loop when the host cancels. Default (<c>None</c>) never
        /// cancels, so nothing changes for a caller that does not set it.
        /// </summary>
        /// <remarks>
        /// <b>It is checked here rather than in <see cref="Dispatch"/>, and the reason is the
        /// <c>catch</c> below.</b> That handler exists to fault-isolate one bad operator from the
        /// rest of the page, and it catches <see cref="Exception"/> — so an
        /// <see cref="OperationCanceledException"/> raised inside an operator would be swallowed,
        /// reported as a content-stream error, and the loop would carry on to the end. Checking
        /// outside the <c>try</c> is what makes cancellation actually stop the render.
        /// </remarks>
        public System.Threading.CancellationToken CancellationToken { get; set; }

        public void Execute(byte[] streamData)
        {
            if (streamData == null || streamData.Length == 0) return;

            // Memoised on the context, so a form drawn N times and a Type 3 glyph shown N times
            // tokenise once rather than N times. The list is shared — do not modify it here.
            var ops = _ctx.GetOrParseOperations(streamData);
            // Nested content — a form, a pattern cell, a soft-mask group, a glyph procedure — is
            // charged the operations it runs, as the SVG writer charges its own.
            if (RenderDepth > 0 && FormBudget != null && !FormBudget.TryChargeFormOperations(ops.Count)) return;
            ExecuteOps(ops);
        }

        /// <summary>
        /// Executes an already-resolved operation list: a caller that already has the (sub)list it wants skips re-tokenising entirely. Built on the
        /// same <see cref="ExecuteOps"/> <see cref="Execute(byte[])"/> calls, so fault isolation
        /// and cancellation behave identically on a filtered list as on the whole stream — there is
        /// one implementation of "run these operators", not two that could drift apart.
        /// </summary>
        public void Execute(IReadOnlyList<ContentStreamOperation> ops) => ExecuteOps(ops);

        /// <summary>
        /// The op index <see cref="ExecuteOps"/> is currently dispatching, or -1 outside a call:
        /// <see cref="BuildDisplayList"/> reads this
        /// from its <see cref="PdfRenderContext.PaintBoundsRecorder"/> callback to tag a reported
        /// bound with the operator that produced it, without either method carrying a duplicate
        /// dispatch loop.
        /// </summary>
        internal int CurrentOpIndex { get; private set; } = -1;

        /// <summary>
        /// Called with an operation's index once it has run, its graphics and text state then in
        /// force. Only this interpreter's own operations: a form, a pattern cell and a Type 3 glyph
        /// procedure each run in an interpreter of their own.
        /// </summary>
        internal Action<int> OperationExecuted { get; set; }

        /// <summary>
        /// How much further along the line the pen has been moved, in text space, than the font's own
        /// widths say (§9.4.4), by text shown in a substituted face — whose advance is the
        /// substitute's measure — since <see cref="TakeAdvanceBeyondWidths"/> last took it.
        /// </summary>
        private float _advanceBeyondWidths;

        /// <summary>
        /// The pen's move beyond the font's own widths since the last call, and none after it: what
        /// a caller subtracts from the text matrix's move to get the advance the file itself defines,
        /// whatever face this host drew the text in.
        /// </summary>
        internal float TakeAdvanceBeyondWidths()
        {
            float beyond = _advanceBeyondWidths;
            _advanceBeyondWidths = 0f;
            return beyond;
        }

        private void ExecuteOps(IReadOnlyList<ContentStreamOperation> ops)
        {
            if (ops == null || ops.Count == 0) return;

            bool cancellable = CancellationToken.CanBeCanceled;
            int sinceCheck = 0;
            int savedOpIndex = CurrentOpIndex;
            // Every paint on the context this stream draws into — the page's, or an offscreen cell's or
            // mask's — spends the page's paint work (PdfLimits.MaxPaintWorkPerPage).
            if (FormBudget != null) _ctx.PaintBudget = FormBudget;
            // Every function the page's operators evaluate — a tint transform a pixel, a shading a
            // sample — spends the page's budget, not one each (PdfLimits.MaxFunctionWorkPerPage).
            using var functionWork = PdfFunctionEvaluator.SpendFrom(FormBudget?.FunctionWork);
            using var charstringWork = Core.Fonts.CharstringWork.SpendFrom(FormBudget?.CharstringWork);
            // Every report made while they run counts against the page's per-kind allowance
            // (PdfLimits.MaxRenderReportsPerKindPerPage).
            using var reports = RenderDiagnostics.ForPage(FormBudget);
            try
            {
                for (int i = 0; i < ops.Count; i++)
                {
                    CurrentOpIndex = i;
                    var op = ops[i];

                    // RenderCancellation, which the SVG writer's loop also calls. The cadence and
                    // the "poll OUTSIDE the per-operator try" rule are one decision for both
                    // renderers, and this loop had its own copy of both — an inline poll and a
                    // private CancellationCheckInterval — so the shared leaf added by the same
                    // round that needed it had exactly one user.
                    RenderCancellation.Poll(ref sinceCheck, cancellable, CancellationToken);

                    try { Dispatch(op); }
                    catch (OperationCanceledException)
                    {
                        // Cancellation is not a content-stream error and must not be fault-isolated.
                        // A form XObject, a Type 3 glyph and a tiling pattern cell each run a NESTED
                        // Execute from inside Dispatch, so the nested loop's own token check threw
                        // straight into the catch below: the parent reported a fabricated
                        // ContentStreamError for a file that has none, carried on to the next
                        // operator, and - because OpDo's PushState/BeginTransparencyGroup were
                        // unwound past without their matching Pop/End - kept drawing with an
                        // unbalanced graphics state and layer stack. The remark on
                        // CancellationToken says the check sits outside the try for exactly this
                        // reason; that only ever held for the outermost stream.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Fault-isolate individual operators so one bad command (malformed
                        // arguments, unsupported feature, missing resource) does not abort
                        // all subsequent content on the page.  Surface via diagnostics.
                        RenderDiagnostics.Report(
                            RenderDiagnosticKind.ContentStreamError,
                            $"Operator '{op.Operator}' threw: {ex.Message}", ex);
                    }
                    OperationExecuted?.Invoke(i);
                }
            }
            finally
            {
                CurrentOpIndex = savedOpIndex;
                // A hidden section the stream never closed (BDC with no EMC) ends with the stream,
                // or the caller's own content after a nested Execute would paint nothing.
                if (_ocSkipDepth > 0)
                {
                    _ocSkipDepth = 0;
                    if (_ctx.HiddenContentDepth > 0) _ctx.HiddenContentDepth--;
                }
            }

            // A stream that opens a text object and never closes it (BT with no ET) would
            // otherwise leave the §9.3.8 knockout layer's canvas save open, and this Execute may
            // be a nested one — a form XObject, a Type 3 glyph, a tiling-pattern cell — whose
            // caller goes on drawing into a canvas one save deeper than it thinks it is.
            EndTextKnockout();
        }

        /// <summary>
        /// Runs <paramref name="ops"/> once against this interpreter's own <see cref="PdfRenderContext"/>,
        /// recording every paint call's page-point-space bound (see <see cref="DisplayListEntry.PageBounds"/>)
        /// — the display list. Painting still happens: nothing here
        /// skips a canvas call (see <see cref="PdfRenderContext.PaintBoundsRecorder"/>'s remarks for
        /// why), so a caller pays one real execution to get the list, same as rendering the
        /// operators once would cost.
        /// </summary>
        /// <remarks>
        /// <b>Every painting entry point on <see cref="PdfRenderContext"/> reports a bound.</b>
        /// <c>FillPath</c>, <c>StrokePath</c>, <c>DrawPatternPath</c> (a pattern-filled or
        /// -stroked path is still exactly <c>_currentPath</c>'s own geometry, so all three share
        /// one implementation, <c>ReportedPathBounds</c>), <c>DrawBitmap</c>, <c>DrawTextBlob</c>
        /// (both of <c>DrawGlyphRunWithWidths</c>'s callers), <c>DrawGlyphPath</c> (Type 1/CFF
        /// outlines) and <c>DrawShading</c> (<c>sh</c> — its bound is the current clip, since the
        /// operator has no geometry of its own) — every paint operator. <b>Even so, a consumer of this list must treat any op index with no entry here
        /// as unconditionally paintable-everywhere, not as provably empty</b> — every bound is
        /// conservative by construction, and two cases need extra care beyond what each method above already does: text-clip unioning (a later operator
        /// in the same <c>BT</c>...<c>ET</c> can paint into a clip a mode-4-7 run set, with no
        /// paint call of its own to hook) and a non-isolated transparency group's backdrop read
        /// (not a paint method at all -- it lives in <c>Do</c>'s own dispatch, not in anything
        /// <see cref="PdfRenderContext"/> exposes).
        /// </remarks>
        internal List<DisplayListEntry> BuildDisplayList(IReadOnlyList<ContentStreamOperation> ops)
        {
            var entries = new List<DisplayListEntry>();
            Action<SKRect> previous = _ctx.PaintBoundsRecorder;
            _ctx.PaintBoundsRecorder = bounds => entries.Add(new DisplayListEntry(CurrentOpIndex, bounds));
            try { ExecuteOps(ops); }
            finally { _ctx.PaintBoundsRecorder = previous; }
            return entries;
        }

        /// <summary>
        /// Executes a Type 3 CharProc stream.  d0/d1 operators inside the stream
        /// set <paramref name="advanceWidth"/> to the glyph's horizontal advance (in
        /// glyph space units, before FontMatrix scaling). Returns NaN if not set.
        /// </summary>
        public void ExecuteType3CharProc(byte[] streamData, out float advanceWidth)
        {
            _inType3CharProc  = true;
            _type3Uncoloured  = false;
            _type3AdvanceWidth = float.NaN;
            try { Execute(streamData); }
            finally { _inType3CharProc = false; _type3Uncoloured = false; }
            advanceWidth = _type3AdvanceWidth;
        }

        // ----------------------------------------------------------------
        //  Dispatch
        // ----------------------------------------------------------------

        private void Dispatch(ContentStreamOperation op)
        {
            // Inside a hidden BDC /OC section (§8.11.3.2) every operator still runs — the graphics
            // state operators apply and the text position moves, the clause's own words — and
            // nothing paints: the context's HiddenContentDepth gates each paint. Only the
            // structural BMC/BDC/EMC are consumed here, to keep the nesting depth.
            if (_ocSkipDepth > 0)
            {
                switch (op.Operator)
                {
                    case PdfOperators.BeginMarkedContent:
                    case PdfOperators.BeginMarkedContentProps: _ocSkipDepth++; return;
                    case PdfOperators.EndMarkedContent:
                        if (--_ocSkipDepth == 0) _ctx.HiddenContentDepth--;
                        return;
                }
            }

            // After d1 a Type 3 glyph is uncoloured: its colour operators are ignored (§9.6.5).
            if (_type3Uncoloured && Type3GlyphColour.IsColourOperator(op.Operator)) return;

            // An operand past float's range skips the operation whole, on both sides (MatrixGuard).
            if (MatrixGuard.HasNonFiniteOperand(op))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    op.Operator + " ignored: an operand past the range of a float");
                return;
            }

            switch (op.Operator)
            {
                // ---- Graphics state ----
                case PdfOperators.Save:         _ctx.PushState(); break;
                case PdfOperators.Restore:      _pendingClip = false; _ctx.PopState();  break;
                case PdfOperators.ConcatMatrix: OpCm(op);  break;
                case PdfOperators.SetLineWidth: OpSetLineWidth(op);  break;
                case PdfOperators.SetLineCap:   OpSetLineCap(op);    break;
                case PdfOperators.SetLineJoin:  OpSetLineJoin(op);   break;
                case PdfOperators.SetMiterLimit: OpSetMiterLimit(op); break;
                case PdfOperators.SetDash:      OpD(op);   break;
                case PdfOperators.SetRenderingIntent: { var riName = GetName(op, 0); if (riName != null) SetRenderingIntent(riName); } break;
                case PdfOperators.SetFlatness:  break; // flatness — ignored
                case PdfOperators.SetExtGState: OpSetExtGState(op);  break;

                // ---- Path construction ----
                case PdfOperators.MoveTo:    OpM(op);   break;
                case PdfOperators.LineTo:    OpL(op);   break;
                case PdfOperators.CurveTo:   OpC(op);   break;
                case PdfOperators.CurveToV:  OpV(op);   break;
                case PdfOperators.CurveToY:  OpY(op);   break;
                case PdfOperators.ClosePath: _ctx.ClosePath(); break;
                case PdfOperators.Rectangle: OpRe(op);  break;

                // ---- Path painting ----
                case PdfOperators.Stroke:      PaintStroke();         break;
                case PdfOperators.CloseStroke: _ctx.ClosePath(); PaintStroke(); break;
                case PdfOperators.Fill:
                case PdfOperators.FillObsolete:  PaintFill(false);      break;
                case PdfOperators.FillEvenOdd:   PaintFill(true);       break;
                case PdfOperators.FillStroke:        PaintFillStroke(false); break;
                case PdfOperators.FillStrokeEvenOdd: PaintFillStroke(true);  break;
                case PdfOperators.CloseFillStroke:        _ctx.ClosePath(); PaintFillStroke(false); break;
                case PdfOperators.CloseFillStrokeEvenOdd: _ctx.ClosePath(); PaintFillStroke(true);  break;
                case PdfOperators.EndPath: ApplyPendingClip(); _ctx.NewPath(); _curX = 0; _curY = 0; break;

                // ---- Clipping ----
                case PdfOperators.Clip:        _pendingClip = true; _pendingClipEvenOdd = false; break;
                case PdfOperators.ClipEvenOdd: _pendingClip = true; _pendingClipEvenOdd = true;  break;

                // ---- Color ----
                case PdfOperators.SetGrayFill:     OpSetGrayFill(op);   break;
                case PdfOperators.SetGrayStroke:   OpSetGrayStroke(op); break;
                case PdfOperators.SetRgbFill:      OpSetRgbFill(op);    break;
                case PdfOperators.SetRgbStroke:    OpSetRgbStroke(op);  break;
                case PdfOperators.SetCmykFill:     OpSetCmykFill(op);   break;
                case PdfOperators.SetCmykStroke:   OpSetCmykStroke(op); break;
                case PdfOperators.SetColorSpaceFill:   OpSetCS(op, stroke: false); break;
                case PdfOperators.SetColorSpaceStroke: OpSetCS(op, stroke: true);  break;
                case PdfOperators.SetColorFill:
                case PdfOperators.SetColorFillN:   OpSc(op, stroke: false); break;
                case PdfOperators.SetColorStroke:
                case PdfOperators.SetColorStrokeN: OpSc(op, stroke: true);  break;

                // ---- Text operators ----
                case PdfOperators.BeginText: BeginText(); break;
                case PdfOperators.EndText:   EndText();   break;
                case PdfOperators.SetFont:           OpTf(op);   break;
                case PdfOperators.TextMove:          OpTd(op);   break;
                case PdfOperators.TextMoveSetLeading: OpTD(op);  break;
                case PdfOperators.SetTextMatrix:     OpTm(op);   break;
                case PdfOperators.NextLine:          OpTStar();  break;
                case PdfOperators.ShowText:          OpTj(op);   break;
                case PdfOperators.ShowTextArray:     OpTJ(op);   break;
                case PdfOperators.NextLineShowText:  OpQuote(op); break;
                case PdfOperators.NextLineSetSpacingShowText: OpDoubleQuote(op); break;
                case PdfOperators.SetCharSpacing:      _ctx.GS.CharSpacing  = GetFloat(op, 0); break;
                case PdfOperators.SetWordSpacing:      _ctx.GS.WordSpacing  = GetFloat(op, 0); break;
                case PdfOperators.SetHorizontalScaling: _ctx.GS.HorizScaling = GetFloat(op, 0) / HorizontalScalingPercent; break;
                case PdfOperators.SetLeading:          _ctx.GS.Leading      = GetFloat(op, 0); break;
                case PdfOperators.SetTextRenderMode:   _ctx.GS.TextRenderMode = GetInt(op, 0); break;
                case PdfOperators.SetTextRise:         _ctx.GS.TextRise     = GetFloat(op, 0); break;

                // ---- XObject ----
                case PdfOperators.InvokeXObject: OpDo(op); break;

                // ---- Inline image ----
                case PdfOperators.BeginInlineImage: OpBi(op); break;

                // ---- Shading ----
                case PdfOperators.ShadingFill: OpSh(op); break;

                // ---- Type 3 glyph width operators ----
                // d0 = setcharwidth (wx wy), d1 = setcachedevice (wx wy llx lly urx ury)
                // These appear only inside Type 3 CharProc streams and set the advance width.
                case PdfOperators.SetCharWidth:   if (_inType3CharProc && op.Operands.Length >= 1) _type3AdvanceWidth = GetFloat(op, 0); break;
                case PdfOperators.SetCacheDevice:
                    if (_inType3CharProc && op.Operands.Length >= 1) _type3AdvanceWidth = GetFloat(op, 0);
                    _type3Uncoloured = _inType3CharProc;
                    break;

                // ---- Ignored / unsupported ----
                case PdfOperators.BeginCompat:
                case PdfOperators.EndCompat: break;    // compatibility sections

                // ---- Marked content operators ----
                case PdfOperators.BeginMarkedContent:      break;
                case PdfOperators.BeginMarkedContentProps: OpBdc(op); break;
                case PdfOperators.EndMarkedContent:        break;

                // Point marked-content operators (PDF 32000-1 §14.6.2). These annotate a
                // single point in the content stream with a tag/properties but do not open
                // a block, and paint nothing.
                case PdfOperators.MarkedPoint:
                case PdfOperators.MarkedPointProps:
                    break;

                // Legacy PDF 1.1 `PS` operator — a PostScript passthrough that ran the
                // preceding string on the RIP.  Deprecated since PDF 1.2 and rare in
                // modern documents.  A registered IPostScriptPlugin can execute it; with
                // no plugin it stays a no-op so content streams that still carry it don't
                // abort parsing.  The string operand has already been popped onto the
                // operand stack by ContentStreamParser.
                case PdfOperators.PostScript:
                    OpPs(op);
                    break;

                default: break;
            }
        }

        // ================================================================
        //  Operator implementations
        // ================================================================

        /// <summary>
        /// Looks up <paramref name="name"/> inside the <paramref name="category"/> sub-dictionary
        /// of the current /Resources (e.g. <c>/ExtGState</c>, <c>/XObject</c>, <c>/Properties</c>).
        /// Takes the category as a <see cref="PdfName"/> so call sites use the cached
        /// <c>PdfName.*</c> statics instead of re-allocating one per lookup.
        /// </summary>
        private PdfDictionary ResolveResourceDict(PdfName category, string name)
        {
            var entry = ResolveResourceEntry(category, name);
            if (entry == null) return null;
            return _doc.ResolveObject(entry) as PdfDictionary ?? entry as PdfDictionary;
        }

        /// <summary>
        /// Returns the <b>unresolved</b> resource entry, so callers that need the object number
        /// (optional-content groups are identified by it) keep the reference.
        /// </summary>
        private PdfObject ResolveResourceEntry(PdfName category, string name)
        {
            if (_resources == null || name == null) return null;
            var cat = _resources[category];
            if (cat == null) return null;
            if (_doc.ResolveObject(cat) is not PdfDictionary catDict) return null;
            // PdfDictionary's internal string indexer exists precisely so Core/Base can look a
            // name up without allocating a PdfName; Base is in Core's InternalsVisibleTo list.
            // This is the single shared resource lookup, reached once per resource-naming
            // operator, so an allocation here would be one per operator.
            return catDict[name];
        }

        // ================================================================
        //  Operand helpers
        // ================================================================

        // Operand coercion lives in Core.Text.ContentOperand (single source of
        // truth shared with PdfSvgRenderer / PdfGraphicsExtractor). These thin
        // forwarders keep the call sites in this file readable and unchanged.
        private static float GetFloat(ContentStreamOperation op, int idx) => ContentOperand.Float(op, idx);

        private static int GetInt(ContentStreamOperation op, int idx) => ContentOperand.Int(op, idx);

        private static string GetName(ContentStreamOperation op, int idx) => ContentOperand.Name(op, idx);

        private static byte[] GetStringBytes(PdfObject obj) => ContentOperand.StringBytes(obj);

        private float GetArrayFloat(PdfArray arr, int idx) => PdfResolve.ArrayFloat(_doc, arr, idx);

        private static float Clamp(float v) => PdfColor.Clamp01(v);

        // Returns the standard char code for the first component of a Latin ligature,
        // or 0 if the char is not a recognised ligature.  Used to render a visible
        // fallback when the embedded font lacks the combined ligature glyph.
        // Delegates to the shared GlyphIdResolver so the mapping has one definition.
        private static int LigatureFirstComponentCode(char decoded)
            => GlyphIdResolver.LigatureFirstComponentCode(decoded);

        // ---- Marked content operators ----

    }
}

