// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Plugins.PostScript.Graphics
{
    /// <summary>
    /// Converts <c>arc</c> / <c>arcn</c> into cubic Béziers.
    ///
    /// <para><b>Béziers, not line segments.</b> PDF has no arc operator, so an arc has to be
    /// approximated — but the emitted stream is vector output that may be rendered at any scale or
    /// re-exported to SVG, so the approximation has to be resolution-independent. Splitting the
    /// sweep at 90° and using the standard control-point distance
    /// <c>k = (4/3)·tan(θ/4)</c> gives a maximum radial error of about 2.7e-4·r for a quarter
    /// circle — invisible at any zoom.</para>
    ///
    /// <para><b><see cref="PsLimits.MaxArcSegmentDegrees"/>, deliberately not
    /// <c>PdfLimits.BezierFlattenSteps</c>.</b> That constant is documented as being for a raster
    /// target ("ample when the result is immediately sampled to pixels") and counts <i>line</i>
    /// segments, which is the opposite of producing curves. CLAUDE.md records the
    /// <c>BezierFlattenSteps</c>-versus-<c>SvgGradientStopCount</c> collapse as exactly this
    /// mistake.</para>
    ///
    /// <para>The arc is built in <b>user space</b>. Under a non-uniform CTM a circle is an ellipse
    /// in device space, so constructing it there and letting the CTM apply would produce a visibly
    /// wrong shape.</para>
    /// </summary>
    internal static class PsArcFlattener
    {
        /// <summary>
        /// Appends the arc to <paramref name="path"/>.
        ///
        /// <para>Per the PLRM, if the path already has a current point a line is drawn to the arc's
        /// start; otherwise the arc begins with a <c>moveto</c>. Both are easy to get wrong and both
        /// are visible in the output.</para>
        /// </summary>
        /// <param name="path">Path to append to.</param>
        /// <param name="cx">Centre x, user space.</param>
        /// <param name="cy">Centre y, user space.</param>
        /// <param name="radius">Radius; a negative radius is a rangecheck for the caller.</param>
        /// <param name="startDegrees">Start angle in degrees, counter-clockwise from +x.</param>
        /// <param name="endDegrees">End angle in degrees.</param>
        /// <param name="clockwise"><c>arcn</c> when true, <c>arc</c> when false.</param>
        public static void Append(PsPath path, double cx, double cy, double radius,
                                  double startDegrees, double endDegrees, bool clockwise)
        {
            double sweep = NormaliseSweep(startDegrees, endDegrees, clockwise);

            double startRad = startDegrees * Math.PI / 180.0;
            var (sx, sy) = (cx + radius * Math.Cos(startRad), cy + radius * Math.Sin(startRad));

            if (path.CurrentPoint.HasValue) path.LineTo(sx, sy);
            else path.MoveTo(sx, sy);

            // A zero sweep is a legal no-op beyond the connecting line/move — `0 0 r 90 90 arc`.
            if (Math.Abs(sweep) < 1e-12) return;

            int segments = SaturatingCast.ToInt(Math.Ceiling(Math.Abs(sweep) / PsLimits.MaxArcSegmentDegrees));
            if (segments < 1) segments = 1;
            if (segments > PsLimits.MaxArcSegments) segments = PsLimits.MaxArcSegments;

            double perSegment = sweep / segments;
            double angle = startDegrees;

            for (int i = 0; i < segments; i++)
            {
                AppendSegment(path, cx, cy, radius, angle, angle + perSegment);
                angle += perSegment;
            }
        }

        /// <summary>
        /// The signed sweep in degrees.
        ///
        /// <para><c>arc</c> sweeps counter-clockwise and <c>arcn</c> clockwise, each taking the
        /// route from start to end in its own direction — so <c>0 0 1 350 10 arc</c> sweeps 20°
        /// forwards, not 340° backwards. A sweep given as a full turn or more (<c>0 720 arc</c>) is
        /// clamped to one revolution: more than that redraws the same circle.</para>
        ///
        /// <para><b>Clamp first, adjust once — never a <c>while</c> loop.</b> This was written as
        /// <c>while (sweep &lt; 0) sweep += 360.0;</c>, which does not terminate: once <c>|sweep|</c>
        /// is large enough that 360 falls below the value's ULP the addition is a no-op, and
        /// <c>-1e300 + 360 == -1e300</c> exactly. The angles arrive as unbounded doubles straight off
        /// the operand stack, so <c>0 0 1 1e300 0 arc</c> hung the thread — and because the spin is
        /// inside one operator, the interpreter's step budget never got to see it. Every other guard
        /// in <see cref="PsLimits"/> is powerless against a loop that never returns to the step loop,
        /// which is why the bound has to be established here, before any iteration.</para>
        /// </summary>
        private static double NormaliseSweep(double start, double end, bool clockwise)
        {
            double raw = end - start;

            // Infinity - Infinity. Unreachable through `arc`, which refuses a non-finite operand,
            // but this is the function that must not be the one to produce a NaN coordinate.
            if (double.IsNaN(raw)) return 0;

            // An infinite difference is unambiguously more than a turn, whichever way it points.
            // Handled before the reduction because fmod(Infinity, x) is NaN.
            if (double.IsInfinity(raw))
                return clockwise ? -PsLimits.MaxArcSweepDegrees : PsLimits.MaxArcSweepDegrees;

            // The clamp tests the SIGNED difference, in the operator's own direction — testing
            // Math.Abs(raw) instead is wrong, and subtly: `400 0 arc` has a raw difference of -400,
            // which is not a multi-turn request at all. 400 degrees is 40 degrees, so sweeping
            // counter-clockwise from there to 0 (== 360) is 320 degrees. Clamping on the magnitude
            // turned that into a full circle ending back at its start point.
            if (clockwise)
            {
                if (raw <= -PsLimits.MaxArcSweepDegrees) return -PsLimits.MaxArcSweepDegrees;

                // fmod: exact, and a single operation. This replaces `while (sweep > 0) sweep -= 360`,
                // which does not terminate once 360 falls below the value's ULP —
                // `1e300 - 360 == 1e300` exactly — and which spun inside one operator, where the
                // interpreter's step budget could never regain control to stop it.
                double s = raw % 360.0;
                return s > 0 ? s - 360.0 : s;
            }

            if (raw >= PsLimits.MaxArcSweepDegrees) return PsLimits.MaxArcSweepDegrees;

            double t = raw % 360.0;
            return t < 0 ? t + 360.0 : t;
        }

        /// <summary>One cubic approximating a sweep of at most 90°.</summary>
        private static void AppendSegment(PsPath path, double cx, double cy, double radius,
                                          double fromDegrees, double toDegrees)
        {
            double a0 = fromDegrees * Math.PI / 180.0;
            double a1 = toDegrees * Math.PI / 180.0;
            double delta = a1 - a0;

            // k = 4/3 · tan(Δ/4): the control-point distance that makes the cubic touch the circle
            // at both ends and at the midpoint.
            double k = 4.0 / 3.0 * Math.Tan(delta / 4.0);

            double cos0 = Math.Cos(a0), sin0 = Math.Sin(a0);
            double cos1 = Math.Cos(a1), sin1 = Math.Sin(a1);

            double x0 = cx + radius * cos0, y0 = cy + radius * sin0;
            double x3 = cx + radius * cos1, y3 = cy + radius * sin1;

            double x1 = x0 - k * radius * sin0, y1 = y0 + k * radius * cos0;
            double x2 = x3 + k * radius * sin1, y2 = y3 - k * radius * cos1;

            path.CurveTo(x1, y1, x2, y2, x3, y3);
        }

        /// <summary>
        /// <c>arct</c> / <c>arcto</c>: the arc of the given radius tangent to both the line from the
        /// current point to (x1,y1) and the line from (x1,y1) to (x2,y2).
        ///
        /// <para>Returns the four tangent coordinates the operators push back
        /// (<c>xt1 yt1 xt2 yt2</c>), or null when the geometry is degenerate — a zero radius, or
        /// collinear points, where there is no such arc and the PLRM says to draw a straight
        /// line.</para>
        /// </summary>
        public static (double Xt1, double Yt1, double Xt2, double Yt2)? AppendTangentArc(
            PsPath path, double x1, double y1, double x2, double y2, double radius)
        {
            if (!path.CurrentPoint.HasValue) return null;
            var (x0, y0) = path.CurrentPoint.Value;

            double v1x = x0 - x1, v1y = y0 - y1;
            double v2x = x2 - x1, v2y = y2 - y1;

            double len1 = Math.Sqrt(v1x * v1x + v1y * v1y);
            double len2 = Math.Sqrt(v2x * v2x + v2y * v2y);
            if (len1 < 1e-12 || len2 < 1e-12 || radius <= 0) return null;

            v1x /= len1; v1y /= len1;
            v2x /= len2; v2y /= len2;

            double cross = v1x * v2y - v1y * v2x;
            if (Math.Abs(cross) < 1e-12) return null;   // collinear: no arc exists

            double dot = v1x * v2x + v1y * v2y;
            dot = Math.Max(-1.0, Math.Min(1.0, dot));
            double angle = Math.Acos(dot);

            // Distance from the corner to each tangent point.
            double tangentDistance = radius / Math.Tan(angle / 2.0);
            // No limit at the segments' ends: PLRM's arct is tangent to the two LINES, and a tangent
            // point past an end is drawn to by the line arct appends. Refusing it drew a straight
            // corner, and rounding left a radius that fits exactly refused at random.
            if (double.IsNaN(tangentDistance) || double.IsInfinity(tangentDistance)) return null;

            double t1x = x1 + v1x * tangentDistance, t1y = y1 + v1y * tangentDistance;
            double t2x = x1 + v2x * tangentDistance, t2y = y1 + v2y * tangentDistance;

            // Centre: along the angle bisector, at radius / sin(angle/2) from the corner.
            double bisectorX = v1x + v2x, bisectorY = v1y + v2y;
            double bisectorLen = Math.Sqrt(bisectorX * bisectorX + bisectorY * bisectorY);
            if (bisectorLen < 1e-12) return null;
            bisectorX /= bisectorLen; bisectorY /= bisectorLen;

            double centreDistance = radius / Math.Sin(angle / 2.0);
            double cx = x1 + bisectorX * centreDistance;
            double cy = y1 + bisectorY * centreDistance;

            double startAngle = Math.Atan2(t1y - cy, t1x - cx) * 180.0 / Math.PI;
            double endAngle   = Math.Atan2(t2y - cy, t2x - cx) * 180.0 / Math.PI;

            // The DIRECTION OF TRAVEL at the corner is -v1, not v1: v1 was built as
            // (currentPoint - corner), which points BACK along the incoming segment. So
            // cross(travel, v2) = -cross(v1, v2), and testing `cross < 0` selected the opposite
            // sweep — the 270-degree complementary arc instead of the 90-degree fillet. The standard
            // rounded-rectangle idiom came out as a loop bulging outside the corner it was meant to
            // round. The collinearity test above is unaffected: it uses Math.Abs.
            Append(path, cx, cy, radius, startAngle, endAngle, clockwise: cross > 0);

            return (t1x, t1y, t2x, t2y);
        }
    }
}
