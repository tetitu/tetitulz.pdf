// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Base.Images;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.Functions;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Text;
using Tetitulz.Pdf.Core.Util;
using HashSetInt = System.Collections.Generic.HashSet<int>;
using SKPath = SkiaSharp.SKPath;
using SKColor = SkiaSharp.SKColor;
using SKFont = SkiaSharp.SKFont;
using SKTypeface = SkiaSharp.SKTypeface;
using SKPoint = SkiaSharp.SKPoint;
using SKPathVerb = SkiaSharp.SKPathVerb;

namespace Tetitulz.Pdf.Base.Rendering
{
    /// <summary>
    /// Interprets a PDF content stream into SVG elements — the vector counterpart of
    /// <see cref="ContentStreamInterpreter"/>, which does the same job onto a Skia canvas.
    ///
    /// <para><b>A type of its own, split as the raster interpreter is</b> — .Path, .Text,
    /// .Shading, .Images and the rest — so a change to one renderer and not the other is a
    /// two-file diff. A silently wrong output is, as a rule, one renderer lagging the other.</para>
    ///
    /// <para>It stays <c>internal</c>. Base's three <c>InternalsVisibleTo</c> friends are all
    /// in-repo — the test project, FontDiag and the benchmarks — so no CONSUMER could see it
    /// before and none can now; promoting it changed its nesting, not its visibility.</para>
    /// </summary>
internal sealed partial class SvgContentWriter
    {
        // One implementation, in SvgSyntax. These forwarders exist because the call sites are
        // in the hundreds and this repo bans `using static`; an alias is not a second
        // implementation.
        //
        // Which is the argument for a METHOD with many callers, and no argument at all for a
        // constant with none. The `DefaultMiterLimit` and `HairlineStrokeWidth` aliases that used
        // to sit here had zero readers between them, and the first was worse than inert: a
        // constant named "the default miter limit", holding PDF's 10, inside the class whose
        // exporter must compare against SVG's 4 — the exact false premise this slice removed.
        // Name SvgSyntax directly for a one-off read.
        private static string XmlEsc(string s) => SvgSyntax.XmlEsc(s);
        private static string F(float v) => SvgSyntax.F(v);
        private static float StrokeWidthOrHairline(float lineWidth, float ctmScale)
            => SvgSyntax.StrokeWidthOrHairline(lineWidth, ctmScale);

        private readonly StringBuilder   _sb;
        private readonly PdfDocument     _doc;
        private readonly PdfDictionary   _resources;
        private readonly float           _pageW;
        private readonly float           _pageH;

        // Graphics state
        private readonly Stack<SvgGs> _gsStack = new Stack<SvgGs>();
        private SvgGs _gs = new SvgGs();

        // Current path (accumulated SVG path data)
        private readonly StringBuilder _path = new StringBuilder();
        private float _curX, _curY;

        // Optional Content Group visibility (hidden OCG object numbers)
        internal HashSetInt HiddenOcgs;

        // Shared <defs>/<use> registry for the whole page (propagated to every child writer —
        // form XObjects, patterns, soft masks, Type 3 glyphs — so a repeated image or glyph is
        // emitted once and referenced thereafter, shrinking the output dramatically).
        internal SvgSharedState Shared;

        // BX/EX compatibility section depth
        private int _bxDepth;

        // Fallback id counter, used only when no shared registry was supplied (a defensively
        // constructed writer). See NextId.
        private int _localIdCounter;

        /// <summary>
        /// The next generated-element id number.
        ///
        /// <para><b>It must come from the shared registry.</b> A page emits one document but
        /// runs many writers — every form XObject, pattern cell, soft mask, Type 3 glyph and
        /// annotation appearance is a child writer with its own fields. A per-writer
        /// counter restarts at 0 in each of them, so a page that clips and also invokes a form
        /// that clips would emit two <c>id="clip1"</c>, and every <c>url(#clip1)</c> would resolve to
        /// whichever the document defined first. Every prefix — <c>clip</c>, <c>pat</c>,
        /// <c>smask</c>, <c>sg</c>, <c>txtclip</c>, <c>shpat</c>, <c>psclip</c> — comes from the
        /// registry.</para>
        /// </summary>
        private int NextId() => Shared != null ? ++Shared.IdCounter : ++_localIdCounter;

