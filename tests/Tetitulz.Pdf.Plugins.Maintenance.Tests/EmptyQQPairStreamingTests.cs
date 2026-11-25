// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Text;
using Xunit;

namespace Tetitulz.Pdf.Plugins.Maintenance.Tests;

/// <summary>
/// The empty <c>q…Q</c> remover streams the content into one buffer. Tokenizing the stream into a
/// list, copying every token and white-space run into an array of its own, and building a second list
/// for the output, two megabytes of content would cost over a hundred megabytes.
/// </summary>
public sealed class EmptyQQPairStreamingTests
{
    [Theory]
    [InlineData("q Q 0 0 m", "  0 0 m")]
    [InlineData("q q Q Q 1 w", "  1 w")]
    [InlineData("q 1 w Q", "q 1 w Q")]
    [InlineData("q q Q 1 w Q", "q   1 w Q")]
    [InlineData("Q q Q", "Q  ")]
    [InlineData("q (Q) Tj Q", "q (Q) Tj Q")]
    [InlineData("q %Q\nQ", " ")]
    public void EmptyPairsAreRemoved_AndEverythingElseKeptByteForByte(string content, string expected)
    {
        byte[] data = Encoding.ASCII.GetBytes(content);

        byte[] cleaned = PdfOptimizer.RemoveEmptyQQPairs(data);

        Assert.Equal(expected, Encoding.ASCII.GetString(cleaned));
    }

    /// <summary>A stream with no empty pair is handed back as the same array, untouched.</summary>
    [Fact]
    public void AStreamWithNoEmptyPair_IsReturnedAsItIs()
    {
        byte[] data = Encoding.ASCII.GetBytes("q 1 0 0 1 0 0 cm Q");

        Assert.Same(data, PdfOptimizer.RemoveEmptyQQPairs(data));
    }

    /// <summary>An inline image refuses the stream, whatever was read before it.</summary>
    [Fact]
    public void AnInlineImage_RefusesTheStream()
    {
        Assert.Null(PdfOptimizer.RemoveEmptyQQPairs(Encoding.ASCII.GetBytes("q Q BI /W 1 /H 1 ID x EI")));
    }

    [Fact]
    public void TwoMegabytesOfContent_AreCleanedInLinearMemory()
    {
        var sb = new StringBuilder("q Q\n");
        while (sb.Length < 2 * 1024 * 1024) sb.Append("1 0 0 1 0 0 cm\n");
        byte[] data = Encoding.ASCII.GetBytes(sb.ToString());

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] cleaned = PdfOptimizer.RemoveEmptyQQPairs(data);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(data.Length - 2, cleaned.Length);   // "q Q" became one space
        // The output buffer and the result: twice the stream, and nothing a token.
        Assert.True(allocated < 8L * 1024 * 1024, "allocated " + allocated + " bytes");
    }
}
