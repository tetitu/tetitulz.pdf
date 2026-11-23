// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Asn1;
using Org.BouncyCastle.Asn1.Ocsp;
using Org.BouncyCastle.Asn1.Oiw;
using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Ocsp;
using Org.BouncyCastle.X509;
using Tetitulz.Pdf.Core.Constants;
using Tetitulz.Pdf.Core.Net;
using Tetitulz.Pdf.Core.Objects;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing;

namespace Tetitulz.Pdf.Signing.Timestamping
{
    /// <summary>
    /// Builds a PDF /DSS (Document Security Store) dictionary for Long-Term Validation (LTV).
    /// The /DSS dictionary embeds OCSP responses and/or CRL data for every certificate
    /// in the signing chain so that the signature can be validated after the certificates expire.
    /// See ISO 32000-2 §12.8.4.3 and ETSI EN 319 102-2.
    ///
    /// Public so callers can build custom LTV workflows (HSM integrations, batch signing,
    /// post-hoc DSS extension on third-party documents) without going through PdfSigner.
    /// <para><b>Diagnostics concurrency:</b> the <c>Last*</c> diagnostic properties are backed by
    /// <c>[ThreadStatic]</c> fields, so simultaneous OCSP/CRL operations on other threads can no
    /// longer overwrite each other's value. Read them on the thread that made the call. (The
    /// <c>EmbedStaleOcspResponses</c> toggle is a deliberate process-wide configuration switch and
    /// stays a plain static.)</para>
    /// <para><b>Async caveat:</b> <see cref="TryBuildDssAsync(System.Collections.Generic.IList{Org.BouncyCastle.X509.X509Certificate}, Org.BouncyCastle.X509.X509Certificate, System.Threading.CancellationToken)"/> sets these diagnostics from inside
    /// <c>await</c> continuations that use <c>ConfigureAwait(false)</c>, so they are written on
    /// whichever pool thread resumed the state machine. After awaiting the async overload the
    /// caller may therefore observe <c>null</c>/<c>false</c> rather than the value produced by the
    /// operation. The synchronous <see cref="TryBuildDss(System.Collections.Generic.IList{Org.BouncyCastle.X509.X509Certificate}, Org.BouncyCastle.X509.X509Certificate, out System.Collections.Generic.List{byte[]}, out System.Collections.Generic.List{byte[]}, out System.Collections.Generic.List{string})"/> path writes and reads on the same
    /// thread and is unaffected. Making these flow across <c>await</c> would need
    /// <c>AsyncLocal&lt;T&gt;</c>, which is a design change (different cost profile), not a
    /// storage-lifetime fix — see the Diagnostics Pattern section of CLAUDE.md.</para>
    /// </summary>
    public static class DssBuilder
    {
        // Shared HttpClient: avoids socket exhaustion and is safe to reuse across calls.
        // No base address; each request uses a fully-qualified URL from the certificate AIA.
        //
        // AllowAutoRedirect is OFF deliberately. OCSP/CRL URLs come from the *untrusted*
        // certificate's AIA / CRL-DP extensions, and PrivateAddressGuard.IsSafeHost is only
        // applied to the URL we are about to request. With auto-redirect on (the .NET default),
        // a responder could answer 302 -> http://169.254.169.254/... and the handler would
        // follow it without the guard ever seeing the new host — exactly the bypass
        // PrivateAddressGuard's own remarks warn about ("re-check on every request and
        // redirect"). Redirects are instead followed manually by FollowGuardedRedirects,
        // which re-runs the guard on each hop.
        /// <summary>
        /// The built-in timeout for every OCSP / CRL HTTP call — the client's overall timeout and
        /// the per-URL budget of <see cref="RevocationBudgetSource"/> — when the caller's
        /// <see cref="SigningNetworkOptions.Timeout"/> sets none. Revocation data is best-effort
        /// LTV material, so a slow responder must not stall signing; a TSA round-trip is a
        /// different transport with its own (longer) built-in in <see cref="TsaClient"/>. Read only
        /// through <see cref="RevocationBudget"/>, so a caller's longer timeout is never capped by it.
        /// </summary>
        private const int RevocationHttpTimeoutSeconds = 15;

        /// <summary>
        /// How old an OCSP response carrying <b>no <c>nextUpdate</c></b> may be and still stand as
        /// current revocation evidence.
        /// </summary>
        /// <remarks>
        /// <para>RFC 6960 §4.2.2.1 makes <c>nextUpdate</c> optional and reads its absence as "newer
        /// revocation information is available all the time". That describes the RESPONDER; it does
        /// not make one old response current. Without a bound, a good, issuer-signed response whose
        /// <c>thisUpdate</c> is years old would be embedded as LTV evidence, a revocation in the
        /// interval invisible and the document asserting otherwise.</para>
        ///
        /// <para><b>Seven days is a policy choice, not a specification.</b> The RFC gives no figure.
        /// It is long enough for the weekly refresh cadence a responder without <c>nextUpdate</c>
        /// typically implies, and short enough that a stale response cannot silently outlive a
        /// revocation. A caller who wants stale responses embedded has
        /// <see cref="EmbedStaleOcspResponses"/>, which downgrades every freshness refusal here to
        /// a reported warning.</para>
        /// </remarks>
        private static readonly TimeSpan MaxAgeWithoutNextUpdate = TimeSpan.FromDays(7);

        /// <summary>
        /// Whether one OCSP single-response may stand as <b>current</b> revocation evidence
        /// (RFC 6960 §3.1.2, §4.2.2.1).
        /// </summary>
        /// <param name="thisUpdate">When the responder says this status was correct.</param>
        /// <param name="nextUpdate">When newer status will be available, or null if the responder gave none.</param>
        /// <param name="now">Current UTC time, passed in so the rule is testable without a clock.</param>
        /// <param name="issue">Why it was refused, or <c>null</c> when it is current.</param>
        /// <remarks>
        /// <para><b>Extracted so the rule can be tested at all.</b> It lived inline inside the
        /// network fetch, so nothing could reach it without a live responder — which is why the
        /// no-<c>nextUpdate</c> hole below survived: the two conditions that were there each looked
        /// correct in isolation.</para>
        ///
        /// <para><b>Three ways a response fails, and the third was missing.</b> An elapsed
        /// <c>nextUpdate</c> is stale. A <c>thisUpdate</c> in the future is skew or forgery. And a
        /// response with <b>no</b> <c>nextUpdate</c> at all fell through both: the first arm was
        /// guarded on <c>HasValue</c> and the second only caught future times, so a good,
        /// issuer-signed response whose <c>thisUpdate</c> was years old was embedded as current LTV
        /// evidence with no issue reported. Any revocation in the interval is then invisible and
        /// the document asserts otherwise.</para>
        /// </remarks>
        internal static bool IsOcspResponseCurrent(
            DateTime thisUpdate, DateTime? nextUpdate, DateTime now, out string? issue)
            => IsOcspResponseCurrent(thisUpdate, nextUpdate, producedAt: null, now, out issue);

        /// <summary>
        /// <see cref="IsOcspResponseCurrent(DateTime, DateTime?, DateTime, out string)"/> with the
        /// response's <c>producedAt</c>, which the verifier also refuses in the future.
        /// </summary>
        internal static bool IsOcspResponseCurrent(
            DateTime thisUpdate, DateTime? nextUpdate, DateTime? producedAt, DateTime now, out string? issue)
        {
            issue = Cms.RevocationFreshness.WhyNotCurrent(
                thisUpdate, nextUpdate, producedAt, now,
                Cms.RevocationFreshness.DefaultClockSkew, MaxAgeWithoutNextUpdate);
            return issue == null;
        }

