// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.EthereumSign.Tests.Helpers;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests.Signing;

public class PdfBlockchainSignerTests
{
    [Fact]
    public void EmbedBlockchainProof_AddsProofToCatalog()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Blockchain test");

        var proof = new BlockchainProof
        {
            TransactionHash = "0xabc123def456",
            BlockNumber = 12345678,
            Timestamp = new DateTime(2026, 2, 17, 12, 0, 0, DateTimeKind.Utc),
            DocumentHash = "a1b2c3d4e5f6",
            Network = "sepolia",
            SignerAddress = "0x1234567890abcdef"
        };

        byte[] result = PdfBlockchainSigner.EmbedBlockchainProof(pdfBytes, proof);

        Assert.True(result.Length > pdfBytes.Length);

        using var ms = new MemoryStream(result);
        using var doc = PdfDocument.Open(ms);

        var proofObj = doc.Catalog.Dictionary[new PdfName("BlockchainProof")] as PdfDictionary;
        Assert.NotNull(proofObj);

        var txHash = proofObj[new PdfName("TxHash")] as PdfString;
        Assert.NotNull(txHash);
        Assert.Equal("0xabc123def456", txHash.GetText());

        var network = proofObj[new PdfName("Network")] as PdfString;
        Assert.NotNull(network);
        Assert.Equal("sepolia", network.GetText());

