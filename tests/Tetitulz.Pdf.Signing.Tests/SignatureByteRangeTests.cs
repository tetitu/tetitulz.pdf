// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The excluded hole in a signature's <c>/ByteRange</c> is the <c>/Contents</c> string object —
/// <c>&lt;…&gt;</c>, <b>delimiters included</b>.
/// </summary>
/// <remarks>
/// <para>This arithmetic was open-coded in four places: <c>PdfSigner</c>,
/// <c>PdfDocumentTimestamper</c>, and the two test fixtures that stand in for the timestamper
/// because its token would otherwise have to come from a real TSA over HTTP. The production
/// timestamper was the outlier by exactly one byte — it took <c>ByteRange[1] = contentsStart</c>,
/// leaving the opening <c>&lt;</c> inside the signed range.</para>
///
/// <para><b>Nothing could have caught it, for two compounding reasons.</b> The missing byte is a
/// constant at a fixed offset, so the digest stayed self-consistent and the signature verified: a
/// reader following that <c>/ByteRange</c> hashes exactly the bytes the producer hashed. And both
/// fixtures — under a comment claiming they produce "the same shape" as the library — used the
/// <i>signer's</i> convention, so the arithmetic that actually shipped was reached by no test.
/// A stand-in that silently differs from what it stands in for is worse than no coverage, because
/// the suite reports the difference as covered.</para>
///
/// <para><b>These assert on the produced bytes, not on the numbers.</b> Comparing a computed
/// <c>/ByteRange</c> against a second copy of the same formula proves only that two spellings
/// agree. What separates the two conventions is which <i>byte</i> the gap starts at, so that is
/// what is read back: <c>file[ByteRange[1]]</c> must be <c>&lt;</c> and
/// <c>file[ByteRange[2] - 1]</c> must be <c>&gt;</c>.</para>
/// </remarks>
public sealed class SignatureByteRangeTests
{
    /// <summary>
    /// A real signature, from the production signer, read back with an independent scan: the
    /// excluded gap starts exactly at the <c>&lt;</c> and ends exactly after the <c>&gt;</c>.
    /// </summary>
    /// <remarks>
    /// The <c>/ByteRange</c> is located by scanning the ASCII rather than through this library's
    /// own reader, so the assertion is a second opinion rather than a restatement.
    /// </remarks>
    [Fact]
    public void ASignedFilesExcludedGapIsExactlyTheContentsStringWithItsDelimiters()
    {
        var root = SigningTestCrypto.Ca("CN=ByteRange Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=ByteRange Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw);

        AssertGapIsTheContentsString(signed);
    }

    /// <summary>
    /// The same invariant on a document timestamp. <c>PdfDocumentTimestamper.AddTimestamp</c>
    /// cannot be called here — it fetches its token from a TSA over HTTP — so this runs the fixture
    /// that stands in for it, which now shares the library's arithmetic rather than repeating it.
    /// That is what makes the stand-in evidence about the real thing.
    /// </summary>
    [Fact]
    public void ATimestampRevisionsExcludedGapIsExactlyTheContentsStringWithItsDelimiters()
    {
        var root = SigningTestCrypto.Ca("CN=ByteRange Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=ByteRange Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);
        var tsa = SigningTestCrypto.TsaCert(root, "CN=ByteRange TSA");

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw);
        byte[] timestamped = SignedPdfFixtures.AppendDocumentTimestamp(
            signed, tsa, DateTime.UtcNow, new[] { root.Cert },
            sigDictObjNum: 20, sigFieldObjNum: 21);

        AssertGapIsTheContentsString(timestamped);
    }

    /// <summary>
    /// The unit-level statement of the same rule, against a hand-laid buffer: the computed range
    /// must land on the delimiters wherever the placeholder happens to sit.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(37)]
    public void ComputeLandsOnTheDelimiters_WhereverThePlaceholderSits(int leadingBytes)
    {
        // ... <deadbeef> ...
        string text = new string('x', leadingBytes) + "<deadbeef>" + new string('y', 11);
        byte[] file = Encoding.ASCII.GetBytes(text);

        long contentsStart = leadingBytes + 1;                 // first hex digit
        long contentsEnd = contentsStart + "deadbeef".Length;  // the '>'

        var range = Tetitulz.Pdf.Signing.SignatureByteRange.Compute(
            contentsStart, contentsEnd, file.Length);

        Assert.Equal((byte)'<', file[range.BeforeLength]);
        Assert.Equal((byte)'>', file[range.AfterStart - 1]);
        Assert.Equal(file.Length, range.AfterStart + range.AfterLength);
        Assert.Equal(leadingBytes + 11, range.SignedLength);
    }

    /// <summary>
    /// A placeholder whose first hex digit is at offset 0 has no room for its own <c>&lt;</c>, so
    /// the arithmetic refuses rather than computing a negative length. The pre-merge copies would
    /// each have produced <c>ByteRange[1] = -1</c> and read past the start of the file.
    /// </summary>
    [Fact]
    public void ComputeRefusesAMalformedPlaceholderRatherThanReturningANegativeLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Tetitulz.Pdf.Signing.SignatureByteRange.Compute(0, 8, 64));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Tetitulz.Pdf.Signing.SignatureByteRange.Compute(10, 4, 64));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Tetitulz.Pdf.Signing.SignatureByteRange.Compute(10, 40, 20));
    }

    // ================================================================ helper

    /// <summary>
    /// Reads every <c>/ByteRange</c> in the file with a plain scan and asserts, for each, that the
    /// gap it leaves is exactly one PDF hex string.
    /// </summary>
    private static void AssertGapIsTheContentsString(byte[] pdf)
    {
        string ascii = Encoding.ASCII.GetString(pdf);
        var matches = Regex.Matches(
            ascii, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");

        Assert.True(matches.Count > 0, "the fixture produced no /ByteRange to check");

        bool anyReachesEof = false;

        foreach (Match m in matches)
        {
            long start1 = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            long length1 = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            long start2 = long.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            long length2 = long.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);

            Assert.Equal(0, start1);

            // Not "every range reaches EOF" — in a counter-signed file every signature but the
            // last legitimately stops short, which is what the coverage warning exists to report.
            Assert.True(start2 + length2 <= pdf.Length,
                "a /ByteRange claims to cover bytes past the end of the file");
            if (start2 + length2 == pdf.Length) anyReachesEof = true;

            // The whole claim: the gap the signature excludes is the /Contents string object,
            // opening and closing delimiter included.
            Assert.Equal((byte)'<', pdf[length1]);
            Assert.Equal((byte)'>', pdf[start2 - 1]);

            // And it holds only hex digits in between — a gap that started one byte late would
            // still end on '>', so the far end alone does not pin the convention.
            for (long i = length1 + 1; i < start2 - 1; i++)
                Assert.True(IsHexDigit(pdf[i]),
                    $"byte {i} inside the excluded gap is 0x{pdf[i]:X2}, not a hex digit");
        }

        Assert.True(anyReachesEof,
            "no signature in this file covers it to EOF, so the fixture is not what it claims");
    }

    private static bool IsHexDigit(byte b)
        => (b >= (byte)'0' && b <= (byte)'9')
        || (b >= (byte)'a' && b <= (byte)'f')
        || (b >= (byte)'A' && b <= (byte)'F');
}
