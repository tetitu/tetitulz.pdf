// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Text;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Document;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Models;

namespace Tetitulz.Pdf.Signing.Cms
{
    /// <summary>
    /// Determines a signer certificate's revocation status from revocation data embedded in the
    /// PDF's <c>/DSS</c> (Document Security Store / LTV). OCSP responses and CRLs are parsed and
    /// their <em>signatures verified</em> (a forged "good" must never be trusted) before their
    /// status is read, and their <em>freshness</em> is checked so a validly-signed but stale
    /// response cannot vouch for a certificate revoked after it was issued. Live OCSP/CRL fetching
    /// over the network is out of scope — only data already in the document is consulted, so the
    /// check is deterministic and offline.
    /// </summary>
    internal static class RevocationChecker
    {
        /// <summary>
        /// A caller's time as UTC, the clock the data is read in (every <c>thisUpdate</c>,
        /// <c>nextUpdate</c> and <c>producedAt</c> is converted to it): a Local time was compared as if
        /// it were UTC, off by the zone's offset. An Unspecified time is taken as UTC, as before.
        /// </summary>
        private static DateTime? Utc(DateTime? time)
            => time.HasValue && time.Value.Kind == DateTimeKind.Local ? time.Value.ToUniversalTime() : time;

        /// <summary>
        /// Resolves the revocation status of a single certificate from the document's /DSS.
        /// </summary>
        /// <param name="doc">Document whose /DSS is consulted.</param>
        /// <param name="signerCert">Certificate whose status is wanted.</param>
        /// <param name="issuerCert">Its issuer, needed to verify responder signatures.</param>
        /// <param name="warnings">Diagnostics sink.</param>
        /// <param name="options">Freshness policy; defaults are used when null.</param>
        /// <param name="assessmentTime">Time to assess freshness at; defaults to now.</param>
        /// <param name="signatureContents">
        /// Raw /Contents bytes of the signature being validated. When supplied, the /DSS /VRI
        /// entry for that signature is read as well as the document-wide /OCSPs and /CRLs lists —
        /// never instead of them (<see cref="Sources"/>).
        /// </param>
        public static PdfRevocationStatus Check(
            PdfDocument doc, X509Certificate signerCert, X509Certificate? issuerCert, List<string> warnings,
            SignatureVerificationOptions? options = null,
            DateTime? assessmentTime = null,
            byte[]? signatureContents = null)
        {
            var opts = SignatureVerificationOptions.OrDefault(options);
            DateTime at = Utc(assessmentTime) ?? DateTime.UtcNow;

            var dss = ResolveDss(doc);
            if (dss == null) return PdfRevocationStatus.NotChecked;

            bool anyData = false;
            bool anyGood = false;
            // What each entry said, folded into one line a message: a list of a thousand entries
            // failing alike wrote a thousand lines a certificate.
            var said = new List<string>();
            try
            {
                foreach (var (ocspKey, crlKey, scope) in Sources(doc, dss, signatureContents))
                {
                    foreach (var bytes in ReadStreams(doc, scope, ocspKey, warnings))
                    {
                        anyData = true;
                        switch (CheckOcsp(bytes, signerCert, issuerCert, said, opts, at))
                        {
                            case PdfRevocationStatus.Revoked:
                                warnings.Add($"OCSP response in /DSS reports certificate '{Name(signerCert)}' as REVOKED.");
                                return PdfRevocationStatus.Revoked;
                            case PdfRevocationStatus.Good:
                                anyGood = true;
                                break;
                        }
                    }

                    foreach (var bytes in ReadStreams(doc, scope, crlKey, warnings))
                    {
                        anyData = true;
                        switch (CheckCrl(bytes, signerCert, issuerCert, said, opts, at))
                        {
                            case PdfRevocationStatus.Revoked:
                                warnings.Add($"CRL in /DSS lists certificate '{Name(signerCert)}' as REVOKED.");
                                return PdfRevocationStatus.Revoked;
                            case PdfRevocationStatus.Good:
                                anyGood = true;
                                break;
                        }
                    }
                }
            }
            finally
            {
                Fold(said, warnings);
            }

            if (anyGood) return PdfRevocationStatus.Good;
            return anyData ? PdfRevocationStatus.Unknown : PdfRevocationStatus.NotChecked;
        }

