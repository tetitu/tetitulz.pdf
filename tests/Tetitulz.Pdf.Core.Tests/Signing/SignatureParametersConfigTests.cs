// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Locks in the contract of the convenience configuration helpers added in
/// ֲ§41 (PAdES baseline) / ֲ§41 (SignatureAlgorithm) / ֲ§41 (UsageRights) and
/// ֲ§41.S6 (DocMDP /UR3).  Each test builds a parameters bundle and asserts
/// the helper sets the documented fields without breaking unrelated state.
/// </summary>
public class SignatureParametersConfigTests
{
    // ==================================================================
    //  PAdES baseline builder (ֲ§41.S5)
    // ==================================================================

    [Fact]
    public void ApplyPadesBaseline_BB_ClearsTimestampAndLtv()
    {
        var p = new SignatureParameters
        {
            TimestampAuthorityUrl = "http://prev",
            EmbedLtv = true,
        };
        p.ApplyPadesBaseline(PadesBaseline.B_B);

        Assert.True(p.UseEtsiPadesSubFilter);
        Assert.Null(p.TimestampAuthorityUrl);
        Assert.False(p.EmbedLtv);
        Assert.Equal(PadesBaseline.B_B, p.PadesBaselineLevel);
    }

    [Fact]
    public void ApplyPadesBaseline_BT_RequiresTsaUrl()
    {
        var p = new SignatureParameters();
        Assert.Throws<ArgumentException>(() => p.ApplyPadesBaseline(PadesBaseline.B_T, null));
    }

    [Fact]
    public void ApplyPadesBaseline_BLT_BumpsEstimatedSize()
    {
        var p = new SignatureParameters { EstimatedSignatureSize = 4096 };
        p.ApplyPadesBaseline(PadesBaseline.B_LT, "https://tsa.example/");

        Assert.True(p.UseEtsiPadesSubFilter);
        Assert.Equal("https://tsa.example/", p.TimestampAuthorityUrl);
        Assert.True(p.EmbedLtv);
        Assert.True(p.EstimatedSignatureSize >= 32768,
            "B-LT must bump EstimatedSignatureSize to at least 32 KiB");
        Assert.Equal(PadesBaseline.B_LT, p.PadesBaselineLevel);
    }

    [Fact]
    public void ApplyPadesBaseline_BLT_PreservesLargerEstimatedSize()
    {
        var p = new SignatureParameters { EstimatedSignatureSize = 65536 };
        p.ApplyPadesBaseline(PadesBaseline.B_LT, "https://tsa.example/");

        Assert.Equal(65536, p.EstimatedSignatureSize);
    }

    // ==================================================================
    //  SignatureAlgorithm enum + extension (ֲ§41.S4 / ֲ§41.S9)
    // ==================================================================

    [Fact]
    public void UseAlgorithm_RsaSha256_SetsDigestAndOid()
    {
        var p = new SignatureParameters();
        p.UseAlgorithm(SignatureAlgorithm.RsaSha256);

        Assert.Equal("SHA-256", p.DigestAlgorithm);
        Assert.Equal(SignatureAlgorithmOids.RsaWithSha256, p.SignatureAlgorithmOid);
    }

    [Fact]
    public void UseAlgorithm_Ed25519_UsesSha512InternalDigest()
    {
        var p = new SignatureParameters();
        p.UseAlgorithm(SignatureAlgorithm.Ed25519);

        Assert.Equal("SHA-512", p.DigestAlgorithm);
        Assert.Equal(SignatureAlgorithmOids.Ed25519, p.SignatureAlgorithmOid);
    }

    [Fact]
    public void UseAlgorithm_Default_ClearsExplicitOid()
    {
        var p = new SignatureParameters
        {
            DigestAlgorithm = "SHA-256",
            SignatureAlgorithmOid = SignatureAlgorithmOids.EcdsaWithSha384,
        };
        p.UseAlgorithm(SignatureAlgorithm.Default);

        Assert.Null(p.SignatureAlgorithmOid);
        // DigestAlgorithm is left alone (caller-chosen).
        Assert.Equal("SHA-256", p.DigestAlgorithm);
    }

    [Fact]
    public void UseAlgorithm_NullParams_Throws()
    {
        SignatureParameters p = null;
        Assert.Throws<ArgumentNullException>(() =>
            p.UseAlgorithm(SignatureAlgorithm.RsaSha256));
    }

    // ==================================================================
    //  ML-DSA post-quantum algorithms (S4 full)
    // ==================================================================

    [Theory]
    [InlineData(SignatureAlgorithm.MlDsa44, "SHA-256",      SignatureAlgorithmOids.MlDsa44)]
    [InlineData(SignatureAlgorithm.MlDsa65, "SHA-384",      SignatureAlgorithmOids.MlDsa65)]
    [InlineData(SignatureAlgorithm.MlDsa87, "SHA-512",      SignatureAlgorithmOids.MlDsa87)]
    [InlineData(SignatureAlgorithm.GostR3410_2012_256, "GOST3411-2012-256", SignatureAlgorithmOids.GostR3410_2012_256)]
    [InlineData(SignatureAlgorithm.GostR3410_2012_512, "GOST3411-2012-512", SignatureAlgorithmOids.GostR3410_2012_512)]
    public void UseAlgorithm_PostQuantumAndGost_SetsCorrectDigestAndOid(
        SignatureAlgorithm alg, string expectedDigest, string expectedOid)
    {
        var p = new SignatureParameters();
        p.UseAlgorithm(alg);
        Assert.Equal(expectedDigest, p.DigestAlgorithm);
        Assert.Equal(expectedOid, p.SignatureAlgorithmOid);
    }

    [Fact]
    public void SignatureAlgorithmOids_MlDsa44_MatchesNistFips204()
        => Assert.Equal("2.16.840.1.101.3.4.3.17", SignatureAlgorithmOids.MlDsa44);

    [Fact]
    public void SignatureAlgorithmOids_MlDsa65_MatchesNistFips204()
        => Assert.Equal("2.16.840.1.101.3.4.3.18", SignatureAlgorithmOids.MlDsa65);

    [Fact]
    public void SignatureAlgorithmOids_MlDsa87_MatchesNistFips204()
        => Assert.Equal("2.16.840.1.101.3.4.3.19", SignatureAlgorithmOids.MlDsa87);

    [Fact]
    public void SignatureAlgorithmOids_GostR3410_2012_256_MatchesRfc7091()
        => Assert.Equal("1.2.643.7.1.1.1.1", SignatureAlgorithmOids.GostR3410_2012_256);

    [Fact]
    public void SignatureAlgorithmOids_GostR3410_2012_512_MatchesRfc7091()
        => Assert.Equal("1.2.643.7.1.1.1.2", SignatureAlgorithmOids.GostR3410_2012_512);

    // ==================================================================
    //  UsageRights flags (ֲ§41.S6)
    // ==================================================================

    [Fact]
    public void UsageRights_FlagsCombineWithBitwiseOr()
    {
        var ur = UsageRights.FormFillIn | UsageRights.Annotation | UsageRights.DocumentSave;
        Assert.True((ur & UsageRights.FormFillIn) == UsageRights.FormFillIn);
        Assert.True((ur & UsageRights.Annotation) == UsageRights.Annotation);
        Assert.True((ur & UsageRights.DocumentSave) == UsageRights.DocumentSave);
        Assert.False((ur & UsageRights.FormSave) == UsageRights.FormSave);
    }

    [Fact]
    public void UsageRights_DefaultIsNone()
    {
        var p = new SignatureParameters();
        Assert.Equal(UsageRights.None, p.UsageRights);
    }
}
