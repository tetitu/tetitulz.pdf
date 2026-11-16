// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.Fonts.Cff;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts.Cff;

/// <summary>
/// A CFF glyph's advance width, read from its Type 2 charstring: <see cref="CffCharstringScanner.TryReadWidth"/>
/// (the optional width operand before the first stack-clearing operator, TN #5177 §3.1),
/// <see cref="CffFont.TryGetAdvance"/> (nominalWidthX / defaultWidthX and the font matrix), and the two
/// parser facts that say when either may be trusted — <see cref="CffFont.WidthScaleIsKnown"/> and
/// <see cref="CffFont.HasExactGlyphNames"/>.
/// </summary>
public class CffAdvanceWidthTests
{
    // ---- Type 2 charstring bytes ----

    private static byte[] N(int v)
    {
        if (v >= -107 && v <= 107) return new[] { (byte)(v + 139) };
        if (v >= 108 && v <= 1131) { int a = v - 108; return new[] { (byte)((a >> 8) + 247), (byte)a }; }
        if (v >= -1131 && v <= -108) { int a = -v - 108; return new[] { (byte)((a >> 8) + 251), (byte)a }; }
        return new byte[] { 28, (byte)(v >> 8), (byte)v };
    }

    private static byte[] Op(params int[] bytes) => bytes.Select(b => (byte)b).ToArray();

