// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Tetitulz.Pdf.Core.Net
{
    /// <summary>
    /// Fetching a URL that untrusted input supplied: the handler such a client is built on, redirects
    /// followed by hand with the guard re-run on every hop, and a body read that stops at a ceiling.
    /// </summary>
    /// <remarks>
    /// <para><b>One implementation for every caller that fetches an attacker-chosen URL</b> — the
    /// revocation fetches in <c>Signing</c> (a certificate's AIA and CRL-DP) and the remote
    /// subresources of <c>HtmlToPdf</c> (a document's <c>src</c> and <c>href</c>). It lives in Core
    /// because neither of those assemblies may reference the other.</para>
    ///
    /// <para><b>The body is read from the headers, never buffered first.</b> Every send goes out with
    /// <see cref="HttpCompletionOption.ResponseHeadersRead"/>. The default,
    /// <c>ResponseContentRead</c>, makes <see cref="HttpClient"/> buffer the whole body before the
    /// call returns, up to <c>MaxResponseContentBufferSize</c> — two gigabytes unless someone lowers
    /// it — so a size check applied to the returned content is applied after the memory was spent.
    /// The revocation fetches were written exactly that way: a 20 MB cap, checked after a hostile
    /// distribution point had already been read in full.</para>
    /// </remarks>
    public static class GuardedHttpFetch
    {
        /// <summary>Redirect hops a fetch follows before refusing. Legitimate servers rarely need more than one.</summary>
        public const int DefaultMaxRedirects = 5;

        /// <summary>Chunk size for the capped body read — a throughput knob only; the ceiling is the caller's.</summary>
        private const int ReadChunkBytes = 80 * 1024;

        /// <summary>
        /// The handler a client reaching untrusted URLs is built on: <b>no automatic redirects</b>, and
        /// a connect callback that dials the address it vetted (<see cref="GuardedHttpConnect"/>) on
        /// every runtime that has the hook — assigned directly on <c>net10.0</c>, installed by
        /// reflection from <c>netstandard2.0</c> on a .NET 5 to 9 host.
        /// </summary>
        /// <remarks>
        /// <para>Automatic redirects would take the first hop unseen, which is the whole attack — a
        /// public URL answering <c>302 Location: http://169.254.169.254/</c>. Follow them with
        /// <see cref="FollowRedirects"/> instead.</para>
        /// <para><b>On .NET Framework</b>, whose <c>HttpClientHandler</c> has no remote-address connect
        /// hook, the handler is the library's own transport (<see cref="GuardedHttpTransport"/>): the
        /// same vetted connection, TLS by the host's name, a bounded HTTP/1.1 exchange. A request with
        /// <paramref name="credentials"/> or a <paramref name="proxy"/>, or to a URL the system proxy
        /// applies to, goes through <c>HttpClientHandler</c> there with the pre-check alone (GAPS.md
        /// §17a): through a proxy the proxy resolves the host, and the transport carries no
        /// authentication.</para>
        /// </remarks>
        /// <param name="credentials">Server credentials, or <c>null</c> for none.</param>
        /// <param name="proxy">An explicit proxy, or <c>null</c> for the system default.</param>
        /// <param name="preAuthenticate">Send credentials with the first request.</param>
        public static HttpMessageHandler CreateHandler(
            ICredentials credentials = null, IWebProxy proxy = null, bool preAuthenticate = false)
        {
#if NET
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = (context, token) => GuardedHttpConnect.ConnectAsync(context, proxy, token),
            };
#else
            HttpMessageHandler guarded = GuardedHttpConnect.TryCreateGuardedSocketsHandler(credentials, proxy, preAuthenticate);
            if (guarded != null) return guarded;
            if (credentials == null && proxy == null) return new GuardedHttpTransport();
            var handler = new HttpClientHandler { AllowAutoRedirect = false };
#endif
            if (credentials != null) handler.Credentials = credentials;
            if (proxy != null) handler.Proxy = proxy;
            if (preAuthenticate) handler.PreAuthenticate = true;
            return handler;
        }

        /// <summary>True for the five redirect status codes: 301, 302, 303, 307, 308.</summary>
        public static bool IsRedirect(HttpResponseMessage response)
        {
            if (response == null) return false;
            int code = (int)response.StatusCode;
            return code == 301 || code == 302 || code == 303 || code == 307 || code == 308;
        }

        /// <summary>
        /// Follows up to <paramref name="maxRedirects"/> redirects by hand, re-running
        /// <see cref="PrivateAddressGuard.IsSafePublicHttpUrl"/> on every target. Returns the first
        /// response that is not a redirect, or <c>null</c> with <paramref name="refusal"/> set when a
        /// hop was refused. The caller owns disposal of what is returned; everything else is disposed
        /// here.
        /// </summary>
        /// <param name="response">The response to the original request.</param>
        /// <param name="send">Sends a request to a vetted target.</param>
        /// <param name="maxRedirects">Hops permitted before refusing.</param>
        /// <param name="refusal">Why the chain was refused — query strings left out — or <c>null</c>.</param>
        public static HttpResponseMessage FollowRedirects(
            HttpResponseMessage response, Func<Uri, HttpResponseMessage> send, int maxRedirects,
            out string refusal)
        {
            if (send == null) throw new ArgumentNullException(nameof(send));
            refusal = null;
            for (int hop = 0; IsRedirect(response); hop++)
            {
                if (!TryNextHop(response, hop, maxRedirects, out Uri target, out refusal))
                {
                    response.Dispose();
                    return null;
                }
                response.Dispose();
                response = send(target);
                if (response == null) return null;
            }
            return response;
        }

        /// <summary>The awaited form of <see cref="FollowRedirects"/>.</summary>
        /// <param name="response">The response to the original request.</param>
        /// <param name="sendAsync">Sends a request to a vetted target.</param>
        /// <param name="maxRedirects">Hops permitted before refusing.</param>
        /// <returns>The final response and a <c>null</c> refusal, or a <c>null</c> response and why.</returns>
        public static async Task<(HttpResponseMessage Response, string Refusal)> FollowRedirectsAsync(
            HttpResponseMessage response, Func<Uri, Task<HttpResponseMessage>> sendAsync, int maxRedirects)
        {
            if (sendAsync == null) throw new ArgumentNullException(nameof(sendAsync));
            for (int hop = 0; IsRedirect(response); hop++)
            {
                if (!TryNextHop(response, hop, maxRedirects, out Uri target, out string refusal))
                {
                    response.Dispose();
                    return (null, refusal);
                }
                response.Dispose();
                response = await sendAsync(target).ConfigureAwait(false);
                if (response == null) return (null, null);
            }
            return (response, null);
        }

        /// <summary>The one hop decision both redirect walks make.</summary>
        private static bool TryNextHop(
            HttpResponseMessage response, int hop, int maxRedirects, out Uri target, out string refusal)
        {
            target = null;
            if (hop >= maxRedirects)
            {
                refusal = $"Exceeded {maxRedirects} redirects.";
                return false;
            }

            Uri location = response.Headers.Location;
            if (location == null)
            {
                refusal = "Redirect response had no Location header.";
                return false;
            }

            // A relative Location resolves against the request it answered. With no request to
            // resolve against there is no target at all — refused, rather than thrown out of the
            // Uri constructor as it once was.
            Uri requested = response.RequestMessage?.RequestUri;
            if (!location.IsAbsoluteUri && (requested == null || !requested.IsAbsoluteUri))
            {
                refusal = "Relative redirect with no request URI to resolve it against.";
                return false;
            }
            target = location.IsAbsoluteUri ? location : new Uri(requested, location);

            if (!PrivateAddressGuard.IsSafePublicHttpUrl(target.ToString(), out _))
            {
                refusal = $"Refused redirect to '{Redact(target)}' (unsafe host or scheme).";
                return false;
            }

            refusal = null;
            return true;
        }

        /// <summary>
        /// Reads a response body into memory, or <c>null</c> when it is longer than
        /// <paramref name="maxBytes"/>. A declared <c>Content-Length</c> over the ceiling is refused
        /// without reading; an absent or lying one is caught while streaming, having read at most one
        /// chunk past the ceiling.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately awaited and cancellable only.</b> A headers-only send returns before the
        /// body arrives, so the body read is the one step the send's cancellation no longer covers; a
        /// blocking read here would let a server trickling one byte a minute hold the caller for as
        /// long as it liked. A synchronous caller blocks on this with its budget's token.
        /// </remarks>
        public static async Task<byte[]> ReadCappedAsync(HttpContent content, long maxBytes, CancellationToken ct)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (content.Headers.ContentLength is long declared && declared > maxBytes) return null;
            using (Stream s = await content.ReadAsStreamAsync().ConfigureAwait(false))
                return await ReadCappedAsync(s, maxBytes, ct).ConfigureAwait(false);
        }

        /// <summary>Reads a stream to its end, or <c>null</c> once it passes <paramref name="maxBytes"/>.</summary>
        public static async Task<byte[]> ReadCappedAsync(Stream stream, long maxBytes, CancellationToken ct)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            using var ms = new MemoryStream();
            byte[] buf = new byte[ReadChunkBytes];
            int read;
            while ((read = await stream.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > maxBytes) return null;
                ms.Write(buf, 0, read);
            }
            return ms.ToArray();
        }

        /// <summary>
        /// GETs an untrusted absolute <c>http(s)</c> URL: the guard first, headers-only sends,
        /// redirects re-checked by hand, a success status required, and the body read under
        /// <paramref name="maxBytes"/> — all inside one <paramref name="timeout"/>, so a slow server
        /// cannot stretch a redirect chain or a trickled body past it.
        /// </summary>
        /// <param name="client">A client built on <see cref="CreateHandler"/>.</param>
        /// <param name="url">The URL to fetch.</param>
        /// <param name="maxBytes">The body ceiling; must be positive.</param>
        /// <param name="maxRedirects">Redirect hops permitted.</param>
        /// <param name="timeout">The budget for the whole fetch; non-positive leaves only the client's own.</param>
        /// <param name="failure">Why nothing came back, or <c>null</c>.</param>
        /// <returns>The body, or <c>null</c> with <paramref name="failure"/> set.</returns>
        public static byte[] Get(
            HttpClient client, Uri url, long maxBytes, int maxRedirects, TimeSpan timeout, out string failure)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes), "The body ceiling must be positive.");

            failure = null;
            if (url == null || !url.IsAbsoluteUri
                || !PrivateAddressGuard.IsSafePublicHttpUrl(url.ToString(), out _))
            {
                failure = url == null
                    ? "no URL was given"
                    : $"'{(url.IsAbsoluteUri ? Redact(url) : url.OriginalString)}' is not an http(s) URL on a public host";
                return null;
            }

            using var budget = new CancellationTokenSource();
            if (timeout > TimeSpan.Zero) budget.CancelAfter(timeout);
            CancellationToken ct = budget.Token;
            try
            {
                HttpResponseMessage response = FollowRedirects(
                    Send(client, url, ct), u => Send(client, u, ct), maxRedirects, out string refusal);
                if (response == null)
                {
                    failure = refusal ?? "the redirect chain ended without a response";
                    return null;
                }
                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        failure = $"the server answered {(int)response.StatusCode} {response.ReasonPhrase}";
                        return null;
                    }
                    byte[] body = ReadCappedAsync(response.Content, maxBytes, ct)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                    if (body == null) failure = $"the response is larger than {maxBytes} bytes";
                    return body;
                }
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                failure = $"the fetch did not finish within {timeout.TotalSeconds:0.###} s";
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException
                                       || ex is OperationCanceledException || ex is InvalidOperationException)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        private static HttpResponseMessage Send(HttpClient client, Uri url, CancellationToken ct)
            => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url),
                                HttpCompletionOption.ResponseHeadersRead, ct)
                     .ConfigureAwait(false).GetAwaiter().GetResult();

        /// <summary>A URL for a diagnostic: scheme, host and path — a query string can carry a secret.</summary>
        private static string Redact(Uri u) => u.GetLeftPart(UriPartial.Path);
    }
}
