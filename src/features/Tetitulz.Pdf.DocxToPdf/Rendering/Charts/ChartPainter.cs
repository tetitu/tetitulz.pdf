// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Charts
{
    /// <summary>
    /// Turns a laid-out <see cref="ChartFrame"/> into PDF operators.
    ///
    /// <para>
    /// The frame's coordinates are chart-local with y upward, so the whole chart needs one
    /// <b>translate-only</b> <c>cm</c>. Never a scaling one: a scale would distort stroke widths and
    /// font sizes, and the layout has already worked in points.
    /// </para>
    /// </summary>
    internal static class ChartPainter
    {
        internal static string Paint(ChartFrame frame, double leftX, double bottomY,
            DocxRenderContext context)
        {
            var ops = new StringBuilder();

            ops.Append("q\n");
            ops.AppendFormat(CultureInfo.InvariantCulture, "1 0 0 1 {0:F2} {1:F2} cm\n", leftX, bottomY);

            // Clip to the chart's own box. Cheap insurance: a mis-scaled series can then never
            // paint over the rest of the page.
            ops.AppendFormat(CultureInfo.InvariantCulture, "0 0 {0:F2} {1:F2} re W n\n",
                frame.Width, frame.Height);

            foreach (var item in frame.Items)
            {
                switch (item)
                {
                    case RectItem r: PaintRect(ops, r); break;
                    case LineItem l: PaintLine(ops, l); break;
                    case PolylineItem p: PaintPolyline(ops, p); break;
                    case SliceItem s: PaintSlice(ops, s); break;
                    case MarkerItem k: PaintMarker(ops, k); break;
                    case TextItem t: PaintText(ops, t, context); break;
                }
            }

            ops.Append("Q\n");
            return ops.ToString();
        }

        private static void PaintRect(StringBuilder ops, RectItem r)
        {
            if (!Finite(r.Box.X, r.Box.Y, r.Box.Width, r.Box.Height)) return;
            AppendFill(ops, r.FillHex);
            ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2:F2} {3:F2} re\nf\n",
                r.Box.X, r.Box.Y, r.Box.Width, r.Box.Height);
        }

        private static void PaintLine(StringBuilder ops, LineItem l)
        {
            if (!Finite(l.X0, l.Y0, l.X1, l.Y1)) return;
            AppendStroke(ops, l.StrokeHex, l.WidthPt);
            ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} m\n{2:F2} {3:F2} l\nS\n",
                l.X0, l.Y0, l.X1, l.Y1);
        }

        private static void PaintMarker(StringBuilder ops, MarkerItem k)
        {
            if (!Finite(k.X, k.Y, k.Size) || k.Size <= 0) return;
            AppendFill(ops, k.FillHex);

            double half = k.Size / 2;
            if (!k.Round)
            {
                ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2:F2} {3:F2} re\nf\n",
                    k.X - half, k.Y - half, k.Size, k.Size);
                return;
            }

            // Four quarter arcs; the same bezier constant the slice code uses.
            double c = half * 0.5522847498;
            ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} m\n", k.X + half, k.Y);
            ops.AppendFormat(CultureInfo.InvariantCulture,
                "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\n",
                k.X + half, k.Y + c, k.X + c, k.Y + half, k.X, k.Y + half);
            ops.AppendFormat(CultureInfo.InvariantCulture,
                "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\n",
                k.X - c, k.Y + half, k.X - half, k.Y + c, k.X - half, k.Y);
            ops.AppendFormat(CultureInfo.InvariantCulture,
                "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\n",
                k.X - half, k.Y - c, k.X - c, k.Y - half, k.X, k.Y - half);
            ops.AppendFormat(CultureInfo.InvariantCulture,
                "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\n",
                k.X + c, k.Y - half, k.X + half, k.Y - c, k.X + half, k.Y);
            ops.Append("h\nf\n");
        }

        private static void PaintPolyline(StringBuilder ops, PolylineItem p)
        {
            if (p.Points.Count == 1)
            {
                // A run of one point between two gaps is not a line. Excel would draw a marker
                // there; without markers the value would simply disappear, so say so.
                DocxDiagnostics.Report("ChartPainter.PaintPolyline",
                    "a line series has a single point isolated between blanks; it was not drawn, "
                        + "because a one-point run has no line to stroke");
                return;
            }
            if (p.Points.Count < 2) return;

            bool filled = p.FillHex != null;
            if (filled) AppendFill(ops, p.FillHex);
            else AppendStroke(ops, p.StrokeHex, p.WidthPt);

            bool started = false;
            foreach (var (x, y) in p.Points)
            {
                if (!Finite(x, y)) continue;
                ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} {2}\n",
                    x, y, started ? "l" : "m");
                started = true;
            }
            if (started) ops.Append(filled ? "h\nf\n" : "S\n");
        }

        /// <summary>
        /// A pie or doughnut slice, as cubic arcs.
        ///
        /// <para>
        /// The quarter-arc constant generalises: a sub-arc of Δ radians needs control points at
        /// <c>4/3·tan(Δ/4)</c> of the radius along the tangents, which is exactly the familiar
        /// 0.5523 when Δ is 90°. Splitting at 90° keeps the approximation inside 0.02%.
        /// </para>
        /// </summary>
        private static void PaintSlice(StringBuilder ops, SliceItem s)
        {
            if (!Finite(s.CentreX, s.CentreY, s.Radius, s.SweepAngle) || s.Radius <= 0) return;
            if (Math.Abs(s.SweepAngle) < 1e-6) return;

            AppendFill(ops, s.FillHex);

            double start = s.StartAngle * Math.PI / 180.0;
            double sweep = s.SweepAngle * Math.PI / 180.0;
            double end = start + sweep;

            if (s.InnerRadius > 0)
            {
                // Outer arc forward, straight in, inner arc back: one closed subpath, correct
                // under nonzero winding.
                MoveTo(ops, s.CentreX, s.CentreY, s.Radius, start);
                Arc(ops, s.CentreX, s.CentreY, s.Radius, start, end);
                LineTo(ops, s.CentreX, s.CentreY, s.InnerRadius, end);
                Arc(ops, s.CentreX, s.CentreY, s.InnerRadius, end, start);
            }
            else
            {
                ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} m\n", s.CentreX, s.CentreY);
                LineTo(ops, s.CentreX, s.CentreY, s.Radius, start);
                Arc(ops, s.CentreX, s.CentreY, s.Radius, start, end);
            }

            ops.Append("h\nf\n");
        }

        private static void Arc(StringBuilder ops, double cx, double cy, double r,
            double from, double to)
        {
            double remaining = to - from;
            int segments = Math.Max(1, SaturatingCast.ToInt(Math.Ceiling(Math.Abs(remaining) / (Math.PI / 2))));
            double step = remaining / segments;
            double k = 4.0 / 3.0 * Math.Tan(step / 4);

            double a = from;
            for (int i = 0; i < segments; i++)
            {
                double b = a + step;
                double x1 = cx + r * Math.Cos(a), y1 = cy + r * Math.Sin(a);
                double x2 = cx + r * Math.Cos(b), y2 = cy + r * Math.Sin(b);

                ops.AppendFormat(CultureInfo.InvariantCulture,
                    "{0:F2} {1:F2} {2:F2} {3:F2} {4:F2} {5:F2} c\n",
                    x1 - k * r * Math.Sin(a), y1 + k * r * Math.Cos(a),
                    x2 + k * r * Math.Sin(b), y2 - k * r * Math.Cos(b),
                    x2, y2);
                a = b;
            }
        }

        private static void MoveTo(StringBuilder ops, double cx, double cy, double r, double angle)
            => ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} m\n",
                cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));

        private static void LineTo(StringBuilder ops, double cx, double cy, double r, double angle)
            => ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} l\n",
                cx + r * Math.Cos(angle), cy + r * Math.Sin(angle));

        private static void PaintText(StringBuilder ops, TextItem t, DocxRenderContext context)
        {
            if (string.IsNullOrEmpty(t.Text) || !Finite(t.X, t.Y)) return;

            // Non-WinAnsi label text would need the embedded-glyph path; drawing its bytes through a
            // standard-14 font is mojibake, which is worse than an honest gap.
            if (UnicodeText.NeedsUnicode(t.Text))
            {
                DocxDiagnostics.Report("ChartPainter.PaintText",
                    "a chart label outside WinAnsi (Hebrew / Arabic / CJK) was omitted rather than "
                        + "drawn as mojibake");
                return;
            }

            string font = context.RegisterFont(t.Bold ? "Helvetica-Bold" : "Helvetica");
            double width = context.StyleResolver.MeasureTextWidth(t.Text,
                t.Bold ? "Helvetica-Bold" : "Helvetica", t.SizePt);

            double x = t.Anchor switch
            {
                TextAnchor.Centre => t.X - width / 2,
                TextAnchor.Right => t.X - width,
                _ => t.X,
            };

            ops.Append("BT\n");
            AppendFill(ops, t.ColorHex);
            ops.AppendFormat(CultureInfo.InvariantCulture, "/{0} {1:F2} Tf\n", font, t.SizePt);

            if (Math.Abs(t.RotationDeg) < 1e-6)
            {
                ops.AppendFormat(CultureInfo.InvariantCulture, "{0:F2} {1:F2} Td\n", x, t.Y);
            }
            else
            {
                // Rotated text needs a text matrix; Td cannot express it. The anchor becomes the
                // START of the rotated run, so a centred label is offset along its own direction.
                double rad = t.RotationDeg * Math.PI / 180.0;
                double cos = Math.Cos(rad), sin = Math.Sin(rad);
                double ax = t.Anchor == TextAnchor.Centre ? t.X - cos * width / 2 : x;
                double ay = t.Anchor == TextAnchor.Centre ? t.Y - sin * width / 2 : t.Y;

                ops.AppendFormat(CultureInfo.InvariantCulture,
                    "{0:F4} {1:F4} {2:F4} {3:F4} {4:F2} {5:F2} Tm\n", cos, sin, -sin, cos, ax, ay);
            }

            ops.AppendFormat("({0}) Tj\nET\n", RunRenderer.EncodeWinAnsiLiteral(t.Text));
        }

        // One implementation, in RunRenderer.AppendFill.
        private static void AppendFill(StringBuilder ops, string hex)
            => RunRenderer.AppendFill(ops, hex);

        private static void AppendStroke(StringBuilder ops, string hex, double widthPt)
            => RunRenderer.AppendStroke(ops, hex, widthPt);

        // A non-finite operand is not merely wrong; some viewers reject the whole stream.
        private static bool Finite(params double[] values)
        {
            foreach (double v in values)
                if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            return true;
        }
    }
}