        /// <summary>Adds each distinct message of <paramref name="said"/> once, with how many entries said it.</summary>
        private static void Fold(List<string> said, List<string> warnings)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();
            foreach (string s in said)
            {
                if (counts.TryGetValue(s, out int n)) counts[s] = n + 1;
                else { counts[s] = 1; order.Add(s); }
            }
            foreach (string s in order)
                warnings.Add(counts[s] == 1 ? s : $"{s} ({counts[s]} entries)");
        }

        /// <summary>
        /// Resolves revocation status for the whole chain — the leaf and every intermediate CA
        /// above it — and returns the worst outcome. A revoked intermediate invalidates every
        /// certificate beneath it, so checking only the leaf would leave a real hole in LTV
        /// validation.
        /// </summary>
        /// <param name="chain">Ordered leaf-first chain, as built by
        /// <see cref="CertificateChainValidator.BuildChain"/>.</param>
        /// <param name="doc">
        /// The signed document, read for its <c>/DSS</c> store. Revocation data is taken from the
        /// file only — this never goes to the network.
        /// </param>
        /// <param name="warnings">Accumulates why a certificate could not be checked conclusively.</param>
        /// <param name="options">
        /// Verification policy, notably the freshness window; <see langword="null"/> uses the
        /// defaults.
        /// </param>
        /// <param name="assessmentTime">
        /// The trusted time to judge freshness against, or <see langword="null"/> for now. Comes
        /// from a verified timestamp when the document has one.
        /// </param>
        /// <param name="signatureContents">
        /// The signature's own CMS bytes, used to scope a <c>/VRI</c> lookup to this signature.
        /// </param>
        public static PdfRevocationStatus CheckChain(
            PdfDocument doc, IList<X509Certificate> chain, List<string> warnings,
            SignatureVerificationOptions? options = null,
            DateTime? assessmentTime = null,
            byte[]? signatureContents = null)
        {
            var opts = SignatureVerificationOptions.OrDefault(options);
            if (chain == null || chain.Count == 0) return PdfRevocationStatus.NotChecked;

            // Leaf first: its status is the one callers care most about, and a revoked leaf
            // short-circuits the rest.
            X509Certificate leaf = chain[0];
            X509Certificate? leafIssuer = chain.Count > 1 ? chain[1]
                : (leaf.IssuerDN.Equivalent(leaf.SubjectDN, true) ? leaf : null);

            var status = Check(doc, leaf, leafIssuer, warnings, opts, assessmentTime, signatureContents);
            if (status == PdfRevocationStatus.Revoked || !opts.CheckIntermediateRevocation)
                return status;

            // Intermediates: chain[i] is issued by chain[i+1]. The final entry is either a
            // self-signed root (its own issuer) or the top of what the CMS supplied.
            for (int i = 1; i < chain.Count; i++)
            {
                X509Certificate ca = chain[i];

                // The issuer above, or none: a self-signed certificate ends the path, and nothing
                // issues a CRL about it — a list it signs itself cannot speak for it (RFC 5280 §6.1
                // validates the path below a trust anchor, not the anchor). Taken as its own issuer and
                // checked against the leaf's CRL, it would read Unknown for every chain, since a
                // partitioned CRL (CrlScope) does not vouch for a certificate outside its scope, which a
                // root always is.
                X509Certificate? caIssuer = i + 1 < chain.Count ? chain[i + 1] : null;
                if (caIssuer == null) continue; // cannot verify responder signatures without it

                var caStatus = Check(doc, ca, caIssuer, warnings, opts, assessmentTime, signatureContents);
                if (caStatus == PdfRevocationStatus.Revoked)
                {
                    warnings.Add($"Intermediate CA '{Name(ca)}' is REVOKED; every certificate below it is untrustworthy.");
                    return PdfRevocationStatus.Revoked;
                }
                // An intermediate we could not resolve degrades a Good leaf to Unknown: the chain
                // as a whole has not been shown to be unrevoked.
                if (caStatus == PdfRevocationStatus.Unknown && status == PdfRevocationStatus.Good)
                    status = PdfRevocationStatus.Unknown;
            }

            return status;
        }

