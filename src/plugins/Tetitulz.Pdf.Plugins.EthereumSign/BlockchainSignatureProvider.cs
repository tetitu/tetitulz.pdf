// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.RPC.Eth.DTOs;
using Nethereum.Web3;
using Nethereum.Web3.Accounts;
using Tetitulz.Pdf.Core;

namespace Tetitulz.Pdf.Plugins.EthereumSign
{
    /// <summary>
    /// Handles Ethereum blockchain interaction for PDF notarization.
    /// Records document hashes on-chain and verifies them.
    /// </summary>
    public sealed class BlockchainSignatureProvider
    {
        // Reused Web3 instances for the (account-less) verification path so repeated verifications
        // don't each allocate a fresh HttpClient (socket-exhaustion antipattern). The signing path
        // is NOT cached — its Web3 carries the private-key account and must not outlive the call.
        //
        // Three properties keep the cache from leaking at a coarser grain:
        //
        //  1. It is bounded (MaxCachedEndpoints): reaching the bound clears the cache, so a
        //     multi-tenant service does not grow one Web3 — one HttpClient connection pool — per
        //     distinct RPC URL for the process lifetime. A stale entry costs one extra Web3.
        //  2. The key is the whole URL, not a collapsed origin: see EndpointKey.
        //  3. The value is a Lazy<Web3> with ExecutionAndPublication: GetOrAdd's value factory is
        //     not atomic, and concurrent first calls for one URL would each construct a Web3 and
        //     drop all but one UNDISPOSED.
        private static readonly ConcurrentDictionary<string, Lazy<Web3>> _verifyWeb3 =
            new ConcurrentDictionary<string, Lazy<Web3>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Distinct endpoints kept alive. Small on purpose: the cache exists to stop one client per
        /// call, not to be a registry.
        /// </summary>
        private const int MaxCachedEndpoints = 16;

        /// <summary>
        /// Drops every cached verification client. Called by
        /// <c>EthereumSignPlugin.Shutdown</c>, so a host that unregisters the plugin releases the
        /// connection pools instead of holding them for the process lifetime.
        /// </summary>
        public static void ClearClientCache() => _verifyWeb3.Clear();

        /// <summary>
        /// The cache key: the <b>whole</b> RPC URL.
        /// </summary>
        /// <remarks>
        /// <para><b>Do not collapse this to scheme + host + port.</b> The origin determines the
        /// connection pool, but Infura and Alchemy carry a per-tenant API key <i>in the path</i>, so
        /// <c>https://mainnet.infura.io/v3/AAA</c> and <c>…/v3/BBB</c> share an origin while being
        /// different credentials. Under a collapsed key both tenants would get the one
        /// <see cref="Web3"/> built from whichever URL arrived first, and tenant B's verification
        /// would be issued against tenant A's project.</para>
        ///
        /// <para>Growth is bounded by <see cref="MaxCachedEndpoints"/> instead. A bound costs a
        /// rebuild; a wrong key costs correctness.</para>
        /// </remarks>
        private static string EndpointKey(string rpcUrl) => rpcUrl ?? string.Empty;

