// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.IO.Filters;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO.Filters;

/// <summary>
/// RunLengthDecode measures its output, then writes it into an array of exactly that length: written a byte at a time
/// into a growing stream, 810 KB of output allocated 2.77 MB.
/// </summary>
public class RunLengthDecodeAllocationTests
{
    /// <summary>Repeat runs and literal runs, a megabyte of them; the decode allocates little beyond its output.</summary>
    [Fact]
    public void ADecode_AllocatesItsOutputAndLittleElse()
    {
        var encoded = new List<byte>();
        var expected = new List<byte>();
        for (int k = 0; k < 8000; k++)
        {
            encoded.Add(129); encoded.Add((byte)k);                                // 128 copies of one byte
            for (int j = 0; j < 128; j++) expected.Add((byte)k);
            encoded.Add(2); encoded.Add(1); encoded.Add(2); encoded.Add(3);       // three literal bytes
            expected.Add(1); expected.Add(2); expected.Add(3);
        }
        encoded.Add(128);
        byte[] data = encoded.ToArray();
        new RunLengthDecodeFilter().Decode(data, null);   // warm

        long before = GC.GetAllocatedBytesForCurrentThread();
        byte[] decoded = new RunLengthDecodeFilter().Decode(data, null);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected.ToArray(), decoded);
        Assert.True(allocated < decoded.Length + 4096, $"{decoded.Length:N0} bytes out allocated {allocated:N0}");
    }

    /// <summary>A decode past the ceiling is refused before its output is made; one at the ceiling is decoded (the control).</summary>
    [Theory]
    [InlineData(1279, false)]
    [InlineData(1280, true)]
    public void PastTheCeiling_IsRefusedBeforeTheOutputIsMade(long ceiling, bool decodes)
    {
        var encoded = new List<byte>();
        for (int k = 0; k < 10; k++) { encoded.Add(129); encoded.Add(7); }   // 1 280 bytes
        encoded.Add(128);

        if (decodes)
            Assert.Equal(1280, RunLengthDecodeFilter.DecodeEmbedded(encoded.ToArray(), 0, encoded.Count, ceiling, out int end).Length);
        else
            Assert.Contains("exceeded 1279 bytes",
                Assert.Throws<InvalidDataException>(() => RunLengthDecodeFilter.DecodeEmbedded(encoded.ToArray(), 0, encoded.Count, ceiling, out _)).Message);
    }
}
