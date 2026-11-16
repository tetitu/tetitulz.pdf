// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Core.IO;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// <see cref="StreamBytes.Fill"/> — the one loop that reads until a buffer is full or the stream
/// ends.
/// </summary>
/// <remarks>
/// A <see cref="MemoryStream"/> answers every read in full, so a caller that takes one
/// <c>Read</c>'s count as the whole buffer passes every test built on one. The stream here hands
/// back a few bytes per call, which is what a network stream or a decompressor is allowed to do.
/// </remarks>
public sealed class StreamBytesFillTests
{
    /// <summary>A stream that returns at most <c>chunk</c> bytes from each <c>Read</c>.</summary>
    private sealed class ChunkedStream : MemoryStream
    {
        private readonly int _chunk;

        public ChunkedStream(string text, int chunk) : base(Encoding.ASCII.GetBytes(text), writable: false)
            => _chunk = chunk;

        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, _chunk));
    }

    [Fact]
    public void AStreamThatReturnsOneByteAtATime_StillFillsTheBuffer()
    {
        var stream = new ChunkedStream("%PDF-1.7", chunk: 1);
        var buffer = new byte[8];

        int read = StreamBytes.Fill(stream, buffer, 0, buffer.Length);

        Assert.Equal(8, read);
        Assert.Equal("%PDF-1.7", Encoding.ASCII.GetString(buffer));
    }

    /// <summary>
    /// The end of the stream stops the loop. Run on its own thread because the failure this pins is
    /// a loop that keeps asking an exhausted stream for more.
    /// </summary>
    [Fact]
    public void AStreamThatEndsFirst_ReportsHowManyBytesArrived()
    {
        var stream = new ChunkedStream("%%EOF", chunk: 1);
        var buffer = new byte[8];
        int read = -1;

        var worker = new Thread(() => read = StreamBytes.Fill(stream, buffer, 0, buffer.Length))
        {
            IsBackground = true,   // a loop that never ends must not also keep the test host alive
        };
        worker.Start();

        Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "Fill did not return once the stream ended.");
        Assert.Equal(5, read);
        Assert.Equal("%%EOF", Encoding.ASCII.GetString(buffer, 0, read));
    }

    /// <summary>
    /// Bytes land from <c>offset</c>, and each later read asks only for what is still missing —
    /// two bytes per read makes a request that forgot the first read overrun the buffer.
    /// </summary>
    [Fact]
    public void TheOffset_PlacesTheBytesAndCountsFromThere()
    {
        var stream = new ChunkedStream("xref", chunk: 2);
        var buffer = Encoding.ASCII.GetBytes("......");

        int read = StreamBytes.Fill(stream, buffer, 2, 3);

        Assert.Equal(3, read);
        Assert.Equal("..xre.", Encoding.ASCII.GetString(buffer));
        Assert.Equal(3, stream.Position);
    }

    [Fact]
    public void ANullStream_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => StreamBytes.Fill(null!, new byte[1], 0, 1));
    }
}