        /// <summary>
        /// Whether a CRL may stand as <b>current</b> revocation evidence (RFC 5280 §5.1.2.4–5.1.2.5)
        /// — the verifier's rule under its default options, so a CRL embedded here is one default
        /// verification reads.
        /// </summary>
        /// <param name="thisUpdate">When the CA says this list was correct.</param>
        /// <param name="nextUpdate">When the next list will be issued, or null if none was given.</param>
        /// <param name="now">Current UTC time, passed in so the rule is testable without a clock.</param>
        /// <param name="issue">Why it was refused, or <c>null</c> when it is current.</param>
        /// <remarks>
        /// <para><b>A CRL with no <c>nextUpdate</c> is not held to OCSP's seven days.</b> RFC 5280
        /// §5.1.2.5 makes the field optional and a CRL that omits it is still the CA's complete list
        /// as of <c>thisUpdate</c>, so a week's cap would refuse valid revocation data.</para>
        /// <para><b>But it is held to the verifier's cap.</b> The verifier caps such data at
        /// <c>MaxRevocationDataAge</c> (365 days by default), so a CRL older than that, embedded and
        /// reported as obtained, would be read as <c>Unknown</c> by default verification of the very
        /// document it was written into — the embedder writing what the verifier will not
        /// read.</para>
        /// </remarks>
        internal static bool IsCrlCurrent(
            DateTime thisUpdate, DateTime? nextUpdate, DateTime now, out string? issue)
        {
            issue = Cms.RevocationFreshness.WhyNotCurrent(
                thisUpdate, nextUpdate, producedAt: null, now,
                Cms.RevocationFreshness.DefaultClockSkew,
                Cms.RevocationFreshness.DefaultMaxAgeWithoutNextUpdate);
            return issue == null;
        }

        private static readonly HttpClient _httpClient = new HttpClient(BuildHandler(null))
        {
            Timeout = RevocationBudget(null)
        };

        /// <summary>
        /// The one handler construction, so every client — shared or per-policy — gets the same two
        /// security properties.
        /// </summary>
        /// <remarks>
        /// <para><b>The connection goes to an address that was checked, not to a name that was.</b>
        /// <c>PrivateAddressGuard.IsSafeHost</c> resolves, classifies, discards the addresses and
        /// answers a bool; fetching by hostname afterwards resolves a second time, and it is the
        /// second answer that decides where the socket lands. An OCSP or CRL URL comes out of an
        /// untrusted certificate's AIA / CRL-DP extension, so the name is the attacker's to choose
        /// — a short TTL answering public to the check and 169.254.169.254 to the connection walks
        /// straight past a guard that is doing its job correctly.
        /// <c>GuardedHttpConnect.ConnectAsync</c> resolves once and dials one of the addresses it
        /// just classified, which leaves no window at all.</para>
        ///
        /// <para><b>On the netstandard2.0 leg there is no such hook and the pre-check is all there
        /// is.</b> <c>SocketsHttpHandler.ConnectCallback</c> is .NET Core only, and .NET Framework
        /// offers no remote-address hook on <c>HttpWebRequest</c> either — its
        /// <c>BindIPEndPointDelegate</c> binds the LOCAL endpoint. GAPS.md says so rather than the
        /// row reading as closed everywhere.</para>
        ///
        /// <para><b><c>AllowAutoRedirect</c> is false on every one of them</b>, so each hop is
        /// re-checked by hand rather than taken unseen.</para>
        ///
        /// <para>Built by <see cref="GuardedHttpFetch.CreateHandler"/>, the one construction every
        /// client reaching an untrusted URL shares. No <c>UseProxy = true</c> beside it: it already
        /// defaults to true on both handlers and both targets — measured, after a mutation that
        /// removed the assignment reddened nothing. DssNetworkPolicyTests still asserts
        /// <c>UseProxy</c>, which pins that default.</para>
        ///
        /// <para><b>The caller's server credentials ride this client only when they are host-scoped.</b>
        /// An OCSP, CRL or AIA URL comes out of an untrusted certificate's extension, so a credential
        /// that answers <i>any</i> host's challenge — a bare <see cref="NetworkCredential"/>, or
        /// <c>CredentialCache.DefaultNetworkCredentials</c>, which is not a
        /// <see cref="CredentialCache"/> — would be sent, in the clear for Basic, to whatever host the
        /// attacker named. A TSA request to the caller's own URL keeps the caller's credentials
        /// unchanged (<see cref="TsaClient"/>); one to a server a document's seed value names goes
        /// through this client (<see cref="PostGuardedCore"/>). Only a <see cref="CredentialCache"/>,
        /// which binds each credential to a specific host, scheme and authentication type, is safe to
        /// ride a fetch to an attacker-chosen host; <see cref="RevocationCredentials"/> drops anything
        /// else.</para>
        /// </remarks>
        private static HttpMessageHandler BuildHandler(SigningNetworkOptions? n)
            => GuardedHttpFetch.CreateHandler(RevocationCredentials(n?.Credentials), n?.Proxy, n?.PreAuthenticate ?? false);

        /// <summary>
        /// The server credentials a revocation fetch may carry: a <see cref="CredentialCache"/> as it
        /// stands, nothing otherwise. See <see cref="BuildHandler"/> for why a credential that is not
        /// bound to a host must not ride a fetch to a host an untrusted certificate chose.
        /// </summary>
        internal static ICredentials? RevocationCredentials(ICredentials? credentials)
            => credentials is CredentialCache ? credentials : null;

        /// <summary>
        /// One <see cref="HttpClient"/> per caller policy, and the shared one when there is none.
        /// </summary>
        /// <remarks>
        /// <para><b><c>AllowAutoRedirect</c> is false on every client this returns, and that is not
        /// a preference.</b> Redirects are followed by hand so each hop can be re-checked against
        /// the private-address guard; a handler that followed them itself would take the very first
        /// hop unguarded, which is the whole attack this code is written against. A network policy
        /// says how to reach a host, never which hosts count.</para>
        ///
        /// <para><b>Cached against the options instance, not created per request.</b> A client per
        /// fetch is the socket-exhaustion pattern the shared instance exists to avoid;
        /// <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/> gives the
        /// client exactly the caller's own lifetime, so a refresh that walks a chain of five
        /// certificates opens one.</para>
        /// </remarks>
        internal static HttpClient ClientFor(SigningNetworkOptions? network)
        {
            if (network == null) return _httpClient;

            return _policyClients.GetValue(network, static n => new HttpClient(BuildHandler(n))
            {
                Timeout = RevocationBudget(n),
            });
        }

        /// <summary>
        /// The budget one OCSP or CRL fetch runs under: the caller's
        /// <see cref="SigningNetworkOptions.Timeout"/> as
        /// <see cref="SigningNetworkOptions.EffectiveTimeout"/> reads it, with
        /// <see cref="RevocationHttpTimeoutSeconds"/> as the built-in. The same figure is the
        /// client's overall timeout, so the two cannot disagree.
        /// </summary>
        internal static TimeSpan RevocationBudget(SigningNetworkOptions? network)
            => SigningNetworkOptions.EffectiveTimeout(
                network, TimeSpan.FromSeconds(RevocationHttpTimeoutSeconds));

        /// <summary>
        /// One cancellation source per fetched URL, cancelled at <see cref="RevocationBudget"/>:
        /// a single budget for the whole redirect chain, because <c>HttpClient.Timeout</c> is per
        /// request and a distribution point redirecting three times at ten seconds a hop would
        /// otherwise run on for the chain. Linked to the caller's token, which still cuts the fetch
        /// short.
        /// </summary>
        /// <remarks>
        /// <b>This is the only place the budget is applied.</b> Each fetch site once built its own
        /// source from the built-in constant, which capped a caller's 60 s timeout at 15 s while the
        /// same policy reached the TSA intact.
        /// </remarks>
        internal static CancellationTokenSource RevocationBudgetSource(
            SigningNetworkOptions? network, CancellationToken ct)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(ct);
            source.CancelAfter(RevocationBudget(network));
            return source;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<
            SigningNetworkOptions, HttpClient> _policyClients = new();

        // Redirect hops permitted per OCSP/CRL fetch. Legitimate responders rarely redirect at
        // all; this bounds both redirect loops and guard-probing chains.
        internal const int MaxRedirects = GuardedHttpFetch.DefaultMaxRedirects;

        /// <summary>
        /// Diagnostic: set when a redirect was refused because its target failed the
        /// private-address guard, or because the hop limit was reached.
        /// <para>
        /// Backed by a <c>[ThreadStatic]</c> field — see the "Diagnostics concurrency" note
        /// on <see cref="DssBuilder"/>.
        /// </para>
        /// </summary>
        public static string? LastBlockedRedirect
        {
            get => _lastBlockedRedirect;
            private set => _lastBlockedRedirect = value;
        }

        [ThreadStatic]
        private static string? _lastBlockedRedirect;

