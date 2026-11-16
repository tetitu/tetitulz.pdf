// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Core.Tests.Signing;

/// <summary>
/// <see cref="IndependentEd448Cms"/>'s primitives against vectors another implementation made:
/// SHAKE256 outputs from Python's <c>hashlib</c>, Ed448 keys and signatures from pyca/cryptography
/// (private keys of 57 repeated bytes 0x01, 0x02, 0x03). A verifier that only ever checks this
/// library's signatures could share its curve constants' mistake.
/// </summary>
public class IndependentEd448CmsTests
{
    [Theory]
    [InlineData("", "46b9dd2b0ba88d13233b3feb743eeb243fcd52ea62b81b82b50c27646ed5762fd75dc4ddd8c0f200cb05019d67b592f6fc821c49479ab48640292eacb3b7c4be")]
    [InlineData("616263", "483366601360a8771c6863080cc4114d8db44530f8f1e1ee4f94ea37e78b5739d5a15bef186a5386c75744c0527e1faa9f8726e462a12a4feb06bd8801e751e4")]
    public void Shake256_MatchesTheVector(string inputHex, string expectedHex)
    {
        Assert.Equal(expectedHex, Convert.ToHexString(IndependentEd448Cms.Shake256(Convert.FromHexString(inputHex), 64)).ToLowerInvariant());
    }

    [Fact]
    public void Shake256_OverSeveralBlocks_MatchesTheVector()
    {
        byte[] input = Enumerable.Range(0, 768).Select(i => (byte)(i % 256)).ToArray();
        Assert.Equal("2c08d3827f9ced84c8263c16ac1d877a70eff56c86a63d3a701ea8cea0ef0b3bf042088081df39105650eccabd3fc225c5f4caffb276b4b53523270e0d13981e",
            Convert.ToHexString(IndependentEd448Cms.Shake256(input, 64)).ToLowerInvariant());
    }

    [Theory]
    [InlineData("", "e0758a33267939a394fb5ccb202ee851cebc2e89c91ac1289e2bfcddfd9ff9fc5694b0f569d7f7e9da16e1cde9301b29f48128b3cbd1168580",
        "23a40d7e617371e81ea8148c0100aa1abbe01fe6fcf144b65dfbace1e351a969f46d7d1097a35f612d83b816b917ceafd1db596289bfa5140083cb74f872e983de6b010ee2d2b0c18df015ce97eb8f81800d801dc64b4a2475fb66f4040a426b44465dfa2d7abe8d65d04e927dd5ed382000")]
    [InlineData("03", "b52fd5b2cb34d6f944ab81d765fa026b63fd8448b4890d025cba17308a312ae4f31a012dc08c891e9a7c3d29dbad1aaf964e6c74073249f300",
        "fc9a7592d7f08a0368f6654168287b28dad1c20b82164068e2fee818631bcc4cfe589b45e773aaa5347f474e790f20f10852372ff1c75ad6802e6896e35ce235570e2847a88768b3ade4a8e29c6ec8afc7c2f9d65b1e2eaacf29bfc65ff9be4bc4d36558e88f3eb0a3fa23cc070669793e00")]
    [InlineData("7465746974756c7a20696e646570656e64656e7420656434343820766563746f72", "18e2f925aef56fc05910474593ee84c932bc7ea4d352a3416f72d115e60eb58cd862f73ac50886fbaad6354a4eeb01fd9f740491fb11f59580",
        "7c56449edd8baafd663aaaa95ecb8e9f7714a494bd12ed1fb7cc17a53abe369c65906d39344734c2517b38672a943a0f8ecc6d4dd90c4a8e00df4f11111b7209c0cdae81d0a68fe1557448090e7c5412211cd59fc35552f75c158daa6a5bdeeffac07550e4479f891627f090900df8970b00")]
    public void Ed448_VerifiesTheVector_AndRefusesItAltered(string messageHex, string publicKeyHex, string signatureHex)
    {
        byte[] message = Convert.FromHexString(messageHex), key = Convert.FromHexString(publicKeyHex), signature = Convert.FromHexString(signatureHex);
        Assert.True(IndependentEd448Cms.Ed448.Verify(key, message, signature));
        signature[10] ^= 1;
        Assert.False(IndependentEd448Cms.Ed448.Verify(key, message, signature));
        signature[10] ^= 1;
        Assert.False(IndependentEd448Cms.Ed448.Verify(key, message.Concat(new byte[] { 0 }).ToArray(), signature));
    }
}
