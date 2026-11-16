// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Plugins.EthereumSign;
using Xunit;
using Tetitulz.Pdf.Signing.Plugins;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests;

/// <summary>
/// Unit tests for <see cref="EthereumSignPlugin"/>.
/// All tests use a stub <see cref="ISignPlugin"/> or in-memory mocks — no real
/// Ethereum network connection is required.
/// </summary>
[Collection(EthereumSignSharedState.Name)]
public sealed class EthereumSignPluginTests
{
    // ── Construction / Initialize ────────────────────────────────────────────

    [Fact]
    public void Constructor_NullParameters_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new EthereumSignPlugin(null!));
    }

    [Fact]
    public void Initialize_MissingRpcUrl_Throws()
    {
        var plugin = new EthereumSignPlugin(new BlockchainParameters
        {
            RpcUrl = "",
            PrivateKey = "0x" + new string('a', 64),
        });
        Assert.Throws<InvalidOperationException>(() => plugin.Initialize());
    }

    [Fact]
    public void Initialize_MissingPrivateKey_Throws()
    {
        var plugin = new EthereumSignPlugin(new BlockchainParameters
        {
            RpcUrl = "http://localhost:8545",
            PrivateKey = "",
        });
        Assert.Throws<InvalidOperationException>(() => plugin.Initialize());
    }

    /// <summary>
    /// <c>Initialize</c> is this plugin's configuration validator, so the claim worth making is
    /// that it <b>discriminates</b>: a complete configuration is accepted — repeatedly, since
    /// validation must not latch — while one whose RpcUrl is only whitespace is refused.
    ///
    /// <para>Either half alone is unfalsifiable. "It did not throw" is equally true of an empty
    /// method body, and the sibling reject-tests would still pass if it threw for everything.</para>
    /// </summary>
    [Fact]
    public void Initialize_ValidParameters_AcceptsTheConfigurationAndIsRepeatable()
    {
        var plugin = new EthereumSignPlugin(new BlockchainParameters
        {
            RpcUrl = "http://localhost:8545",
            PrivateKey = "0x" + new string('a', 64),
        });

        plugin.Initialize();
        plugin.Initialize();   // repeatable: validation consumes nothing

        var whitespaceRpc = new EthereumSignPlugin(new BlockchainParameters
        {
            RpcUrl = "   ",
            PrivateKey = "0x" + new string('a', 64),
        });
        Assert.Throws<InvalidOperationException>(() => whitespaceRpc.Initialize());

        // Initialising changes nothing the plugin declares about itself.
        Assert.Equal("Tetitulz.Plugins.EthereumSign", plugin.Name);
        Assert.Equal(new Version(1, 0, 0), plugin.Version);
    }

    // ── Metadata ─────────────────────────────────────────────────────────────

    [Fact]
    public void Name_IsExpected()
    {
        var plugin = MakePlugin();
        Assert.Equal("Tetitulz.Plugins.EthereumSign", plugin.Name);
    }

    [Fact]
    public void Version_IsOneZeroZero()
    {
        var plugin = MakePlugin();
        Assert.Equal(new Version(1, 0, 0), plugin.Version);
    }

    // ── Pass-through hooks (ComputeDigest / WrapSignature / OnBeforeSign) ─────

    [Fact]
    public void ComputeDigest_ReturnsNull_DefersToPki()
    {
        var result = MakePlugin().ComputeDigest("SHA-256", new byte[] { 1, 2, 3 });
        Assert.Null(result);
    }

    [Fact]
    public void WrapSignature_ReturnsNull_DeferencesToDefault()
    {
        var result = MakePlugin().WrapSignature(new byte[] { 0x30 }, new SignatureParameters());
        Assert.Null(result);
    }

    [Fact]
    public void OnBeforeSign_DoesNotModifyParameters()
    {
        var parameters = new SignatureParameters
        {
            Reason = "original",
            TimestampAuthorityUrl = null
        };
        MakePlugin().OnBeforeSign(parameters);
        Assert.Equal("original", parameters.Reason);
        Assert.Null(parameters.TimestampAuthorityUrl);
    }

    // ── OnAfterSign edge-cases (no network required) ─────────────────────────

    [Fact]
    public void OnAfterSign_NullBytes_ReturnsNull()
    {
        var result = MakePlugin().OnAfterSign(null!, new SignatureParameters());
        Assert.Null(result);
    }

    [Fact]
    public void OnAfterSign_EmptyBytes_ReturnsNull()
    {
        var result = MakePlugin().OnAfterSign(Array.Empty<byte>(), new SignatureParameters());
        Assert.Null(result);
    }

    /// <summary>
    /// An invalid <c>PrivateKey</c> — non-whitespace, so <c>Initialize</c> passes, but not valid hex —
    /// makes <c>new Account(…)</c> throw a <c>FormatException</c> deep in the anchoring chain, outside
    /// <c>RecordHashAsync</c>'s own try. That is neither a <c>PdfSigningException</c> nor an
    /// <c>OperationCanceledException</c>, so it used to escape the hook and fault the whole
    /// <c>PdfOperations.Sign</c> — destroying the PKI signature already produced. The hook now records
    /// it and passes the document through unchanged (<c>null</c>), honouring its documented contract.
    /// </summary>
    [Fact]
    public void OnAfterSign_InvalidPrivateKey_RecordsError_DoesNotDestroyTheSignature()
    {
        var plugin = new EthereumSignPlugin(new BlockchainParameters
        {
            RpcUrl = "http://localhost:8545",
            PrivateKey = "0xZZ",   // non-whitespace (Initialize passes), not valid hex
        });

        // Must NOT throw: an anchoring failure of any kind degrades to "signed but not notarized".
        byte[]? result = plugin.OnAfterSign(new byte[] { 0x25, 0x50, 0x44, 0x46 }, new SignatureParameters());

        Assert.Null(result);                                   // the signed document passes through unchanged
        Assert.NotNull(EthereumSignPlugin.LastAnchorError);    // the failure was recorded, not thrown
    }

    // ── PdfPluginHost integration ─────────────────────────────────────────────

    [Fact]
    public void GetPlugins_AfterRegister_FindsEthereumPlugin()
    {
        var host = new TestPluginHost();
        var plugin = MakePlugin();
        host.RegisterDirect(plugin);
        var found = host.GetPlugins<EthereumSignPlugin>();
        Assert.Contains(plugin, found);
    }

    /// <summary>
    /// This plugin owns no connection or handle, so <c>Shutdown</c> must leave the instance
    /// <b>reusable</b> — a host that stops and restarts it gets the same plugin back, still
    /// accepting its configuration and still deferring the PKI hooks.
    ///
    /// <para>That is the observable state change (or rather, the observable absence of one) that
    /// "Shutdown did not throw" could not distinguish from a Shutdown which quietly disposed the
    /// plugin's parameters.</para>
    /// </summary>
    [Fact]
    public void Shutdown_LeavesThePluginReusable()
    {
        var plugin = MakePlugin();
        plugin.Initialize();
        plugin.Shutdown();

        plugin.Initialize();   // still valid after a shutdown

        Assert.Null(plugin.ComputeDigest("SHA-256", new byte[] { 1, 2, 3 }));
        Assert.Null(plugin.WrapSignature(new byte[] { 0x30 }, new SignatureParameters()));
        Assert.Equal("Tetitulz.Plugins.EthereumSign", plugin.Name);
    }

    // ── EmbedBlockchainProof round-trip ──────────────────────────────────────

    [Fact]
    public void EmbedBlockchainProof_AddsIncrementalUpdate()
    {
        byte[] minimalPdf = BuildMinimalPdf();
        var proof = new BlockchainProof
        {
            TransactionHash = "0xabc123",
            BlockNumber     = 42,
            Timestamp       = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            DocumentHash    = "deadbeef",
            Network         = "sepolia",
            SignerAddress   = "0x1234"
        };

        byte[] result = PdfBlockchainSigner.EmbedBlockchainProof(minimalPdf, proof);

        Assert.NotNull(result);
        Assert.True(result.Length > minimalPdf.Length,
            "Result should be longer than input (incremental update appended).");

        string text = Encoding.Latin1.GetString(result);
        Assert.Contains("BlockchainProof", text);
        Assert.Contains("0xabc123",        text);
        Assert.Contains("sepolia",         text);
        Assert.Contains("0x1234",          text);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static EthereumSignPlugin MakePlugin() =>
        new EthereumSignPlugin(new BlockchainParameters
        {
            RpcUrl     = "http://localhost:8545",
            PrivateKey = "0x" + new string('a', 64),
            ChainId    = 31337,
        });

    /// <summary>
    /// Builds the smallest valid PDF that <see cref="PdfBlockchainSigner.EmbedBlockchainProof"/>
    /// can parse: 1-page PDF/1.4 with a real catalog, pages tree, and a single empty page.
    /// </summary>
    private static byte[] BuildMinimalPdf()
    {
        var sb = new StringBuilder();
        sb.AppendLine("%PDF-1.4");
        sb.AppendLine("1 0 obj");
        sb.AppendLine("<< /Type /Catalog /Pages 2 0 R >>");
        sb.AppendLine("endobj");
        sb.AppendLine("2 0 obj");
        sb.AppendLine("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        sb.AppendLine("endobj");
        sb.AppendLine("3 0 obj");
        sb.AppendLine("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>");
        sb.AppendLine("endobj");

        long xrefOffset = Encoding.Latin1.GetByteCount(sb.ToString());

        sb.AppendLine("xref");
        sb.AppendLine("0 4");
        sb.AppendLine("0000000000 65535 f ");
        sb.AppendLine("0000000009 00000 n ");
        sb.AppendLine("0000000062 00000 n ");
        sb.AppendLine("0000000119 00000 n ");
        sb.AppendLine("trailer");
        sb.AppendLine("<< /Size 4 /Root 1 0 R >>");
        sb.AppendLine("startxref");
        sb.AppendLine(xrefOffset.ToString());
        sb.AppendLine("%%EOF");

        return Encoding.Latin1.GetBytes(sb.ToString());
    }

    /// <summary>Thin wrapper to expose internal Register for tests without touching the singleton.</summary>
    private sealed class TestPluginHost
    {
        private readonly List<IPdfPlugin> _plugins = new();

        public void RegisterDirect(IPdfPlugin plugin)
        {
            plugin.Initialize();
            _plugins.Add(plugin);
        }

        public IReadOnlyList<T> GetPlugins<T>() where T : IPdfPlugin
            => _plugins.OfType<T>().ToList();
    }
}
