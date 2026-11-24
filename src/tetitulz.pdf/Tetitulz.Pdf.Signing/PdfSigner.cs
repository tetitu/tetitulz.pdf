// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Parsing;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Plugins;
using Tetitulz.Pdf.Signing.Timestamping;

namespace Tetitulz.Pdf.Signing
{
    public sealed class PdfSigner
    {
        [ThreadStatic] private static string? _lastSignerWarning;

        /// <summary>
        /// Why this library's verifier, under its default options, refuses the signature the most
        /// recent <see cref="Sign(Stream, Stream, SignatureParameters)"/> or <see cref="Complete"/>
        /// on this thread made — or <c>null</c> when it has no such reason, or nothing was signed.
        /// </summary>
        /// <remarks>
        /// <para><b>A warning, not a refusal.</b> The signature is written: a verifier configured
        /// otherwise accepts it, and what a relying party requires is the relying party's to say.
        /// What is reported is what the defaults refuse for the certificate and the digest alone —
        /// a certificate outside its validity period at the signing time, a key usage that does not
        /// permit signing, an extended key usage naming no purpose a document is signed under
        /// (<see cref="SignatureVerificationOptions.RequireSignerKeyPurpose"/>), a critical
        /// extension the chain walk does not process, a SHA-1 digest or a key below the default
        /// floors (<see cref="SignatureVerificationOptions.RejectWeakAlgorithms"/>). Trust is not
        /// asked: the signer has no anchors.</para>
        /// <para>Each reason is the verifier's own, from the function the verifier asks
        /// (<c>CertificateChainValidator.WhyTheLeafIsRefused</c>, <c>WeakAlgorithmPolicy</c>).
        /// Read it on the thread that signed; it is cleared when the next signing starts.</para>
        /// </remarks>
        public static string? LastSignerWarning => _lastSignerWarning;

        /// <summary>
        /// The reasons default verification refuses a signature made with
        /// <paramref name="certificate"/> and <paramref name="digestAlgorithm"/> at
        /// <paramref name="signingTime"/>, joined, or <c>null</c>.
        /// </summary>
        internal static string? WhyDefaultVerificationRefuses(
            Org.BouncyCastle.X509.X509Certificate certificate, string digestAlgorithm, DateTime signingTime)
        {
            var defaults = new SignatureVerificationOptions();
            var reasons = new List<string>();

            if (CertificateChainValidator.WhyTheLeafIsRefused(
                    certificate, signingTime, "Signer", defaults.RequireSignerKeyPurpose) is { } leaf)
                reasons.Add(leaf);

            if (defaults.RejectWeakAlgorithms)
            {
                string? digestOid;
                try { digestOid = DigestAlgorithms.ResolveOidForNewSignature(digestAlgorithm, nameof(digestAlgorithm), defaultWhenUnnamed: true); }
                catch (ArgumentException) { digestOid = null; }   // refused by name where the CMS is made
                string? weak = (digestOid == null ? null : WeakAlgorithmPolicy.DetectWeakDigest(digestOid))
                               ?? WeakAlgorithmPolicy.Resolve(defaults).DetectWeakKey(certificate, "signer");
                if (weak != null) reasons.Add("Weak signature algorithm: " + weak);
            }

            return reasons.Count == 0 ? null : string.Join(" ", reasons);
        }

        /// <summary>
        /// Signs a PDF file and writes the signed result to a new file.
        /// </summary>
        public void Sign(string inputPath, string outputPath, SignatureParameters parameters)
        {
            using (var input = File.OpenRead(inputPath))
            using (var output = File.Create(outputPath))
            {
                Sign(input, output, parameters);
            }
        }

        /// <summary>
        /// Signs a PDF document using an incremental update.
        /// The inputPdf must be seekable.
        /// </summary>
        public void Sign(Stream inputPdf, Stream outputPdf, SignatureParameters parameters)
            => Sign(inputPdf, outputPdf, parameters, refuseConflictingSignatures: true);

