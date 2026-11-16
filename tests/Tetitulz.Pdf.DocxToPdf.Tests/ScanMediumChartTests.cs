// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.DocxToPdf.Rendering.Charts;
using Xunit;

namespace Tetitulz.Pdf.DocxToPdf.Tests
{
    /// <summary>
    /// Chart-layout defects from the 2026-08 assembly scan (MEDIUM band).
    ///
    /// <para>
    /// These drive <see cref="ChartLayout.Compute"/> directly rather than through a DOCX: the
    /// defects are arithmetic — an unchecked cast, an unclosed subpath, a coordinate read off the
    /// wrong scale — and asserting them on positioned geometry is what makes them falsifiable
    /// without a content-stream diff. The <c>measure</c> delegate is supplied by the caller, which
    /// is also what lets the label-width overflow be reproduced at all.
    /// </para>
    /// </summary>
    public class ScanMediumChartTests
    {
        private static readonly string[] Palette = { "4472C4", "ED7D31" };

        /// <summary>A plausible text measurer: half an em per character.</summary>
        private static double Measure(string s, double sizePt) => (s?.Length ?? 0) * sizePt * 0.5;

        // ------------------------------------------------------------------ ChartLayout.cs:565
        //
        // The scan's "category-label skip is an unchecked double-to-int cast that can go negative"
        // finding is REFUTED on this assembly's target framework and no change was made. .NET Core
        // 3.0 made floating-point→integer conversion saturating and deterministic, so on net10.0
        // `(int)1e13` is int.MaxValue, not int.MinValue — measured, not assumed. The skip therefore
        // saturates POSITIVE, the loop draws the first label and exits, and no negative index is
        // ever formed. The test below pins that, so the finding cannot be re-raised from a reading
        // of the source alone.

        [Fact]
        public void CategoryLabelSkip_AbsurdLabelWidth_SaturatesPositiveAndDrawsOneLabel()
        {
            var model = new ChartModel { Kind = ChartKind.Bar, BarDir = BarDirection.Column };
            model.Series.Add(new SeriesData
            {
                Values = new double?[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            });
            string longLabel = new string('x', 1000);
            model.Categories.Add(longLabel);

            // 1000 chars at 1e9 pt each, against a plot clamped to 1 pt wide over ten categories:
            // widest / band ≈ 1e13, far past int.MaxValue.
            var frame = ChartLayout.Compute(model, 1, 1, Palette, (s, _) => (s?.Length ?? 0) * 1e9);

            Assert.Single(frame.Items.OfType<TextItem>().Where(t => t.Text == longLabel));
        }

        // ------------------------------------------------------------------ ChartLayout.cs:477

        [Fact]
        public void AreaSeries_WithABlankCell_ClosesEverySubpathBackToTheBaseline()
        {
            var model = new ChartModel { Kind = ChartKind.Area };
            model.Series.Add(new SeriesData { Values = new double?[] { 10, 20, null, 30, 40 } });

            var frame = ChartLayout.Compute(model, 400, 300, Palette, Measure);

            var areas = frame.Items.OfType<PolylineItem>().Where(p => p.FillHex != null).ToList();
            Assert.Equal(2, areas.Count);

            // Each filled subpath is baseline → data → data → baseline. The closing point used to
            // be appended once, after the whole loop, for whichever PolylineItem happened to be
            // current at the end — so the FIRST subpath kept its opening baseline point and never
            // got its closing one, and ChartPainter's `h f` filled the triangle from (x1,y1)
            // straight back to (x0,baseline) instead of the region under the data.
            double baseline = areas[0].Points[0].y;
            foreach (var a in areas)
            {
                Assert.Equal(4, a.Points.Count);
                Assert.Equal(baseline, a.Points[0].y, 3);
                Assert.Equal(baseline, a.Points[a.Points.Count - 1].y, 3);
            }
        }

        // ------------------------------------------------------------------ ChartLayout.cs:515

        [Fact]
        public void PercentStackedValueLabels_LandOnTheSegmentTheyAnnotate()
        {
            var model = new ChartModel
            {
                Kind = ChartKind.Bar,
                BarDir = BarDirection.Column,
                Grouping = Grouping.PercentStacked,
                ShowValueLabels = true,
            };
            model.Series.Add(new SeriesData { Values = new double?[] { 5 } });
            model.Series.Add(new SeriesData { Values = new double?[] { 10 } });
            model.Categories.Add("A");

            var frame = ChartLayout.Compute(model, 400, 300, Palette, Measure);
            var plot = frame.Plot;

            var bars = frame.Items.OfType<RectItem>()
                .OrderBy(r => r.Box.Y)
                .ToList();
            Assert.Equal(2, bars.Count);

            // The only text inside the plot rectangle is the two value labels: the value-axis
            // ticks sit left of plot.X and the category labels below plot.Y. Reading raw.Value
            // through a 0..1 percent axis put BOTH labels five and ten plot-heights above
            // plot.Top, where ChartPainter's clip discarded them and the chart shipped with no
            // data labels at all.
            var inPlot = frame.Items.OfType<TextItem>()
                .Where(t => t.X >= plot.X && t.X <= plot.Right && t.Y >= plot.Y && t.Y <= plot.Top)
                .OrderBy(t => t.Y)
                .ToList();
            Assert.Equal(2, inPlot.Count);

            for (int i = 0; i < 2; i++)
                Assert.InRange(inPlot[i].Y, bars[i].Box.Y, bars[i].Box.Y + bars[i].Box.Height);
        }

        // ------------------------------------------------------------------ ChartLayout.cs:208

        [Fact]
        public void ScatterChart_BottomLabelsComeFromTheXValueScale_NotFromCategoryBands()
        {
            var model = new ChartModel { Kind = ChartKind.Scatter };
            model.Series.Add(new SeriesData
            {
                Values = new double?[] { 1, 1, 1 },
                XValues = new double?[] { 1, 2, 100 },
            });
            // ChartPartReader fills Categories from c:xVal when a scatter has no c:cat, which is
            // how these ever became labels in the first place.
            foreach (string c in new[] { "1", "2", "100" }) model.Categories.Add(c);

            var frame = ChartLayout.Compute(model, 400, 300, Palette, Measure);
            var plot = frame.Plot;

            var markers = frame.Items.OfType<MarkerItem>().ToList();
            Assert.Equal(3, markers.Count);
            double rightmostPoint = markers.Max(m => m.X);

            var bottomLabels = frame.Items.OfType<TextItem>().Where(t => t.Y < plot.Y).ToList();

            // Both labels and points now come from one x scale, so a label lines up with the
            // extreme point and one sits on the axis origin. The category bands they replaced put
            // labels at 1/6, 3/6 and 5/6 of the width while the points sat at ~0%, ~1% and 100% —
            // every label pointed somewhere the data was not.
            Assert.Contains(bottomLabels, t => Math.Abs(t.X - rightmostPoint) < 0.5);
            Assert.Contains(bottomLabels, t => Math.Abs(t.X - plot.X) < 0.5);

            // And the category labels themselves are gone. "2" is the second category's text and
            // cannot be a tick of this axis: the scale spans 0..100 over at most eleven ticks, so
            // its step is at least 10 and every tick label is a multiple of it.
            Assert.DoesNotContain(bottomLabels, t => t.Text == "2");
        }
    }
}
