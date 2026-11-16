// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Appends one revision to an <b>encrypted</b> document, in place.
    ///
    /// <para><b>The trade this makes, against <see cref="PdfEncryptedRoundTrip"/>.</b> The round
    /// trip decrypts the whole file, hands it to an operation, and re-encrypts — correct, but it
    /// rewrites every byte, so it cannot keep an existing signature valid (the <c>/ByteRange</c>
    /// no longer covers what it signed) and cannot preserve the owner password (<c>/O</c> and
    /// <c>/U</c> derive one-way from both). This keeps the original bytes <b>untouched</b> and
    /// appends an encrypted revision, so both survive. Use the round trip when the operation has
    /// to rewrite the whole document (sanitize, optimize, linearize, redact); use this when it
    /// only adds or replaces objects.</para>
    ///
    /// <para><b>Two documents, on purpose.</b> The operation <i>reads</i> a decrypted copy —
    /// <see cref="PdfDocument.Open(Stream)"/> loads no catalog for an encrypted file, so the field
    /// tree, page tree and resources are simply not reachable from the original — while the
    /// revision is appended to the original, still-encrypted bytes, whose byte offsets are the
    /// ones the appended cross-reference section must name. The plan callback therefore receives
    /// the decrypted document and must return objects carrying the object numbers they supersede.</para>
    ///
    /// <para><b>Object numbering.</b> Decryption unpacks object streams into ordinary objects and
    /// drops the cross-reference stream, so the decrypted copy's highest object number can be
    /// <i>lower</i> than the original's. New numbers are therefore allocated from the original's
    /// ceiling, which is passed to the plan — allocating from the decrypted copy's would overwrite
    /// a live object.</para>
    ///
    /// <para><b>PDF MAC.</b> A document protected by an ISO/TS 32004 PDF MAC token gets a fresh
    /// standalone token in the appended revision's trailer, covering the whole file under the same file
    /// key and <c>/KDFSalt</c> — the previous token stops covering the file the moment anything is
    /// appended. See <see cref="PdfMacTokenSeal.ForAppendedRevision(PdfDocument, PdfDecryptionCredential)"/> for when a document counts as
    /// protected.</para>
    /// </summary>
    public static class PdfEncryptedIncrementalUpdate
    {
        /// <summary>
        /// Decrypts <paramref name="input"/> in memory, asks <paramref name="plan"/> what to
        /// write, and appends that revision to the original bytes — encrypted with the document's
        /// own file key.
        /// </summary>
        /// <param name="input">The encrypted PDF. Read only.</param>
        /// <param name="output">Receives the original bytes followed by one appended revision.</param>
        /// <param name="password">User or owner password.</param>
        /// <param name="plan">
        /// Receives the decrypted document and the first object number it may allocate, and
        /// returns the objects to append. Returning none writes the original bytes unchanged.
        /// </param>
        /// <exception cref="PdfException">
        /// The document is not encrypted, uses an unsupported handler, or the password is wrong.
        /// </exception>
        public static void Run(
            Stream input,
            Stream output,
            string password,
            Func<PdfDocument, int, IReadOnlyList<IndirectObject>> plan)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            Run(input, output, password,
                (doc, firstFree) => new EncryptedRevision(plan(doc, firstFree)));
        }

        /// <summary>
        /// As <see cref="Run(Stream,Stream,string,Func{PdfDocument,int,IReadOnlyList{IndirectObject}})"/>,
        /// but the plan may also name an object the <b>trailer</b> must point at.
        /// </summary>
        /// <remarks>
        /// <para><b>Why this overload exists.</b> The objects-only form can append any revision that
        /// <i>revises</i> existing objects, and any that mints an object something already-revised
        /// points at. It cannot append one that mints an object only the <b>trailer</b> can reach —
        /// and <c>/Info</c> is exactly that object. So a document with no <c>/Info</c> could be given
        /// a title through this path, correctly, and the title was written into an object nothing
        /// referenced: a title no reader can find. That is UA04, and it failed silently.</para>
        /// <para><c>IncrementalUpdateOptions.OriginalTrailer</c> is where the writer takes
        /// <c>/Root</c>, <c>/Info</c>, <c>/ID</c> and <c>/Encrypt</c> from, so it is handed a trailer
        /// whose <c>/Info</c> is the minted number, and the writer learns nothing new. <b>The original trailer is cloned, never mutated</b>: it
        /// belongs to the open original document, which the writer is still reading from.</para>
        /// </remarks>
        public static void Run(
            Stream input,
            Stream output,
            string password,
            Func<PdfDocument, int, EncryptedRevision> plan)
            => Run(input, output, PdfDecryptionCredential.Password(password), plan);

        /// <summary>
        /// As above, for a document opened with any credential — including an
        /// <c>/Adobe.PubSec</c> one, which has no password.
        /// </summary>
        /// <remarks>
        /// <b>Nothing about appending a revision depends on the security handler.</b> ISO 32000-2
        /// §7.6.5 differs from §7.6.3 only in how the <i>file</i> key is recovered; the per-object
        /// keys, the crypt filters and the appended cross-reference section are the same
        /// afterwards. So this is the same method with the credential as a parameter, and a
        /// public-key document keeps its PDF MAC token through an append for the same reason a
        /// password-protected one does.
        /// </remarks>
        public static void Run(
            Stream input,
            Stream output,
            PdfDecryptionCredential credential,
            Func<PdfDocument, int, EncryptedRevision> plan)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (credential == null) throw new ArgumentNullException(nameof(credential));
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            byte[] original = ReadAll(input);

            using var originalDoc = OpenOriginal(original);
            if (!originalDoc.IsEncrypted)
                throw new PdfException(
                    "The document is not encrypted; append the revision with " +
                    "Core.Writing.IncrementalUpdateWriter directly rather than through this path.");

            if (!PdfIncrementalEncryption.TryCreate(originalDoc, credential,
                                                   out IPdfObjectEncryptor encryptor, out string error))
                throw new PdfException("Cannot append to the encrypted document: " + error);

            // A decrypted copy to read the object graph from. The original bytes stay untouched.
            byte[] decrypted;
            using (var source = new MemoryStream(original, writable: false))
            using (var plaintext = new MemoryStream())
            {
                credential.Decrypt(source, plaintext);
                decrypted = plaintext.ToArray();
            }

            int firstFreeObjectNumber = originalDoc.CrossReferenceTable.GetMaxObjectNumber() + 1;

            EncryptedRevision revision;
            using (var plaintext = new MemoryStream(decrypted, writable: false))
            using (PdfDocument decryptedDoc = PdfDocument.Open(plaintext))
            {
                revision = plan(decryptedDoc, firstFreeObjectNumber);
            }

            IReadOnlyList<IndirectObject> objects =
                revision?.Objects ?? (IReadOnlyList<IndirectObject>)Array.Empty<IndirectObject>();

            var options = new IncrementalUpdateOptions
            {
                Document = originalDoc,
                ObjectEncryptor = encryptor,
                Size = firstFreeObjectNumber,
            };

            int newInfo = revision?.NewInfoObjectNumber ?? -1;
            if (newInfo > 0)
            {
                // Clone: this trailer belongs to originalDoc, which the writer is still reading.
                // Mutating it would edit the document out from under the write in progress.
                //
                // KEPT DELIBERATELY THOUGH INERT, and saying so is the point. Handing the writer the
                // live trailer reddens nothing in Encryption or Accessibility, and that is not a
                // coverage gap: originalDoc is opened and disposed inside this method, so the aliasing
                // never escapes it, and the only place the writer re-reads Document.Trailer is
                // DeclaresXrefStream — an answer an added /Info cannot change. So the hazard is real
                // and unreachable.
                //
                // It stays because a defensive copy of a dictionary costs nothing and the hazard
                // returns the moment the writer reads one more trailer key. It is annotated rather
                // than left silent because an unexplained line that survives every mutation is
                // exactly what the next reader deletes as dead — or worse, trusts as load-bearing.
                PdfDictionary trailer = originalDoc.Trailer;
                PdfDictionary carried = trailer != null
                    ? PdfDictionary.CloneShallow(trailer)
                    : new PdfDictionary();

                carried[PdfName.Info] = new PdfReference(newInfo, 0);
                options.OriginalTrailer = carried;
            }

            // A plan that brought its own AuthCode dictionary has taken responsibility for this
            // revision's PDF MAC, and the standalone seal must NOT also run: §6.5.2's placement
            // exists precisely because a standalone token in a signed revision would cover the
            // signature's own placeholders and stop verifying the moment the CMS is written into
            // them. Nothing is sealed afterwards either, so the output needs no second pass and the
            // caller's stream need not be readable.
            if (revision?.AuthCode != null)
            {
                options.AuthCode = revision.AuthCode;
                IncrementalUpdateWriter.WriteUpdate(original, output, objects, options);
                return;
            }

            // The writer appends nothing, and so seals nothing, when there are no objects: the original
            // token still covers the whole file then.
            options.PdfMac = PdfMacTokenSeal.ForAppendedRevision(originalDoc, credential);
            if (options.PdfMac == null)
            {
                IncrementalUpdateWriter.WriteUpdate(original, output, objects, options);
                return;
            }

            // Sealing reads and rewrites the finished file, which the caller's stream need not allow.
            using (var sealedFile = new MemoryStream())
            {
                IncrementalUpdateWriter.WriteUpdate(original, sealedFile, objects, options);
                sealedFile.Position = 0;
                sealedFile.CopyTo(output);
            }
        }

        /// <summary>
        /// Reads the whole input document.
        ///
        /// <para>This was a private copy of the rule <see cref="StreamBytes.ReadAll"/> owns —
        /// behaviourally correct (it rewound a seekable stream), but the <i>second</i> of two copies
        /// in this assembly, and its twin in <see cref="PdfEncryptedRoundTrip"/> did not rewind. Two
        /// public entry points therefore answered differently for the same seekable non-
        /// <see cref="MemoryStream"/> positioned past zero. Delegating both is what makes them agree
        /// by construction rather than by inspection.</para>
        /// </summary>
        private static byte[] ReadAll(Stream input) => StreamBytes.ReadAll(input);

        private static PdfDocument OpenOriginal(byte[] original)
        {
            // Owns its own stream: PdfDocument keeps reading from it for the life of the document.
            var stream = new MemoryStream(original, writable: false);
            return PdfDocument.Open(stream);
        }
    }

    /// <summary>
    /// What a plan wants appended: the objects, and optionally an <c>/Info</c> the trailer must be
    /// pointed at.
    /// </summary>
    /// <remarks>
    /// <para><b>Two named entries, deliberately, rather than a trailer dictionary.</b> The other
    /// three trailer keys the writer carries are not a plan's to change: <c>/Root</c> would repoint
    /// the catalog (an operation that supersedes the document rather than revising it),
    /// <c>/Encrypt</c> is the dictionary this very write is encrypting against, and <c>/ID</c> is
    /// derived. Widening this to a general trailer dictionary would hand a caller three ways to
    /// produce a file that cannot be opened, for two legitimate uses.</para>
    ///
    /// <para><b><c>/AuthCode</c> is the second of those uses and arrived the same way the first
    /// did</b> — as a revision that mints something only the trailer can reach. ISO/TS 32004 §6.5.2
    /// puts the <c>AuthCode</c> dictionary in the trailer of a <i>signed</i> revision, saying that
    /// the PDF MAC token is an unsigned attribute on the signature rather than carrying it; Table 6
    /// forbids <c>/ByteRange</c> and <c>/MAC</c> there, so the whole dictionary is two entries that
    /// a plan already knows, because it allocated the signature's object number.</para>
    /// </remarks>
    public sealed class EncryptedRevision
    {
        /// <param name="objects">Objects to append. Null is treated as none.</param>
        /// <param name="newInfoObjectNumber">
        /// Object number of a newly minted <c>/Info</c> the appended trailer must reference, or
        /// <c>-1</c> (the default) when the revision mints none. A value pointing at an object the
        /// revision does not contain would produce a dangling <c>/Info</c>, so pass it only for an
        /// object in <paramref name="objects"/>.
        /// </param>
        /// <param name="authCode">
        /// A finished ISO/TS 32004 <c>AuthCode</c> dictionary for the appended trailer, or null.
        /// Only §6.5.2's <c>AttachedToSig</c> form belongs here: the standalone form is written and
        /// sealed by this class itself, because its token digests bytes that do not exist until the
        /// write is over. Supplying one <b>replaces</b> that automatic sealing for this revision.
        /// </param>
        public EncryptedRevision(
            IReadOnlyList<IndirectObject> objects, int newInfoObjectNumber = -1,
            PdfDictionary authCode = null)
        {
            Objects = objects ?? Array.Empty<IndirectObject>();
            NewInfoObjectNumber = newInfoObjectNumber;
            AuthCode = authCode;
        }

        /// <summary>The objects to append.</summary>
        public IReadOnlyList<IndirectObject> Objects { get; }

        /// <summary>The minted <c>/Info</c> object number, or <c>-1</c>.</summary>
        public int NewInfoObjectNumber { get; }

        /// <summary>The <c>AuthCode</c> dictionary for the appended trailer, or null.</summary>
        public PdfDictionary AuthCode { get; }
    }

}
