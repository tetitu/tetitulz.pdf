// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Tests.Signing;

public partial class EcCurveSignatureRoundTripTests
{
    /// <summary>
    /// An Ed448 verifier none of whose code the signer runs on (<see cref="IndependentEd448Cms"/>:
    /// its own DER reader, SHAKE256 and RFC 8032 arithmetic) verifies the Ed448 CMS over the signed
    /// byte ranges, and refuses it over those ranges with a byte changed.
    /// </summary>
    [Fact]
    public void Ed448_Cms_VerifiesInAnIndependentImplementation()
    {
        var (cms, content, _) = SignEd448("Ed448 for an independent verifier");
        var result = IndependentEd448Cms.Verify(cms, content);
        Assert.True(result.Verified, "the independent check refused the signature: " + result.Reason);
        Assert.Equal("2.16.840.1.101.3.4.2.12", result.DigestAlgorithm);
        Assert.False(result.DigestHasParameters);
        content[content.Length / 2] ^= 1;
        Assert.False(IndependentEd448Cms.Verify(cms, content).Verified);
    }
}
