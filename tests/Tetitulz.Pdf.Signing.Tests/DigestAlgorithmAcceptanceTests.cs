// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Tests.TestInfrastructure;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The digest algorithm a caller NAMES must be the digest algorithm the CMS carries — or the call
/// must fail. It may never be quietly replaced by a different one.
/// </summary>
/// <remarks>
/// <para><b>The defect.</b> <c>CmsSignatureGenerator.GetDigestOid</c>'s <c>default:</c> arm
/// returned the SHA-256 OID for any name it did not recognise, and <c>PdfSigner.Sign</c> passes
/// <c>SignatureParameters.DigestAlgorithm</c> into it with no acceptance check of its own — the
/// <c>TsaClient.GetHashOid</c> call on that path gates only the separate
/// <c>TimestampDigestAlgorithm</c>. So <c>DigestAlgorithm = "SHA-224"</c>, or "MD5", or a typo,
/// produced a <b>SHA-256</b> signature and reported success. The same string handed to
/// <c>PdfCounterSigner.AddCounterSignature</c> was refused, and <c>TsaClient.GetHashOid</c>'s own
/// comment called the identical behaviour a defect — four copies of one rule, disagreeing.</para>
///
/// <para><b>Why it survived.</b> The substitution leaves no trace in the artefact. The CMS is
/// well-formed, it verifies, and its <c>digestAlgorithm</c> field reads SHA-256 — byte-identical to
/// a signature the caller really did ask for in SHA-256. Nothing short of naming an unsupported
/// algorithm and reading the OID back out can tell the two apart, which is what the pair of tests
/// below does: a positive control that proves the name reaches the CMS at all, and the refusal.</para>
/// </remarks>
public sealed class DigestAlgorithmAcceptanceTests
{
    private static readonly byte[] Content = System.Text.Encoding.ASCII.GetBytes("byte range content");

    /// <summary>
    /// The positive control. Without it the refusal test below would pass with the whole digest
    /// selection deleted — every name would throw, and nothing would show that a supported name
    /// actually reaches the generated CMS.
    /// </summary>
    [Theory]
    [InlineData("SHA-1", "1.3.14.3.2.26")]
    [InlineData("SHA-256", "2.16.840.1.101.3.4.2.1")]
    [InlineData("SHA-384", "2.16.840.1.101.3.4.2.2")]
    [InlineData("SHA-512", "2.16.840.1.101.3.4.2.3")]
    [InlineData("SHA3-512", "2.16.840.1.101.3.4.2.10")]
    [InlineData("SHA3_256", "2.16.840.1.101.3.4.2.8")]
    public void ANamedDigest_IsTheDigestTheCmsCarries(string name, string expectedOid)
    {
        Assert.Equal(expectedOid, DigestOidOfGeneratedCms(name));
    }

    /// <summary>
    /// SHAKE256 (ISO/TS 32001, id-shake256) resolves to the right OID — the same acceptance
    /// machinery every digest above goes through — but is deliberately <b>not</b> in the theory
    /// above, because it cannot actually reach a signed CMS from this entry point. See
    /// <see cref="ANRsaKeyCannotSignWithShake256"/> for why, and do not "fix" this by loosening the
    /// assertion there.
    /// </summary>
    [Fact]
    public void Shake256_ResolvesToTheCorrectOid_EvenThoughItCannotSignWithAnRsaKeyYet()
    {
        Assert.Equal("2.16.840.1.101.3.4.2.12",
            DigestAlgorithms.ResolveOidForNewSignature("SHAKE256", "digestAlgorithm"));
    }

