// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Linq;
using AngleSharp.Dom;
using Tetitulz.Pdf.Builder.Export;
using M = Tetitulz.Pdf.Builder.Model;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// <c>border-radius</c>, <c>box-shadow</c> and <c>transform</c> on a block.
    ///
    /// <para><b>The decline said "each needs a per-box graphics-state stack".</b> The content
    /// builder has had one all along — <c>SaveState</c>, <c>RestoreState</c>, <c>Transform</c>,
    /// <c>CurveTo</c> and <c>ClipNonzero</c> are all on <c>PdfContentStreamBuilder</c>, and the
    /// exporter already brackets its box fills with save/restore. What was missing was a model
    /// field to carry the value and the drawing to read it.</para>
    ///
    /// <para><b><c>overflow</c> is the one that genuinely cannot be done, and for a different
    /// reason than the row gave.</b> Clipping is available; what is missing is something to clip
    /// <i>against</i>. A block's height in this model comes from its content, so it can never
    /// exceed its own box and there is no overflow to hide. Honouring <c>overflow</c> would first
    /// require an explicit block height — a layout change, not a painting one — so it stays
    /// declined with that as its reason.</para>
    /// </summary>
    internal sealed partial class HtmlToBuilderMapper
    {
        /// <summary>
        /// Copies the block-decoration properties from <paramref name="el"/> onto
        /// <paramref name="para"/>. Called for every block, and does nothing for the overwhelming
        /// majority that declare none.
        /// </summary>
        private void ApplyBoxDecoration(IElement el, M.Paragraph para)
        {
            ApplyBorderRadius(el, para);
            ApplyBoxShadow(el, para);
            ApplyTransform(el, para);
            ReportOverflowIfDeclared(el);
        }

        private void ApplyBorderRadius(IElement el, M.Paragraph para)
        {
            if (ResolveBorderRadius(el, DefaultFontSizePt) is not { } r) return;
            para.CornerRadiusPercent = r.Percent;
            para.CornerRadiusPt = r.Uniform; para.CornerRadiiPt = r.Corners; para.CornerRadiiYPt = r.CornersY;
            para.CornerRadiiPercent = r.Percents; para.CornerRadiiYPercent = r.PercentsY;
        }

        /// <summary>
        /// An element's <c>border-radius</c> as the model carries it — a paragraph's, a box of blocks'
        /// and an inline box's alike: one percentage for all four corners
        /// (<see cref="ResolveCornerPercent"/>), else the one uniform radius or the four corners with
        /// their vertical radii and per-corner percentages (<see cref="ResolveCornerRadii"/>);
        /// <c>null</c> when every corner is square. A tab rounded at the top only is rounded at the top
        /// only, never all round by its largest corner's radius.
        /// </summary>
        private (double Percent, double Uniform, double[]? Corners, double[]? CornersY, double[]? Percents, double[]? PercentsY)? ResolveBorderRadius(IElement el, double fontSize)
        {
            if (ResolveCornerPercent(el) is double pct) return (pct, 0, null, null, null, null);
            if (ResolveCornerRadii(el, fontSize) is not { } r) return null;
            return r.Uniform is double u
                ? (0, u, null, r.CornersY, r.Percents, r.PercentsY)
                : (0, 0, r.Corners, r.CornersY, r.Percents, r.PercentsY);
        }

        /// <summary>
        /// An element's corner radii in points — top-left, top-right, bottom-right, bottom-left —
        /// from the <c>border-radius</c> shorthand (one to four values, CSS's corner order) and the
        /// four <c>border-*-*-radius</c> longhands over it; <c>Uniform</c> when all four agree and
        /// are circular. <c>null</c> when every corner is square. The one reading for a paragraph, a
        /// box of blocks and an inline box.
        /// </summary>
        /// <remarks>
        /// <para>It kept the first value only, so a Bootstrap panel heading — whose top corners are
        /// rounded and bottom ones square — drew all four rounded, over the panel's straight edge.</para>
        /// <para>An elliptical radius — the shorthand's <c>x / y</c>, a longhand's <c>x y</c> — gives
        /// <c>CornersY</c>, the vertical radii; circular corners leave it <c>null</c>. Each list
        /// expands from one to four values the same way (CSS Backgrounds 3 §5.3).</para>
        /// <para>A percentage among the values goes to <c>Percents</c> (horizontal, of the width)
        /// or <c>PercentsY</c> (vertical, of the height) at its corner, never read as 0; a corner
        /// with a percentage has 0 in the length arrays.</para>
        /// </remarks>
        private (double? Uniform, double[] Corners, double[]? CornersY, double[]? Percents, double[]? PercentsY)? ResolveCornerRadii(IElement el, double fontSize)
        {
            double[] c = new double[4], y = new double[4];
            double[] pc = new double[4], py = new double[4];
            string? shorthand = StyleValue(el, "border-radius");
            if (!string.IsNullOrWhiteSpace(shorthand))
            {
                string[] halves = shorthand!.Split('/');
                Expand(halves[0], c, pc);
                if (halves.Length > 1) Expand(halves[1], y, py);
                else { Array.Copy(c, y, 4); Array.Copy(pc, py, 4); }
            }
            string[] longhands = { "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius" };
            for (int i = 0; i < 4; i++)
                if (StyleValue(el, longhands[i]) is { } lv
                    && lv.Split(new[] { ' ', '\t', '/' }, StringSplitOptions.RemoveEmptyEntries) is { Length: > 0 } parts)
                {
                    (c[i], pc[i]) = Radius(parts[0]);
                    (y[i], py[i]) = parts.Length > 1 ? Radius(parts[1]) : (c[i], pc[i]);
                }

            bool anyLength = c[0] > 0 || c[1] > 0 || c[2] > 0 || c[3] > 0;
            bool anyPercent = pc[0] > 0 || pc[1] > 0 || pc[2] > 0 || pc[3] > 0;
            if (!anyLength && !anyPercent) return null;
            bool circular = c[0] == y[0] && c[1] == y[1] && c[2] == y[2] && c[3] == y[3]
                            && pc[0] == py[0] && pc[1] == py[1] && pc[2] == py[2] && pc[3] == py[3];
            bool uniform = circular && !anyPercent && c[0] == c[1] && c[1] == c[2] && c[2] == c[3];
            bool anyPercentY = py[0] > 0 || py[1] > 0 || py[2] > 0 || py[3] > 0;
            return (uniform ? c[0] : null, c, circular ? null : y, anyPercent ? pc : null, anyPercentY ? py : null);

            // One radius token: a percentage, else a length (0 for one this cannot read).
            (double Length, double Percent) Radius(string token)
            {
                string t = token.Trim();
                if (t.EndsWith("%", StringComparison.Ordinal)
                    && double.TryParse(t.Substring(0, t.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                    return (0, Math.Max(0, pct));
                return (NonNeg(ParseLength(t, 0, fontSize)), 0);
            }

            void Expand(string list, double[] into, double[] intoPercent)
            {
                string[] v = list.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                (double, double) R(int i) => i < v.Length ? Radius(v[i]) : (0, 0);
                int[] corners = v.Length switch
                {
                    1 => new[] { 0, 0, 0, 0 },
                    2 => new[] { 0, 1, 0, 1 },
                    3 => new[] { 0, 1, 2, 1 },
                    _ => new[] { 0, 1, 2, 3 },
                };
                if (v.Length == 0) return;
                for (int i = 0; i < 4; i++) (into[i], intoPercent[i]) = R(corners[i]);
            }
        }

        /// <summary>
        /// A <c>border-radius</c> of one percentage — <c>50%</c>, Bootstrap's <c>.img-circle</c> —
        /// with no longhand beside it, as that percentage (the model's one-for-all-four
        /// <c>CornerRadiusPercent</c>, which an inline box and an image take too); <c>null</c>
        /// otherwise, and <see cref="ResolveCornerRadii"/> reads the corners one by one — a
        /// percentage among lengths, or in a longhand, at its own corner. A longhand beside the shorthand overrides its corner,
        /// which the one-value path cannot express.
        /// </summary>
        private double? ResolveCornerPercent(IElement el)
        {
            string? shorthand = StyleValue(el, "border-radius")?.Trim();
            bool anyLonghand = false;
            foreach (string name in new[] { "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius" })
                if (StyleValue(el, name) != null) anyLonghand = true;

            if (!anyLonghand && !string.IsNullOrEmpty(shorthand) && shorthand!.EndsWith("%", StringComparison.Ordinal)
                && double.TryParse(shorthand.Substring(0, shorthand.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct)
                && pct > 0)
                return pct;
            return null;
        }

        private void ApplyBoxShadow(IElement el, M.Paragraph para)
        {
            var shadows = ResolveShadows(el);
            if (shadows.Count == 0) return;
            para.Shadow = shadows[0];
            para.MoreShadows.AddRange(shadows.Skip(1));
        }

        /// <summary>
        /// An element's <c>box-shadow</c> list, in CSS's order — drop shadows and <c>inset</c> ones
        /// alike (CSS Backgrounds 3 §7.1) — for a paragraph and a box of blocks. A shadow with fewer
        /// than two lengths, or a fully transparent one, draws nothing and is left out.
        /// </summary>
        /// <remarks>
        /// Every shadow is drawn since the model has a list (<c>MoreShadows</c>) and an inset flag;
        /// only the first was, and an inset one dropped the whole declaration — every Bootstrap
        /// <c>.form-control</c>, <c>.well</c> and <c>.progress</c> has one.
        /// </remarks>
        private System.Collections.Generic.List<M.BoxShadow> ResolveShadows(IElement el)
        {
            var shadows = new System.Collections.Generic.List<M.BoxShadow>();
            string? raw = StyleValue(el, "box-shadow");
            if (string.IsNullOrWhiteSpace(raw)) return shadows;

            string v = raw!.Trim();
            if (Eq(v, "none")) return shadows;

            // The shadows are split at top-level commas and a shadow's parts at top-level spaces, so
            // `rgba(0, 0, 0, 0.5)` stays one token. Splitting at every comma and space tore a spaced
            // rgba() into pieces that were no colour, so the shadow was drawn in the grey default.
            foreach (string part in SplitTopLevel(v, ch => ch == ',', keepEmpty: true))
                if (ParseShadow(part) is { } shadow) shadows.Add(shadow);
            return shadows;
        }

        /// <summary>One shadow of a <c>box-shadow</c> list, or <c>null</c> when it draws nothing.</summary>
        private M.BoxShadow? ParseShadow(string shadow)
        {
            var lengths = new System.Collections.Generic.List<double>();
            string? colour = null;
            bool inset = false;
            foreach (string token in SplitTopLevelTokens(shadow))
            {
                if (Eq(token, "inset")) { inset = true; continue; }
                double len = ResolveWidth(token, 0, double.NaN);
                if (!double.IsNaN(len)) { lengths.Add(len); continue; }
                // A fully transparent shadow draws nothing. The colour came back absent, so the
                // shadow was drawn in the grey default: `box-shadow: 0 0 transparent`, a common
                // "no shadow" spelling, painted a grey box.
                if (Tetitulz.Pdf.Core.Text.CssColor.IsFullyTransparent(token)) return null;
                colour ??= TryNormalizeColor(token);
            }

            // <offset-x> <offset-y> are the first two lengths and the only ones required; a third
            // is the blur radius (never negative, CSS Backgrounds 3 §7.1) and a fourth the spread.
            if (lengths.Count < 2) return null;
            return new M.BoxShadow
            {
                OffsetXPt = lengths[0],
                OffsetYPt = lengths[1],
                Color = colour ?? "#808080",
                BlurPt = lengths.Count > 2 ? Math.Max(0, lengths[2]) : 0,
                SpreadPt = lengths.Count > 3 ? lengths[3] : 0,
                Inset = inset,
            };
        }

        private void ApplyTransform(IElement el, M.Paragraph para)
        {
            string? raw = StyleValue(el, "transform");
            if (string.IsNullOrWhiteSpace(raw)) return;

            string v = raw!.Trim();
            if (Eq(v, "none")) return;

            double[]? m = ParseTransform(v);
            if (m == null)
            {
                ReportApproximation($"transform '{v}' was not applied: this engine reads translate, scale, " +
                          "rotate, skew and matrix, and composes no others.");
                return;
            }

            para.Transform = m;
            // transform-origin (CSS Transforms 1 §6): the centre unless stated. The link and
            // form-field rectangles inside the block follow the transform in the exporter, so there is
            // nothing to report.
            para.TransformOrigin = ParseTransformOrigin(StyleValue(el, "transform-origin"));
        }

        /// <summary>
        /// <c>transform-origin</c> as <see cref="M.Paragraph.TransformOrigin"/>'s four numbers — a
        /// fraction and an offset per axis — or <c>null</c> for the default centre. One value sets the
        /// horizontal origin and leaves the vertical at the centre; two set both, and may be keywords
        /// in either order (<c>top left</c>), a percentage or a length; a third (the z offset) is
        /// ignored. A value this cannot read is reported and leaves the centre.
        /// </summary>
        private double[]? ParseTransformOrigin(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string[] tokens = raw!.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 || tokens.Length > 3)
            {
                ReportApproximation($"transform-origin '{raw.Trim()}' was not read; the transform is taken about the centre.");
                return null;
            }
            string xToken = tokens[0];
            string? yToken = tokens.Length > 1 ? tokens[1] : null;
            // A vertical keyword first, or a horizontal one second, names the axes the other way round.
            if (IsOriginKeyword(xToken, "top", "bottom") || (yToken != null && IsOriginKeyword(yToken, "left", "right")))
                (xToken, yToken) = (yToken ?? "center", xToken);

            if (TryOriginComponent(xToken, "left", "right", out double fx, out double dx) &&
                (yToken == null ? SetCentre(out double fy, out double dy) : TryOriginComponent(yToken, "top", "bottom", out fy, out dy)))
                return new[] { fx, dx, fy, dy };

            ReportApproximation($"transform-origin '{raw.Trim()}' was not read; the transform is taken about the centre.");
            return null;
        }

        private static bool SetCentre(out double fraction, out double offset)
        {
            fraction = 0.5; offset = 0;
            return true;
        }

        private static bool IsOriginKeyword(string token, string start, string end)
            => Eq(token, start) || Eq(token, end);

        /// <summary>One <c>transform-origin</c> component on an axis whose edges are <paramref name="start"/> and <paramref name="end"/>.</summary>
        private bool TryOriginComponent(string token, string start, string end, out double fraction, out double offset)
        {
            fraction = 0; offset = 0;
            if (Eq(token, start)) return true;
            if (Eq(token, "center")) { fraction = 0.5; return true; }
            if (Eq(token, end)) { fraction = 1; return true; }
            if (token.EndsWith("%", StringComparison.Ordinal))
            {
                if (!double.TryParse(token.Substring(0, token.Length - 1), NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                    return false;
                fraction = pct / 100;
                return true;
            }
            double length = ParseLength(token, double.NaN);
            if (double.IsNaN(length)) return false;
            offset = length;
            return true;
        }

        /// <summary>
        /// A <c>transform</c> function list, composed into one affine matrix, or <c>null</c> when
        /// it names something this cannot express.
        /// </summary>
        /// <remarks>
        /// <para>CSS applies a transform list RIGHT to left to the coordinate: in
        /// <c>translateX(10pt) scale(2)</c> the point is scaled first and then translated, so the
        /// translation is NOT scaled. PDF composes row-vector matrices, so that means the
        /// first-listed function must end up as the LAST factor — <c>Multiply(step, result)</c>,
        /// not <c>Multiply(result, step)</c>.</para>
        /// <para>This was written the other way round first, and it is invisible for a single
        /// function: every one-function case passed. Only the pair distinguishes them, which is why
        /// there is a two-function test and why it uses translate-then-scale rather than two
        /// rotations, whose composition is the same in either order.</para>
        /// </remarks>
        private double[]? ParseTransform(string value)
        {
            double[] result = { 1, 0, 0, 1, 0, 0 };
            bool any = false;
            int i = 0;

            while (i < value.Length)
            {
                while (i < value.Length && (char.IsWhiteSpace(value[i]) || value[i] == ',')) i++;
                if (i >= value.Length) break;

                int open = value.IndexOf('(', i);
                if (open < 0) return null;
                int close = value.IndexOf(')', open);
                if (close < 0) return null;

                string name = value.Substring(i, open - i).Trim().ToLowerInvariant();
                string[] tokens = value.Substring(open + 1, close - open - 1)
                    .Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var args = new double[tokens.Length];
                for (int k = 0; k < tokens.Length; k++)
                    if (!TryTransformArg(name, tokens[k], out args[k])) return null;

                double[]? step = TransformStep(name, args);
                if (step == null) return null;

                // step FIRST: see the remarks above for why the order is this way round.
                result = Tetitulz.Pdf.Core.Writing.TransformMatrix.Multiply(step, result);
                any = true;
                i = close + 1;
            }

            // A composed matrix with a non-finite entry — from an overflowing scale(1e200) scale(1e200),
            // or an Infinity/NaN token on .NET Core — draws a degenerate or blank box and, worse, reaches
            // the Builder model, which refuses it at export with an exception naming an argument the
            // caller never passed. The transform is dropped (and the caller reports it) instead.
            if (!any) return null;
            foreach (double x in result) if (double.IsNaN(x) || double.IsInfinity(x)) return null;
            return result;
        }

        /// <summary>
        /// One <c>transform</c> argument, read by the function it belongs to: a <c>scale*</c> takes a
        /// number or a percentage (<c>scale(50%)</c> is 0.5); a <c>translate*</c> takes a length in any
        /// absolute or font-relative unit (through <c>ParseLength</c>, the one length path), and a
        /// percentage is refused here because the box it resolves against is not known at parse time; a
        /// <c>rotate</c> or <c>skew</c> takes an angle; a <c>matrix</c> takes a number. A non-finite or
        /// unreadable token refuses, so the whole transform is dropped and reported rather than silently
        /// read as 0 — which turned <c>scale(50%)</c> into a zero matrix and <c>translateX(1em)</c> into
        /// no shift.
        /// </summary>
        private bool TryTransformArg(string funcName, string token, out double value)
        {
            value = 0;
            string t = token.Trim().ToLowerInvariant();
            if (t.Length == 0) return false;

            if (funcName.StartsWith("rotate", StringComparison.Ordinal) || funcName.StartsWith("skew", StringComparison.Ordinal))
            {
                foreach (var (suffix, factor) in AngleUnits)
                    if (t.EndsWith(suffix, StringComparison.Ordinal) &&
                        double.TryParse(t.Substring(0, t.Length - suffix.Length), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out double a))
                        return Finite(a * factor, out value);
                // A bare number is degrees nowhere, but a unit-less 0 is a valid zero angle.
                return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double z) && z == 0 && Finite(0, out value);
            }

            if (funcName.StartsWith("scale", StringComparison.Ordinal))
            {
                if (t.EndsWith("%", StringComparison.Ordinal))
                    return double.TryParse(t.Substring(0, t.Length - 1), NumberStyles.Float,
                                           CultureInfo.InvariantCulture, out double pct) && Finite(pct / 100, out value);
                return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) && Finite(n, out value);
            }

            if (funcName.StartsWith("translate", StringComparison.Ordinal))
            {
                // A percentage translate resolves against the box's own size, which is not known here;
                // dropping the transform and reporting is better than a silent zero shift.
                if (t.EndsWith("%", StringComparison.Ordinal)) return false;
                if (t == "0") return Finite(0, out value);
                double pt = ParseLength(t, double.NaN);   // em/rem/absolute units, the one length path
                return !double.IsNaN(pt) && Finite(pt, out value);
            }

            // matrix(): six numbers.
            return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double m) && Finite(m, out value);
        }

        private static readonly (string Suffix, double Factor)[] AngleUnits =
        {
            ("deg", Math.PI / 180), ("grad", Math.PI / 200), ("turn", 2 * Math.PI), ("rad", 1.0),
        };

        private static bool Finite(double v, out double value)
        {
            value = v;
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        private static double[]? TransformStep(string name, double[] a)
        {
            double At(int i, double fallback = 0) => i < a.Length ? a[i] : fallback;

            switch (name)
            {
                case "translate":  return new[] { 1.0, 0, 0, 1, At(0), -At(1) };
                case "translatex": return new[] { 1.0, 0, 0, 1, At(0), 0 };
                // CSS Y grows downward and PDF upward, so a positive translateY moves DOWN, which
                // is a negative PDF offset. Missing this makes every vertical shift go the wrong
                // way, which reads as the transform being ignored in one axis.
                case "translatey": return new[] { 1.0, 0, 0, 1, 0, -At(0) };
                case "scale":      return new[] { At(0, 1), 0, 0, At(1, At(0, 1)), 0, 0 };
                case "scalex":     return new[] { At(0, 1), 0, 0, 1.0, 0, 0 };
                case "scaley":     return new[] { 1.0, 0, 0, At(0, 1), 0, 0 };
                case "rotate":
                {
                    // Negated for the same axis reason: CSS rotates clockwise, PDF anticlockwise.
                    double t = -At(0);
                    return new[] { Math.Cos(t), Math.Sin(t), -Math.Sin(t), Math.Cos(t), 0, 0 };
                }
                case "skewx":  return new[] { 1.0, 0, -Math.Tan(At(0)), 1, 0, 0 };
                case "skewy":  return new[] { 1.0, -Math.Tan(At(0)), 0, 1, 0, 0 };
                case "matrix":
                    return a.Length >= 6 ? new[] { a[0], a[1], a[2], a[3], a[4], a[5] } : null;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether an element's <c>overflow</c> — the shorthand or either axis — clips: <c>hidden</c>,
        /// <c>clip</c>, <c>auto</c> or <c>scroll</c> (a printed page scrolls nothing, so the last two
        /// clip as a browser prints them). A box of blocks takes it (<c>BoxStart.ClipsContent</c>);
        /// a paragraph's is reported, below.
        /// </summary>
        private bool ClipsOverflow(IElement el)
        {
            foreach (string name in new[] { "overflow", "overflow-x", "overflow-y" })
            {
                string? v = StyleValue(el, name)?.Trim();
                if (string.IsNullOrEmpty(v)) continue;
                foreach (string token in v!.Split(new[] { ' ', '	' }, StringSplitOptions.RemoveEmptyEntries))
                    if (Eq(token, "hidden") || Eq(token, "clip") || Eq(token, "auto") || Eq(token, "scroll")) return true;
            }
            return false;
        }

        private void ReportOverflowIfDeclared(IElement el)
        {
            string? overflow = StyleValue(el, "overflow")?.Trim();
            if (overflow == null || Eq(overflow, "visible")) return;
            // With no height to hold it to, a paragraph is as tall as its content, so nothing
            // overflows and nothing is lost: normalize.css's `textarea { overflow: auto }` reported
            // itself on every form. `height: auto` and `max-height: none` are no height either —
            // Bootstrap's `textarea.form-control` says the first.
            string? height = StyleValue(el, "height")?.Trim(), maxHeight = StyleValue(el, "max-height")?.Trim();
            bool held = (height != null && !Eq(height, "auto")) || (maxHeight != null && !Eq(maxHeight, "none"));
            if (!held) return;

            ReportApproximation($"overflow:{overflow} under a stated height is not applied as a clip at that height: " +
                      "a block's stated height is a minimum in this engine, so the box grows to hold " +
                      "content taller than it rather than hiding what overflows.");
        }

    }
}