        /// <summary>
        /// Follows up to <see cref="MaxRedirects"/> redirects by hand through
        /// <see cref="GuardedHttpFetch.FollowRedirects"/>, which re-runs the private-address guard
        /// on every hop, and records a refusal on <see cref="LastBlockedRedirect"/>. Returns the
        /// first non-redirect response, or <c>null</c> when a hop was blocked or the limit was hit.
        /// The caller owns disposal of the returned response.
        /// </summary>
        internal static HttpResponseMessage? FollowGuardedRedirects(
            HttpResponseMessage response, Func<Uri, HttpResponseMessage> send)
        {
            HttpResponseMessage? followed =
                GuardedHttpFetch.FollowRedirects(response, send, MaxRedirects, out string? refusal);
            if (refusal != null) LastBlockedRedirect = refusal;
            return followed;
        }

        /// <summary>The awaited form of <see cref="FollowGuardedRedirects"/>.</summary>
        internal static async Task<HttpResponseMessage?> FollowGuardedRedirectsAsync(
            HttpResponseMessage response,
            Func<Uri, Task<HttpResponseMessage>> sendAsync)
        {
            var (followed, refusal) =
                await GuardedHttpFetch.FollowRedirectsAsync(response, sendAsync, MaxRedirects).ConfigureAwait(false);
            if (refusal != null) LastBlockedRedirect = refusal;
            return followed;
        }

        internal static bool IsRedirect(HttpResponseMessage r) => GuardedHttpFetch.IsRedirect(r);

        internal static void ResetLastBlockedRedirect() => LastBlockedRedirect = null;

        // OCSP/CRL URLs come from the (untrusted) certificate's AIA/CRL-DP extensions, so a
        // malicious responder can return an arbitrarily large body to exhaust memory. Real OCSP
        // responses are KB; CRLs for large CAs can be a few MB.
        //
        // THE CAP ONLY BOUNDS MEMORY BECAUSE EVERY SEND READS HEADERS ONLY. Each of the four send
        // sites below passes HttpCompletionOption.ResponseHeadersRead; before, they took the
        // default ResponseContentRead, which buffers the whole body inside SendAsync — up to
        // MaxResponseContentBufferSize, two gigabytes here — so this 20 MB check ran after a
        // hostile distribution point had already been read in full.
        // The verifier reads a /DSS stream under the same figure (RevocationChecker.ReadStreams):
        // what the embedder may write, the verifier reads, and nothing larger.
        internal const long MaxResponseBytes = 20L * 1024 * 1024;

        // The body read is GuardedHttpFetch's, which every caller fetching an untrusted URL shares,
        // and it is awaited and cancellable only. A headers-only send returns before the body
        // arrives, so the read must carry the fetch's budget token or a trickling server holds the
        // call past it; the synchronous fetches block on this with their budget's token. It must
        // also stay async all the way down for TryBuildDssAsync, documented as "fully non-blocking
        // HTTP calls" — a blocking drain once held a thread-pool thread for a slow 15 MB CRL past
        // the caller's timeout.
        private static Task<byte[]?> ReadCappedAsync(HttpContent content, CancellationToken ct)
            => GuardedHttpFetch.ReadCappedAsync(content, MaxResponseBytes, ct)!;

        /// <summary>
        /// Fetches OCSP responses for each certificate in the chain and builds the
        /// PDF objects needed for the /DSS dictionary.
        /// </summary>
        /// <param name="chain">Ordered chain: [0] = end-entity, last = root.</param>
        /// <param name="signerCert">The signing certificate.</param>
        /// <param name="ocspResponseBytes">Output: raw DER OCSP response bytes.</param>
        /// <param name="crlBytes">
        /// Output: raw DER CRL bytes — one per chain link for which OCSP yielded nothing and a CRL
        /// was fetched, <b>issuer-verified and found current</b> by
        /// <see cref="ValidateAndFilterCrl"/>. Empty when OCSP covered every link (a CRL is only
        /// the fallback) or when every candidate CRL was refused. What reaches <c>/DSS /CRLs</c>
        /// is validated first.
        /// </param>
        /// <param name="warnings">
        /// Output: non-fatal diagnostic messages (e.g. OCSP fetch failures, refused CRLs).
        /// The caller should log these so the operator knows LTV data is incomplete.
        /// </param>
        /// <returns>
        /// True if at least one OCSP response <i>or</i> CRL was obtained; false otherwise.
        /// </returns>
        public static bool TryBuildDss(
            IList<X509Certificate> chain,
            X509Certificate signerCert,
            out List<byte[]> ocspResponseBytes,
            out List<byte[]> crlBytes,
            out List<string> warnings)
            => TryBuildDss(chain, signerCert, null, out ocspResponseBytes, out crlBytes, out warnings);

        /// <summary>
        /// <see cref="TryBuildDss(IList{X509Certificate}, X509Certificate, out List{byte[]}, out List{byte[]}, out List{string})"/>
        /// with credentials, a proxy and a timeout for the OCSP and CRL fetches.
        /// </summary>
        /// <remarks>
        /// A separate overload rather than a widened signature: C# bakes an optional argument's
        /// default in at the call site, so widening the shipped one is a binary break. The policy
        /// sits before the <c>out</c> parameters so it cannot be confused with them.
        /// </remarks>
        /// <param name="chain">The certificate chain to walk.</param>
        /// <param name="signerCert">The signer's own certificate.</param>
        /// <param name="network">Credentials, proxy and timeout; null fetches anonymously.</param>
        /// <param name="ocspResponseBytes">Output: DER OCSP responses obtained.</param>
        /// <param name="crlBytes">Output: DER CRLs obtained.</param>
        /// <param name="warnings">Output: non-fatal diagnostics.</param>
        /// <returns>True if at least one OCSP response or CRL was obtained.</returns>
        public static bool TryBuildDss(
            IList<X509Certificate> chain,
            X509Certificate signerCert,
            SigningNetworkOptions? network,
            out List<byte[]> ocspResponseBytes,
            out List<byte[]> crlBytes,
            out List<string> warnings)
        {
            // The one walk, run without suspending: every step blocks in place, so the walk and the
            // [ThreadStatic] diagnostics it writes stay on the calling thread.
            var (ok, ocsp, crls, notes) =
                WalkRevocationPath(chain, signerCert, network, sync: true, CancellationToken.None)
                    .GetAwaiter().GetResult();
            ocspResponseBytes = ocsp;
            crlBytes = crls;
            warnings = notes;
            return ok;
        }

