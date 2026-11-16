// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Plugins.EthereumSign.Tests.Helpers;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests.Signing;

/// <summary>
/// The tamper check in <see cref="PdfBlockchainVerifier"/> must not be switchable off by the
/// document it is checking. <c>/HashedLength</c> — the length of the notarized prefix — lives in
/// the same attacker-controlled <c>/BlockchainProof</c> dictionary as everything else, and the
/// verifier used to fall back to hashing the <c>/DocumentHash</c> <i>string read from that same
/// file</i> when the key was absent. That compares the chain against itself: it matches by
/// construction, so a modified document produced <c>TransactionFound &amp;&amp; HashMatches &amp;&amp;
/// SignerMatches</c> and therefore <c>IsValid == true</c>, with the only signal on
/// <see cref="BlockchainVerificationResult.Warning"/>, which <c>IsValid</c> does not read.
///
/// <para>These tests never reach the network. They pass an already-cancelled token, which
/// <see cref="BlockchainSignatureProvider.VerifyHashAsync"/> observes before it constructs a Web3
/// client — but only <i>after</i> it records the hash it was handed on
/// <see cref="BlockchainVerificationResult.ComputedHash"/>. That field is therefore an exact,
/// offline read-out of which bytes the verifier presented to the chain, which is the behaviour
/// under test.</para>
/// </summary>
public class PdfBlockchainVerifierIntegrityTests
{
    // A valid http URL is required: BlockchainSignatureProvider.ValidateRpcUrl rejects anything
    // else with a PdfSigningException before the cancellation is observed. No request is made.
    private static BlockchainParameters LocalParameters() =>
        new BlockchainParameters { RpcUrl = "http://127.0.0.1:8545", ChainId = 31337 };

    [Fact]
    public async Task Deleting_HashedLength_does_not_disable_the_tamper_check()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("PAYOUT 100 USD");

