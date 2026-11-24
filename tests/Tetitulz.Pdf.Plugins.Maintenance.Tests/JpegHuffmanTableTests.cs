// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using Tetitulz.Pdf.Plugins.Maintenance.Jpeg;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// <see cref="JpegHuffmanTable"/> at its edges: the §K.2 length limit,
/// the tie-break that makes a fitted table reproducible, and the over-full check at its boundary.
/// </summary>
public class JpegHuffmanTableTests
{
    /// <summary>
    /// Doubling frequencies over 20 symbols give an unconstrained Huffman tree 20 bits deep, past
    /// the 16 bits §B.2.4.2 allows. §K.2's adjustment folds the long codes back. The counts per
    /// length are pinned — one code at each length 1 to 13, none at 14 or 15, seven at 16, as the
    /// algorithm computes them — and the table is one a decoder accepts, mapping each code back to
    /// its symbol.
    /// </summary>
    [Fact]
    public void SkewedFrequenciesAreFoldedIntoSixteenBitCodes()
    {
        var freq = new long[256];
        for (int s = 0; s < 20; s++) freq[s] = 1L << s;

        var table = JpegHuffmanTable.FromFrequencies(freq);
        Assert.NotNull(table);
        Assert.Equal(new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 7 }, table!.Bits.Skip(1));

        Assert.True(table.TryBuildDecodeTables(out string reason), reason);
        for (int s = 0; s < 20; s++)
            Assert.Equal(s, table.SymbolFor(table.Length(s), table.Code(s)));
    }

    /// <summary>
    /// Equal frequencies are broken towards the highest symbol index, as the reference JPEG library does, so the same
    /// statistics always give the same table. For { 0:2, 1:1, 2:1, 3:1 } that makes symbol 0's
    /// code one bit and the rest three; breaking ties the other way gives 2, 3, 3, 2.
    /// </summary>
    [Fact]
    public void TiesGoToTheHighestSymbol()
    {
        var freq = new long[256];
        freq[0] = 2; freq[1] = 1; freq[2] = 1; freq[3] = 1;

        var table = JpegHuffmanTable.FromFrequencies(freq)!;
        Assert.True(table.TryBuildDecodeTables(out _));

        Assert.Equal(new[] { 1, 3, 3, 3 }, Enumerable.Range(0, 4).Select(table.Length));
    }

    /// <summary>
    /// Two one-bit codes fill the one-bit code space exactly and are a valid table; three need
    /// more codes than one bit can express and are refused.
    /// </summary>
    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void OneBitCodesFitUpToTwo(int count, bool valid)
    {
        var data = new byte[16 + count];
        data[0] = (byte)count;
        for (int i = 0; i < count; i++) data[16 + i] = (byte)i;

        var table = JpegHuffmanTable.Parse(data, 0, data.Length, out _, out string parseReason);
        Assert.True(table != null, parseReason);
        Assert.Equal(valid, table!.TryBuildDecodeTables(out _));
    }
}
