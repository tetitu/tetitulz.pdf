// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;

namespace Tetitulz.Pdf.Core.Document
{
    /// <summary>
    /// Pure-structure (no cryptography) analysis of a PDF's DocMDP (Modification Detection and
    /// Prevention) certification policy. Shared by the standalone DocMDP API
    /// (<c>Tetitulz.Pdf.Base.Operations.PdfMdpVerifier</c>) and the signature-verification path
    /// (<c>Tetitulz.Pdf.Signing.PdfSignatureVerifier</c>) so there is a single implementation.
    ///
    /// <para>When a PDF is certified, the certification signature's
    /// <c>/Reference … /TransformParams /P</c> sets what may change afterwards:
    /// <list type="bullet">
    ///   <item><term>P=1</term><description>No changes (read-only).</description></item>
    ///   <item><term>P=2</term><description>Form-field values and signatures may be added.</description></item>
    ///   <item><term>P=3</term><description>Annotations may also be added/modified/deleted.</description></item>
    /// </list></para>
    ///
    /// <para>Enforcement follows the usual approach: the bytes the
    /// certification signature covers (<c>/ByteRange</c>) are exactly the document as certified, so
    /// the certified-revision prefix is re-parsed as a standalone revision and every object that a
    /// later incremental update added or rewrote is classified, by role, against the permitted
    /// level. Classification fails closed: an object change it cannot positively attribute to a
    /// permitted operation is reported as a violation. LTV additions (a <c>/DSS</c> dictionary and
    /// the revocation streams it references, and document timestamps) are permitted at every level,
    /// matching Adobe's "certification preserved for long-term validation" behaviour — <b>additions</b>:
    /// an object the certified revision already held is never LTV data, whatever the <c>/DSS</c>
    /// names (<see cref="BuildLtvAllowedSet"/>).</para>
    /// </summary>
    public static class DocMdpAnalyzer
    {

        /// <summary>
        /// Maximum depth of the shallow canonical serialisation used to compare an object's
        /// pre- and post-certification versions. References are not followed, so real nesting
        /// stays far below this; the guard only stops a self-nesting direct structure. Beyond
        /// it the serialiser emits a placeholder, which compares unequal to a shallower value
        /// and therefore fails closed (reported as changed).
        /// </summary>
        private const int MaxCanonicalDepth = 64;

        // Field keys whose change is a permitted "fill-in" of an existing form field
        // (value + appearance), as opposed to a structural alteration.
        private static readonly HashSet<string> FillInKeys = new HashSet<string>(StringComparer.Ordinal)
        { "V", "AP", "AS", "DV", "I", "RV", "TI" };

        // AcroForm keys that may legitimately change when a field/signature is added or filled. Not
        // /DA: with /NeedAppearances a reader redraws every field by it, the size and colour included.
        // /DR only gains names (ResourcesOnlyGrow): a name rebound to another font draws a signed
        // field's value in glyphs that say something else.
        private static readonly HashSet<string> AcroFormDeltaKeys = new HashSet<string>(StringComparer.Ordinal)
        { "Fields", "SigFlags", "DR", "NeedAppearances" };

        /// <summary>
        /// Analyses the DocMDP compliance of an already-open document. <paramref name="fileLength"/>
        /// is the total length of the underlying file (used to detect incremental updates).
        /// Never throws on malformed input — problems surface as violations / a non-compliant result.
        /// </summary>
        public static DocMdpAnalysis Analyze(PdfDocument doc, long fileLength)
            => Analyze(doc, doc, fileLength, null);

        /// <summary>
        /// Analyses DocMDP compliance where the <b>object graph</b> and the <b>byte offsets</b> come
        /// from different views of the same document.
        /// </summary>
        /// <param name="objectGraph">
        /// Where the certification signature, its <c>/P</c> and the post-certification object
        /// contents are read from. For an encrypted document this is a decrypted copy.
        /// </param>
        /// <param name="offsetSource">
        /// Whose <see cref="PdfDocument.CrossReferenceTable"/> and <see cref="PdfDocument.BaseStream"/>
        /// carry the offsets. <b>Always the original file</b>, encrypted or not.
        /// </param>
        /// <param name="fileLength">Length of <paramref name="offsetSource"/>'s stream.</param>
        /// <param name="openCertifiedRevision">
        /// Turns the certified revision's byte prefix into a readable document. <c>null</c> opens it
        /// directly, which is right whenever the prefix is not encrypted.
        /// </param>
        /// <remarks>
        /// <para><b>Why the two views have to be separable.</b> A DocMDP verdict is two answers:
        /// <i>is this certified and at what <c>/P</c></i>, which needs the object graph, and
        /// <i>has it been modified since</i>, which is decided by comparing cross-reference offsets
        /// against the certification <c>/ByteRange</c> and by reading the certified revision out of
        /// the stream. An encrypted document exposes no catalog, so the first needs decryption — and
        /// a decrypted copy is <b>re-serialised</b>, so its offsets are its own while the stored
        /// <c>/ByteRange</c> still indexes the original. Running the whole analysis on the copy
        /// gets the first answer right and the second silently wrong.</para>
        ///
        /// <para><b>What makes the split sound.</b> Three facts, none of them obvious:
        /// <c>PdfDocument.Load</c> parses the cross-reference table <i>before</i> it returns early
        /// on <c>/Encrypt</c>, so the offsets are available on the original with no decryption at
        /// all; <c>/ByteRange</c> is an array of numbers, which are never encrypted, so it reads the
        /// same from either view and always names the original's bytes; and decryption <b>preserves
        /// object numbers</b>, so an xref entry for object N names object N in the copy.</para>
        ///
        /// <para><b>The single-argument overload passes the same document for both</b>, which is
        /// exactly today's behaviour and is why the unencrypted path cannot drift: there is one
        /// analysis, not two.</para>
        /// </remarks>
        public static DocMdpAnalysis Analyze(
            PdfDocument objectGraph,
            PdfDocument offsetSource,
            long fileLength,
            Func<byte[], PdfDocument> openCertifiedRevision)
        {
            if (objectGraph == null) throw new ArgumentNullException(nameof(objectGraph));
            if (offsetSource == null) throw new ArgumentNullException(nameof(offsetSource));

            PdfDocument doc = objectGraph;
            var result = new DocMdpAnalysis { FileLength = fileLength };

            bool usedDefaultP = result.UsedDefaultP;
            var certSigDict = FindCertification(doc, out int p, out string fieldName,
                                                ref usedDefaultP, out bool declaredButUnreadable);
            result.UsedDefaultP = usedDefaultP;
            if (certSigDict == null)
            {
                result.HasCertification = false;
                result.CertificationDeclarationUnreadable = declaredButUnreadable;
                result.Summary = declaredButUnreadable
                    ? "This document's catalog declares /Perms /DocMDP, but the certification it " +
                      "names could not be read, so no certification could be checked. Treat this " +
                      "as a damaged or tampered file rather than as an uncertified one."
                    : "No DocMDP certification signature found in this document.";
                return result;
            }

            result.HasCertification = true;
            result.PermissionLevel = p;
            result.PermissionDescription = DescribePermission(p);
            result.CertFieldName = fieldName;
            result.CertSignature = certSigDict;

            return JudgeChangesSince(result, doc, offsetSource, GetByteRangeEnd(certSigDict, doc), fileLength, p,
                                     openCertifiedRevision, "certification");
        }

        /// <summary>
        /// What the revisions appended after a signature changed, judged by Table 257's rules at
        /// <paramref name="permission"/> — the analysis a certification signature gets, applied to an
        /// approval signature, which certifies nothing but still signed a document that was then
        /// changed.
        /// </summary>
        /// <remarks>
        /// <para><b>Why an approval signature needs this.</b> A shadow attack appends a revision that
        /// rewrites a page and leaves every earlier signature's CMS verifying over its own range. The
        /// verifier's document-level defence — "the furthest-reaching signature stops short of EOF" —
        /// could be suppressed by a decoy signature that reaches EOF, and a self-signed certificate
        /// verifies against itself for free. Trusting only anchored signatures to vouch was tried and
        /// refused conforming documents (a counter-signature under another CA, every PAdES B-LT file's
        /// document timestamp). <b>What the later revision changed</b> is the question that separates
        /// the attack from the ordinary case, and it is asked of each signature about its own range, so
        /// no other signature can answer for it.</para>
        ///
        /// <para><b>Level 3 is the level to ask at.</b> Table 257: form filling, instantiating page
        /// templates, signing, and annotation creation, deletion and modification are the changes a
        /// document is meant to take after it is signed; a page's content or the catalog's structure
        /// is not. A caller judging by another level passes it.</para>
        /// </remarks>
        /// <param name="doc">The document, whose <see cref="PdfDocument.BaseStream"/> holds the file as stored.</param>
        /// <param name="signedEnd">Where the signature's <c>/ByteRange</c> coverage ends.</param>
        /// <param name="fileLength">The file's length in bytes.</param>
        /// <param name="permission">The DocMDP level (Table 257) the later revisions are judged at.</param>
        public static DocMdpAnalysis AnalyzeChangesSince(PdfDocument doc, long signedEnd, long fileLength, int permission)
            => AnalyzeChangesSince(doc, doc, signedEnd, fileLength, permission, null);

        /// <summary>
        /// <see cref="AnalyzeChangesSince(PdfDocument, long, long, int)"/> with the object graph and
        /// the byte offsets read from different views of one file — the shape
        /// <see cref="Analyze(PdfDocument, PdfDocument, long, Func{byte[], PdfDocument})"/> takes for an
        /// encrypted document.
        /// </summary>
        public static DocMdpAnalysis AnalyzeChangesSince(
            PdfDocument objectGraph, PdfDocument offsetSource, long signedEnd, long fileLength, int permission,
            Func<byte[], PdfDocument> openSignedRevision)
        {
            if (objectGraph == null) throw new ArgumentNullException(nameof(objectGraph));
            if (offsetSource == null) throw new ArgumentNullException(nameof(offsetSource));

            var result = new DocMdpAnalysis
            {
                FileLength = fileLength,
                PermissionLevel = permission,
                PermissionDescription = DescribePermission(permission),
            };
            return JudgeChangesSince(result, objectGraph, offsetSource, signedEnd, fileLength, permission,
                                     openSignedRevision, "signature");
        }

