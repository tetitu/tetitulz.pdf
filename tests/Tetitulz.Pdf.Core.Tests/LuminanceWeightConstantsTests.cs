// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Core.Constants;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// Pins the ITU-R BT.601 luminance weights in <see cref="PdfColorSpaceNames"/>, which replaced five
/// independent copies scattered across Base, the Maintenance plugin and the PostScript plugin
/// (GAPS.md §2b).
/// </summary>
/// <remarks>
/// The point of these tests is <b>not</b> that 0.299 equals 0.299. It is that the three numeric
/// representations are pinned as <i>distinct</i>, so a later "simplification" down to a single set
/// fails here with an explanation rather than silently shifting output by a quantisation level.
/// </remarks>
public class LuminanceWeightConstantsTests
{
    [Fact]
    public void FloatAndDoubleWeightSets_AreNotInterchangeable()
    {
        // This is the whole reason both sets exist. 0.299 is not representable in binary
        // floating point, so narrowing to float and widening back does not round-trip:
        // (double)0.299f is 0.29899999499320984. Any call site that swaps one set for the
        // other changes its arithmetic — by a tiny amount, but enough to move a value across
        // a byte quantisation boundary.
        Assert.NotEqual(PdfColorSpaceNames.LumaRed,   (double)PdfColorSpaceNames.LumaRedF);
        Assert.NotEqual(PdfColorSpaceNames.LumaGreen, (double)PdfColorSpaceNames.LumaGreenF);
        Assert.NotEqual(PdfColorSpaceNames.LumaBlue,  (double)PdfColorSpaceNames.LumaBlueF);

        // ...but they are the same weights, so the gap must be tiny. A pair that drifted apart
        // for any other reason would blow this bound.
        Assert.True(System.Math.Abs(PdfColorSpaceNames.LumaRed - PdfColorSpaceNames.LumaRedF) < 1e-7);
        Assert.True(System.Math.Abs(PdfColorSpaceNames.LumaGreen - PdfColorSpaceNames.LumaGreenF) < 1e-7);
        Assert.True(System.Math.Abs(PdfColorSpaceNames.LumaBlue - PdfColorSpaceNames.LumaBlueF) < 1e-7);
    }

    [Fact]
    public void WeightsSumToOne_SoWhiteMapsToWhite()
    {
        // The defining property: a fully white pixel must yield the maximum grey level. If a
        // weight is ever mistyped this is what catches it.
        Assert.Equal(1.0, PdfColorSpaceNames.LumaRed + PdfColorSpaceNames.LumaGreen + PdfColorSpaceNames.LumaBlue, 12);
        Assert.Equal(PdfColorSpaceNames.LumaScale,
            PdfColorSpaceNames.LumaRedPermille + PdfColorSpaceNames.LumaGreenPermille + PdfColorSpaceNames.LumaBluePermille);
    }

    [Fact]
    public void PermilleWeights_MatchTheDoubleWeights()
    {
        Assert.Equal(PdfColorSpaceNames.LumaRed,   PdfColorSpaceNames.LumaRedPermille   / (double)PdfColorSpaceNames.LumaScale, 12);
        Assert.Equal(PdfColorSpaceNames.LumaGreen, PdfColorSpaceNames.LumaGreenPermille / (double)PdfColorSpaceNames.LumaScale, 12);
        Assert.Equal(PdfColorSpaceNames.LumaBlue,  PdfColorSpaceNames.LumaBluePermille  / (double)PdfColorSpaceNames.LumaScale, 12);
    }

    [Fact]
    public void PermilleWeights_AgreeWithTruncatingTheDoubles()
    {
        // They do agree — 0.299 * 1000 rounds to exactly 299.0 in IEEE 754 double, so the
        // derivation and the literals coincide. The literals are still written out rather than
        // computed, because the C# spec permits constant expressions on `double` to be folded at
        // higher precision than double, which would make the derived value implementation-defined.
        // This test pins that the two agree today, so the literals cannot silently rot.
        Assert.Equal(PdfColorSpaceNames.LumaRedPermille,
            (int)(PdfColorSpaceNames.LumaRed * PdfColorSpaceNames.LumaScale));
        Assert.Equal(PdfColorSpaceNames.LumaGreenPermille,
            (int)(PdfColorSpaceNames.LumaGreen * PdfColorSpaceNames.LumaScale));
        Assert.Equal(PdfColorSpaceNames.LumaBluePermille,
            (int)(PdfColorSpaceNames.LumaBlue * PdfColorSpaceNames.LumaScale));
    }

    [Fact]
    public void IntegerLuminance_TruncatesWhereTheFloatFormRounds()
    {
        // The two forms genuinely disagree, and the disagreement is load-bearing:
        // PdfColorSpaceConverter.RgbToGray must stay integer or its output shifts a level.
        // r=g=b=128 is not the interesting case; a value that lands on a fraction is.
        const int r = 100, g = 150, b = 200;

        int integerForm = (r * PdfColorSpaceNames.LumaRedPermille
                         + g * PdfColorSpaceNames.LumaGreenPermille
                         + b * PdfColorSpaceNames.LumaBluePermille) / PdfColorSpaceNames.LumaScale;

        int roundedFloatForm = (int)(PdfColorSpaceNames.LumaRedF   * r
                                   + PdfColorSpaceNames.LumaGreenF * g
                                   + PdfColorSpaceNames.LumaBlueF  * b + 0.5f);

        // 0.299*100 + 0.587*150 + 0.114*200 = 140.75
        Assert.Equal(140, integerForm);        // truncated
        Assert.Equal(141, roundedFloatForm);   // rounded
        Assert.NotEqual(integerForm, roundedFloatForm);
    }
}