        /// <summary>
        /// The revocation walk, written once and run two ways: <paramref name="sync"/> blocks on each
        /// network step in place, so nothing suspends and the whole walk — the
        /// <c>[ThreadStatic]</c> diagnostics with it — runs on the calling thread, as the synchronous
        /// API promises; otherwise each step is awaited, as <see cref="TryBuildDssAsync(IList{X509Certificate}, X509Certificate, CancellationToken)"/>
        /// promises ("fully non-blocking").
        /// </summary>
        /// <remarks>
        /// <para><b>One walk, sync and async.</b> Two copies drift: warnings naming less in one, an
        /// OCSP POST outside the revocation budget in the other, so a responder that stalls holds a
        /// signing call past the caller's timeout. One walk, one set of network steps (<see cref="FetchOcspCore"/>, <see cref="FetchCrlCore"/>,
        /// <see cref="PostOcspRequestCore"/>), each with the same switch.</para>
        /// <para>A cancellation is rethrown only when it is the caller's: a revocation budget running
        /// out is a fetch that failed, and is a warning like any other.</para>
        /// </remarks>
        private static async Task<(bool ok, List<byte[]> ocspBytes, List<byte[]> crlBytes, List<string> warnings)>
            WalkRevocationPath(
                IList<X509Certificate> chain, X509Certificate signerCert, SigningNetworkOptions? network,
                bool sync, CancellationToken ct)
        {
            var ocspBytes = new List<byte[]>();
            var crlBytes  = new List<byte[]>();
            var warnings  = new List<string>();

            // Walk the path: for each pair (subject, issuer), fetch an OCSP response.
            var allCerts = RevocationPath(chain, signerCert, warnings);

            for (int i = 0; i < allCerts.Count - 1; i++)
            {
                ct.ThrowIfCancellationRequested();
                var subject = allCerts[i];
                var issuer  = allCerts[i + 1];

                bool gotRevocationData = false;
                // Said only when the CRL fallback does not supply validated data either: a path a CRL
                // covers is complete, and a warning here made Sign throw over a correct /DSS.
                string? ocspWarning = null;
                try
                {
                    var fetch = FetchOcspCore(subject, issuer, network, sync, ct);
                    byte[]? ocspResp = sync ? fetch.GetAwaiter().GetResult() : await fetch.ConfigureAwait(false);
                    if (ocspResp != null && ocspResp.Length > 0)
                    {
                        ocspBytes.Add(ocspResp);
                        gotRevocationData = true;
                    }
                    else
                        ocspWarning = $"No OCSP response returned for certificate " +
                            $"'{subject.SubjectDN}' (issuer: '{issuer.SubjectDN}').";
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // OCSP fetch failure is non-fatal — fall through to CRL.
                    ocspWarning = $"OCSP fetch failed for certificate '{subject.SubjectDN}': {ex.Message}";
                }

                // CRL fallback when OCSP didn't yield a response.  Per ETSI
                // EN 319 142-1 §5.4.2 PAdES B-LT requires *some* revocation
                // data per certificate; CRL is the historical alternative when
                // OCSP is unavailable or the responder doesn't support the
                // certificate.  We extract CRL distribution-point URLs from the
                // certificate's CRLDistributionPoints extension and download
                // the first reachable one.
                if (!gotRevocationData)
                {
                    if (HttpCrlUrlCount(subject) > MaxCrlUrlsPerCertificate)
                        warnings.Add($"Certificate '{subject.SubjectDN}' lists more than {MaxCrlUrlsPerCertificate} " +
                                     "http(s) CRL distribution points; only the first were tried.");
                    try
                    {
                        var fetch = FetchCrlCore(subject, network, sync, ct);
                        byte[]? crl = sync ? fetch.GetAwaiter().GetResult() : await fetch.ConfigureAwait(false);
                        if (crl == null || crl.Length == 0)
                        {
                            if (ocspWarning != null) warnings.Add(ocspWarning);
                            warnings.Add($"No CRL distribution-point URL found for certificate " +
                                $"'{subject.SubjectDN}' — LTV data will be incomplete.");
                        }
                        else
                        {
                            // The bytes came from a URL named by the *untrusted* certificate's
                            // CRL-DP extension over plain HTTP. Nothing about a 200 makes the body
                            // a CRL, let alone this certificate's CRL — see ValidateAndFilterCrl.
                            byte[]? validated = ValidateAndFilterCrl(crl, subject, issuer, warnings);
                            if (validated != null)
                                crlBytes.Add(validated);
                            else
                            {
                                if (ocspWarning != null) warnings.Add(ocspWarning);
                                warnings.Add($"CRL refused for certificate '{subject.SubjectDN}': " +
                                    $"{CrlRefusalReason()} — LTV data will be incomplete.");
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception crlEx)
                    {
                        if (ocspWarning != null) warnings.Add(ocspWarning);
                        warnings.Add($"CRL fetch failed for certificate '{subject.SubjectDN}': {crlEx.Message}");
                    }
                }
            }

            return (ocspBytes.Count > 0 || crlBytes.Count > 0, ocspBytes, crlBytes, warnings);
        }

        /// <summary>
        /// The certification path both revocation walks follow: <paramref name="signerCert"/>, then
        /// the certificate in <paramref name="chain"/> whose key signed it, and so on up to a
        /// self-signed root or the first certificate whose issuer the chain does not hold.
        /// </summary>
        /// <remarks>
        /// <para><b>Not the chain's list order.</b> Pairing each certificate with the next one as its
        /// issuer would fail for <c>PdfLtvRefresher</c>, which passes the certificates a signature's
        /// CMS carried: a CMS certificate set is a SET OF — DER sorts it by encoding — so in any
        /// chain with an intermediate the leaf could be asked about under the root. A responder
        /// answers such a request with "unknown" or nothing, and the document would be left without
        /// the revocation data it was refreshed for.</para>
        ///
        /// <para>A path that stops below a root is reported: the certificate at its top is one whose
        /// revocation status nothing can be asked about, which is not the same as having none.</para>
        /// </remarks>
        internal static List<X509Certificate> RevocationPath(
            IList<X509Certificate> chain, X509Certificate signerCert, List<string> warnings)
        {
            var candidates = new List<X509Certificate>();
            foreach (var c in chain)
                if (c != null) candidates.Add(c);

            var path = CertificateChainValidator.BuildChain(
                signerCert, Org.BouncyCastle.Utilities.Collections.CollectionUtilities.CreateStore(candidates));

            var top = path[path.Count - 1];
            if (!top.IssuerDN.Equivalent(top.SubjectDN, true))
                warnings.Add($"The issuer of certificate '{top.SubjectDN}' is not in the chain, so its " +
                             "revocation status cannot be requested — LTV data will be incomplete.");
            return path;
        }

        /// <summary>
        /// The CRL distribution-point URLs from <paramref name="cert"/> that this library is
        /// willing to fetch: absolute, <c>http</c>/<c>https</c>, and not resolving to a private or
        /// loopback host.
        /// </summary>
        /// <remarks>
        /// <para><b>This is a security control, decided once.</b> <c>FetchCrl</c> and
        /// <c>FetchCrlAsync</c> both ask it. Two copies of a filter whose job is to reject hostile
        /// input is worse than two copies of anything else, because hardening one of them looks
        /// exactly like hardening the feature. The fetch loops stay separate -- one blocks, one
        /// awaits, and that difference is real.</para>
        ///
        /// <para><b>Why the scheme whitelist is strict.</b> These URLs come from an untrusted
        /// certificate extension, so any protocol handler the runtime or OS knows about is a
        /// potential side channel: <c>ldap://</c>, <c>file://</c>, <c>javascript:</c> in some
        /// hosting setups, plus exotic schemes a custom <c>HttpMessageHandler</c> might intercept.
        /// RFC 5280 §4.2.1.13 permits HTTP, FTP and LDAP; only HTTP and HTTPS are accepted here.
        /// <c>Uri.TryCreate</c> runs as well as the scheme test, so a value carrying an embedded
        /// newline and a second request line cannot smuggle it past a naive string check.</para>
        ///
        /// <para><b>The guard runs again on every redirect hop</b> -- see
        /// <c>FollowGuardedRedirects</c> -- because a distribution point that legitimately redirects
        /// could otherwise be used to reach an address this filter just rejected.</para>
        ///
        /// <para>Sets <see cref="LastCrlExtParseError"/>, and yields nothing when the extension is
        /// absent or will not parse, so both callers fall through to their own "no CRL" return.</para>
        ///
        /// <para><b><c>internal</c> so it can be tested</b>, on the same grounds as
        /// <c>FollowGuardedRedirects</c> and <c>ValidateCrl</c>: the filter is pure — a certificate
        /// in, a list of URLs out, no network — so the guards are checkable without standing up a
        /// distribution point, which through <c>FetchCrl</c> they would not be at all.</para>
        /// </remarks>
        internal static IEnumerable<Uri> SafeCrlDistributionUrls(X509Certificate cert)
        {
            LastCrlUrlsCut = null;
            byte[]? crlDpExt = cert.GetExtensionValue(X509Extensions.CrlDistributionPoints)?.GetOctets();
            if (crlDpExt == null) yield break;

            CrlDistPoint? dist = ParseCrlDistributionPoints(crlDpExt);
            if (dist == null) yield break;

            int considered = 0;
            foreach (var dp in dist.GetDistributionPoints())
            {
                var dpName = dp.DistributionPointName;
                if (dpName?.Type != DistributionPointName.FullName) continue;

                var fullName = (GeneralNames)dpName.Name;
                foreach (var gn in fullName.GetNames())
                {
                    if (gn.TagNo != GeneralName.UniformResourceIdentifier) continue;

                    string url = ((DerIA5String)gn.Name).GetString();
                    if (string.IsNullOrEmpty(url)) continue;
                    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? crlUri)) continue;
                    if (crlUri.Scheme != Uri.UriSchemeHttps && crlUri.Scheme != Uri.UriSchemeHttp)
                        continue;

                    // The certificate chooses how many it lists, and each from here costs a name resolution
                    // and a fetch with its own budget in the caller: a few, the way issuers publish them, and
                    // no more.
                    if (++considered > MaxCrlUrlsPerCertificate)
                    {
                        LastCrlUrlsCut = $"the certificate lists more than {MaxCrlUrlsPerCertificate} http(s) CRL URLs; the rest were not tried.";
                        yield break;
                    }
                    if (!PrivateAddressGuard.IsSafeHost(crlUri.DnsSafeHost)) continue;

                    yield return crlUri;
                }
            }
        }

