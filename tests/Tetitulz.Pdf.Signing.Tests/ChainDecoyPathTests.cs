// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// A decoy path through the CMS certificate bag — which no signature covers — cannot deny trust to a genuine chain.
/// The walk marked a certificate visited for the whole search before checking its path length, which depends on
/// the path that reached it: a decoy reaching the genuine issuer by a longer route failed that issuer's path length
/// and left it "already visited", so the genuine one-hop path was never tried.
/// </summary>
public sealed class ChainDecoyPathTests
{
    private static readonly SecureRandom Random = new();
    private static long _serial = 7_000_000;

    [Fact]
    public void ADecoyPathReachingTheIssuerByALongerRoute_DoesNotDenyTheGenuinePath()
    {
        AsymmetricCipherKeyPair rootKeys = SigningTestCrypto.Keys(), issuerKeys = SigningTestCrypto.Keys();
        AsymmetricCipherKeyPair leafKeys = SigningTestCrypto.Keys(), loopKeys = SigningTestCrypto.Keys();
        var rootDn = new X509Name("CN=Decoy Test Root");
        var issuerDn = new X509Name("CN=Decoy Test Issuing CA");
        var loopDn = new X509Name("CN=Decoy Loop");

        X509Certificate root = Cert(rootDn, rootDn, rootKeys.Public, rootKeys.Private, ca: true, pathLength: null);
        X509Certificate issuer = Cert(rootDn, issuerDn, issuerKeys.Public, rootKeys.Private, ca: true, pathLength: 0);
        X509Certificate leaf = Cert(issuerDn, new X509Name("CN=Decoy Test Signer"), leafKeys.Public, issuerKeys.Private, ca: false, pathLength: null);

        // The decoy carries the issuer's name and copies its public key, so the leaf verifies under it; it names a
        // loop certificate as its issuer, which names the issuer — two intermediates below the genuine issuer.
        X509Certificate decoy = Cert(loopDn, issuerDn, issuerKeys.Public, loopKeys.Private, ca: true, pathLength: null);
        X509Certificate loop = Cert(issuerDn, loopDn, loopKeys.Public, loopKeys.Private, ca: true, pathLength: null);

        var anchors = new[] { root };
        Assert.Equal(PdfSignatureChainStatus.Trusted, Resolve(leaf, anchors, issuer));                 // control: no decoy
        Assert.Equal(PdfSignatureChainStatus.Trusted, Resolve(leaf, anchors, decoy, loop, issuer));    // decoy first in the bag
    }

    /// <summary>
    /// The relying party's anchors are tried before the bag: a bag that put same-named decoys first spent the shared
    /// verification allowance before an issuer that is itself an anchor was reached.
    /// </summary>
    [Fact]
    public void AnAnchorIssuer_IsTriedBeforeSameNamedDecoysInTheBag()
    {
        AsymmetricCipherKeyPair rootKeys = SigningTestCrypto.Keys(), leafKeys = SigningTestCrypto.Keys();
        var rootDn = new X509Name("CN=Anchor First Root");
        X509Certificate root = Cert(rootDn, rootDn, rootKeys.Public, rootKeys.Private, ca: true, pathLength: null);
        X509Certificate leaf = Cert(rootDn, new X509Name("CN=Anchor First Signer"), leafKeys.Public, rootKeys.Private, ca: false, pathLength: null);

        var decoys = new List<X509Certificate>();
        AsymmetricCipherKeyPair decoyKeys = SigningTestCrypto.Keys();
        for (int i = 0; i < CertificateChainValidator.MaxIssuerVerifications + 4; i++)
            decoys.Add(Cert(rootDn, rootDn, decoyKeys.Public, decoyKeys.Private, ca: true, pathLength: null));

        var bag = new List<X509Certificate>(decoys) { root };
        Assert.Equal(PdfSignatureChainStatus.Trusted, Resolve(leaf, new[] { root }, bag.ToArray()));
    }

    private static PdfSignatureChainStatus Resolve(X509Certificate leaf, X509Certificate[] anchors, params X509Certificate[] bag)
    {
        var store = CollectionUtilities.CreateStore(new List<X509Certificate>(bag));
        return CertificateChainValidator.Resolve(leaf, store, anchors, DateTime.UtcNow, new List<string>());
    }

    private static X509Certificate Cert(X509Name issuer, X509Name subject, AsymmetricKeyParameter publicKey,
        AsymmetricKeyParameter signingKey, bool ca, int? pathLength)
    {
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _serial)));
        gen.SetIssuerDN(issuer);
        gen.SetSubjectDN(subject);
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(5));
        gen.SetPublicKey(publicKey);
        if (ca)
        {
            gen.AddExtension(X509Extensions.BasicConstraints, true,
                pathLength is int n ? new BasicConstraints(n) : new BasicConstraints(true));
            gen.AddExtension(X509Extensions.KeyUsage, true,
                new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
        }
        else
            gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", signingKey, Random));
    }
}
