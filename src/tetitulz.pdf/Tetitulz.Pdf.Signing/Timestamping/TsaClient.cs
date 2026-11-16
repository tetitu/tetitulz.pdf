// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using Tetitulz.Pdf.Core.Net;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Signing.Models;
using Org.BouncyCastle.Cms;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Tsp;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Signing;
using Tetitulz.Pdf.Signing.Cms;

namespace Tetitulz.Pdf.Signing.Timestamping
{
    /// <summary>
    /// RFC 3161 Timestamp Authority (TSA) client.
    /// Sends a timestamp request to a TSA HTTP endpoint and returns
    /// the DER-encoded TimeStampToken bytes for embedding in the signature.
    /// Public so callers can use the same client for ad-hoc timestamping
    /// outside the PdfSigner (custom workflows, third-party integrations).
    /// <para><b>Diagnostics concurrency:</b> the <c>Last*</c> properties below are backed by
    /// <c>[ThreadStatic]</c> fields, so simultaneous calls on other threads cannot overwrite
    /// them. Read them on the same thread that made the call — a thread that never called
    /// <see cref="GetTimestampToken(byte[], string, string)"/> sees <c>null</c>/<c>false</c>, never another thread's
    /// value (see the Diagnostics Pattern section of CLAUDE.md).</para>
    /// </summary>
    public static class TsaClient
    {
        /// <summary>
        /// Nonce extracted from the most recent successfully-validated TSA
        /// response, or <c>null</c> when the response had no nonce or
        /// validation failed.  Audit pipelines can compare this against the
        /// nonce the request was generated with.
        /// </summary>
        public static byte[]? LastVerifiedNonce
        {
            get => _lastVerifiedNonce;
            private set => _lastVerifiedNonce = value;
        }

        [ThreadStatic]
        private static byte[]? _lastVerifiedNonce;

        /// <summary>
        /// Most recent signature- or EKU-validation failure detected on a
        /// TSA timestamp token, or <c>null</c> when the last token validated
        /// cleanly.  A non-null value here means
        /// <see cref="GetTimestampToken(byte[], string, string)"/> threw and the token bytes were
        /// not returned (fail-closed).
        /// </summary>
        public static string? LastTokenSignatureIssue
        {
            get => _lastTokenSignatureIssue;
            private set => _lastTokenSignatureIssue = value;
        }

        [ThreadStatic]
        private static string? _lastTokenSignatureIssue;

        /// <summary>
        /// Subject DN of the TSA cert that signed the most recent token, or
        /// <c>null</c> if validation failed before a cert was identified.
        /// </summary>
        public static string? LastTsaCertSubject
        {
            get => _lastTsaCertSubject;
            private set => _lastTsaCertSubject = value;
        }

        [ThreadStatic]
        private static string? _lastTsaCertSubject;

        /// <summary>
        /// Why the most recent token is one this library's verifier refuses under its default
        /// policy (<see cref="SignatureVerificationOptions.RejectWeakAlgorithms"/>): a SHA-1 digest
        /// or signature algorithm in the token's SignerInfo, a SHA-1 message imprint, or a TSA key
        /// below the default floors — or <c>null</c> when the token clears that policy or no token
        /// came back.
        /// </summary>
        /// <remarks>
        /// <para><b>A warning, not a refusal.</b> The token is still returned and embedded: a legacy
        /// TSA that signs with SHA-1 must not stop signing, and a verifier with
        /// <c>RejectWeakAlgorithms = false</c> accepts the token. Without this slot the signer would
        /// learn nothing until default verification of the signed document rejected its timestamp.
        /// A broken digest (MD5, MD2), which verification refuses
        /// under every policy, is refused here instead, on <see cref="LastTokenSignatureIssue"/>.</para>
        /// <para>The most recent token on this thread: <c>PdfSigner</c> at PAdES B-LTA requests two
        /// (the signature's, then the document timestamp's), and this reports the second.</para>
        /// </remarks>
        public static string? LastTokenWeakAlgorithm
        {
            get => _lastTokenWeakAlgorithm;
            private set => _lastTokenWeakAlgorithm = value;
        }

