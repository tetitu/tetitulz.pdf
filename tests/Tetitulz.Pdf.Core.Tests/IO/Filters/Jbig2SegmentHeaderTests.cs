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
/// JBIG2 segment headers (T.88 §7.2): the width of a referred-to segment number, and the long-form
/// referred-to count.
/// </summary>
public class Jbig2SegmentHeaderTests
{
    /// <summary>
    /// §7.2.5: a segment numbered up to 256 refers to others in one byte, up to 65536 in two, and
    /// beyond in four. The bounds were 255 and 65535, so segment 256 read its one-byte reference as
    /// two bytes, took the page association for half of it, and misparsed every header after it.
    /// </summary>
    [Theory]
    [InlineData(255u, 1)]
    [InlineData(256u, 1)]
    [InlineData(257u, 2)]
    [InlineData(65536u, 2)]
    [InlineData(65537u, 4)]
    public void AReferredToNumberIsAsWideAsTheSegmentNumberRequires(uint segmentNumber, int width)
    {
        var bytes = new List<byte>
        {
            (byte)(segmentNumber >> 24), (byte)(segmentNumber >> 16), (byte)(segmentNumber >> 8), (byte)segmentNumber,
            50,                                   // flags: end of stripe, one-byte page association
            0x20,                                 // one referred-to segment, short form
        };
        for (int i = 0; i < width - 1; i++) bytes.Add(0);
        bytes.Add(5);                             // refers to segment 5
        bytes.Add(1);                             // page 1
        bytes.AddRange(new byte[] { 0, 0, 0, 4 }); // data length 4

        var header = Jbig2Decoder.ReadSegmentHeader(new Jbig2Decoder.Jbig2BitReader(bytes.ToArray()));

        Assert.Equal(new uint[] { 5 }, header.ReferredToSegments);
        Assert.Equal(1u, header.PageAssociation);
        Assert.Equal(4u, header.DataLength);
        Assert.Equal(bytes.Count, header.HeaderLength);
    }

    /// <summary>
    /// Hostile header: a data length of 2^31 in front of bytes that read as a second header. The
    /// body runs to the end of the data, as <c>Decode</c> takes it, so the walkers list one segment.
    /// They cast the length to a negative int, skipped nothing, and listed the body as a segment.
    /// The control, a body of 0, lists both.
    /// </summary>
    [Theory]
    [InlineData(0x80000000u, 1)]
    [InlineData(0u, 2)]
    public void Hostile_ADataLengthPastInt32_RunsToTheEndForEveryWalker(uint length, int segments)
    {
        byte[] data =
        {
            0, 0, 0, 0, 50, 0, 1, (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length,
            0, 0, 0, 1, 49, 0, 1, 0, 0, 0, 0,     // segment 1: end of page, no body
        };

        Assert.Equal(segments, Jbig2Decoder.EnumerateSegments(data).Count);
        Assert.Equal(segments, Jbig2Decoder.Diagnose(data).Count);
    }

    /// <summary>
    /// Hostile header: the long form's 29-bit count at its maximum, in a header of a few bytes. It
    /// was allocated as read (2 GB of references); it is refused against the bytes that remain.
    /// </summary>
    [Fact]
    public void Hostile_ALongFormCountPastTheData_IsRefusedWithoutAllocating()
    {
        byte[] bytes = { 0, 0, 0, 1, 50, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0, 0 };

        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() =>
            Jbig2Decoder.ReadSegmentHeader(new Jbig2Decoder.Jbig2BitReader(bytes)));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated < 1024 * 1024, $"allocated {allocated} bytes");
    }
}
