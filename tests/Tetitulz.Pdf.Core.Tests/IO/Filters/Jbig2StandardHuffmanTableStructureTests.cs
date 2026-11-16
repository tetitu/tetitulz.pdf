// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Core.IO.Filters;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// Every standard JBIG2 Huffman table must be a <b>structurally possible</b> prefix code, whatever
/// ITU-T T.88 Annex B says its values are.
/// </summary>
/// <remarks>
/// <para><b>This test needs no spec text and no stream fixture</b>, which is the whole point of it.
/// Three properties are pure mathematics and hold of any Huffman table ever written:</para>
/// <list type="number">
///   <item><b>Prefix-free</b> — no codeword is a prefix of another, or decoding is ambiguous.</item>
///   <item><b>Kraft equality</b> — Σ2<sup>−len</sup> = 1 exactly. Below 1 leaves bit patterns that
///         decode to nothing; above 1 is impossible: two codewords must collide.</item>
///   <item><b>Contiguous ranges</b> — the value ranges tile without gap or overlap, so every input
///         has exactly one encoding.</item>
/// </list>
/// <para><b>Why it exists.</b> An earlier version of the tables in <c>Jbig2Decoder</c> carried a
/// comment admitting they were "the author's best recall of ITU-T T.88 Annex B". Three of the
/// fifteen failed all three properties outright — old <c>StandardTableC</c> (Kraft 1.0078, and
/// <c>"0"</c> is a prefix of <c>"00000000"</c>), and two delta tables (Kraft 1.25 and 1.40625, and
/// <c>"10"</c> is a prefix of <c>"100"</c>). A Kraft sum above 1 is not an approximation of a
/// correct table; it is proof the row data is wrong, obtainable without owning the standard. Those
/// three were deleted rather than left looking authoritative, and all fifteen tables (B.1-B.15)
/// were later rebuilt from (PREFLEN, RANGELEN, RANGELOW) triples cross-checked between two
/// independent implementations, with codes computed by <c>AssignCanonicalPrefixes</c> rather than
/// hand-assigned — see GAPS.md §2a.</para>
/// <para><b>Keep this passing by fixing the table, never by exempting it.</b> There is no
/// allow-list here on purpose. A table that cannot satisfy these three properties cannot decode
/// anything correctly, so an exemption would only preserve a defect behind a green run.</para>
/// </remarks>
public class Jbig2StandardHuffmanTableStructureTests
{
    // Names rather than tables: the test methods are public and Jbig2HuffmanTable is internal, so
    // it cannot appear in a public signature. Resolved by name below.
    public static IEnumerable<object[]> StandardTables() => new[]
    {
        new object[] { "A" }, new object[] { "B" }, new object[] { "C" }, new object[] { "D" },
        new object[] { "E" }, new object[] { "F" }, new object[] { "G" }, new object[] { "H" },
        new object[] { "I" }, new object[] { "J" }, new object[] { "K" }, new object[] { "L" },
        new object[] { "M" }, new object[] { "N" }, new object[] { "O" },
    };

    private static Jbig2Decoder.Jbig2HuffmanTable Resolve(string name) => name switch
    {
        "A" => Jbig2Decoder.StandardTableA,
        "B" => Jbig2Decoder.StandardTableB,
        "C" => Jbig2Decoder.StandardTableC,
        "D" => Jbig2Decoder.StandardTableD,
        "E" => Jbig2Decoder.StandardTableE,
        "F" => Jbig2Decoder.StandardTableF,
        "G" => Jbig2Decoder.StandardTableG,
        "H" => Jbig2Decoder.StandardTableH,
        "I" => Jbig2Decoder.StandardTableI,
        "J" => Jbig2Decoder.StandardTableJ,
        "K" => Jbig2Decoder.StandardTableK,
        "L" => Jbig2Decoder.StandardTableL,
        "M" => Jbig2Decoder.StandardTableM,
        "N" => Jbig2Decoder.StandardTableN,
        "O" => Jbig2Decoder.StandardTableO,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown standard table"),
    };

