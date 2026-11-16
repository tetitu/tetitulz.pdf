// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Base.Encryption;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Encrypts PDF documents using standard PDF encryption (RC4-128, AES-128, AES-256).
    /// </summary>
    public static class PdfEncryptor
    {
        // ------------------------------------------------------------------
        //  Sizes fixed by the spec. Revision/round counts, key-length bounds and the AES block
        //  size are shared with the decrypt path via PdfEncryptionPrimitives.
        // ------------------------------------------------------------------

        /// <summary>Length of the random /ID file identifier generated when the document has none.</summary>
        private const int FileIdLength = 16;

        /// <summary>Length of a validation or key salt in a revision-6 /U or /O (ISO 32000-2 §7.6.4.3.3).</summary>
        private const int SaltLength = 8;

        /// <summary>
        /// Total length of a revision-6 /U or /O string: a 32-byte hash followed by the 8-byte
        /// validation salt and the 8-byte key salt.
        /// </summary>
        private const int PasswordEntryLength = 48;

        /// <summary>Offset of the validation salt within a revision-6 /U or /O string.</summary>
        private const int ValidationSaltOffset = 32;

        /// <summary>Offset of the key salt within a revision-6 /U or /O string.</summary>
        private const int KeySaltOffset = 40;

        /// <summary>Length of the AES-ECB-encrypted /Perms entry (ISO 32000-2 §7.6.4.3.4).</summary>
        private const int PermsLength = 16;

        /// <summary>Bytes of the little-endian /P value that open the /Perms plaintext.</summary>
        private const int PermsPValueBytes = 4;

        /// <summary>Random tail bytes closing the /Perms plaintext.</summary>
        private const int PermsRandomTailBytes = 4;

        /// <summary>Crypt-filter /Length for AES-128, expressed in bytes as ISO 32000-1 §7.6.5 requires.</summary>
        private const int Aes128KeyBytes = 16;

        /// <summary>Crypt-filter /Length for AES-256, expressed in bytes.</summary>
        private const int Aes256KeyBytes = 32;

        /// <summary>
        /// The <c>/CryptFilter</c> <c>/Type</c> value, written on the /StdCF entry of both the
        /// AES-128 and the AES-256 /CF dictionary. There is no <c>PdfName.CryptFilter</c> static.
        /// </summary>
        private static readonly PdfName CryptFilterType = new PdfName("CryptFilter");

        /// <summary>
        /// Encrypts a PDF file and writes the encrypted result to the output path.
        /// </summary>
        /// <param name="inputPath">Path to the unencrypted PDF.</param>
        /// <param name="outputPath">Path where the encrypted PDF will be written.</param>
        /// <param name="parameters">Encryption parameters including passwords and algorithm.</param>
        public static void Encrypt(string inputPath, string outputPath, EncryptionParameters parameters)
        {
            if (inputPath == null) throw new ArgumentNullException(nameof(inputPath));
            if (outputPath == null) throw new ArgumentNullException(nameof(outputPath));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            using (var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Encrypt(input, output, parameters);
            }
        }

        /// <summary>
        /// Encrypts a PDF from the input stream and writes the encrypted result to the output stream.
        /// </summary>
        /// <param name="input">Stream containing the unencrypted PDF.</param>
        /// <param name="output">Stream where the encrypted PDF will be written.</param>
        /// <param name="parameters">Encryption parameters including passwords and algorithm.</param>
        /// <exception cref="ArgumentException">
        /// The parameters cannot be written: no owner password, an algorithm that is not one, only
        /// attachments encrypted without crypt filters, or <see cref="EncryptionParameters.EnablePdfMac"/>
        /// with an algorithm written below <c>/V 5</c> (anything but AES-256 and AES-256-GCM).
        /// </exception>
        public static void Encrypt(Stream input, Stream output, EncryptionParameters parameters)
            => Encrypt(input, output, parameters, refuseTokenBelowVersion5: true);

        /// <summary>
        /// <see cref="Encrypt(Stream, Stream, EncryptionParameters)"/>, with the ISO/TS 32004 rule
        /// that a PDF MAC token needs <c>/V 5</c> switchable.
        /// </summary>
        /// <param name="input">Stream containing the unencrypted PDF.</param>
        /// <param name="output">Stream where the encrypted PDF will be written.</param>
        /// <param name="parameters">Encryption parameters including passwords and algorithm.</param>
        /// <param name="refuseTokenBelowVersion5">
        /// <c>false</c> only in tests, to build the non-conforming document a verifier has to
        /// recognise — a token under RC4 or AES-128, which another producer may have written and
        /// the public entry point refuses to.
        /// </param>
        internal static void Encrypt(Stream input, Stream output, EncryptionParameters parameters, bool refuseTokenBelowVersion5)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            ValidateParameters(parameters, refuseTokenBelowVersion5);

            using (PdfDocument doc = OpenUnencrypted(input))
            {
                var allObjects = ContentObjects(doc, out bool inputHasObjectStreams);

                // Determine a file ID. Reuse existing /ID if present, else generate a new one.
                byte[] fileId = GenerateOrExtractFileId(doc);

                // Compute the /P value from the permissions flags.
                int pValue = ComputePermissionValue(parameters.Permissions, parameters.EnablePdfMac);

                // Choose algorithm-specific parameters.
                int v, r, keyLength;
                GetAlgorithmParameters(parameters.Algorithm, out v, out r, out keyLength);

                // Derive the encryption key, /O, /U (and /OE, /UE, /Perms for AES-256).
                byte[] encryptionKey;
                byte[] oValue, uValue;
                byte[] oeValue = null, ueValue = null, permsValue = null;

                string userPwd = parameters.UserPassword ?? "";
                string ownerPwd = parameters.OwnerPassword ?? "";

                // ISO 32000-1 §7.6.3.3 Algorithm 3 step (a): an empty owner password is replaced by
                // the user password. This applies to EVERY algorithm, and it is hoisted above the
                // branch for that reason. Inside the RC4/AES-128 arm only, an AES-256 document
                // requested with an empty owner password would have its /O and /OE built from the
                // EMPTY string; PdfDecryptor would then authenticate "" against /O and return the file
                // key from /OE, so the document would open with no password at all.
                string effectiveOwnerPwd = ownerPwd.Length > 0 ? ownerPwd : userPwd;

                if (parameters.Algorithm == EncryptionAlgorithm.AES256
                    || parameters.Algorithm == EncryptionAlgorithm.AES256GCM)
                {
                    // ISO/TS 32003 §5.1: V=6/R=7's password algorithms are identical to R6's — only
                    // the per-object cipher (below, in EncryptBytes) changes to AES-GCM. The file
                    // key, /O, /U, /OE, /UE and /Perms are all still AES-256-CBC-wrapped exactly as
                    // for AES256, so this method is reused rather than duplicated.
                    ComputeAes256Keys(
                        userPwd, effectiveOwnerPwd, pValue, fileId, parameters.EncryptMetadata,
                        out encryptionKey, out oValue, out uValue,
                        out oeValue, out ueValue, out permsValue);
                }
                else
                {
                    oValue = ComputeOwnerValue(userPwd, effectiveOwnerPwd, r, keyLength);
                    encryptionKey = PdfEncryptionPrimitives.ComputeEncryptionKey(userPwd, oValue, pValue, fileId, r, keyLength, parameters.EncryptMetadata);
                    uValue = PdfEncryptionPrimitives.ComputeUserValue(encryptionKey, fileId, r);
                }

                // Allocate an object number for the /Encrypt dictionary.
                int maxObjNum = doc.CrossReferenceTable.GetMaxObjectNumber();
                int encryptDictObjNum = maxObjNum + 1;

                // ISO/TS 32004 §6.4: a fresh 32-byte KDFSalt, independent of the crypt filter
                // algorithm. Generated once here so both the /Encrypt dictionary and the token
                // built after the file is written derive from the same value.
                byte[] kdfSalt = null;
                if (parameters.EnablePdfMac)
                {
                    kdfSalt = new byte[32];
                    using (var rng = RandomNumberGenerator.Create())
                        rng.GetBytes(kdfSalt);
                }

                // Build the /Encrypt dictionary.
                var encryptDict = BuildEncryptDictionary(
                    parameters.Algorithm, v, r, keyLength, pValue,
                    oValue, uValue, oeValue, ueValue, permsValue,
                    parameters.EncryptMetadata, kdfSalt, parameters.EncryptOnlyAttachments);

                // Encrypting only the attachments: the embedded file streams — those naming themselves,
                // and those a file specification's /EF names — are the only data encrypted.
                HashSet<int> embeddedFiles = null;
                if (parameters.EncryptOnlyAttachments)
                {
                    embeddedFiles = EmbeddedFileStreams.ReferencedBy(allObjects);
                    foreach (var kvp in allObjects)
                        if (EmbeddedFileStreams.NamesItself(kvp.Value)) embeddedFiles.Add(kvp.Key);
                }

                // The document-level metadata stream — the catalog's /Metadata, a /Type /Metadata stream —
                // the one stream /EncryptMetadata false leaves in clear (Table 20); every other is encrypted.
                int metadataObjNum = parameters.EncryptMetadata ? -1 : PdfIncrementalEncryption.DocumentMetadataObjectNumber(doc);

                // The compressed layout (§7.5.7), kept from the input or asked for: PdfCompressedFileWriter
                // encrypts each object as it writes it — the object streams whole, under their own
                // numbers, their members in the clear inside — through the writer's own seam, with the
                // encryptor an appended revision uses (GAPS §19h item 7: the round trip wrote the
                // optimizer's 118 915 packed objects back out one by one, larger than its input).
                if (parameters.UseObjectStreams ?? inputHasObjectStreams)
                {
                    foreach (var kvp in allObjects)
                    {
                        RequireKnownCryptFilter(kvp.Value, kvp.Key);
                        // The writer tells an embedded file by its /Type (IPdfEmbeddedFileEncryptor); a
                        // stream a file specification's /EF names without one is declared, as §7.11.4
                        // recommends, so /EFF reaches it the way the decryptor will expect.
                        if (embeddedFiles != null && embeddedFiles.Contains(kvp.Key)
                            && kvp.Value is PdfStream attachment && attachment[PdfName.Type] == null)
                            attachment.Set(PdfName.Type, PdfName.EmbeddedFile);
                    }
                    WriteCompressedEncryptedFile(output,
                        DetermineMinimumVersion(doc.Version, parameters.Algorithm, parameters.EnablePdfMac),
                        allObjects, encryptDictObjNum, encryptDict,
                        CloneTrailerForEncryption(doc.Trailer, encryptDictObjNum, fileId, 0, allObjects),
                        PdfIncrementalEncryption.ForNewDocument(encryptionKey, parameters.Algorithm,
                            parameters.EncryptMetadata, parameters.EncryptOnlyAttachments, metadataObjNum),
                        parameters.EnablePdfMac ? new PdfMacTokenSeal(encryptionKey, kdfSalt) : null);
                    return;
                }

                // The trailer first: a direct value of it becomes an object of its own, encrypted with the rest.
                var newTrailer = CloneTrailerForEncryption(doc.Trailer, encryptDictObjNum, fileId, 0, allObjects);

                // Encrypt all string and stream objects. Each is keyed under, and later written at, the
                // generation it was read at — the same generation, because Algorithm 1 mixes it into the
                // per-object key; see ObjectIdentity for why it is kept rather than reset to 0.
                var encryptedObjects = new Dictionary<int, (PdfObject Object, int Generation)>();
                foreach (var kvp in allObjects)
                {
                    int objNum = kvp.Key;
                    PdfObject obj = kvp.Value;
                    int generation = ObjectIdentity.GenerationOf(obj);

                    // Skip encrypting the metadata stream when EncryptMetadata is false.
                    if (objNum == metadataObjNum)
                    {
                        encryptedObjects[objNum] = (obj, generation);
                        continue;
                    }

                    PdfObject encrypted;
                    if (obj is PdfStream withFilter && Core.IO.Filters.StreamCryptFilter.TryGetName(withFilter, out PdfName own))
                    {
                        // §7.4.10: the stream names its own crypt filter. /Identity leaves its data in the
                        // clear (its dictionary's strings still go under /StrF); the one filter this
                        // encryptor's /CF defines is /StdCF, the document's own algorithm.
                        RequireKnownCryptFilter(obj, objNum);
                        EncryptionAlgorithm strings = embeddedFiles == null ? parameters.Algorithm : EncryptionAlgorithm.Identity;
                        encrypted = EncryptObject(obj, objNum, generation, encryptionKey, strings, r);
                        if (encrypted is PdfStream file)
                        {
                            file.RawData = own == PdfName.Identity
                                ? withFilter.RawData ?? Array.Empty<byte>()
                                : EncryptBytes(withFilter.RawData ?? Array.Empty<byte>(), objNum, generation,
                                               encryptionKey, parameters.Algorithm, r);
                            file.Set(PdfName.Length, new PdfNumber(file.RawData.Length));
                        }
                    }
                    else if (embeddedFiles == null)
                        encrypted = EncryptObject(obj, objNum, generation, encryptionKey, parameters.Algorithm, r);
                    else
                    {
                        // /StmF and /StrF are /Identity: everything is written as it is, and an embedded
                        // file's data alone is encrypted, under /EFF.
                        encrypted = EncryptObject(obj, objNum, generation, encryptionKey, EncryptionAlgorithm.Identity, r);
                        if (embeddedFiles.Contains(objNum) && encrypted is PdfStream file && obj is PdfStream source)
                        {
                            file.RawData = EncryptBytes(source.RawData ?? Array.Empty<byte>(), objNum, generation,
                                                        encryptionKey, parameters.Algorithm, r);
                            file.Set(PdfName.Length, new PdfNumber(file.RawData.Length));
                        }
                    }
                    encryptedObjects[objNum] = (encrypted, generation);
                }

                // Determine the PDF version we need to write.
                PdfVersion version = DetermineMinimumVersion(doc.Version, parameters.Algorithm, parameters.EnablePdfMac);

                // Build a new trailer dictionary.
                //
                // /Size is one greater than the highest object number in the file (§7.5.5) — NOT
                // the object count. The two agree only when the source's live object numbers are
                // exactly 1..N with no gaps; `encryptDictObjNum` comes from
                // GetMaxObjectNumber() + 1 (which counts free entries) while the count comes from
                // EnumerateAllObjects() (which skips free entries and unparseable objects). On any
                // sparse xref the /Encrypt dictionary — always allocated at the very top — fell
                // outside the declared /Size, so a reader that bounds object numbers by /Size
                // cannot resolve /Encrypt and the document does not open at all.
                int highestObjNum = encryptDictObjNum;
                foreach (var kvp in encryptedObjects)
                    if (kvp.Key > highestObjNum) highestObjNum = kvp.Key;
                newTrailer.Set(PdfName.Size, new PdfNumber(highestObjNum + 1));

                // Write the output PDF.
                var writer = new PdfWriter(output);
                writer.WriteHeader(version);

                // Write all encrypted objects, each at its generation — in the header and in the entry.
                var xrefEntries = new List<(int objNum, int gen, long offset)>();
                foreach (var kvp in encryptedObjects.OrderBy(x => x.Key))
                {
                    long offset = writer.WriteIndirectObject(kvp.Key, kvp.Value.Generation, kvp.Value.Object);
                    xrefEntries.Add((kvp.Key, kvp.Value.Generation, offset));
                }

                // Write the /Encrypt dictionary as an indirect object.
                {
                    long offset = writer.WriteIndirectObject(encryptDictObjNum, 0, encryptDict);
                    xrefEntries.Add((encryptDictObjNum, 0, offset));
                }

                // Write the xref table (the overload adds the free head, object 0) and the trailer.
                long xrefOffset = writer.WriteXrefTable(xrefEntries, 0);

                if (parameters.EnablePdfMac)
                    WriteTrailerWithPdfMac(writer, output, newTrailer, xrefOffset, encryptionKey, kdfSalt);
                else
                    writer.WriteTrailer(newTrailer, xrefOffset);
            }
        }

        /// <summary>
        /// Encrypts an unencrypted document under an existing document's encryption, kept as it was:
        /// its <c>/Encrypt</c> dictionary written unchanged, its first file identifier reused (the
        /// RC4 and AES-128 key derivations mix it in), every object encrypted with the file key and
        /// algorithms the decryptor resolved, and a PDF MAC token sealed again under the same
        /// <c>/KDFSalt</c> when the dictionary carries one — so whatever opened the original opens
        /// this, both passwords of a <c>/Standard</c> document and every recipient of an
        /// <c>/Adobe.PubSec</c> one. The layout follows the input's, as <see cref="Encrypt(Stream, Stream, EncryptionParameters)"/>'s does.
        /// </summary>
        internal static void EncryptKeepingOriginal(Stream input, Stream output, PdfDictionary originalEncrypt,
            byte[] fileId, PdfDecryptor.ResolvedEncryption resolved, EncryptionAlgorithm algorithm, byte[] kdfSalt)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (originalEncrypt == null) throw new ArgumentNullException(nameof(originalEncrypt));
            if (resolved == null) throw new ArgumentNullException(nameof(resolved));

            using (PdfDocument doc = OpenUnencrypted(input))
            {
                var allObjects = ContentObjects(doc, out bool inputHasObjectStreams);

                int encryptDictObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
                int highestObjNum = encryptDictObjNum;
                foreach (var kvp in allObjects)
                    if (kvp.Key > highestObjNum) highestObjNum = kvp.Key;

                bool mac = kdfSalt != null;
                PdfVersion version = DetermineMinimumVersion(doc.Version, algorithm, mac);
                var trailer = CloneTrailerForEncryption(doc.Trailer, encryptDictObjNum, fileId, highestObjNum + 1, allObjects);

                // The writer tells an embedded file by its /Type (IPdfEmbeddedFileEncryptor): a stream a file
                // specification names without one is declared, as §7.11.4 recommends and the fresh encryptor
                // does, so /EFF reaches it — under an attachments-only original /StmF is /Identity, and the
                // stream would be written in clear.
                foreach (int n in EmbeddedFileStreams.ReferencedBy(allObjects))
                    if (allObjects.TryGetValue(n, out PdfObject named) && named is PdfStream attachment && attachment[PdfName.Type] == null)
                        attachment.Set(PdfName.Type, PdfName.EmbeddedFile);
                IPdfObjectEncryptor encryptor = PdfIncrementalEncryption.ForResolved(resolved,
                    resolved.EncryptMetadata ? -1 : PdfIncrementalEncryption.DocumentMetadataObjectNumber(doc));

                if (inputHasObjectStreams)
                {
                    WriteCompressedEncryptedFile(output, version, allObjects, encryptDictObjNum, originalEncrypt, trailer,
                        encryptor, mac ? new PdfMacTokenSeal(resolved.Key, kdfSalt) : null);
                    return;
                }

                var writer = new PdfWriter(output) { ObjectEncryptor = encryptor };
                writer.WriteHeader(version);
                var xrefEntries = new List<(int objNum, int gen, long offset)>();
                foreach (var kvp in allObjects.OrderBy(x => x.Key))
                {
                    int generation = ObjectIdentity.GenerationOf(kvp.Value);
                    xrefEntries.Add((kvp.Key, generation, writer.WriteIndirectObject(kvp.Key, generation, kvp.Value)));
                }

                // §7.6.1: the /Encrypt dictionary is never encrypted.
                writer.ObjectEncryptor = null;
                xrefEntries.Add((encryptDictObjNum, 0, writer.WriteIndirectObject(encryptDictObjNum, 0, originalEncrypt)));

                long xrefOffset = writer.WriteXrefTable(xrefEntries, 0);
                if (mac)
                    WriteTrailerWithPdfMac(writer, output, trailer, xrefOffset, resolved.Key, kdfSalt);
                else
                    writer.WriteTrailer(trailer, xrefOffset);
            }
        }

        /// <summary>
        /// §7.4.10: a stream naming its own crypt filter may name <c>/Identity</c> or <c>/StdCF</c>, the one
        /// filter this encryptor's <c>/CF</c> defines. Any other name is one no reader of the file could
        /// resolve, so it is refused, not guessed — before any byte is written, on either layout.
        /// </summary>
        private static void RequireKnownCryptFilter(PdfObject obj, int objectNumber)
        {
            if (obj is PdfStream stream && Core.IO.Filters.StreamCryptFilter.TryGetName(stream, out PdfName own)
                && own != PdfName.Identity && own != PdfName.StdCF)
                throw new PdfException(
                    $"Object {objectNumber} names crypt filter /{own.Value}, which this document's /CF " +
                    "would not define; only /Identity and /StdCF can be honoured.");
        }

        /// <summary>
        /// Writes an encrypted document in the compressed layout — object streams and a cross-reference
        /// stream — through <see cref="PdfCompressedFileWriter"/> with <paramref name="encryptor"/> on its
        /// options: every top-level object encrypted, each object stream whole under its own number with
        /// its members in the clear inside (ISO 32000-1 §7.5.7), the <c>/Encrypt</c> dictionary and the
        /// cross-reference stream unencrypted (§7.6.2, §7.5.8.2). With <paramref name="seal"/> the PDF MAC
        /// placeholder travels in the trailer the writer copies into the cross-reference stream's
        /// dictionary (ISO/TS 32004 §6.5.1), and is sealed from the <c>startxref</c> the file ends with.
        /// Shared by the password and public-key encryptors.
        /// </summary>
        internal static void WriteCompressedEncryptedFile(
            Stream output, PdfVersion version, Dictionary<int, PdfObject> content,
            int encryptDictObjNum, PdfDictionary encryptDict, PdfDictionary trailer,
            IPdfObjectEncryptor encryptor, PdfMacTokenSeal seal)
        {
            var objects = new List<(int objectNumber, PdfObject obj)>(content.Count + 1);
            foreach (var kvp in content.OrderBy(x => x.Key))
                objects.Add((kvp.Key, kvp.Value));
            objects.Add((encryptDictObjNum, encryptDict));

            if (seal != null)
                trailer.Set(PdfName.AuthCode, seal.CreatePlaceholder());

            // Object streams are PDF 1.5 (§7.5.7); the writer would raise the header itself, with a warning.
            var compressedFloor = new PdfVersion(1, 5);
            if (version.CompareTo(compressedFloor) < 0) version = compressedFloor;

            long fileStart = output.CanSeek ? output.Position : 0;
            PdfCompressedFileWriter.Write(output, version, objects, trailer,
                new PdfCompressedWriteOptions { ObjectEncryptor = encryptor });

            if (seal != null)
                seal.Seal(output, fileStart + StartXrefOffset(output, fileStart));
        }

        /// <summary>The <c>startxref</c> value of the file just written — the cross-reference stream's offset, where the PDF MAC placeholder is.</summary>
        private static long StartXrefOffset(Stream output, long fileStart)
        {
            long end = output.Position;
            int tailLength = (int)Math.Min(64, end - fileStart);
            var tail = new byte[tailLength];
            output.Position = end - tailLength;
            StreamBytes.Fill(output, tail, 0, tailLength);
            output.Position = end;

            string text = Core.Text.Latin1Text.Decode(tail, 0, tailLength);
            int at = text.LastIndexOf("startxref", StringComparison.Ordinal);
            if (at < 0) throw new PdfException("The compressed writer's output ends without a startxref line.");
            string digits = new string(text.Substring(at + "startxref".Length).Trim().TakeWhile(char.IsDigit).ToArray());
            return long.Parse(digits, System.Globalization.CultureInfo.InvariantCulture);
        }

        #region PDF MAC (ISO/TS 32004)

        /// <summary>
        /// Writes the trailer with a standalone PDF MAC token (§6.5.1) in its <c>/AuthCode</c>
        /// dictionary — placed and sealed by <see cref="StandalonePdfMacSeal"/>, the one implementation
        /// of that byte arithmetic, shared with <c>IncrementalUpdateWriter</c>'s appended revisions.
        /// </summary>
        internal static void WriteTrailerWithPdfMac(
            PdfWriter writer, Stream output, PdfDictionary trailer, long xrefOffset,
            byte[] fileEncryptionKey, byte[] kdfSalt)
        {
            var seal = new PdfMacTokenSeal(fileEncryptionKey, kdfSalt);
            trailer.Set(PdfName.AuthCode, seal.CreatePlaceholder());
            writer.WriteTrailer(trailer, xrefOffset);
            seal.Seal(output, xrefOffset);
        }

        #endregion

        #region Parameter Validation

        /// <summary>
        /// Whether an algorithm is written at <c>/V 5</c> or later, where ISO/TS 32004 allows a PDF
        /// MAC token (Table 5: "If present, the value of the V entry in the document's Encrypt
        /// dictionary shall be at least 5") — read off the one algorithm table, for the password
        /// encryptor, the public-key one and the round trip alike.
        /// </summary>
        /// <remarks>
        /// Below it nothing authenticates the permission bit that says a token is required
        /// (<c>/Perms</c> begins at revision 6), so a token written there is removed by deleting
        /// two plain-text entries and no reader can tell.
        /// </remarks>
        internal static bool CanCarryPdfMac(EncryptionAlgorithm algorithm)
        {
            if (!Enum.IsDefined(typeof(EncryptionAlgorithm), algorithm) || algorithm == EncryptionAlgorithm.Identity)
                return false;
            GetAlgorithmParameters(algorithm, out int v, out _, out _);
            return v >= PdfMacTokenSeal.MinimumEncryptionVersion;
        }

        private static void ValidateParameters(EncryptionParameters parameters, bool refuseTokenBelowVersion5)
        {
            if (parameters.EnablePdfMac && refuseTokenBelowVersion5 && !CanCarryPdfMac(parameters.Algorithm))
                throw new ArgumentException(
                    $"PDF MAC cannot be written with {parameters.Algorithm}: ISO/TS 32004 Table 5 allows a token " +
                    "only where the encryption dictionary's /V is at least 5, which is AES-256 or AES-256-GCM. " +
                    "Below it nothing authenticates the permission that says a token is required, so the token " +
                    "could be removed unseen. Choose EncryptionAlgorithm.AES256, or leave EnablePdfMac off.",
                    nameof(parameters));

            // Per PDF spec §7.6.3.3, an empty owner password is permitted; when empty it
            // defaults to the user password during key derivation.  We therefore only
            // reject a null reference, not an empty string.
            if (parameters.OwnerPassword == null)
                throw new ArgumentException("Owner password must not be null (use an empty string to allow no owner password).", nameof(parameters));

            if (!Enum.IsDefined(typeof(EncryptionAlgorithm), parameters.Algorithm))
                throw new ArgumentException("Invalid encryption algorithm.", nameof(parameters));

            // §7.6.5: /EFF is a crypt filter, and crypt filters begin at V 4 — RC4 here is V 1 or 2,
            // which has one key for everything and no way to leave streams in the clear.
            if (parameters.EncryptOnlyAttachments
                && parameters.Algorithm != EncryptionAlgorithm.AES128
                && parameters.Algorithm != EncryptionAlgorithm.AES256
                && parameters.Algorithm != EncryptionAlgorithm.AES256GCM)
                throw new ArgumentException(
                    "EncryptOnlyAttachments needs a crypt-filter algorithm (AES-128 or later); " +
                    $"{parameters.Algorithm} has no crypt filters.", nameof(parameters));

            // /EncryptMetadata is "meaningful only when the value of V is 4 or 5" (ISO 32000-2
            // Table 21): under RC4 a reader decrypts the metadata stream whatever the entry says, so
            // leaving it plain would turn it into garbage. The public-key encryptor refuses the same pair.
            if (!parameters.EncryptMetadata
                && parameters.Algorithm != EncryptionAlgorithm.AES128
                && parameters.Algorithm != EncryptionAlgorithm.AES256
                && parameters.Algorithm != EncryptionAlgorithm.AES256GCM)
                throw new ArgumentException(
                    "EncryptMetadata = false needs a crypt-filter algorithm (AES-128 or later); under " +
                    $"{parameters.Algorithm} the entry means nothing and readers decrypt the metadata anyway.",
                    nameof(parameters));
        }

        #endregion

        #region Algorithm Parameters

        /// <summary>
        /// An algorithm's <c>/V</c>, <c>/R</c> and key length in bits (ISO 32000-2 Tables 20 and 21,
        /// ISO/TS 32003) — the one table, read by the password encryptor and the public-key one alike.
        /// </summary>
        internal static void GetAlgorithmParameters(EncryptionAlgorithm algorithm, out int v, out int r, out int keyLength)
        {
            switch (algorithm)
            {
                case EncryptionAlgorithm.RC4_40:
                    v = 1;
                    r = 2;
                    keyLength = 40;
                    break;
                case EncryptionAlgorithm.RC4_40R3:
                    // Revision 3's key derivation and /U over a 40-bit RC4 key (ISO 32000-1 Table 20
                    // allows /R 3 with /V 1): the form kept for an input that had it.
                    v = 1;
                    r = 3;
                    keyLength = 40;
                    break;
                case EncryptionAlgorithm.RC4_128:
                    v = 2;
                    r = 3;
                    keyLength = 128;
                    break;
                case EncryptionAlgorithm.AES128:
                    v = 4;
                    r = 4;
                    keyLength = 128;
                    break;
                case EncryptionAlgorithm.AES256:
                    v = 5;
                    r = 6;
                    keyLength = 256;
                    break;
                case EncryptionAlgorithm.AES256GCM:
                    // ISO/TS 32003 Table 2/3: V=6 "defines the use of encryption and decryption in
                    // the same way as when the value of V is 5", and R=7 is required alongside it.
                    v = 6;
                    r = 7;
                    keyLength = 256;
                    break;
                default:
                    throw new PdfException($"Unsupported encryption algorithm: {algorithm}");
            }
        }

        internal static PdfVersion DetermineMinimumVersion(
            PdfVersion current, EncryptionAlgorithm algorithm, bool pdfMacEnabled = false)
        {
            int reqMajor = 1;
            int reqMinor;
            switch (algorithm)
            {
                case EncryptionAlgorithm.RC4_40:
                    reqMinor = 1;
                    break;
                case EncryptionAlgorithm.RC4_40R3:   // revision 3 arrived with PDF 1.4
                case EncryptionAlgorithm.RC4_128:
                    reqMinor = 4;
                    break;
                case EncryptionAlgorithm.AES128:
                    reqMinor = 6;
                    break;
                case EncryptionAlgorithm.AES256:
                case EncryptionAlgorithm.AES256GCM:
                    reqMajor = 2;
                    reqMinor = 0;
                    break;
                default:
                    reqMinor = 4;
                    break;
            }

            // ISO/TS 32004 is a PDF 2.0 feature (/KDFSalt and /AuthCode are both new entries),
            // regardless of which crypt filter the document otherwise uses.
            if (pdfMacEnabled)
            {
                reqMajor = 2;
                reqMinor = 0;
            }

            if (current.Major > reqMajor || (current.Major == reqMajor && current.Minor >= reqMinor))
                return current;
            return new PdfVersion(reqMajor, reqMinor);
        }

        #endregion

        #region Permission Value

        /// <summary>
        /// Computes the 32-bit /P value from PdfPermissions flags.
        /// Bits 1-2 must be zero, bits 7-8 are reserved (must be 1 for revision >= 2),
        /// and bits 13-32 must be 1.
        /// </summary>
        /// <param name="permissions">The caller's requested permission flags.</param>
        /// <param name="pdfMacRequired">
        /// ISO/TS 32004 §5.1.2 repurposes bit 13, otherwise reserved-as-1: clearing it signals
        /// that a PDF MAC token is required in every revision of the document. Pass <c>true</c>
        /// only when <see cref="EncryptionParameters.EnablePdfMac"/> is set, so a document with no
        /// PDF MAC support keeps the all-1s reserved block every other caller expects.
        /// </param>
        internal static int ComputePermissionValue(PdfPermissions permissions, bool pdfMacRequired = false)
        {
            // Keep the caller's permission bits — spec bits 3-12, i.e. 0-based 2-11 = mask 0xFFC.
            //
            // Bit 3 is not reserved: spec bit 3 IS the print permission (PdfPermissions.Print ==
            // 1 << 2), so ORing in 0x4 would grant printing no matter what the caller passed. Bits
            // 1-2 are the ones that "shall be 0" (ISO 32000-1 Table 22), and masking with 0xFFC
            // leaves them clear.
            int p = (int)permissions & 0xFFC;

            // Set bits 13-31 to 1 (as required by the spec for rev 2+).
            // In two's-complement 32-bit, bits 13-31 set means OR with 0xFFFFF000.
            p |= unchecked((int)0xFFFFF000);

            // Bits 7-8 must be set to 1 for revision 3+.
            p |= 0xC0; // bits 7 and 8

            // ISO/TS 32004 §5.1.2, Table 3/4: bit 13 (0-based bit 12, mask 0x1000) is zero rather
            // than the reserved 1 when a PDF MAC token is required in every revision.
            if (pdfMacRequired)
                p &= ~0x1000;

            return p;
        }

        #endregion

        #region File ID

        internal static byte[] GenerateOrExtractFileId(PdfDocument doc)
        {
            var idArray = doc.Trailer.GetArray(PdfName.ID);
            if (idArray != null && idArray.Count >= 1)
            {
                var firstId = idArray[0] as PdfString;
                if (firstId != null && firstId.RawBytes.Length > 0)
                    return firstId.RawBytes;
            }

            // Generate a random 16-byte file ID.
            var id = new byte[FileIdLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(id);
            }
            return id;
        }

        #endregion

        #region RC4/AES-128 Key Derivation (Standard Handler, Algorithm 2)

        /// <summary>
        /// Computes the /O (owner) value per Algorithm 3 of the PDF spec.
        /// </summary>
        private static byte[] ComputeOwnerValue(string userPassword, string ownerPassword, int revision, int keyLengthBits)
        {
            int keyLenBytes = keyLengthBits / PdfEncryptionPrimitives.BitsPerByte;

            // Steps a to d — the padded owner password's MD5, re-hashed for revision 3 over its first
            // n bytes, the first n bytes the RC4 key — are the decryptor's too (PdfEncryptionPrimitives).
            byte[] rc4Key = PdfEncryptionPrimitives.ComputeOwnerRc4Key(ownerPassword, revision, keyLenBytes);

            // Step e: Pad the user password.
            byte[] userPadded = PdfEncryptionPrimitives.PadPassword(userPassword);

            // Step f: RC4-encrypt the padded user password.
            byte[] encrypted = PdfEncryptionPrimitives.Rc4Process(rc4Key, userPadded);

            // Step g: For revision 3+, do 19 additional RC4 passes with modified keys.
            if (revision >= PdfEncryptionPrimitives.RevisionRc4Variable)
            {
                for (int i = 1; i <= PdfEncryptionPrimitives.Rc4MixRounds; i++)
                {
                    byte[] tempKey = new byte[keyLenBytes];
                    for (int j = 0; j < keyLenBytes; j++)
                        tempKey[j] = (byte)(rc4Key[j] ^ (byte)i);
                    encrypted = PdfEncryptionPrimitives.Rc4Process(tempKey, encrypted);
                }
            }

            return encrypted;
        }

        #endregion

        #region AES-256 Key Derivation (PDF 2.0, Extension Level 3 / ISO 32000-2)

        /// <summary>
        /// Computes all AES-256 encryption values per ISO 32000-2 (revision 6).
        /// Uses SHA-256 for key derivation with validation/key salt patterns.
        /// </summary>
        private static void ComputeAes256Keys(
            string userPassword, string ownerPassword, int pValue, byte[] fileId, bool encryptMetadata,
            out byte[] encryptionKey, out byte[] oValue, out byte[] uValue,
            out byte[] oeValue, out byte[] ueValue, out byte[] permsValue)
        {
            // ISO 32000-2 §7.6.4.3.3: a revision-6 password is SASLprep-normalised (RFC 4013) before
            // it is hashed — on BOTH sides, at the one derivation site each (this method, and
            // PdfDecryptor.ValidateAndDeriveAes256Key). Built from the raw string on one side only,
            // a password holding a mapped code point — a no-break space, a soft hyphen, a zero-width
            // space — would produce a document its own password cannot open, here or in a conformant
            // reader. ASCII passwords are unchanged by the normalisation, which is why no ASCII round
            // trip can see it. Revisions 2–4 take the raw Latin-1 bytes and must not be normalised;
            // their branch in Encrypt never reaches this method.
            userPassword = PdfPasswordNormalizer.Normalize(userPassword);
            ownerPassword = PdfPasswordNormalizer.Normalize(ownerPassword);

            // Truncate passwords to 127 bytes per spec, encoded as UTF-8.
            byte[] userPwdBytes = PdfEncryptionPrimitives.TruncateUtf8(
                userPassword, PdfEncryptionPrimitives.MaxPasswordBytesR6);
            byte[] ownerPwdBytes = PdfEncryptionPrimitives.TruncateUtf8(
                ownerPassword, PdfEncryptionPrimitives.MaxPasswordBytesR6);

            // Generate a random 32-byte file encryption key.
            encryptionKey = new byte[PdfEncryptionPrimitives.FileKeyLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(encryptionKey);
            }

            // --- Compute /U value ---
            // Generate random validation salt (8 bytes) and key salt (8 bytes).
            byte[] userValSalt = GenerateRandomBytes(SaltLength);
            byte[] userKeySalt = GenerateRandomBytes(SaltLength);

            // /U = AlgorithmR6Hash(password, validationSalt) || validationSalt || keySalt
            // Total: 48 bytes.
            byte[] uHash = PdfEncryptionPrimitives.ComputeHashR6(userPwdBytes, userValSalt);
            uValue = new byte[PasswordEntryLength];
            Buffer.BlockCopy(uHash, 0, uValue, 0, PdfEncryptionPrimitives.FileKeyLength);
            Buffer.BlockCopy(userValSalt, 0, uValue, ValidationSaltOffset, SaltLength);
            Buffer.BlockCopy(userKeySalt, 0, uValue, KeySaltOffset, SaltLength);

            // --- Compute /UE value ---
            // UE key = AlgorithmR6Hash(password, keySalt)
            byte[] ueKey = PdfEncryptionPrimitives.ComputeHashR6(userPwdBytes, userKeySalt);
            // Encrypt the file encryption key with AES-256 CBC, zero IV.
            ueValue = PdfEncryptionPrimitives.AesCbcNoPadding(
                true, ueKey, new byte[PdfEncryptionPrimitives.AesBlockBytes], encryptionKey);

            // --- Compute /O value ---
            byte[] ownerValSalt = GenerateRandomBytes(SaltLength);
            byte[] ownerKeySalt = GenerateRandomBytes(SaltLength);

            // /O = AlgorithmR6Hash(password, validationSalt, /U) || validationSalt || keySalt
            byte[] oHash = PdfEncryptionPrimitives.ComputeHashR6(ownerPwdBytes, ownerValSalt, uValue);
            oValue = new byte[PasswordEntryLength];
            Buffer.BlockCopy(oHash, 0, oValue, 0, PdfEncryptionPrimitives.FileKeyLength);
            Buffer.BlockCopy(ownerValSalt, 0, oValue, ValidationSaltOffset, SaltLength);
            Buffer.BlockCopy(ownerKeySalt, 0, oValue, KeySaltOffset, SaltLength);

            // --- Compute /OE value ---
            byte[] oeKey = PdfEncryptionPrimitives.ComputeHashR6(ownerPwdBytes, ownerKeySalt, uValue);
            oeValue = PdfEncryptionPrimitives.AesCbcNoPadding(
                true, oeKey, new byte[PdfEncryptionPrimitives.AesBlockBytes], encryptionKey);

            // --- Compute /Perms value ---
            // 16-byte value: first 4 bytes = P (little-endian), next 4 = 0xFFFFFFFF,
            // byte 8 = 'T' (if EncryptMetadata is true, which we always do), bytes 9-11 = 'adb',
            // bytes 12-15 = random.
            var permsInput = new byte[PermsLength];
            byte[] pBytes = BitConverter.GetBytes(pValue);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(pBytes);
            Buffer.BlockCopy(pBytes, 0, permsInput, 0, PermsPValueBytes);
            permsInput[4] = 0xFF;
            permsInput[5] = 0xFF;
            permsInput[6] = 0xFF;
            permsInput[7] = 0xFF;
            permsInput[8] = encryptMetadata ? (byte)'T' : (byte)'F';
            permsInput[9] = (byte)'a';
            permsInput[10] = (byte)'d';
            permsInput[11] = (byte)'b';
            var randomTail = GenerateRandomBytes(PermsRandomTailBytes);
            Buffer.BlockCopy(randomTail, 0, permsInput,
                PermsLength - PermsRandomTailBytes, PermsRandomTailBytes);

            // Encrypt with AES-256 ECB (no padding, single block).
            permsValue = AesEcbEncryptBlock(encryptionKey, permsInput);
        }

        internal static byte[] GenerateRandomBytes(int count)
        {
            var bytes = new byte[count];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return bytes;
        }

        #endregion

        #region Encrypt Dictionary

        private static PdfDictionary BuildEncryptDictionary(
            EncryptionAlgorithm algorithm, int v, int r, int keyLengthBits, int pValue,
            byte[] oValue, byte[] uValue, byte[] oeValue, byte[] ueValue, byte[] permsValue,
            bool encryptMetadata = true, byte[] kdfSalt = null, bool attachmentsOnly = false)
        {
            var dict = new PdfDictionary();
            dict.Set(PdfName.Type, PdfName.Encrypt);
            dict.Set(PdfName.Filter, PdfName.Standard);
            dict.Set(PdfName.V, new PdfNumber(v));
            dict.Set(PdfName.R, new PdfNumber(r));
            dict.Set(PdfName.P, new PdfNumber(pValue));
            dict.Set(PdfName.O, new PdfString(oValue, true));
            dict.Set(PdfName.U, new PdfString(uValue, true));

            if (!encryptMetadata)
                dict.Set(PdfName.EncryptMetadata, PdfBoolean.False);

            // ISO/TS 32004 §5.1.1: required whenever the document makes use of PDF MAC, independent
            // of which crypt filter algorithm is otherwise in play.
            if (kdfSalt != null)
                dict.Set(PdfName.KDFSalt, new PdfString(kdfSalt, true));

            if (algorithm == EncryptionAlgorithm.RC4_40)
            {
                // V=1 implies 40-bit; /Length is optional (40 is default)
            }
            else if (algorithm == EncryptionAlgorithm.RC4_128 || algorithm == EncryptionAlgorithm.RC4_40R3)
            {
                // Stated for revision 3 even at 40 bits, as the documents that carry /V 1 /R 3 state it.
                dict.Set(PdfName.Length, new PdfNumber(keyLengthBits));
            }
            else if (algorithm == EncryptionAlgorithm.AES128)
            {
                dict.Set(PdfName.Length, new PdfNumber(keyLengthBits));
                SetStandardCryptFilter(dict, PdfName.AESV2, Aes128KeyBytes);
            }
            else if (algorithm == EncryptionAlgorithm.AES256 || algorithm == EncryptionAlgorithm.AES256GCM)
            {
                dict.Set(PdfName.Length, new PdfNumber(keyLengthBits));

                // AES-GCM (ISO/TS 32003 Table 4) is AES-256's shape with /CFM /AESV4 in place of /AESV3.
                SetStandardCryptFilter(dict,
                    algorithm == EncryptionAlgorithm.AES256 ? PdfName.AESV3 : PdfName.AESV4, Aes256KeyBytes);

                // The same key-wrapping entries for both — ISO/TS 32003 §5.1 changes only the per-object cipher.
                dict.Set(PdfName.OE, new PdfString(oeValue, true));
                dict.Set(PdfName.UE, new PdfString(ueValue, true));
                dict.Set(PdfName.Perms, new PdfString(permsValue, true));
            }

            // §7.6.5: streams and strings through /Identity, embedded files through the crypt filter.
            if (attachmentsOnly)
            {
                dict.Set(PdfName.StmF, PdfName.Identity);
                dict.Set(PdfName.StrF, PdfName.Identity);
                dict.Set(PdfName.EFF, PdfName.StdCF);
            }

            return dict;
        }

        /// <summary>
        /// The input opened from a seekable copy, refused when it is already encrypted. The copy
        /// rewinds a seekable input first: copied from its current position, a <see cref="FileStream"/>
        /// a host had read from would be a truncated document <c>Open</c> fails on.
        /// </summary>
        private static PdfDocument OpenUnencrypted(Stream input)
        {
            MemoryStream ms = SeekableStream.Buffered(input);
            PdfDocument doc;
            try { doc = PdfDocument.Open(ms); }
            catch (Exception ex) when (!(ex is PdfException))
            {
                throw new PdfException("Failed to parse the input PDF for encryption.", ex);
            }

            if (doc.IsEncrypted)
            {
                doc.Dispose();
                throw new PdfException("The input PDF is already encrypted. Decrypt it first before re-encrypting.");
            }
            return doc;
        }

        /// <summary>
        /// Every indirect object of the cross-reference table that is content: the source's object
        /// streams and cross-reference stream are its layout (see <see cref="FileLayoutObjects"/>), and
        /// the parser hands every packed member over as an object of its own.
        /// </summary>
        private static Dictionary<int, PdfObject> ContentObjects(PdfDocument doc, out bool inputHasObjectStreams)
        {
            var allObjects = new Dictionary<int, PdfObject>();
            inputHasObjectStreams = false;
            foreach (var kvp in doc.Parser.EnumerateAllObjects())
            {
                if (FileLayoutObjects.IsLayout(kvp.Value))
                {
                    if (FileLayoutObjects.IsObjectStream(kvp.Value)) inputHasObjectStreams = true;
                    continue;
                }
                allObjects[kvp.Key] = kvp.Value;
            }
            // The references the parser resolved into place go back before any writer sees the graph,
            // whatever layout and encryptor write it (ParserInlinedObjects).
            ParserInlinedObjects.RestoreReferences(allObjects);
            return allObjects;
        }

        /// <summary>
        /// The standard handler's <c>/CF</c> with its one filter, <c>/StdCF</c>, and <c>/StmF</c> and
        /// <c>/StrF</c> naming it. The filter's <c>/Length</c> is in bytes, unlike <c>/Encrypt</c>'s.
        /// </summary>
        private static void SetStandardCryptFilter(PdfDictionary dict, PdfName cfm, int keyBytes)
        {
            var stdCf = new PdfDictionary();
            stdCf.Set(PdfName.Type, CryptFilterType);
            stdCf.Set(PdfName.CFM, cfm);
            stdCf.Set(PdfName.AuthEvent, PdfName.DocOpen);
            stdCf.Set(PdfName.Length, new PdfNumber(keyBytes));

            var cfDict = new PdfDictionary();
            cfDict.Set(PdfName.StdCF, stdCf);

            dict.Set(PdfName.CF, cfDict);
            dict.Set(PdfName.StmF, PdfName.StdCF);
            dict.Set(PdfName.StrF, PdfName.StdCF);
        }

        #endregion

        #region Trailer Cloning

        /// <summary>
        /// Builds the output trailer for a full encrypting rewrite.
        /// </summary>
        /// <param name="encryptObjNum">
        /// The object number allocated for the /Encrypt dictionary. Both callers allocate it as
        /// <c>CrossReferenceTable.GetMaxObjectNumber() + 1</c> and every other object they write
        /// comes out of the same table, so this is always the <b>highest</b> object number in the
        /// output — which is what makes it, and not a count, the correct basis for /Size.
        /// </param>
        /// <param name="newSize">
        /// An optional /Size the caller already knows to be correct, used only when it is
        /// <b>higher</b> than the floor derived from <paramref name="encryptObjNum"/>. Omit it
        /// (the default 0) to take the derived value, which is what
        /// <c>PdfPublicKeyEncryptor</c> does.
        /// </param>
        /// <remarks>
        /// A count such as <c>encryptedObjects.Count + 2</c> is only right when the source's live
        /// object numbers are exactly 1..N with no gaps. <c>GetMaxObjectNumber()</c> spans free
        /// entries too, while <c>PdfParser.EnumerateAllObjects()</c> skips free entries and objects
        /// that fail to parse — so on any sparse xref the count understates the range. §7.5.5
        /// requires /Size to be one greater than the highest object number used, and since /Encrypt
        /// is allocated at the very top it is always the object that falls outside an understated
        /// /Size: a conforming reader that bounds object numbers by /Size cannot resolve /Encrypt, so
        /// the document does not open at all. Deriving the floor here rather than at each call site
        /// keeps the two writers in this assembly agreeing about it; a caller may
        /// still raise it, never lower it.
        /// </remarks>
        /// <param name="originalTrailer">
        /// The source document's trailer. Copied rather than mutated, so the loaded document is
        /// left as it was.
        /// </param>
        /// <param name="fileId">
        /// The first element of <c>/ID</c>, which the key derivation is bound to and which must
        /// therefore be written unencrypted alongside <c>/Encrypt</c>.
        /// </param>
        /// <param name="promoteInto">
        /// The objects about to be written, encrypted: a direct dictionary, array or string of the trailer
        /// (but <c>/ID</c>) is moved into it under a new number above <paramref name="encryptObjNum"/> and
        /// referred to from the trailer. <c>null</c> copies every value as it is.
        /// </param>
        internal static PdfDictionary CloneTrailerForEncryption(PdfDictionary originalTrailer, int encryptObjNum, byte[] fileId, int newSize = 0,
            IDictionary<int, PdfObject> promoteInto = null)
        {
            var trailer = new PdfDictionary();
            int next = Math.Max(newSize, encryptObjNum + 1);

            // Copy the trailer entries a rewrite carries — one rule with the decryptor, see TrailerRewrite.
            // /Encrypt is replaced below; a PDF MAC writer sets its own /AuthCode after this returns.
            foreach (var kvp in originalTrailer)
            {
                if (TrailerRewrite.IsNotCarried(kvp.Key))
                    continue;

                // §7.6.2: a string outside an indirect object is never encrypted, so a direct value here
                // — a direct /Info's title and author, a direct catalog — would stay readable without the
                // password. Table 15 requires /Info and /Root to be indirect: each such value is written
                // as an object of its own, which the writer encrypts. /ID is replaced below.
                PdfObject value = kvp.Value;
                if (promoteInto != null && kvp.Key != PdfName.ID
                    && (value is PdfDictionary || value is PdfArray || value is PdfString))
                {
                    promoteInto[next] = value;
                    value = new PdfReference(next, 0);
                    next++;
                }
                trailer.Set(kvp.Key, value);
            }

            // /Size is one greater than the highest object number used (§7.5.5). The /Encrypt
            // dictionary is the highest object this writer emits but for any value promoted above, so
            // that number + 1 is the floor no caller may fall below. Clamped rather than computed
            // outright so a caller that has walked what it is actually about to write
            // (PdfEncryptor.Encrypt does) can still raise it — an object number above
            // GetMaxObjectNumber() is the one shape the derived floor cannot see.
            int size = Math.Max(next, Math.Max(newSize, encryptObjNum + 1));
            trailer.Set(PdfName.Size, new PdfNumber(size));

            // Set /Encrypt as a reference to the encrypt dictionary object.
            trailer.Set(PdfName.Encrypt, new PdfReference(encryptObjNum, 0));

            // Set /ID array. Both elements should be the same file ID for new encryption.
            var idArray = new PdfArray();
            idArray.Add(new PdfString(fileId, true));
            idArray.Add(new PdfString(fileId, true));
            trailer.Set(PdfName.ID, idArray);

            return trailer;
        }

        #endregion

        #region Object Encryption

        /// <summary>
        /// Recursively encrypts strings and streams within a PdfObject.
        /// Returns a new object tree with encrypted data.
        /// </summary>
        internal static PdfObject EncryptObject(PdfObject obj, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm algorithm, int revision)
        {
            if (obj is PdfStream stream)
            {
                return EncryptStream(stream, objectNumber, generationNumber, encryptionKey, algorithm, revision);
            }

            if (obj is PdfDictionary dict)
            {
                return EncryptDictionary(dict, objectNumber, generationNumber, encryptionKey, algorithm, revision);
            }

            if (obj is PdfArray array)
            {
                return EncryptArray(array, objectNumber, generationNumber, encryptionKey, algorithm, revision);
            }

            if (obj is PdfString str)
            {
                return EncryptString(str, objectNumber, generationNumber, encryptionKey, algorithm, revision);
            }

            // Other types (PdfNumber, PdfName, PdfBoolean, PdfNull, PdfReference) pass through unchanged.
            return obj;
        }

        /// <summary>
        /// Encrypts <paramref name="value"/>, found inside the indirect object (<paramref name="objectNumber"/>,
        /// <paramref name="generationNumber"/>). A direct value is keyed by that object — ISO 32000-2 §7.6.3.2,
        /// Algorithm 1 step (a): "If the string is a direct object, use the identifier of the indirect object
        /// containing it." An indirect object the parser resolved into place goes back as the reference the file had,
        /// and is encrypted where it is written: as the top-level object it is, under its own identifier. An inline
        /// copy was keyed under the object it came from, which no reader deciphers it under — see
        /// <see cref="ParserInlinedObjects"/>.
        /// </summary>
        private static PdfObject EncryptContained(PdfObject value, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm algorithm, int revision)
            => ParserInlinedObjects.TryReference(value, out PdfReference reference)
                ? reference
                : EncryptObject(value, objectNumber, generationNumber, encryptionKey, algorithm, revision);

        private static PdfStream EncryptStream(PdfStream stream, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm algorithm, int revision)
        {
            var newStream = new PdfStream();
            newStream.ObjectNumber = objectNumber;
            newStream.GenerationNumber = generationNumber;

            // Copy dictionary entries, encrypting string values.
            foreach (var kvp in stream)
            {
                newStream.Set(kvp.Key, EncryptContained(kvp.Value, objectNumber, generationNumber, encryptionKey, algorithm, revision));
            }

            // Encrypt the raw stream data. Per PDF spec, encryption is applied to the raw (encoded)
            // stream data — as-is, already encoded with its filters.
            byte[] rawData = stream.RawData ?? Array.Empty<byte>();
            byte[] encryptedData = EncryptBytes(rawData, objectNumber, generationNumber, encryptionKey, algorithm, revision);
            newStream.RawData = encryptedData;

            // Update /Length to match the encrypted data size.
            newStream.Set(PdfName.Length, new PdfNumber(encryptedData.Length));

            return newStream;
        }

        private static PdfDictionary EncryptDictionary(PdfDictionary dict, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm algorithm, int revision)
        {
            var newDict = new PdfDictionary();
            newDict.ObjectNumber = objectNumber;
            newDict.GenerationNumber = generationNumber;

            foreach (var kvp in dict)
            {
                // ISO 32000-2 §7.6.2: the /Contents of a signature dictionary is not encrypted. The
                // decryptor skips it on the way back for the same reason, and the two must agree —
                // encrypting it here and not decrypting it there would garble a signature's CMS on
                // every round trip through this pair.
                if (SignatureDictionaryShape.IsUnencryptedEntry(dict, kvp.Key))
                {
                    newDict.Set(kvp.Key, kvp.Value);
                    continue;
                }

                newDict.Set(kvp.Key, EncryptContained(kvp.Value, objectNumber, generationNumber, encryptionKey, algorithm, revision));
            }

            return newDict;
        }

        private static PdfArray EncryptArray(PdfArray array, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm algorithm, int revision)
        {
            var newArray = new PdfArray();
            newArray.ObjectNumber = array.ObjectNumber;
            newArray.GenerationNumber = array.GenerationNumber;

            foreach (var item in array)
            {
                newArray.Add(EncryptContained(item, objectNumber, generationNumber, encryptionKey, algorithm, revision));
            }

            return newArray;
        }

        private static PdfString EncryptString(PdfString str, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm algorithm, int revision)
        {
            byte[] encrypted = EncryptBytes(str.RawBytes, objectNumber, generationNumber, encryptionKey, algorithm, revision);
            return new PdfString(encrypted, true);
        }

        #endregion

        #region Low-Level Encryption

        /// <summary>
        /// Encrypts a byte array using the appropriate algorithm.
        /// For RC4 and AES-128, derives a per-object key. For AES-256, uses the file encryption key directly.
        /// </summary>
        private static byte[] EncryptBytes(byte[] data, int objectNumber, int generationNumber,
            byte[] encryptionKey, EncryptionAlgorithm algorithm, int revision)
            => PdfEncryptionPrimitives.EncryptData(data, objectNumber, generationNumber, encryptionKey, algorithm);

        #endregion

        #region Cryptographic Primitives (BouncyCastle)

        /// <summary>
        /// AES-CBC encryption with PKCS7 padding. Prepends a random 16-byte IV.
        /// </summary>
        internal static byte[] AesCbcEncrypt(byte[] key, byte[] data)
        {
            // Generate a random IV.
            byte[] iv = GenerateRandomBytes(PdfEncryptionPrimitives.AesBlockBytes);

            byte[] encrypted = Tetitulz.Pdf.Core.Encryption.PdfCryptoPrimitives.AesCbcPkcs7(
                encrypt: true, key, iv, data);

            // Prepend the IV to the ciphertext per PDF spec.
            byte[] result = new byte[PdfEncryptionPrimitives.AesBlockBytes + encrypted.Length];
            Buffer.BlockCopy(iv, 0, result, 0, PdfEncryptionPrimitives.AesBlockBytes);
            Buffer.BlockCopy(encrypted, 0, result, PdfEncryptionPrimitives.AesBlockBytes, encrypted.Length);
            return result;
        }

        /// <summary>
        /// AES-256-GCM encryption per ISO/TS 32003 §5.2: a random 12-byte IV, then the
        /// ciphertext, then the 16-byte authentication tag — <c>IV || ciphertext || tag</c>. No
        /// AAD (the TS requires it nil) and no padding (GCM needs none).
        /// </summary>
        internal static byte[] AesGcmEncrypt(byte[] key, byte[] data)
        {
            byte[] iv = GenerateRandomBytes(PdfGcmCipher.IvLength);
            var ciphertext = new byte[data.Length];
            var tag = new byte[PdfGcmCipher.TagLength];
            PdfGcmCipher.Encrypt(key, iv, data, ciphertext, tag);

            var result = new byte[PdfGcmCipher.IvLength + ciphertext.Length + PdfGcmCipher.TagLength];
            Buffer.BlockCopy(iv, 0, result, 0, PdfGcmCipher.IvLength);
            Buffer.BlockCopy(ciphertext, 0, result, PdfGcmCipher.IvLength, ciphertext.Length);
            Buffer.BlockCopy(tag, 0, result, PdfGcmCipher.IvLength + ciphertext.Length, PdfGcmCipher.TagLength);
            return result;
        }

        /// <summary>
        /// AES-ECB encryption of a single 16-byte block (no padding). Used for /Perms.
        /// </summary>
        internal static byte[] AesEcbEncryptBlock(byte[] key, byte[] data)
        {
            byte[] output = Tetitulz.Pdf.Core.Encryption.PdfCryptoPrimitives.AesEcbNoPadding(
                encrypt: true, key, data);

            var result = new byte[PermsLength];
            Buffer.BlockCopy(output, 0, result, 0, PermsLength);
            return result;
        }

        #endregion
    }
}
