// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The Code 128 and EAN-13 symbol tables, checked against the symbologies' own structural rules.
/// </summary>
/// <remarks>
/// <para><b>A transcribed table is the one thing a round trip cannot check.</b> Encoding with a
/// wrong table and decoding with the same wrong table closes perfectly — CLAUDE.md's third blind
/// spot, in its purest form. Nothing in this repository could have caught a bad row, because until
/// there was a reader nothing read a barcode at all, and afterwards both halves would share the
/// mistake.</para>
///
/// <para><b>So these assert properties of the symbology rather than the bits.</b> Every rule below
/// is a statement about how Code 128 and EAN-13 are built, and each is checkable without owning the
/// standard or trusting anyone's recollection: a table that breaks one is wrong whatever it was
/// copied from. That is what makes them evidence instead of a second copy of the same claim.</para>
/// </remarks>
public class BarcodeSymbolTableTests
{
    // ================================================================
    //  Code 128
    // ================================================================

    /// <summary>
    /// Every Code 128 character is 11 modules of six alternating elements, starting with a bar and
    /// ending with a space.
    /// </summary>
    /// <remarks>
    /// The bar-then-space shape is what lets characters abut without their end bars merging into
    /// one unreadable run, and it is why the STOP character — which has to terminate the symbol in
    /// dark — needs a seventh element and is excluded here.
    /// </remarks>
    [Fact]
    public void EveryCode128CharacterIsElevenModulesOfSixAlternatingElements()
    {
        for (int value = 0; value <= Code128Symbols.LastCharacterValue; value++)
        {
            IReadOnlyList<int> runs = Runs(Code128Symbols.Pattern(value),
                                           Code128Symbols.CharacterModules);

            Assert.Equal(11, Sum(runs));
            Assert.Equal(6, runs.Count);
            Assert.True(Code128Symbols.StartsWithBar(value), $"value {value} starts with a space");
            Assert.False(Code128Symbols.EndsWithBar(value), $"value {value} ends with a bar");
        }
    }

    /// <summary>Code 128's own parity rule: the bar modules of a character sum to an even number.</summary>
    /// <remarks>
    /// <b>This is the check the symbology carries for itself</b>, and it is completely independent
    /// of the bar/space layout asserted above — a row could satisfy every width rule and still be
    /// the wrong character. One transcription slip in eleven bits breaks it roughly half the time.
    /// </remarks>
    [Fact]
    public void EveryCode128CharacterHasAnEvenNumberOfBarModules()
    {
        for (int value = 0; value <= Code128Symbols.LastCharacterValue; value++)
        {
            int barModules = 0;
            int pattern = Code128Symbols.Pattern(value);
            for (int bit = 0; bit < Code128Symbols.CharacterModules; bit++)
                barModules += (pattern >> bit) & 1;

            Assert.True(barModules % 2 == 0,
                        $"value {value} has {barModules} bar modules, which is odd");
        }
    }

    /// <summary>No two Code 128 characters share a pattern.</summary>
    /// <remarks>
    /// A duplicated row is the transcription error the width and parity rules cannot see: both
    /// copies are individually well-formed, and the reader simply returns the wrong character for
    /// one of them.
    /// </remarks>
    [Fact]
    public void TheCode128PatternsAreAllDistinct()
    {
        var seen = new Dictionary<int, int>();
        for (int value = 0; value <= Code128Symbols.LastCharacterValue; value++)
        {
            int pattern = Code128Symbols.Pattern(value);
            Assert.False(seen.ContainsKey(pattern),
                         $"values {seen.GetValueOrDefault(pattern)} and {value} share a pattern");
            seen[pattern] = value;
        }
    }

