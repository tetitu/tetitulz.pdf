// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Net.Http;
using Tetitulz.Pdf.Core.Net;

namespace Tetitulz.Pdf.HtmlToPdf
{
    /// <summary>
    /// An <see cref="IRemoteResourceLoader"/> safe to point at HTML you do not control: it refuses
    /// private and reserved addresses, follows redirects by hand re-checking every hop, and bounds
    /// each fetch in bytes and in time.
    /// </summary>
    /// <remarks>
    /// <para><b>The guard is on the connection, not only on the name.</b> Every request goes
    /// through <see cref="GuardedHttpFetch"/>: the URL is vetted before anything is sent, and the
    /// connection is dialled to one of the very addresses that were vetted — the client's connect
    /// callback on .NET 5 and later, the library's own transport on .NET Framework — so a name
    /// answering public to the check and <c>169.254.169.254</c> to the connection is refused either
    /// way. <b>Through a proxy the pre-check is all there is</b>: the proxy resolves the host, so a
    /// DNS-rebinding name can still reach a private address behind it (GAPS.md §17a records the
    /// same limit for the revocation fetches). Behind a proxy, prefer your own loader with an
    /// allow-list of hosts.</para>
    ///
    /// <para><b>Reuse one instance.</b> It owns an <see cref="HttpClient"/>; one per conversion is
    /// the socket-exhaustion pattern. It is safe to share across threads and conversions.</para>
    /// </remarks>
    public sealed class GuardedRemoteResourceLoader : IRemoteResourceLoader, IDisposable
    {
        /// <summary>The default per-resource ceiling: 16 MiB — larger than any sane web font or page image.</summary>
        public const long DefaultMaxBytes = 16L * 1024 * 1024;

        /// <summary>The default per-resource budget, covering the redirect chain and the body alike.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

        private readonly HttpClient _client;

        /// <summary>Creates a loader.</summary>
        /// <param name="maxBytes">Largest body accepted per resource; must be positive.</param>
        /// <param name="timeout">
        /// Budget per resource, redirects and body included; <c>null</c> for
        /// <see cref="DefaultTimeout"/>. Must be positive.
        /// </param>
        /// <param name="maxRedirects">Redirect hops followed per resource; zero follows none.</param>
        public GuardedRemoteResourceLoader(
            long maxBytes = DefaultMaxBytes, TimeSpan? timeout = null,
            int maxRedirects = GuardedHttpFetch.DefaultMaxRedirects)
            : this(GuardedHttpFetch.CreateHandler(), maxBytes, timeout, maxRedirects)
        {
        }

        /// <summary>
        /// The seam a test uses to stand a handler in for the network. Production goes through the
        /// public constructor, whose handler is <see cref="GuardedHttpFetch.CreateHandler"/>'s.
        /// </summary>
        internal GuardedRemoteResourceLoader(
            HttpMessageHandler handler, long maxBytes, TimeSpan? timeout, int maxRedirects)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (maxBytes <= 0)
                throw new ArgumentOutOfRangeException(nameof(maxBytes), "The per-resource ceiling must be positive.");
            TimeSpan budget = timeout ?? DefaultTimeout;
            if (budget <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(timeout), "The per-resource budget must be positive.");
            if (maxRedirects < 0)
                throw new ArgumentOutOfRangeException(nameof(maxRedirects), "The redirect limit cannot be negative.");

            MaxBytes = maxBytes;
            Timeout = budget;
            MaxRedirects = maxRedirects;
            // The budget is the fetch's own, applied across the whole chain and the body by
            // GuardedHttpFetch.Get. The client's per-request timeout is switched off so the two
            // cannot disagree about which one fired.
            _client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        }

        /// <summary>Largest body accepted per resource.</summary>
        public long MaxBytes { get; }

        /// <summary>Budget per resource, redirects and body included.</summary>
        public TimeSpan Timeout { get; }

        /// <summary>Redirect hops followed per resource.</summary>
        public int MaxRedirects { get; }

        /// <inheritdoc />
        public byte[]? Load(Uri url, out string? failure)
            => GuardedHttpFetch.Get(_client, url, MaxBytes, MaxRedirects, Timeout, out failure);

        /// <summary>Releases the underlying <see cref="HttpClient"/>.</summary>
        public void Dispose() => _client.Dispose();
    }
}
