// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Charts
{
    /// <summary>A rectangle in chart-local coordinates.</summary>
    internal readonly struct ChartRect
    {
        internal ChartRect(double x, double y, double w, double h)
        {
            X = x; Y = y; Width = w; Height = h;
        }

        internal double X { get; }
        internal double Y { get; }
        internal double Width { get; }
        internal double Height { get; }
        internal double Right => X + Width;
        internal double Top => Y + Height;
        internal double CentreX => X + Width / 2;
        internal double CentreY => Y + Height / 2;
    }

    /// <summary>Something to draw. The order of these in the frame <b>is</b> the paint order.</summary>
    internal abstract class ChartItem
    {
    }

    internal sealed class RectItem : ChartItem
    {
        internal ChartRect Box;
        internal string FillHex;
    }

    internal sealed class LineItem : ChartItem
    {
        internal double X0, Y0, X1, Y1;
        internal string StrokeHex;
        internal double WidthPt = 0.75;
    }

    internal sealed class PolylineItem : ChartItem
    {
        internal readonly List<(double x, double y)> Points = new List<(double, double)>();
        internal string StrokeHex;
        internal double WidthPt = 2.25;

        /// <summary>When set, the path closes and fills instead of stroking — an area series.</summary>
        internal string FillHex;
    }

    /// <summary>A data-point marker: a small square, or a circle for the round symbols.</summary>
    internal sealed class MarkerItem : ChartItem
    {
        internal double X, Y, Size;
        internal string FillHex;
        internal bool Round;
    }

    /// <summary>A pie or doughnut slice; <see cref="InnerRadius"/> is 0 for a pie.</summary>
    internal sealed class SliceItem : ChartItem
    {
        internal double CentreX, CentreY, Radius, InnerRadius;

        /// <summary>Degrees, counter-clockwise from +x — PDF's own convention.</summary>
        internal double StartAngle, SweepAngle;

        internal string FillHex;
    }

    internal enum TextAnchor { Left, Centre, Right }

    internal sealed class TextItem : ChartItem
    {
        internal string Text;
        internal double X, Y;          // the anchor point; Y is the baseline
        internal double SizePt = 10;
        internal TextAnchor Anchor = TextAnchor.Left;

        /// <summary>Counter-clockwise degrees. Non-zero forces a text matrix instead of <c>Td</c>.</summary>
        internal double RotationDeg;

        internal string ColorHex = "000000";
        internal bool Bold;
    }

    /// <summary>
    /// A laid-out chart: everything positioned, nothing yet expressed as PDF.
    ///
    /// <para>
    /// Coordinates are <b>chart-local with y upward</b>, matching PDF, so the painter needs only a
    /// translation. Charts deliberately do not use the y-down frame the DrawingML shape path
    /// establishes: an axis is naturally y-up, and staying y-up means chart text can use an
    /// ordinary <c>Td</c> rather than a re-flipping text matrix.
    /// </para>
    /// </summary>
    internal sealed class ChartFrame
    {
        internal double Width;
        internal double Height;
        internal ChartRect Plot;
        internal readonly List<ChartItem> Items = new List<ChartItem>();
    }
}