        [ThreadStatic]
        private static string? _lastTokenWeakAlgorithm;

        /// <summary>
        /// Request/response timeout for the TSA HTTP round-trip. Deliberately longer than the
        /// OCSP/CRL fetches in <see cref="DssBuilder"/>: a TSA signs on demand and a slow one
        /// blocks signing entirely, whereas revocation data is best-effort.
        /// </summary>
        private const int TsaHttpTimeoutMs = 30_000;

        /// <summary>
        /// Chunk size used while draining the TSA response body. A throughput knob only — the
        /// size limit is <c>MaxTsaResponseBytes</c> inside <see cref="PostRequest"/>.
        /// </summary>
        private const int ResponseReadChunkBytes = 80 * 1024;

        // RFC 3161 §2.4.2 PKIStatus values this client accepts; anything else is a TSA refusal.
        private const int PkiStatusGranted = 0;
        private const int PkiStatusGrantedWithMods = 1;

        /// <summary>
        /// Requests a timestamp from the first TSA in <paramref name="tsaUrls"/>
        /// that responds successfully.  Used to provide failover for production
        /// signing workflows where a single TSA outage would otherwise block
        /// signing entirely.  Each URL is tried in order; the first successful
        /// response is returned.  When every URL fails, the last exception is
        /// re-thrown so the caller sees the most recent diagnostic.
        /// </summary>
        public static byte[] GetTimestampTokenWithFallback(
            byte[] hash, string hashAlgorithmOid, IEnumerable<string> tsaUrls)
            => GetTimestampTokenWithFallback(hash, hashAlgorithmOid, tsaUrls, null);

        /// <summary>
        /// <see cref="GetTimestampTokenWithFallback(byte[], string, IEnumerable{string})"/> with a
        /// network policy applied to every attempt.
        /// </summary>
        /// <remarks>
        /// A separate overload rather than a widened signature: C# bakes an optional argument's
        /// default in at the call site, so widening a shipped method is a binary break for an
        /// already-compiled consumer.
        /// </remarks>
        public static byte[] GetTimestampTokenWithFallback(
            byte[] hash, string hashAlgorithmOid, IEnumerable<string> tsaUrls,
            SigningNetworkOptions? network)
        {
            if (tsaUrls == null) throw new ArgumentNullException(nameof(tsaUrls));
            Exception? last = null;
            foreach (var url in tsaUrls)
            {
                if (string.IsNullOrEmpty(url)) continue;
                try { return GetTimestampToken(hash, hashAlgorithmOid, url, network); }
                catch (Exception ex) { last = ex; }
            }
            throw last ?? new PdfSigningException("No TSA URLs were provided.");
        }

        /// <summary>
        /// Requests a timestamp for the given hash from the TSA.
        /// </summary>
        /// <param name="hash">Hash of the data to timestamp (signature bytes).</param>
        /// <param name="hashAlgorithmOid">OID of the hash algorithm (e.g. "2.16.840.1.101.3.4.2.1" for SHA-256).</param>
        /// <param name="tsaUrl">URL of the RFC 3161 TSA endpoint.</param>
        /// <returns>DER-encoded TimeStampToken bytes.</returns>
        public static byte[] GetTimestampToken(byte[]? hash, string hashAlgorithmOid, string? tsaUrl)
            => GetTimestampToken(hash, hashAlgorithmOid, tsaUrl, null);

