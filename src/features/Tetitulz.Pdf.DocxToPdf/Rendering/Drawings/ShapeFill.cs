// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>An RGB colour with components in 0..1, ready for a <c>rg</c> / <c>RG</c> operand.</summary>
    internal readonly struct Rgb
    {
        internal Rgb(double r, double g, double b) { R = r; G = g; B = b; }
        internal double R { get; }
        internal double G { get; }
        internal double B { get; }
    }

    /// <summary>
    /// How a shape is painted: fill, outline colour, outline width, and the two alphas.
    /// A <c>null</c> colour means "not painted".
    /// </summary>
    internal readonly struct ShapeFill
    {
        private ShapeFill(Rgb? fill, double fillAlpha, Rgb? stroke, double strokeAlpha,
            double strokeWidthPt, double[] dash)
        {
            FillRgb = fill; FillAlpha = fillAlpha;
            StrokeRgb = stroke; StrokeAlpha = strokeAlpha;
            StrokeWidthPt = strokeWidthPt; Dash = dash;
        }

        internal Rgb? FillRgb { get; }
        internal double FillAlpha { get; }
        internal Rgb? StrokeRgb { get; }
        internal double StrokeAlpha { get; }
        internal double StrokeWidthPt { get; }

        /// <summary>
        /// The dash array in points, or <c>null</c> for a solid line. ECMA-376 states these as
        /// multiples of the line width (§20.1.10.49), so they are scaled here — a 3pt dashed line
        /// has longer dashes than a 1pt one, which is what makes the pattern look the same weight
        /// at any thickness.
        /// </summary>
        internal double[] Dash { get; }

        /// <summary>True when either paint is translucent and so needs an ExtGState.</summary>
        internal bool NeedsAlpha => FillAlpha < 0.999 || StrokeAlpha < 0.999;

        /// <summary>
        /// Resolves a shape's paint from its <c>…spPr</c> and, failing that, from its
        /// <c>wps:style</c> / <c>dsp:style</c> theme reference.
        ///
        /// <para>
        /// <b>Order matters and is the spec's:</b> what the shape states directly wins; the style
        /// reference is the fallback. Word writes only the reference for a default autoshape — no
        /// <c>spPr/a:solidFill</c> at all — which is why reading <c>spPr</c> alone made every such
        /// shape disappear.
        /// </para>
        /// </summary>
        internal static ShapeFill Resolve(OpenXmlElement spPr, OpenXmlElement shape,
            DrawingColorResolver colors)
        {
            var style = shape?.ChildElements.FirstOrDefault(e => e.LocalName == "style");

            // ---- fill
            Rgb? fill = null;
            double fillAlpha = 1.0;
            bool noFill = Child(spPr, "noFill") != null;
            if (!noFill)
            {
                var solid = Child(spPr, "solidFill");
                if (solid != null && colors.TryResolve(solid, null, out var direct))
                {
                    fill = direct.ToRgb();
                    fillAlpha = direct.A;
                }
                else if (solid == null && UnsupportedFillKind(spPr) is string kind)
                {
                    DocxDiagnostics.Report("ShapeFill.Resolve",
                        "a " + kind + " on a shape is not painted yet; the shape was left unfilled");
                }
                else if (solid == null && style != null && colors.TryResolveFillRef(style, out var themed))
                {
                    fill = themed.ToRgb();
                    fillAlpha = themed.A;
                }
            }

            // ---- outline
            var ln = Child(spPr, "ln");
            Rgb? stroke = null;
            double strokeAlpha = 1.0, widthPt = 0;

            bool lnSuppressed = ln != null && Child(ln, "noFill") != null;
            if (!lnSuppressed)
            {
                if (ln != null)
                {
                    var lnSolid = Child(ln, "solidFill");
                    if (lnSolid != null && colors.TryResolve(lnSolid, null, out var lnColor))
                    {
                        stroke = lnColor.ToRgb();
                        strokeAlpha = lnColor.A;
                    }
                    widthPt = WidthOf(ln);
                }

                if (stroke == null && style != null
                    && colors.TryResolveLineRef(style, out var themedLine, out double themedWidth))
                {
                    stroke = themedLine.ToRgb();
                    strokeAlpha = themedLine.A;
                    if (widthPt <= 0) widthPt = themedWidth;
                }

                // An <a:ln> with a width but no colour still strokes — in black, as before.
                if (stroke == null && ln != null) stroke = new Rgb(0, 0, 0);
                if (stroke != null && widthPt <= 0) widthPt = DefaultStrokeWidthPt;
            }

            double[] dash = stroke == null ? null : DashOf(ln, widthPt);
            return new ShapeFill(fill, fillAlpha, stroke, strokeAlpha, widthPt, dash);
        }

        // ECMA-376 §20.1.10.49 preset dash patterns, in multiples of the line width.
        private static readonly Dictionary<string, double[]> PresetDashes =
            new Dictionary<string, double[]>(StringComparer.Ordinal)
            {
                ["dot"] = new double[] { 1, 3 },
                ["dash"] = new double[] { 4, 3 },
                ["lgDash"] = new double[] { 8, 3 },
                ["dashDot"] = new double[] { 4, 3, 1, 3 },
                ["lgDashDot"] = new double[] { 8, 3, 1, 3 },
                ["lgDashDotDot"] = new double[] { 8, 3, 1, 3, 1, 3 },
                ["sysDash"] = new double[] { 3, 1 },
                ["sysDot"] = new double[] { 1, 1 },
                ["sysDashDot"] = new double[] { 3, 1, 1, 1 },
                ["sysDashDotDot"] = new double[] { 3, 1, 1, 1, 1, 1 },
            };

        private static double[] DashOf(OpenXmlElement ln, double widthPt)
        {
            if (ln == null || widthPt <= 0) return null;

            // A custom a:custDash states its own dash/space pairs; honoured when present.
            var custDash = Child(ln, "custDash");
            if (custDash != null)
            {
                var lengths = new List<double>();
                foreach (var ds in custDash.ChildElements.Where(e => e.LocalName == "ds"))
                {
                    lengths.Add(Percent(Attr(ds, "d")) * widthPt);
                    lengths.Add(Percent(Attr(ds, "sp")) * widthPt);
                }
                return lengths.Count > 0 && lengths.Any(v => v > 0) ? lengths.ToArray() : null;
            }

            string preset = Attr(Child(ln, "prstDash"), "val");
            if (string.IsNullOrEmpty(preset) || preset == "solid") return null;

            if (!PresetDashes.TryGetValue(preset, out double[] pattern))
            {
                DocxDiagnostics.Report("ShapeFill.Resolve",
                    "dash pattern '" + preset + "' is not in the preset table; the line was drawn solid");
                return null;
            }

            return pattern.Select(v => v * widthPt).ToArray();
        }

        private static double Percent(string v)
            => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double raw)
                ? raw / 100000.0
                : 0;

        /// <summary>Word's own default outline weight when <c>a:ln</c> states none.</summary>
        private const double DefaultStrokeWidthPt = 0.75;

        private static double WidthOf(OpenXmlElement ln)
            => long.TryParse(Attr(ln, "w"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long emu) && emu > 0
                ? emu / PdfUnits.EmuPerPoint
                : 0;

        // Named so the diagnostic can say which kind was dropped — "not painted" and "painted the
        // wrong way" need to be distinguishable by the caller.
        private static string UnsupportedFillKind(OpenXmlElement spPr)
        {
            // a:gradFill is deliberately absent: it is painted as a real PDF shading by
            // ShapeTreeRenderer, so reporting it here would claim a loss that did not happen.
            if (Child(spPr, "blipFill") != null) return "picture fill";
            if (Child(spPr, "pattFill") != null) return "pattern fill";
            if (Child(spPr, "grpFill") != null) return "group fill";
            return null;
        }

        private static OpenXmlElement Child(OpenXmlElement e, string localName)
            => e?.ChildElements.FirstOrDefault(c => c.LocalName == localName);

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);
    }
}
