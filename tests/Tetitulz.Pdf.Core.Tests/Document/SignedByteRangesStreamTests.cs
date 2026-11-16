// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Document;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Document;

/// <summary>
/// The two questions <see cref="SignedByteRanges"/> answers against the file itself: whether a
/// signature's coverage reaches the end of the document, and what bytes it covers.
/// </summary>
public sealed class SignedByteRangesStreamTests
{
    /// <summary>A stream that returns at most one byte from each <c>Read</c>.</summary>
    private sealed class OneByteAtATimeStream : MemoryStream
    {
        public OneByteAtATimeStream(byte[] data) : base(data, writable: false) { }

        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, 1));
    }

    private static MemoryStream File(string text) => new(Encoding.ASCII.GetBytes(text), writable: false);

    /// <summary>
    /// ISO 32000-2 §12.8.1: the range ends "at the end of the "%%EOF" comment, possibly followed by
    /// an optional EOL marker".
    /// </summary>
    [Theory]
    [InlineData("startxref\n9\n%%EOF", 0, true)]         // covers the last byte
    [InlineData("startxref\n9\n%%EOF\n", 1, true)]       // LF left out
    [InlineData("startxref\n9\n%%EOF\r", 1, true)]       // CR left out
    [InlineData("startxref\n9\n%%EOF\r\n", 2, true)]     // CR LF left out
    [InlineData("startxref\n9\n%%EOF\n\r", 2, false)]    // LF CR is two markers, not one
    [InlineData("startxref\n9\n%%EOF\n\n", 2, false)]
    [InlineData("startxref\n9\n%%EOF\r\n\n", 3, false)]  // a marker and then something more
    [InlineData("startxref\n9\n%%EOX\n", 1, false)]      // the byte before is not the end of %%EOF
    [InlineData("startxref\n9\n%%EOF x", 2, false)]
    public void ReachesEndOfFile_OnlyAtTheLastByteOrBeforeOneEndOfLineAfterEof(
        string text, int unsignedTail, bool reaches)
    {
        using var file = File(text);
        long end = text.Length - unsignedTail;

        Assert.Equal(reaches, SignedByteRanges.ReachesEndOfFile(file, end, text.Length));
    }

    [Fact]
    public void ReachesEndOfFile_LeavesTheStreamWhereItWas()
    {
        using var file = File("startxref\n9\n%%EOF\n");
        file.Position = 3;

        SignedByteRanges.ReachesEndOfFile(file, file.Length - 1, file.Length);

        Assert.Equal(3, file.Position);
    }

    [Fact]
    public void ReachesEndOfFile_ReadsAStreamThatReturnsOneByteAtATime()
    {
        using var file = new OneByteAtATimeStream(Encoding.ASCII.GetBytes("startxref\n9\n%%EOF\r\n"));

        Assert.True(SignedByteRanges.ReachesEndOfFile(file, file.Length - 2, file.Length));
    }

    /// <summary>A range claiming bytes past the end of the file is not coverage, and must not be read as such.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ReachesEndOfFile_IsFalseForARangeEndingPastTheFile(int beyond)
    {
        using var file = File("startxref\n9\n%%EOF\n");

        Assert.False(SignedByteRanges.ReachesEndOfFile(file, file.Length + beyond, file.Length));
    }

    [Fact]
    public void ReachesEndOfFile_IsFalseForAFileTooShortToHoldTheMarker()
    {
        using var file = File("OF\n");

        Assert.False(SignedByteRanges.ReachesEndOfFile(file, 2, 3));
    }

    /// <summary>A length that over-reports the stream is a short read, not a match on whatever the buffer held.</summary>
    [Fact]
    public void ReachesEndOfFile_IsFalseWhenTheStreamEndsBeforeTheStatedLength()
    {
        using var file = File("%%EOF");

        Assert.False(SignedByteRanges.ReachesEndOfFile(file, 5, 6));
    }

    [Fact]
    public void ReachesEndOfFile_WithNoStream_OnlyAnExactMatchCounts()
    {
        Assert.True(SignedByteRanges.ReachesEndOfFile(null, 100, 100));
        Assert.False(SignedByteRanges.ReachesEndOfFile(null, 99, 100));
    }

    [Fact]
    public void ReadSignedBytes_ConcatenatesBothRanges()
    {
        using var file = new OneByteAtATimeStream(Encoding.ASCII.GetBytes("before<00>after"));

        byte[] signed = SignedByteRanges.ReadSignedBytes(file, 0, 6, 10, 5);

        Assert.Equal("beforeafter", Encoding.ASCII.GetString(signed));
    }

    [Theory]
    [InlineData(0, 6, 10, 6)]    // inside the second range
    [InlineData(0, 20, 20, 0)]   // inside the first range, with nothing asked of the second
    public void ReadSignedBytes_IsNullWhenTheFileEndsFirst(long o1, long l1, long o2, long l2)
    {
        using var file = File("before<00>after");

        Assert.Null(SignedByteRanges.ReadSignedBytes(file, o1, l1, o2, l2));
    }

    /// <summary>
    /// The streamed digest hashes exactly what <see cref="SignedByteRanges.ReadSignedBytes"/> returns —
    /// over ranges several chunks long, through a stream that hands back a few bytes per read.
    /// </summary>
    [Fact]
    public void DigestSignedBytes_FeedsExactlyTheSignedBytes()
    {
        var data = new byte[300_000];
        new Random(32000).NextBytes(data);
        const long o1 = 0, l1 = 200_000, o2 = 200_018, l2 = 99_982;

        byte[] expected = SHA256(SignedByteRanges.ReadSignedBytes(new MemoryStream(data), o1, l1, o2, l2));

        using var file = new ChunkedStream(data, 4_093);
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(
            System.Security.Cryptography.HashAlgorithmName.SHA256);
        Assert.True(SignedByteRanges.DigestSignedBytes(file, o1, l1, o2, l2, sha.AppendData));

        Assert.Equal(expected, sha.GetHashAndReset());
    }

    [Theory]
    [InlineData(0, 6, 10, 6)]    // inside the second range
    [InlineData(0, 20, 20, 0)]   // inside the first range
    public void DigestSignedBytes_IsFalseWhenTheFileEndsFirst(long o1, long l1, long o2, long l2)
    {
        using var file = File("before<00>after");

        Assert.False(SignedByteRanges.DigestSignedBytes(file, o1, l1, o2, l2, (_, _, _) => { }));
    }

    [Theory]
    [InlineData(-1, 6, 10, 5)]
    [InlineData(0, -6, 10, 5)]
    [InlineData(0, 6, -10, 5)]
    [InlineData(0, 6, 10, -5)]
    public void DigestSignedBytes_IsFalseForANegativeValue(long o1, long l1, long o2, long l2)
    {
        using var file = File("before<00>after");

        Assert.False(SignedByteRanges.DigestSignedBytes(file, o1, l1, o2, l2, (_, _, _) => { }));
    }

    private static byte[] SHA256(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return sha.ComputeHash(data);
    }

    /// <summary>A stream that returns at most <c>chunk</c> bytes from each <c>Read</c>.</summary>
    private sealed class ChunkedStream : MemoryStream
    {
        private readonly int _chunk;

        public ChunkedStream(byte[] data, int chunk) : base(data, writable: false) => _chunk = chunk;

        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, _chunk));
    }

    [Theory]
    [InlineData(-1, 6, 10, 5)]
    [InlineData(0, -6, 10, 5)]
    [InlineData(0, 6, -10, 5)]
    [InlineData(0, 6, 10, -5)]
    [InlineData(0, int.MaxValue, 10, 1)]              // one past what an array can hold
    [InlineData(0, 1, 10, (long)int.MaxValue + 1)]
    [InlineData(0, long.MaxValue, 10, long.MaxValue)] // a sum that wraps
    public void ReadSignedBytes_IsNullForRangesNoArrayCanHold(long o1, long l1, long o2, long l2)
    {
        using var file = File("before<00>after");

        Assert.Null(SignedByteRanges.ReadSignedBytes(file, o1, l1, o2, l2));
    }
}
