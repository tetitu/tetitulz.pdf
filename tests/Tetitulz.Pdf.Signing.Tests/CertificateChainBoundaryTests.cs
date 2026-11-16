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
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The edges of <see cref="CertificateChainValidator"/>: the hop allowance of the trust search and
/// of <c>BuildChain</c>, names compared in RDN order, certificate cycles, and the path-length
/// constraint at its exact values — each on the side the code puts it, with its control.
/// </summary>
/// <remarks>
/// Driven through the validator directly, as <see cref="SigningRefusalAndChainTests"/> is: a CMS
/// bag's order is BouncyCastle's to choose, and these fixtures need the store exactly as built.
/// </remarks>
public sealed class CertificateChainBoundaryTests
{
    // ------------------------------------------------------------------ hop allowance

    /// <summary>
    /// The trust search has 16 hops: an anchor 16 issuers above the leaf is reached, one 17 above
    /// is not — the search reports an unanchored chain instead.
    /// </summary>
    [Theory]
    [InlineData(16, PdfSignatureChainStatus.Trusted)]
    [InlineData(17, PdfSignatureChainStatus.UntrustedRoot)]
    public void Resolve_ReachesAnAnchorSixteenIssuersUp_AndNoFurther(int anchorLevel, PdfSignatureChainStatus expected)
    {
        (X509Certificate leaf, List<X509Certificate> issuers) = LongChain(17);
        var store = CollectionUtilities.CreateStore(new List<X509Certificate>(issuers) { leaf });

        var warnings = new List<string>();
        PdfSignatureChainStatus status = CertificateChainValidator.Resolve(
            leaf, store, new[] { issuers[anchorLevel - 1] }, DateTime.UtcNow, warnings);

        Assert.Equal(expected, status);
    }

    /// <summary>
    /// <c>BuildChain</c> follows at most 16 issuers: over a chain of 17 below a self-signed root it
    /// returns the leaf and the first 16, and not the root.
    /// </summary>
    [Fact]
    public void BuildChain_FollowsSixteenIssuers()
    {
        (X509Certificate leaf, List<X509Certificate> issuers) = LongChain(17);
        var store = CollectionUtilities.CreateStore(issuers);

        List<X509Certificate> chain = CertificateChainValidator.BuildChain(leaf, store);

        Assert.Equal(17, chain.Count);
        Assert.Equal(issuers[15], chain[16]);
    }

    // ------------------------------------------------------------------ names in RDN order

    /// <summary>
    /// A leaf whose issuer name holds its own subject's RDNs in another order is <b>not</b>
    /// self-signed: RFC 5280 §7.1 compares names RDN by RDN in order. Its issuer — a CA literally
    /// named that way — anchors it. Judged self-signed, the leaf would fail its own-key check and
    /// read <c>Invalid</c>.
    /// </summary>
    [Fact]
    public void Resolve_LeafWhoseIssuerIsItsSubjectReordered_IsNotSelfSigned()
    {
        var caKeys = SigningTestCrypto.Keys();
        var ca = Cert(new X509Name("O=Org,CN=Leaf"), caKeys.Private, new X509Name("O=Org,CN=Leaf"),
            caKeys.Public, CaKind.Unlimited);
        var leaf = Cert(new X509Name("O=Org,CN=Leaf"), caKeys.Private, new X509Name("CN=Leaf,O=Org"),
            SigningTestCrypto.Keys().Public, CaKind.EndEntity);
        var store = CollectionUtilities.CreateStore(new List<X509Certificate> { leaf, ca });

        var warnings = new List<string>();
        PdfSignatureChainStatus status = CertificateChainValidator.Resolve(
            leaf, store, new[] { ca }, DateTime.UtcNow, warnings);

        Assert.Equal(PdfSignatureChainStatus.Trusted, status);
    }

