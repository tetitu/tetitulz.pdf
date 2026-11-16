// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Signing for <b>encrypted</b> documents, in place: an appended revision whose objects are
    /// encrypted under the document's own file key, so the output opens with the same password and
    /// the signature verifies.
    ///
    /// <para><see cref="PdfSigner.Sign(Stream, Stream, SignatureParameters)"/> refuses one, and has
    /// to: ISO 32000-2 §7.6.2 encrypts every string a signature revision writes — the field's
    /// <c>/T</c>, <c>/Reason</c>, <c>/Location</c>, <c>/ContactInfo</c>, <c>/M</c> — except "any
    /// hexadecimal strings representing the value of the Contents key in a Signature dictionary", and
    /// <c>Tetitulz.Pdf.Signing</c> holds no file key. This assembly does, so the composition lives here,
    /// in the same shape as <see cref="PdfEncryptedForms"/>: the object graph comes from a decrypted
    /// copy, the revision is appended to the original bytes through
    /// <see cref="PdfEncryptedIncrementalUpdate"/>, and the signer's plan is what travels between the
    /// two — <see cref="PdfSigner.Plan"/> before the append, <see cref="PdfSigner.Complete"/> after.</para>
    ///
    /// <para><b>The original bytes are preserved</b>, so an earlier signature over them still verifies
    /// and the owner password is untouched — neither survives <see cref="PdfEncryptedRoundTrip"/>.</para>
    ///
    /// <para><b>A PDF MAC-protected document is signed, not declined.</b> The appended revision's
    /// fresh <i>standalone</i> token would cover the signature's placeholders and break the moment
    /// the CMS is written into them, and a signature's <c>/ByteRange</c> covers the token in turn —
    /// so TS 32004 §6.5.2's <c>AttachedToSig</c> placement is used instead: the token rides inside
    /// the CMS as an unsigned attribute, and the trailer's <c>AuthCode</c> dictionary says where to
    /// find it. The standalone seal is suppressed for that revision; the two placements are
    /// alternatives, not layers.</para>
    ///
    /// <para><b>Still declined, each with its reason.</b> A public-key encrypted document:
    /// <c>PdfDecryptor.ResolveEncryption</c> is <c>/Standard</c>-only, as for every other write in
    /// this assembly. A visible appearance: it is a stream, and none of the appearance generators
    /// plan one as an object yet.</para>
    /// </summary>
    public static class PdfEncryptedSigner
    {
        /// <summary>
        /// Signs an encrypted document and appends the signature as one encrypted revision.
        /// </summary>
        /// <param name="input">The encrypted PDF. Read only.</param>
        /// <param name="output">
        /// Receives the original bytes followed by the signed revision. Written only once the whole
        /// signature is complete; a refusal or a wrong password leaves it untouched.
        /// </param>
        /// <param name="password">User or owner password.</param>
        /// <param name="parameters">
        /// The signature to make, as for <see cref="PdfSigner"/>. <see cref="SignatureParameters.EmbedLtv"/>
        /// and <see cref="SignatureParameters.UsageRights"/> are refused by the plan — see <see cref="PdfSigner.Plan"/>.
        /// </param>
        /// <returns>The plan the signature was completed from: the field name and the reserve.</returns>
        /// <exception cref="PdfException">
        /// The document is not encrypted, uses an unsupported handler, or the password is wrong.
        /// </exception>
        /// <exception cref="PdfSigningException">The signature cannot be made or does not fit its reserve.</exception>
        public static SignaturePlan Sign(Stream input, Stream output, string password, SignatureParameters parameters)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            byte[] original = StreamBytes.ReadAll(input);

            // Refusals and key derivation before a byte moves. A wrong password is refused here
            // too, by the derivation itself: TryAttachedToSigMaterial resolves the file key.
            bool macProtected;
            byte[] macKey, macSalt;
            using (var probe = PdfDocument.Open(new MemoryStream(original, writable: false)))
            {
                if (!probe.IsEncrypted)
                    throw new PdfException(
                        "The document is not encrypted; sign it with Tetitulz.Pdf.Signing.PdfSigner, " +
                        "which writes the revision in the clear.");

                macProtected = PdfMacTokenSeal.TryAttachedToSigMaterial(
                    probe, password, out macKey, out macSalt);
            }

            // Assembled in memory: Complete reads the whole signed range back and patches two
            // placeholders, which the caller's stream need not allow — and a failure anywhere below
            // must leave the caller's output holding nothing rather than an unsigned revision.
            var signer = new PdfSigner();
            SignaturePlan plan = null;
            using (var signed = new MemoryStream())
            {
                PdfEncryptedIncrementalUpdate.Run(
                    new MemoryStream(original, writable: false), signed, password,
                    (decrypted, firstFreeObjectNumber) =>
                    {
                        plan = signer.Plan(decrypted, firstFreeObjectNumber, parameters);
                        if (!macProtected) return new EncryptedRevision(plan.Objects);

                        // ISO/TS 32004 §6.5.2, and the order matters. The AuthCode dictionary must
                        // be in the trailer BEFORE the signature's /ByteRange is digested, because
                        // it sits inside that range — so it is written now, complete, which Table 6
                        // permits only because AttachedToSig forbids /ByteRange and /MAC and leaves
                        // two entries this plan already knows. The token itself cannot exist yet:
                        // its signatureDigest covers a signature that has not been made.
                        plan.CmsPostProcessor = (cms, dataDigest) =>
                            PdfMacToken.AttachToSignature(cms, dataDigest, macKey, macSalt);

                        return new EncryptedRevision(
                            plan.Objects, authCode: AttachedToSigAuthCode(plan.SignatureObjectNumber));
                    });

                signer.Complete(plan, signed);

                signed.Position = 0;
                signed.CopyTo(output);
            }

            output.Flush();
            return plan;
        }

        /// <summary>
        /// ISO/TS 32004 Table 6's <c>AuthCode</c> dictionary for a signed revision: two entries and
        /// no others.
        /// </summary>
        /// <remarks>
        /// <b>What is absent is required to be absent.</b> Table 6 makes <c>/ByteRange</c> and
        /// <c>/MAC</c> conditionally required for <c>Standalone</c> and says of each that "if
        /// MACLocation is AttachedToSig, this entry shall not be present" — the token is in the
        /// signature's CMS, and the range it is bound to is the signature's own. <c>/SigObjRef</c>
        /// is the one entry Table 6 allows to be indirect, and NOTE 4 records that its value equals
        /// the signature field's <c>/V</c>: it exists purely so a validator can find the token.
        /// </remarks>
        private static PdfDictionary AttachedToSigAuthCode(int signatureObjectNumber)
        {
            var authCode = new PdfDictionary();
            authCode.Set(PdfName.MACLocation, PdfName.AttachedToSig);
            authCode.Set(PdfName.SigObjRef, new PdfReference(signatureObjectNumber, 0));
            return authCode;
        }
    }
}