        // ---------------------------------------------------------------- online

        /// <summary>Fetches a validated OCSP response about <c>subject</c> from the responder its AIA names, or null.</summary>
        internal delegate byte[]? OcspFetcher(X509Certificate subject, X509Certificate issuer);

        /// <summary>Fetches a CRL from <c>subject</c>'s distribution points, or null.</summary>
        internal delegate byte[]? CrlFetcher(X509Certificate subject);

        /// <summary>
        /// <see cref="CheckChain"/>, and then — when <see cref="SignatureVerificationOptions.OnlineRevocation"/>
        /// is set and the document did not settle the status — the same question asked online, through
        /// the LTV embedder's guarded fetches (<see cref="Timestamping.DssBuilder"/>), judged by the
        /// same functions at the current time. Nothing fetched is written back.
        /// </summary>
        public static PdfRevocationStatus CheckChainWithOnlineFallback(
            PdfDocument doc, IList<X509Certificate> chain, List<string> warnings,
            SignatureVerificationOptions? options = null,
            DateTime? assessmentTime = null,
            byte[]? signatureContents = null)
        {
            var opts = SignatureVerificationOptions.OrDefault(options);
            return CheckChainWithOnlineFallback(doc, chain, warnings, opts, assessmentTime, signatureContents,
                (subject, issuer) => Timestamping.DssBuilder.FetchOcspResponse(subject, issuer, opts.Network),
                subject => Timestamping.DssBuilder.FetchCrl(subject, opts.Network));
        }

        /// <summary>
        /// The seam the tests use: <see cref="CheckChainWithOnlineFallback(PdfDocument, IList{X509Certificate}, List{string}, SignatureVerificationOptions, DateTime?, byte[])"/>
        /// with the fetches supplied, so a synthetic responder stands in for the network.
        /// </summary>
        internal static PdfRevocationStatus CheckChainWithOnlineFallback(
            PdfDocument doc, IList<X509Certificate> chain, List<string> warnings,
            SignatureVerificationOptions? options, DateTime? assessmentTime, byte[]? signatureContents,
            OcspFetcher fetchOcsp, CrlFetcher fetchCrl)
        {
            var opts = SignatureVerificationOptions.OrDefault(options);
            var embedded = CheckChain(doc, chain, warnings, opts, assessmentTime, signatureContents);
            if (!opts.OnlineRevocation) return embedded;
            if (embedded == PdfRevocationStatus.Good || embedded == PdfRevocationStatus.Revoked) return embedded;
            if (chain == null || chain.Count == 0) return embedded;

            // Online data is current: it answers whether the certificate is revoked now, so it is
            // judged now. A trusted timestamp still fixed the chain's validity assessment above.
            DateTime now = DateTime.UtcNow;
            assessmentTime = Utc(assessmentTime);
            if (assessmentTime.HasValue && assessmentTime.Value < now.AddMinutes(-1))
                warnings.Add($"Online revocation status is as of now ({now:u}); the signature's assessment time is {assessmentTime.Value:u}.");

            X509Certificate leaf = chain[0];
            X509Certificate? leafIssuer = chain.Count > 1 ? chain[1]
                : (leaf.IssuerDN.Equivalent(leaf.SubjectDN, true) ? leaf : null);

            var status = CheckOnline(leaf, leafIssuer, warnings, opts, now, fetchOcsp, fetchCrl);
            if (status == PdfRevocationStatus.Revoked || !opts.CheckIntermediateRevocation)
                return Combine(embedded, status);

            for (int i = 1; i < chain.Count; i++)
            {
                X509Certificate? caIssuer = i + 1 < chain.Count ? chain[i + 1] : null;
                if (caIssuer == null) continue;   // a self-signed top is outside every scope (CheckChain)
                var caStatus = CheckOnline(chain[i], caIssuer, warnings, opts, now, fetchOcsp, fetchCrl);
                if (caStatus == PdfRevocationStatus.Revoked)
                {
                    warnings.Add($"Intermediate CA '{Name(chain[i])}' is REVOKED (online); every certificate below it is untrustworthy.");
                    return PdfRevocationStatus.Revoked;
                }
                if (caStatus != PdfRevocationStatus.Good && status == PdfRevocationStatus.Good)
                    status = PdfRevocationStatus.Unknown;
            }
            return Combine(embedded, status);
        }

