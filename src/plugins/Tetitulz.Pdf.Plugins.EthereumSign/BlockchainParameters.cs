// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Plugins.EthereumSign
{
    /// <summary>
    /// Parameters for Ethereum blockchain notarization.
    /// </summary>
    public sealed class BlockchainParameters
    {
        /// <summary>Ethereum JSON-RPC URL (e.g. "http://localhost:8545" or Infura/Alchemy URL).</summary>
        public string RpcUrl { get; set; }

        /// <summary>
        /// Private key of the Ethereum account (hex string, with or without 0x prefix).
        /// <para>
        /// SECURITY: a .NET <c>string</c> is immutable and cannot be zeroed, so a key
        /// stored here lingers in the managed heap (and any crash/swap dump) for the
        /// object's lifetime. Do NOT hard-code it or hold it on a long-lived singleton.
        /// Load it from a secrets manager / key vault immediately before signing,
        /// populate this only for the duration of the <c>RecordHashAsync</c> call, and
        /// drop the reference afterwards. Required only for signing, never for verification.
        /// </para>
        /// </summary>
        public string PrivateKey { get; set; }

        /// <summary>
        /// For verification: the address an anchor is accepted from, or null to accept whichever
        /// address the document's own proof names.
        /// </summary>
        /// <remarks>
        /// A proof carries its transaction and its signer address inside the document, and anyone
        /// can anchor any document's hash — on a test network, for nothing. With no address stated
        /// here, a valid result says the document was anchored and unchanged since; it does not say
        /// by whom, and someone who altered the document and anchored it again passes. A caller
        /// who relies on <i>who</i> anchored it states that address here.
        /// </remarks>
        public string ExpectedSignerAddress { get; set; }

        /// <summary>Chain ID (1=mainnet, 11155111=Sepolia, 31337=Hardhat local). Default: Sepolia.</summary>
        public int ChainId { get; set; } = 11155111;

        /// <summary>Gas limit for the transaction. Default: 50000.</summary>
        public long GasLimit { get; set; } = 50000;

        /// <summary>
        /// Network name for display purposes (e.g. "mainnet", "sepolia", "localhost").
        /// If null, derived from ChainId.
        /// </summary>
        public string NetworkName { get; set; }

        public string GetNetworkName()
        {
            if (!string.IsNullOrEmpty(NetworkName)) return NetworkName;
            switch (ChainId)
            {
                case 1: return "mainnet";
                case 11155111: return "sepolia";
                case 31337: return "hardhat";
                case 1337: return "ganache";
                default: return $"chain-{ChainId}";
            }
        }
    }
}