        /// <summary>
        /// The one judgement of "what changed after the bytes this signature covers" — for the
        /// certification and for an approval signature alike, so the two cannot classify a change
        /// differently. <paramref name="signed"/> names the signature in the messages.
        /// </summary>
        private static DocMdpAnalysis JudgeChangesSince(
            DocMdpAnalysis result, PdfDocument doc, PdfDocument offsetSource, long signedEnd, long fileLength,
            int p, Func<byte[], PdfDocument> openSignedRevision, string signed)
        {
            result.CertByteRangeEnd = signedEnd;

            // A malformed / out-of-bounds byte range cannot be reasoned about — fail closed.
            if (signedEnd <= 0 || signedEnd > fileLength)
            {
                result.HasIncrementalUpdates = signedEnd > 0 && signedEnd < fileLength;
                result.IsCompliant = false;
                result.Violations.Add(
                    $"{signed} signature /ByteRange is missing, malformed, or out of bounds.");
                result.Summary = $"DocMDP P={p}: {signed} byte range is invalid; cannot confirm integrity.";
                return result;
            }

            // Not `signedEnd < fileLength`: §12.8.1 lets the range stop at %%EOF with one end-of-line
            // marker after it, and that marker is not an update.
            bool hasUpdates = !SignedByteRanges.ReachesEndOfFile(offsetSource.BaseStream, signedEnd, fileLength);
            result.HasIncrementalUpdates = hasUpdates;
            if (!hasUpdates)
            {
                result.IsCompliant = true;
                result.Summary = $"Document is unmodified since {signed} (P={p}).";
                return result;
            }

            ClassifyIncrementalUpdates(doc, offsetSource, signedEnd, p, result, openSignedRevision);

            result.IsCompliant = result.Violations.Count == 0;
            result.Summary = result.IsCompliant
                ? $"Document modifications comply with P={p} {signed} policy."
                : $"Document violates P={p} {signed} policy. " + string.Join(" ", result.Violations);
            return result;
        }

        // ---------------------------------------------------------------- locators

        private static PdfDictionary FindMdpViaPerms(
            PdfDocument doc, out int p, out string fieldName, out bool declaredButUnreadable)
        {
            fieldName = null;
            return CertificationNamedByPerms(
                doc.Catalog?.Dictionary, doc.ResolveObject, out p, out declaredButUnreadable);
        }

        /// <summary>
        /// The graph half of the verdict on its own: whether the document carries a DocMDP
        /// certification signature, and at what <c>/P</c>. No byte offset is consulted, so the
        /// answer is the same from a decrypted copy as from the original.
        /// </summary>
        /// <remarks>
        /// For a caller that only needs to know <i>is this certified</i> — the image replacer, which
        /// refuses a certified document unless told otherwise — this is the question to ask.
        /// Running <see cref="Analyze(PdfDocument, long)"/> for it computes a modification verdict
        /// against a length the caller then discards, and for a decrypted copy that verdict compares
        /// the original's <c>/ByteRange</c> against another file's bytes.
        /// </remarks>
        /// <param name="doc">The document whose object graph is read.</param>
        /// <param name="permission">The certification's <c>/P</c>, 1 to 3; 0 when there is none.</param>
        /// <param name="description">Table 257's wording for <paramref name="permission"/>; null when there is none.</param>
        internal static bool TryReadCertification(PdfDocument doc, out int permission, out string description)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            bool usedDefault = false;
            bool certified = FindCertification(doc, out permission, out _, ref usedDefault) != null;
            description = certified ? DescribePermission(permission) : null;
            return certified;
        }

        /// <summary>
        /// The certification signature: the one <c>/Perms /DocMDP</c> names in the catalog, else the
        /// first signature field carrying a <c>/Reference</c> whose transform method is
        /// <c>/DocMDP</c>. One search for the full analysis and for the graph-only question, so the
        /// two cannot find different signatures in the same document.
        /// </summary>
        private static PdfDictionary FindCertification(
            PdfDocument doc, out int p, out string fieldName, ref bool usedDefaultP)
            => FindCertification(doc, out p, out fieldName, ref usedDefaultP, out _);

        private static PdfDictionary FindCertification(
            PdfDocument doc, out int p, out string fieldName, ref bool usedDefaultP,
            out bool declaredButUnreadable)
        {
            PdfDictionary named = FindMdpViaPerms(doc, out p, out fieldName, out declaredButUnreadable);
            PdfDictionary listed = FindMdpViaSigFields(doc, out int listedP, out string listedName, ref usedDefaultP);
            if (listed == null) return named;

            // /Perms is a catalog entry, and a later revision's rewrite of the catalog can name a
            // signature of its own reaching the end of the file: "unmodified since certification".
            // A certification a signature field lists over fewer bytes came first, and is the one.
            if (named != null && !CoversLess(listed, named, doc)) return named;

            p = listedP;
            fieldName = listedName;
            return listed;
        }

        private static bool CoversLess(PdfDictionary a, PdfDictionary b, PdfDocument doc)
        {
            long endA = GetByteRangeEnd(a, doc), endB = GetByteRangeEnd(b, doc);
            return endA > 0 && (endB <= 0 || endA < endB);
        }

        /// <summary>
        /// The certification signature a catalog names through <c>/Perms /DocMDP</c> (Table 263), with
        /// its permission — or <c>null</c> and <c>0</c> when the catalog names none.
        /// </summary>
        /// <remarks>
        /// A <c>/DocMDP</c> entry whose signature carries no readable <c>/P</c> still certifies, at the
        /// default of 2. Shared with <c>Signing.SignatureOrdering</c>, which has to reach the same
        /// answer before it lets a new signature be written.
        /// </remarks>
        internal static PdfDictionary CertificationNamedByPerms(
            PdfDictionary catalog, Func<PdfObject, PdfObject> resolve, out int p)
            => CertificationNamedByPerms(catalog, resolve, out p, out _);

        /// <summary>
        /// As above, and separately reporting that the catalog <b>declared</b> a certification this
        /// could not read — the distinction between "this document is not certified" and "this
        /// document says it is certified and the file is damaged".
        /// </summary>
        /// <remarks>
        /// <para><b>The absent reading of a failed read grants something here</b>, which is what
        /// makes this one worth separating (GAPS.md §23). A <c>/Perms</c> that will not resolve, or
        /// a <c>/DocMDP</c> naming an object whose cross-reference entry is corrupt, produced
        /// exactly the answer an uncertified document produces — and "not certified" is the answer
        /// that permits modification, permits the next signature, and lets the image replacer
        /// proceed.</para>
        ///
        /// <para><b>The verdict deliberately does not change.</b> Falling through to the signature
        /// fields is correct: §12.8.2.2's certification lives in the signature's own
        /// <c>/Reference</c>, and <c>/Perms</c> is the catalog's pointer to it, so an unreadable
        /// pointer is not an unreadable certification. What changes is that the caller is told,
        /// instead of being handed "no certification found" about a file that says otherwise.</para>
        /// </remarks>
        internal static PdfDictionary CertificationNamedByPerms(
            PdfDictionary catalog, Func<PdfObject, PdfObject> resolve, out int p,
            out bool declaredButUnreadable)
        {
            p = 0;
            declaredButUnreadable = false;
            if (catalog == null) return null;

            PdfObject permsEntry = catalog[PdfName.Perms];
            var perms = resolve(permsEntry) as PdfDictionary ?? permsEntry as PdfDictionary;
            if (perms == null)
            {
                // Present but unreadable is a damaged file; absent is an ordinary uncertified one.
                declaredButUnreadable = permsEntry != null;
                return null;
            }

            var mdpRef = perms[PdfName.DocMDP];
            if (mdpRef == null) return null;

            var sigDict = resolve(mdpRef) as PdfDictionary ?? mdpRef as PdfDictionary;
            if (sigDict == null)
            {
                declaredButUnreadable = true;
                return null;
            }

            p = DocMdpPermissionOf(sigDict, resolve, out _);
            if (p == 0) p = 2; // a /Perms /DocMDP without a readable /P still certifies at the default level.
            return sigDict;
        }

        private static PdfDictionary FindMdpViaSigFields(PdfDocument doc, out int p, out string fieldName, ref bool usedDefaultP)
        {
            p = 0; fieldName = null;
            if (doc.AcroForm == null) return null;

            // The certification comes first (§12.8.1), so the one that covers least is the one that
            // counts: /Fields is a list a later revision may add to, at its head too, so the first
            // listed may be a later signature naming a kinder level over bytes it reaches the end of.
            PdfDictionary certification = null;
            long least = long.MaxValue;
            foreach (var field in doc.AcroForm.GetSignatureFields())
            {
                if (!field.HasSignature) continue;
                var sigDict = field.SignatureValue;
                if (sigDict == null) continue;

                int pp = DocMdpPermissionOf(sigDict, doc.ResolveObject, out bool defaulted);
                if (pp <= 0) continue;

                long end = GetByteRangeEnd(sigDict, doc);
                if (end <= 0) end = long.MaxValue - 1;

                // Two that cover the same are one signature and a copy of it, or a broken file: the
                // stricter is the one that counts, so a copy naming a kinder level changes nothing.
                if (end > least || (end == least && pp >= p)) continue;

                least = end;
                certification = sigDict;
                usedDefaultP = defaulted;
                p = pp;
                fieldName = field.ReportedName;
            }
            return certification;
        }

        /// <summary>
        /// The DocMDP permission a signature dictionary declares through a <c>/Reference</c> whose
        /// transform method is <c>/DocMDP</c> — <c>1</c> to <c>3</c> — or <c>0</c> when it declares
        /// none, which makes it an approval signature or a document timestamp.
        /// </summary>
        /// <remarks>
        /// <para><b>Resolver-shaped, because the signer asks too.</b> <c>PdfSigner</c> works over a
        /// bare <c>PdfParser</c> rather than a <see cref="PdfDocument"/>, and it has to know whether a
        /// document is already certified — ISO 32000-2 §12.8.1 allows "at most one certification
        /// signature" and requires approval signatures to "follow the certification signature" — before
        /// it writes a byte. Reading <c>/P</c> a second way there is how the two would come to
        /// disagree about the same document.</para>
        /// </remarks>
        /// <param name="sigDict">The signature dictionary.</param>
        /// <param name="resolve">Follows an indirect reference (§7.3.10); returns a direct object as is.</param>
        /// <param name="usedDefault">True when the reference names no <c>/P</c> and the default, 2, was taken.</param>
        internal static int DocMdpPermissionOf(PdfDictionary sigDict, Func<PdfObject, PdfObject> resolve, out bool usedDefault)
        {
            usedDefault = false;
            foreach (PdfDictionary sigRef in SignatureReferences(sigDict, resolve))
            {
                int pp = PermissionOfReference(sigRef, resolve, out usedDefault);
                if (pp > 0) return pp;
            }
            return 0;
        }

