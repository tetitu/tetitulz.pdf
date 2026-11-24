// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Base.Encryption;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.IO;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Encryption
{
    /// <summary>
    /// Runs a plaintext-only PDF operation over an <b>encrypted</b> document:
    /// decrypt → invoke the operation → re-encrypt, as one call.
    ///
    /// <para><b>Why this lives here.</b> A large class of operations refuses encrypted input —
    /// <c>PdfSanitizer</c>, <c>PdfOptimizer</c> and <c>PdfLinearizer</c> renumber every object
    /// while copying <c>/Encrypt</c> verbatim (per-object keys derive from the object number, ISO
    /// 32000-1 §7.6.2, so their output would be silently corrupt, and they fail closed);
    /// every writer in <c>Tetitulz.Pdf.Accessibility</c> appends a <i>clear-text</i> incremental
    /// update that would not match the encryption dictionary. Those refusals are not caution, they
    /// are a missing capability: neither assembly references this one, so neither can decrypt.</para>
    ///
    /// <para><b>Why the dependency is inverted rather than pushed down.</b>
    /// <c>Tetitulz.Pdf.Encryption</c> references <c>Tetitulz.Pdf.Base</c>, so <c>Base</c> can never
    /// reference back; and giving the Maintenance plugin or the Accessibility feature a reference
    /// to this assembly would drag BouncyCastle into their package graphs. So the operation comes
    /// <i>here</i>, as a delegate, and every refusing site stays exactly as it is — still failing
    /// closed when called directly with an encrypted document, which is still correct.</para>
    ///
    /// <example>
    /// <code>
    /// var result = PdfEncryptedRoundTrip.Run(input, output,
    ///     new EncryptedRoundTripOptions { OpenPassword = "secret" },
    ///     (plain, produced) =&gt; PdfSanitizer.Sanitize(plain, produced));
    ///
    /// if (!result.Succeeded) throw new InvalidOperationException(result.FailureReason);
    /// if (result.OwnerPasswordChanged) Log("owner password could not be preserved");
    /// </code>
    /// </example>
    ///
    /// <para><b>Two limits no round trip can remove.</b> (1) The original <c>/Encrypt</c>
    /// dictionary cannot be reproduced — <c>/O</c> and <c>/U</c> derive one-way from the two
    /// passwords and only one of them is in the caller's hand; see
    /// <see cref="ReEncryptionPolicy"/>. (2) Re-encryption rewrites every string and stream, so any
    /// existing digital signature is invalidated; a signed document is refused unless
    /// <see cref="EncryptedRoundTripOptions.AllowSignatureInvalidation"/> is set.</para>
    ///
    /// <para><b>Failures are reported, not thrown</b> (except for argument errors). Every refusal
    /// returns a <see cref="RoundTripResult"/> with <see cref="RoundTripResult.Succeeded"/> false
    /// and a <see cref="RoundTripResult.FailureReason"/>, and <b>nothing is written to the output
    /// stream</b> — not a truncated document, not a zero-byte file where one already existed. An
    /// exception thrown by the caller's own <c>operation</c> propagates unwrapped, because its type
    /// carries meaning (a <c>PdfRedactionException</c> must stay one).</para>
    /// </summary>
    public static class PdfEncryptedRoundTrip
    {
        /// <summary>
        /// The reason the last round trip on <b>this thread</b> did not run, or <c>null</c>.
        /// Mirrors <see cref="RoundTripResult.FailureReason"/> for callers that discard the result.
        /// <c>[ThreadStatic]</c> per the diagnostics pattern in CLAUDE.md: a process-wide slot
        /// would report a neighbouring thread's failure.
        /// </summary>
        [ThreadStatic]
        private static string _lastError;

        /// <summary>See <see cref="_lastError"/>. Read it on the thread that ran the operation.</summary>
        public static string LastError => _lastError;

        /// <summary>
        /// Decrypts <paramref name="input"/>, runs <paramref name="operation"/> over the plaintext,
        /// and re-encrypts the result into <paramref name="output"/>.
        ///
        /// <para>An <b>unencrypted</b> input is not an error: the operation runs directly and the
        /// result reports <see cref="ReEncryptionPolicy.None"/>, so one call site can handle both
        /// kinds of document. The signature guard does not apply on that path — nothing is
        /// re-encrypted, so only the operation itself can affect a signature, which is the
        /// caller's business.</para>
        /// </summary>
        /// <param name="input">
        /// The document, read in full. A <b>seekable</b> stream is rewound first, so a caller that
        /// has already read part of it (a header sniff, a reused handle) still gets the whole
        /// document; a non-seekable one is read from where it is, because there is no alternative.
        /// The position is not restored — see <see cref="ReadAll"/>.
        /// </param>
        /// <param name="output">
        /// Receives the produced document. <b>Untouched unless the whole chain succeeds</b> —
        /// the result is assembled in memory and copied out only at the end, so a mid-chain
        /// failure cannot leave a half-written file behind.
        /// </param>
        /// <param name="options">Password and re-encryption policy. <c>null</c> uses the defaults.</param>
        /// <param name="operation">
        /// The plaintext operation, e.g. <c>PdfSanitizer.Sanitize</c>, <c>PdfOptimizer.Optimize</c>,
        /// <c>PdfLinearizer.Linearize</c>, <c>PdfRedactor.RedactRegions</c> or
        /// <c>PdfAccessibility.RemediateWithReport</c>. (Named as prose deliberately: this assembly
        /// must not reference a plugin or a feature to name them in code.)
        /// </param>
        public static RoundTripResult Run(
            Stream input,
            Stream output,
            EncryptedRoundTripOptions options,
            Action<Stream, Stream> operation)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (operation == null) throw new ArgumentNullException(nameof(operation));

            options = options ?? new EncryptedRoundTripOptions();

            if (options.Policy == ReEncryptionPolicy.Explicit && options.ExplicitParameters == null)
                throw new ArgumentException(
                    "ReEncryptionPolicy.Explicit requires EncryptedRoundTripOptions.ExplicitParameters.",
                    nameof(options));

            // Refused before the file is read, like the one above: an empty recipient list would
            // otherwise produce a document nobody can open, which is a worse outcome than a refusal
            // and is indistinguishable from success until someone tries to open it.
            if (options.Policy == ReEncryptionPolicy.Recipients
                && (options.RecipientCertificates == null || options.RecipientCertificates.Count == 0))
                throw new ArgumentException(
                    "ReEncryptionPolicy.Recipients requires EncryptedRoundTripOptions.RecipientCertificates: " +
                    "a public-key document does not carry its recipients' certificates, only enough to " +
                    "identify them, so there is nothing in the input to re-encrypt to.",
                    nameof(options));

            _lastError = null;

            byte[] original = ReadAll(input);
            byte[] plaintext = null;

            try
            {
                if (!IsEncrypted(original))
                    return RunUnencrypted(original, output, operation);

                EncryptionInfo info;
                try
                {
                    using (var probe = new MemoryStream(original, writable: false))
                        info = PdfDecryptor.GetEncryptionInfo(probe);
                }
                catch (PdfException ex)
                {
                    return Fail("the encryption dictionary could not be read: " + ex.Message);
                }

                EncryptionParameters parameters;
                string mapFailure;
                if (!TryDeriveParameters(info, options, out parameters, out mapFailure))
                    return Fail(mapFailure);

                EncryptionAlgorithm algorithmIn;
                if (!TryDetectAlgorithm(info, options.Policy, out algorithmIn, out mapFailure))
                    return Fail(mapFailure);

                // The password the output is written under, when ReuseOpenPassword asks for the one
                // that opened the input: the credential's when the caller handed one, else OpenPassword.
                // Two that differ are refused — the caller named two passwords for one document.
                string openPassword = options.OpenPassword ?? "";
                if (options.Credential != null && options.Credential.IsPassword)
                {
                    string given = options.Credential.PasswordValue ?? "";
                    if (!string.IsNullOrEmpty(options.OpenPassword) && given != options.OpenPassword)
                        return Fail("EncryptedRoundTripOptions.Credential and OpenPassword name different passwords; " +
                                    "set one of them.");
                    openPassword = given;
                }

                var warnings = new List<string>();

                // Decrypt. A wrong password surfaces here, before anything is produced.
                var credential = options.Credential ?? PdfDecryptionCredential.Password(openPassword);
                using (var source = new MemoryStream(original, writable: false))
                using (var decrypted = new MemoryStream())
                {
                    try
                    {
                        // The credential is the only thing in the round trip that ever needed the
                        // password: everything after the plaintext is handler-independent.
                        credential.Decrypt(source, decrypted);
                    }
                    catch (PdfException ex)
                    {
                        return Fail("the document could not be decrypted: " + ex.Message);
                    }

                    plaintext = decrypted.ToArray();
                }

                // What the decrypt could not carry is not in the output either: said, never inferred.
                if (PdfDecryptor.LastObjectStreamRejection is string dropped)
                    warnings.Add("Objects were dropped while decrypting the input, and are absent from the output: " + dropped);

                // An owner-only document opens with the empty user password whatever the caller typed:
                // the output then opens with the same empty password, never with the caller's string,
                // which authenticated nothing.
                if (PdfDecryptor.LastAuthenticatedCredential == PdfDecryptor.AuthenticatedCredential.EmptyUserFallback
                    && openPassword.Length > 0)
                {
                    openPassword = "";
                    if (options.Policy == ReEncryptionPolicy.ReuseOpenPassword)
                        warnings.Add("The supplied password did not open the document; its empty user password did, " +
                                     "so the output is written with the empty user password it opened with.");
                }
                if (options.Policy == ReEncryptionPolicy.ReuseOpenPassword)
                    parameters.UserPassword = parameters.OwnerPassword = openPassword;

                // KeepOriginal: what the output is written under — the original's file key and
                // algorithms, its /Encrypt dictionary and its first file identifier — read now, before
                // the operation runs, so a document this cannot keep is refused with nothing produced.
                KeptEncryption kept = null;
                if (options.Policy == ReEncryptionPolicy.KeepOriginal)
                {
                    string keepFailure;
                    if (!TryReadKeptEncryption(original, credential, info, out kept, out keepFailure))
                        return Fail(keepFailure);
                }

                // The signature check needs the catalog, which PdfDocument.Load does not read for
                // an encrypted file — so it runs here, on the plaintext, and still before any
                // output is produced.
                if (options.Policy != ReEncryptionPolicy.None && HasSignature(plaintext))
                {
                    if (!options.AllowSignatureInvalidation)
                        return Fail(
                            "the document carries a digital signature, which re-encryption would " +
                            "invalidate (every string and stream is rewritten, so the signature's " +
                            "/ByteRange no longer covers what it signed). Set " +
                            "EncryptedRoundTripOptions.AllowSignatureInvalidation to proceed and " +
                            "discard the signature.");

                    warnings.Add(
                        "The document carried a digital signature. Re-encryption has invalidated " +
                        "it; verification of the output will fail.");
                }

                byte[] produced;
                using (var plainIn = new MemoryStream(plaintext, writable: false))
                using (var operationOut = new MemoryStream())
                {
                    // Deliberately unwrapped: the operation is the caller's own code and its
                    // exception type carries meaning.
                    operation(plainIn, operationOut);
                    produced = operationOut.ToArray();
                }

                if (produced.Length == 0)
                    return Fail("the operation produced no output.");

                EncryptionAlgorithm? algorithmOut;
                byte[] final;

                if (options.Policy == ReEncryptionPolicy.None)
                {
                    algorithmOut = null;
                    final = produced;
                }
                else
                {
                    algorithmOut = parameters.Algorithm;
                    using (var toEncrypt = new MemoryStream(produced, writable: false))
                    using (var encrypted = new MemoryStream())
                    {
                        try
                        {
                            if (kept != null)
                                PdfEncryptor.EncryptKeepingOriginal(
                                    toEncrypt, encrypted, kept.EncryptDictionary, kept.FileId,
                                    kept.Resolved, parameters.Algorithm, kept.KdfSalt);
                            else if (options.Policy == ReEncryptionPolicy.Recipients)
                                // The same four things the password handler takes from `parameters`
                                // — algorithm, permissions, PDF MAC, plaintext metadata — carried
                                // across; what differs is who can open the result, and only the
                                // caller knows that.
                                PdfPublicKeyEncryptor.Encrypt(
                                    toEncrypt, encrypted, options.RecipientCertificates,
                                    parameters.Permissions, parameters.Algorithm,
                                    parameters.EnablePdfMac, parameters.EncryptMetadata);
                            else
                                PdfEncryptor.Encrypt(toEncrypt, encrypted, parameters);
                        }
                        catch (PdfException ex)
                        {
                            return Fail("the produced document could not be re-encrypted: " + ex.Message);
                        }
                        if (PdfEncryptor.LastDroppedObjects is string notEncrypted)
                            warnings.Add("Objects of the produced document could not be read and are absent from the output: " + notEncrypted);
                        final = encrypted.ToArray();
                    }
                }

                // Only now is the caller's stream touched.
                output.Write(final, 0, final.Length);
                output.Flush();

                return new RoundTripResult(
                    succeeded: true,
                    policyApplied: options.Policy,
                    algorithmIn: algorithmIn,
                    algorithmOut: algorithmOut,
                    ownerPasswordChanged: options.Policy == ReEncryptionPolicy.ReuseOpenPassword,   // KeepOriginal keeps it
                    warnings: warnings,
                    failureReason: null);
            }
            finally
            {
                // A decrypted copy should not outlive the call in a pooled or long-lived buffer.
                if (plaintext != null) Array.Clear(plaintext, 0, plaintext.Length);
            }
        }

        /// <summary>
        /// Derives the encryption parameters the output will be written with, from the input's
        /// <see cref="EncryptionInfo"/> and the chosen policy.
        ///
        /// <para>Public because it answers a question a caller may legitimately ask before
        /// committing to a round trip: <i>what protection will my output actually have?</i></para>
        ///
        /// <para><b>Fails rather than downgrades.</b> An encryption version/revision combination
        /// this library cannot reproduce yields <c>false</c> and a reason — never a silent fall
        /// back to the <see cref="EncryptionAlgorithm.AES128"/> default, which would quietly weaken
        /// (or strengthen, equally wrongly) a document's protection without telling anyone.</para>
        /// </summary>
        public static bool TryDeriveParameters(
            EncryptionInfo info,
            EncryptedRoundTripOptions options,
            out EncryptionParameters parameters,
            out string failureReason)
        {
            parameters = null;
            failureReason = null;
            if (info == null) throw new ArgumentNullException(nameof(info));
            options = options ?? new EncryptedRoundTripOptions();

            if (options.Policy == ReEncryptionPolicy.Explicit)
            {
                if (options.ExplicitParameters == null)
                {
                    failureReason = "ReEncryptionPolicy.Explicit requires ExplicitParameters.";
                    return false;
                }
                // Refused here, before anything is read or run: the encryptor refuses this pair
                // with an ArgumentException, which would leave Run after the caller's operation had
                // run, against this class's "failures are reported, not thrown".
                if (options.ExplicitParameters.EnablePdfMac
                    && !PdfEncryptor.CanCarryPdfMac(options.ExplicitParameters.Algorithm))
                {
                    failureReason =
                        $"ExplicitParameters ask for PDF MAC with {options.ExplicitParameters.Algorithm}: " + TokenNeedsVersion5;
                    return false;
                }
                parameters = options.ExplicitParameters;
                return true;
            }

            EncryptionAlgorithm algorithm;
            if (!TryDetectAlgorithm(info, options.Policy, out algorithm, out failureReason))
                return false;

            if (options.Policy == ReEncryptionPolicy.None)
            {
                // Nothing will be written with these, but a caller inspecting the derivation
                // should still see what the input used.
                parameters = new EncryptionParameters { Algorithm = algorithm };
                return true;
            }

            if (options.Policy == ReEncryptionPolicy.KeepOriginal)
            {
                // Nothing is derived: the original dictionary is written as it is. These say what the
                // output will carry, for a caller asking before the trip.
                parameters = new EncryptionParameters
                {
                    Algorithm       = algorithm,
                    Permissions     = (PdfPermissions)(info.PValue & (int)PdfPermissions.All),
                    EncryptMetadata = info.EncryptMetadata,
                    EnablePdfMac    = UsesPdfMac(info),
                };
                return true;
            }

            if (options.Policy == ReEncryptionPolicy.Recipients)
            {
                // The public-key writer writes RC4-128, AES-128 and AES-256 (PDF MAC on AES-256 only);
                // anything else would throw NotSupportedException or ArgumentException out of Run after
                // the caller's operation had run, against this class's "failures are reported, not
                // thrown". Refused here, before anything is read or run.
                bool writable = algorithm == EncryptionAlgorithm.RC4_128
                    || algorithm == EncryptionAlgorithm.AES128
                    || algorithm == EncryptionAlgorithm.AES256;
                if (!writable || (UsesPdfMac(info) && algorithm != EncryptionAlgorithm.AES256))
                {
                    failureReason =
                        $"the document's encryption ({algorithm}{(UsesPdfMac(info) ? " with PDF MAC" : "")}) " +
                        "cannot be written for recipients: the public-key handler writes RC4-128, AES-128 or " +
                        "AES-256, and PDF MAC only with AES-256. Use ReEncryptionPolicy.Explicit to choose one.";
                    return false;
                }
                // No passwords: the public-key handler derives its file key from the recipients'
                // envelopes, and leaving these unset is what says so rather than an empty string
                // that reads as "no password".
                parameters = new EncryptionParameters
                {
                    Algorithm       = algorithm,
                    Permissions     = (PdfPermissions)(info.PValue & (int)PdfPermissions.All),
                    EncryptMetadata = info.EncryptMetadata,
                    EnablePdfMac    = UsesPdfMac(info),
                };
                return true;
            }

            // A token below /V 5 is one another producer wrote against ISO/TS 32004 Table 5. It is
            // not written again, and it is not dropped either: both would change the document's
            // protection without saying so.
            if (UsesPdfMac(info) && !PdfEncryptor.CanCarryPdfMac(algorithm))
            {
                failureReason =
                    $"the document's encryption ({algorithm} with PDF MAC) cannot be written again: " + TokenNeedsVersion5 +
                    " ReEncryptionPolicy.KeepOriginal keeps the document's own key and token.";
                return false;
            }

            // Run replaces these with the password that opened the input, which may be the credential's
            // or the empty user password an owner-only document opens with.
            string password = options.OpenPassword ?? "";
            parameters = new EncryptionParameters
            {
                UserPassword    = password,
                OwnerPassword   = password,
                Algorithm       = algorithm,
                Permissions     = (PdfPermissions)(info.PValue & (int)PdfPermissions.All),
                EncryptMetadata = info.EncryptMetadata,
                // ISO/TS 32004: a document protected by PDF MAC keeps that protection — dropping it would
                // be exactly the silent downgrade this method refuses.
                EnablePdfMac    = UsesPdfMac(info),
            };
            return true;
        }

        /// <summary>Why a PDF MAC token is not written below AES-256, and what to choose instead.</summary>
        private const string TokenNeedsVersion5 =
            "ISO/TS 32004 allows a PDF MAC token only where /V is at least 5 (AES-256 or AES-256-GCM). " +
            "Use ReEncryptionPolicy.Explicit with one of them.";

        /// <summary>What <see cref="ReEncryptionPolicy.KeepOriginal"/> writes the output under.</summary>
        private sealed class KeptEncryption
        {
            internal PdfDecryptor.ResolvedEncryption Resolved;
            internal PdfDictionary EncryptDictionary;
            internal byte[] FileId;
            internal byte[] KdfSalt;
        }

        /// <summary>
        /// Reads the original's file key (through the credential that opened it), a copy of its
        /// <c>/Encrypt</c> dictionary with every reference resolved, its first file identifier and
        /// its PDF MAC salt. Refused for an RC4 or AES-128 document with no <c>/ID</c>: its key was
        /// derived from an empty identifier, and a written file always carries one.
        /// </summary>
        private static bool TryReadKeptEncryption(byte[] original, PdfDecryptionCredential credential, EncryptionInfo info,
            out KeptEncryption kept, out string failureReason)
        {
            kept = null;
            failureReason = null;
            try
            {
                using (var source = new MemoryStream(original, writable: false))
                using (var doc = PdfDocument.Open(source))
                {
                    var resolved = credential.Resolve(doc);
                    var id = doc.Trailer.GetArray(PdfName.ID);
                    bool hasId = id != null && id.Count >= 1 && id[0] is PdfString first && first.RawBytes.Length > 0;
                    if (!hasId && resolved.Revision <= 4)
                    {
                        failureReason = "the document has no /ID, from which its RC4 or AES-128 key was derived, so its " +
                                        "encryption cannot be kept; use ReEncryptionPolicy.ReuseOpenPassword or Explicit.";
                        return false;
                    }
                    int copied = 0;
                    if (!(ResolvedCopy(doc, doc.Trailer[PdfName.Encrypt], 0, ref copied) is PdfDictionary encrypt))
                    {
                        failureReason = "the document's /Encrypt dictionary could not be read.";
                        return false;
                    }
                    kept = new KeptEncryption
                    {
                        Resolved = resolved,
                        EncryptDictionary = encrypt,
                        FileId = PdfEncryptor.GenerateOrExtractFileId(doc),
                        KdfSalt = PdfMacTokenSeal.SaltWhenProtected(info.EncryptDict, info.PValue, info.TrailerHasAuthCode),
                    };
                    return true;
                }
            }
            catch (PdfException ex)
            {
                failureReason = "the document's encryption could not be kept: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// A copy of <paramref name="value"/> with every reference resolved from <paramref name="doc"/>,
        /// bounded in depth and in the objects copied — a reference the file repeats is copied each
        /// time, so ten references to an array of ten, seven levels deep, would be 10⁶ objects inside
        /// the depth bound — and refused when a reference cannot be read, which is not an absent entry.
        /// </summary>
        private static PdfObject ResolvedCopy(PdfDocument doc, PdfObject value, int depth, ref int copied)
        {
            if (depth > MaxEncryptDictionaryDepth)
                throw new PdfException("the /Encrypt dictionary nests deeper than " + MaxEncryptDictionaryDepth + " levels.");
            if (++copied > MaxEncryptDictionaryObjects)
                throw new PdfException("the /Encrypt dictionary holds more than " + MaxEncryptDictionaryObjects + " objects.");
            if (value is PdfReference)
            {
                value = doc.ResolveObject(value);
                if ((value == null || value is PdfNull) && PdfParser.LastObjectParseError is string unreadable)
                    throw new PdfException("an object the /Encrypt dictionary names could not be read: " + unreadable);
            }
            switch (value)
            {
                case PdfDictionary d:
                    var copy = new PdfDictionary();
                    foreach (var kvp in d) copy.Set(kvp.Key, ResolvedCopy(doc, kvp.Value, depth + 1, ref copied));
                    return copy;
                case PdfArray a:
                    var items = new PdfArray();
                    foreach (var item in a) items.Add(ResolvedCopy(doc, item, depth + 1, ref copied));
                    return items;
                default:
                    return value;
            }
        }

        private const int MaxEncryptDictionaryDepth = 16;

        /// <summary>A handler's dictionary is a few dozen objects; a public-key one adds a string per recipient.</summary>
        private const int MaxEncryptDictionaryObjects = 65_536;

        /// <summary>
        /// Whether the input is protected by PDF MAC: <see cref="PdfMacTokenSeal.SaltWhenProtected(PdfDictionary, int, bool)"/>,
        /// the decision an appended revision is sealed on, over the facts <see cref="EncryptionInfo"/> carries.
        /// </summary>
        /// <remarks>
        /// This read the <c>/KDFSalt</c> and permission bit 13 only, so a document holding a token with bit 13 set was
        /// written back with no token at all. The output of a protected input always has bit 13 clear:
        /// <c>PdfEncryptor</c> clears it whenever it writes a token, so an input whose token was optional comes back
        /// requiring one in every revision.
        /// </remarks>
        private static bool UsesPdfMac(EncryptionInfo info)
            => PdfMacTokenSeal.SaltWhenProtected(info.EncryptDict, info.PValue, info.TrailerHasAuthCode) != null;

        /// <summary>
        /// Classifies the input's encryption from <c>/V</c>, <c>/R</c>, <c>/Length</c> and the
        /// <c>/StdCF</c> crypt-filter method (ISO 32000-1 §7.6.5 / ISO 32000-2 §7.6.4).
        /// </summary>
        private static bool TryDetectAlgorithm(
            EncryptionInfo info, ReEncryptionPolicy policy,
            out EncryptionAlgorithm algorithm, out string failureReason)
        {
            algorithm = EncryptionAlgorithm.AES128;
            failureReason = null;

            // The security handler comes first: /V and /CFM describe the CIPHER, not who holds the
            // key. A certificate-encrypted (/Adobe.PubSec) document carries /V 5 /CFM AESV3 exactly
            // as a password-encrypted one does, so classifying on those alone reported it as
            // /Standard AES-256 with an empty password — a silent scheme change from
            // certificate-based to password-based, which this method's own contract says it will
            // never make. PdfDecryptor.ResolveEncryption refuses the same input outright.
            // The hazard is a SILENT SCHEME CHANGE, and only one policy can still cause it.
            // ReuseOpenPassword would re-encrypt a certificate-protected document with a password —
            // and since there is no open password, with an empty one — turning a document only a
            // key holder could read into a document anyone can. The other policies each state the
            // output's handler: Recipients writes /Adobe.PubSec, Explicit is the caller's own
            // parameters, None emits plaintext and must be asked for by name.
            if (!string.IsNullOrEmpty(info.Filter)
                && !string.Equals(info.Filter, PdfName.Standard.Value, StringComparison.Ordinal)
                && policy == ReEncryptionPolicy.ReuseOpenPassword)
            {
                failureReason =
                    $"the document uses the /{info.Filter} security handler, which has no password " +
                    "to reuse — re-encrypting it with one would turn a document only a key holder " +
                    "can read into a document anyone can. Use ReEncryptionPolicy.Recipients with " +
                    "RecipientCertificates to keep it certificate-protected, Explicit to state a " +
                    "password scheme deliberately, or None to emit it decrypted.";
                return false;
            }

            string cfm = CryptFilterMethod(info);

            switch (info.V)
            {
                // A 40-bit key under revision 3 (/V 1 /R 3, or /V 2 /R 3 /Length 40 — ISO 32000-1's
                // own PDF) keeps its revision: the output was written as revision 2, a different key
                // derivation and /U for the same cipher (GAPS §19h item 7).
                case 1:
                    // /V 1 is 40 bits (ISO 32000-1 Table 20 allows /Length only for /V 2 and 3), but the
                    // decryptor reads a /Length it finds — so a nonconforming /V 1 /Length 128 file would
                    // be decrypted with a 128-bit key and re-encrypted at 40 bits, the result reporting no
                    // change. That is the silent downgrade this method refuses.
                    if (info.KeyLengthBits > 40)
                    {
                        failureReason =
                            $"the document declares /V 1 with a {info.KeyLengthBits}-bit key; /V 1 is 40-bit, " +
                            "and re-encrypting it at 40 bits would weaken the key it was read with.";
                        return false;
                    }
                    algorithm = info.R >= 3 ? EncryptionAlgorithm.RC4_40R3 : EncryptionAlgorithm.RC4_40;
                    return true;

                case 2:
                    algorithm = info.KeyLengthBits <= 40
                        ? (info.R >= 3 ? EncryptionAlgorithm.RC4_40R3 : EncryptionAlgorithm.RC4_40)
                        : EncryptionAlgorithm.RC4_128;
                    return true;

                case 4:
                    if (cfm == "AESV2") { algorithm = EncryptionAlgorithm.AES128; return true; }
                    if (cfm == "V2")    { algorithm = EncryptionAlgorithm.RC4_128; return true; }
                    if (cfm == "None" || cfm == "Identity")
                    {
                        algorithm = EncryptionAlgorithm.Identity;
                        return true;
                    }
                    failureReason =
                        $"the document uses /V 4 with an unsupported crypt filter method " +
                        $"(/CFM {(cfm ?? "absent")}), which this library cannot reproduce.";
                    return false;

                case 5:
                    if (cfm == null || cfm == "AESV3")
                    {
                        algorithm = EncryptionAlgorithm.AES256;
                        return true;
                    }
                    failureReason =
                        $"the document uses /V 5 with /CFM {cfm}, which this library cannot reproduce.";
                    return false;

                case 6:
                    if (cfm == "AESV4")
                    {
                        algorithm = EncryptionAlgorithm.AES256GCM;
                        return true;
                    }
                    failureReason =
                        $"the document uses /V 6 with /CFM {(cfm ?? "absent")}, which this library " +
                        "cannot reproduce.";
                    return false;

                default:
                    failureReason =
                        $"the document uses encryption /V {info.V} /R {info.R}, which this library " +
                        "cannot reproduce. Re-encrypting it under a different scheme would change " +
                        "the document's protection without saying so, which this method will not do.";
                    return false;
            }
        }

        /// <remarks>
        /// All three reads resolve. Any one of them being indirect returned null, which falls
        /// through every branch of <c>TryDetectAlgorithm</c>'s <c>/V 4</c> case to the failure
        /// "the document uses /V 4 with an unsupported crypt filter method (/CFM absent), which
        /// this library cannot reproduce" — <b>before decryption is even attempted</b>. A
        /// perfectly reproducible AES-128 or RC4-128 document is refused for sanitize, optimize
        /// and redact, and the stated reason is untrue of the file.
        /// </remarks>
        private static string CryptFilterMethod(EncryptionInfo info)
        {
            // Prefer the value the decryptor resolved while the document was open. The raw walk
            // below stays for an EncryptionInfo a caller built itself, and is deliberately
            // unchanged: by this point there is no document left to resolve against.
            if (info.StdCryptFilterMethod != null) return info.StdCryptFilterMethod;

            var cf = info.EncryptDict?[PdfName.CF] as PdfDictionary;
            if (cf == null) return null;

            var stdCf = cf[PdfName.StdCF] as PdfDictionary;
            if (stdCf == null) return null;

            return (stdCf[PdfName.CFM] as PdfName)?.Value;
        }

        private static RoundTripResult RunUnencrypted(
            byte[] original, Stream output, Action<Stream, Stream> operation)
        {
            byte[] produced;
            using (var plainIn = new MemoryStream(original, writable: false))
            using (var operationOut = new MemoryStream())
            {
                operation(plainIn, operationOut);
                produced = operationOut.ToArray();
            }

            if (produced.Length == 0)
                return Fail("the operation produced no output.");

            output.Write(produced, 0, produced.Length);
            output.Flush();

            return new RoundTripResult(
                succeeded: true,
                policyApplied: ReEncryptionPolicy.None,
                algorithmIn: null,
                algorithmOut: null,
                ownerPasswordChanged: false,
                warnings: Array.Empty<string>(),
                failureReason: null);
        }

        private static bool IsEncrypted(byte[] bytes)
        {
            try
            {
                using (var ms = new MemoryStream(bytes, writable: false))
                using (var doc = PdfDocument.Open(ms))
                    return doc.IsEncrypted;
            }
            catch (PdfException)
            {
                // Unparseable here means unparseable later too; let the operation report it.
                return false;
            }
        }

        /// <summary>
        /// True when re-encrypting this document would destroy a signature — a signature field
        /// carrying a value, or a certification reached through <c>/Perms /DocMDP</c>.
        /// </summary>
        /// <remarks>
        /// <b>This asks <see cref="Tetitulz.Pdf.Core.Document.PdfSignatureDetection"/>
        /// and nothing else, because a rewriter that answers the question differently from the
        /// other rewriters is the defect that predicate exists to prevent.</b> Testing
        /// <c>/AcroForm /SigFlags</c> bit 1 would refuse a document that merely has an <i>empty</i>
        /// signature field — a form prepared for signing. §12.7.2 makes bit 1 mean a signature field
        /// exists, not that anything is signed, and the shared walk says as much where it requires a
        /// <c>/V</c>: an unsigned field is a placeholder and rewriting it invalidates nothing. The
        /// optimizer, the sanitizer, the linearizer and both conformance converters proceed on such a
        /// document, and so does this.
        /// </remarks>
        private static bool HasSignature(byte[] plaintext)
        {
            try
            {
                using (var ms = new MemoryStream(plaintext, writable: false))
                using (var doc = PdfDocument.Open(ms))
                {
                    // The shared detection, rather than a hand-rolled field walk.
                    //
                    // An earlier private walk iterated /AcroForm /Fields ONCE and never followed
                    // /Kids, so a signature field nested under a parent — the ordinary shape for a
                    // form of any size — was not seen; and it never read /Perms /DocMDP, so a
                    // certified document with no signature *field* was not seen either. Both cases
                    // round-tripped silently: decrypt, rewrite every byte, re-encrypt, signature
                    // destroyed, no refusal and no warning, even though the caller had not set
                    // AllowSignatureInvalidation. The shared walk resolves every read on the way,
                    // which matters because a producer may write /AcroForm, /Fields or a field's
                    // /V indirectly and a non-resolving read would answer "no signature" — the one
                    // wrong answer this guard must not give.
                    return Tetitulz.Pdf.Core.Document.PdfSignatureDetection.HasSignature(
                        doc.Trailer, n => doc.ResolveObject(new PdfReference(n, 0)));
                }
            }
            catch (PdfException)
            {
                // Cannot establish it either way. Say "no signature" rather than refuse a document
                // that may well not have one — the operation will fail on its own if it is broken.
                return false;
            }
        }

        /// <summary>
        /// Reads the whole input document.
        ///
        /// <para><b>A seekable input is rewound first.</b> Read from its current position, a
        /// seekable non-<see cref="MemoryStream"/> already past zero (a <c>FileStream</c> whose owner
        /// had sniffed the header, or a reused handle) would yield the headerless tail — zero bytes
        /// when the caller had read to the end; <see cref="IsEncrypted"/> would swallow the parse
        /// failure and answer <c>false</c>, <c>RunUnencrypted</c> would run the operation over an
        /// empty document, and the call would end at <c>Fail("the operation produced no output.")</c>:
        /// a wrong reason for a wrong answer. <see cref="PdfEncryptedIncrementalUpdate"/> reads the
        /// same way, so the two entry points agree on the same stream.</para>
        ///
        /// <para><b>Only a caller's stream reaches the rewind.</b> Nothing in the repository calls
        /// either overload with a pre-positioned stream — <c>PdfEncryptionOperations</c> opens a
        /// fresh <c>FileStream</c> at zero — and a <see cref="MemoryStream"/> takes the fast path,
        /// which answers correctly regardless of position; both <c>Run</c> overloads are public and
        /// take caller-owned streams.</para>
        /// </summary>
        private static byte[] ReadAll(Stream input) => StreamBytes.ReadAll(input);

        private static RoundTripResult Fail(string reason)
        {
            string message = "The encrypted round trip did not run: " + reason;
            _lastError = message;
            return new RoundTripResult(
                succeeded: false,
                policyApplied: ReEncryptionPolicy.None,
                algorithmIn: null,
                algorithmOut: null,
                ownerPasswordChanged: false,
                warnings: Array.Empty<string>(),
                failureReason: message);
        }
    }
}