    /// <summary>
    /// A known, upstream limitation, pinned rather than left silently absent.
    /// </summary>
    /// <remarks>
    /// <para><b>There is no standard "RSA with SHAKE256" signature algorithm</b> — unlike SHA-2/
    /// SHA-3, PKCS#1 v1.5 and RSA-PSS have no registered OID pairing RSA with a SHAKE digest, so
    /// <c>CmsSignedDataGenerator.AddSigner</c>'s single-digest-OID overload (the one
    /// <see cref="CmsSignatureGenerator.GenerateDetachedSignature"/> uses) composes the literal
    /// string <c>"SHAKE256withRSA"</c> and <see cref="Asn1SignatureFactory"/> rejects it as neither
    /// a known name nor a valid OID.</para>
    /// <para><b>The standards-correct pairing is Ed448</b> (ISO/TS 32002 Table 4: SHAKE256 under the
    /// fixed-length id-shake256), which signs through a SignerInfo built to that rule
    /// (<c>CmsSignatureGenerator.Ed448SignedData</c>, pinned by
    /// <c>EcCurveSignatureRoundTripTests.Sign_ThenVerify_Ed448_IsIntegrityValid</c>). SHAKE256 is
    /// reachable through Ed448 and through no RSA key, which has no SHAKE256 pairing to name.</para>
    /// </remarks>
    [Fact]
    public void ANRsaKeyCannotSignWithShake256()
    {
        var ex = Record.Exception(() => DigestOidOfGeneratedCms("SHAKE256"));

        Assert.IsType<FormatException>(ex);
        Assert.Contains("SHAKE256withRSA", ex!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// GOST R 34.11-2012 (Streebog) stays accepted here and only here: it is a live signing path —
    /// <c>UseAlgorithm(SignatureAlgorithm.GostR3410_2012_256)</c> sets exactly this digest name —
    /// and the other three resolvers have never taken it.
    /// </summary>
    /// <remarks>
    /// It cannot be driven all the way to a CMS from this suite, which has only RSA key
    /// generation and BouncyCastle has no GOST-digest-with-RSA signature scheme. The observable
    /// is what BouncyCastle composes from the digest OID this generator handed it: refusing the
    /// name would raise <see cref="ArgumentException"/> from the resolver instead, and the silent
    /// SHA-256 substitution would have produced a finished signature.
    /// </remarks>
    [Theory]
    [InlineData("GOST3411-2012-256", "GOST3411-2012-256withRSA")]
    [InlineData("STREEBOG-512", "GOST3411-2012-512withRSA")]
    public void AGostDigestName_ReachesTheGeneratorAsItsStreebogOid(string name, string composed)
    {
        var ex = Record.Exception(() => DigestOidOfGeneratedCms(name));

        Assert.IsType<FormatException>(ex);
        Assert.Contains(composed, ex!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A name outside the accepted set is refused rather than silently becoming SHA-256. Before the
    /// fix every one of these returned <c>"2.16.840.1.101.3.4.2.1"</c> from the assertion helper.
    /// </summary>
    [Theory]
    [InlineData("SHA-224")]  // real algorithm, never supported here
    [InlineData("MD5")]      // PdfDigestOids.FromName resolves it; a broken digest must not be selectable
    [InlineData("MD2")]
    [InlineData("SHA-2566")] // a plain typo
    [InlineData("BLAKE2")]
    public void AnUnsupportedDigestName_IsRefusedRatherThanSubstituted(string name)
    {
        var ex = Record.Exception(() => DigestOidOfGeneratedCms(name));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains(name, ex!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Declining to name an algorithm is not the same as naming one that does not exist: the public
    /// generator entry points keep their documented "unnamed means SHA-256" default.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void AnUnnamedDigest_StillMeansSha256(string? name)
    {
        Assert.Equal("2.16.840.1.101.3.4.2.1", DigestOidOfGeneratedCms(name));
    }

    /// <summary>
    /// The defect's reachability from the public signing API — the input the audit named. The
    /// signer must refuse, not hand back a document signed with an algorithm nobody asked for.
    /// </summary>
    [Fact]
    public void PdfSigner_WithAnUnsupportedDigestName_RefusesInsteadOfSigningWithSha256()
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);
        var (pfx, password) = SigningTestCrypto.Pfx(leaf, ca.Cert);

        using var input = new MemoryStream(SignedPdfFixtures.MinimalPdf());
        using var output = new MemoryStream();

        var ex = Record.Exception(() => new PdfSigner().Sign(input, output, new SignatureParameters
        {
            CertificateStream = new MemoryStream(pfx),
            CertificatePassword = password,
            DigestAlgorithm = "SHA-224",
            Reason = "test",
        }));

        Assert.IsType<PdfSigningException>(ex);
        Assert.Contains("SHA-224", ex!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The counter-signing path already refused these names, and the TSA path already refused them
    /// too. Pinning that here is what keeps the consolidated resolver from drifting back apart:
    /// the whole finding was that these three agreed and the fourth did not.
    /// </summary>
    [Theory]
    [InlineData("SHA-224")]
    [InlineData("MD5")]
    [InlineData("GOST3411-2012-256")] // accepted for a CMS signature, never for a TSA request
    public void TsaClient_RefusesEveryNameOutsideTheShaFamilies(string name)
    {
        var ex = Record.Exception(() => TsaClient.GetHashOid(name));

        Assert.IsType<ArgumentException>(ex);
        Assert.Contains(name, ex!.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Generates a detached CMS over <see cref="Content"/> with <paramref name="digestName"/> and
    /// reads the <c>digestAlgorithm</c> OID back out of the parsed SignerInfo — the field a
    /// verifier picks its digest from, so this is what the caller's choice actually became.
    /// </summary>
    private static string DigestOidOfGeneratedCms(string digestName)
    {
        var ca = SigningTestCrypto.Ca();
        var leaf = SigningTestCrypto.Leaf(ca);

        byte[] signature = CmsSignatureGenerator.GenerateDetachedSignature(
            Content,
            leaf.Private,
            leaf.Cert,
            new List<X509Certificate> { leaf.Cert, ca.Cert },
            digestName);

        var parsed = new CmsSignedData(new CmsProcessableByteArray(Content), signature);
        foreach (SignerInformation signer in parsed.GetSignerInfos().GetSigners())
            return signer.DigestAlgorithmID.Algorithm.Id;

        throw new InvalidOperationException("the generated CMS carried no SignerInfo");
    }

    /// <summary>
    /// The one digest set (<c>DigestAlgorithms.IsStandard</c>) and the factory that builds them
    /// (<c>CreateFromOid</c>) answer the same question for every OID <c>PdfDigestOids</c> names: the
    /// generator's acceptance and the verifier's ESS check read the first, the time-stamp verifier the
    /// second, and the verifier once lacked a family the generator emitted. MD5 and MD2 are named there
    /// and are in neither; the control is that the set is not empty.
    /// </summary>
    [Fact]
    public void TheStandardSetAndTheFactory_AgreeOnEveryNamedDigest()
    {
        var oids = new List<string>();
        foreach (var field in typeof(Tetitulz.Pdf.Core.Constants.PdfDigestOids).GetFields(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            if (field.IsLiteral && field.FieldType == typeof(string)) oids.Add((string)field.GetRawConstantValue()!);

        int standard = 0;
        foreach (string oid in oids)
        {
            bool built;
            try { using (DigestAlgorithms.CreateFromOid(oid)) built = true; }
            catch (System.Security.Cryptography.CryptographicException) { built = false; }
            Assert.True(built == DigestAlgorithms.IsStandard(oid), oid + ": the set says " + DigestAlgorithms.IsStandard(oid) + ", the factory " + built);
            if (built) standard++;
        }
        Assert.Equal(8, standard);
        Assert.False(DigestAlgorithms.IsStandard(Tetitulz.Pdf.Core.Constants.PdfDigestOids.Md5));
    }
}
