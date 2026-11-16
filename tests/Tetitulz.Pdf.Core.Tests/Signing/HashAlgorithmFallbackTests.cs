// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// Locks in ֲ§62 (C4): unknown hash-algorithm names no longer silently
/// downgrade to SHA-256 in the signing path.  Pre-ֲ§62 a caller passing
/// <c>"SHA3-512"</c> to <see cref="TsaClient.GetHashOid"/> would silently
/// receive the SHA-256 OID, while <c>CmsSignatureGenerator</c> understood
/// the SHA-3 name correctly — an asymmetry that produced
/// half-downgraded signatures with no diagnostic.
/// </summary>
public class HashAlgorithmFallbackTests
{
    [Fact]
    public void TsaClient_GetHashOid_Sha3_512_ReturnsSha3Oid()
    {
        Assert.Equal("2.16.840.1.101.3.4.2.10", TsaClient.GetHashOid("SHA3-512"));
        Assert.Equal("2.16.840.1.101.3.4.2.9",  TsaClient.GetHashOid("SHA3-384"));
        Assert.Equal("2.16.840.1.101.3.4.2.8",  TsaClient.GetHashOid("SHA3-256"));
    }

    [Fact]
    public void TsaClient_GetHashOid_UnknownAlgorithm_Throws()
    {
        // No more silent SHA-256 fallback.
        var ex = Assert.Throws<ArgumentException>(() =>
            TsaClient.GetHashOid("BLAKE2"));
        Assert.Contains("Unsupported", ex.Message);
        Assert.Contains("BLAKE2", ex.Message);
    }

    [Fact]
    public void TsaClient_GetHashOid_KnownAlgorithms_StillReturnCorrectOids()
    {
        Assert.Equal("2.16.840.1.101.3.4.2.1", TsaClient.GetHashOid("SHA-256"));
        Assert.Equal("2.16.840.1.101.3.4.2.2", TsaClient.GetHashOid("SHA-384"));
        Assert.Equal("2.16.840.1.101.3.4.2.3", TsaClient.GetHashOid("SHA-512"));
        Assert.Equal("1.3.14.3.2.26",          TsaClient.GetHashOid("SHA-1"));
    }
}
