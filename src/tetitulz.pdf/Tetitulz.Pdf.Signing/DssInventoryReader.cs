// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Timestamping;

namespace Tetitulz.Pdf.Signing
{
    /// <summary>Whether every single response in an OCSP response said "good".</summary>
    internal enum OcspStatus { Good, NotGood }

    /// <summary>
    /// Reads a document's <c>/DSS</c> and reports what revocation evidence it carries and whether
    /// that evidence is still current.
    /// </summary>
    /// <remarks>
    /// <para><b>Nothing here re-implements the reading or the rule.</b> The <c>/DSS</c> is located
    /// and its streams enumerated by <see cref="RevocationChecker"/>, which is what verification
    /// itself uses, and currency is decided by <c>DssBuilder.IsOcspResponseCurrent</c> and
    /// <c>IsCrlCurrent</c> — the same predicates the signer applies when choosing what to embed,
    /// over the rule the verifier reads with (<c>Cms.RevocationFreshness</c>). An inventory that judged staleness
    /// by its own rule could report a document as fine that the signer would refuse to build, or
    /// the reverse.</para>
    ///
    /// <para><b>Why the two OCSP and CRL calls differ.</b> An absent <c>nextUpdate</c> bounds an
    /// OCSP response's age at seven days (RFC 6960 §4.2.2.1 reads the omission as "newer information
    /// is available all the time") and a CRL's only at the verifier's default cap (RFC 5280
    /// §5.1.2.5 makes it optional, and a CRL without one is still the CA's complete list as of
    /// <c>thisUpdate</c>). Calling the OCSP predicate for a CRL would refuse valid CRLs.</para>
    /// </remarks>
    internal static class DssInventoryReader
    {
        internal static DssInventory Read(PdfDocument doc, DateTime nowUtc)
        {
            var dss = RevocationChecker.ResolveDss(doc);
            if (dss == null)
            {
                return new DssInventory(false, 0,
                    Array.Empty<DssRevocationEntry>(), Array.Empty<DssRevocationEntry>(),
                    Array.Empty<string>(), nowUtc);
            }

            int certCount = 0;
            foreach (byte[] _ in RevocationChecker.ReadStreams(doc, dss, "Certs")) certCount++;

            var ocsp = new List<DssRevocationEntry>();
            foreach (byte[] bytes in RevocationChecker.ReadStreams(doc, dss, "OCSPs"))
                ocsp.Add(ReadOcsp(bytes, nowUtc));

            var crls = new List<DssRevocationEntry>();
            foreach (byte[] bytes in RevocationChecker.ReadStreams(doc, dss, "CRLs"))
                crls.Add(ReadCrl(bytes, nowUtc));

            var vriKeys = new List<string>();
            var vriEvidence = new List<DssVriEvidence>();
            if (doc.ResolveObject(dss[new PdfName("VRI")]) is PdfDictionary vri)
            {
                foreach (var key in vri.Keys)
                {
                    vriKeys.Add(key.Value);

                    // The keys INSIDE a /VRI entry are singular - /OCSP, /CRL, /Cert - not the
                    // document-wide /OCSPs, /CRLs, /Certs. Reading it with the plural names finds
                    // nothing and reports an entry carrying no evidence, which is exactly what a
                    // document with no evidence looks like. RevocationChecker.SelectSource has
                    // always used the singular forms; this reads them the same way rather than
                    // starting a second opinion about the same dictionary.
                    if (doc.ResolveObject(vri[key]) is not PdfDictionary entry) continue;

                    int entryCerts = 0;
                    foreach (byte[] _ in RevocationChecker.ReadStreams(doc, entry, "Cert")) entryCerts++;

                    var entryOcsp = new List<DssRevocationEntry>();
                    foreach (byte[] bytes in RevocationChecker.ReadStreams(doc, entry, "OCSP"))
                        entryOcsp.Add(ReadOcsp(bytes, nowUtc));

                    var entryCrls = new List<DssRevocationEntry>();
                    foreach (byte[] bytes in RevocationChecker.ReadStreams(doc, entry, "CRL"))
                        entryCrls.Add(ReadCrl(bytes, nowUtc));

                    vriEvidence.Add(new DssVriEvidence(key.Value, entryCerts, entryOcsp, entryCrls));
                }
            }

            return new DssInventory(true, certCount, ocsp, crls, vriKeys, vriEvidence, nowUtc);
        }

