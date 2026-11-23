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
using Tetitulz.Pdf.Core.Forms;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Core.Writing;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Timestamping;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>
    /// Appends revocation evidence to an already-signed document's <c>/DSS</c>, as one incremental
    /// revision — the writing half of a PAdES B-LT/B-LTA refresh.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists separately from signing.</b> <c>PdfSigner.EmbedDss</c> appends a
    /// <c>/DSS</c> only while signing, with evidence only <c>Sign</c> fetches — through the
    /// <see cref="DssRevision"/> writer below, which is shared. Archival is a refresh cycle,
    /// not a one-shot: a signature made before its CA's responder went dark, or one whose embedded
    /// responses have aged past their <c>nextUpdate</c>, can be rescued only by appending current
    /// evidence <i>later</i>. The alternative is to re-sign, which changes the signer and defeats
    /// the point.</para>
    ///
    /// <para><b>The merge is the whole risk.</b> An existing <c>/DSS</c> is carried forward by
    /// reference — its stream objects keep their numbers and are not rewritten — and the new
    /// evidence is appended to the arrays. Replacing the store instead would destroy the evidence
    /// an earlier B-LT signature depends on, which is a defect this repository has shipped once
    /// (<c>PdfCounterSigner</c> passing null CRLs into a method that replaces rather than merges)
    /// and which no test caught, because a lost array is an <i>absence</i>. <c>/VRI</c> is carried
    /// forward untouched for the same reason: its entries are keyed to specific signatures and this
    /// refresh knows nothing about which.</para>
    ///
    /// <para><b><see cref="AddVerificationInfo"/> is supplied evidence and never fetches.</b> That
    /// keeps the writing half testable without a network and lets a host that reaches its responder
    /// through a proxy — for which this assembly has no seam of its own — do the fetching itself.
    /// <see cref="RefreshVerificationInfo"/> is the convenience half and <b>does</b> reach the
    /// network by default; its <c>EvidenceProvider</c> is how that stays replaceable, so the
    /// property above is preserved rather than traded away.</para>
    /// </remarks>
    public static class PdfLtvRefresher
    {
        /// <summary>
        /// Gathers evidence for every signature already in <paramref name="input"/> and appends it,
        /// as one revision — the convenience half of a PAdES B-LT refresh.
        /// </summary>
        /// <remarks>
        /// <para><b>This reaches the network by default</b>, which
        /// <see cref="AddVerificationInfo"/> deliberately never does. It reads each signature's own
        /// certificate and chain out of its CMS, walks the chain through <c>DssBuilder</c> for an
        /// OCSP response (falling back to a CRL), and hands the merged result to
        /// <see cref="AddVerificationInfo"/> — which still does all the writing, so the merge rules
        /// that protect an existing <c>/DSS</c> are unchanged and untouched by this overload.</para>
        ///
        /// <para><b>The fetch is replaceable, not hard-wired.</b>
        /// <see cref="LtvRefreshOptions.EvidenceProvider"/> substitutes any source — a proxy this
        /// assembly has no seam for, a local store on an air-gapped host, or a stub in a test. That
        /// is what keeps the network out of the part that has to be verifiable.</para>
        ///
        /// <para><b>A signature that yields nothing is not a failure.</b> An unreachable responder
        /// contributes no evidence and a warning; the refresh still appends whatever the other
        /// signatures produced. What would be a failure is silently reporting success for a
        /// document that gained nothing, so read <see cref="LtvRefreshResult.OcspAdded"/> and
        /// <see cref="LtvRefreshResult.CrlsAdded"/> rather than the absence of an exception.</para>
        /// </remarks>
        /// <param name="input">The signed document. Not modified.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="options">How to obtain evidence; null fetches over the network.</param>
        /// <exception cref="PdfSigningException">
        /// The document is encrypted, carries no signature to refresh, or its cross-reference chain
        /// cannot be named.
        /// </exception>
        public static LtvRefreshResult RefreshVerificationInfo(
            Stream input, Stream output, LtvRefreshOptions? options = null)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));

            options ??= new LtvRefreshOptions();
            var warnings = new List<string>();
            var ocsp = new List<byte[]>();
            var crls = new List<byte[]>();
            var certs = new List<byte[]>();

            var seekable = SigningHelpers.EnsureSeekable(input);
            long start = seekable.CanSeek ? seekable.Position : 0L;

            int signatures = 0;
            var fetchedFor = new HashSet<string>(StringComparer.Ordinal);
            using (var doc = PdfDocument.Open(seekable))
            {
                // Refused here rather than deeper, for the reason AddVerificationInfo states: an
                // encrypted document has no readable catalog, so "no signatures found" would be a
                // statement about the file and it would be false.
                if (doc.IsEncrypted)
                    throw new PdfSigningException(
                        "The document is encrypted, so its signatures cannot be read. Decrypt it " +
                        "before refreshing its /DSS.");

                foreach (byte[] cmsBytes in SignatureCmsBlobs(doc))
                {
                    // The verifier's budget (PdfLimits.MaxSignaturesPerDocument): each signature here
                    // is a CMS parse and a round of network fetches, and a file chooses how many
                    // signature dictionaries it holds.
                    if (signatures >= PdfLimits.MaxSignaturesPerDocument)
                    {
                        warnings.Add($"The document holds more than {PdfLimits.MaxSignaturesPerDocument} " +
                                     "signatures (PdfLimits.MaxSignaturesPerDocument); the rest contributed no evidence.");
                        break;
                    }
                    signatures++;
                    if (!TryReadSigner(cmsBytes, out X509Certificate? signerCert,
                                       out IReadOnlyList<X509Certificate> chain, out string? why))
                    {
                        warnings.Add("A signature's CMS could not be read, so it contributed no " +
                                     "evidence: " + why);
                        continue;
                    }

                    // A signer and chain already fetched for are not fetched for again: the evidence would be the
                    // same, and a file chooses how many signatures repeat one CMS — each a round of network fetches.
                    // A caller's provider is asked for each signature, as it always was: it decides for itself.
                    if (options.EvidenceProvider == null && !fetchedFor.Add(EvidenceKey(signerCert!, chain)))
                        continue;

                    LtvEvidence? gathered = options.EvidenceProvider != null
                        ? options.EvidenceProvider(signerCert!, chain)
                        : FetchEvidence(signerCert!, chain, options.Network, warnings);

                    if (gathered != null)
                    {
                        if (gathered.OcspResponses != null) ocsp.AddRange(gathered.OcspResponses);
                        if (gathered.Crls != null) crls.AddRange(gathered.Crls);
                        if (gathered.Certificates != null) certs.AddRange(gathered.Certificates);
                    }

                    if (options.IncludeSignerChains)
                        foreach (var c in chain)
                            certs.Add(c.GetEncoded());
                }
            }

            if (signatures == 0)
                throw new PdfSigningException(
                    "The document carries no signature, so there is nothing to refresh. /DSS " +
                    "evidence is about the signatures a document already has.");

            if (seekable.CanSeek) seekable.Position = start;

            LtvRefreshResult written = AddVerificationInfo(
                seekable, output, new LtvEvidence(ocsp, crls, certs));

            if (warnings.Count == 0) return written;

            // The write's own warnings come first: they are about what reached the file, and these
            // are about what could not be gathered to put in it.
            var merged = new List<string>(written.Warnings);
            merged.AddRange(warnings);
            return new LtvRefreshResult(
                written.OcspAdded, written.CrlsAdded, written.CertificatesAdded,
                written.OcspCarriedForward, written.CrlsCarriedForward,
                written.CertificatesCarriedForward,
                merged);
        }

        /// <summary>The raw CMS bytes of every signature the document's AcroForm carries, once each.</summary>
        /// <remarks>
        /// <b>Deduplicated by the /V dictionary's identity, and the first draft was not.</b>
        /// <c>PdfAcroForm.GetSignatureFields</c> is deliberately permissive - it returns every node
        /// whose resolved /FT is /Sig, which includes a grouping parent and each widget kid - so one
        /// signature reached the provider three times and its evidence was appended three times.
        /// <c>Core.Forms.SignatureFieldWalk.DistinctSignatures</c> is the rule; this restated it while
        /// the walk lived in Base, which this assembly may not reference.
        /// </remarks>
        private static IEnumerable<byte[]> SignatureCmsBlobs(PdfDocument doc)
        {
            foreach (var (_, sig) in SignatureFieldWalk.DistinctSignatures(doc.AcroForm))
            {
                var contents = doc.ResolveObject(sig[PdfName.Contents]) as PdfString
                               ?? sig[PdfName.Contents] as PdfString;
                // Trimmed of the /Contents reserve padding — every consumer of this sequence parses
                // the bytes as CMS.
                byte[]? bytes = contents?.RawBytes;
                if (bytes != null && bytes.Length > 0)
                    yield return SigningDefaults.TrimToAsn1Object(bytes);
            }
        }

        /// <summary>Pulls the signer certificate and the chain its CMS carried.</summary>
        private static bool TryReadSigner(
            byte[] cmsBytes, out X509Certificate? signerCert,
            out IReadOnlyList<X509Certificate> chain, out string? why)
        {
            signerCert = null;
            chain = System.Array.Empty<X509Certificate>();
            why = null;

            try
            {
                var cms = new CmsSignedData(cmsBytes);
                var store = cms.GetCertificates();

                var all = new List<X509Certificate>();
                foreach (X509Certificate c in store.EnumerateMatches(null)) all.Add(c);

                foreach (SignerInformation signer in cms.GetSignerInfos().GetSigners())
                {
                    foreach (X509Certificate c in store.EnumerateMatches(signer.SignerID))
                    {
                        signerCert = c;
                        break;
                    }
                    if (signerCert != null) break;
                }

                if (signerCert == null) { why = "it names no signer certificate"; return false; }

                chain = all;
                return true;
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return false;
            }
        }

        /// <summary>A signer and the certificates its CMS carried, as one key: the input the default provider fetches for.</summary>
        private static string EvidenceKey(X509Certificate signerCert, IReadOnlyList<X509Certificate> chain)
        {
            var key = new System.Text.StringBuilder(Convert.ToBase64String(signerCert.GetEncoded()));
            foreach (var c in chain) key.Append('|').Append(Convert.ToBase64String(c.GetEncoded()));
            return key.ToString();
        }

        /// <summary>The default provider: walk the chain and fetch.</summary>
        private static LtvEvidence FetchEvidence(
            X509Certificate signerCert, IReadOnlyList<X509Certificate> chain,
            SigningNetworkOptions? network, List<string> warnings)
        {
            var asList = new List<X509Certificate>(chain);
            DssBuilder.TryBuildDss(asList, signerCert, network,
                                   out var ocsp, out var crls, out var fetchWarnings);
            if (fetchWarnings != null) warnings.AddRange(fetchWarnings);
            return new LtvEvidence(ocsp, crls, null);
        }

        /// <summary>
        /// Writes <paramref name="evidence"/> into a new revision of <paramref name="input"/>,
        /// merged with whatever <c>/DSS</c> the document already carries.
        /// </summary>
        /// <param name="input">The signed document. Not modified.</param>
        /// <param name="output">Receives the original bytes plus one appended revision.</param>
        /// <param name="evidence">The evidence to append.</param>
        /// <exception cref="PdfSigningException">
        /// The document is encrypted, its cross-reference chain cannot be named, or its
        /// <c>/Root</c> is not an indirect reference.
        /// </exception>
        public static LtvRefreshResult AddVerificationInfo(
            Stream input, Stream output, LtvEvidence evidence)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (output == null) throw new ArgumentNullException(nameof(output));
            if (evidence == null) throw new ArgumentNullException(nameof(evidence));

            // Validated BEFORE a byte reaches the caller's stream. The original document is copied
            // to `output` further down, and a null element would then throw out of WriteStream —
            // after a full, truncated, unusable file had already been written. The same reason
            // PdfDocumentTimestamper refuses an encrypted document up here rather than mid-write.
            RefuseNullElements(evidence.OcspResponses, "OcspResponses");
            RefuseNullElements(evidence.Crls, "Crls");
            RefuseNullElements(evidence.Certificates, "Certificates");

            var ms = SigningHelpers.EnsureSeekable(input);
            using var doc = PdfDocument.Open(ms);

            // Refused before a byte reaches the caller's stream, for the reason
            // PdfDocumentTimestamper states: writing a partial file and then throwing leaves a
            // corrupt output, and clear-text objects appended to an encrypted document would be
            // unreadable anyway.
            if (doc.IsEncrypted)
                throw new PdfSigningException(
                    "The document is encrypted, so its catalog cannot be superseded to carry /DSS.");

            SigningHelpers.RefuseUnnameableCrossReferenceChain(ms, doc.Parser.StartXrefOffset);

            // Planned before the copy, so every refusal still lands before the first byte is written.
            DssRevision revision = DssRevision.Plan(doc, evidence);

            ms.Position = 0;
            ms.CopyTo(output);

            LtvRefreshResult result = revision.AppendTo(output);
            output.Flush();
            return result;
        }

        /// <summary>
        /// One <c>/DSS</c> revision: everything read from the document it extends, then written in
        /// one pass after that document's last byte.
        /// </summary>
        /// <remarks>
        /// <para><b>The one writer of a <c>/DSS</c> revision</b>, <c>PdfSigner.EmbedDss</c>'s too, and
        /// it merges with the evidence already in the file. A <c>/DSS</c> holding only the new
        /// signer's responses would unreference every OCSP response, CRL and <c>/VRI</c> entry an
        /// earlier signature relies on — the replace-instead-of-merge defect this class exists to
        /// avoid — and a trailer spelled key by key would drop <c>/Info</c> and every other key from
        /// the newest revision, which ISO 32000-2 §7.5.6 does not allow ("the added trailer shall
        /// contain all the entries except the Prev entry … from the previous trailer"). That path
        /// needs a live responder, so it is held by sharing this writer, not by a test of its own.</para>
        ///
        /// <para><b>Reading and writing are separate steps</b> so the caller decides what happens
        /// between them: <see cref="AddVerificationInfo"/> copies the original out only after every
        /// refusal has had its chance, and the signer appends to the very stream the document was
        /// parsed from, which is safe only once nothing more is read from it.</para>
        /// </remarks>
        internal sealed class DssRevision
        {
            private readonly PdfDictionary _catalogUpdate;
            private readonly int _catalogObjNum;
            private readonly int _catalogGen;
            private readonly PdfDictionary _trailer;
            private readonly long _previousXrefOffset;
            private readonly int _firstNewObjNum;
            private readonly PdfArray _existingOcsp = new PdfArray();
            private readonly PdfArray _existingCrls = new PdfArray();
            private readonly PdfArray _existingCerts = new PdfArray();
            private readonly PdfObject? _existingVri;
            private readonly LtvEvidence _evidence;

            private DssRevision(PdfDocument doc, LtvEvidence evidence)
            {
                _evidence = evidence;

                var catalog = doc.Catalog?.Dictionary
                    ?? throw new PdfSigningException("The document has no catalog.");

                PdfObject? root = doc.Trailer?[PdfName.Root];
                _catalogObjNum = SigningHelpers.ObjectNumberOf(root);
                if (_catalogObjNum <= 0)
                    throw new PdfSigningException(
                        "/Root is not an indirect reference, so the catalog cannot be superseded to " +
                        "carry /DSS. Object 0 is reserved as the head of the free list.");

                // §7.5.6 — the catalog is superseded at the generation the cross-reference table
                // gives it, the same reading every other writer in this assembly uses. This read the
                // /Root reference's generation, which a damaged file can have disagree with the table.
                _catalogGen = Core.Writing.ExistingObjectGeneration.Of(
                    doc.CrossReferenceTable, _catalogObjNum, root);

                // Everything the existing /DSS already references, by reference. Not re-read and not
                // rewritten: these object numbers stay valid in the appended revision.
                var currentDss = RevocationChecker.ResolveDss(doc);
                if (currentDss != null)
                {
                    CopyRefs(doc, currentDss, "OCSPs", _existingOcsp);
                    CopyRefs(doc, currentDss, "CRLs", _existingCrls);
                    CopyRefs(doc, currentDss, "Certs", _existingCerts);
                    _existingVri = currentDss[new PdfName("VRI")];
                }

                // The WHOLE catalog: this object supersedes the existing one rather than merging with
                // it, so a stub here would destroy /Pages, /AcroForm and /Perms.
                _catalogUpdate = PdfDictionary.CloneShallow(catalog);
                _trailer = doc.Trailer!;
                _previousXrefOffset = doc.Parser.StartXrefOffset;
                _firstNewObjNum = doc.CrossReferenceTable.GetMaxObjectNumber() + 1;
            }

            /// <summary>Reads what the revision needs from <paramref name="doc"/>, refusing a document it cannot extend.</summary>
            /// <exception cref="PdfSigningException">No catalog, or a <c>/Root</c> that is not an indirect reference.</exception>
            internal static DssRevision Plan(PdfDocument doc, LtvEvidence evidence) => new DssRevision(doc, evidence);

            /// <summary>
            /// Writes the revision at <paramref name="file"/>'s current position, which must be the end
            /// of the document <see cref="Plan"/> read.
            /// </summary>
            internal LtvRefreshResult AppendTo(Stream file)
            {
                int nextObjNum = _firstNewObjNum;
                var writer = new PdfWriter(file);
                var xrefEntries = new List<(int objNum, int gen, long offset)>();

                var ocspRefs = new PdfArray();
                foreach (var r in _existingOcsp) ocspRefs.Add(r);
                foreach (byte[] b in _evidence.OcspResponses)
                    ocspRefs.Add(WriteStream(writer, b, ref nextObjNum, xrefEntries));

                var crlRefs = new PdfArray();
                foreach (var r in _existingCrls) crlRefs.Add(r);
                foreach (byte[] b in _evidence.Crls)
                    crlRefs.Add(WriteStream(writer, b, ref nextObjNum, xrefEntries));

                var certRefs = new PdfArray();
                foreach (var r in _existingCerts) certRefs.Add(r);
                foreach (byte[] b in _evidence.Certificates)
                    certRefs.Add(WriteStream(writer, b, ref nextObjNum, xrefEntries));

                var dss = new PdfDictionary();
                dss.Set(PdfName.Type, PdfName.DSS);
                if (ocspRefs.Count > 0) dss.Set(new PdfName("OCSPs"), ocspRefs);
                if (crlRefs.Count > 0) dss.Set(new PdfName("CRLs"), crlRefs);
                if (certRefs.Count > 0) dss.Set(new PdfName("Certs"), certRefs);
                // Carried forward untouched: /VRI entries are keyed to individual signatures by the
                // SHA-1 of their /Contents, and this revision knows nothing about which signature its
                // evidence belongs to. Dropping it would silently un-scope evidence that was scoped.
                if (_existingVri != null) dss.Set(new PdfName("VRI"), _existingVri);

                int dssObjNum = nextObjNum++;
                xrefEntries.Add((dssObjNum, 0, writer.WriteIndirectObject(dssObjNum, 0, dss)));

                var catalogUpdate = PdfDictionary.CloneShallow(_catalogUpdate);
                catalogUpdate.Set(PdfName.Type, PdfName.Catalog);
                catalogUpdate.Set(PdfName.DSS, new PdfReference(dssObjNum, 0));
                xrefEntries.Add((_catalogObjNum, _catalogGen,
                    writer.WriteIndirectObject(_catalogObjNum, _catalogGen, catalogUpdate)));

                long xrefOffset = writer.WriteXrefTable(xrefEntries);

                // Through PdfTrailerBuilder, not hand-spelled. A key-by-key trailer carries exactly
                // the keys it names and drops every other key the document's own trailer held from
                // the NEWEST revision — producer-specific entries above all. PdfDocumentTimestamper
                // and PdfSigner.EmbedDss both made that mistake, and TimestampTrailerKeyPreservationTests
                // pins it by reading the raw bytes of the last trailer. The loss is invisible through
                // PdfDocument, which merges trailers along the /Prev chain, so only those raw bytes
                // show it. ForAppendedRevision uses a DENY list: the cross-reference-section keys are
                // stripped and everything else survives.
                var trailer = PdfTrailerBuilder.ForAppendedRevision(
                    _trailer, nextObjNum, _previousXrefOffset, xrefOffset);
                trailer.Set(PdfName.Root, new PdfReference(_catalogObjNum, _catalogGen));

                writer.WriteTrailer(trailer, xrefOffset);

                var warnings = new List<string>();
                if (_evidence.IsEmpty)
                    warnings.Add("No evidence was supplied, so the appended revision only restates the existing /DSS.");

                return new LtvRefreshResult(
                    _evidence.OcspResponses.Count, _evidence.Crls.Count, _evidence.Certificates.Count,
                    _existingOcsp.Count, _existingCrls.Count, _existingCerts.Count,
                    warnings);
            }
        }

        /// <summary>
        /// Refuses a null entry in an evidence list. A null reaches <see cref="WriteStream"/> as a
        /// <c>NullReferenceException</c>, and by then the caller's stream holds a partial file.
        /// </summary>
        private static void RefuseNullElements(IReadOnlyList<byte[]> items, string name)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null)
                    throw new ArgumentException(
                        $"LtvEvidence.{name}[{i}] is null. Evidence entries must be DER bytes.",
                        "evidence");
            }
        }

        private static PdfReference WriteStream(PdfWriter writer, byte[] data,
            ref int nextObjNum, List<(int, int, long)> xref)
        {
            int objNum = nextObjNum++;
            var stream = new PdfStream();
            stream.RawData = data;
            stream.Set(PdfName.Length, new PdfNumber(data.Length));
            xref.Add((objNum, 0, writer.WriteIndirectObject(objNum, 0, stream)));
            return new PdfReference(objNum, 0);
        }

        /// <summary>
        /// Copies the <b>references</b> from one /DSS array, never the resolved objects. Copying
        /// the objects would rewrite every existing stream into the new revision and grow the file
        /// by the whole store on each refresh.
        /// </summary>
        private static void CopyRefs(PdfDocument doc, PdfDictionary dss, string key, PdfArray into)
        {
            if (doc.ResolveObject(dss[new PdfName(key)]) is not PdfArray arr) return;
            foreach (var item in arr) if (item is PdfReference r) into.Add(r);
        }
    }
}