        /// <summary>
        /// <see cref="GetTimestampToken(byte[], string, string)"/> with credentials, a proxy and a
        /// timeout — what an authenticated commercial TSA behind an enterprise proxy needs.
        /// </summary>
        /// <param name="hash">Hash of the data to timestamp (signature bytes).</param>
        /// <param name="hashAlgorithmOid">OID of the hash algorithm.</param>
        /// <param name="tsaUrl">URL of the RFC 3161 TSA endpoint.</param>
        /// <param name="network">
        /// The policy to apply; null keeps the anonymous, platform-default behaviour.
        /// </param>
        public static byte[] GetTimestampToken(
            byte[]? hash, string hashAlgorithmOid, string? tsaUrl, SigningNetworkOptions? network)
            => RequestToken(hash, hashAlgorithmOid, tsaUrl, (url, body) => PostRequest(url, body, network,
                TimestampQueryType, TimestampReplyType));

        /// <summary>
        /// <see cref="GetTimestampToken(byte[], string, string, SigningNetworkOptions)"/> for a server
        /// the document named — a seed value's <c>/TimeStamp /URL</c> — rather than the caller. The
        /// request goes out as an OCSP request to a responder a certificate names does
        /// (<c>DssBuilder.PostGuardedCore</c>): connected to an address the private-address guard
        /// passed, with host-scoped credentials only, no redirect followed, under the revocation
        /// fetches' budget and body ceiling. The reply is judged as every token is.
        /// </summary>
        internal static byte[] GetTimestampTokenFromDocumentUrl(
            byte[]? hash, string hashAlgorithmOid, string? tsaUrl, SigningNetworkOptions? network)
            => RequestToken(hash, hashAlgorithmOid, tsaUrl, (url, body) => PostGuarded(url, body, network));

        private const string TimestampQueryType = "application/timestamp-query";
        private const string TimestampReplyType = "application/timestamp-reply";

        private static byte[] RequestToken(
            byte[]? hash, string hashAlgorithmOid, string? tsaUrl, Func<string, byte[], byte[]> post)
        {
            // At the start, on every path: a call that fails before a response arrives must not
            // leave the previous token's verdicts standing.
            ResetDiagnostics();
            if (hash == null) throw new ArgumentNullException(nameof(hash));
            if (tsaUrl == null) throw new ArgumentNullException(nameof(tsaUrl));

            // Build TSA request
            var reqGen = new TimeStampRequestGenerator();
            reqGen.SetCertReq(true); // Ask TSA to include its certificate

            // Use a cryptographically random nonce per RFC 3161 §2.4.1.
            var nonceBytes = new byte[8];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                rng.GetBytes(nonceBytes);
            var nonce = new BigInteger(1, nonceBytes);
            var request = reqGen.Generate(
                hashAlgorithmOid,
                hash,
                nonce);

            byte[] requestBytes = request.GetEncoded();

            byte[] responseBytes = post(tsaUrl, requestBytes);

            return ValidateResponse(request, responseBytes);
        }

