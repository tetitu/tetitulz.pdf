// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Timestamping;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// The single signature-verification engine: integrity, structure (ByteRange / shadow
    /// updates), DocMDP, X.509 chain trust and embedded-DSS revocation, evaluated for every
    /// signature field in the document.
    /// <para>
    /// <see cref="PdfSignatureValidator"/> is a thin adapter over this class, so a signature this
    /// class rejects outright cannot be reported valid there.
    /// </para>
    /// </summary>
    public sealed class PdfSignatureVerifier
    {
        // Per-thread so a value read after one call cannot belong to another thread's call
        // (see the diagnostics note in CLAUDE.md).
        [ThreadStatic] private static string? _lastVerificationError;

        /// <summary>
        /// Why an encrypted document yields no signatures — one wording, reached from both
        /// <see cref="VerifyAll(PdfDocument, SignatureVerificationOptions)"/> and the by-field-name
        /// <c>Verify</c>.
        /// </summary>
        /// <remarks>
        /// <para>The two entry points say the same thing about the same file: "Document has no
        /// AcroForm." from <c>Verify</c> would be true for an unencrypted file and false for this
        /// one.</para>
        ///
        /// <para>The <c>cref</c> above carries its full signature deliberately. <c>VerifyAll</c> has
        /// two overloads, so the bare name is CS0419-ambiguous and the compiler resolved it to the
        /// one-argument convenience overload — while the message this constant carries is produced
        /// by the two-argument engine. A doc link that silently points at the wrong member is the
        /// same failure as a <c>param</c> tag naming a parameter that does not exist.</para>
        /// </remarks>
        private const string EncryptedAcroFormUnreadable =
            EncryptedDocumentMessages.AcroFormUnreadable +
            " No signature could be found, which is not evidence that the document is unsigned.";

        /// <summary>
        /// Reason the most recent verification on this thread failed structurally, or <c>null</c>.
        /// Populated even when the failure is reported through a result object, so hosts can log
        /// a single cause without walking every result.
        /// </summary>
        public static string? LastVerificationError => _lastVerificationError;

        /// <summary>Verifies all signatures in a PDF document using default policy.</summary>
        public IReadOnlyList<SignatureVerificationResult> VerifyAll(PdfDocument document)
            => VerifyAll(document, null);

        /// <summary>
        /// Whether <paramref name="r"/> may be believed when it says the file is covered as far as
        /// its <c>/ByteRange</c> claims — i.e. whether it is allowed to suppress the shadow-attack
        /// error on <i>other</i> signatures.
        /// </summary>
        /// <remarks>
        /// <para><b>This deliberately asks only whether the CMS verifies, and an attempt to
        /// strengthen it to a trust test was reverted.</b> Requiring
        /// <see cref="PdfSignatureChainStatus.Trusted"/> here is the obvious-looking fix for the
        /// self-signed-decoy laundering described on <c>VerifyAll</c>, and it silently refuses
        /// conforming documents: in a two-party contract each signer is under their own CA and the
        /// verifier anchors only its own, and in <b>every PAdES B-LT/B-LTA file</b> the object
        /// reaching EOF is a <c>/DocTimeStamp</c> whose TSA chains to a commercial TSA root rather
        /// than to the signer's CA — which <c>SignatureVerificationOptions.TimestampTrustAnchors</c>
        /// itself documents as the normal configuration ("most callers only set one list"). The
        /// EOF-covering object is then excluded, the furthest *trusted* coverage stops at the first
        /// signature, and every signature in the file is told the document has unsigned trailing
        /// bytes. The asymmetry made it worse than it sounds: a caller supplying no anchors was
        /// unaffected, so only the caller following the documented advice got the false refusal.
        /// See <c>SignatureShadowAttackTests.CounterSignatureUnderAnotherCa_…</c>.</para>
        /// <para><b>Trust is the wrong axis.</b> What separates the attack from the legitimate case
        /// is not who signed the later revision but <i>what that revision changed</i>: a
        /// counter-signature or archival timestamp adds signature objects, while a shadow attack
        /// rewrites page content. <c>VerifyAll</c> therefore runs the post-signature change analysis
        /// (<c>DocMdpAnalyzer.AnalyzeChangesSince</c>) for every <b>approval</b> signature, at
        /// <see cref="ApprovalSignatureChangeLevel"/>, which is what closed the laundering: the
        /// genuine signature reports the rewritten page itself, whoever signed after it. This
        /// predicate still decides only the document-level trailing-bytes error.</para>
        /// </remarks>
        private static bool MayVouchForCoverage(SignatureVerificationResult r)
        {
            return r != null && r.IsIntegrityValid;
        }

        /// <summary>
        /// The DocMDP level (ISO 32000-2 Table 257) the revisions appended after an approval signature
        /// are judged at: form filling, page templates, signing and annotations may follow a
        /// signature; page content and document structure may not. See <c>VerifyAll</c>.
        /// </summary>
        private const int ApprovalSignatureChangeLevel = 3;

        /// <summary>
        /// Verifies all signatures in a PDF document.
        /// </summary>
        /// <param name="document">The document to verify.</param>
        /// <param name="options">
        /// Verification policy — trust anchors, weak-algorithm handling, revocation freshness.
        /// When null, defaults apply and no chain can be anchored (chain status stays
        /// <see cref="PdfSignatureChainStatus.UntrustedRoot"/> or <c>SelfSigned</c>) — and the
        /// legacy process-wide <see cref="CmsSignatureVerifier.RejectWeakAlgorithms"/> static
        /// governs weak-algorithm handling, as its own documentation promises. Passing any
        /// instance, even a default-constructed one, overrides that static outright.
        /// </param>
        public IReadOnlyList<SignatureVerificationResult> VerifyAll(
            PdfDocument document, SignatureVerificationOptions? options)
            => VerifyAll(document, document, null, options);

        /// <summary>
        /// <see cref="VerifyAll(PdfDocument, SignatureVerificationOptions)"/> with the <b>object
        /// graph</b> and the <b>signed bytes</b> read from different views of one file — the shape an
        /// encrypted document needs.
        /// </summary>
        /// <param name="objectGraph">
        /// Where the form, its signature fields and their dictionaries are read from. For an encrypted
        /// document this is a decrypted copy, whose own bytes are never digested.
        /// </param>
        /// <param name="original">
        /// The file as stored — whose <see cref="PdfDocument.BaseStream"/> the <c>/ByteRange</c>s
        /// index and whose cross-reference table the post-signature change analysis reads. Pass the
        /// same document as <paramref name="objectGraph"/> for a plaintext file.
        /// </param>
        /// <param name="openSignedRevision">
        /// Opens a signed revision's byte prefix as a readable document — a decryptor for an encrypted
        /// original; <c>null</c> opens it directly.
        /// </param>
        /// <param name="options">As for <see cref="VerifyAll(PdfDocument, SignatureVerificationOptions)"/>.</param>
        /// <remarks>
        /// <para><b>Three facts make the split sound</b>, the same three <c>DocMdpAnalyzer</c> rests
        /// on: <c>PdfDocument.Load</c> parses the cross-reference table before it stops at
        /// <c>/Encrypt</c>, so the original's offsets need no decryption; <c>/ByteRange</c> is numbers,
        /// which are never encrypted, so it reads the same from either view and always indexes the
        /// original; and ISO 32000-2 §7.6.2 leaves a signature's <c>/Contents</c> unencrypted, so the
        /// CMS reads the same from either view too — provided the decryptor honours the exemption,
        /// which <c>Tetitulz.Pdf.Encryption</c>'s does.</para>
        /// <para><b>The single-document overload passes the same document for both</b>, so there is
        /// one analysis and the plaintext path cannot drift from this one. Every byte this method
        /// digests, every file length it compares and every revision boundary it classifies comes from
        /// <paramref name="original"/>; a decrypted copy is a fresh serialisation whose offsets
        /// describe nothing in the file, and reading them would report a modified document intact.</para>
        /// </remarks>
        public IReadOnlyList<SignatureVerificationResult> VerifyAll(
            PdfDocument objectGraph, PdfDocument original, Func<byte[], PdfDocument>? openSignedRevision,
            SignatureVerificationOptions? options)
        {
            if (objectGraph == null) throw new ArgumentNullException(nameof(objectGraph));
            if (original == null) throw new ArgumentNullException(nameof(original));
            _lastVerificationError = null;

            PdfDocument document = objectGraph;
            var results = new List<SignatureVerificationResult>();

            if (document.AcroForm == null)
            {
                // An empty list is the honest answer for an unsigned document and a WRONG one for
                // an encrypted signed document: PdfDocument.Load reads neither catalog nor AcroForm
                // for an encrypted file, so a signed contract under a password verified to "no
                // signatures" — indistinguishable from "this document is not signed", which is a
                // wrong answer to a security question rather than a missing feature.
                //
                // The list stays empty (this verifier holds no decryptor and must not guess), but
                // the reason is now on the diagnostic slot the class already documents.
                if (document.IsEncrypted)
                    _lastVerificationError = EncryptedAcroFormUnreadable;
                return results;
            }

            // The caller's ORIGINAL reference goes in, not a resolved one: calling
            // SignatureVerificationOptions.OrDefault here would erase the difference between "no
            // policy stated" and "the default policy stated" for everything downstream. See
            // VerificationContext.CallerOptions.
            var context = new VerificationContext(document, original, openSignedRevision, options);

            var sigFields = document.AcroForm.GetSignatureFields();
            // A field the walk could not read is not a signature it can list: said, not read as "unsigned".
            if (document.AcroForm.UnreadableNodes.Count > 0)
                _lastVerificationError ??= "The form's field tree holds " + document.AcroForm.UnreadableNodes.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " entr" + (document.AcroForm.UnreadableNodes.Count == 1 ? "y" : "ies") + " that could not be read, so a signature among them is not listed: "
                    + string.Join("; ", System.Linq.Enumerable.Take(document.AcroForm.UnreadableNodes, 20)) + ".";
            var signatures = new List<(SignatureVerificationResult Result, PdfDictionary? Signature)>();
            foreach (var field in sigFields)
            {
                if (field.HasSignature)
                {
                    var result = VerifyField(field, context);
                    results.Add(result);
                    signatures.Add((result, field.SignatureValue));
                }
            }

            // Shadow / incremental-append detection: every byte of the file must be covered by
            // SOME signature's ByteRange. If the FURTHEST-reaching signature still stops short of
            // EOF, the trailing bytes are unsigned attacker content (a classic PDF "shadow attack"
            // that changes what the document renders while each CMS still verifies its own range).
            // Flag EVERY signature in that case — not just the furthest — because otherwise an
            // attacker adds a decoy signature reaching deeper than a genuine one, the flag lands on
            // the decoy, and the genuine (earlier-covering) signature still reports IsValid while
            // attacker-appended bytes go unsigned. When the furthest signature DOES reach EOF
            // (the normal multi-signature case) nothing is flagged.
            //
            // Only signatures whose CMS actually VERIFIES (IsIntegrityValid) may vouch for their
            // coverage: ByteRangeCoverageEnd is set from the declared /ByteRange *before* the CMS
            // is checked, so an attacker can otherwise add a decoy whose ByteRange reaches EOF (and
            // whose garbage CMS never verifies) to push `maxEnd` to fileLength and suppress the
            // warning on a genuine signature that only covers an earlier revision.
            //
            // IsIntegrityValid ALONE is not enough, and that was a live signature-spoofing hole.
            // It means "this CMS verifies against the certificate embedded in it", which a
            // SELF-SIGNED certificate satisfies for free: the attacker generates a key pair, signs
            // the tampered file to EOF, and their decoy — untrusted, unanchored, and never itself
            // believed — pushes maxEnd to fileLength and suppresses the warning on the genuine
            // signature. Neither IsValid nor IsFullyTrusted consults CoversEntireDocument, so with
            // the warning suppressed there is nothing left to fail, and a document whose visible
            // content was rewritten after signing reports IsFullyTrusted == true.
            // See SignatureShadowAttackTests.
            // Every byte question below is asked of context.Bytes — the ORIGINAL file — never of the
            // object graph's stream, which for an encrypted document is a decrypted copy whose
            // offsets describe nothing the /ByteRange indexes.
            if (results.Count > 0 && context.Bytes != null)
            {
                long fileLength = context.Bytes.Length;

                long maxEnd = 0;
                foreach (var r in results)
                    if (MayVouchForCoverage(r) && r.ByteRangeCoverageEnd > maxEnd)
                        maxEnd = r.ByteRangeCoverageEnd;
                // "Reaches the end" rather than "equals the length": §12.8.1 lets a range stop at
                // %%EOF and leave one end-of-line marker outside it, and that marker is not a
                // revision anyone appended.
                if (maxEnd > 0 && !SignedByteRanges.ReachesEndOfFile(context.Bytes, maxEnd, fileLength))
                {
                    long trailing = fileLength - maxEnd;
                    // What the unsigned tail holds decides whether it is an error. A revision that
                    // adds nothing but evidence about the signatures — the /DSS a B-LT signing or an
                    // LTV refresh appends after the signature, a blockchain anchoring's proof — is
                    // what a signed document is meant to take after its last signature, and nothing
                    // in it is read as the document's content; it is said, as a warning, never as the
                    // shadow-attack error, which would make the library's own B-LT output invalid.
                    var tail = DocMdpAnalyzer.AnalyzeChangesSince(
                        document, context.OffsetSource, maxEnd, fileLength,
                        ApprovalSignatureChangeLevel, context.OpenSignedRevision);
                    if (HoldsOnlyEvidence(tail))
                    {
                        foreach (var r in results)
                            r.Warnings.Add(
                                $"{trailing} byte(s) appended after the last signature's ByteRange hold only " +
                                "revocation evidence or an anchoring proof (" + string.Join("; ", tail.PermittedChanges) +
                                "); no signature covers them.");
                    }
                    else
                    {
                        foreach (var r in results)
                            r.ValidationErrors.Add(
                                $"{trailing} byte(s) appended after the last signature's ByteRange are unsigned " +
                                "(possible shadow/incremental-update attack); no signature covers the full document.");
                    }
                }

                // Said per signature, about that signature's own /ByteRange, and therefore
                // impossible for any other signature to suppress. It is a Warning rather than a
                // ValidationError on purpose: in a legitimate multi-signature document every
                // signature but the last one legitimately stops short of EOF, so making this an
                // error would report every counter-signed contract as invalid. What it buys is that
                // "the bytes I am looking at are not the bytes this signature covers" is never
                // simply absent from the result — which is the state the laundering above created,
                // and the state a caller with no trust anchors is still in.
                foreach (var r in results)
                {
                    if (r.ByteRangeCoverageEnd > 0
                        && !SignedByteRanges.ReachesEndOfFile(context.Bytes, r.ByteRangeCoverageEnd, fileLength))
                        r.Warnings.Add(
                            $"This signature covers the first {r.ByteRangeCoverageEnd:N0} of " +
                            $"{fileLength:N0} bytes; the remaining " +
                            $"{fileLength - r.ByteRangeCoverageEnd:N0} byte(s) were appended after it " +
                            "and are outside its digest. That is normal for an earlier signature in " +
                            "a counter-signed document and is how a shadow attack looks — check " +
                            "CoversEntireDocument and what the later revisions changed.");
                }

                // DocMDP enforcement: if this document carries a certification signature, surface
                // its permission level on the certifying signature's result, and if a
                // post-certification incremental update exceeds that level, record it as a
                // validation error so IsValid reflects "the certified document is intact".
                var mdp = DocMdpAnalyzer.Analyze(document, context.OffsetSource, fileLength, context.OpenSignedRevision);
                if (mdp.HasCertification)
                {
                    // Identify the certifying signature by its signature DICTIONARY — the object the
                    // analyzer found, whether through /Perms or through a field.
                    //
                    // It was matched by field name, then by signed-region end, and before that by
                    // region end alone with a fall back to `results[0]`. A name is not an identity —
                    // two fields may share a partial name — and the /Perms route yields none at all.
                    // And ByteRangeCoverageEnd is left 0 by every early return in VerifyField
                    // (unsupported /SubFilter, missing or non-numeric /ByteRange, missing /Contents,
                    // null BaseStream), which is exactly when a certification most needs finding: the
                    // permission level and the MDP violations were then recorded against no signature,
                    // or against whichever came first.
                    SignatureVerificationResult? certResult = null;
                    foreach (var (r, signature) in signatures)
                        if (signature != null && ReferenceEquals(signature, mdp.CertSignature))
                        { certResult = r; break; }

                    if (certResult != null)
                    {
                        certResult.IsCertification = true;
                        certResult.CertificationPermission = mdp.PermissionLevel;
                        WarnOfPermittedChanges(certResult, mdp);
                        if (!mdp.IsCompliant)
                            foreach (var v in mdp.Violations)
                            {
                                certResult.MdpViolations.Add(v);
                                certResult.ValidationErrors.Add("DocMDP: " + v);
                            }
                    }
                    else if (!mdp.IsCompliant)
                    {
                        // The certifying signature could not be identified, but the document IS
                        // certified and IS in violation. Record it against every signature rather
                        // than picking one arbitrarily or — worse — dropping the violation: the
                        // document is untrustworthy no matter which signature the caller asked
                        // about. Deliberately does NOT set IsCertification on anything, since we
                        // do not know which field it was.
                        foreach (var r in results)
                            foreach (var v in mdp.Violations)
                            {
                                r.MdpViolations.Add(v);
                                r.ValidationErrors.Add("DocMDP: " + v);
                            }
                    }
                }
            }

            // What the revisions after each APPROVAL signature changed, judged at Table 257's level 3
            // — form filling, annotating and further signing are what a signed document is meant to
            // take; a page's content and the catalog's structure are not. Only a signature whose CMS
            // verified could vouch that the file was covered to EOF, and a self-signed certificate
            // verifies against itself, so an attacker who rewrote a page in an appended revision and
            // signed that revision suppressed the genuine signature's trailing-bytes error for every
            // signature in the file. Trust was the wrong axis (see MayVouchForCoverage); what the
            // later revision CHANGED is the right one, and it is asked of each signature about its
            // own range, so no other signature can answer for it. The certification is judged by its
            // own /P above and is passed over here.
            if (context.Bytes != null)
            {
                long fileLength = context.Bytes.Length;
                foreach (var (result, signature) in signatures)
                {
                    if (signature == null || result.IsCertification || result.ByteRangeCoverageEnd <= 0) continue;

                    var changes = DocMdpAnalyzer.AnalyzeChangesSince(
                        document, context.OffsetSource, result.ByteRangeCoverageEnd, fileLength,
                        ApprovalSignatureChangeLevel, context.OpenSignedRevision);
                    WarnOfPermittedChanges(result, changes);
                    if (changes.IsCompliant) continue;

                    foreach (var v in changes.Violations)
                    {
                        result.MdpViolations.Add(v);
                        result.ValidationErrors.Add("Changed after signing: " + v);
                    }
                }
            }

            // §12.8.2.4 — a FieldMDP signature "shall be validated in a similar manner to DocMDP
            // signatures": every field it locked must still hold the value it signed. Reported on
            // the locking signature, matched by its dictionary as the certification is above.
            if (context.Bytes != null)
            {
                foreach (var fieldLock in FieldMdpAnalyzer.Analyze(
                             document, context.OffsetSource, context.Bytes.Length, context.OpenSignedRevision))
                {
                    if (fieldLock.IsCompliant) continue;
                    foreach (var (result, signature) in signatures)
                    {
                        if (signature == null || !ReferenceEquals(signature, fieldLock.Signature)) continue;
                        foreach (var v in fieldLock.Violations)
                            result.ValidationErrors.Add("FieldMDP: " + v);
                        break;
                    }
                }
            }

            ReportSignatureOrderViolations(document, signatures);

            return results;
        }

        /// <summary>How many permitted changes a warning names; a file can make as many as it has objects.</summary>
        private const int MaxPermittedChangesNamed = 20;

        /// <summary>
        /// Whether the revisions after the last signature's range add nothing but evidence about the
        /// signatures: compliant at the approval level, with every permitted change a <c>/DSS</c>
        /// (LTV) addition or the catalog's <c>/BlockchainProof</c>, and no change at all is not
        /// evidence either — bytes a reader never reads are still bytes nothing signed.
        /// </summary>
        private static bool HoldsOnlyEvidence(DocMdpAnalysis tail)
        {
            if (tail == null || !tail.IsCompliant || tail.PermittedChanges.Count == 0) return false;
            foreach (string change in tail.PermittedChanges)
            {
                bool evidence = change.IndexOf("/DSS", StringComparison.Ordinal) >= 0
                                || change.IndexOf("/BlockchainProof", StringComparison.Ordinal) >= 0;
                if (!evidence) return false;
            }
            return true;
        }

        /// <summary>
        /// Says what a later revision changed that its level allows. A permitted change is no error, and a
        /// signature stays valid over it, but an annotation or a new signature's appearance can stand over
        /// the signed text and read as part of it (the published evil-annotation and sneaky-signature
        /// attacks): the warning is what lets a relying party ask about it.
        /// </summary>
        private static void WarnOfPermittedChanges(SignatureVerificationResult result, DocMdpAnalysis changes)
        {
            if (changes.PermittedChanges.Count == 0) return;

            int named = Math.Min(changes.PermittedChanges.Count, MaxPermittedChangesNamed);
            string list = string.Join("; ", changes.PermittedChanges.GetRange(0, named));
            string more = changes.PermittedChanges.Count > named ? $"; and {changes.PermittedChanges.Count - named} more" : "";
            result.Warnings.Add(
                "Later revisions made changes this signature's level permits, which can redraw the page though " +
                "the signature stays valid: " + list + more + ".");
        }

        /// <summary>
        /// Records a validation error on a certification signature that ISO 32000-2 §12.8.1 does not
        /// allow where it stands: a second certification, or a certification added after an approval
        /// signature.
        /// </summary>
        /// <remarks>
        /// <para><b>§12.8.1:</b> a document may contain "at most one certification signature", and
        /// approval signatures "shall follow the certification signature if one is present". Document
        /// timestamps are "any number", anywhere, so they are passed over.</para>
        ///
        /// <para><b>Order is the order of the revisions that added them</b>, which is the order of where
        /// each signature's coverage ends: a signature's range runs to the end of the revision that
        /// added it. A signature whose range could not be read has no place in that order and is
        /// passed over — it already carries its own error.</para>
        ///
        /// <para><b>The error lands on the certification, not on the approvals.</b> The approval
        /// signatures signed what they were shown; it is the certification that claims authority over a
        /// document someone else had already signed, which is the claim a relying party must not
        /// believe. <c>PdfSigner</c> refuses to write either shape.</para>
        /// </remarks>
        private static void ReportSignatureOrderViolations(
            PdfDocument document, List<(SignatureVerificationResult Result, PdfDictionary? Signature)> signatures)
        {
            Func<PdfObject, PdfObject> resolve = document.ResolveObject;
            var seen = new HashSet<PdfDictionary>(PdfReferenceEquality.Instance);
            bool sawApproval = false, sawCertification = false;

            signatures.Sort((a, b) => a.Result.ByteRangeCoverageEnd.CompareTo(b.Result.ByteRangeCoverageEnd));
            foreach (var (result, signature) in signatures)
            {
                if (signature == null || result.ByteRangeCoverageEnd <= 0 || !seen.Add(signature)) continue;
                if (DocMdpAnalyzer.IsDocumentTimestamp(signature, resolve)) continue;

                if (DocMdpAnalyzer.DocMdpPermissionOf(signature, resolve, out _) == 0)
                {
                    sawApproval = true;
                    continue;
                }

                if (sawCertification)
                    Fail(result,
                        "This is a second certification signature; ISO 32000-2 §12.8.1 allows at most one, " +
                        "so it cannot be taken to certify the document.");
                else if (sawApproval)
                    Fail(result,
                        "This certification signature was added after an approval signature; ISO 32000-2 " +
                        "§12.8.1 requires approval signatures to follow the certification signature, so it " +
                        "cannot be taken to certify a document someone had already signed.");
                sawCertification = true;
            }
        }

        /// <summary>Verifies a specific signature by field name using default policy.</summary>
        public SignatureVerificationResult Verify(PdfDocument document, string signatureFieldName)
            => Verify(document, signatureFieldName, null);

        /// <summary>
        /// Verifies a specific signature by field name.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This runs the <b>same</b> analysis as <see cref="VerifyAll(PdfDocument, SignatureVerificationOptions)"/>
        /// and then returns the requested field's result. It must: the shadow-append and DocMDP
        /// checks are document-level, and both need every signature's coverage to reach a correct
        /// verdict.
        /// </para>
        /// <para>
        /// Calling <c>VerifyField</c> directly would skip both: a shadow-attacked document —
        /// legitimately signed, then extended with an incremental update carrying a replacement
        /// page tree — would verify with <c>IsValid</c> and <c>IsFullyTrusted</c> both <c>true</c>
        /// here while <c>VerifyAll</c> reports it invalid for the identical bytes. Two public APIs
        /// disagreeing about whether a file is trustworthy is the worst possible failure mode for a
        /// verification library, so the by-name path is a projection of the full analysis rather
        /// than a parallel one.
        /// </para>
        /// </remarks>
        public SignatureVerificationResult Verify(
            PdfDocument document, string signatureFieldName, SignatureVerificationOptions? options)
            => Verify(document, document, null, signatureFieldName, options);

        /// <summary>
        /// <see cref="Verify(PdfDocument, string, SignatureVerificationOptions)"/> with the object graph
        /// and the signed bytes read from different views of one file — see
        /// <see cref="VerifyAll(PdfDocument, PdfDocument, Func{byte[], PdfDocument}, SignatureVerificationOptions)"/>.
        /// </summary>
        /// <param name="objectGraph">Where the form and its signature fields are read from.</param>
        /// <param name="original">The file as stored, whose bytes the <c>/ByteRange</c>s index.</param>
        /// <param name="openSignedRevision">Opens a signed revision's byte prefix; <c>null</c> opens it directly.</param>
        /// <param name="signatureFieldName">The field to report on.</param>
        /// <param name="options">The verification policy, or <c>null</c> for the defaults.</param>
        public SignatureVerificationResult Verify(
            PdfDocument objectGraph, PdfDocument original, Func<byte[], PdfDocument>? openSignedRevision,
            string signatureFieldName, SignatureVerificationOptions? options)
        {
            if (objectGraph == null) throw new ArgumentNullException(nameof(objectGraph));
            if (original == null) throw new ArgumentNullException(nameof(original));
            _lastVerificationError = null;

            PdfDocument document = objectGraph;

            if (document.AcroForm == null)
            {
                // "Document has no AcroForm." is TRUE for an unencrypted file and FALSE for an
                // encrypted one: PdfDocument.Load reads neither catalog nor AcroForm for an
                // encrypted file, so a signed document under a password produced an exception
                // asserting it has no form at all. VerifyAll already explains this case; this entry
                // point stated the opposite. The message is a shared constant so the two cannot
                // drift into telling a caller two different things about the same file.
                if (document.IsEncrypted)
                {
                    _lastVerificationError = EncryptedAcroFormUnreadable;
                    throw new PdfVerificationException(EncryptedAcroFormUnreadable);
                }
                throw new PdfVerificationException("Document has no AcroForm.");
            }

            var field = document.AcroForm.FindSignatureField(signatureFieldName, out int partialNameMatches);
            if (field == null)
                throw new PdfVerificationException(
                    SigningHelpers.SignatureFieldNotFound(signatureFieldName, partialNameMatches));

            if (!field.HasSignature)
                throw new PdfVerificationException($"Signature field '{signatureFieldName}' has no signature value.");

            // Verify every signature so document-level coverage is computed over all of them,
            // then project out the one that was asked for — by the name the results carry, which is
            // the resolved field's fully qualified one whatever form the caller used.
            var all = VerifyAll(document, original, openSignedRevision, options);
            foreach (var r in all)
                if (string.Equals(r.FieldName, field.ReportedName, StringComparison.Ordinal))
                    return r;

            // VerifyAll enumerates the same AcroForm signature fields that FindSignatureField
            // resolved, so this is unreachable in practice; fail closed rather than silently
            // falling back to an unchecked single-field verification.
            throw new PdfVerificationException(
                $"Signature field '{signatureFieldName}' was resolved but produced no verification result.");
        }

        /// <summary>
        /// Per-call state shared across the signatures of one document: the policy, the document
        /// bytes, and the lazily-resolved document timestamp (which would otherwise be
        /// re-verified once per signature).
        /// </summary>
        private sealed class VerificationContext
        {
            /// <summary>The object graph: the form, its fields, their signature dictionaries, the <c>/DSS</c>.</summary>
            public readonly PdfDocument Document;

            /// <summary>
            /// The file as stored — whose cross-reference offsets the post-signature change analyses
            /// read. The same document as <see cref="Document"/> for a plaintext file; the encrypted
            /// original when the graph is a decrypted copy.
            /// </summary>
            public readonly PdfDocument OffsetSource;

            /// <summary>
            /// The bytes every <c>/ByteRange</c> indexes: <see cref="OffsetSource"/>'s stream, never
            /// the graph's. A decrypted copy's stream holds a re-serialised file the ranges do not
            /// describe, and digesting it reports every signature broken — or, one bug further on,
            /// reports a modified document intact.
            /// </summary>
            public readonly Stream? Bytes;

            /// <summary>Opens a signed revision's byte prefix as a document; <c>null</c> opens it directly.</summary>
            public readonly Func<byte[], PdfDocument>? OpenSignedRevision;

            /// <summary>The policy to read every knob from: the caller's, or the shared defaults.</summary>
            public readonly SignatureVerificationOptions Options;

            /// <summary>
            /// The caller's <b>original</b> options reference — <c>null</c> when they passed none.
            /// <para>
            /// This exists for exactly one consumer: <see cref="CmsSignatureVerifier"/>, whose
            /// documented contract is that the legacy process-wide
            /// <see cref="CmsSignatureVerifier.RejectWeakAlgorithms"/> static applies "only when no
            /// options object is passed". Resolving
            /// <see cref="SignatureVerificationOptions.OrDefault"/> up front and handing the
            /// (never-null) instance down would make the CMS layer always see an explicit policy, and
            /// the static would be consulted by <i>no path through the public API at all</i>. Passing
            /// the original reference keeps it live without touching precedence:
            /// explicit options still win outright in both directions, including the relaxing one.
            /// </para>
            /// </summary>
            public readonly SignatureVerificationOptions? CallerOptions;

            /// <summary>
            /// The signed bytes this verification may still read and digest:
            /// <see cref="PdfLimits.MaxSignatureCoverageMultiple"/> times the file's length, shared by
            /// every signature and document timestamp it reads.
            /// </summary>
            private long _coverageLeft;

            /// <summary>How many more signatures and document timestamps this verification reads (<see cref="PdfLimits.MaxSignaturesPerDocument"/>).</summary>
            private int _signaturesLeft;

            /// <summary>
            /// Spends one signature and <paramref name="bytes"/> of the coverage budget, or answers
            /// why not — <see cref="PdfLimits.MaxSignaturesPerDocument"/> or
            /// <see cref="PdfLimits.MaxSignatureCoverageMultiple"/> — and spends nothing.
            /// </summary>
            public string? TrySpend(long bytes)
            {
                if (_signaturesLeft <= 0)
                    return $"the {PdfLimits.MaxSignaturesPerDocument} signatures read before it are the verifier's budget " +
                           "(PdfLimits.MaxSignaturesPerDocument)";
                if (bytes > _coverageLeft)
                    return $"the signatures read before it already cover the document {PdfLimits.MaxSignatureCoverageMultiple} " +
                           "times over, the verifier's budget (PdfLimits.MaxSignatureCoverageMultiple)";
                _signaturesLeft--;
                _coverageLeft -= bytes;
                return null;
            }

            private bool _docTimestampsResolved;
            private List<DocumentTimestampCoverage>? _docTimestamps;

            public VerificationContext(
                PdfDocument document, PdfDocument offsetSource, Func<byte[], PdfDocument>? openSignedRevision,
                SignatureVerificationOptions? callerOptions)
            {
                Document = document;
                OffsetSource = offsetSource;
                Bytes = offsetSource.BaseStream;
                OpenSignedRevision = openSignedRevision;
                CallerOptions = callerOptions;
                Options = SignatureVerificationOptions.OrDefault(callerOptions);
                _coverageLeft = Bytes == null ? 0 : Math.Max(0, PdfLimits.MaxSignatureCoverageMultiple) * Bytes.Length;
                _signaturesLeft = PdfLimits.MaxSignaturesPerDocument;
            }

            /// <summary>
            /// Time asserted by the EARLIEST verified, trust-anchored <c>/DocTimeStamp</c> whose
            /// signed byte ranges actually <b>cover</b> <c>[holeStart, holeEnd)</c> — the file
            /// region holding the signature being assessed — or <c>null</c> when no such timestamp
            /// exists.
            /// <para>
            /// <paramref name="holeStart"/>/<paramref name="holeEnd"/> bound the signature's
            /// <c>/Contents</c> hex string (its <c>/ByteRange</c> elements 1..2). A timestamp
            /// proves only that the bytes IT hashed existed at its GenTime, so it may vouch for
            /// this signature only when one of its own signed ranges contains that region outright.
            /// Without that test any anchored timestamp anywhere in the file back-dated certificate
            /// validity for EVERY signature in it, including one appended years later with an
            /// expired certificate — the precise attack
            /// <see cref="DeriveTrustedAssessmentTime"/> exists to prevent, arriving through the
            /// other door.
            /// </para>
            /// <para>
            /// <b>Containment, not a prefix-end comparison.</b> Testing only that the timestamp's
            /// first range ENDS past the signature assumes that range STARTS at offset 0. A crafted
            /// <c>/ByteRange</c> whose first element is non-zero leaves the file's prefix unsigned
            /// while still reaching far up the file, so a signature sitting in that unsigned prefix
            /// would pass an end-only test while the token hashed none of its bytes.
            /// <see cref="DocumentTimestampCoverage.Covers"/> compares both ends of both ranges.
            /// </para>
            /// <para>
            /// The earliest covering timestamp wins: it is the tightest upper bound on when the
            /// signature existed, and preferring a later one could push the assessment past a
            /// certificate's NotAfter that the earlier timestamp proves it was inside.
            /// Verification of the candidates is done once per document (they are cached); only the
            /// selection is per signature.
            /// </para>
            /// </summary>
            public DateTime? TrustedDocumentTimestampCovering(
                long holeStart, long holeEnd, List<string> warnings)
            {
                DateTime? earliest = null;
                bool sawNonCovering = false;
                foreach (var ts in DocumentTimestamps(warnings))
                {
                    if (!ts.Covers(holeStart, holeEnd)) { sawNonCovering = true; continue; }
                    if (!earliest.HasValue || ts.Time < earliest.Value) earliest = ts.Time;
                }

                if (!earliest.HasValue && sawNonCovering)
                    warnings?.Add(
                        "A verified document timestamp is present, but its signed byte ranges do not " +
                        "cover this signature — the signature was appended after the timestamp was " +
                        "made. A timestamp that does not cover this signature says nothing about when " +
                        "it existed, so certificate validity is assessed at the current time instead.");

                return earliest;
            }

            /// <summary>The document timestamps that may vouch for a time, verified once a document.</summary>
            private List<DocumentTimestampCoverage> DocumentTimestamps(List<string> warnings)
            {
                if (!_docTimestampsResolved)
                {
                    _docTimestampsResolved = true;
                    _docTimestamps = ResolveDocumentTimestamps(
                        Document, Bytes, Options, CallerOptions, warnings, TrySpend);
                }
                return _docTimestamps ?? new List<DocumentTimestampCoverage>();
            }

            /// <summary>
            /// Whether the authority behind a verified, anchored signature timestamp can be relied
            /// on for its time: its certificate is not revoked, and — under
            /// <see cref="SignatureVerificationOptions.RequireCurrentTimestampAuthority"/> — its
            /// chain is valid at the current time or a document timestamp that may itself vouch
            /// covers <c>[holeStart, holeEnd)</c>, the file region the token lies in. Says why not
            /// on <paramref name="warnings"/>.
            /// </summary>
            public bool AuthorityMayVouch(
                TsaTokenVerifier.TsaVerificationResult ts, string what, List<string> warnings,
                long holeStart, long holeEnd)
            {
                if (AuthorityIsRevoked(ts, Document, Options, warnings, what)) return false;
                if (!Options.RequireCurrentTimestampAuthority || ts.IsAuthorityCurrent) return true;

                foreach (var later in DocumentTimestamps(warnings))
                    if (later.Covers(holeStart, holeEnd)) return true;

                warnings.Add(
                    what + " verified, but its TSA certificate is no longer valid at the current time and " +
                    "no later document timestamp covers the token, so nothing shows the token was made " +
                    "while the certificate's key was the authority's; its time is ignored for " +
                    "certificate-validity assessment (SignatureVerificationOptions.RequireCurrentTimestampAuthority).");
                return false;
            }
        }

        /// <summary>
        /// Whether the document's <c>/DSS</c> — or, when the caller asked for online revocation, the
        /// authority's own responder or CRL — reports the TSA certificate of a verified token, or a
        /// CA above it, revoked. A revoked authority's time is no one's assertion; what is not
        /// settled either way leaves the token as it was, as it does a signer's certificate.
        /// </summary>
        private static bool AuthorityIsRevoked(
            TsaTokenVerifier.TsaVerificationResult ts, PdfDocument document,
            SignatureVerificationOptions opts, List<string> warnings, string what)
        {
            if (ts.TsaCertificate == null) return false;

            // Kept apart from the caller's warnings unless the answer is "revoked": that the TSA's
            // status is unsettled is the ordinary state of a signature with no LTV data.
            var said = new List<string>();
            // Along the path the token's chain was anchored through, as a signer's is.
            var chain = ts.TrustedPath.Count > 0
                ? CertificateChainValidator.RevocationPath(ts.TrustedPath, ts.Certificates, opts.EffectiveTimestampAnchors)
                : CertificateChainValidator.BuildChain(ts.TsaCertificate, ts.Certificates, opts.EffectiveTimestampAnchors);
            if (RevocationChecker.CheckChainWithOnlineFallback(document, chain, said, opts, ts.TokenTime)
                != PdfRevocationStatus.Revoked)
                return false;

            warnings.AddRange(said);
            warnings.Add(what + ": the timestamp authority's certificate is revoked; its time is ignored " +
                         "for certificate-validity assessment.");
            return true;
        }

        /// <summary>
        /// A verified, trust-anchored <c>/DocTimeStamp</c> reduced to the two file regions its
        /// TimeStampToken actually signs, plus the UTC time it asserts.
        /// </summary>
        /// <remarks>
        /// Both ranges are kept, rather than just the end of the first one, because the start of
        /// the first range is not guaranteed to be 0 — see the remarks on
        /// <see cref="VerificationContext.TrustedDocumentTimestampCovering"/>.
        /// </remarks>
        private sealed class DocumentTimestampCoverage
        {
            /// <summary>
            /// Start of the timestamp's first signed range — its <c>/ByteRange</c> element 0.
            /// Normally 0, but a crafted file may raise it to leave the prefix unsigned, which is
            /// why it is compared rather than assumed.
            /// </summary>
            public long RangeAStart;

            /// <summary>
            /// End (exclusive) of the first signed range, i.e. the offset of this timestamp's own
            /// <c>/Contents</c> opening delimiter.
            /// </summary>
            public long RangeAEnd;

            /// <summary>Start of the second signed range, just past the timestamp's own hole.</summary>
            public long RangeBStart;

            /// <summary>Exclusive.</summary>
            public long RangeBEnd;

            /// <summary>The token's GenTime, in UTC.</summary>
            public DateTime Time;

            /// <summary>
            /// Whether the token's authority can be relied on without help: its chain is valid at
            /// the current time, or the caller does not ask. One that cannot is kept only when a
            /// later timestamp that can covers this one's own hole.
            /// </summary>
            public bool AuthorityCurrent;

            /// <summary>
            /// True when <c>[start, endExclusive)</c> lies wholly inside ONE of the two signed
            /// ranges. Straddling the excluded gap does not count: the bytes in the gap are the
            /// timestamp's own /Contents hole and are not signed by it.
            /// </summary>
            public bool Covers(long start, long endExclusive)
                => (start >= RangeAStart && endExclusive <= RangeAEnd)
                || (start >= RangeBStart && endExclusive <= RangeBEnd);
        }

        /// <summary>
        /// Reads <paramref name="key"/> following an indirect reference, which
        /// <see cref="PdfDictionary"/>'s indexer and typed getters do not.
        /// </summary>
        /// <remarks>
        /// §7.3.10 permits any value to be written indirectly, and a getter's <c>null</c> for one
        /// is indistinguishable from the key being absent. In a verifier that difference is a
        /// verdict: the sub-filter ladder below reads "absent" as "unsupported" and fails a valid
        /// signature. <c>Signing</c> may not reference <c>Base</c>, so this is local rather than
        /// <c>PdfResolve</c>.
        /// </remarks>
        private static PdfObject? Res(PdfDocument doc, PdfDictionary? dict, PdfName key)
        {
            PdfObject? value = dict?[key];
            return value is PdfReference && doc != null ? doc.ResolveObject(value) : value;
        }

        private SignatureVerificationResult VerifyField(PdfSignatureField field, VerificationContext context)
        {
            var document = context.Document;
            var opts = context.Options;

            var result = new SignatureVerificationResult
            {
                FieldName = field.ReportedName,
                Reason = field.Reason,
                Location = field.Location,
                SigningTime = field.SigningTime,
            };

            var sigDict = field.SignatureValue;
            if (sigDict == null)
            {
                Fail(result, "No signature value dictionary.");
                return result;
            }

            // Get SubFilter. Resolved: GetName does NOT follow an indirect reference, and §7.3.10
            // permits one here — so `/SubFilter 9 0 R` read as absent, the ladder below matched
            // neither branch, and a cryptographically VALID signature was reported invalid,
            // quoting an empty sub-filter the file does not contain. Worse than the wrong verdict,
            // the early return happens before /ByteRange is read, so ByteRangeCoverageEnd stays 0
            // — and a 0 there disables the shadow-append check at :169 and stops the DocMDP block
            // matching this signature at all, which is the hazard that block's own comment names.
            var subFilter = Res(document, sigDict, PdfName.SubFilter) as PdfName;
            string subFilterValue = subFilter?.Value ?? "";

            // Supported: adbe.pkcs7.detached, ETSI.CAdES.detached (same CMS structure),
            // and ETSI.RFC3161 (document timestamp — raw TimeStampToken in /Contents).
            bool isCms = subFilterValue == "adbe.pkcs7.detached"
                      || subFilterValue == "ETSI.CAdES.detached";
            bool isRfc3161 = subFilterValue == "ETSI.RFC3161";

            if (!isCms && !isRfc3161)
            {
                Fail(result,
                    $"Unsupported signature sub-filter: '{subFilterValue}'. " +
                    "Supported: adbe.pkcs7.detached, ETSI.CAdES.detached, ETSI.RFC3161.");
                return result;
            }

            result.SubFilter = subFilterValue;

            // Get ByteRange.
            // ONE reader, in Core's SignedByteRanges, which type-checks the elements (PdfArray.GetLong
            // THROWS on anything that is not a PdfNumber). It resolves for the same reason /SubFilter
            // above does: unlike /Contents below, §12.8.1 places no in-place requirement on
            // /ByteRange — it is an ordinary array inside the digested dictionary — and §7.3.10
            // permits a reference anywhere a value is permitted, for the array AND for each element.
            // Refusing a reference element as "not an integer" would report a valid signature
            // invalid and leave ByteRangeCoverageEnd at 0, which disables the shadow/append check and
            // stops the DocMDP block matching this signature at all.
            //
            // The `!= 4` rule stays HERE rather than moving into the reader: a fifth pair would be
            // a second signed region this code does not digest, so a verifier must refuse it while
            // the reporting callers tolerate a longer array.
            if (!SignedByteRanges.TryRead(sigDict, document, out var byteRanges, out string byteRangeError)
                || byteRanges.ElementCount != 4)
            {
                Fail(result, byteRangeError ?? "Invalid or missing /ByteRange array.");
                return result;
            }

            long offset1 = byteRanges.Offset1;
            long length1 = byteRanges.Length1;
            long offset2 = byteRanges.Offset2;
            long length2 = byteRanges.Length2;

            // Get Contents (the PKCS#7 signature).
            //
            // DELIBERATELY NOT resolved, unlike the two reads above. §12.8.1 requires /Contents to
            // be a direct hex string written in place, because the gap the /ByteRange skips IS the
            // bytes of that string. An indirect /Contents cannot be what the byte range excludes,
            // so accepting one would mean verifying a blob that is not the one the file signed.
            // Failing here is the correct, closed answer.
            var contentsObj = sigDict.GetString(PdfName.Contents);
            if (contentsObj == null)
            {
                Fail(result, "Missing /Contents in signature dictionary.");
                return result;
            }

            byte[] signatureBytes = contentsObj.RawBytes;

            // The document must still be backed by a readable stream. VerifyAll guards this
            // before its shadow-detection pass, but the by-name Verify() path reaches here
            // directly — dereferencing a null BaseStream threw a NullReferenceException out of a
            // public API instead of reporting a clean validation error.
            //
            // The ORIGINAL file's stream, by way of the context — for an encrypted document the
            // graph's own stream is a decrypted copy the /ByteRange does not describe.
            var baseStream = context.Bytes;
            if (baseStream == null)
            {
                Fail(result, "Document has no backing stream; signed byte ranges cannot be read.");
                return result;
            }

            // Check ByteRange coverage
            long fileLength = baseStream.Length;
            result.CoversEntireDocument =
                offset1 == 0 && SignedByteRanges.ReachesEndOfFile(baseStream, offset2 + length2, fileLength);

            // Check if ByteRange is contiguous (the gap is exactly the /Contents hex string)
            // A valid ByteRange has: [0, beforeContents, afterContents, lengthAfter]
            // The gap between the two ranges should be the signature contents only
            result.CoversRevision = (offset1 == 0 && offset2 > length1);

            if (!result.CoversRevision)
            {
                Fail(result, "ByteRange has unexpected gaps (possible tampering).");
            }

            // Validate ByteRange bounds before allocating/reading. The values are
            // attacker-controlled; without this, the (int) casts below can overflow
            // (a huge length wraps negative) and the read can run past the file.
            // Per ISO 32000 the first range always starts at 0, the two ranges are
            // ordered, and the gap between them is the (excluded) /Contents hole.
            if (offset1 != 0 || length1 < 0 || length2 < 0
                || offset2 < offset1 + length1
                || offset2 + length2 > fileLength
                || (long)length1 + length2 > int.MaxValue)
            {
                Fail(result, "Invalid /ByteRange bounds (possible tampering).");
                return result;
            }

            // The excluded gap between the two signed ranges must hold ONLY the /Contents
            // hex string (`<...>`). A gap larger than the hex-encoded signature means
            // unsigned bytes are hidden in the hole, outside the CMS — flag it. (2×raw + 2
            // delimiters; allow a small slack for whitespace some writers add around it.)
            long gap = offset2 - (offset1 + length1);
            long expectedContentsHole = 2L * signatureBytes.Length + 2;
            if (gap > expectedContentsHole + 32)
            {
                Fail(result,
                    "ByteRange gap is larger than the /Contents placeholder — unsigned bytes are hidden in the excluded region (possible tampering).");
            }
            else if (!SignedByteRanges.GapHoldsOnlyContents(baseStream, offset1 + length1, gap, signatureBytes))
            {
                // Its size cannot tell: a padded /Contents written as a literal string takes half the
                // room its hex form is allowed, and the rest of the gap, which no digest covers, can
                // hold a new dictionary, new objects and the table the moved second block's startxref
                // now lands on — signature wrapping. So what the gap holds is read.
                Fail(result,
                    "ByteRange gap holds something other than this signature's /Contents — unsigned bytes are hidden in the excluded region (possible signature wrapping).");
            }


            // Signatures × bytes: each may cover the whole file, so the bytes all of them read are
            // bounded by the file's own length (PdfLimits.MaxSignatureCoverageMultiple), and each
            // costs its CMS work however little it covers (PdfLimits.MaxSignaturesPerDocument). One
            // past either is refused, and has no coverage to vouch with.
            string? overBudget = context.TrySpend(length1 + length2);
            if (overBudget != null)
            {
                Fail(result, "Not verified: " + overBudget + ".");
                return result;
            }

            // The digest proves the signed bytes and says nothing of the dictionary it was read from: a
            // genuine signature's /Contents and /ByteRange copied into a field added later verify,
            // beside a /Reason, /Name, /M and field name of the forger's. The object in force has to
            // be the one the gap stands in. An inline value stands in its field's object.
            int holder = sigDict.IsIndirect ? sigDict.ObjectNumber : field.Dictionary.ObjectNumber;
            CrossReferenceEntry? entry = context.OffsetSource.CrossReferenceTable.GetEntry(holder);
            if (entry == null || entry.Type != CrossReferenceEntry.EntryType.Normal
                || !SignedByteRanges.GapIsInObjectAt(baseStream, entry.Offset, offset1 + length1))
            {
                Fail(result,
                    "This signature dictionary is not among the bytes its /ByteRange signs: its /Contents is a copy of another's, and what it says beside them is unsigned (possible forgery).");
            }
            result.ByteRangeCoverageEnd = offset2 + length2;

            // Read the signed byte ranges
            byte[] signedData;
            try
            {
                signedData = SignedByteRanges.ReadSignedBytes(baseStream, offset1, length1, offset2, length2);
            }
            catch (IOException ex)
            {
                Fail(result, $"Failed to read byte ranges: {ex.Message}");
                return result;
            }

            if (signedData == null)
            {
                Fail(result, "Failed to read byte ranges: the file ended before the signed bytes did.");
                return result;
            }

            // Verify the signature. The CMS path is handed the file region holding THIS
            // signature's /Contents (the excluded gap), because only a document timestamp whose
            // own signed ranges cover that region may back-date this signature's certificate
            // validity — see VerificationContext.TrustedDocumentTimestampCovering.
            // Trimmed HERE and not at the RawBytes read, because the gap arithmetic above sizes
            // the expected hole from the PADDED length — a trimmed array would make every
            // legitimate signature look like it had unsigned bytes hidden in the hole.
            byte[] signatureObject =
                SigningDefaults.TrimToAsn1Object(signatureBytes, out bool nonZeroPadding);

            // The bytes after the CMS sit in the excluded gap, so they are unsigned and were
            // writable after signing. The oversized-gap check above sees bytes OUTSIDE the hex
            // string; these are inside it, where nothing was looking. Reported either way, and
            // refused only on request: nothing in ISO 32000-1 requires the padding to be zeros,
            // so a producer that fills it otherwise is unusual rather than provably hostile.
            if (nonZeroPadding)
            {
                result.HasNonZeroContentsPadding = true;

                if (context.Options.RejectNonZeroContentsPadding)
                    Fail(result,
                        "/Contents carries non-zero bytes after the CMS object \u2014 unsigned data "
                        + "in the excluded ByteRange gap (possible tampering).");
                else
                    result.Warnings.Add(
                        "/Contents carries non-zero bytes after the CMS object; they lie in the "
                        + "excluded ByteRange gap and are not covered by the signature.");
            }

            if (isRfc3161)
                VerifyRfc3161Field(result, signatureObject, signedData, context);
            else
                VerifyCmsField(result, signatureObject, signedData, context,
                               offset1 + length1, offset2);

            return result;
        }

        /// <summary>
        /// /SubFilter /ETSI.RFC3161 — /Contents is a raw DER TimeStampToken. Verifies the hash in
        /// the TSTInfo matches the signed byte ranges, and anchors the TSA certificate.
        /// </summary>
        private static void VerifyRfc3161Field(
            SignatureVerificationResult result, byte[] signatureBytes, byte[] signedData,
            VerificationContext context)
        {
            var opts = context.Options;
            var warnings = new List<string>();
            // CallerOptions, not opts — same precedence as the CMS path: only a caller who stated
            // no policy at all falls back to the legacy process-wide RejectWeakAlgorithms static.
            var tsResult = TsaTokenVerifier.VerifyTimestampToken(
                signatureBytes, signedData, opts.EffectiveTimestampAnchors, warnings,
                context.CallerOptions);

            // The CRYPTOGRAPHIC verdict, not the combined one — the same distinction VerifyCmsField
            // draws, and for the same reason: IsIntegrityValid is what MayVouchForCoverage consults
            // when deciding which signatures may vouch for their /ByteRange. Mapping the policy
            // verdict here would make a weak-algorithm refusal look like a forged token and would
            // silently drop this timestamp out of the shadow-attack coverage sum, so a document
            // ending in a SHA-1 /DocTimeStamp would report unsigned trailing bytes on every
            // signature in it. The refusal still reaches the caller through ValidationErrors below.
            result.IsIntegrityValid = tsResult.IsCryptographicallyValid;
            result.SignerCommonName = tsResult.TsaName;
            result.SignerSubject = tsResult.TsaName;
            result.DigestAlgorithm = tsResult.DigestAlgorithm;
            result.ChainStatus = tsResult.ChainStatus;
            result.HasTrustedTimestamp = tsResult.IsTrusted;
            result.WeakAlgorithm = tsResult.WeakAlgorithm;
            result.WeakAlgorithmReason = tsResult.WeakAlgorithmReason;
            if (tsResult.WeakAlgorithm && tsResult.IsValid)
                result.Warnings.Add("Weak algorithm: " + tsResult.WeakAlgorithmReason);
            result.Warnings.AddRange(warnings);

            if (!tsResult.IsValid && !string.IsNullOrEmpty(tsResult.Error))
                Fail(result, tsResult.Error);
            if (tsResult.TokenTime.HasValue)
                result.TimestampTime = tsResult.TokenTime;

            if (tsResult.IsValid && !tsResult.IsTrusted)
                result.Warnings.Add(
                    "Document timestamp verified but its TSA certificate is not anchored to a supplied " +
                    "trust anchor; the asserted time is not authenticated.");
            else if (tsResult.IsValid && !tsResult.IsAuthorityCurrent)
                result.Warnings.Add(
                    "Document timestamp verified against a TSA certificate that is no longer valid at the " +
                    "current time; its time counts for certificate validity only while a later document " +
                    "timestamp covers it.");
        }

        /// <summary>
        /// adbe.pkcs7.detached / ETSI.CAdES.detached — full CMS pipeline: integrity, weak-algorithm
        /// policy, ESS binding, trusted-time derivation, chain trust and embedded-DSS revocation.
        /// </summary>
        /// <param name="contentsHoleStart">
        /// Offset of the first byte of this signature's excluded /Contents region.
        /// </param>
        /// <param name="contentsHoleEnd">
        /// Offset one past the last byte of it. Together these identify the bytes a document
        /// timestamp must sign before it may vouch for when this signature was made.
        /// </param>
        /// <param name="result">Receives the verdict; its fields are set in place.</param>
        /// <param name="signatureBytes">The signature dictionary's <c>/Contents</c> CMS blob.</param>
        /// <param name="signedData">
        /// The bytes the signature covers — the two <c>/ByteRange</c> spans concatenated, with the
        /// <c>/Contents</c> hole excluded.
        /// </param>
        /// <param name="context">The per-document verification state: options, trailer and streams.</param>
        private static void VerifyCmsField(
            SignatureVerificationResult result, byte[] signatureBytes, byte[] signedData,
            VerificationContext context, long contentsHoleStart, long contentsHoleEnd)
        {
            var opts = context.Options;

            // CallerOptions, not opts: the CMS layer distinguishes "the caller stated a policy"
            // from "the caller stated nothing", and only in the second case may the legacy
            // process-wide RejectWeakAlgorithms static apply. Handing it the resolved instance
            // made every call look explicit, so the static could never be reached from here —
            // see VerificationContext.CallerOptions. Key sizes are unaffected either way, since
            // CmsSignatureVerifier falls back to the same SignatureVerificationOptions.Default
            // values this method's `opts` would have carried.
            var cmsResult = CmsSignatureVerifier.VerifyDetachedSignature(
                signatureBytes, signedData, context.CallerOptions);

            // The CRYPTOGRAPHIC verdict, not the combined one. The weak-algorithm policy refuses by
            // default, so mapping
            // cmsResult.IsValid here would make a policy refusal indistinguishable from a
            // tampered document — and worse, would silently exclude every SHA-1-signed
            // signature from the shadow-attack coverage check above, which asks IsIntegrityValid
            // precisely to establish "this signature really verified, so it may vouch for its
            // range". The refusal still reaches the caller: cmsResult.Error is added to
            // ValidationErrors below, and IsValid is derived as integrity && no errors.
            result.IsIntegrityValid = cmsResult.IsCryptographicallyValid;
            result.SignerCommonName = cmsResult.SignerCommonName;
            result.DigestAlgorithm = cmsResult.DigestAlgorithm;

            // Surface the weak-algorithm finding. CmsSignatureVerifier computes this and its
            // docs promise a SHA-1 signature is "merely flagged" when RejectWeakAlgorithms is
            // false (the default) — but the flag was dropped here, so the documented safe
            // path did not reach any caller of the public verify API.
            result.WeakAlgorithm = cmsResult.WeakAlgorithm;
            result.WeakAlgorithmReason = cmsResult.WeakAlgorithmReason;
            if (cmsResult.WeakAlgorithm && cmsResult.IsValid)
                result.Warnings.Add("Weak algorithm: " + cmsResult.WeakAlgorithmReason);

            if (!cmsResult.IsValid && !string.IsNullOrEmpty(cmsResult.Error))
                Fail(result, cmsResult.Error);

            // A CMS with more than one SignerInfo is non-standard for PDF (ISO 32000 §12.8) and
            // means the identity reported above is simply whichever SignerInfo came first — an
            // attacker can prepend a decoy to swap the displayed signer. Treat as structural.
            if (cmsResult.SignerCount > 1)
                Fail(result,
                    $"CMS carries {cmsResult.SignerCount} SignerInfos; PDF signatures are single-signer " +
                    "per ISO 32000 §12.8, so the reported signer identity is ambiguous.");

            var signerCert = cmsResult.SignerCertificate;
            if (signerCert == null) return;

            try
            {
                result.SignerSubject = signerCert.SubjectDN?.ToString();
                result.SignerIssuer = signerCert.IssuerDN?.ToString();
            }
            catch { /* DN encoding issues are not fatal to the verdict */ }

            var warnings = new List<string>();

            // The claimed signing time (Pkcs9AtSigningTime) is a SIGNED attribute set by the
            // signer and is UNAUTHENTICATED by any trusted clock — a signer with an expired
            // certificate can simply claim a signing time inside its old validity window. It is
            // therefore surfaced as INFORMATIONAL only and must NOT be used to extend a
            // certificate past NotAfter when assessing chain validity.
            result.ClaimedSigningTime = ExtractSigningTime(cmsResult.Signer, warnings);

            // Trusted assessment time: only a cryptographically-verified RFC 3161 timestamp token
            // whose TSA certificate chains to a trust anchor may move certificate validity back in
            // time. Absent one, validity is assessed at "now" so an expired cert fails closed.
            DateTime? trustedTime = DeriveTrustedAssessmentTime(
                cmsResult.Signer, context, warnings, contentsHoleStart, contentsHoleEnd);
            DateTime assessmentTime = trustedTime ?? DateTime.UtcNow;
            result.AssessmentTime = assessmentTime;
            result.HasTrustedTimestamp = trustedTime.HasValue;
            if (trustedTime.HasValue) result.TimestampTime = trustedTime;

            if (!trustedTime.HasValue && result.ClaimedSigningTime.HasValue
                && result.ClaimedSigningTime.Value < DateTime.UtcNow)
                warnings.Add(
                    "No verified timestamp present; certificate validity is assessed at the current " +
                    "time, not the (unauthenticated) claimed SigningTime.");

            // Chain trust.
            result.ChainStatus = CertificateChainValidator.Resolve(
                signerCert, cmsResult.Certificates, opts.TrustAnchors, assessmentTime, warnings,
                out List<X509Certificate> trustedPath,
                requireDocumentSigningPurpose: opts.RequireSignerKeyPurpose);

            // Revocation from the document's embedded /DSS (LTV) store — leaf plus intermediates —
            // and, when the caller opted in and the document did not settle it, asked online: along
            // the path the walk anchored, whose issuers are the ones the trust rests on (a separately
            // built chain follows whichever same-named certificate's key verifies first, and a bag can
            // put a decoy there); along the best-effort chain for a signer that is not trusted. The
            // anchors are issuers there too: without them a CMS that leaves its root out leaves its top
            // intermediate with no issuer to check it against.
            var chain = result.ChainStatus == PdfSignatureChainStatus.Trusted
                ? CertificateChainValidator.RevocationPath(trustedPath, cmsResult.Certificates, opts.TrustAnchors)
                : CertificateChainValidator.BuildChain(signerCert, cmsResult.Certificates, opts.TrustAnchors);
            result.RevocationStatus = RevocationChecker.CheckChainWithOnlineFallback(
                context.Document, chain, warnings, opts, assessmentTime, signatureBytes);
            if (result.RevocationStatus == PdfRevocationStatus.Revoked)
                result.ChainStatus = PdfSignatureChainStatus.Invalid;

            result.Warnings.AddRange(warnings);
        }

        /// <summary>
        /// Returns a cryptographically-verified, trust-anchored UTC time at which to assess
        /// certificate validity, or <c>null</c> when none is present (in which case the caller
        /// falls back to <see cref="DateTime.UtcNow"/>).
        /// <para>
        /// Anchoring is the point. A timestamp token carries its own TSA certificate, so verifying
        /// the token against that certificate proves nothing about who issued it: anyone can mint
        /// a self-signed certificate bearing the timeStamping EKU and assert any GenTime. Honouring
        /// such a token would let an expired signer certificate back-date itself into its own
        /// validity window — exactly the failure this function exists to prevent.
        /// </para>
        /// <para>
        /// Coverage is the second half of the same point, and it is enforced on the
        /// <c>/DocTimeStamp</c> fallback below: a document timestamp is accepted only when one of
        /// its own signed ranges wholly contains this signature's <c>/Contents</c> hole
        /// (<paramref name="contentsHoleStart"/>..<paramref name="contentsHoleEnd"/>). A timestamp
        /// proves that the bytes IT hashed existed at its GenTime and nothing more, so the first
        /// anchored timestamp in the file must not vouch for a signature appended to the file
        /// afterwards.
        /// </para>
        /// </summary>
        /// <param name="contentsHoleStart">
        /// Offset of the first byte of this signature's excluded /Contents region.
        /// </param>
        /// <param name="contentsHoleEnd">
        /// Offset one past the last byte of it. Together these identify the bytes a document
        /// timestamp must sign before it may vouch for when this signature was made.
        /// </param>
        /// <param name="signer">The CMS signer whose claimed signing time is being assessed.</param>
        /// <param name="context">The per-document verification state: options, trailer and streams.</param>
        /// <param name="warnings">
        /// Accumulates why no trusted time could be derived — which is what stops an
        /// unauthenticated <c>/M</c> being mistaken for one.
        /// </param>
        private static DateTime? DeriveTrustedAssessmentTime(
            SignerInformation? signer, VerificationContext context, List<string> warnings,
            long contentsHoleStart, long contentsHoleEnd)
        {
            var opts = context.Options;

            // 1) Signature timestamp: an id-aa-signatureTimeStampToken UNSIGNED attribute whose
            //    message imprint covers the signer's signature value (RFC 3161 §2.4.1 / CAdES).
            try
            {
                var unsigned = signer?.UnsignedAttributes;
                if (unsigned != null)
                {
                    var tsAttr = unsigned[SignatureTimeStampOid];
                    if (tsAttr != null && tsAttr.AttrValues.Count > 0)
                    {
                        byte[] tokenDer = tsAttr.AttrValues[0].ToAsn1Object().GetEncoded();
                        byte[] imprintInput = signer!.GetSignature();
                        // context.CallerOptions carries the weak-algorithm policy; a token the
                        // policy refuses comes back IsValid == false and therefore cannot reach the
                        // return below, which is the whole point — a SHA-1 signature timestamp must
                        // not be the clock a certificate's validity is judged against.
                        var ts = TsaTokenVerifier.VerifyTimestampToken(
                            tokenDer, imprintInput, opts.EffectiveTimestampAnchors, warnings,
                            context.CallerOptions);

                        if (ts.IsValid && ts.TokenTime.HasValue)
                        {
                            // An anchored token still has to come from an authority that can be
                            // relied on today — not revoked, and either still valid or covered by a
                            // later document timestamp (the token lies in this signature's own
                            // /Contents hole, so a timestamp over the hole is one over the token).
                            if (ts.IsTrusted
                                    ? context.AuthorityMayVouch(ts, "Signature timestamp", warnings,
                                                                contentsHoleStart, contentsHoleEnd)
                                    : !opts.RequireTrustedTimestamp)
                                return ts.TokenTime.Value.ToUniversalTime();

                            if (!ts.IsTrusted)
                                warnings.Add(
                                    "Signature timestamp verified but its TSA certificate does not chain to a " +
                                    "supplied trust anchor; the claimed time is not authenticated and is ignored " +
                                    "for certificate-validity assessment.");
                        }
                        else if (!ts.IsValid && !string.IsNullOrEmpty(ts.Error))
                        {
                            warnings.Add("Embedded signature timestamp did not verify; ignored. " + ts.Error);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                warnings.Add("Signature-timestamp extraction failed: " + ex.Message);
            }

            // 2) Document timestamp (/DocTimeStamp, ETSI.RFC3161): a separate LTV timestamp field
            //    whose /Contents is a raw TimeStampToken over its own ByteRange — accepted only
            //    when that range actually covers THIS signature. An unrelated one proves nothing
            //    about a signature appended after it.
            try
            {
                return context.TrustedDocumentTimestampCovering(
                    contentsHoleStart, contentsHoleEnd, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add("Document-timestamp verification failed: " + ex.Message);
                return null;
            }
        }

        // id-aa-signatureTimeStampToken (RFC 3161 / RFC 5652) — the CAdES signature timestamp,
        // carried as an UNSIGNED attribute on the SignerInfo whose message imprint is the
        // signer's signature value.
        private static readonly Org.BouncyCastle.Asn1.DerObjectIdentifier SignatureTimeStampOid =
            new Org.BouncyCastle.Asn1.DerObjectIdentifier(PdfEkuOids.SignatureTimeStampToken);

        /// <summary>
        /// Locates EVERY ETSI.RFC3161 document-timestamp field, reconstructs each one's signed byte
        /// ranges from the document bytes, and verifies the embedded TimeStampToken. Returns one
        /// entry per timestamp that fully verifies AND whose TSA certificate is anchored, each
        /// carrying <i>both</i> ranges of what it signed so a caller can ask whether it covers a
        /// given signature.
        /// <para>
        /// The FIRST such time alone would make "a timestamp exists"
        /// indistinguishable from "a timestamp covers this signature". See
        /// <see cref="VerificationContext.TrustedDocumentTimestampCovering"/> for why the coverage
        /// travels with the time rather than being discarded here.
        /// </para>
        /// </summary>
        /// <remarks>
        /// <c>callerOptions</c> is the caller's ORIGINAL options reference (null when they stated
        /// none), carried separately from <c>opts</c> for the weak-algorithm policy alone — see
        /// <see cref="VerificationContext.CallerOptions"/>. A token that policy refuses comes back
        /// <c>IsValid == false</c> and so never becomes a <see cref="DocumentTimestampCoverage"/>,
        /// which is what stops a SHA-1 <c>/DocTimeStamp</c> back-dating any signature in the file.
        /// </remarks>
        private static List<DocumentTimestampCoverage> ResolveDocumentTimestamps(
            PdfDocument doc, Stream? stream, SignatureVerificationOptions opts,
            SignatureVerificationOptions? callerOptions, List<string> warnings, Func<long, string?> trySpend)
        {
            var found = new List<DocumentTimestampCoverage>();
            bool overBudget = false;

            var acro = doc.AcroForm;
            if (acro == null) return found;

            // The original file's bytes (VerificationContext.Bytes), which the token's ranges index.
            if (stream == null) return found;

            foreach (var field in acro.GetSignatureFields())
            {
                if (!field.HasSignature) continue;
                var v = field.SignatureValue;
                if (v == null) continue;
                // Resolved: an unresolved /SubFilter made this `continue`, so the field never
                // became a DocumentTimestampCoverage and TrustedDocumentTimestampCovering returned
                // null. That only changes a verdict for a signature with no verifying anchored CMS
                // signature-timestamp — a B-B signature archived later under a document timestamp
                // is the case — but for exactly those it is the whole assessment time.
                if ((Res(doc, v, PdfName.SubFilter) as PdfName)?.Value != "ETSI.RFC3161") continue;

                // The same one reader VerifyField uses, so the two cannot disagree about which
                // timestamps are readable.
                var contents = v.GetString(PdfName.Contents);   // in place, per §12.8.1 — see VerifyField
                if (contents == null) continue;
                if (!SignedByteRanges.TryRead(v, doc, out var byteRange, out _)) continue;
                if (byteRange.ElementCount != 4) continue;

                long a = byteRange.Offset1, b = byteRange.Length1;
                long c = byteRange.Offset2, d = byteRange.Length2;

                long len = stream.Length;
                if (a != 0 || b < 0 || c < 0 || d < 0 || c < a + b
                    || a + b > len || c + d > len || b + d > int.MaxValue)
                    continue;

                string? refused = trySpend(b + d);
                if (refused != null)
                {
                    if (!overBudget)
                        warnings.Add("Document timestamps past the verifier's budget were not verified (" + refused +
                                     "); their asserted times are ignored.");
                    overBudget = true;
                    continue;
                }

                byte[]? signed;
                try
                {
                    signed = SignedByteRanges.ReadSignedBytes(stream, a, b, c, d);
                }
                catch (IOException) { continue; }
                if (signed == null) continue;

                // Trimmed of its /Contents reserve padding for the same reason as the signature
                // path above: the token parser must see the object, not the hole it sits in.
                byte[] rawToken = contents.RawBytes ?? Array.Empty<byte>();
                byte[] tokenDer = SigningDefaults.TrimToAsn1Object(rawToken);
                var ts = TsaTokenVerifier.VerifyTimestampToken(
                    tokenDer, signed, opts.EffectiveTimestampAnchors, warnings, callerOptions);

                if (ts.IsValid && ts.TokenTime.HasValue)
                {
                    // A revoked authority's token is dropped here, before it can vouch for a time
                    // or for an earlier timestamp.
                    if (ts.IsTrusted && AuthorityIsRevoked(ts, doc, opts, warnings, "Document timestamp"))
                        continue;

                    if (ts.IsTrusted || !opts.RequireTrustedTimestamp)
                    {
                        // Both ranges are recorded as written: [a, a+b) ends at this timestamp's
                        // own '<', and [c, c+d) resumes past its own hole. `a` is not assumed to
                        // be 0 — see DocumentTimestampCoverage.
                        found.Add(new DocumentTimestampCoverage
                        {
                            RangeAStart = a,
                            RangeAEnd = a + b,
                            RangeBStart = c,
                            RangeBEnd = c + d,
                            Time = ts.TokenTime.Value.ToUniversalTime(),
                            // An unanchored token is here only because the caller asked to read
                            // untrusted times; there is no authority to hold to the present.
                            AuthorityCurrent = !ts.IsTrusted || !opts.RequireCurrentTimestampAuthority
                                               || ts.IsAuthorityCurrent,
                        });
                        continue;
                    }

                    warnings.Add(
                        "Document timestamp verified but its TSA certificate does not chain to a supplied " +
                        "trust anchor; the asserted time is ignored for certificate-validity assessment.");
                }
                else if (!ts.IsValid && !string.IsNullOrEmpty(ts.Error))
                {
                    warnings.Add("Document timestamp did not verify; ignored. " + ts.Error);
                }
            }
            return KeepRenewed(found, warnings);
        }

        /// <summary>
        /// The timestamps of <paramref name="found"/> that may vouch for a time: each whose
        /// authority is current, and each whose own <c>/Contents</c> hole lies inside the signed
        /// ranges of a later one that may — the renewal chain of long-term validation, where every
        /// timestamp is made while the one before it can still be relied on.
        /// </summary>
        /// <remarks>
        /// Asked in order of where each timestamp's coverage ends, furthest first: a timestamp over
        /// another was made after it and reaches further up the file, so by the time a timestamp is
        /// asked every one that could cover it has its answer. A file arranged otherwise loses the
        /// timestamps it arranged, never gains one.
        /// </remarks>
        private static List<DocumentTimestampCoverage> KeepRenewed(
            List<DocumentTimestampCoverage> found, List<string> warnings)
        {
            found.Sort((x, y) => y.RangeBEnd.CompareTo(x.RangeBEnd));
            var kept = new List<DocumentTimestampCoverage>(found.Count);
            int dropped = 0;
            foreach (DocumentTimestampCoverage ts in found)
            {
                bool renewed = ts.AuthorityCurrent;
                for (int i = 0; !renewed && i < kept.Count; i++)
                    renewed = kept[i].Covers(ts.RangeAEnd, ts.RangeBStart);
                if (renewed) kept.Add(ts);
                else dropped++;
            }

            if (dropped > 0)
                warnings.Add(
                    dropped + " document timestamp(s) verified, but the TSA certificate is no longer valid at the " +
                    "current time and no later document timestamp covers the token; the asserted time is ignored " +
                    "for certificate-validity assessment (SignatureVerificationOptions.RequireCurrentTimestampAuthority).");
            return kept;
        }

        internal static DateTime? ExtractSigningTime(SignerInformation? signer, List<string> warnings)
        {
            try
            {
                var attrs = signer?.SignedAttributes;
                if (attrs == null) return null;
                var timeAttr = attrs[Org.BouncyCastle.Asn1.Pkcs.PkcsObjectIdentifiers.Pkcs9AtSigningTime];
                if (timeAttr == null) return null;
                var set = timeAttr.AttrValues;
                if (set.Count == 0) return null;
                var time = Org.BouncyCastle.Asn1.Cms.Time.GetInstance(set[0]);
                return time.ToDateTime();
            }
            catch (Exception ex)
            {
                warnings?.Add("SigningTime parse failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>Records a hard validation error and mirrors it into the thread-local diagnostic.</summary>
        private static void Fail(SignatureVerificationResult result, string? message)
        {
            if (message != null) result.ValidationErrors.Add(message);
            _lastVerificationError = message;
        }
    }
}
