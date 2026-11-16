// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// Renders a SmartArt diagram (<c>a:graphicData uri=".../diagram"</c>).
    ///
    /// <para>
    /// <b>No diagram layout engine is involved, and none is needed.</b> The body only holds a
    /// <c>dgm:relIds</c> pointing at four parts (data, layout, quick style, colours) — running that
    /// layout would mean implementing the whole SmartArt engine. But Word also <i>persists the
    /// result</i>: a <c>DiagramPersistLayoutPart</c> (<c>word/diagrams/drawing1.xml</c>) whose
    /// <c>dsp:spTree</c> holds one shape per node, each with an <b>absolute</b> <c>a:xfrm</c>,
    /// its resolved fill, and its text. It exists so that a consumer which cannot lay out a
    /// diagram can still draw one — which is exactly this case.
    /// </para>
    ///
    /// <para>
    /// So this file is a part lookup and a scale. The painting is
    /// <see cref="ShapeTreeRenderer"/>, unchanged: a <c>dsp:sp</c> is structurally identical to a
    /// <c>wps:wsp</c>, which is why everything there is matched by local name.
    /// </para>
    ///
    /// <para>
    /// A document produced by something other than Word may have no persisted drawing. That case
    /// reserves the diagram's box and reports, rather than guessing at a layout.
    /// </para>
    /// </summary>
    internal static class SmartArtRenderer
    {
        /// <summary>
        /// Finds the persisted drawing's shape tree for the <c>dgm:relIds</c> inside
        /// <paramref name="graphicData"/>, or <c>null</c> with a reported reason.
        /// </summary>
        internal static OpenXmlElement FindShapeTree(OpenXmlElement graphicData, DocxRenderContext context)
        {
            var relIds = graphicData.Descendants().FirstOrDefault(e => e.LocalName == "relIds");
            if (relIds == null)
            {
                DocxDiagnostics.Report("SmartArtRenderer.Resolve",
                    "the SmartArt drawing carries no dgm:relIds, so its parts cannot be located");
                return null;
            }

            var drawingPart = ResolvePersistedDrawing(relIds, graphicData, context);
            if (drawingPart?.Drawing == null)
            {
                DocxDiagnostics.Report("SmartArtRenderer.Resolve",
                    "this SmartArt diagram has no persisted drawing part (word/diagrams/drawingN.xml), "
                        + "which is where Word stores the laid-out shapes; laying the diagram out from "
                        + "its data model is not supported, so its box was reserved instead");
                return null;
            }

            var spTree = drawingPart.Drawing.ChildElements.FirstOrDefault(e => e.LocalName == "spTree");
            if (spTree == null)
                DocxDiagnostics.Report("SmartArtRenderer.Resolve",
                    "the persisted SmartArt drawing has no dsp:spTree");
            return spTree;
        }

        /// <summary>
        /// The persisted drawing's own extent, so it can be scaled onto the <c>wp:extent</c> the
        /// body reserves. In Word's own output the two agree and the scale is the identity, but a
        /// diagram resized after it was persisted needs it.
        /// </summary>
        internal static DrawingTransform.Matrix ScaleToBox(OpenXmlElement spTree, DrawingPlacement.Box box)
        {
            var grpSpPr = spTree.ChildElements.FirstOrDefault(e => e.LocalName == "grpSpPr");
            var xfrm = DrawingTransform.FindXfrm(grpSpPr);

            double persistW = 0, persistH = 0;
            if (xfrm != null && DrawingTransform.TryReadBox(xfrm, out _, out _, out double bw, out double bh))
            {
                persistW = bw; persistH = bh;
            }
            else
            {
                // No group extent: take the union of the children's own boxes.
                foreach (var sp in spTree.ChildElements.Where(e => e.LocalName == "sp"))
                {
                    var spPr = sp.ChildElements.FirstOrDefault(e => e.LocalName == "spPr");
                    if (!DrawingTransform.TryReadBox(DrawingTransform.FindXfrm(spPr),
                            out double x, out double y, out double w, out double h))
                        continue;
                    persistW = Math.Max(persistW, x + w);
                    persistH = Math.Max(persistH, y + h);
                }
            }

            if (persistW <= 0 || persistH <= 0) return DrawingTransform.Matrix.Identity;

            double sx = box.Width / persistW, sy = box.Height / persistH;
            if (Math.Abs(sx - 1.0) < 1e-6 && Math.Abs(sy - 1.0) < 1e-6)
                return DrawingTransform.Matrix.Identity;

            return new DrawingTransform.Matrix(sx, 0, 0, sy, 0, 0);
        }

        /// <summary>
        /// Locates the <see cref="DiagramPersistLayoutPart"/> for a <c>dgm:relIds</c>.
        ///
        /// <para>
        /// The link is the <c>dsp:dataModelExt/@relId</c> extension inside the <b>data</b> part —
        /// and that relationship belongs to <i>the part the drawing lives in</i>, which is the main
        /// document only when the diagram is in the body. A diagram inside a header, a footer or a
        /// nested text box hangs off a different part, so resolving against
        /// <c>MainDocumentPart</c> alone would silently miss it.
        /// </para>
        /// </summary>
        private static DiagramPersistLayoutPart ResolvePersistedDrawing(OpenXmlElement relIds,
            OpenXmlElement graphicData, DocxRenderContext context)
        {
            var owner = OpenXmlPartLookup.OwningPart(graphicData) ?? context.WordDocument.MainDocumentPart;
            if (owner == null) return null;

            string dmId = Attr(relIds, "dm");
            if (!string.IsNullOrEmpty(dmId) && TryGetPart(owner, dmId) is DiagramDataPart dataPart)
            {
                // dsp:dataModelExt names the drawing part, by a relationship of the data part.
                var ext = dataPart.DataModelRoot?.Descendants()
                    .FirstOrDefault(e => e.LocalName == "dataModelExt");
                string drawingRel = Attr(ext, "relId");
                if (!string.IsNullOrEmpty(drawingRel)
                    && TryGetPart(dataPart, drawingRel) is DiagramPersistLayoutPart viaData)
                    return viaData;

                // Some producers hang the extension's relationship off the owning part instead.
                if (!string.IsNullOrEmpty(drawingRel)
                    && TryGetPart(owner, drawingRel) is DiagramPersistLayoutPart viaOwner)
                    return viaOwner;
            }

            // Last resort: an unambiguous single persisted drawing on the owning part. Only when
            // there is exactly one, because picking arbitrarily from several would draw the wrong
            // diagram — which is worse than reserving the box and saying so.
            var candidates = owner.Parts
                .Select(p => p.OpenXmlPart)
                .OfType<DiagramPersistLayoutPart>()
                .ToList();
            if (candidates.Count == 1) return candidates[0];

            if (candidates.Count > 1)
                DocxDiagnostics.Report("SmartArtRenderer.Resolve", string.Format(
                    CultureInfo.InvariantCulture,
                    "the diagram's data part does not name its persisted drawing and the document has "
                        + "{0} of them, so none could be chosen safely",
                    candidates.Count));
            return null;
        }

        // GetPartById throws for an id the part does not own — an ordinary outcome here, since we
        // try more than one owner. The single implementation lives in OpenXmlPartLookup.
        private static OpenXmlPart TryGetPart(OpenXmlPart owner, string relationshipId)
            => OpenXmlPartLookup.TryGetPart(owner, relationshipId);

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);
    }
}
