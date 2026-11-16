// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Security.Cryptography;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Checks a document's ISO/TS 32004 PDF MAC token — both placements — against the document it
    /// claims to protect.
    /// </summary>
    /// <remarks>
    /// <para><b>What a PDF MAC adds over a signature.</b> It binds the file to the encryption key,
    /// so a party who cannot open the document cannot alter it undetectably either, and §6.6.3
    /// NOTE 2 records the design intent for the signed case: the token carries both digests "so
    /// that the PDF MAC can be validated independently of the underlying signature", protecting the
    /// document and the signature "irrespective of the validation status of said signature".</para>
    ///
    /// <para><b>This is not a signature check and does not pretend to be one.</b> A valid MAC says
    /// the holder of the file encryption key produced these bytes; who that was is a question only
    /// a signature answers. <c>PdfEncryptionOperations.VerifySignatures</c> is the other half.</para>
    ///
    /// <para><b>No token is not a failure.</b> Most encrypted documents have none — ISO/TS 32004 is
    /// a PDF 2.0 addition — so absence is reported as <see cref="PdfMacVerification.IsProtected"/>
    /// false rather than as an invalid MAC. Collapsing the two would make every pre-2.0 document
    /// look tampered with.</para>
    /// </remarks>
    public static class PdfMacVerifier
    {
        /// <summary>
        /// Verifies <paramref name="input"/>'s PDF MAC token, if it has one.
        /// </summary>
        /// <param name="input">The document. Read from position 0; not disposed.</param>
        /// <param name="password">The user or owner password — the token is keyed on the file key.</param>
        /// <exception cref="PdfException">The document is not encrypted, or the password is wrong.</exception>
        public static PdfMacVerification Verify(Stream input, string password)
            => Verify(input, doc =>
            {
                // Named here rather than left to the decryptor's "unsupported handler", because at
                // this point the answer is not "this cannot be done" but "this needs the other
                // overload" — a refusal a caller can act on is worth the two lines.
                if (PdfPublicKeyDecryptor.UsesPublicKeyHandler(doc))
                    throw new PdfException(
                        "The document uses the /Adobe.PubSec security handler, which has no password. " +
                        "Its PDF MAC token is keyed on a file key recovered from a CMS envelope, so " +
                        "verify it with the PKCS#12 or IExternalDecryptor overload of Verify.");

                return PdfDecryptor.ResolveEncryption(doc, password ?? string.Empty);   // SASLprep is the V≥5 derivation's
            });

        /// <summary>
        /// Verifies the PDF MAC token of an <c>/Adobe.PubSec</c> document, using a PKCS#12 store
        /// holding the recipient's certificate and private key.
        /// </summary>
        /// <remarks>
        /// <b>A public-key document can carry a token too</b> — <c>PdfPublicKeyEncryptor</c> writes
        /// one — and §6.4 keys it on "the file encryption key used by the document's security
        /// handler" whichever handler that is. The password overload cannot check such a document:
        /// there is no password, and the key comes from a CMS envelope instead.
        /// </remarks>
        /// <param name="input">The document. Read from position 0; not disposed.</param>
        /// <param name="pkcs12">The recipient's PKCS#12 store. Not disposed.</param>
        /// <param name="password">The store's password, or null when it has none.</param>
        public static PdfMacVerification Verify(Stream input, Stream pkcs12, string password)
        {
            if (pkcs12 == null) throw new ArgumentNullException(nameof(pkcs12));
            return Verify(input, doc => PdfPublicKeyDecryptor.Resolve(doc, pkcs12, password));
        }

        /// <summary>
        /// Verifies the PDF MAC token of an <c>/Adobe.PubSec</c> document with a private key that
        /// stays on a device.
        /// </summary>
        /// <param name="input">The document. Read from position 0; not disposed.</param>
        /// <param name="decryptor">The device holding the recipient's private key.</param>
        public static PdfMacVerification Verify(Stream input, IExternalDecryptor decryptor)
        {
            if (decryptor == null) throw new ArgumentNullException(nameof(decryptor));
            return Verify(input, doc => PdfPublicKeyDecryptor.Resolve(doc, decryptor));
        }

        /// <summary>
        /// The one verification, with the credential as its only parameter.
        /// </summary>
        /// <remarks>
        /// <b>Everything after the file key is handler-independent</b>, which is why the three
        /// public overloads differ in one delegate and nothing else. §6.4 derives the KEK from the
        /// file encryption key and <c>/KDFSalt</c> without asking how the key was recovered, and
        /// §6.5/§6.6 are about where the token sits and what it digests. A second copy of this for
        /// the public-key handler could only agree with this one or be a defect.
        /// </remarks>
        private static PdfMacVerification Verify(
            Stream input, Func<Core.Document.PdfDocument, PdfDecryptor.ResolvedEncryption> resolve)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            byte[] bytes = StreamBytes.ReadAll(input);
            using var file = new MemoryStream(bytes, writable: false);
            using var doc = Core.Document.PdfDocument.Open(file);

            if (!doc.IsEncrypted)
                throw new PdfException(
                    "The document is not encrypted. ISO/TS 32004 keys a PDF MAC token on the file " +
                    "encryption key, so an unencrypted document cannot carry one.");

            // No token in the trailer is "unprotected" only when none was required. The trailer and
            // the encryption dictionary are plain text anyone can edit, so a token is removed by
            // deleting its entry — and reading that as "this document has no PDF MAC" answers the
            // question with the tamperer's own edit.
            PdfObject entry = doc.Trailer?[PdfName.AuthCode];
            if (entry == null || entry is PdfNull)
            {
                bool publicKey = PdfPublicKeyDecryptor.UsesPublicKeyHandler(doc);
                PdfDecryptor.ResolvedEncryption resolved = resolve(doc);
                string required = WhyATokenIsRequired(doc, resolved, publicKey);
                return required == null
                    ? PdfMacVerification.NotProtected()
                    : PdfMacVerification.Invalid(null, required);
            }

            // Table 5: the entry "shall be a direct object". An indirect one is a token placed where
            // no reader is told to look for it, not an absent one.
            if (!(entry is PdfDictionary authCode))
                return PdfMacVerification.Invalid(
                    null, "the trailer's /AuthCode is not a direct dictionary (ISO/TS 32004 Table 5).");

            // SaltWhenProtected answers "is this protected" from /KDFSalt and /P; an /AuthCode
            // without a usable salt is a document claiming a protection it cannot carry, which is a
            // failure rather than an absence.
            byte[] salt = PdfMacTokenSeal.SaltWhenProtected(doc);
            if (salt == null)
                return PdfMacVerification.Invalid(
                    null,
                    "the trailer carries an /AuthCode but the encryption dictionary has no 32-byte " +
                    "/KDFSalt, so the token cannot be keyed (§6.4, Table 2).");

            byte[] key = resolve(doc).Key;

            string location = authCode.GetName(PdfName.MACLocation)?.Value;

            // §B.2.1: "Absence of the MACLocation entry is indicative of an invalid MAC."
            if (location == null)
                return PdfMacVerification.Invalid(null, "the /AuthCode dictionary has no /MACLocation entry (Table 6).");

            switch (location)
            {
                case "Standalone":
                    return VerifyStandalone(file, doc, authCode, key, salt);
                case "AttachedToSig":
                    return VerifyAttachedToSig(file, doc, authCode, key, salt);
                default:
                    // Table 6: any other value "shall be a second-class name", and B.2.1 makes the
                    // outcome implementation-dependent. Refusing names what was not understood
                    // rather than passing a document whose protection we did not check.
                    return PdfMacVerification.Invalid(
                        location,
                        $"/MACLocation is '{location}', which is neither Standalone nor AttachedToSig; " +
                        "the token's placement is not one this implementation knows how to check (Table 6).");
            }
        }

        /// <summary>
        /// Why a document whose trailer carries no <c>/AuthCode</c> should have carried one, or
        /// <c>null</c> when it is simply not protected.
        /// </summary>
        /// <remarks>
        /// <para><b>The requirement is permission bit 13</b> — ISO/TS 32004 Table 3: "When zero,
        /// indicates that a PDF MAC token is required to be present in all revisions of the
        /// document" — <b>and the bit is only as good as what authenticates it.</b> Under the
        /// AES-256 standard handler that is <c>/Perms</c>, the permissions encrypted under the file
        /// key, which the key derivation has just checked: anything but "OK" means the <c>/P</c> in the file is
        /// not the one the document was encrypted with, and a flipped bit 13 is what removing a
        /// token undetected needs. Such a document is reported, not passed as unprotected.</para>
        /// <para><b>Under the public-key handler it is the envelope.</b> Table 4 puts the bit in the
        /// recipient's permissions, the four bytes after the seed. Every recipient blob is hashed
        /// into the file key, so the envelope that opened is the one the document was encrypted
        /// with, and its word decides whatever the dictionary's plain-text <c>/P</c> and
        /// <c>/KDFSalt</c> now say.</para>
        /// <para><b>Below version 5 the salt is the only witness.</b> Table 5 allows a token only
        /// from <c>/V 5</c>; a document below it that carries one is outside the specification,
        /// nothing authenticates its <c>/P</c>, and producers cleared the bit freely before it had
        /// a meaning. Table 2 puts <c>/KDFSalt</c> in "documents that make use of PDF MAC" and
        /// nowhere else, so there a token is held required only where the salt says the document
        /// used one — a witness the same edit can remove.</para>
        /// </remarks>
        private static string WhyATokenIsRequired(
            Core.Document.PdfDocument doc, PdfDecryptor.ResolvedEncryption resolved, bool publicKey)
        {
            PdfDictionary encrypt = doc.EncryptionDictionary;
            bool hasSalt = encrypt?[PdfName.KDFSalt] is PdfString salt && salt.RawBytes.Length == 32;
            int permissions = PdfPermissionValue.FromNumber(doc.ResolveObject(encrypt?[PdfName.P])) ?? 0;
            bool bitSaysRequired = (permissions & PdfMacTokenSeal.PdfMacRequiredPermissionBit) == 0;

            const string removed =
                " The trailer carries no /AuthCode: the token was removed, or a revision was appended " +
                "without one (ISO/TS 32004 Annex B).";

            if (resolved.Version >= 5 && resolved.EnvelopePermissions is int enveloped)
                return (enveloped & PdfMacTokenSeal.PdfMacRequiredPermissionBit) == 0
                    ? "permission bit 13 is zero in the permissions this recipient's envelope holds, which requires a " +
                      "PDF MAC token in every revision of the document (ISO/TS 32004 Table 4)." + removed
                    : null;

            if (publicKey || resolved.Version < 5)
                return hasSalt && bitSaysRequired
                    ? "the encryption dictionary carries a /KDFSalt, which only a document that uses PDF MAC has " +
                      "(Table 2), and permission bit 13 is zero, which requires a token in every revision (Table 3)." + removed
                    : null;

            string perms = PdfDecryptor.LastPermsValidation;
            if (perms != "OK")
                return "whether this document requires a PDF MAC token cannot be read: the requirement is " +
                       "permission bit 13, and the /P that holds it is not authenticated by /Perms (" +
                       (perms ?? "not checked") + "). A /P altered after encryption is how a token is removed unseen.";

            return bitSaysRequired
                ? "permission bit 13 is zero, which requires a PDF MAC token in every revision of the document " +
                  "(ISO/TS 32004 Table 3)." + removed
                : null;
        }

        /// <summary>§6.5.1: the token is the <c>/MAC</c> string, over the <c>/ByteRange</c> beside it.</summary>
        private static PdfMacVerification VerifyStandalone(
            Stream file, Core.Document.PdfDocument doc, PdfDictionary authCode, byte[] key, byte[] salt)
        {
            if (!(authCode[PdfName.MAC] is PdfString mac))
                return PdfMacVerification.Invalid("Standalone", "/MACLocation is Standalone but there is no /MAC entry (Table 6).");

            if (!TryReadRange(doc, authCode[PdfName.ByteRange], out long[] range, out string rangeError))
                return PdfMacVerification.Invalid("Standalone", rangeError);

            // Table 6: the range is the whole file but the /MAC value, and that value is '<', the
            // token's hex digits and '>' with no white space (NOTE 2). What fills the gap is read, not
            // sized: the token written as a literal string takes half the room, and the rest — no
            // digest covers it — would close /AuthCode and add trailer keys the parser obeys.
            if (range[0] != 0 || !mac.IsHex
                || !Core.Writing.StandalonePdfMacSeal.GapHoldsOnlyToken(file, range[1], range[2], mac.RawBytes))
                return PdfMacVerification.Invalid(
                    "Standalone",
                    "the bytes the /ByteRange leaves out are not exactly the /MAC token as '<', its hex " +
                    "digits and '>' (ISO/TS 32004 Table 6), or the range does not start at the file's " +
                    "first byte; bytes no digest covers stand where only the token may.");

            if (!TryDigest(file, range, out byte[] dataDigest))
                return PdfMacVerification.Invalid("Standalone", "the /ByteRange reaches past the end of the file.");

            // §6.5.1: the range of the most recent unsigned revision's token "shall cover the entire
            // PDF file". Checked BEFORE the digest is compared, because the digest will match — the
            // bytes the range names are still there and unchanged — and a token that verifies over
            // a prefix is precisely Annex B.2.2.2's stale one.
            if (!Core.Writing.StandalonePdfMacSeal.CoversToEndOfFile(file, range[2] + range[3]))
                return PdfMacVerification.Invalid(
                    "Standalone",
                    "the /ByteRange stops before the end of the file, so the token protects only part " +
                    "of it. ISO/TS 32004 §6.5.1 requires a standalone token to cover the whole file; " +
                    "Annex B.2.2.2 reads a short range as tampering, or as a token left stale by a " +
                    "processor that appended a revision without applying a new MAC.");

            string problem = PdfMacToken.Verify(mac.RawBytes, key, salt, dataDigest, expectedSignatureDigest: null);
            return problem == null
                ? PdfMacVerification.Valid("Standalone")
                : PdfMacVerification.Invalid("Standalone", problem);
        }

        /// <summary>
        /// §6.5.2: the token is an unsigned attribute on the signature <c>/SigObjRef</c> names, and
        /// the range it covers is that signature's, not the <c>AuthCode</c> dictionary's.
        /// </summary>
        private static PdfMacVerification VerifyAttachedToSig(
            Stream file, Core.Document.PdfDocument doc, PdfDictionary authCode, byte[] key, byte[] salt)
        {
            // Table 6 makes these two conditional-presence rules, and a document breaking them is
            // describing a placement it is not using.
            if (authCode[PdfName.ByteRange] != null || authCode[PdfName.MAC] != null)
                return PdfMacVerification.Invalid(
                    "AttachedToSig",
                    "the /AuthCode dictionary carries /ByteRange or /MAC, which Table 6 forbids when " +
                    "/MACLocation is AttachedToSig: the token is in the signature's CMS and the " +
                    "range it covers is the signature's own.");

            if (!(doc.ResolveObject(authCode[PdfName.SigObjRef]) is PdfDictionary signature))
                return PdfMacVerification.Invalid(
                    "AttachedToSig", "/SigObjRef does not resolve to a signature dictionary (Table 6, B.2.3.1).");

            if (!(signature[PdfName.Contents] is PdfString contents))
                return PdfMacVerification.Invalid(
                    "AttachedToSig", "the signature /SigObjRef names has no /Contents to take the token from.");

            if (!PdfMacToken.TryExtractAttachedToken(contents.RawBytes, out byte[] token, out byte[] signatureValue))
                return PdfMacVerification.Invalid(
                    "AttachedToSig",
                    "the signature carries no id-attr-pdfMacData unsigned attribute with exactly one value, " +
                    "so the token /MACLocation promises is not there (§6.5.2).");

            // B.2.3.2/B.2.3.3: the range is the SIGNATURE's, which is also what makes the MAC's
            // coverage the signature's coverage.
            if (!TryReadRange(doc, signature[PdfName.ByteRange], out long[] range, out string rangeError))
                return PdfMacVerification.Invalid("AttachedToSig", rangeError);

            // The gap is the signature's /Contents and nothing else — the rule the signature verifier
            // holds it to (§12.8.1): bytes beside the token's CMS that no digest covers would stand
            // where a reader takes the signature dictionary's own.
            if (range[0] != 0
                || !Core.Writing.StandalonePdfMacSeal.SignatureGapHoldsOnlyContents(file, range[1], range[2] - range[1], contents.RawBytes))
                return PdfMacVerification.Invalid(
                    "AttachedToSig",
                    "the bytes the signature's /ByteRange leaves out hold more than its /Contents, or the " +
                    "range does not start at the file's first byte (ISO 32000-2 §12.8.1).");

            if (!TryDigest(file, range, out byte[] dataDigest))
                return PdfMacVerification.Invalid("AttachedToSig", "the signature's /ByteRange reaches past the end of the file.");

            // §6.5.2's counterpart of the rule above: "If the most recent revision in a document
            // with a PDF MAC token is a signed revision, then the ByteRange of the relevant
            // signature shall cover the entire PDF file". B.2.3.2 says the coverage question is the
            // same as the standalone case and only the range's location differs.
            if (!Core.Writing.StandalonePdfMacSeal.CoversToEndOfFile(file, range[2] + range[3]))
                return PdfMacVerification.Invalid(
                    "AttachedToSig",
                    "the signature's /ByteRange stops before the end of the file, so the token " +
                    "protects only part of it — ISO/TS 32004 §6.5.2 requires the relevant " +
                    "signature's range to cover the whole file, and B.2.3.2 reads it the same way " +
                    "as the standalone case.");

            byte[] signatureDigest;
            using (var sha = SHA256.Create()) signatureDigest = sha.ComputeHash(signatureValue);

            string problem = PdfMacToken.Verify(token, key, salt, dataDigest, signatureDigest);
            return problem == null
                ? PdfMacVerification.Valid("AttachedToSig")
                : PdfMacVerification.Invalid("AttachedToSig", problem);
        }

        /// <summary>The four nonnegative integers of a <c>/ByteRange</c> (Table 6, Table 255).</summary>
        /// <remarks>
        /// <para><b>This is a second reader of one grammar, and the reason is the assembly boundary.</b>
        /// <c>Core.Document.SignedByteRanges.TryRead</c> is the §12.8.1 read side that the verifier,
        /// the DocMDP analysis, the counter-signer, the incremental-save report and the revision
        /// attributor all go through — and it is <c>internal</c> to Core, which <c>Encryption</c>
        /// cannot see. That is the same wall that made <c>SignatureDictionaryShape</c> public.
        /// <b>Consolidating means promoting that type to public API, which is a decision about
        /// Core's surface rather than a tidy-up</b>, so it is recorded here rather than done quietly.</para>
        ///
        /// <para><b>Two differences from the shared reader are deliberate, not drift.</b> This one
        /// requires <i>exactly</i> four elements, because ISO/TS 32004 §6.5.1 covers the whole file
        /// either side of one token hole and a fifth entry would be a region the MAC does not
        /// digest; the shared reader accepts a longer array and hands back <c>ElementCount</c> so
        /// each caller can impose its own rule. And this one rejects negative values, where the
        /// shared reader leaves bounds entirely to its callers by design.</para>
        /// </remarks>
        private static bool TryReadRange(
            Core.Document.PdfDocument doc, PdfObject value, out long[] range, out string error)
        {
            range = null;
            error = null;

            if (!(doc.ResolveObject(value) is PdfArray array) || array.Count != 4)
            {
                error = "the /ByteRange is missing or is not an array of four integers.";
                return false;
            }

            range = new long[4];
            for (int i = 0; i < 4; i++)
            {
                // IsInteger, not a comparison on DoubleValue: PdfNumber documents that its LongValue
                // cast "would silently truncate (e.g. 1e30 -> long.MinValue)", and IsInteger is what
                // bounds the value to long's range and to a whole number. Testing DoubleValue < 0 and
                // then casting let a huge POSITIVE value through the check and out the other side as
                // long.MinValue — a negative offset produced by the guard against negative offsets.
                if (!(doc.ResolveObject(array[i]) is PdfNumber number)
                    || !number.IsInteger || number.LongValue < 0)
                {
                    error = "the /ByteRange contains a value that is not a nonnegative integer.";
                    return false;
                }
                range[i] = number.LongValue;
            }

            return true;
        }

        /// <summary>
        /// The digest the token should carry — computed by the same code that computes it when a
        /// token is written, so the two cannot drift.
        /// </summary>
        private static bool TryDigest(Stream file, long[] range, out byte[] digest)
        {
            digest = Core.Writing.StandalonePdfMacSeal.DigestByteRange(
                file, range[0], range[1], range[2], range[3]);
            return digest != null;
        }
    }

    /// <summary>The outcome of checking a document's PDF MAC token.</summary>
    /// <remarks>
    /// <b>Three states, not two.</b> A document with no token is neither valid nor invalid — it is
    /// unprotected, and a caller deciding what to show a user needs that distinction more than it
    /// needs a boolean.
    /// </remarks>
    public sealed class PdfMacVerification
    {
        private PdfMacVerification(bool isProtected, bool isValid, string location, string error)
        {
            IsProtected = isProtected;
            IsValid = isValid;
            Location = location;
            Error = error;
        }

        /// <summary>Whether the document carries a PDF MAC token at all.</summary>
        public bool IsProtected { get; }

        /// <summary>Whether that token verifies. Always false when <see cref="IsProtected"/> is false.</summary>
        public bool IsValid { get; }

        /// <summary>
        /// The <c>/MACLocation</c> value — <c>Standalone</c> or <c>AttachedToSig</c> — or null when
        /// the document has no token or names a placement this implementation does not know.
        /// </summary>
        public string Location { get; }

        /// <summary>Why the token did not verify, or null when it did or there was none.</summary>
        public string Error { get; }

        internal static PdfMacVerification NotProtected() => new PdfMacVerification(false, false, null, null);

        internal static PdfMacVerification Valid(string location) =>
            new PdfMacVerification(true, true, location, null);

        internal static PdfMacVerification Invalid(string location, string error) =>
            new PdfMacVerification(true, false, location, error);
    }
}
