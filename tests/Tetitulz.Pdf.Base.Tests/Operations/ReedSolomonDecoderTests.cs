// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// GF(256) and the Reed–Solomon decoder.
/// </summary>
/// <remarks>
/// <para><b>Encoding and decoding are different algorithms over one field, which is what makes
/// "damage it and recover" worth something.</b> Encoding is a single polynomial division; decoding
/// is syndromes, Berlekamp–Massey, a Chien search and Forney. They share the exp/log tables and
/// nothing else — so unlike a writer and a reader over one lookup table, this pair cannot agree by
/// sharing a mistake. The tables themselves are pinned separately below, for exactly the reason they
/// could not be.</para>
///
/// <para><b>And the correction bound is a fact, not a choice.</b> A code with <i>n</i> check symbols
/// corrects <i>n</i>/2 errors and no more; past that the received word may be nearer a different
/// valid codeword, and a decoder that answers anyway returns confident wrong data. Both halves of
/// that are asserted — recovery up to the bound, refusal past it.</para>
/// </remarks>
public class ReedSolomonDecoderTests
{
    // ================================================================
    //  The field itself
    // ================================================================

    /// <summary>Exp and log invert each other across every non-zero element.</summary>
    /// <remarks>
    /// <b>The tables are built by one loop that fills both</b>, so a wrong primitive polynomial or a
    /// mis-stepped loop corrupts them together and consistently — every arithmetic result would then
    /// be wrong in a way the arithmetic itself cannot see. This is the check that does not go
    /// through the arithmetic.
    /// </remarks>
    [Theory]
    [InlineData(GaloisField256.QrPrimitive)]
    [InlineData(GaloisField256.DataMatrixPrimitive)]
    public void ExpAndLogAreInverseAcrossTheField(int primitive)
    {
        var field = new GaloisField256(primitive, 8);

        for (int power = 0; power < 255; power++)
            Assert.Equal(power, field.Log(field.Exp(power)));
    }

    /// <summary>α generates the whole multiplicative group: 255 distinct powers, then it repeats.</summary>
    /// <remarks>
    /// <b>This is what "primitive" means, and a polynomial that is not primitive still produces a
    /// plausible-looking table.</b> It would simply cycle early — say every 85 powers — and every
    /// codeword built on it would be wrong in a way that is perfectly self-consistent.
    /// </remarks>
    [Theory]
    [InlineData(GaloisField256.QrPrimitive)]
    [InlineData(GaloisField256.DataMatrixPrimitive)]
    public void AlphaIsPrimitiveSoItsPowersCoverEveryNonZeroElement(int primitive)
    {
        var field = new GaloisField256(primitive, 8);
        var seen = new HashSet<int>();

        for (int power = 0; power < 255; power++)
            Assert.True(seen.Add(field.Exp(power)),
                        $"α^{power} repeats an earlier power; the polynomial is not primitive");

        Assert.Equal(255, seen.Count);
        Assert.DoesNotContain(0, seen);
        Assert.Equal(1, field.Exp(255));
    }

    /// <summary>Multiplication has inverses, so the elements really do form a field.</summary>
    [Theory]
    [InlineData(GaloisField256.QrPrimitive)]
    [InlineData(GaloisField256.DataMatrixPrimitive)]
    public void EveryNonZeroElementHasAMultiplicativeInverse(int primitive)
    {
        var field = new GaloisField256(primitive, 8);

        for (int value = 1; value < 256; value++)
            Assert.Equal(1, field.Multiply(value, field.Inverse(value)));
    }

    // ================================================================
    //  Correcting
    // ================================================================

    /// <summary>A codeword with no errors is left exactly as it was.</summary>
    /// <remarks>
    /// <b>The acceptance control.</b> A decoder that "corrects" a clean word is worse than one that
    /// cannot correct at all, and every damaged-word test below would still pass with that defect.
    /// </remarks>
    [Fact]
    public void AnUndamagedCodewordIsUnchangedAndReportsNoErrors()
    {
        var field = new GaloisField256(GaloisField256.QrPrimitive, 26);
        int[] codeword = Codeword(field, Data(16), ecLength: 10);
        var copy = (int[])codeword.Clone();

        Assert.True(ReedSolomonDecoder.TryCorrect(field, codeword, 10, out int corrected));

        Assert.Equal(0, corrected);
        Assert.Equal(copy, codeword);
    }

    /// <summary>Damage up to the code's limit is repaired exactly.</summary>
    /// <remarks>
    /// Every count from one error up to the full <i>n</i>/2 is exercised, because the algorithm's
    /// awkward cases are at the ends: one error takes a degree-1 locator, and the maximum takes the
    /// longest recurrence Berlekamp–Massey will build.
    /// </remarks>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void DamageWithinTheCodesLimitIsRepairedExactly(int errors)
    {
        const int EcLength = 10;
        var field = new GaloisField256(GaloisField256.QrPrimitive, 26);

        int[] original = Codeword(field, Data(16), EcLength);
        var damaged = (int[])original.Clone();

        var random = new Random(errors * 7919);
        foreach (int position in Positions(random, damaged.Length, errors))
            damaged[position] ^= 1 + random.Next(255);   // never a no-op

        Assert.True(ReedSolomonDecoder.TryCorrect(field, damaged, EcLength, out int corrected),
                    $"{errors} error(s) should be within a {EcLength}-symbol code's reach");

        Assert.Equal(errors, corrected);
        Assert.Equal(original, damaged);
    }

