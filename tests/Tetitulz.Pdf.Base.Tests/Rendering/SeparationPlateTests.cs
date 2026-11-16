// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="SeparationPlate.TryTint"/>: which tint a paint lays on one plate, and when it leaves
/// the plate alone — §8.6.6.4's colorant names and §11.7.4's overprint, rule by rule.
/// </summary>
public class SeparationPlateTests
{
    private static readonly SKColor White = SKColors.White;

    private static Dictionary<string, float> Set(params (string Name, float Tint)[] names)
    {
        var d = new Dictionary<string, float>();
        foreach (var (n, t) in names) d[n] = t;
        return d;
    }

    private static (bool Marks, float Tint) On(string plate, IReadOnlyDictionary<string, float> sep = null,
        float[] cmyk = null, SKColor? rgb = null, bool overprint = false, int opm = 0)
    {
        var p = new SeparationPlate(plate, new SpotColorantList());
        bool marks = p.TryTint(sep, cmyk, rgb ?? White, overprint, opm, out float tint);
        return (marks, tint);
    }

    [Fact]
    public void All_MarksEveryPlate_ProcessAndSpot_AtItsTint()
    {
        var all = Set(("All", 0.6f));
        Assert.Equal((true, 0.6f), On("Cyan", all));
        Assert.Equal((true, 0.6f), On("SPOT 185 C", all, overprint: true));
    }

    [Fact]
    public void ANamedColorant_TakesItsTint_AndKnocksOutTheOthers_UnlessItOverprints()
    {
        var spot = Set(("Spot1", 0.7f));
        Assert.Equal((true, 0.7f), On("Spot1", spot));
        Assert.Equal((true, 0f), On("Cyan", spot));                     // knocked out
        Assert.False(On("Cyan", spot, overprint: true).Marks);          // §11.7.4.5: left alone
        Assert.False(On("Cyan", spot, overprint: true, opm: 0).Marks);  // whatever the mode
    }

    [Fact]
    public void None_NeverMarks_ButADeviceNWithNoneBesideANamedColorant_StillMarksThatOne()
    {
        Assert.False(On("Cyan", Set(("None", 1f))).Marks);
        Assert.False(On("Black", Set(("None", 1f))).Marks);
        Assert.Equal((true, 0.4f), On("Spot2", Set(("None", 1f), ("Spot2", 0.4f))));
        Assert.Equal((true, 0f), On("Cyan", Set(("None", 1f), ("Spot2", 0.4f))));
    }

    [Fact]
    public void DeviceCmyk_PutsEachComponentOnItsPlate()
    {
        float[] rich = { 0.6f, 0.5f, 0.4f, 1f };
        Assert.Equal((true, 0.6f), On("Cyan", cmyk: rich));
        Assert.Equal((true, 0.5f), On("Magenta", cmyk: rich));
        Assert.Equal((true, 0.4f), On("Yellow", cmyk: rich));
        Assert.Equal((true, 1f), On("Black", cmyk: rich));
    }

    [Fact]
    public void DeviceCmyk_UnderNonzeroOverprintMode_LeavesAZeroComponentsPlateAlone_ModeZeroKnocksItOut()
    {
        float[] yellow = { 0f, 0f, 1f, 0f };
        Assert.False(On("Cyan", cmyk: yellow, overprint: true, opm: 1).Marks);
        Assert.Equal((true, 1f), On("Yellow", cmyk: yellow, overprint: true, opm: 1));
        Assert.Equal((true, 0f), On("Cyan", cmyk: yellow, overprint: true, opm: 0));
        Assert.Equal((true, 0f), On("Cyan", cmyk: yellow, overprint: false, opm: 1));
    }

    [Fact]
    public void ASpaceNamingNoSpot_KnocksOutASpotPlate_OrLeavesItAloneWhenItOverprints()
    {
        float[] cyan = { 1f, 0f, 0f, 0f };
        Assert.Equal((true, 0f), On("Spot1", cmyk: cyan));
        Assert.False(On("Spot1", cmyk: cyan, overprint: true).Marks);
        Assert.Equal((true, 0f), On("Spot1", rgb: new SKColor(255, 0, 0)));
        Assert.False(On("Spot1", rgb: new SKColor(255, 0, 0), overprint: true).Marks);
    }

    [Fact]
    public void Grey_LandsOnBlackAlone_AndRgbGoesThroughTheInverseOfTheCmykConversion()
    {
        var grey = new SKColor(64, 64, 64);   // 0.25 g
        Assert.Equal(0f, On("Cyan", rgb: grey).Tint);
        Assert.Equal(0f, On("Yellow", rgb: grey).Tint);
        Assert.Equal(1f - 64f / 255f, On("Black", rgb: grey).Tint, 4);

        // Pure red: k 0, magenta and yellow full, cyan none (RgbToCmykExactInverse).
        var red = new SKColor(255, 0, 0);
        Assert.Equal((0f, 1f, 1f, 0f), (On("Cyan", rgb: red).Tint, On("Magenta", rgb: red).Tint,
                                       On("Yellow", rgb: red).Tint, On("Black", rgb: red).Tint));
    }

    [Fact]
    public void TheSpotList_KeepsFirstSeenOrder_AndLeavesOutProcessAllAndNone()
    {
        var spots = new SpotColorantList();
        var p = new SeparationPlate("Cyan", spots);
        p.TryTint(Set(("Spot2", 1f), ("Cyan", 1f)), null, White, false, 0, out _);
        p.TryTint(Set(("All", 1f)), null, White, false, 0, out _);
        p.TryTint(Set(("None", 1f), ("Spot1", 1f), ("Spot2", 1f)), null, White, false, 0, out _);
        Assert.Equal(new[] { "Spot2", "Spot1" }, spots.Names);
    }

    [Fact]
    public void APlatesColour_IsGreyOneMinusTheTint()
    {
        Assert.Equal(new SKColor(191, 191, 191, 255), SeparationPlate.PlateColor(0.25f, 255));
        Assert.Equal(new SKColor(255, 255, 255, 128), SeparationPlate.PlateColor(0f, 128));
        Assert.Equal(new SKColor(0, 0, 0, 255), SeparationPlate.PlateColor(1f, 255));
    }
}
