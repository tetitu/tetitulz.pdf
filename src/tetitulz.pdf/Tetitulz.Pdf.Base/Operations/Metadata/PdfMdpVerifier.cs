// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core.Document;

namespace Tetitulz.Pdf.Base.Operations.Metadata
{
    /// <summary>
    /// Verifies whether a PDF document has been modified in a way that violates its
    /// DocMDP (Modification Detection and Prevention) certification policy.
    ///
    /// <para>
    /// When a PDF is certified with a DocMDP signature, the /TransformParams /P entry
    /// specifies what changes are permitted after signing:
    /// <list type="bullet">
    ///   <item><term>P=1</term><description>No changes allowed (read-only).</description></item>
    ///   <item><term>P=2</term><description>Form field values and digital signatures may be added.</description></item>
    ///   <item><term>P=3</term><description>Annotations may also be added/modified (but not deleted).</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// This implementation detects:
    /// <list type="bullet">
    ///   <item>Whether a DocMDP certification signature is present.</item>
    ///   <item>The permitted change level (P value).</item>
    ///   <item>Whether incremental updates exist beyond the certification byte range.</item>
    ///   <item>For P=1: any incremental update is a violation.</item>
    ///   <item>For P=2/3: reports incremental updates and their approximate content types.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class PdfMdpVerifier
    {
        // ----------------------------------------------------------------
        //  Public API
        // ----------------------------------------------------------------

        /// <summary>Verifies DocMDP compliance of a PDF file.</summary>
        public static PdfMdpResult Verify(string pdfPath)
        {
            using var fs = File.OpenRead(pdfPath);
            return Verify(fs);
        }

        /// <summary>Verifies DocMDP compliance of a PDF stream.</summary>
        public static PdfMdpResult Verify(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            // Cleared before anything that can throw. Opening a malformed document throws below,
            // and this flag feeds security-critical reports ("the permission level was DEFAULTED,
            // not explicit") - so leaving the previous document's verdict in place is exactly the
            // wrong failure. A false negative here is a reviewer told a defaulted /P was explicit.
            LastDocMdpUsedDefaultP = false;

            // An encrypted document has no readable catalog, so the AcroForm walk below finds no
            // signature fields and this reported "No DocMDP certification signature found" - about
            // a document that may well be certified. That is a security answer, and a wrong one:
            // the caller asked whether modification is restricted and was told it is not.
            //
            // No password parameter, and the reason is the /ByteRange rather than reluctance: a
            // DocMDP verdict is a digest over the file AS SIGNED, so decrypting produces different
            // bytes and every document would report as modified. Refusing is what is left, and it
            // is the honest answer - the same one PdfSigningOperations gives for an encrypted
            // AcroForm read.
            // The onward pointer is deliberately NOT PdfSignatureVerifier, which this message used
            // to name as the place that "holds the file-key handling". It holds no decryptor at
            // all — it says so itself ("this verifier holds no decryptor and must not guess") and
            // answers an empty list or throws for an encrypted document. Sending a caller there
            // was sending them nowhere.
            if (PdfHelpers.IsEncryptedDocument(input))
                throw new Core.PdfException(
                    "The document is encrypted, so its AcroForm cannot be read and a DocMDP " +
                    "verdict would report a certified document as uncertified. This operation " +
                    "takes no password because a DocMDP verdict is two answers over two different " +
                    "views of the file: whether it is certified and at what /P, which needs the " +
                    "object graph, and whether it has been modified since, which is decided by " +
                    "comparing cross-reference offsets against the certification /ByteRange. " +
                    "Decrypting recovers the first and destroys the second — a decrypted copy is " +
                    "re-serialised, so its offsets are its own while the stored /ByteRange still " +
                    "indexes the original bytes. Decrypt with Tetitulz.Pdf.Encryption and verify " +
                    "the result if the certification level is what you need; there is no supported " +
                    "route to the modification half of the verdict on an encrypted document.");

            var ms = PdfMetadataWriter.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // All analysis lives in the shared, pure-Core DocMdpAnalyzer so this standalone API
            // and the signature-verification path (Tetitulz.Pdf.Signing) agree.
            var a = DocMdpAnalyzer.Analyze(doc, ms.Length);
            LastDocMdpUsedDefaultP = a.UsedDefaultP;

            var result = new PdfMdpResult
            {
                HasCertificationSignature = a.HasCertification,
                PermissionLevel = a.PermissionLevel,
                PermissionDescription = a.PermissionDescription,
                HasIncrementalUpdates = a.HasIncrementalUpdates,
                CertificationByteRangeEnd = a.CertByteRangeEnd,
                FileLength = a.FileLength,
                IsCompliant = a.IsCompliant,
                Summary = a.Summary,
            };
            result.ModifiedObjectTypes.AddRange(a.ModifiedObjectTypes);
            result.Violations.AddRange(a.Violations);
            return result;
        }

        /// <summary>
        /// Set to <c>true</c> when the most recent <see cref="Verify(Stream)"/> found a /DocMDP
        /// whose TransformParams omitted the /P value and defaulted to P=2 per spec. Callers
        /// building security-critical reports should surface this so reviewers know they are
        /// looking at a <em>defaulted</em> permission level, not an explicit one.
        /// <para>
        /// Backed by a <c>[ThreadStatic]</c> field, so a concurrent verification on another
        /// thread cannot overwrite it — read it on the thread that ran <c>Verify</c> (see the
        /// Diagnostics Pattern section of CLAUDE.md).
        /// </para>
        /// </summary>
        public static bool LastDocMdpUsedDefaultP
        {
            get => _lastDocMdpUsedDefaultP;
            private set => _lastDocMdpUsedDefaultP = value;
        }

        [System.ThreadStatic]
        private static bool _lastDocMdpUsedDefaultP;
    }

    // ----------------------------------------------------------------
    //  Result type
    // ----------------------------------------------------------------

    /// <summary>Result of a DocMDP compliance verification.</summary>
    public sealed class PdfMdpResult
    {
        /// <summary>Whether a DocMDP certification signature was found.</summary>
        public bool HasCertificationSignature { get; set; }

        /// <summary>Permission level P from the certification (1, 2, or 3). 0 = not certified.</summary>
        public int PermissionLevel { get; set; }

        /// <summary>Human-readable description of the permission level.</summary>
        public string PermissionDescription { get; set; }

        /// <summary>Whether incremental updates exist beyond the certification byte range.</summary>
        public bool HasIncrementalUpdates { get; set; }

        /// <summary>Byte offset where the certification signature's signed region ends.</summary>
        public long CertificationByteRangeEnd { get; set; }

        /// <summary>Total file length.</summary>
        public long FileLength { get; set; }

        /// <summary>Whether the document complies with its DocMDP policy.</summary>
        public bool IsCompliant { get; set; }

        /// <summary>Object types found in incremental updates after the certification signature.</summary>
        public List<string> ModifiedObjectTypes { get; } = new List<string>();

        /// <summary>List of policy violations found.</summary>
        public List<string> Violations { get; } = new List<string>();

        /// <summary>Overall summary message.</summary>
        public string Summary { get; set; }
    }
}