    /// <summary>Errors in the check symbols are repaired too, not only in the data.</summary>
    /// <remarks>
    /// <b>A codeword is a codeword.</b> Reed–Solomon draws no distinction between the two halves,
    /// and a decoder that quietly assumed damage lands in the data would still pass every test that
    /// only ever damages the data. The scanner has no idea which half a smudge fell on.
    /// </remarks>
    [Fact]
    public void DamageToTheCheckSymbolsIsAlsoRepaired()
    {
        const int EcLength = 10;
        var field = new GaloisField256(GaloisField256.QrPrimitive, 26);

        int[] original = Codeword(field, Data(16), EcLength);
        var damaged = (int[])original.Clone();

        damaged[damaged.Length - 1] ^= 0x5A;
        damaged[damaged.Length - 4] ^= 0x33;

        Assert.True(ReedSolomonDecoder.TryCorrect(field, damaged, EcLength, out int corrected));

        Assert.Equal(2, corrected);
        Assert.Equal(original, damaged);
    }

    /// <summary>Damage beyond the code's limit is refused, not silently "corrected".</summary>
    /// <remarks>
    /// <para><b>The single most important assertion here.</b> Past <i>n</i>/2 errors the received
    /// word can be closer to a <i>different</i> valid codeword than to the one that was sent, so a
    /// decoder that always answers will sometimes hand back a perfectly well-formed message that
    /// nobody wrote. For a barcode that means a scanned label reporting the wrong part number, with
    /// no error anywhere to say so.</para>
    ///
    /// <para>The word is checked to be untouched as well: a caller that ignores the return value
    /// must not find a half-corrected buffer.</para>
    /// </remarks>
    [Theory]
    [InlineData(6)]
    [InlineData(8)]
    [InlineData(12)]
    public void DamageBeyondTheCodesLimitIsRefused(int errors)
    {
        const int EcLength = 10;
        var field = new GaloisField256(GaloisField256.QrPrimitive, 26);

        int[] original = Codeword(field, Data(16), EcLength);
        var damaged = (int[])original.Clone();

        var random = new Random(errors * 104729);
        foreach (int position in Positions(random, damaged.Length, errors))
            damaged[position] ^= 1 + random.Next(255);

        var asDamaged = (int[])damaged.Clone();

        bool corrected = ReedSolomonDecoder.TryCorrect(field, damaged, EcLength, out _);

        // It may refuse, and it must never claim a wrong answer or leave a half-written buffer.
        if (!corrected) Assert.Equal(asDamaged, damaged);
        else Assert.Equal(original, damaged);
    }

    /// <summary>Both fields correct, so neither primitive polynomial is wired up wrongly.</summary>
    /// <remarks>
    /// Data Matrix uses a different primitive polynomial from QR. The decoder never names either —
    /// it takes the field it is handed — and this is what proves that, rather than a comment saying
    /// so.
    /// </remarks>
    [Fact]
    public void TheDataMatrixFieldCorrectsToo()
    {
        const int EcLength = 12;
        var field = new GaloisField256(GaloisField256.DataMatrixPrimitive, 24);

        int[] original = Codeword(field, Data(12), EcLength);
        var damaged = (int[])original.Clone();
        damaged[3] ^= 0x9C;
        damaged[7] ^= 0x11;

        Assert.True(ReedSolomonDecoder.TryCorrect(field, damaged, EcLength, out int corrected));

        Assert.Equal(2, corrected);
        Assert.Equal(original, damaged);
    }

    /// <summary>A word shorter than its own check symbols is refused rather than indexed.</summary>
    [Fact]
    public void AMalformedCodewordLengthIsRefused()
    {
        var field = new GaloisField256(GaloisField256.QrPrimitive, 26);

        Assert.False(ReedSolomonDecoder.TryCorrect(field, new int[4], 10, out _));
        Assert.False(ReedSolomonDecoder.TryCorrect(field, new int[10], 10, out _));
        Assert.False(ReedSolomonDecoder.TryCorrect(field, null, 10, out _));
    }

    // ================================================================

    private static int[] Data(int length)
    {
        var data = new int[length];
        for (int i = 0; i < length; i++) data[i] = (i * 37 + 11) & 0xFF;
        return data;
    }

    private static int[] Codeword(GaloisField256 field, int[] data, int ecLength)
    {
        int[] ec = field.ErrorCorrection(data, ecLength);
        var codeword = new int[data.Length + ecLength];

        Array.Copy(data, codeword, data.Length);
        Array.Copy(ec, 0, codeword, data.Length, ecLength);
        return codeword;
    }

    /// <summary>Distinct positions, so "n errors" really means n symbols changed.</summary>
    private static IEnumerable<int> Positions(Random random, int length, int count)
    {
        var chosen = new HashSet<int>();
        while (chosen.Count < count) chosen.Add(random.Next(length));
        return chosen;
    }
}
