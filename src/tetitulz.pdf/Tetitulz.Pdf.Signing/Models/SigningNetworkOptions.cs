// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Net;

namespace Tetitulz.Pdf.Signing.Models
{
    /// <summary>
    /// Credentials, proxy and timeout for the outbound requests signing makes — the TSA POST today.
    /// </summary>
    /// <remarks>
    /// <para><b>Plain <c>System.Net</c> types, because the seam has to work on both legs.</b>
    /// <c>Tetitulz.Pdf.Signing</c> multi-targets <c>netstandard2.0</c>, which is why
    /// <see cref="Timestamping.TsaClient"/> is on <c>HttpWebRequest</c> at all — so a caller-supplied
    /// <c>HttpMessageHandler</c> is not expressible here. <see cref="ICredentials"/> and
    /// <see cref="IWebProxy"/> are: they exist on both targets, and both <c>HttpWebRequest</c> and
    /// <c>HttpClientHandler</c> accept them. The register recorded the constraint and read it as
    /// "no seam"; it rules out one seam.</para>
    ///
    /// <para><b>Why this is needed at all.</b> Most commercial timestamp authorities are
    /// authenticated and most enterprises reach the internet through a proxy, so without this the
    /// difference is "timestamping works" and "timestamping works here".</para>
    ///
    /// <para><b>It does not widen what a URL may be.</b> The scheme restriction and the
    /// no-redirect rule are applied before any of this is used: a caller-configured TSA URL is
    /// trusted enough to reach a private host, and that trust still does not transfer to whatever a
    /// 3xx points at.</para>
    /// </remarks>
    public sealed class SigningNetworkOptions
    {
        /// <summary>
        /// Credentials for the request. <c>CredentialCache.DefaultNetworkCredentials</c> uses the
        /// process identity; a <see cref="NetworkCredential"/> supplies a specific one.
        /// </summary>
        /// <remarks>
        /// <b>These authenticate the TSA request as given, but a revocation fetch carries them only
        /// when they are a <see cref="CredentialCache"/>.</b> An OCSP, CRL or AIA URL comes out of an
        /// untrusted certificate's extension, so a credential that answers any host's challenge — a
        /// bare <see cref="NetworkCredential"/>, or <c>DefaultNetworkCredentials</c> — would be sent to
        /// whatever host a chain names; a <see cref="CredentialCache"/> binds each credential to a
        /// host, scheme and authentication type and is the one shape that both reaches a private
        /// revocation responder and stays safe (<c>DssBuilder.RevocationCredentials</c>). The TSA URL
        /// is the caller's own, so the TSA request uses whatever is set.
        /// </remarks>
        public ICredentials? Credentials { get; set; }

        /// <summary>
        /// The proxy to reach the endpoint through. Null leaves the platform default in place,
        /// which is <b>not</b> the same as "no proxy" — pass an explicitly empty
        /// <see cref="WebProxy"/> for that.
        /// </summary>
        public IWebProxy? Proxy { get; set; }

        /// <summary>
        /// Request timeout, for the TSA request and for every OCSP and CRL fetch. Null keeps each
        /// transport's built-in one (30 s for the TSA, 15 s for revocation data); zero or negative
        /// disables it; a value beyond <c>int.MaxValue</c> milliseconds is clamped to that.
        /// </summary>
        /// <remarks>
        /// A timeout is part of a network policy rather than a tuning knob: a TSA that accepts the
        /// connection and never answers would otherwise hold the signing call for the platform
        /// default, which on some hosts is effectively forever. Every transport reads the value
        /// through <see cref="EffectiveTimeout"/>, so it means the same thing wherever it is read.
        /// </remarks>
        public TimeSpan? Timeout { get; set; }

        /// <summary>
        /// The timeout a transport applies under a policy: <paramref name="builtIn"/> when the
        /// policy is null or sets none, <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>
        /// when it sets a non-positive one, and otherwise the value, clamped to the
        /// <c>int.MaxValue</c> milliseconds that both <c>HttpClient.Timeout</c> and
        /// <c>HttpWebRequest.Timeout</c> can express.
        /// </summary>
        /// <remarks>
        /// The one reading for the TSA request and for every OCSP and CRL fetch. Each transport had
        /// its own: a non-positive value disabled the TSA's timeout and kept the revocation
        /// built-in, an overlong one was clamped for the TSA and thrown out by <c>HttpClient</c>,
        /// and the revocation fetches capped whatever was set at the built-in through a fixed
        /// cancellation — so one policy object meant three things depending on who read it.
        /// </remarks>
        internal static TimeSpan EffectiveTimeout(SigningNetworkOptions? network, TimeSpan builtIn)
        {
            if (network?.Timeout is not { } t) return builtIn;
            if (t <= TimeSpan.Zero) return System.Threading.Timeout.InfiniteTimeSpan;
            return t.TotalMilliseconds > int.MaxValue ? TimeSpan.FromMilliseconds(int.MaxValue) : t;
        }

        /// <summary>
        /// Whether to send credentials before being challenged. Default <c>false</c>.
        /// </summary>
        /// <remarks>
        /// <b>Opt-in, because it sends the credential to whatever answers.</b> Some TSAs require it
        /// (they return 400 rather than 401 for an unauthenticated POST), which is the only reason
        /// it exists; with the default the credential is only sent in response to a challenge.
        /// </remarks>
        public bool PreAuthenticate { get; set; }
    }
}
