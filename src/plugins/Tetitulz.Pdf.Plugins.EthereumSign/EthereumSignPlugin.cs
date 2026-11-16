// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Plugins;

namespace Tetitulz.Pdf.Plugins.EthereumSign
{
    /// <summary>
    /// <see cref="ISignPlugin"/> that records the SHA-256 hash of a signed PDF on
    /// the Ethereum blockchain and embeds the resulting <see cref="BlockchainProof"/>
    /// back into the document as an incremental update.
    ///
    /// <para>
    /// The plugin participates only in <see cref="OnAfterSign"/>: the PDF is first
    /// PKI-signed by the standard pipeline, then this plugin anchors the hash on-chain
    /// and appends the proof into the catalog.  The PKI signature itself is unmodified.
    /// </para>
    ///
    /// <para>
    /// Registration (once at app startup):
    /// <code>
    ///   PdfPluginHost.Instance.Register(new EthereumSignPlugin(new BlockchainParameters
    ///   {
    ///       RpcUrl     = "https://sepolia.infura.io/v3/YOUR_PROJECT_ID",
    ///       PrivateKey = "0xYOUR_PRIVATE_KEY",
    ///       ChainId    = 11155111,    // Sepolia testnet
    ///   }));
    /// </code>
    ///
    /// After registration every call to <c>PdfOperations.Sign(...)</c> will automatically
    /// anchor the signed document on Ethereum.
    /// </para>
    /// </summary>
    public sealed class EthereumSignPlugin : ISignPlugin
    {
        private readonly BlockchainParameters _blockchainParams;

        /// <param name="blockchainParams">
        /// Ethereum connection parameters.  Must include a valid <see cref="BlockchainParameters.RpcUrl"/>
        /// and <see cref="BlockchainParameters.PrivateKey"/>.
        /// </param>
        public EthereumSignPlugin(BlockchainParameters blockchainParams)
        {
            _blockchainParams = blockchainParams
                ?? throw new ArgumentNullException(nameof(blockchainParams));
        }

        public string Name        => "Tetitulz.Plugins.EthereumSign";
        public string Description => "Anchors signed PDF document hashes on the Ethereum blockchain.";
        public Version Version    => new Version(1, 0, 0);

        public void Initialize()
        {
            if (string.IsNullOrWhiteSpace(_blockchainParams.RpcUrl))
                throw new InvalidOperationException(
                    "EthereumSignPlugin: BlockchainParameters.RpcUrl must be set.");
            if (string.IsNullOrWhiteSpace(_blockchainParams.PrivateKey))
                throw new InvalidOperationException(
                    "EthereumSignPlugin: BlockchainParameters.PrivateKey must be set.");
        }

        /// <summary>
        /// Releases the verification clients <see cref="BlockchainSignatureProvider"/> caches.
        /// </summary>
        /// <remarks>
        /// A host that unregisters the plugin releases every cached Web3, and its HttpClient
        /// connection pool, here.
        /// </remarks>
        public void Shutdown() => BlockchainSignatureProvider.ClearClientCache();

        // ── ISignPlugin ──────────────────────────────────────────────────────

        /// <summary>No parameter changes needed before signing.</summary>
        public void OnBeforeSign(SignatureParameters parameters) { }

        /// <summary>Uses the default PKI digest — return null to pass through.</summary>
        public byte[]? ComputeDigest(string algorithm, byte[] bytesToSign) => null;

        /// <summary>Uses the default CMS blob — return null to pass through.</summary>
        public byte[]? WrapSignature(byte[] cmsBlob, SignatureParameters parameters) => null;

        [ThreadStatic] private static string? _lastAnchorError;

        /// <summary>
        /// Why the last <see cref="OnAfterSign"/> on this thread produced no proof, or
        /// <see langword="null"/> when it produced one (or was not called).
        ///
        /// <para><b>Check this after signing if the anchor is not optional to you.</b> A signing run
        /// whose blockchain step failed returns a perfectly valid PKI-signed PDF that simply carries
        /// no <c>/BlockchainProof</c>, and nothing else distinguishes it from a document the caller
        /// never asked to notarize.</para>
        ///
        /// <para>Per-thread, as CLAUDE.md requires of every <c>Last*</c> slot, and reset at the
        /// start of every <see cref="OnAfterSign"/> so a value read after one call can never belong
        /// to an earlier one.</para>
        /// </summary>
        public static string? LastAnchorError => _lastAnchorError;

        /// <summary>
        /// Computes SHA-256 of the PKI-signed PDF, records the hash on Ethereum,
        /// and embeds the <see cref="BlockchainProof"/> as an incremental update.
        /// Returns the augmented PDF bytes.
        /// </summary>
        /// <remarks>
        /// <b>An anchoring failure degrades to "signed but not notarized" and is reported on
        /// <see cref="LastAnchorError"/>; it does not destroy the signature.</b> Letting
        /// <see cref="PdfSigningException"/> escape would fail the whole <c>PdfOperations.Sign</c>
        /// call, so an RPC endpoint that is merely unreachable would throw away a PKI signature
        /// already computed. Returning <see langword="null"/> is this contract's documented "pass
        /// through unchanged", the same answer <see cref="ComputeDigest"/> and
        /// <see cref="WrapSignature"/> give.
        /// </remarks>
        public byte[]? OnAfterSign(byte[] signedPdfBytes, SignatureParameters parameters)
        {
            _lastAnchorError = null;

            if (signedPdfBytes == null || signedPdfBytes.Length == 0)
                return null;

            try
            {
                // Run the async operation synchronously — plugins are called from a sync context.
                return AnchorAndEmbed(signedPdfBytes);
            }
            catch (PdfSigningException ex)
            {
                _lastAnchorError = ex.Message;
                return null;
            }
            catch (OperationCanceledException ex)
            {
                _lastAnchorError = "Blockchain anchoring was cancelled: " + ex.Message;
                return null;
            }
            catch (Exception ex)
            {
                // The contract above holds for EVERY anchoring failure, not only a PdfSigningException.
                // An invalid PrivateKey (non-whitespace but not valid hex) reaches new Account(...) outside
                // RecordHashAsync's own try, so Nethereum's FormatException/ArgumentException would otherwise
                // escape the hook and fault the whole PdfOperations.Sign — throwing away a PKI signature that
                // was already produced. Record it and pass the document through unchanged instead.
                _lastAnchorError = "Blockchain anchoring failed: " + ex.Message;
                return null;
            }
        }

        // ── Internals ────────────────────────────────────────────────────────

        private byte[] AnchorAndEmbed(byte[] signedPdfBytes)
        {
            // Compute document hash
            byte[] hash;
            using (var sha = SHA256.Create())
                hash = sha.ComputeHash(signedPdfBytes);

            // Record on Ethereum (sync-over-async: safe here because no sync context in .NET CLR)
            var provider = new BlockchainSignatureProvider();
            BlockchainProof proof = Task.Run(
                () => provider.RecordHashAsync(hash, _blockchainParams)).GetAwaiter().GetResult();

            // Embed proof as incremental update (reuses existing PdfBlockchainSigner helper)
            return PdfBlockchainSigner.EmbedBlockchainProof(signedPdfBytes, proof);
        }
    }
}