        internal static Web3 GetVerifyClient(string rpcUrl)
        {
            string key = EndpointKey(rpcUrl);

            // Bounded: past the cap, start over rather than grow. Clearing is safe — a dropped
            // Web3 is collectable, and the next call rebuilds one.
            if (_verifyWeb3.Count >= MaxCachedEndpoints && !_verifyWeb3.ContainsKey(key))
                _verifyWeb3.Clear();

            return _verifyWeb3.GetOrAdd(key,
                _ => new Lazy<Web3>(() => new Web3(rpcUrl), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
        }

        /// <summary>Endpoints currently cached. For tests and diagnostics.</summary>
        internal static int CachedClientCount => _verifyWeb3.Count;

        /// <summary>
        /// How many times <see cref="RecordHashAsync"/> polls for the transaction receipt, and how
        /// long it waits between polls — two minutes in total.
        /// </summary>
        /// <remarks>
        /// Named constants, private: a caller who needs a different budget has
        /// <c>cancellationToken</c>, which interrupts the wait, and a knob on
        /// <c>BlockchainParameters</c> would put a value on a shipping package's API that no offline
        /// test could exercise.
        /// </remarks>
        private const int ReceiptPollAttempts = 120;
        private const int ReceiptPollIntervalMs = 1000;

        /// <summary>
        /// Records a document hash on the Ethereum blockchain by sending a transaction
        /// with the hash as the data payload, and waits for it to be mined.
        /// </summary>
        /// <param name="documentHash">The digest to anchor. Sent as the transaction's data payload.</param>
        /// <param name="parameters">
        /// Endpoint, private key, chain id and gas limit. <c>RpcUrl</c> must be an absolute
        /// http/https URL and <c>PrivateKey</c> must be set.
        /// </param>
        /// <param name="cancellationToken">
        /// Observed before the first request and around every RPC round trip, not only between
        /// receipt polls. A request already in flight is abandoned rather than aborted — Nethereum
        /// owns the socket — so the endpoint may still see it; what the token buys is that the
        /// caller stops waiting.
        /// </param>
        /// <returns>The proof: transaction hash, block number, block timestamp and signer address.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="documentHash"/> or <paramref name="parameters"/> is null.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.</exception>
        /// <exception cref="PdfSigningException">
        /// The RPC URL or private key is missing, the URL is not absolute http/https, the
        /// transaction could not be sent, it was not mined within the poll budget, or it reverted
        /// on-chain.
        /// </exception>
        public async Task<BlockchainProof> RecordHashAsync(byte[] documentHash, BlockchainParameters parameters,
            CancellationToken cancellationToken = default)
        {
            if (documentHash == null) throw new ArgumentNullException(nameof(documentHash));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (string.IsNullOrEmpty(parameters.RpcUrl))
                throw new PdfSigningException("Ethereum RPC URL is required.");
            if (string.IsNullOrEmpty(parameters.PrivateKey))
                throw new PdfSigningException("Ethereum private key is required.");
            ValidateRpcUrl(parameters.RpcUrl);

            // Before anything reaches the network, so a caller whose request is already abandoned
            // does not wait out SendTransaction against a black-holing endpoint — Nethereum's
            // HttpClient default timeout is 100 seconds.
            cancellationToken.ThrowIfCancellationRequested();

            var account = new Account(parameters.PrivateKey, parameters.ChainId);
            var web3 = new Web3(account, parameters.RpcUrl);

            string hexHash = "0x" + documentHash.ToHex();

            var txInput = new TransactionInput
            {
                From = account.Address,
                To = account.Address,
                Value = new Nethereum.Hex.HexTypes.HexBigInteger(BigInteger.Zero),
                Data = hexHash,
                Gas = new Nethereum.Hex.HexTypes.HexBigInteger(parameters.GasLimit),
            };

            // WaitAsync rather than a Nethereum cancellation overload: the RPC round trips take no
            // token, so only WaitAsync lets cancellation interrupt a hung endpoint. It abandons the
            // wait rather than the request — the socket is Nethereum's and the endpoint may still see
            // the call — which is the honest limit of what can be done from here, and the difference
            // between the caller waiting 100 s and not waiting at all.
            string txHash;
            try
            {
                txHash = await web3.Eth.Transactions.SendTransaction.SendRequestAsync(txInput)
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the caller's decision, not a signing failure; wrapping it in a
                // PdfSigningException would make "I stopped waiting" indistinguishable from "the
                // chain rejected this".
                throw;
            }
            catch (Exception ex)
            {
                throw new PdfSigningException($"Failed to send Ethereum transaction: {ex.Message}", ex);
            }

            TransactionReceipt receipt;
            try
            {
                receipt = await web3.Eth.Transactions.GetTransactionReceipt
                    .SendRequestAsync(txHash).WaitAsync(cancellationToken).ConfigureAwait(false);

                int attempts = 0;
                while (receipt == null && attempts < ReceiptPollAttempts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await Task.Delay(ReceiptPollIntervalMs, cancellationToken).ConfigureAwait(false);
                    receipt = await web3.Eth.Transactions.GetTransactionReceipt
                        .SendRequestAsync(txHash).WaitAsync(cancellationToken).ConfigureAwait(false);
                    attempts++;
                }

                if (receipt == null)
                    throw new PdfSigningException("Transaction was not mined within timeout.");

                if (receipt.Status?.Value == 0)
                    throw new PdfSigningException("Transaction reverted on-chain.");
            }
            catch (PdfSigningException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new PdfSigningException($"Failed waiting for transaction receipt: {ex.Message}", ex);
            }

            DateTime blockTime = DateTime.UtcNow;
            try
            {
                var block = await web3.Eth.Blocks.GetBlockWithTransactionsByNumber
                    .SendRequestAsync(receipt.BlockNumber).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (block != null)
                {
                    blockTime = DateTimeOffset.FromUnixTimeSeconds((long)block.Timestamp.Value).UtcDateTime;
                }
            }
            catch
            {
                // Use current time as fallback — INCLUDING on cancellation, which is why this catch
                // stays blanket while the two above do not. The transaction is already mined by
                // now, so the proof exists on-chain; throwing here would discard a proof the caller
                // has already paid gas for because a nice-to-have timestamp lookup was interrupted.
            }

            return new BlockchainProof
            {
                TransactionHash = txHash,
                BlockNumber = (long)receipt.BlockNumber.Value,
                Timestamp = blockTime,
                DocumentHash = documentHash.ToHex(),
                Network = parameters.GetNetworkName(),
                SignerAddress = account.Address
            };
        }

        /// <summary>
        /// Verifies that a document hash exists on the Ethereum blockchain
        /// by fetching the transaction and comparing its data payload.
        /// </summary>
        public async Task<BlockchainVerificationResult> VerifyHashAsync(
            byte[] documentHash, BlockchainProof proof, BlockchainParameters parameters,
            CancellationToken cancellationToken = default)
        {
            if (proof == null) throw new ArgumentNullException(nameof(proof));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));
            if (string.IsNullOrEmpty(parameters.RpcUrl))
                throw new PdfSigningException("Ethereum RPC URL is required for verification.");
            ValidateRpcUrl(parameters.RpcUrl);

            var result = new BlockchainVerificationResult
            {
                TransactionHash = proof.TransactionHash,
                ComputedHash = documentHash?.ToHex()
            };

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var web3 = GetVerifyClient(parameters.RpcUrl);

                var tx = await web3.Eth.Transactions.GetTransactionByHash
                    .SendRequestAsync(proof.TransactionHash).ConfigureAwait(false);

                if (tx == null)
                {
                    result.Error = "Transaction not found on blockchain.";
                    return result;
                }

                Judge(result, tx.Input, tx.From, tx.BlockNumber?.Value, documentHash, proof,
                      parameters.ExpectedSignerAddress);
            }
            catch (Exception ex)
            {
                result.Error = $"Blockchain verification failed: {ex.Message}";
            }

