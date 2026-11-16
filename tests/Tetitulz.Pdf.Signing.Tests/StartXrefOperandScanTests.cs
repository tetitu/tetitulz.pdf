// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Signing;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfSigner"/>'s <c>startxref</c> scan reads the operand that belongs to the keyword it
/// found, and leaves the stream at end of file.
/// </summary>
/// <remarks>
/// <para>Both properties are load-bearing and neither was asserted. The scan is what supplies
/// <c>/Prev</c> for the signature revision <b>and</b> for the DSS revision, and in the latter case
/// its read-to-EOF was the only thing moving the stream off the <c>/Contents</c> placeholder before
/// <c>PdfWriter</c> — which writes at the current position and never seeks — appended. A short read
/// therefore did not merely truncate the window: it left the position mid-file and the DSS revision
/// would have landed on top of the signature. The seek is now explicit in <c>EmbedDss</c>; these
/// tests pin the scan's own two guarantees.</para>
///
/// <para><b>Why the scan is tested directly rather than through <c>Sign</c>.</b> The second call
/// site runs only on the PAdES B-LT path, which needs a live OCSP/CRL responder — the reason
/// <c>DssRevisionCatalogTests</c> gives for not covering it. A test that cannot reach the code is
/// worse than one that names why it reaches it a level down.</para>
/// </remarks>
public sealed class StartXrefOperandScanTests
{
    /// <summary>
    /// §7.2.3 Table 1 lists six whitespace bytes. The operand is found after any of them.
    /// </summary>
    [Theory]
    [InlineData(" ")]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    [InlineData("\f")]
    [InlineData("\0")]
    public void TheOperandIsFoundAfterAnySpecWhitespace(string gap)
    {
        using var s = Tail("startxref" + gap + "12345\n%%EOF\n");
        Assert.Equal(12345, PdfSigner.FindLastXrefOffset(s));
    }

    /// <summary>
    /// A negative offset is not a positive one. The old scan skipped every non-digit without limit,
    /// so it ate the sign and reported <c>1234</c> — a plausible offset, which is the dangerous
    /// kind of wrong, because a caller cannot tell it from a real one.
    /// </summary>
    [Fact]
    public void ASignedOperandIsNotReadAsItsAbsoluteValue()
    {
        using var s = Tail("startxref\n-1234\n%%EOF\n");
        Assert.Equal(0, PdfSigner.FindLastXrefOffset(s));
    }

    /// <summary>
    /// With no operand at all, the answer is "none" — not the first number appearing in whatever
    /// trailing junk a producer left after <c>%%EOF</c>, which the unbounded skip used to return.
    /// </summary>
    [Fact]
    public void AMissingOperandDoesNotBorrowADigitFromTrailingJunk()
    {
        using var s = Tail("startxref\n%%EOF\nProducerBuildStamp 987654\n");
        Assert.Equal(0, PdfSigner.FindLastXrefOffset(s));
    }

    /// <summary>
    /// <b>The one that matters for correctness of the output file.</b> A stream that returns fewer
    /// bytes than asked for — permitted of any <see cref="Stream"/>, and normal for pipe- and
    /// network-backed ones — must still yield the offset, and must still leave the position at end
    /// of file.
    /// </summary>
    [Fact]
    public void AShortReadingStreamStillYieldsTheOffsetAndEndsAtEof()
    {
        var bytes = Encoding.ASCII.GetBytes(Pad("startxref\n54321\n%%EOF\n"));
        using var s = new DripStream(bytes, bytesPerRead: 7);

        Assert.Equal(54321, PdfSigner.FindLastXrefOffset(s));
        Assert.Equal(s.Length, s.Position);
    }

    /// <summary>
    /// The premise of the test above: this fixture really does underfill a read, so the assertion
    /// there is about the scan rather than about a stream that behaves like a
    /// <see cref="MemoryStream"/>.
    /// </summary>
    [Fact]
    public void TheDripFixtureReallyUnderfills()
    {
        var s = new DripStream(new byte[100], bytesPerRead: 7);
        Assert.Equal(7, s.Read(new byte[100], 0, 100));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// The scan reads the last 1 KB, so the fixture is padded past that to prove the window is
    /// being honoured rather than the whole (tiny) stream being read by accident.
    /// </summary>
    private static string Pad(string tail) => new string('%', 2000) + "\n" + tail;

    private static MemoryStream Tail(string tail)
        => new MemoryStream(Encoding.ASCII.GetBytes(Pad(tail)));

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