        // Pending clip from W / W*.  §8.5.4: the clipping path does not change until AFTER the
        // next painting operator, so the request is recorded here and ApplyPendingClip opens the
        // <g clip-path> group once that operator has emitted its element.
        //
        // Applied eagerly inside the W handler, which clears _path, `W f`, `W B` and `W S` would
        // reach PaintPath with an empty path and paint nothing. The raster interpreter defers it
        // too (ContentStreamInterpreter._pendingClip).
        private bool _pendingClip;
        private bool _pendingClipEvenOdd;

        // Accumulates glyph outlines (as <text>) across a BT..ET block when a clipping text
        // render mode (4-7) is active; flushed into one <clipPath> + clip <g> at ET so the
        // clip is the union of all runs (matching the bitmap renderer) rather than per-run.
        private StringBuilder _textClipAccum;
        private bool _textClipUsed;

        // Advance width set by d0/d1 inside a Type 3 CharProc stream.
        private float _t3AdvanceWidth = float.NaN;

        // Set by d1: this writer is running an uncoloured Type 3 glyph (Type3GlyphColour).
        private bool _t3Uncoloured;

        // This writer runs a Type 3 glyph's procedure (set by DrawType3GlyphsSvg on the child), the
        // only place d1 means anything — mirroring the raster's own in-CharProc flag.
        private bool _inCharProc;

        // The page's FontCache (SvgSharedState.Fonts), which every child writer draws through. A
        // writer with no registry — the defensive case, as NextId has — keeps its own, created on
        // first text show and disposed in Execute.
        private FontCache _fontCache;
        private FontCache Fonts => Shared?.Fonts ?? (_fontCache ??= new FontCache());

        // Nesting depth (Form XObject / pattern / soft-mask / Type 3). Bounds
        // recursion so a self-referential or cyclic XObject cannot raise an
        // uncatchable StackOverflowException during SVG export.
        internal int Depth { get; set; }

        /// <summary>
        /// Aborts the content-stream loop when the host cancels. Default (<c>None</c>) never cancels,
        /// so nothing changes for a caller that does not set it.
        /// </summary>
        /// <remarks>
        /// <para>A <b>per-writer</b> property rather than a field on <see cref="SvgSharedState"/>,
        /// because the raster twin is a per-instance property and the mirror is the point — the two
        /// renderers' partials are named alike so "did the SVG side get this too?" is a two-file
        /// diff rather than a search.</para>
        /// <para>It is polled in <see cref="Execute"/> <b>outside</b> the per-operator <c>try</c>,
        /// for the same reason the raster interpreter documents: the blanket
        /// <c>catch (Exception)</c> that fault-isolates one bad operator would otherwise swallow the
        /// cancellation, report a content-stream error the file does not have, and run the loop to
        /// the end.</para>
        /// </remarks>
        internal System.Threading.CancellationToken CancellationToken { get; set; }

        /// <summary>
        /// The ambient context every writer needs, set from the renderer that owns the page.
        /// </summary>
        /// <remarks>
        /// <para><b>One method because forgetting a field here has already been a defect.</b> The
        /// annotation-appearance writer once missed <see cref="CancellationToken"/>, so a page
        /// carrying one enormous <c>/AP</c> honoured the token for its content and ignored it for
        /// its annotations. <see cref="Shared"/> is worse: it owns the id counter, so a writer that
        /// misses it mints element ids colliding with the ones already emitted.</para>
        /// <para><b>The null-coalesce is a deliberate widening.</b> Null is not reachable today,
        /// because <c>RenderPage</c> coalesces before either
        /// runs — but <see cref="Options"/> carries a non-null default that a straight assignment
        /// would clobber, so taking the safer of the two makes both call sites identical rather
        /// than merely similar.</para>
        /// </remarks>
        internal void SetAmbient(HashSetInt hiddenOcgs, SvgSharedState shared,
            PdfRenderOptions options, System.Threading.CancellationToken cancellationToken)
        {
            HiddenOcgs        = hiddenOcgs;
            Shared            = shared;
            Options           = options ?? PdfRenderOptions.Default;
            CancellationToken = cancellationToken;
        }