        /// <summary>
        /// <see cref="Sign(Stream, Stream, SignatureParameters)"/>, with the ISO 32000-2 §12.8.1
        /// ordering checks switchable.
        /// </summary>
        /// <param name="inputPdf">The document to sign.</param>
        /// <param name="outputPdf">Receives the signed document.</param>
        /// <param name="parameters">The signature to make.</param>
        /// <param name="refuseConflictingSignatures">
        /// <c>false</c> only in tests, to build the non-conforming documents a verifier has to recognise
        /// — a certification behind an approval signature, a second certification — which the public
        /// entry point refuses to produce.
        /// </param>
        internal void Sign(Stream inputPdf, Stream outputPdf, SignatureParameters parameters, bool refuseConflictingSignatures)
        {
            _lastSignerWarning = null;
            if (inputPdf == null) throw new ArgumentNullException(nameof(inputPdf));
            if (outputPdf == null) throw new ArgumentNullException(nameof(outputPdf));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            // ISO 32000-2 §12.8.1: a usage-rights signature "shall only be applied if no other type
            // of signature is already present" — its dictionary is in no field, referenced only from
            // /Perms /UR3. It therefore cannot also carry a DocMDP transform (that combination would
            // itself be "another type of signature" on the same dictionary) or a field's /Lock
            // (there is no field). GAPS.md §1 traces this shape.
            bool usageRightsOnly = parameters.UsageRights != UsageRights.None;
            if (usageRightsOnly && parameters.DocMdpPermission != DocMdpPermission.None)
                throw new PdfSigningException(
                    "UsageRights cannot be combined with DocMdpPermission. ISO 32000-2 §12.8.1: a " +
                    "usage-rights signature 'shall only be applied if no other type of signature is " +
                    "already present', so it cannot also certify the document. Sign twice instead — " +
                    "certify first, then apply usage rights to the certified document only if no " +
                    "further signature will ever be checked against them — or omit one.");

            // The signer is captured into a local rather than re-read from `parameters` at each
            // use: a bool standing in for "ExternalSigner != null" cannot carry that fact to the
            // dereferences below.
            IExternalSigner? externalSigner = parameters.ExternalSigner;
            CertificateData certData = LoadCertificate(parameters, externalSigner);

            // Parse the original PDF to get structure info
            inputPdf.Position = 0;
            PdfParser parser;
            PdfDictionary originalTrailer;
            int maxObjNum;
            long previousXrefOffset;
            int firstPageObjNum = 0;
            PdfDictionary rootObj;
            var existingFieldObjects = new List<PdfObject>();
            ISet<string> existingFieldNames = new HashSet<string>();

            // An already-present, still-EMPTY signature field with the requested name is adopted
            // rather than collided with — see the remarks on FindAdoptableSignatureField.
            int adoptFieldObjNum = 0;
            PdfDictionary? adoptFieldDict = null;

            // An adopted field's /Lock (Table 236) becomes a FieldMDP reference, and its /P may make
            // this signature a certification at that level — so the level the signature is made at
            // is settled here, once, and read from `docMdp` below rather than from the parameters.
            SignatureFieldLock? fieldLock = null;
            DocMdpPermission docMdp = parameters.DocMdpPermission;

            // …and its /SV (Table 237) settles the sub-filter, the reason, the digest, the level and
            // the timestamping server, once, before the first byte — see SeedValueConstraints.
            SeedValueConstraints.Choices choices;

            using (var tempMs = new MemoryStream())
            {
                inputPdf.CopyTo(tempMs);
                tempMs.Position = 0;

                // Through PdfDocument, which refuses a document with no catalog to supersede here —
                // before the output is touched, so a refusal never leaves the caller's output
                // half-written.
                using var document = Core.Document.PdfDocument.Open(tempMs);
                parser = document.Parser;
                originalTrailer = parser.Trailer;

                // §7.6.2 encrypts every string in an encrypted document except /ID, the /Encrypt
                // dictionary's own and a signature's /Contents — so /Reason, /Location, /M and the
                // field's /T written in the clear read back as garbage, and the field names this
                // compares against are ciphertext. Every other signing writer refused an encrypted
                // document; this one signed it.
                if (originalTrailer[PdfName.Encrypt] != null)
                    throw new PdfSigningException(
                        EncryptedDocumentMessages.AcroFormUnreadable + " A signature cannot be appended to it.");

                maxObjNum = parser.CrossReferenceTable.GetMaxObjectNumber();
                // The parser's own reading of startxref — the value every other writer in this
                // assembly chains to. PdfSigner kept a second scan of the last 1 KB that had to agree
                // with PdfParser.FindStartXref by review; RefuseUnnameableCrossReferenceChain below
                // verifies the value either way.
                previousXrefOffset = parser.StartXrefOffset;
                SigningHelpers.RefuseUnnameableCrossReferenceChain(tempMs, previousXrefOffset);

                rootObj = document.Catalog.Dictionary;

                // The first page, for the widget's /P — which is its only link to a page, since
                // PdfSigner adds it to no page's /Annots. By the page tree every reader walks: this
                // took the root node's /Kids[0], which in any balanced tree (§7.7.3.2) is an
                // intermediate /Pages node, so the widget named a node as its page. An inline page
                // dictionary has no object number to name, and the widget is written without /P.
                if (document.Pages.Count > 0)
                    firstPageObjNum = document.Pages[0].Dictionary.ObjectNumber;

                // The existing form's fields, to merge, and every name in use, to avoid.
                var acroFormDict = SignatureFieldRegistration.FormOf(rootObj[PdfName.AcroForm], parser.ResolveObject);
                existingFieldObjects = SignatureFieldRegistration.FieldsOf(acroFormDict, parser.ResolveObject);
                existingFieldNames = SignatureFieldRegistration.FieldNames(acroFormDict, parser.ResolveObject);

                // A usage-rights signature adopts no field and locks nothing — it is not in any
                // field at all (§12.8.1), so none of this discovery applies to it.
                if (!usageRightsOnly)
                {
                    FindAdoptableSignatureField(
                        rootObj, parser, parameters.SignatureFieldName,
                        out adoptFieldObjNum, out adoptFieldDict);

                    // §12.8.2.4 — the adopted field's lock travels into the signature, and its /P decides
                    // the DocMDP level before the ordering rules below judge that level.
                    if (adoptFieldDict != null)
                    {
                        fieldLock = SignatureFieldLock.Read(adoptFieldDict, parser.ResolveObject);
                        if (fieldLock != null) docMdp = fieldLock.Reconcile(docMdp);
                    }
                }

                choices = SeedValueConstraints.Decide(adoptFieldDict, parser.ResolveObject, parameters, docMdp, certData);
                docMdp = choices.Permission;

                // Before a byte is written: what the signatures already present allow this one to be.
                if (refuseConflictingSignatures)
                {
                    var existing = SignatureOrdering.Read(rootObj, acroFormDict, parser.ResolveObject);
                    if (usageRightsOnly)
                    {
                        // §12.8.1's own rule, stricter than the ordering check below: not merely
                        // "in the right order" but "no other type of signature at all", and "at
                        // most one usage rights signature" — so an existing /Perms /UR3 refuses too,
                        // which SignatureOrdering.Read (built for the ordering rule) does not see.
                        if (existing.HasApproval || existing.CertificationPermission > 0)
                            throw new PdfSigningException(
                                "The document already carries a signature. ISO 32000-2 §12.8.1: a " +
                                "usage-rights signature 'shall only be applied if no other type of " +
                                "signature is already present'.");
                        if (parser.ResolveObject(rootObj[PdfName.Perms]) is PdfDictionary existingPermsForUr
                            && existingPermsForUr[PdfName.UR3] != null)
                            throw new PdfSigningException(
                                "The document already carries a usage-rights signature. ISO 32000-2 " +
                                "§12.8.1 allows at most one.");
                    }
                    else
                    {
                        SignatureOrdering.RefuseConflicts(existing, docMdp, parameters.AllowCertificationInvalidation);
                    }
                }
            }

            // The catalog's object number, needed before the first byte is written: a FieldMDP
            // reference names the catalog as its /Data, and the signed catalog is written at it.
            //
            // A trailer with a DIRECT /Root — `/Root << /Type /Catalog … >>` — has no object number
            // to supersede, and ObjectNumberOf answers 0, which is reserved as the head of the free
            // list. Without this the writer would emit `0 0 obj` plus an xref entry for it, so the
            // signed catalog would be written but unreachable: a MALFORMED FILE THAT REPORTED SUCCESS,
            // with a signature verifying over a document whose catalog cannot be found.
            //
            // Plugins.EthereumSign.PdfBlockchainSigner refuses the same trailer: a fix to one
            // writer is a finding about every writer of the same shape.
            int catalogObjNum = CatalogObjectNumberOrRefuse(originalTrailer);
            int catalogGen = Core.Writing.ExistingObjectGeneration.Of(
                parser.CrossReferenceTable, catalogObjNum, originalTrailer[PdfName.Root]);

            // Adopting supersedes the existing field object in place, so its name is by
            // definition not a collision and must not be renamed away from.
            bool adoptField = adoptFieldObjNum > 0 && adoptFieldDict != null;

            // A new field's name must be free, and must not contain a period (§12.7.4.2).
            string sigFieldName = adoptField
                ? parameters.SignatureFieldName
                : SignatureFieldRegistration.UniqueName(parameters.SignatureFieldName, existingFieldNames);

            // Allocate new object numbers. When adopting, the field object already exists and is
            // superseded at its own number, so only the signature dictionary is new. A usage-rights
            // signature is in no field at all (§12.8.1), so it allocates only itself — sigFieldObjNum
            // stays 0, a value no real object number ever takes (0 is the free-list head, §7.5.4),
            // and every use below is already conditioned on usageRightsOnly or adoptField.
            int sigDictObjNum = maxObjNum + 1;
            int sigFieldObjNum = usageRightsOnly ? 0 : adoptField ? adoptFieldObjNum : maxObjNum + 2;
            int newSize = usageRightsOnly ? maxObjNum + 2 : adoptField ? maxObjNum + 2 : maxObjNum + 3;

            DateTime signingTime = parameters.SigningTime ?? DateTime.UtcNow;
            string pdfDate = FormatPdfDate(signingTime);

            // Prepare the signature placeholder size.
            //
            // The reserve honours BOTH the caller's floor and the signer's own estimate.
            // `IExternalSigner.EstimatedSignatureSize` reserves space in the /Contents placeholder:
            // a signer that sizes itself correctly (`CertStoreExternalSigner` measures its real chain
            // and adds 8 KB for exactly this) gets that space even when the caller left the
            // parameter at its 16 KB default, instead of a PdfSigningException at the fit check.
            //
            // Max rather than "the signer wins": the parameter is the caller's floor and the signer
            // knows its own output, so under-reserving from either side is the failure to avoid.
            int reserveBytes = Math.Max(parameters.EstimatedSignatureSize,
                                        externalSigner?.EstimatedSignatureSize ?? 0);
            int contentsHexLength = reserveBytes * 2; // 2 hex chars per byte

            // Copy original to output
            inputPdf.Position = 0;
            inputPdf.CopyTo(outputPdf);

            // Write the incremental update manually
            var writer = new PdfWriter(outputPdf);

            // 1. Write signature dictionary object
            long sigDictOffset = outputPdf.Position;
            writer.WriteRaw($"{sigDictObjNum} 0 obj\n");

            // Every key below goes through `sig`, which refuses to emit one twice. The raw writing
            // itself has to stay — /ByteRange and /Contents are placeholders whose byte offsets are
            // captured mid-write and patched afterwards — so this is the structure that raw writing
            // otherwise has none of. A duplicated /Location once passed all 197 tests in this
            // suite; see RawPdfDictionaryWriter's remarks.
            var sig = new RawPdfDictionaryWriter(writer, "the signature dictionary");

            writer.WriteRaw("<<");
            sig.Entry("Type", "/Sig");
            sig.Entry("Filter", "/" + SeedValueConstraints.ThisFilter);
            sig.Entry("SubFilter", "/" + choices.SubFilter);

            // ByteRange placeholder — 12 digits per number supports files up to ~999 GB.
            // The placeholder must be exactly the same length as the actual value that will
            // overwrite it later; excess space is left as ASCII spaces (harmless in PDF).
            sig.BeginEntry("ByteRange");
            long byteRangePosition = outputPdf.Position;
            string byteRangePlaceholder = SigningDefaults.ByteRangePlaceholder;
            writer.WriteRaw(byteRangePlaceholder);

            // Contents placeholder
            sig.BeginEntry("Contents");
            writer.WriteRaw("<");
            long contentsStart = outputPdf.Position;
            writer.WriteRaw(new string('0', contentsHexLength));
            long contentsEnd = outputPdf.Position;
            writer.WriteRaw(">");

            // Captured into locals: `!IsNullOrEmpty(x)` narrows x, but the narrowing is on the
            // property access and does not survive being read a second time inside the
            // interpolation. A local says it once and keeps it.
            string? reason = choices.Reason;
            string? location = parameters.Location;
            string? contactInfo = parameters.ContactInfo;
            if (!string.IsNullOrEmpty(reason))
                sig.Entry("Reason", SigningHelpers.TextStringToken(reason));
            if (!string.IsNullOrEmpty(location))
                sig.Entry("Location", SigningHelpers.TextStringToken(location));
            if (!string.IsNullOrEmpty(contactInfo))
                sig.Entry("ContactInfo", SigningHelpers.TextStringToken(contactInfo));
            sig.Entry("M", $"({pdfDate})");

            // /Reference array: the optional DocMDP (certification), FieldMDP (an adopted field's
            // lock) and /UR3 (usage rights) transforms. Each is a SigRef entry in the same array; one
            // signature can carry more than one.
            bool hasDocMdp = docMdp != DocMdpPermission.None;
            bool hasUr     = parameters.UsageRights != UsageRights.None;
            if (hasDocMdp || fieldLock != null || hasUr)
            {
                // The array and the SigRef dictionaries inside it are opaque values here: a nested
                // dictionary is a different entry set, and its own /Type must not read as a
                // collision with this one's.
                sig.BeginEntry("Reference");
                writer.WriteRaw("[");
                if (hasDocMdp)
                {
                    int p = (int)docMdp;
                    writer.WriteRaw(
                        $"<< /Type /SigRef /TransformMethod /DocMDP" +
                        $" /TransformParams << /Type /TransformParams /P {p} /V /1.2 >>" +
                        $" /DigestMethod /SHA256 >>");
                }
                if (fieldLock != null)
                    fieldLock.WriteSigRef(writer, catalogObjNum, catalogGen);
                if (hasUr)
                {
                    var sb = new StringBuilder();
                    sb.Append("<< /Type /SigRef /TransformMethod /UR3");
                    sb.Append(" /TransformParams << /Type /TransformParams /V /2.2");
                    // Table 258's names, from the table PdfUsageRightsReader reads them back with.
                    sb.Append(Core.Document.UsageRightsTransform.Entries(parameters.UsageRights));
                    sb.Append(" >>");
                    sb.Append(" /DigestMethod /SHA256");
                    sb.Append(" >>");
                    writer.WriteRaw(sb.ToString());
                }
                writer.WriteRaw("]");
            }

            writer.WriteRaw(" >>\n");
            writer.WriteRaw("endobj\n");

            // 2. Write signature field / widget annotation — skipped entirely for a usage-rights
            // signature, which §12.8.1 puts in no field at all.
            long sigFieldOffset = 0;
            int sigFieldGen = 0;
            if (!usageRightsOnly)
            {
                // §7.5.6 — when adopting an existing empty field this SUPERSEDES it, so it has to be
                // re-declared at the generation the document already gave it; at generation 0 against a
                // gen>0 field it supersedes nothing and a strict reader keeps the empty one. A newly
                // allocated field number is genuinely generation 0, and the helper returns that.
                sigFieldGen = adoptField
                    ? Core.Writing.ExistingObjectGeneration.Of(
                        parser.CrossReferenceTable, sigFieldObjNum, adoptFieldDict)
                    : 0;

                sigFieldOffset = outputPdf.Position;
                writer.WriteRaw($"{sigFieldObjNum} {sigFieldGen} obj\n");
                if (adoptField)
                {
                    // Supersede the existing empty field, keeping every entry it already carries —
                    // /Rect, /AP, /P, /F, /DA — and adding only /V. Writing a fresh invisible field
                    // instead is what left a visible stamp permanently unsigned; see the remarks on
                    // FindAdoptableSignatureField.
                    writer.WriteRaw("<<");
                    // Same guard as the signature dictionary. The `/V` skip below was already correct —
                    // it exists so the /V added afterwards is not a second one — but it was correctness
                    // by remembering. Claiming each copied key makes it correctness by construction, and
                    // also catches a source field that itself carries a duplicate.
                    var field = new RawPdfDictionaryWriter(writer, "the signature field dictionary");

                    // `adoptFieldDict != null` is one of the two clauses of `adoptField`; re-testing it
                    // is what carries that to here, since a bool records the conclusion and not the fact.
                    foreach (var kvp in adoptFieldDict!)
                    {
                        if (kvp.Key == PdfName.V) continue; // written below, pointing at the new /Sig
                        field.ClaimOnly(kvp.Key.Value);
                        writer.WriteRaw(" ");
                        writer.WriteObject(kvp.Key);
                        writer.WriteRaw(" ");
                        writer.WriteObject(kvp.Value);
                    }
                    field.Entry("V", $"{sigDictObjNum} 0 R");
                    writer.WriteRaw(" >>\n");
                }
                else
                {
                    writer.WriteRaw("<< /Type /Annot");
                    writer.WriteRaw(" /Subtype /Widget");
                    writer.WriteRaw(" /FT /Sig");
                    writer.WriteRaw(" /T " + SigningHelpers.TextStringToken(sigFieldName));
                    writer.WriteRaw($" /V {sigDictObjNum} 0 R");
                    writer.WriteRaw($" /F {PdfAnnotationFlags.Print | PdfAnnotationFlags.Locked}");
                    writer.WriteRaw(" /Rect [0 0 0 0]"); // Invisible
                    if (firstPageObjNum > 0)
                    {
                        // The page at its own generation (ExistingObjectGeneration), never 0.
                        writer.WriteRaw(" /P ");
                        writer.WriteObject(Core.Writing.ExistingObjectGeneration.ReferenceTo(parser.CrossReferenceTable, firstPageObjNum));
                    }
                    writer.WriteRaw(" >>\n");
                }
                writer.WriteRaw("endobj\n");
            }

            // 3. Write updated catalog with AcroForm, or with /Perms /UR3 for a usage-rights
            // signature, which touches no field and so no /AcroForm.
            // BuildSignedCatalog is the ONLY place the signed catalog's key set is decided — this
            // revision and the later /DSS revision write the same instance, so they cannot drift.
            var signedCatalog = usageRightsOnly
                ? BuildUsageRightsSignedCatalog(rootObj, sigDictObjNum, parser)
                : BuildSignedCatalog(
                    rootObj, existingFieldObjects, sigFieldObjNum, adoptField,
                    sigDictObjNum, docMdp, parser);

            long catalogOffset = writer.WriteIndirectObject(catalogObjNum, catalogGen, signedCatalog);

            // 4. Write xref table.
            //
            // The GENERATION-AWARE overload. The other one assumes 0 for every entry and says so on
            // its own doc comment — "for incremental updates that may carry gen>0 objects, use the
            // … overload so the xref generation matches the object header" — and all three writers
            // in this assembly were calling it, so the entries contradicted the headers above.
            var xrefEntries = new List<(int objNum, int gen, long offset)>
            {
                (sigDictObjNum, 0, sigDictOffset),
                (catalogObjNum, catalogGen, catalogOffset)
            };
            if (!usageRightsOnly)
                xrefEntries.Insert(1, (sigFieldObjNum, sigFieldGen, sigFieldOffset));

            long xrefOffset = writer.WriteXrefTable(xrefEntries);

            // 5. Write trailer.
            // ONE implementation, in Core.Writing.PdfTrailerBuilder. This site wrote /Prev
            // unconditionally: PdfSigner builds its own trailer and calls WriteTrailer directly, so
            // IncrementalUpdateWriter's guard against an unusable /Prev never ran for it. The guard
            // stops the trailer asserting a history that is not there; it does NOT rescue such a
            // document — see PdfTrailerBuilder for what was measured and what is still open.
            var newTrailer = PdfTrailerBuilder.ForAppendedRevision(
                originalTrailer, newSize, previousXrefOffset, xrefOffset);

            writer.WriteTrailer(newTrailer, xrefOffset);

            long fileLength = outputPdf.Position;

            // 6. Compute actual ByteRange and overwrite placeholder.
            // ONE implementation of this arithmetic, in SignatureByteRange — it was open-coded here,
            // in PdfDocumentTimestamper and in two test fixtures, and the timestamper disagreed by
            // one byte. See that type for what the disagreement was and why it verified anyway.
            var range = SignatureByteRange.Compute(contentsStart, contentsEnd, fileLength);
            long rangeBeforeLen = range.BeforeLength;
            long rangeAfterStart = range.AfterStart;
            long rangeAfterLen = range.AfterLength;

            // Overwrite ByteRange placeholder
            outputPdf.Position = byteRangePosition;
            writer.WriteRaw(range.Format(byteRangePlaceholder.Length));

            // 7. Read the byte ranges and compute hash
            byte[] rangeData = Core.Document.SignedByteRanges.ReadSignedBytes(
                                   outputPdf, 0, rangeBeforeLen, rangeAfterStart, rangeAfterLen)
                ?? throw new PdfSigningException("Unexpected end of stream while reading byte range.");

            // 8. Generate the CMS — the one implementation, shared with the planned form.
            byte[] pkcs7Signature = BuildSignatureValue(
                rangeData, certData, externalSigner, choices, parameters, reserveBytes, signingTime);

            // 9. Write signature into Contents placeholder
            SigningHelpers.WriteContents(outputPdf, contentsStart, contentsHexLength, pkcs7Signature);

            // 10. Optionally embed LTV data (/DSS dictionary) as a second incremental update.
            List<string>? ltvWarnings = null;
            if (parameters.EmbedLtv && !string.IsNullOrEmpty(choices.TimestampUrl))
            {
                Exception? ltvException = null;
                try
                {
                    // Deliberately without parameters.TimestampNetwork: its credentials are for the
                    // TSA, and the responders asked here are named by the certificates.
                    if (DssBuilder.TryBuildDss(certData.Chain, certData.Certificate,
                            out List<byte[]> ocspResponses, out List<byte[]> crls, out ltvWarnings))
                        EmbedDss(outputPdf, new LtvEvidence(ocspResponses, crls));
                }
                catch (Exception ex)
                {
                    // LTV embedding is best-effort; the signature is already written
                    // above and the document is valid without /DSS.  Surface the
                    // failure to the caller so they know LTV degraded silently.
                    ltvException = ex;
                }

                if (ltvException != null)
                    throw SignedButIncomplete.Raise(
                        "The PDF was signed successfully but LTV (/DSS) embedding failed: " +
                        ltvException.GetType().Name + ": " + ltvException.Message,
                        ltvException);
            }

            // 11. PAdES B-LTA (ETSI EN 319 142-1 §6.3): the document timestamp over everything
            // written so far — the signature revision and the /DSS — as a third revision, in this
            // same call, not a second pass through PdfDocumentTimestamper by the caller.
            // Before the revocation warnings below, so a /DSS that is there is under the archive
            // timestamp whatever the responders said.
            if (parameters.PadesBaselineLevel == PadesBaseline.B_LTA)
                AppendDocumentTimestamp(outputPdf, parameters);

            // Notify the caller about OCSP issues AFTER the signature is fully written.
            if (ltvWarnings != null && ltvWarnings.Count > 0)
                throw SignedButIncomplete.Raise(
                    "The PDF was signed successfully but LTV (DSS) data is incomplete. " +
                    "Revocation-data issues: " + string.Join("; ", ltvWarnings));

            // Flush
            outputPdf.Flush();
        }

