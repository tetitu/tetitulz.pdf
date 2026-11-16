// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Security.Cryptography;
using Tetitulz.Pdf.Core.Constants;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <see cref="DigestAlgorithms.CreateFromOid"/> for <see cref="PdfDigestOids.Shake256"/>
/// (ISO/TS 32001, id-shake256, RFC 8419 §2.3 — a fixed 512-bit output rather than a variable-length
/// XOF) against the published FIPS 202 SHAKE256 test vectors. This is the primitive
/// <see cref="DigestAlgorithmAcceptanceTests"/> assumes is correct; that class covers OID
/// acceptance/rejection, this covers the actual bytes.
/// </summary>
public class Shake256DigestTests
{
    [Fact]
    public void CreateFromOid_Shake256_MatchesFips202EmptyMessageVector()
    {
        using HashAlgorithm hash = DigestAlgorithms.CreateFromOid(PdfDigestOids.Shake256);

        byte[] digest = hash.ComputeHash(Array.Empty<byte>());

        Assert.Equal(
            "46b9dd2b0ba88d13233b3feb743eeb243fcd52ea62b81b82b50c27646ed5762" +
            "fd75dc4ddd8c0f200cb05019d67b592f6fc821c49479ab48640292eacb3b7c4be",
            Convert.ToHexString(digest).ToLowerInvariant());
    }

    [Fact]
    public void CreateFromOid_Shake256_MatchesFips202AbcVector()
    {
        using HashAlgorithm hash = DigestAlgorithms.CreateFromOid(PdfDigestOids.Shake256);

        byte[] digest = hash.ComputeHash(System.Text.Encoding.ASCII.GetBytes("abc"));

        Assert.Equal(
            "483366601360a8771c6863080cc4114d8db44530f8f1e1ee4f94ea37e78b57" +
            "39d5a15bef186a5386c75744c0527e1faa9f8726e462a12a4feb06bd8801e751e4",
            Convert.ToHexString(digest).ToLowerInvariant());
    }

    /// <summary>The fixed length the TS mandates — 64 bytes / 512 bits, never variable.</summary>
    [Fact]
    public void CreateFromOid_Shake256_Produces64ByteOutput()
    {
        using HashAlgorithm hash = DigestAlgorithms.CreateFromOid(PdfDigestOids.Shake256);

        byte[] digest = hash.ComputeHash(new byte[] { 1, 2, 3 });

        Assert.Equal(64, digest.Length);
    }
}