        /// <summary>
        /// The same context, inherited by a writer running nested content — a form XObject, a
        /// tiling pattern, a soft mask or a Type 3 glyph procedure — one level deeper.
        /// </summary>
        /// <remarks>
        /// <see cref="Depth"/> is part of it because all four nested sites increment it and it
        /// bounds the recursion that stops a self-referential XObject overflowing the stack. The
        /// graphics state is deliberately <b>not</b>: three sites clone the parent's and the tiling
        /// pattern's depends on whether the pattern is uncoloured, so folding it in would be the
        /// half-consolidation that is worse than none.
        /// </remarks>
        private void InheritFrom(SvgContentWriter parent)
        {
            Depth = parent.Depth + 1;
            parent.CopyAmbientTo(this);
        }

        /// <summary>The four ambient fields, copied without the depth step.</summary>
        private void CopyAmbientTo(SvgContentWriter child)
            => child.SetAmbient(HiddenOcgs, Shared, Options, CancellationToken);

        // The Tz operand is a percentage of the normal horizontal scaling (PDF §9.3.4).
        private const float HorizontalScalingPercent = 100f;

        // Alpha within this of 1.0 counts as fully opaque, so `fill-opacity="1"` is not
        // emitted for a ca/CA of 0.9999.
        private const float OpaqueAlphaEpsilon = 0.001f;

        public SvgContentWriter(StringBuilder sb, PdfDocument doc,
                         PdfDictionary resources, float pageW, float pageH)
        {
            _sb        = sb;
            _doc       = doc;
            _resources = resources;
            _pageW     = pageW;
            _pageH     = pageH;
        }

        public void Execute(byte[] data)
        {
            // The page's function budget, as the raster interpreter's operator loop opens it.
            using var functionWork = Core.Functions.PdfFunctionEvaluator.SpendFrom(Shared?.FormBudget?.FunctionWork);
            using var charstringWork = Core.Fonts.CharstringWork.SpendFrom(Shared?.FormBudget?.CharstringWork);
            using var reports = RenderDiagnostics.ForPage(Shared?.FormBudget);
            try
            {
                var ops = ContentStreamParser.Parse(data);
                // Nested content is charged the operations it runs, as the raster interpreter charges its own.
                if (Depth > 0 && Shared?.FormBudget != null && !Shared.FormBudget.TryChargeFormOperations(ops.Count)) return;
                bool cancellable = CancellationToken.CanBeCanceled;
                int sinceCheck = 0;
                foreach (var op in ops)
                {
                    // OUTSIDE the try below, for the reason that catch documents: it fault-isolates
                    // one bad operator by catching Exception, so a cancellation raised inside
                    // Dispatch would be swallowed, reported as a content-stream error this file does
                    // not have, and the loop would run to the end. Same cadence and same shared
                    // constant as the raster interpreter.
                    RenderCancellation.Poll(ref sinceCheck, cancellable, CancellationToken);

                    // Per-operator fault isolation, as the raster interpreter has. Without it ONE
                    // bad operator would abort the whole export: a truncated /ImageMask, say,
                    // would throw out of Dispatch and propagate out of RenderPageToSvg, so the
                    // caller would get an exception instead of a page. The raster path loses one
                    // image and renders the rest.
                    try { Dispatch(op); }
                    catch (OperationCanceledException)
                    {
                        // Cancellation is not a content-stream error and must not be fault-isolated.
                        // A form XObject, a Type 3 glyph, a tiling-pattern cell and a soft mask each
                        // run a NESTED Execute from inside Dispatch, so the nested loop's own poll
                        // throws straight into the handler below — the parent would report a
                        // fabricated error for a file that has none and carry on drawing. The poll
                        // sitting outside the try only ever protected the outermost stream.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                            $"SVG: operator '{op.Operator}' threw and was skipped", ex);
                    }
                }
            }
            finally
            {
                CloseOpenGroups();
                _fontCache?.Dispose();
                _fontCache = null;
            }
        }