        /// <summary>
        /// The most CRL distribution-point URIs considered for one certificate — each a name resolution and a
        /// fetch with its own budget, and a certificate's extension may list any number.
        /// </summary>
        internal const int MaxCrlUrlsPerCertificate = 4;

        /// <summary>
        /// How many http(s) CRL distribution-point URIs <paramref name="cert"/> lists, counted from the extension
        /// alone — no name is resolved — so a walk can say it tried fewer than the certificate names.
        /// </summary>
        internal static int HttpCrlUrlCount(X509Certificate cert)
        {
            byte[]? ext = cert.GetExtensionValue(X509Extensions.CrlDistributionPoints)?.GetOctets();
            CrlDistPoint? dist = ext == null ? null : ParseCrlDistributionPoints(ext);
            if (dist == null) return 0;
            int count = 0;
            foreach (var dp in dist.GetDistributionPoints())
            {
                if (dp.DistributionPointName?.Type != DistributionPointName.FullName) continue;
                foreach (var gn in ((GeneralNames)dp.DistributionPointName.Name).GetNames())
                    if (gn.TagNo == GeneralName.UniformResourceIdentifier
                        && Uri.TryCreate(((DerIA5String)gn.Name).GetString(), UriKind.Absolute, out Uri? u)
                        && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp))
                        count++;
            }
            return count;
        }

        /// <summary>
        /// Why the last <see cref="SafeCrlDistributionUrls"/> on this thread stopped before the certificate's last
        /// URI, or <c>null</c> when it considered them all.
        /// </summary>
        [ThreadStatic] internal static string? LastCrlUrlsCut;