        // ==================================================================
        //  The planned form: objects out, CMS in later
        // ==================================================================

        /// <summary>
        /// Plans a signature revision as objects — for a writer that must serialise them itself,
        /// above all <c>Tetitulz.Pdf.Encryption.PdfEncryptedIncrementalUpdate</c>, which encrypts an
        /// appended revision under the document's own file key.
        /// </summary>
        /// <param name="document">
        /// The document to sign, <b>readable</b>: for an encrypted file this is the decrypted copy
        /// the encrypted writer hands its plan, never the encrypted original, which exposes no
        /// catalog. The plan reads the object graph from it and writes nothing to it.
        /// </param>
        /// <param name="firstFreeObjectNumber">
        /// The first object number the plan may allocate, or 0 to allocate past the document's own
        /// highest. An encrypted writer passes the <b>original's</b> ceiling: decryption unpacks object
        /// streams and drops the cross-reference stream, so the copy's ceiling can be lower than the
        /// file's, and allocating from it would overwrite a live object.
        /// </param>
        /// <param name="parameters">The signature to make. See the remarks for what the planned form declines.</param>
        /// <returns>
        /// The revision's objects and everything <see cref="Complete"/> needs. Append the objects
        /// with any writer that honours their numbers and generations, then hand the finished file to
        /// <see cref="Complete"/>.
        /// </returns>
        /// <remarks>
        /// <para><b>Same decisions as <see cref="Sign(Stream, Stream, SignatureParameters)"/>, by the
        /// same code.</b> Certificate loading, field adoption, the adopted field's <c>/Lock</c>,
        /// the seed value's constraints, the §12.8.1 ordering rules, the catalog's key set and the
        /// CMS are each one implementation reached from both forms; what differs is only how the
        /// revision reaches the file — raw bytes there, <see cref="IndirectObject"/>s here.</para>
        ///
        /// <para><b>ISO 32000-2 §7.6.2</b> leaves "any hexadecimal strings representing the value of the
        /// Contents key in a Signature dictionary" unencrypted, and everything else this revision
        /// writes — the field's <c>/T</c>, <c>/Reason</c>, <c>/Location</c>, <c>/ContactInfo</c>,
        /// <c>/M</c> — is a string an encrypted document's reader will decrypt. The plan writes them all
        /// as ordinary objects; <c>Core.Writing.PdfWriter</c> applies the exemption to the one entry the
        /// clause names and encrypts the rest.</para>
        ///
        /// <para><b>Declined, each with its reason:</b> <see cref="SignatureParameters.EmbedLtv"/>,
        /// because the <c>/DSS</c> is a second revision this plan does not contain and an encrypted
        /// document would need it encrypted too; <see cref="SignatureParameters.UsageRights"/>, whose
        /// <c>/UR3</c> reference only the raw writer spells; and a document with no catalog — the
        /// encrypted original handed in by mistake.</para>
        /// </remarks>
        /// <exception cref="PdfSigningException">The document or the parameters cannot be signed this way.</exception>
        public SignaturePlan Plan(Core.Document.PdfDocument document, int firstFreeObjectNumber, SignatureParameters parameters)
        {
            _lastSignerWarning = null;
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (parameters == null) throw new ArgumentNullException(nameof(parameters));

            if (document.IsEncrypted || document.Catalog == null)
                throw new PdfSigningException(
                    "Plan needs a readable object graph and this document exposes no catalog. For an " +
                    "encrypted file, plan against the decrypted copy — Tetitulz.Pdf.Encryption." +
                    "PdfEncryptionOperations.Sign does that and appends the revision encrypted.");

            if (parameters.PadesBaselineLevel == PadesBaseline.B_LTA)
                throw new PdfSigningException(
                    "PAdES B-LTA cannot be used with Plan: the document timestamp is a third revision " +
                    "appended after the signature and its /DSS, which this plan does not contain — and for " +
                    "an encrypted document it would have to be encrypted as well. Complete the signature, " +
                    "then add validation information and a document timestamp to a decrypted copy " +
                    "(PdfLtvRefresher.AddVerificationInfo, PdfDocumentTimestamper.AddTimestamp).");

            if (parameters.EmbedLtv)
                throw new PdfSigningException(
                    "EmbedLtv cannot be used with Plan: the /DSS is a second revision appended after the " +
                    "signature, which this plan does not contain — and for an encrypted document it would " +
                    "have to be encrypted as well. Complete the signature, then add validation " +
                    "information with PdfLtvRefresher.AddVerificationInfo on a decrypted copy.");

            if (parameters.UsageRights != UsageRights.None)
                throw new PdfSigningException(
                    "UsageRights cannot be used with Plan: the /UR3 signature reference is written by " +
                    "Sign only. Usage rights are deprecated in PDF 2.0 and honoured by a reader only for a " +
                    "signature under its own vendor root.");

            IExternalSigner? externalSigner = parameters.ExternalSigner;
            CertificateData certData = LoadCertificate(parameters, externalSigner);

            PdfParser parser = document.Parser;
            PdfDictionary originalTrailer = parser.Trailer;
            PdfDictionary rootObj = document.Catalog.Dictionary;

            // The first page, for the widget's /P — the same tree walk Sign takes; see there.
            int firstPageObjNum = document.Pages.Count > 0 ? document.Pages[0].Dictionary.ObjectNumber : 0;

            var acroFormDict = SignatureFieldRegistration.FormOf(rootObj[PdfName.AcroForm], parser.ResolveObject);
            List<PdfObject> existingFieldObjects = SignatureFieldRegistration.FieldsOf(acroFormDict, parser.ResolveObject);
            HashSet<string> existingFieldNames = SignatureFieldRegistration.FieldNames(acroFormDict, parser.ResolveObject);

            FindAdoptableSignatureField(
                rootObj, parser, parameters.SignatureFieldName,
                out int adoptFieldObjNum, out PdfDictionary? adoptFieldDict);

            SignatureFieldLock? fieldLock = null;
            DocMdpPermission docMdp = parameters.DocMdpPermission;
            if (adoptFieldDict != null)
            {
                fieldLock = SignatureFieldLock.Read(adoptFieldDict, parser.ResolveObject);
                if (fieldLock != null) docMdp = fieldLock.Reconcile(docMdp);
            }

            SeedValueConstraints.Choices choices =
                SeedValueConstraints.Decide(adoptFieldDict, parser.ResolveObject, parameters, docMdp, certData);
            docMdp = choices.Permission;

            SignatureOrdering.RefuseConflicts(
                SignatureOrdering.Read(rootObj, acroFormDict, parser.ResolveObject),
                docMdp, parameters.AllowCertificationInvalidation);

            int catalogObjNum = CatalogObjectNumberOrRefuse(originalTrailer);
            int catalogGen = Core.Writing.ExistingObjectGeneration.Of(
                parser.CrossReferenceTable, catalogObjNum, originalTrailer[PdfName.Root]);

            bool adoptField = adoptFieldObjNum > 0 && adoptFieldDict != null;
            string sigFieldName = adoptField
                ? parameters.SignatureFieldName
                : SignatureFieldRegistration.UniqueName(parameters.SignatureFieldName, existingFieldNames);

            // Past both ceilings: the caller's (the original file's, for an encrypted document) and
            // the graph's own. Whichever is lower names a live object.
            int next = Math.Max(firstFreeObjectNumber, parser.CrossReferenceTable.GetMaxObjectNumber() + 1);
            int sigDictObjNum = next;
            int sigFieldObjNum = adoptField ? adoptFieldObjNum : next + 1;
            int nextFree = adoptField ? next + 1 : next + 2;

            // The same reserve rule as Sign: the caller's floor and the signer's own estimate.
            int reserveBytes = Math.Max(parameters.EstimatedSignatureSize,
                                        externalSigner?.EstimatedSignatureSize ?? 0);

            DateTime signingTime = parameters.SigningTime ?? DateTime.UtcNow;

            // 1. The signature dictionary, in the key order Sign writes. /ByteRange is a fixed-width
            //    placeholder — [0 V V V] at the twelve-digit value — and /Contents a zero-filled hex
            //    string of the reserve, both overwritten in place by Complete.
            var sig = new PdfDictionary();
            sig.Set(PdfName.Type, PdfName.Sig);
            sig.Set(PdfName.Filter, new PdfName(SeedValueConstraints.ThisFilter));
            sig.Set(PdfName.SubFilter, new PdfName(choices.SubFilter));

            var byteRange = new PdfArray();
            byteRange.Add(new PdfNumber(0));
            for (int i = 0; i < 3; i++) byteRange.Add(new PdfNumber(SigningDefaults.ByteRangePlaceholderValue));
            sig.Set(PdfName.ByteRange, byteRange);
            sig.Set(PdfName.Contents, new PdfString(new byte[reserveBytes], isHex: true));

            string? reason = choices.Reason;
            string? location = parameters.Location;
            string? contactInfo = parameters.ContactInfo;
            if (!string.IsNullOrEmpty(reason)) sig.Set(PdfName.Reason, new PdfString(reason));
            if (!string.IsNullOrEmpty(location)) sig.Set(PdfName.Location, new PdfString(location));
            if (!string.IsNullOrEmpty(contactInfo)) sig.Set(PdfName.ContactInfo, new PdfString(contactInfo));
            sig.Set(PdfName.M, new PdfString(FormatPdfDate(signingTime)));

            if (docMdp != DocMdpPermission.None || fieldLock != null)
            {
                var references = new PdfArray();
                if (docMdp != DocMdpPermission.None)
                    references.Add(DocMdpSigRef(docMdp));
                if (fieldLock != null)
                    references.Add(fieldLock.SigRefDictionary(catalogObjNum, catalogGen));
                sig.Set(PdfName.Reference, references);
            }

            // 2. The field: the adopted one superseded in place at its own generation (§7.5.6), every
            //    entry kept and /V added — or a fresh invisible widget, as Sign writes it.
            PdfDictionary field;
            int sigFieldGen;
            if (adoptField)
            {
                field = PdfDictionary.CloneShallow(adoptFieldDict!);
                field.Set(PdfName.V, new PdfReference(sigDictObjNum, 0));
                sigFieldGen = Core.Writing.ExistingObjectGeneration.Of(
                    parser.CrossReferenceTable, sigFieldObjNum, adoptFieldDict);
            }
            else
            {
                field = new PdfDictionary();
                field.Set(PdfName.Type, PdfName.Annot);
                field.Set(PdfName.Subtype, PdfName.Widget);
                field.Set(PdfName.FT, PdfName.Sig);
                field.Set(PdfName.T, new PdfString(sigFieldName));
                field.Set(PdfName.V, new PdfReference(sigDictObjNum, 0));
                field.Set(PdfName.F, new PdfNumber(PdfAnnotationFlags.Print | PdfAnnotationFlags.Locked));
                var rect = new PdfArray();
                for (int i = 0; i < 4; i++) rect.Add(new PdfNumber(0));
                field.Set(PdfName.Rect, rect);
                if (firstPageObjNum > 0)
                    field.Set(PdfName.P, Core.Writing.ExistingObjectGeneration.ReferenceTo(parser.CrossReferenceTable, firstPageObjNum));
                sigFieldGen = 0;
            }

            // 3. The catalog — the single source of its key set, shared with Sign.
            PdfDictionary signedCatalog = BuildSignedCatalog(
                rootObj, existingFieldObjects, sigFieldObjNum, adoptField, sigDictObjNum, docMdp, parser);

            var objects = new List<IndirectObject>
            {
                new IndirectObject(sigDictObjNum, 0, sig),
                new IndirectObject(sigFieldObjNum, sigFieldGen, field),
                new IndirectObject(catalogObjNum, catalogGen, signedCatalog),
            };

            return new SignaturePlan(
                objects, sigFieldName, sigDictObjNum, nextFree, reserveBytes,
                choices, certData, externalSigner, parameters, signingTime);
        }

