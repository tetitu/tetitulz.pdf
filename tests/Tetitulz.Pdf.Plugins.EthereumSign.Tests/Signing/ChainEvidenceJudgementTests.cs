// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Numerics;
using Tetitulz.Pdf.Plugins.EthereumSign;
using Xunit;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests.Signing;

/// <summary>
/// What a transaction an endpoint returned proves about a document
/// (<c>BlockchainSignatureProvider.Judge</c>), asked with the transaction's fields given directly —
/// no endpoint is reached.
/// </summary>
/// <remarks>
/// Every refusal starts from the transaction and proof the first test accepts and changes one
/// thing, so the refusal is that thing's.
/// </remarks>
public class ChainEvidenceJudgementTests
{
    private const string Signer = "0xAbCdEf0123456789aBcDeF0123456789abcdef01";
    private const string Someone = "0x1111111111111111111111111111111111111111";
    private static readonly byte[] Hash = { 0xAA, 0xBB, 0xCC, 0xDD };

    private static BlockchainVerificationResult Judge(
        string input = "0xaabbccdd", string from = Signer, long? block = 1234,
        string proofSigner = Signer, string? expectedSigner = null)
    {
        var result = new BlockchainVerificationResult { IsLastRevision = true };
        BlockchainSignatureProvider.Judge(result, input, from, block == null ? null : new BigInteger(block.Value),
            Hash, new BlockchainProof { TransactionHash = "0x01", SignerAddress = proofSigner }, expectedSigner);
        return result;
    }

    [Fact]
    public void AMinedTransactionCarryingTheHashFromTheNamedSigner_IsAccepted()
    {
        BlockchainVerificationResult result = Judge();

        Assert.True(result.IsValid, result.Error);
        Assert.Equal(1234, result.BlockNumber);
        Assert.Equal(Signer, result.SignerOnChain);
        Assert.Contains("not by whom", result.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// An endpoint answers for a pending transaction as it does for a mined one, only without a
    /// block. Nothing has been recorded, so it is not found — whatever its data and sender say.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void ATransactionThatIsNotInABlock_IsNotAProof(long? block)
    {
        BlockchainVerificationResult result = Judge(block: block);

        Assert.False(result.TransactionFound);
        Assert.False(result.IsValid);
        Assert.Contains("not in a block", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ATransactionCarryingAnotherHash_IsRefused()
    {
        BlockchainVerificationResult result = Judge(input: "0xaabbccde");

        Assert.True(result.TransactionFound);
        Assert.False(result.HashMatches);
        Assert.False(result.IsValid);
    }

    /// <summary>A proof naming one signer over a transaction another address sent is forged.</summary>
    [Fact]
    public void AProofNamingASignerOtherThanTheSender_IsRefused()
    {
        BlockchainVerificationResult result = Judge(proofSigner: Someone);

        Assert.True(result.HashMatches);
        Assert.False(result.SignerMatches);
        Assert.False(result.IsValid);
    }

    /// <summary>
    /// The case the proof's own claim cannot answer: someone alters the document, anchors the new
    /// hash themselves and writes a proof naming themselves. Sender and proof agree — so it passes
    /// until the caller says whose anchor it accepts, and then it does not.
    /// </summary>
    [Fact]
    public void AnAnchorBySomeoneElse_PassesOnTheProofsOwnWord_AndIsRefusedWhenTheSignerIsStated()
    {
        Assert.True(Judge(from: Someone, proofSigner: Someone).IsValid);

        BlockchainVerificationResult held = Judge(from: Someone, proofSigner: Someone, expectedSigner: Signer);

        Assert.True(held.HashMatches);
        Assert.False(held.SignerMatches);
        Assert.False(held.IsValid);
        Assert.Contains("is not the expected signer", held.Error, StringComparison.Ordinal);
    }

    /// <summary>The stated signer accepts its own anchor, however the address is cased or prefixed, and then no warning is owed.</summary>
    [Theory]
    [InlineData("0xABCDEF0123456789ABCDEF0123456789ABCDEF01")]
    [InlineData("abcdef0123456789abcdef0123456789abcdef01")]
    public void TheStatedSignersOwnAnchor_IsAccepted(string expected)
    {
        BlockchainVerificationResult result = Judge(expectedSigner: expected);

        Assert.True(result.IsValid, result.Error);
        Assert.Null(result.Warning);
    }

    /// <summary>A stated signer does not excuse a forged proof: the proof's own name is still held to the sender.</summary>
    [Fact]
    public void AStatedSigner_StillHoldsTheProofsOwnClaimToTheSender()
    {
        BlockchainVerificationResult result = Judge(proofSigner: Someone, expectedSigner: Signer);

        Assert.False(result.SignerMatches);
    }

    /// <summary>With no digest to compare there is no match: an empty transaction payload does not equal "no hash".</summary>
    [Fact]
    public void WithNoDocumentHash_NothingMatches()
    {
        var result = new BlockchainVerificationResult { IsLastRevision = true };
        BlockchainSignatureProvider.Judge(result, "0x", Signer, new BigInteger(5), Array.Empty<byte>(),
            new BlockchainProof { SignerAddress = Signer }, null);

        Assert.False(result.HashMatches);
    }
}
