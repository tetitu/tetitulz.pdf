// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The TSA client says, at signing time, when the token it was handed is one this library's
/// verifier refuses under its default policy — <c>TsaClient.LastTokenWeakAlgorithm</c> — and still
/// returns it; a broken digest, which verification refuses under every policy, it refuses itself.
/// </summary>
/// <remarks>
/// With the weak-algorithm policy applied by the verifier alone, a SHA-1 token would be embedded
/// without a word and default verification of the signed document would then reject its timestamp.
/// Each weak case is asserted on both sides in one test: the client's slot and the
/// verifier's refusal of the same bytes, with a SHA-256 control that neither flags.
/// </remarks>
public class TsaClientWeakTokenTests
{
    private const string Sha256 = "2.16.840.1.101.3.4.2.1";
    private const string Sha1 = "1.3.14.3.2.26";
    private const string Md5 = "1.2.840.113549.2.5";

    private static readonly byte[] Data = Encoding.ASCII.GetBytes("the signature value being timestamped");

    [Fact]
    public void ASha256Token_IsReturnedWithNoWarning_AndVerifies()
    {
        var tsa = SigningTestCrypto.TsaCert();
        var (request, reply) = Reply(tsa, Sha256, Sha256);

        byte[] token = TsaClient.ValidateResponse(request, reply);

        Assert.Null(TsaClient.LastTokenWeakAlgorithm);
        Assert.Equal(tsa.Cert.SubjectDN.ToString(), TsaClient.LastTsaCertSubject);
        Assert.True(TsaTokenVerifier.VerifyTimestampToken(token, Data).IsValid);
    }

    [Fact]
    public void ASha1SignedToken_IsReturnedAndSaid_AndDefaultVerificationRefusesIt()
    {
        var tsa = SigningTestCrypto.TsaCert();
        var (request, reply) = Reply(tsa, Sha256, Sha1);

        byte[] token = TsaClient.ValidateResponse(request, reply);

        Assert.Contains("SHA-1", TsaClient.LastTokenWeakAlgorithm, StringComparison.Ordinal);
        var verified = TsaTokenVerifier.VerifyTimestampToken(token, Data);
        Assert.False(verified.IsValid);
        Assert.Equal(TsaClient.LastTokenWeakAlgorithm, verified.WeakAlgorithmReason);
    }

    /// <summary>
    /// The imprint is the requester's field and the signature the TSA's, so a token can be
    /// SHA-256-signed over a SHA-1 imprint; the imprint half of the rule is reached only so.
    /// </summary>
    [Fact]
    public void ASha1Imprint_UnderASha256Signature_IsSaid_AndDefaultVerificationRefusesIt()
    {
        var tsa = SigningTestCrypto.TsaCert();
        var (request, reply) = Reply(tsa, Sha1, Sha256);

        byte[] token = TsaClient.ValidateResponse(request, reply);

        Assert.Contains("message imprint", TsaClient.LastTokenWeakAlgorithm, StringComparison.Ordinal);
        Assert.False(TsaTokenVerifier.VerifyTimestampToken(token, Data).IsValid);
    }

    [Fact]
    public void ATsaKeyBelowTheDefaultFloor_IsSaid_AndDefaultVerificationRefusesIt()
    {
        var tsa = SigningTestCrypto.TsaCert(keyBits: 1024);
        var (request, reply) = Reply(tsa, Sha256, Sha256);

        byte[] token = TsaClient.ValidateResponse(request, reply);

        Assert.Contains("TSA RSA key is 1024 bits", TsaClient.LastTokenWeakAlgorithm, StringComparison.Ordinal);
        Assert.False(TsaTokenVerifier.VerifyTimestampToken(token, Data).IsValid);
    }

    /// <summary>
    /// MD5 is refused by verification whatever its policy, so writing such a token would make a
    /// document whose timestamp can never verify: the client refuses it, as it refuses a forged one.
    /// </summary>
    [Fact]
    public void AnMd5Imprint_IsRefusedAtSigning()
    {
        var tsa = SigningTestCrypto.TsaCert();
        var (request, reply) = Reply(tsa, Md5, Sha256);

        var ex = Assert.Throws<PdfSigningException>(() => TsaClient.ValidateResponse(request, reply));
        Assert.Contains("broken", ex.Message, StringComparison.Ordinal);
        Assert.Equal(ex.Message, TsaClient.LastTokenSignatureIssue);
        Assert.Null(TsaClient.LastTokenWeakAlgorithm);
    }

    /// <summary>
    /// Reset at the start of every call, on every path: a call that fails before any reply arrives
    /// (here, no hash) must not leave the previous token's warning standing.
    /// </summary>
    [Fact]
    public void AFailedCall_ClearsThePreviousTokensWarning()
    {
        var tsa = SigningTestCrypto.TsaCert();
        var (request, reply) = Reply(tsa, Sha256, Sha1);
        TsaClient.ValidateResponse(request, reply);
        Assert.NotNull(TsaClient.LastTokenWeakAlgorithm);

        Assert.Throws<ArgumentNullException>(() => TsaClient.GetTimestampToken(null, Sha256, "http://tsa.invalid/"));

        Assert.Null(TsaClient.LastTokenWeakAlgorithm);
        Assert.Null(TsaClient.LastTsaCertSubject);
    }

    // ------------------------------------------------------------------ fixtures

    /// <summary>A request over <see cref="Data"/> and the TSA's granted reply to it.</summary>
    private static (TimeStampRequest request, byte[] reply) Reply(
        SigningTestCrypto.Issued tsa, string imprintOid, string tokenDigestOid)
    {
        var reqGen = new TimeStampRequestGenerator();
        reqGen.SetCertReq(true);
        byte[] imprint = DigestUtilities.CalculateDigest(new DerObjectIdentifier(imprintOid), Data);
        var request = reqGen.Generate(imprintOid, imprint, BigInteger.ValueOf(42));

        var gen = new TimeStampTokenGenerator(tsa.Private, tsa.Cert, tokenDigestOid, "1.2.3.4.5");
        gen.SetCertificates(CollectionUtilities.CreateStore(new List<X509Certificate> { tsa.Cert }));
        byte[] reply = new TimeStampResponseGenerator(gen, TspAlgorithms.Allowed)
            .Generate(request, BigInteger.ValueOf(7), DateTime.UtcNow)
            .GetEncoded();
        return (request, reply);
    }
}
