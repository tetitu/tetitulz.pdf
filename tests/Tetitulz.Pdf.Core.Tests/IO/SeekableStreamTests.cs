// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.IO;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.IO;

/// <summary>
/// The one seekable-view helper, and the ownership answer that is the reason it exists.
/// </summary>
/// <remarks>
/// <para>Four implementations existed across three assemblies, differing on whether an
/// already-seekable stream is rewound. That difference is kept as a parameter, because it was
/// deliberate: verification must read from byte 0 while the signing and timestamping paths read
/// from the caller's current position.</para>
///
/// <para><b>What none of them expressed is who owns the result.</b> A helper returning
/// <c>Stream</c> alone leaves a caller unable to tell a buffer it allocated from the argument it
/// was handed — so the choices are to leak the buffer or to close the caller's stream. Every call
/// site in the tree took the first, except three that hand-wrote
/// <c>ReferenceEquals(seekable, input)</c>.</para>
/// </remarks>
public class SeekableStreamTests
{
    private static readonly byte[] Payload = { 9, 8, 7, 6, 5 };

    [Fact]
    public void ASeekableStreamIsHandedBackUnchangedAndIsNotTheCallersToDispose()
    {
        using var source = new MemoryStream(Payload);
        source.Position = 3;

        Stream result = SeekableStream.Ensure(source, rewind: false, out bool owned);

        Assert.Same(source, result);
        Assert.False(owned);
        Assert.Equal(3, result.Position);   // rewind:false leaves the caller's cursor alone
    }

    [Fact]
    public void RewindMovesAnAlreadySeekableStreamToZero()
    {
        using var source = new MemoryStream(Payload);
        source.Position = 3;

        Stream result = SeekableStream.Ensure(source, rewind: true, out bool owned);

        Assert.Same(source, result);
        Assert.False(owned);
        Assert.Equal(0, result.Position);
    }

    /// <summary>
    /// A forward-only stream is buffered, and the caller is told the buffer is theirs to release.
    /// That flag is the whole point: without it the buffer is unreachable and simply leaks.
    /// </summary>
    [Fact]
    public void AForwardOnlyStreamIsBufferedAndTheBufferIsOwned()
    {
        using var source = new ForwardOnlyStream(Payload);

        Stream result = SeekableStream.Ensure(source, rewind: true, out bool owned);

        Assert.NotSame(source, result);
        Assert.True(owned);
        Assert.True(result.CanSeek);
        Assert.Equal(0, result.Position);
        Assert.Equal(Payload, ((MemoryStream)result).ToArray());

        // The caller can release what it owns, and doing so leaves its own stream alone.
        result.Dispose();
        Assert.False(source.Disposed);
    }

    /// <summary>
    /// A buffered result really is disposable — <see cref="MemoryStream.Position"/> throws
    /// afterwards. Without this the "owned" flag could be true and mean nothing.
    /// </summary>
    [Fact]
    public void DisposingAnOwnedBufferReleasesIt()
    {
        using var source = new ForwardOnlyStream(Payload);
        Stream result = SeekableStream.Ensure(source, rewind: true, out bool owned);

        Assert.True(owned);
        result.Dispose();

        Assert.Throws<ObjectDisposedException>(() => result.Position);
    }

    /// <summary>
    /// <c>Rewound</c> never allocates, whatever it is given. It is what a caller uses when the
    /// consumer already buffers a non-seekable stream itself — which <c>PdfDocument.Open</c> does,
    /// and owns the copy, which is why wrapping its argument bought nothing and cost a leak.
    /// </summary>
    [Fact]
    public void RewoundAllocatesNothingForEitherKindOfStream()
    {
        using var seekable = new MemoryStream(Payload) { Position = 4 };
        Assert.Same(seekable, SeekableStream.Rewound(seekable));
        Assert.Equal(0, seekable.Position);

        using var forwardOnly = new ForwardOnlyStream(Payload);
        Assert.Same(forwardOnly, SeekableStream.Rewound(forwardOnly));
    }

    [Fact]
    public void ANullStreamIsRefusedRatherThanDereferenced()
    {
        Assert.Throws<ArgumentNullException>(() => SeekableStream.Ensure(null, rewind: true, out _));
        Assert.Throws<ArgumentNullException>(() => SeekableStream.Ensure(null));
        Assert.Throws<ArgumentNullException>(() => SeekableStream.Rewound(null));
    }

    /// <summary>A stream that can be read once, front to back, and nothing else.</summary>
    private sealed class ForwardOnlyStream : Stream
    {
        private readonly byte[] _data;
        private int _at;

        public ForwardOnlyStream(byte[] data) => _data = data;

        /// <summary>
        /// Set by Dispose. `CanRead` cannot stand in for this: it is an override returning a
        /// constant, so it stays true after disposal and an assertion on it could never fail —
        /// banned test pattern 3, and the first draft of these tests had exactly that.
        /// </summary>
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _data.Length - _at);
            Array.Copy(_data, _at, buffer, offset, n);
            _at += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