        /// <summary>
        /// Closes every <c>&lt;g&gt;</c> this writer opened and never closed.
        ///
        /// <para>A clip or marked-content group is closed by the matching <c>Q</c>/<c>EMC</c>.
        /// Both are legal to omit at the end of a content stream — <c>… re W n …</c> at top
        /// level needs no <c>Q</c> at all — and without this the writer would emit an unbalanced
        /// document: <b>SVG that is not well-formed XML</b>, which a test searching the output for
        /// substrings cannot see; the tests parse it with <c>XDocument.Parse</c>.</para>
        /// </summary>
        private void CloseOpenGroups()
        {
            for (; _unclosedGroups > 0; _unclosedGroups--) _sb.Append("</g>\n");
            _gs.ClipGroupCount = 0;
            _gs.MarkedContentGroupCount = 0;
        }

        /// <summary>
        /// Groups this writer has opened and not yet closed.
        ///
        /// <para>Counted per <b>writer</b>, not per graphics state, because a child writer
        /// (form XObject, pattern cell, soft mask, Type 3 glyph) starts from
        /// <c>_gs.Clone()</c> and would otherwise inherit the parent's open-group count and
        /// close groups it never opened.</para>
        /// </summary>
        private int _unclosedGroups;

        // ----------------------------------------------------------------
        private void Dispatch(ContentStreamOperation op)
        {
            // BX/EX compatibility section: skip unknown operators between BX…EX.
            // BX/EX is NOT a suppression block. §7.8.2 Table 33: it marks a section in which
            // UNRECOGNISED operators shall be ignored — recognised ones still execute, so a block
            // wrapped in BX … EX is exported. Both renderers treat BX and EX as no-ops; unknown operators
            // already fall through this writer's switch harmlessly.
            if (op.Operator == PdfOperators.BeginCompat) { _bxDepth++; return; }
            if (op.Operator == PdfOperators.EndCompat)   { if (_bxDepth > 0) _bxDepth--; return; }

            // A hidden BDC /OC section (§8.11.3.2) is exported as a group that is not displayed
            // (OpBdc), so every operator in it runs: the graphics state operators apply and the
            // text position moves, as the clause says, and the content after it lands where a
            // reader puts it.

            // After d1 a Type 3 glyph is uncoloured: its colour operators are ignored (§9.6.5).
            if (_t3Uncoloured && Type3GlyphColour.IsColourOperator(op.Operator)) return;

            // An operand past float's range skips the operation whole, on both sides (MatrixGuard).
            if (MatrixGuard.HasNonFiniteOperand(op))
            {
                RenderDiagnostics.Report(RenderDiagnosticKind.ContentStreamError,
                    op.Operator + " ignored: an operand past the range of a float");
                return;
            }

            switch (op.Operator)
            {
                case PdfOperators.Save:        OpQSave(); break;
                case PdfOperators.Restore:     OpQ(); break;
                case PdfOperators.ConcatMatrix: OpCm(op); break;

                case PdfOperators.SetLineWidth:  _gs.LineWidth  = Flt(op, 0); break;
                // Saturated, as the raster's reads are (ContentOperand.Int): the low 32 bits of
                // 4294967296 are 0, a valid cap, join or render mode the file never asked for.
                case PdfOperators.SetLineCap:    _gs.LineCap    = ContentOperand.Int(op, 0); break;
                case PdfOperators.SetLineJoin:   _gs.LineJoin   = ContentOperand.Int(op, 0); break;
                case PdfOperators.SetMiterLimit: _gs.MiterLimit = Flt(op, 0); break;
                case PdfOperators.SetDash:       OpDash(op); break;
                // The `ri` operator and ExtGState /RI set the same graphics-state parameter, so both
                // must reach SetRenderingIntent, as both do in the raster path
                // (ContentStreamInterpreter's Dispatch routes it to the same helper).
                case PdfOperators.SetRenderingIntent:
                {
                    string riName = ContentOperand.Name(op, 0);
                    if (riName != null) SetRenderingIntent(riName);
                    break;
                }
                case PdfOperators.SetFlatness:        break; // flatness — SkiaSharp internal
                case PdfOperators.SetExtGState:  OpGs(op);  break;

                case PdfOperators.MoveTo:    OpMoveTo(op);  break;
                case PdfOperators.LineTo:    OpLineTo(op);  break;
                case PdfOperators.CurveTo:   OpCurveTo(op); break;
                case PdfOperators.CurveToV:  OpV(op);       break;
                case PdfOperators.CurveToY:  OpY(op);       break;
                case PdfOperators.ClosePath: _path.Append("Z "); break;
                case PdfOperators.Rectangle: OpRect(op);   break;

                case PdfOperators.Stroke:      PaintPath(stroke: true,  fill: false, evenOdd: false); break;
                case PdfOperators.CloseStroke: _path.Append("Z "); PaintPath(stroke: true, fill: false, evenOdd: false); break;
                case PdfOperators.Fill:
                case PdfOperators.FillObsolete:      PaintPath(stroke: false, fill: true,  evenOdd: false); break;
                case PdfOperators.FillEvenOdd:       PaintPath(stroke: false, fill: true,  evenOdd: true);  break;
                case PdfOperators.FillStroke:        PaintPath(stroke: true,  fill: true,  evenOdd: false); break;
                case PdfOperators.FillStrokeEvenOdd: PaintPath(stroke: true,  fill: true,  evenOdd: true);  break;
                case PdfOperators.CloseFillStroke:        _path.Append("Z "); PaintPath(stroke: true, fill: true, evenOdd: false); break;
                case PdfOperators.CloseFillStrokeEvenOdd: _path.Append("Z "); PaintPath(stroke: true, fill: true, evenOdd: true);  break;
                case PdfOperators.EndPath: OpEndPath(); break;

                case PdfOperators.Clip:        OpClip(evenOdd: false); break;
                case PdfOperators.ClipEvenOdd: OpClip(evenOdd: true);  break;

                // FillIsCmyk/StrokeIsCmyk mirror the raster path's GraphicsState.FillCmyk: they
                // record whether the CURRENT colour was specified in DeviceCMYK, which
                // conversion to an sRGB string otherwise discards. Overprint simulation needs
                // it, because §11.7.4.2's nonzero-overprint rule applies only to DeviceCMYK.
                //
                // Each also leaves the Pattern space: `g`/`rg`/`k` select a device space
                // outright (§8.6.8), so an earlier `/Pattern cs` no longer governs the paint.
                // The raster operators clear GraphicsState.FillIsPattern for the same reason.
                // Every device colour operator also ends any colorant set the side was carrying:
                // the channel mask and the spot flag belong to a /Separation or /DeviceN colour
                // and must not outlive it, or a `k` after a magenta Separation would keep writing
                // "magenta only" and multiply under /OPM 0, where a DeviceCMYK source must not.
                case PdfOperators.SetGrayFill: if (PaintThroughDefault(PdfColorSpaceNames.DeviceGray, op, stroke: false)) break; _gs.FillColor   = GrayColor(Flt(op, 0)); _gs.FillIsCmyk   = false; _gs.FillIsPattern   = false; EndColorantSet(stroke: false); break;
                case PdfOperators.SetGrayStroke: if (PaintThroughDefault(PdfColorSpaceNames.DeviceGray, op, stroke: true)) break; _gs.StrokeColor = GrayColor(Flt(op, 0)); _gs.StrokeIsCmyk = false; _gs.StrokeIsPattern = false; EndColorantSet(stroke: true);  break;
                case PdfOperators.SetRgbFill: if (PaintThroughDefault(PdfColorSpaceNames.DeviceRGB, op, stroke: false)) break; _gs.FillColor   = RgbColor(Flt(op,0), Flt(op,1), Flt(op,2)); _gs.FillIsCmyk   = false; _gs.FillIsPattern   = false; EndColorantSet(stroke: false); break;
                case PdfOperators.SetRgbStroke: if (PaintThroughDefault(PdfColorSpaceNames.DeviceRGB, op, stroke: true)) break; _gs.StrokeColor = RgbColor(Flt(op,0), Flt(op,1), Flt(op,2)); _gs.StrokeIsCmyk = false; _gs.StrokeIsPattern = false; EndColorantSet(stroke: true);  break;
                case PdfOperators.SetCmykFill: if (PaintThroughDefault(PdfColorSpaceNames.DeviceCMYK, op, stroke: false)) break; _gs.FillColor   = CmykColor(Flt(op,0), Flt(op,1), Flt(op,2), Flt(op,3)); _gs.FillIsCmyk   = true; _gs.FillIsPattern   = false; EndColorantSet(stroke: false); break;
                case PdfOperators.SetCmykStroke: if (PaintThroughDefault(PdfColorSpaceNames.DeviceCMYK, op, stroke: true)) break; _gs.StrokeColor = CmykColor(Flt(op,0), Flt(op,1), Flt(op,2), Flt(op,3)); _gs.StrokeIsCmyk = true; _gs.StrokeIsPattern = false; EndColorantSet(stroke: true);  break;
                case PdfOperators.SetColorSpaceFill:   OpSetCs(op, stroke: false); break;
                case PdfOperators.SetColorSpaceStroke: OpSetCs(op, stroke: true);  break;
                case PdfOperators.SetColorFill:   case PdfOperators.SetColorFillN:   OpSc(op, stroke: false); break;
                case PdfOperators.SetColorStroke: case PdfOperators.SetColorStrokeN: OpSc(op, stroke: true);  break;

                case PdfOperators.ShadingFill: OpSh(op); break;

                case PdfOperators.BeginText: BeginText(); break;
                case PdfOperators.EndText:   EndText();   break;
                case PdfOperators.SetFont:   OpTf(op);    break;
                case PdfOperators.TextMove:           OpTd(op, updateLeading: false); break;
                case PdfOperators.TextMoveSetLeading: OpTd(op, updateLeading: true);  break;
                case PdfOperators.SetTextMatrix: OpTm(op);    break;
                case PdfOperators.NextLine:      OpTStar();   break;
                case PdfOperators.ShowText:      OpTj(op);    break;
                case PdfOperators.ShowTextArray: OpTJ(op);    break;
                case PdfOperators.NextLineShowText: OpTStar(); OpTj(op);  break;
                case PdfOperators.SetTextRenderMode: _gs.TextRenderMode = ContentOperand.Int(op, 0); OnTextRenderModeChanged(); break;
                case PdfOperators.NextLineSetSpacingShowText:
                    if (op.Operands.Length >= 3)
                    {
                        _gs.WordSpacing = Flt(op, 0);
                        _gs.CharSpacing = Flt(op, 1);
                        OpTStar();
                        var strOp = new ContentStreamOperation(PdfOperators.ShowText,
                            new PdfObject[] { op.Operands[2] });
                        OpTj(strOp);
                    }
                    break;
                case PdfOperators.SetCharSpacing:       _gs.CharSpacing = Flt(op, 0); break;
                case PdfOperators.SetWordSpacing:       _gs.WordSpacing = Flt(op, 0); break;
                case PdfOperators.SetHorizontalScaling: _gs.HorizScale  = Flt(op, 0) / HorizontalScalingPercent; break;
                case PdfOperators.SetLeading:           _gs.Leading     = Flt(op, 0); break;
                case PdfOperators.SetTextRise:          _gs.TextRise    = Flt(op, 0); break;

                case PdfOperators.InvokeXObject:    OpDo(op); break;
                case PdfOperators.BeginInlineImage: OpBi(op); break;

                case PdfOperators.BeginMarkedContent:      OpBmc(op); break;
                case PdfOperators.BeginMarkedContentProps: OpBdc(op); break;
                case PdfOperators.EndMarkedContent:        OpEmc();   break;
                case PdfOperators.MarkedPoint:
                case PdfOperators.MarkedPointProps: break;   // marked-content point operators

                case PdfOperators.BeginCompat: _bxDepth++; break;
                case PdfOperators.EndCompat:   if (_bxDepth > 0) _bxDepth--; break;

                // Type 3 font glyph advance operators.
                case PdfOperators.SetCharWidth:   _t3AdvanceWidth = op.Operands.Length >= 1 ? Flt(op, 0) : float.NaN; break;
                // Only a Type 3 glyph's own writer is marked uncoloured by d1 (§9.6.5): a stray d1 on
                // a page marks nothing, as the raster gates its flag on being inside a CharProc.
                case PdfOperators.SetCacheDevice: _t3AdvanceWidth = op.Operands.Length >= 1 ? Flt(op, 0) : float.NaN; _t3Uncoloured = _inCharProc; break;

                // Legacy PS passthrough operator (PDF 1.1, deprecated in 1.2).
                // A registered IPostScriptPlugin can execute it; with no plugin it
                // stays a no-op so parsers don't abort on documents that still carry
                // embedded PostScript draw code.
                case PdfOperators.PostScript: OpPs(op); break;
            }
        }

