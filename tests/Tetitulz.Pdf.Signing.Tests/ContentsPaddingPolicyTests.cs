// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A <c>/Contents</c> string whose padding is not all zeros is reported, and refused only when the
/// caller asks for it.
/// </summary>
/// <remarks>
/// <para><b>The padding is unsigned storage, and nothing was looking at it.</b> A signature's
/// <c>/Contents</c> is written at a fixed reserve and the CMS inside it is always shorter, so the
/// remainder lies in the <c>/ByteRange</c> gap — excluded from the digest by construction, and
/// therefore writable after signing without disturbing the signature. The verifier's existing
/// whole-hole check measures the gap against the <b>padded</b> length, so it sees bytes
/// <i>outside</i> the hex string and says nothing at all about bytes inside it. The CMS parser sees
/// nothing either: the reader stops at the end of the DER object.</para>
///
/// <para><b>Reporting and refusing are separated on purpose.</b> Nothing in ISO 32000-1 requires
/// the padding to be zeros, so a non-zero tail is evidence of an unusual producer as readily as of
/// tampering — and a verifier that rejected on it would call other people's valid documents
/// invalid, which is the more damaging error for a library whose verdict a caller acts on. So the
/// fact is always on the result, the warning is always raised, and the verdict changes only under
/// <see cref="SignatureVerificationOptions.RejectNonZeroContentsPadding"/>.</para>
///
/// <para><b>Every fixture here rewrites bytes in the real signed file</b> rather than constructing
/// a document that looks tampered with: the claim is that a signature stays cryptographically
/// intact while its hole is edited, and only doing the edit demonstrates it.</para>
/// </remarks>
public sealed class ContentsPaddingPolicyTests
{
    /// <summary>An untouched signature reports nothing and warns about nothing.</summary>
    /// <remarks>
    /// <b>The acceptance control, and it is the load-bearing one.</b> Every signature this library
    /// writes is padded, so a check that fired on the padding itself would fire on every document
    /// in the world — including its own output — and a caller would learn to ignore it. That is
    /// `IgnoredParameterReporting.Deviates`' rule in another area: presence is not deviation.
    /// </remarks>
    [Fact]
    public void AnUntouchedSignatureReportsNoPaddingAnomaly()
    {
        SignatureVerificationResult result = Verify(Signed(), reject: false);

        Assert.True(result.IsIntegrityValid, "the fixture is not a valid signature to begin with");
        Assert.False(result.HasNonZeroContentsPadding);
        Assert.DoesNotContain(result.Warnings,
            w => w.IndexOf("non-zero", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>A byte written into the padding is reported, and does not invalidate by default.</summary>
    /// <remarks>
    /// <b>Both halves are the finding.</b> That the signature still verifies is the security
    /// observation — the bytes are outside the digest, so cryptography has nothing to say about
    /// them — and that the result now carries the fact is the only reason a caller can act.
    /// </remarks>
    [Fact]
    public void AByteWrittenIntoThePaddingIsReportedAndStillVerifies()
    {
        SignatureVerificationResult result = Verify(SignedWithPaddingByte(), reject: false);

        Assert.True(result.IsIntegrityValid,
            "editing the excluded gap broke the signature, so the fixture is not demonstrating "
            + "what it claims: those bytes are outside the digest");
        Assert.True(result.HasNonZeroContentsPadding);
        Assert.True(result.IsValid, "the default policy must not reject other producers' documents");

        Assert.Contains(result.Warnings,
            w => w.IndexOf("non-zero bytes after the CMS object", StringComparison.Ordinal) >= 0);
    }

    /// <summary>Under the strict policy the same document is a validation error.</summary>
    [Fact]
    public void UnderTheStrictPolicyThePaddingByteIsAValidationError()
    {
        SignatureVerificationResult result = Verify(SignedWithPaddingByte(), reject: true);

        Assert.True(result.IsIntegrityValid, "the signature itself must still be intact");
        Assert.False(result.IsValid);

        // The reason must be this one. A refusal test that merely counts errors passes when some
        // other check fails for some other reason, which is the failure mode a fixture built by
        // editing bytes is most exposed to.
        Assert.Contains(result.ValidationErrors,
            e => e.IndexOf("non-zero bytes after the CMS object", StringComparison.Ordinal) >= 0);
    }

    /// <summary>The strict policy leaves an ordinary padded signature alone.</summary>
    /// <remarks>
    /// The acceptance control for the refusal above: without it, "strict rejects the edited file"
    /// is equally satisfied by a policy that rejects everything.
    /// </remarks>
    [Fact]
    public void TheStrictPolicyStillAcceptsAnOrdinaryPaddedSignature()
    {
        SignatureVerificationResult result = Verify(Signed(), reject: true);

        Assert.False(result.HasNonZeroContentsPadding);
        Assert.True(result.IsValid);
    }

    /// <summary>The default is off, so upgrading does not change any caller's verdicts.</summary>
    [Fact]
    public void TheDefaultPolicyIsToReportRatherThanRefuse()
        => Assert.False(new SignatureVerificationOptions().RejectNonZeroContentsPadding);

    // ==================================================================== helpers

    private SignatureVerificationResult Verify(byte[] pdf, bool reject)
    {
        using var ms = new MemoryStream(pdf);
        using PdfDocument doc = PdfDocument.Open(ms);

        var results = new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            RejectNonZeroContentsPadding = reject,

            // Trust is a separate question and an untrusted chain would mask the verdict this file
            // is about, so the fixture's own root is anchored.
            TrustAnchors = new[] { Root.Cert },
        });

        Assert.True(results.Count == 1,
            $"expected one signature, found {results.Count}; the fixture never reached the check");
        return results[0];
    }

    /// <summary>
    /// One CA per test, so the anchor above matches every fixture it builds. Per test rather than
    /// static: <see cref="SigningTestCrypto.Keys"/> hands each test its own sequence of distinct keys,
    /// and a static root would hold a key from whichever test touched it first — one a later test's
    /// leaf could be given too.
    /// </summary>
    private readonly SigningTestCrypto.Issued Root =
        SigningTestCrypto.Ca("CN=Contents Padding Policy Root");

    private byte[] Signed()
    {
        var leaf = SigningTestCrypto.Leaf(Root, "CN=Contents Padding Policy Leaf");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, Root.Cert);

        return SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, password);
    }

    /// <summary>
    /// The same signature with one non-zero byte written into the padding, in place.
    /// </summary>
    /// <remarks>
    /// <para>The byte goes into the <b>hex digits</b>, not into the file's raw bytes: the hole is
    /// an ASCII hex string, so writing a byte means writing two digits. It is placed near the end
    /// of the hole, comfortably past where any CMS could reach, and the file length does not change
    /// — so every offset in the document, the <c>/ByteRange</c> included, stays valid and the
    /// signature stays verifiable. That is the point: nothing about the document is broken.</para>
    ///
    /// <para>The hole is located through the <c>/ByteRange</c> rather than by matching
    /// <c>/Contents</c>, because the gap the signature excludes <i>is</i> the <c>/Contents</c>
    /// string and its delimiters — a fact already pinned elsewhere in this suite, so this reuses it
    /// rather than guessing again.</para>
    /// </remarks>
    private byte[] SignedWithPaddingByte()
    {
        byte[] pdf = Signed();
        string ascii = Encoding.ASCII.GetString(pdf);

        Match m = Regex.Match(ascii, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Assert.True(m.Success, "the fixture produced no /ByteRange, so it is not a signed file");

        int gapStart = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int gapEnd = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);

        Assert.Equal((byte)'<', pdf[gapStart]);
        Assert.Equal((byte)'>', pdf[gapEnd - 1]);

        // Two digits, four bytes in from the closing '>' — inside the reserve, on a byte boundary.
        int digit = gapEnd - 1 - 8;
        Assert.True(digit > gapStart + 1, "the hole is too small to carry a padding byte");
        Assert.Equal((byte)'0', pdf[digit]);
        Assert.Equal((byte)'0', pdf[digit + 1]);

        pdf[digit] = (byte)'4';
        pdf[digit + 1] = (byte)'2';
        return pdf;
    }
}