        /// <summary>The document's verdict and the online one together: a revocation from either, else the better of the two.</summary>
        private static PdfRevocationStatus Combine(PdfRevocationStatus embedded, PdfRevocationStatus online)
        {
            if (online == PdfRevocationStatus.Revoked || embedded == PdfRevocationStatus.Revoked) return PdfRevocationStatus.Revoked;
            if (online == PdfRevocationStatus.Good) return PdfRevocationStatus.Good;
            return online == PdfRevocationStatus.Unknown || embedded == PdfRevocationStatus.Unknown
                ? PdfRevocationStatus.Unknown
                : PdfRevocationStatus.NotChecked;
        }

        /// <summary>
        /// One certificate's status asked online: the OCSP responder first, then the CRL
        /// distribution points, each answer judged by the function the embedded data goes through.
        /// A fetch that throws is a warning and <see cref="PdfRevocationStatus.Unknown"/>.
        /// </summary>
        private static PdfRevocationStatus CheckOnline(
            X509Certificate cert, X509Certificate? issuer, List<string> warnings,
            SignatureVerificationOptions opts, DateTime now, OcspFetcher fetchOcsp, CrlFetcher fetchCrl)
        {
            if (issuer == null)
            {
                warnings.Add($"Certificate '{Name(cert)}' has no issuer in the chain; nothing online could be verified for it.");
                return PdfRevocationStatus.Unknown;
            }

            bool anyData = false;
            byte[]? ocsp;
            try { ocsp = fetchOcsp(cert, issuer); }
            catch (Exception ex) { warnings.Add($"Online OCSP fetch for '{Name(cert)}' failed: {ex.Message}"); ocsp = null; }
            if (ocsp != null)
            {
                anyData = true;
                switch (CheckOcsp(ocsp, cert, issuer, warnings, opts, now))
                {
                    case PdfRevocationStatus.Revoked:
                        warnings.Add($"Online OCSP response reports certificate '{Name(cert)}' as REVOKED.");
                        return PdfRevocationStatus.Revoked;
                    case PdfRevocationStatus.Good:
                        warnings.Add($"Revocation status of '{Name(cert)}' resolved online by OCSP (not written to the document).");
                        return PdfRevocationStatus.Good;
                }
            }

            byte[]? crl;
            try { crl = fetchCrl(cert); }
            catch (Exception ex) { warnings.Add($"Online CRL fetch for '{Name(cert)}' failed: {ex.Message}"); crl = null; }
            if (crl != null)
            {
                anyData = true;
                switch (CheckCrl(crl, cert, issuer, warnings, opts, now))
                {
                    case PdfRevocationStatus.Revoked:
                        warnings.Add($"Online CRL lists certificate '{Name(cert)}' as REVOKED.");
                        return PdfRevocationStatus.Revoked;
                    case PdfRevocationStatus.Good:
                        warnings.Add($"Revocation status of '{Name(cert)}' resolved online by CRL (not written to the document).");
                        return PdfRevocationStatus.Good;
                }
            }

            if (!anyData)
                warnings.Add($"No online revocation data could be fetched for '{Name(cert)}' (no reachable OCSP responder or CRL distribution point).");
            return anyData ? PdfRevocationStatus.Unknown : PdfRevocationStatus.NotChecked;
        }

        private static string? Name(X509Certificate cert)
        {
            try
            {
                var cn = cert.SubjectDN.GetValueList(Org.BouncyCastle.Asn1.X509.X509Name.CN);
                if (cn != null && cn.Count > 0) return cn[0]?.ToString();
                return cert.SubjectDN.ToString();
            }
            catch { return "<unknown>"; }
        }

        // ---------------------------------------------------------------- OCSP