        /// <summary>
        /// The signature reference dictionaries a signature's <c>/Reference</c> names, in order.
        /// </summary>
        /// <remarks>
        /// <para><b>Table 252 says an array, and this also accepts a lone dictionary</b>, because
        /// producers write one and a reader that refuses it silently drops the transform the entry
        /// exists to declare.</para>
        ///
        /// <para><b>Shared because the two readers disagreed, and disagreed in the unsafe
        /// direction.</b> This analysis accepted both shapes; <see cref="FieldMdpAnalyzer"/>
        /// required an array. A file whose <c>/Reference</c> is a single dictionary therefore had
        /// its DocMDP certification honoured and its <b>FieldMDP lock silently ignored</b> — the
        /// lock is the thing that says a signed field may not change, so the strict reading failed
        /// <i>open</i> on the half that enforces, while the lenient one governed the half that
        /// grants. Neither analysis can be the place this is decided twice.</para>
        ///
        /// <para>A non-dictionary element is skipped rather than guessed at, and an element is
        /// resolved before being cast for the reason §7.3.10 gives: any of these may be indirect.</para>
        /// </remarks>
        internal static IEnumerable<PdfDictionary> SignatureReferences(
            PdfDictionary sigDict, Func<PdfObject, PdfObject> resolve)
        {
            PdfObject refObj = sigDict?[PdfName.Reference];
            if (refObj == null) yield break;

            PdfObject resolved = resolve(refObj);
            if (resolved is PdfArray refArr)
            {
                for (int i = 0; i < refArr.Count; i++)
                {
                    var item = resolve(refArr[i]) as PdfDictionary ?? refArr[i] as PdfDictionary;
                    if (item != null) yield return item;
                }
                yield break;
            }

            var single = resolved as PdfDictionary ?? refObj as PdfDictionary;
            if (single != null) yield return single;
        }

        private static int PermissionOfReference(PdfDictionary sigRef, Func<PdfObject, PdfObject> resolve, out bool usedDefault)
        {
            usedDefault = false;

            // PdfName's == is value equality, so a null /TransformMethod also falls out here — and
            // that is exactly why this read must RESOLVE. GetName tests the raw value, so a legal
            // /TransformMethod 12 0 R (§7.3.10) would return null, this 0, FindMdpViaSigFields no
            // certification signature, and Analyze "No DocMDP certification signature found in this
            // document."
            //
            // Every restriction the document declares then becomes invisible. This is the
            // fail-OPEN direction, on the analyser that decides whether a certified document's
            // permissions are enforced at all.
            if ((resolve(sigRef[PdfName.TransformMethod]) as PdfName) != PdfName.DocMDP) return 0;

            var tp = resolve(sigRef[PdfName.TransformParams]) as PdfDictionary
                     ?? sigRef[PdfName.TransformParams] as PdfDictionary;
            if (tp == null) return 0;

            if (resolve(tp[PdfName.P]) is PdfNumber pNum)
            {
                // Table 257: an integer, 1 to 3. Read as `(int)DoubleValue`, 2.5 would become level 2
                // and a value past int wrap; and returning 0 — "not a certification" — for a value
                // outside 1–3 would let a document that DECLARED DocMDP with a malformed level have
                // every restriction it asked for ignored, the fail-open direction. A level that is not one
                // of the three is the absent one: the default, 2, and usedDefault says so.
                if (pNum.IsInteger && pNum.LongValue >= 1 && pNum.LongValue <= 3)
                    return (int)pNum.LongValue;
            }
            usedDefault = true;
            return 2; // /P default per spec.
        }

        // ---------------------------------------------------------------- byte range

        /// <summary>
        /// Where a signature's coverage ends, or 0 for a range this analysis will not reason about.
        /// Shared with <see cref="FieldMdpAnalyzer"/>, which asks the same question of the same kind of
        /// dictionary and must refuse the same ranges.
        /// </summary>
        internal static long GetByteRangeEnd(PdfDictionary sigDict, PdfDocument doc)
        {
            // Resolved. Unlike the two reads above, this one's null path is fail-CLOSED — the
            // caller treats certEnd <= 0 as "cannot reason about this file" and records a
            // violation. So the defect here produced a FALSE non-compliance report on a valid
            // document rather than a false clean bill. Still worth fixing: a compliance channel
            // that cries wolf is one a caller learns to ignore, which is how the fail-open ones
            // above get missed.
            //
            // ONE reader, in SignedByteRanges. This copy resolved the ARRAY and then called
            // GetLong on the elements, which THROWS for anything that is not a PdfNumber — a
            // PdfNull, or the indirect reference §7.3.10 permits. Analyze's own doc comment
            // promises it "never throws on malformed input", and that exception escaped it,
            // straight out of a public API. Now such a file returns 0 and is reported as a
            // malformed certification range, which is what the comment above describes.
            if (!SignedByteRanges.TryRead(sigDict, doc, out var br, out _)) return 0;
            // Reject the same ill-formed ranges the signature verifier rejects so a crafted
            // ByteRange cannot understate the certified region.
            if (br.Offset1 != 0 || br.Length1 < 0 || br.Length2 < 0 || br.Offset2 < br.HoleStart) return 0;
            return br.CoverageEnd;
        }

        // ---------------------------------------------------------------- update classification

        private static void ClassifyIncrementalUpdates(
            PdfDocument doc, PdfDocument offsetSource, long certEnd, int p, DocMdpAnalysis result,
            Func<byte[], PdfDocument> openCertifiedRevision)
        {
            // Objects (re)written after the certified byte range are exactly the post-certification
            // additions/modifications. (Compressed/free entries carry no usable file offset; the
            // object stream that contains them is itself a Normal object caught here.)
            // Use >= certEnd: the first appended object starts at exactly the certified file's
            // end (= certEnd), and no pre-certification object can start at or beyond that offset.
            var changed = new List<int>();
            var freed = new List<int>();
            foreach (var e in offsetSource.CrossReferenceTable.AllEntries)
            {
                if (e.Type == CrossReferenceEntry.EntryType.Normal && e.Offset >= certEnd)
                    changed.Add(e.ObjectNumber);
                // A DELETION leaves no offset to compare — the later revision simply marks the
                // entry free. Collecting only Normal entries therefore made deleting an object
                // invisible: a certified P=1 document with its page object freed reported
                // IsCompliant = true, in a component whose own doc promises to fail closed.
                // Whether each of these was really present before is decided against the
                // certified revision below; an object freed in the certified revision and still
                // free now is not a change at all.
                else if (e.Type == CrossReferenceEntry.EntryType.Free && e.ObjectNumber > 0)
                    freed.Add(e.ObjectNumber);
            }
            changed.Sort();
            freed.Sort();

            // Recover the certified revision: its byte prefix is itself a complete PDF, giving the
            // pre-modification version of each changed object so a modification can be classified
            // (fill-in vs structural, /Annots-only vs content change, …), and its own tables, so a
            // change that writes no object is seen.
            PdfDocument certDoc = null;
            PdfDocument certTables = null;
            try
            {
                // The prefix is a prefix of the ORIGINAL file, so it is itself a complete document
                // of the same kind — encrypted if the original was. openCertifiedRevision is how the
                // caller decrypts it; null means "open it directly", which is right for a
                // plaintext original. Its tables are the original's own, whatever the decrypted copy
                // renumbers into offsets of its own, and an encrypted file's are readable undecrypted.
                byte[] prefix = ReadPrefix(offsetSource.BaseStream, certEnd);
                certTables = PdfDocument.Open(new MemoryStream(prefix, writable: false));
                certDoc = openCertifiedRevision != null ? openCertifiedRevision(prefix) : certTables;
            }
            catch (Exception ex)
            {
                certDoc = null;
                result.Violations.Add(
                    $"the signed revision could not be read ({ex.Message}); " +
                    "what changed since cannot be established");
            }

            try
            {
                if (certTables != null)
                    ClassifyTableChanges(certTables, offsetSource, certEnd, result);

                // A free entry is only a deletion if the certified revision had something there.
                // Read only if a change asks: it walks every page, and each signature opens a revision.
                var annotations = new Lazy<HashSet<int>>(() => certDoc != null ? AnnotationsPagesList(certDoc) : new HashSet<int>());
                if (certDoc != null)
                    ClassifyDeletions(certDoc, freed, p, annotations, result);

                // An object the signed revision names without holding was null when it was signed
                // (§7.3.10). A later revision that supplies it changes what the signed objects draw,
                // whatever its own keys say: page content behind a reference the page always carried
                // would otherwise pass as LTV data or as an appearance stream.
                var namedBySigned = new Lazy<HashSet<int>>(() => certDoc != null ? NamedBySigned(certDoc) : new HashSet<int>());

                int catalogObjNum = (doc.Trailer[PdfName.Root] as PdfReference)?.ReferenceObjectNumber ?? 0;
                int acroFormObjNum = (doc.Catalog?.Dictionary?[PdfName.AcroForm] as PdfReference)?.ReferenceObjectNumber ?? -1;
                var ltvAllowed = BuildLtvAllowedSet(doc);

                List<string> allowed = result.PermittedChanges;
                var typeSet = new HashSet<string>(StringComparer.Ordinal);

                foreach (int n in changed)
                {
                    // A changed object that cannot be read cannot be judged, and this analyzer
                    // fails closed: a null from a parse error read as "deleted" (or, for the
                    // certified copy, "added") would go through the permission ladder as such — at
                    // P=3 an unreadable rewrite of an annotation would be a permitted deletion. The
                    // slot belongs to the most recent GetObject, so each is read at once.
                    var newObj = doc.GetObject(n);
                    string unreadable = IsNull(newObj) ? Parsing.PdfParser.LastObjectParseError : null;
                    string revision = "current";
                    var oldObj = certDoc?.GetObject(n);
                    if (unreadable == null && certDoc != null && IsNull(oldObj))
                    {
                        unreadable = Parsing.PdfParser.LastObjectParseError;
                        revision = "certified";
                    }
                    if (unreadable != null)
                    {
                        result.Violations.Add(
                            $"obj {n}: its {revision} revision's object could not be read ({unreadable}); " +
                            "a change that cannot be read is not permitted");
                        continue;
                    }

                    string tn = GetObjectTypeName(newObj, doc);
                    if (tn != null && typeSet.Add(tn)) result.ModifiedObjectTypes.Add(tn);

                    if (certDoc != null && IsNull(oldObj) && !IsNull(newObj) && namedBySigned.Value.Contains(n))
                    {
                        result.Violations.Add(
                            $"obj {n}: the signed revision referred to it without holding it, and a later revision " +
                            "supplies it — a change to what the signed objects draw, not permitted at any level");
                        continue;
                    }

                    Classify(doc, certDoc, annotations, n, oldObj, newObj, p, catalogObjNum, acroFormObjNum, ltvAllowed,
                        result.Violations, allowed);
                }
            }
            finally
            {
                if (!ReferenceEquals(certDoc, certTables)) certDoc?.Dispose();
                certTables?.Dispose();
            }
        }