        /// <summary>
        /// Finishes a planned signature in the file a writer appended it to: writes the real
        /// <c>/ByteRange</c> over its placeholder, digests the file around the <c>/Contents</c>
        /// hole, builds the CMS and writes it into the hole.
        /// </summary>
        /// <param name="plan">What <see cref="Plan"/> returned.</param>
        /// <param name="signedFile">
        /// The whole document — original bytes plus the appended revision — readable, writable and
        /// seekable, starting at position 0. Only the two placeholders change; every other byte is
        /// what the <c>/ByteRange</c> then describes.
        /// </param>
        /// <remarks>
        /// <para><b>The placeholders are found in the file, not remembered from the plan.</b> The
        /// writer decided where the objects landed; the file's own cross-reference table says where
        /// the signature dictionary is, and <c>SigningHelpers.LocatePlannedPlaceholders</c> refuses
        /// anything at that offset that is not the planned dictionary with a reserve of exactly the
        /// planned size. In an encrypted file the dictionary's other strings are ciphertext by now,
        /// which is why the scan relies on the two placeholders and nothing else.</para>
        /// <para>The <c>/ByteRange</c> arithmetic, the range read, the CMS and the <c>/Contents</c>
        /// write are the same four implementations <see cref="Sign(Stream, Stream, SignatureParameters)"/>
        /// uses.</para>
        /// </remarks>
        /// <exception cref="PdfSigningException">
        /// The file does not carry the plan's objects, the CMS does not fit the reserve, or the
        /// signature could not be generated.
        /// </exception>
        public void Complete(SignaturePlan plan, Stream signedFile)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (signedFile == null) throw new ArgumentNullException(nameof(signedFile));
            if (!signedFile.CanSeek || !signedFile.CanRead || !signedFile.CanWrite)
                throw new PdfSigningException(
                    "Complete needs a readable, writable, seekable stream: the placeholders are located " +
                    "in the written file and overwritten in place.");
            _lastSignerWarning = null;

