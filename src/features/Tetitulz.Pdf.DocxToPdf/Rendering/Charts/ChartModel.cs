// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Charts
{
    /// <summary>The chart kinds this renderer can lay out.</summary>
    internal enum ChartKind
    {
        /// <summary>Not a kind we draw; the caller reserves the box and reports.</summary>
        Unsupported,
        Bar,
        Line,
        Pie,
        Doughnut,
        Area,

        /// <summary>
        /// The one kind whose <b>x axis is a value axis</b> rather than a category band — which is
        /// why it is the only in-family kind that changes the layout engine rather than just the
        /// geometry it emits.
        /// </summary>
        Scatter,

        /// <summary>
        /// Scatter plus a third value per point, <c>c:bubbleSize</c>, drawn as the marker's
        /// <b>area</b> (ECMA-376 <c>c:sizeRepresents</c> defaults to <c>area</c>, not diameter —
        /// scaling the radius instead exaggerates every large bubble quadratically).
        /// </summary>
        Bubble,
    }

    internal enum BarDirection { Column, Bar }

    internal enum Grouping { Clustered, Stacked, PercentStacked, Standard }

    /// <summary>One plotted series: its name, its explicit colour if it states one, and its values.</summary>
    internal sealed class SeriesData
    {
        internal string Name;

        /// <summary>An explicit <c>c:spPr</c> colour, or <c>null</c> to take the palette's turn.</summary>
        internal string ColorHex;

        /// <summary>
        /// Values indexed by category. <c>null</c> is a blank cell — <b>not</b> zero, and not a
        /// missing element: <c>c:pt</c> is sparse and carries its own <c>@idx</c>, so a gap has to
        /// stay a gap. Reading the points in document order instead would slide every later value
        /// one place left and draw an unbroken line through a hole.
        /// </summary>
        internal double?[] Values;

        /// <summary>Per-point colours, used by pie and doughnut where colour varies by category.</summary>
        internal string[] PointColors;

        /// <summary>
        /// A scatter series' x values (<c>c:xVal</c>), indexed like <see cref="Values"/>. Null for
        /// every other kind, where x comes from the category band instead.
        /// </summary>
        internal double?[] XValues;

        /// <summary>
        /// A bubble series' sizes (<c>c:bubbleSize</c>), indexed like <see cref="Values"/>. Null for
        /// every other kind, including scatter, whose markers are a fixed size.
        /// </summary>
        internal double?[] BubbleSizes;
    }

    /// <summary>An axis's stated properties. Everything is optional; absent means "derive it".</summary>
    internal sealed class AxisSpec
    {
        internal double? Min;
        internal double? Max;
        internal double? MajorUnit;
        internal double? LogBase;
        internal bool Reversed;
        internal bool MajorGridlines;
        internal bool Deleted;
        internal string NumberFormat;
        internal string Title;

        /// <summary>
        /// <c>c:crossBetween</c>. True (the default) puts each category in a band and labels its
        /// centre; false (<c>midCat</c>) puts point <i>i</i> exactly on its tick.
        /// </summary>
        internal bool CrossBetween = true;

        /// <summary><c>c:tickLblPos val="none"</c> suppresses the labels entirely.</summary>
        internal bool LabelsHidden;

        /// <summary>Rotation from <c>c:txPr/a:bodyPr@rot</c>, in 60000ths of a degree.</summary>
        internal double LabelRotation;
    }

    /// <summary>
    /// A chart, parsed and PDF-agnostic. This is the contract between reading and layout: once a
    /// <see cref="ChartModel"/> exists nothing downstream touches OpenXml, which is what makes the
    /// axis and geometry maths unit-testable without building a DOCX.
    /// </summary>
    internal sealed class ChartModel
    {
        internal ChartKind Kind = ChartKind.Unsupported;

        /// <summary>The element name that produced <see cref="Kind"/>, for the diagnostic.</summary>
        internal string KindName;

        /// <summary>True when the source was a 3-D variant, drawn flat and reported.</summary>
        internal bool WasThreeDimensional;

        internal BarDirection BarDir = BarDirection.Column;
        internal Grouping Grouping = Grouping.Clustered;

        internal string Title;
        internal bool AutoTitleDeleted;

        /// <summary><c>r</c>, <c>l</c>, <c>t</c>, <c>b</c>, or <c>null</c> for no legend.</summary>
        internal string LegendPosition;

        internal readonly List<SeriesData> Series = new List<SeriesData>();
        internal readonly List<string> Categories = new List<string>();

        internal AxisSpec ValueAxis = new AxisSpec();
        internal AxisSpec CategoryAxis = new AxisSpec();

        /// <summary>Percentage of one bar's width left as a gap between clusters (default 150).</summary>
        internal double GapWidth = 150;

        /// <summary>Percentage overlap between bars of a cluster (default 0; Word 2013+ writes −27).</summary>
        internal double Overlap;

        /// <summary>Doughnut hole as a percentage of the outer radius.</summary>
        internal double HoleSize = 50;

        /// <summary><c>gap</c> (default), <c>zero</c> or <c>span</c>.</summary>
        internal string DisplayBlanksAs = "gap";

        /// <summary>Pie and doughnut show percentages / values on the slices when asked.</summary>
        internal bool ShowPercentLabels;
        internal bool ShowValueLabels;

        /// <summary>The number of categories to plot — the longest series, not the label cache.</summary>
        internal int PointCount
        {
            get
            {
                int n = 0;
                foreach (var s in Series)
                    if (s.Values != null && s.Values.Length > n) n = s.Values.Length;
                return n;
            }
        }

        internal bool HasAnyValue
        {
            get
            {
                foreach (var s in Series)
                {
                    if (s.Values == null) continue;
                    foreach (var v in s.Values) if (v.HasValue) return true;
                }
                return false;
            }
        }

        /// <summary>True for the kinds drawn against axes rather than as a circle.</summary>
        internal bool HasAxes => Kind == ChartKind.Bar || Kind == ChartKind.Line
                              || Kind == ChartKind.Area || Kind == ChartKind.Scatter
                              || Kind == ChartKind.Bubble;

        /// <summary>True when the horizontal axis carries values rather than category bands.</summary>
        internal bool HasValueXAxis => Kind == ChartKind.Scatter || Kind == ChartKind.Bubble;

        /// <summary>
        /// True for the kinds whose points carry their own x, so a per-category value label would
        /// have no band to sit under.
        /// </summary>
        internal bool IsPointKind => Kind == ChartKind.Scatter || Kind == ChartKind.Bubble;

        /// <summary>The x-axis spec — a scatter's horizontal axis is a second value axis.</summary>
        internal AxisSpec XAxis = new AxisSpec();
    }
}
