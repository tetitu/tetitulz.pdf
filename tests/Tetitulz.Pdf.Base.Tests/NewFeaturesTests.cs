// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.TestHelpers;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Base.Tests;

/// <summary>
/// The external-signer (PKCS#11 / HSM) interface contract.
/// </summary>
/// <remarks>
/// This file also held seven <c>ConvertToLayoutHtml</c> tests, which tested
/// <c>Tetitulz.Pdf.Plugins.Html</c> from <c>Base.Tests</c>. They now live beside that package's own
/// tests as <c>LayoutHtmlConversionTests</c>.
/// </remarks>
public class NewFeaturesTests
{
    // ================================================================
    //  External signer (IExternalSigner) interface
    // ================================================================

    /// <summary>
    /// <see cref="IExternalSigner"/> is implemented <b>outside</b> this repository — by callers
    /// wiring up PKCS#11, an HSM or a cloud KMS — so its shape is the actual product, and a
    /// silent change to a parameter or return type breaks every one of them at their next upgrade.
    /// This pins the full signature of each member, not just its existence.
    ///
    /// <para>The predecessor asserted <c>NotNull(typeof(IExternalSigner))</c>, which the C#
    /// compiler makes unfalsifiable — <c>typeof</c> never yields null — and then checked four
    /// members by name only. Renaming a parameter type from <c>byte[]</c> to <c>Stream</c>, or
    /// flipping a return type, left it green.</para>
    /// </summary>
    [Fact]
    public void IExternalSigner_PinsTheSignatureEveryImplementorCompilesAgainst()
    {
        Assert.True(typeof(IExternalSigner).IsInterface);

        AssertMethod("SignDigest",          typeof(byte[]), typeof(byte[]), typeof(string));
        AssertMethod("SignData",            typeof(byte[]), typeof(byte[]), typeof(string));
        AssertMethod("GetCertificateChain", typeof(IList<byte[]>));

        var size = typeof(IExternalSigner).GetProperty("EstimatedSignatureSize");
        Assert.NotNull(size);
        Assert.Equal(typeof(int), size!.PropertyType);
        Assert.NotNull(size.GetMethod);

        // The interface is what PdfSigner reaches for, so the hand-off point is part of the
        // contract too: SignatureParameters must expose it, and default to "no external signer".
        var hook = typeof(SignatureParameters).GetProperty(nameof(SignatureParameters.ExternalSigner));
        Assert.NotNull(hook);
        Assert.Equal(typeof(IExternalSigner), hook!.PropertyType);
        Assert.Null(new SignatureParameters().ExternalSigner);
    }

    private static void AssertMethod(string name, Type returnType, params Type[] parameterTypes)
    {
        var method = typeof(IExternalSigner).GetMethod(name, parameterTypes);
        Assert.True(method != null,
            $"IExternalSigner.{name}({string.Join(", ", parameterTypes.Select(t => t.Name))}) is gone " +
            "or changed shape — every external implementor breaks.");
        Assert.Equal(returnType, method!.ReturnType);
    }

    [Fact]
    public void SignatureParameters_HasExternalSigner_Property()
    {
        var p = new SignatureParameters();
        Assert.Null(p.ExternalSigner); // default is null
        p.ExternalSigner = new MockExternalSigner();
        Assert.NotNull(p.ExternalSigner);
    }

    [Fact]
    public void MockExternalSigner_Implements_Interface()
    {
        var signer = new MockExternalSigner();
        Assert.NotNull(signer.GetCertificateChain());
        Assert.True(signer.EstimatedSignatureSize > 0);
    }

    /// <summary>
    /// A mock external signer for testing that the interface works.
    /// In real usage this would delegate to PKCS#11, Azure Key Vault, etc.
    /// </summary>
    private sealed class MockExternalSigner : IExternalSigner
    {
        public byte[] SignDigest(byte[] digest, string digestAlgorithm)
        {
            // Return a fake PKCS#7 for testing
            return new byte[] { 0x30, 0x00 };
        }

        public byte[] SignData(byte[] data, string digestAlgorithm)
        {
            // Hash then sign
            using var sha = System.Security.Cryptography.SHA256.Create();
            byte[] hash = sha.ComputeHash(data);
            return SignDigest(hash, digestAlgorithm);
        }

        public IList<byte[]> GetCertificateChain()
        {
            // Return a self-signed test certificate (minimal DER)
            return new List<byte[]> { TestCertificateGenerator.GenerateSelfSignedDer() };
        }

        public int EstimatedSignatureSize => 8192;
    }

    /// <summary>Generates a minimal self-signed certificate for testing.</summary>
    private static class TestCertificateGenerator
    {
        public static byte[] GenerateSelfSignedDer()
        {
            var gen = new Org.BouncyCastle.X509.X509V3CertificateGenerator();
            var keyPair = Tetitulz.Pdf.TestHelpers.RsaTestKeys.Next();

            var now = DateTime.UtcNow;
            gen.SetSerialNumber(Org.BouncyCastle.Math.BigInteger.ValueOf(1));
            gen.SetIssuerDN(new Org.BouncyCastle.Asn1.X509.X509Name("CN=Test"));
            gen.SetSubjectDN(new Org.BouncyCastle.Asn1.X509.X509Name("CN=Test"));
            gen.SetNotBefore(now.AddDays(-1));
            gen.SetNotAfter(now.AddYears(1));
            gen.SetPublicKey(keyPair.Public);

            var cert = gen.Generate(
                new Org.BouncyCastle.Crypto.Operators.Asn1SignatureFactory(
                    "SHA256WITHRSA", keyPair.Private));
            return cert.GetEncoded();
        }
    }
}
