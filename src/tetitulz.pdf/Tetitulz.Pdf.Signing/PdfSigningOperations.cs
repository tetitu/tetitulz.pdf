// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Plugins;
using Tetitulz.Pdf.Signing.Plugins;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Timestamping;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Facade for all PDF signing, verification, and timestamping operations.
    /// This class lives in <c>Tetitulz.Pdf.Signing</c> so that the core rendering
    /// library (<c>Tetitulz.Pdf.Base</c>) has no dependency on signing infrastructure.
    /// </summary>
    public static class PdfSigningOperations
    {
        // ── Plugin orchestration ─────────────────────────────────────────────

        /// <summary>
        /// The single orchestrator behind every <c>Sign</c> overload: runs each registered
        /// <see cref="ISignPlugin"/>'s <see cref="ISignPlugin.OnBeforeSign"/>, signs into a
        /// buffer, then chains the bytes through every <see cref="ISignPlugin.OnAfterSign"/>
        /// before they reach the caller's stream.
        /// <para>
        /// <b>These are the only two <see cref="ISignPlugin"/> members dispatched <i>here</i>.</b>
        /// The other two operate on state that exists only inside <c>PdfSigner</c>, which this
        /// method never holds: <see cref="ISignPlugin.WrapSignature"/> is dispatched there, on the
        /// CMS blob before it is written into the reserved <c>/Contents</c> hole, while
        /// <see cref="ISignPlugin.ComputeDigest"/> has no call site in the library at all — the
        /// supported HSM path is <see cref="Tetitulz.Pdf.Signing.Models.IExternalSigner"/>. The
        /// interface documents the same facts at each member.
        /// </para>
        /// </summary>
        private static void RunWithSignPlugins(
            SignatureParameters parameters,
            Stream finalOutput,
            Action<MemoryStream> signerAction)
        {
            var host = PdfPluginHost.Instance;
            var signPlugins = host.GetPlugins<ISignPlugin>();

            foreach (var p in signPlugins)
                p.OnBeforeSign(parameters);

            using var captureOut = new MemoryStream();
            // A refusal raised after the signature was written (LTV data incomplete) still delivers
            // the signed document, as PdfSigner.Sign leaves it in its output: discarding it left the
            // caller an empty file under a message saying the PDF was signed.
            PdfSigningException? afterWrite = null;
            try { signerAction(captureOut); }
            catch (PdfSigningException ex) when (SignedButIncomplete.Is(ex)) { afterWrite = ex; }
            byte[] signedBytes = captureOut.ToArray();

            foreach (var p in signPlugins)
                signedBytes = p.OnAfterSign(signedBytes, parameters) ?? signedBytes;

            finalOutput.Write(signedBytes, 0, signedBytes.Length);
            if (afterWrite != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(afterWrite).Throw();
        }

        private static Stream EnsureSeekable(Stream input) => SigningHelpers.EnsureSeekable(input);

        // ── Sign ─────────────────────────────────────────────────────────────

        /// <summary>Signs a PDF with a digital signature using a .pfx/.p12 certificate.</summary>
        public static void Sign(string inputPath, string outputPath, string pfxPath, string password,
            string? reason = null, string? location = null, string fieldName = "Signature1")
        {
            var parameters = new SignatureParameters
            {
                CertificatePath = pfxPath,
                CertificatePassword = password,
                Reason = reason,
                Location = location,
                SignatureFieldName = fieldName
            };
            using var input  = File.OpenRead(inputPath);
            using var output = File.Create(outputPath);
            RunWithSignPlugins(parameters, output, cap => new PdfSigner().Sign(input, cap, parameters));
        }

        /// <summary>Signs a PDF with a digital signature using streams.</summary>
        public static void Sign(Stream input, Stream output, string pfxPath, string password,
            string? reason = null, string? location = null)
        {
            var parameters = new SignatureParameters
            {
                CertificatePath = pfxPath,
                CertificatePassword = password,
                Reason = reason,
                Location = location
            };
            RunWithSignPlugins(parameters, output, cap => new PdfSigner().Sign(input, cap, parameters));
        }

        /// <summary>
        /// Signs a PDF with a digital signature and a trusted RFC 3161 timestamp.
        /// </summary>
        public static void Sign(string inputPath, string outputPath, string pfxPath, string password,
            string timestampAuthorityUrl,
            bool embedLtv = false,
            string? reason = null, string? location = null, string fieldName = "Signature1")
        {
            var parameters = new SignatureParameters
            {
                CertificatePath = pfxPath,
                CertificatePassword = password,
                Reason = reason,
                Location = location,
                SignatureFieldName = fieldName,
                TimestampAuthorityUrl = timestampAuthorityUrl,
                EmbedLtv = embedLtv,
                // A timestamped (and optionally LTV) CMS does not fit the 16 KB default.
                EstimatedSignatureSize = SigningDefaults.SignatureReserveBytes
            };
            using var input  = File.OpenRead(inputPath);
            using var output = File.Create(outputPath);
            RunWithSignPlugins(parameters, output, cap => new PdfSigner().Sign(input, cap, parameters));
        }

        /// <summary>Signs a PDF using a pre-configured <see cref="SignatureParameters"/> object.</summary>
        public static void Sign(string inputPath, string outputPath, SignatureParameters parameters)
        {
            using var input  = File.OpenRead(inputPath);
            using var output = File.Create(outputPath);
            RunWithSignPlugins(parameters, output, cap => new PdfSigner().Sign(input, cap, parameters));
        }

        /// <summary>Signs a PDF using a pre-configured <see cref="SignatureParameters"/> object (stream overload).</summary>
        public static void Sign(Stream input, Stream output, SignatureParameters parameters)
            => RunWithSignPlugins(parameters, output, cap => new PdfSigner().Sign(input, cap, parameters));

        // ── Visual signature ─────────────────────────────────────────────────

        /// <summary>Signs a PDF with a visible JPEG image stamp on the specified page.</summary>
        public static void SignWithImage(string inputPath, string outputPath,
            string pfxPath, string password, string imagePath,
            int pageNumber = 1, float x = 50, float y = 50,
            float width = 200, float height = 80,
            string? reason = null, string? location = null)
            => VisualSignatureBuilder.Sign(inputPath, outputPath, pfxPath, password, imagePath,
                pageNumber, x, y, width, height, reason, location);

        // ── Timestamp ────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a PAdES B-LTA document-level RFC 3161 timestamp to the PDF.
        /// </summary>
        public static void AddDocumentTimestamp(Stream input, Stream output,
            string tsaUrl, string hashAlgorithm = "SHA-256", string fieldName = "DocTimeStamp1")
            => PdfDocumentTimestamper.AddTimestamp(input, output, tsaUrl, hashAlgorithm, fieldName);

        // ── Verify ───────────────────────────────────────────────────────────

        /// <summary>
        /// The names of signature fields the document already contains that carry <b>no</b>
        /// signature — the values a caller can pass as
        /// <see cref="SignatureParameters.SignatureFieldName"/> to sign into a prepared field
        /// rather than creating a new one.
        /// </summary>
        /// <remarks>
        /// <para><b>No new traversal.</b> This is <c>PdfAcroForm.GetSignatureFields()</c> filtered
        /// by <see cref="Core.Forms.PdfSignatureField.HasSignature"/>; that walk already carries the depth cap
        /// and the reference-keyed visited set a <c>/Kids</c> cycle needs, and duplicating it here
        /// would give the signer and this enumeration two answers about the same document — which
        /// is the defect <c>PdfSigner</c>'s own remarks record having had twice.</para>
        ///
        /// <para><b>Fully-qualified names</b> (§12.7.3.1), which is the identity that is required to
        /// be unique, and the name <c>Sign</c> adopts a prepared field by: a signature field nested
        /// under a parent is listed as <c>parent.child</c>, and signing with that name supersedes
        /// it in place, as it does a top-level one.</para>
        /// </remarks>
        /// <param name="input">The document to inspect. Left open, and its position restored.</param>
        public static IReadOnlyList<string> GetUnsignedSignatureFieldNames(Stream input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            long start = input.CanSeek ? input.Position : 0L;
            try
            {
                using var doc = PdfDocument.Open(input);

                // An encrypted document leaves PdfDocument with a null catalog, so every read
                // below answers "nothing is there" — indistinguishable from a document that
                // genuinely has no signature fields. Refusing is the honest answer and the one
                // PdfDocumentTimestamper already gives; an empty list would tell a caller its
                // prepared signature field does not exist.
                if (doc.IsEncrypted)
                    throw new PdfSigningException(
                        "The document is encrypted, so its AcroForm cannot be read. Decrypt it " +
                        "before enumerating signature fields.");

                var form = doc.AcroForm;
                if (form == null) return Array.Empty<string>();

                var names = new List<string>();
                foreach (var field in form.GetSignatureFields())
                {
                    // The walk is permissive on purpose (see its own remarks), so the filtering
                    // happens here. Without IsAddressableField this listed a widget kid, whose
                    // fully qualified name is its PARENT'S - so a field with one separate widget
                    // reported its name twice, in a list whose remarks below call that name the
                    // identity required to be unique - and a grouping node holding /FT for its
                    // kids, which no caller can sign into.
                    if (!field.IsAddressableField) continue;
                    if (field.HasSignature) continue;
                    string name = field.FullyQualifiedName;
                    if (!string.IsNullOrEmpty(name)) names.Add(name);
                }
                return names;
            }
            finally
            {
                if (input.CanSeek) input.Position = start;
            }
        }

        /// <summary>
        /// Writes (or replaces) a signature field's seed-value dictionary, constraining which
        /// digest algorithms a future signer may use for it (PDF 32000-1:2008 §12.7.4.3, Table
        /// 234's <c>/SV</c> entry — see <see cref="SignatureSeedValue"/> for the dictionary shape).
        /// </summary>
        /// <remarks>
        /// <para><b>The field must already be an indirect object.</b> Writing <c>/SV</c> means
        /// replacing the field's own dictionary through an incremental update — the same object
        /// number, a new generation's worth of content — which needs the field to have an object
        /// number to write at. A field embedded directly as a dictionary literal inside
        /// <c>/AcroForm /Fields</c> (legal, if unusual) has none, and is refused rather than
        /// silently promoted to an indirect object the array's own entry does not point at.</para>
        /// <para>Looked up by <see cref="Core.Forms.PdfFormField.FullyQualifiedName"/> — the
        /// identity §12.7.3.1 requires to be unique — through the same
        /// <c>IsAddressableField</c>-filtered walk <see cref="GetUnsignedSignatureFieldNames"/>
        /// uses, so the two methods agree on which fields exist to name.</para>
        /// </remarks>
        /// <param name="input">The document to edit. Left open, and its position restored.</param>
        /// <param name="output">Destination for the complete edited document.</param>
        /// <param name="fieldName">The signature field's fully-qualified name.</param>
        /// <param name="digestMethodNames">
        /// Acceptable digest algorithm names, in preference order — see
        /// <see cref="SignatureSeedValue.BuildDigestMethodDictionary"/>.
        /// </param>
        /// <param name="required">
        /// When true, a conforming signer must use one of <paramref name="digestMethodNames"/> or
        /// refuse to sign; when false (the default), the list is a preference only.
        /// </param>
        public static void SetSeedValueDigestMethods(
            Stream input, Stream output, string fieldName,
            IReadOnlyList<string> digestMethodNames, bool required = false)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (string.IsNullOrEmpty(fieldName))
                throw new ArgumentException("Field name is required.", nameof(fieldName));

            Stream ms = SigningHelpers.EnsureSeekable(input, rewind: true);
            using var doc = PdfDocument.Open(ms);

            if (doc.IsEncrypted)
                throw new PdfSigningException(
                    "The document is encrypted, so its AcroForm cannot be edited. Decrypt it " +
                    "before writing a seed-value dictionary.");

            var form = doc.AcroForm;
            if (form == null)
                throw new PdfSigningException(
                    $"The document has no AcroForm, so field '{fieldName}' does not exist.");

            Core.Forms.PdfSignatureField? target = null;
            foreach (var field in form.GetSignatureFields())
            {
                if (!field.IsAddressableField) continue;
                if (field.FullyQualifiedName != fieldName) continue;
                target = field;
                break;
            }
            if (target == null)
                throw new PdfSigningException($"No signature field named '{fieldName}' was found.");

            var fieldDict = target.Dictionary;
            if (!fieldDict.IsIndirect)
                throw new PdfSigningException(
                    $"Signature field '{fieldName}' is not an indirect object, so its /SV entry " +
                    "cannot be written through an incremental update.");

            int nextObj = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;

            var svDict = SignatureSeedValue.BuildDigestMethodDictionary(digestMethodNames, required);
            svDict.ObjectNumber = nextObj++;
            svDict.GenerationNumber = 0;

            var newField = Core.Objects.PdfDictionary.CloneShallow(fieldDict);
            newField.Set(Core.Objects.PdfName.SV, new Core.Objects.PdfReference(svDict.ObjectNumber, 0));
            newField.ObjectNumber = fieldDict.ObjectNumber;
            newField.GenerationNumber = fieldDict.GenerationNumber;

            var objects = new List<Core.Writing.IndirectObject>
            {
                new Core.Writing.IndirectObject(svDict.ObjectNumber, 0, svDict),
                new Core.Writing.IndirectObject(newField.ObjectNumber, newField.GenerationNumber, newField),
            };

            int size = nextObj;
            var trailer = Core.Writing.PdfTrailerBuilder.ForUpdate(doc.Trailer, size);
            ms.Position = 0;
            Core.Writing.IncrementalUpdateWriter.WriteUpdate(
                ms, output, objects, trailer, doc.Parser.StartXrefOffset, size);
        }

        /// <summary>
        /// What revocation evidence the document's <c>/DSS</c> carries, and whether it is still
        /// current — the question a PAdES B-LT/B-LTA archival cycle turns on.
        /// </summary>
        /// <remarks>
        /// <para><b>This is not a verdict.</b> <see cref="VerifySignatures"/> reports whether the
        /// signatures verify <i>today</i>, computed over the <c>/DSS</c> among other things, and
        /// never says what the store contains. A caller deciding whether a document's LTV evidence
        /// needs refreshing needs the inventory, not the verdict: a signature can verify perfectly
        /// while the evidence behind it is weeks past its <c>nextUpdate</c>.</para>
        ///
        /// <para><b>Currency is judged against <paramref name="nowUtc"/></b> by the same predicate
        /// the signer uses when choosing what to embed, so the inventory cannot call a document
        /// fine that the signer would refuse to build. Pass the instant explicitly — a hidden clock
        /// would make the answer irreproducible.</para>
        ///
        /// <para><b><see cref="DssInventory.IsCurrent"/> judges the document-wide lists only, so
        /// read <see cref="DssInventory.VriEvidence"/> before concluding a refresh is needed.</b>
        /// ISO 32000-2 §12.8.4.3 lets a <c>/DSS</c> scope evidence to one signature under a
        /// <c>/VRI</c> key, and this library's own verifier treats such an entry as
        /// <i>authoritative</i> for that signature — so a document whose LTV data lives only there
        /// reports empty <see cref="DssInventory.OcspResponses"/> and <c>IsCurrent</c> false while
        /// being perfectly well covered. <c>VriEvidence</c> is what says so, keyed by signature.
        /// The pairing is stated here as well as on the model because this is where a caller
        /// looks first.</para>
        /// </remarks>
        /// <param name="input">The document to inspect. Left open, and its position restored.</param>
        /// <param name="nowUtc">The instant to judge currency against.</param>
        public static DssInventory GetDssInventory(Stream input, DateTime nowUtc)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            long start = input.CanSeek ? input.Position : 0L;
            try
            {
                using var doc = PdfDocument.Open(input);

                // Same reason as GetUnsignedSignatureFieldNames: an encrypted document has no
                // catalog to read, so the inventory would report HasDss = false — which its own
                // documentation defines as "the document carries no LTV evidence". That is a
                // statement about the file, and it would be false.
                if (doc.IsEncrypted)
                    throw new PdfSigningException(
                        "The document is encrypted, so its /DSS cannot be read. Decrypt it " +
                        "before taking an inventory.");

                return DssInventoryReader.Read(doc, nowUtc);
            }
            finally
            {
                if (input.CanSeek) input.Position = start;
            }
        }

        /// <summary>
        /// <see cref="GetDssInventory(Stream, DateTime)"/> judged against the current UTC time.
        /// </summary>
        public static DssInventory GetDssInventory(Stream input)
            => GetDssInventory(input, DateTime.UtcNow);

        /// <summary>
        /// Verifies all digital signatures in a PDF, including regular signatures and document
        /// timestamps.
        /// </summary>
        /// <param name="input">The PDF to verify.</param>
        /// <param name="options">
        /// Verification policy. Supply <see cref="SignatureVerificationOptions.TrustAnchors"/> to
        /// get a chain verdict — without anchors nothing can be reported as
        /// <see cref="PdfSignatureChainStatus.Trusted"/> and
        /// <see cref="SignatureVerificationResult.IsFullyTrusted"/> is always false.
        /// </param>
        public static IReadOnlyList<SignatureVerificationResult> VerifySignatures(
            Stream input, SignatureVerificationOptions? options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            SigningHelpers.RefuseEncryptedVerification(doc);
            return new PdfSignatureVerifier().VerifyAll(doc, options);
        }

        /// <summary>Verifies a specific signature field by name.</summary>
        public static SignatureVerificationResult VerifySignature(
            Stream input, string fieldName, SignatureVerificationOptions? options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            using var doc = PdfDocument.Open(input);
            return new PdfSignatureVerifier().Verify(doc, fieldName, options);
        }

        // ── Counter-signatures ───────────────────────────────────────────────

        /// <summary>
        /// Adds a CMS counter-signature to an existing digital signature field.
        /// </summary>
        public static void AddCounterSignature(
            Stream input, Stream output,
            string sigFieldName,
            string pfxPath, string pfxPassword,
            string digestAlgorithm = "SHA-256")
            => PdfCounterSigner.AddCounterSignature(
                input, output, sigFieldName, pfxPath, pfxPassword, digestAlgorithm);

        /// <summary>Adds a counter-signature using PFX bytes already in memory.</summary>
        public static void AddCounterSignature(
            Stream input, Stream output,
            string sigFieldName,
            byte[] pfxBytes, string pfxPassword,
            string digestAlgorithm = "SHA-256")
            => PdfCounterSigner.AddCounterSignature(
                input, output, sigFieldName, pfxBytes, pfxPassword, digestAlgorithm);
    }
}
