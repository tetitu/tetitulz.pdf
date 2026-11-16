// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Charts
{
    /// <summary>
    /// Reads a <c>c:chartSpace</c> into a <see cref="ChartModel"/>.
    ///
    /// <para>
    /// <b>The chart carries its own data.</b> <c>c:numCache</c> / <c>c:strCache</c> hold the plotted
    /// values, so nothing here opens the embedded workbook or evaluates a formula — the whole input
    /// is a small immutable value tree. That is what collapses the hard part of chart support to
    /// arithmetic, and why a cache-less chart is an honest "cannot draw" rather than a half-drawn
    /// guess.
    /// </para>
    ///
    /// <para>
    /// Walks by local name, so <c>c14</c> / <c>cx</c> extension elements and SDK class coverage
    /// cannot affect it. This is the only file in the folder that references OpenXml.
    /// </para>
    /// </summary>
    internal static class ChartPartReader
    {
        private static readonly Dictionary<string, (ChartKind kind, bool threeD)> PlotElements =
            new Dictionary<string, (ChartKind, bool)>(StringComparer.Ordinal)
            {
                ["barChart"] = (ChartKind.Bar, false),
                ["bar3DChart"] = (ChartKind.Bar, true),
                ["lineChart"] = (ChartKind.Line, false),
                ["line3DChart"] = (ChartKind.Line, true),
                ["pieChart"] = (ChartKind.Pie, false),
                ["pie3DChart"] = (ChartKind.Pie, true),
                ["doughnutChart"] = (ChartKind.Doughnut, false),
                ["areaChart"] = (ChartKind.Area, false),
                ["area3DChart"] = (ChartKind.Area, true),
                ["scatterChart"] = (ChartKind.Scatter, false),
                ["bubbleChart"] = (ChartKind.Bubble, false),
            };

        internal static bool TryRead(ChartPart part, DrawingColorSource colors, out ChartModel model)
        {
            model = null;

            var chartSpace = part?.ChartSpace;
            var chart = chartSpace?.ChildElements.FirstOrDefault(e => e.LocalName == "chart");
            var plotArea = chart?.ChildElements.FirstOrDefault(e => e.LocalName == "plotArea");
            if (plotArea == null) return false;

            var m = new ChartModel();
            ReadTitle(chart, m);
            ReadLegend(chart, m);

            // c:dispBlanksAs is a child of c:chart, beside c:plotArea and c:legend. Nothing read
            // it, so ChartModel.DisplayBlanksAs stayed at its "gap" default and two of the three
            // branches in LayoutLines were unreachable: a chart stating "span" (join the line
            // across the hole) or "zero" was drawn broken in two, and an isolated point between
            // two blanks was dropped by ChartPainter's two-point minimum.
            m.DisplayBlanksAs = Val(chart.ChildElements
                .FirstOrDefault(e => e.LocalName == "dispBlanksAs")) ?? "gap";

            // A plot area may hold several chart groups — a bar group and a line group over one
            // category axis, say. The first is drawn; the others are reported, since their series
            // are not.
            var groups = plotArea.ChildElements.Where(e => PlotElements.ContainsKey(e.LocalName)).ToList();
            var plot = groups.FirstOrDefault();
            if (groups.Count > 1)
                DocxDiagnostics.Report("ChartPartReader",
                    $"the chart plots {groups.Count} groups ({string.Join(", ", groups.Select(g => g.LocalName))}); " +
                    $"only the first, {plot.LocalName}, is drawn, and the series of the others are not.");
            if (plot == null)
            {
                // Name what we found, so the diagnostic says which kind is missing rather than
                // "unsupported chart".
                var unknown = plotArea.ChildElements.FirstOrDefault(
                    e => e.LocalName.EndsWith("Chart", StringComparison.Ordinal));
                m.KindName = unknown?.LocalName ?? "(no plot element)";
                model = m;
                return true;   // a model with Kind == Unsupported; the caller reserves and reports
            }

            var (kind, threeD) = PlotElements[plot.LocalName];
            m.Kind = kind;
            m.KindName = plot.LocalName;
            m.WasThreeDimensional = threeD;

            ReadPlotOptions(plot, m);
            ReadSeries(plot, m, colors);
            ReadAxes(plotArea, m, plot.ChildElements.Where(e => e.LocalName == "axId").Select(Val).Where(v => v != null).ToList());

            model = m;
            return true;
        }

        // ------------------------------------------------------------------ chart-level

        private static void ReadTitle(OpenXmlElement chart, ChartModel m)
        {
            m.AutoTitleDeleted = BoolVal(chart.ChildElements
                .FirstOrDefault(e => e.LocalName == "autoTitleDeleted"), false);

            var title = chart.ChildElements.FirstOrDefault(e => e.LocalName == "title");
            if (title != null) m.Title = RichText(title);
        }

        private static void ReadLegend(OpenXmlElement chart, ChartModel m)
        {
            var legend = chart.ChildElements.FirstOrDefault(e => e.LocalName == "legend");
            if (legend == null) return;

            m.LegendPosition = Val(legend.ChildElements
                .FirstOrDefault(e => e.LocalName == "legendPos")) ?? "r";
        }

        private static void ReadPlotOptions(OpenXmlElement plot, ChartModel m)
        {
            string barDir = Val(plot.ChildElements.FirstOrDefault(e => e.LocalName == "barDir"));
            m.BarDir = string.Equals(barDir, "bar", StringComparison.Ordinal)
                ? BarDirection.Bar
                : BarDirection.Column;

            m.Grouping = Val(plot.ChildElements.FirstOrDefault(e => e.LocalName == "grouping")) switch
            {
                "stacked" => Grouping.Stacked,
                "percentStacked" => Grouping.PercentStacked,
                "standard" => Grouping.Standard,
                _ => Grouping.Clustered,
            };

            // Clamped on read. These three are percentages with defined ranges (ECMA-376
            // §21.2.2.55 / §21.2.2.131 / §21.2.2.83), and NumVal accepts any double the attribute
            // spells — NumberStyles.Float takes a leading sign and the NaN symbol. A c:gapWidth of
            // −100 makes `lanes + GapWidth / 100.0` exactly zero, so every bar width comes out
            // +Infinity and ChartPainter's finite guard drops the whole series: a chart drawn as
            // axes and gridlines around an empty plot, with nothing reported and no way to tell it
            // from a chart whose data really is missing.
            m.GapWidth = Clamped(plot, "gapWidth", 150, 0, 500);
            m.Overlap = Clamped(plot, "overlap", m.Grouping == Grouping.Clustered ? 0 : 100, -100, 100);
            m.HoleSize = Clamped(plot, "holeSize", 50, 10, 90);

            var dLbls = plot.ChildElements.FirstOrDefault(e => e.LocalName == "dLbls");
            if (dLbls != null)
            {
                m.ShowPercentLabels = BoolVal(dLbls.ChildElements
                    .FirstOrDefault(e => e.LocalName == "showPercent"), false);
                m.ShowValueLabels = BoolVal(dLbls.ChildElements
                    .FirstOrDefault(e => e.LocalName == "showVal"), false);
            }
        }

        // ------------------------------------------------------------------ series

        private static void ReadSeries(OpenXmlElement plot, ChartModel m, DrawingColorSource colors)
        {
            // Each series allocates its own point arrays up to MaxChartDataPoints, so the series count
            // multiplies that per-series cap — twenty thousand empty series each declaring a hundred
            // thousand points is tens of gigabytes before decimation runs. The series past the cap are
            // dropped and the drop reported.
            int seriesRead = 0;
            // And every series' caches draw on one chart-wide budget of points: a cap on each array
            // and one on the series count still let a thousand series of three hundred-thousand-point
            // caches allocate 4.8 GB, each declaring its points and supplying one.
            long pointBudget = 4L * PdfLimits.MaxChartDataPoints;
            foreach (var ser in plot.ChildElements.Where(e => e.LocalName == "ser"))
            {
                if (seriesRead++ >= PdfLimits.MaxChartSeries)
                {
                    DocxDiagnostics.Report("ChartPartReader",
                        $"the chart declares more than {PdfLimits.MaxChartSeries} series; the rest were not read.");
                    break;
                }
                var data = new SeriesData
                {
                    Name = SeriesName(ser),
                    ColorHex = colors?.SolidFillHexOf(ser.ChildElements
                        .FirstOrDefault(e => e.LocalName == "spPr")),
                };

                var val = ser.ChildElements.FirstOrDefault(e => e.LocalName == "val")
                       ?? ser.ChildElements.FirstOrDefault(e => e.LocalName == "yVal");
                data.Values = ReadNumericCache(val, ref pointBudget);

                // A scatter or bubble point needs its own x; every other kind takes x from the band
                // index.
                var xVal = ser.ChildElements.FirstOrDefault(e => e.LocalName == "xVal");
                if (xVal != null) data.XValues = ReadNumericCache(xVal, ref pointBudget);

                // The bubble's third dimension. Read unconditionally rather than gated on the kind:
                // the element only exists on a bubble series, so the presence test is the same
                // check, and this keeps the reader free of a kind switch it would otherwise need.
                var bubbleSize = ser.ChildElements.FirstOrDefault(e => e.LocalName == "bubbleSize");
                if (bubbleSize != null) data.BubbleSizes = ReadNumericCache(bubbleSize, ref pointBudget);

                data.PointColors = ReadPointColors(ser, colors);
                m.Series.Add(data);

                if (m.Categories.Count == 0)
                {
                    var cat = ser.ChildElements.FirstOrDefault(e => e.LocalName == "cat")
                           ?? ser.ChildElements.FirstOrDefault(e => e.LocalName == "xVal");
                    foreach (string s in ReadStringCache(cat)) m.Categories.Add(s);
                }
            }
        }

        private static string[] ReadPointColors(OpenXmlElement ser, DrawingColorSource colors)
        {
            var dPts = ser.ChildElements.Where(e => e.LocalName == "dPt").ToList();
            if (dPts.Count == 0 || colors == null) return null;

            int max = 0;
            var byIndex = new Dictionary<int, string>();
            foreach (var dPt in dPts)
            {
                int idx = SaturatingCast.ToInt(NumVal(dPt, "idx", -1));
                if (idx < 0) continue;
                string hex = colors.SolidFillHexOf(dPt.ChildElements
                    .FirstOrDefault(e => e.LocalName == "spPr"));
                if (hex == null) continue;
                byIndex[idx] = hex;
                if (idx + 1 > max) max = idx + 1;
            }
            if (max == 0) return null;

            // `max` is one past the largest `idx` ATTRIBUTE, so a single
            // <c:dPt><c:idx val="2000000000"/></c:dPt> sizes this array — the dictionary above is
            // bounded by the real element count, but this is not.
            int requested = max;
            max = Math.Min(requested, PdfLimits.MaxChartDataPoints);
            if (max < requested)
                DocxDiagnostics.Report("ChartPartReader",
                    $"chart point-colour list indexes up to {requested} data points; " +
                    $"only the first {max} were kept.");

            var result = new string[max];
            foreach (var kvp in byIndex)
                if (kvp.Key < max) result[kvp.Key] = kvp.Value;
            return result;
        }

        private static string SeriesName(OpenXmlElement ser)
        {
            var tx = ser.ChildElements.FirstOrDefault(e => e.LocalName == "tx");
            if (tx == null) return null;

            var cached = ReadStringCache(tx);
            if (cached.Count > 0 && !string.IsNullOrEmpty(cached[0])) return cached[0];

            // A literal <c:v> directly under <c:tx>.
            var v = tx.ChildElements.FirstOrDefault(e => e.LocalName == "v");
            return v?.InnerText;
        }

        /// <summary>
        /// Reads a numeric cache into an array indexed by <c>c:pt/@idx</c>.
        ///
        /// <para>
        /// <b>The points are sparse.</b> A blank cell simply has no <c>c:pt</c>, so appending them
        /// in document order slides every later value one place left — the line still draws, it
        /// just draws the wrong data, and it never breaks where the gap is. Indexing by <c>@idx</c>
        /// is the whole fix.
        /// </para>
        /// </summary>
        private static double?[] ReadNumericCache(OpenXmlElement holder, ref long pointBudget)
        {
            var cache = FindCache(holder, "numCache", "numLit");
            if (cache == null) return Array.Empty<double?>();

            var points = cache.ChildElements.Where(e => e.LocalName == "pt").ToList();
            int declared = SaturatingCast.ToInt(NumVal(cache, "ptCount", 0));
            int maxIdx = 0;
            foreach (var pt in points)
            {
                int idx = SaturatingCast.ToInt(Attr(pt, "idx", -1));
                if (idx + 1 > maxIdx) maxIdx = idx + 1;
            }

            // Both inputs to this size come from the file: `ptCount` is declared outright, and
            // `maxIdx` is the largest `idx` attribute seen. Either can ask for billions of elements
            // while the file supplies no actual points — a crafted <c:ptCount val="2000000000"/> is
            // ~32 GB of double? before a single value is read.
            int requested = Math.Max(declared, maxIdx);
            if (requested <= 0) return Array.Empty<double?>();

            int count = (int)Math.Min(Math.Min(requested, PdfLimits.MaxChartDataPoints), Math.Max(0, pointBudget));
            pointBudget -= count;

            // Reported FROM the truncation, not alongside it. Writing the report against
            // `requested > cap` instead makes the message and the cap two independent statements —
            // remove the cap and the message still fires, so a test asserting the message passes
            // over an unbounded allocation. Deriving it from `count < requested` means the two
            // cannot disagree.
            if (count < requested)
                DocxDiagnostics.Report("ChartPartReader",
                    $"chart series declares {requested} data points; only the first {count} were read.");

            var values = new double?[count];
            foreach (var pt in points)
            {
                int idx = SaturatingCast.ToInt(Attr(pt, "idx", -1));
                if (idx < 0 || idx >= count) continue;

                string text = pt.ChildElements.FirstOrDefault(e => e.LocalName == "v")?.InnerText;
                if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                    && !double.IsNaN(v) && !double.IsInfinity(v))
                    values[idx] = v;
                // Anything else — #N/A, an empty <c:v>, unparseable text — stays a blank.
            }
            return values;
        }

        private static List<string> ReadStringCache(OpenXmlElement holder)
        {
            var result = new List<string>();
            var cache = FindCache(holder, "strCache", "strLit")
                     ?? FindCache(holder, "numCache", "numLit");
            if (cache == null) return result;

            var byIndex = new Dictionary<int, string>();
            int max = 0;
            foreach (var pt in cache.ChildElements.Where(e => e.LocalName == "pt"))
            {
                int idx = SaturatingCast.ToInt(Attr(pt, "idx", -1));
                if (idx < 0) continue;
                byIndex[idx] = pt.ChildElements.FirstOrDefault(e => e.LocalName == "v")?.InnerText ?? "";
                if (idx + 1 > max) max = idx + 1;
            }

            // `max` is the largest `idx` attribute, a file-supplied number: a single
            // <c:pt idx="2000000000"/> of ~45 bytes would otherwise fill the list with two billion
            // entries. Clamped as ReadNumericCache clamps its array, and reported from the truncation.
            int count = Math.Min(max, PdfLimits.MaxChartDataPoints);
            if (count < max)
                DocxDiagnostics.Report("ChartPartReader",
                    $"chart labels declare {max} entries; only the first {count} were read.");

            for (int i = 0; i < count; i++)
                result.Add(byIndex.TryGetValue(i, out string s) ? s : "");
            return result;
        }

        // A cache sits under c:numRef / c:strRef / c:multiLvlStrRef, or is a literal sibling.
        private static OpenXmlElement FindCache(OpenXmlElement holder, string cacheName, string literalName)
        {
            if (holder == null) return null;

            var literal = holder.ChildElements.FirstOrDefault(e => e.LocalName == literalName);
            if (literal != null) return literal;

            foreach (var refEl in holder.ChildElements)
            {
                var cache = refEl.ChildElements.FirstOrDefault(e => e.LocalName == cacheName);
                if (cache != null) return cache;

                // c:multiLvlStrRef nests one c:lvl per level; the innermost is the leaf label.
                var multi = refEl.ChildElements.FirstOrDefault(e => e.LocalName == "multiLvlStrCache");
                var lvl = multi?.ChildElements.LastOrDefault(e => e.LocalName == "lvl");
                if (lvl != null) return lvl;
            }
            return null;
        }

        // ------------------------------------------------------------------ axes

        /// <summary>
        /// The axes of <paramref name="plotArea"/> into <paramref name="m"/>: a value axis (a scatter's
        /// first is its x axis), a category or date axis. An axis the drawn group does not name in its
        /// <c>c:axId</c>s (<paramref name="groupAxisIds"/>) is another group's — a secondary value axis,
        /// whose bounds would otherwise replace the primary's — and is passed over; a group naming none
        /// takes every axis of the plot area, in order.
        /// </summary>
        private static void ReadAxes(OpenXmlElement plotArea, ChartModel m, List<string> groupAxisIds)
        {
            // A scatter chart writes two c:valAx: the first is its x axis, the second its y.
            bool xAxisTaken = !m.HasValueXAxis;

            foreach (var ax in plotArea.ChildElements)
            {
                AxisSpec spec;
                if (ax.LocalName != "valAx" && ax.LocalName != "catAx" && ax.LocalName != "dateAx") continue;
                if (groupAxisIds.Count > 0)
                {
                    string id = Val(ax.ChildElements.FirstOrDefault(e => e.LocalName == "axId"));
                    if (id == null || !groupAxisIds.Contains(id)) continue;
                }
                if (ax.LocalName == "valAx")
                {
                    if (!xAxisTaken) { spec = m.XAxis; xAxisTaken = true; }
                    else spec = m.ValueAxis;
                }
                else if (ax.LocalName == "catAx" || ax.LocalName == "dateAx") spec = m.CategoryAxis;
                else continue;

                var scaling = ax.ChildElements.FirstOrDefault(e => e.LocalName == "scaling");
                if (scaling != null)
                {
                    spec.Min = OptionalNum(scaling, "min");
                    spec.Max = OptionalNum(scaling, "max");
                    spec.LogBase = OptionalNum(scaling, "logBase");
                    spec.Reversed = string.Equals(
                        Val(scaling.ChildElements.FirstOrDefault(e => e.LocalName == "orientation")),
                        "maxMin", StringComparison.Ordinal);
                }

                spec.MajorUnit = OptionalNum(ax, "majorUnit");
                spec.Deleted = BoolVal(ax.ChildElements.FirstOrDefault(e => e.LocalName == "delete"), false);
                spec.MajorGridlines = ax.ChildElements.Any(e => e.LocalName == "majorGridlines");
                spec.LabelsHidden = string.Equals(
                    Val(ax.ChildElements.FirstOrDefault(e => e.LocalName == "tickLblPos")),
                    "none", StringComparison.Ordinal);

                string crossBetween = Val(ax.ChildElements.FirstOrDefault(e => e.LocalName == "crossBetween"));
                if (crossBetween != null)
                    spec.CrossBetween = !string.Equals(crossBetween, "midCat", StringComparison.Ordinal);

                var numFmt = ax.ChildElements.FirstOrDefault(e => e.LocalName == "numFmt");
                spec.NumberFormat = Attr(numFmt, "formatCode");

                var title = ax.ChildElements.FirstOrDefault(e => e.LocalName == "title");
                if (title != null) spec.Title = RichText(title);

                var txPr = ax.ChildElements.FirstOrDefault(e => e.LocalName == "txPr");
                var bodyPr = txPr?.ChildElements.FirstOrDefault(e => e.LocalName == "bodyPr");
                spec.LabelRotation = Attr(bodyPr, "rot", 0);
            }
        }

        // ------------------------------------------------------------------ helpers

        // A title / axis title is c:tx/c:rich/a:p/a:r/a:t, possibly across several runs.
        private static string RichText(OpenXmlElement holder)
        {
            var rich = holder.Descendants().FirstOrDefault(e => e.LocalName == "rich");
            if (rich == null)
            {
                var v = holder.Descendants().FirstOrDefault(e => e.LocalName == "v");
                return string.IsNullOrWhiteSpace(v?.InnerText) ? null : v.InnerText;
            }

            string text = string.Concat(rich.Descendants()
                .Where(e => e.LocalName == "t")
                .Select(e => e.InnerText));
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        private static string Val(OpenXmlElement e) => Attr(e, "val");

        private static string Attr(OpenXmlElement e, string name)
            => OoxmlAttribute.Value(e, name);

        private static double Attr(OpenXmlElement e, string name, double fallback)
            => double.TryParse(Attr(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v
                : fallback;

        private static double NumVal(OpenXmlElement parent, string childName, double fallback)
        {
            var child = parent?.ChildElements.FirstOrDefault(e => e.LocalName == childName);
            return child == null ? fallback : Attr(child, "val", fallback);
        }

        /// <summary>
        /// A stated percentage, held inside the range the schema defines for it, and reported when
        /// the file's own value could not be used. NaN is rejected outright: it compares false
        /// against every bound, so it has to be tested before them or it passes through untouched.
        /// </summary>
        private static double Clamped(OpenXmlElement plot, string name, double fallback,
            double min, double max)
        {
            double v = NumVal(plot, name, fallback);
            double used = double.IsNaN(v) ? fallback : v < min ? min : v > max ? max : v;
            if (used == v) return used;

            DocxDiagnostics.Report("ChartPartReader.PlotOptions", string.Format(
                CultureInfo.InvariantCulture,
                "c:{0} states {1}, which is outside its {2}..{3} range; {4} was used instead",
                name, v, min, max, used));
            return used;
        }

        private static double? OptionalNum(OpenXmlElement parent, string childName)
        {
            var child = parent?.ChildElements.FirstOrDefault(e => e.LocalName == childName);
            if (child == null) return null;
            return double.TryParse(Attr(child, "val"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double v) ? v : (double?)null;
        }

        private static bool BoolVal(OpenXmlElement e, bool fallback)
        {
            return e == null ? fallback : OoxmlOnOff.ElementIsOn(e);
        }
    }

    /// <summary>
    /// The narrow slice of DrawingML colour resolution the chart reader needs, so
    /// <see cref="ChartPartReader"/> depends on a one-method contract rather than on the whole
    /// resolver — and unit tests can read a chart with no theme machinery at all.
    /// </summary>
    internal interface DrawingColorSource
    {
        /// <summary>The <c>a:solidFill</c> colour under a <c>c:spPr</c>, as RRGGBB, or null.</summary>
        string SolidFillHexOf(OpenXmlElement spPr);
    }
}
