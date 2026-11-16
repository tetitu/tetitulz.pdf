// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A signature made with <see cref="SignatureParameters.TimestampAuthorityUrl"/> carries a signature
/// timestamp its own verifier accepts — through a real HTTP round trip to a loopback TSA.
/// </summary>
/// <remarks>
/// <para><b>RFC 3161 Appendix A:</b> a signature timestamp's message imprint is the hash of the
/// SignerInfo's signature value. <c>PdfSigner</c> timestamped a hash of the whole CMS encoding and then
/// built the CMS again around the token, with a new signing time and so a new signature value; the
/// token matched nothing, and <c>HasTrustedTimestamp</c> stayed false for every locally signed
/// PAdES B-T document. An external signer's CMS was not timestamped at all — the token was fetched and
/// dropped. Nothing tested either path, because both needed a TSA.</para>
/// </remarks>
public sealed class SignatureTimestampRoundTripTests
{
    // Per test, not static: SigningTestCrypto.Keys hands each test its own sequence of distinct
    // keys, and a static root would hold a key a later test's TSA certificate could be given too.
    private readonly Lazy<(SigningTestCrypto.Issued Root, byte[] Pfx, string Password, SigningTestCrypto.Issued Leaf)> Keys = new(() =>
    {
        var root = SigningTestCrypto.Ca("CN=Timestamp Round Trip Root");
        var leaf = SigningTestCrypto.Leaf(root, "CN=Timestamp Round Trip Signer");
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, root.Cert);
        return (root, pfx, password, leaf);
    });

    [Fact]
    public void ALocallySignedTimestampIsTrustedByTheVerifier()
    {
        var root = Keys.Value.Root;
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);

        byte[] signed = Sign(new SignatureParameters
        {
            CertificateStream = new MemoryStream(Keys.Value.Pfx),
            CertificatePassword = Keys.Value.Password,
        }, tsa.Url);

        var result = Verify(signed);

        Assert.Equal(1, tsa.Requests);
        Assert.True(result.IsIntegrityValid);
        Assert.True(result.HasTrustedTimestamp,
            "Warnings: " + string.Join(" | ", result.Warnings));
    }

    /// <summary>An external signer's CMS is timestamped the same way, not fetched for and dropped.</summary>
    [Fact]
    public void AnExternalSignersSignatureIsTimestampedToo()
    {
        var root = Keys.Value.Root;
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);

        byte[] signed = Sign(new SignatureParameters
        {
            ExternalSigner = new LocalKeySigner(Keys.Value.Leaf, root.Cert, timestampToken: null),
        }, tsa.Url);

        var result = Verify(signed);

        Assert.Equal(1, tsa.Requests);
        Assert.True(result.HasTrustedTimestamp,
            "Warnings: " + string.Join(" | ", result.Warnings));
    }

    /// <summary>A device that timestamped its own CMS keeps its token; no second one is fetched.</summary>
    [Fact]
    public void AnExternalSignatureThatIsAlreadyTimestampedIsLeftAlone()
    {
        var root = Keys.Value.Root;
        var tsaCert = SigningTestCrypto.TsaCert(root, "CN=Device TSA");
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);

        byte[] signed = Sign(new SignatureParameters
        {
            ExternalSigner = new LocalKeySigner(Keys.Value.Leaf, root.Cert, timestampToken: tsaCert),
        }, tsa.Url);

        Assert.Equal(0, tsa.Requests);
        Assert.True(Verify(signed).HasTrustedTimestamp);
    }

    /// <summary>
    /// The imprint names one signature value, so a CMS with two SignerInfos cannot be given a token for
    /// one of them and passed off as timestamped — the signature is refused instead.
    /// </summary>
    [Fact]
    public void AnExternalCmsWithTwoSignersIsRefusedRatherThanTimestampedForOne()
    {
        var root = Keys.Value.Root;
        using var tsa = new LoopbackTsa(SigningTestCrypto.TsaCert(root, "CN=Loopback TSA"), root.Cert);

        var ex = Assert.Throws<PdfSigningException>(() => Sign(new SignatureParameters
        {
            ExternalSigner = new TwoSignerCms(Keys.Value.Leaf, root),
        }, tsa.Url));

        Assert.Contains("exactly one SignerInfo", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, tsa.Requests);
    }

    /// <summary>
    /// The verifier's side of the same rule. A CMS with two SignerInfos reports whichever came
    /// first as the signer, so a decoy put in front changes the name a reader is shown, while the
    /// cryptography of each SignerInfo still verifies. It is a validation failure, named; the
    /// control is the same document signed by one signer, which is valid.
    /// </summary>
    [Fact]
    public void AnEmbeddedCmsWithTwoSignersFailsVerificationAsAmbiguous()
    {
        var root = Keys.Value.Root;
        byte[] twoSigners = SignWithoutTimestamp(new SignatureParameters
        {
            ExternalSigner = new TwoSignerCms(Keys.Value.Leaf, root),
        });

        var result = Verify(twoSigners);

        Assert.True(result.IsIntegrityValid, "Each SignerInfo verifies; the refusal is structural.");
        Assert.False(result.IsValid);
        Assert.Contains(result.ValidationErrors, e => e.Contains("2 SignerInfos", StringComparison.Ordinal));

        byte[] oneSigner = SignWithoutTimestamp(new SignatureParameters
        {
            ExternalSigner = new LocalKeySigner(Keys.Value.Leaf, root.Cert, timestampToken: null),
        });
        var control = Verify(oneSigner);
        Assert.True(control.IsValid, "Errors: " + string.Join(" | ", control.ValidationErrors));
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] SignWithoutTimestamp(SignatureParameters parameters)
    {
        parameters.EstimatedSignatureSize = 32768;
        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, parameters);
        return output.ToArray();
    }

    /// <summary>An external signer whose CMS carries two SignerInfos over the same content.</summary>
    private sealed class TwoSignerCms : IExternalSigner
    {
        private readonly SigningTestCrypto.Issued _leaf;
        private readonly SigningTestCrypto.Issued _root;

        public TwoSignerCms(SigningTestCrypto.Issued leaf, SigningTestCrypto.Issued root)
        {
            _leaf = leaf;
            _root = root;
        }

        public byte[] SignData(byte[] data, string digestAlgorithm)
        {
            var generator = new CmsSignedDataGenerator();
            generator.AddSigner(_leaf.Private, _leaf.Cert, CmsSignedGenerator.DigestSha256);
            generator.AddSigner(_root.Private, _root.Cert, CmsSignedGenerator.DigestSha256);
            generator.AddCertificates(Org.BouncyCastle.Utilities.Collections.CollectionUtilities.CreateStore(
                new List<X509Certificate> { _leaf.Cert, _root.Cert }));
            return generator.Generate(new CmsProcessableByteArray(data), false).GetEncoded();
        }

        public byte[] SignDigest(byte[] digest, string digestAlgorithm) => throw new NotSupportedException();

        public IList<byte[]> GetCertificateChain() => new[] { _leaf.Cert.GetEncoded(), _root.Cert.GetEncoded() };

        public int EstimatedSignatureSize => 32768;
    }

    private static byte[] Sign(SignatureParameters parameters, string tsaUrl)
    {
        parameters.TimestampAuthorityUrl = tsaUrl;
        parameters.EstimatedSignatureSize = 32768;
        // No proxy: the TSA is on the loopback interface, and a system proxy must not see the request.
        parameters.TimestampNetwork = new SigningNetworkOptions { Proxy = new WebProxy() };

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();
        new PdfSigner().Sign(input, output, parameters);
        return output.ToArray();
    }

    private SignatureVerificationResult Verify(byte[] pdf)
    {
        using var doc = PdfDocument.Open(new MemoryStream(pdf, writable: false));
        return new PdfSignatureVerifier().VerifyAll(doc, new SignatureVerificationOptions
        {
            TrustAnchors = new[] { Keys.Value.Root.Cert },
        }).Single();
    }

    /// <summary>
    /// An <see cref="IExternalSigner"/> over a local key — optionally timestamping its own CMS the way a
    /// device that talks to its own TSA would.
    /// </summary>
    private sealed class LocalKeySigner : IExternalSigner
    {
        private readonly SigningTestCrypto.Issued _leaf;
        private readonly X509Certificate _root;
        private readonly SigningTestCrypto.Issued? _deviceTsa;

        public LocalKeySigner(SigningTestCrypto.Issued leaf, X509Certificate root, SigningTestCrypto.Issued? timestampToken)
        {
            _leaf = leaf;
            _root = root;
            _deviceTsa = timestampToken;
        }

        public byte[] SignData(byte[] data, string digestAlgorithm)
        {
            byte[] cms = CmsSignatureGenerator.GenerateDetachedSignature(
                data, _leaf.Private, _leaf.Cert, new List<X509Certificate> { _leaf.Cert, _root }, digestAlgorithm);
            if (_deviceTsa == null) return cms;

            SignerInformation signer = new CmsSignedData(cms).GetSignerInfos().GetSigners().Cast<SignerInformation>().Single();
            byte[] token = SigningTestCrypto.TimestampToken(signer.GetSignature(), _deviceTsa, DateTime.UtcNow, _root);
            return CmsSignatureGenerator.AddTimestampUnsignedAttribute(new CmsSignedData(cms), token).GetEncoded();
        }

        public byte[] SignDigest(byte[] digest, string digestAlgorithm) => throw new NotSupportedException();

        public IList<byte[]> GetCertificateChain() => new[] { _leaf.Cert.GetEncoded(), _root.GetEncoded() };

        public int EstimatedSignatureSize => 32768;
    }
}
