// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// One reader for an image's <c>/Decode</c> array (§8.9.5.2), shared by both renderers.
/// </summary>
/// <remarks>
/// <para>The two copies disagreed on what a <b>non-numeric entry</b> means. The raster reader
/// substituted <c>0.0</c>, so an unreadable upper bound collapsed every sample onto the lower one;
/// the SVG reader substituted <c>i % 2 == 0 ? 0 : 1</c> — the identity for a 0..1 space, but wrong
/// for an <c>/Indexed</c> image, whose default upper bound is 2<sup>bpc</sup> − 1.</para>
///
/// <para><b>Neither old answer survived.</b> A malformed entry now makes the whole array absent, so
/// the decision returns to the caller, which already knows the right identity for its own colour
/// space — exactly what each does when the array is genuinely missing.</para>
/// </remarks>
public sealed class DecodeArrayReaderTests
{
    /// <summary>An ordinary array is read through.</summary>
    [Fact]
    public void AWellFormedArrayIsReadThrough()
    {
        float[] result = PdfColor.DecodeArray(null, Array(new PdfNumber(0), new PdfNumber(0.5)));

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal(0f, result[0]);
        Assert.Equal(0.5f, result[1], 4);
    }

    /// <summary>
    /// A non-numeric entry makes the array absent, rather than being replaced by either copy's
    /// guess. <c>null</c> is what both callers already handle as "use my own identity".
    /// </summary>
    [Fact]
    public void AMalformedEntryMakesTheWholeArrayAbsent()
    {
        Assert.Null(PdfColor.DecodeArray(null, Array(new PdfNumber(0), new PdfName("Bogus"))));
    }

    /// <summary>An array too short to describe one component is absent too.</summary>
    [Fact]
    public void AnArrayShorterThanOnePairIsAbsent()
    {
        Assert.Null(PdfColor.DecodeArray(null, Array(new PdfNumber(0))));
        Assert.Null(PdfColor.DecodeArray(null, null));
    }

    private static PdfArray Array(params PdfObject[] items)
    {
        var arr = new PdfArray();
        foreach (var i in items) arr.Add(i);
        return arr;
    }
}