            // Where the writer put the signature dictionary: the finished file's own table. Opening
            // an encrypted file parses exactly that much before stopping at /Encrypt.
            long objectOffset;
            signedFile.Position = 0;
            using (var written = Core.Document.PdfDocument.Open(signedFile))
            {
                var entry = written.CrossReferenceTable?.GetEntry(plan.SignatureObjectNumber);
                if (entry == null || entry.Type != Core.Parsing.CrossReferenceEntry.EntryType.Normal)
                    throw new PdfSigningException(
                        $"The file's cross-reference table does not name object {plan.SignatureObjectNumber} " +
                        "as an in-file object, so the planned signature dictionary is not in it. Append " +
                        "the plan's objects before completing.");
                objectOffset = entry.Offset;
            }

            int reservedHexChars = plan.ReserveBytes * 2;
            SigningHelpers.LocatePlannedPlaceholders(
                signedFile, objectOffset, plan.SignatureObjectNumber, reservedHexChars,
                out long byteRangeOpen, out int byteRangeWidth, out long contentsStart, out long contentsEnd);

            long fileLength = signedFile.Length;
            var range = SignatureByteRange.Compute(contentsStart, contentsEnd, fileLength);

            // Format pads to the placeholder's width and never past it: a longer value would overwrite
            // the key that follows.
            string formatted = range.Format(byteRangeWidth);
            if (formatted.Length != byteRangeWidth)
                throw new PdfSigningException(
                    $"The /ByteRange {formatted.Trim()} does not fit its {byteRangeWidth}-character placeholder.");
            signedFile.Position = byteRangeOpen;
            byte[] rangeBytes = Encoding.ASCII.GetBytes(formatted);
            signedFile.Write(rangeBytes, 0, rangeBytes.Length);

            byte[] rangeData = Core.Document.SignedByteRanges.ReadSignedBytes(
                                   signedFile, 0, range.BeforeLength, range.AfterStart, range.AfterLength)
                ?? throw new PdfSigningException("Unexpected end of stream while reading byte range.");

            byte[] cms = BuildSignatureValue(
                rangeData, plan.Certificate, plan.ExternalSigner, plan.Choices, plan.Parameters, plan.ReserveBytes,
                plan.SigningTime);

            if (plan.CmsPostProcessor != null)
            {
                // The digest of exactly the bytes this signature covers, computed here because this
                // is the only point where they are in hand: ISO/TS 32004 §6.6.3's dataDigest.
                byte[] dataDigest;
                using (var sha = System.Security.Cryptography.SHA256.Create())
                    dataDigest = sha.ComputeHash(rangeData);

                cms = plan.CmsPostProcessor(cms, dataDigest)
                      ?? throw new PdfSigningException(
                          "The CMS post-processor returned nothing; it must return the value to write.");

                // Re-checked against what came BACK, not what went in. BuildSignatureValue already
                // measured its own output, so a post-processor that grows the CMS past the reserve
                // would otherwise reach WriteContents having passed a check on a different value.
                if (cms.Length > plan.ReserveBytes)
                    throw new PdfSigningException(
                        $"The signature value grew to {cms.Length} bytes after the ISO/TS 32004 PDF MAC token " +
                        $"was attached, which exceeds the {plan.ReserveBytes}-byte reserve the placeholder was " +
                        "written from. Raise SignatureParameters.EstimatedSignatureSize: the token is an " +
                        "unsigned attribute on the signature and is written into the same /Contents string.");
            }

            SigningHelpers.WriteContents(signedFile, contentsStart, reservedHexChars, cms);
            signedFile.Flush();
        }

        /// <summary>
        /// The DocMDP signature reference (Tables 258, 260) as an object — what
        /// <see cref="Sign(Stream, Stream, SignatureParameters)"/> spells raw.
        /// </summary>
        private static PdfDictionary DocMdpSigRef(DocMdpPermission permission)
        {
            var transformParams = new PdfDictionary();
            transformParams.Set(PdfName.Type, PdfName.TransformParams);
            transformParams.Set(PdfName.P, new PdfNumber((int)permission));
            transformParams.Set(PdfName.V, new PdfName("1.2"));

            var sigRef = new PdfDictionary();
            sigRef.Set(PdfName.Type, PdfName.SigRef);
            sigRef.Set(PdfName.TransformMethod, PdfName.DocMDP);
            sigRef.Set(PdfName.TransformParams, transformParams);
            sigRef.Set(PdfName.DigestMethod, new PdfName("SHA256"));
            return sigRef;
        }

        /// <summary>
        /// The signing certificate and chain — from the external signer, the PFX stream or the PFX
        /// path, in that order of precedence. One loader for both forms of signing.
        /// </summary>
        private static CertificateData LoadCertificate(SignatureParameters parameters, IExternalSigner? externalSigner)
        {
            if (externalSigner != null)
            {
                // External signer provides its own certificate chain.
                // We don't need a private key — the external device holds it.
                // Create a minimal CertificateData with just the certificate chain.
                var chainBytes = externalSigner.GetCertificateChain();
                if (chainBytes == null || chainBytes.Count == 0)
                    throw new PdfSigningException("External signer returned no certificate chain.");
                return CertificateHelper.LoadFromDer(chainBytes);
            }

            if (parameters.CertificateStream != null)
                return CertificateHelper.LoadFromPfx(parameters.CertificateStream, parameters.CertificatePassword);

            if (!string.IsNullOrEmpty(parameters.CertificatePath))
                return CertificateHelper.LoadFromPfx(parameters.CertificatePath!, parameters.CertificatePassword);

            throw new PdfSigningException("No certificate provided. Set ExternalSigner, CertificatePath, or CertificateStream.");
        }

