// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The decisions in Data Matrix and Reed–Solomon that the <c>Barcodes</c> sweep, batch 2, found
/// nothing asserting. Several are symmetric — the encoder and the reader share the walk and the
/// finder painter, so a round trip agrees with any mistake in either — and are pinned against a
/// reference from outside the library instead.
/// </summary>
public class DataMatrixDecisionTests
{
    /// <summary>
    /// Annex F's placement of eighteen codewords in a 12×12 mapping matrix (a 14×14 symbol), against
    /// an independent reader's <c>PlacementTestCase</c> — an independent implementation's expected modules, row by row.
    /// The encoder and the reader both run this one walk, so no round trip can see a wrong wrap.
    /// </summary>
    [Fact]
    public void ThePlacementMatchesAReferenceSymbol()
    {
        var codewords = "66 74 78 66 74 78 129 56 35 102 192 96 226 100 156 1 107 221"
            .Split(' ').Select(int.Parse).ToList();
        string[] expected =
        {
            "011100001111", "001010101000", "010001010100", "001010100010",
            "000111000100", "011000010100", "000100001101", "011000010000",
            "001100001101", "100010010111", "011101011010", "001011001010",
        };

        var mapping = new bool?[12, 12];
        Ecc200Placement.Place(mapping, 12, 12, codewords);

        for (int y = 0; y < 12; y++)
        {
            var row = new char[12];
            for (int x = 0; x < 12; x++) row[x] = mapping[y, x] == true ? '1' : '0';
            Assert.Equal(expected[y], new string(row));
        }
    }

    /// <summary>
    /// The clock track's phase: along the top, dark from the corner the L owns and alternating; down
    /// the right, dark from the L's bottom corner up — so on an even size the top-right corner is
    /// light. This is what an independent writer paints (checked 2026-10-18); this test once said the
    /// opposite, from recollection, and the writer and the reader shared that wrong phase, so the
    /// library read its own symbols and an independent reader read none. The reader checks the finder with the
    /// same painter, so only the modules can say which is right.
    /// </summary>
    [Fact]
    public void TheClockTrack_RunsDarkFromTheLsCorners_LightAtTheTopRight()
    {
        bool[,] grid = DataMatrixEncoder.Encode("A");
        Assert.Equal(10, grid.GetLength(0));

        var top = new char[10];
        var right = new char[10];
        for (int i = 0; i < 10; i++)
        {
            top[i] = grid[0, i] ? '1' : '0';
            right[i] = grid[i, 9] ? '1' : '0';
        }
        Assert.Equal("1010101010", new string(top));
        Assert.Equal("0101010101", new string(right));
    }

    /// <summary>
    /// Two digits make one codeword: ten digits are five codewords, which a 12×12 holds — written one
    /// per digit they would need ten, and a 16×16.
    /// </summary>
    [Fact]
    public void DigitPairsArePackedIntoOneCodewordEach()
        => Assert.Equal(12, DataMatrixEncoder.Encode("0123456789").GetLength(0));

    /// <summary>The ends of the ASCII ranges: 128 is DEL, the last character; 229 is the digit pair 99.</summary>
    [Theory]
    [InlineData(128, "\u007f")]
    [InlineData(229, "99")]
    public void TheLastAsciiValueAndTheLastDigitPair_AreRead(int codeword, string expected)
    {
        Assert.Equal(expected, DataMatrixGridReader.ReadAscii(new[] { codeword }, 1, out string refusal));
        Assert.Null(refusal);
    }

    private static readonly GaloisField256 Field = new GaloisField256(GaloisField256.DataMatrixPrimitive, 10);

    /// <summary>
    /// A code word over GF(256) is at most 255 symbols: a longer one is refused even when it looks
    /// clean, where 255 zeros are a codeword.
    /// </summary>
    [Fact]
    public void AWordLongerThanTheFieldAllows_IsRefused()
    {
        Assert.False(ReedSolomonDecoder.TryCorrect(Field, new int[300], 10, out _));
        Assert.True(ReedSolomonDecoder.TryCorrect(Field, new int[255], 10, out int corrected));
        Assert.Equal(0, corrected);
    }

    /// <summary>
    /// Damage beyond the code's reach is never "corrected" by more than half the error-correction
    /// symbols. Full-length words (255) put every root of any locator inside the word, so the guards
    /// that bound the locator — its degree, the radius — are the only thing between a too-damaged word
    /// and a confident wrong answer. Seeded, so it is the same 400 words every run.
    /// </summary>
    [Fact]
    public void DamageBeyondTheRadius_IsNeverCorrectedPastIt()
    {
        const int Ec = 4;
        var random = new Random(20261006);
        int refused = 0, repaired = 0;

        for (int trial = 0; trial < 400; trial++)
        {
            var data = new int[255 - Ec];
            for (int i = 0; i < data.Length; i++) data[i] = random.Next(256);
            var word = data.Concat(Field.ErrorCorrection(data, Ec)).ToArray();

            int errors = 3 + (trial % 2);   // 3 or 4: past the radius of 2
            foreach (int position in Enumerable.Range(0, 255).OrderBy(_ => random.Next()).Take(errors))
                word[position] ^= 1 + random.Next(255);

            if (ReedSolomonDecoder.TryCorrect(Field, word, Ec, out int corrected))
            {
                Assert.True(corrected <= Ec / 2, $"trial {trial}: {corrected} symbols \"corrected\" by {Ec} error-correction symbols");
                // And what it returns is a codeword: its error correction is its data's.
                Assert.Equal(Field.ErrorCorrection(word.Take(255 - Ec).ToArray(), Ec), word.Skip(255 - Ec).ToArray());
                repaired++;
            }
            else refused++;
        }

        // Potency: both outcomes occur. A full-length code with four error-correction symbols is dense
        // — about half of all words lie within two symbols of some codeword — so roughly half of these
        // land within reach of a codeword other than the one sent; that is the code's distance, which
        // the layer above answers with its check digit or CRC.
        Assert.True(refused > 100, $"only {refused} of 400 refused");
        Assert.True(repaired > 0, "no word landed within reach of another codeword, so the radius was never tested at its edge");
    }
}