    /// <summary>
    /// The same rule one hop up: an intermediate whose issuer is its subject reordered is not a
    /// self-signed root, so the search goes on past it to the root that anchors it.
    /// </summary>
    [Fact]
    public void Resolve_IntermediateWhoseIssuerIsItsSubjectReordered_IsNotARoot()
    {
        var rootKeys = SigningTestCrypto.Keys();
        var midKeys = SigningTestCrypto.Keys();
        var root = Cert(new X509Name("O=Org,CN=Mid"), rootKeys.Private, new X509Name("O=Org,CN=Mid"),
            rootKeys.Public, CaKind.Unlimited);
        var mid = Cert(new X509Name("O=Org,CN=Mid"), rootKeys.Private, new X509Name("CN=Mid,O=Org"),
            midKeys.Public, CaKind.Unlimited);
        var leaf = Cert(new X509Name("CN=Mid,O=Org"), midKeys.Private, new X509Name("CN=Signer"),
            SigningTestCrypto.Keys().Public, CaKind.EndEntity);
        var store = CollectionUtilities.CreateStore(new List<X509Certificate> { leaf, mid, root });

        var warnings = new List<string>();
        PdfSignatureChainStatus status = CertificateChainValidator.Resolve(
            leaf, store, new[] { root }, DateTime.UtcNow, warnings);

        Assert.Equal(PdfSignatureChainStatus.Trusted, status);
    }

    // ------------------------------------------------------------------ BuildChain's stops

    /// <summary>
    /// A cycle — A issued by B, B issued by A, each genuinely signed by the other's key — ends the
    /// chain the first time a certificate would repeat: leaf, A, B.
    /// </summary>
    [Fact]
    public void BuildChain_StopsAtACertificateCycle()
    {
        var aKeys = SigningTestCrypto.Keys();
        var bKeys = SigningTestCrypto.Keys();
        var a = Cert(new X509Name("CN=B"), bKeys.Private, new X509Name("CN=A"), aKeys.Public, CaKind.Unlimited);
        var b = Cert(new X509Name("CN=A"), aKeys.Private, new X509Name("CN=B"), bKeys.Public, CaKind.Unlimited);
        var leaf = Cert(new X509Name("CN=A"), aKeys.Private, new X509Name("CN=Signer"),
            SigningTestCrypto.Keys().Public, CaKind.EndEntity);
        var store = CollectionUtilities.CreateStore(new List<X509Certificate> { a, b });

        List<X509Certificate> chain = CertificateChainValidator.BuildChain(leaf, store);

        Assert.Equal(new[] { leaf, a, b }, chain);
    }

    /// <summary>
    /// A self-signed leaf is its own root: the chain is the leaf alone, even when the bag carries a
    /// cross-signed copy of it (same name, same key, issued by someone else) that would verify it.
    /// </summary>
    [Fact]
    public void BuildChain_OfASelfSignedLeaf_IsTheLeafAlone()
    {
        var rootKeys = SigningTestCrypto.Keys();
        var otherKeys = SigningTestCrypto.Keys();
        var root = Cert(new X509Name("CN=R"), rootKeys.Private, new X509Name("CN=R"), rootKeys.Public, CaKind.Unlimited);
        var crossSigned = Cert(new X509Name("CN=O"), otherKeys.Private, new X509Name("CN=R"),
            rootKeys.Public, CaKind.Unlimited);
        var store = CollectionUtilities.CreateStore(new List<X509Certificate> { crossSigned });

        List<X509Certificate> chain = CertificateChainValidator.BuildChain(root, store);

        Assert.Equal(new[] { root }, chain);
    }

    // ------------------------------------------------------------------ path length

    /// <summary>
    /// RFC 5280 §4.2.1.9: <c>pathLenConstraint</c> is the number of non-self-issued intermediate CAs
    /// that may follow. A CA limited to 0 may issue the leaf directly but not an intermediate; one
    /// limited to 1 may have one intermediate below it, not two. A limit of 0 is still a CA.
    /// </summary>
    [Theory]
    [InlineData(0, 0, PdfSignatureChainStatus.Trusted)]
    [InlineData(0, 1, PdfSignatureChainStatus.Invalid)]
    [InlineData(1, 1, PdfSignatureChainStatus.Trusted)]
    [InlineData(1, 2, PdfSignatureChainStatus.Invalid)]
    public void Resolve_PathLengthConstraint_CountsTheIntermediatesBelow(
        int pathLen, int intermediates, PdfSignatureChainStatus expected)
    {
        var rootKeys = SigningTestCrypto.Keys();
        var root = Cert(new X509Name("CN=Limited Root"), rootKeys.Private, new X509Name("CN=Limited Root"),
            rootKeys.Public, pathLen == 0 ? CaKind.PathLenZero : CaKind.PathLenOne);

        var certs = new List<X509Certificate> { root };
        X509Name issuerName = root.SubjectDN;
        AsymmetricKeyParameter issuerKey = rootKeys.Private;
        for (int i = 0; i < intermediates; i++)
        {
            var keys = SigningTestCrypto.Keys();
            var name = new X509Name("CN=Intermediate " + i);
            certs.Add(Cert(issuerName, issuerKey, name, keys.Public, CaKind.Unlimited));
            issuerName = name;
            issuerKey = keys.Private;
        }
        var leaf = Cert(issuerName, issuerKey, new X509Name("CN=Signer"), SigningTestCrypto.Keys().Public,
            CaKind.EndEntity);
        certs.Add(leaf);

        var warnings = new List<string>();
        PdfSignatureChainStatus status = CertificateChainValidator.Resolve(
            leaf, CollectionUtilities.CreateStore(certs), new[] { root }, DateTime.UtcNow, warnings);

        Assert.Equal(expected, status);
        if (expected == PdfSignatureChainStatus.Invalid)
            Assert.Contains(warnings, w => w.Contains("Path-length constraint violated"));
    }