        // The honest proof: the signer notarized SHA-256 over exactly these bytes, so this hex
        // string is what sits on-chain and what /DocumentHash records.
        string notarizedHash = Sha256Hex(original, original.Length);
        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, new BlockchainProof
        {
            TransactionHash = "0xfeed",
            BlockNumber = 42,
            Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash = notarizedHash,
            Network = "hardhat",
            SignerAddress = "0xsigner",
        });

        // The attack: change the notarized body, then delete the one key that makes the verifier
        // re-hash it. Both edits preserve every byte offset, so the xref still resolves.
        byte[] attacked = OverwriteAscii(withProof, "PAYOUT 100 USD", "PAYOUT 900 USD");
        attacked = BlankOutHashedLength(attacked);

        using var ms = new MemoryStream(attacked);
        using var doc = PdfDocument.Open(ms);

        // Fixture check: the document must still parse as a proof-carrying PDF, or the assertions
        // below would be satisfied by a document that simply failed earlier.
        var proofDict = doc.Catalog.Dictionary[new PdfName("BlockchainProof")] as PdfDictionary;
        Assert.NotNull(proofDict);
        Assert.Equal("0xfeed", (proofDict[new PdfName("TxHash")] as PdfString)?.GetText());
        Assert.Equal(notarizedHash, (proofDict[new PdfName("DocumentHash")] as PdfString)?.GetText());
        Assert.Null(proofDict.GetInteger(new PdfName("HashedLength")));

        var result = await new PdfBlockchainVerifier()
            .VerifyAsync(doc, LocalParameters(), new CancellationToken(canceled: true));

        // The load-bearing assertion. Before the fix the verifier handed the chain the
        // /DocumentHash string it read out of the tampered file, i.e. the hash of the ORIGINAL
        // document — which matches on-chain by construction, so the modified body verified.
        Assert.NotEqual(notarizedHash, result.ComputedHash);
        Assert.Null(result.ComputedHash);

        Assert.False(result.IsValid);
        Assert.Contains("/HashedLength", result.Error);
        // The refusal is fatal, not an advisory the caller has to remember to read.
        Assert.Null(result.Warning);
    }

    [Fact]
    public async Task An_out_of_range_HashedLength_is_refused_rather_than_falling_back()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("PAYOUT 100 USD");
        string notarizedHash = Sha256Hex(original, original.Length);
        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, new BlockchainProof
        {
            TransactionHash = "0xfeed",
            BlockNumber = 42,
            Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash = notarizedHash,
            Network = "hardhat",
            SignerAddress = "0xsigner",
        });

        // Same escape hatch reached from the other side: a length no prefix of the file can
        // satisfy. Rewritten in place so every offset survives.
        byte[] attacked = OverwriteAscii(withProof, "PAYOUT 100 USD", "PAYOUT 900 USD");
        attacked = MakeHashedLengthOversized(attacked);

        using var ms = new MemoryStream(attacked);
        using var doc = PdfDocument.Open(ms);

        // Fixture check: the edit must have produced exactly the condition under test.
        var proofDict = doc.Catalog.Dictionary[new PdfName("BlockchainProof")] as PdfDictionary;
        Assert.NotNull(proofDict);
        int? declared = proofDict.GetInteger(new PdfName("HashedLength"));
        Assert.NotNull(declared);
        Assert.True(declared > attacked.Length,
            $"Fixture declared /HashedLength {declared} for a {attacked.Length}-byte file; " +
            "that is not the out-of-range case.");

        var result = await new PdfBlockchainVerifier()
            .VerifyAsync(doc, LocalParameters(), new CancellationToken(canceled: true));

        Assert.NotEqual(notarizedHash, result.ComputedHash);
        Assert.Null(result.ComputedHash);
        Assert.False(result.IsValid);
        Assert.Contains("exceeds the document length", result.Error);
    }

    [Fact]
    public async Task With_HashedLength_intact_the_prefix_is_re_hashed_and_DocumentHash_is_ignored()
    {
        byte[] original = MinimalPdfBuilder.CreateMinimalPdf("PAYOUT 100 USD");

        // A proof whose /DocumentHash is a lie. A verifier that trusts the embedded string would
        // present these zeros to the chain; one that re-computes presents SHA-256 of the prefix.
        const string LyingHash = "0000000000000000000000000000000000000000000000000000000000000000";
        byte[] withProof = PdfBlockchainSigner.EmbedBlockchainProof(original, new BlockchainProof
        {
            TransactionHash = "0xfeed",
            BlockNumber = 42,
            Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash = LyingHash,
            Network = "hardhat",
            SignerAddress = "0xsigner",
        });

        using var ms = new MemoryStream(withProof);
        using var doc = PdfDocument.Open(ms);

        var result = await new PdfBlockchainVerifier()
            .VerifyAsync(doc, LocalParameters(), new CancellationToken(canceled: true));

        // /HashedLength records the length of the document BEFORE the proof update, so the
        // re-computed hash is SHA-256 over exactly the pre-proof bytes.
        Assert.Equal(Sha256Hex(original, original.Length), result.ComputedHash);
        Assert.NotEqual(LyingHash, result.ComputedHash);
    }

    // ── fixture helpers ──────────────────────────────────────────────────────

    private static string Sha256Hex(byte[] bytes, int count)
    {
        using var sha = SHA256.Create();
        // Lowercase, unprefixed — the format Nethereum's ToHex() produces for ComputedHash.
        return Convert.ToHexString(sha.ComputeHash(bytes, 0, count)).ToLowerInvariant();
    }

    /// <summary>Replaces an ASCII run in place; the two strings must be the same length so that
    /// every byte offset in the file (and therefore the xref) stays valid.</summary>
    private static byte[] OverwriteAscii(byte[] pdf, string find, string replace)
    {
        Assert.Equal(find.Length, replace.Length);
        int at = IndexOfAscii(pdf, find);
        Assert.True(at >= 0, $"Fixture never contained '{find}'.");

        byte[] copy = (byte[])pdf.Clone();
        Encoding.ASCII.GetBytes(replace, 0, replace.Length, copy, at);
        return copy;
    }

    /// <summary>Erases " /HashedLength &lt;digits&gt;" with spaces, leaving the file the same
    /// length — the minimal edit an attacker makes to reach the removed fallback.</summary>
    private static byte[] BlankOutHashedLength(byte[] pdf)
    {
        byte[] copy = (byte[])pdf.Clone();
        int at = IndexOfAscii(copy, "/HashedLength");
        Assert.True(at >= 0, "Fixture carried no /HashedLength to remove.");

        int end = at + "/HashedLength".Length;
        while (end < copy.Length && copy[end] != (byte)'>')
            end++;
        for (int i = at; i < end; i++)
            copy[i] = (byte)' ';
        return copy;
    }

    /// <summary>Multiplies the declared /HashedLength by ten (appending a digit) without changing
    /// the file's length: the entry is shifted one byte left, which costs the space separating it
    /// from the preceding <c>)</c> — and <c>)/HashedLength</c> is a perfectly legal PDF token
    /// boundary, so the dictionary still parses.</summary>
    private static byte[] MakeHashedLengthOversized(byte[] pdf)
    {
        byte[] copy = (byte[])pdf.Clone();
        int at = IndexOfAscii(copy, "/HashedLength");
        Assert.True(at > 0, "Fixture carried no /HashedLength to rewrite.");
        Assert.Equal((byte)' ', copy[at - 1]);

        int i = at + "/HashedLength".Length;
        while (i < copy.Length && copy[i] == (byte)' ') i++;
        int digitStart = i;
        while (i < copy.Length && copy[i] >= (byte)'0' && copy[i] <= (byte)'9') i++;
        Assert.True(i > digitStart, "Fixture's /HashedLength had no digits.");

        string rewritten = "/HashedLength " +
                           Encoding.ASCII.GetString(copy, digitStart, i - digitStart) + "9";
        Assert.Equal(i - (at - 1), rewritten.Length);   // same length ⇒ every offset survives
        Encoding.ASCII.GetBytes(rewritten, 0, rewritten.Length, copy, at - 1);
        return copy;
    }

    private static int IndexOfAscii(byte[] haystack, string needle)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(needle);
        for (int i = 0; i <= haystack.Length - pattern.Length; i++)
        {
            int j = 0;
            while (j < pattern.Length && haystack[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }
}