        private static PdfRevocationStatus CheckOcsp(
            byte[] bytes, X509Certificate signer, X509Certificate? issuer, List<string> warnings,
            SignatureVerificationOptions opts, DateTime assessmentTime)
        {
            ParsedOcspEntry parsedOcsp = ParsedOcsp(bytes);
            BasicOcspResp? basic = parsedOcsp.Basic;
            if (basic == null) return PdfRevocationStatus.Unknown;

            // Locate the single response for this certificate — its serial and, since serials are
            // unique only within an issuer, the issuer hashes under the response's own algorithm.
            // The one matcher the LTV embedder uses before writing a response into /DSS.
            SingleResp? match = OcspSingleResponse.Find(basic, signer, issuer);
            if (match == null) return PdfRevocationStatus.Unknown; // not about this certificate

            // RFC 6960 §3.2: verify the responder's signature before relying on the status.
            // The responder is the issuer directly, or a delegated cert embedded in the response —
            // the one search the LTV embedder makes before writing a response into /DSS.
            // Kept with the parse, by issuer and time, as a CRL's signature check is: every certificate of every
            // signature asks the same response again, and the search tries each certificate it carries.
            string? responder;
            if (issuer == null)
                responder = OcspSingleResponse.WhyTheResponderCannotVouch(basic, issuer, assessmentTime);
            else
                lock (parsedOcsp)
                {
                    if (!parsedOcsp.Responder.TryGetValue((issuer, assessmentTime), out responder))
                        parsedOcsp.Responder[(issuer, assessmentTime)] = responder =
                            OcspSingleResponse.WhyTheResponderCannotVouch(basic, issuer, assessmentTime);
                }
            if (responder != null)
            {
                warnings.Add($"OCSP response {responder} Status ignored.");
                return PdfRevocationStatus.Unknown;
            }

            // CertStatus: null = good, RevokedStatus = revoked, anything else (UnknownStatus) = unknown.
            var status = match.GetCertStatus();
            if (status is RevokedStatus) return PdfRevocationStatus.Revoked;
            if (status != null) return PdfRevocationStatus.Unknown;

            // Good — but only if the response is actually current. A validly-signed response
            // produced BEFORE the certificate was revoked still says "good" forever, so replaying
            // an old one would defeat revocation entirely.
            DateTime? nextUpdate = null;
            DateTime thisUpdate;
            DateTime producedAt;
            try
            {
                thisUpdate = match.ThisUpdate.ToUniversalTime();
                nextUpdate = match.NextUpdate?.ToUniversalTime();
                producedAt = basic.ProducedAt.ToUniversalTime();
            }
            catch { return PdfRevocationStatus.Unknown; }

            string? stale = AssessFreshness("OCSP response", thisUpdate, nextUpdate, producedAt, assessmentTime, opts);
            if (stale != null)
            {
                warnings.Add(stale + " Treating the 'good' status as unknown.");
                return PdfRevocationStatus.Unknown;
            }

            return PdfRevocationStatus.Good;
        }

        // ---------------------------------------------------------------- CRL