        /// <summary>
        /// The catalog's object number, refused when the trailer's <c>/Root</c> is direct.
        /// </summary>
        /// <remarks>
        /// A trailer with a DIRECT /Root — <c>/Root &lt;&lt; /Type /Catalog … &gt;&gt;</c> — has no object
        /// number to supersede, and ObjectNumberOf answers 0, which is reserved as the head of the
        /// free list. Without this the writer emitted <c>0 0 obj</c> plus an xref entry for it, so the
        /// signed catalog was written but unreachable: a MALFORMED FILE THAT REPORTED SUCCESS, with a
        /// signature verifying over a document whose catalog cannot be found.
        /// </remarks>
        internal static int CatalogObjectNumberOrRefuse(PdfDictionary trailer)
        {
            int catalogObjNum = SigningHelpers.ObjectNumberOf(trailer[PdfName.Root]);
            if (catalogObjNum <= 0)
                throw new PdfSigningException(
                    "/Root is not an indirect reference, so the catalog cannot be superseded by an " +
                    "incremental update. Object 0 is reserved as the head of the free list and " +
                    "cannot carry the signed catalog.");
            return catalogObjNum;
        }

        /// <summary>
        /// The CMS for <paramref name="rangeData"/>: signed locally or by the external signer,
        /// timestamped when a TSA is named, offered to the sign plugins, and measured against the
        /// reserve it has to fit. The one implementation behind both forms of signing.
        /// </summary>
        /// <remarks>
        /// <c>signingTime</c> is the instant the signature dictionary's <c>/M</c> claims. A locally
        /// built CMS carries it as its <c>signing-time</c> attribute, or carries none under PAdES
        /// (<c>CmsSignatureGenerator.SigningTimeAttributeFor</c>); an external signer builds its own.
        /// </remarks>
        private static byte[] BuildSignatureValue(
            byte[] rangeData, CertificateData certData, IExternalSigner? externalSigner,
            SeedValueConstraints.Choices choices, SignatureParameters parameters, int reserveBytes,
            DateTime signingTime)
        {
            // Said before the CMS is made, of the certificate and digest it is made with, so a
            // signature default verification refuses is reported where it is written.
            _lastSignerWarning = WhyDefaultVerificationRefuses(certData.Certificate, choices.DigestAlgorithm, signingTime);

            byte[] pkcs7Signature;
            try
            {
                if (externalSigner != null)
                {
                    // 8a-ext. Delegate signing to external provider (PKCS#11/HSM/smart card).
                    // The external signer handles hashing + signing internally.
                    pkcs7Signature = externalSigner.SignData(rangeData, choices.DigestAlgorithm);
                }
                else
                {
                    // Only the external-signer path may lack a private key, and this is the other
                    // branch — so a null here means a PFX that carried no key. Stating it turns
                    // what was a NullReferenceException from inside BouncyCastle into the
                    // library's own exception naming the cause.
                    if (certData.PrivateKey == null)
                        throw new PdfSigningException(
                            "The supplied certificate contains no private key, and no ExternalSigner " +
                            "was set. A signing certificate must carry its key or delegate to one.");

                    // 8a. Generate initial signature using local private key. An explicit
                    // signature-algorithm OID (RSA-PSS, ECDSA, Ed25519, …) is used as given; with
                    // none, BouncyCastle picks the default for the key type.
                    pkcs7Signature = CmsSignatureGenerator.Generate(
                        rangeData,
                        certData.PrivateKey,
                        certData.Certificate,
                        certData.Chain,
                        choices.DigestAlgorithm,
                        string.IsNullOrEmpty(parameters.SignatureAlgorithmOid) ? null : parameters.SignatureAlgorithmOid,
                        CmsSignatureGenerator.SigningTimeAttributeFor(choices.SubFilter, signingTime),
                        timestampToken: null);
                }

                // 8b. If a TSA URL is configured, timestamp the signature VALUE and attach the token
                //     to the CMS just made — see SignatureTimestamp for why neither half may differ.
                //     The same for an external signer's CMS, unless its device already timestamped it.
                string? tsaUrl = choices.TimestampUrl;
                if (!string.IsNullOrEmpty(tsaUrl))
                {
                    // A server the document named is reached as an OCSP responder a certificate
                    // names is: through the guarded transport, with host-scoped credentials only.
                    SigningNetworkOptions? network = parameters.TimestampNetwork;
                    bool fromDocument = choices.TimestampUrlFromDocument;
                    pkcs7Signature = SignatureTimestamp.Add(
                        pkcs7Signature, parameters.TimestampDigestAlgorithm,
                        (imprint, oid) => fromDocument
                            ? TsaClient.GetTimestampTokenFromDocumentUrl(imprint, oid, tsaUrl, network)
                            : TsaClient.GetTimestampToken(imprint, oid, tsaUrl, network));
                }
            }
            catch (Exception ex) when (!(ex is PdfSigningException))
            {
                throw new PdfSigningException($"Failed to generate PKCS#7 signature: {ex.Message}", ex);
            }

            // 8c. Offer the finished CMS blob to the registered sign plugins. Done here, after the
            // timestamp round, so a plugin sees the blob that would otherwise be written — and
            // before the size check, so an augmented blob is measured rather than the original.
            pkcs7Signature = ApplyWrapSignaturePlugins(pkcs7Signature, parameters);

            // Measured against the SAME `reserveBytes` the placeholder was written from. Comparing
            // against `parameters.EstimatedSignatureSize` here while reserving from the max would
            // reject a signature that fits the hole — the two numbers must be one number.
            if (pkcs7Signature.Length > reserveBytes)
            {
                throw new PdfSigningException(
                    $"PKCS#7 signature ({pkcs7Signature.Length} bytes) exceeds the reserved size ({reserveBytes} bytes). " +
                    "Increase EstimatedSignatureSize in SignatureParameters.");
            }

            return pkcs7Signature;
        }

        /// <summary>
        /// Runs <see cref="ISignPlugin.WrapSignature"/> over the freshly generated CMS blob and
        /// returns the first non-null replacement, per that member's stated contract ("First
        /// non-null return value wins; subsequent plugins are not called").
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the hook's call site, so a plugin implementing the LTV-embedding use-case its own
        /// documentation recommends has its returned blob written, not the un-augmented one.
        /// </para>
        /// <para>
        /// A throwing plugin <b>fails the signing operation</b> rather than being skipped: this
        /// hook exists to strengthen the signature (embedded revocation data), so quietly writing
        /// the weaker blob would give the caller a document that does not carry what they asked
        /// for. A plugin wanting best-effort behaviour returns <c>null</c>, which is the
        /// contract's documented "leave unchanged" escape.
        /// </para>
        /// </remarks>
        private static byte[] ApplyWrapSignaturePlugins(byte[] cmsBlob, SignatureParameters parameters)
        {
            var plugins = PdfPluginHost.Instance.GetPlugins<ISignPlugin>();
            for (int i = 0; i < plugins.Count; i++)
            {
                byte[]? replacement;
                try
                {
                    replacement = plugins[i].WrapSignature(cmsBlob, parameters);
                }
                catch (Exception ex)
                {
                    throw new PdfSigningException(
                        $"Sign plugin '{plugins[i].Name}' failed in WrapSignature: {ex.Message}", ex);
                }

                if (replacement != null && replacement.Length > 0)
                    return replacement;
            }
            return cmsBlob;
        }

