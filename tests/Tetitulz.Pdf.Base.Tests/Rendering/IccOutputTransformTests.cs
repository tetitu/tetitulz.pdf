// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SkiaSharp;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="IccOutputTransform"/>: sRGB into an output profile, with black-point compensation and
/// white kept white. Against the reference renderer.3 (the reference colour engine) where the host has RSWOP.icm —
/// <c>-sDEVICE=separation output -sOutputICCProfile=RSWOP.icm</c> with <c>-dRenderIntent</c> and
/// <c>-dBlackPtComp</c>, eight RGB swatches — and on synthetic profiles everywhere.
/// </summary>
public class IccOutputTransformTests
{
    private static readonly SKColor[] Swatches =
    {
        new SKColor(255, 0, 0), new SKColor(0, 255, 0), new SKColor(0, 0, 255), new SKColor(255, 255, 0),
        new SKColor(128, 128, 128), new SKColor(51, 102, 204), new SKColor(0, 0, 0), new SKColor(255, 255, 255),
    };

    /// <summary>The reference renderer's CMYK (×255) for <see cref="Swatches"/>: relative colorimetric with BPC (its default).</summary>
    private static readonly int[][] GsRelativeBpc =
    {
        new[] { 0, 255, 252, 2 }, new[] { 161, 0, 255, 0 }, new[] { 252, 163, 0, 37 }, new[] { 12, 0, 248, 0 },
        new[] { 63, 53, 50, 88 }, new[] { 238, 113, 0, 3 }, new[] { 5, 7, 6, 245 }, new[] { 0, 0, 0, 0 },
    };

    /// <summary>Relative colorimetric without BPC — the bare <c>B2A1</c> table, white fixed up.</summary>
    private static readonly int[][] GsRelative =
    {
        new[] { 0, 255, 255, 3 }, new[] { 167, 0, 255, 0 }, new[] { 253, 172, 0, 54 }, new[] { 11, 0, 249, 0 },
        new[] { 63, 53, 50, 94 }, new[] { 242, 115, 0, 6 }, new[] { 0, 0, 0, 255 }, new[] { 0, 0, 0, 0 },
    };

    /// <summary>Perceptual (BPC on, which changes nothing: RSWOP's perceptual black is L* 0).</summary>
    private static readonly int[][] GsPerceptual =
    {
        new[] { 0, 255, 255, 2 }, new[] { 166, 0, 255, 0 }, new[] { 253, 168, 0, 43 }, new[] { 11, 0, 249, 0 },
        new[] { 63, 53, 50, 93 }, new[] { 241, 113, 0, 6 }, new[] { 0, 0, 0, 255 }, new[] { 0, 0, 0, 0 },
    };

    private static void AssertMatches(IccOutputTransform t, int[][] expected, string what)
    {
        for (int i = 0; i < Swatches.Length; i++)
        {
            var device = new float[4];
            Assert.True(t.TryConvert(Swatches[i], device));
            for (int k = 0; k < 4; k++)
            {
                int got = PdfColor.Channel(device[k]);
                Assert.True(Math.Abs(got - expected[i][k]) <= 1,
                    $"{what}, swatch {i} {Swatches[i]}, channel {k}: {got}, reference {expected[i][k]}");
            }
        }
    }

    [RealIccProfileFact("RSWOP.icm")]
    public void RelativeColorimetricWithBlackPointCompensation_MatchesTheReferenceEngine()
    {
        byte[] p = File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm"));
        AssertMatches(IccOutputTransform.TryCreate(p, IccRenderingIntent.MediaRelativeColorimetric, true, out _), GsRelativeBpc, "relative + BPC");
        AssertMatches(IccOutputTransform.TryCreate(p, IccRenderingIntent.MediaRelativeColorimetric, false, out _), GsRelative, "relative");
        AssertMatches(IccOutputTransform.TryCreate(p, IccRenderingIntent.Perceptual, true, out _), GsPerceptual, "perceptual");
    }

    /// <summary>
    /// RSWOP's relative black point by the destination method: the midrange of its relative round
    /// trip is not straight, so the quadratic fit decides — L* 12.66, the value that reproduces
    /// the reference colour engine's swatches above (characterisation, not a second oracle).
    /// </summary>
    [RealIccProfileFact("RSWOP.icm")]
    public void RswopsRelativeBlackPoint_IsFoundByTheFit()
    {
        byte[] p = File.ReadAllBytes(RealIccProfile.Path("RSWOP.icm"));
        Assert.True(IccBlackPoint.TryDetectDestination(p, IccRenderingIntent.MediaRelativeColorimetric, out double l));
        Assert.InRange(l, 12.6, 12.7);
        Assert.True(IccBlackPoint.TryDetectDestination(p, IccRenderingIntent.Perceptual, out double lp));
        Assert.Equal(0, lp, 3);
    }

    // ------------------------------------------------------------------ the fit