        var signerAddr = proofObj[new PdfName("SignerAddress")] as PdfString;
        Assert.NotNull(signerAddr);
        Assert.Equal("0x1234567890abcdef", signerAddr.GetText());
    }

    [Fact]
    public void EmbedBlockchainProof_PreservesExistingCatalogEntries()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Catalog preservation test");

        var proof = new BlockchainProof
        {
            TransactionHash = "0xtest",
            BlockNumber = 1,
            Timestamp = DateTime.UtcNow,
            DocumentHash = "deadbeef",
            Network = "hardhat",
            SignerAddress = "0xtest"
        };

        byte[] result = PdfBlockchainSigner.EmbedBlockchainProof(pdfBytes, proof);

        using var ms = new MemoryStream(result);
        using var doc = PdfDocument.Open(ms);

        var catalog = doc.Catalog.Dictionary;
        var typeObj = catalog[PdfName.Type] as PdfName;
        Assert.Equal("Catalog", typeObj?.Value);

        var pagesRef = catalog[PdfName.Pages];
        Assert.NotNull(pagesRef);

        Assert.NotNull(catalog[new PdfName("BlockchainProof")]);
    }

    [Fact]
    public void EmbedBlockchainProof_ProofIsPreservedAfterReopen()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Reopen test");

        var proof = new BlockchainProof
        {
            TransactionHash = "0xreopen123",
            BlockNumber = 99999,
            Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash = "aabbccdd",
            Network = "mainnet",
            SignerAddress = "0xreopenaddr"
        };

        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(pdfBytes, proof);

        string tempPath = Path.Combine(Path.GetTempPath(), $"blockchain_test_{Guid.NewGuid()}.pdf");
        try
        {
            File.WriteAllBytes(tempPath, withProof);

            using var doc = PdfDocument.Open(tempPath);
            var proofDict = doc.Catalog.Dictionary[new PdfName("BlockchainProof")] as PdfDictionary;
            Assert.NotNull(proofDict);

            var txHash = proofDict[new PdfName("TxHash")] as PdfString;
            Assert.Equal("0xreopen123", txHash?.GetText());

            var docHash = proofDict[new PdfName("DocumentHash")] as PdfString;
            Assert.Equal("aabbccdd", docHash?.GetText());
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Renamed from <c>…RequiresAllThreeFlags</c> when <c>IsLastRevision</c> joined the conjunction:
    /// leaving the old name over an assertion about four flags is the failure mode CLAUDE.md calls
    /// out as invisible to every guard — a perfectly falsifiable test that falsifies the wrong claim.
    /// </summary>
    [Fact]
    public void BlockchainVerificationResult_IsValid_RequiresAllFourFlags()
    {
        // Security contract: IsValid requires the transaction to be found, the (re-computed)
        // document hash to match the on-chain data, the on-chain sender to match the claimed
        // signer, AND the notarized revision to still be the last one in the file. A matching hash
        // alone is forgeable; a hash over a PREFIX says nothing about bytes appended after it.
        var result = new BlockchainVerificationResult();
        Assert.False(result.IsValid);

        result.TransactionFound = true;
        Assert.False(result.IsValid);

        result.HashMatches = true;
        Assert.False(result.IsValid);          // signer not yet verified

        result.SignerMatches = true;
        Assert.False(result.IsValid);          // and content may have been appended after the proof

        result.IsLastRevision = true;
        Assert.True(result.IsValid);           // all four now satisfied

        // Each one alone is enough to invalidate.
        result.IsLastRevision = false;
        Assert.False(result.IsValid);

        result.IsLastRevision = true;
        result.SignerMatches = false;
        Assert.False(result.IsValid);

        result.SignerMatches = true;
        result.TransactionFound = false;
        Assert.False(result.IsValid);
    }

    [Fact]
    public void BlockchainParameters_GetNetworkName_DeriveFromChainId()
    {
        var p = new BlockchainParameters { ChainId = 1 };
        Assert.Equal("mainnet", p.GetNetworkName());

        p.ChainId = 11155111;
        Assert.Equal("sepolia", p.GetNetworkName());

        p.ChainId = 31337;
        Assert.Equal("hardhat", p.GetNetworkName());

        p.ChainId = 1337;
        Assert.Equal("ganache", p.GetNetworkName());

        p.ChainId = 42;
        Assert.Equal("chain-42", p.GetNetworkName());

        p.NetworkName = "custom-network";
        Assert.Equal("custom-network", p.GetNetworkName());
    }

    [Fact]
    public void EmbedBlockchainProof_SpecialCharactersInProof_AreEscaped()
    {
        byte[] pdfBytes = MinimalPdfBuilder.CreateMinimalPdf("Escape test");

        // Each field carries a character that BREAKS a PDF literal string if written raw.
        // The previous fixture used only *balanced* parens — "0x(test)hash" — which §7.3.4.2
        // permits unescaped, so it could not detect an unescaped write at all.
        var proof = new BlockchainProof
        {
            TransactionHash = "0x(unbalanced",       // lone "(" — swallows the rest of the object
            BlockNumber = 1,
            Timestamp = DateTime.UtcNow,
            DocumentHash = "abc)123",                // lone ")" — closes the string early
            Network = "test\\net",                    // lone backslash — "\\n" would become a newline
            SignerAddress = "0x\ttab\u0007bell"        // control bytes needing octal escapes
        };

        byte[] result = PdfBlockchainSigner.EmbedBlockchainProof(pdfBytes, proof);

        using var ms = new MemoryStream(result);
        using var doc = PdfDocument.Open(ms);

        var proofDict = doc.Catalog.Dictionary[new PdfName("BlockchainProof")] as PdfDictionary;
        Assert.NotNull(proofDict);

        // Escaping means the values survive the round trip byte for byte. An unescaped "(" or ")"
        // terminates the PDF string early, so the value read back would be truncated or the
        // dictionary would fail to parse at all — and a lone backslash would swallow the next
        // character. Asserting only that the dictionary exists cannot see any of that: the proof
        // could come back as "0x" and still pass.
        Assert.Equal(proof.TransactionHash, StringValue(proofDict, "TxHash"));
        Assert.Equal(proof.SignerAddress,   StringValue(proofDict, "SignerAddress"));
        Assert.Equal(proof.Network,         StringValue(proofDict, "Network"));
        Assert.Equal(proof.DocumentHash,    StringValue(proofDict, "DocumentHash"));
    }

    private static string StringValue(PdfDictionary dict, string key) =>
        (dict[new PdfName(key)] as PdfString)?.GetText();
}
