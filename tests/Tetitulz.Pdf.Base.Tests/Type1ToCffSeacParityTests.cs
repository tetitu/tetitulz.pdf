// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.Core.Fonts.Type1;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// A Type 1 font converted to CFF draws its accented composites where the Type 1 renderer draws
/// them. The two renderers are independent implementations, so the parity is the evidence. The
/// Type 1 one is measured against Skia; the CFF one places a Type 2 seac accent at
/// <c>adx</c>, as Skia's CFF driver does.
/// </summary>
public class Type1ToCffSeacParityTests
{
    /// <summary>
    /// Base sidebearing 10, accent sidebearing (asb) 100, adx 50. The Type 1 renderer puts the
    /// accent's 50-unit bar at x 60 to 110, and so must the converted font — not at 150 to 200,
    /// widening the composite's bounds by 90 units.
    /// </summary>
    [Fact]
    public void AConvertedSeacCompositeDrawsWhereTheType1OneDoes()
    {
        var charStrings = new Dictionary<string, byte[]>
        {
            [".notdef"] = Cs(Num(0), Num(0), Op(13), Op(14)),
            // A: a 50-unit square from its sidebearing at x 10.
            ["A"] = Cs(Num(10), Num(600), Op(13), Num(0), Num(0), Op(21),
                       Num(50), Num(0), Op(5), Num(0), Num(50), Op(5), Num(-50), Num(0), Op(5), Op(9), Op(14)),
            // acute: a 50-unit bar from its sidebearing at x 100, y 400.
            ["acute"] = Cs(Num(100), Num(300), Op(13), Num(0), Num(400), Op(21),
                           Num(50), Num(0), Op(5), Num(0), Num(50), Op(5), Num(-50), Num(0), Op(5), Op(9), Op(14)),
            ["Aacute"] = Cs(Num(10), Num(600), Op(13),
                            Num(100), Num(50), Num(200), Num(65), Num(194), Op(12), Op(6)),
        };
        var type1 = new Type1Font(new string[256], charStrings, new List<byte[]>());

        var type1Bounds = Type1CharstringEvaluator.Evaluate(type1, "Aacute").Path.Bounds;
        Assert.Equal(110f, type1Bounds.Right, 1);              // potency: the accent sets the right edge

        CffFont cff = CffParser.Parse(Type1ToCffConverter.Convert(type1, "Parity"));
        Assert.NotNull(cff);
        var cffBounds = Type2CharstringEvaluator.Evaluate(cff, cff.GlyphNameToGid["Aacute"]).Path.Bounds;

        Assert.Equal(type1Bounds.Left, cffBounds.Left, 1);
        Assert.Equal(type1Bounds.Right, cffBounds.Right, 1);
        Assert.Equal(type1Bounds.Top, cffBounds.Top, 1);
        Assert.Equal(type1Bounds.Bottom, cffBounds.Bottom, 1);
    }

    private static byte[] Cs(params byte[][] parts)
    {
        var all = new List<byte>();
        foreach (byte[] part in parts) all.AddRange(part);
        return all.ToArray();
    }

    private static byte[] Op(int op) => new[] { (byte)op };

    private static byte[] Num(int v)
    {
        if (v >= -107 && v <= 107) return new[] { (byte)(v + 139) };
        if (v >= 108 && v <= 1131) { int a = v - 108; return new[] { (byte)((a >> 8) + 247), (byte)a }; }
        int b = -v - 108;
        return new[] { (byte)((b >> 8) + 251), (byte)b };
    }
}