    [Fact]
    public void TheQuadraticFit_GivesTheLargerRootForAnUpwardCurve_AndTheSmallerForADownwardOne()
    {
        var x = new double[21];
        var up = new double[21];
        var down = new double[21];
        for (int i = 0; i < 21; i++)
        {
            x[i] = 10 + i;
            up[i] = ((x[i] - 10) * (x[i] - 10) - 4) / 100;      // roots 8 and 12
            down[i] = 25 - (x[i] - 30) * (x[i] - 30);           // roots 25 and 35
        }
        Assert.Equal(12, IccBlackPoint.RootOfQuadraticFit(21, x, up), 6);
        Assert.Equal(25, IccBlackPoint.RootOfQuadraticFit(21, x, down), 6);
    }

    /// <summary>
    /// The fit's three refusals, each the reference colour engine's: fewer than four points, no real root, and a line —
    /// for which the reference colour engine writes <c>cmsmin(0, cmsmax(50, -c/b))</c>, which is 0 whatever the line.
    /// </summary>
    [Fact]
    public void TheQuadraticFit_AnswersZero_ForTooFewPoints_NoRealRoot_OrALine()
    {
        double[] x = { 10, 11, 12, 13, 14 };
        Assert.Equal(0, IccBlackPoint.RootOfQuadraticFit(3, x, new double[] { 1, 2, 3 }));
        Assert.Equal(0, IccBlackPoint.RootOfQuadraticFit(5, x, new double[] { 101, 122, 145, 170, 197 }));   // (x+0)²+1: no root
        Assert.Equal(0, IccBlackPoint.RootOfQuadraticFit(5, x, new double[] { 0.1, 0.11, 0.12, 0.13, 0.14 }));
    }

    // ------------------------------------------------------------------ synthetic profiles

    /// <summary>
    /// White is kept white: a table that puts half a cyan everywhere still leaves sRGB white with no
    /// ink, while a colour a level off white takes the table's answer.
    /// </summary>
    [Fact]
    public void WhiteIsKeptWhite_EvenWhenTheTableInksIt()
    {
        byte[] p = SyntheticCmykProfile.Build(b2a: SyntheticCmykProfile.ConstantCyan(0x8000));
        var t = IccOutputTransform.TryCreate(p, IccRenderingIntent.MediaRelativeColorimetric, false, out string reason);
        Assert.NotNull(t);
        var device = new float[4];
        Assert.True(t.TryConvert(SKColors.White, device));
        Assert.Equal(new float[4], device);
        Assert.True(t.TryConvert(new SKColor(254, 255, 255), device));
        Assert.Equal(128, PdfColor.Channel(device[0]));
    }

    /// <summary>
    /// Black-point compensation on a profile whose relative black is not L* 0 lifts what sRGB black
    /// asks of the table: the toe profile's black point is above 0, so black lands lighter (less K)
    /// with compensation than without, and white is unchanged either way.
    /// </summary>
    [Fact]
    public void BlackPointCompensation_LandsBlackOnTheProfilesBlack()
    {
        byte[] p = SyntheticCmykProfile.Build(b2a: SyntheticCmykProfile.KFromLightness(), a2b: SyntheticCmykProfile.LightnessFromKWithToe());
        Assert.True(IccBlackPoint.TryDetectDestination(p, IccRenderingIntent.MediaRelativeColorimetric, out double l));
        Assert.InRange(l, 1, 50);

        var with = IccOutputTransform.TryCreate(p, IccRenderingIntent.MediaRelativeColorimetric, true, out _);
        var without = IccOutputTransform.TryCreate(p, IccRenderingIntent.MediaRelativeColorimetric, false, out _);
        var a = new float[4];
        var b = new float[4];
        with.TryConvert(SKColors.Black, a);
        without.TryConvert(SKColors.Black, b);
        Assert.Equal(255, PdfColor.Channel(b[3]));
        Assert.True(a[3] < b[3] - 0.02f, $"compensated K {a[3]} should sit below the uncompensated {b[3]}");
        Assert.True(with.BlackPoint.Y > 0);
        Assert.Equal((0.0, 0.0, 0.0), without.BlackPoint);
    }

    /// <summary>Absolute colorimetric reads the relative table and is never compensated — the reference colour engine leaves BPC off for it.</summary>
    [Fact]
    public void AbsoluteColorimetric_IsNeverCompensated()
    {
        byte[] p = SyntheticCmykProfile.Build(b2a: SyntheticCmykProfile.KFromLightness(), a2b: SyntheticCmykProfile.LightnessFromKWithToe());
        var absolute = IccOutputTransform.TryCreate(p, IccRenderingIntent.AbsoluteColorimetric, true, out _);
        var relative = IccOutputTransform.TryCreate(p, IccRenderingIntent.MediaRelativeColorimetric, true, out _);
        Assert.Equal((0.0, 0.0, 0.0), absolute.BlackPoint);
        Assert.True(relative.BlackPoint.Y > 0);
    }

    [Fact]
    public void AProfileThatIsNotADestination_IsRefusedWithItsReason()
    {
        Assert.Null(IccOutputTransform.TryCreate(new byte[300], IccRenderingIntent.Perceptual, true, out string reason));
        Assert.False(string.IsNullOrEmpty(reason));
    }
}
