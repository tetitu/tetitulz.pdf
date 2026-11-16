// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// Reads an <c>a:gradFill</c> into the stops and axis a PDF shading needs.
    ///
    /// <para>
    /// Gradients are the default for several stock SmartArt styles and every "Intense Effect" shape
    /// style, so approximating one with a solid colour is a <i>confidently wrong</i> result rather
    /// than an obviously missing one — the failure mode this repository treats as worst. Hence real
    /// axial and radial shadings.
    /// </para>
    /// </summary>
    internal static class GradientFill
    {
        /// <summary>
        /// A gradient resolved into device terms: its stops, and whether it is linear (with an
        /// angle) or radial.
        /// </summary>
        internal readonly struct Resolved
        {
            internal Resolved(List<PdfContentStreamBuilder.ShadingStop> stops, bool radial, double angleRad)
            {
                Stops = stops; IsRadial = radial; AngleRadians = angleRad;
            }

            internal List<PdfContentStreamBuilder.ShadingStop> Stops { get; }
            internal bool IsRadial { get; }

            /// <summary>Clockwise-on-screen, matching <c>a:lin@ang</c>. Unused when radial.</summary>
            internal double AngleRadians { get; }
        }

        /// <summary>
        /// Reads the <c>a:gradFill</c> under <paramref name="spPr"/>, or returns false when there is
        /// none or it cannot be resolved.
        /// </summary>
        internal static bool TryRead(OpenXmlElement spPr, DrawingColorResolver colors, out Resolved result)
        {
            result = default;

            var grad = spPr?.ChildElements.FirstOrDefault(e => e.LocalName == "gradFill");
            var gsLst = grad?.ChildElements.FirstOrDefault(e => e.LocalName == "gsLst");
            if (gsLst == null) return false;

            var stops = new List<PdfContentStreamBuilder.ShadingStop>();
            foreach (var gs in gsLst.ChildElements.Where(e => e.LocalName == "gs"))
            {
                if (!colors.TryResolve(gs, null, out var rgba)) continue;

                double pos = 0;
                string posAttr = Attr(gs, "pos");
                if (double.TryParse(posAttr, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw))
                    pos = raw / 100000.0;

                stops.Add(new PdfContentStreamBuilder.ShadingStop(pos, rgba.R, rgba.G, rgba.B));
                if (stops.Count >= PdfLimits.MaxShadingStops) break;
            }

            if (stops.Count == 0) return false;

            if (gsLst.ChildElements.Count(e => e.LocalName == "gs") > PdfLimits.MaxShadingStops)
                DocxDiagnostics.Report("GradientFill.TryRead", string.Format(
                    CultureInfo.InvariantCulture,
                    "the gradient declares more than {0} stops; the remainder were dropped",
                    PdfLimits.MaxShadingStops));

            var lin = grad.ChildElements.FirstOrDefault(e => e.LocalName == "lin");
            var path = grad.ChildElements.FirstOrDefault(e => e.LocalName == "path");

            if (path != null)
            {
                string kind = Attr(path, "path");
                if (!string.Equals(kind, "circle", StringComparison.Ordinal))
                    DocxDiagnostics.Report("GradientFill.TryRead",
                        "gradient path '" + (kind ?? "(none)") + "' is approximated as a radial gradient");
                result = new Resolved(stops, radial: true, angleRad: 0);
                return true;
            }

            double angle = 0;
            if (lin != null && double.TryParse(Attr(lin, "ang"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double ang60000))
                angle = ang60000 / 60000.0 * Math.PI / 180.0;

            result = new Resolved(stops, radial: false, angleRad: angle);
            return true;
        }

        /// <summary>
        /// Registers the shading for a gradient covering the shape's box <c>(0,0)–(w,h)</c> in the
        /// drawing's top-down frame, and returns its resource name.
        ///
        /// <para>
        /// <c>a:lin@ang</c> is measured clockwise from the +x axis. Inside the y-down drawing frame
        /// that is already the positive direction, so the angle is used as-is — the same reasoning
        /// as the shape rotation in <see cref="DrawingTransform.ShapeMatrix"/>.
        /// </para>
        /// </summary>
        internal static string Register(Resolved gradient, double w, double h, DocxRenderContext context)
        {
            if (gradient.IsRadial)
            {
                // A circle covering the box: the corner distance, so the last stop reaches the
                // corners rather than stopping at the edge midpoints.
                double radius = Math.Sqrt(w * w + h * h) / 2.0;
                return context.RegisterRadialShading(w / 2.0, h / 2.0, radius, gradient.Stops);
            }

            // Project the box onto the gradient axis through its centre, so the axis spans exactly
            // the box's extent in that direction whatever the angle.
            double cx = w / 2.0, cy = h / 2.0;
            double dx = Math.Cos(gradient.AngleRadians), dy = Math.Sin(gradient.AngleRadians);
            double half = (Math.Abs(w * dx) + Math.Abs(h * dy)) / 2.0;

            return context.RegisterAxialShading(
                cx - dx * half, cy - dy * half,
                cx + dx * half, cy + dy * half,
                gradient.Stops);
        }

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);
    }
}