        /// <summary>
        /// Builds the catalog the signature revision writes — every original entry, plus the
        /// <c>/AcroForm</c> naming the new signature field, plus the <c>/Perms /DocMDP</c> entry a
        /// certification signature needs. A later revision supersedes the catalog object wholesale,
        /// so anything missing here is <b>deleted from the document</b>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the single source of that key set.</b> Step 3 of <c>Sign</c> writes the
        /// dictionary this returns through <see cref="PdfWriter.WriteIndirectObject(int,int,PdfObject)"/>,
        /// and the <c>/DSS</c> revision clones the catalog it reads back out of that output — so the
        /// two revisions cannot disagree about what a signed catalog contains.
        /// </para>
        /// <para>
        /// <c>/Perms</c> is the entry that matters most: it is created by the signature revision
        /// itself, so a <c>/DSS</c> revision built from the original catalog's entries and the new
        /// <c>/AcroForm</c> alone would silently strip a certification — a document signed with
        /// <see cref="SignatureParameters.DocMdpPermission"/> set <i>and</i>
        /// <see cref="SignatureParameters.EmbedLtv"/> on would lose DocMDP to the very revision meant
        /// to strengthen it.
        /// </para>
        /// <para>
        /// A dictionary rather than streamed bytes, so re-signing a document that already has
        /// <c>/Perms</c> writes the key once: <c>Set</c> replaces.
        /// </para>
        /// <para>
        /// Parity is pinned by <c>DssRevisionCatalogTests</c>, which signs a document and asserts
        /// the catalog the signature revision actually wrote against this method's output.
        /// </para>
        /// </remarks>
        internal static PdfDictionary BuildSignedCatalog(
            PdfDictionary rootObj,
            List<PdfObject> existingFieldObjects,
            int sigFieldObjNum,
            bool fieldWasAdopted,
            int sigDictObjNum,
            DocMdpPermission docMdpPermission,
            PdfParser? parser = null)
        {
            // An adopted field is already reachable and must not be added again. For a top-level one
            // that is because it IS one of the existing entries; for a nested one it is because its
            // parent is, and the parent's /Kids still points at it. Adding a nested field here would
            // put it in the tree twice — once as a root field and once as somebody's kid — which
            // §12.7.3.2 does not allow and which gives it two different fully qualified names.
            var existingAcroForm = parser != null
                ? SignatureFieldRegistration.FormOf(rootObj[PdfName.AcroForm], parser.ResolveObject)
                : rootObj[PdfName.AcroForm] as PdfDictionary;

            var signedAcroForm = SignatureFieldRegistration.FormWithSignature(
                existingAcroForm, existingFieldObjects,
                fieldWasAdopted ? null : new PdfReference(sigFieldObjNum, 0));

            var signedCatalog = PdfDictionary.CloneShallow(rootObj);
            signedCatalog.Set(PdfName.AcroForm, signedAcroForm);

            if (docMdpPermission != DocMdpPermission.None)
            {
                var perms = new PdfDictionary();

                // Everything the document already granted (notably the /UR3 usage rights of a
                // Reader-extended document) survives; only /DocMDP is replaced, since this
                // signature is the certification. Building a fresh dictionary and dropping the
                // rest would silently revoke those rights. `Set` also means /Perms is written
                // exactly once — §7.3.7 leaves a duplicated key undefined and readers disagree
                // on which wins, which is what a copy-then-append writer produced here.
                var existingPerms = (parser != null
                    ? parser.ResolveObject(rootObj[PdfName.Perms])
                    : rootObj[PdfName.Perms]) as PdfDictionary;
                if (existingPerms != null)
                {
                    foreach (var kvp in existingPerms)
                    {
                        if (kvp.Key == PdfName.DocMDP) continue;
                        perms.Set(kvp.Key, kvp.Value);
                    }
                }

                perms.Set(PdfName.DocMDP, new PdfReference(sigDictObjNum, 0));
                signedCatalog.Set(PdfName.Perms, perms);
            }

            return signedCatalog;
        }

        /// <summary>
        /// Builds the catalog a usage-rights signature revision writes — every original entry, plus
        /// <c>/Perms /UR3</c> referencing the signature dictionary. No <c>/AcroForm</c> change: a
        /// usage-rights signature is in no field (§12.8.1), so nothing here is a field to register.
        /// </summary>
        /// <remarks>
        /// The counterpart of <see cref="BuildSignedCatalog"/>'s <c>/Perms /DocMDP</c> branch, same
        /// shape: every existing <c>/Perms</c> entry except the one being replaced survives, so
        /// signing a document that already carries a <c>/Perms /DocMDP</c> certification does not
        /// silently drop it, and <c>Set</c> means <c>/Perms</c> is written exactly once. Called only
        /// when <see cref="SignatureOrdering"/>'s pre-flight check has already refused a document
        /// that carries any other signature or an existing <c>/Perms /UR3</c>, so there is exactly
        /// one <c>/Perms</c> entry to add here, never one to replace.
        /// </remarks>
        internal static PdfDictionary BuildUsageRightsSignedCatalog(
            PdfDictionary rootObj, int sigDictObjNum, PdfParser? parser = null)
        {
            var signedCatalog = PdfDictionary.CloneShallow(rootObj);

            var perms = new PdfDictionary();
            var existingPerms = (parser != null
                ? parser.ResolveObject(rootObj[PdfName.Perms])
                : rootObj[PdfName.Perms]) as PdfDictionary;
            if (existingPerms != null)
            {
                foreach (var kvp in existingPerms)
                {
                    if (kvp.Key == PdfName.UR3) continue;
                    perms.Set(kvp.Key, kvp.Value);
                }
            }

            perms.Set(PdfName.UR3, new PdfReference(sigDictObjNum, 0));
            signedCatalog.Set(PdfName.Perms, perms);

            return signedCatalog;
        }

        /// <summary>
        /// Appends a <c>/DSS</c> revision carrying <paramref name="evidence"/> to the document
        /// <c>Sign</c> just wrote, through the one <c>/DSS</c> revision writer.
        /// </summary>
        /// <remarks>
        /// <para><b>The signed file is read back rather than described</b>, and the revision is
        /// written by the same writer as <see cref="PdfLtvRefresher"/>'s. Written from what
        /// <c>Sign</c> remembered — the catalog it built, the input's trailer, the next object number
        /// — it would lose data two ways: a <c>/DSS</c> holding only this signer's responses
        /// unreferences every OCSP response, CRL and <c>/VRI</c> entry earlier signatures rely on,
        /// and a trailer of four keys drops <c>/Info</c> and every other key from the newest
        /// revision, which ISO 32000-2 §7.5.6 does not allow.</para>
        ///
        /// <para><b>Internal so a test can reach what the network hides.</b> <c>Sign</c> fetches the
        /// evidence and hands it here; a test hands evidence of its own to the same method.</para>
        /// </remarks>
        /// <param name="outputPdf">
        /// The signed document, readable and seekable. The revision is appended after its last byte,
        /// so the signed bytes stay byte-identical.
        /// </param>
        /// <param name="evidence">The revocation data to embed.</param>
        internal static void EmbedDss(Stream outputPdf, LtvEvidence evidence)
        {
            PdfLtvRefresher.DssRevision revision;

            // Planned while the document is open, and nothing is read from it afterwards: the
            // revision is appended to the very stream it was parsed from.
            outputPdf.Position = 0;
            using (var signed = Core.Document.PdfDocument.Open(outputPdf))
                revision = PdfLtvRefresher.DssRevision.Plan(signed, evidence);

            // Append point, stated rather than inherited: PdfWriter writes at the current position
            // and never seeks, and the parse above left the position wherever it last read.
            outputPdf.Position = outputPdf.Length;
            revision.AppendTo(outputPdf);
        }

        /// <summary>
        /// Appends the PAdES B-LTA document timestamp (<c>/DocTimeStamp</c>, ETSI EN 319 142-1 §6.3)
        /// over everything <paramref name="outputPdf"/> holds — the signature revision and the
        /// <c>/DSS</c> — as a revision of its own, through <see cref="PdfDocumentTimestamper"/>, the
        /// one writer of document timestamps: the same TSA as the signature's
        /// (<see cref="SignatureParameters.TimestampAuthorityUrl"/>, <see cref="SignatureParameters.TimestampNetwork"/>)
        /// and <see cref="SignatureParameters.TimestampDigestAlgorithm"/>.
        /// </summary>
        /// <remarks>
        /// The timestamper reads a document and writes its bytes plus one revision to another
        /// stream, so the signed bytes are copied out, timestamped into a buffer, and only the
        /// revision past them appended — the bytes already in the caller's stream stay where they
        /// are, as <see cref="EmbedDss"/> leaves them.
        /// </remarks>
        private static void AppendDocumentTimestamp(Stream outputPdf, SignatureParameters parameters)
        {
            string tsaUrl = parameters.TimestampAuthorityUrl
                ?? throw new PdfSigningException("PAdES B-LTA needs a TSA URL for the document timestamp.");

            outputPdf.Position = 0;
            var signed = new MemoryStream();
            outputPdf.CopyTo(signed);
            signed.Position = 0;

            var stamped = new MemoryStream();
            PdfDocumentTimestamper.AddTimestamp(signed, stamped, tsaUrl, parameters.TimestampNetwork,
                parameters.TimestampDigestAlgorithm);

            outputPdf.Position = outputPdf.Length;
            stamped.Position = signed.Length;
            stamped.CopyTo(outputPdf);
        }

        /// <summary>
        /// Scans the last <see cref="StartXrefScanWindowBytes"/> bytes of <paramref name="stream"/>
        /// for the last <c>startxref</c> keyword and returns its operand, or 0 when none is found or
        /// the operand cannot be parsed as a non-negative integer.
        /// </summary>
        /// <remarks>
        /// <para><b>Both guarantees below are load-bearing.</b> This scan supplies <c>/Prev</c> for
        /// the signature revision and for the DSS revision alike. A stream that returns fewer bytes
        /// than asked for — permitted of any <see cref="Stream"/>, and normal for pipe- and
        /// network-backed ones — must still yield the offset, so the read is done through
        /// <see cref="Core.IO.StreamBytes.Fill"/> rather than a single <c>Read</c>. And because the
        /// window is sized to exactly the remaining bytes to end of stream, filling it always leaves
        /// <paramref name="stream"/> positioned at its own end as a side effect — a caller appending
        /// a revision needs that and must not have to seek separately.</para>
        ///
        /// <para><b>Returns 0 rather than throwing.</b> Unlike <see cref="PdfParser.FindStartXref"/>,
        /// which is the strict, parse-or-recover contract for opening a document, this scan is used
        /// only to locate the offset a new revision's own <c>/Prev</c> should chain from — a missing
        /// or malformed operand here is not fatal, it just means there is nothing to chain to.</para>
        ///
        /// <para><b>An unsigned scan, not an unbounded one.</b> Skipping every non-digit past the
        /// keyword would eat a negative operand's sign and report "-1234" as <c>1234</c> — a
        /// plausible-looking offset, the dangerous kind of wrong, because a caller cannot tell it
        /// from a real one. The digit scan stops at the first non-digit, so a sign (or nothing at
        /// all) yields no digits and the method reports 0.</para>
        /// </remarks>
        internal static long FindLastXrefOffset(Stream stream)
        {
            long searchStart = Math.Max(0, stream.Length - StartXrefScanWindowBytes);
            stream.Position = searchStart;

            var buffer = new byte[stream.Length - searchStart];
            // A single Read may underfill the buffer; fill until full or true end of stream, and
            // leave the stream positioned there — see remarks above.
            int read = Core.IO.StreamBytes.Fill(stream, buffer, 0, buffer.Length);
            string tail = Encoding.ASCII.GetString(buffer, 0, read);

            const string keyword = "startxref";
            int idx = tail.LastIndexOf(keyword, StringComparison.Ordinal);
            if (idx < 0) return 0;

            // §7.2.3 Table 1 defines six whitespace bytes; PdfConstants.IsWhitespace is the one
            // predicate for all of them, shared with PdfParser.FindStartXref.
            int numStart = idx + keyword.Length;
            while (numStart < tail.Length && PdfConstants.IsWhitespace((byte)tail[numStart]))
                numStart++;

            int numEnd = numStart;
            while (numEnd < tail.Length && char.IsDigit(tail[numEnd]))
                numEnd++;

            if (numEnd == numStart) return 0; // no digits: a sign, or trailing junk with none at all

            string offsetStr = tail.Substring(numStart, numEnd - numStart);
            return long.TryParse(offsetStr, out long offset) ? offset : 0;
        }

