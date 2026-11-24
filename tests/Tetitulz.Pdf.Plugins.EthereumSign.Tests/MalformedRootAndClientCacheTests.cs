// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Plugins.EthereumSign;
using Xunit;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests;

/// <summary>
/// Names the xunit collection that owns <c>BlockchainSignatureProvider</c>'s process-wide
/// verification-client cache. Every class that reads or clears it joins this collection, so the
/// classes run one at a time.
/// </summary>
[CollectionDefinition(Name)]
public sealed class EthereumSignSharedState
{
    public const string Name = "EthereumSign shared static state";
}

/// <summary>
/// A public method that dereferences a parse result only once it is validated, and a
/// process-wide client cache that can be bounded and cleared.
/// </summary>
/// <remarks>
/// <para><b>Shares a collection with <see cref="EthereumSignPluginTests"/> on purpose.</b> xunit
/// runs test <i>classes</i> in parallel, and the verification-client cache is process-wide — so
/// that class's <c>Shutdown_LeavesThePluginReusable</c> (which clears the cache) could empty it
/// between this class's <c>Touch</c> and its assertion. CLAUDE.md
/// records exactly this trap: "a negative assertion that reads shared state is asserting something
/// about the whole process". Serialising the two classes removes the coupling rather than hiding
/// it behind a retry.</para>
/// </remarks>
[Collection(EthereumSignSharedState.Name)]
public class MalformedRootAndClientCacheTests
{
    private static BlockchainProof Proof() => new BlockchainProof
    {
        TransactionHash = "0xabc123",
        BlockNumber     = 42,
        Timestamp       = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        DocumentHash    = "deadbeef",
        Network         = "sepolia",
        SignerAddress   = "0x1234",
    };

