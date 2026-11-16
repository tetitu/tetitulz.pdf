// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="AnnotationPointList"/> — which <c>/InkList</c> strokes and <c>/Vertices</c> runs are
/// drawable, decided once for both renderers.
/// </summary>
/// <remarks>
/// <para><b>Written because the rule it centralises had no coverage.</b> The raster and SVG paths
/// each carried their own copy of "skip an entry with fewer than four coordinates", and mutating
/// that condition out of the merged helper left all 3 030 Base tests green — no fixture in the
/// suite has a short stroke. The merge was still right (the copies agreed), but it moved an
/// untested decision into one place, which is where it becomes worth testing.</para>
///
/// <para>Two points is the floor because one point draws nothing: a <c>MoveTo</c> with no
/// <c>LineTo</c> is an empty path in the raster renderer and a <c>d</c> attribute with a lone
/// <c>M</c> in the SVG one. Neither is harmful, but "skip it" and "emit an empty figure" are
/// different answers and only one of them is what both renderers actually do.</para>
/// </remarks>
public class AnnotationPointListTests
{
    private static PdfArray Numbers(params double[] values)
    {
        var arr = new PdfArray();
        foreach (double v in values) arr.Add(new PdfNumber(v));
        return arr;
    }

    private static PdfDictionary AnnotWith(PdfName key, PdfObject value)
    {
        var annot = new PdfDictionary();
        annot.Set(key, value);
        return annot;
    }

    // ---- /InkList ----------------------------------------------------

    [Fact]
    public void AStrokeWithTwoPointsIsDrawable()
    {
        var inkList = new PdfArray();
        inkList.Add(Numbers(0, 0, 10, 10));

        var strokes = AnnotationPointList.InkStrokes(null, AnnotWith(PdfName.InkList, inkList)).ToList();

        Assert.Single(strokes);
        Assert.Equal(4, strokes[0].Count);
    }

    /// <summary>
    /// The rule the mutation exposed. A stroke of one point — or of none — is skipped, and skipping
    /// it must not skip the rest: a producer that emits a stray degenerate stroke should lose that
    /// stroke, not the annotation.
    /// </summary>
    [Fact]
    public void ShortStrokesAreSkippedWithoutDiscardingTheDrawableOnes()
    {
        var inkList = new PdfArray();
        inkList.Add(Numbers(1, 1));                 // one point — not drawable
        inkList.Add(Numbers(0, 0, 10, 10, 20, 5));  // three points — drawable
        inkList.Add(new PdfArray());                // empty
        inkList.Add(Numbers(3, 3, 4, 4));           // two points — drawable

        var strokes = AnnotationPointList.InkStrokes(null, AnnotWith(PdfName.InkList, inkList)).ToList();

        Assert.Equal(2, strokes.Count);
        Assert.Equal(6, strokes[0].Count);
        Assert.Equal(4, strokes[1].Count);
    }

    /// <summary>A stroke entry that is not an array at all is skipped, not dereferenced.</summary>
    [Fact]
    public void ANonArrayStrokeEntryIsSkipped()
    {
        var inkList = new PdfArray();
        inkList.Add(new PdfNumber(7));
        inkList.Add(Numbers(0, 0, 10, 10));

        var strokes = AnnotationPointList.InkStrokes(null, AnnotWith(PdfName.InkList, inkList)).ToList();

        Assert.Single(strokes);
    }

    [Fact]
    public void AnAbsentOrMistypedInkListYieldsNothing()
    {
        Assert.Empty(AnnotationPointList.InkStrokes(null, new PdfDictionary()));
        Assert.Empty(AnnotationPointList.InkStrokes(null, AnnotWith(PdfName.InkList, new PdfNumber(1))));
    }

    // ---- /Vertices ---------------------------------------------------

    /// <summary>
    /// Asserts the <i>coordinates</i>, not just that something came back: the helper returns the
    /// caller's own array, so a version that manufactured an empty one would satisfy a null check.
    /// </summary>
    [Fact]
    public void VerticesWithAtLeastTwoPointsAreReturnedIntact()
    {
        var annot = AnnotWith(PdfName.Vertices, Numbers(0, 0, 10, 10));

        PdfArray verts = AnnotationPointList.Vertices(null, annot);

        Assert.Equal(4, verts.Count);
        Assert.Equal(new double[] { 0, 0, 10, 10 },
                     verts.Select(v => ((PdfNumber)v).DoubleValue).ToArray());
    }

    /// <summary>
    /// The same floor as a stroke, and the reason both live in one class: a Polygon with one vertex
    /// is not a smaller polygon, it is nothing to draw.
    /// </summary>
    [Theory]
    [InlineData(new double[] { 0, 0 })]
    [InlineData(new double[] { 0 })]
    [InlineData(new double[0])]
    public void VerticesTooShortToDrawAreRefused(double[] values)
    {
        var annot = AnnotWith(PdfName.Vertices, Numbers(values));
        Assert.Null(AnnotationPointList.Vertices(null, annot));
    }

    [Fact]
    public void AbsentVerticesAreRefused()
    {
        Assert.Null(AnnotationPointList.Vertices(null, new PdfDictionary()));
    }

    /// <summary>
    /// An odd trailing coordinate is ignored rather than completed with an implied zero — the
    /// caller's <c>i + 1 &lt; Count</c> step drops it. Pinned here because the array is still
    /// <i>accepted</i>, so a reader could reasonably expect the count to have been normalised.
    /// </summary>
    [Fact]
    public void AnOddTrailingCoordinateIsKeptInTheArrayAndLeftToTheCaller()
    {
        var annot = AnnotWith(PdfName.Vertices, Numbers(0, 0, 10, 10, 20));
        PdfArray verts = AnnotationPointList.Vertices(null, annot);

        Assert.NotNull(verts);
        Assert.Equal(5, verts.Count);
    }
}
