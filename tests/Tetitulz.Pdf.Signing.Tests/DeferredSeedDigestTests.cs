// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="PdfDeferredSigner.Prepare"/> names the digest <see cref="PdfSigner"/> signs with, which a
/// field's seed value can require (<c>/SV /DigestMethod</c>): it named the caller's, so the far side
/// built its CMS with a digest the seed forbids. With no seed the caller's digest stands (the control).
/// </summary>
public sealed class DeferredSeedDigestTests
{
    private static byte[] FormPdf(string? seed) => SignedPdfFixtures.Assemble(
        "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [5 0 R] /SigFlags 3 >> >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Annots [5 0 R] >>",
        "<< /Length 0 >>\nstream\n\nendstream",
        "<< /FT /Sig /T (Sig1) /Type /Annot /Subtype /Widget /Rect [50 600 250 640] /F 4 /P 3 0 R" +
            (seed == null ? "" : " /SV " + seed) + " >>");

    private static PreparedSignature Prepare(string? seed)
    {
        var root = SigningTestCrypto.Ca("CN=Deferred Seed Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Deferred Seed Signer");
        var chain = new List<byte[]> { leaf.Cert.GetEncoded(), root.Cert.GetEncoded() };
        using var input = new MemoryStream(FormPdf(seed));
        using var output = new MemoryStream();
        return PdfDeferredSigner.Prepare(input, output, chain, new SignatureParameters { SignatureFieldName = "Sig1" });
    }

    [Fact]
    public void ASeedRequiringItsDigest_IsTheDigestThePreparedSignatureNames()
    {
        Assert.Equal("SHA512", Prepare("<< /DigestMethod [/SHA512] /Ff 64 >>").DigestAlgorithm.Replace("-", ""));
        Assert.Equal("SHA256", Prepare(null).DigestAlgorithm.Replace("-", ""));
    }
}