        /// <summary>
        /// Separate from the iterator above because C# forbids <c>yield</c> inside a <c>try</c>
        /// that has a <c>catch</c>.
        /// </summary>
        private static CrlDistPoint? ParseCrlDistributionPoints(byte[] crlDpExt)
        {
            try
            {
                CrlDistPoint dist = CrlDistPoint.GetInstance(Asn1Object.FromByteArray(crlDpExt));
                LastCrlExtParseError = null;
                return dist;
            }
            catch (Exception ex)
            {
                LastCrlExtParseError = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Fetches the certificate revocation list from the first reachable
        /// CRL distribution-point URL embedded in the certificate.  Returns
        /// <c>null</c> when no CRL URL is present or every URL failed to fetch.
        /// </summary>
        // Internal: the verifier's online revocation check (RevocationChecker) fetches through the
        // same guarded path as the embedder, so the two sides cannot differ on what a URL may reach.
        internal static byte[]? FetchCrl(X509Certificate cert, SigningNetworkOptions? network)
            => FetchCrlCore(cert, network, sync: true, CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>
        /// The CRL fetch, run as <see cref="WalkRevocationPath"/> runs: blocking in place when
        /// <paramref name="sync"/>, awaited otherwise. Each distribution point gets the revocation
        /// budget for its whole hop chain; CRL distribution points legitimately redirect, so hops are
        /// followed, by hand, the private-address guard re-run on each. A URL that fails in any way
        /// moves on to the next.
        /// </summary>
        private static async Task<byte[]?> FetchCrlCore(
            X509Certificate cert, SigningNetworkOptions? network, bool sync, CancellationToken ct)
        {
            foreach (Uri crlUri in SafeCrlDistributionUrls(cert))
            {
                try
                {
                    // One budget for the whole hop chain. Passing the caller's token alone left each
                    // hop bounded only by HttpClient.Timeout, so a distribution point redirecting
                    // three times ran on for the full chain.
                    using var budget = RevocationBudgetSource(network, ct);
                    CancellationToken hop = budget.Token;

                    HttpClient client = ClientFor(network);
                    var first = client.GetAsync(crlUri, HttpCompletionOption.ResponseHeadersRead, hop);
                    HttpResponseMessage? resp = sync ? first.GetAwaiter().GetResult() : await first.ConfigureAwait(false);
                    resp = sync
                        ? FollowGuardedRedirects(
                            resp, u => client.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, hop).GetAwaiter().GetResult())
                        : await FollowGuardedRedirectsAsync(
                            resp, u => client.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, hop)).ConfigureAwait(false);
                    if (resp == null) continue;   // blocked hop or redirect limit
                    using (resp)
                    {
                        if (!resp.IsSuccessStatusCode) continue;
                        // The hop budget, not the caller's token alone: with a headers-only send the
                        // body is the one step SendAsync's cancellation does not bound.
                        var read = ReadCappedAsync(resp.Content, hop);
                        var crl = sync ? read.ConfigureAwait(false).GetAwaiter().GetResult() : await read.ConfigureAwait(false);
                        if (crl != null) return crl;
                    }
                }
                catch
                {
                    // try the next URL
                }
            }
            return null;
        }

        /// <summary>
        /// Validates a raw CRL body before it may be embedded in <c>/DSS /CRLs</c>, returning the
        /// original bytes when it is acceptable or <c>null</c> when it is not. Exposed as
        /// <c>internal</c> so tests can feed synthetic CRLs without standing up a distribution
        /// point. Resets and updates <see cref="LastCrlSignatureIssue"/>,
        /// <see cref="LastCrlSignatureValid"/> and <see cref="LastCrlFreshnessIssue"/>.
        /// </summary>
        /// <param name="crlBytes">The fetched body, exactly as the distribution point returned it.</param>
        /// <param name="subject">The certificate the CRL is supposed to cover (diagnostics only).</param>
        /// <param name="issuer">
        /// The subject's issuer — the only key that may have signed a CRL covering it
        /// (RFC 5280 §6.3.3 (b)(1)). Without it nothing can be verified, so the CRL is refused.
        /// </param>
        /// <param name="warnings">
        /// Optional sink for a CRL that is embedded <b>with a reservation</b> — today only the
        /// missing-<c>nextUpdate</c> case within the verifier's age cap, which is legal and therefore
        /// not a refusal. Past the cap it is refused (<see cref="IsCrlCurrent"/>).
        /// </param>
        /// <remarks>
        /// <para><b>Every OCSP response was validated and no CRL was validated at all.</b> A CRL
        /// distribution-point URL comes from the untrusted certificate's own CRL-DP extension and is
        /// fetched over plain HTTP, so an HTTP 200 carrying an error page, a CRL from an unrelated
        /// CA, or a forged CRL listing nothing at all went straight into <c>/DSS /CRLs</c> and
        /// <see cref="TryBuildDss(System.Collections.Generic.IList{Org.BouncyCastle.X509.X509Certificate}, Org.BouncyCastle.X509.X509Certificate, out System.Collections.Generic.List{byte[]}, out System.Collections.Generic.List{byte[]}, out System.Collections.Generic.List{string})"/> reported <c>true</c> — "revocation data obtained". The three
        /// checks below are the CRL counterparts of what
        /// <see cref="ValidateAndFilterOcspResponse"/> already did.</para>
        ///
        /// <para><b>There is deliberately no CRL analogue of
        /// <see cref="EmbedStaleOcspResponses"/>.</b> The verify side already discards a stale CRL
        /// (<c>Cms.RevocationChecker</c> downgrades absence from one to <c>Unknown</c>, since a
        /// certificate may have been revoked after it was issued), so embedding one produces a
        /// document that claims LTV and fails to validate on it. If a caller ever needs the lenient
        /// behaviour, add the toggle additively rather than widening the OCSP one — its name states
        /// what it covers.</para>
        ///
        /// <para><b>What this does not do.</b> It does not refuse a CRL that lists
        /// <paramref name="subject"/> as revoked — that CRL is honest evidence and belongs in the
        /// document, whatever its scope; whether to sign with a revoked certificate is a decision
        /// for the signing path, not for the embedder. A CRL whose scope (<see cref="Cms.CrlScope"/>:
        /// a delta, a CA-only or user-only partition, some reasons, a distribution point the subject
        /// does not name, an indirect CRL) cannot speak for the subject is refused unless it lists
        /// it, because the verifier would read absence from it as <c>Unknown</c>.</para>
        /// </remarks>
        internal static byte[]? ValidateAndFilterCrl(
            byte[] crlBytes, X509Certificate subject, X509Certificate issuer,
            List<string>? warnings = null)
        {
            if (crlBytes == null) return null;

            // Reset per-call so a caller can attribute a refusal to *this* CRL. These run
            // per-certificate over a whole chain, so a leftover would be reported against every
            // certificate examined afterwards (the defect DiagnosticSlotResetTests exists for).
            LastCrlSignatureIssue = null;
            LastCrlFreshnessIssue = null;
            LastCrlSignatureValid = false;

            string serial = subject?.SerialNumber?.ToString() ?? "<unknown>";

            if (issuer == null)
            {
                LastCrlSignatureIssue =
                    $"CRL for serial {serial} cannot be verified: no issuer certificate available.";
                return null;
            }

            X509Crl crl;
            try { crl = new X509CrlParser().ReadCrl(crlBytes); }
            catch (Exception ex)
            {
                LastCrlSignatureIssue = $"CRL for serial {serial} failed to parse: {ex.Message}";
                return null;
            }
            if (crl == null)
            {
                LastCrlSignatureIssue =
                    $"CRL for serial {serial} failed to parse: the body was not a DER/PEM CRL.";
                return null;
            }

            // RFC 5280 §6.3.3 (b)(1): the CRL issuer must be the certificate's issuer. Checking the
            // signature alone is not enough — a CRL from an unrelated CA verifies perfectly against
            // its own key and says nothing about this certificate.
            if (!crl.IssuerDN.Equivalent(issuer.SubjectDN, true))
            {
                LastCrlSignatureIssue =
                    $"CRL for serial {serial} is issued by '{crl.IssuerDN}', not by the " +
                    $"certificate's issuer '{issuer.SubjectDN}'.";
                return null;
            }

            try { crl.Verify(PublicKeyCost.KeyOf(issuer)); }
            catch (Exception ex)
            {
                LastCrlSignatureIssue =
                    $"CRL signature verification failed for serial {serial}: {ex.Message}";
                return null;
            }

            LastCrlSignatureValid = true;

            // RFC 5280 §6.3.3 (b), (c): the CRL must be THIS certificate's — not the issuer's CA-only
            // list, a delta, or a partition by reason or distribution point — before absence from it
            // means anything. The verifier (Cms.RevocationChecker, through the same CrlScope) reads
            // absence from such a CRL as Unknown, so embedding one produces a document that claims
            // LTV and fails to validate on it. One that LISTS the subject is honest evidence whatever
            // its partition and is kept, as the remarks above say of any listing CRL.
            string? scope = subject == null ? null : Cms.CrlScope.WhyItCannotVouchFor(crl, subject);
            if (scope != null && crl.GetRevokedCertificate(subject!.SerialNumber) == null)
            {
                LastCrlSignatureIssue =
                    $"CRL for serial {serial} {scope}, so absence from it cannot vouch for the certificate.";
                return null;
            }

            DateTime thisUpdate;
            DateTime? nextUpdate;
            try
            {
                thisUpdate = crl.ThisUpdate.ToUniversalTime();
                nextUpdate = crl.NextUpdate?.ToUniversalTime();
            }
            catch (Exception ex)
            {
                LastCrlFreshnessIssue =
                    $"CRL for serial {serial}: freshness validation failed: {ex.Message}";
                return null;
            }

            if (!IsCrlCurrent(thisUpdate, nextUpdate, DateTime.UtcNow, out string? issue))
            {
                LastCrlFreshnessIssue = $"CRL for serial {serial}: {issue}";
                return null;
            }

            // Legal (RFC 5280 §5.1.2.5 makes nextUpdate optional) and within the verifier's age cap,
            // but nothing says when this list stops being the current one. Embedded, and said out loud.
            if (!nextUpdate.HasValue)
                warnings?.Add(
                    $"CRL from '{crl.IssuerDN}' carries no nextUpdate (RFC 5280 §5.1.2.5 makes it " +
                    $"optional), so its currency is bounded only by the verifier's " +
                    $"{Cms.RevocationFreshness.DefaultMaxAgeWithoutNextUpdate.TotalDays:0}-day default; " +
                    $"thisUpdate is {thisUpdate:O}.");

            return crlBytes;
        }

        /// <summary>
        /// The reason the CRL just passed to <see cref="ValidateAndFilterCrl"/> was refused, for
        /// the caller-facing warning list. Signature/anchoring is checked first, so it is reported
        /// first when both slots are set.
        /// </summary>
        private static string? CrlRefusalReason()
            => LastCrlSignatureIssue ?? LastCrlFreshnessIssue ?? "failed validation.";

        /// <summary>
        /// Why the most recent CRL was refused on issuer-anchoring, parseability, signature or scope
        /// grounds (which certificates the CRL speaks for — <see cref="Cms.CrlScope"/>), or
        /// <c>null</c> when it verified cleanly. A non-null value means the CRL was <b>not</b>
        /// embedded. A scope refusal leaves <see cref="LastCrlSignatureValid"/> true: the signature
        /// did verify; the list is simply not this certificate's.
        /// </summary>
        public static string? LastCrlSignatureIssue
        {
            get => _lastCrlSignatureIssue;
            private set => _lastCrlSignatureIssue = value;
        }

        [ThreadStatic]
        private static string? _lastCrlSignatureIssue;

        /// <summary>
        /// True when the most recent CRL parsed, named the certificate's issuer as its own issuer,
        /// and carried a signature that verified against that issuer's public key
        /// (RFC 5280 §6.3.3 (b)(1)).
        /// </summary>
        public static bool LastCrlSignatureValid
        {
            get => _lastCrlSignatureValid;
            private set => _lastCrlSignatureValid = value;
        }

        [ThreadStatic]
        private static bool _lastCrlSignatureValid;

        /// <summary>
        /// Why the most recent CRL was refused as stale or future-dated, or <c>null</c> when its
        /// <c>thisUpdate</c>/<c>nextUpdate</c> window was current. A non-null value means the CRL
        /// was <b>not</b> embedded — unlike <see cref="LastOcspFreshnessIssue"/> there is no
        /// override, and a CRL that omits <c>nextUpdate</c> but is within the verifier's age cap is
        /// reported on the caller's warning list rather than here, because it is not a refusal.
        /// </summary>
        public static string? LastCrlFreshnessIssue
        {
            get => _lastCrlFreshnessIssue;
            private set => _lastCrlFreshnessIssue = value;
        }

        [ThreadStatic]
        private static string? _lastCrlFreshnessIssue;

        /// <summary>
        /// Builds a /DSS dictionary PDF object that references pre-written OCSP stream objects.
        /// </summary>
        /// <param name="ocspObjectNumbers">Object numbers of the OCSP stream indirect objects.</param>
        /// <returns>A PdfDictionary suitable for writing as the /DSS catalog entry.</returns>
        public static PdfDictionary BuildDssDictionary(IList<int> ocspObjectNumbers)
            => BuildDssDictionary(ocspObjectNumbers, null);

        /// <summary>
        /// Overload that also embeds CRL stream object references via the
        /// <c>/CRLs</c> entry per ISO 32000-2 §12.8.4.3.  Either or both lists
        /// may be empty; an empty <c>/DSS</c> with neither is still valid.
        /// </summary>
        public static PdfDictionary BuildDssDictionary(
            IList<int> ocspObjectNumbers, IList<int>? crlObjectNumbers)
        {
            var dss = new PdfDictionary();
            dss.Set(PdfName.Type, PdfName.DSS);

            if (ocspObjectNumbers != null && ocspObjectNumbers.Count > 0)
            {
                var ocspsArray = new PdfArray();
                foreach (int objNum in ocspObjectNumbers)
                    ocspsArray.Add(new PdfReference(objNum, 0));
                dss.Set(new PdfName("OCSPs"), ocspsArray);
            }

            if (crlObjectNumbers != null && crlObjectNumbers.Count > 0)
            {
                var crlArray = new PdfArray();
                foreach (int objNum in crlObjectNumbers)
                    crlArray.Add(new PdfReference(objNum, 0));
                dss.Set(new PdfName("CRLs"), crlArray);
            }

            return dss;
        }

        // -----------------------------------------------------------------------

        // Internal for the same reason as FetchCrl: the verifier's online check fetches here.
        internal static byte[]? FetchOcspResponse(X509Certificate subject, X509Certificate issuer,
                                                 SigningNetworkOptions? network)
            => FetchOcspCore(subject, issuer, network, sync: true, CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>
        /// The OCSP fetch, run as <see cref="WalkRevocationPath"/> runs: the responder the subject's
        /// AIA names, asked about the subject under its issuer, the reply validated before it is
        /// returned (<see cref="ValidateAndFilterOcspResponse"/>). <c>null</c> when the subject
        /// names no responder or the responder said nothing.
        /// </summary>
        private static async Task<byte[]?> FetchOcspCore(
            X509Certificate subject, X509Certificate issuer, SigningNetworkOptions? network,
            bool sync, CancellationToken ct)
        {
            // Extract OCSP URL from the subject's Authority Information Access extension.
            string? ocspUrl = GetOcspUrl(subject);
            if (string.IsNullOrEmpty(ocspUrl))
                return null;

            var generator = new OcspReqGenerator();
            generator.AddRequest(new CertificateID(
                new AlgorithmIdentifier(OiwObjectIdentifiers.IdSha1), issuer, subject.SerialNumber));
            byte[] requestBytes = generator.Generate().GetEncoded();

            var post = PostOcspRequestCore(ocspUrl!, requestBytes, network, sync, ct);
            byte[]? responseBytes = sync ? post.GetAwaiter().GetResult() : await post.ConfigureAwait(false);
            if (responseBytes == null || responseBytes.Length == 0)
                return null;

            return ValidateAndFilterOcspResponse(responseBytes, subject, issuer);
        }

        /// <summary>
        /// Validates a raw OCSP response against the RFC 6960 §3.5 / §4.2.2.2
        /// signature requirement and the §3.1.2 freshness window, returning
        /// the original bytes if the response is acceptable for embedding,
        /// or <c>null</c> if it fails any check.  Exposed as <c>internal</c>
        /// so tests can feed synthetic responses without standing up an
        /// HTTP responder.  Resets and updates the
        /// <see cref="LastOcspFreshnessIssue"/>,
        /// <see cref="LastOcspSignatureIssue"/>,
        /// <see cref="LastOcspSubjectIssue"/> and
        /// <see cref="LastOcspSignatureValid"/> diagnostics.
        /// </summary>
        internal static byte[]? ValidateAndFilterOcspResponse(
            byte[] responseBytes, X509Certificate subject, X509Certificate issuer)
        {
            if (responseBytes == null) return null;

            // Reset per-call diagnostic state so callers can attribute failures
            // to *this* fetch rather than a leftover from a previous call.
            LastOcspFreshnessIssue   = null;
            LastOcspSignatureIssue   = null;
            LastOcspSubjectIssue     = null;
            LastOcspSignatureValid   = false;

            OcspResp ocspResp;
            try { ocspResp = new OcspResp(responseBytes); }
            catch (Exception ex)
            {
                LastOcspSignatureIssue = "OCSP response failed to parse: " + ex.Message;
                return null;
            }
            if (ocspResp.Status != OcspRespStatus.Successful)
                return null;

            BasicOcspResp? basic;
            try
            {
                basic = ocspResp.GetResponseObject() as BasicOcspResp;
            }
            catch (Exception ex)
            {
                LastOcspSignatureIssue = "OCSP response was not a BasicOCSPResponse: " + ex.Message;
                return null;
            }
            if (basic == null)
            {
                LastOcspSignatureIssue = "OCSP response did not contain a BasicOCSPResponse.";
                return null;
            }

            // RFC 6960 §4.2.2.2: a response vouches for the certificate its SingleResponse names —
            // the serial and the issuer's hashes. A responder answering about another certificate
            // (or the same serial under another CA) is no evidence for this one, however well it is
            // signed, and embedding it would make the DSS claim evidence it does not hold. The one
            // matcher the verifier uses.
            SingleResp? about = OcspSingleResponse.Find(basic, subject, issuer);
            if (about == null)
            {
                LastOcspSubjectIssue =
                    $"OCSP response names no SingleResponse for serial {subject.SerialNumber} under " +
                    "its issuer; it is about another certificate and is not embedded.";
                return null;
            }

            // RFC 6960 §3.5 mandates verifying the responder's signature on the
            // BasicOCSPResponse before relying on its status. Without this check,
            // a MITM or compromised endpoint can return a fully-fabricated "good"
            // status for a revoked certificate. The responder must either BE the
            // certificate's issuer or be a delegated authority the issuer named, valid now
            // (RFC 6960 §4.2.2.2) — the one search the verifier makes before it reads a status.
            // Trust-anchor / chain validation beyond the immediate issuer is a caller concern
            // (the issuer itself was already validated when it was put into the chain).
            var now = DateTime.UtcNow;
            string? responder = OcspSingleResponse.WhyTheResponderCannotVouch(basic, issuer, now);
            if (responder != null)
            {
                LastOcspSignatureIssue =
                    $"OCSP signature verification failed for serial {subject.SerialNumber}: the response {responder}";
                return null;
            }

            LastOcspSignatureValid = true;

            // Freshness check (RFC 6960 §3.1.2), on the entry about this certificate — the one the
            // verifier reads. Every entry was judged, so a stale entry about ANOTHER certificate
            // refused a response that was current evidence for this one. nextUpdate must not have
            // elapsed, thisUpdate and producedAt must not be in the future. Stale responses are
            // *rejected* (not embedded) — they would otherwise weaken LTV by claiming current
            // revocation state from old data. Callers wanting lenient behaviour can opt in via
            // <see cref="EmbedStaleOcspResponses"/>.
            try
            {
                if (!IsOcspResponseCurrent(about.ThisUpdate.ToUniversalTime(),
                        about.NextUpdate?.ToUniversalTime(), basic.ProducedAt.ToUniversalTime(),
                        now, out string? issue))
                {
                    LastOcspFreshnessIssue =
                        $"OCSP response for serial {subject.SerialNumber}: {issue}";
                    if (!EmbedStaleOcspResponses) return null;
                }
            }
            catch (Exception ex)
            {
                LastOcspFreshnessIssue = "OCSP freshness validation failed: " + ex.Message;
                if (!EmbedStaleOcspResponses) return null;
            }

            return responseBytes;
        }

        /// <summary>
        /// Most recent freshness issue (stale <c>nextUpdate</c>, future
        /// <c>thisUpdate</c>) detected on an OCSP response, or <c>null</c> when
        /// the last response validated cleanly. With
        /// <see cref="EmbedStaleOcspResponses"/> false (default), a non-null
        /// value here also means the response was *not* embedded.
        /// </summary>
        public static string? LastOcspFreshnessIssue
        {
            get => _lastOcspFreshnessIssue;
            private set => _lastOcspFreshnessIssue = value;
        }

        [ThreadStatic]
        private static string? _lastOcspFreshnessIssue;

        /// <summary>
        /// Most recent OCSP signature / responder-anchoring failure, or
        /// <c>null</c> when the last response's signature validated cleanly.
        /// A non-null value means the response was rejected (not embedded).
        /// </summary>
        public static string? LastOcspSignatureIssue
        {
            get => _lastOcspSignatureIssue;
            private set => _lastOcspSignatureIssue = value;
        }

        [ThreadStatic]
        private static string? _lastOcspSignatureIssue;

        /// <summary>
        /// Why the most recent OCSP response was not about the certificate it was fetched for — no
        /// <c>SingleResponse</c> names its serial under its issuer (RFC 6960 §4.2.2.2) — or
        /// <c>null</c> when the last response named it. A non-null value means the response was
        /// rejected (not embedded): a document must not claim revocation evidence it does not hold.
        /// </summary>
        public static string? LastOcspSubjectIssue
        {
            get => _lastOcspSubjectIssue;
            private set => _lastOcspSubjectIssue = value;
        }

        [ThreadStatic]
        private static string? _lastOcspSubjectIssue;

        /// <summary>
        /// True when the most recent OCSP response's signature was verified
        /// against the responder cert, and the responder was anchored to the
        /// certificate's issuer (RFC 6960 §3.5 / §4.2.2.2).
        /// </summary>
        public static bool LastOcspSignatureValid
        {
            get => _lastOcspSignatureValid;
            private set => _lastOcspSignatureValid = value;
        }

        [ThreadStatic]
        private static bool _lastOcspSignatureValid;

        /// <summary>
        /// When <c>true</c>, OCSP responses that fail freshness checks
        /// (<c>nextUpdate</c> elapsed, <c>thisUpdate</c> too far in the future)
        /// are still embedded into the DSS — the issue is recorded on
        /// <see cref="LastOcspFreshnessIssue"/> but the response is kept.
        /// Default: <c>false</c> — stale responses are rejected, matching
        /// RFC 6960 §3.5 and ETSI EN 319 102-1 §5.3.3.
        /// </summary>
        public static bool EmbedStaleOcspResponses { get; set; }

        /// <summary>
        /// Reason the most recent <see cref="FetchCrl"/> call could not parse
        /// the certificate's CRL-distribution-points extension.  <c>null</c> when
        /// the extension was either absent or successfully parsed.
        /// </summary>
        public static string? LastCrlExtParseError
        {
            get => _lastCrlExtParseError;
            private set => _lastCrlExtParseError = value;
        }

        [ThreadStatic]
        private static string? _lastCrlExtParseError;

        private static string? GetOcspUrl(X509Certificate cert)
        {
            // Cleared at the start of every call: LastAiaParseError documents itself as null "when
            // the URL resolved cleanly (or the extension was simply absent)", which only holds if a
            // clean resolution clears a previous certificate's failure. Without this, one
            // unparseable AIA extension was reported against every certificate examined afterwards
            // - and this runs per-certificate over a whole chain.
            LastAiaParseError = null;

            try
            {
                // id-pe-authorityInfoAccess
                var aiaExt = cert.GetExtensionValue(
                    new DerObjectIdentifier(PdfEkuOids.AuthorityInfoAccess));
                if (aiaExt == null) return null;

                var seq = (Asn1Sequence)Asn1Object.FromByteArray(
                    ((Asn1OctetString)aiaExt).GetOctets());

                foreach (Asn1Encodable item in seq)
                {
                    var accessDesc = AccessDescription.GetInstance(item);
                    // id-ad-ocsp — the OCSP access method within the AIA extension.
                    if (accessDesc.AccessMethod.Id == PdfEkuOids.AuthorityInfoAccessOcsp)
                    {
                        var gn = accessDesc.AccessLocation;
                        if (gn.TagNo == GeneralName.UniformResourceIdentifier)
                            return ((DerIA5String)gn.Name).GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                LastAiaParseError = ex.GetType().Name + ": " + ex.Message;
            }

            return null;
        }

        /// <summary>
        /// Reason the most recent <see cref="GetOcspUrl"/> failed to find an
        /// OCSP responder URL in the AIA extension, or <c>null</c> when the URL
        /// resolved cleanly (or the extension was simply absent).
        /// </summary>
        public static string? LastAiaParseError
        {
            get => _lastAiaParseError;
            private set => _lastAiaParseError = value;
        }

        [ThreadStatic]
        private static string? _lastAiaParseError;

        /// <summary>
        /// The most recent HTTP (non-HTTPS) OCSP responder URL contacted, or
        /// <c>null</c> when the last fetch used HTTPS.  Surfaces transport-
        /// security concerns to callers — RFC 6960 permits unencrypted HTTP
        /// because the response is signed, but unencrypted transport leaks
        /// which certificates a verifier is checking.
        /// </summary>
        public static string? LastInsecureOcspUrl
        {
            get => _lastInsecureOcspUrl;
            private set => _lastInsecureOcspUrl = value;
        }

        [ThreadStatic]
        private static string? _lastInsecureOcspUrl;

        /// <summary>
        /// The OCSP POST, run as <see cref="WalkRevocationPath"/> runs, under the revocation budget
        /// in both forms, never under the caller's token alone.
        /// </summary>
        private static async Task<byte[]?> PostOcspRequestCore(string url, byte[] requestBytes,
            SigningNetworkOptions? network, bool sync, CancellationToken ct)
        {
            // Strict scheme whitelist via Uri.TryCreate — defends against URL
            // smuggling and exotic schemes that .NET's HttpClient might honour
            // in unusual hosting environments.  Only http / https are allowed;
            // ldap:// and other RFC-5280 schemes are rejected.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var ocspUri))
                throw new InvalidOperationException($"Malformed OCSP URL: {url}");
            if (ocspUri.Scheme != Uri.UriSchemeHttps && ocspUri.Scheme != Uri.UriSchemeHttp)
                throw new InvalidOperationException(
                    $"Invalid OCSP URL scheme '{ocspUri.Scheme}': only http/https are accepted.");
            // SSRF guard: the OCSP URL is from an untrusted cert AIA extension; refuse hosts
            // that resolve to loopback/private/link-local/metadata addresses.
            if (!PrivateAddressGuard.IsSafeHost(ocspUri.DnsSafeHost))
                throw new InvalidOperationException(
                    $"OCSP URL host '{ocspUri.DnsSafeHost}' resolves to a private/reserved address; refusing (SSRF guard).");

            if (ocspUri.Scheme == Uri.UriSchemeHttp)
            {
                // HTTP is allowed by the OCSP RFC (RFC 6960 §A.1) because the OCSP
                // response itself is signed.  Track that this happened so callers
                // who care about transport-level confidentiality can audit.
                LastInsecureOcspUrl = url;
            }
            else
            {
                LastInsecureOcspUrl = null;
            }

            return await PostGuardedCore(url, requestBytes, "application/ocsp-request", "application/ocsp-response",
                network, sync, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// A POST to an address nobody vetted — the OCSP responder an untrusted certificate names, the
        /// timestamp server a document's seed value names — through the guarded client
        /// (<see cref="ClientFor"/>: the connection made to an address the private-address guard
        /// passed, host-scoped credentials only), under the revocation budget, the body read under
        /// <see cref="MaxResponseBytes"/>; null for a body past it.
        /// </summary>
        internal static async Task<byte[]?> PostGuardedCore(string url, byte[] body, string contentType,
            string accept, SigningNetworkOptions? network, bool sync, CancellationToken ct)
        {
            using (var content = new ByteArrayContent(body))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

                using (var cts = RevocationBudgetSource(network, ct))
                {
                    var requestMsg = new HttpRequestMessage(HttpMethod.Post, url)
                    {
                        Content = content
                    };
                    requestMsg.Headers.Accept.ParseAdd(accept);

                    var send = ClientFor(network)
                        .SendAsync(requestMsg, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    using var response = sync
                        ? send.ConfigureAwait(false).GetAwaiter().GetResult()
                        : await send.ConfigureAwait(false);

                    // A redirect is not followed. Auto-redirect is off on every guarded handler, so
                    // a 3xx is no success status and this throws: a conforming responder does not
                    // redirect a POST, and following one would send the body to a host the guard
                    // never vetted.
                    response.EnsureSuccessStatusCode();

                    var read = ReadCappedAsync(response.Content, cts.Token);
                    return sync ? read.ConfigureAwait(false).GetAwaiter().GetResult() : await read.ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Async variant using fully non-blocking HTTP calls. Hosts that care about avoiding
        /// sync-over-async (ASP.NET, Azure Functions) should call this instead of the sync version.
        /// </summary>
        public static async Task<(bool ok,
            List<byte[]> ocspBytes, List<byte[]> crlBytes, List<string> warnings)>
            TryBuildDssAsync(
                IList<X509Certificate> chain,
                X509Certificate signerCert,
                CancellationToken cancellationToken = default)
            => await WalkRevocationPath(chain, signerCert, null, sync: false, cancellationToken)
                .ConfigureAwait(false);

        /// <summary>
        /// The async variant with credentials, a proxy and a timeout for the fetches.
        /// </summary>
        /// <param name="chain">The certificate chain to walk.</param>
        /// <param name="signerCert">The signer's own certificate.</param>
        /// <param name="network">Credentials, proxy and timeout; null fetches anonymously.</param>
        /// <param name="cancellationToken">Cancels the walk.</param>
        public static async Task<(bool ok,
            List<byte[]> ocspBytes, List<byte[]> crlBytes, List<string> warnings)>
            TryBuildDssAsync(
                IList<X509Certificate> chain,
                X509Certificate signerCert,
                SigningNetworkOptions? network,
                CancellationToken cancellationToken = default)
            => await WalkRevocationPath(chain, signerCert, network, sync: false, cancellationToken)
                .ConfigureAwait(false);
    }
}