            return result;
        }

        /// <summary>
        /// What a transaction the endpoint returned proves about a document: fills
        /// <paramref name="result"/> from the transaction's data, sender and block.
        /// </summary>
        /// <remarks>
        /// <para><b>A transaction that is not in a block is not on the chain.</b> An endpoint
        /// answers for a pending transaction too — one broadcast with a fee that will never be
        /// paid, or seen only by the node asked — with its data and sender and no block. It is
        /// reported as not found: nothing has been recorded.</para>
        /// <para><b>The proof names its own signer.</b> The transaction hash and the signer address
        /// both come out of the document, so "the sender is the signer the proof names" holds for
        /// whoever made the proof — anyone can anchor any hash. It stops a proof that points at
        /// someone else's transaction; it does not say who anchored the document. A caller who
        /// knows whose anchor to accept states it (<paramref name="expectedSigner"/>), and then the
        /// sender is held to that address and the proof's own claim is not what is believed.</para>
        /// </remarks>
        /// <param name="result">The result to fill.</param>
        /// <param name="input">The transaction's data, as the endpoint gives it (hexadecimal, with or without <c>0x</c>).</param>
        /// <param name="from">The transaction's sender.</param>
        /// <param name="blockNumber">The block the transaction is in, or null for a pending one.</param>
        /// <param name="documentHash">The digest computed from the document.</param>
        /// <param name="proof">The proof read from the document.</param>
        /// <param name="expectedSigner">The address the caller accepts an anchor from, or null to accept the proof's own.</param>
        internal static void Judge(
            BlockchainVerificationResult result, string input, string from, BigInteger? blockNumber,
            byte[] documentHash, BlockchainProof proof, string expectedSigner)
        {
            if (blockNumber == null || blockNumber.Value <= 0)
            {
                result.Error = "The transaction is known to the endpoint but is not in a block: nothing has been recorded on the chain.";
                return;
            }

            result.TransactionFound = true;
            result.BlockNumber = (long)blockNumber.Value;

            string onChainData = input;
            if (onChainData != null && onChainData.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                onChainData = onChainData.Substring(2);
            result.OnChainHash = onChainData;

            string expectedHash = documentHash?.ToHex();
            result.HashMatches = !string.IsNullOrEmpty(expectedHash)
                && string.Equals(onChainData, expectedHash, StringComparison.OrdinalIgnoreCase);

            result.SignerOnChain = from;
            string accepted = string.IsNullOrEmpty(expectedSigner) ? proof.SignerAddress : expectedSigner;
            result.SignerMatches = !string.IsNullOrEmpty(from)
                && !string.IsNullOrEmpty(accepted)
                && SameAddress(from, accepted)
                // The proof's own claim is still held to the chain: a proof naming one signer over
                // another's transaction is forged whichever address the caller accepts.
                && !string.IsNullOrEmpty(proof.SignerAddress) && SameAddress(from, proof.SignerAddress);

            if (!result.HashMatches)
                result.Error = "Document hash does not match on-chain data.";
            else if (!result.SignerMatches)
                result.Error = string.IsNullOrEmpty(expectedSigner)
                    ? $"On-chain transaction sender ({from}) does not match the proof's claimed signer ({proof.SignerAddress})."
                    : $"On-chain transaction sender ({from}) is not the expected signer ({expectedSigner}), or not the one the proof names ({proof.SignerAddress}).";
            else if (string.IsNullOrEmpty(expectedSigner))
                result.Warning = "The proof names its own signer (" + from + "): this shows that the document was " +
                                 "anchored, not by whom. Set BlockchainParameters.ExpectedSignerAddress to accept " +
                                 "an anchor from one address only.";
        }

        /// <summary>Two addresses compared as the 20 bytes they spell: case is a checksum, the <c>0x</c> optional.</summary>
        private static bool SameAddress(string a, string b)
        {
            static string Bare(string s) => s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s.Substring(2) : s;
            return string.Equals(Bare(a.Trim()), Bare(b.Trim()), StringComparison.OrdinalIgnoreCase);
        }

        // Rejects RPC endpoints that aren't http/https. The RpcUrl is fed straight
        // into Web3; an attacker-influenced value could point at file://, an
        // internal service (SSRF), or a malicious endpoint. Callers that need a
        // local node still use http://localhost:8545, which is allowed — pin the
        // host via configuration rather than accepting it from untrusted input.
        private static void ValidateRpcUrl(string rpcUrl)
        {
            if (!Uri.TryCreate(rpcUrl, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new PdfSigningException(
                    $"Ethereum RPC URL must be an absolute http/https URL: '{rpcUrl}'.");
            }
        }
    }
}