        private static PdfRevocationStatus CheckCrl(
            byte[] bytes, X509Certificate signer, X509Certificate? issuer, List<string> warnings,
            SignatureVerificationOptions opts, DateTime assessmentTime)
        {
            ParsedCrlEntry parsed = ParsedCrl(bytes);
            X509Crl? crl = parsed.Crl;
            if (crl == null) return PdfRevocationStatus.Unknown;

            // The CRL must be issued by the certificate's issuer and carry a valid signature,
            // otherwise its contents cannot be trusted. The check is kept with the parse, by issuer:
            // every certificate of every signature asks the same list again.
            if (issuer == null) return PdfRevocationStatus.Unknown;
            if (!crl.IssuerDN.Equivalent(issuer.SubjectDN, true)) return PdfRevocationStatus.Unknown;
            bool valid;
            lock (parsed)
            {
                if (!parsed.SignatureValid.TryGetValue(issuer, out valid))
                {
                    try { crl.Verify(PublicKeyCost.KeyOf(issuer)); valid = true; }
                    catch { valid = false; }
                    parsed.SignatureValid[issuer] = valid;
                }
            }
            if (!valid)
            {
                warnings.Add("CRL signature verification failed; status ignored.");
                return PdfRevocationStatus.Unknown;
            }

            // A listed certificate is revoked regardless of how old the CRL is — revocation is
            // not undone by staleness, so this check comes before the freshness gate. It comes
            // before the scope gate too: a listing in a partitioned or delta CRL is a revocation.
            if (crl.GetRevokedCertificate(signer.SerialNumber) != null)
                return PdfRevocationStatus.Revoked;

            // Absence from a CRL that is not THIS certificate's CRL proves nothing either: a CA's
            // CA-only list, a delta, a per-reason or per-distribution-point partition are all
            // genuine, signed and current, and silent about a certificate outside their scope.
            // Reading "not listed here" as Good would let whoever assembled the /DSS pick the
            // partition that does not list the certificate (RFC 5280 §6.3.3).
            string? scope = CrlScope.WhyItCannotVouchFor(crl, signer);
            if (scope != null)
            {
                warnings.Add($"CRL {scope} (RFC 5280 §6.3.3). Absence from it does not prove the certificate is unrevoked.");
                return PdfRevocationStatus.Unknown;
            }

            // Absence from a STALE CRL proves nothing: the certificate may have been revoked
            // after the CRL was issued. Only a current CRL can vouch for "not revoked".
            string? stale = AssessFreshness("CRL", crl.ThisUpdate.ToUniversalTime(),
                crl.NextUpdate?.ToUniversalTime(), producedAt: null, assessmentTime, opts);
            if (stale != null)
            {
                warnings.Add(stale + " Absence from it does not prove the certificate is unrevoked.");
                return PdfRevocationStatus.Unknown;
            }

            return PdfRevocationStatus.Good;
        }

        // ---------------------------------------------------------------- freshness

        /// <summary>
        /// Returns null when the revocation data is current at <paramref name="assessmentTime"/>,
        /// otherwise a human-readable reason it cannot be relied on — the one rule the LTV embedder
        /// also asks (<see cref="RevocationFreshness"/>), under this call's skew and age cap.
        /// </summary>
        private static string? AssessFreshness(
            string what, DateTime thisUpdate, DateTime? nextUpdate, DateTime? producedAt,
            DateTime assessmentTime, SignatureVerificationOptions opts)
        {
            string? reason = RevocationFreshness.WhyNotCurrent(
                thisUpdate, nextUpdate, producedAt, assessmentTime,
                opts.ClockSkew, opts.MaxRevocationDataAge);
            return reason == null ? null : what + " " + reason;
        }

        // ---------------------------------------------------------------- /DSS access

        internal static PdfDictionary? ResolveDss(PdfDocument doc)
        {
            var catalog = doc.Catalog?.Dictionary;
            if (catalog == null) return null;
            var obj = catalog[PdfName.DSS];
            return doc.ResolveObject(obj) as PdfDictionary ?? obj as PdfDictionary;
        }

        /// <summary>
        /// The revocation lists to read: the signature's own /VRI entry when there is one (ISO 32000-2
        /// §12.8.4.3 — keyed by the uppercase hex SHA-1 of its /Contents, holding its /OCSP and /CRL),
        /// then the document-wide /OCSPs and /CRLs, always.
        /// </summary>
        /// <remarks>
        /// The entry is read beside the document-wide lists, never instead of them. Evidence is scoped
        /// per certificate already — an OCSP response by the certificate its <c>SingleResponse</c> names
        /// (<see cref="OcspSingleResponse.Find"/>), a CRL by issuer, signature, scope and serial — so a
        /// list kept for another signature cannot vouch for this one; read alone, an entry a later
        /// revision added, empty, hid a listing the document-wide CRL held, and a listing anywhere is
        /// decisive.
        /// </remarks>
        private static IEnumerable<(string ocspKey, string crlKey, PdfDictionary scope)> Sources(
            PdfDocument doc, PdfDictionary dss, byte[]? signatureContents)
        {
            if (signatureContents != null && signatureContents.Length > 0
                && doc.ResolveObject(dss[new PdfName("VRI")]) is PdfDictionary vri)
            {
                string? key = Sha1Hex(signatureContents);
                if (key != null && doc.ResolveObject(vri[new PdfName(key)]) is PdfDictionary entry)
                    yield return ("OCSP", "CRL", entry);
            }
            yield return ("OCSPs", "CRLs", dss);
        }

