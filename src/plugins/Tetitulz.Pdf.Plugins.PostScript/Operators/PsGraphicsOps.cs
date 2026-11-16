// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript.Document;
using Tetitulz.Pdf.Plugins.PostScript.Graphics;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Operators
{
    /// <summary>
    /// Path construction, painting, graphics state, transformations, colour and line style
    /// (PLRM §8.8–§8.11).
    ///
    /// <para>Every operator here needs a graphics engine. When none is attached — an interpreter
    /// built to run the language without drawing — they consume their operands and report, rather
    /// than throwing: a program that draws should still <i>run</i> to completion so its arithmetic
    /// and its diagnostics are still worth reading.</para>
    /// </summary>
    internal static class PsGraphicsOps
    {
        public static void Register(PsSystemDict.Registrar op)
        {
            // ---- path construction ----
            op("newpath",    i => G(i)?.Path.Clear());
            op("moveto",     i => MoveTo(i, relative: false));
            op("rmoveto",    i => MoveTo(i, relative: true));
            op("lineto",     i => LineTo(i, relative: false));
            op("rlineto",    i => LineTo(i, relative: true));
            op("curveto",    i => CurveTo(i, relative: false));
            op("rcurveto",   i => CurveTo(i, relative: true));
            op("closepath",  i => G(i)?.Path.ClosePath());
            op("currentpoint", CurrentPoint);
            op("arc",        i => Arc(i, clockwise: false));
            op("arcn",       i => Arc(i, clockwise: true));
            op("arct",       i => ArcTo(i, pushTangents: false));
            op("arcto",      i => ArcTo(i, pushTangents: true));
            op("pathbbox",   PathBBox);
            op("reversepath", ReversePath);
            op("strokepath", StrokePath);
            op("clippath",   ClipPath);

            // `flattenpath` would replace curves with line segments. The emitted stream keeps the
            // curves, which is strictly better output, and no program can observe the difference
            // without `pathforall` — which is not implemented.
            op("flattenpath", i => { });

            // ---- painting ----
            op("fill",    i => Paint(i, g => g.Fill(evenOdd: false)));
            op("eofill",  i => Paint(i, g => g.Fill(evenOdd: true)));
            op("stroke",  i => Paint(i, g => g.Stroke()));
            // clip is charged per path segment, unlike fill/stroke: it deliberately does NOT clear
            // the path, so `{ 1 1 rlineto clip } loop` re-emits an ever-longer path every
            // iteration. The budget charges one tick per operator, so without this the cost is
            // invisible to it — a 37-byte program ran for days. fill/stroke clear the path, which
            // amortises them to O(1) per segment, but they are charged too so the rule is uniform.
            op("clip",    i => Paint(i, g => g.Clip(evenOdd: false)));
            op("eoclip",  i => Paint(i, g => g.Clip(evenOdd: true)));
            op("rectfill",   i => RectOp(i, RectMode.Fill));
            op("rectstroke", i => RectOp(i, RectMode.Stroke));
            op("rectclip",   i => RectOp(i, RectMode.Clip));
            op("initclip", i => { if (RefuseInGlyph(i, "initclip")) return; G(i)?.InitClip(); });
            op("clipsave",    i => G(i)?.ClipSave());
            op("cliprestore", i => G(i)?.ClipRestore());
            op("erasepage", i => { if (RefuseInGlyph(i, "erasepage")) return; ErasePage(i); });

            // ---- graphics state ----
            op("gsave",       GSave);
            op("grestore",    i => CtmChange(i, g => g.GRestore()));
            op("grestoreall", i => CtmChange(i, g => g.GRestoreAll()));
            op("save",        Save);
            op("restore",     Restore);
            op("gstate",        GState);
            op("currentgstate", CurrentGState);
            op("setgstate",     SetGState);
            op("initgraphics", InitGraphics);
            op("setpagedevice", SetPageDevice);
            op("currentpagedevice", CurrentPageDevice);
            op("showpage",    i => { if (RefuseInGlyph(i, "showpage")) return; ShowPage(i); });
            op("copypage",    i => { });

            // ---- transformations ----
            op("translate", Translate);
            op("scale",     ScaleOp);
            op("rotate",    RotateOp);
            op("concat",    Concat);
            op("setmatrix", SetMatrix);
            op("currentmatrix", CurrentMatrix);
            op("initmatrix", i => CtmChange(i, g => g.SetMatrix(g.BaseMatrix)));
            op("defaultmatrix", DefaultMatrix);
            op("matrix",    i => i.Operands.Push(MatrixToArray(i, PsMatrix.Identity)));
            op("identmatrix", IdentMatrix);
            op("invertmatrix", InvertMatrix);
            op("concatmatrix", ConcatMatrix);
            op("transform",  i => TransformOp(i, delta: false, inverse: false));
            op("dtransform", i => TransformOp(i, delta: true,  inverse: false));
            op("itransform", i => TransformOp(i, delta: false, inverse: true));
            op("idtransform",i => TransformOp(i, delta: true,  inverse: true));

            // ---- colour ----
            op("setgray",       i => SetColor(i, PsColor.Gray(i.Operands.PopNumber("setgray"))));
            op("setrgbcolor",   SetRgb);
            op("setcmykcolor",  SetCmyk);
            op("sethsbcolor",   SetHsb);
            op("currentgray",   i => i.Operands.Push(PsObject.Real(Gs(i).Color.ToGray())));
            op("currentrgbcolor", CurrentRgb);
            op("currentcmykcolor", CurrentCmyk);
            op("currenthsbcolor", CurrentHsb);

            // ---- line style ----
            op("setlinewidth",  i => Gs(i).LineWidth  = Math.Abs(i.Operands.PopNumber("setlinewidth")));
            op("setlinecap",    i => Gs(i).LineCap    = ClampStyle(i.Operands.PopInt("setlinecap")));
            op("setlinejoin",   i => Gs(i).LineJoin   = ClampStyle(i.Operands.PopInt("setlinejoin")));
            op("setmiterlimit", i => Gs(i).MiterLimit = Math.Max(1.0, i.Operands.PopNumber("setmiterlimit")));
            op("setdash",       SetDash);
            op("currentlinewidth",  i => i.Operands.Push(PsObject.Real(Gs(i).LineWidth)));
            op("currentlinecap",    i => i.Operands.Push(PsObject.Int(Gs(i).LineCap)));
            op("currentlinejoin",   i => i.Operands.Push(PsObject.Int(Gs(i).LineJoin)));
            op("currentmiterlimit", i => i.Operands.Push(PsObject.Real(Gs(i).MiterLimit)));
            op("currentdash",       CurrentDash);
        }

        // ------------------------------------------------------------------
        //  Engine access
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs a painting operator, charging the budget for the path it walks.
        ///
        /// <para>Every one of these emits or measures the whole current path, so their real cost is
        /// O(segments) while <c>WorkBudget</c> charges one tick per operator. That gap is what made
        /// <c>{ 1 1 rlineto clip } loop</c> quadratic and invisible: the guard documented as the
        /// thing that "still terminates <c>{ } loop</c> in bounded time" could not see it.</para>
        /// </summary>
        private static void Paint(PsInterpreter i, Action<PsGraphicsEngine> paint)
        {
            var g = G(i); if (g == null) return;
            i.Tick(g.Path.Count);
            paint(g);
        }

        /// <summary>
        /// Runs an operator that may move the CTM while a path is open, charging the path's
        /// re-expression per segment. <see cref="PsGraphicsEngine"/> keeps the open path where it
        /// was drawn when the CTM changes (PLRM's current path is in device space), which is
        /// O(segments) work the budget would otherwise see as one tick —
        /// <c>{ 1 1 rlineto 1.0001 1 scale } loop</c> is the quadratic shape <c>clip</c> and
        /// <c>pathbbox</c> are charged for.
        /// </summary>
        private static void CtmChange(PsInterpreter i, Action<PsGraphicsEngine> change)
        {
            var g = G(i); if (g == null) return;
            i.Tick(g.Path.Count);
            change(g);
        }

        /// <summary>The engine, or null with a one-shot report when none is attached.</summary>
        private static PsGraphicsEngine G(PsInterpreter i)
        {
            if (i.Graphics != null) return i.Graphics;
            PostScriptDiagnostics.Report("PsGraphicsOps",
                "a graphics operator ran with no graphics engine attached; it drew nothing");
            return null;
        }

        /// <summary>
        /// The graphics state, or a throwaway one so operand handling stays uniform when no engine
        /// is attached.
        ///
        /// <para><c>[ThreadStatic]</c> because the line-style operators <i>write</i> to whatever this
        /// returns. A single shared instance would let one thread's <c>setlinewidth</c> be read back
        /// by another thread's <c>currentlinewidth</c>, and would carry state between conversions on
        /// the same thread. It is deliberately not reset per conversion: a detached interpreter draws
        /// nothing, so nothing observable depends on its contents — the isolation is what matters.</para>
        /// </summary>
        private static PsGraphicsState Gs(PsInterpreter i) => i.Graphics?.Gs ?? Detached;

        [ThreadStatic] private static PsGraphicsState _detached;
        private static PsGraphicsState Detached => _detached ??= new PsGraphicsState();

        // ------------------------------------------------------------------
        //  Path construction
        // ------------------------------------------------------------------

        private static void MoveTo(PsInterpreter i, bool relative)
        {
            double y = i.Operands.PopNumber(relative ? "rmoveto" : "moveto");
            double x = i.Operands.PopNumber(relative ? "rmoveto" : "moveto");
            var g = G(i); if (g == null) return;

            if (relative)
            {
                if (!g.Path.CurrentPoint.HasValue)
                    throw new PsErrorException(PsErrorKind.Undefined, "rmoveto", "no current point");
                var (cx, cy) = g.Path.CurrentPoint.Value;
                x += cx; y += cy;
            }
            g.Path.MoveTo(x, y);
        }

        private static void LineTo(PsInterpreter i, bool relative)
        {
            double y = i.Operands.PopNumber(relative ? "rlineto" : "lineto");
            double x = i.Operands.PopNumber(relative ? "rlineto" : "lineto");
            var g = G(i); if (g == null) return;

            if (!g.Path.CurrentPoint.HasValue)
                throw new PsErrorException(PsErrorKind.Undefined,
                    relative ? "rlineto" : "lineto", "no current point");

            if (relative)
            {
                var (cx, cy) = g.Path.CurrentPoint.Value;
                x += cx; y += cy;
            }
            g.Path.LineTo(x, y);
        }

        private static void CurveTo(PsInterpreter i, bool relative)
        {
            string name = relative ? "rcurveto" : "curveto";
            double y3 = i.Operands.PopNumber(name), x3 = i.Operands.PopNumber(name);
            double y2 = i.Operands.PopNumber(name), x2 = i.Operands.PopNumber(name);
            double y1 = i.Operands.PopNumber(name), x1 = i.Operands.PopNumber(name);

            var g = G(i); if (g == null) return;
            if (!g.Path.CurrentPoint.HasValue)
                throw new PsErrorException(PsErrorKind.Undefined, name, "no current point");

            if (relative)
            {
                // Every control point is relative to the CURRENT point, not to the previous
                // control point — a classic off-by-one in curve translation.
                var (cx, cy) = g.Path.CurrentPoint.Value;
                x1 += cx; y1 += cy;
                x2 += cx; y2 += cy;
                x3 += cx; y3 += cy;
            }
            g.Path.CurveTo(x1, y1, x2, y2, x3, y3);
        }

        private static void CurrentPoint(PsInterpreter i)
        {
            var g = G(i);
            if (g?.Path.CurrentPoint == null)
                throw new PsErrorException(PsErrorKind.Undefined, "currentpoint", "no current point");

            var (x, y) = g.Path.CurrentPoint.Value;
            i.Operands.Push(PsObject.Real(x));
            i.Operands.Push(PsObject.Real(y));
        }

        private static void Arc(PsInterpreter i, bool clockwise)
        {
            string name = clockwise ? "arcn" : "arc";
            double end    = i.Operands.PopNumber(name);
            double start  = i.Operands.PopNumber(name);
            double radius = i.Operands.PopNumber(name);
            double cy     = i.Operands.PopNumber(name);
            double cx     = i.Operands.PopNumber(name);

            if (radius < 0) throw new PsErrorException(PsErrorKind.RangeCheck, name);

            // NaN or Infinity would propagate into every coordinate the flattener computes and out
            // into the emitted stream as an unparseable number. Refused here rather than clamped:
            // there is no sensible arc through an infinite centre, and the angles in particular are
            // the operands that would hang the sweep normaliser.
            if (!PsNumeric.IsFinite(cx) || !PsNumeric.IsFinite(cy) || !PsNumeric.IsFinite(radius) ||
                !PsNumeric.IsFinite(start) || !PsNumeric.IsFinite(end))
                throw new PsErrorException(PsErrorKind.RangeCheck, name, "a non-finite operand");

            var g = G(i); if (g == null) return;
            i.Tick(PsLimits.MaxArcSegments);
            PsArcFlattener.Append(g.Path, cx, cy, radius, start, end, clockwise);
        }

        private static void ArcTo(PsInterpreter i, bool pushTangents)
        {
            string name = pushTangents ? "arcto" : "arct";
            double radius = i.Operands.PopNumber(name);
            double y2 = i.Operands.PopNumber(name), x2 = i.Operands.PopNumber(name);
            double y1 = i.Operands.PopNumber(name), x1 = i.Operands.PopNumber(name);

            var g = G(i); if (g == null) return;
            if (!g.Path.CurrentPoint.HasValue)
                throw new PsErrorException(PsErrorKind.Undefined, name, "no current point");

            var tangents = PsArcFlattener.AppendTangentArc(g.Path, x1, y1, x2, y2, radius);
            if (tangents == null)
            {
                // Degenerate geometry — collinear points or a zero radius. The PLRM's fallback is
                // a straight line to the corner.
                g.Path.LineTo(x1, y1);
                tangents = (x1, y1, x1, y1);
            }

            if (!pushTangents) return;
            i.Operands.Push(PsObject.Real(tangents.Value.Xt1));
            i.Operands.Push(PsObject.Real(tangents.Value.Yt1));
            i.Operands.Push(PsObject.Real(tangents.Value.Xt2));
            i.Operands.Push(PsObject.Real(tangents.Value.Yt2));
        }

        private static void PathBBox(PsInterpreter i)
        {
            var g = G(i);

            // Charged for the same reason clip is: BoundingBox walks every segment, and the path
            // grows unboundedly, so `{ 1 1 rlineto pathbbox … } loop` was quadratic work the budget
            // charged as one tick per iteration.
            if (g != null) i.Tick(g.Path.Count);

            var box = g?.Path.BoundingBox();
            if (box == null)
                throw new PsErrorException(PsErrorKind.Undefined, "pathbbox", "the path is empty");

            // After `strokepath` the path stands for the outline of its own stroke, which extends
            // half a line width beyond it on every side. Padding is exact for a butt-capped straight
            // segment and an under-estimate for a mitred corner, where the spike reaches further —
            // the same trade NoteMarkExtent already makes for the drawn extent.
            double pad = g.Path.StrokeOutlinePending ? Gs(i).LineWidth / 2 : 0;

            i.Operands.Push(PsObject.Real(box.Value.X0 - pad));
            i.Operands.Push(PsObject.Real(box.Value.Y0 - pad));
            i.Operands.Push(PsObject.Real(box.Value.X1 + pad));
            i.Operands.Push(PsObject.Real(box.Value.Y1 + pad));
        }

        /// <summary>
        /// <c>reversepath</c> — see <see cref="PsPath.Reverse"/> for why reversing direction changes
        /// what a non-zero fill paints.
        /// </summary>
        private static void ReversePath(PsInterpreter i)
        {
            var g = G(i); if (g == null) return;

            // Charged per segment, like clip and pathbbox: the rebuild walks the whole path, and
            // `{ 1 1 rlineto reversepath } loop` is otherwise one tick per iteration over a path
            // that keeps growing.
            i.Tick(g.Path.Count);

            if (g.Path.StrokeOutlinePending)
            {
                // The path is standing in for an outline that was never built, so there is no
                // direction to reverse. Reversing the underlying path would silently change what
                // the eventual stroke traces without changing what it paints.
                PostScriptDiagnostics.Report("PsGraphicsOps.ReversePath",
                    "'reversepath' after 'strokepath' was ignored: the stroke outline is never " +
                    "built as a path, so it has no direction to reverse. A stroke is unaffected by " +
                    "direction, so the drawing is unchanged");
                return;
            }

            g.Path.Reverse();
        }

        /// <summary>
        /// <c>strokepath</c> — replace the path with the outline of its stroke.
        ///
        /// <para>Flags the path rather than building the outline; <see cref="PsPath.StrokeOutlinePending"/>
        /// states why, and every consumer that cannot honour the flag reports.</para>
        /// </summary>
        private static void StrokePath(PsInterpreter i)
        {
            var g = G(i); if (g == null) return;
            if (g.Path.IsEmpty) return;

            g.Path.StrokeOutlinePending = true;
        }

        private enum RectMode { Fill, Stroke, Clip }

        /// <summary>
        /// <c>rectfill</c> / <c>rectstroke</c> / <c>rectclip</c> (PLRM Level 2).
        ///
        /// <para><b>These are not a convenience the way they look.</b> Generated PostScript — print
        /// drivers, Illustrator, dvips — paints rules, table cells, backgrounds and crop marks with
        /// them by the hundred, so leaving them undefined would lose a top-level object per
        /// rectangle.</para>
        ///
        /// <para>The PLRM defines each as <c>gsave newpath …rect… closepath ⟨paint⟩ grestore</c>, so
        /// <b>the current path must survive</b> — and this engine's <c>gsave</c> does not save it
        /// (see <see cref="PsPath.Snapshotted"/>), so it is saved and put back explicitly.
        /// <c>rectclip</c> is the exception the spec itself makes: it ends with an implicit
        /// <c>newpath</c>, so there the path is cleared rather than restored.</para>
        /// </summary>
        private static void RectOp(PsInterpreter i, RectMode mode)
        {
            string name = mode switch
            {
                RectMode.Fill   => "rectfill",
                RectMode.Stroke => "rectstroke",
                _               => "rectclip"
            };

            // rectstroke alone takes an optional trailing matrix, and the operand below it may be
            // another array — the rectangle list. A matrix is always exactly six numbers and a
            // rectangle list is always a multiple of four, so six is unambiguous.
            PsMatrix? penMatrix = null;
            if (mode == RectMode.Stroke && i.Operands.Count > 0)
            {
                var top = i.Operands.Peek();
                if (top.Type == PsType.Array && top.AsArray().Length == 6)
                    penMatrix = PsMatrix.FromOperands(i.Operands.Pop().AsArray(), name);
            }

            var rects = ReadRectangles(i, name);
            if (rects == null) return;

            var g = G(i); if (g == null) return;
            i.Tick(rects.Count);

            var saved = g.Path.Snapshot();
            g.Path.Clear();

            foreach (var (x, y, w, h) in rects) g.Path.AppendRectangle(x, y, w, h);

            switch (mode)
            {
                case RectMode.Fill:
                    g.Fill(evenOdd: false);
                    g.Path.RestoreFrom(saved);
                    break;

                case RectMode.Stroke:
                    // The matrix affects the pen, so it has to be in force for the stroke and gone
                    // afterwards — which is what the gsave/grestore pair the PLRM writes it with
                    // does, and it brackets the emitted q/Q too.
                    if (penMatrix.HasValue) { g.GSave(); g.Concat(penMatrix.Value); }
                    g.Stroke();
                    if (penMatrix.HasValue) g.GRestore();
                    g.Path.RestoreFrom(saved);
                    break;

                default:
                    g.Clip(evenOdd: false);
                    // PLRM ends rectclip with newpath, unlike clip, which leaves the path intact.
                    g.Path.Clear();
                    break;
            }
        }

        /// <summary>
        /// The rectangle list for a <c>rect*</c> operator: four numbers, or an array of 4n numbers.
        /// Returns null when the operands could not be read, having consumed them.
        /// </summary>
        private static List<(double X, double Y, double W, double H)> ReadRectangles(
            PsInterpreter i, string name)
        {
            var rects = new List<(double, double, double, double)>();

            if (i.Operands.Count > 0 && i.Operands.Peek().Type == PsType.String)
            {
                // A `numstring` is a binary homogeneous number array (PLRM §3.12.5). Decoding it
                // needs the binary-token reader this scanner does not have, so it is consumed and
                // reported rather than half-read into rectangles at the wrong scale.
                i.Operands.Pop();
                PostScriptDiagnostics.ReportUnimplemented(name + " (numstring form)");
                return null;
            }

            if (i.Operands.Count > 0 && i.Operands.Peek().Type == PsType.Array)
            {
                var array = i.Operands.Pop().AsArray();
                if (array.Length % 4 != 0)
                    throw new PsErrorException(PsErrorKind.RangeCheck, name,
                        $"a rectangle array holds 4 numbers per rectangle, not {array.Length}");

                for (int k = 0; k < array.Length; k += 4)
                {
                    for (int c = 0; c < 4; c++)
                        if (!array[k + c].IsNumber)
                            throw new PsErrorException(PsErrorKind.TypeCheck, name);

                    rects.Add((array[k].Num, array[k + 1].Num, array[k + 2].Num, array[k + 3].Num));
                }
                return rects;
            }

            double h = i.Operands.PopNumber(name);
            double w = i.Operands.PopNumber(name);
            double y = i.Operands.PopNumber(name);
            double x = i.Operands.PopNumber(name);
            rects.Add((x, y, w, h));
            return rects;
        }

        /// <summary>
        /// <c>clippath</c> — set the current path to the current clipping path.
        ///
        /// <para>The dominant use is <c>clippath pathbbox</c>, to discover the drawable extent, and
        /// that answer is exact: <c>pathbbox</c> asks for a bounding box, which is what the engine
        /// tracks even when it cannot keep the clip's exact outline.</para>
        /// </summary>
        private static void ClipPath(PsInterpreter i)
        {
            var g = G(i); if (g == null) return;
            i.Tick(g.Path.Count);
            g.ClipPath();
        }

        // ------------------------------------------------------------------
        //  Graphics state
        // ------------------------------------------------------------------

        /// <summary>
        /// Refuses a page-level operator inside a Type 3 glyph procedure, reporting it.
        ///
        /// <para>The child engine has no page sink, so <c>showpage</c> would take the fragment-mode
        /// reset branch and <b>clear the glyph mid-build</b>; <c>initclip</c> asks for the page's
        /// region, which a glyph has none of, and <c>erasepage</c> means the page. A glyph that resets
        /// the page state is a malformed font, not a page break.</para>
        /// </summary>
        private static bool RefuseInGlyph(PsInterpreter i, string op)
        {
            if (i.GlyphBuild == null) return false;

            PostScriptDiagnostics.Report("PsGraphicsOps",
                $"'{op}' inside a Type 3 glyph procedure was ignored: it is a page-level operator, " +
                "and honouring it would discard the glyph being built");
            return true;
        }

        private static void ErasePage(PsInterpreter i)
        {
            // Painting the whole page white would blank the host PDF page the XObject sits on.
            PostScriptDiagnostics.Report("PsGraphicsOps.ErasePage",
                "'erasepage' was ignored: erasing would blank the host PDF page this PostScript " +
                "is drawn on");
        }

        /// <summary>
        /// <c>gsave</c>, charged for the state it clones.
        ///
        /// <para><c>PsGraphicsEngine</c> caps the stack depth, which is what bounds what a
        /// <c>gsave</c> bomb retains. The charge is the other half: <c>{ gsave grestore } loop</c>
        /// never grows the stack at all, so the depth cap cannot see it, and every iteration still
        /// allocates a state. Charges are never refunded — <c>PsAllocationBudget</c>'s documented
        /// rule — so a matching <c>grestore</c> does not give the bytes back; the budget measures
        /// allocation traffic, which is the quantity the program controls.</para>
        /// </summary>
        private static void GSave(PsInterpreter i)
        {
            var g = G(i); if (g == null) return;
            // The saved state keeps a copy of the path (PLRM §4.2), charged by its segments.
            i.Tick(g.Path.Count);
            i.ChargeAllocation(PsAllocationBudget.BytesPerGraphicsState + (long)g.Path.Count * PsPath.BytesPerSegment, "gsave");
            g.GSave();
        }

        private static void Save(PsInterpreter i)
        {
            var g = G(i);

            // Charged before the push, like every other allocation in this interpreter, and only
            // when there is an engine to push onto: a detached interpreter clones nothing.
            if (g != null)
            {
                i.Tick(g.Path.Count);
                i.ChargeAllocation(PsAllocationBudget.BytesPerGraphicsState + (long)g.Path.Count * PsPath.BytesPerSegment, "save");
            }

            // The save object names the VM's save, which carries the engine's (PsVm).
            int graphics = g?.Save() ?? 0;
            int id = PsVm.Current?.Save(graphics) ?? graphics;
            i.Operands.Push(PsObject.SaveMark(id));
        }

        private static void Restore(PsInterpreter i)
        {
            var o = i.Operands.Pop();
            if (o.Type != PsType.Save) throw new PsErrorException(PsErrorKind.TypeCheck, "restore");

            // VM first — every change to an array or a dictionary since the save undone — then the
            // graphics state, which the save carried. A graphics save a page boundary already closed
            // (`save … showpage restore`) leaves the page's fresh state as it is.
            int id = o.SaveId;
            int graphics = id;
            bool known = PsVm.Current == null || PsVm.Current.Restore(id, out graphics);
            if (!known) throw new PsErrorException(PsErrorKind.InvalidRestore, "restore");

            // The CTM may move under an open path, so it is charged and run like every other
            // transform change.
            CtmChange(i, g =>
            {
                if (!g.Restore(graphics) && PsVm.Current == null)
                    throw new PsErrorException(PsErrorKind.InvalidRestore, "restore");
            });
        }

        /// <summary>
        /// <c>gstate</c>: a new graphics state object holding a copy of the current state and path
        /// (PLRM §8.2), charged as a saved state is.
        /// </summary>
        private static void GState(PsInterpreter i)
        {
            var saved = new PsGStateObject();
            CopyState(i, saved, "gstate");
            i.Operands.Push(PsObject.GState(saved));
        }

        /// <summary><c>gstate currentgstate gstate</c>: the object given filled with a copy of the current state, and pushed back.</summary>
        private static void CurrentGState(PsInterpreter i)
        {
            PsObject target = i.Operands.Pop();
            if (target.Type != PsType.GState) throw new PsErrorException(PsErrorKind.TypeCheck, "currentgstate");
            CopyState(i, target.AsGState(), "currentgstate");
            i.Operands.Push(target);
        }

        private static void CopyState(PsInterpreter i, PsGStateObject target, string op)
        {
            var g = i.Graphics;
            if (g == null) return;
            i.Tick(g.Path.Count);
            i.ChargeAllocation(PsAllocationBudget.BytesPerGraphicsState + (long)g.Path.Count * PsPath.BytesPerSegment, op);
            g.CopyStateTo(target);
        }

        /// <summary><c>gstate setgstate</c>: the object's state made current (PLRM §8.2), the path it holds charged as the transform change of any other operator is.</summary>
        private static void SetGState(PsInterpreter i)
        {
            PsObject saved = i.Operands.Pop();
            if (saved.Type != PsType.GState) throw new PsErrorException(PsErrorKind.TypeCheck, "setgstate");
            CtmChange(i, g => g.SetGState(saved.AsGState()));
        }

        /// <summary>
        /// <c>dict setpagedevice</c> (PLRM §6.1.1): a <c>/PageSize</c> of two positive numbers is the
        /// medium of this page and every one after it — its <c>/MediaBox</c>, and what <c>clippath</c>
        /// answers — and, as PLRM has it, the graphics state is initialised. Every other request
        /// (resolution, orientation, duplex, the media's other keys) describes a printer this converter
        /// does not drive and is ignored. A page already drawn on keeps its marks: erasing them is a
        /// device's business, and PDF's page begins blank anyway.
        /// </summary>
        private static void SetPageDevice(PsInterpreter i)
        {
            PsDict request = i.Operands.PopDict("setpagedevice");
            var g = G(i); if (g == null) return;
            if (request.TryGetName("PageSize", out PsObject size))
            {
                if (size.Type != PsType.Array || size.AsArray().Length < 2
                    || !size.AsArray()[0].IsNumber || !size.AsArray()[1].IsNumber)
                    throw new PsErrorException(PsErrorKind.TypeCheck, "setpagedevice", "/PageSize is not two numbers");
                double w = size.AsArray()[0].Num, h = size.AsArray()[1].Num;
                if (!(w > 0 && h > 0 && w < 1e6 && h < 1e6))
                    throw new PsErrorException(PsErrorKind.RangeCheck, "setpagedevice", $"/PageSize [{w} {h}]");
                var media = new PsBoundingBox(0, 0, w, h);
                g.PageMedia = media;
                g.DefaultClipBox = media;
            }
            InitGraphics(i);
        }

        /// <summary><c>currentpagedevice dict</c>: the <c>/PageSize</c> in force — the page device's, the declared box's, or Letter.</summary>
        private static void CurrentPageDevice(PsInterpreter i)
        {
            var g = i.Graphics;
            PsBoundingBox box = g?.PageMedia ?? g?.DefaultClipBox ?? new PsBoundingBox(0, 0, 612, 792);
            var size = new PsArray(new[] { PsObject.Real(box.Width), PsObject.Real(box.Height) });
            var device = new PsDict(1);
            device.PutName("PageSize", PsObject.Arr(size));
            i.Operands.Push(PsObject.Dict(device));
        }

        /// <summary>
        /// <c>initgraphics</c> — PLRM §8.2 defines it as
        /// <c>initmatrix newpath initclip 1 setlinewidth 0 setlinecap 0 setlinejoin
        /// 10 setmiterlimit [] 0 setdash 0 setgray</c>.
        ///
        /// <para><b><c>initclip</c> is part of it</b>, and was the one piece missing: everything
        /// after an <c>initgraphics</c> stayed inside whatever clip was in force. It runs first, so
        /// the defaults below are written into the state the widening leaves current. With no clip
        /// active it returns immediately, so the common case is unchanged.</para>
        /// </summary>
        private static void InitGraphics(PsInterpreter i)
        {
            var g = G(i); if (g == null) return;

            g.InitClip();

            // `0 setgray` is the last step PLRM lists, and setgray establishes the colour SPACE as
            // well as the colour — so the component count goes back to DeviceGray's 1 with it.
            g.Gs.Color           = PsColor.Black;
            g.Gs.ColorComponents = 1;

            // `0 setgray` also puts the current colour SPACE back to DeviceGray, so any space
            // setcolorspace established is gone — and an image after initgraphics must not carry
            // the palette of a space the program has just discarded.
            g.Gs.ImageSpace      = null;
            g.Gs.LineWidth  = 1.0;
            g.Gs.LineCap    = 0;
            g.Gs.LineJoin   = 0;
            g.Gs.MiterLimit = 10.0;
            g.Gs.DashArray  = Array.Empty<double>();
            g.Gs.DashPhase  = 0;

            // `newpath` before `initmatrix`: the path is discarded anyway, so there is nothing for
            // the transform change to re-express.
            g.Path.Clear();
            g.SetMatrix(g.BaseMatrix);
        }

        private static void ShowPage(PsInterpreter i)
        {
            var g = G(i); if (g == null) return;

            if (i.Options.ShowPage == ShowPageBehavior.NewPage && i.PageSink != null)
            {
                // Document mode: close the page and start another, keeping VM so the prologue's
                // procedures stay defined — PostScript preserves userdict across pages.
                var (content, extent) = g.EndPage();
                i.PageSink.Add(content, extent, media: g.PageMedia,
                               rotate: i.Options.AutoRotatePages == PostScriptAutoRotate.PageByPage ? g.EndedPageRotation : 0);
                return;
            }
            g.ShowPageReset();
        }

        // ------------------------------------------------------------------
        //  Transformations
        // ------------------------------------------------------------------

        /// <summary>
        /// The transform operators are overloaded: with a matrix operand they modify <i>it</i> and
        /// push the result; without one they modify the CTM. Getting this wrong silently applies a
        /// transform that was meant to be captured.
        /// </summary>
        private static bool TryTakeMatrixOperand(PsInterpreter i, out PsArray target)
        {
            target = null;
            if (i.Operands.Count == 0) return false;
            var top = i.Operands.Peek();
            if (top.Type != PsType.Array || top.AsArray().Length < 6) return false;
            target = i.Operands.Pop().AsArray();
            return true;
        }

        private static void Translate(PsInterpreter i)
        {
            bool hasMatrix = TryTakeMatrixOperand(i, out var target);
            double ty = i.Operands.PopNumber("translate");
            double tx = i.Operands.PopNumber("translate");
            var m = PsMatrix.Translate(tx, ty);

            if (hasMatrix) { StoreMatrix(target, m); i.Operands.Push(PsObject.Arr(target)); return; }
            CtmChange(i, g => g.Concat(m));
        }

        private static void ScaleOp(PsInterpreter i)
        {
            bool hasMatrix = TryTakeMatrixOperand(i, out var target);
            double sy = i.Operands.PopNumber("scale");
            double sx = i.Operands.PopNumber("scale");
            var m = PsMatrix.Scale(sx, sy);

            if (hasMatrix) { StoreMatrix(target, m); i.Operands.Push(PsObject.Arr(target)); return; }
            CtmChange(i, g => g.Concat(m));
        }

        private static void RotateOp(PsInterpreter i)
        {
            bool hasMatrix = TryTakeMatrixOperand(i, out var target);
            double degrees = i.Operands.PopNumber("rotate");
            var m = PsMatrix.Rotate(degrees);

            if (hasMatrix) { StoreMatrix(target, m); i.Operands.Push(PsObject.Arr(target)); return; }
            CtmChange(i, g => g.Concat(m));
        }

        private static void Concat(PsInterpreter i)
        {
            var arr = i.Operands.PopArray("concat");
            var m = PsMatrix.FromOperands(arr, "concat");
            CtmChange(i, g => g.Concat(m));
        }

        private static void SetMatrix(PsInterpreter i)
        {
            var arr = i.Operands.PopArray("setmatrix");
            var m = PsMatrix.FromOperands(arr, "setmatrix");
            CtmChange(i, g => g.SetMatrix(m));
        }

        private static void CurrentMatrix(PsInterpreter i)
        {
            var arr = i.Operands.PopArray("currentmatrix");
            StoreMatrix(arr, Gs(i).Ctm);
            i.Operands.Push(PsObject.Arr(arr));
        }

        private static void DefaultMatrix(PsInterpreter i)
        {
            var arr = i.Operands.PopArray("defaultmatrix");
            StoreMatrix(arr, i.Graphics?.BaseMatrix ?? PsMatrix.Identity);
            i.Operands.Push(PsObject.Arr(arr));
        }

        private static void IdentMatrix(PsInterpreter i)
        {
            var arr = i.Operands.PopArray("identmatrix");
            StoreMatrix(arr, PsMatrix.Identity);
            i.Operands.Push(PsObject.Arr(arr));
        }

        private static void InvertMatrix(PsInterpreter i)
        {
            var result = i.Operands.PopArray("invertmatrix");
            var source = i.Operands.PopArray("invertmatrix");

            var inverse = PsMatrix.FromOperands(source, "invertmatrix").Invert();
            if (inverse == null) throw new PsErrorException(PsErrorKind.UndefinedResult, "invertmatrix");

            StoreMatrix(result, inverse.Value);
            i.Operands.Push(PsObject.Arr(result));
        }

        private static void ConcatMatrix(PsInterpreter i)
        {
            var result = i.Operands.PopArray("concatmatrix");
            var b = i.Operands.PopArray("concatmatrix");
            var a = i.Operands.PopArray("concatmatrix");

            StoreMatrix(result, PsMatrix.FromOperands(a, "concatmatrix").Concat(PsMatrix.FromOperands(b, "concatmatrix")));
            i.Operands.Push(PsObject.Arr(result));
        }

        private static void TransformOp(PsInterpreter i, bool delta, bool inverse)
        {
            string name = (inverse ? "i" : "") + (delta ? "d" : "") + "transform";

            bool hasMatrix = TryTakeMatrixOperand(i, out var matrixArr);
            double y = i.Operands.PopNumber(name);
            double x = i.Operands.PopNumber(name);

            var m = hasMatrix ? PsMatrix.FromOperands(matrixArr, name) : Gs(i).Ctm;

            if (inverse)
            {
                var inv = m.Invert();
                if (inv == null) throw new PsErrorException(PsErrorKind.UndefinedResult, name);
                m = inv.Value;
            }

            var (rx, ry) = delta ? m.ApplyDelta(x, y) : m.Apply(x, y);
            i.Operands.Push(PsObject.Real(rx));
            i.Operands.Push(PsObject.Real(ry));
        }


        private static void StoreMatrix(PsArray a, in PsMatrix m)
        {
            if (a.Length < 6) return;
            a[0] = PsObject.Real(m.A);  a[1] = PsObject.Real(m.B);
            a[2] = PsObject.Real(m.C);  a[3] = PsObject.Real(m.D);
            a[4] = PsObject.Real(m.Tx); a[5] = PsObject.Real(m.Ty);
        }

        private static PsObject MatrixToArray(PsInterpreter i, in PsMatrix m)
        {
            i.ChargeAllocation(6 * PsAllocationBudget.BytesPerArrayElement, "matrix");
            var items = new PsObject[6];
            var arr = new PsArray(items);
            StoreMatrix(arr, m);
            return PsObject.Arr(arr);
        }

        // ------------------------------------------------------------------
        //  Colour
        // ------------------------------------------------------------------

        private static void SetColor(PsInterpreter i, PsColor c)
        {
            // Emits nothing: the colour is flushed by the next fill or stroke, which is what lets
            // one PostScript colour become PDF's separate fill and stroke colours.
            var gs = Gs(i);
            gs.Color = c;

            // These operators establish the current colour space too (PLRM §4.8.2: setgray DeviceGray,
            // setrgbcolor and sethsbcolor DeviceRGB, setcmykcolor DeviceCMYK), and that is what
            // decides how many operands a later `setcolor` consumes — and which space a Level 2
            // `image` paints in: a print driver's `0 0 0 setrgbcolor` before an RGB image dictionary.
            // The palette an earlier `[/Indexed …] setcolorspace` established is gone with the space.
            i.CurrentColorComponents = c.Space switch
            {
                PsColorSpace.Gray => 1,
                PsColorSpace.Rgb  => 3,
                _                 => 4
            };
            gs.ImageSpace = PsImageColorSpace.Device(i.CurrentColorComponents);
            gs.ColorSpaceOperand = null;   // currentcolorspace and currentcolor answer the device family and the colour
            gs.ColorOperands = null;
        }

        private static void SetRgb(PsInterpreter i)
        {
            double b = i.Operands.PopNumber("setrgbcolor");
            double g = i.Operands.PopNumber("setrgbcolor");
            double r = i.Operands.PopNumber("setrgbcolor");
            SetColor(i, PsColor.Rgb(r, g, b));
        }

        private static void SetCmyk(PsInterpreter i)
        {
            double k = i.Operands.PopNumber("setcmykcolor");
            double y = i.Operands.PopNumber("setcmykcolor");
            double m = i.Operands.PopNumber("setcmykcolor");
            double c = i.Operands.PopNumber("setcmykcolor");
            SetColor(i, PsColor.Cmyk(c, m, y, k));
        }

        private static void SetHsb(PsInterpreter i)
        {
            double b = i.Operands.PopNumber("sethsbcolor");
            double s = i.Operands.PopNumber("sethsbcolor");
            double h = i.Operands.PopNumber("sethsbcolor");
            SetColor(i, PsColor.Hsb(h, s, b));
        }

        private static void CurrentRgb(PsInterpreter i)
        {
            var (r, g, b) = Gs(i).Color.ToRgb();
            i.Operands.Push(PsObject.Real(r));
            i.Operands.Push(PsObject.Real(g));
            i.Operands.Push(PsObject.Real(b));
        }

        private static void CurrentCmyk(PsInterpreter i)
        {
            var c = Gs(i).Color;
            if (c.Space == PsColorSpace.Cmyk)
            {
                i.Operands.Push(PsObject.Real(c.C0));
                i.Operands.Push(PsObject.Real(c.C1));
                i.Operands.Push(PsObject.Real(c.C2));
                i.Operands.Push(PsObject.Real(c.C3));
                return;
            }

            // Undercolour removal is a device decision; the naive conversion is what PLRM's
            // default black generation amounts to.
            var (r, g, b) = c.ToRgb();
            double k = 1 - Math.Max(r, Math.Max(g, b));
            double denom = 1 - k;
            i.Operands.Push(PsObject.Real(denom <= 0 ? 0 : (1 - r - k) / denom));
            i.Operands.Push(PsObject.Real(denom <= 0 ? 0 : (1 - g - k) / denom));
            i.Operands.Push(PsObject.Real(denom <= 0 ? 0 : (1 - b - k) / denom));
            i.Operands.Push(PsObject.Real(k));
        }

        private static void CurrentHsb(PsInterpreter i)
        {
            var (r, g, b) = Gs(i).Color.ToRgb();
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double delta = max - min;

            double h = 0;
            if (delta > 0)
            {
                if (max == r)      h = (g - b) / delta / 6.0;
                else if (max == g) h = (2 + (b - r) / delta) / 6.0;
                else               h = (4 + (r - g) / delta) / 6.0;
                if (h < 0) h += 1;
            }

            i.Operands.Push(PsObject.Real(h));
            i.Operands.Push(PsObject.Real(max <= 0 ? 0 : delta / max));
            i.Operands.Push(PsObject.Real(max));
        }

        // ------------------------------------------------------------------
        //  Line style
        // ------------------------------------------------------------------

        private static int ClampStyle(int v) => v < 0 ? 0 : v > 2 ? 2 : v;

        private static void SetDash(PsInterpreter i)
        {
            double phase = i.Operands.PopNumber("setdash");
            var arr = i.Operands.PopArray("setdash");

            if (arr.Length > PsLimits.MaxDashArrayEntries)
                throw new PsErrorException(PsErrorKind.LimitCheck, "setdash",
                    $"{arr.Length} entries exceeds {PsLimits.MaxDashArrayEntries}");

            var dashes = new double[arr.Length];
            double total = 0;
            for (int k = 0; k < arr.Length; k++)
            {
                if (!arr[k].IsNumber) throw new PsErrorException(PsErrorKind.TypeCheck, "setdash");
                if (arr[k].Num < 0) throw new PsErrorException(PsErrorKind.RangeCheck, "setdash");
                dashes[k] = arr[k].Num;
                total += dashes[k];
            }

            // An all-zero array means "solid" in PostScript but is a division by zero for a
            // renderer walking the pattern, so it is normalised to the empty (solid) array.
            if (arr.Length > 0 && total <= 0) dashes = Array.Empty<double>();

            Gs(i).DashArray = dashes;
            Gs(i).DashPhase = Math.Max(0, phase);
        }

        private static void CurrentDash(PsInterpreter i)
        {
            var gs = Gs(i);
            i.ChargeAllocation((long)gs.DashArray.Length * PsAllocationBudget.BytesPerArrayElement,
                               "currentdash");

            var items = new PsObject[gs.DashArray.Length];
            for (int k = 0; k < items.Length; k++) items[k] = PsObject.Real(gs.DashArray[k]);

            i.Operands.Push(PsObject.Arr(new PsArray(items)));
            i.Operands.Push(PsObject.Real(gs.DashPhase));
        }
    }
}
