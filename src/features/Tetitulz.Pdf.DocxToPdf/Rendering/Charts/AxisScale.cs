// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Charts
{
    /// <summary>
    /// Maps data values onto device positions, and chooses the tick marks.
    ///
    /// <para>
    /// Pure arithmetic — no PDF, no OpenXml — because this is where a chart renderer actually goes
    /// wrong. Every rule below exists for a specific way of producing a plausible-looking chart
    /// that misrepresents its data.
    /// </para>
    /// </summary>
    internal readonly struct AxisScale
    {
        private AxisScale(double min, double max, double step, double logBase,
            double lo, double hi, bool reversed)
        {
            Min = min; Max = max; Step = step; LogBase = logBase;
            Lo = lo; Hi = hi; Reversed = reversed;
        }

        internal double Min { get; }
        internal double Max { get; }
        internal double Step { get; }

        /// <summary>Greater than 1 for a logarithmic axis; 0 for a linear one.</summary>
        internal double LogBase { get; }

        /// <summary>Device coordinate of <see cref="Min"/>.</summary>
        internal double Lo { get; }

        /// <summary>Device coordinate of <see cref="Max"/>.</summary>
        internal double Hi { get; }

        internal bool Reversed { get; }

        /// <summary>
        /// Builds a value axis over <paramref name="values"/>, honouring whatever the file states
        /// and deriving the rest.
        /// </summary>
        /// <param name="allowBaselineLift">
        /// Excel lifts a value axis off zero when the data is a narrow band high above it — but
        /// only for line and scatter. <b>Never for bar, column or area</b>: a bar chart of
        /// 100/101/102 drawn from 99 is a lie about the ratios its own bars encode, while a line
        /// chart of the same numbers is a legitimate zoom.
        /// </param>
        /// <param name="targetTicks">
        /// Derived from the box's size, not from the plot rectangle — which is what keeps the tick
        /// count from depending on the label widths that depend on the tick count.
        /// </param>
        /// <param name="values">The plotted values, scanned for their own min and max.</param>
        /// <param name="spec">
        /// The axis as the DOCX declares it — an explicit min/max here overrides what the data
        /// suggests.
        /// </param>
        /// <param name="lo">Caller-supplied lower bound, used when <paramref name="spec"/> fixes none.</param>
        /// <param name="hi">Caller-supplied upper bound, used when <paramref name="spec"/> fixes none.</param>
        internal static AxisScale ForValues(IEnumerable<double> values, AxisSpec spec,
            double lo, double hi, bool allowBaselineLift, int targetTicks)
        {
            double dataMin = double.MaxValue, dataMax = double.MinValue;
            bool any = false;
            foreach (double v in values)
            {
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                if (v < dataMin) dataMin = v;
                if (v > dataMax) dataMax = v;
                any = true;
            }
            if (!any) { dataMin = 0; dataMax = 1; }

            double logBase = spec?.LogBase ?? 0;
            if (logBase > 1) return Logarithmic(dataMin, dataMax, spec, lo, hi, logBase);

            double min = dataMin, max = dataMax;

            // The rule that makes column charts start at zero. Getting this wrong is the most
            // visible failure a chart renderer has.
            if (!allowBaselineLift || !NarrowBandAboveZero(dataMin, dataMax))
            {
                if (dataMin >= 0) min = 0;
                if (dataMax <= 0) max = 0;
            }

            if (max - min <= 0)
            {
                // All values equal. Without this, Map divides by zero and writes Infinity into the
                // content stream, which some viewers reject outright.
                if (Math.Abs(max) < 1e-12) { min = 0; max = 1; }
                else if (max > 0) { min = 0; max = NiceCeil(max); }
                else { min = NiceFloor(min); max = 0; }
            }

            double step = spec?.MajorUnit ?? NiceStep((max - min) / Math.Max(1, targetTicks - 1));
            if (step <= 0) step = (max - min) / Math.Max(1, targetTicks - 1);

            // Round outward to whole steps so the ends carry round labels.
            if (spec?.Min == null) min = Math.Floor(min / step) * step;
            if (spec?.Max == null) max = Math.Ceiling(max / step) * step;

            if (spec?.Min != null) min = spec.Min.Value;
            if (spec?.Max != null) max = spec.Max.Value;
            if (max - min <= 0) max = min + (step > 0 ? step : 1);

            bool reversed = spec?.Reversed ?? false;
            return new AxisScale(min, max, step, 0,
                reversed ? hi : lo, reversed ? lo : hi, reversed);
        }

        private static AxisScale Logarithmic(double dataMin, double dataMax, AxisSpec spec,
            double lo, double hi, double logBase)
        {
            // Non-positive values have no place on a log axis; the caller drops them and reports.
            double min = dataMin > 0 ? dataMin : 1;
            double max = dataMax > 0 ? dataMax : min * logBase;

            double loExp = Math.Floor(Math.Log(min, logBase));
            double hiExp = Math.Ceiling(Math.Log(max, logBase));
            if (hiExp <= loExp) hiExp = loExp + 1;

            double axisMin = spec?.Min ?? Math.Pow(logBase, loExp);
            double axisMax = spec?.Max ?? Math.Pow(logBase, hiExp);
            bool reversed = spec?.Reversed ?? false;

            return new AxisScale(axisMin, axisMax, 0, logBase,
                reversed ? hi : lo, reversed ? lo : hi, reversed);
        }

        /// <summary>
        /// The device position of <paramref name="value"/>, or <see cref="double.NaN"/> when it is
        /// not representable (a non-positive value on a log axis).
        /// </summary>
        internal double Map(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return double.NaN;

            double t;
            if (LogBase > 1)
            {
                if (value <= 0 || Min <= 0 || Max <= 0) return double.NaN;
                double lnMin = Math.Log(Min), lnMax = Math.Log(Max);
                if (Math.Abs(lnMax - lnMin) < 1e-12) return Lo;
                t = (Math.Log(value) - lnMin) / (lnMax - lnMin);
            }
            else
            {
                if (Math.Abs(Max - Min) < 1e-12) return Lo;
                t = (value - Min) / (Max - Min);
            }

            return Lo + t * (Hi - Lo);
        }

        /// <summary>The major tick values, inclusive of both ends.</summary>
        internal List<double> MajorTicks()
        {
            var ticks = new List<double>();

            if (LogBase > 1)
            {
                for (double v = Min; v <= Max * (1 + 1e-9); v *= LogBase)
                {
                    ticks.Add(v);
                    if (ticks.Count > 64) break;
                }
                return ticks;
            }

            if (Step <= 0) { ticks.Add(Min); ticks.Add(Max); return ticks; }

            // Accumulating by multiplication rather than repeated addition keeps the labels exact:
            // adding 0.1 ten times does not give 1.
            int n = SaturatingCast.ToInt(Math.Round((Max - Min) / Step));
            for (int i = 0; i <= n && i <= 1000; i++)
                ticks.Add(Min + i * Step);
            return ticks;
        }

        /// <summary>
        /// Excel's "nice" step ladder: 1, 2, 2.5, 5, 10 times a power of ten.
        /// <b>The 2.5 rung is not optional</b> — without it a 0–250 range gets a step of 50 where
        /// Excel gives 25, and every tick label in the chart differs from the source.
        /// </summary>
        internal static double NiceStep(double rawStep)
        {
            if (rawStep <= 0 || double.IsNaN(rawStep) || double.IsInfinity(rawStep)) return 1;

            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rawStep)));
            double fraction = rawStep / magnitude;

            double nice = fraction <= 1 ? 1
                        : fraction <= 2 ? 2
                        : fraction <= 2.5 ? 2.5
                        : fraction <= 5 ? 5
                        : 10;
            return nice * magnitude;
        }

        // Excel lifts the baseline when the data occupies less than a sixth of the span from zero.
        private static bool NarrowBandAboveZero(double min, double max)
            => min > 0 && max > 0 && (max - min) / max < 1.0 / 6.0;

        private static double NiceCeil(double v) => NiceStep(Math.Abs(v)) * Math.Ceiling(v / NiceStep(Math.Abs(v)));

        private static double NiceFloor(double v) => -NiceCeil(-v);
    }
}
