// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Encryption;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Decrypts PDF documents, removing encryption and producing a clean unencrypted PDF.
    /// Supports RC4-40 (V=1, R=2), RC4-128 (V=2, R=2/3), AES-128 (V=4, R=4),
    /// AES-256 (V=5, R=5/6), and crypt filter-based algorithm selection.
    /// </summary>
    public static class PdfDecryptor
    {
        // ------------------------------------------------------------------
        //  Sizes fixed by the spec. Revision/round counts, key-length bounds and the AES block
        //  size are shared with the encrypt path via PdfEncryptionPrimitives.
        // ------------------------------------------------------------------

        /// <summary>
        /// Key length assumed when /Length is absent or unusable: 40 bits, the V=1 default
        /// (ISO 32000-1 §7.6.3.2 — /Length defaults to 40).
        /// </summary>
        private const int DefaultKeyLengthBits = 40;

        /// <summary>
        /// Largest /Length accepted for V&lt;5. The standard handler's key is sliced from a
        /// 16-byte MD5 digest, so 128 bits is the real ceiling however large /Length claims to be.
        /// </summary>
        private const int MaxKeyLengthBitsBelowV5 = 128;

        /// <summary>Length of a validation or key salt in a revision-5/6 /U or /O.</summary>
        private const int SaltLength = 8;

        /// <summary>
        /// Minimum length of a revision-5/6 /U or /O string: a 32-byte hash followed by the
        /// 8-byte validation salt and the 8-byte key salt.
        /// </summary>
        private const int PasswordEntryLength = 48;

        /// <summary>Offset of the validation salt within a revision-5/6 /U or /O string.</summary>
        private const int ValidationSaltOffset = 32;

        /// <summary>Offset of the key salt within a revision-5/6 /U or /O string.</summary>
        private const int KeySaltOffset = 40;

        /// <summary>Length of the AES-ECB-encrypted /Perms entry (ISO 32000-2 §7.6.4.3.4).</summary>
        private const int PermsLength = 16;

        /// <summary>Offset of the ASCII "adb" tag inside the decrypted /Perms plaintext.</summary>
        private const int PermsTagOffset = 9;

        /// <summary>Length of that tag, and hence the minimum readable /Perms plaintext.</summary>
        private const int PermsTagLength = 3;

        /// <summary>
        /// Offset of the <c>'T'</c>/<c>'F'</c> byte authenticating <c>/EncryptMetadata</c> inside the
        /// decrypted <c>/Perms</c> plaintext. Bytes 0-3 before it are <c>/P</c> little-endian.
        /// </summary>
        private const int PermsEncryptMetadataOffset = 8;

        /// <summary>
        /// Bytes of /U compared for revision 2: the whole 32-byte value. Comparing fewer let a
        /// truncated /U pass validation.
        /// </summary>
        private const int UserValueCompareLengthR2 = 32;

        /// <summary>
        /// Bytes of /U compared for revision 3+: the 16-byte MD5-derived prefix, the rest being
        /// arbitrary padding per the spec.
        /// </summary>
        private const int UserValueCompareLengthR3 = 16;

        /// <summary>
        /// Length of the LEGACY substitute file ID: sixteen 0x00 bytes, hashed into the key when
        /// the trailer carries no <c>/ID</c>.
        /// <para>
        /// This is <b>not</b> what the spec or any other implementation does — §7.6.3.3 Algorithm 2
        /// step (e) passes the first element of the file identifier array to MD5, and with no array
        /// there is nothing to pass (other readers hash nothing or skip the
        /// update entirely). Hashing sixteen zeros instead produces a different digest, so a
        /// third-party PDF with no <c>/ID</c> could not be opened with its correct password. It is
        /// kept only as a second attempt, for files earlier versions of this library wrote under
        /// that rule; <see cref="GetFileId"/> returns an empty array.
        /// </para>
        /// </summary>
        private const int LegacyMissingFileIdLength = 16;

        /// <summary>/V = 1 — 40-bit RC4; /Length is absent.</summary>
        private const int VersionRc4_40 = 1;

        /// <summary>/V = 2 — variable-length RC4 (/Length 40..128).</summary>
        private const int VersionRc4Variable = 2;

        /// <summary>/V = 4 — crypt filters select the algorithm per stream / string.</summary>
        private const int VersionCryptFilters = 4;

        /// <summary>/V = 5 — AES-256; the key is fixed at 256 bits and /Length is ignored.</summary>
        private const int VersionAes256 = 5;

        /// <summary>/R = 5 — the deprecated Adobe AES-256 extension (Algorithm 2.A, plain SHA-256).</summary>
        private const int RevisionAes256Deprecated = 5;

        /// <summary>/R = 6 — ISO 32000-2 / PDF 2.0 AES-256 (Algorithm 2.B, iterative hash).</summary>
        private const int RevisionAes256 = 6;

        /// <summary>/V = 6 — ISO/TS 32003 AES-GCM; password algorithms are identical to /V 5.</summary>
        private const int VersionAesGcm = 6;

        /// <summary>/R = 7 — required alongside /V 6 (ISO/TS 32003 Table 3); uses R6's Algorithm 2.B.</summary>
        private const int RevisionAesGcm = 7;

        /// <summary>
        /// Whether this V/R pair authenticates through <see cref="ValidateAndDeriveAes256Key"/> —
        /// the deprecated R5 extension and R6 (both under V=5), or R7 under V=6 (ISO/TS 32003),
        /// which reuses R6's Algorithm 2.B unchanged (only the per-object cipher differs at V=6).
        /// </summary>
        private static bool UsesAes256KeyDerivation(int v, int r)
            => (v == VersionAes256 && (r == RevisionAes256Deprecated || r == RevisionAes256))
            || (v == VersionAesGcm && r == RevisionAesGcm);

        /// <summary>
        /// Decrypts a PDF file and writes the unencrypted result to the output path.
        /// </summary>
        public static void Decrypt(string inputPath, string outputPath, string password)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));

            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Decrypt(input, output, password);
            }
        }

        /// <summary>
        /// Decrypts a PDF stream and writes the unencrypted result to the output stream.
        /// </summary>
        public static void Decrypt(Stream input, Stream output, string password)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (password == null) password = "";
            // Not normalised here: SASLprep applies to a revision-5+ password only, and it is applied
            // at the one place that knows the revision — ValidateAndDeriveAes256Key. Done here, it
            // would be wrong for a revision 2–4 password holding a no-break space or soft hyphen,
            // which §7.6.3.3 hashes as raw Latin-1 bytes.

            // Buffered() rewinds a seekable input BEFORE copying: copied from its current position, a
            // FileStream a host had already read from would be a truncated, corrupt document.
            MemoryStream ms = SeekableStream.Buffered(input);

            PdfDocument doc;
            try
            {
                doc = PdfDocument.Open(ms);
            }
            catch (Exception ex) when (!(ex is PdfException))
            {
                throw new PdfException("Failed to parse the input PDF for decryption.", ex);
            }

            using (doc)
            {
                WriteDecrypted(doc, output, ResolveEncryption(doc, password));
            }
        }

        /// <summary>
        /// Decrypts every object of an already-resolved document and writes the plain PDF.
        /// </summary>
        /// <param name="doc">The open, still-encrypted document.</param>
        /// <param name="output">Where the decrypted PDF is written.</param>
        /// <param name="resolved">
        /// The file key and per-stream/per-string algorithms, from whichever security handler
        /// supplied them.
        /// </param>
        /// <remarks>
        /// <b>Separated from the password path so the public-key handler shares it rather than
        /// copying it.</b> Everything below the key derivation — the object walk, the metadata
        /// exemption, the drop accounting, the trailer rebuild — is identical whichever handler
        /// produced the key, and this file's own comment on <see cref="ResolveEncryption"/> says
        /// why security logic must not exist twice. The handler-specific part is the derivation,
        /// and that is the only part <c>PdfPublicKeyDecryptor</c> implements.
        /// </remarks>
        internal static void WriteDecrypted(PdfDocument doc, Stream output, ResolvedEncryption resolved)
        {
            {
                // Reasons objects were dropped from the output, accumulated across both loops
                // below. Only the FIRST is kept verbatim plus a count: a hostile file can carry
                // thousands of unparseable objects, and concatenating every message would be an
                // unbounded allocation driven by the input.
                string firstDropReason = null;
                int dropCount = 0;
                void RecordDrop(string reason)
                {
                    dropCount++;
                    if (firstDropReason == null) firstDropReason = reason;
                }

                byte[] encryptionKey = resolved.Key;
                EncryptionAlgorithm stmAlgorithm = resolved.StreamAlgorithm;
                EncryptionAlgorithm strAlgorithm = resolved.StringAlgorithm;
                EncryptionAlgorithm effAlgorithm = resolved.EmbeddedFileAlgorithm;
                int r = resolved.Revision;

                // Streams' own crypt filters, resolved once per name: a file can name one on every
                // stream. A name the document does not define is reported once in all, not per stream
                // or per name — the names are the file's to multiply.
                var streamsWithOwnFilter = new HashSet<int>();
                var ownFilters = new Dictionary<string, EncryptionAlgorithm?>(StringComparer.Ordinal);
                bool undefinedReported = false;
                EncryptionAlgorithm? OwnCryptFilter(PdfName name)
                {
                    if (ownFilters.TryGetValue(name.Value, out EncryptionAlgorithm? known)) return known;
                    EncryptionAlgorithm? algorithm = resolved.StreamCryptFilter(name);
                    if (algorithm == null && !undefinedReported)
                    {
                        undefinedReported = true;
                        RecordCryptFilterFinding(PdfName.Crypt,
                            $"a stream names crypt filter /{name.Value}, which /CF does not define; such streams " +
                            "were decrypted under /StmF.");
                    }
                    ownFilters[name.Value] = algorithm;
                    return algorithm;
                }
                bool encryptMetadata = resolved.EncryptMetadata;

                // Find the metadata stream object number (to skip when EncryptMetadata is false)
                int metadataObjNum = -1;
                if (!encryptMetadata)
                {
                    var rootRef = doc.Trailer[PdfName.Root];
                    if (rootRef != null)
                    {
                        try
                        {
                            var rootObj = doc.ResolveObject(rootRef) as PdfDictionary;
                            if (rootObj != null)
                            {
                                var metaRef = rootObj[PdfName.Metadata] as PdfReference;
                                if (metaRef != null)
                                    metadataObjNum = metaRef.ReferenceObjectNumber;
                            }
                        }
                        catch { /* catalog may not be resolvable in encrypted doc */ }
                    }
                }

                // Step 1: Enumerate only Normal (type 1) objects from the xref table.
                // We cannot enumerate Compressed (type 2) objects yet because their
                // containing object streams are still encrypted. Trying to decompress
                // encrypted data produces garbage, causing those objects to be silently
                // lost — which manifests as empty pages.
                int encryptObjNum = GetEncryptObjNumber(doc.Trailer);
                var xrefTable = doc.Parser.CrossReferenceTable;
                var decryptedObjects = new Dictionary<int, PdfObject>();
                // The generation each top-level object is written back at — see ObjectIdentity. An object
                // taken from an object stream has no entry here and is written at 0, as §7.5.7 requires.
                var generations = new Dictionary<int, int>();
                var objectStreamNumbers = new HashSet<int>();
                var deferredMetadata = new List<int>();

                // Collect the object stream numbers referenced by compressed entries
                foreach (var entry in xrefTable.AllEntries)
                {
                    if (entry.Type == CrossReferenceEntry.EntryType.Compressed)
                        objectStreamNumbers.Add(entry.ObjectStreamNumber);
                }

                // Decrypt all Normal (type 1) objects
                foreach (var entry in xrefTable.AllEntries)
                {
                    if (entry.Type != CrossReferenceEntry.EntryType.Normal)
                        continue;
                    if (entry.ObjectNumber == 0)
                        continue; // object 0 is always free
                    if (entry.ObjectNumber == encryptObjNum)
                        continue;

                    PdfObject obj;
                    string unreadable;
                    try
                    {
                        obj = doc.Parser.GetObject(entry.ObjectNumber);
                        // Read at once: the slot belongs to the most recent call, and GetObject answers an
                        // object it could not read with the null object, as it answers a genuine one.
                        unreadable = PdfParser.LastObjectParseError;
                    }
                    catch (Exception ex)
                    {
                        // Same rule as the object-stream catch below: an object that cannot be
                        // parsed is an object missing from the decrypted output, which the caller
                        // cannot otherwise tell from an object the file never had.
                        RecordDrop(
                            $"Object {entry.ObjectNumber} could not be parsed ({ex.GetType().Name}: " +
                            $"{ex.Message}); it is absent from the output.");
                        continue;
                    }

                    if (obj == null || (obj is PdfNull && unreadable != null))
                    {
                        // A Normal xref entry whose object cannot be read is an object missing from
                        // the output. The parser is deliberately tolerant and answers the null object
                        // rather than throwing for most damage, with the reason in its slot.
                        RecordDrop(
                            $"Object {entry.ObjectNumber} could not be read at the offset its xref " +
                            "entry names" + (unreadable != null ? " (" + unreadable + ")" : "") +
                            "; it is absent from the output.");
                        continue;
                    }

                    if (obj is PdfNull)
                        continue;   // a genuine null object, not a drop

                    // A cross-reference stream is not encrypted — ISO 32000-2 §7.5.8.2: "The cross-reference
                    // stream shall not be encrypted and strings appearing in the cross-reference stream
                    // dictionary shall not be encrypted" — and is not carried into an output that closes with a
                    // classic table. Decrypted like any other stream, its bytes would be read under AES as
                    // IV + ciphertext, and whenever the length was 16 + 16k the PKCS#7 unpad would run, fail,
                    // and take the whole decrypt with it.
                    if (FileLayoutObjects.IsCrossReferenceStream(obj))
                        continue;

                    generations[entry.ObjectNumber] = ObjectIdentity.GenerationOf(obj);

                    // Skip decrypting the metadata stream when EncryptMetadata is false
                    if (entry.ObjectNumber == metadataObjNum)
                    {
                        decryptedObjects[entry.ObjectNumber] = obj;
                        continue;
                    }

                    // With the metadata left in clear and the catalog inside an object stream this step
                    // cannot read yet, which stream is the document's metadata is not known here: a metadata
                    // stream waits for step 2 to find the catalog, rather than being decrypted while it may
                    // be the plaintext one (AES-CBC over plaintext either throws or empties it).
                    if (!encryptMetadata && metadataObjNum < 0 && obj is PdfStream metadataCandidate
                        && metadataCandidate.GetName(PdfName.Type) == PdfName.Metadata
                        && !Core.IO.Filters.StreamCryptFilter.TryGetName(metadataCandidate, out _))
                    {
                        deferredMetadata.Add(entry.ObjectNumber);
                        decryptedObjects[entry.ObjectNumber] = obj;
                        continue;
                    }

                    // Keyed by the object's own identifier — the `N G obj` header PdfParser read and stamped on it
                    // (ISO 32000-2 §7.6.3.2, Algorithm 1 step (a)). This passed a literal 0, so a top-level array or
                    // string at generation 1 — the generation §7.5.4 gives a reused object number — decrypted under
                    // a key that belongs to no object in the file.
                    // A stream naming its own crypt filter (§7.4.10) is decrypted under that filter — /Identity
                    // for one left in the clear — and written without it, since the output is decrypted.
                    EncryptionAlgorithm streamAlgorithm = stmAlgorithm;
                    PdfName ownName = null;
                    bool ownFilter = obj is PdfStream withFilter
                                     && Core.IO.Filters.StreamCryptFilter.TryGetName(withFilter, out ownName);
                    if (ownFilter)
                    {
                        streamAlgorithm = OwnCryptFilter(ownName) ?? stmAlgorithm;
                        streamsWithOwnFilter.Add(entry.ObjectNumber);
                    }
                    var decrypted = DecryptObject(obj, entry.ObjectNumber, ObjectIdentity.GenerationOf(obj), encryptionKey, streamAlgorithm, strAlgorithm, r);
                    if (ownFilter && decrypted is PdfStream decryptedStream)
                        Core.IO.Filters.StreamCryptFilter.Remove(decryptedStream);
                    decryptedObjects[entry.ObjectNumber] = decrypted;
                }

                // Step 2: Extract objects from decrypted object streams.
                // Now that ObjStm streams have been decrypted, we can decompress
                // their data and parse the individual objects contained within.
                //
                // Only the copies the merged cross-reference table still points at. ISO 32000-2 §7.5.6: "the most
                // recent copy of each object shall be the one accessed from the PDF file". An object stream keeps
                // every object it was written with, so writing ALL of them over what step 1 decrypted would bring
                // a page a later revision rewrote at the top level, moved into a newer object stream or deleted
                // back as the older revision left it. PdfEncryptedRoundTrip re-encrypts this output and
                // PdfEncryptedIncrementalUpdate plans the next revision from it, so the later change would be
                // lost from the file itself.
                //
                // Every way a stream can fail to yield its members is recorded here, once, with its reason —
                // including the two skips below and the refusals ExtractObjectsFromStream makes through its own
                // slot, which the next stream's clean expansion would otherwise clear. The per-object pass after
                // this loop would report the members as absent, but as members that "did not yield", without the
                // reason and one drop per member rather than one per cause; a stream recorded here is exempted
                // from it.
                var unexpandedStreams = new HashSet<int>();
                foreach (int objStmNum in objectStreamNumbers)
                {
                    if (!decryptedObjects.TryGetValue(objStmNum, out var objStmObj))
                    {
                        RecordDrop(
                            $"Object stream {objStmNum} is named by a compressed cross-reference entry but has no " +
                            "readable object of its own; every object it contained is absent from the output.");
                        unexpandedStreams.Add(objStmNum);
                        continue;
                    }

                    if (!(objStmObj is PdfStream objStmStream))
                    {
                        RecordDrop(
                            $"Object stream {objStmNum} is not a stream (it is a {objStmObj.GetType().Name}); " +
                            "every object it contained is absent from the output.");
                        unexpandedStreams.Add(objStmNum);
                        continue;
                    }

                    try
                    {
                        ExtractObjectsFromStream(objStmStream, decryptedObjects,
                            (objNum, index) => IsCurrentObjectStreamMember(xrefTable, objStmNum, objNum, index),
                            doc.Parser);

                        // A refusal is a return with the slot set — the per-call contract direct callers rely
                        // on. Folded into the accounting the moment it is made, because the next call clears it.
                        string refused = LastObjectStreamRejection;
                        if (refused != null)
                        {
                            RecordDrop($"Object stream {objStmNum}: {refused}");
                            unexpandedStreams.Add(objStmNum);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Report it. The loud, bounded refusals inside ExtractObjectsFromStream set
                        // LastObjectStreamRejection; this path - a throw part-way through the index
                        // - must too, or the removal below drops every object the stream contained
                        // (possibly the catalog and the whole page tree) and Decrypt returns a
                        // syntactically valid but eviscerated PDF with nothing said.
                        RecordDrop(
                            $"Object stream {objStmNum} could not be expanded ({ex.GetType().Name}: " +
                            $"{ex.Message}); every object it contained is absent from the output.");
                        unexpandedStreams.Add(objStmNum);
                    }
                }

                // The metadata streams step 1 left waiting: the catalog, now read, names the document's own,
                // which stays as it is; every other is a page's, an image's or a font's, encrypted as the rest
                // (Table 20 exempts the document-level stream alone).
                if (deferredMetadata.Count > 0)
                {
                    int documentMetadata = -1;
                    if (doc.Trailer[PdfName.Root] is PdfReference rootReference
                        && decryptedObjects.TryGetValue(rootReference.ReferenceObjectNumber, out PdfObject catalogObject)
                        && catalogObject is PdfDictionary catalogDictionary
                        && catalogDictionary[PdfName.Metadata] is PdfReference documentMetadataReference)
                        documentMetadata = documentMetadataReference.ReferenceObjectNumber;
                    foreach (int n in deferredMetadata)
                    {
                        if (n == documentMetadata) continue;
                        PdfObject waiting = decryptedObjects[n];
                        decryptedObjects[n] = DecryptObject(waiting, n, ObjectIdentity.GenerationOf(waiting), encryptionKey, stmAlgorithm, strAlgorithm, r);
                    }
                }

                // An embedded file stream decrypts under /EFF's crypt filter (§7.6.5), not /StmF's: the two
                // differ in a document that encrypts only its attachments. Which streams are embedded files
                // is a question about the decrypted objects — one names itself /EmbeddedFile, or a file
                // specification's /EF names it, and that specification may have been inside an object
                // stream — so every object above went under /StmF, and where /EFF differs each embedded
                // file is decrypted again, from the file, under /EFF. The encryptor asks the same question.
                if (effAlgorithm != stmAlgorithm)
                {
                    var embeddedFiles = EmbeddedFileStreams.ReferencedBy(decryptedObjects);
                    foreach (var kv in decryptedObjects)
                        if (EmbeddedFileStreams.NamesItself(kv.Value)) embeddedFiles.Add(kv.Key);
                    foreach (int n in embeddedFiles)
                    {
                        // A stream's own crypt filter decides over /EFF, as it does over /StmF (§7.4.10).
                        if (!decryptedObjects.ContainsKey(n) || streamsWithOwnFilter.Contains(n)) continue;
                        if (!(doc.Parser.GetObject(n) is PdfStream original)) continue;
                        decryptedObjects[n] = DecryptObject(original, n, ObjectIdentity.GenerationOf(original), encryptionKey,
                                                            effAlgorithm, strAlgorithm, r);
                    }
                }

                // Every object the table places in an object stream must now be in the output. Taking only current
                // copies is itself a way for one to be missing — its entry names an index where some other object is
                // stored, which PdfParser.ParseObjectFromStream refuses in the same way — and an object missing from
                // the output is reported, never left for the caller to infer. A stream whose expansion threw has
                // already said that everything it held is absent.
                foreach (var entry in xrefTable.AllEntries)
                {
                    if (entry.Type != CrossReferenceEntry.EntryType.Compressed
                        || unexpandedStreams.Contains(entry.ObjectStreamNumber)
                        || decryptedObjects.ContainsKey(entry.ObjectNumber))
                        continue;

                    RecordDrop(
                        $"Object {entry.ObjectNumber} is placed by its cross-reference entry at index " +
                        $"{entry.IndexInObjectStream} of object stream {entry.ObjectStreamNumber}, which did not " +
                        "yield it there; it is absent from the output.");
                }

                // The document-level answer to "was anything dropped?". Written after the
                // extraction loop because ExtractObjectsFromStream clears the slot on entry (its
                // own per-call contract), so anything recorded earlier would be erased.
                ComposeDropDiagnostic(firstDropReason, dropCount);

                // Remove object streams from the output — their contained objects
                // are written as regular indirect objects. (The cross-reference stream never
                // gets in: step 1 skips it.)
                foreach (int objStmNum in objectStreamNumbers)
                    decryptedObjects.Remove(objStmNum);

                // A clean trailer: everything the source's carried except what belonged to its own
                // cross-reference section or its protection — one rule with the encryptors, see TrailerRewrite.
                var newTrailer = new PdfDictionary();
                foreach (var kvp in doc.Trailer)
                {
                    if (TrailerRewrite.IsNotCarried(kvp.Key)) continue;
                    newTrailer.Set(kvp.Key, kvp.Value);
                }

                // /Size must be one more than the highest object number
                int maxObjNum = 0;
                foreach (var kvp in decryptedObjects)
                {
                    if (kvp.Key > maxObjNum)
                        maxObjNum = kvp.Key;
                }
                newTrailer.Set(PdfName.Size, new PdfNumber(maxObjNum + 1));

                // Write the output PDF
                var writer = new PdfWriter(output);
                writer.WriteHeader(doc.Version);

                // Each object at the generation it was read at, in its header and in its cross-reference entry
                // — see ObjectIdentity for why neither may be reset to 0.
                var xrefEntries = new List<(int objNum, int gen, long offset)>();
                foreach (var kvp in decryptedObjects.OrderBy(x => x.Key))
                {
                    int generation = generations.TryGetValue(kvp.Key, out int read) ? read : 0;
                    long offset = writer.WriteIndirectObject(kvp.Key, generation, kvp.Value);
                    xrefEntries.Add((kvp.Key, generation, offset));
                }

                long xrefOffset = writer.WriteXrefTable(xrefEntries, 0);
                writer.WriteTrailer(newTrailer, xrefOffset);
            }
        }

        /// <summary>
        /// Extracts encryption parameters from an encrypted PDF for fast password testing.
        /// Parse once, then call TryPassword() repeatedly without re-parsing the PDF.
        /// </summary>
        public static EncryptionInfo GetEncryptionInfo(string filePath)
        {
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                return GetEncryptionInfo(stream);
        }

        /// <summary>
        /// Extracts encryption parameters from an encrypted PDF stream.
        /// </summary>
        public static EncryptionInfo GetEncryptionInfo(Stream input)
        {
            // Buffered() rewinds a seekable input BEFORE copying: copied from its current position, a
            // FileStream a host had already read from would be a truncated, corrupt document.
            MemoryStream ms = SeekableStream.Buffered(input);

            using (var doc = PdfDocument.Open(ms))
            {
                if (!doc.IsEncrypted)
                    throw new PdfException("The input PDF is not encrypted.");

                var encryptDict = doc.EncryptionDictionary;
                if (encryptDict == null)
                    throw new PdfException("Cannot find /Encrypt dictionary in encrypted PDF.");

                bool encryptMetadata = true;
                var encMetaObj = Res(doc, encryptDict, PdfName.EncryptMetadata);
                if (encMetaObj is PdfBoolean emBool)
                    encryptMetadata = emBool.Value;

                return new EncryptionInfo
                {
                    Filter = (Res(doc, encryptDict, PdfName.Filter) as PdfName)?.Value,
                    V = ResInt(doc, encryptDict, PdfName.V) ?? 0,
                    R = ResInt(doc, encryptDict, PdfName.R) ?? 0,
                    KeyLengthBits = KeyLengthBitsFor(ResInt(doc, encryptDict, PdfName.V) ?? 0, ResInt(doc, encryptDict, PdfName.Length)),
                    // PdfPermissionValue.FromNumber, not Read: /P is written unsigned by real
                    // producers and GetInteger SATURATES to int.MaxValue, reporting every
                    // permission as granted. FromNumber is the overload for a caller that has
                    // already resolved — Read deliberately does not, having no document. 0 remains
                    // the "unreadable" fallback here because this struct has no way to say "absent".
                    PValue = PdfPermissionValue.FromNumber(Res(doc, encryptDict, PdfName.P)) ?? 0,
                    OValue = GetBytesFromDict(encryptDict, PdfName.O),
                    UValue = GetBytesFromDict(encryptDict, PdfName.U),
                    FileId = GetFileId(doc, doc.Trailer),
                    EncryptMetadata = encryptMetadata,
                    EncryptDict = encryptDict,
                    StdCryptFilterMethod = ReadStdCryptFilterMethod(doc, encryptDict),
                    TrailerHasAuthCode = PdfMacTokenSeal.TrailerHasToken(doc.Trailer)
                };
            }
        }

        /// <summary>
        /// Tests whether a password is valid for the given encryption info.
        /// This is the fast path for brute force — no PDF re-parsing, no object decryption.
        /// </summary>
        /// <exception cref="PdfException">
        /// The V&lt;5 <c>/Encrypt</c> dictionary carries no <c>/O</c> entry, so no key can be
        /// derived for any password. Thrown rather than returned as <c>false</c> because a
        /// caller iterating a password list must stop, not keep trying: the refusal is a fact
        /// about the document, not about the password.
        /// </exception>
        /// <remarks>
        /// <para>
        /// <b>An empty-user-password document has no discoverable password, and this method now
        /// says so by returning false.</b> The <c>Decrypt</c> path deliberately falls back to the
        /// empty user password when nothing else authenticates — opening the document is its goal
        /// — but that fallback made this method return <c>true</c> for the FIRST candidate a
        /// brute-force caller tried on any owner-only document (encrypted, opens with no password,
        /// permissions restricted). The caller then reported that candidate as the owner password.
        /// Only a genuine user- or owner-password match counts here.
        /// </para>
        /// </remarks>
        public static bool TryPassword(EncryptionInfo info, string password)
        {
            if (password == null) password = "";
            // Normalised, when the revision calls for it, inside ValidateAndDeriveAes256Key — see
            // the note there and in Decrypt. A revision 2–4 candidate is tested as its raw bytes.

            // Same per-document diagnostic reset as ResolveEncryption. This is the SECOND entry
            // point into the key-derivation code; without a reset, an AES-256 document's "OK" /Perms
            // verdict would still be in the slot when the next call tested an RC4 document — where
            // /Perms does not apply and is never checked — and a caller following
            // LastPermsValidation's documented rule would honour document B's attacker-supplied /P
            // bits on the strength of document A's verdict.
            BeginDocument(info.V);

            byte[] key;
            if (UsesAes256KeyDerivation(info.V, info.R))
            {
                key = ValidateAndDeriveAes256Key(password, info.OValue, info.UValue, info.EncryptDict, info.R, info.EncryptMetadata);
                return key != null;
            }

            key = ValidateAndDeriveKey(password, info.OValue, info.UValue,
                info.PValue, info.FileId, info.R, info.KeyLengthBits, info.EncryptMetadata,
                out AuthenticatedCredential matched);

            return key != null
                && (matched == AuthenticatedCredential.User || matched == AuthenticatedCredential.Owner);
        }

        /// <summary>
        /// Which of the document's two passwords <paramref name="password"/> is — the user's, which
        /// opens the document under its <c>/P</c> permissions (ISO 32000-2 §7.6.4.2), or the owner's,
        /// which opens it unrestricted — or <see cref="PdfPasswordRole.None"/> when it is neither. The
        /// empty string is the user password of a document that opens without one. As
        /// <see cref="TryPassword"/>, a fact about the password alone: nothing is decrypted.
        /// </summary>
        /// <remarks>
        /// A host honouring <c>/P</c> needs this beside <see cref="TryPassword"/>, which answers "does
        /// it open" for both passwords alike: the same bits bind a user and leave the owner alone, so
        /// a viewer that knew only that the file opened either restricted the owner or freed the user.
        /// Under AES-256 the slot <see cref="LastPermsValidation"/> is set by this call too, so a caller
        /// reads it right after, as it would after <see cref="Decrypt(Stream, Stream, string)"/>.
        /// </remarks>
        /// <exception cref="PdfException">As <see cref="TryPassword"/>: the dictionary derives no key for any password.</exception>
        public static PdfPasswordRole IdentifyPassword(EncryptionInfo info, string password)
        {
            if (info == null) throw new ArgumentNullException(nameof(info));
            if (password == null) password = "";
            BeginDocument(info.V);

            AuthenticatedCredential matched;
            if (UsesAes256KeyDerivation(info.V, info.R))
            {
                byte[] key = ValidateAndDeriveAes256Key(password, info.OValue, info.UValue, info.EncryptDict, info.R, info.EncryptMetadata);
                matched = key == null ? AuthenticatedCredential.None : LastAuthenticatedCredential;
            }
            else
            {
                byte[] key = ValidateAndDeriveKey(password, info.OValue, info.UValue,
                    info.PValue, info.FileId, info.R, info.KeyLengthBits, info.EncryptMetadata, out matched);
                if (key == null) matched = AuthenticatedCredential.None;
            }

            return matched == AuthenticatedCredential.Owner ? PdfPasswordRole.Owner
                : matched == AuthenticatedCredential.User ? PdfPasswordRole.User
                : PdfPasswordRole.None;
        }

        /// <summary>
        /// Which credential the last document on this thread opened with — set by the revision-4-or-lower
        /// derivation and, as <see cref="AuthenticatedCredential.User"/> or <see cref="AuthenticatedCredential.Owner"/>,
        /// by the AES-256 one at the <c>/U</c> or <c>/O</c> it matched: the round trip's
        /// <c>ReuseOpenPassword</c> asks whether the caller's password opened the file or the empty user
        /// password did, so it writes the output under the one that did, and <see cref="IdentifyPassword"/>
        /// tells a host which password it holds.
        /// </summary>
        [ThreadStatic] private static AuthenticatedCredential _lastAuthenticatedCredential;

        /// <inheritdoc cref="_lastAuthenticatedCredential"/>
        internal static AuthenticatedCredential LastAuthenticatedCredential
        {
            get => _lastAuthenticatedCredential;
            private set => _lastAuthenticatedCredential = value;
        }

        /// <summary>Which credential authenticated in <see cref="ValidateAndDeriveKey"/>.</summary>
        internal enum AuthenticatedCredential
        {
            /// <summary>Nothing matched; no key was derived.</summary>
            None,
            /// <summary>The supplied password reproduced <c>/U</c> as the user password.</summary>
            User,
            /// <summary>The supplied password recovered the user password from <c>/O</c>.</summary>
            Owner,
            /// <summary>
            /// Nothing the caller supplied matched, but the EMPTY user password did — the document
            /// opens without a password. Enough to decrypt, never enough to report the caller's
            /// candidate as correct.
            /// </summary>
            EmptyUserFallback,
        }

        /// <summary>
        /// The file key's length in bits: 128 under /V 4, whatever /Length says or omits (ISO 32000-2
        /// Table 20: /Length is "only if V is 2 or 3", and V 4 is "a file encryption key length of 128
        /// bits"); otherwise /Length, 40 when absent.
        /// </summary>
        private static int KeyLengthBitsFor(int v, int? declared)
            => v == VersionCryptFilters ? MaxKeyLengthBitsBelowV5 : declared ?? DefaultKeyLengthBits;

        /// <summary>
        /// Resets the per-document diagnostic slots that a caller reads AFTER an operation.
        /// <para>
        /// <c>[ThreadStatic]</c> gives per-thread isolation, not per-document scope — the
        /// distinction CLAUDE.md records <c>LastPermsValidation</c> as the cautionary tale for. A
        /// slot must therefore be cleared at the START of the operation that writes it, on every
        /// path, which is why both entry points into key derivation call this rather than each
        /// resetting the subset it happens to remember.
        /// </para>
        /// </summary>
        internal static void BeginDocument(int v)
        {
            // /Perms validation is a V=5 concept (ISO 32000-2 §7.6.4.3.4, Algorithm 11), so it is
            // reached only from the AES-256 key-derivation path and never runs for RC4 / AES-128.
            // Say that plainly rather than leaving the previous document's verdict in place.
            LastPermsValidation = v < VersionAes256 ? "SkippedNonAes256Key" : null;
            LastAuthenticatedCredential = AuthenticatedCredential.None;

            // "Was anything dropped from THIS document?" - so it is cleared here, not left
            // holding the previous document's answer. ExtractObjectsFromStream additionally
            // clears it per call, which is its own documented contract for direct callers.
            LastObjectStreamRejection = null;

            // Cleared once per document, NOT once per AES block: AesCbcDecrypt is called once per
            // encrypted string and once per stream, so a per-block reset would leave the slot
            // describing only the LAST block, and an early object that needed the no-padding
            // fallback — the one event the slot exists to surface — would be erased by every clean
            // object after it.
            LastAesCbcPaddingFallback = null;
            _lastAesCbcMalformedLength = null;
            _aesCbcMalformedFirst = null;
            _aesCbcMalformedCount = 0;

            // Cleared here rather than only on the V=5 branch that writes it. Its own reset lived
            // inside ValidateAndDeriveAes256Key, so decrypting an RC4 or AES-128 document after a
            // V=5 one whose key derivation failed left the PREVIOUS document's message in the slot
            // — the same per-thread-is-not-per-document confusion this method's summary describes.
            LastV5KeyDerivationIssue = null;

            // Cleared here for a second reason as well as that one: ResolveCryptFilter nulls this on
            // entry and is called twice in a row (/StmF then /StrF), so a /StmF fallback message was
            // destroyed whenever /StrF resolved cleanly — including the very common case of /StrF
            // being absent or /Identity, which returns immediately after the reset. The two calls
            // now ACCUMULATE into the slot and only this method clears it.
            LastCryptFilterFallback = null;
        }

        #region Password Validation

        /// <summary>
        /// Everything a caller needs to encrypt or decrypt one document's objects: the file
        /// encryption key and the algorithm chosen for streams and for strings (which differ when
        /// the document uses crypt filters and sets one of them to <c>/Identity</c>).
        /// </summary>
        internal sealed class ResolvedEncryption
        {
            internal byte[] Key;
            internal EncryptionAlgorithm StreamAlgorithm;
            internal EncryptionAlgorithm StringAlgorithm;
            /// <summary>
            /// The algorithm for embedded file streams: <c>/EFF</c>'s crypt filter (§7.6.5), else the
            /// stream algorithm. It differs where a document encrypts only its attachments.
            /// </summary>
            internal EncryptionAlgorithm EmbeddedFileAlgorithm;
            /// <summary>
            /// A stream's own crypt filter (§7.4.10), by the name its <c>/Crypt</c> filter gives:
            /// <c>null</c> for a name the document does not define. Never null itself.
            /// </summary>
            internal Func<PdfName, EncryptionAlgorithm?> StreamCryptFilter = name =>
                name == PdfName.Identity ? EncryptionAlgorithm.Identity : (EncryptionAlgorithm?)null;
            internal int Version;
            internal int Revision;
            internal bool EncryptMetadata;
            /// <summary>
            /// The public-key handler's permissions as the opened recipient envelope holds them
            /// (ISO 32000-2 Table 24), <c>null</c> under the standard handler. The envelope is
            /// hashed into the file key, so this word is the recipient's own copy; the
            /// dictionary's <c>/P</c> is plain text.
            /// </summary>
            internal int? EnvelopePermissions;
        }

        /// <summary>
        /// Validates the password against the document's <c>/Encrypt</c> dictionary and derives
        /// the file encryption key, together with the stream and string algorithms.
        ///
        /// <para>Shared by <see cref="Decrypt(Stream, Stream, string)"/> and by
        /// <see cref="PdfIncrementalEncryption"/>, which needs the same key in the <i>encrypt</i>
        /// direction to append a revision to an encrypted document. One implementation, because
        /// the V/R branch and the attacker-controlled <c>/Length</c> validation are exactly the
        /// kind of security logic that must not exist twice.</para>
        /// </summary>
        /// <exception cref="PdfException">
        /// The document is not encrypted, uses a non-<c>/Standard</c> handler, declares an
        /// unsupported or malformed configuration, or the password is wrong.
        /// </exception>
        internal static ResolvedEncryption ResolveEncryption(PdfDocument doc, string password)
        {
            if (!doc.IsEncrypted)
                throw new PdfException("The input PDF is not encrypted.");

            var encryptDict = doc.EncryptionDictionary;
            if (encryptDict == null)
                throw new PdfException("Cannot find /Encrypt dictionary in encrypted PDF.");

            // Validate encryption handler — only /Standard is supported. Resolved, as GetEncryptionInfo
            // and PdfPublicKeyDecryptor already read it: read raw, an indirect /Adobe.PubSec passed
            // this check as if it were /Standard and failed later as a wrong password.
            var handlerFilter = Res(doc, encryptDict, PdfName.Filter) as PdfName;
            if (handlerFilter != null && handlerFilter != PdfName.Standard)
                throw new PdfException(
                    $"Unsupported encryption handler: /{handlerFilter.Value}. " +
                    "Only the /Standard security handler is supported.");

            // Read encryption parameters
            int v = ResInt(doc, encryptDict, PdfName.V) ?? 0;
            int r = ResInt(doc, encryptDict, PdfName.R) ?? 0;
            int keyLengthBits = KeyLengthBitsFor(v, ResInt(doc, encryptDict, PdfName.Length));
            // Algorithm 2 hashes the FOUR BYTES of /P, so a saturated read derives the wrong
            // file key and a correct password is rejected — this is not a permissions question.
            int pValue = PdfPermissionValue.FromNumber(Res(doc, encryptDict, PdfName.P)) ?? 0;

            // Reset the per-document diagnostic slots. Without this they kept the previous
            // document's answers: decrypt an AES-256 file (-> /Perms "OK"), then an RC4 file, and
            // the slot still reads "OK". The contract documented on LastPermsValidation tells
            // security-sensitive callers to refuse to honour /P bits unless it says "OK" — a stale
            // "OK" from an unrelated file defeats exactly that check, which is the one thing the
            // diagnostic exists for. TryPassword reaches the same key-derivation code and calls
            // the same helper, so the two entry points cannot drift again.
            BeginDocument(v);

            // Validate the attacker-controlled /Length (V<5). An out-of-range value
            // otherwise drives `new byte[keyLen/8]` (negative → exception, or ~250 MB →
            // OOM) or an MD5 ComputeHash with an out-of-bounds count — an uncaught crash
            // out of document open. The standard handler's key derives from a 16-byte MD5
            // digest, so the real ceiling for V<5 is 128 bits (RC4-40..RC4-128 / AES-128);
            // a value in 136..256 would read past the digest and throw an uncaught
            // ArgumentException. AES-256 (V5) fixes the key at 256 bits, ignoring /Length.
            if (v < VersionAes256)
            {
                if (keyLengthBits <= 0) keyLengthBits = DefaultKeyLengthBits;
                if (keyLengthBits % PdfEncryptionPrimitives.BitsPerByte != 0
                    || keyLengthBits < DefaultKeyLengthBits
                    || keyLengthBits > MaxKeyLengthBitsBelowV5)
                    throw new PdfException(
                        $"Invalid /Length in encryption dictionary: {keyLengthBits} bits.");
            }

            byte[] oValue = GetBytesFromDict(encryptDict, PdfName.O);
            byte[] uValue = GetBytesFromDict(encryptDict, PdfName.U);

            // Get file ID
            byte[] fileId = GetFileId(doc, doc.Trailer);

            // Read EncryptMetadata flag (default true per spec)
            bool encryptMetadata = true;
            var encMetaObj = Res(doc, encryptDict, PdfName.EncryptMetadata);
            if (encMetaObj is PdfBoolean emBool)
                encryptMetadata = emBool.Value;

            // Determine algorithm from V/R and crypt filters
            EncryptionAlgorithm stmAlgorithm, strAlgorithm;
            EncryptionAlgorithm? effAlgorithm = null;
            if (v >= VersionCryptFilters)
            {
                // V=4/V=5 use crypt filters to determine the actual algorithm
                stmAlgorithm = ResolveCryptFilter(doc, encryptDict, PdfName.StmF);
                strAlgorithm = ResolveCryptFilter(doc, encryptDict, PdfName.StrF);
                // §7.6.5: /EFF names the embedded files' crypt filter, defaulting to /StmF's — an
                // absent /EFF is not /Identity, as an absent /StmF is.
                if (Res(doc, encryptDict, PdfName.EFF) != null)
                    effAlgorithm = ResolveCryptFilter(doc, encryptDict, PdfName.EFF);
            }
            else if (v == VersionRc4Variable
                     && (r == PdfEncryptionPrimitives.RevisionRc4Basic
                      || r == PdfEncryptionPrimitives.RevisionRc4Variable))
            {
                stmAlgorithm = strAlgorithm = EncryptionAlgorithm.RC4_128;
            }
            else if (v == VersionRc4_40)
            {
                // /V 1 under revision 3 is RC4_40R3 — the same cipher and key length, but the member
                // the writer side distinguishes, so a reader and an incremental update carry the
                // revision they read rather than every /V 1 as revision 2.
                stmAlgorithm = strAlgorithm = r >= PdfEncryptionPrimitives.RevisionRc4Variable
                    ? EncryptionAlgorithm.RC4_40R3
                    : EncryptionAlgorithm.RC4_40;
            }
            else
            {
                throw new PdfException($"Unsupported encryption: V={v}, R={r}");
            }

            // Validate password and derive encryption key
            byte[] encryptionKey;
            if (UsesAes256KeyDerivation(v, r))
            {
                encryptionKey = ValidateAndDeriveAes256Key(password, oValue, uValue, encryptDict, r, encryptMetadata);
            }
            else
            {
                encryptionKey = ValidateAndDeriveKey(password, oValue, uValue, pValue, fileId, r,
                    keyLengthBits, encryptMetadata, out AuthenticatedCredential matched);
                LastAuthenticatedCredential = matched;
            }

            if (encryptionKey == null)
                throw new PdfException("Invalid password. Cannot decrypt the PDF.");

            return new ResolvedEncryption
            {
                Key = encryptionKey,
                StreamAlgorithm = stmAlgorithm,
                StringAlgorithm = strAlgorithm,
                EmbeddedFileAlgorithm = effAlgorithm ?? stmAlgorithm,
                StreamCryptFilter = v >= VersionCryptFilters
                    ? name => ResolveStreamCryptFilter(doc, encryptDict, name)
                    : name => name == PdfName.Identity ? EncryptionAlgorithm.Identity : (EncryptionAlgorithm?)null,
                Version = v,
                Revision = r,
                EncryptMetadata = encryptMetadata,
            };
        }

        /// <summary>
        /// Validates <paramref name="password"/> against <c>/O</c> and <c>/U</c> and derives the
        /// file encryption key, reporting through <paramref name="matched"/> WHICH credential
        /// authenticated — the caller that only wants to open the document ignores it, and the
        /// caller asking "is this the right password?" must not.
        /// </summary>
        private static byte[] ValidateAndDeriveKey(string password, byte[] oValue, byte[] uValue,
            int pValue, byte[] fileId, int r, int keyLengthBits, bool encryptMetadata,
            out AuthenticatedCredential matched)
        {
            matched = AuthenticatedCredential.None;

            // §7.6.3.3 Algorithm 2 step (c) feeds the 32-byte /O entry into the key MD5, so a
            // document without one has no derivable key. GetBytesFromDict returns null for an
            // absent /O *and* for an /O that is not a string, and nothing between there and
            // ComputeEncryptionKey looked at it — `md5.TransformBlock(oValue, 0, oValue.Length, …)`
            // then dereferenced null, so a 400-byte crafted /Encrypt with no /O threw a raw
            // NullReferenceException out of EVERY entry point (Decrypt, TryPassword,
            // PdfIncrementalEncryption.TryCreate — whose contract is to return false — and
            // PdfEncryptedRoundTrip.Run, documented as reporting rather than throwing). The
            // missing /U case was already handled: PasswordMatches simply returns false.
            //
            // Refusing beats substituting an empty /O (which some readers hash): a file with
            // no /O cannot have had a key derived from one, so "no password matches" would be a
            // guess dressed as an answer. This is the ONE choke point both the Decrypt path
            // (ResolveEncryption) and the brute-force fast path (TryPassword) pass through, so
            // the two cannot diverge on it.
            if (oValue == null)
                throw new PdfException(
                    "The /Encrypt dictionary has no /O entry (or its /O is not a string), so the " +
                    "standard security handler's encryption key cannot be derived. The document " +
                    "is malformed and no password can open it.");

            // Defensive clamp: the standard security handler's key is sliced from a 16-byte MD5
            // digest, so keyLenBytes can never legitimately exceed 16 (or be < 5 / 40 bits). The
            // GetEncryptionInfo + TryPassword fast-brute-force path does NOT run the Decrypt-path
            // /Length validation, so an attacker /Length (e.g. 136 or 2e9 bits) would otherwise
            // drive an out-of-bounds Buffer.BlockCopy (or a huge allocation) in
            // RecoverUserPasswordFromOwner below. Mirrors the §86 clamp in ComputeEncryptionKey.
            int keyLenBytes = keyLengthBits / PdfEncryptionPrimitives.BitsPerByte;
            if (keyLenBytes < PdfEncryptionPrimitives.MinKeyBytes)
                keyLenBytes = PdfEncryptionPrimitives.MinKeyBytes;
            else if (keyLenBytes > PdfEncryptionPrimitives.MaxKeyBytes)
                keyLenBytes = PdfEncryptionPrimitives.MaxKeyBytes;

            // Try as user password
            byte[] userKey = PdfEncryptionPrimitives.ComputeEncryptionKey(password, oValue, pValue, fileId, r, keyLengthBits, encryptMetadata);
            byte[] computedU = PdfEncryptionPrimitives.ComputeUserValue(userKey, fileId, r);

            if (PasswordMatches(computedU, uValue, r))
            {
                matched = AuthenticatedCredential.User;
                return userKey;
            }

            // Try as owner password: derive the user password from /O value, then validate
            byte[] userPwdFromOwner = RecoverUserPasswordFromOwner(password, oValue, r, keyLenBytes);
            string recoveredPwd = Core.Text.Latin1Text.Decode(userPwdFromOwner).TrimEnd('\0');
            // Remove padding
            int padIdx = -1;
            for (int i = 0; i < userPwdFromOwner.Length; i++)
            {
                if (i < PdfEncryptionPrimitives.PaddingBytes.Length && userPwdFromOwner[i] == PdfEncryptionPrimitives.PaddingBytes[0])
                {
                    bool match = true;
                    for (int j = 0; j + i < userPwdFromOwner.Length && j < PdfEncryptionPrimitives.PaddingBytes.Length; j++)
                    {
                        if (userPwdFromOwner[i + j] != PdfEncryptionPrimitives.PaddingBytes[j]) { match = false; break; }
                    }
                    if (match) { padIdx = i; break; }
                }
            }
            if (padIdx >= 0)
                recoveredPwd = Core.Text.Latin1Text.Decode(userPwdFromOwner, 0, padIdx);

            // Now try with the recovered user password
            byte[] ownerKey = PdfEncryptionPrimitives.ComputeEncryptionKey(recoveredPwd, oValue, pValue, fileId, r, keyLengthBits, encryptMetadata);
            byte[] ownerComputedU = PdfEncryptionPrimitives.ComputeUserValue(ownerKey, fileId, r);

            if (PasswordMatches(ownerComputedU, uValue, r))
            {
                matched = AuthenticatedCredential.Owner;
                return ownerKey;
            }

            // (No third attempt — "the owner password directly as user password": it would recompute
            // ComputeEncryptionKey with arguments identical to the user attempt above and re-run the
            // identical comparison, so it could never succeed where that one failed.)

            // The trailer had no /ID (or an empty one). §7.6.3.3 Algorithm 2 step (e) passes the
            // first element of the file identifier to MD5, and with no array there is nothing to
            // pass — conformant readers hash zero bytes, which is what GetFileId now
            // returns. Sixteen 0x00 bytes was this library's own historical substitute, so a file
            // it produced that way (or another implementation making the same choice) would stop
            // opening if the empty form were the only attempt. Try the interoperable form first,
            // the legacy substitute second — never silently pick the non-interoperable one.
            if (fileId != null && fileId.Length == 0)
            {
                byte[] legacyId = new byte[LegacyMissingFileIdLength];
                byte[] legacyKey = PdfEncryptionPrimitives.ComputeEncryptionKey(
                    password, oValue, pValue, legacyId, r, keyLengthBits, encryptMetadata);
                if (PasswordMatches(PdfEncryptionPrimitives.ComputeUserValue(legacyKey, legacyId, r), uValue, r))
                {
                    matched = AuthenticatedCredential.User;
                    return legacyKey;
                }
            }

            // Try empty user password with owner password validation. This opens the very common
            // owner-only document (no user password, restricted permissions) — but it authenticates
            // the EMPTY password, not the caller's, so it is reported as its own outcome. Treating
            // it as success is what made TryPassword return true for every candidate on such a file.
            byte[] emptyKey = PdfEncryptionPrimitives.ComputeEncryptionKey("", oValue, pValue, fileId, r, keyLengthBits, encryptMetadata);
            if (PasswordMatches(PdfEncryptionPrimitives.ComputeUserValue(emptyKey, fileId, r), uValue, r))
            {
                matched = AuthenticatedCredential.EmptyUserFallback;
                return emptyKey;
            }

            return null;
        }

        private static byte[] ValidateAndDeriveAes256Key(string password, byte[] oValue, byte[] uValue, PdfDictionary encryptDict, int revision, bool declaredEncryptMetadata)
        {
            // Reset per-call diagnostics so callers see only this attempt's outcome.
            LastV5KeyDerivationIssue = null;
            LastSupersededR6Derivation = null;

            // SASLprep (ISO 32000-2 §7.6.4.3.3) belongs HERE — the one V≥5 derivation site every
            // door (Decrypt, TryPassword, the credential, the MAC seal and verifier) reaches through
            // ResolveEncryption — and not at those doors, where it would also normalise a revision
            // 2–4 password. Those take the raw Latin-1 bytes (§7.6.3.3 Algorithm 2), so a no-break
            // space in an RC4 or AES-128 password would become a space before PadPassword and the
            // file's own password would be refused. PdfEncryptor.ComputeAes256Keys normalises the
            // same way, so the two sides derive from one string.
            password = PdfPasswordNormalizer.Normalize(password ?? string.Empty);

            byte[] pwdBytes = PdfEncryptionPrimitives.TruncateUtf8(
                password, PdfEncryptionPrimitives.MaxPasswordBytesR6);

            // Revision 5 (the deprecated Adobe extension) and revision 6 (ISO 32000-2 / PDF 2.0)
            // use DIFFERENT hash functions. R5 uses Algorithm 2.A: a single plain SHA-256 over
            // (password || salt [|| U]). R6 uses Algorithm 2.B: the iterative key-reinforcing hash
            // (ComputeHashR6). Running the R6 hash for an R5 document would never authenticate a
            // legitimate R5 password (and silently masks the distinction between the two formats),
            // so we branch on the revision and fail closed when R5 auth fails.
            byte[] Hash(byte[] salt, byte[] extra)
                => revision == RevisionAes256Deprecated
                    ? Sha256R5(pwdBytes, salt, extra)
                    : PdfEncryptionPrimitives.ComputeHashR6(pwdBytes, salt, extra);

            // Derives the 32-byte file key from /UE or /OE, mapping any failure to a
            // null return + diagnostic instead of letting an exception escape open.
            // /UE and /OE are EXACTLY 32 bytes per ISO 32000-2; a non-32 length would
            // otherwise make AesCbcNoPadding throw (33-47) or produce a 48-byte "key"
            // that AES later rejects.
            byte[] DeriveFileKey(string who, byte[] eValue, byte[] hashKey)
            {
                if (eValue == null || eValue.Length != PdfEncryptionPrimitives.FileKeyLength)
                {
                    LastV5KeyDerivationIssue = eValue == null
                        ? $"{who} password matched but the key-encryption entry is missing."
                        : $"{who} password matched but the key-encryption entry has length {eValue.Length} " +
                          $"(expected exactly {PdfEncryptionPrimitives.FileKeyLength}).";
                    return null;
                }
                try
                {
                    byte[] key = PdfEncryptionPrimitives.AesCbcNoPadding(
                        false, hashKey, new byte[PdfEncryptionPrimitives.AesBlockBytes], eValue);
                    if (key == null || key.Length != PdfEncryptionPrimitives.FileKeyLength)
                    {
                        LastV5KeyDerivationIssue = $"{who} password matched but the derived key length is invalid.";
                        return null;
                    }
                    ValidatePerms(encryptDict, key, declaredEncryptMetadata);
                    return key;
                }
                catch (Exception ex)
                {
                    LastV5KeyDerivationIssue = $"{who} password matched but key decryption failed: {ex.Message}";
                    return null;
                }
            }

            // Try as user password: validate against /U (Algorithm 2.B hash)
            if (uValue != null && uValue.Length >= PasswordEntryLength)
            {
                byte[] validationSalt = new byte[SaltLength];
                byte[] keySalt = new byte[SaltLength];
                Buffer.BlockCopy(uValue, ValidationSaltOffset, validationSalt, 0, SaltLength);
                Buffer.BlockCopy(uValue, KeySaltOffset, keySalt, 0, SaltLength);

                byte[] testHash = Hash(validationSalt, null);
                if (BytesMatch(testHash, 0, uValue, 0, PdfEncryptionPrimitives.FileKeyLength))
                {
                    LastAuthenticatedCredential = AuthenticatedCredential.User;
                    byte[] ueValue = GetBytesFromDict(encryptDict, PdfName.UE);
                    byte[] ueKey = Hash(keySalt, null);
                    return DeriveFileKey("User", ueValue, ueKey);
                }
            }

            // Try as owner password: validate against /O (Algorithm 2.B hash with /U as extra input).
            // The /U extra input MUST be the spec-defined 48 bytes — passing an attacker-oversized
            // /U through unbounded forces a 64×|U| per-iteration allocation in ComputeHashR6 (OOM).
            if (oValue != null && oValue.Length >= PasswordEntryLength)
            {
                byte[] u48;
                if (uValue == null) u48 = Array.Empty<byte>();
                else if (uValue.Length > PasswordEntryLength)
                {
                    u48 = new byte[PasswordEntryLength];
                    Buffer.BlockCopy(uValue, 0, u48, 0, PasswordEntryLength);
                }
                else u48 = uValue;

                byte[] validationSalt = new byte[SaltLength];
                byte[] keySalt = new byte[SaltLength];
                Buffer.BlockCopy(oValue, ValidationSaltOffset, validationSalt, 0, SaltLength);
                Buffer.BlockCopy(oValue, KeySaltOffset, keySalt, 0, SaltLength);

                byte[] testHash = Hash(validationSalt, u48);
                if (BytesMatch(testHash, 0, oValue, 0, PdfEncryptionPrimitives.FileKeyLength))
                {
                    LastAuthenticatedCredential = AuthenticatedCredential.Owner;
                    byte[] oeValue = GetBytesFromDict(encryptDict, PdfName.OE);
                    byte[] oeKey = Hash(keySalt, u48);
                    return DeriveFileKey("Owner", oeValue, oeKey);
                }
            }

            // Rejected. Before giving up, say whether this library's own superseded reading would
            // have accepted it — see LastSupersededR6Derivation. For R6 and R7: R7 (ISO/TS 32003)
            // reuses R6's Algorithm 2.B unchanged, so it can hit the identical historical
            // termination-offset ambiguity; R5 uses Algorithm 2.A and has no termination predicate
            // to have got wrong.
            if (revision == RevisionAes256 || revision == RevisionAesGcm)
                NoteSupersededR6Derivation(pwdBytes, oValue, uValue);

            return null;
        }

        /// <summary>
        /// Set when an R6 password was rejected but <b>would have been accepted by the off-by-one
        /// Algorithm 2.B termination reading</b>; <c>null</c> otherwise.
        /// </summary>
        /// <remarks>
        /// <para><b>What it means.</b> Stopping Algorithm 2.B one round late — the termination
        /// predicate read against the zero-based index rather than the round number — produces a
        /// different key for about one input in thirty-eight. Files written under that reading,
        /// including by earlier versions of this library, carry a <c>/U</c> and <c>/UE</c> pair that
        /// no conformant reader can authenticate, this one included. The external vectors in
        /// <c>HashR6ExternalVectorTests</c> settle which reading is right.</para>
        ///
        /// <para><b>Why it is a diagnostic and not a fallback.</b> Accepting the superseded hash
        /// would let this library go on opening files that are unopenable everywhere else, which
        /// hides the damage instead of reporting it. The affected file needs to be re-encrypted from
        /// a decrypted copy — and to do that a caller first has to know that is what happened,
        /// rather than seeing "wrong password" and trying again.</para>
        ///
        /// <para><b>The cost is one extra Algorithm 2.B, and only on a failed attempt.</b> A
        /// successful password never reaches this. It slows a password guesser by the same factor it
        /// slows a legitimate retry, so it is not a weakening.</para>
        ///
        /// <para><b>It is a strong signal, not proof of origin.</b> Any writer with the same
        /// off-by-one would produce the same file. What the message can honestly say is which
        /// reading authenticates it.</para>
        /// <para>Read on the calling thread; it is cleared at the start of every R6 attempt.</para>
        /// </remarks>
        public static string LastSupersededR6Derivation
        {
            get => _lastSupersededR6Derivation;
            private set => _lastSupersededR6Derivation = value;
        }

        [ThreadStatic]
        private static string _lastSupersededR6Derivation;

        /// <summary>
        /// After an R6 password has been rejected, checks whether the superseded reading would have
        /// accepted it, and records that if so.
        /// </summary>
        /// <remarks>
        /// Only the <b>validation</b> hashes are recomputed, not the key salts: the question is
        /// "was this password right under the off-by-one reading", and the key derivation that would
        /// follow is deliberately not performed.
        /// </remarks>
        private static void NoteSupersededR6Derivation(byte[] pwdBytes, byte[] oValue, byte[] uValue)
        {
            byte[] SupersededHash(byte[] salt, byte[] extra)
                => PdfEncryptionPrimitives.ComputeHashR6(
                    pwdBytes, salt, extra, PdfEncryptionPrimitives.HashR6SupersededTerminationOffset);

            string who = null;

            if (uValue != null && uValue.Length >= PasswordEntryLength)
            {
                var salt = new byte[SaltLength];
                Buffer.BlockCopy(uValue, ValidationSaltOffset, salt, 0, SaltLength);
                if (BytesMatch(SupersededHash(salt, null), 0, uValue, 0,
                               PdfEncryptionPrimitives.FileKeyLength))
                    who = "user";
            }

            if (who == null && oValue != null && oValue.Length >= PasswordEntryLength
                && uValue != null && uValue.Length >= PasswordEntryLength)
            {
                var u48 = new byte[PasswordEntryLength];
                Buffer.BlockCopy(uValue, 0, u48, 0, PasswordEntryLength);
                var salt = new byte[SaltLength];
                Buffer.BlockCopy(oValue, ValidationSaltOffset, salt, 0, SaltLength);
                if (BytesMatch(SupersededHash(salt, u48), 0, oValue, 0,
                               PdfEncryptionPrimitives.FileKeyLength))
                    who = "owner";
            }

            if (who == null) return;

            LastSupersededR6Derivation =
                $"The {who} password is correct under this library's SUPERSEDED Algorithm 2.B " +
                "termination reading, and wrong under the conformant one. This file was almost " +
                "certainly written by an older build of this library, whose R6 key derivation ran " +
                "one round too long for about one input in thirty-eight; no conformant reader " +
                "can open it either. It is not accepted here, because " +
                "accepting it would hide that. Recover the content with the build that wrote it and " +
                "re-encrypt.";
        }


        /// <summary>
        /// ISO 32000-1 Adobe extension level 3, Algorithm 2.A for revision 5 (deprecated):
        /// a single plain SHA-256 over (password || salt [|| 48-byte /U for owner operations]).
        /// Unlike revision 6 this does NOT use the iterative Algorithm 2.B hash.
        /// </summary>
        private static byte[] Sha256R5(byte[] password, byte[] salt, byte[] extra)
        {
            extra ??= Array.Empty<byte>();
            return PdfEncryptionPrimitives.Sha256Concat(password, salt, extra);
        }

        /// <summary>
        /// When V=5/R=6 password validation succeeds but the matching
        /// /UE (user) or /OE (owner) entry is missing or truncated,
        /// records the reason here.  <c>null</c> when key derivation
        /// completed cleanly or no V=5/R=6 attempt was made.  Lets callers
        /// distinguish "wrong password" (no diagnostic) from "password OK
        /// but /Encrypt dictionary is corrupt".
        /// </summary>
        public static string LastV5KeyDerivationIssue
        {
            get => _lastV5KeyDerivationIssue;
            private set => _lastV5KeyDerivationIssue = value;
        }

        [ThreadStatic]
        private static string _lastV5KeyDerivationIssue;

        /// <summary>
        /// Validates the V=5/R=6 <c>/Perms</c> field against the freshly-derived
        /// file encryption key per ISO 32000-2 §7.6.4.3.4 (Algorithm 11).
        /// /Perms is 16 bytes AES-ECB-encrypted; the plaintext bytes 9-11 are
        /// the ASCII tag "adb" (Adobe) when authentic.
        ///
        /// <para>Outcome is recorded on <see cref="LastPermsValidation"/> for
        /// auditing.  Failures are <b>not</b> thrown — the user has already
        /// authenticated via the password (which derived the file key), so
        /// killing the decrypt would lock legitimate users out when a
        /// PDF producer's /Perms encoding deviates slightly from the Adobe
        /// reference.  Security-sensitive callers should check
        /// <see cref="LastPermsValidation"/> after opening the document and
        /// refuse to honour /P bits when validation didn't say "OK".</para>
        /// </summary>
        private static void ValidatePerms(PdfDictionary encryptDict, byte[] fileKey, bool declaredEncryptMetadata)
        {
            LastPermsValidation = null;
            if (fileKey == null || fileKey.Length != PdfEncryptionPrimitives.FileKeyLength)
            {
                LastPermsValidation = "SkippedNonAes256Key";
                return;
            }
            byte[] permsCipher = GetBytesFromDict(encryptDict, PdfName.Perms);
            if (permsCipher == null || permsCipher.Length != PermsLength)
            {
                LastPermsValidation = "Absent";
                return;
            }

            byte[] permsPlain;
            try
            {
                using var aes = Aes.Create();
                aes.Mode    = CipherMode.ECB;
                aes.Padding = System.Security.Cryptography.PaddingMode.None;
                aes.Key     = fileKey;
                using var dec = aes.CreateDecryptor();
                permsPlain = dec.TransformFinalBlock(permsCipher, 0, permsCipher.Length);
            }
            catch (Exception ex)
            {
                LastPermsValidation = "DecryptFailed: " + ex.GetType().Name + ": " + ex.Message;
                return;
            }

            if (permsPlain.Length < PermsTagOffset + PermsTagLength ||
                permsPlain[PermsTagOffset]     != (byte)'a' ||
                permsPlain[PermsTagOffset + 1] != (byte)'d' ||
                permsPlain[PermsTagOffset + 2] != (byte)'b')
            {
                LastPermsValidation =
                    $"TagMismatch: got bytes {PermsTagOffset}-{PermsTagOffset + PermsTagLength - 1} = " +
                    $"0x{permsPlain[PermsTagOffset]:X2}{permsPlain[PermsTagOffset + 1]:X2}{permsPlain[PermsTagOffset + 2]:X2}; " +
                    "expected 'adb'.";
                return;
            }

            // The tag only proves the block decrypted. Authenticating /P is what Algorithm 11 is
            // FOR, and checking the tag alone leaves it undone: the file key derives from the password
            // and the /U + /O salts, never from /P, so rewriting /P in the plaintext /Encrypt
            // dictionary does not disturb decryption at all. A one-byte hex edit turning /P -3904
            // into /P -4 — every permission granted — would leave this method answering "OK", which
            // defeats a caller following LastPermsValidation's own documented rule to the letter.
            // See PermsForgeryTests.
            // NOT `?? 0`. A /P that is absent, indirect, or not an integer reads as null, and
            // defaulting it to 0 would compare 0 against the authenticated value and announce
            // "the permission bitmask was altered" for a document nobody touched — accusing a
            // conforming file of tampering, which is a worse failure than the one this check closes.
            // "I could not read /P" is a third answer and is reported as itself.
            // Through PdfPermissionValue: GetInteger saturates an unsigned-spelled /P to
            // int.MaxValue, which never compares equal to the authenticated value — so this
            // check would announce PMismatch for an untouched file, the exact accusation the comment
            // above avoids. The low 32 bits are what Algorithm 9 stored.
            int? declaredPOrNull = PdfPermissionValue.Read(encryptDict);
            if (declaredPOrNull == null)
            {
                LastPermsValidation =
                    "PUnreadable: /Perms decrypted and carries the 'adb' tag, but /P is absent or is " +
                    "not a direct integer, so the authenticated value could not be compared against " +
                    "it. Treat the permissions as unverified rather than as tampered.";
                return;
            }

            int declaredP = declaredPOrNull.Value;
            int authenticatedP = permsPlain[0]
                               | (permsPlain[1] << 8)
                               | (permsPlain[2] << 16)
                               | (permsPlain[3] << 24);

            if (declaredP != authenticatedP)
            {
                LastPermsValidation =
                    $"PMismatch: the /Encrypt dictionary declares /P {declaredP}, but the " +
                    $"authenticated /Perms value says {authenticatedP}. The permission bitmask was " +
                    "altered after the document was encrypted; do not honour /P.";
                return;
            }

            // Byte 8 authenticates /EncryptMetadata the same way. Its absence means true (§7.6.4.2),
            // which is also what PdfEncryptor writes when the caller does not say otherwise.
            // Taken from the caller rather than re-read here. The value the caller holds was
            // RESOLVED (§7.3.10 permits /EncryptMetadata to be an indirect reference), and reading
            // it raw a second time would disagree with the key derivation that already used it —
            // byte 8 would then mismatch and a legal document be refused.
            byte expectedFlag = declaredEncryptMetadata ? (byte)'T' : (byte)'F';

            if (permsPlain.Length > PermsEncryptMetadataOffset &&
                permsPlain[PermsEncryptMetadataOffset] != expectedFlag)
            {
                LastPermsValidation =
                    $"EncryptMetadataMismatch: the /Encrypt dictionary declares /EncryptMetadata " +
                    $"{declaredEncryptMetadata.ToString().ToLowerInvariant()}, but the authenticated " +
                    $"/Perms value says '{(char)permsPlain[PermsEncryptMetadataOffset]}'. " +
                    "Do not honour /P.";
                return;
            }

            LastPermsValidation = "OK";
        }

        /// <summary>
        /// Outcome of the most recent <c>/Perms</c> validation:
        /// <list type="bullet">
        ///   <item><c>"OK"</c> — the field decrypted and the "adb" tag matched.</item>
        ///   <item><c>"Absent"</c> — V=5 document with no /Perms entry (allowed).</item>
        ///   <item><c>"SkippedNonAes256Key"</c> — non-AES256 path; /Perms doesn't apply.</item>
        ///   <item><c>"DecryptFailed: ..."</c> — AES-ECB decryption threw.</item>
        ///   <item><c>"TagMismatch: ..."</c> — plaintext bytes 9-11 ≠ "adb"; possible tamper.</item>
        ///   <item><c>null</c> — a V=5 document whose password never authenticated, so validation
        ///     was not reached.</item>
        /// </list>
        /// <para><b>Scoped to the most recent <see cref="Decrypt(Stream,Stream,string)"/> call on this thread.</b> It is
        /// reset at the start of every document — to <c>"SkippedNonAes256Key"</c> below V=5, where
        /// /Perms does not apply — so a verdict can never be inherited from a previous file; an RC4
        /// document opened after an AES-256 one reading <c>"OK"</c> would silently defeat the
        /// refuse-unless-OK rule below.</para>
        /// </summary>
        public static string LastPermsValidation
        {
            get => _lastPermsValidation;
            private set => _lastPermsValidation = value;
        }

        [ThreadStatic]
        private static string _lastPermsValidation;

        private static bool PasswordMatches(byte[] computed, byte[] stored, int revision)
        {
            if (computed == null || stored == null) return false;

            if (revision == PdfEncryptionPrimitives.RevisionRc4Basic)
            {
                // /U is exactly 32 bytes. Comparing only min(len) bytes let a truncated /U
                // (e.g. a 1-byte or empty /U in a crafted file) pass validation with ~1/256
                // odds for an arbitrary password (empty /U → 0-length compare → always match).
                // Require the full 32 bytes.
                if (computed.Length < UserValueCompareLengthR2
                    || stored.Length != UserValueCompareLengthR2) return false;
                return BytesMatch(computed, 0, stored, 0, UserValueCompareLengthR2);
            }
            else
            {
                // For revision 3+, compare the first 16 bytes (the spec compares the
                // 16-byte MD5-derived prefix); require both sides to carry them.
                if (computed.Length < UserValueCompareLengthR3
                    || stored.Length < UserValueCompareLengthR3) return false;
                return BytesMatch(computed, 0, stored, 0, UserValueCompareLengthR3);
            }
        }

        // Constant-time byte comparison — prevents timing-based password oracles.
        private static bool BytesMatch(byte[] a, int offsetA, byte[] b, int offsetB, int length)
        {
            if (a.Length - offsetA < length || b.Length - offsetB < length)
                return false;
            int diff = 0;
            for (int i = 0; i < length; i++)
                diff |= a[offsetA + i] ^ b[offsetB + i];
            return diff == 0;
        }

        #endregion

        #region Key Derivation

        private static byte[] RecoverUserPasswordFromOwner(string ownerPassword, byte[] oValue, int revision, int keyLenBytes)
        {
            // Algorithm 3 steps a to d, the encryptor's rule (PdfEncryptionPrimitives): each revision-3
            // round hashes the first n bytes, as every other reader does — hashing all sixteen here
            // refused the owner password of a 40-bit revision-3 file another producer wrote.
            byte[] rc4Key = PdfEncryptionPrimitives.ComputeOwnerRc4Key(ownerPassword, revision, keyLenBytes);

            if (revision == PdfEncryptionPrimitives.RevisionRc4Basic)
            {
                return PdfEncryptionPrimitives.Rc4Process(rc4Key, oValue);
            }
            else
            {
                // Undo Algorithm 3 step g: the 19 XOR-keyed RC4 passes plus the base pass,
                // applied in reverse order.
                byte[] result = (byte[])oValue.Clone();
                for (int i = PdfEncryptionPrimitives.Rc4MixRounds; i >= 0; i--)
                {
                    byte[] tempKey = new byte[keyLenBytes];
                    for (int j = 0; j < keyLenBytes; j++)
                        tempKey[j] = (byte)(rc4Key[j] ^ (byte)i);
                    result = PdfEncryptionPrimitives.Rc4Process(tempKey, result);
                }
                return result;
            }
        }

        #endregion

        #region Object Decryption

        /// <summary>
        /// Decrypts <paramref name="obj"/> under the identifier (<paramref name="objectNumber"/>,
        /// <paramref name="generationNumber"/>) of the indirect object it belongs to.
        /// </summary>
        private static PdfObject DecryptObject(PdfObject obj, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm stmAlgorithm, EncryptionAlgorithm strAlgorithm, int revision)
        {
            if (obj is PdfStream stream)
                return DecryptStream(stream, objectNumber, generationNumber, encryptionKey, stmAlgorithm, strAlgorithm, revision);
            if (obj is PdfDictionary dict)
                return DecryptDictionary(dict, objectNumber, generationNumber, encryptionKey, stmAlgorithm, strAlgorithm, revision);
            if (obj is PdfArray array)
                return DecryptArray(array, objectNumber, generationNumber, encryptionKey, stmAlgorithm, strAlgorithm, revision);
            if (obj is PdfString str)
                return DecryptString(str, objectNumber, generationNumber, encryptionKey, strAlgorithm, revision);
            return obj;
        }

        /// <summary>
        /// Decrypts <paramref name="value"/>, found inside the indirect object (<paramref name="objectNumber"/>,
        /// <paramref name="generationNumber"/>), under the identifier its strings and stream data were encrypted with.
        /// </summary>
        /// <param name="value">A dictionary value or array element.</param>
        /// <param name="objectNumber">The object number of the indirect object <paramref name="value"/> was found in.</param>
        /// <param name="generationNumber">That object's generation number.</param>
        /// <param name="encryptionKey">The file encryption key.</param>
        /// <param name="stmAlgorithm">The algorithm for stream data.</param>
        /// <param name="strAlgorithm">The algorithm for strings.</param>
        /// <param name="revision">The security handler revision.</param>
        /// <remarks>
        /// <para>ISO 32000-2 §7.6.3.2, Algorithm 1 step (a): "Obtain the object number and generation number from the
        /// object identifier of the string or stream to be encrypted (see 7.3.10, "Indirect objects"). If the string is a
        /// direct object, use the identifier of the indirect object containing it." So a direct value inherits the
        /// identifier at every depth. Keyed by its OWN generation instead — always 0 for a direct object — every string
        /// below the first level of an object at generation 1 would decrypt to RC4 noise, or fail AES padding and take
        /// the whole decrypt with it.</para>
        /// <para>A value that carries an identifier of its own is not direct: it is an indirect object PdfParser resolved
        /// into place (a <c>/Filter</c> or <c>/DecodeParms</c> value, or an entry inside one). It is written back as
        /// the reference the file had there and decrypted where it is written — as the top-level object it is, under
        /// its own identifier. See <see cref="ParserInlinedObjects"/> for what an inline copy would have lost.</para>
        /// </remarks>
        private static PdfObject DecryptContained(PdfObject value, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm stmAlgorithm, EncryptionAlgorithm strAlgorithm, int revision)
            => ParserInlinedObjects.TryReference(value, out PdfReference reference)
                ? reference
                : DecryptObject(value, objectNumber, generationNumber, encryptionKey, stmAlgorithm, strAlgorithm, revision);

        private static PdfStream DecryptStream(PdfStream stream, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm stmAlgorithm, EncryptionAlgorithm strAlgorithm, int revision)
        {
            var newStream = new PdfStream();
            newStream.ObjectNumber = stream.ObjectNumber;
            newStream.GenerationNumber = stream.GenerationNumber;

            foreach (var kvp in stream)
            {
                newStream.Set(kvp.Key, DecryptContained(kvp.Value, objectNumber, generationNumber, encryptionKey, stmAlgorithm, strAlgorithm, revision));
            }

            byte[] rawData = stream.RawData ?? Array.Empty<byte>();
            byte[] decryptedData = DecryptBytes(rawData, objectNumber, generationNumber, encryptionKey, stmAlgorithm, revision);
            newStream.RawData = decryptedData;
            newStream.Set(PdfName.Length, new PdfNumber(decryptedData.Length));

            return newStream;
        }

        private static PdfDictionary DecryptDictionary(PdfDictionary dict, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm stmAlgorithm, EncryptionAlgorithm strAlgorithm, int revision)
        {
            var newDict = new PdfDictionary();
            newDict.ObjectNumber = dict.ObjectNumber;
            newDict.GenerationNumber = dict.GenerationNumber;

            foreach (var kvp in dict)
            {
                // ISO 32000-2 §7.6.2 exempts "any hexadecimal strings representing the value of the
                // Contents key in a Signature dictionary": the CMS was never encrypted, so running the
                // cipher over it here turned every signature in an encrypted document into noise on the
                // decrypted copy — and a verifier reading that copy's object graph reported a valid
                // signature broken. The same classification the writer uses to leave it in the clear.
                if (SignatureDictionaryShape.IsUnencryptedEntry(dict, kvp.Key))
                {
                    newDict.Set(kvp.Key, kvp.Value);
                    continue;
                }

                newDict.Set(kvp.Key, DecryptContained(kvp.Value, objectNumber, generationNumber, encryptionKey, stmAlgorithm, strAlgorithm, revision));
            }
            return newDict;
        }

        private static PdfArray DecryptArray(PdfArray array, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm stmAlgorithm, EncryptionAlgorithm strAlgorithm, int revision)
        {
            var newArray = new PdfArray();
            newArray.ObjectNumber = array.ObjectNumber;
            newArray.GenerationNumber = array.GenerationNumber;

            foreach (var item in array)
                newArray.Add(DecryptContained(item, objectNumber, generationNumber, encryptionKey, stmAlgorithm, strAlgorithm, revision));
            return newArray;
        }

        private static PdfString DecryptString(PdfString str, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm algorithm, int revision)
        {
            byte[] decrypted = DecryptBytes(str.RawBytes, objectNumber, generationNumber, encryptionKey, algorithm, revision);
            return new PdfString(decrypted, str.IsHex);
        }

        private static byte[] DecryptBytes(byte[] data, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm algorithm, int revision)
            => PdfEncryptionPrimitives.DecryptData(data, objectNumber, generationNumber, encryptionKey, algorithm);

        #endregion

        #region Cryptographic Primitives

        /// <summary>
        /// AES-CBC decryption. First 16 bytes of data are the IV, rest is ciphertext.
        /// </summary>
        internal static byte[] AesCbcDecrypt(byte[] key, byte[] data)
        {
            // Deliberately does NOT clear LastAesCbcPaddingFallback. This method runs once per
            // encrypted string and once per stream - hundreds of times per document - so clearing
            // here meant the slot described only the LAST block decrypted, and an early object
            // that needed the fallback was erased by every clean object after it. The slot is
            // cleared once per document in BeginDocument, and only ever written here.

            // Too short to hold even the IV: conforming AES-CBC output is the IV plus at least one
            // padded block, so this is malformed. Empty, as the misaligned-length case below returns,
            // and for the same reason — handing the raw bytes back as "plaintext" echoes an attacker's
            // bytes as the decrypted value. Every sibling path fails closed the same way.
            // Malformed — an IV and no whole block after it: emptied, and said (LastAesCbcMalformedLength), since
            // an emptied stream is otherwise a page that lost its content with nothing to show for it.
            int cipherLen = data.Length - PdfEncryptionPrimitives.AesBlockBytes;
            if (cipherLen <= 0 || cipherLen % PdfEncryptionPrimitives.AesBlockBytes != 0)
            {
                _aesCbcMalformedCount++;
                _lastAesCbcMalformedLength =
                    (_aesCbcMalformedFirst ??= $"AES-CBC data of {data.Length} bytes is not a 16-byte IV followed by whole " +
                                               "16-byte blocks; it was decrypted to nothing")
                    + (_aesCbcMalformedCount > 1 ? $" (and {_aesCbcMalformedCount - 1} more)." : ".");
                return Array.Empty<byte>();
            }

            byte[] iv = new byte[PdfEncryptionPrimitives.AesBlockBytes];
            Buffer.BlockCopy(data, 0, iv, 0, PdfEncryptionPrimitives.AesBlockBytes);

            byte[] ciphertext = new byte[cipherLen];
            Buffer.BlockCopy(data, PdfEncryptionPrimitives.AesBlockBytes, ciphertext, 0, cipherLen);

            try
            {
                return Tetitulz.Pdf.Core.Encryption.PdfCryptoPrimitives.AesCbcPkcs7(
                    encrypt: false, key, iv, ciphertext);
            }
            catch (Exception padded)
            {
                // PKCS#7 padding failed. Per ISO 32000 conforming AES-CBC is ALWAYS PKCS#7-padded,
                // so a padding failure means the ciphertext is wrong (tampered, truncated, or wrong
                // key). The default is therefore to FAIL CLOSED: tampered ciphertext must not be
                // silently re-interpreted as valid unpadded plaintext, which a no-padding retry would
                // do. The retry is strictly OPT-IN via
                // <see cref="AllowAesCbcNoPaddingFallback"/> for the rare non-conforming producer
                // that genuinely encrypts without padding; the <see cref="LastAesCbcPaddingFallback"/>
                // diagnostic is always set so callers can audit when the fallback fires.
                if (!AllowAesCbcNoPaddingFallback)
                {
                    LastAesCbcPaddingFallback =
                        "PKCS#7 padding rejected (" + padded.GetType().Name + ": " +
                        padded.Message + ") - failing closed (no-padding fallback is disabled).";
                    throw new PdfException(
                        "AES-CBC decryption failed - PKCS#7 padding is invalid (tampered ciphertext or wrong key). " +
                        "Conforming PDFs are always PKCS#7-padded; enable AllowAesCbcNoPaddingFallback only for known non-conforming producers.",
                        padded);
                }

                try
                {
                    var result = PdfEncryptionPrimitives.AesCbcNoPadding(false, key, iv, ciphertext);
                    LastAesCbcPaddingFallback =
                        "PKCS#7 padding rejected (" + padded.GetType().Name + ": " +
                        padded.Message + ") - fell back to AES-CBC no-padding (opt-in).";
                    return result;
                }
                catch (Exception inner)
                {
                    // Both padded and un-padded decryption failed. The ciphertext is genuinely
                    // undecryptable (wrong key, truncated data, or corruption). Returning the
                    // raw bytes would silently produce garbage plaintext and hide tampering.
                    throw new PdfException("AES-CBC decryption failed — ciphertext is malformed or the key is incorrect.", inner);
                }
            }
        }

        /// <summary>
        /// AES-CBC data the last document decrypted on this thread held that was not an IV followed by whole
        /// blocks — a string or stream cut short — and was decrypted to nothing: the first described, the
        /// rest counted. <c>null</c> when there was none. Cleared at the start of each document.
        /// </summary>
        public static string LastAesCbcMalformedLength => _lastAesCbcMalformedLength;

        [ThreadStatic] private static string _lastAesCbcMalformedLength;
        [ThreadStatic] private static string _aesCbcMalformedFirst;
        [ThreadStatic] private static int _aesCbcMalformedCount;

        /// <summary>
        /// AES-256-GCM decryption per ISO/TS 32003 §5.2: <c>data</c> is
        /// <c>IV(12) || ciphertext || tag(16)</c>. Fails closed on a short buffer or a failed tag
        /// verification — GCM is authenticated, so a bad tag means tampered ciphertext or the
        /// wrong key, and returning anything other than a hard failure would defeat the entire
        /// point of choosing an authenticated cipher over AES-CBC.
        /// </summary>
        internal static byte[] AesGcmDecrypt(byte[] key, byte[] data)
        {
            int minLength = PdfGcmCipher.IvLength + PdfGcmCipher.TagLength;
            if (data.Length < minLength)
                throw new PdfException(
                    $"AES-GCM decryption failed — ciphertext is too short ({data.Length} bytes; " +
                    $"needs at least the {PdfGcmCipher.IvLength}-byte IV plus the " +
                    $"{PdfGcmCipher.TagLength}-byte authentication tag).");

            var iv = new byte[PdfGcmCipher.IvLength];
            Buffer.BlockCopy(data, 0, iv, 0, PdfGcmCipher.IvLength);

            int cipherLen = data.Length - minLength;
            var ciphertext = new byte[cipherLen];
            Buffer.BlockCopy(data, PdfGcmCipher.IvLength, ciphertext, 0, cipherLen);

            var tag = new byte[PdfGcmCipher.TagLength];
            Buffer.BlockCopy(data, PdfGcmCipher.IvLength + cipherLen, tag, 0, PdfGcmCipher.TagLength);

            if (!PdfGcmCipher.TryDecrypt(key, iv, ciphertext, tag, out byte[] plaintext))
                throw new PdfException(
                    "AES-GCM decryption failed — the authentication tag did not verify " +
                    "(tampered ciphertext or the wrong key).");

            return plaintext;
        }

        /// <summary>
        /// When <c>true</c>, an AES-CBC block whose PKCS#7 padding fails to validate is retried
        /// WITHOUT padding instead of failing. Default: <c>false</c> — conforming AES per ISO 32000
        /// is always PKCS#7-padded, so a padding failure normally means the ciphertext was tampered
        /// with (or the key is wrong) and must fail closed. Enable ONLY for known non-conforming
        /// producers that encrypt without padding. Whether or not the fallback runs, the reason is
        /// recorded on <see cref="LastAesCbcPaddingFallback"/>.
        /// <para>
        /// <b>Per-thread, not process-wide.</b> The setting applies to decrypts on the thread that
        /// set it and to no others - it is backed by a <c>[ThreadStatic]</c> field, like every
        /// diagnostic slot on this type. As a process-wide static it would be the one piece of
        /// shared mutable state in the assembly that is not thread-scoped, and the one that decides
        /// whether tampered AES ciphertext fails closed: a support path enabling it for a single
        /// known-bad file would relax it for every concurrent request on a server host, for an
        /// unbounded window.
        /// </para>
        /// <para>
        /// A host that wants it on for every thread must set it on each thread that decrypts.
        /// </para>
        /// </summary>
        public static bool AllowAesCbcNoPaddingFallback
        {
            get => _allowAesCbcNoPaddingFallback;
            set => _allowAesCbcNoPaddingFallback = value;
        }

        [ThreadStatic]
        private static bool _allowAesCbcNoPaddingFallback;

        // Internal seam so adversarial tests can exercise AES-CBC decryption (incl. the fail-closed
        // path) directly without standing up a full encrypted PDF. data = IV(16) || ciphertext.
        internal static byte[] AesCbcDecryptForTest(byte[] key, byte[] data) => AesCbcDecrypt(key, data);

        /// <summary>
        /// Reason an AES-CBC block in the document just decrypted fell back from PKCS#7-padded
        /// mode to no-padding mode (or, when the fallback is disabled, why it failed closed), or
        /// <c>null</c> when every block took the standard padded path. Callers tracking format
        /// provenance / forensic auditing use this to flag the document.
        /// <para>
        /// <b>Scoped to the document, not to the block.</b> It is cleared once at the start of
        /// each decrypt (<c>BeginDocument</c>) and keeps the FIRST fallback that fired - it used
        /// to be cleared on entry to every AES block, which left it null after any document whose
        /// last object decrypted cleanly, i.e. after almost every document.
        /// </para>
        /// <para>
        /// <b>Per-thread</b> (<c>[ThreadStatic]</c>), like the other <c>Last*</c> diagnostics on
        /// this type: read it on the thread that ran the decrypt. A thread that never decrypted
        /// sees <c>null</c>.
        /// </para>
        /// </summary>
        public static string LastAesCbcPaddingFallback
        {
            get => _lastAesCbcPaddingFallback;
            private set => _lastAesCbcPaddingFallback = value;
        }

        [ThreadStatic]
        private static string _lastAesCbcPaddingFallback;

        #endregion

        #region Crypt Filter Resolution

        /// <summary>
        /// Allowed PDF crypt-filter method names per ISO 32000-2 §7.6.5.1, plus /AESV4
        /// (ISO/TS 32003, AES-GCM). Anything outside this set is suspicious — a typo or a
        /// vendor-specific extension silently degrading to AES-128 is a classic foot-gun.
        /// </summary>
        private static readonly HashSet<string> _knownCfmNames =
            new HashSet<string>(StringComparer.Ordinal)
            {
                PdfName.CryptFilterNone.Value,   // None
                PdfName.CryptFilterV2.Value,     // V2
                PdfName.AESV2.Value,
                PdfName.AESV3.Value,
                PdfName.AESV4.Value,
            };

        /// <summary>
        /// What <see cref="ResolveCryptFilter"/> found worth reporting about this document's crypt
        /// filters — <c>null</c> when it found nothing.
        /// </summary>
        /// <remarks>
        /// <para><b>Not "a fallback was hit", and not "the most recent call".</b> It also reports
        /// configurations where nothing fell back and nothing was guessed: <c>/StmF</c> or
        /// <c>/StrF</c> resolving to <c>/Identity</c> — including by being <b>absent</b>, which is the
        /// spec default for a V≥4 document — and <c>/CFM /None</c>. Those are explicit instructions
        /// to leave half the document in cleartext inside a file that presents as encrypted, which is
        /// worth telling a caller; but a host gating on <c>!= null</c> as "this document is suspect"
        /// will reject conforming files.</para>
        ///
        /// <para><b>It accumulates.</b> <c>ResolveCryptFilter</c> runs twice per document, once for
        /// <c>/StmF</c> and once for <c>/StrF</c>, and both findings are joined with <c>"; "</c>.
        /// Only <c>BeginDocument</c> clears the slot — cleared on entry, a <c>/StmF</c> message
        /// would be destroyed whenever <c>/StrF</c> resolved cleanly, which is the common
        /// case.</para>
        ///
        /// <para><b>Read it as a description, not a verdict.</b> Callers that need "was anything
        /// guessed?" should match on the text for the guessing cases (an unknown filter name, a
        /// missing <c>/CF</c> dictionary, an unrecognised <c>/CFM</c>) rather than on nullness.</para>
        /// </remarks>
        public static string LastCryptFilterFallback
        {
            get => _lastCryptFilterFallback;
            private set => _lastCryptFilterFallback = value;
        }

        [ThreadStatic]
        private static string _lastCryptFilterFallback;

        /// <summary>
        /// Appends a crypt-filter finding to <see cref="LastCryptFilterFallback"/>, tagged with the
        /// key it concerns.
        /// </summary>
        /// <remarks>
        /// <b>Appends rather than assigns, because this method runs twice per document</b> — once
        /// for <c>/StmF</c> and once for <c>/StrF</c>. Clearing on entry and assigning would destroy
        /// a stream-filter finding whenever the string filter resolved cleanly, and the most common
        /// shape of that (a <c>/StrF</c> that is absent or <c>/Identity</c>) returns within two lines
        /// of the reset. The loss would be invisible on the ordinary document where both keys name
        /// the same filter — both calls produce the same text — and bite precisely on the asymmetric
        /// documents worth auditing. The slot is cleared once per document in
        /// <c>BeginDocument</c>.
        /// </remarks>
        private static void RecordCryptFilterFinding(PdfName filterKey, string message)
        {
            string entry = filterKey?.Value is string k ? "/" + k + ": " + message : message;

            LastCryptFilterFallback = string.IsNullOrEmpty(LastCryptFilterFallback)
                ? entry
                : LastCryptFilterFallback + "; " + entry;
        }

        /// <summary>
        /// Resolves the encryption algorithm for a crypt filter entry (/StmF or /StrF).
        /// Reads the named filter from the /CF dictionary and returns the algorithm
        /// based on the /CFM (crypt filter method) value.
        /// </summary>
        internal static EncryptionAlgorithm ResolveCryptFilter(PdfDocument doc, PdfDictionary encryptDict, PdfName filterKey)
        {
            var filterName = Res(doc, encryptDict, filterKey) as PdfName;
            if (filterName == null || filterName == PdfName.Identity)
            {
                // Reported, not silent. /Identity inside a document that declares itself encrypted
                // means those objects are in CLEARTEXT, and a caller that gates on "is this
                // encrypted?" could otherwise not tell that from a properly protected document.
                // Absent is the spec default and means the same thing in practice, so both say so.
                RecordCryptFilterFinding(filterKey,
                    filterName == null
                        ? "absent, so this half of the document is not encrypted (Identity is the default)."
                        : "/Identity, so this half of the document is deliberately left unencrypted.");
                return EncryptionAlgorithm.Identity; // Identity = no encryption (pass-through)
            }
            return ResolveNamedCryptFilter(doc, encryptDict, filterName, filterKey);
        }

        /// <summary>
        /// The algorithm of a stream's own crypt filter (§7.4.10) named <paramref name="name"/>:
        /// <c>/Identity</c> as itself, a filter the <c>/CF</c> dictionary defines as its <c>/CFM</c>
        /// says, and <c>null</c> for a name it does not define — which the caller decrypts under the
        /// document's stream filter and reports once.
        /// </summary>
        internal static EncryptionAlgorithm? ResolveStreamCryptFilter(PdfDocument doc, PdfDictionary encryptDict, PdfName name)
        {
            if (name == PdfName.Identity) return EncryptionAlgorithm.Identity;
            if (!(Res(doc, encryptDict, PdfName.CF) is PdfDictionary cf) || !(Res(doc, cf, name) is PdfDictionary)) return null;
            return ResolveNamedCryptFilter(doc, encryptDict, name, PdfName.Crypt);
        }

        /// <summary>The algorithm of crypt filter <paramref name="filterName"/>, looked up in <c>/CF</c>.</summary>
        private static EncryptionAlgorithm ResolveNamedCryptFilter(PdfDocument doc, PdfDictionary encryptDict,
                                                                   PdfName filterName, PdfName filterKey)
        {
            // Look up the named filter in the /CF dictionary
            var cfDict = Res(doc, encryptDict, PdfName.CF) as PdfDictionary;
            if (cfDict == null)
            {
                // No /CF dictionary but V>=4: infer from revision.
                // Defaulting to AES-128 (instead of RC4-128) when /CF is absent is intentional:
                // AES is stronger than RC4, so the fallback is safer-than-wrong. A V=4 RC4 PDF
                // with a missing /CF will fail to decrypt (garbled output), making the problem
                // immediately visible rather than silently producing readable but insecure output.
                int r = ResInt(doc, encryptDict, PdfName.R) ?? 0;
                RecordCryptFilterFinding(filterKey,
                    $"crypt filter '{filterName.Value}' referenced but /CF dictionary missing (V>=4 PDFs require /CF). " +
                    $"Inferred from /R={r}.");
                if (r == RevisionAesGcm) return EncryptionAlgorithm.AES256GCM;
                if (r == RevisionAes256 || r == RevisionAes256Deprecated) return EncryptionAlgorithm.AES256;
                return EncryptionAlgorithm.AES128;
            }

            var filter = Res(doc, cfDict, filterName) as PdfDictionary;
            if (filter == null)
            {
                RecordCryptFilterFinding(filterKey,
                    $"named crypt filter '{filterName.Value}' not found in /CF dictionary; defaulting to AES-128.");
                return EncryptionAlgorithm.AES128;
            }

            var cfm = Res(doc, filter, PdfName.CFM) as PdfName;
            if (cfm == null)
            {
                RecordCryptFilterFinding(filterKey,
                    $"crypt filter '{filterName.Value}' has no /CFM; defaulting to AES-128.");
                return EncryptionAlgorithm.AES128;
            }

            // Validate /CFM is one of the spec-allowed values; reject typos like
            // /AESV4 instead of silently defaulting.
            // PdfException, not InvalidOperationException: every other refusal in this type throws
            // PdfException and every caller catches only that. PdfIncrementalEncryption.TryCreate
            // (contract: return false) and PdfEncryptedRoundTrip.Run ("failures are reported, not
            // thrown") both let an InvalidOperationException escape, so one attacker-controlled
            // name in /CF /StdCF /CFM turned a Try-method into a throw.
            if (!_knownCfmNames.Contains(cfm.Value))
                throw new PdfException(
                    $"Unsupported /CFM value '{cfm.Value}' in crypt filter '{filterName.Value}'. " +
                    $"Allowed: {string.Join(", ", _knownCfmNames)}.");

            if (cfm == PdfName.AESV4) return EncryptionAlgorithm.AES256GCM;
            if (cfm == PdfName.AESV3) return EncryptionAlgorithm.AES256;
            if (cfm == PdfName.AESV2) return EncryptionAlgorithm.AES128;
            if (cfm == PdfName.CryptFilterV2) return EncryptionAlgorithm.RC4_128;
            if (cfm == PdfName.CryptFilterNone)
            {
                // Same reasoning as /Identity above: this is an explicit instruction to leave these
                // objects in cleartext inside a document that presents as encrypted.
                RecordCryptFilterFinding(filterKey,
                    $"crypt filter '{filterName.Value}' has /CFM /None, so this half of the " +
                    "document is left unencrypted.");
                return EncryptionAlgorithm.Identity; // /CFM /None = no encryption
            }

            // Unreachable due to the HashSet check above, but kept for safety.
            RecordCryptFilterFinding(filterKey,
                $"recognised but unhandled /CFM '{cfm.Value}'; defaulting to AES-128.");
            return EncryptionAlgorithm.AES128;
        }

        #endregion

        #region Helpers

        /// <summary>
        /// A byte string from the encryption dictionary (<c>/O</c>, <c>/U</c>, <c>/UE</c>,
        /// <c>/OE</c>, <c>/Perms</c>).
        /// </summary>
        /// <remarks>
        /// <b>Deliberately does NOT resolve, unlike every other read in this file.</b> The entries
        /// it reads are <i>strings</i>, and §7.6.2 exempts strings in the encryption dictionary
        /// itself from encryption — an exemption that applies to the dictionary, not to some other
        /// object it points at. So a string reached through an indirect reference would be an
        /// ordinary encrypted object, and reading it would need the very file key these values
        /// exist to derive. An indirect <c>/O</c> is therefore not a document we are failing to
        /// read; it is a document that cannot be read by anyone.
        /// <para>The numbers, names, booleans and sub-dictionaries elsewhere in the encryption
        /// dictionary are <b>not</b> encrypted whether direct or indirect, which is why those reads
        /// go through <see cref="Res"/> and these do not. That is the line between the two, and it
        /// is a property of the cipher rather than a convention.</para>
        /// </remarks>
        private static byte[] GetBytesFromDict(PdfDictionary dict, PdfName key)
        {
            var obj = dict[key];
            if (obj is PdfString str)
                return str.RawBytes;
            return null;
        }

        /// <summary>
        /// Reads <paramref name="key"/> from <paramref name="dict"/>, following an indirect
        /// reference (§7.3.10 permits one anywhere, including inside the encryption dictionary).
        /// </summary>
        /// <remarks>
        /// <para><b>A read that tests the raw value's type reads an indirect entry as
        /// <i>absent</i></b> — and in a security handler "absent" is never neutral, it selects a
        /// default:</para>
        /// <list type="bullet">
        ///   <item><description><c>/StmF</c> or <c>/StrF</c> absent means <b>Identity</b>, i.e. that
        ///   half of the document is not encrypted at all. So an indirect <c>/StmF</c> would make the
        ///   decryptor pass ciphertext through as though it were plaintext — <i>and report</i>
        ///   "absent, so this half of the document is not encrypted", which is a false statement
        ///   about the file rather than a silent failure.</description></item>
        ///   <item><description><c>/V</c>, <c>/R</c>, <c>/Length</c>, <c>/P</c> and <c>/ID</c> all
        ///   feed Algorithm 2. A wrong value derives a wrong file key, so a <b>correct password is
        ///   rejected</b>. <c>/ID</c> is the one a real file is most likely to write
        ///   indirectly.</description></item>
        ///   <item><description><c>/EncryptMetadata</c> absent means <c>true</c> (§7.6.4.2), which
        ///   changes both the R=4 key derivation and the <c>/Perms</c> byte-8
        ///   check.</description></item>
        /// </list>
        /// <para>Resolving here is safe: the encryption dictionary's own strings are not encrypted,
        /// and every entry read through this helper is a number, a name, a boolean or a dictionary
        /// of those — never a string that would need the key we are still deriving.</para>
        /// </remarks>
        private static PdfObject Res(PdfDocument doc, PdfDictionary dict, PdfName key)
        {
            var raw = dict?[key];
            return doc?.ResolveObject(raw) ?? raw;
        }

        /// <summary>
        /// Walks <c>/CF</c> → <c>/StdCF</c> → <c>/CFM</c>, following an indirect reference at every
        /// step. Done here, eagerly, because this is the one place the document is open — see
        /// <c>EncryptionInfo.StdCryptFilterMethod</c> for why a captured resolver will not do.
        /// </summary>
        private static string ReadStdCryptFilterMethod(PdfDocument doc, PdfDictionary encryptDict)
        {
            if (!(Res(doc, encryptDict, PdfName.CF) is PdfDictionary cf)) return null;
            if (!(Res(doc, cf, PdfName.StdCF) is PdfDictionary stdCf)) return null;
            return (Res(doc, stdCf, PdfName.CFM) as PdfName)?.Value;
        }

        /// <summary>The integer at <paramref name="key"/>, following an indirect reference.</summary>
        private static int? ResInt(PdfDocument doc, PdfDictionary dict, PdfName key)
            => Res(doc, dict, key) is PdfNumber n ? n.IntValue : (int?)null;

        private static byte[] GetFileId(PdfDocument doc, PdfDictionary trailer)
        {
            // Resolved: /ID is legal as an indirect reference and feeds Algorithm 2, so reading it
            // as absent derives the wrong file key and rejects a correct password.
            var idArray = Res(doc, trailer, PdfName.ID) as PdfArray;
            if (idArray != null && idArray.Count >= 1)
            {
                var firstId = idArray[0] as PdfString;
                if (firstId != null && firstId.RawBytes.Length > 0)
                    return firstId.RawBytes;
            }
            // Genuinely empty — see LegacyMissingFileIdLength for why this is not sixteen zeros,
            // and ValidateAndDeriveKey for the second attempt that opens a file encrypted under the
            // sixteen-zeros substitute.
            return Array.Empty<byte>();
        }

        private static int GetEncryptObjNumber(PdfDictionary trailer)
        {
            var encRef = trailer[PdfName.Encrypt];
            if (encRef is PdfReference r)
                return r.ReferenceObjectNumber;
            return -1;
        }

        /// <summary>
        /// Reason the most recent object stream was refused before its index was read, or
        /// <c>null</c> when every object stream this thread decrypted was accepted.
        /// </summary>
        /// <remarks>
        /// A refusal is not a decryption failure: the rest of the document still decrypts, and the
        /// objects inside the refused stream are simply absent. Without a diagnostic those two
        /// outcomes are identical to a caller — which is the reason this slot exists rather than a
        /// silent <c>return</c>.
        /// </remarks>
        public static string LastObjectStreamRejection
        {
            get => _lastObjectStreamRejection;
            private set => _lastObjectStreamRejection = value;
        }

        [ThreadStatic]
        private static string _lastObjectStreamRejection;

        /// <summary>
        /// Folds the drops recorded during one <see cref="Decrypt(Stream, Stream, string)"/> into
        /// <see cref="LastObjectStreamRejection"/>: the first reason verbatim, the rest as a count.
        /// </summary>
        /// <param name="firstReason">The first drop recorded, or <c>null</c> if none.</param>
        /// <param name="dropCount">How many drops were recorded in total.</param>
        /// <remarks>
        /// Every refusal is recorded as a drop when it is made — <see cref="ExtractObjectsFromStream"/> clears its own
        /// slot on entry, so only the last stream's refusal would survive the loop otherwise — so the slot holds nothing
        /// the accounting does not, and when nothing was dropped it is null.
        /// </remarks>
        private static void ComposeDropDiagnostic(string firstReason, int dropCount)
        {
            if (dropCount == 0) return;

            LastObjectStreamRejection = dropCount == 1
                ? firstReason
                : firstReason + $" (and {dropCount - 1} further object(s) or object stream(s) " +
                  "were dropped from this document)";
        }

        /// <summary>
        /// Extracts individual objects from a decrypted Object Stream (ObjStm).
        /// After decryption, the ObjStm's RawData is decrypted but still compressed.
        /// This method decompresses it and parses the contained objects, adding them
        /// to the decrypted objects dictionary as regular indirect objects.
        /// </summary>
        /// <param name="objStmStream">The object stream, already decrypted.</param>
        /// <param name="decryptedObjects">Receives each member taken, keyed by its object number.</param>
        /// <param name="isCurrentCopy">
        /// Given a member's object number and its index in this stream, whether that member is the copy of the object the
        /// document's cross-reference table points at. A member it rejects is neither parsed nor written. <c>null</c>
        /// takes every member, which is right only for a stream considered on its own: inside a document, an older object
        /// stream still holds copies that later revisions superseded.
        /// </param>
        /// <param name="documentParser">
        /// The parser of the document the stream came from, whose syntax-stream budget and image-codec
        /// refusal the decode runs under (<see cref="PdfParser.DecodeSyntaxStream"/>), so every object
        /// stream of a document spends one budget. <c>null</c> runs it under a budget of its own.
        /// </param>
        /// <remarks>
        /// <b>This is the second ObjStm index reader in the tree</b>, and it applies the bounds
        /// <c>PdfParser.GetObjectStreamReader</c> carries, from the same constants: a hard cap on
        /// <c>/N</c>, a cross-check that the decoded body is big enough for the count to be true, and
        /// the decode itself under the parser's rules — an image codec refused by name, the document's
        /// syntax-stream budget spent.
        /// </remarks>
        // internal rather than private so its bounds can be exercised directly. Reaching them
        // through PdfEncryptor would mean producing an encrypted document that still contains an
        // object stream with a hostile /N, which the encryptor's own rewrite does not preserve —
        // the test would be about the encryptor, not about this guard.
        internal static void ExtractObjectsFromStream(PdfStream objStmStream, Dictionary<int, PdfObject> decryptedObjects,
            Func<int, int, bool> isCurrentCopy = null, PdfParser documentParser = null)
        {
            // Cleared at the START, on every path, because a caller reads it AFTER the operation.
            // [ThreadStatic] gives per-thread isolation and NOT per-document scope: without this, a
            // server thread that refused a hostile object stream in document A would still be
            // holding A's message when a caller asked whether anything was dropped from document B,
            // and the answer would name A's /N. That is the exact trap CLAUDE.md records for
            // LastPermsValidation — which lives in this same class.
            LastObjectStreamRejection = null;

            // Verify this is actually an ObjStm
            var typeName = objStmStream.GetTypeName();
            if (typeName == null || typeName != PdfName.ObjStm)
                return;

            int n = objStmStream.GetInteger(PdfName.N) ?? 0;
            int first = objStmStream.GetInteger(PdfName.First) ?? 0;

            if (n <= 0) return;

            if (n > PdfLimits.MaxObjStmEntries)
            {
                LastObjectStreamRejection =
                    $"Object stream declares /N {n}, above the {PdfLimits.MaxObjStmEntries} limit; " +
                    "its objects were not extracted.";
                return;
            }

            // Decoded as the parser decodes an object stream: an image codec refused by name, every
            // stage charged to the document's syntax-stream budget, nothing memoised on the stream. A refusal
            // throws, and the caller reports the stream as one it could not expand.
            byte[] decoded = (documentParser ?? new PdfParser(Stream.Null)).DecodeSyntaxStream(objStmStream);
            if (decoded == null || decoded.Length == 0) return;

            // The count must be answerable by the bytes the file supplied — see
            // PdfLimits.MinObjStmIndexBytesPerEntry for why the constant cap alone leaves a large
            // amplification open.
            if ((long)n * PdfLimits.MinObjStmIndexBytesPerEntry > decoded.Length)
            {
                LastObjectStreamRejection =
                    $"Object stream declares /N {n}, needing at least " +
                    $"{(long)n * PdfLimits.MinObjStmIndexBytesPerEntry} bytes of index, but it " +
                    $"decodes to {decoded.Length}; its objects were not extracted.";
                return;
            }

            using (var ms = new MemoryStream(decoded))
            {
                var tokenizer = new PdfTokenizer(ms);
                var parser = new PdfParser(ms);

                // Read the index: N pairs of (objNum, byteOffset)
                var entries = new List<(int objNum, int offset)>();
                for (int i = 0; i < n; i++)
                {
                    int objNum = tokenizer.ReadInteger();
                    int offset = tokenizer.ReadInteger();
                    entries.Add((objNum, offset));
                }

                // Parse each object from its offset within the stream. A member naming an offset an
                // earlier member already holds is skipped, as PdfParser.ParseObjectFromStream refuses
                // it (ISO 32000-2 §7.5.7: "The byte offsets shall be in increasing order"): each was
                // parsed as its own copy, so a few kilobytes of index could hold the stream's largest
                // object hundreds of times. The caller reports it as a member the stream did not yield.
                var offsetsTaken = new HashSet<int>();
                for (int i = 0; i < entries.Count; i++)
                {
                    int objNum = entries[i].objNum;
                    if (!offsetsTaken.Add(entries[i].offset))
                        continue;
                    if (isCurrentCopy != null && !isCurrentCopy(objNum, i))
                        continue;

                    long objOffset = first + entries[i].offset;

                    try
                    {
                        tokenizer.Seek(objOffset);
                        var obj = parser.ParseDirectObject();
                        obj.ObjectNumber = objNum;
                        decryptedObjects[objNum] = obj;
                    }
                    catch
                    {
                        // Skip objects that can't be parsed
                    }
                }
            }
        }

        /// <summary>
        /// Whether member <paramref name="index"/> of object stream <paramref name="objectStreamNumber"/>, stored under
        /// <paramref name="objectNumber"/>, is the copy of that object the merged cross-reference table points at: its
        /// entry is a compressed one naming this stream and this index.
        /// </summary>
        /// <param name="xref">The document's cross-reference table, merged newest revision first.</param>
        /// <param name="objectStreamNumber">The object stream the member was read from.</param>
        /// <param name="objectNumber">The object number the stream's index stores for the member.</param>
        /// <param name="index">The member's position in the stream's index.</param>
        /// <remarks>
        /// Both halves are needed, and they are the test <c>PdfParser.ParseObjectFromStream</c> makes when it reads a
        /// compressed object from a plaintext file — it goes to the index the entry names and refuses the object if a
        /// different number is stored there. The stream number rejects a copy a later revision moved into a newer object
        /// stream; the index rejects a second copy under the same number within one stream. An object whose entry is no
        /// longer compressed at all — rewritten at the top level, or freed — is rejected by the entry type.
        /// </remarks>
        private static bool IsCurrentObjectStreamMember(CrossReferenceTable xref, int objectStreamNumber, int objectNumber, int index)
        {
            CrossReferenceEntry entry = xref.GetEntry(objectNumber);
            return entry != null
                && entry.Type == CrossReferenceEntry.EntryType.Compressed
                && entry.ObjectStreamNumber == objectStreamNumber
                && entry.IndexInObjectStream == index;
        }

        #endregion
    }
}
