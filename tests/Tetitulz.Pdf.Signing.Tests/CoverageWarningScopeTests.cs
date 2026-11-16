// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.IO;
using System.Linq;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The partial-coverage warning fires for a signature that really does cover only part of the file,
/// and <b>not</b> for one that covers all of it.
/// </summary>
/// <remarks>
/// <para><c>if (r.ByteRangeCoverageEnd &gt; 0 &amp;&amp; r.ByteRangeCoverageEnd &lt; fileLength)</c>
/// is two conditions and only the first was pinned. Deleting <c>&amp;&amp; … &lt; fileLength</c>
/// left the entire Signing suite green while making the warning fire on <b>every conforming
/// signature</b> — telling each caller that bytes were appended outside its digest when none
/// were.</para>
///
/// <para><b>A false warning on a security surface is not a cosmetic defect.</b> This particular
/// text tells the reader to go and check what later revisions changed and names a shadow attack as
/// the thing it might be. Fired on every ordinary signature, it is either acted on — wasting the
/// reader's time on a file that is fine — or learned to be ignored, which is worse, because the
/// one case it exists for then looks like all the others.</para>
///
/// <para>Both directions are asserted here, which is the point: a test for only the firing case
/// cannot tell a correct guard from one that always fires.</para>
/// </remarks>
public sealed class CoverageWarningScopeTests
{
    /// <summary>
    /// A single signature covers the whole file, so no partial-coverage warning may appear.
    /// </summary>
    [Fact]
    public void ASignatureCoveringTheWholeFile_IsNotWarnedAboutCoverage()
    {
        var root = SigningTestCrypto.Ca("CN=Coverage Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Coverage Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        byte[] signed = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw);

        var result = Verify(signed).Single();

        Assert.True(result.ByteRangeCoverageEnd > 0,
            "the fixture never reached the coverage check");
        Assert.DoesNotContain(result.Warnings, w => w.Contains("covers the first"));
    }

    /// <summary>
    /// The floor, and the half the guard's second condition exists for: once a second signature is
    /// appended, the first one really does cover only part of the file and must say so.
    /// </summary>
    [Fact]
    public void AnEarlierSignatureInACounterSignedDocument_IsWarnedAboutCoverage()
    {
        var root = SigningTestCrypto.Ca("CN=Coverage Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Coverage Leaf");
        var (pfx, pw) = SigningTestCrypto.Pfx(leaf, root.Cert);

        byte[] once = SignedPdfFixtures.Sign(SignedPdfFixtures.MinimalPdf(), pfx, pw);
        byte[] twice = SignedPdfFixtures.Sign(once, pfx, pw, fieldName: "Signature2");

        var results = Verify(twice);
        var first = results.Single(r => r.FieldName == "Signature1");
        var second = results.Single(r => r.FieldName == "Signature2");

        Assert.Contains(first.Warnings, w => w.Contains("covers the first"));

        // And the later one, which does reach EOF, still must not be warned — so the pair
        // separates "warns when it should" from "warns about whichever signature it is handed".
        Assert.DoesNotContain(second.Warnings, w => w.Contains("covers the first"));
    }

    private static SignatureVerificationResult[] Verify(byte[] pdf)
    {
        using var ms = new MemoryStream(pdf);
        using var doc = PdfDocument.Open(ms);
        return new PdfSignatureVerifier()
            .VerifyAll(doc, new SignatureVerificationOptions())
            .ToArray();
    }
}