        private static string? Sha1Hex(byte[] data)
        {
            try
            {
                byte[] hash = DigestUtilities.CalculateDigest(PdfDigestOids.Sha1Name, data);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("X2"));
                return sb.ToString();
            }
            catch { return null; }
        }

        /// <summary>The most streams one <c>/DSS</c> or <c>/VRI</c> list is read for.</summary>
        internal const int MaxStreamsPerList = 1000;

        /// <summary>Streams <see cref="ReadStreams"/> has read on the calling thread: the evidence that each is read once.</summary>
        [ThreadStatic] internal static long StreamsRead;

        /// <summary>
        /// The decoded streams a <c>/DSS</c> or <c>/VRI</c> list names: each stream once, however often
        /// the list names it; decoded no further than <see cref="Timestamping.DssBuilder.MaxResponseBytes"/>,
        /// the most the LTV embedder accepts from a responder; at most <see cref="MaxStreamsPerList"/>.
        /// What could not be read, and a list cut short, is said in <paramref name="warnings"/>.
        /// </summary>
        /// <remarks>
        /// Each was decoded as listed, under the 512 MB per-stream ceiling sized for images, a failure
        /// swallowed — and every certificate of every signature's chain asks the list again, so one
        /// CRL listed a hundred thousand times was a hundred thousand parses and signature checks a
        /// certificate. A decoded stream hands back the same buffer each time it is asked, which is
        /// what lets <see cref="ParsedCrl"/> and <see cref="ParsedOcsp"/> parse it once.
        /// </remarks>
        internal static IEnumerable<byte[]> ReadStreams(PdfDocument doc, PdfDictionary source, string key,
            List<string>? warnings = null)
        {
            if (doc.ResolveObject(source[new PdfName(key)]) is not PdfArray arr) yield break;
            DocumentReads reads = ReadsOf(doc);
            var seen = new HashSet<PdfStream>(PdfReferenceEquality.Instance);
            int unreadable = 0;
            string? firstReason = null;
            foreach (var item in arr)
            {
                if (doc.ResolveObject(item) is not PdfStream stream || !seen.Add(stream)) continue;
                if (seen.Count > MaxStreamsPerList)
                {
                    warnings?.Add($"/{key} names more than {MaxStreamsPerList} streams; the rest were not read.");
                    break;
                }
                if (!reads.TryDecode(stream, out byte[]? data, out string? reason))
                {
                    unreadable++;
                    firstReason ??= reason;
                    continue;
                }
                if (data != null && data.Length > 0) yield return data;
            }
            if (unreadable > 0)
                warnings?.Add($"{unreadable} /{key} stream(s) could not be read and were not consulted: {firstReason}");
        }

        /// <summary>
        /// The decoded bytes one document's <c>/DSS</c> and <c>/VRI</c> lists may cost, every list and every
        /// question together: the verifier asks once a signature, a certificate and a source, and the inventory once
        /// a <c>/VRI</c> entry, and a list may be named from any number of them.
        /// </summary>
        internal const long MaxDecodedBytesPerDocument = 256L * 1024 * 1024;

        /// <summary>
        /// What one document's revocation streams decoded to, each stream once whoever asks: a decode, or the
        /// reason there is none, kept for the document's life — and what is left of
        /// <see cref="MaxDecodedBytesPerDocument"/>. Every later question about a stream is answered from here, so
        /// its parse (<see cref="ParsedCrl"/>, keyed by the buffer) is kept too.
        /// </summary>
        private sealed class DocumentReads
        {
            private readonly Dictionary<PdfStream, byte[]?> _decoded = new Dictionary<PdfStream, byte[]?>(PdfReferenceEquality.Instance);
            private readonly Dictionary<PdfStream, string> _refused = new Dictionary<PdfStream, string>(PdfReferenceEquality.Instance);
            private long _bytesLeft;

            internal DocumentReads(long budget) => _bytesLeft = budget;

            internal bool TryDecode(PdfStream stream, out byte[]? data, out string? reason)
            {
                lock (this)
                {
                    reason = null;
                    if (_decoded.TryGetValue(stream, out data)) return true;
                    if (_refused.TryGetValue(stream, out reason)) return false;

                    long ceiling = Math.Min(Timestamping.DssBuilder.MaxResponseBytes, _bytesLeft);
                    StreamsRead++;
                    if (ceiling <= 0)
                        reason = $"the document's revocation streams passed {MaxDecodedBytesPerDocument} decoded bytes together";
                    else if (stream.TryGetDecodedDataUncached(ceiling, out data, out string why))
                    {
                        _bytesLeft -= data?.LongLength ?? 0;
                        _decoded[stream] = data;
                        return true;
                    }
                    else
                    {
                        // A refused decode spends the budget too, half of what is left: only the decoder knows
                        // how far it got.
                        _bytesLeft -= Math.Max(1, _bytesLeft / 2);
                        reason = why;
                    }
                    _refused[stream] = reason ?? "";
                    data = null;
                    return false;
                }
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PdfDocument, DocumentReads> Reads =
            new System.Runtime.CompilerServices.ConditionalWeakTable<PdfDocument, DocumentReads>();

        private static DocumentReads ReadsOf(PdfDocument doc) => Reads.GetValue(doc, _ => new DocumentReads(MaxDecodedBytesPerDocument));

        /// <summary>A test seam: <paramref name="doc"/>'s revocation streams read under <paramref name="budget"/> bytes rather than <see cref="MaxDecodedBytesPerDocument"/>; before its first read.</summary>
        internal static void LimitReads(PdfDocument doc, long budget) => Reads.Add(doc, new DocumentReads(budget));

        /// <summary>A CRL stream's parse, kept with its decoded buffer, and its signature check by issuer.</summary>
        private sealed class ParsedCrlEntry
        {
            internal X509Crl? Crl;
            internal readonly Dictionary<X509Certificate, bool> SignatureValid = new Dictionary<X509Certificate, bool>();
        }

        /// <summary>An OCSP stream's parse, kept with its decoded buffer: its basic response, or null.</summary>
        private sealed class ParsedOcspEntry
        {
            internal BasicOcspResp? Basic;
            internal readonly Dictionary<(X509Certificate Issuer, DateTime At), string?> Responder =
                new Dictionary<(X509Certificate Issuer, DateTime At), string?>();
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], ParsedCrlEntry> Crls =
            new System.Runtime.CompilerServices.ConditionalWeakTable<byte[], ParsedCrlEntry>();

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], ParsedOcspEntry> Ocsps =
            new System.Runtime.CompilerServices.ConditionalWeakTable<byte[], ParsedOcspEntry>();

        /// <summary>The CRL in <paramref name="bytes"/>, parsed once a buffer — a failure too.</summary>
        private static ParsedCrlEntry ParsedCrl(byte[] bytes) => Crls.GetValue(bytes, b =>
        {
            ParsesMade++;
            var entry = new ParsedCrlEntry();
            try { entry.Crl = new X509CrlParser().ReadCrl(b); }
            catch { entry.Crl = null; }
            return entry;
        });

        /// <summary>The OCSP response in <paramref name="bytes"/>, parsed once a buffer — a failure too.</summary>
        private static ParsedOcspEntry ParsedOcsp(byte[] bytes) => Ocsps.GetValue(bytes, b =>
        {
            ParsesMade++;
            var entry = new ParsedOcspEntry();
            try
            {
                var resp = new OcspResp(b);
                if (resp.Status == OcspRespStatus.Successful) entry.Basic = resp.GetResponseObject() as BasicOcspResp;
            }
            catch { entry.Basic = null; }
            return entry;
        });

        /// <summary>The CRL a <c>/DSS</c> stream's decoded buffer holds, parsed once a buffer; null when it is none.</summary>
        internal static X509Crl? CachedCrl(byte[] bytes) => ParsedCrl(bytes).Crl;

        /// <summary>The successful OCSP response a decoded buffer holds, parsed once a buffer; null otherwise.</summary>
        internal static BasicOcspResp? CachedOcsp(byte[] bytes) => ParsedOcsp(bytes).Basic;

        /// <summary>CRL and OCSP parses made on the calling thread: the evidence that a buffer is parsed once.</summary>
        [ThreadStatic] internal static long ParsesMade;
    }
}
