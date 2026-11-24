// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// Data Matrix symbols another tool made — an independent writer, checked in as module rows (one hex
/// string a row, no quiet zone) — read by this library's reader. An encoder and reader sharing a
/// departure from ISO/IEC 16022 (the right clock edge in the opposite phase, say, or Reed–Solomon
/// over roots from α⁰ where the standard's start at α¹) pass every round trip while an independent
/// reader reads nothing the library writes. A symbol from an independent writer is what a shared
/// mistake cannot pass.
/// </summary>
public class DataMatrixThirdPartySymbolTests
{
    [Fact]
    public void ASquareSymbol_IsRead()
    {
        Assert.Equal("Data Matrix from tetitulz 123", DataMatrixGridReader.Decode(Grid(Small, 20), out string refusal, out _));
        Assert.Null(refusal);
    }

    /// <summary>A 40×40 symbol of four data regions, each with its own finder and clock.</summary>
    [Fact]
    public void ASymbolOfFourRegions_IsRead()
        => Assert.Equal(string.Concat(Enumerable.Repeat("Four data regions hold this longer text from zxing-cpp, 0123456789 abcdefghij ", 2)),
                        DataMatrixGridReader.Decode(Grid(Regions, 40), out _, out _));

    /// <summary>
    /// Damaged modules are repaired — the error correction itself, over the standard's roots from
    /// α¹, with Forney's factor for that base. Three flipped modules inside the data region.
    /// </summary>
    [Fact]
    public void DamagedModules_AreCorrected()
    {
        bool[,] grid = Grid(Small, 20);
        foreach ((int y, int x) in new[] { (5, 5), (9, 12), (14, 7) }) grid[y, x] = !grid[y, x];
        Assert.Equal("Data Matrix from tetitulz 123", DataMatrixGridReader.Decode(grid, out string refusal, out _));
        Assert.Null(refusal);
    }

    /// <summary>A grid from hex rows: each row's last <paramref name="width"/> bits, the first the leftmost module; true is dark.</summary>
    private static bool[,] Grid(string[] rows, int width)
    {
        var grid = new bool[rows.Length, width];
        for (int y = 0; y < rows.Length; y++)
        {
            string bits = string.Concat(rows[y].Select(h => Convert.ToString(Convert.ToInt32(h.ToString(), 16), 2).PadLeft(4, '0')));
            bits = bits.Substring(bits.Length - width);
            for (int x = 0; x < width; x++) grid[y, x] = bits[x] == '1';
        }
        return grid;
    }

    // ---------------------------------------------------------------- an independent reader's symbols
    private static readonly string[] Small =
    {
        "aaaaa",
        "b6d13",
        "c7e08",
        "a5bc5",
        "bee54",
        "f80f9",
        "daf4c",
        "cd327",
        "ea69a",
        "fa56d",
        "854b2",
        "d6873",
        "88fb4",
        "a0483",
        "87df4",
        "e814b",
        "997d0",
        "b399d",
        "eeae2",
        "fffff",
    };

    private static readonly string[] Regions =
    {
        "aaaaaaaaaa",
        "f37b1a2747",
        "d01d498916",
        "f58f1bb777",
        "9bc50aee72",
        "f514fd0169",
        "f9fe6ba922",
        "d5edb92383",
        "92de0aad98",
        "f4ae1f207f",
        "8b532bba3e",
        "c1c1bfb6e1",
        "a97e6e7fac",
        "a73c1bd109",
        "8720887bd0",
        "e3763f1a33",
        "8b26cddffe",
        "fcb71aa7c5",
        "e3ed48ed1a",
        "ffffffffff",
        "aaaaaaaaaa",
        "8abf196be7",
        "ca850a8d32",
        "ca94d8219f",
        "d4fe48bec4",
        "98ed9d8463",
        "82dece7d86",
        "bcafdefb5f",
        "ab55af2354",
        "91cf580d49",
        "a9730bbd70",
        "e7317a8f05",
        "87b1cd0262",
        "e32f7bd643",
        "88b8a9bff8",
        "fddcfe5b2d",
        "ee076d2b12",
        "8f1918fba7",
        "bd926b5e6e",
        "ffffffffff",
    };
}