    [Theory]
    [MemberData(nameof(StandardTables))]
    public void StandardTable_IsAPrefixFreeCode(string name)
    {
        var table = Resolve(name);
        var codes = table.Entries
            .Select(e => (Text: Bits(e.Prefix, e.PrefixLen), e.PrefixLen))
            .ToList();

        foreach (var a in codes)
            foreach (var b in codes)
            {
                if (ReferenceEquals(a.Text, b.Text) && a.PrefixLen == b.PrefixLen) continue;
                if (a.Text.Length >= b.Text.Length) continue;
                Assert.False(b.Text.StartsWith(a.Text, StringComparison.Ordinal),
                    $"{name}: codeword \"{a.Text}\" is a prefix of \"{b.Text}\", so a decoder " +
                    "cannot tell them apart — it returns whichever entry it scans first.");
            }
    }

    [Theory]
    [MemberData(nameof(StandardTables))]
    public void StandardTable_SatisfiesKraftEqualityExactly(string name)
    {
        var table = Resolve(name);
        // Exact in binary: every term is a negative power of two, so no tolerance is warranted and
        // a tolerance would hide precisely the small overshoot that proves a row is wrong.
        double kraft = table.Entries.Sum(e => Math.Pow(2, -e.PrefixLen));

        Assert.True(kraft == 1.0,
            $"{name}: Kraft sum is {kraft}, not 1. Above 1 is impossible — two codewords must " +
            "collide. Below 1 leaves bit patterns that decode to no entry at all.");
    }

    [Theory]
    [MemberData(nameof(StandardTables))]
    public void StandardTable_RangesTileWithoutGapOrOverlap(string name)
    {
        var table = Resolve(name);
        // OOB (-1) carries no value range, and a lower-tail entry runs downwards from its base, so
        // neither participates in the upward tiling.
        var ranges = table.Entries
            .Where(e => e.RangeBits >= 0 && !e.LowerSign)
            .OrderBy(e => e.RangeLow)
            .Select(e => (Low: (long)e.RangeLow, Count: e.RangeBits >= 32 ? long.MaxValue : 1L << e.RangeBits))
            .ToList();

        Assert.NotEmpty(ranges);

        long expected = ranges[0].Low;
        foreach (var (low, count) in ranges)
        {
            Assert.True(low == expected,
                $"{name}: the value ranges do not tile — expected the next range to start at " +
                $"{expected} but it starts at {low}. A gap makes some values unencodable; an " +
                "overlap makes some values ambiguous.");
            if (count == long.MaxValue) break;   // the unbounded tail is necessarily last
            expected = low + count;
        }
    }

    /// <summary>
    /// The guard must be able to see a broken table, or it is three assertions that never fire.
    /// This is the shape that was actually in the tree: <c>StandardTableC</c>'s 8-bit row against
    /// its 1-bit row.
    /// </summary>
    [Fact]
    public void TheGuardItself_RejectsTheShapeThatWasInTheTree()
    {
        // "0" (len 1) is a prefix of "00000000" (len 8), and 2^-1 + 2^-8 is nowhere near 1.
        var broken = new[]
        {
            (Len: 8, Code: 0b00000000),
            (Len: 1, Code: 0b0),
        };

        var codes = broken.Select(e => Bits(e.Code, e.Len)).ToList();
        Assert.True(codes[1].Length < codes[0].Length && codes[0].StartsWith(codes[1], StringComparison.Ordinal),
            "the synthetic fixture is not actually prefix-violating, so it proves nothing");

        double kraft = broken.Sum(e => Math.Pow(2, -e.Len));
        Assert.NotEqual(1.0, kraft);
    }

    private static string Bits(int value, int length)
    {
        var chars = new char[length];
        for (int i = 0; i < length; i++)
            chars[i] = ((value >> (length - 1 - i)) & 1) == 1 ? '1' : '0';
        return new string(chars);
    }
}
