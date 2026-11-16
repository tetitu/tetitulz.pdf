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
        private void OpMoveTo(ContentStreamOperation op)
        {
            _curX = Flt(op, 0); _curY = Flt(op, 1);
            var (tx, ty) = TxPt(_curX, _curY);
            _path.Append($"M{F(tx)},{F(ty)} ");
        }

        private void OpLineTo(ContentStreamOperation op)
        {
            _curX = Flt(op, 0); _curY = Flt(op, 1);
            var (tx, ty) = TxPt(_curX, _curY);
            _path.Append($"L{F(tx)},{F(ty)} ");
        }

        private void OpCurveTo(ContentStreamOperation op)
        {
            var (x1, y1) = TxPt(Flt(op,0), Flt(op,1));
            var (x2, y2) = TxPt(Flt(op,2), Flt(op,3));
            var (x3, y3) = TxPt(Flt(op,4), Flt(op,5));
            _curX = Flt(op,4); _curY = Flt(op,5);
            _path.Append($"C{F(x1)},{F(y1)} {F(x2)},{F(y2)} {F(x3)},{F(y3)} ");
        }

        private void OpV(ContentStreamOperation op)
        {
            var (cx, cy) = TxPt(_curX, _curY);
            var (x2, y2) = TxPt(Flt(op,0), Flt(op,1));
            var (x3, y3) = TxPt(Flt(op,2), Flt(op,3));
            _curX = Flt(op,2); _curY = Flt(op,3);
            _path.Append($"C{F(cx)},{F(cy)} {F(x2)},{F(y2)} {F(x3)},{F(y3)} ");
        }

        private void OpY(ContentStreamOperation op)
        {
            var (x1, y1) = TxPt(Flt(op,0), Flt(op,1));
            var (x3, y3) = TxPt(Flt(op,2), Flt(op,3));
            _curX = Flt(op,2); _curY = Flt(op,3);
            _path.Append($"C{F(x1)},{F(y1)} {F(x3)},{F(y3)} {F(x3)},{F(y3)} ");
        }

        private void OpRect(ContentStreamOperation op)
        {
            float x = Flt(op,0), y = Flt(op,1), w = Flt(op,2), h = Flt(op,3);
            var (tx,  ty)  = TxPt(x,     y    );
            var (tx2, ty2) = TxPt(x + w, y    );
            var (tx3, ty3) = TxPt(x + w, y + h);
            var (tx4, ty4) = TxPt(x,     y + h);
            _path.Append($"M{F(tx)},{F(ty)} L{F(tx2)},{F(ty2)} L{F(tx3)},{F(ty3)} L{F(tx4)},{F(ty4)} Z ");
        }

        // ---- Path painting ----

        // Tiling-pattern keys with no entry in the PdfName cache.

        /// <summary>
        /// A fresh graphics state for a <c>/PaintType 1</c> pattern cell: everything at its
        /// §8.4.1 initial value except the CTM, which the caller sets. The counterpart of the
        /// raster path constructing a brand-new <c>PdfRenderContext</c> for the cell and copying
        /// nothing into it unless the pattern is uncoloured.
        /// </summary>
        private SvgGs InitialCellState() => new SvgGs { CTM = Identity() };

        /// <param name="patternName">The <c>/Pattern</c> resource's name.</param>
        /// <param name="elementInverse">
        /// The inverse of the transform the painted element carries, when it carries one, so the
        /// paint resolves in page space after the element's transform; null for a path, whose
        /// coordinates are already in page space.
        /// </param>
        private string ResolvePatternFill(string patternName, float[] elementInverse = null)
        {
            if (Depth >= PdfLimits.MaxRenderRecursionDepth) return null;
            var patRes = _resources?[PdfName.Pattern];
            var patDict = _doc.ResolveObject(patRes) as PdfDictionary;
            if (patDict == null) return null;
            var patStream = _doc.ResolveObject(patDict[new PdfName(patternName)]) as PdfStream;
            if (patStream == null) return null;

            int pid = NextId();
            string id = $"pat{pid}";

            // The tile lives in PATTERN space: its origin is the /BBox corner and its period is
            // XStep/YStep, which default to the BBox extent (§8.7.3.1) — not to the arbitrary
            // 10 units, which would detach every tile from its cell.
            var bboxArr = PdfResolve.Array(_doc, patStream, PdfName.BBox);
            float bx0 = GetArrFloatOr(bboxArr, 0, 0f), by0 = GetArrFloatOr(bboxArr, 1, 0f);
            float bx1 = GetArrFloatOr(bboxArr, 2, 0f), by1 = GetArrFloatOr(bboxArr, 3, 0f);
            float cellW = Math.Abs(bx1 - bx0), cellH = Math.Abs(by1 - by0);
            float originX = Math.Min(bx0, bx1), originY = Math.Min(by0, by1);
            if (!(cellW > 0f) || !(cellH > 0f)) return null;

            // XStep/YStep are direct number entries, not arrays.
            float xStep = (float?)((PdfResolve.Number(_doc, patStream, PdfName.XStep))?.DoubleValue) ?? cellW;
            float yStep = (float?)((PdfResolve.Number(_doc, patStream, PdfName.YStep))?.DoubleValue) ?? cellH;
            if (!(xStep > 0f)) xStep = cellW;
            if (!(yStep > 0f)) yStep = cellH;
            xStep = Math.Abs(xStep);
            yStep = Math.Abs(yStep);

            // A pattern cell is a form XObject execution, charged for the same reason the raster
            // cell render is (RenderToBitmap) — and after the /BBox and step checks, as it is there.
            if (Shared != null && Shared.FailedDecodes.Contains(patStream)) return null;   // failed earlier on this page
            byte[] data;
            try { data = patStream.GetDecodedData(); }
            catch (Exception ex)
            {
                Shared?.FailedDecodes.Add(patStream);
                RenderDiagnostics.Report(RenderDiagnosticKind.TilingPatternDecodeFailed,
                    "SVG tiling-pattern stream decode failed", ex);
                return null;
            }
            if (Shared?.FormBudget != null && !Shared.FormBudget.TryChargeFormInvocation(data.Length)) return null;

            var patResources = patStream[PdfName.Resources] is PdfObject ro
                ? _doc.ResolveObject(ro) as PdfDictionary ?? _resources
                : _resources;

            // §8.7.3.1: a COLOURED pattern (/PaintType 1) supplies its own colour, and its
            // content stream begins in the INITIAL graphics state — "the pattern's appearance
            // is independent of the graphics state in effect when it is used". Only an
            // UNCOLOURED pattern (/PaintType 2) takes the current colour, together with the
            // non-colour parameters (line width, dash, blend mode) the raster path copies in
            // RenderToBitmap.
            //
            // The child does not clone _gs unconditionally, or `1 0 0 rg /Pattern cs /P1 scn` would
            // paint a PaintType 1 cell red wherever the cell relied on the default black.
            long paintType = PdfResolve.Number(_doc, patStream, PdfName.PaintType)?.LongValue ?? 1L;
            bool uncoloured = paintType == 2;

            var patSb = new StringBuilder();
            var child = new SvgContentWriter(patSb, _doc, patResources, _pageW, _pageH);
            child.InheritFrom(this);
            child._gs = uncoloured ? _gs.Clone() : InitialCellState();
            if (uncoloured)
            {
                // The cell takes the current colour and the non-colour parameters, not the alpha,
                // blend mode or mask: those apply once, on the path the pattern fills, as the
                // raster's RenderToBitmap copies none of them into the cell.
                child._gs.FillAlpha   = 1f;
                child._gs.StrokeAlpha = 1f;
                child._gs.BlendMode   = null;
                child._gs.SvgMaskId   = null;
            }
            // An SVG <pattern> tile establishes its OWN coordinate system at the tile corner
            // (x, y) and clips content to [0, 0, width, height] in that system — it does not
            // crop content left in the referencing user space. So the cell has to be emitted
            // with its /BBox origin subtracted; anchoring the tile at the origin AND leaving
            // the content at raw pattern-space coordinates applies the origin twice, which
            // displaces the tile and clips most of the cell away.
            //
            // The raster path does exactly this subtraction in both of its halves —
            // RenderToBitmap(..., bboxOriginX: _minBx, bboxOriginY: _minBy) and
            // flipCell.TransX = -_leftX * _s — and the two renderers must agree.
            child._gs.CTM = new float[] { 1, 0, 0, 1, -originX, -originY };
            // §8.7.3.1: a pattern used inside the cell maps through the cell's own space.
            child._gs.PatternBase = (float[])child._gs.CTM.Clone();
            // Clear pattern names so the child does not recurse — AND the Pattern-space flags
            // with them, or an uncoloured cell would inherit "in a Pattern space with nothing
            // selected" and PatternSelectedNothing would suppress every paint in it.
            child._gs.PatternNameFill   = null;
            child._gs.PatternNameStroke = null;
            child._gs.FillIsPattern     = false;
            child._gs.StrokeIsPattern   = false;
            child.Execute(data);

            // A pattern's /Matrix maps pattern space to the DEFAULT coordinate space of the content
            // stream the pattern is used in (§8.7.3.1, SvgGs.PatternBase: the page's, a form's at
            // its Do, a glyph's), not to the space in force where the pattern is used. Every path
            // this writer emits already carries the CTM (TxPt bakes it in), so patternTransform is
            // /Matrix ∘ base, never the CTM — which applied the CTM a second time on top of the
            // coordinates that already contain it, the same trap the raster construction warns
            // about at ContentStreamInterpreter's "Do NOT include gsCTM⁻¹" note. An element that
            // carries a transform of its own (a <text>, a stencil's rect) resolves the paint in
            // its transformed space, so its inverse is composed on as well.
            float[] m = Compose(ReadMatrix(patStream[PdfName.Matrix]) ?? Identity(), _gs.PatternBase ?? Identity());
            if (elementInverse != null) m = Compose(m, elementInverse);
            string transform = $"matrix({F(m[0])},{F(m[1])},{F(m[2])},{F(m[3])},{F(m[4])},{F(m[5])})";

            // x/y anchor the tile on the BBox corner; width/height are the tiling period. SVG
            // clips tile content to that rectangle, which is the /BBox clip where the step is the
            // cell; a smaller cell is clipped to its box below.
            //
            // A /BBox LARGER than its step is how a pattern makes adjacent cells OVERLAP
            // (§8.7.3.1), and an SVG <pattern> would cut away the part of each cell that spills
            // into its neighbour's tile. So the tile holds every cell of the lattice that reaches
            // into it (TilingLattice, which the raster tile is built from too): the cell once,
            // clipped to its /BBox, and a <use> of it per copy. Only an overlap past the lattice's
            // bound is still PatternApproximated — painted, just not as described.
            var lattice = TilingLattice.For(cellW, cellH, xStep, yStep);
            if (lattice.Approximated && (Shared?.FormBudget?.FirstTilingApproximation() ?? true))
                RenderDiagnostics.Report(RenderDiagnosticKind.PatternApproximated,
                    $"SVG: tiling pattern /{patternName} has a /BBox ({F(cellW)}x{F(cellH)}) so much " +
                    $"larger than its step ({F(xStep)}x{F(yStep)}) that one tile would need more than " +
                    $"{TilingLattice.MaxCopies} copies of its cell; each cell is drawn without its overlap.");

            _sb.Append($"<defs><pattern id=\"{id}\" x=\"{F(originX)}\" y=\"{F(originY)}\"" +
                       $" width=\"{F(xStep)}\" height=\"{F(yStep)}\"" +
                       $" patternUnits=\"userSpaceOnUse\"" +
                       $" patternTransform=\"{transform}\">\n");
            if (lattice.Copies.Count == 1)
            {
                // The tile clips to the step, which is the /BBox clip only when the box is at least
                // the step: a box smaller than its step is clipped to itself, as the raster's cell
                // bitmap — the box's size — clips it, or what the cell draws between the two shows.
                if (cellW < xStep || cellH < yStep)
                {
                    string clipId = $"pat{pid}clip";
                    _sb.Append($"<defs><clipPath id=\"{clipId}\"><rect x=\"0\" y=\"0\" width=\"{F(cellW)}\" height=\"{F(cellH)}\"/></clipPath></defs>" +
                               $"<g clip-path=\"url(#{clipId})\">\n");
                    _sb.Append(patSb.ToString());
                    _sb.Append("</g>\n");
                }
                else
                {
                    _sb.Append(patSb.ToString());
                }
            }
            else
            {
                string cellId = $"pat{pid}cell", clipId = $"pat{pid}clip";
                _sb.Append($"<defs><clipPath id=\"{clipId}\"><rect x=\"0\" y=\"0\" width=\"{F(cellW)}\" height=\"{F(cellH)}\"/></clipPath>" +
                           $"<g id=\"{cellId}\" clip-path=\"url(#{clipId})\">\n");
                _sb.Append(patSb.ToString());
                _sb.Append("</g></defs>\n");
                foreach (var (dx, dy) in lattice.Copies)
                    _sb.Append($"<use xlink:href=\"#{cellId}\" transform=\"translate({F(dx)},{F(dy)})\"/>\n");
            }
            _sb.Append($"</pattern></defs>\n");
            return $"url(#{id})";
        }

        private void PaintPath(bool stroke, bool fill, bool evenOdd)
        {
            string d = _path.ToString().Trim();
            _path.Clear();
            if (d.Length == 0) { ApplyPendingClip(d); return; }

            // §8.6.8 Table 74: a /Pattern space's INITIAL colour "is a pattern object that
            // causes nothing to be painted". `cs` sets that state and only `scn` leaves it, so
            // a paint issued in between must produce no ink. Falling through to _gs.FillColor —
            // the previous behaviour — painted an opaque black rectangle for `/Pattern cs … f`
            // where the raster path (PatternSelectedNothing) paints nothing at all.
            fill   = fill   && !PatternSelectedNothing(stroke: false);
            stroke = stroke && !PatternSelectedNothing(stroke: true);
            if (!fill && !stroke) { ApplyPendingClip(d); return; }

            // §11.7.4.4's overprint case: the fill and stroke at alpha 1 and Normal inside a group
            // that carries the alpha and the blend mode (PdfRenderContext.InOverprintFillStrokeGroup).
            float groupAlpha = _gs.StrokeAlpha;
            string groupBlend = _gs.BlendMode;
            bool overprintGroup = fill && stroke && FillStrokeMode() == FillStrokeComposite.OverprintGroup;
            if (overprintGroup)
            {
                _sb.Append($"<g opacity=\"{F(groupAlpha)}\"{PaintStyleAttribute(true, true)}>\n");
                _gs.FillAlpha = _gs.StrokeAlpha = 1f;
                _gs.BlendMode = null;
            }

            string fillVal = FillPaint;
            string fillPatName = SelectedPatternName(stroke: false);
            // Only when the fill is actually used: a stroke-only paint must not report a
            // problem with a fill pattern whose value it never emits.
            if (fill && fillPatName != null)
            {
                // A named pattern that cannot be built paints NOTHING, as in the raster path.
                // Falling through to _gs.FillColor — the previous behaviour — paints whatever
                // solid colour the graphics state happened to be left holding, which is a
                // confidently wrong result rather than a visibly missing one.
                fillVal = ResolvePatternPaintOrNone(fillPatName, "fill");
            }
            string fillAttr = fill ? $"fill=\"{fillVal}\" fill-opacity=\"{F(_gs.FillAlpha)}\""
                                   : "fill=\"none\"";
            string strokeAttr;
            if (stroke)
            {
                // The stroke half of the same pattern decision the fill half makes above. It used
                // to emit _gs.StrokeColor unconditionally, so a pattern-stroked path exported as
                // whatever solid colour the graphics state was left holding — black by default,
                // because `/Pattern CS` sets no stroke colour and `SCN` with only a name sets none
                // either. The raster path resolves both fill and stroke patterns
                // (ContentStreamInterpreter.Path.cs PaintStroke -> RenderTilingPattern(stroke:true)),
                // so a red pattern-stroked line rendered red and exported black, with no diagnostic.
                string strokeVal = StrokePaint;
                string strokePatName = SelectedPatternName(stroke: true);
                if (strokePatName != null)
                {
                    strokeVal = ResolvePatternPaintOrNone(strokePatName, "stroke");
                }
                // Cap, join, miter limit and dash come from the ONE place all three emitters use —
                // a <path>, a <text> and a glyph-outline <g> are the same §8.4.3 stroke.
                strokeAttr = $"stroke=\"{strokeVal}\" stroke-opacity=\"{F(_gs.StrokeAlpha)}\" " + PathStrokeShape();
            }
            else strokeAttr = "stroke=\"none\"";

            string fillRule  = evenOdd ? " fill-rule=\"evenodd\"" : "";
            // Blend mode or the overprint approximation, decided in ONE place for every painting
            // element, text included. See PaintStyleAttribute for the terms.
            string styleAttr = PaintStyleAttribute(fill, stroke);
            string maskAttr  = SoftMaskAttribute();
            if (fill && stroke && FillStrokeMode() == FillStrokeComposite.Knockout)
                EmitFillKnockedOutByStroke(XmlEsc(d), fillAttr, strokeAttr, fillRule, maskAttr);
            else
                _sb.Append($"<path d=\"{XmlEsc(d)}\" {fillAttr} {strokeAttr}{fillRule}{styleAttr}{maskAttr}/>\n");
            if (overprintGroup)
            {
                _sb.Append("</g>\n");
                _gs.FillAlpha = _gs.StrokeAlpha = groupAlpha;
                _gs.BlendMode = groupBlend;
            }

            // The paint is emitted first: a W/W* recorded before it takes effect only for what
            // follows (§8.5.4), so this element must not be inside the new clip group.
            ApplyPendingClip(d);
        }

        /// <summary>
        /// True when the current colour is a Pattern space's <b>initial</b> colour — §8.6.8
        /// Table 74, "a pattern object that causes nothing to be painted". Set by
        /// <c>cs</c>/<c>CS</c> and cleared by the <c>scn</c> that names a pattern. Reported
        /// rather than silently skipped: a file that reaches here is malformed, and an
        /// invisible fill is exactly the kind of omission that stays invisible.
        /// </summary>
        private bool PatternSelectedNothing(bool stroke)
        {
            bool isPattern = stroke ? _gs.StrokeIsPattern : _gs.FillIsPattern;
            string name    = stroke ? _gs.PatternNameStroke : _gs.PatternNameFill;
            if (!PatternPaint.SelectedNothing(isPattern, name)) return false;
            if (!_unselectedPatternReported)
            {
                _unselectedPatternReported = true;
                RenderDiagnostics.Report(RenderDiagnosticKind.PatternOperandNoName,
                    "SVG: paint operator in a /Pattern colour space with no pattern selected by " +
                    "scn/SCN; nothing painted (§8.6.8 Table 74).");
            }
            return true;
        }

        // One-shot guard for the diagnostic above, as on the raster side.
        private bool _unselectedPatternReported;

        /// <summary>
        /// How a combined fill and stroke composites under this writer's state —
        /// <see cref="FillStrokeCompositing"/>, the answer the raster renderer asks too.
        /// </summary>
        private FillStrokeComposite FillStrokeMode()
            => FillStrokeCompositing.Of(_gs.OverprintFill, _gs.OverprintStroke, _gs.FillAlpha, _gs.StrokeAlpha,
                                        normalBlend: _gs.BlendMode == null, softMask: _gs.SvgMaskId != null);

        private bool _fillStrokeKnockoutReported;


        /// <summary>
        /// A page-space path's stroke shape — width and §8.4.3 geometry, no paint — for the stroke
        /// itself and for the knockout mask, which must cover exactly what the stroke covers.
        /// </summary>
        /// <remarks>
        /// The path data is already in page space (every coordinate went through TxPt), so the
        /// width and the dash array — both user-space lengths — are scaled by the CTM. The geometry's
        /// own scale is 1: this <c>&lt;path&gt;</c> carries no transform, and the dash still picks up
        /// the CTM inside — see <see cref="StrokeGeometryAttributes"/>.
        /// </remarks>
        private string PathStrokeShape()
            => $"stroke-width=\"{F(StrokeWidthOrHairline(_gs.LineWidth, CtmScale(_gs.CTM)))}\""
             + StrokeGeometryAttributes(1f);

        /// <summary>
        /// <c>B</c> and its kin as §11.7.4.4 composites them: the fill masked by its own shape less
        /// the stroke's — the path filled white and stroked black — then the stroke as its own
        /// element, so a translucent stroke composites with the backdrop rather than the fill.
        /// </summary>
        /// <remarks>
        /// <para><b>A soft mask goes on a group round the fill</b>, since an element takes one
        /// <c>mask</c>; the stroke carries it as usual. A masked group is an isolated one in SVG,
        /// so a blend mode inside it would blend against transparency: with both a soft mask and a
        /// blend mode the path is emitted as one element, as before, and reported.</para>
        /// </remarks>
        private void EmitFillKnockedOutByStroke(string d, string fillAttr, string strokeAttr,
                                                string fillRule, string maskAttr)
        {
            if (maskAttr.Length > 0 && _gs.BlendMode != null)
            {
                if (!_fillStrokeKnockoutReported)
                {
                    _fillStrokeKnockoutReported = true;
                    RenderDiagnostics.Report(RenderDiagnosticKind.TransparencyGroupApproximated,
                        "SVG: a path filled and stroked under both a soft mask and a blend mode is " +
                        "exported as one element, so its stroke composites over its fill (§11.7.4.4 " +
                        "keeps the fill out from under it).");
                }
                _sb.Append($"<path d=\"{d}\" {fillAttr} {strokeAttr}{fillRule}{PaintStyleAttribute(true, true)}{maskAttr}/>\n");
                return;
            }

            string id = "fsko" + NextId();
            // The mask's region defaults to the fill element's box with a 10% margin, which holds
            // the fill; what it hides is the stroke's own shape, painted black at full opacity.
            _sb.Append($"<defs><mask id=\"{id}\">" +
                       $"<path d=\"{d}\" fill=\"white\"{fillRule}/>" +
                       $"<path d=\"{d}\" fill=\"none\" stroke=\"black\" {PathStrokeShape()}/></mask></defs>\n");

            string fillElement = $"<path d=\"{d}\" {fillAttr} stroke=\"none\"{fillRule}" +
                                 $"{PaintStyleAttribute(true, false)} mask=\"url(#{id})\"/>";
            if (maskAttr.Length > 0) _sb.Append($"<g{maskAttr}>{fillElement}</g>\n");
            else _sb.Append(fillElement).Append('\n');
            _sb.Append($"<path d=\"{d}\" fill=\"none\" {strokeAttr}{PaintStyleAttribute(false, true)}{maskAttr}/>\n");
        }

        /// <summary>
        /// The pattern this paint side must actually paint, or <c>null</c> — the <b>one</b> place
        /// the Pattern-space flag and the <c>scn</c>-selected name are combined, shared with the
        /// raster interpreter through <see cref="PatternPaint.SelectedName"/>.
        /// </summary>
        /// <remarks>
        /// Not the <b>name alone</b>: a name is not cleared by the operator that leaves the Pattern
        /// space, so <c>/Pattern cs /P1 scn  1 0 0 rg  re f</c> would export the stale <c>/P1</c>
        /// while the raster path — which asks for the flag too — paints red. That is the pattern leak
        /// <c>PatternLeakAndRotatedImageTests</c> was written for, still live on this side because
        /// the test renders raster only.
        /// </remarks>
        private string SelectedPatternName(bool stroke)
            => PatternPaint.SelectedName(
                stroke ? _gs.StrokeIsPattern    : _gs.FillIsPattern,
                stroke ? _gs.PatternNameStroke  : _gs.PatternNameFill);

        private void OpClip(bool evenOdd)
        {
            // Record only — the path still belongs to the painting operator that follows.
            _pendingClip = true;
            _pendingClipEvenOdd = evenOdd;
        }

        /// <summary>
        /// Opens the <c>&lt;g clip-path&gt;</c> group for a pending <c>W</c>/<c>W*</c>, using the
        /// path the painting operator just consumed.
        /// </summary>
        /// <param name="d">
        /// The path data, already trimmed. An <b>empty</b> path yields an empty
        /// <c>&lt;clipPath&gt;</c>, which clips everything out — matching both
        /// <c>SKCanvas.ClipPath</c> on an empty path and the empty-outline text-clip case below.
        /// Returning early instead would leave the region unclipped, which
        /// is the opposite result.
        /// </param>
        private void ApplyPendingClip(string d)
        {
            if (!_pendingClip) return;
            _pendingClip = false;

            int id = NextId();
            string fillRule = _pendingClipEvenOdd ? " clip-rule=\"evenodd\"" : "";
            _sb.Append($"<defs><clipPath id=\"clip{id}\"{fillRule}>");
            if (d.Length > 0) _sb.Append($"<path d=\"{XmlEsc(d)}\"/>");
            _sb.Append("</clipPath></defs>\n");
            _sb.Append($"<g clip-path=\"url(#clip{id})\">\n");
            _gs.ClipGroupCount++; _unclosedGroups++;
        }

        /// <summary>
        /// <c>n</c> — end the path without painting, applying any pending <c>W</c>/<c>W*</c>.
        /// </summary>
        private void OpEndPath()
        {
            string d = _path.ToString().Trim();
            _path.Clear();
            ApplyPendingClip(d);
        }

        // ---- Shading ----
    }
}
