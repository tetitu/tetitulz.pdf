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
    /// The chart axis maths, tested directly — no DOCX, no PDF.
    ///
    /// <para>
    /// Every rule here exists for a specific way of drawing a chart that looks right and
    /// misrepresents its data: a column chart that does not start at zero, a step ladder that
    /// disagrees with Excel's labels, an all-equal series that divides by zero, a log axis silently
    /// drawn linear.
    /// </para>
    /// </summary>
    public class ChartAxisScaleTests
    {
        // Device coordinates 0 (bottom) → 200 (top), as a column chart's value axis would be.
        private static AxisScale Scale(double[] values, AxisSpec spec = null,
            bool allowBaselineLift = false, int targetTicks = 6)
            => AxisScale.ForValues(values, spec, 0, 200, allowBaselineLift, targetTicks);

        // ------------------------------------------------------------------ the zero rule

        [Fact]
        public void AllPositiveValues_PutTheAxisMinimumAtZero()
        {
            var s = Scale(new double[] { 10, 20, 47 });

            Assert.Equal(0, s.Min, 6);
            Assert.True(s.Max >= 47, "the axis must cover the data");
        }

        [Fact]
        public void AllNegativeValues_PutTheAxisMaximumAtZero()
        {
            var s = Scale(new double[] { -10, -20 });

            Assert.Equal(0, s.Max, 6);
            Assert.True(s.Min <= -20);
        }

        [Fact]
        public void MixedSignValues_SpanBothSidesOfZero()
        {
            var s = Scale(new double[] { -30, 50 });

            Assert.True(s.Min <= -30);
            Assert.True(s.Max >= 50);
        }

        [Fact]
        public void BarCharts_NeverLiftTheBaselineOffZero()
        {
            // 100/101/102 is a narrow band high above zero — exactly the case Excel zooms for on a
            // line chart. Doing it on bars would misstate the ratios the bars themselves encode.
            var bars = Scale(new double[] { 100, 101, 102 }, allowBaselineLift: false);

            Assert.Equal(0, bars.Min, 6);
        }

        [Fact]
        public void LineCharts_MayLiftTheBaselineForANarrowBand()
        {
            var line = Scale(new double[] { 100, 101, 102 }, allowBaselineLift: true);

            Assert.True(line.Min > 0,
                "a line chart of a narrow high band should zoom, as Excel does");
        }

        // ------------------------------------------------------------------ nice numbers

        [Theory]
        [InlineData(1.0, 1.0)]
        [InlineData(1.5, 2.0)]
        [InlineData(2.1, 2.5)]
        [InlineData(3.0, 5.0)]
        [InlineData(6.0, 10.0)]
        public void NiceStep_ClimbsExcelsLadder(double raw, double expected)
        {
            Assert.Equal(expected, AxisScale.NiceStep(raw), 6);
        }

        [Fact]
        public void NiceStep_IncludesTheTwoPointFiveRung()
        {
            // A 0–250 range over 11 ticks wants 25, not 50. Omitting the 2.5 rung changes every
            // label on the axis.
            Assert.Equal(25.0, AxisScale.NiceStep(250.0 / 10), 6);
        }

        [Fact]
        public void AutoRange_ProducesRoundTickValuesThatCoverTheData()
        {
            var s = Scale(new double[] { 47 });
            var ticks = s.MajorTicks();

            Assert.Contains(0.0, ticks);
            Assert.Contains(50.0, ticks);
            Assert.DoesNotContain(47.0, ticks);   // the data max is not a tick
        }

        // ------------------------------------------------------------------ degenerate input

        [Fact]
        public void AllValuesEqual_StillProducesAUsableFiniteRange()
        {
            var s = Scale(new double[] { 5, 5, 5 });

            Assert.True(s.Max > s.Min, "a zero-width range divides by zero when mapping");
            Assert.True(double.IsFinite(s.Map(5)), "Infinity in a content stream is rejected by some viewers");
        }

        [Fact]
        public void AllValuesZero_StillProducesAUsableFiniteRange()
        {
            var s = Scale(new double[] { 0, 0 });

            Assert.True(s.Max > s.Min);
            Assert.True(double.IsFinite(s.Map(0)));
        }

        [Fact]
        public void NoValuesAtAll_StillProducesAUsableFiniteRange()
        {
            var s = Scale(Array.Empty<double>());

            Assert.True(s.Max > s.Min);
            Assert.True(double.IsFinite(s.Map(0)));
        }

        // ------------------------------------------------------------------ mapping

        [Fact]
        public void Map_IsLinearBetweenTheDeviceEnds()
        {
            var s = AxisScale.ForValues(new double[] { 0, 100 }, null, 0, 200, false, 6);

            Assert.Equal(0.0, s.Map(s.Min), 6);
            Assert.Equal(200.0, s.Map(s.Max), 6);
            Assert.Equal(100.0, s.Map((s.Min + s.Max) / 2), 6);
        }

        [Fact]
        public void ExplicitMinAndMax_OverrideTheDerivedRange()
        {
            var spec = new AxisSpec { Min = 0, Max = 100 };
            var s = Scale(new double[] { 10, 20 }, spec);

            Assert.Equal(0, s.Min, 6);
            Assert.Equal(100, s.Max, 6);
            Assert.Equal(20.0, s.Map(10), 6);   // 10% of a 0–200 device span
        }

        [Fact]
        public void ReversedOrientation_FlipsTheDeviceEnds()
        {
            var normal = Scale(new double[] { 0, 100 });
            var reversed = Scale(new double[] { 0, 100 }, new AxisSpec { Reversed = true });

            Assert.True(normal.Map(normal.Max) > normal.Map(normal.Min));
            Assert.True(reversed.Map(reversed.Max) < reversed.Map(reversed.Min));
        }

        // ------------------------------------------------------------------ log axes

        [Fact]
        public void LogarithmicAxis_SpacesDecadesEvenly()
        {
            var spec = new AxisSpec { LogBase = 10 };
            var s = Scale(new double[] { 1, 1000 }, spec);

            double a = s.Map(1), b = s.Map(10), c = s.Map(100), d = s.Map(1000);
            Assert.Equal(b - a, c - b, 4);
            Assert.Equal(c - b, d - c, 4);
        }

        [Fact]
        public void LogarithmicAxis_TicksAtEachPower()
        {
            var s = Scale(new double[] { 1, 100 }, new AxisSpec { LogBase = 10 });
            var ticks = s.MajorTicks();

            Assert.Contains(ticks, t => Math.Abs(t - 1) < 1e-9);
            Assert.Contains(ticks, t => Math.Abs(t - 10) < 1e-9);
            Assert.Contains(ticks, t => Math.Abs(t - 100) < 1e-9);
        }

        [Fact]
        public void LogarithmicAxis_ReportsNonPositiveValuesAsUnmappable()
        {
            var s = Scale(new double[] { 1, 100 }, new AxisSpec { LogBase = 10 });

            // NaN is the caller's signal to drop the point, not to draw it at the origin.
            Assert.True(double.IsNaN(s.Map(0)));
            Assert.True(double.IsNaN(s.Map(-5)));
        }

        [Fact]
        public void LinearAxis_MapsZeroNormally()
        {
            // The control for the log tests: on a linear axis zero is an ordinary value.
            var s = Scale(new double[] { -10, 10 });

            Assert.True(double.IsFinite(s.Map(0)));
        }
    }
}
