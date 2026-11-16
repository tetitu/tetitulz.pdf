// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

namespace Tetitulz.Pdf.Plugins.EthereumSign
{
    /// <summary>
    /// Result of verifying a blockchain-notarized PDF document.
    /// </summary>
    public sealed class BlockchainVerificationResult
    {
        /// <summary>Overall validity: the transaction was found, the document hash
        /// (re-computed from the current document, not trusted from the PDF) matches
        /// the on-chain data, the on-chain transaction sender matches the proof's
        /// claimed signer, AND the notarized revision is still the last revision in the
        /// file. All four are required — a matching hash alone is forgeable (anyone can
        /// broadcast a tx with arbitrary data), a hash read from the PDF rather than
        /// re-computed proves nothing about the current bytes, and a hash over a PREFIX
        /// of the file says nothing at all about bytes appended after it.
        /// <para><b>Valid says the document was anchored, not by whom</b>, unless the caller named
        /// the signer it accepts (<see cref="BlockchainParameters.ExpectedSignerAddress"/>): the
        /// proof carries its own signer address, and anyone can anchor a document they
        /// altered.</para></summary>
        public bool IsValid => TransactionFound && HashMatches && SignerMatches && IsLastRevision;

        /// <summary>Whether the transaction is in a block of the chain the endpoint serves. A
        /// transaction the endpoint knows but that is still pending is not.</summary>
        public bool TransactionFound { get; set; }

        /// <summary>Whether the document hash matches the data stored on-chain.</summary>
        public bool HashMatches { get; set; }

        /// <summary>Whether the on-chain transaction sender (tx.From) equals the proof's claimed
        /// SignerAddress — which stops a forged proof that references someone else's transaction —
        /// and, when the caller stated one, the address the caller accepts
        /// (<see cref="BlockchainParameters.ExpectedSignerAddress"/>).</summary>
        public bool SignerMatches { get; set; }

        /// <summary>The actual transaction sender observed on-chain (tx.From).</summary>
        public string SignerOnChain { get; set; }

        /// <summary>
        /// Whether the notarized revision is still the <b>last</b> revision in the file.
        ///
        /// <para>The proof covers the document prefix named by <c>/HashedLength</c> — the bytes as
        /// they were when the hash went on-chain — and says nothing whatever about bytes appended
        /// afterwards. A PDF is append-only editable: one incremental update can supersede a page's
        /// content stream so the document renders entirely differently, while every byte the proof
        /// covers stays untouched and re-hashes to exactly the value on the chain. Without this
        /// flag <see cref="TransactionFound"/>, <see cref="HashMatches"/> and
        /// <see cref="SignerMatches"/> all held and <see cref="IsValid"/> was <c>true</c> for a
        /// document whose visible content was the attacker's.</para>
        ///
        /// <para><see cref="HashMatches"/> stays meaningful when this is false, and the pair is
        /// what tells the two attacks apart: hash true + this false is "the notarized bytes are
        /// intact and something was added after them"; hash false is "the notarized bytes
        /// themselves were altered".</para>
        /// </summary>
        public bool IsLastRevision { get; set; }

        /// <summary>Non-fatal advisory about a verification that still ran to completion.
        /// It is never used to report that integrity could not be re-computed: that case
        /// is fatal and is reported through <see cref="Error"/> with no chain lookup, so
        /// that a caller reading only <see cref="IsValid"/> cannot miss it.</summary>
        public string Warning { get; set; }

        /// <summary>The transaction hash that was checked.</summary>
        public string TransactionHash { get; set; }

        /// <summary>Block number of the transaction.</summary>
        public long BlockNumber { get; set; }

        /// <summary>The hash stored on-chain.</summary>
        public string OnChainHash { get; set; }

        /// <summary>The computed hash of the current document.</summary>
        public string ComputedHash { get; set; }

        /// <summary>Error message if verification failed.</summary>
        public string Error { get; set; }
    }
}
