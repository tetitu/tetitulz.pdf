// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Plugins.EthereumSign.Tests.Helpers;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Plugins.EthereumSign.Tests.Signing;

/// <summary>
/// Anchoring on a public chain is the step most likely to fail — an unreachable endpoint, a
/// transaction that never mines, a revert — and it is the <i>last</i> step, after a PKI signature
/// has already been computed successfully. Two places used to throw that work away.
///
/// <para>None of these tests reaches the network: each failure is reached from an argument the
/// provider validates before it opens a socket.</para>
/// </summary>
public class AnchoringFailureIsNotDestructiveTests
{
    // Publicly published Hardhat/Ganache development key — well-known, funds-free, and never used
    // here to sign anything: every test below fails before it reaches the chain.
    private const string DevKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";

    /// <summary>
    /// <c>SignAsync(inputPath, outputPath, …)</c> opened the destination with <c>File.Create</c>
    /// before doing any work, which truncates it the moment the call starts. Any failure after that
    /// — and the blockchain round trip is one — left the caller with a zero-byte file where their
    /// previous output had been.
    /// </summary>
    [Fact]
    public async Task A_failed_run_leaves_an_existing_output_file_untouched()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tetitulz-anchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string inputPath = Path.Combine(dir, "in.pdf");
            string outputPath = Path.Combine(dir, "out.pdf");

            File.WriteAllBytes(inputPath, MinimalPdfBuilder.CreateMinimalPdf("input"));

            // The caller's previous output. Its survival is the whole assertion.
            byte[] previous = MinimalPdfBuilder.CreateMinimalPdf("PREVIOUS RUN");
            File.WriteAllBytes(outputPath, previous);

            // Signature parameters with no certificate: PdfSigner refuses, so the run fails at the
            // same place in the sequence a dead RPC endpoint does — after the destination would
            // have been opened, before anything was written to it.
            await Assert.ThrowsAnyAsync<Exception>(() => new PdfBlockchainSigner().SignAsync(
                inputPath, outputPath,
                new SignatureParameters(),
                new BlockchainParameters { RpcUrl = "http://127.0.0.1:8545", PrivateKey = DevKey },
                CancellationToken.None));

            Assert.True(File.Exists(outputPath), "the destination file was deleted by a failed run.");
            Assert.Equal(previous, File.ReadAllBytes(outputPath));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// The plugin hook used to let <see cref="PdfSigningException"/> escape, which fails the whole
    /// <c>PdfOperations.Sign</c> call — so an endpoint that was merely misconfigured discarded a
    /// PKI signature that had already succeeded. It now degrades to "signed but not notarized" and
    /// says why.
    /// </summary>
    [Fact]
    public void An_anchoring_failure_passes_the_signed_bytes_through_and_reports_the_reason()
    {
        // Rejected by ValidateRpcUrl before any socket is opened.
        var plugin = new EthereumSignPlugin(new BlockchainParameters
        {
            RpcUrl = "ftp://not-an-rpc-endpoint/",
            PrivateKey = DevKey,
            ChainId = 31337,
        });

        byte[] signed = MinimalPdfBuilder.CreateMinimalPdf("already PKI-signed");

        byte[] result = plugin.OnAfterSign(signed, new SignatureParameters());

        // null is this contract's documented "pass through unchanged" — the same answer
        // ComputeDigest and WrapSignature give — so the caller keeps the signed document.
        Assert.Null(result);

        // And it is not silent: a caller for whom the anchor is not optional can tell this run from
        // one that was never asked to notarize.
        Assert.NotNull(EthereumSignPlugin.LastAnchorError);
        Assert.Contains("http", EthereumSignPlugin.LastAnchorError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A <c>Last*</c> slot a caller reads after an operation must be reset at the <b>start</b> of
    /// that operation, on every path — CLAUDE.md's rule, written after a slot that was per-thread
    /// and still reported the previous document's verdict.
    /// </summary>
    [Fact]
    public void LastAnchorError_is_cleared_at_the_start_of_every_call()
    {
        var failing = new EthereumSignPlugin(new BlockchainParameters
        {
            RpcUrl = "ftp://not-an-rpc-endpoint/",
            PrivateKey = DevKey,
        });

        failing.OnAfterSign(MinimalPdfBuilder.CreateMinimalPdf("x"), new SignatureParameters());
        Assert.NotNull(EthereumSignPlugin.LastAnchorError);

        // A call that does nothing at all must not leave the previous call's reason standing.
        failing.OnAfterSign(Array.Empty<byte>(), new SignatureParameters());
        Assert.Null(EthereumSignPlugin.LastAnchorError);
    }

    /// <summary>
    /// The cancellation token used to reach nothing but <c>Task.Delay</c> inside the receipt-poll
    /// loop, so the very first RPC — <c>SendTransaction</c> against a black-holing endpoint — ran
    /// to Nethereum's 100-second HttpClient timeout before the token was consulted once.
    ///
    /// <para>The private key here is deliberately <b>not</b> a valid key: constructing the
    /// <c>Account</c> is the step immediately after the cancellation check, so if the check moved
    /// or vanished this test would see that constructor's failure instead of
    /// <see cref="OperationCanceledException"/>. That is what pins the check's position rather than
    /// merely its existence.</para>
    /// </summary>
    [Fact]
    public async Task Cancellation_is_observed_before_anything_reaches_the_network()
    {
        var provider = new BlockchainSignatureProvider();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.RecordHashAsync(
            new byte[32],
            new BlockchainParameters
            {
                RpcUrl = "http://127.0.0.1:8545",
                PrivateKey = "0xnotahexprivatekeyatall",
                ChainId = 31337,
            },
            new CancellationToken(canceled: true)));
    }

    /// <summary>
    /// The counterpart that keeps the check narrow: argument validation still runs first, so a
    /// cancelled token cannot mask a missing endpoint. A guard placed above the validation would
    /// report "cancelled" for a call that was misconfigured.
    /// </summary>
    [Fact]
    public async Task A_cancelled_token_does_not_mask_a_missing_RPC_url()
    {
        var provider = new BlockchainSignatureProvider();

        await Assert.ThrowsAsync<PdfSigningException>(() => provider.RecordHashAsync(
            new byte[32],
            new BlockchainParameters { RpcUrl = null, PrivateKey = DevKey },
            new CancellationToken(canceled: true)));
    }
}
