// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core.Parsing;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Parsing;

/// <summary>
/// <see cref="PdfParser.FindStartXref"/> reads the operand that belongs to the keyword it found, and
/// nothing else.
/// </summary>
/// <remarks>
/// <para><b>These pinned a second scan until it was deleted.</b> <c>PdfSigner</c> carried its own
/// copy of this reading of the last kilobyte, returning 0 where the parser throws, and the signing
/// tests held it to these three answers. The signer now chains to the parser's value, so the tests
/// moved with the rule; the whitespace set is <c>StartXrefWhitespaceTests</c>' subject.</para>
/// </remarks>
public sealed class StartXrefOperandTests
{
    /// <summary>
    /// A negative offset is not a positive one. A scan that skips every non-digit eats the sign and
    /// reports <c>1234</c> — a plausible offset, which is the dangerous kind of wrong.
    /// </summary>
    [Fact]
    public void ASignedOperandIsNotReadAsItsAbsoluteValue()
    {
        using var s = Tail("startxref\n-1234\n%%EOF\n");

        Assert.Throws<PdfParseException>(() => new PdfParser(s).FindStartXref());
    }

    /// <summary>
    /// With no operand at all there is no offset — not the first number in whatever trailing junk a
    /// producer left after <c>%%EOF</c>.
    /// </summary>
    [Fact]
    public void AMissingOperandDoesNotBorrowADigitFromTrailingJunk()
    {
        using var s = Tail("startxref\n%%EOF\nProducerBuildStamp 987654\n");

        Assert.Throws<PdfParseException>(() => new PdfParser(s).FindStartXref());
    }

    /// <summary>
    /// A stream that returns fewer bytes than asked for — permitted of any <see cref="Stream"/>, and
    /// normal for pipe- and network-backed ones — still yields the offset.
    /// </summary>
    [Fact]
    public void AShortReadingStreamStillYieldsTheOffset()
    {
        var bytes = Encoding.ASCII.GetBytes(Pad("startxref\n54321\n%%EOF\n"));
        using var s = new DripStream(bytes, bytesPerRead: 7);

        Assert.Equal(54321, new PdfParser(s).FindStartXref());
    }

    /// <summary>
    /// The premise of the test above: the fixture really does underfill a read, so the assertion there
    /// is about the scan rather than about a stream that behaves like a <see cref="MemoryStream"/>.
    /// </summary>
    [Fact]
    public void TheDripFixtureReallyUnderfills()
    {
        var s = new DripStream(new byte[100], bytesPerRead: 7);
        Assert.Equal(7, s.Read(new byte[100], 0, 100));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The scan reads the last 1 KB, so the fixture is padded past that to prove the window is being
    /// honoured rather than the whole (tiny) stream being read by accident.
    /// </summary>
    private static string Pad(string tail) => new string('%', 2000) + "\n" + tail;

    private static MemoryStream Tail(string tail) => new(Encoding.ASCII.GetBytes(Pad(tail)));

    /// <summary>A seekable stream that never returns more than <c>bytesPerRead</c> bytes at once.</summary>
    private sealed class DripStream : MemoryStream
    {
        private readonly int _bytesPerRead;

        public DripStream(byte[] buffer, int bytesPerRead) : base(buffer, writable: false)
            => _bytesPerRead = bytesPerRead;

        public override int Read(byte[] buffer, int offset, int count)
            => base.Read(buffer, offset, Math.Min(count, _bytesPerRead));
    }
}
