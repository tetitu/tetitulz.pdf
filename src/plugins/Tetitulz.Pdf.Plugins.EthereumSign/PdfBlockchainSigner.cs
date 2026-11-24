// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Plugins.EthereumSign
{
    /// <summary>
    /// Orchestrates combined PKI + Ethereum blockchain signing.
    /// Signs the PDF with a certificate, records the hash on Ethereum,
    /// then embeds the blockchain proof back into the PDF.
    /// </summary>
    public sealed class PdfBlockchainSigner
    {
        /// <summary>
        /// Signs a PDF with PKI certificate, records the document hash on the Ethereum blockchain,
        /// and embeds the blockchain proof in the PDF as a /BlockchainProof catalog entry.
        /// </summary>
        public async Task SignAsync(Stream input, Stream output,
            SignatureParameters sigParams, BlockchainParameters blockchainParams,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (sigParams == null) throw new ArgumentNullException(nameof(sigParams));
            if (blockchainParams == null) throw new ArgumentNullException(nameof(blockchainParams));

            byte[] signedPdfBytes;
            using (var tempOutput = new MemoryStream())
            {
                var signer = new PdfSigner();
                signer.Sign(input, tempOutput, sigParams);
                signedPdfBytes = tempOutput.ToArray();
            }

            byte[] documentHash;
            using (var sha256 = SHA256.Create())
            {
                documentHash = sha256.ComputeHash(signedPdfBytes);
            }

            var provider = new BlockchainSignatureProvider();
            BlockchainProof proof = await provider.RecordHashAsync(documentHash, blockchainParams, cancellationToken).ConfigureAwait(false);

            byte[] finalPdf = EmbedBlockchainProof(signedPdfBytes, proof);

            output.Write(finalPdf, 0, finalPdf.Length);
            output.Flush();
        }

        /// <summary>
        /// Signs a PDF with PKI certificate, records the document hash on the Ethereum blockchain,
        /// and embeds the blockchain proof in the PDF.
        /// </summary>
        /// <remarks>
        /// <b>The destination is not touched until every step has succeeded.</b> Opening it with
        /// <c>File.Create</c> first would truncate an existing file the moment the call starts, so an
        /// unreachable RPC endpoint, a transaction that never mines, or a revert would leave a
        /// zero-byte file where the previous output was. Anchoring on a public chain is the step most
        /// likely to fail, and it is the last one.
        /// </remarks>
        public async Task SignAsync(string inputPath, string outputPath,
            SignatureParameters sigParams, BlockchainParameters blockchainParams,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));

            byte[] finished;
            using (var input = File.OpenRead(inputPath))
            using (var buffer = new MemoryStream())
            {
                await SignAsync(input, buffer, sigParams, blockchainParams, cancellationToken).ConfigureAwait(false);
                finished = buffer.ToArray();
            }

            File.WriteAllBytes(outputPath, finished);
        }

        /// <summary>
        /// Appends an incremental update that supersedes the catalog with a copy carrying
        /// <c>/BlockchainProof</c>, and returns the whole document.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>/HashedLength</c> records <paramref name="pdfBytes"/>.Length — the prefix the proof
        /// notarizes — so <see cref="PdfBlockchainVerifier"/> can re-compute the hash over the same
        /// bytes instead of trusting the <c>/DocumentHash</c> string written beside it. That key is
        /// the whole tamper check, so the verifier refuses a proof without it rather than falling back.
        /// </para>
        /// <para>
        /// The revision is written by <see cref="IncrementalUpdateWriter"/>, which matches the
        /// superseded section's format (a cross-reference stream stays a stream, never a hybrid
        /// file), whitelists the trailer keys, refuses to write a <c>/Prev</c> it cannot verify, and
        /// synthesises a deterministic <c>/ID</c>.
        /// </para>
        /// </remarks>
        /// <exception cref="ArgumentNullException">An argument is null.</exception>
        /// <exception cref="PdfSigningException">
        /// The document is encrypted, or its <c>/Root</c> is not an indirect reference to a
        /// dictionary.
        /// </exception>
        public static byte[] EmbedBlockchainProof(byte[] pdfBytes, BlockchainProof proof)
        {
            if (pdfBytes == null) throw new ArgumentNullException(nameof(pdfBytes));
            if (proof == null) throw new ArgumentNullException(nameof(proof));

            using (var parseMs = new MemoryStream(pdfBytes, writable: false))
            {
                var parser = new PdfParser(parseMs);
                parser.ParseHeader();
                parser.ParseCrossReferences();
                var trailer = parser.Trailer;

                // Fail closed, with the reason. The proof's strings would be appended in clear
                // text where the reader expects per-object ciphertext, so it would "decrypt" them
                // into noise — and this assembly holds no decryptor (adding one would pull the
                // crypto stack into this package's graph, which is the whole point of keeping
                // Nethereum isolated here).
                if (trailer != null && trailer.ContainsKey(PdfName.Encrypt))
                    throw new PdfSigningException(
                        "Cannot embed a blockchain proof in an encrypted document: the proof " +
                        "strings would be written in clear text where the reader expects " +
                        "per-object ciphertext, and this assembly cannot decrypt. Decrypt first " +
                        "(see Tetitulz.Pdf.Encryption.PdfEncryptedRoundTrip), then notarize.");

                var rootRef = trailer?[PdfName.Root];

                // A trailer with a DIRECT /Root — `/Root << /Type /Catalog … >>` — has no object
                // number to supersede, and GetObjectNumber answers 0, which is reserved as the head
                // of the free list: writing `0 0 obj` and an xref entry for it would emit a catalog
                // no reader can reach, in a malformed file that reports success.
                int catalogObjNum = GetObjectNumber(rootRef);
                if (catalogObjNum <= 0)
                    throw new PdfSigningException(
                        "/Root is not an indirect reference, so the catalog cannot be superseded " +
                        "by an incremental update. Object 0 is reserved as the head of the free " +
                        "list and cannot carry the proof.");

                // A dangling or non-dictionary /Root is refused here, as a PdfSigningException like
                // every other failure of this API, rather than reaching `foreach (var kvp in rootObj)`.
                var rootObj = parser.ResolveObject(rootRef) as PdfDictionary;
                if (rootObj == null)
                    throw new PdfSigningException(
                        $"/Root ({catalogObjNum} 0 R) does not resolve to a catalog dictionary, " +
                        "so there is nothing to attach the blockchain proof to.");

                // Rebuilt rather than cloned-and-edited: removing a key from a clone and adding one
                // back reuses the freed slot, so /BlockchainProof would not land last. The
                // verifier does not care, but a fixture that edits the emitted bytes does.
                var newCatalog = new PdfDictionary(rootObj.Count + 1);
                foreach (var kvp in rootObj)
                {
                    if (kvp.Key.Value == BlockchainProofKey) continue;
                    newCatalog.Set(kvp.Key, kvp.Value);
                }
                newCatalog.Set(PdfName.BlockchainProof, BuildProofDictionary(proof, pdfBytes.Length));

                int generation = (rootRef as PdfReference)?.ReferenceGenerationNumber ?? 0;

                using (var output = new MemoryStream())
                {
                    IncrementalUpdateWriter.WriteUpdate(
                        pdfBytes, output,
                        new[] { new IndirectObject(catalogObjNum, generation, newCatalog) },
                        new IncrementalUpdateOptions
                        {
                            OriginalTrailer = trailer,
                            Size = parser.CrossReferenceTable.GetMaxObjectNumber() + 1,
                        });
                    return output.ToArray();
                }
            }
        }

        private const string BlockchainProofKey = "BlockchainProof";

        private static readonly PdfName TxHashName        = PdfName.TxHash;
        private static readonly PdfName BlockNumberName   = new PdfName("BlockNumber");
        private static readonly PdfName TimestampName     = new PdfName("Timestamp");
        private static readonly PdfName DocumentHashName  = PdfName.DocumentHash;
        private static readonly PdfName NetworkName       = new PdfName("Network");
        private static readonly PdfName SignerAddressName = new PdfName("SignerAddress");
        private static readonly PdfName HashedLengthName  = new PdfName("HashedLength");

        /// <summary>
        /// Builds the <c>/BlockchainProof</c> dictionary. <c>/HashedLength</c> goes in last so it
        /// is the final entry of the emitted dictionary, the layout every proof this signer writes
        /// carries.
        /// </summary>
        private static PdfDictionary BuildProofDictionary(BlockchainProof proof, int hashedLength)
        {
            var dict = new PdfDictionary(7);
            dict.Set(TxHashName,        new PdfString(proof.TransactionHash ?? string.Empty));
            dict.Set(BlockNumberName,   new PdfNumber(proof.BlockNumber));
            dict.Set(TimestampName,     new PdfString(PdfDate.FormatUtc(proof.Timestamp)));
            dict.Set(DocumentHashName,  new PdfString(proof.DocumentHash ?? string.Empty));
            dict.Set(NetworkName,       new PdfString(proof.Network ?? string.Empty));
            dict.Set(SignerAddressName, new PdfString(proof.SignerAddress ?? string.Empty));
            // The hashed bytes are exactly the document BEFORE this proof update (= pdfBytes).
            // Record the length so the verifier can re-compute the hash over the same prefix
            // instead of trusting the /DocumentHash string written above it.
            dict.Set(HashedLengthName,  new PdfNumber(hashedLength));
            return dict;
        }

        private static int GetObjectNumber(PdfObject obj)
        {
            if (obj is PdfReference r) return r.ReferenceObjectNumber;
            return obj?.ObjectNumber ?? 0;
        }
    }
}