    /// <summary>
    /// The STOP character is 13 modules of seven elements and <b>ends with a bar</b>.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the assertion that found a real defect.</b> The shipped table held STOP as
    /// eleven modules — the standard pattern with its final two-module bar missing — so every
    /// Code 128 barcode this library wrote ended in white space and was two modules short. Nothing
    /// noticed for as long as generation shipped without a reader.</para>
    ///
    /// <para>The rule is not a recalled bit pattern. Every other character ends with a space so
    /// that characters can abut; the symbol as a whole must still end in dark, or its final element
    /// runs into the quiet zone and cannot be measured. Something has to supply that terminating
    /// bar, and STOP is the only candidate.</para>
    /// </remarks>
    [Fact]
    public void TheCode128StopCharacterIsThirteenModulesAndEndsWithABar()
    {
        IReadOnlyList<int> runs = Runs(Code128Symbols.Pattern(Code128Symbols.Stop),
                                       Code128Symbols.StopModules);

        Assert.Equal(13, Sum(runs));
        Assert.Equal(7, runs.Count);
        Assert.True(Code128Symbols.StartsWithBar(Code128Symbols.Stop));
        Assert.True(Code128Symbols.EndsWithBar(Code128Symbols.Stop));
    }

    // ================================================================
    //  EAN-13
    // ================================================================

    /// <summary>
    /// An EAN-13 right-hand pattern is the bitwise complement of its left-hand one.
    /// </summary>
    /// <remarks>
    /// <b>A relation between two tables, so it cannot be satisfied by copying one of them.</b>
    /// The right half is printed in reversed ink so that a scanner can tell which half it is
    /// reading — and therefore which way round the symbol is — which is exactly why the two tables
    /// are complements rather than independent.
    /// </remarks>
    [Fact]
    public void EveryEanRightPatternIsTheComplementOfItsLeftPattern()
    {
        for (int digit = 0; digit <= 9; digit++)
        {
            int complement = ~EanSymbols.LeftOdd(digit) & 0x7F;
            Assert.Equal(complement, EanSymbols.Right(digit));
        }
    }

    /// <summary>
    /// An EAN-13 even-parity pattern is its right-hand pattern read backwards.
    /// </summary>
    /// <remarks>
    /// The second independent relation, and it ties in the third table. Together the two rules
    /// leave only the ten left-odd rows free, so a single wrong bit anywhere in thirty rows shows
    /// up here.
    /// </remarks>
    [Fact]
    public void EveryEanEvenParityPatternIsItsRightPatternReversed()
    {
        for (int digit = 0; digit <= 9; digit++)
        {
            int reversed = 0, right = EanSymbols.Right(digit);
            for (int bit = 0; bit < 7; bit++)
                if (((right >> bit) & 1) != 0) reversed |= 1 << (6 - bit);

            Assert.Equal(reversed, EanSymbols.LeftEven(digit));
        }
    }

    /// <summary>
    /// The first digit's parity pattern is what distinguishes the ten first digits, so the ten
    /// patterns must be distinct — and digit 0 must be all-odd.
    /// </summary>
    /// <remarks>
    /// EAN-13 encodes only twelve digits directly; the thirteenth — the first — is carried entirely
    /// by which of the two left-hand tables each of the six left digits uses. Two equal rows would
    /// make two different products encode identically.
    /// </remarks>
    [Fact]
    public void TheEanFirstDigitParityPatternsAreDistinctAndZeroIsAllOdd()
    {
        Assert.Equal(0, EanSymbols.FirstDigitParity(0));

        var seen = new HashSet<int>();
        for (int digit = 0; digit <= 9; digit++)
            Assert.True(seen.Add(EanSymbols.FirstDigitParity(digit)),
                        $"first digit {digit} repeats an earlier parity pattern");
    }

    // ================================================================

    /// <summary>Element widths of a pattern, most significant module first.</summary>
    private static IReadOnlyList<int> Runs(int pattern, int modules)
    {
        var runs = new List<int>();
        int previous = -1;
        for (int bit = modules - 1; bit >= 0; bit--)
        {
            int module = (pattern >> bit) & 1;
            if (module == previous) runs[runs.Count - 1]++;
            else { runs.Add(1); previous = module; }
        }
        return runs;
    }

    private static int Sum(IReadOnlyList<int> runs)
    {
        int total = 0;
        foreach (int run in runs) total += run;
        return total;
    }
}