        /// <summary>
        /// How many bytes at the end of a stream <see cref="FindLastXrefOffset"/> scans for
        /// <c>startxref</c>. Shares <see cref="PdfParser"/>'s window size by convention, not by
        /// reference — the two scans have different failure contracts (see
        /// <see cref="FindLastXrefOffset"/>'s remarks) and may be tuned independently.
        /// </summary>
        private const int StartXrefScanWindowBytes = 1024;

        // Date formatting, literal-string escaping and hex encoding are shared with the
        // counter-signer and the document timestamper; see SigningHelpers.
        private static string FormatPdfDate(DateTime dt) => SigningHelpers.FormatPdfDate(dt);


        /// <summary>
        /// Locates the AcroForm field whose fully qualified name is <paramref name="wantedName"/>
        /// — at the top level or nested under a parent — that is a signature field with no value
        /// yet, and returns its object number and dictionary so the caller can supersede it in
        /// place instead of allocating a second field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what makes a visible signature actually sign anything.</b>
        /// <see cref="VisualSignatureBuilder"/> writes a widget carrying the stamp's <c>/Rect</c>
        /// and <c>/AP</c> under a generated <c>/T</c>, then hands the file to this signer with the
        /// same <see cref="SignatureParameters.SignatureFieldName"/>. Without adoption the name
        /// looked like a <i>collision</i>: the signer renamed itself (<c>VisualSig…</c> →
        /// <c>VisualSig2</c>) and wrote a brand-new field with <c>/Rect [0 0 0 0]</c> to carry the
        /// <c>/V</c>. The output then opened as one invisible valid signature plus an empty,
        /// clickable, permanently UNSIGNED field showing the stamp — the stamp was decorative, and
        /// the signature it advertised had no visual representation at all.
        /// </para>
        /// <para>
        /// Only a field written as an indirect reference qualifies (the <c>/Kids</c> walk below
        /// reaches nested ones by their fully qualified names), and only when the field carries no
        /// <c>/V</c>. A field that is already signed is a genuine collision and still falls through
        /// to the renaming path, so an existing signature can never be overwritten.
        /// </para>
        /// </remarks>
        private static void FindAdoptableSignatureField(
            PdfDictionary root, PdfParser parser, string wantedName,
            out int objNum, out PdfDictionary? dict)
        {
            objNum = 0;
            dict = null;
            if (string.IsNullOrEmpty(wantedName)) return;

            var acroFormObj = root[PdfName.AcroForm];
            if (acroFormObj == null) return;
            var acroForm = parser.ResolveObject(acroFormObj) as PdfDictionary;
            // Resolved: /AcroForm is resolved on the line above, /Fields was not. A null here
            // returns "no adoptable field", so the caller takes the RENAME branch instead — and
            // SignatureFieldRegistration.FieldNames reads /Fields through the resolved path, so the wanted name is
            // still found and still collides. The named empty signature field the document
            // provided is left permanently unsigned while a fresh invisible one carries the /V.
            var fields = parser.ResolveObject(acroForm?[PdfName.Fields]) as PdfArray;
            if (fields == null) return;

            FindAdoptableSignatureField(
                fields, parser, wantedName, prefix: null, inheritedFieldType: null,
                depth: 0, visited: new HashSet<int>(), ref objNum, ref dict);
        }

        /// <summary>
        /// The <c>/Kids</c> walk behind <see cref="FindAdoptableSignatureField(PdfDictionary,
        /// PdfParser, string, out int, out PdfDictionary)"/>, matching on the <b>fully qualified</b>
        /// field name.
        /// </summary>
        /// <remarks>
        /// <para><b>It walks <c>/Kids</c> because <c>SignatureFieldRegistration.FieldNames</c> does, and the two halves
        /// must agree about the same document.</b> Scanning only the top level meant a signature
        /// field prepared under a parent node was enumerable but not adoptable: the collision check
        /// saw the name and the adoption did not, so the signer renamed itself and wrote a second,
        /// invisible field to carry the <c>/V</c> — leaving the prepared field permanently unsigned.
        /// That is the same disagreement an unresolved <c>/FT</c> once produced, one method
        /// apart.</para>
        ///
        /// <para><b>The comparison is the fully qualified name (§12.7.3.2), not the leaf.</b> A leaf
        /// name is not an identity — two parents may each hold a <c>sig</c> — so matching on it would
        /// adopt whichever came first. <c>SignatureFieldRegistration.FieldNames</c> adds <i>both</i> forms to the
        /// collision set, deliberately: a name that is ambiguous is still a name a new field must not
        /// take.</para>
        ///
        /// <para><b>Two conditions exist for reasons the top-level scan never had to face.</b>
        /// <c>/FT</c> is <b>inheritable</b> (Table 220), so a nested signature field routinely
        /// carries none of its own and the type has to travel down the walk — reading it off the node
        /// alone finds nothing. And a match must carry its <b>own</b> <c>/T</c>: a terminal field's
        /// kids are its <i>widgets</i>, which have no name of their own, so without that test a
        /// widget inherits its field's qualified name and gets adopted in its place — superseding an
        /// annotation with a field dictionary.</para>
        /// </remarks>
        private static void FindAdoptableSignatureField(
            PdfArray? fields, PdfParser parser, string wantedName,
            string? prefix, string? inheritedFieldType, int depth, HashSet<int> visited,
            ref int objNum, ref PdfDictionary? dict)
        {
            if (fields == null || depth > PdfLimits.MaxTreeDepth) return;

            for (int i = 0; i < fields.Count; i++)
            {
                // A visited set, not the depth cap alone: a /Kids cycle is shallow, so the cap would
                // walk it once per entry point. Same guard, same reason, as SignatureFieldRegistration.FieldNames.
                var fieldRef = fields[i] as PdfReference;
                if (fieldRef != null && !visited.Add(fieldRef.ReferenceObjectNumber)) continue;

                var field = parser.ResolveObject(fields[i]) as PdfDictionary;
                if (field == null) continue;

                // Resolved, like /AcroForm, /Fields and the field itself above — and, decisively,
                // like the /T read in SignatureFieldRegistration.FieldNames below. GetName/GetString test the RAW
                // value's type and do not follow a reference (§7.3.10 permits one), so an indirect
                // /FT or /T read as ABSENT and the field was skipped while the collision check,
                // which resolves, saw it.
                // The name and the type as Core's field model gives them: an empty /T adds no
                // segment (FieldNames.Qualify) and is no name of its own (AcroFieldClassification),
                // and a chain that declares no /FT takes the one a bare widget kid declares. Joined
                // by hand, /T () named a field "section." and adopted nothing; and a field whose
                // type sat on its nameless widget was a signature field to Core and not to this
                // walk, so signing it wrote a second field.
                string? leaf = (parser.ResolveObject(field[PdfName.T]) as PdfString)?.GetText();
                string? qualified = Core.Forms.FieldNames.Qualify(prefix, leaf);

                string? chainType = (parser.ResolveObject(field[PdfName.FT]) as PdfName)?.Value
                                    ?? inheritedFieldType;
                string? fieldType = chainType
                                    ?? Core.Forms.AcroFieldClassification.WidgetFieldType(field, o => parser.ResolveObject(o))?.Value;

                // The object number is needed to supersede it, so a directly-embedded field
                // dictionary cannot be adopted — but its kids still can, so the walk continues
                // either way.
                if (fieldRef != null
                    && !string.IsNullOrEmpty(leaf)                  // a widget has no name of its own
                    && fieldType == PdfName.Sig.Value
                    && qualified == wantedName
                    && field[PdfName.V] == null)                    // already signed — a real collision
                {
                    objNum = fieldRef.ReferenceObjectNumber;
                    dict = field;
                    return;
                }

                // The chain's type travels down, never the one read up from a widget: that is this
                // node's own repair, not something its kids inherit.
                FindAdoptableSignatureField(
                    parser.ResolveObject(field[PdfName.Kids]) as PdfArray,
                    parser, wantedName, qualified, chainType, depth + 1, visited,
                    ref objNum, ref dict);

                if (objNum > 0) return;
            }
        }
    }
}
