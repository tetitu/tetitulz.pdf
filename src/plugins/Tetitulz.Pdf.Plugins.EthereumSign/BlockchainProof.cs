// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Plugins.EthereumSign
{
    /// <summary>
    /// Blockchain notarization proof embedded in a PDF document.
    /// Contains the Ethereum transaction details that recorded the document hash on-chain.
    /// </summary>
    public sealed class BlockchainProof
    {
        /// <summary>Ethereum transaction hash (e.g. "0xabc123...").</summary>
        public string TransactionHash { get; set; }

        /// <summary>Block number in which the transaction was mined.</summary>
        public long BlockNumber { get; set; }

        /// <summary>Timestamp of the block.</summary>
        public DateTime Timestamp { get; set; }

        /// <summary>SHA-256 hash of the document at the time of notarization (hex string).</summary>
        public string DocumentHash { get; set; }

        /// <summary>Ethereum network name (e.g. "mainnet", "sepolia").</summary>
        public string Network { get; set; }

        /// <summary>Ethereum address that submitted the transaction.</summary>
        public string SignerAddress { get; set; }
    }
}
