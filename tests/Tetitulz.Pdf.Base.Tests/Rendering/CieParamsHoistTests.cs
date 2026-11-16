// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <c>CalRgbToSRgb</c> and <c>LabToSRgb</c> read <c>/Gamma</c>, <c>/WhitePoint</c>, <c>/Matrix</c>
/// and <c>/Range</c> out of the parameter dictionary on <b>every call</b> — three
/// indirect-reference resolutions and up to fifteen <c>PdfNumber</c> casts. That is right for an
/// <c>sc</c> fill, which happens once per operator, and wrong for an <b>image</b>, where the call
/// is per pixel and every read returns the same value. Their <c>CalGray</c> and
/// <c>Separation</c> siblings in the same <c>switch</c> already hoisted their work into a LUT.
///
/// <para><b>These tests are about the thing that must NOT change.</b> Hoisting the parse is a
/// pure allocation/CPU change, so the only interesting assertion is that every pixel comes out
/// byte-identical — including on the malformed inputs where the two spaces disagree about their
/// defaults.</para>
/// </summary>
public class CieParamsHoistTests
{
    private static PdfArray Arr(params object[] items)
    {
        var a = new PdfArray();
        foreach (object o in items)
        {
            if (o == null) a.Add(PdfNull.Instance);
            else a.Add(new PdfNumber(System.Convert.ToDouble(o)));
        }
        return a;
    }

    private static PdfDictionary CalRgbParams(PdfArray gamma, PdfArray wp, PdfArray matrix)
    {
        var d = new PdfDictionary();
        if (gamma != null) d.Set(PdfName.Gamma, gamma);
        if (wp != null) d.Set(PdfName.WhitePoint, wp);
        if (matrix != null) d.Set(PdfName.Matrix, matrix);
        return d;
    }

    public static TheoryData<PdfDictionary> CalRgbDictionaries() => new()
    {
        null,
        CalRgbParams(null, null, null),
        CalRgbParams(Arr(2.2, 2.2, 2.2), Arr(0.9505, 1.0, 1.089), null),
        CalRgbParams(Arr(1.8, 1.8, 1.8), Arr(0.9642, 1.0, 0.8251),
                     Arr(0.4124, 0.2126, 0.0193, 0.3576, 0.7152, 0.1192, 0.1805, 0.0722, 0.9505)),
        // Malformed: a /WhitePoint whose first element is not a number. Each space falls back to
        // its OWN default here - D65 for CalRGB, D50 for Lab - which is why the hoisted parse
        // needs two factories rather than one shared set of defaults.
        CalRgbParams(null, Arr(null, 1.0, 1.089), null),
        CalRgbParams(Arr(2.2, null, 2.2), null, null),
        CalRgbParams(null, null, Arr(1, 0, 0, 0, 1, 0, 0, 0)),   // too short - ignored entirely
    };

    /// <summary>
    /// The per-call overload and the hoisted one must agree on every input, malformed ones
    /// included. Anything else is a silent colour change on files nobody tests.
    /// </summary>
    [Theory]
    [MemberData(nameof(CalRgbDictionaries))]
    public void HoistedCalRgbParametersProduceTheSameColourAsThePerCallOverload(PdfDictionary p)
    {
        var hoisted = PdfColor.CieParams.ForCalRgb(p, null);

        for (int i = 0; i <= 16; i++)
        {
            float v = i / 16f;
            var comps = new[] { v, 1f - v, 0.5f };

            SKColor perCall = PdfColor.CalRgbToSRgb(comps, p, null);
            SKColor viaHoist = PdfColor.CalRgbToSRgb(comps, hoisted);

            Assert.Equal(perCall, viaHoist);
        }
    }

    public static TheoryData<PdfDictionary> LabDictionaries() => new()
    {
        null,
        CalRgbParams(null, null, null),
        CalRgbParams(null, Arr(0.9642, 1.0, 0.8251), null),
        CalRgbParams(null, Arr(0.9505, 1.0, 1.089), null),
        CalRgbParams(null, Arr(null, 1.0, 0.8251), null),    // D50 fallback per element
        CalRgbParams(null, Arr(0, 1.0, 0.8251), null),       // non-positive -> D50 path
        CalRgbParams(null, Arr(0.9642, 1.0), null),          // too short -> D50 path
    };

    [Theory]
    [MemberData(nameof(LabDictionaries))]
    public void HoistedLabParametersProduceTheSameColourAsThePerCallOverload(PdfDictionary p)
    {
        // A /Range as well, since Lab reads one and CalRGB does not.
        if (p != null) p.Set(PdfName.Range, Arr(-80, 80, -70, 70));

        var hoisted = PdfColor.CieParams.ForLab(p, null);

        foreach (float L in new[] { 0f, 25f, 50f, 100f, -10f, 120f })
            foreach (float a in new[] { -120f, -50f, 0f, 50f, 120f })
            {
                SKColor perCall = PdfColor.LabToSRgb(L, a, -a, p, null);
                SKColor viaHoist = PdfColor.LabToSRgb(L, a, -a, hoisted);

                Assert.Equal(perCall, viaHoist);
            }
    }

    /// <summary>
    /// The trap the two factories exist for, asserted directly rather than left implicit: CalRGB's
    /// white point defaults to <b>D65</b> and Lab's to <b>D50</b>, so a malformed
    /// <c>/WhitePoint</c> must not resolve to the same numbers for both. A single shared factory
    /// would have made these equal and changed Lab's colour on that input.
    /// </summary>
    [Fact]
    public void TheTwoSpacesKeepTheirOwnWhitePointDefaultsOnAMalformedEntry()
    {
        var malformed = CalRgbParams(null, Arr(null, null, null), null);

        var cal = PdfColor.CieParams.ForCalRgb(malformed, null);
        var lab = PdfColor.CieParams.ForLab(malformed, null);

        Assert.Equal(0.9505, cal.Xw, 4);   // D65
        Assert.Equal(0.9642, lab.Xw, 4);   // D50
        Assert.NotEqual(cal.Zw, lab.Zw);
    }
}