    // ------------------------------------------------------------------ FindIssuers

    /// <summary>
    /// <c>FindIssuers</c> of no certificate is no candidates, not an exception; the control finds
    /// the one issuer of a real leaf.
    /// </summary>
    [Fact]
    public void FindIssuers_OfNoCertificate_IsEmpty()
    {
        var ca = SigningTestCrypto.Ca("CN=Issuer Lookup Root");
        var leaf = SigningTestCrypto.Leaf(ca, "CN=Issuer Lookup Signer");
        var store = CollectionUtilities.CreateStore(new List<X509Certificate> { leaf.Cert, ca.Cert });

        Assert.Empty(CertificateChainValidator.FindIssuers(null!, store));
        Assert.Equal(new[] { ca.Cert }, CertificateChainValidator.FindIssuers(leaf.Cert, store));
    }

    // ------------------------------------------------------------------ fixtures

    private enum CaKind { EndEntity, Unlimited, PathLenZero, PathLenOne }

    private static readonly SecureRandom Random = new();
    private static long _serial = 5000;

    /// <summary>
    /// A leaf issued by I1, I(k) issued by I(k+1), and I(<paramref name="levels"/>) a self-signed
    /// root; <c>issuers[k - 1]</c> is I(k).
    /// </summary>
    private static (X509Certificate Leaf, List<X509Certificate> Issuers) LongChain(int levels)
    {
        var keys = new AsymmetricCipherKeyPair[levels];
        var names = new X509Name[levels];
        for (int k = 0; k < levels; k++)
        {
            keys[k] = SigningTestCrypto.Keys();
            names[k] = new X509Name("CN=Level " + (k + 1));
        }

        var issuers = new List<X509Certificate>(levels);
        for (int k = 0; k < levels; k++)
        {
            int above = k + 1 < levels ? k + 1 : k;   // the top one signs itself
            issuers.Add(Cert(names[above], keys[above].Private, names[k], keys[k].Public, CaKind.Unlimited));
        }

        var leaf = Cert(names[0], keys[0].Private, new X509Name("CN=Long Chain Signer"),
            SigningTestCrypto.Keys().Public, CaKind.EndEntity);
        return (leaf, issuers);
    }

    private static X509Certificate Cert(X509Name issuer, AsymmetricKeyParameter signingKey, X509Name subject,
        AsymmetricKeyParameter subjectKey, CaKind kind)
    {
        var gen = new X509V3CertificateGenerator();
        gen.SetSerialNumber(BigInteger.ValueOf(System.Threading.Interlocked.Increment(ref _serial)));
        gen.SetIssuerDN(issuer);
        gen.SetSubjectDN(subject);
        gen.SetNotBefore(DateTime.UtcNow.AddDays(-1));
        gen.SetNotAfter(DateTime.UtcNow.AddYears(5));
        gen.SetPublicKey(subjectKey);

        if (kind == CaKind.EndEntity)
        {
            gen.AddExtension(X509Extensions.KeyUsage, true, new KeyUsage(KeyUsage.DigitalSignature));
        }
        else
        {
            gen.AddExtension(X509Extensions.BasicConstraints, true, kind switch
            {
                CaKind.PathLenZero => new BasicConstraints(0),
                CaKind.PathLenOne => new BasicConstraints(1),
                _ => new BasicConstraints(true),
            });
            gen.AddExtension(X509Extensions.KeyUsage, true,
                new KeyUsage(KeyUsage.KeyCertSign | KeyUsage.CrlSign | KeyUsage.DigitalSignature));
        }

        return gen.Generate(new Asn1SignatureFactory("SHA256WithRSA", signingKey, Random));
    }
}
