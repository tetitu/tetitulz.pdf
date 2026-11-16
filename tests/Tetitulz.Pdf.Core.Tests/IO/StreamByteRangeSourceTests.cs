// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.IO;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// The <see cref="IByteRangeSource"/> over a complete local file.
/// </summary>
/// <remarks>
/// <para><b>Its contract is mostly about what it never does.</b> Every byte is here, so it answers
/// <c>true</c> for every range and reads short only at the end of the file — which is what lets a
/// reader above it have one path for a finished file and an arriving one. The tests that matter are
/// therefore the ones nothing else in the tree reaches: a read past the end, a read that straddles
/// it, and a read at an offset other than zero.</para>
/// </remarks>
public class StreamByteRangeSourceTests
{
    private static readonly byte[] Bytes =
        Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();

    private static StreamByteRangeSource Source(out MemoryStream stream)
    {
        stream = new MemoryStream(Bytes, writable: false);
        return new StreamByteRangeSource(stream);
    }

    /// <summary>A range in the middle is read from where it actually is.</summary>
    [Fact]
    public void ARangeIsReadFromItsOwnOffset()
    {
        var source = Source(out MemoryStream stream);
        using (stream)
        {
            var buffer = new byte[10];

            Assert.True(source.TryRead(100, buffer, 0, 10, out int read));
            Assert.Equal(10, read);
            Assert.Equal(Bytes.Skip(100).Take(10).ToArray(), buffer);
        }
    }

    /// <summary>
    /// The caller's position in the buffer is honoured, and nothing outside the written slice is
    /// touched.
    /// </summary>
    [Fact]
    public void TheBufferOffsetIsWhereTheBytesGo()
    {
        var source = Source(out MemoryStream stream);
        using (stream)
        {
            var buffer = new byte[10];

            Assert.True(source.TryRead(50, buffer, 4, 3, out int read));
            Assert.Equal(3, read);
            Assert.Equal(new byte[] { 0, 0, 0, 0, Bytes[50], Bytes[51], Bytes[52], 0, 0, 0 }, buffer);
        }
    }

    /// <summary>
    /// <b>A range past the end is not "not yet".</b> The bytes will never arrive because there are
    /// none — answering <c>false</c> there would tell a progressive reader to wait for a file that
    /// has already finished.
    /// </summary>
    [Fact]
    public void ARangePastTheEndSucceedsWithNothing()
    {
        var source = Source(out MemoryStream stream);
        using (stream)
        {
            Assert.True(source.TryRead(Bytes.Length + 50, new byte[10], 0, 10, out int read));
            Assert.Equal(0, read);
        }
    }

    /// <summary>A range straddling the end reads what exists — the short count that means EOF.</summary>
    [Fact]
    public void ARangeStraddlingTheEndReadsWhatExists()
    {
        var source = Source(out MemoryStream stream);
        using (stream)
        {
            var buffer = new byte[20];

            Assert.True(source.TryRead(Bytes.Length - 5, buffer, 0, 20, out int read));
            Assert.Equal(5, read);
            Assert.Equal(Bytes.Skip(Bytes.Length - 5).ToArray(), buffer.Take(5).ToArray());
        }
    }

    /// <summary>
    /// It never answers "not yet" — the one property that distinguishes this source from a
    /// progressive one, and the reason a reader above it needs no second path.
    /// </summary>
    [Fact]
    public void ItNeverDefers()
    {
        var source = Source(out MemoryStream stream);
        using (stream)
        {
            foreach (long offset in new long[] { 0, 1, 299, 300, 10_000 })
                Assert.True(source.TryRead(offset, new byte[64], 0, 64, out _),
                            $"deferred at offset {offset}");
        }
    }

    /// <summary>The length is the file's, so a short read above can mean end of file.</summary>
    [Fact]
    public void TheLengthIsTheStreams()
    {
        var source = Source(out MemoryStream stream);
        using (stream) Assert.Equal(Bytes.Length, source.Length);
    }

    /// <summary>
    /// A stream that cannot seek is refused, by name. Buffering it silently would give this type a
    /// hidden memory cost precisely where the caller was avoiding holding the file.
    /// </summary>
    [Fact]
    public void ANonSeekableStreamIsRefused()
    {
        using var forwardOnly = new ForwardOnlyStream(Bytes);

        var ex = Assert.Throws<ArgumentException>(() => new StreamByteRangeSource(forwardOnly));
        Assert.Contains("seekable", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A write-only stream is refused too, and for its own reason.</summary>
    [Fact]
    public void AnUnreadableStreamIsRefused()
    {
        using var writeOnly = new WriteOnlyStream();

        var ex = Assert.Throws<ArgumentException>(() => new StreamByteRangeSource(writeOnly));
        Assert.Contains("readable", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A null stream is a programming error, not a source with nothing in it.</summary>
    [Fact]
    public void ANullStreamIsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => new StreamByteRangeSource(null));
    }

    /// <summary>
    /// A buffer the range does not fit in is refused rather than silently truncated — the caller has
    /// made an arithmetic mistake, and reading less than it asked for would hide it.
    /// </summary>
    [Fact]
    public void ARangeThatOverrunsTheBufferIsRefused()
    {
        var source = Source(out MemoryStream stream);
        using (stream)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => source.TryRead(0, new byte[8], 4, 8, out _));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => source.TryRead(-1, new byte[8], 0, 8, out _));
            Assert.Throws<ArgumentNullException>(
                () => source.TryRead(0, null, 0, 8, out _));
        }
    }

    // ----------------------------------------------------------------

    private sealed class ForwardOnlyStream : Stream
    {
        private readonly MemoryStream _inner;

        public ForwardOnlyStream(byte[] bytes) => _inner = new MemoryStream(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class WriteOnlyStream : Stream
    {
        private long _length;

        public override bool CanRead => false;
        public override bool CanSeek => true;
        public override bool CanWrite => true;
        public override long Length => _length;
        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => Position;
        public override void SetLength(long value) => _length = value;
        public override void Write(byte[] buffer, int offset, int count) => _length += count;
    }
}
