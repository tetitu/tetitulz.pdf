// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Charts
{
    /// <summary>
    /// Turns a <see cref="ChartModel"/> into positioned geometry. No PDF, no OpenXml — which is what
    /// makes the parts that actually go wrong (axis ranges, the zero baseline, cluster arithmetic,
    /// label collision) testable as arithmetic rather than as a diff of a content stream.
    /// </summary>
    internal static class ChartLayout
    {
        private const double TitleSizePt = 14;
        private const double LabelSizePt = 9;
        private const double AxisTitleSizePt = 10;
        private const double LegendSwatch = 8;
        private const double Gap = 4;
        private const double TickLength = 3;

        /// <summary>Default gridline grey, when the file states none.</summary>
        private const string GridlineHex = "D9D9D9";
        private const string AxisHex = "808080";

        /// <summary>
        /// Excel's series palette: accent1–6, then the same hues at lighter and darker luminance.
        /// The caller supplies resolved theme colours; this is the cycling rule.
        /// </summary>
        internal static string PaletteAt(IReadOnlyList<string> accents, int index)
        {
            if (accents == null || accents.Count == 0) return "4472C4";
            return accents[index % accents.Count];
        }

        /// <summary>
        /// Lays the chart out into a <paramref name="width"/> × <paramref name="height"/> box.
        /// <paramref name="measure"/> gives a string's width at a point size.
        /// </summary>
        internal static ChartFrame Compute(ChartModel model, double width, double height,
            IReadOnlyList<string> accents, Func<string, double, double> measure)
        {
            var frame = new ChartFrame { Width = width, Height = height };

            double top = height, bottom = 0, left = 0, right = width;

            // --- title
            if (!string.IsNullOrEmpty(model.Title) && !model.AutoTitleDeleted)
            {
                double band = TitleSizePt * 1.2 + Gap;
                top -= band;
                frame.Items.Add(new TextItem
                {
                    Text = model.Title,
                    X = width / 2,
                    Y = top + Gap,
                    SizePt = TitleSizePt,
                    Anchor = TextAnchor.Centre,
                    Bold = true,
                });
            }

            // --- legend band (drawn last, but its space comes out of the box now)
            var legendEntries = LegendEntries(model, accents);
            double legendWidth = 0, legendHeight = 0;
            if (model.LegendPosition != null && legendEntries.Count > 0)
            {
                if (model.LegendPosition == "r" || model.LegendPosition == "l")
                {
                    double widest = legendEntries.Max(e => measure(e.name, LabelSizePt));
                    legendWidth = Math.Min(widest + LegendSwatch + Gap * 2, width * 0.40);
                    if (model.LegendPosition == "r") right -= legendWidth;
                    else left += legendWidth;
                }
                else
                {
                    legendHeight = LabelSizePt * 1.25 + Gap;
                    if (model.LegendPosition == "b") bottom += legendHeight;
                    else top -= legendHeight;
                }
            }

            // --- the value scale, sized from the BOX not the plot, so the tick count cannot
            //     depend on the label widths that depend on the tick count.
            int targetTicks = SaturatingCast.ToInt(Math.Round(height / 40.0));
            targetTicks = Math.Max(2, Math.Min(11, targetTicks));

            if (model.HasAxes)
                LayoutAxisChart(model, frame, accents, measure, left, right, bottom, top, targetTicks);
            else
                LayoutPieChart(model, frame, accents, measure,
                    new ChartRect(left, bottom, right - left, top - bottom));

            // --- legend, painted last so it sits above the plot
            if (legendEntries.Count > 0 && model.LegendPosition != null)
                DrawLegend(frame, model.LegendPosition, legendEntries, measure,
                    width, height, legendWidth, legendHeight, left, right, bottom, top);

            return frame;
        }

        // ------------------------------------------------------------------ axis charts

        private static void LayoutAxisChart(ChartModel model, ChartFrame frame,
            IReadOnlyList<string> accents, Func<string, double, double> measure,
            double left, double right, double bottom, double top, int targetTicks)
        {
            int pointCount = model.PointCount;
            bool horizontal = model.BarDir == BarDirection.Bar && model.Kind == ChartKind.Bar;

            // c:delete removes the axis from the chart — its line, its ticks, its labels and its
            // title — which is a stronger statement than c:tickLblPos="none". Honoured, so a chart
            // authored in Excel with the value axis deleted carries no axis furniture the source
            // document does not have.
            bool valueAxisDeleted = model.ValueAxis?.Deleted ?? false;
            bool categoryAxisDeleted = model.CategoryAxis?.Deleted ?? false;
            bool valueLabelsHidden = valueAxisDeleted || (model.ValueAxis?.LabelsHidden ?? false);
            bool categoryLabelsHidden = categoryAxisDeleted || (model.CategoryAxis?.LabelsHidden ?? false);

            var values = StackedTotalsOrValues(model);
            bool allowLift = model.Kind == ChartKind.Line;

            // Provisional device ends; the real ones follow once the bands are known.
            var probe = AxisScale.ForValues(values, model.ValueAxis, 0, 1, allowLift, targetTicks);
            var tickValues = probe.MajorTicks();

            // --- reserve the label bands
            double valueLabelWidth = 0, categoryLabelHeight = 0;
            var valueLabels = new List<string>();
            foreach (double t in tickValues)
                valueLabels.Add(ExcelNumberFormat.Format(t, model.ValueAxis?.NumberFormat, probe.Step));

            if (!valueLabelsHidden)
            {
                foreach (string s in valueLabels)
                    valueLabelWidth = Math.Max(valueLabelWidth, measure(s, LabelSizePt));
                valueLabelWidth += TickLength + Gap;
            }

            if (!categoryLabelsHidden)
                categoryLabelHeight = LabelSizePt * 1.25 + TickLength + Gap;

            bool valueTitle = !valueAxisDeleted && !string.IsNullOrEmpty(model.ValueAxis?.Title);
            bool categoryTitle = !categoryAxisDeleted && !string.IsNullOrEmpty(model.CategoryAxis?.Title);
            double axisTitleLeft = valueTitle ? AxisTitleSizePt * 1.4 : 0;
            double axisTitleBottom = categoryTitle ? AxisTitleSizePt * 1.4 : 0;

            double plotLeft = left + (horizontal ? categoryLabelHeight : valueLabelWidth) + axisTitleLeft;
            double plotBottom = bottom + (horizontal ? valueLabelWidth : categoryLabelHeight) + axisTitleBottom;
            var plot = new ChartRect(plotLeft, plotBottom,
                Math.Max(1, right - plotLeft - Gap), Math.Max(1, top - plotBottom - Gap));
            frame.Plot = plot;

            // --- the real scale, over the plot's own extent
            var scale = horizontal
                ? AxisScale.ForValues(values, model.ValueAxis, plot.X, plot.Right, allowLift, targetTicks)
                : AxisScale.ForValues(values, model.ValueAxis, plot.Y, plot.Top, allowLift, targetTicks);
            tickValues = scale.MajorTicks();

            // --- gridlines first: they belong behind the data, always.
            if (model.ValueAxis?.MajorGridlines ?? false)
                foreach (double t in tickValues)
                {
                    double d = scale.Map(t);
                    if (double.IsNaN(d)) continue;
                    frame.Items.Add(horizontal
                        ? new LineItem { X0 = d, Y0 = plot.Y, X1 = d, Y1 = plot.Top, StrokeHex = GridlineHex }
                        : new LineItem { X0 = plot.X, Y0 = d, X1 = plot.Right, Y1 = d, StrokeHex = GridlineHex });
                }

            // --- series
            switch (model.Kind)
            {
                case ChartKind.Bar:
                    LayoutBars(model, frame, accents, plot, scale, pointCount, horizontal);
                    break;
                case ChartKind.Scatter:
                case ChartKind.Bubble:
                    LayoutScatter(model, frame, accents, plot, scale, targetTicks);
                    break;
                default:
                    LayoutLines(model, frame, accents, plot, scale, pointCount,
                        area: model.Kind == ChartKind.Area);
                    break;
            }

            if (model.ShowValueLabels && !model.IsPointKind)
                LayoutValueLabels(model, frame, plot, scale, pointCount, horizontal);

            // --- axis lines and ticks, above the data so a bar cannot hide the baseline
            double baseline = scale.Map(Clamp(0, scale.Min, scale.Max));
            if (double.IsNaN(baseline)) baseline = horizontal ? plot.X : plot.Y;

            // The line at value zero IS the category axis, in both orientations, so a deleted
            // category axis draws none.
            if (!categoryAxisDeleted)
                frame.Items.Add(horizontal
                    ? new LineItem { X0 = baseline, Y0 = plot.Y, X1 = baseline, Y1 = plot.Top, StrokeHex = AxisHex }
                    : new LineItem { X0 = plot.X, Y0 = baseline, X1 = plot.Right, Y1 = baseline, StrokeHex = AxisHex });

            // --- tick labels
            if (!valueLabelsHidden)
                for (int i = 0; i < tickValues.Count; i++)
                {
                    double d = scale.Map(tickValues[i]);
                    if (double.IsNaN(d)) continue;
                    string text = ExcelNumberFormat.Format(tickValues[i],
                        model.ValueAxis?.NumberFormat, scale.Step);

                    frame.Items.Add(horizontal
                        ? new TextItem
                        {
                            Text = text, X = d, Y = plot.Y - TickLength - LabelSizePt,
                            SizePt = LabelSizePt, Anchor = TextAnchor.Centre,
                        }
                        : new TextItem
                        {
                            Text = text, X = plot.X - TickLength - Gap, Y = d - LabelSizePt * 0.35,
                            SizePt = LabelSizePt, Anchor = TextAnchor.Right,
                        });
                }

            // A scatter or bubble x axis is a VALUE axis — its points carry their own x, so there
            // is no category band to label. Drawing category labels at band centres (i+0.5)*band
            // under points placed by xScale.Map put every label somewhere the data is not: a
            // confidently wrong chart. Point kinds emit their real x ticks from LayoutScatter,
            // which is the only place the x scale exists.
            if (!categoryLabelsHidden && !model.IsPointKind)
                LayoutCategoryLabels(model, frame, plot, pointCount, horizontal, measure);

            // --- axis titles
            if (valueTitle)
                frame.Items.Add(new TextItem
                {
                    Text = model.ValueAxis.Title,
                    X = left + AxisTitleSizePt,
                    Y = plot.CentreY,
                    SizePt = AxisTitleSizePt,
                    Anchor = TextAnchor.Centre,
                    RotationDeg = 90,
                });

            if (categoryTitle)
                frame.Items.Add(new TextItem
                {
                    Text = model.CategoryAxis.Title,
                    X = plot.CentreX,
                    Y = bottom,
                    SizePt = AxisTitleSizePt,
                    Anchor = TextAnchor.Centre,
                });
        }

        /// <summary>
        /// Bar and column geometry. One code path: only the final rectangle's operand order differs,
        /// because <c>barDir</c> swaps which axis is the value one.
        /// </summary>
        private static void LayoutBars(ChartModel model, ChartFrame frame,
            IReadOnlyList<string> accents, ChartRect plot, AxisScale scale, int pointCount, bool horizontal)
        {
            if (pointCount <= 0 || model.Series.Count == 0) return;

            double span = horizontal ? plot.Height : plot.Width;
            double catLo = horizontal ? plot.Y : plot.X;
            double band = span / pointCount;

            bool stacked = model.Grouping == Grouping.Stacked
                        || model.Grouping == Grouping.PercentStacked;
            int lanes = stacked ? 1 : model.Series.Count;

            double barW = band / (lanes + model.GapWidth / 100.0);
            double advance = barW * (1 - model.Overlap / 100.0);
            double clusterW = barW + (lanes - 1) * advance;

            // Value 0, clamped into the axis — NOT the plot edge. This one expression is what makes
            // negative bars hang from the baseline, all-negative charts hang from the top, and an
            // explicit minimum above zero clip instead of inverting.
            double baseDev = scale.Map(Clamp(0, scale.Min, scale.Max));
            if (double.IsNaN(baseDev)) baseDev = horizontal ? plot.X : plot.Y;

            var positiveTotals = new double[pointCount];
            var negativeTotals = new double[pointCount];

            for (int s = 0; s < model.Series.Count; s++)
            {
                var series = model.Series[s];
                string colour = series.ColorHex ?? PaletteAt(accents, s);

                for (int i = 0; i < pointCount; i++)
                {
                    double? raw = i < (series.Values?.Length ?? 0) ? series.Values[i] : null;
                    if (!raw.HasValue) continue;
                    double v = raw.Value;

                    if (model.Grouping == Grouping.PercentStacked)
                    {
                        double total = CategoryAbsTotal(model, i);
                        v = total > 0 ? v / total : 0;
                    }

                    double from, to;
                    if (stacked)
                    {
                        // Positive and negative stacks accumulate separately: a category holding
                        // +5 and −3 spans −3..+5, not −3..+2.
                        double start = v >= 0 ? positiveTotals[i] : negativeTotals[i];
                        double end = start + v;
                        if (v >= 0) positiveTotals[i] = end; else negativeTotals[i] = end;
                        from = scale.Map(start);
                        to = scale.Map(end);
                    }
                    else
                    {
                        from = baseDev;
                        to = scale.Map(v);
                    }

                    if (double.IsNaN(from) || double.IsNaN(to)) continue;
                    double lo = Math.Min(from, to), hi = Math.Max(from, to);
                    if (hi - lo < 1e-9) continue;   // a zero-height bar is a hairline artefact

                    double catCentre = catLo + (i + 0.5) * band;
                    double laneStart = catCentre - clusterW / 2 + (stacked ? 0 : s * advance);

                    frame.Items.Add(new RectItem
                    {
                        FillHex = colour,
                        Box = horizontal
                            ? new ChartRect(lo, laneStart, hi - lo, barW)
                            : new ChartRect(laneStart, lo, barW, hi - lo),
                    });
                }
            }
        }

        /// <summary>
        /// Scatter: the only kind whose <b>x is a value axis</b>. Each point carries its own pair,
        /// so there is no category band and both scales are derived from data.
        /// </summary>
        private static void LayoutScatter(ChartModel model, ChartFrame frame,
            IReadOnlyList<string> accents, ChartRect plot, AxisScale yScale, int targetTicks)
        {
            var xValues = new List<double>();
            foreach (var s in model.Series)
            {
                if (s.XValues == null) continue;
                foreach (var v in s.XValues) if (v.HasValue) xValues.Add(v.Value);
            }

            // With no x values at all the points have no position; fall back to the index so the
            // series is still visible rather than silently absent.
            bool haveX = xValues.Count > 0;
            var xScale = AxisScale.ForValues(
                haveX ? xValues : Enumerable.Range(0, model.PointCount).Select(i => (double)i),
                model.XAxis, plot.X, plot.Right, allowBaselineLift: true, targetTicks);

            if (!haveX)
                DocxDiagnostics.Report("ChartLayout.Scatter",
                    "the scatter series has no c:xVal cache; its points were spaced by index");

            // The x axis's own tick labels. LayoutAxisChart draws the value axis's ticks but has
            // no x scale to draw these from; without them the bottom of the plot would carry
            // evenly-spaced CATEGORY labels instead — see the guard on
            // LayoutCategoryLabels. The band LayoutAxisChart reserved for category labels is what
            // these occupy.
            if (!(model.XAxis?.LabelsHidden ?? false) && !(model.XAxis?.Deleted ?? false))
                foreach (double t in xScale.MajorTicks())
                {
                    double d = xScale.Map(t);
                    if (double.IsNaN(d)) continue;
                    frame.Items.Add(new TextItem
                    {
                        Text = ExcelNumberFormat.Format(t, model.XAxis?.NumberFormat, xScale.Step),
                        X = d,
                        Y = plot.Y - TickLength - LabelSizePt,
                        SizePt = LabelSizePt,
                        Anchor = TextAnchor.Centre,
                    });
                }

            // Bubble sizing is normalised across EVERY series, because a bubble chart's sizes are
            // comparable between series — scaling each series against its own maximum would make
            // the smallest bubble of a large series match the largest of a small one.
            double maxBubble = 0;
            if (model.Kind == ChartKind.Bubble)
                foreach (var s in model.Series)
                {
                    if (s.BubbleSizes == null) continue;
                    foreach (var b in s.BubbleSizes)
                        if (b.HasValue && b.Value > maxBubble) maxBubble = b.Value;
                }

            // The largest bubble spans this fraction of the plot's smaller side. A bubble chart
            // whose biggest point covered the plot would hide every other point behind it.
            double maxRadius = Math.Min(plot.Width, plot.Height) * 0.5 * 0.25;

            bool reportedMissingSize = false;

            for (int s = 0; s < model.Series.Count; s++)
            {
                var series = model.Series[s];
                string colour = series.ColorHex ?? PaletteAt(accents, s);
                int n = series.Values?.Length ?? 0;

                for (int i = 0; i < n; i++)
                {
                    double? y = series.Values[i];
                    if (!y.HasValue) continue;

                    double? xv = haveX && series.XValues != null && i < series.XValues.Length
                        ? series.XValues[i]
                        : i;
                    if (!xv.HasValue) continue;

                    double px = xScale.Map(xv.Value), py = yScale.Map(y.Value);
                    if (double.IsNaN(px) || double.IsNaN(py)) continue;

                    double size = ScatterMarkerSize;
                    if (model.Kind == ChartKind.Bubble)
                    {
                        double? bs = series.BubbleSizes != null && i < series.BubbleSizes.Length
                            ? series.BubbleSizes[i]
                            : null;

                        if (!bs.HasValue || maxBubble <= 0)
                        {
                            // A bubble with no size is not a bubble; drawing it at the scatter size
                            // would silently misreport the third dimension as "smallest".
                            if (!reportedMissingSize)
                            {
                                DocxDiagnostics.Report("ChartLayout.Bubble",
                                    "a bubble point has no c:bubbleSize; it was drawn at the " +
                                    "minimum radius, which understates it");
                                reportedMissingSize = true;
                            }
                            size = MinBubbleDiameter;
                        }
                        else
                        {
                            // c:sizeRepresents defaults to "area" (ECMA-376 §21.2.2.180), so the
                            // RADIUS goes as the square root. Scaling the radius linearly instead
                            // is the classic bubble-chart exaggeration: a value twice as large
                            // would cover four times the area.
                            double r = maxRadius * Math.Sqrt(Math.Max(0, bs.Value) / maxBubble);
                            size = Math.Max(MinBubbleDiameter, r * 2);
                        }
                    }

                    frame.Items.Add(new MarkerItem
                    {
                        X = px, Y = py, Size = size, FillHex = colour, Round = true,
                    });
                }
            }
        }

        /// <summary>Diameter of a scatter marker, in points.</summary>
        private const double ScatterMarkerSize = 5;

        /// <summary>
        /// Floor for a bubble's diameter. A zero or missing size must still be locatable on the
        /// plot — an invisible point reads as missing data rather than as a small value.
        /// </summary>
        private const double MinBubbleDiameter = 2;

        private static void LayoutLines(ChartModel model, ChartFrame frame,
            IReadOnlyList<string> accents, ChartRect plot, AxisScale scale, int pointCount,
            bool area = false)
        {
            if (pointCount <= 0) return;

            bool between = model.CategoryAxis?.CrossBetween ?? true;
            double band = plot.Width / Math.Max(1, pointCount);

            for (int s = 0; s < model.Series.Count; s++)
            {
                var series = model.Series[s];
                string colour = series.ColorHex ?? PaletteAt(accents, s);

                PolylineItem current = null;
                double lastX = plot.X;

                // Closes the area subpath currently being built back down to the baseline, at the
                // last point it reached. Called wherever `current` is ABANDONED, not only after
                // the loop: a blank cell starts a new PolylineItem, so closing the final one only
                // left every earlier subpath with its opening baseline point and no closing one —
                // and `h f` then filled the triangle from the last data point straight back to
                // that opening point instead of the region under the data.
                void CloseArea()
                {
                    if (area && current != null && current.Points.Count > 1)
                        current.Points.Add((lastX, BaselineOf(scale, plot)));
                }

                for (int i = 0; i < pointCount; i++)
                {
                    double? raw = i < (series.Values?.Length ?? 0) ? series.Values[i] : null;

                    if (!raw.HasValue)
                    {
                        // "gap" (the default) starts a new subpath; "span" simply keeps going, so
                        // the line joins across the hole; "zero" plots it as zero.
                        if (model.DisplayBlanksAs == "span") continue;
                        if (model.DisplayBlanksAs == "zero") raw = 0;
                        else { CloseArea(); current = null; continue; }
                    }

                    double y = scale.Map(raw.Value);
                    if (double.IsNaN(y)) { CloseArea(); current = null; continue; }

                    double x = between
                        ? plot.X + (i + 0.5) * band
                        : plot.X + (pointCount > 1 ? i * plot.Width / (pointCount - 1) : plot.Width / 2);

                    if (current == null)
                    {
                        current = new PolylineItem { StrokeHex = colour };
                        if (area) current.FillHex = colour;
                        frame.Items.Add(current);

                        // An area's outline closes down to the baseline, so the polygon it fills is
                        // the region between the data and zero — not the data alone.
                        if (area) current.Points.Add((x, BaselineOf(scale, plot)));
                    }
                    current.Points.Add((x, y));
                    lastX = x;
                }

                // Close the area back down to the baseline at the last point drawn.
                CloseArea();
            }
        }

        private static double BaselineOf(AxisScale scale, ChartRect plot)
        {
            double b = scale.Map(Clamp(0, scale.Min, scale.Max));
            return double.IsNaN(b) ? plot.Y : b;
        }

        /// <summary>
        /// Value labels above each bar or point. Drawn last so a bar cannot cover its own number.
        /// </summary>
        private static void LayoutValueLabels(ChartModel model, ChartFrame frame, ChartRect plot,
            AxisScale scale, int pointCount, bool horizontal)
        {
            if (pointCount <= 0) return;

            double span = horizontal ? plot.Height : plot.Width;
            double catLo = horizontal ? plot.Y : plot.X;
            double band = span / pointCount;
            bool stacked = model.Grouping == Grouping.Stacked
                        || model.Grouping == Grouping.PercentStacked;
            int lanes = stacked ? 1 : model.Series.Count;

            double barW = band / (lanes + model.GapWidth / 100.0);
            double advance = barW * (1 - model.Overlap / 100.0);
            double clusterW = barW + (lanes - 1) * advance;

            // A stacked bar segment is NOT drawn at scale.Map(raw): LayoutBars maps the running
            // cumulative position, and a percent-stacked chart maps each value's fraction of its
            // category total onto a 0..1 axis. Reading the raw value here placed a percent-stacked
            // label several plot-heights above the plot, where ChartPainter's clip discarded it —
            // the chart shipped with no data labels at all. The accumulation below is the same one
            // LayoutBars performs, in the same series order, so the label lands on the segment it
            // annotates. Only Bar stacks: LayoutLines maps the raw value for every grouping, so
            // stacking a line/area chart's labels would recreate the disagreement in reverse.
            bool stackedBars = stacked && model.Kind == ChartKind.Bar;
            var positiveTotals = new double[pointCount];
            var negativeTotals = new double[pointCount];

            for (int s = 0; s < model.Series.Count; s++)
            {
                var series = model.Series[s];
                for (int i = 0; i < pointCount; i++)
                {
                    double? raw = i < (series.Values?.Length ?? 0) ? series.Values[i] : null;
                    if (!raw.HasValue) continue;

                    double pos;
                    if (stackedBars)
                    {
                        double v = raw.Value;
                        if (model.Grouping == Grouping.PercentStacked)
                        {
                            double total = CategoryAbsTotal(model, i);
                            v = total > 0 ? v / total : 0;
                        }

                        double start = v >= 0 ? positiveTotals[i] : negativeTotals[i];
                        double end = start + v;
                        if (v >= 0) positiveTotals[i] = end; else negativeTotals[i] = end;

                        double from = scale.Map(start), to = scale.Map(end);
                        if (double.IsNaN(from) || double.IsNaN(to)) continue;
                        pos = (from + to) / 2;   // the drawn segment's own middle
                    }
                    else
                    {
                        pos = scale.Map(raw.Value);
                        if (double.IsNaN(pos)) continue;
                    }

                    double catCentre = catLo + (i + 0.5) * band;
                    double lane = model.Kind == ChartKind.Bar
                        ? catCentre - clusterW / 2 + (stacked ? 0 : s * advance) + barW / 2
                        : catCentre;

                    // The value shown is always the value the file states, not the stacked
                    // coordinate it was drawn at — Excel labels a 100%-stacked segment with its
                    // own number, not with the fraction.
                    string text = ExcelNumberFormat.Format(raw.Value,
                        model.ValueAxis?.NumberFormat, scale.Step);

                    frame.Items.Add(horizontal
                        ? new TextItem
                        {
                            Text = text,
                            X = stackedBars ? pos : pos + Gap,
                            Y = lane - LabelSizePt * 0.35,
                            SizePt = LabelSizePt,
                            Anchor = stackedBars ? TextAnchor.Centre : TextAnchor.Left,
                        }
                        : new TextItem
                        {
                            Text = text,
                            X = lane,
                            Y = stackedBars ? pos - LabelSizePt * 0.35 : pos + Gap,
                            SizePt = LabelSizePt,
                            Anchor = TextAnchor.Centre,
                        });
                }
            }
        }

        private static void LayoutCategoryLabels(ChartModel model, ChartFrame frame, ChartRect plot,
            int pointCount, bool horizontal, Func<string, double, double> measure)
        {
            if (pointCount <= 0) return;

            double span = horizontal ? plot.Height : plot.Width;
            double catLo = horizontal ? plot.Y : plot.X;
            double band = span / pointCount;

            double widest = 0;
            for (int i = 0; i < pointCount; i++)
                widest = Math.Max(widest, measure(CategoryAt(model, i), LabelSizePt));

            // DrawingML states rotation clockwise, in 60000ths of a degree; PDF's text matrix is
            // counter-clockwise. Getting the sign wrong tilts every label the wrong way, which
            // looks deliberate and therefore survives review — hence one named conversion.
            double rotationDeg = PdfRotationDegrees(model.CategoryAxis?.LabelRotation ?? 0);

            // Skip every Nth rather than truncate. An automatic converter cannot ask the user, and
            // every drawn label staying whole is the more legible failure — a half-word category is
            // worse than an absent one. A stated rotation is honoured instead of skipping, since
            // the file has already decided how to make them fit.
            int skip = 1;
            if (!horizontal && Math.Abs(rotationDeg) < 1e-6 && widest > band && band > 0)
                skip = SaturatingCast.ToInt(Math.Ceiling(widest / band));

            for (int i = 0; i < pointCount; i += skip)
            {
                double centre = catLo + (i + 0.5) * band;
                frame.Items.Add(horizontal
                    ? new TextItem
                    {
                        Text = CategoryAt(model, i),
                        X = plot.X - TickLength - Gap,
                        Y = centre - LabelSizePt * 0.35,
                        SizePt = LabelSizePt,
                        Anchor = TextAnchor.Right,
                    }
                    : new TextItem
                    {
                        Text = CategoryAt(model, i),
                        X = centre,
                        Y = plot.Y - TickLength - LabelSizePt,
                        SizePt = LabelSizePt,
                        Anchor = TextAnchor.Centre,
                        RotationDeg = rotationDeg,
                    });
            }
        }

        /// <summary>
        /// DrawingML's <c>@rot</c> (clockwise, 60000ths of a degree) as PDF's counter-clockwise
        /// degrees. Named rather than inlined because the sign is the easy thing to get backwards
        /// and the result still looks intentional.
        /// </summary>
        internal static double PdfRotationDegrees(double drawingMlRot)
            => -drawingMlRot / 60000.0;

        // ------------------------------------------------------------------ pie

        private static void LayoutPieChart(ChartModel model, ChartFrame frame,
            IReadOnlyList<string> accents, Func<string, double, double> measure, ChartRect area)
        {
            // Excel plots only the first series of a pie; more rings need a doughnut.
            var series = model.Series.FirstOrDefault();
            if (series?.Values == null) return;

            double total = 0;
            foreach (var v in series.Values) if (v.HasValue) total += Math.Abs(v.Value);
            if (total <= 0) return;

            double radius = Math.Min(area.Width, area.Height) / 2 * 0.9;
            double cx = area.CentreX, cy = area.CentreY;
            double inner = model.Kind == ChartKind.Doughnut
                ? radius * Math.Max(0, Math.Min(90, model.HoleSize)) / 100.0
                : 0;

            // Start at twelve o'clock and go clockwise, as Excel does. In PDF's counter-clockwise
            // angle convention that is a start of +90° with a negative sweep.
            double angle = 90;
            for (int i = 0; i < series.Values.Length; i++)
            {
                double? raw = series.Values[i];
                if (!raw.HasValue) continue;

                double fraction = Math.Abs(raw.Value) / total;
                double sweep = fraction * 360.0;
                if (sweep <= 0) continue;

                string colour = series.PointColors != null && i < series.PointColors.Length
                                && series.PointColors[i] != null
                    ? series.PointColors[i]
                    : PaletteAt(accents, i);   // a pie cycles per POINT, not per series

                frame.Items.Add(new SliceItem
                {
                    CentreX = cx, CentreY = cy, Radius = radius, InnerRadius = inner,
                    StartAngle = angle, SweepAngle = -sweep, FillHex = colour,
                });

                if (model.ShowPercentLabels || model.ShowValueLabels)
                {
                    double mid = (angle - sweep / 2) * Math.PI / 180.0;
                    double labelR = inner > 0 ? (radius + inner) / 2 : radius * 0.75;
                    string text = model.ShowPercentLabels
                        ? ExcelNumberFormat.Format(fraction, "0%", 0.01)
                        : ExcelNumberFormat.General(raw.Value, 1);

                    frame.Items.Add(new TextItem
                    {
                        Text = text,
                        X = cx + labelR * Math.Cos(mid),
                        Y = cy + labelR * Math.Sin(mid) - LabelSizePt * 0.35,
                        SizePt = LabelSizePt,
                        Anchor = TextAnchor.Centre,
                        ColorHex = "FFFFFF",
                    });
                }

                angle -= sweep;
            }
        }

        // ------------------------------------------------------------------ legend

        private static List<(string name, string colour)> LegendEntries(ChartModel model,
            IReadOnlyList<string> accents)
        {
            var entries = new List<(string, string)>();

            // A pie's legend lists its categories, not its one series.
            if (model.Kind == ChartKind.Pie || model.Kind == ChartKind.Doughnut)
            {
                var series = model.Series.FirstOrDefault();
                int n = series?.Values?.Length ?? 0;
                for (int i = 0; i < n; i++)
                {
                    string colour = series.PointColors != null && i < series.PointColors.Length
                                    && series.PointColors[i] != null
                        ? series.PointColors[i]
                        : PaletteAt(accents, i);
                    entries.Add((CategoryAt(model, i), colour));
                }
                return entries;
            }

            for (int s = 0; s < model.Series.Count; s++)
                entries.Add((model.Series[s].Name ?? "Series " + (s + 1),
                    model.Series[s].ColorHex ?? PaletteAt(accents, s)));
            return entries;
        }

        private static void DrawLegend(ChartFrame frame, string position,
            List<(string name, string colour)> entries, Func<string, double, double> measure,
            double width, double height, double legendWidth, double legendHeight,
            double left, double right, double bottom, double top)
        {
            double lineHeight = LabelSizePt * 1.25;

            if (position == "r" || position == "l")
            {
                double x = position == "r" ? right + Gap : left - legendWidth + Gap;
                double y = (top + bottom) / 2 + entries.Count * lineHeight / 2 - lineHeight;

                foreach (var (name, colour) in entries)
                {
                    if (y < bottom) break;
                    frame.Items.Add(new RectItem
                    {
                        Box = new ChartRect(x, y, LegendSwatch, LegendSwatch),
                        FillHex = colour,
                    });
                    frame.Items.Add(new TextItem
                    {
                        Text = name, X = x + LegendSwatch + Gap, Y = y, SizePt = LabelSizePt,
                    });
                    y -= lineHeight;
                }
                return;
            }

            // Top or bottom: one row, left to right, dropping what does not fit.
            double totalWidth = entries.Sum(e => LegendSwatch + Gap + measure(e.name, LabelSizePt) + Gap * 2);
            double cursor = Math.Max(0, (width - totalWidth) / 2);
            double rowY = position == "b" ? bottom - legendHeight + Gap : top + Gap;

            foreach (var (name, colour) in entries)
            {
                double entryWidth = LegendSwatch + Gap + measure(name, LabelSizePt) + Gap * 2;
                if (cursor + entryWidth > width) break;

                frame.Items.Add(new RectItem
                {
                    Box = new ChartRect(cursor, rowY, LegendSwatch, LegendSwatch),
                    FillHex = colour,
                });
                frame.Items.Add(new TextItem
                {
                    Text = name, X = cursor + LegendSwatch + Gap, Y = rowY, SizePt = LabelSizePt,
                });
                cursor += entryWidth;
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        /// The numbers the value axis has to cover. For a stacked chart that is the per-category
        /// running totals, kept separately by sign — the axis must reach both ends of a category
        /// holding positive and negative values.
        /// </summary>
        private static IEnumerable<double> StackedTotalsOrValues(ChartModel model)
        {
            if (model.Grouping == Grouping.PercentStacked)
            {
                yield return 0;
                yield return 1;
                bool anyNegative = model.Series.Any(s => s.Values != null
                    && s.Values.Any(v => v.HasValue && v.Value < 0));
                if (anyNegative) yield return -1;
                yield break;
            }

            if (model.Grouping != Grouping.Stacked)
            {
                foreach (var s in model.Series)
                {
                    if (s.Values == null) continue;
                    foreach (var v in s.Values) if (v.HasValue) yield return v.Value;
                }
                yield break;
            }

            int n = model.PointCount;
            for (int i = 0; i < n; i++)
            {
                double pos = 0, neg = 0;
                foreach (var s in model.Series)
                {
                    double? v = i < (s.Values?.Length ?? 0) ? s.Values[i] : null;
                    if (!v.HasValue) continue;
                    if (v.Value >= 0) pos += v.Value; else neg += v.Value;
                }
                yield return pos;
                yield return neg;
            }
        }

        private static double CategoryAbsTotal(ChartModel model, int index)
        {
            double total = 0;
            foreach (var s in model.Series)
            {
                double? v = index < (s.Values?.Length ?? 0) ? s.Values[index] : null;
                if (v.HasValue) total += Math.Abs(v.Value);
            }
            return total;
        }

        private static string CategoryAt(ChartModel model, int index)
            => index < model.Categories.Count && !string.IsNullOrEmpty(model.Categories[index])
                ? model.Categories[index]
                : (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

        private static double Clamp(double v, double min, double max)
            => v < min ? min : v > max ? max : v;
    }
}