    private static byte[] Cs(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static double? Width(byte[] charstring, IReadOnlyList<byte[]> localSubrs = null)
    {
        Assert.True(CffCharstringScanner.TryReadWidth(charstring, localSubrs, null, out double? width));
        return width;
    }

    private static bool Refuses(byte[] charstring)
        => !CffCharstringScanner.TryReadWidth(charstring, null, null, out _);

    // ---- TryReadWidth ----

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Hmoveto_TakesOneOperand_AnExtraOneIsTheWidth(bool withWidth)
    {
        byte[] cs = withWidth ? Cs(N(250), N(10), Op(22), Op(14)) : Cs(N(10), Op(22), Op(14));
        Assert.Equal(withWidth ? 250 : (double?)null, Width(cs));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Vmoveto_TakesOneOperand_AnExtraOneIsTheWidth(bool withWidth)
    {
        byte[] cs = withWidth ? Cs(N(-300), N(10), Op(4), Op(14)) : Cs(N(10), Op(4), Op(14));
        Assert.Equal(withWidth ? -300 : (double?)null, Width(cs));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rmoveto_TakesTwoOperands_AnExtraOneIsTheWidth(bool withWidth)
    {
        byte[] cs = withWidth ? Cs(N(640), N(10), N(20), Op(21), Op(14)) : Cs(N(10), N(20), Op(21), Op(14));
        Assert.Equal(withWidth ? 640 : (double?)null, Width(cs));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(18, false)]
    [InlineData(23, false)]
    public void StemOperators_TakePairs_AnOddOperandIsTheWidth(int stemOperator, bool withWidth)
    {
        byte[] stems = Cs(N(0), N(50), N(100), N(50));
        byte[] cs = withWidth
            ? Cs(N(480), stems, Op(stemOperator), N(10), Op(22), Op(14))
            : Cs(stems, Op(stemOperator), N(480), N(10), Op(21), Op(14));
        Assert.Equal(withWidth ? 480 : (double?)null, Width(cs));
    }

    [Fact]
    public void HintmaskFirst_ItsImplicitStemPairsPlusOneIsTheWidth()
    {
        Assert.Equal(520, Width(Cs(N(520), N(0), N(50), Op(19), Op(0x80), N(10), Op(22), Op(14))));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, 700)]
    [InlineData(4, null)]
    [InlineData(5, 700)]
    public void Endchar_TakesNoneOrFourOperands_AnExtraOneIsTheWidth(int operands, int? expected)
    {
        byte[] seac = Cs(N(0), N(0), N(65), N(194));   // adx ady bchar achar
        byte[] cs = operands switch
        {
            0 => Op(14),
            1 => Cs(N(700), Op(14)),
            4 => Cs(seac, Op(14)),
            _ => Cs(N(700), seac, Op(14)),
        };
        Assert.Equal(expected, Width(cs));
    }

    [Fact]
    public void AWidthInsideALocalSubroutine_IsRead()
    {
        // One local subroutine: bias 107, so operand -107 calls subroutine 0.
        var subrs = new List<byte[]> { Cs(N(333), N(10), Op(22), Op(11)) };
        Assert.Equal(333, Width(Cs(N(-107), Op(10), Op(14)), subrs));
    }

    [Fact]
    public void OperatorsAfterTheFirstStackClearingOne_AreNotRead()
    {
        // The arithmetic escape after hmoveto would refuse a full walk; the width walk has stopped.
        Assert.Equal(250, Width(Cs(N(250), N(10), Op(22), N(1), N(2), Op(12, 10), Op(14))));
    }

    [Fact]
    public void AnEmptyCharstring_IsRefused() => Assert.True(Refuses(new byte[0]));

    [Fact]
    public void OperandsWithNoOperator_AreRefused() => Assert.True(Refuses(Cs(N(250), N(10))));

    [Fact]
    public void DrawingBeforeTheFirstMoveto_IsRefused() => Assert.True(Refuses(Cs(N(10), N(10), Op(5), Op(14))));

    [Fact]
    public void FlexBeforeTheFirstMoveto_IsRefused()
        => Assert.True(Refuses(Cs(Enumerable.Repeat(N(1), 7).SelectMany(b => b).ToArray(), Op(12, 34), Op(14))));

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void EndcharWithAnOperandCountNoFormTakes_IsRefused(int operands)
        => Assert.True(Refuses(Cs(Enumerable.Repeat(N(1), operands).SelectMany(b => b).ToArray(), Op(14))));

    [Theory]
    [InlineData(22, 3)]
    [InlineData(4, 0)]
    [InlineData(21, 1)]
    [InlineData(21, 4)]
    public void AMovetoWithAnOperandCountItCannotTake_IsRefused(int moveOperator, int operands)
        => Assert.True(Refuses(Cs(Enumerable.Repeat(N(1), operands).SelectMany(b => b).ToArray(), Op(moveOperator), Op(14))));

    [Fact]
    public void DotsectionWithOperandsBeforeTheWidth_IsRefused()
        => Assert.True(Refuses(Cs(N(250), Op(12, 0), N(10), Op(22), Op(14))));

    [Fact]
    public void ArithmeticBeforeTheWidth_IsRefused()
        => Assert.True(Refuses(Cs(N(200), N(50), Op(12, 10), N(10), Op(22), Op(14))));

    // ---- CffFont.TryGetAdvance ----

    private static CffFont Build(double[] fontMatrix = null, bool duplicateName = false)
    {
        var font = new CffFontWriter.Font { NominalWidthX = 100, DefaultWidthX = 500, FontMatrix = fontMatrix };
        font.GlyphNames.Add(".notdef"); font.CharStrings.Add(Cs(Op(14)));
        font.GlyphNames.Add("A");       font.CharStrings.Add(Cs(N(350), N(0), Op(22), Op(14)));
        font.GlyphNames.Add(duplicateName ? "A" : "B"); font.CharStrings.Add(Cs(N(0), Op(22), Op(14)));
        CffFont parsed = CffParser.Parse(CffFontWriter.Build(font));
        Assert.NotNull(parsed);
        return parsed;
    }

    [Fact]
    public void TheAdvance_IsTheWidthOperandPlusNominalWidthX_OrDefaultWidthX()
    {
        CffFont font = Build();

        Assert.True(font.WidthScaleIsKnown);
        Assert.True(font.TryGetAdvance(1, out double withOperand));
        Assert.Equal(450, withOperand, 3);
        Assert.True(font.TryGetAdvance(2, out double withoutOperand));
        Assert.Equal(500, withoutOperand, 3);
    }

    [Fact]
    public void TheAdvance_IsScaledByTheFontMatrix()
    {
        CffFont font = Build(new[] { 0.0005, 0, 0, 0.0005, 0, 0 });

        Assert.True(font.WidthScaleIsKnown);
        Assert.True(font.TryGetAdvance(1, out double advance));
        Assert.Equal(225, advance, 3);
    }

    [Fact]
    public void AFontMatrixTheParserRejects_MakesTheScaleUnknown_AndNoAdvanceIsGiven()
    {
        CffFont font = Build(new double[] { 1, 0, 0, 1, 0, 0 });

        Assert.False(font.WidthScaleIsKnown);
        Assert.False(font.TryGetAdvance(1, out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void AGlyphIdOutsideTheCharStrings_GivesNoAdvance(int gid)
        => Assert.False(Build().TryGetAdvance(gid, out _));

    // ---- HasExactGlyphNames ----

    [Fact]
    public void ACustomCharsetNamingEveryGlyph_HasExactGlyphNames()
    {
        CffFont font = Build();
        Assert.True(font.HasExactGlyphNames);
        Assert.Equal(2, font.GlyphNameToGid["B"]);
    }

    [Fact]
    public void ACharsetNamingTwoGlyphsAlike_DoesNotHaveExactGlyphNames()
        => Assert.False(Build(duplicateName: true).HasExactGlyphNames);

    // ---- WidthScaleIsKnown for a CID-keyed font ----

    private static byte[] Real(params byte[] nibbleBytes) => new byte[] { 30 }.Concat(nibbleBytes).ToArray();

    /// <summary>FontMatrix [sx 0 0 sx 0 0] as DICT bytes, sx given as its encoded operand.</summary>
    private static byte[] Matrix(byte[] sx) => Cs(sx, Op(139, 139), sx, Op(139, 139), Op(12, 7));

    private static readonly byte[] Thousandth = Real(0xA0, 0x01, 0xFF);        // .001
    private static readonly byte[] HalfThousandth = Real(0xA0, 0x00, 0x5F);    // .0005
    private static readonly byte[] One = Op(140);                              // 1

    [Theory]
    [InlineData(null, null, true)]
    [InlineData(null, "0.001", true)]
    [InlineData("1", "0.001", true)]
    [InlineData("0.001", null, true)]
    [InlineData(null, "0.0005", false)]
    [InlineData("1", null, false)]
    [InlineData("2", "0.0005", true)]
    public void ACidKeyedFontsScale_IsKnownOnlyWhenEveryFontDictComesToTheScaleUsed(
        string top, string fd, bool known)
    {
        byte[] Encoded(string sx) => sx switch
        {
            null => null,
            "1" => Matrix(One),
            "2" => Matrix(Op(141)),
            "0.001" => Matrix(Thousandth),
            _ => Matrix(HalfThousandth),
        };

        CffFont font = CffParser.Parse(MinimalOpenTypeCff.BuildCidKeyed(new[] { 10, 20 }, Encoded(top), Encoded(fd)));
        Assert.NotNull(font);
        Assert.True(font.IsCidKeyed);
        Assert.Equal(known, font.WidthScaleIsKnown);
    }
}