        private static byte[] PostGuarded(string url, byte[] requestData, SigningNetworkOptions? network)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var tsaUri) ||
                (tsaUri.Scheme != Uri.UriSchemeHttp && tsaUri.Scheme != Uri.UriSchemeHttps))
                throw new PdfSigningException($"Invalid TSA URL '{url}': only http/https are accepted.");
            // The pre-check the OCSP request runs too: on .NET Framework, a request with credentials or
            // a proxy goes through HttpClientHandler, where it is the only check (GAPS.md §17a).
            if (!PrivateAddressGuard.IsSafeHost(tsaUri.DnsSafeHost))
                throw new PdfSigningException(
                    $"The TSA the document names, at host '{tsaUri.DnsSafeHost}', resolves to a private or reserved " +
                    "address; refusing (SSRF guard). Set SignatureParameters.TimestampAuthorityUrl.");

            try
            {
                return DssBuilder.PostGuardedCore(url, requestData, TimestampQueryType, TimestampReplyType,
                           network, sync: true, CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult()
                       ?? throw new PdfSigningException(
                           $"The TSA at {url} sent a reply past the {DssBuilder.MaxResponseBytes}-byte limit.");
            }
            catch (Exception ex) when (!(ex is PdfSigningException))
            {
                throw new PdfSigningException($"Failed to contact TSA at {url}: {ex.Message}", ex);
            }
        }

        private static void ResetDiagnostics()
        {
            LastVerifiedNonce = null;
            LastTokenSignatureIssue = null;
            LastTsaCertSubject = null;
            LastTokenWeakAlgorithm = null;
        }

        /// <summary>
        /// Everything <see cref="GetTimestampToken(byte[], string, string, SigningNetworkOptions)"/>
        /// does with the TSA's reply: the nonce, the status, the token's certificate, its EKU, its
        /// signature, and the algorithms verification will judge — returning the token's bytes or
        /// throwing. Its own method so a test can hand it a reply without a network.
        /// </summary>
        internal static byte[] ValidateResponse(TimeStampRequest request, byte[] responseBytes)
        {
            ResetDiagnostics();

            // Parse and validate response.
            //
            // BouncyCastle's `Validate(request)` already cross-checks the
            // response's TimeStampToken nonce against the request's nonce as
            // part of its internal consistency checks (RFC 3161 §2.4.2).  We
            // additionally surface the verified nonce on
            // <see cref="LastVerifiedNonce"/> so callers building audit
            // pipelines can confirm the response was bound to *their* request
            // (not a replay).
            var response = new TimeStampResponse(responseBytes);
            response.Validate(request);
            try
            {
                LastVerifiedNonce = response.TimeStampToken?
                    .TimeStampInfo?.Nonce?.ToByteArrayUnsigned();
            }
            catch
            {
                LastVerifiedNonce = null;
            }

            if (response.Status != PkiStatusGranted && response.Status != PkiStatusGrantedWithMods)
            {
                throw new PdfSigningException(
                    $"TSA returned status {response.Status}: {response.GetStatusString()}");
            }

            var token = response.TimeStampToken;
            if (token == null)
                throw new PdfSigningException("TSA response contained no timestamp token.");

            // RFC 3161 §2.4.2 signature & EKU validation — `response.Validate`
            // only cross-checks the nonce, which by itself does not guarantee
            // authenticity.  We must verify the CMS signature on the
            // TimeStampToken using the embedded TSA certificate, and confirm
            // that certificate carries the id-kp-timeStamping EKU (§2.3).
            // Failure here is fail-closed: we throw rather than return an
            // unverified token, since silently accepting a forged token would
            // undermine PAdES B-T / B-LTA validation.
            X509Certificate? signerCert = null;
            try
            {
                var certStore = token.GetCertificates();
                foreach (X509Certificate cert in certStore.EnumerateMatches(token.SignerID))
                {
                    signerCert = cert;
                    break;
                }
            }
            catch (Exception ex)
            {
                LastTokenSignatureIssue = "Failed to enumerate TSA certificates: " + ex.Message;
                throw new PdfSigningException(LastTokenSignatureIssue, ex);
            }

            if (signerCert == null)
            {
                LastTokenSignatureIssue = "TSA token contained no certificate matching the signer.";
                throw new PdfSigningException(LastTokenSignatureIssue);
            }

            // Verify the EKU. RFC 3161 §2.3: the TSA cert MUST contain the
            // id-kp-timeStamping EKU (1.3.6.1.5.5.7.3.8) as the *only* EKU.
            // We relax to "must contain" to accept TSAs that include other
            // benign EKUs, but we still reject certs that lack it entirely.
            try
            {
                var ekuList = signerCert.GetExtendedKeyUsage();
                bool hasTsEku = false;
                if (ekuList != null)
                {
                    foreach (var oid in ekuList)
                    {
                        if (oid != null && oid.ToString() == PdfEkuOids.TimeStamping)
                        {
                            hasTsEku = true;
                            break;
                        }
                    }
                }
                if (!hasTsEku)
                {
                    LastTokenSignatureIssue =
                        "TSA cert lacks id-kp-timeStamping EKU (" + PdfEkuOids.TimeStamping + ").";
                    throw new PdfSigningException(LastTokenSignatureIssue);
                }
            }
            catch (PdfSigningException) { throw; }
            catch (Exception ex)
            {
                LastTokenSignatureIssue = "Failed to read TSA cert EKU: " + ex.Message;
                throw new PdfSigningException(LastTokenSignatureIssue, ex);
            }

            // Verify the token's CMS signature against the signer cert.
            // BouncyCastle's TimeStampToken.Validate(X509Certificate) throws
            // TspValidationException on mismatch — exactly what we want.
            try
            {
                PublicKeyCost.KeyOf(signerCert);
                token.Validate(signerCert);
            }
            catch (Exception ex)
            {
                LastTokenSignatureIssue =
                    "TSA token signature verification failed: " + ex.Message;
                throw new PdfSigningException(LastTokenSignatureIssue, ex);
            }

            // The algorithms, as TsaTokenVerifier will judge them. A broken digest is refused under
            // every verification policy, so a token carrying one is refused here rather than
            // embedded; a weak one is refused only under the default policy, so it is returned and
            // said (LastTokenWeakAlgorithm) — a legacy TSA must not stop signing.
            string imprintOid = token.TimeStampInfo.HashAlgorithm.Algorithm.Id;
            SignerInformation? tokenSigner = null;
            try { tokenSigner = token.ToCmsSignedData().GetSignerInfos().GetFirstSigner(token.SignerID); }
            catch { /* judged on the imprint and the key alone */ }

            string? broken = WeakAlgorithmPolicy.DetectBrokenDigest(imprintOid)
                ?? (tokenSigner == null ? null : WeakAlgorithmPolicy.DetectBrokenDigest(tokenSigner.DigestAlgorithmID.Algorithm.Id));
            if (broken != null)
            {
                LastTokenSignatureIssue =
                    broken + " on the TSA token, which verification refuses under every policy.";
                throw new PdfSigningException(LastTokenSignatureIssue);
            }

            LastTokenWeakAlgorithm = WeakAlgorithmPolicy.Resolve(SignatureVerificationOptions.Default)
                .DetectWeakTimestamp(tokenSigner, imprintOid, signerCert);

            LastTsaCertSubject = signerCert.SubjectDN?.ToString();

            return token.GetEncoded();
        }

        /// <summary>
        /// Returns the OID string for the given algorithm name.
        /// </summary>
        /// <remarks>
        /// <para>ONE implementation of this rule, in
        /// <c>DigestAlgorithms.ResolveOidForNewSignature</c>; open-coded copies would disagree about
        /// what an unrecognised name means — see that method for the silent SHA-256 downgrade such a
        /// divergence produces on the signing path.</para>
        ///
        /// <para>The restriction to the SHA-1/2/3 families is this call site's policy:
        /// <c>PdfDigestOids.FromName</c> also knows MD5/MD2/Streebog, none of which a TSA request
        /// here may name, so <c>allowStreebog</c> stays at its default. An unknown algorithm is
        /// rejected rather than silently downgraded to SHA-256, which would mask crypto-strength
        /// errors when callers asked for an algorithm name with a typo.</para>
        /// </remarks>
        public static string GetHashOid(string algorithm)
            => DigestAlgorithms.ResolveOidForNewSignature(algorithm, nameof(algorithm));

        /// <summary>
        /// Builds the TSA POST, applying <paramref name="network"/> where it is supplied.
        /// </summary>
        /// <remarks>
        /// <para><b>Its own method so the policy can be asserted without a network.</b> Everything
        /// below this point connects, so a test of "are the credentials applied" would otherwise
        /// have to reach a real TSA - and a test that needs the internet is one that gets deleted.</para>
        ///
        /// <para><b>The two security properties are set here and are not part of the policy.</b>
        /// Redirects are refused: the caller-configured TSA URL is trusted enough to name a private
        /// host, and that trust does not transfer to whatever a 3xx points at - a compromised or
        /// MITM-d TSA could otherwise redirect the request, body included, anywhere. And the scheme
        /// is restricted by the caller before this runs. Neither is settable, because a network
        /// policy is about reaching the endpoint, not about which endpoint counts.</para>
        /// </remarks>
        internal static HttpWebRequest BuildRequest(
            Uri tsaUri, int contentLength, string contentType, string acceptType,
            SigningNetworkOptions? network)
        {
            var webReq = (HttpWebRequest)WebRequest.Create(tsaUri);
            webReq.Method = "POST";
            webReq.ContentType = contentType;
            webReq.Accept = acceptType;
            webReq.ContentLength = contentLength;
            // One reading of the policy's timeout for every transport
            // (SigningNetworkOptions.EffectiveTimeout): null keeps this built-in, a non-positive
            // value means "do not time out" - which HttpWebRequest spells Timeout.Infinite (-1),
            // where 0 would mean the opposite, time out immediately - and an overlong value is
            // clamped to int.MaxValue milliseconds before the conversion below.
            //
            // THE CLAMP IS LOAD-BEARING ONLY ON THE LEGACY LEG, and the test suite structurally
            // cannot see it there. On .NET Core 3.0+ an out-of-range double-to-int conversion
            // SATURATES, so (int) of an overlong value is already int.MaxValue - measured, not
            // assumed. On .NET Framework, which is what the netstandard2.0 leg exists for, the same
            // conversion is undefined and yields int.MinValue, and HttpWebRequest.Timeout throws on
            // a negative value other than Infinite. Below the clamp the conversion is exact on both.
            webReq.Timeout = SaturatingCast.ToInt(SigningNetworkOptions.EffectiveTimeout(
                network, TimeSpan.FromMilliseconds(TsaHttpTimeoutMs)).TotalMilliseconds);
            webReq.AllowAutoRedirect = false;

            if (network == null) return webReq;

            if (network.Credentials != null) webReq.Credentials = network.Credentials;
            if (network.Proxy != null) webReq.Proxy = network.Proxy;
            if (network.PreAuthenticate) webReq.PreAuthenticate = true;

            return webReq;
        }
        private static byte[] PostRequest(string url, byte[] requestData,
            SigningNetworkOptions? network, string contentType, string acceptType)
        {
            // Restrict to http/https (the URL flows into WebRequest.Create, which would
            // otherwise honour file://, ftp://, etc.). The TSA URL is caller-configured, so
            // private/internal hosts are intentionally allowed; we only bound the response.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var tsaUri) ||
                (tsaUri.Scheme != Uri.UriSchemeHttp && tsaUri.Scheme != Uri.UriSchemeHttps))
                throw new PdfSigningException($"Invalid TSA URL '{url}': only http/https are accepted.");

            try
            {
                HttpWebRequest webReq = BuildRequest(
                    tsaUri, requestData.Length, contentType, acceptType, network);

                using (var reqStream = webReq.GetRequestStream())
                {
                    reqStream.Write(requestData, 0, requestData.Length);
                }

                using (var webResp = (HttpWebResponse)webReq.GetResponse())
                using (var respStream = webResp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    // Bound the response: a malicious/MITM'd TSA could otherwise stream an
                    // unbounded body to exhaust memory. Timestamp tokens are a few KB.
                    const long MaxTsaResponseBytes = 10L * 1024 * 1024;
                    byte[] buf = new byte[ResponseReadChunkBytes];
                    int read;
                    while ((read = respStream.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (ms.Length + read > MaxTsaResponseBytes)
                            throw new PdfSigningException(
                                $"TSA response exceeds the {MaxTsaResponseBytes}-byte limit.");
                        ms.Write(buf, 0, read);
                    }
                    return ms.ToArray();
                }
            }
            catch (Exception ex) when (!(ex is PdfSigningException))
            {
                throw new PdfSigningException(
                    $"Failed to contact TSA at {url}: {ex.Message}", ex);
            }
        }
    }
}
