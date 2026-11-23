// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// A marker is drawn again at every vertex it marks and an SVG image is its own document drawn whole: both are
/// charged as a <c>&lt;use&gt;</c> reference's copy is, against <c>PdfLimits.MaxSvgUseExpansions</c> drawings and
/// <c>PdfLimits.MaxSvgUseExpandedElements</c> elements, all of them together.
/// </summary>
[Collection(TestInfrastructure.PdfLimitsCollection.Name)]
public class SvgRedrawBudgetTests
{
    /// <summary>
    /// Three levels of distinct markers, each marking the next at the nineteen middle vertices of its path: 19³
    /// leaf rectangles, a product no depth cap or self-reference guard bounds. Under a thousand drawings the
    /// chain stops there and says so; the control, under the default ceiling, draws all 6 859.
    /// </summary>
    [Theory]
    [InlineData(1_000, true)]
    [InlineData(PdfLimits.DefaultMaxSvgUseExpansions, false)]
    public void AdversarialMarkerChain_IsBoundedByTheDrawingCount(int ceiling, bool capped)
    {
        try
        {
            PdfLimits.MaxSvgUseExpansions = ceiling;
            int drawn = Rectangles(Convert(MarkerChain(levels: 3, vertices: 20, leafRects: 1)));

            if (capped)
            {
                Assert.InRange(drawn, 1, ceiling);
                Assert.Contains("markers drawn past", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
            }
            else
            {
                Assert.Equal(19 * 19 * 19, drawn);
                Assert.DoesNotContain("markers drawn past", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
            }
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// One marker of a hundred rectangles at 399 vertices is 39 900 rectangles inside every drawing count: under a
    /// ceiling of 20 000 elements the marks past it are skipped and said. The control draws all of them.
    /// </summary>
    [Theory]
    [InlineData(20_000L, true)]
    [InlineData(PdfLimits.DefaultMaxSvgUseExpandedElements, false)]
    public void AdversarialLargeMarkerAtManyVertices_IsBoundedByTheElementsDrawn(long ceiling, bool capped)
    {
        try
        {
            PdfLimits.MaxSvgUseExpandedElements = ceiling;
            int drawn = Rectangles(Convert(MarkerChain(levels: 1, vertices: 400, leafRects: 100)));

            if (capped)
            {
                Assert.InRange(drawn, 100, (int)ceiling);
                Assert.Contains("copied elements were skipped", SvgToPdfConverter.LastError ?? "", StringComparison.Ordinal);
            }
            else Assert.Equal(399 * 100, drawn);
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// Fifty SVG images, each a document of ten rectangles: under twenty drawings the images past it are skipped
    /// and said, and the twenty drawn are whole — ten rectangles and the viewport's clip each. The control draws all
    /// fifty.
    /// </summary>
    [Theory]
    [InlineData(20, 220)]
    [InlineData(PdfLimits.DefaultMaxSvgUseExpansions, 550)]
    public void SvgImages_AreChargedAsDrawings(int ceiling, int rectangles)
    {
        string inner = @"<svg xmlns=""http://www.w3.org/2000/svg"" width=""10"" height=""10"">" +
                       string.Concat(Enumerable.Repeat(@"<rect width=""1"" height=""1""/>", 10)) + "</svg>";
        string uri = "data:image/svg+xml;base64," + System.Convert.ToBase64String(Encoding.UTF8.GetBytes(inner));
        string images = string.Concat(Enumerable.Range(0, 50).Select(i => $@"<image x=""{i}"" width=""10"" height=""10"" href=""{uri}""/>"));
        try
        {
            PdfLimits.MaxSvgUseExpansions = ceiling;
            int drawn = Rectangles(Convert(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""200"" height=""200"">" + images + "</svg>"));

            Assert.Equal(rectangles, drawn);
            Assert.Equal(rectangles < 550, (SvgToPdfConverter.LastError ?? "").Contains("SVG images past", StringComparison.Ordinal));
        }
        finally { PdfLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// <paramref name="levels"/> distinct markers: each but the last holds a path of <paramref name="vertices"/> + 1
    /// points marking the next at its middle vertices; the last holds <paramref name="leafRects"/> rectangles. The
    /// drawing's own path marks the first.
    /// </summary>
    private static string MarkerChain(int levels, int vertices, int leafRects)
    {
        var pts = new StringBuilder("M0 0");
        for (int i = 1; i <= vertices; i++) pts.Append(" L").Append(i % 10).Append(' ').Append(i % 7);
        string path = pts.ToString();
        var sb = new StringBuilder(@"<svg xmlns=""http://www.w3.org/2000/svg"" width=""100"" height=""100""><defs>");
        for (int k = 0; k < levels; k++)
        {
            sb.Append($@"<marker id=""m{k}"" markerWidth=""3"" markerHeight=""3"" overflow=""visible"">");
            if (k == levels - 1) sb.Append(string.Concat(Enumerable.Repeat(@"<rect width=""1"" height=""1""/>", leafRects)));
            else sb.Append($@"<path d=""{path}"" stroke=""black"" fill=""none"" marker-mid=""url(#m{k + 1})""/>");
            sb.Append("</marker>");
        }
        return sb.Append($@"</defs><path d=""{path}"" stroke=""black"" fill=""none"" marker-mid=""url(#m0)""/></svg>").ToString();
    }

    private static byte[] Convert(string svg) =>
        SvgToPdfConverter.Convert(svg, new SvgConversionOptions { PageWidth = 200, PageHeight = 200, FlipY = false });

    /// <summary>The rectangles the converted page draws: its content's <c>re</c> lines.</summary>
    private static int Rectangles(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf));
        string c = Encoding.Latin1.GetString(doc.Pages[0].GetContentStreamData());
        return c.Split('\n').Count(l => l.TrimEnd().EndsWith(" re", StringComparison.Ordinal));
    }
}
