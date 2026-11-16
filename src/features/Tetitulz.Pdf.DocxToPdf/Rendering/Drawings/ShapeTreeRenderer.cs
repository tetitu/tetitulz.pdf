// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// Paints a DrawingML shape tree — one <c>wps:wsp</c>, a <c>wpg:wgp</c> group, or the
    /// <c>dsp:spTree</c> of a persisted SmartArt drawing.
    ///
    /// <para>
    /// The three are the <b>same structure in three namespaces</b>: a shape with
    /// <c>…spPr</c> (geometry + fill + line), an optional <c>…style</c>, an optional transform, and
    /// children. Everything here is therefore matched by <see cref="OpenXmlElement.LocalName"/>,
    /// never by SDK wrapper type — <c>GetFirstChild&lt;A.ShapeStyle&gt;()</c> silently returns null
    /// for the <c>dsp</c> spelling, which is exactly the kind of near-miss that produces a blank
    /// diagram and no error.
    /// </para>
    ///
    /// <para>
    /// Every shape, at its own <c>a:xfrm</c>: painting only the <b>first</b> <c>prstGeom</c> found
    /// anywhere in the drawing, at the drawing's full extent, would turn a group of N shapes into
    /// one rectangle.
    /// </para>
    /// </summary>
    internal sealed class ShapeTreeRenderer
    {
        private DrawingColorResolver _colors;
        private DocxRenderContext _context;

        /// <summary>
        /// Paints the tree rooted at <paramref name="root"/> into <paramref name="box"/>.
        /// <paramref name="rootScale"/> maps the tree's own coordinate space onto the box (identity
        /// for an ordinary shape, whose geometry is already in the box's units; a real scale for a
        /// SmartArt drawing that was resized after Word persisted it).
        /// </summary>
        internal void Render(OpenXmlElement root, DrawingPlacement.Box box,
            DrawingTransform.Matrix rootScale, DocxRenderContext context)
        {
            if (root == null) return;

            var ops = new StringBuilder();
            ops.Append(DrawingTransform.OpenDrawingFrame(box.X, box.TopY));

            _colors = context.DrawingColors;
            _context = context;
            int painted = 0;
            bool truncated = false;
            WalkNode(root, rootScale, box, ops, 0, ref painted, ref truncated, context);

            ops.Append(DrawingTransform.CloseFrame);
            context.AppendContent(ops.ToString());

            // Reported from the truncation itself, not from `painted` reaching the cap. The walk
            // stops BEFORE painting the (cap+1)-th shape, so `painted == cap` is the case where
            // every shape was drawn and nothing was skipped — and the message claimed the
            // opposite. A diagnostic channel that fires on conformant input is one callers learn
            // to ignore, which is worse than not reporting at all.
            if (truncated)
                DocxDiagnostics.Report("ShapeTreeRenderer.Walk", string.Format(
                    CultureInfo.InvariantCulture,
                    "the drawing declares more than {0} shapes; the remainder were skipped",
                    PdfLimits.MaxDrawingShapes));
        }

        private void WalkNode(OpenXmlElement node, DrawingTransform.Matrix parent,
            DrawingPlacement.Box box, StringBuilder ops, int depth, ref int painted,
            ref bool truncated, DocxRenderContext context)
        {
            if (node == null) return;
            if (painted >= PdfLimits.MaxDrawingShapes) { truncated = true; return; }

            if (depth > PdfLimits.MaxShapeTreeDepth)
            {
                DocxDiagnostics.Report("ShapeTreeRenderer.Walk", string.Format(
                    CultureInfo.InvariantCulture,
                    "group nesting exceeds {0} levels; the deeper shapes were skipped",
                    PdfLimits.MaxShapeTreeDepth));
                return;
            }

            switch (node.LocalName)
            {
                case "wsp":     // wps:wsp — a Word shape
                case "sp":      // a:sp / dsp:sp
                case "cxnSp":   // a connector, painted like any other stroked shape
                    PaintShape(node, parent, box, ops, ref painted);
                    return;

                case "wgp":     // wpg:wgp — a Word group
                case "grpSp":   // a:grpSp / dsp:grpSp
                case "spTree":  // dsp:spTree — the root of a persisted SmartArt drawing
                case "grpSpPr": // reached only when a group's properties precede its children
                {
                    var frame = DrawingTransform.GroupMatrix(FindGroupXfrm(node)).Then(parent);
                    foreach (var child in node.ChildElements)
                    {
                        if (painted >= PdfLimits.MaxDrawingShapes) { truncated = true; return; }
                        if (IsPropertiesOnly(child.LocalName)) continue;
                        WalkNode(child, frame, box, ops, depth + 1, ref painted, ref truncated, context);
                    }
                    return;
                }

                case "pic":     // a picture inside a group — needs the raster path, not this one
                    DocxDiagnostics.Report("ShapeTreeRenderer.Walk",
                        "a picture inside a shape group is not painted yet");
                    return;

                case "graphicFrame":
                    DocxDiagnostics.Report("ShapeTreeRenderer.Walk",
                        "a nested graphic frame (table / chart / diagram inside a group) is not painted");
                    return;

                default:
                    // Wrappers the tree hangs off (a:graphicData, wpg:wgp's own containers, …).
                    foreach (var child in node.ChildElements)
                    {
                        if (painted >= PdfLimits.MaxDrawingShapes) { truncated = true; return; }
                        WalkNode(child, parent, box, ops, depth + 1, ref painted, ref truncated, context);
                    }
                    return;
            }
        }

        private void PaintShape(OpenXmlElement shape, DrawingTransform.Matrix parent,
            DrawingPlacement.Box box, StringBuilder ops, ref int painted)
        {
            var spPr = shape.ChildElements.FirstOrDefault(e => e.LocalName == "spPr");
            var geom = spPr?.ChildElements.FirstOrDefault(e => e.LocalName == "prstGeom");
            var custGeom = spPr?.ChildElements.FirstOrDefault(e => e.LocalName == "custGeom");
            string preset = Attr(geom, "prst") ?? "rect";

            var xfrm = DrawingTransform.FindXfrm(spPr);
            // A shape with no a:xfrm fills the whole drawing box — which is the only case the old
            // renderer handled, and the reason a group collapsed to a single rectangle.
            if (!DrawingTransform.TryReadBox(xfrm, out double x, out double y, out double w, out double h))
            {
                x = 0; y = 0; w = box.Width; h = box.Height;
            }
            if (w <= 0 || h <= 0) return;

            var fill = ShapeFill.Resolve(spPr, shape, _colors);
            bool strokeOnly = PresetGeometry.IsStrokeOnly(preset);
            bool hasFill = !strokeOnly && fill.FillRgb != null;
            bool hasStroke = fill.StrokeRgb != null && fill.StrokeWidthPt > 0;
            bool hasText = ShapeTextRenderer.HasText(shape);

            string gradientName = null;
            if (!strokeOnly && !hasFill && _context != null
                && GradientFill.TryRead(spPr, _colors, out var gradient))
                gradientName = GradientFill.Register(gradient, w, h, _context);

            // A text box commonly has neither fill nor outline — dropping it here would lose the
            // text with it, which is why the early-out tests every way a shape can be visible.
            if (!hasFill && !hasStroke && !hasText && gradientName == null) return;

            var matrix = DrawingTransform.ShapeMatrix(xfrm, x, y, w, h).Then(parent);

            ops.Append("q\n");
            ops.Append(matrix.ToCm());

            if (fill.NeedsAlpha && _context != null)
            {
                string gs = _context.RegisterAlpha(
                    hasFill ? fill.FillAlpha : (double?)null,
                    hasStroke ? fill.StrokeAlpha : (double?)null);
                ops.Append('/').Append(gs).Append(" gs\n");
            }

            if (hasFill)
                ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F3} {1:F3} {2:F3} rg\n",
                    fill.FillRgb.Value.R, fill.FillRgb.Value.G, fill.FillRgb.Value.B);
            if (hasStroke)
            {
                // Undo the group frame's scale so the pen keeps its authored on-page width.
                double widthPt = fill.StrokeWidthPt / matrix.UniformScale;
                ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F3} {1:F3} {2:F3} RG\n{3:F2} w\n",
                    fill.StrokeRgb.Value.R, fill.StrokeRgb.Value.G, fill.StrokeRgb.Value.B, widthPt);

                if (fill.Dash != null)
                {
                    ops.Append('[');
                    for (int i = 0; i < fill.Dash.Length; i++)
                    {
                        if (i > 0) ops.Append(' ');
                        ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2}",
                            fill.Dash[i] / matrix.UniformScale);
                    }
                    ops.Append("] 0 d\n");
                }
            }

            if (gradientName != null)
            {
                // A shading has no path of its own: clip to the geometry, then let `sh` fill
                // everything the clip allows. Wrapped in its own q…Q so the clip does not leak
                // onto the outline stroked afterwards.
                ops.Append("q\n");
                EmitGeometry(preset, geom, custGeom, w, h, ops);
                ops.Append("W n\n");
                ops.Append('/').Append(gradientName).Append(" sh\n");
                ops.Append("Q\n");
            }

            if (hasFill || hasStroke)
            {
                EmitGeometry(preset, geom, custGeom, w, h, ops);
                ops.Append(hasFill && hasStroke ? "B\n" : hasFill ? "f\n" : "S\n");
            }

            // Text rides inside the shape's own q…Q, so it follows the shape's placement and
            // rotation without a second transform.
            if (hasText) ShapeTextRenderer.Render(shape, w, h, ops, _context, _colors);

            ops.Append("Q\n");
            painted++;
        }

        // Writes the shape's outline. Shared by the gradient clip and the fill/stroke paint, so the
        // two can never describe different shapes.
        private static void EmitGeometry(string preset, OpenXmlElement geom, OpenXmlElement custGeom,
            double w, double h, StringBuilder ops)
        {
            var path = new GeometryPath(ops);

            // A custom geometry states its own contours, so it wins over any preset name.
            if (custGeom != null)
            {
                if (PresetGeometry.TryBuildCustom(custGeom, path, w, h)) return;
                DocxDiagnostics.Report("PresetGeometry.TryBuildCustom",
                    "a custom geometry produced no drawable contour; its bounding rectangle was drawn instead");
                path.Rectangle(0, 0, w, h);
                return;
            }

            if (!PresetGeometry.TryBuild(preset, path, w, h, AdjustValues.Read(geom)))
                DocxDiagnostics.Report("PresetGeometry.TryBuild",
                    "preset geometry '" + preset + "' is not implemented; its bounding rectangle was drawn instead");
        }

        // A group's own a:xfrm lives under its group-shape-properties element, whose local name
        // differs per namespace (wpg:grpSpPr, a:grpSpPr, dsp:grpSpPr) but always ends "grpSpPr".
        private static OpenXmlElement FindGroupXfrm(OpenXmlElement group)
        {
            var grpSpPr = group.ChildElements.FirstOrDefault(e => e.LocalName == "grpSpPr");
            return DrawingTransform.FindXfrm(grpSpPr);
        }

        private static bool IsPropertiesOnly(string localName)
            => localName == "nvGrpSpPr" || localName == "grpSpPr"
            || localName == "cNvPr" || localName == "cNvGrpSpPr";

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);
    }
}
