// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A revision's trailer must not name a previous cross-reference section that cannot exist.
/// <c>/Prev 0</c> points the chain at the <c>%PDF</c> header; an out-of-range value points past the
/// end of the file. Either is a claim a reader cannot follow, so the honest encoding of "there is
/// no previous section I can name" is to omit the key.
/// </summary>
/// <remarks>
/// <para><c>IncrementalUpdateWriter</c> applies that rule. <c>PdfSigner</c> and
/// <c>VisualSignatureBuilder</c> build and write their own trailers straight through
/// <c>PdfWriter.WriteTrailer</c>, where the value reaches them as 0 whenever <c>startxref</c>
/// cannot be located — <c>VisualSignatureBuilder</c> takes <c>parser.StartXrefOffset</c>, which is
/// 0 when the parser had to recover the file by scanning.</para>
///
/// <para><b>Signing refuses such a document outright</b>
/// (<c>SigningHelpers.RefuseUnnameableCrossReferenceChain</c>, in all three writers that build their
/// own trailer): omitting the key does not rescue it, since the appended revision declares only the
/// objects the signature wrote, nothing re-declares the rest, and the result reopens with a catalog
/// and <b>zero pages</b> — while the signature over it still verifies, because
/// <c>PdfSignatureVerifier</c> never consults the page tree. So <b>signing cannot reach the
/// <c>/Prev</c>-omission path at all</b>; <see cref="UnnameableXrefSigningTests"/> holds the
/// refusal, and what remains below is the floor, the half that stops the guard degenerating into
/// "never write <c>/Prev</c>".</para>
///
/// <para>The guard itself is live and needed — <c>IncrementalUpdateWriter</c> reaches it on the edit
/// path, where the answer is to recover rather than refuse, because a broken edit is loudly broken
/// and retryable while a broken signature is quietly authoritative.</para>
///
/// <para>So what is asserted is exactly what the guard changes: the trailer stops making a claim
/// that cannot be followed. That is worth having on its own — a <c>/Prev 0</c> is indistinguishable
/// to a reader from a deliberate chain into the header, and it is the difference between a file
/// that reports itself as unchained and one that lies about where its history is.</para>
/// </remarks>
public sealed class UnusablePreviousXrefTests
{
    /// <summary>
    /// Signing a document whose previous section cannot be located is <b>refused</b>, and nothing
    /// is written to the caller's stream.
    /// </summary>
    /// <remarks>
    /// Omitting the <c>/Prev</c> key is correct and insufficient — see the class remarks.
    /// </remarks>
    [Fact]
    public void SigningAFileWithNoNameablePreviousSection_IsRefusedRatherThanWrittenUnchained()
    {
        var root = SigningTestCrypto.Ca("CN=Prev Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Prev Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        byte[] input = WithTrailingJunk(SignedPdfFixtures.MinimalPdf());

        // The fixture really does drive the offset to 0 — asserted through the parser, which uses
        // the same 1 KB tail window Adobe's implementation note prescribes, so this is a file a
        // conforming reader is entitled to treat as needing recovery rather than a well-formed one.
        using (var probe = new MemoryStream(input))
        using (var doc = PdfDocument.Open(probe))
            Assert.Equal(0, doc.Parser.StartXrefOffset);

        // An appended trailer omitting /Prev is not enough: the resulting file would reopen with a
        // catalog and zero pages, and the signature over it would still verify. Signing refuses.
        using var toSign = new MemoryStream(input);
        using var output = new MemoryStream();
        Assert.Throws<PdfSigningException>(() => new PdfSigner().Sign(
            toSign, output, new SignatureParameters
            {
                CertificateStream = new MemoryStream(pfx),
                CertificatePassword = pw,
                SignatureFieldName = "Signature1",
            }));
        Assert.Equal(0, output.Length);
    }

    /// <summary>
    /// The floor, and the half that stops the guard from being "never write <c>/Prev</c>": an
    /// ordinary document, signed twice, chains its second revision to a real offset.
    /// </summary>
    /// <remarks>
    /// Not merely "a <c>/Prev</c> is present" — the value is checked to land on a byte range that
    /// really holds a cross-reference section, so a guard that emitted some arbitrary non-zero
    /// number would still fail.
    /// </remarks>
    [Fact]
    public void SigningATwiceSignedDocument_StillChainsToTheRealPreviousSection()
    {
        var root = SigningTestCrypto.Ca("CN=Prev Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Prev Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        byte[] once = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw);
        byte[] twice = SignedPdfFixtures.Sign(once, pfx, pw, fieldName: "Signature2");

        long[] prevs = PrevValues(twice);
        Assert.NotEmpty(prevs);

        foreach (long prev in prevs)
        {
            Assert.True(prev > 0 && prev < twice.Length,
                $"/Prev {prev} does not lie inside a {twice.Length}-byte file");

            // A cross-reference section starts with either "xref" (classic) or "N 0 obj" (stream).
            string at = Encoding.ASCII.GetString(
                twice, (int)prev, Math.Min(24, twice.Length - (int)prev));
            Assert.True(
                at.StartsWith("xref", StringComparison.Ordinal) ||
                Regex.IsMatch(at, @"^\d+\s+\d+\s+obj"),
                $"/Prev {prev} points at \"{at}\", which is not a cross-reference section");
        }

        // And the file is still a working two-signature document, so the chain really is followed.
        using var ms = new MemoryStream(twice);
        using var doc = PdfDocument.Open(ms);
        Assert.Single(doc.Pages);
    }

    // ================================================================ fixture

    /// <summary>Every <c>/Prev</c> value in the file, found by a plain scan.</summary>
    private static long[] PrevValues(byte[] pdf)
        => Regex.Matches(Encoding.ASCII.GetString(pdf), @"/Prev\s+(\d+)")
                .Select(m => long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();

    /// <summary>
    /// Appends more than a kilobyte after <c>%%EOF</c>, putting <c>startxref</c> outside the 1 KB
    /// tail window every reader here scans — the parser included, which is why the input is a
    /// recovery case rather than a well-formed file.
    /// </summary>
    private static byte[] WithTrailingJunk(byte[] pdf)
        => pdf.Concat(Encoding.ASCII.GetBytes("% " + new string('j', 2000) + "\n")).ToArray();
}
