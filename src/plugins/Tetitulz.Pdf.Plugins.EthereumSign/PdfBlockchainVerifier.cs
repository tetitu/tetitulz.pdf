// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Plugins.EthereumSign
{
    /// <summary>
    /// Verifies the blockchain proof embedded in a PDF document.
    /// Re-computes the document hash (of the PDF before the blockchain proof was added)
    /// and checks it against the on-chain data.
    /// </summary>
    public sealed class PdfBlockchainVerifier
    {
        /// <summary>
        /// Verifies the blockchain proof in a PDF document.
        /// </summary>
        public async Task<BlockchainVerificationResult> VerifyAsync(
            PdfDocument document, BlockchainParameters parameters,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            var proofObj = document.Catalog.Dictionary[PdfName.BlockchainProof];
            if (proofObj == null)
            {
                return new BlockchainVerificationResult
                {
                    Error = "No /BlockchainProof found in the PDF catalog."
                };
            }

            var proofDict = proofObj as PdfDictionary;
            if (proofDict == null)
            {
                proofDict = document.ResolveObject(proofObj) as PdfDictionary;
            }
            if (proofDict == null)
            {
                return new BlockchainVerificationResult
                {
                    Error = "/BlockchainProof is not a valid dictionary."
                };
            }

            var proof = new BlockchainProof
            {
                TransactionHash = GetStringValue(proofDict, "TxHash", document),
                DocumentHash = GetStringValue(proofDict, "DocumentHash", document),
                Network = GetStringValue(proofDict, "Network", document),
                SignerAddress = GetStringValue(proofDict, "SignerAddress", document),
                // Same family as the four above: it reaches the caller-visible result, so an
                // unresolved one reports block 0 for a proof that names a real block.
                BlockNumber = (Resolve(document, proofDict[new PdfName("BlockNumber")])
                               as PdfNumber)?.LongValue ?? 0,
            };

            if (string.IsNullOrEmpty(proof.TransactionHash))
            {
                return new BlockchainVerificationResult
                {
                    Error = "Missing /TxHash in /BlockchainProof dictionary."
                };
            }

            if (string.IsNullOrEmpty(proof.DocumentHash))
            {
                return new BlockchainVerificationResult
                {
                    Error = "Missing /DocumentHash in /BlockchainProof dictionary."
                };
            }

            // Re-compute the document hash from the actual bytes rather than trusting
            // /DocumentHash (which an attacker who tampered with the body would leave intact). The
            // notarized bytes are the document prefix BEFORE the proof incremental update; its
            // length is recorded in /HashedLength. The RE-COMPUTED hash goes to the on-chain check,
            // so a tampered document yields a hash that does not match the chain.
            //
            // There is deliberately NO fallback to the embedded /DocumentHash when /HashedLength is
            // missing, non-integer or out of range. Every proof this library writes carries it
            // (PdfBlockchainSigner.EmbedBlockchainProof), so its absence means the proof dictionary
            // was edited — and hashing the /DocumentHash string read out of that same edited file
            // would compare the chain against itself, matching by construction. The integrity
            // re-computation is the only path to the on-chain comparison: when it cannot run, refuse.
            // Resolved: GetInteger does not follow a reference, and an indirect /HashedLength read
            // as absent would reach the refusal below — a false accusation of tampering against a
            // document whose only unusual property is legal object placement.
            // Read as a long and kept one until the bounds below pass: an (int) cast would keep the
            // low 32 bits, so a /HashedLength of 4294967796 (2^32 + 500) would hash only the first
            // 500 bytes. A non-integer or absent value is 0, which the refusal below catches
            // (PdfNumber.IsInteger).
            var hashedLengthNumber = Resolve(document, proofDict[new PdfName("HashedLength")]) as PdfNumber;
            long hashedLength = hashedLengthNumber != null && hashedLengthNumber.IsInteger
                ? hashedLengthNumber.LongValue : 0;
            byte[] rawBytes = ReadAllBytes(document.BaseStream);
            if (hashedLength <= 0)
            {
                return new BlockchainVerificationResult
                {
                    Error = "/BlockchainProof has no usable /HashedLength, so document integrity " +
                            "cannot be independently re-computed. The proof dictionary was modified " +
                            "after signing; verification is refused rather than trusting the " +
                            "embedded /DocumentHash."
                };
            }
            if (rawBytes == null)
            {
                return new BlockchainVerificationResult
                {
                    Error = "The document bytes could not be read, so document integrity cannot be " +
                            "independently re-computed; verification is refused."
                };
            }
            if (hashedLength > rawBytes.Length)
            {
                return new BlockchainVerificationResult
                {
                    Error = $"/HashedLength ({hashedLength}) exceeds the document length " +
                            $"({rawBytes.Length}), so document integrity cannot be independently " +
                            "re-computed; verification is refused."
                };
            }

            // Past the bounds checks hashedLength is in 1..rawBytes.Length, so it fits an int.
            int hashedPrefix = (int)hashedLength;
            byte[] hashForChain;
            using (var sha256 = SHA256.Create())
                hashForChain = sha256.ComputeHash(rawBytes, 0, hashedPrefix);

            // The hash covers a PREFIX of the file and nothing else, so on its own it is silent
            // about every byte after it. A PDF is append-only editable: one incremental update can
            // supersede page 1's content stream so the document renders completely differently,
            // while every byte /HashedLength covers stays untouched and re-hashes to exactly the
            // value on the chain. This check is what makes IsValid false for such a document.
            bool notarizedRevisionIsLast = NoBytesFollowNotarizedRevision(rawBytes, hashedPrefix,
                                                                          out string appendReason);

            // The revision carrying the proof is as appendable as any other. Nothing follows it, and
            // the prefix's hash holds, whatever it rewrites beside the proof — a page's content, say.
            if (notarizedRevisionIsLast)
            {
                IReadOnlyList<string> rewritten = DocMdpAnalyzer.ChangesBesidesCatalogEntry(document, hashedPrefix, PdfName.BlockchainProof);
                if (rewritten.Count > 0)
                {
                    notarizedRevisionIsLast = false;
                    appendReason = "The revision carrying /BlockchainProof changes more than the proof, so the " +
                                   "document no longer reads as what the on-chain hash covers: " +
                                   string.Join("; ", rewritten.Take(MaxChangesNamed)) + ".";
                }
            }

            var provider = new BlockchainSignatureProvider();
            var result = await provider.VerifyHashAsync(hashForChain, proof, parameters, cancellationToken).ConfigureAwait(false);

            // Recorded after the chain lookup rather than instead of it, deliberately: HashMatches
            // stays meaningful and the PAIR distinguishes the two attacks. Hash true + this false
            // is "the notarized bytes are intact, something was appended after them"; hash false is
            // "the notarized bytes themselves were altered". Refusing before the lookup would
            // collapse both into one unhelpful verdict.
            result.IsLastRevision = notarizedRevisionIsLast;
            if (!notarizedRevisionIsLast)
            {
                result.Error = string.IsNullOrEmpty(result.Error)
                    ? appendReason
                    : appendReason + " " + result.Error;
            }

            return result;
        }

        /// <summary>
        /// Whether the notarized revision — the one that begins at
        /// <paramref name="hashedLength"/> and carries the proof — is the last thing in the file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this is not <c>Base.Operations.Documents.PdfRevisions</c>.</b> That type is the
        /// repository's machinery for enumerating revisions and is the obvious tool for the
        /// question — but it lives in <c>Tetitulz.Pdf.Base</c>, and this plugin references only
        /// <c>Core</c> and <c>Signing</c> on purpose. Taking a <c>Base</c> reference would pull
        /// SkiaSharp and its native assets into the package graph of every consumer who wanted
        /// nothing but blockchain notarization, which is the exact cost the plugin ring exists to
        /// avoid — CLAUDE.md's rule is that a plugin earns its place by keeping a dependency OUT of
        /// the core graph, and this one is here to isolate Nethereum.
        /// </para>
        /// <para>
        /// What is duplicated is also much narrower than <c>PdfRevisions</c>: that walks the whole
        /// file finding every boundary and validating each <c>startxref</c> back-pointer, while
        /// this asks one question about one known offset. The <c>%%EOF</c>-inside-a-stream hazard
        /// that forces <c>PdfRevisions</c> to validate the <c>startxref</c> cannot arise here — the
        /// scan starts at the first byte of a revision that holds one dictionary object, a cross-reference
        /// section and a trailer (or a cross-reference stream), which
        /// <see cref="DocMdpAnalyzer.ChangesBesidesCatalogEntry"/> holds it to. A proof this library did
        /// not write is already refused above for having no usable <c>/HashedLength</c>.
        /// </para>
        /// </remarks>
        private static bool NoBytesFollowNotarizedRevision(byte[] bytes, int hashedLength, out string reason)
        {
            int eof = IndexOfEofMarker(bytes, hashedLength);
            if (eof < 0)
            {
                reason = "The revision carrying /BlockchainProof has no %%EOF marker, so the " +
                         "extent of the notarized document cannot be established; verification " +
                         "is refused.";
                return false;
            }

            for (int i = eof + EofMarker.Length; i < bytes.Length; i++)
            {
                if (IsPdfWhitespace(bytes[i])) continue;

                reason = $"{bytes.Length - i} byte(s) were appended after the revision carrying " +
                         "/BlockchainProof, so the document no longer ends where the on-chain hash " +
                         "says it does. An incremental update can replace what every page renders " +
                         "while leaving the notarized prefix byte-identical, so the proof covers " +
                         "content this document no longer shows.";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>How many changes a refusal names; a file can make as many as it has objects.</summary>
        private const int MaxChangesNamed = 20;

        private static readonly byte[] EofMarker = { (byte)'%', (byte)'%', (byte)'E', (byte)'O', (byte)'F' };

        private static int IndexOfEofMarker(byte[] bytes, int from)
        {
            for (int i = Math.Max(0, from); i <= bytes.Length - EofMarker.Length; i++)
            {
                int j = 0;
                while (j < EofMarker.Length && bytes[i + j] == EofMarker[j]) j++;
                if (j == EofMarker.Length) return i;
            }
            return -1;
        }

        // ISO 32000-1 7.2.2, Table 1: Core's one whitespace predicate.
        private static bool IsPdfWhitespace(byte b) => Core.Constants.PdfSyntax.IsWhitespace(b);

        /// <summary>
        /// The whole stream as bytes, or <c>null</c> when it cannot be read.
        /// </summary>
        /// <remarks>
        /// <para><b>Deliberately not <see cref="Tetitulz.Pdf.Core.IO.StreamBytes.ReadAll"/></b>, which
        /// is the one implementation everywhere else. Two differences, both load-bearing here:</para>
        ///
        /// <para><b>1. It restores the caller's position.</b> The argument is a live
        /// <c>PdfDocument.BaseStream</c> — the document is still open and its parser will seek that
        /// stream again. The shared reader documents that it does <i>not</i> restore, which is the
        /// right default for a caller that owns the stream and the wrong one for a caller borrowing
        /// an open document's.</para>
        ///
        /// <para><b>2. It returns <c>null</c> rather than throwing.</b> Every call site here tests
        /// for <c>null</c> and turns it into a refusal with a reason, because this is a verifier: an
        /// escaping exception from a damaged file would be reported as a crash rather than as
        /// "cannot establish integrity". The shared reader throws on <c>null</c> input and lets a
        /// read fault escape.</para>
        ///
        /// <para>If a second caller ever needs this contract, name it in <c>StreamBytes</c> rather
        /// than copying it again — two named contracts beat one contract and several private
        /// variants.</para>
        /// </remarks>
        private static byte[] ReadAllBytes(Stream stream)
        {
            if (stream == null) return null;
            try
            {
                if (stream is MemoryStream ms0) return ms0.ToArray();
                long savedPos = stream.CanSeek ? stream.Position : 0;
                if (stream.CanSeek) stream.Position = 0;
                using (var ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    if (stream.CanSeek) stream.Position = savedPos;
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Verifies the blockchain proof from a PDF file path.
        /// </summary>
        public async Task<BlockchainVerificationResult> VerifyAsync(
            string pdfPath, BlockchainParameters parameters,
            System.Threading.CancellationToken cancellationToken = default)
        {
            using (var stream = File.OpenRead(pdfPath))
            using (var doc = PdfDocument.Open(stream))
            {
                return await VerifyAsync(doc, parameters, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Follows an indirect reference; §7.3.10 permits one for any value.</summary>
        private static PdfObject Resolve(PdfDocument doc, PdfObject obj)
            => obj is PdfReference && doc != null ? doc.ResolveObject(obj) : obj;

        /// <remarks>
        /// <c>doc</c> is a parameter because every one of the four values read through here —
        /// <c>/TxHash</c>, <c>/DocumentHash</c>, <c>/Network</c>, <c>/SignerAddress</c> — reaches
        /// the caller-visible result. Read as absent they become nulls in a report that otherwise
        /// looks complete: a verification result naming no transaction and no signer.
        /// </remarks>
        private static string GetStringValue(PdfDictionary dict, string key, PdfDocument doc)
        {
            var obj = Resolve(doc, dict[new PdfName(key)]);
            if (obj is PdfString pdfStr)
                return pdfStr.GetText();
            if (obj is PdfName pdfName)
                return pdfName.Value;
            return null;
        }
    }
}
