// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests;

/// <summary>
/// <see cref="SaturatingCast"/> answers what .NET 10's own explicit conversion to <c>int</c>,
/// <c>long</c> and <c>uint</c> answers, for every value — so routing the library's conversions to
/// those through it changed nothing on this runtime, and gave .NET Framework (where an out-of-range
/// cast is the type's minimum) the same integers. The net462 half of the claim is the
/// package-consumption run's (tools/package-consumption).
/// </summary>
/// <remarks>
/// <b>Not for the small types.</b> .NET 10 saturates a conversion to a 32- or 64-bit integer, but a
/// cast to <c>byte</c> or <c>ushort</c> still wraps — <c>(byte)300.0</c> is 44, <c>(ushort)-1.0</c>
/// is 65535 — so a colour channel past its range came out dark. Those saturate here, measured
/// against the rule written out independently (<see cref="Clamped"/>), never against the cast.
/// </remarks>
public sealed class SaturatingCastTests
{
    /// <summary>The edges, every bound and its neighbours, the non-finite values, and a spread of magnitudes.</summary>
    private static IEnumerable<double> Values()
    {
        double[] edges =
        {
            0, -0.0, 0.5, -0.5, 0.9999999, -0.9999999, 1, -1, 254.5, 255, 255.5, 256, 65535.5, 65536,
            int.MaxValue, int.MaxValue + 0.5, int.MaxValue + 1.0, int.MinValue, int.MinValue - 0.5, int.MinValue - 1.0,
            uint.MaxValue, uint.MaxValue + 1.0, 9223372036854775807.0, -9223372036854775808.0, 1e19, -1e19,
            1e300, -1e300, double.MaxValue, double.MinValue, double.Epsilon,
            double.NaN, double.PositiveInfinity, double.NegativeInfinity,
        };
        foreach (double e in edges) yield return e;

        var random = new Random(20261027);
        for (int i = 0; i < 20000; i++)
        {
            double magnitude = Math.Pow(10, random.NextDouble() * 24 - 4);   // 1e-4 … 1e20
            yield return (random.Next(2) == 0 ? -1 : 1) * magnitude;
        }
    }

    [Fact]
    public void WideConversions_AnswerAsTheRuntimesCast_AndNarrowOnesClampWhereTheCastWraps()
    {
        int compared = 0;
        foreach (double v in Values())
        {
            Assert.True((int)v == SaturatingCast.ToInt(v), $"ToInt({v:R}): cast {(int)v}, helper {SaturatingCast.ToInt(v)}");
            Assert.True((long)v == SaturatingCast.ToLong(v), $"ToLong({v:R}): cast {(long)v}, helper {SaturatingCast.ToLong(v)}");
            Assert.True((uint)v == SaturatingCast.ToUInt(v), $"ToUInt({v:R}): cast {(uint)v}, helper {SaturatingCast.ToUInt(v)}");
            Assert.True(Clamped(v, ushort.MaxValue) == SaturatingCast.ToUShort(v), $"ToUShort({v:R}): {SaturatingCast.ToUShort(v)}");
            Assert.True(Clamped(v, byte.MaxValue) == SaturatingCast.ToByte(v), $"ToByte({v:R}): {SaturatingCast.ToByte(v)}");
            float f = (float)v;
            Assert.True((int)f == SaturatingCast.ToInt(f), $"ToInt((float){v:R})");
            compared++;
        }
        Assert.True(compared > 20000);
    }

    /// <summary>Truncation toward zero, then the range [0, <paramref name="max"/>]; 0 for NaN.</summary>
    private static double Clamped(double v, double max)
        => double.IsNaN(v) ? 0 : Math.Min(max, Math.Max(0, Math.Truncate(v)));

    /// <summary>The answers themselves, pinned — a comparison with the runtime cannot see a runtime that changed.</summary>
    [Fact]
    public void TheAnswers_ArePinned()
    {
        Assert.Equal(int.MaxValue, SaturatingCast.ToInt(1e10));
        Assert.Equal(int.MinValue, SaturatingCast.ToInt(-1e10));
        Assert.Equal(0, SaturatingCast.ToInt(double.NaN));
        Assert.Equal(-7, SaturatingCast.ToInt(-7.9));
        Assert.Equal(long.MaxValue, SaturatingCast.ToLong(1e30));
        Assert.Equal(long.MinValue, SaturatingCast.ToLong(double.NegativeInfinity));
        Assert.Equal(0u, SaturatingCast.ToUInt(-5));
        Assert.Equal(uint.MaxValue, SaturatingCast.ToUInt(1e12));
        Assert.Equal(ushort.MaxValue, SaturatingCast.ToUShort(70000));
        Assert.Equal((byte)255, SaturatingCast.ToByte(300));
        Assert.Equal((byte)0, SaturatingCast.ToByte(-1));
        Assert.Equal((byte)200, SaturatingCast.ToByte(200.99));
    }
}