        private static DssRevocationEntry ReadOcsp(byte[] bytes, DateTime nowUtc)
        {
            DateTime? thisUpdate = null, nextUpdate = null, producedAt = null;
            var status = OcspStatus.Good;
            try
            {
                // Parsed once a buffer, as the verifier parses it: a /VRI of many entries naming one
                // response parsed it once an entry. A failure is parsed again for its message.
                var basic = RevocationChecker.CachedOcsp(bytes)
                            ?? new OcspResp(bytes).GetResponseObject() as BasicOcspResp;
                producedAt = basic?.ProducedAt.ToUniversalTime();
                SingleResp[]? responses = basic?.Responses;
                if (responses != null && responses.Length > 0)
                {
                    // The earliest thisUpdate and the earliest nextUpdate across the responses:
                    // the whole entry is only as current as its least current status.
                    foreach (SingleResp r in responses)
                    {
                        DateTime t = r.ThisUpdate.ToUniversalTime();
                        if (thisUpdate == null || t < thisUpdate) thisUpdate = t;

                        DateTime? n = r.NextUpdate?.ToUniversalTime();
                        if (n != null && (nextUpdate == null || n < nextUpdate)) nextUpdate = n;

                        // The STATUS, not just the timestamps. A response saying the certificate is
                        // revoked is perfectly fresh evidence — of the worst possible fact — and an
                        // inventory that reported only "current" would let a caller conclude the
                        // document needs nothing done. GetCertStatus returns null for "good".
                        if (r.GetCertStatus() != null) status = OcspStatus.NotGood;
                    }
                }
            }
            catch (Exception ex)
            {
                return new DssRevocationEntry(DssEvidenceKind.OcspResponse, bytes.Length,
                    null, null, false, "could not be parsed as an OCSP response: " + ex.Message);
            }

            if (thisUpdate == null)
            {
                return new DssRevocationEntry(DssEvidenceKind.OcspResponse, bytes.Length,
                    null, null, false, "carries no single response, so it asserts nothing.");
            }

            bool current = DssBuilder.IsOcspResponseCurrent(
                thisUpdate.Value, nextUpdate, producedAt, nowUtc, out string? issue);

            if (status == OcspStatus.NotGood)
            {
                // Reported through the same channel rather than a separate flag: a caller reading
                // "is this evidence usable as it stands" must not get "yes" for a response that
                // says the certificate is revoked or its status unknown. Refreshing will not help
                // such a document, which is exactly what the caller needs to be told.
                return new DssRevocationEntry(DssEvidenceKind.OcspResponse, bytes.Length,
                    thisUpdate, nextUpdate, false,
                    "reports the certificate as revoked or of unknown status" +
                    (issue != null ? "; also " + issue : "."));
            }

            return new DssRevocationEntry(DssEvidenceKind.OcspResponse, bytes.Length,
                thisUpdate, nextUpdate, current, issue);
        }

        private static DssRevocationEntry ReadCrl(byte[] bytes, DateTime nowUtc)
        {
            DateTime thisUpdate;
            DateTime? nextUpdate;
            try
            {
                X509Crl? crl = RevocationChecker.CachedCrl(bytes) ?? new X509CrlParser().ReadCrl(bytes);
                if (crl == null)
                {
                    return new DssRevocationEntry(DssEvidenceKind.Crl, bytes.Length,
                        null, null, false, "could not be parsed as a CRL.");
                }
                thisUpdate = crl.ThisUpdate.ToUniversalTime();
                nextUpdate = crl.NextUpdate?.ToUniversalTime();
            }
            catch (Exception ex)
            {
                return new DssRevocationEntry(DssEvidenceKind.Crl, bytes.Length,
                    null, null, false, "could not be parsed as a CRL: " + ex.Message);
            }

            bool current = DssBuilder.IsCrlCurrent(thisUpdate, nextUpdate, nowUtc, out string? issue);

            return new DssRevocationEntry(DssEvidenceKind.Crl, bytes.Length,
                thisUpdate, nextUpdate, current, issue);
        }
    }
}