        // ---- CTM ----

        private (float x, float y) TxPt(float x, float y) => GraphicsState.Transform(_gs.CTM, x, y);

        /// <summary>
        /// <see cref="TxPt"/> under an explicit matrix — used for a form's <c>/BBox</c>, which
        /// is in form space and so must go through <c>/Matrix ∘ CTM</c>, not the CTM alone.
        /// </summary>
        private static (float x, float y) TxPtWith(float[] ctm, float x, float y)
            => GraphicsState.Transform(ctm, x, y);

        /// <summary>
        /// Emits a <c>&lt;defs&gt;&lt;clipPath&gt;</c> holding a <c>/BBox</c> mapped through
        /// <paramref name="ctm"/>, and returns its id — or <c>null</c> when the array is not a
        /// usable box, in which case nothing is written.
        /// </summary>
        /// <param name="bbox">The <c>/BBox</c> array; fewer than four entries writes nothing.</param>
        /// <param name="ctm">
        /// The matrix the corners go through. Pass <c>_gs.CTM</c> for a box already in the current
        /// space, or <c>/Matrix ∘ CTM</c> for one in form space.
        /// </param>
        /// <param name="idPrefix">Distinguishes the emitted ids per call site.</param>
        /// <param name="openGroup">
        /// Whether to follow the definition with <c>&lt;g clip-path&gt;</c>. The soft-mask caller
        /// does not: it holds the id and applies it to a group it opens later, with other
        /// attributes.
        /// </param>
        /// <remarks>
        /// <b>Three byte-identical copies of this existed</b> — in <c>.Shading</c>, <c>.XObject</c>
        /// and <c>.GraphicsState</c> — down to the format string. They differed only in the three
        /// parameters above, and the corner order is load-bearing: the four points are emitted as a
        /// closed quadrilateral rather than as an axis-aligned rect, because a rotated or skewed
        /// <paramref name="ctm"/> maps the box to a parallelogram that an <c>x/y/width/height</c>
        /// rect cannot express.
        /// </remarks>
        private string EmitBBoxClip(PdfArray bbox, float[] ctm, string idPrefix, bool openGroup)
        {
            if (bbox == null || bbox.Count < 4) return null;

            float bx0 = GetArrFloat(bbox, 0), by0 = GetArrFloat(bbox, 1);
            float bx1 = GetArrFloat(bbox, 2), by1 = GetArrFloat(bbox, 3);
            float lx = Math.Min(bx0, bx1), ly = Math.Min(by0, by1);
            float hx = Math.Max(bx0, bx1), hy = Math.Max(by0, by1);

            var (ax, ay) = TxPtWith(ctm, lx, ly);
            var (bx, by) = TxPtWith(ctm, hx, ly);
            var (cx, cy) = TxPtWith(ctm, hx, hy);
            var (dx, dy) = TxPtWith(ctm, lx, hy);

            string id = idPrefix + NextId();   // shared counter — see NextId
            _sb.Append($"<defs><clipPath id=\"{id}\"><path d=\"M{F(ax)},{F(ay)} " +
                       $"L{F(bx)},{F(by)} L{F(cx)},{F(cy)} L{F(dx)},{F(dy)} Z\"/></clipPath></defs>\n");
            if (openGroup) _sb.Append($"<g clip-path=\"url(#{id})\">\n");
            return id;
        }

