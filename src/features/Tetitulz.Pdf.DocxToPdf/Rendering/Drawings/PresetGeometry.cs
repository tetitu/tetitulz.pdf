// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using DocumentFormat.OpenXml;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Drawings
{
    /// <summary>
    /// DrawingML preset shape geometry — <c>a:prstGeom/@prst</c> → an outline.
    ///
    /// <para>
    /// <b>A curated table, deliberately, rather than a guide-formula evaluator.</b> ECMA-376
    /// §20.1.9.18 defines ~187 presets as little programs (<c>avLst</c>/<c>gdLst</c>/<c>pathLst</c>
    /// with the operators <c>*/ +- pin sin cos at2 …</c>). Building the evaluator is the *cheap*
    /// half of that; the expensive half is that **the 187 definitions are not in the OpenXml SDK**,
    /// so the general route is an evaluator *plus* a ~5,000-line transcription — and a transcription
    /// bug produces a silently wrong outline, which is the defect class this repository exists to
    /// avoid. A hand-written preset is checkable against an exact operator sequence.
    /// </para>
    ///
    /// <para>
    /// The set below is what business documents and the SmartArt gallery actually use. An
    /// unrecognised preset falls back to its bounding rectangle <b>and is reported</b>, which is
    /// strictly more informative than the silent nothing that came before.
    /// </para>
    ///
    /// <para>
    /// Builders draw in the shape's own <b>top-down</b> frame spanning (0,0)–(w,h) in points — the
    /// frame ECMA-376 authors <c>pathLst</c> in. See <see cref="DrawingTransform"/> for the single
    /// y-flip that establishes it.
    /// </para>
    /// </summary>
    internal static class PresetGeometry
    {
        /// <summary>Writes one preset's outline into <paramref name="p"/>.</summary>
        internal delegate void Builder(GeometryPath p, double w, double h, AdjustValues adj);

        private static readonly Dictionary<string, Builder> Presets =
            new Dictionary<string, Builder>(StringComparer.Ordinal)
            {
                ["rect"] = Rect,
                ["flowChartProcess"] = Rect,
                ["roundRect"] = RoundRect,
                ["flowChartAlternateProcess"] = RoundRect,
                ["flowChartTerminator"] = Stadium,
                ["ellipse"] = Ellipse,
                ["circle"] = Ellipse,
                ["flowChartConnector"] = Ellipse,
                ["triangle"] = Triangle,
                ["rtTriangle"] = RightTriangle,
                ["diamond"] = Diamond,
                ["flowChartDecision"] = Diamond,
                ["pentagon"] = RegularPentagon,
                ["hexagon"] = Hexagon,
                ["octagon"] = Octagon,
                ["trapezoid"] = Trapezoid,
                ["parallelogram"] = Parallelogram,
                ["flowChartInputOutput"] = Parallelogram,
                ["chevron"] = Chevron,
                ["homePlate"] = HomePlate,
                ["rightArrow"] = RightArrow,
                ["leftArrow"] = LeftArrow,
                ["upArrow"] = UpArrow,
                ["downArrow"] = DownArrow,
                ["leftRightArrow"] = LeftRightArrow,
                ["plus"] = Plus,
                ["star5"] = Star5,
                ["corner"] = Corner,
                ["cube"] = Cube,
                ["can"] = Can,
                ["flowChartPredefinedProcess"] = PredefinedProcess,
                ["flowChartDocument"] = FlowChartDocument,
                ["line"] = OpenLine,
                ["straightConnector1"] = OpenLine,
                ["bentConnector2"] = BentConnector,
                ["bentConnector3"] = BentConnector,
                ["bentConnector4"] = BentConnector,
                ["bentConnector5"] = BentConnector,
            };

        /// <summary>
        /// Writes a literal <c>a:custGeom</c> outline — the shape's own <c>a:pathLst</c> rather
        /// than a named preset.
        ///
        /// <para>
        /// Only the literal commands are honoured (<c>moveTo</c>, <c>lnTo</c>, <c>cubicBezTo</c>,
        /// <c>close</c>), scaled from each <c>a:path</c>'s own <c>@w</c>/<c>@h</c> onto the shape
        /// box. <c>arcTo</c> and <c>quadBezTo</c> would need the same elliptical-arc machinery the
        /// preset table deliberately avoids, so a subpath containing one is skipped and reported —
        /// drawing part of a contour would close it across the gap and produce a shape the file
        /// never described.
        /// </para>
        /// </summary>
        internal static bool TryBuildCustom(OpenXmlElement custGeom, GeometryPath p, double w, double h)
        {
            var pathLst = custGeom?.ChildElements.FirstOrDefault(e => e.LocalName == "pathLst");
            if (pathLst == null) return false;

            bool drewAnything = false;
            foreach (var path in pathLst.ChildElements.Where(e => e.LocalName == "path"))
            {
                double pw = Coord(Attr(path, "w"), 0);
                double ph = Coord(Attr(path, "h"), 0);
                double sx = pw > 0 ? w / pw : 1.0;
                double sy = ph > 0 ? h / ph : 1.0;

                if (path.ChildElements.Any(e => e.LocalName == "arcTo" || e.LocalName == "quadBezTo"))
                {
                    DocxDiagnostics.Report("PresetGeometry.TryBuildCustom",
                        "a custom-geometry subpath uses arcTo or quadBezTo, which are not supported; "
                            + "the subpath was skipped rather than closed across the gap");
                    continue;
                }

                foreach (var cmd in path.ChildElements)
                {
                    switch (cmd.LocalName)
                    {
                        case "moveTo":
                            if (TryPoint(cmd, "pt", sx, sy, out double mx, out double my))
                            { p.MoveTo(mx, my); drewAnything = true; }
                            break;
                        case "lnTo":
                            if (TryPoint(cmd, "pt", sx, sy, out double lx, out double ly))
                            { p.LineTo(lx, ly); drewAnything = true; }
                            break;
                        case "cubicBezTo":
                        {
                            var pts = cmd.ChildElements.Where(e => e.LocalName == "pt").ToList();
                            if (pts.Count < 3) break;
                            if (TryRead(pts[0], sx, sy, out double x1, out double y1)
                                && TryRead(pts[1], sx, sy, out double x2, out double y2)
                                && TryRead(pts[2], sx, sy, out double x3, out double y3))
                            { p.CurveTo(x1, y1, x2, y2, x3, y3); drewAnything = true; }
                            break;
                        }
                        case "close":
                            p.Close();
                            break;
                    }
                }
            }

            return drewAnything;
        }

        private static bool TryPoint(OpenXmlElement cmd, string childName, double sx, double sy,
            out double x, out double y)
        {
            x = y = 0;
            var pt = cmd.ChildElements.FirstOrDefault(e => e.LocalName == childName);
            return pt != null && TryRead(pt, sx, sy, out x, out y);
        }

        private static bool TryRead(OpenXmlElement pt, double sx, double sy, out double x, out double y)
        {
            x = Coord(Attr(pt, "x"), double.NaN) * sx;
            y = Coord(Attr(pt, "y"), double.NaN) * sy;
            return !double.IsNaN(x) && !double.IsNaN(y);
        }

        private static double Coord(string v, double fallback)
            => double.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d)
                ? d
                : fallback;

        private static string Attr(OpenXmlElement e, string localName)
            => OoxmlAttribute.Value(e, localName);

        /// <summary>
        /// Writes <paramref name="prst"/>'s outline, or the bounding rectangle when the preset is
        /// not in the table. Returns <c>false</c> in the fallback case so the caller can report it.
        /// </summary>
        internal static bool TryBuild(string prst, GeometryPath p, double w, double h, AdjustValues adj)
        {
            if (!string.IsNullOrEmpty(prst) && Presets.TryGetValue(prst, out var builder))
            {
                builder(p, w, h, adj);
                return true;
            }

            Rect(p, w, h, adj);
            return false;
        }

        /// <summary>
        /// The presets that are a line rather than an area: stroked and never filled, however the
        /// shape's fill is set.
        ///
        /// <para>
        /// An explicit set, the same shape as <see cref="Presets"/> — <b>not</b> a substring test.
        /// "any name containing <c>line</c> or <c>Connector</c>" also matched
        /// <c>flowChartConnector</c>, which this very file registers as an <c>Ellipse</c>, so a
        /// flowchart circle filled with accent1 could never be filled; and
        /// <c>flowChartOnlineStorage</c> matched on the "line" inside "Online". A name cannot be
        /// classified by accident here.
        /// </para>
        /// </summary>
        private static readonly HashSet<string> StrokeOnlyPresets =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "line", "lineInv", "arc",
                "straightConnector1",
                "bentConnector2", "bentConnector3", "bentConnector4", "bentConnector5",
                "curvedConnector2", "curvedConnector3", "curvedConnector4", "curvedConnector5",
            };

        /// <summary>
        /// True for presets that are a line rather than an area: they are stroked and never filled,
        /// however the shape's fill is set.
        /// </summary>
        internal static bool IsStrokeOnly(string prst)
            => !string.IsNullOrEmpty(prst) && StrokeOnlyPresets.Contains(prst);

        // ------------------------------------------------------------------ builders

        private static void Rect(GeometryPath p, double w, double h, AdjustValues adj)
            => p.Rectangle(0, 0, w, h);

        private static void Ellipse(GeometryPath p, double w, double h, AdjustValues adj)
            => p.Ellipse(0, 0, w, h);

        private static void RoundRect(GeometryPath p, double w, double h, AdjustValues adj)
            => RoundedRect(p, w, h, adj.Fraction("adj", 16667, 0, 0.5) * Math.Min(w, h));

        // A terminator / "stadium": fully rounded ends, i.e. the corner radius is half the
        // shorter side. Not an adjust handle — the preset has none.
        private static void Stadium(GeometryPath p, double w, double h, AdjustValues adj)
            => RoundedRect(p, w, h, Math.Min(w, h) / 2.0);

        private static void RoundedRect(GeometryPath p, double w, double h, double r)
        {
            r = Math.Max(0, Math.Min(r, Math.Min(w, h) / 2.0));
            if (r <= 0) { p.Rectangle(0, 0, w, h); return; }

            p.MoveTo(r, 0);
            p.LineTo(w - r, 0);
            p.CornerTo(w - r, 0, w, 0, w, r);
            p.LineTo(w, h - r);
            p.CornerTo(w, h - r, w, h, w - r, h);
            p.LineTo(r, h);
            p.CornerTo(r, h, 0, h, 0, h - r);
            p.LineTo(0, r);
            p.CornerTo(0, r, 0, 0, r, 0);
            p.Close();
        }

        private static void Triangle(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double apex = adj.Fraction("adj", 50000) * w;
            p.MoveTo(apex, 0);
            p.LineTo(w, h);
            p.LineTo(0, h);
            p.Close();
        }

        private static void RightTriangle(GeometryPath p, double w, double h, AdjustValues adj)
        {
            p.MoveTo(0, 0);
            p.LineTo(w, h);
            p.LineTo(0, h);
            p.Close();
        }

        private static void Diamond(GeometryPath p, double w, double h, AdjustValues adj)
        {
            p.MoveTo(w / 2, 0);
            p.LineTo(w, h / 2);
            p.LineTo(w / 2, h);
            p.LineTo(0, h / 2);
            p.Close();
        }

        // A regular pentagon inscribed in the box, point up. The preset's own guides describe the
        // regular figure, so there is no adjust handle to read.
        private static void RegularPentagon(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double cx = w / 2, cy = h / 2, rx = w / 2, ry = h / 2;
            for (int i = 0; i < 5; i++)
            {
                // −90° puts the first vertex at the top; y is inverted because the frame is top-down.
                double a = (-90.0 + i * 72.0) * Math.PI / 180.0;
                double x = cx + rx * Math.Cos(a);
                double y = cy + ry * Math.Sin(a);
                if (i == 0) p.MoveTo(x, y); else p.LineTo(x, y);
            }
            p.Close();
        }

        private static void Hexagon(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double a = Math.Min(adj.Fraction("adj", 25000) * Math.Min(w, h), w / 2);
            p.MoveTo(a, 0);
            p.LineTo(w - a, 0);
            p.LineTo(w, h / 2);
            p.LineTo(w - a, h);
            p.LineTo(a, h);
            p.LineTo(0, h / 2);
            p.Close();
        }

        private static void Octagon(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double a = Math.Min(adj.Fraction("adj", 29289) * Math.Min(w, h), Math.Min(w, h) / 2);
            p.MoveTo(a, 0);
            p.LineTo(w - a, 0);
            p.LineTo(w, a);
            p.LineTo(w, h - a);
            p.LineTo(w - a, h);
            p.LineTo(a, h);
            p.LineTo(0, h - a);
            p.LineTo(0, a);
            p.Close();
        }

        private static void Trapezoid(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double a = Math.Min(adj.Fraction("adj", 25000) * w, w / 2);
            p.MoveTo(a, 0);
            p.LineTo(w - a, 0);
            p.LineTo(w, h);
            p.LineTo(0, h);
            p.Close();
        }

        private static void Parallelogram(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double a = Math.Min(adj.Fraction("adj", 25000) * w, w);
            p.MoveTo(a, 0);
            p.LineTo(w, 0);
            p.LineTo(w - a, h);
            p.LineTo(0, h);
            p.Close();
        }

        private static void Chevron(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double a = Math.Min(adj.Fraction("adj", 50000) * Math.Min(w, h), w);
            p.MoveTo(0, 0);
            p.LineTo(w - a, 0);
            p.LineTo(w, h / 2);
            p.LineTo(w - a, h);
            p.LineTo(0, h);
            p.LineTo(a, h / 2);
            p.Close();
        }

        private static void HomePlate(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double a = Math.Min(adj.Fraction("adj", 50000) * Math.Min(w, h), w);
            p.MoveTo(0, 0);
            p.LineTo(w - a, 0);
            p.LineTo(w, h / 2);
            p.LineTo(w - a, h);
            p.LineTo(0, h);
            p.Close();
        }

        private static void RightArrow(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double half = adj.Fraction("adj1", 50000) * h / 2.0;
            double head = Math.Min(adj.Fraction("adj2", 50000) * Math.Min(w, h), w);
            p.MoveTo(0, h / 2 - half);
            p.LineTo(w - head, h / 2 - half);
            p.LineTo(w - head, 0);
            p.LineTo(w, h / 2);
            p.LineTo(w - head, h);
            p.LineTo(w - head, h / 2 + half);
            p.LineTo(0, h / 2 + half);
            p.Close();
        }

        private static void LeftArrow(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double half = adj.Fraction("adj1", 50000) * h / 2.0;
            double head = Math.Min(adj.Fraction("adj2", 50000) * Math.Min(w, h), w);
            p.MoveTo(w, h / 2 - half);
            p.LineTo(head, h / 2 - half);
            p.LineTo(head, 0);
            p.LineTo(0, h / 2);
            p.LineTo(head, h);
            p.LineTo(head, h / 2 + half);
            p.LineTo(w, h / 2 + half);
            p.Close();
        }

        private static void UpArrow(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double half = adj.Fraction("adj1", 50000) * w / 2.0;
            double head = Math.Min(adj.Fraction("adj2", 50000) * Math.Min(w, h), h);
            p.MoveTo(w / 2 - half, h);
            p.LineTo(w / 2 - half, head);
            p.LineTo(0, head);
            p.LineTo(w / 2, 0);
            p.LineTo(w, head);
            p.LineTo(w / 2 + half, head);
            p.LineTo(w / 2 + half, h);
            p.Close();
        }

        private static void DownArrow(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double half = adj.Fraction("adj1", 50000) * w / 2.0;
            double head = Math.Min(adj.Fraction("adj2", 50000) * Math.Min(w, h), h);
            p.MoveTo(w / 2 - half, 0);
            p.LineTo(w / 2 - half, h - head);
            p.LineTo(0, h - head);
            p.LineTo(w / 2, h);
            p.LineTo(w, h - head);
            p.LineTo(w / 2 + half, h - head);
            p.LineTo(w / 2 + half, 0);
            p.Close();
        }

        private static void LeftRightArrow(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double half = adj.Fraction("adj1", 50000) * h / 2.0;
            double head = Math.Min(adj.Fraction("adj2", 50000) * Math.Min(w, h), w / 2);
            p.MoveTo(0, h / 2);
            p.LineTo(head, 0);
            p.LineTo(head, h / 2 - half);
            p.LineTo(w - head, h / 2 - half);
            p.LineTo(w - head, 0);
            p.LineTo(w, h / 2);
            p.LineTo(w - head, h);
            p.LineTo(w - head, h / 2 + half);
            p.LineTo(head, h / 2 + half);
            p.LineTo(head, h);
            p.Close();
        }

        private static void Plus(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double a = adj.Fraction("adj", 25000, 0, 0.5) * Math.Min(w, h);
            p.MoveTo(a, 0);
            p.LineTo(w - a, 0);
            p.LineTo(w - a, a);
            p.LineTo(w, a);
            p.LineTo(w, h - a);
            p.LineTo(w - a, h - a);
            p.LineTo(w - a, h);
            p.LineTo(a, h);
            p.LineTo(a, h - a);
            p.LineTo(0, h - a);
            p.LineTo(0, a);
            p.LineTo(a, a);
            p.Close();
        }

        private static void Star5(GeometryPath p, double w, double h, AdjustValues adj)
        {
            // The classic 5-point star: alternating vertices on two concentric ellipses. The inner
            // radius ratio is 1/φ² — the value that makes the arms straight-sided.
            const double InnerRatio = 0.381966;
            double cx = w / 2, cy = h / 2, rx = w / 2, ry = h / 2;
            for (int i = 0; i < 10; i++)
            {
                double a = (-90.0 + i * 36.0) * Math.PI / 180.0;
                double f = (i % 2 == 0) ? 1.0 : InnerRatio;
                double x = cx + rx * f * Math.Cos(a);
                double y = cy + ry * f * Math.Sin(a);
                if (i == 0) p.MoveTo(x, y); else p.LineTo(x, y);
            }
            p.Close();
        }

        private static void Corner(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double a = adj.Fraction("adj1", 50000, 0, 1) * Math.Min(w, h);
            p.MoveTo(0, 0);
            p.LineTo(a, 0);
            p.LineTo(a, h - a);
            p.LineTo(w, h - a);
            p.LineTo(w, h);
            p.LineTo(0, h);
            p.Close();
        }

        // Three faces as three subpaths. They are painted with one fill, so the result is a flat
        // silhouette rather than a shaded solid — the shading needs gradients, which are reported
        // as unsupported rather than faked.
        private static void Cube(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double d = Math.Min(adj.Fraction("adj", 25000) * Math.Min(w, h), Math.Min(w, h) / 2);

            p.MoveTo(0, d);            // front face
            p.LineTo(w - d, d);
            p.LineTo(w - d, h);
            p.LineTo(0, h);
            p.Close();

            p.MoveTo(0, d);            // top face
            p.LineTo(d, 0);
            p.LineTo(w, 0);
            p.LineTo(w - d, d);
            p.Close();

            p.MoveTo(w - d, d);        // right face
            p.LineTo(w, 0);
            p.LineTo(w, h - d);
            p.LineTo(w - d, h);
            p.Close();
        }

        // A cylinder: the body silhouette plus the visible top ellipse.
        private static void Can(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double ry = Math.Min(adj.Fraction("adj", 25000) * h / 2.0, h / 2.0);
            double rx = w / 2.0, cx = w / 2.0;
            double k = GeometryPath.QuarterArcK;

            p.MoveTo(0, ry);                                                   // left wall down
            p.LineTo(0, h - ry);
            p.CurveTo(0, h - ry + ry * k, cx - rx * k, h, cx, h);              // bottom front arc
            p.CurveTo(cx + rx * k, h, w, h - ry + ry * k, w, h - ry);
            p.LineTo(w, ry);                                                   // right wall up
            p.CurveTo(w, ry - ry * k, cx + rx * k, 0, cx, 0);                  // top back arc
            p.CurveTo(cx - rx * k, 0, 0, ry - ry * k, 0, ry);
            p.Close();

            p.Ellipse(0, 0, w, 2 * ry);                                        // the visible lid
        }

        private static void PredefinedProcess(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double inset = w / 8.0;
            p.Rectangle(0, 0, w, h);
            p.MoveTo(inset, 0);
            p.LineTo(inset, h);
            p.MoveTo(w - inset, 0);
            p.LineTo(w - inset, h);
        }

        // A document: flat top and sides, wavy bottom edge.
        private static void FlowChartDocument(GeometryPath p, double w, double h, AdjustValues adj)
        {
            double waveTop = h * 0.84, waveBottom = h;
            p.MoveTo(0, 0);
            p.LineTo(w, 0);
            p.LineTo(w, waveTop);
            p.CurveTo(w * 0.75, waveBottom, w * 0.25, waveTop - (waveBottom - waveTop),
                      0, waveTop);
            p.Close();
        }

        private static void OpenLine(GeometryPath p, double w, double h, AdjustValues adj)
        {
            p.MoveTo(0, 0);
            p.LineTo(w, h);
        }

        private static void BentConnector(GeometryPath p, double w, double h, AdjustValues adj)
        {
            p.MoveTo(0, 0);
            p.LineTo(w / 2, 0);
            p.LineTo(w / 2, h);
            p.LineTo(w, h);
        }
    }
}
