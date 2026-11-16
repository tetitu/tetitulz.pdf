// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using WpDrawing = DocumentFormat.OpenXml.Drawing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// The single entry point for a <c>&lt;w:drawing&gt;</c>: decides what kind of graphic it is
    /// and routes it, and guarantees that anything it cannot draw still occupies its declared
    /// space and says so.
    ///
    /// <para>
    /// A DrawingML graphic names its kind in <c>a:graphicData/@uri</c> — picture, shape, group,
    /// SmartArt diagram, chart. Dispatching on that is what lets a new kind be added without
    /// disturbing the others. The structural heuristics (does
    /// a descendant <c>a:blip</c> carry an <c>r:embed</c>? is there a <c>prstGeom</c>?) are kept
    /// as the <c>default</c> arm, ahead of the fallback, so a producer that writes an absent or
    /// non-standard uri keeps rendering exactly as it did.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing is dropped on the floor.</b> A drawing with neither a blip nor a preset geometry —
    /// which is every SmartArt diagram and every chart — would otherwise paint nothing, report
    /// nothing and consume <b>zero vertical space</b>, so every subsequent page of the document
    /// would paginate as though the drawing were not there. Silent
    /// wrong output is the failure mode this repository treats as worst, so unrenderable content
    /// now reserves its <c>wp:extent</c> box and reports through <see cref="DocxDiagnostics"/>.
    /// Nothing is painted in its place: a placeholder box would be content the source document
    /// does not contain.
    /// </para>
    /// </summary>
    internal sealed class DrawingRenderer
    {
        // a:graphicData/@uri values. These identify the graphic's kind and are stable parts of
        // ECMA-376 / the Office extensions, not implementation details of a particular producer.
        private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
        private const string ChartUri = "http://schemas.openxmlformats.org/drawingml/2006/chart";
        private const string DiagramUri = "http://schemas.openxmlformats.org/drawingml/2006/diagram";
        private const string TableUri = "http://schemas.openxmlformats.org/drawingml/2006/table";
        private const string ShapeUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingShape";
        private const string GroupUri = "http://schemas.microsoft.com/office/word/2010/wordprocessingGroup";
        private const string ChartExUri = "http://schemas.microsoft.com/office/drawing/2014/chartex";

        private readonly ImageRenderer _images = new ImageRenderer();
        private readonly ShapeTreeRenderer _shapes = new ShapeTreeRenderer();
        private readonly Charts.ChartRenderer _charts = new Charts.ChartRenderer();

        internal void Render(OpenXmlElement drawing, DocxRenderContext context)
        {
            var inline = drawing.Descendants<DW.Inline>().FirstOrDefault();
            var anchor = inline == null ? drawing.Descendants<DW.Anchor>().FirstOrDefault() : null;
            OpenXmlElement container = (OpenXmlElement)inline ?? anchor;
            if (container == null) return;

            switch (GraphicDataUri(container))
            {
                case PictureUri:
                    _images.RenderPicture(inline, anchor, context);
                    return;

                case ShapeUri:
                case GroupUri:
                    RenderShapeTree(inline, anchor, container, context);
                    return;

                case DiagramUri:
                    RenderSmartArt(inline, anchor, container, context);
                    return;

                case ChartUri:
                    RenderChart(inline, anchor, container, context);
                    return;

                // No handler yet. Each reserves its box and reports rather than vanishing.
                case ChartExUri:
                    Reserve("extended chart (waterfall / treemap / sunburst / funnel / box-whisker)",
                        inline, anchor, container, context);
                    return;
                case TableUri:
                    Reserve("floating DrawingML table", inline, anchor, container, context);
                    return;

                default:
                    // Absent or unrecognised uri: fall back to the structural test, so files from
                    // producers that do not write a standard uri render as they always have.
                    if (HasBlip(container))
                    {
                        _images.RenderPicture(inline, anchor, context);
                        return;
                    }
                    if (HasPresetGeometry(container))
                    {
                        RenderShapeTree(inline, anchor, container, context);
                        return;
                    }
                    Reserve(Describe(GraphicDataUri(container)), inline, anchor, container, context);
                    return;
            }
        }

        /// <summary>
        /// The height an inline drawing would occupy in a column <paramref name="maxWidth"/> wide,
        /// or 0 when it has no usable extent. Lets a caller that lays out its own boxes — a table
        /// cell — reserve the right amount of room before anything is painted.
        /// </summary>
        internal static double MeasureInline(OpenXmlElement drawing, double maxWidth)
        {
            var container = Container(drawing);
            if (container == null) return 0;
            if (!DrawingPlacement.TryReadExtent(container, out double w, out double h)) return 0;

            if (w > maxWidth && w > 0) { h *= maxWidth / w; }
            return h;
        }

        /// <summary>
        /// Paints a drawing into a box the caller decided, without touching the flow cursor or
        /// registering a float. This is the entry point for a drawing inside a <b>table cell</b>,
        /// whose position comes from the cell's own layout.
        ///
        /// <para>
        /// Anchoring is deliberately ignored here: a float inside a cell would need the cell's
        /// frame as its reference, which the flow's page-level exclusion rects cannot express. The
        /// drawing is placed inline within the cell instead, which is what Word does for the common
        /// "layoutInCell" case.
        /// </para>
        /// </summary>
        internal void RenderAt(OpenXmlElement drawing, double x, double topY, double maxWidth,
            DocxRenderContext context)
        {
            var container = Container(drawing);
            if (container == null) return;

            // A cell's drawing is placed by the cell, so there is no flow space to reserve — but
            // the loss is still reported. Silence here was indistinguishable from a cell that never
            // held a drawing.
            if (!DrawingPlacement.TryReadExtent(container, out double w, out double h))
            {
                DocxDiagnostics.Report("DrawingRenderer.RenderAt",
                    "a drawing in a table cell declares no usable wp:extent; it was not painted "
                        + "and no space could be reserved for it");
                return;
            }

            if (w > maxWidth && w > 0) { double s = maxWidth / w; w *= s; h *= s; }
            var box = new DrawingPlacement.Box(x, topY, w, h);

            string uri = GraphicDataUri(container);
            var graphicData = container.Descendants().FirstOrDefault(e => e.LocalName == "graphicData");

            switch (uri)
            {
                case PictureUri:
                    if (!_images.RenderPictureAt(container, box, context))
                        DocxDiagnostics.Report("DrawingRenderer.RenderAt",
                            "a picture in a table cell could not be decoded and was not painted");
                    return;

                case ShapeUri:
                case GroupUri:
                    _shapes.Render(graphicData, box, DrawingTransform.Matrix.Identity, context);
                    return;

                case DiagramUri:
                {
                    var spTree = graphicData == null ? null : SmartArtRenderer.FindShapeTree(graphicData, context);
                    if (spTree != null)
                        _shapes.Render(spTree, box, SmartArtRenderer.ScaleToBox(spTree, box), context);
                    else
                        DocxDiagnostics.Report("DrawingRenderer.RenderAt",
                            "a SmartArt diagram in a table cell has no persisted drawing; its space was "
                                + "reserved but nothing was painted");
                    return;
                }

                default:
                    if (HasBlip(container))
                    {
                        _images.RenderPictureAt(container, box, context);
                        return;
                    }
                    if (HasPresetGeometry(container))
                    {
                        _shapes.Render(graphicData, box, DrawingTransform.Matrix.Identity, context);
                        return;
                    }
                    DocxDiagnostics.Report("DrawingRenderer.RenderAt",
                        Describe(uri) + " in a table cell is not rendered; its space was reserved");
                    return;
            }
        }

        private static OpenXmlElement Container(OpenXmlElement drawing)
        {
            var inline = drawing.Descendants<DW.Inline>().FirstOrDefault();
            return (OpenXmlElement)inline ?? drawing.Descendants<DW.Anchor>().FirstOrDefault();
        }

        /// <summary>
        /// A chart: lay it out from the values cached in its chart part and paint it. Anything that
        /// cannot be plotted — an unsupported kind, or values that live only in the embedded
        /// workbook — falls back to reserving the box and reporting.
        /// </summary>
        private void RenderChart(DW.Inline inline, DW.Anchor anchor, OpenXmlElement container,
            DocxRenderContext context)
        {
            if (!DrawingPlacement.TryReadExtent(container, out double w, out double h))
            {
                Reserve("chart", inline, anchor, container, context);
                return;
            }

            var graphicData = container.Descendants().FirstOrDefault(e => e.LocalName == "graphicData");

            if (inline != null)
            {
                var box = DrawingPlacement.PlaceInline(w, h, context);
                if (!_charts.TryRender(graphicData, box, context))
                    ReportReserved("chart", box);
                context.AdvanceCursor(box.Height);
            }
            else
            {
                var box = DrawingPlacement.PlaceAnchored(anchor, w, h, context);
                if (!_charts.TryRender(graphicData, box, context))
                    ReportReserved("chart", box);
                DrawingPlacement.RegisterFloat(anchor, box, context);
            }
        }

        private static void ReportReserved(string what, DrawingPlacement.Box box)
            => DocxDiagnostics.Report("DrawingRenderer.Reserve", string.Format(
                CultureInfo.InvariantCulture,
                "{0} is not rendered; {1:F1}pt of vertical space was reserved so the rest of "
                    + "the document still paginates correctly",
                what, box.Height));

        /// <summary>
        /// A SmartArt diagram: paint the shape tree Word persisted for it, scaled onto the box the
        /// body reserves. With no persisted drawing there is nothing to lay out, so it falls back
        /// to reserving the box and reporting.
        /// </summary>
        private void RenderSmartArt(DW.Inline inline, DW.Anchor anchor, OpenXmlElement container,
            DocxRenderContext context)
        {
            var graphicData = container.Descendants().FirstOrDefault(e => e.LocalName == "graphicData");
            var spTree = graphicData == null ? null : SmartArtRenderer.FindShapeTree(graphicData, context);
            if (spTree == null)
            {
                Reserve("SmartArt diagram", inline, anchor, container, context);
                return;
            }

            // No usable wp:extent. Route through Reserve rather than returning: it reports the loss
            // (and reserves whatever space it can), so a SmartArt diagram that vanishes is
            // distinguishable from a document that never had one, whatever its graphicData uri.
            if (!DrawingPlacement.TryReadExtent(container, out double w, out double h))
            {
                Reserve("SmartArt diagram", inline, anchor, container, context);
                return;
            }

            if (inline != null)
            {
                var box = DrawingPlacement.PlaceInline(w, h, context);
                _shapes.Render(spTree, box, SmartArtRenderer.ScaleToBox(spTree, box), context);
                context.AdvanceCursor(box.Height);
            }
            else
            {
                var box = DrawingPlacement.PlaceAnchored(anchor, w, h, context);
                _shapes.Render(spTree, box, SmartArtRenderer.ScaleToBox(spTree, box), context);
                DrawingPlacement.RegisterFloat(anchor, box, context);
            }
        }

        /// <summary>
        /// Places the drawing, paints its shape tree into that box, and — for an anchored drawing —
        /// registers the exclusion rectangle body text wraps around.
        /// </summary>
        private void RenderShapeTree(DW.Inline inline, DW.Anchor anchor, OpenXmlElement container,
            DocxRenderContext context)
        {
            // Both exits report. A wps:wsp that omits wp:extent (or states cx="0"), or that carries
            // no a:graphicData, took its text box and everything in it out of the PDF with
            // LastDiagnostic still null — the exact silent-loss defect this type exists to close,
            // in the one arm that never got the treatment.
            if (!DrawingPlacement.TryReadExtent(container, out double w, out double h))
            {
                Reserve("shape or group", inline, anchor, container, context);
                return;
            }

            var root = container.Descendants().FirstOrDefault(e => e.LocalName == "graphicData");
            if (root == null)
            {
                Reserve("shape or group", inline, anchor, container, context);
                return;
            }

            if (inline != null)
            {
                var box = DrawingPlacement.PlaceInline(w, h, context);
                _shapes.Render(root, box, DrawingTransform.Matrix.Identity, context);
                context.AdvanceCursor(box.Height);
            }
            else
            {
                var box = DrawingPlacement.PlaceAnchored(anchor, w, h, context);
                _shapes.Render(root, box, DrawingTransform.Matrix.Identity, context);
                // An anchored shape excludes body text exactly as an anchored image does, or text
                // would run straight over every floating shape.
                DrawingPlacement.RegisterFloat(anchor, box, context);
            }
        }

        /// <summary>
        /// Gives unrenderable content its declared space back, and says what was lost.
        /// An inline drawing advances the flow cursor by its <c>wp:extent</c> height; an anchored
        /// one is placed and registers its exclusion rectangle, exactly as a rendered drawing
        /// would, so text neither overlaps the gap nor closes it up.
        /// </summary>
        private static void Reserve(string what, DW.Inline inline, DW.Anchor anchor,
            OpenXmlElement container, DocxRenderContext context)
        {
            if (!DrawingPlacement.TryReadExtent(container, out double w, out double h))
            {
                DocxDiagnostics.Report("DrawingRenderer.Reserve",
                    what + " is not rendered, and it declares no usable wp:extent, so no space "
                         + "could be reserved for it either");
                return;
            }

            if (inline != null)
            {
                var box = DrawingPlacement.PlaceInline(w, h, context);
                context.AdvanceCursor(box.Height);
                DocxDiagnostics.Report("DrawingRenderer.Reserve", string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} is not rendered; {1:F1}pt of vertical space was reserved so the rest of "
                        + "the document still paginates correctly",
                    what, box.Height));
            }
            else
            {
                var box = DrawingPlacement.PlaceAnchored(anchor, w, h, context);
                DrawingPlacement.RegisterFloat(anchor, box, context);
                DocxDiagnostics.Report("DrawingRenderer.Reserve", string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} is not rendered; its {1:F1}×{2:F1}pt floating box was reserved so "
                        + "surrounding text still wraps around it",
                    what, box.Width, box.Height));
            }
        }

        private static string Describe(string uri)
            => string.IsNullOrEmpty(uri)
                ? "a drawing with no recognised graphic content"
                : "a drawing of an unsupported kind (graphicData uri '" + uri + "')";

        private static string GraphicDataUri(OpenXmlElement container)
            => OoxmlAttribute.Value(
                container.Descendants().FirstOrDefault(e => e.LocalName == "graphicData"), "uri");

        private static bool HasBlip(OpenXmlElement container)
            => container.Descendants<WpDrawing.Blip>().Any(b => !string.IsNullOrEmpty(b.Embed?.Value));

        private static bool HasPresetGeometry(OpenXmlElement container)
            => container.Descendants().Any(e => e.LocalName == "prstGeom");
    }
}
