// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Base.Operations.Barcodes;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Operations;

/// <summary>
/// The QR encoder's exact output, pinned.
/// </summary>
/// <remarks>
/// <para><b>A characterisation test: it proves nothing about correctness, and that is not its
/// job.</b> The layout rules — function patterns, the reserved map, the mask functions, the zigzag
/// data order, the format code — live outside the encoder so a reader can share them, and nothing
/// else in the tree can tell a faithful layout from a subtly different one, because every other QR
/// assertion in this suite goes through the encoder itself.</para>
///
/// <para><b>What a moved hash can mean.</b> A data module marked <i>reserved</i> as it is written
/// would be skipped by masking, sending every QR code out <b>unmasked, with a format field
/// announcing a mask</b> — which any conforming reader then undoes, turning the payload into
/// noise; <c>QrGridReaderTests.AnUnmaskedGridDoesNotDecode</c> holds that, on Reed–Solomon's
/// evidence rather than this library's own opinion. And a format field written at the transpose
/// of the standard's positions, read there by this library's reader, passes every round trip
/// while an independent reader finds no symbol; the positions are the standard's
/// (<c>QrLayout.FormatPositionA</c>), and the reader is held to symbols an independent encoder
/// made (<c>QrThirdPartySymbolTests</c>).</para>
///
/// <para><b>So these hashes move only alongside a stated reason</b>, and the reason is the point.
/// A pin updated to make a run go green is worse than no pin at all.</para>
/// </remarks>
public class QrCodeEncoderPinTests
{
    /// <summary>The module grid for a fixed payload is byte-for-byte what it should be.</summary>
    /// <remarks>
    /// The payloads pick out structurally different symbols: version 1, which has no alignment
    /// pattern, and a longer one that does.
    /// </remarks>
    [Theory]
    [InlineData("HELLO", "3e46edc4531321e7fd0376f170a34f7401b798e06a75c1eef7fcc77744552966")]
    [InlineData("https://example.com/order/4711",
                "c4a99cc19e7feb55d9cb4f50dc297857e759a74b44062b3c9c331541c00897b2")]
    public void TheEncodedGridIsUnchanged(string payload, string expected)
    {
        Assert.Equal(expected, GridHash(QrCodeEncoder.Encode(payload)));
    }


    /// <summary>A hash over the grid, row by row, so a single flipped module shows.</summary>
    private static string GridHash(bool[,] grid)
    {
        int size = grid.GetLength(0);
        var sb = new StringBuilder(size * size);

        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                sb.Append(grid[y, x] ? '1' : '0');

        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.ASCII.GetBytes(sb.ToString()));

        var hex = new StringBuilder(hash.Length * 2);
        foreach (byte b in hash) hex.Append(b.ToString("x2"));
        return hex.ToString();
    }
}
