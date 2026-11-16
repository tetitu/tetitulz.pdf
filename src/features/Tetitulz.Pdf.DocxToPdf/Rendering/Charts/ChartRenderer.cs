// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.DocxToPdf.Rendering.Drawings;

namespace Tetitulz.Pdf.DocxToPdf.Rendering.Charts
{
    /// <summary>
    /// Resolves a <c>c:chart</c> reference, lays the chart out and paints it — or, when it cannot,
    /// leaves the box reserved and says why.
    /// </summary>
    internal sealed class ChartRenderer : DrawingColorSource
    {
        private static readonly string[] AccentTokens =
        {
            "accent1", "accent2", "accent3", "accent4", "accent5", "accent6",
        };

        private DrawingColorResolver _colors;

        /// <summary>
        /// Paints the chart into <paramref name="box"/>. Returns false when nothing was drawn, so
        /// the caller reserves the space and reports.
        /// </summary>
        internal bool TryRender(OpenXmlElement graphicData, DrawingPlacement.Box box,
            DocxRenderContext context)
        {
            var part = ResolveChartPart(graphicData, context);
            if (part == null)
            {
                DocxDiagnostics.Report("ChartRenderer.Resolve",
                    "the chart's r:id does not resolve to a chart part");
                return false;
            }

            _colors = context.DrawingColors;
            if (!ChartPartReader.TryRead(part, this, out var model) || model == null)
            {
                DocxDiagnostics.Report("ChartRenderer.Read",
                    "the chart part has no plot area that could be read");
                return false;
            }

            if (model.Kind == ChartKind.Unsupported)
            {
                DocxDiagnostics.Report("ChartRenderer.Kind",
                    "chart type '" + (model.KindName ?? "(unknown)")
                        + "' is not supported; its box was reserved but nothing was drawn");
                return false;
            }

            if (!model.HasAnyValue)
            {
                // A chart whose c:numRef has no c:numCache keeps its numbers only in the embedded
                // workbook, which this renderer does not open by design.
                DocxDiagnostics.Report("ChartRenderer.Cache",
                    "the chart carries no c:numCache values (they live in the embedded workbook), "
                        + "so nothing could be plotted");
                return false;
            }

            if (model.WasThreeDimensional)
                DocxDiagnostics.Report("ChartRenderer.Kind3D",
                    "'" + model.KindName + "' was approximated as its two-dimensional equivalent");

            DecimateIfOversized(model);

            var accents = ResolveAccents(context);
            var frame = ChartLayout.Compute(model, box.Width, box.Height, accents,
                (text, size) => context.StyleResolver.MeasureTextWidth(text, "Helvetica", size));

            context.AppendContent(ChartPainter.Paint(frame, box.X, box.BottomY, context));
            return true;
        }

        /// <summary>
        /// Bounds what a crafted cache can expand into. A chart with more points than
        /// <see cref="Tetitulz.Pdf.Core.Constants.PdfLimits.MaxChartPointsPerSeries"/> is decimated
        /// with an even stride rather than truncated — keeping the first N points would redraw the
        /// left of the chart and silently drop the rest, which reads as complete data.
        /// </summary>
        private static void DecimateIfOversized(ChartModel model)
        {
            int cap = Tetitulz.Pdf.Core.Constants.PdfLimits.MaxChartPointsPerSeries;
            int points = model.PointCount;
            if (points <= cap) return;

            int stride = SaturatingCast.ToInt(Math.Ceiling(points / (double)cap));

            foreach (var series in model.Series)
            {
                if (series.Values == null) continue;
                var kept = new List<double?>((series.Values.Length + stride - 1) / stride);
                for (int i = 0; i < series.Values.Length; i += stride) kept.Add(series.Values[i]);
                series.Values = kept.ToArray();
            }

            var categories = new List<string>();
            for (int i = 0; i < model.Categories.Count; i += stride) categories.Add(model.Categories[i]);
            model.Categories.Clear();
            model.Categories.AddRange(categories);

            DocxDiagnostics.Report("ChartRenderer.Points", string.Format(CultureInfo.InvariantCulture,
                "the chart declares {0} points per series, above the {1} cap; every {2}th point was "
                    + "plotted and the rest dropped",
                points, cap, stride));
        }

        /// <summary>The theme's six accent colours, for the default series palette.</summary>
        private static IReadOnlyList<string> ResolveAccents(DocxRenderContext context)
        {
            var accents = new List<string>(AccentTokens.Length);
            foreach (string token in AccentTokens)
            {
                string hex = context.StyleResolver.ThemeHexByDrawingMlToken(token)
                             ?? OfficeThemeDefaults.Hex(token);
                if (hex != null) accents.Add(hex);
            }
            return accents;
        }

        private static ChartPart ResolveChartPart(OpenXmlElement graphicData, DocxRenderContext context)
        {
            var chart = graphicData?.ChildElements.FirstOrDefault(e => e.LocalName == "chart");
            string relId = OoxmlAttribute.Value(chart, "id");
            if (string.IsNullOrEmpty(relId)) return null;

            // The relationship belongs to the part holding the drawing, which is the main document
            // for a body chart but not for one in a header or a nested text box.
            return OpenXmlPartLookup.Resolve(graphicData, relId,
                context.WordDocument.MainDocumentPart) as ChartPart;
        }

        string DrawingColorSource.SolidFillHexOf(OpenXmlElement spPr)
        {
            var solid = spPr?.ChildElements.FirstOrDefault(e => e.LocalName == "solidFill");
            if (solid == null || _colors == null) return null;
            if (!_colors.TryResolve(solid, null, out var rgba)) return null;

            return (SaturatingCast.ToInt(Math.Round(rgba.R * 255))).ToString("X2", CultureInfo.InvariantCulture)
                 + (SaturatingCast.ToInt(Math.Round(rgba.G * 255))).ToString("X2", CultureInfo.InvariantCulture)
                 + (SaturatingCast.ToInt(Math.Round(rgba.B * 255))).ToString("X2", CultureInfo.InvariantCulture);
        }
    }
}