    // ────────────────────────────────────────────────────────────────────
    //  /Root validation
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A trailer naming an object that is not there. <c>ResolveObject</c> returns null and the
    /// enumeration of the catalog threw <see cref="NullReferenceException"/> — never a contract —
    /// out of a public API that reports every other failure as <see cref="PdfSigningException"/>.
    /// </summary>
    [Fact]
    public void ADanglingRootReference_IsRefusedWithATypedException()
    {
        byte[] pdf = PdfWithRoot("/Root 9 0 R");

        var ex = Assert.Throws<PdfSigningException>(
            () => PdfBlockchainSigner.EmbedBlockchainProof(pdf, Proof()));

        Assert.Contains("/Root", ex.Message, StringComparison.Ordinal);
        Assert.Contains("catalog", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// An inline <c>/Root &lt;&lt; … &gt;&gt;</c> gives object number 0, which is reserved as the
    /// head of the cross-reference free list. Writing <c>0 0 obj</c> and an xref entry for object 0
    /// would produce a malformed file whose proof is unreachable — and report success.
    /// </summary>
    [Fact]
    public void ADirectRootDictionary_IsRefusedRatherThanWrittenAsObjectZero()
    {
        byte[] pdf = PdfWithRoot("/Root << /Type /Catalog /Pages 2 0 R >>");

        var ex = Assert.Throws<PdfSigningException>(
            () => PdfBlockchainSigner.EmbedBlockchainProof(pdf, Proof()));

        Assert.Contains("indirect reference", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Both refusals must be catchable through the shared base, so a caller's existing
    /// <c>catch (PdfException)</c> covers them.
    /// </summary>
    [Fact]
    public void TheRefusals_ArePdfExceptions()
    {
        Assert.True(typeof(PdfException).IsAssignableFrom(typeof(PdfSigningException)));
    }

    /// <summary>
    /// <b>The control.</b> A well-formed <c>/Root 1 0 R</c> still embeds — the guards reject
    /// malformed input, not all input.
    /// </summary>
    [Fact]
    public void AWellFormedRoot_StillEmbedsTheProof()
    {
        byte[] pdf = PdfWithRoot("/Root 1 0 R");

        byte[] result = PdfBlockchainSigner.EmbedBlockchainProof(pdf, Proof());

        Assert.True(result.Length > pdf.Length);
        Assert.Contains("BlockchainProof", Encoding.Latin1.GetString(result), StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Verification client cache
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Infura and Alchemy carry a per-tenant API key in the URL <i>path</i>, so two tenants share an
    /// origin while being different credentials. They must NOT share a cached client: whichever URL
    /// arrived first would build the one <c>Web3</c>, and every later tenant's verification would be
    /// issued against that tenant's project.
    /// </summary>
    /// <remarks>
    /// An earlier revision collapsed the key to scheme+host+port and asserted the opposite of this,
    /// reasoning that the origin is what determines the connection pool. It is — which is precisely
    /// why collapsing is a correctness bug rather than a cheaper leak fix. Unbounded growth is
    /// bounded by the cap instead, which this test also pins: 50 distinct tenants must not produce
    /// 50 live entries.
    /// </remarks>
    [Fact]
    public void PerTenantPaths_DoNotShareACachedClient()
    {
        BlockchainSignatureProvider.ClearClientCache();

        Touch("https://sepolia.infura.io/v3/tenant-A");
        Touch("https://sepolia.infura.io/v3/tenant-B");

        Assert.Equal(2, BlockchainSignatureProvider.CachedClientCount);

        // …and the leak the collapse was reaching for is closed by the bound, not by the key.
        BlockchainSignatureProvider.ClearClientCache();
        for (int i = 0; i < 50; i++)
            Touch($"https://sepolia.infura.io/v3/tenant-key-{i}");

        Assert.InRange(BlockchainSignatureProvider.CachedClientCount, 1, 16);
    }

    /// <summary>
    /// Genuinely distinct endpoints get their own entry — otherwise the collapse above would be
    /// achieved by caching nothing per host, which is a different bug.
    /// </summary>
    [Fact]
    public void DistinctHosts_GetDistinctCachedClients()
    {
        BlockchainSignatureProvider.ClearClientCache();

        Touch("https://sepolia.infura.io/v3/key");
        Touch("https://mainnet.infura.io/v3/key");

        Assert.Equal(2, BlockchainSignatureProvider.CachedClientCount);
    }

    /// <summary>
    /// The cache is bounded. Without a cap, a host handed many endpoints grew it without limit and
    /// nothing ever released the entries.
    /// </summary>
    [Fact]
    public void TheCache_IsBounded()
    {
        BlockchainSignatureProvider.ClearClientCache();

        for (int i = 0; i < 200; i++)
            Touch($"https://node-{i}.example.com/rpc");

        Assert.True(BlockchainSignatureProvider.CachedClientCount <= 16,
            $"the cache grew to {BlockchainSignatureProvider.CachedClientCount} entries; it must be bounded");
    }

    /// <summary>
    /// <c>Shutdown</c> was an empty body, so unregistering the plugin released nothing. It now
    /// clears the cache.
    /// </summary>
    [Fact]
    public void PluginShutdown_ClearsTheCache()
    {
        BlockchainSignatureProvider.ClearClientCache();
        Touch("https://sepolia.infura.io/v3/key");
        Assert.True(BlockchainSignatureProvider.CachedClientCount > 0);

        new EthereumSignPlugin(new BlockchainParameters
        {
            RpcUrl     = "http://localhost:8545",
            PrivateKey = "0x" + new string('a', 64),
        }).Shutdown();

        Assert.Equal(0, BlockchainSignatureProvider.CachedClientCount);
    }

    /// <summary>
    /// Populates the cache for one endpoint. The client factory is called directly rather than
    /// through <c>VerifyHashAsync</c>: caching happens before any request is made, and driving it
    /// through the RPC call would put a real network round-trip (and its DNS timeout) inside a unit
    /// test for something that has nothing to do with the network.
    /// </summary>
    private static void Touch(string rpcUrl) => BlockchainSignatureProvider.GetVerifyClient(rpcUrl);

    /// <summary>A minimal PDF whose trailer carries <paramref name="rootEntry"/> verbatim.</summary>
    private static byte[] PdfWithRoot(string rootEntry)
    {
        var pdf = new StringBuilder();
        pdf.Append("%PDF-1.4\n");

        int off1 = pdf.Length;
        pdf.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        int off2 = pdf.Length;
        pdf.Append("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        int off3 = pdf.Length;
        pdf.Append("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        int xref = pdf.Length;
        pdf.Append("xref\n0 4\n0000000000 65535 f \n");
        foreach (int o in new[] { off1, off2, off3 })
            pdf.AppendFormat("{0:D10} 00000 n \n", o);
        pdf.AppendFormat("trailer\n<< /Size 4 {0} >>\nstartxref\n{1}\n%%EOF\n", rootEntry, xref);

        return Encoding.Latin1.GetBytes(pdf.ToString());
    }
}
