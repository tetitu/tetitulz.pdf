// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="IExternalSigner.EstimatedSignatureSize"/> reserves the space it says it does.
/// </summary>
/// <remarks>
/// <para><b>It documented itself as "used to reserve space in the /Contents placeholder" and
/// nothing read it.</b> Both the reserve (<c>PdfSigner</c>, where the placeholder is sized) and the
/// fit check that rejects an oversized CMS took <c>SignatureParameters.EstimatedSignatureSize</c>
/// alone, so a signer that measured its own output — <c>CertStoreExternalSigner</c> sums its real
/// certificate chain and adds 8 KB for exactly this purpose — was ignored, and a caller who left
/// the parameter at its 16 KB default got a <c>PdfSigningException</c> instead of the space their
/// signer had asked for.</para>
///
/// <para>It is the class of defect CLAUDE.md names repeatedly: <b>a comment asserting a property is
/// not the property</b>. Nothing else could see it — the interface member exists, is implemented
/// in-tree, and is pinned by the API baseline; only a signer that actually needs more than the
/// default separates "read" from "declared".</para>
/// </remarks>
public sealed class ExternalSignerReserveTests
{
    /// <summary>Comfortably over <c>SignatureParameters</c>' 16 KB default.</summary>
    private const int SignerWants = 40_000;

    /// <summary>
    /// Bigger than the default reserve, smaller than what the signer asked for — the one window
    /// where the two numbers disagree, and therefore the only fixture that can tell them apart.
    /// </summary>
    private const int BlobSize = 20_000;

    /// <summary>
    /// The claim: a signer that asks for more room than the parameter gets it, and signing succeeds.
    /// </summary>
    [Fact]
    public void TheSignersOwnEstimate_WidensTheContentsReserve()
    {
        byte[] signed = Sign(new FixedSizeSigner(SignerWants, BlobSize));

        // The placeholder really was sized from the signer's number: the /Contents hex run is
        // 2 * SignerWants characters, not 2 * the 16 KB default.
        Assert.Equal(SignerWants * 2, ContentsHexLength(signed));
    }

    /// <summary>
    /// The control that makes the test above mean something: the <b>same blob</b> with a signer that
    /// asks for nothing is refused, because it does not fit the default reserve. Without this, "it
    /// signed" is satisfied by a 20 KB blob that would have fitted anyway.
    /// </summary>
    [Fact]
    public void WithoutTheSignersEstimate_TheSameBlobIsRefused()
    {
        var ex = Assert.Throws<Tetitulz.Pdf.Core.PdfSigningException>(
            () => Sign(new FixedSizeSigner(estimatedSize: 0, blobSize: BlobSize)));

        Assert.Contains("exceeds the reserved size", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The caller's parameter is a floor, not an override: a signer asking for <i>less</i> than the
    /// parameter does not shrink the reserve. Max, not "the signer wins" — under-reserving from
    /// either side is the failure to avoid, and this pins which way the comparison goes.
    /// </summary>
    [Fact]
    public void ASmallerSignerEstimate_DoesNotShrinkTheCallersReserve()
    {
        byte[] signed = Sign(new FixedSizeSigner(estimatedSize: 1000, blobSize: 500),
                             callerEstimate: 30_000);

        Assert.Equal(30_000 * 2, ContentsHexLength(signed));
    }

    // ------------------------------------------------------------------

    /// <summary>Length of the hex run inside the signature dictionary's <c>/Contents &lt;…&gt;</c>.</summary>
    private static int ContentsHexLength(byte[] pdf)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        int at = raw.LastIndexOf("/Contents <", StringComparison.Ordinal);
        if (at < 0) at = raw.LastIndexOf("/Contents<", StringComparison.Ordinal);
        Assert.True(at >= 0, "no /Contents placeholder in the signed output");

        int open = raw.IndexOf('<', at);
        int close = raw.IndexOf('>', open + 1);
        Assert.True(close > open, "the /Contents placeholder is unterminated");

        return close - open - 1;
    }

    private static byte[] Sign(IExternalSigner signer, int? callerEstimate = null)
    {
        var root = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(root);

        var parameters = new SignatureParameters { ExternalSigner = signer };
        if (callerEstimate.HasValue) parameters.EstimatedSignatureSize = callerEstimate.Value;

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, parameters);
        return output.ToArray();
    }

    /// <summary>
    /// An <see cref="IExternalSigner"/> that returns a blob of a chosen size and declares a chosen
    /// estimate — the two numbers this test needs to vary independently.
    /// </summary>
    /// <remarks>
    /// The blob is not a real CMS. Nothing on this path parses it: <c>PdfSigner</c> measures it
    /// against the reserve and hex-encodes it into <c>/Contents</c>, which is exactly the behaviour
    /// under test. A real CMS would make the fixture's size depend on BouncyCastle's output rather
    /// than on the number this test is varying.
    /// </remarks>
    private sealed class FixedSizeSigner : IExternalSigner
    {
        private readonly int _estimatedSize;
        private readonly byte[] _blob;
        private readonly IList<byte[]> _chain;

        internal FixedSizeSigner(int estimatedSize, int blobSize)
        {
            _estimatedSize = estimatedSize;
            _blob = new byte[blobSize];
            for (int i = 0; i < _blob.Length; i++) _blob[i] = (byte)(i & 0xFF);

            var root = SigningTestCrypto.Ca();
            var leaf = SigningTestCrypto.Leaf(root);
            _chain = new List<byte[]> { leaf.Cert.GetEncoded(), root.Cert.GetEncoded() };
        }

        public byte[] SignDigest(byte[] digest, string digestAlgorithm) => _blob;

        public byte[] SignData(byte[] data, string digestAlgorithm) => _blob;

        public IList<byte[]> GetCertificateChain() => _chain;

        public int EstimatedSignatureSize => _estimatedSize;
    }
}
