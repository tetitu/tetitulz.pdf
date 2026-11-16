// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="SignatureParametersExtensions.UseAlgorithm"/> — the table that turns a
/// <see cref="SignatureAlgorithm"/> into the digest name and signature-algorithm OID the CMS
/// generator is driven with.
///
/// <para><b>Why a lookup table deserves tests.</b> Nothing here computes anything, so it looks like
/// the last code that could be wrong — which is exactly why a mismatched pair survives review. A
/// digest that disagrees with its OID (SHA-256 named alongside the SHA-384 OID, say) produces a
/// CMS structure that generates without complaint and that every verifier rejects, because the
/// digest a verifier recomputes is chosen from the OID.</para>
///
/// <para>Nothing in this assembly's own tests exercised it before: <c>Tetitulz.Pdf.Signing</c> had
/// no test project at all.</para>
/// </summary>
public class SignatureAlgorithmMappingTests
{
    /// <summary>Every algorithm and the pair it must produce, written out rather than derived.</summary>
    public static TheoryData<SignatureAlgorithm, string, string> Expected => new()
    {
        { SignatureAlgorithm.RsaSha256,          "SHA-256",           SignatureAlgorithmOids.RsaWithSha256 },
        { SignatureAlgorithm.RsaSha384,          "SHA-384",           SignatureAlgorithmOids.RsaWithSha384 },
        { SignatureAlgorithm.RsaSha512,          "SHA-512",           SignatureAlgorithmOids.RsaWithSha512 },
        { SignatureAlgorithm.RsaPssSha256,       "SHA-256",           SignatureAlgorithmOids.RsaPss },
        { SignatureAlgorithm.EcdsaSha256,        "SHA-256",           SignatureAlgorithmOids.EcdsaWithSha256 },
        { SignatureAlgorithm.EcdsaSha384,        "SHA-384",           SignatureAlgorithmOids.EcdsaWithSha384 },
        { SignatureAlgorithm.EcdsaSha512,        "SHA-512",           SignatureAlgorithmOids.EcdsaWithSha512 },
        { SignatureAlgorithm.Ed25519,            "SHA-512",           SignatureAlgorithmOids.Ed25519 },
        { SignatureAlgorithm.Ed448,              "SHA-512",           SignatureAlgorithmOids.Ed448 },
        { SignatureAlgorithm.MlDsa44,            "SHA-256",           SignatureAlgorithmOids.MlDsa44 },
        { SignatureAlgorithm.MlDsa65,            "SHA-384",           SignatureAlgorithmOids.MlDsa65 },
        { SignatureAlgorithm.MlDsa87,            "SHA-512",           SignatureAlgorithmOids.MlDsa87 },
        { SignatureAlgorithm.GostR3410_2012_256, "GOST3411-2012-256", SignatureAlgorithmOids.GostR3410_2012_256 },
        { SignatureAlgorithm.GostR3410_2012_512, "GOST3411-2012-512", SignatureAlgorithmOids.GostR3410_2012_512 },
    };

    [Theory]
    [MemberData(nameof(Expected))]
    public void UseAlgorithm_SetsTheCanonicalDigestAndOid(
        SignatureAlgorithm algorithm, string digest, string oid)
    {
        var parameters = new SignatureParameters().UseAlgorithm(algorithm);

        Assert.Equal(digest, parameters.DigestAlgorithm);
        Assert.Equal(oid, parameters.SignatureAlgorithmOid);
    }

    /// <summary>
    /// <see cref="SignatureAlgorithm.Default"/> clears the explicit OID so BouncyCastle chooses
    /// from the key — and must leave the digest alone rather than resetting it, or a caller that
    /// set a digest first would silently lose it.
    /// </summary>
    [Fact]
    public void UseAlgorithm_Default_ClearsTheOidAndKeepsTheDigest()
    {
        var parameters = new SignatureParameters { DigestAlgorithm = "SHA-384" }
            .UseAlgorithm(SignatureAlgorithm.RsaSha512)
            .UseAlgorithm(SignatureAlgorithm.Default);

        Assert.Null(parameters.SignatureAlgorithmOid);
        Assert.Equal("SHA-512", parameters.DigestAlgorithm);
    }

    /// <summary>
    /// The mapping is exhaustive: every declared enum member is handled. The <c>default:</c> arm
    /// throws, so adding a member without a case turns into a runtime failure for whoever selects
    /// it — this catches that at build time instead.
    /// </summary>
    [Fact]
    public void UseAlgorithm_HandlesEveryDeclaredAlgorithm()
    {
        var unhandled = new List<SignatureAlgorithm>();

        foreach (SignatureAlgorithm algorithm in Enum.GetValues<SignatureAlgorithm>())
        {
            try { new SignatureParameters().UseAlgorithm(algorithm); }
            catch (ArgumentOutOfRangeException) { unhandled.Add(algorithm); }
        }

        Assert.Empty(unhandled);
    }

    /// <summary>
    /// The theory table above must itself stay exhaustive, or a new algorithm could be added,
    /// handled, and never checked for the <i>right</i> pairing.
    /// </summary>
    [Fact]
    public void TheExpectedTable_CoversEveryAlgorithmExceptDefault()
    {
        var covered = Expected
            .Select(row => (SignatureAlgorithm)row[0])
            .ToHashSet();

        var missing = Enum.GetValues<SignatureAlgorithm>()
            .Where(a => a != SignatureAlgorithm.Default && !covered.Contains(a))
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// Distinct algorithms must map to distinct OIDs: a duplicate would mean two enum members
    /// select the same signature scheme, so one of them silently does not do what it says.
    /// </summary>
    [Fact]
    public void EveryAlgorithm_MapsToADistinctOid()
    {
        var byOid = new Dictionary<string, SignatureAlgorithm>(StringComparer.Ordinal);

        foreach (SignatureAlgorithm algorithm in Enum.GetValues<SignatureAlgorithm>())
        {
            if (algorithm == SignatureAlgorithm.Default) continue;

            string? oid = new SignatureParameters().UseAlgorithm(algorithm).SignatureAlgorithmOid;
            Assert.False(string.IsNullOrEmpty(oid), $"{algorithm} produced no OID");

            Assert.False(byOid.TryGetValue(oid!, out SignatureAlgorithm other),
                $"{algorithm} and {other} both map to OID {oid}");
            byOid[oid!] = algorithm;
        }
    }

    /// <summary>Every OID must be a well-formed dotted decimal, or the generator cannot parse it.</summary>
    [Fact]
    public void EveryOid_IsAWellFormedDottedDecimal()
    {
        foreach (SignatureAlgorithm algorithm in Enum.GetValues<SignatureAlgorithm>())
        {
            if (algorithm == SignatureAlgorithm.Default) continue;

            string oid = new SignatureParameters().UseAlgorithm(algorithm).SignatureAlgorithmOid!;
            string[] arcs = oid.Split('.');

            Assert.True(arcs.Length >= 3, $"{algorithm}: '{oid}' has too few arcs to be an OID");
            Assert.All(arcs, arc => Assert.True(
                arc.Length > 0 && arc.All(char.IsDigit), $"{algorithm}: '{oid}' has a non-numeric arc"));
        }
    }

    [Fact]
    public void UseAlgorithm_OnNullParameters_Throws()
    {
        SignatureParameters? parameters = null;
        Assert.Throws<ArgumentNullException>(() => parameters!.UseAlgorithm(SignatureAlgorithm.RsaSha256));
    }
}