        /// <summary>
        /// What the bytes from <paramref name="revisionStart"/> on change besides one entry of the
        /// catalog — for a revision whose only business is that entry, as a notarization's is. Each
        /// thing it finds is said; empty means the document reads as it did at
        /// <paramref name="revisionStart"/>, the entry apart.
        /// </summary>
        /// <remarks>
        /// A notarized or otherwise hashed prefix says nothing of what follows it, and the revision that
        /// carries the proof is as appendable as any other: a rewritten page written beside the proof
        /// leaves the prefix's hash true and no bytes after the revision.
        /// </remarks>
        /// <param name="doc">The document, whose <see cref="PdfDocument.BaseStream"/> holds the file as stored.</param>
        /// <param name="revisionStart">Where the revision starts; the bytes before it are a complete document.</param>
        /// <param name="catalogEntry">The catalog entry the revision exists to write.</param>
        public static IReadOnlyList<string> ChangesBesidesCatalogEntry(PdfDocument doc, long revisionStart, PdfName catalogEntry)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (catalogEntry == null) throw new ArgumentNullException(nameof(catalogEntry));

            var found = new DocMdpAnalysis();
            PdfDocument before;
            try
            {
                before = PdfDocument.Open(new MemoryStream(ReadPrefix(doc.BaseStream, revisionStart), writable: false));
            }
            catch (Exception ex) when (ex is PdfException || ex is IOException || ex is ArgumentException)
            {
                found.Violations.Add($"the document before the revision could not be read ({ex.Message})");
                return found.Violations;
            }