        // ---- Matrix math ----

        // Deliberately a factory, not a shared `static readonly` instance (and not
        // Core.Text.Matrix3x3.Identity, which is a double-based struct): the PDF matrices in
        // this renderer are mutable float[6] buffers, so every caller needs its own copy.
        private static float[] Identity() => new float[] { 1, 0, 0, 1, 0, 0 };
        private static float[] CopyArr(float[] m) => (float[])m.Clone();
        // Matrix composition lives in GraphicsState (single source of truth).
        private static float[] Compose(float[] A, float[] B) => GraphicsState.Compose(A, B);

        /// <summary>
        /// The uniform scale factor a matrix applies to a length: the geometric mean of its two
        /// axis lengths. This is the exact figure <c>PdfRenderContext.BuildStrokePaint</c> uses
        /// to scale the line width and the dash array, so the two renderers stroke identically
        /// under a non-unit CTM.
        ///
        /// <para>Returns 1 for a degenerate or non-finite matrix rather than 0, so a caller
        /// dividing by it (the text and glyph-outline emitters, whose element already carries
        /// its own transform) cannot produce an infinity in the markup.</para>
        /// </summary>
        internal static float CtmScale(float[] m)
        {
            if (m == null || m.Length < 4) return 1f;
            // The arithmetic is PdfMatrix.UniformScaleRaw, shared with the raster path; the guard
            // below stays here because the two renderers guard it differently on purpose.
            float s = PdfMatrix.UniformScaleRaw(m);
            return s > 0f && !float.IsNaN(s) && !float.IsInfinity(s) ? s : 1f;
        }

        // §8.4.3.6 lives in DashPattern, which the raster path also calls. These were copies of
        // PdfRenderContext's, and the comment that stood here said exactly that — the marker
        // this project treats as a defect rather than as documentation.
        private static float EffectiveDashPeriod(float[] pattern)
            => DashPattern.EffectiveDashPeriod(pattern);

        private static float NormaliseDashPhase(float phase, float[] pattern)
            => DashPattern.NormalisePhase(phase, pattern);

        // ---- Operand helpers ----

        // Operand coercion delegated to Core.Text.ContentOperand (shared).
        private static float Flt(ContentStreamOperation op, int idx) => ContentOperand.Float(op, idx);


        private static float Clamp(float v) => PdfColor.Clamp01(v);

        // XmlEsc / F are the enclosing PdfSvgRenderer's helpers — one definition per file.
    }
}