            using (before)
            {
                ClassifyTableChanges(before, doc, revisionStart, found);

                int catalogNumber = (doc.Trailer?[PdfName.Root] as PdfReference)?.ReferenceObjectNumber ?? 0;
                int entryNumber = (doc.Catalog?.Dictionary?[catalogEntry] as PdfReference)?.ReferenceObjectNumber ?? 0;

                HashSet<string> changedKeys = ChangedKeys(before.Catalog?.Dictionary, doc.Catalog?.Dictionary);
                changedKeys.Remove(catalogEntry.Value);
                if (changedKeys.Count > 0) found.Violations.Add("catalog: " + JoinKeys(changedKeys) + " changed");

                foreach (CrossReferenceEntry e in doc.CrossReferenceTable.AllEntries)
                {
                    if (e.Type == CrossReferenceEntry.EntryType.Free)
                    {
                        CrossReferenceEntry was = before.CrossReferenceTable.GetEntry(e.ObjectNumber);
                        if (was != null && was.Type != CrossReferenceEntry.EntryType.Free)
                            found.Violations.Add($"obj {e.ObjectNumber}: freed");
                        continue;
                    }
                    if (e.Type != CrossReferenceEntry.EntryType.Normal || e.Offset < revisionStart) continue;
                    if (e.ObjectNumber == catalogNumber) continue;

                    PdfObject written = doc.GetObject(e.ObjectNumber);
                    bool isNew = IsNull(before.GetObject(e.ObjectNumber));
                    if (isNew && e.ObjectNumber == entryNumber && !(written is PdfStream)) continue;
                    if (isNew && written is PdfStream && NameEq(doc, (PdfStream)written, PdfName.Type, PdfName.XRef)) continue;
                    found.Violations.Add($"obj {e.ObjectNumber}: {(isNew ? "added" : "rewritten")}");
                }
            }
            return found.Violations;
        }

        /// <summary>
        /// The changes that write no object past the signature: a cross-reference entry that now names
        /// other bytes the signed revision holds — an earlier version of the object, or another in an
        /// object stream — and a trailer that names another catalog. The signature covers the old
        /// bytes, so the bytes verify, and no later object is there for the classifier to see.
        /// </summary>
        private static void ClassifyTableChanges(
            PdfDocument signed, PdfDocument current, long signedEnd, DocMdpAnalysis result)
        {
            foreach (CrossReferenceEntry e in current.CrossReferenceTable.AllEntries)
            {
                if (e.Type == CrossReferenceEntry.EntryType.Free) continue;
                if (e.Type == CrossReferenceEntry.EntryType.Normal && e.Offset >= signedEnd) continue;

                CrossReferenceEntry was = signed.CrossReferenceTable.GetEntry(e.ObjectNumber);
                if (was != null && SameEntry(was, e)) continue;

                result.Violations.Add(
                    $"obj {e.ObjectNumber}: a later cross-reference section points it at other signed bytes " +
                    $"({(was == null ? "none" : was.ToString())}, now {e}) — not a permitted change");
            }

            string root = Canonical(signed.Trailer?[PdfName.Root]);
            string rootNow = Canonical(current.Trailer?[PdfName.Root]);
            if (root != rootNow)
                result.Violations.Add($"trailer: /Root named {root}, a later revision names {rootNow} — not a permitted change");
        }

        private static bool SameEntry(CrossReferenceEntry a, CrossReferenceEntry b)
        {
            if (a.Type != b.Type) return false;
            return a.Type == CrossReferenceEntry.EntryType.Normal
                ? a.Offset == b.Offset && a.GenerationNumber == b.GenerationNumber
                : a.ObjectStreamNumber == b.ObjectStreamNumber && a.IndexInObjectStream == b.IndexInObjectStream;
        }

        /// <summary>Keys a signature dictionary never holds and an annotation, a field or an action does (Tables 255, 220, 166, 193).</summary>
        private static readonly PdfName[] AnnotationOrFieldKeys =
        {
            PdfName.Subtype, PdfName.Rect, PdfName.A, PdfName.AA, PdfName.FT, PdfName.T, PdfName.Kids,
            PdfName.Parent, PdfName.AP, PdfName.P,
        };

        private static bool CarriesAnnotationOrFieldKeys(PdfDictionary dict)
        {
            foreach (PdfName key in AnnotationOrFieldKeys)
                if (dict.ContainsKey(key)) return true;
            return false;
        }

        /// <summary>How deep <see cref="KeepsEveryEntry"/> compares: a /DSS, its /VRI, an entry, an array.</summary>
        private const int MaxLtvCompareDepth = 6;

        /// <summary>
        /// Whether <paramref name="now"/> (in <paramref name="doc"/>) keeps every entry of
        /// <paramref name="was"/> (in <paramref name="signed"/>): each key of a dictionary, each element
        /// of an array (in any order), the same object for a reference — or, for one replaced by another
        /// object, a dictionary that keeps every entry of the one replaced.
        /// </summary>
        private static bool KeepsEveryEntry(PdfDocument signed, PdfObject was, PdfDocument doc, PdfObject now, int depth)
        {
            if (was == null || was is PdfNull) return true;
            if (now == null || now is PdfNull || depth > MaxLtvCompareDepth) return false;
            if (was is PdfReference wasRef && now is PdfReference nowRef
                && wasRef.ReferenceObjectNumber == nowRef.ReferenceObjectNumber)
                return true;

            PdfObject oldValue = signed.ResolveObject(was);
            PdfObject newValue = doc.ResolveObject(now);
            if (oldValue is PdfStream || newValue is PdfStream) return false;   // evidence replaced by other evidence
            if (oldValue is PdfDictionary oldDict)
            {
                if (!(newValue is PdfDictionary newDict)) return false;
                foreach (var kvp in oldDict)
                    if (!KeepsEveryEntry(signed, kvp.Value, doc, newDict[kvp.Key], depth + 1)) return false;
                return true;
            }
            if (oldValue is PdfArray oldArray)
            {
                if (!(newValue is PdfArray newArray)) return false;
                var present = new HashSet<string>(StringComparer.Ordinal);
                foreach (PdfObject item in newArray) present.Add(Canonical(item));
                foreach (PdfObject item in oldArray)
                    if (!present.Contains(Canonical(item))) return false;
                return true;
            }
            return Canonical(oldValue) == Canonical(newValue);
        }

        /// <summary>
        /// The object numbers the signed revision's objects and trailer refer to — held or not: the
        /// caller asks it only of an object the signed revision did not hold.
        /// </summary>
        private static HashSet<int> NamedBySigned(PdfDocument signed)
        {
            var named = new HashSet<int>();
            var seen = new HashSet<object>(PdfReferenceEquality.Instance);
            var pending = new Stack<PdfObject>();
            pending.Push(signed.Trailer);
            foreach (CrossReferenceEntry e in signed.CrossReferenceTable.AllEntries)
                if (e.Type != CrossReferenceEntry.EntryType.Free && e.ObjectNumber > 0)
                    pending.Push(signed.GetObject(e.ObjectNumber));
            while (pending.Count > 0)
            {
                switch (pending.Pop())
                {
                    case PdfReference r:
                        named.Add(r.ReferenceObjectNumber);
                        break;
                    case PdfDictionary d when seen.Add(d):   // a stream's dictionary too
                        foreach (var kvp in d) pending.Push(kvp.Value);
                        break;
                    case PdfArray a when seen.Add(a):
                        foreach (PdfObject item in a) pending.Push(item);
                        break;
                }
            }
            return named;
        }

        /// <summary>
        /// The objects the signed revision's pages list in <c>/Annots</c>, widgets excepted: what is an
        /// annotation by its place in the document, which a dictionary's own keys are not — a rewrite
        /// chooses them. Empty when past <see cref="Constants.PdfLimits.MaxAnnotationsPerDocument"/> or
        /// unreadable, so an annotation change is then refused rather than guessed at.
        /// </summary>
        private static HashSet<int> AnnotationsPagesList(PdfDocument signed)
        {
            var annotations = new HashSet<int>();
            var arraysSeen = new HashSet<PdfArray>(PdfReferenceEquality.Instance);
            int listed = 0;
            try
            {
                foreach (PdfPage page in signed.Pages)
                {
                    if (!(signed.ResolveObject(page.Dictionary[PdfName.Annots]) is PdfArray list) || !arraysSeen.Add(list)) continue;
                    foreach (PdfObject item in list)
                    {
                        if (++listed > Constants.PdfLimits.MaxAnnotationsPerDocument) return new HashSet<int>();
                        if (item is PdfReference r
                            && signed.ResolveObject(r) is PdfDictionary d && !(d is PdfStream)
                            && !NameEq(signed, d, PdfName.Subtype, PdfName.Widget))
                            annotations.Add(r.ReferenceObjectNumber);
                    }
                }
            }
            catch (Exception ex) when (ex is PdfException || ex is InvalidOperationException)
            {
                return new HashSet<int>();
            }
            return annotations;
        }

        private static void Classify(PdfDocument doc, PdfDocument signed, Lazy<HashSet<int>> annotations, int n, PdfObject oldObj, PdfObject newObj,
            int p, int catalogObjNum, int acroFormObjNum, Dictionary<int, LtvPosition> ltvAllowed,
            List<string> violations, List<string> allowed)
        {
            // LTV / revocation data — permitted at every level, and only ever ADDED. An object the
            // certified revision held is judged as what it was, whatever the /DSS now names: an
            // allow-list blessing every object reachable from the catalog's /DSS before any other
            // classification would report compliant at P=1 a revision that rewrote the page's
            // content stream and listed it under /Certs. The one rewrite
            // LTV makes is of its own dictionaries — a refreshed /DSS or /VRI — which is what
            // MayBeRewrittenLtv admits.
            if (ltvAllowed.TryGetValue(n, out LtvPosition ltvPosition)
                && (IsNull(oldObj)
                    || (MayBeRewrittenLtv(ltvPosition, oldObj) && signed != null && KeepsEveryEntry(signed, oldObj, doc, newObj, 0))))
            {
                allowed.Add($"obj {n}: LTV/revocation data (/DSS)");
                return;
            }

            bool oldNull = IsNull(oldObj), newNull = IsNull(newObj);
            var oldD = oldObj as PdfDictionary;   // PdfStream derives from PdfDictionary
            var newD = newObj as PdfDictionary;
            var dict = newD ?? oldD;
            string change = oldNull ? "added" : newNull ? "deleted" : "modified";

            // A stream that existed is never a permitted change, and the keys below cannot say it is:
            // they are the rewrite's own to choose, and they do nothing to what a reader draws from its
            // bytes. A page's content stream rewritten under /Type /DocTimeStamp is still that page's
            // content. Nothing a level permits rewrites one: filling a field adds its appearance.
            if (!oldNull && (oldObj is PdfStream || newObj is PdfStream))
            {
                violations.Add($"obj {n}: a stream {change} after the signature — not permitted at any level");
                return;
            }

            // Catalog.
            if (n == catalogObjNum || NameEq(doc, dict, PdfName.Type, PdfName.Catalog))
            {
                ClassifyCatalog(doc, signed, oldD, newD, violations, allowed);
                return;
            }

            // AcroForm (indirect object).
            if (acroFormObjNum > 0 && n == acroFormObjNum)
            {
                if (AcroFormDeltaAllowed(doc, signed, oldD, newD))
                    allowed.Add($"obj {n}: AcroForm fields/flags updated");
                else
                    violations.Add($"obj {n}: AcroForm changed beyond field/flag additions ({JoinKeys(ChangedKeys(oldD, newD))})");
                return;
            }

            // Signature value dictionary (/Type /Sig|/DocTimeStamp, or /ByteRange + /Contents) — and
            // nothing else: a signature dictionary holds no annotation's, field's or action's keys, so
            // an added annotation or field carrying /ByteRange and /Contents is judged as what it is.
            if (IsSignatureValueDict(doc, dict) && !(dict is PdfStream) && !CarriesAnnotationOrFieldKeys(dict))
            {
                if (oldNull && IsDocTimeStamp(doc, dict)) { allowed.Add($"obj {n}: document timestamp ({change})"); return; }
                if (oldNull)
                {
                    if (p >= 2) allowed.Add($"obj {n}: approval signature added");
                    else violations.Add($"obj {n}: signature added — DocMDP P=1 permits no changes");
                }
                else violations.Add($"obj {n}: signature dictionary {change} after certification");
                return;
            }

            bool isWidget = NameEq(doc, dict, PdfName.Subtype, PdfName.Widget);
            bool isSigField = NameEq(doc, dict, PdfName.FT, PdfName.Sig);
            bool looksAnnot = dict != null &&
                (NameEq(doc, dict, PdfName.Type, PdfName.Annot)
                 || (dict.ContainsKey(PdfName.Subtype) && dict.ContainsKey(PdfName.Rect)));

            // Signature field / widget (signing an existing field or adding one).
            if (isSigField)
            {
                if (oldNull)
                {
                    bool ts = IsDocTimeStamp(doc, doc.ResolveObject(newD?[PdfName.V]) as PdfDictionary);

                    // /FT /Sig is the classifier's word for it and the writer's to choose: a field that
                    // runs an action when touched, or is drawn as another kind of annotation, is one of
                    // the changes this branch would otherwise wave through.
                    if (newD != null && (newD.ContainsKey(PdfName.A) || newD.ContainsKey(PdfName.AA)
                                         || (newD.ContainsKey(PdfName.Subtype) && !isWidget)))
                        violations.Add($"obj {n}: a signature field added with an action, or as another kind of annotation — not permitted");
                    // A signature is one field's: a second naming it reports the signer under another name.
                    else if (signed != null && newD?[PdfName.V] is PdfReference held && !IsNull(signed.GetObject(held.ReferenceObjectNumber)))
                        violations.Add($"obj {n}: a signature field added holding a signature the signed revision already held — not permitted");
                    else if (ts) allowed.Add($"obj {n}: document-timestamp field added");
                    else if (p >= 2) allowed.Add($"obj {n}: signature field added");
                    else violations.Add($"obj {n}: signature field added — DocMDP P=1 permits no changes");
                }
                else if (newNull)
                    violations.Add($"obj {n}: signature field deleted after certification");
                else
                    ClassifyFieldFillIn(signed, n, oldD, newD, p, violations, allowed);
                return;
            }

            // Annotation (non-widget): comments, highlights, etc.
            if (looksAnnot && !isWidget)
            {
                // An object that existed answers to what it was: an annotation is what a page listed.
                if (!oldNull && !annotations.Value.Contains(n))
                    violations.Add($"obj {n}: rewritten to look like an annotation, and no page listed it as one — not permitted");
                // A comment says something; a script runs, on the page's opening under /AA, and can
                // change what every signed field shows when the file is read.
                else if (newD != null && ActsWhenRead(doc, newD))
                    violations.Add($"obj {n}: an annotation with an action other than following a link — not permitted");
                else if (p >= 3) allowed.Add($"obj {n}: annotation {change}");
                else violations.Add($"obj {n}: annotation {change} — requires DocMDP P=3");
                return;
            }

            // Existing form-field / widget being filled in.
            // `dict` is null when the object is a non-dictionary (e.g. an array or a number) in
            // BOTH revisions — `IsNull` only treats a missing object or /Null as null, so an array
            // rewritten after certification reaches here with oldNull == newNull == false. Such an
            // object cannot be a form field, so it must fall through to the fail-closed default
            // rather than dereference null: every other guard on this path (NameEq,
            // IsAppearanceStream, looksAnnot) is already null-tolerant.
            if (!oldNull && !newNull && dict != null &&
                (isWidget || dict.ContainsKey(PdfName.FT) || dict.ContainsKey(PdfName.T)))
            {
                ClassifyFieldFillIn(signed, n, oldD, newD, p, violations, allowed);
                return;
            }

            // Page object.
            if (NameEq(doc, dict, PdfName.Type, PdfName.Page))
            {
                var ck = ChangedKeys(oldD, newD);
                ck.Remove("Annots");
                if (ck.Count == 0)
                {
                    if (p >= 3 || (p == 2 && AnnotsOnlyGrowByNewObjects(doc, signed, oldD, newD)))
                        allowed.Add($"obj {n}: page /Annots updated");
                    else if (p == 2)
                        violations.Add($"obj {n}: page /Annots dropped an entry or listed an object the signed revision held — requires DocMDP P=3");
                    else violations.Add($"obj {n}: page annotations changed — DocMDP P=1 permits no changes");
                }
                else
                    violations.Add($"obj {n}: page modified ({JoinKeys(ck)}) — content/structure change not permitted");
                return;
            }

            // New annotation/field appearance stream backing a permitted change.
            if (oldNull && IsAppearanceStream(doc, dict))
            {
                if (p >= 2) allowed.Add($"obj {n}: appearance stream added");
                else violations.Add($"obj {n}: appearance stream added — DocMDP P=1 permits no changes");
                return;
            }

            // Anything else is, by default, a violation (conservative / secure default).
            violations.Add($"obj {n}: object {change} — not permitted under DocMDP P={p}");
        }

        /// <summary>
        /// Whether a page's <c>/Annots</c> kept every entry and adds only objects written after the
        /// signature, each then classified for itself. Dropping an entry deletes an annotation with no
        /// object freed; listing one the signed bytes hold draws what no classification will see.
        /// </summary>
        private static bool AnnotsOnlyGrowByNewObjects(PdfDocument doc, PdfDocument signed, PdfDictionary oldD, PdfDictionary newD)
        {
            if (signed == null) return false;
            var kept = new HashSet<string>(StringComparer.Ordinal);
            if (signed.ResolveObject(oldD?[PdfName.Annots]) is PdfArray before)
                foreach (PdfObject item in before) kept.Add(Canonical(item));

            if (!(doc.ResolveObject(newD?[PdfName.Annots]) is PdfArray after)) return kept.Count == 0;
            foreach (PdfObject item in after)
            {
                if (kept.Remove(Canonical(item))) continue;
                if (!(item is PdfReference r) || !IsNull(signed.GetObject(r.ReferenceObjectNumber))) return false;
            }
            return kept.Count == 0;
        }

        /// <summary>
        /// Whether an annotation does more than a link: any <c>/AA</c>, or an <c>/A</c> that is not a
        /// lone <c>/URI</c> or <c>/GoTo</c>. <c>/Next</c> chains another action on.
        /// </summary>
        private static bool ActsWhenRead(PdfDocument doc, PdfDictionary annotation)
        {
            if (annotation.ContainsKey(PdfName.AA)) return true;
            if (!annotation.ContainsKey(PdfName.A)) return false;
            if (!(doc.ResolveObject(annotation[PdfName.A]) is PdfDictionary action) || action.ContainsKey(PdfName.Next)) return true;
            return !NameEq(doc, action, PdfName.S, PdfName.URI) && !NameEq(doc, action, PdfName.S, PdfName.GoTo);
        }

        private static void ClassifyCatalog(PdfDocument doc, PdfDocument signed, PdfDictionary oldD, PdfDictionary newD,
            List<string> violations, List<string> allowed)
        {
            foreach (string k in ChangedKeys(oldD, newD))
            {
                switch (k)
                {
                    case "DSS":
                        // LTV data is only ever added: a /DSS that loses a certificate, a response or a
                        // list the signed one held hides what the verifier would have read.
                        if (oldD?[PdfName.DSS] == null || signed == null
                            || KeepsEveryEntry(signed, oldD[PdfName.DSS], doc, newD?[PdfName.DSS], 0))
                            allowed.Add("catalog: /DSS (LTV) added");
                        else
                            violations.Add("catalog: /DSS dropped LTV data the signed revision held — it is only ever added");
                        break;
                    case "Extensions":
                    case "Version":
                        allowed.Add($"catalog: /{k} updated");
                        break;
                    case "AcroForm":
                        var oa = oldD?[PdfName.AcroForm];
                        var na = newD?[PdfName.AcroForm];
                        if (na is PdfReference nr && (signed == null || !IsNull(signed.GetObject(nr.ReferenceObjectNumber))))
                        {
                            // Another form the signed bytes already hold: written by no later revision,
                            // so no classification would see it.
                            violations.Add("catalog: /AcroForm now names another object the signed revision held");
                            break;
                        }

                        // The form that was, inline or indirect, against the form that is: the indirect
                        // object's own classification sees it only as added, with nothing to compare.
                        var od = (signed?.ResolveObject(oa) ?? oa) as PdfDictionary;
                        var nd = doc.ResolveObject(na) as PdfDictionary ?? na as PdfDictionary;
                        if (nd != null && AcroFormDeltaAllowed(doc, signed, od, nd))
                            allowed.Add("catalog: /AcroForm fields/flags updated");
                        else
                            violations.Add($"catalog: /AcroForm changed beyond field/flag additions ({JoinKeys(ChangedKeys(od, nd))})");
                        break;
                    case "Perms":
                        violations.Add("catalog: /Perms modified after certification");
                        break;
                    case "BlockchainProof":
                        // A proof that travels with the document (an anchoring transaction's hash over
                        // the signed bytes) is evidence about the signature, as /DSS is: added once, in
                        // the proof's shape, never changed or removed.
                        if (oldD?[PdfName.BlockchainProof] == null && HasBlockchainProofShape(doc, newD?[PdfName.BlockchainProof]))
                            allowed.Add("catalog: /BlockchainProof added");
                        else
                            violations.Add("catalog: /BlockchainProof changed — a proof is only ever added, in its own shape");
                        break;
                    default:
                        violations.Add($"catalog: /{k} changed — not permitted");
                        break;
                }
            }
        }

        /// <summary>
        /// Whether <paramref name="value"/> is a blockchain proof dictionary: a transaction hash and the
        /// document hash it anchors, both strings — the two entries every proof the plugin writes carries.
        /// </summary>
        private static bool HasBlockchainProofShape(PdfDocument doc, PdfObject value)
            => doc.ResolveObject(value) is PdfDictionary proof
               && doc.ResolveObject(proof[PdfName.TxHash]) is PdfString
               && doc.ResolveObject(proof[PdfName.DocumentHash]) is PdfString;

        private static void ClassifyFieldFillIn(PdfDocument signed, int n, PdfDictionary oldD, PdfDictionary newD,
            int p, List<string> violations, List<string> allowed)
        {
            // /V is a fill-in key, and on a field already signed the value is the signature: rewriting it
            // takes a genuine signature out of the document and can put a decoy in its place. The value's
            // own shape decides, since a widget may take its /FT from its parent.
            if (signed != null && signed.ResolveObject(oldD?[PdfName.V]) is PdfDictionary oldValue
                && IsSignatureValue(oldValue, signed.ResolveObject)
                && Canonical(oldD[PdfName.V]) != Canonical(newD?[PdfName.V]))
            {
                violations.Add($"obj {n}: a signed field's signature was replaced — not a permitted fill-in");
                return;
            }

            // A signature the signed revision held, put into a field that did not hold it, reports that
            // field signed under the first field's signer: a new signature is a new object, as the rule
            // for an added signature field holds too.
            if (signed != null && newD?[PdfName.V] is PdfReference newValue
                && Canonical(oldD?[PdfName.V]) != Canonical(newValue)
                && signed.ResolveObject(newValue) is PdfDictionary heldValue
                && IsSignatureValue(heldValue, signed.ResolveObject))
            {
                violations.Add($"obj {n}: a field's value set to a signature the signed revision already held — not a permitted fill-in");
                return;
            }

            var ck = ChangedKeys(oldD, newD);

            // A signed field's appearance is the name and date a reader takes for the signature's.
            if (ck.Count > 0 && signed != null && HoldsASignature(signed, oldD))
            {
                violations.Add($"obj {n}: a signed field changed after signing ({JoinKeys(ck)}) — not a permitted fill-in");
                return;
            }

            ck.ExceptWith(FillInKeys);
            if (ck.Count == 0)
            {
                if (p >= 2) allowed.Add($"obj {n}: form field fill-in");
                else violations.Add($"obj {n}: form field filled — DocMDP P=1 permits no changes");
            }
            else
                violations.Add($"obj {n}: form field changed structurally ({JoinKeys(ck)}) — not a permitted fill-in");
        }

        /// <summary>Whether a field node, or a field above a widget, held a signature in the signed revision: the value inherits down (§12.7.3.1), read a bounded way up.</summary>
        private static bool HoldsASignature(PdfDocument signed, PdfDictionary node)
        {
            for (int step = 0; step < MaxFieldAncestorsRead && node != null; step++)
            {
                if (signed.ResolveObject(node[PdfName.V]) is PdfDictionary value && IsSignatureValue(value, signed.ResolveObject)) return true;
                node = signed.ResolveObject(node[PdfName.Parent]) as PdfDictionary;
            }
            return false;
        }

        /// <summary>How far up a field tree a value is looked for; a file can make the chain any length, or a loop.</summary>
        private const int MaxFieldAncestorsRead = 64;

        private static bool AcroFormDeltaAllowed(PdfDocument doc, PdfDocument signed, PdfDictionary oldD, PdfDictionary newD)
        {
            var ck = ChangedKeys(oldD, newD);
            ck.ExceptWith(AcroFormDeltaKeys);
            if (ck.Count != 0) return false;

            // /Fields only grows: a field dropped from it is no fill-in, though its object is untouched.
            if (oldD == null || signed == null) return oldD == null;
            var kept = new HashSet<string>(StringComparer.Ordinal);
            if (signed.ResolveObject(oldD[PdfName.Fields]) is PdfArray before)
                foreach (PdfObject item in before) kept.Add(Canonical(item));
            if (doc.ResolveObject(newD?[PdfName.Fields]) is PdfArray after)
                foreach (PdfObject item in after) kept.Remove(Canonical(item));
            return kept.Count == 0 && ResourcesOnlyGrow(doc, signed, oldD[PdfName.DR], newD?[PdfName.DR])
                   && SignatureFlagsOnlyGrow(doc, signed, oldD[PdfName.SigFlags], newD?[PdfName.SigFlags]);
        }

        /// <summary>
        /// Whether <c>/SigFlags</c> keeps every bit it had (Table 225): a reader told that no signature
        /// exists need not show one, and a file told it may be rewritten need not stay appendable.
        /// </summary>
        private static bool SignatureFlagsOnlyGrow(PdfDocument doc, PdfDocument signed, PdfObject oldFlags, PdfObject newFlags)
        {
            int before = (signed.ResolveObject(oldFlags) as PdfNumber)?.FlagWord ?? 0;
            int after = (doc.ResolveObject(newFlags) as PdfNumber)?.FlagWord ?? 0;
            return (before & ~after) == 0;
        }

        /// <summary>Whether each category of a form's default resources keeps every name the signed revision gave it, naming what it named.</summary>
        private static bool ResourcesOnlyGrow(PdfDocument doc, PdfDocument signed, PdfObject oldResources, PdfObject newResources)
        {
            if (!(signed.ResolveObject(oldResources) is PdfDictionary before)) return true;
            if (!(doc.ResolveObject(newResources) is PdfDictionary after)) return false;

            foreach (var category in before)
            {
                if (!(signed.ResolveObject(category.Value) is PdfDictionary oldNames))
                {
                    if (Canonical(category.Value) != Canonical(after[category.Key])) return false;
                    continue;
                }

                if (!(doc.ResolveObject(after[category.Key]) is PdfDictionary newNames)) return false;
                foreach (var name in oldNames)
                    if (Canonical(name.Value) != Canonical(newNames[name.Key])) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- LTV (/DSS) allowed set

        /// <summary>Where in the <c>/DSS</c> an allowed object stands — which decides what shape it may have.</summary>
        private enum LtvPosition
        {
            /// <summary>The <c>/DSS</c> dictionary itself.</summary>
            Dss,
            /// <summary>The <c>/VRI</c> dictionary: one entry per signature, keyed by the hex SHA-1 of its <c>/Contents</c>.</summary>
            VriContainer,
            /// <summary>One signature's validation-related information (Table 263).</summary>
            VriEntry,
            /// <summary>A certificate, OCSP response, CRL or timestamp stream.</summary>
            EvidenceStream,
            /// <summary>The indirect <c>/Length</c> of such a stream.</summary>
            StreamLength,
        }

        /// <summary>The keys a <c>/DSS</c> dictionary holds (ISO 32000-2 Table 261).</summary>
        private static readonly HashSet<string> DssKeys = new HashSet<string>(StringComparer.Ordinal)
        { "Type", "VRI", "Certs", "OCSPs", "CRLs" };

        /// <summary>The keys a <c>/VRI</c> entry holds (ISO 32000-2 Table 263).</summary>
        private static readonly HashSet<string> VriEntryKeys = new HashSet<string>(StringComparer.Ordinal)
        { "Type", "Cert", "CRL", "OCSP", "TU", "TS" };

        /// <summary>
        /// The keys a stream of evidence may carry — §7.3.8.2's stream keys and a <c>/Type</c> —
        /// so that nothing with a page's, an annotation's or a field's keys passes as one.
        /// </summary>
        private static readonly HashSet<string> EvidenceStreamKeys = new HashSet<string>(StringComparer.Ordinal)
        { "Type", "Length", "Filter", "DecodeParms", "F", "FFilter", "FDecodeParms", "DL" };

        /// <summary>
        /// The objects at the positions LTV data occupies under the catalog's <c>/DSS</c>, each with
        /// its position: the <c>/DSS</c> dictionary; the streams its <c>/Certs</c>, <c>/OCSPs</c> and
        /// <c>/CRLs</c> name; its <c>/VRI</c> dictionary, that one's entries, and the streams their
        /// <c>/Cert</c>, <c>/OCSP</c>, <c>/CRL</c> and <c>/TS</c> name; and an indirect
        /// <c>/Length</c> of any such stream. An object is listed only when it has the shape its
        /// position calls for — a dictionary of the table's keys, a stream of stream keys alone, a
        /// <c>/VRI</c> whose keys are hex digests — and a reference under any other key is not followed.
        /// </summary>
        /// <remarks>
        /// <para>Not <b>every</b> object reachable from the <c>/DSS</c> through any key: admitted
        /// before anything else, those would let a revision appended after a P=1 certification
        /// rewrite the page's content stream, add <c>/DSS &lt;&lt; /Certs [4 0 R] &gt;&gt;</c> to the
        /// catalog, and have the analysis — the one <c>PdfSignatureVerifier</c> reports — call the
        /// rewritten page compliant; a new annotation under <c>/VRI</c> would pass at P=2 the same
        /// way. A depth bounds a walk, not what it admits.</para>
        /// <para>The position is what <see cref="Classify"/> needs as well as the number: a changed
        /// object is LTV only when its old version had the same position's dictionary shape — a
        /// refreshed <c>/DSS</c> — and a stream never is.</para>
        /// </remarks>
        private static Dictionary<int, LtvPosition> BuildLtvAllowedSet(PdfDocument doc)
        {
            var set = new Dictionary<int, LtvPosition>();
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return set;

            PdfObject dssEntry = catalog[PdfName.DSS];
            if (!(doc.ResolveObject(dssEntry) is PdfDictionary dss) || dss is PdfStream || !KeysWithin(dss, DssKeys))
                return set;
            AddReferenced(set, dssEntry, LtvPosition.Dss);

            foreach (var kv in dss)
            {
                switch (kv.Key.Value)
                {
                    case "Certs":
                    case "OCSPs":
                    case "CRLs":
                        AddEvidenceStreams(doc, kv.Value, set);
                        break;
                    case "VRI":
                        if (!(doc.ResolveObject(kv.Value) is PdfDictionary vri) || vri is PdfStream || !HasOnlyDigestKeys(vri))
                            break;
                        AddReferenced(set, kv.Value, LtvPosition.VriContainer);
                        foreach (var entry in vri)
                        {
                            if (!(doc.ResolveObject(entry.Value) is PdfDictionary vriEntry) || vriEntry is PdfStream
                                || !KeysWithin(vriEntry, VriEntryKeys))
                                continue;
                            AddReferenced(set, entry.Value, LtvPosition.VriEntry);
                            foreach (var evidence in vriEntry)
                            {
                                switch (evidence.Key.Value)
                                {
                                    case "Cert":
                                    case "OCSP":
                                    case "CRL":
                                        AddEvidenceStreams(doc, evidence.Value, set);
                                        break;
                                    case "TS":
                                        AddEvidenceStream(doc, evidence.Value, set);
                                        break;
                                }
                            }
                        }
                        break;
                }
            }
            return set;
        }

        /// <summary>Whether a changed object at <paramref name="position"/> may be LTV's own rewrite: a refreshed dictionary of the same shape. A stream never is.</summary>
        private static bool MayBeRewrittenLtv(LtvPosition position, PdfObject oldObj)
        {
            if (!(oldObj is PdfDictionary old) || old is PdfStream) return false;
            switch (position)
            {
                case LtvPosition.Dss: return KeysWithin(old, DssKeys);
                case LtvPosition.VriContainer: return HasOnlyDigestKeys(old);
                case LtvPosition.VriEntry: return KeysWithin(old, VriEntryKeys);
                default: return false;
            }
        }

        /// <summary>The streams an evidence array names (§12.8.4.3: an array of indirect references to streams).</summary>
        private static void AddEvidenceStreams(PdfDocument doc, PdfObject arrayEntry, Dictionary<int, LtvPosition> set)
        {
            if (!(doc.ResolveObject(arrayEntry) is PdfArray array)) return;
            foreach (var element in array) AddEvidenceStream(doc, element, set);
        }

        /// <summary>One evidence stream — a stream, reached by reference, with no keys but a stream's — and its indirect <c>/Length</c>.</summary>
        private static void AddEvidenceStream(PdfDocument doc, PdfObject entry, Dictionary<int, LtvPosition> set)
        {
            if (!(entry is PdfReference reference)) return;
            if (!(doc.ResolveObject(reference) is PdfStream stream) || !KeysWithin(stream, EvidenceStreamKeys)) return;
            set[reference.ReferenceObjectNumber] = LtvPosition.EvidenceStream;
            if (stream[PdfName.Length] is PdfReference lengthRef && doc.ResolveObject(lengthRef) is PdfNumber)
                set[lengthRef.ReferenceObjectNumber] = LtvPosition.StreamLength;
        }

        private static void AddReferenced(Dictionary<int, LtvPosition> set, PdfObject entry, LtvPosition position)
        {
            if (entry is PdfReference r) set[r.ReferenceObjectNumber] = position;
        }

        private static bool KeysWithin(PdfDictionary d, HashSet<string> allowed)
        {
            foreach (var kv in d)
                if (!allowed.Contains(kv.Key.Value)) return false;
            return true;
        }

        /// <summary>
        /// Whether every key is a hex digest — §12.8.4.3's <c>/VRI</c> keys are the base-16 SHA-1 of a
        /// signature's <c>/Contents</c>, forty digits. Either case is taken; a dictionary with any other
        /// key is some other dictionary.
        /// </summary>
        private static bool HasOnlyDigestKeys(PdfDictionary d)
        {
            foreach (var kv in d)
            {
                string key = kv.Key.Value;
                if (key.Length != 40) return false;
                foreach (char c in key)
                    if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- object predicates

        private static bool IsSignatureValueDict(PdfDocument doc, PdfDictionary d)
            => IsSignatureValue(d, doc.ResolveObject);

        /// <summary>
        /// Whether a dictionary is a signature field's value: <c>/Type /Sig</c> or
        /// <c>/DocTimeStamp</c>, or — <c>/Type</c> being optional — a <c>/ByteRange</c> with a
        /// <c>/Contents</c>.
        /// </summary>
        /// <remarks>
        /// One reading, in <see cref="SignatureDictionaryShape"/>: the encryption layer answers the
        /// same question to decide which <c>/Contents</c> §7.6.2 leaves in the clear, and it must
        /// classify exactly the dictionaries this analysis does.
        /// </remarks>
        internal static bool IsSignatureValue(PdfDictionary d, Func<PdfObject, PdfObject> resolve)
            => SignatureDictionaryShape.IsSignatureValue(d, resolve);

        private static bool IsDocTimeStamp(PdfDocument doc, PdfDictionary d)
            => IsDocumentTimestamp(d, doc.ResolveObject);

        /// <summary>
        /// Whether a signature dictionary is a document timestamp (§12.8.5): <c>/Type /DocTimeStamp</c>
        /// or <c>/SubFilter /ETSI.RFC3161</c>, either possibly written indirectly.
        /// </summary>
        /// <remarks>
        /// A document timestamp is neither a certification nor an approval signature, and ISO 32000-2
        /// §12.8.1 places no order on it: "any number of document timestamp signatures". Adding one is
        /// not a change under any DocMDP level (Table 257).
        /// </remarks>
        internal static bool IsDocumentTimestamp(PdfDictionary d, Func<PdfObject, PdfObject> resolve)
        {
            if (d == null) return false;
            return (resolve(d[PdfName.Type]) as PdfName)?.Value == "DocTimeStamp"
                || (resolve(d[PdfName.SubFilter]) as PdfName)?.Value == "ETSI.RFC3161";
        }

        private static bool IsAppearanceStream(PdfDocument doc, PdfDictionary d)
            => d is PdfStream && (NameEq(doc, d, PdfName.Type, PdfName.XObject)
                                  || NameEq(doc, d, PdfName.Subtype, PdfName.Form));

        // PdfName's == is value equality, so an absent key (null) never matches a named value.
        private static bool NameEq(PdfDocument doc, PdfDictionary d, PdfName key, PdfName value)
            => ResolvedName(doc, d, key) == value;

        /// <summary>A name-valued entry, following an indirect reference (§7.3.10).</summary>
        /// <remarks>
        /// <para><b>Every classifier here read names raw</b>, through <c>GetName</c>, which returns
        /// only a direct name. Mostly that produced false <i>violations</i> — a signature field whose
        /// <c>/FT</c> was <c>9 0 R</c>, an appearance stream whose <c>/Subtype</c> was, or a document
        /// timestamp whose <c>/Type</c> was, reported a certified document as modified beyond its
        /// permissions.</para>
        ///
        /// <para><b>But not only.</b> A widget is deliberately kept out of the "annotation" category,
        /// and the check for it was one of these raw reads: an added widget whose <c>/Subtype</c> was
        /// indirect failed it, fell into the annotation branch through its <c>/Subtype</c> and
        /// <c>/Rect</c> keys, and was <b>allowed</b> at P=3 — where adding a form widget is not.</para>
        /// </remarks>
        private static PdfName ResolvedName(PdfDocument doc, PdfDictionary d, PdfName key)
        {
            PdfObject raw = d?[key];
            if (raw == null) return null;
            return (doc?.ResolveObject(raw) ?? raw) as PdfName;
        }

        private static bool IsNull(PdfObject o) => o == null || o is PdfNull;

        // ---------------------------------------------------------------- shallow canonical compare
        // References are NOT followed (objects compared shallowly) so a change to one object does
        // not cascade through the whole graph. Dictionary keys are sorted so a byte-identical-but-
        // reordered serialisation compares equal.

        private static HashSet<string> ChangedKeys(PdfDictionary oldD, PdfDictionary newD)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (oldD != null) foreach (var kv in oldD) keys.Add(kv.Key.Value);
            if (newD != null) foreach (var kv in newD) keys.Add(kv.Key.Value);
            foreach (string k in keys)
            {
                // PdfDictionary's string-keyed indexer — the keys already are the raw name
                // strings, so wrapping each one back into a PdfName only to unwrap it again
                // inside the indexer allocates once per key of every changed object.
                if (Canonical(oldD?[k]) != Canonical(newD?[k]))
                    result.Add(k);
            }
            return result;
        }

        /// <summary>
        /// A value's shallow canonical form — references named, not followed — so two revisions' copies
        /// of an entry compare equal exactly when a reader would see the same value. Shared with
        /// <see cref="FieldMdpAnalyzer"/>, which compares locked fields' <c>/V</c> the same way.
        /// </summary>
        internal static string Canonical(PdfObject obj)
        {
            var sb = new StringBuilder(64);
            WriteCanonical(obj, sb, 0);
            return sb.ToString();
        }

        private static void WriteCanonical(PdfObject obj, StringBuilder sb, int depth)
        {
            if (depth > MaxCanonicalDepth) { sb.Append('~'); return; }
            switch (obj)
            {
                case null:
                case PdfNull:
                    sb.Append("null"); break;
                case PdfBoolean b:
                    sb.Append(b.Value ? "true" : "false"); break;
                case PdfNumber n:
                    sb.Append(n.ToString()); break;
                case PdfName nm:
                    sb.Append('/').Append(nm.Value); break;
                case PdfString s:
                    sb.Append('<');
                    foreach (byte by in s.RawBytes) sb.Append(by.ToString("x2"));
                    sb.Append('>'); break;
                case PdfReference r:
                    sb.Append('R').Append(r.ReferenceObjectNumber).Append('_').Append(r.ReferenceGenerationNumber); break;
                case PdfStream st:
                    sb.Append("S<<");
                    WriteDictCanonical(st, sb, depth);
                    sb.Append(">>:").Append(HashBytes(st.RawData));
                    break;
                case PdfArray a:
                    sb.Append('[');
                    foreach (var it in a) { WriteCanonical(it, sb, depth + 1); sb.Append(' '); }
                    sb.Append(']'); break;
                case PdfDictionary d:
                    sb.Append("<<");
                    WriteDictCanonical(d, sb, depth);
                    sb.Append(">>"); break;
                default:
                    sb.Append(obj.GetType().Name); break;
            }
        }

        private static void WriteDictCanonical(PdfDictionary d, StringBuilder sb, int depth)
        {
            var keys = new List<string>(d.Count);
            foreach (var kv in d) keys.Add(kv.Key.Value);
            keys.Sort(StringComparer.Ordinal);
            foreach (string k in keys)
            {
                sb.Append('/').Append(k).Append(' ');
                WriteCanonical(d[k], sb, depth + 1);
                sb.Append(';');
            }
        }

        private static string HashBytes(byte[] data)
        {
            if (data == null) return "0";
            using var sha = SHA256.Create();
            byte[] h = sha.ComputeHash(data);
            var sb = new StringBuilder(64);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static string JoinKeys(IEnumerable<string> keys) => string.Join(",", keys);

        /// <summary>The first <paramref name="count"/> bytes of the file — a signed revision, itself a complete document.</summary>
        internal static byte[] ReadPrefix(Stream stream, long count)
        {
            byte[] buffer = new byte[count];
            stream.Position = 0;
            if (Tetitulz.Pdf.Core.IO.StreamBytes.Fill(stream, buffer, 0, buffer.Length) < buffer.Length)
                throw new IOException("Unexpected end of stream while reading certified prefix.");
            return buffer;
        }

        // ---------------------------------------------------------------- deletions

        /// <summary>
        /// Classifies objects the certified revision defined and a later revision freed.
        /// </summary>
        /// <remarks>
        /// <para>Deletion is the one edit that leaves no evidence in the modification pass: a freed
        /// entry has no file offset, so <see cref="ClassifyIncrementalUpdates"/>'s
        /// <c>Offset &gt;= certEnd</c> test never sees it. That made "delete the page object" a
        /// compliant change at <b>every</b> permission level.</para>
        /// <para><b>Only P=3 permits any deletion at all</b>, and only of an annotation a page lists:
        /// §12.8.2.2 Table 254 allows annotation creation, deletion and modification there.
        /// P=1 permits no change, and P=2 permits form fill-in and signing — neither of which
        /// requires freeing an object, so a deletion under either is reported. An object whose old
        /// value cannot be read is reported rather than excused, per this analyzer's fail-closed
        /// contract.</para>
        /// </remarks>
        private static void ClassifyDeletions(PdfDocument certDoc, List<int> freed, int p, Lazy<HashSet<int>> annotations,
                                              DocMdpAnalysis result)
        {
            foreach (int n in freed)
            {
                PdfObject oldObj;
                string unreadable;
                try
                {
                    oldObj = certDoc.GetObject(n);
                    unreadable = IsNull(oldObj) ? Parsing.PdfParser.LastObjectParseError : null;
                }
                catch (Exception ex)
                {
                    oldObj = null;
                    unreadable = ex.Message;
                }

                if (oldObj == null || oldObj is PdfNull)
                {
                    // Never defined in the certified revision — a free-list slot, not a deletion.
                    // Defined but unreadable is something else: a freed object whose certified value
                    // cannot be judged, which this analyzer's fail-closed contract reports rather
                    // than excuses as a free-list slot.
                    if (unreadable != null)
                        result.Violations.Add(
                            $"obj {n}: freed after certification, and its certified value could not be read " +
                            $"({unreadable}) — a deletion that cannot be judged is not permitted.");
                    continue;
                }

                string typeName = GetObjectTypeName(oldObj, certDoc);
                if (typeName != null) result.ModifiedObjectTypes.Add(typeName);

                // `p >= 3` rather than `== 3`, matching the annotation-modification test in
                // Classify: a level above 3 is not defined by the spec, and treating it as more
                // permissive is what that neighbour already does.
                if (p >= 3 && annotations.Value.Contains(n))
                    continue;   // P=3: deleting an annotation a page lists is a permitted change.

                result.Violations.Add(
                    $"obj {n}: deleted after certification" +
                    (typeName != null ? $" (was /{typeName})" : "") +
                    $" — not permitted at DocMDP /P {p}.");
            }
        }


        // ---------------------------------------------------------------- informational type name

        private static string GetObjectTypeName(PdfObject obj, PdfDocument doc)
        {
            PdfDictionary dict = obj as PdfDictionary ?? (obj as PdfStream);
            if (dict == null)
            {
                return obj switch
                {
                    PdfArray _ => "(array)",
                    PdfString _ => "(string)",
                    PdfNumber _ => "(number)",
                    PdfBoolean _ => "(boolean)",
                    PdfName _ => "(name)",
                    PdfNull _ => "(null)",
                    _ => null,
                };
            }

            var typeName = doc.ResolveObject(dict[PdfName.Type]) as PdfName ?? dict[PdfName.Type] as PdfName;
            if (typeName != null) return typeName.Value;

            var sub = ResolvedName(doc, dict, PdfName.Subtype);
            if (sub != null) return sub.Value;

            if (ResolvedName(doc, dict, PdfName.FT) is PdfName ft) return "Field:" + ft.Value;
            if (ResolvedName(doc, dict, PdfName.S) is PdfName actionS) return "Action:" + actionS.Value;
            if (dict[PdfName.Contents] != null) return "(content-stream-bearing dict)";
            if (dict[PdfName.Kids] != null) return "(tree node)";
            return "(untyped dict)";
        }

        private static string DescribePermission(int p) => p switch
        {
            1 => "No changes permitted after certification.",
            2 => "Form field values and digital signatures may be added.",
            3 => "Annotations may be added/modified; form filling and signing allowed.",
            _ => $"Unknown permission level ({p}).",
        };
    }

    /// <summary>Result of <see cref="DocMdpAnalyzer.Analyze(PdfDocument, long)"/>.</summary>
    /// <remarks>
    /// Read-only to its callers: a certification verdict, <see cref="IsCompliant"/> among it, was a
    /// set of public fields any holder could overwrite, unlike every other result this library
    /// returns. The analyzer is the only writer.
    /// </remarks>
    public sealed class DocMdpAnalysis
    {
        /// <summary>Whether a DocMDP certification signature was found.</summary>
        public bool HasCertification { get; internal set; }
        /// <summary>Certified permission level P (1/2/3); 0 = not certified.</summary>
        public int PermissionLevel { get; internal set; }
        /// <summary>Human-readable description of the permission level.</summary>
        public string PermissionDescription { get; internal set; }
        /// <summary>True when /P was absent and defaulted to 2 per spec.</summary>
        public bool UsedDefaultP { get; internal set; }
        /// <summary>
        /// True when the catalog <b>declared</b> <c>/Perms /DocMDP</c> and the certification it names
        /// could not be read, so <see cref="HasCertification"/> being false says nothing about
        /// whether this document was certified.
        /// </summary>
        /// <remarks>
        /// <b>"Not certified" is the answer that permits things</b> — modification, the next
        /// signature, an in-place image replacement — so a caller enforcing any of them must treat
        /// this as a refusal rather than as permission. It is set only when the declaration is
        /// present and unreadable; an absent <c>/Perms</c> is an ordinary uncertified document and
        /// leaves it false.
        /// </remarks>
        public bool CertificationDeclarationUnreadable { get; internal set; }
        /// <summary>
        /// Fully qualified field name (§12.7.4.2) of the certification signature, when it was located
        /// through its field rather than through <c>/Perms</c>.
        /// </summary>
        public string CertFieldName { get; internal set; }
        /// <summary>
        /// The certification's signature dictionary — the identity a verifier matches its results
        /// against, which no name can be.
        /// </summary>
        internal PdfDictionary CertSignature;
        /// <summary>End offset of the certification signature's signed region.</summary>
        public long CertByteRangeEnd { get; internal set; }
        /// <summary>Total file length.</summary>
        public long FileLength { get; internal set; }
        /// <summary>Whether any incremental update exists past the certification byte range.</summary>
        public bool HasIncrementalUpdates { get; internal set; }
        /// <summary>Whether the document complies with its DocMDP policy.</summary>
        public bool IsCompliant { get; internal set; }
        /// <summary>Object types found in post-certification incremental updates.</summary>
        public readonly List<string> ModifiedObjectTypes = new List<string>();
        /// <summary>Policy violations found (empty when compliant or not certified).</summary>
        public readonly List<string> Violations = new List<string>();
        /// <summary>
        /// The changes the level permitted, each named. <b>A permitted change can still redraw the
        /// page</b>: an annotation at level 3, and at level 2 a new signature field's appearance, can stand
        /// over the signed text and read as part of it. The level says such a change is allowed, not that
        /// it left the page as signed, so a relying party is told of them.
        /// </summary>
        public readonly List<string> PermittedChanges = new List<string>();
        /// <summary>Overall summary message.</summary>
        public string Summary { get; internal set; }
    }
}
