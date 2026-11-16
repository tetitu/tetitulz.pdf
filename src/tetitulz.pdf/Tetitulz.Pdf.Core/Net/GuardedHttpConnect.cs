// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq.Expressions;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Tetitulz.Pdf.Core.Net
{
    /// <summary>
    /// Connects an <see cref="HttpClient"/> to an address that has been checked, rather than to a
    /// name that was checked and then resolved again.
    /// </summary>
    /// <remarks>
    /// <para><b>The gap this closes is between the check and the connection.</b>
    /// <see cref="PrivateAddressGuard.IsSafeHost"/> resolves a name, classifies the addresses,
    /// discards them and answers a <c>bool</c>; the caller then fetches <i>by hostname</i>, so the
    /// stack resolves independently and the connection can land somewhere the guard never saw. A
    /// name with a short TTL under an attacker's control answers public to the check and
    /// <c>169.254.169.254</c> to the connection. Nothing about the guard's classification is wrong
    /// — the two answers simply come from two different lookups.</para>
    ///
    /// <para>Assigning <c>ConnectAsync</c> to <c>SocketsHttpHandler.ConnectCallback</c>
    /// removes the second lookup from the picture: the handler hands over the host it wants, this
    /// resolves it <b>once</b>, refuses if anything is private, and opens the socket to one of the
    /// very addresses it just classified. There is no window left to move.</para>
    ///
    /// <para><b>Every runtime that has the hook gets it, whichever build it loads.</b> The
    /// <c>net10.0</c> build assigns it directly. The <c>netstandard2.0</c> build — which a .NET 5 to 9
    /// host loads, since it cannot load <c>net10.0</c> assets — finds <c>SocketsHttpHandler</c> and its
    /// <c>ConnectCallback</c> at run time (<see cref="TryCreateGuardedSocketsHandler"/>) and installs the
    /// same connection through a compiled expression. Only .NET Framework has no remote-address hook
    /// (<c>ServicePoint.BindIPEndPointDelegate</c> binds the <i>local</i> endpoint): there the library's
    /// own transport, <see cref="GuardedHttpTransport"/>, makes this same connection itself, and a
    /// request with credentials or a proxy keeps the pre-check alone (GAPS.md §17a).</para>
    ///
    /// <para><b>This does not replace the pre-check.</b> The guard still runs before a URL is
    /// attempted at all, because it also decides <i>whether</i> to fetch, it reports the refusal
    /// where the caller can attribute it, and it is the only protection a proxied request has. This is
    /// the authoritative half; that one is the fast and portable half.</para>
    ///
    /// <para><b>The proxy is the caller's, and is dialled as configured.</b> The handler opens its
    /// connection to a proxy through the same callback, and a corporate proxy is on a private address
    /// (a local agent on loopback), so vetting it refused every fetch behind one. An endpoint is taken
    /// for the proxy only when it is not the request's own server and is what the proxy answers for
    /// the request (<see cref="IsConfiguredProxy"/>); every other endpoint is vetted.</para>
    /// </remarks>
    public static class GuardedHttpConnect
    {
#if NET
        /// <summary>
        /// A <c>SocketsHttpHandler.ConnectCallback</c> that refuses any host resolving to a
        /// private or reserved address, and connects to the address it checked.
        /// </summary>
        /// <example>
        /// <code>
        /// var handler = new SocketsHttpHandler
        /// {
        ///     AllowAutoRedirect = false,
        ///     ConnectCallback   = GuardedHttpConnect.ConnectAsync,
        /// };
        /// </code>
        /// </example>
        /// <remarks>The system proxy (<see cref="HttpClient.DefaultProxy"/>) is dialled as configured; a
        /// handler given a proxy of its own is made by <see cref="GuardedHttpFetch.CreateHandler"/>.</remarks>
        public static ValueTask<Stream> ConnectAsync(
            SocketsHttpConnectionContext context, CancellationToken cancellationToken)
            => ConnectAsync(context, null, cancellationToken);

        /// <summary><see cref="ConnectAsync(SocketsHttpConnectionContext, CancellationToken)"/> for a handler whose proxy is <paramref name="proxy"/> (<c>null</c>: the system's).</summary>
        internal static ValueTask<Stream> ConnectAsync(
            SocketsHttpConnectionContext context, IWebProxy proxy, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            DnsEndPoint endPoint = context.DnsEndPoint;
            return new ValueTask<Stream>(ConnectForRequestTaskAsync(
                endPoint.Host, endPoint.Port, context.InitialRequestMessage, proxy, cancellationToken));
        }

        /// <summary>
        /// Resolves <paramref name="host"/>, refuses it if anything it resolves to is private or
        /// reserved, and connects to one of the addresses that passed.
        /// </summary>
        /// <param name="host">The hostname or IP literal to reach.</param>
        /// <param name="port">The TCP port.</param>
        /// <param name="cancellationToken">Cancels the connection attempt.</param>
        /// <exception cref="HttpRequestException">
        /// The host resolves to a private or reserved address, does not resolve at all, or none of
        /// its addresses accepted a connection.
        /// </exception>
        public static ValueTask<Stream> ConnectToVettedAddressAsync(
            string host, int port, CancellationToken cancellationToken = default)
            => new ValueTask<Stream>(ConnectToVettedTaskAsync(host, port, cancellationToken));
#endif

        /// <summary>The vetted connection both builds make: resolve once, refuse anything private, dial what passed.</summary>
        internal static async Task<Stream> ConnectToVettedTaskAsync(string host, int port, CancellationToken cancellationToken)
        {
            if (!PrivateAddressGuard.TryResolveSafeAddresses(host, out IPAddress[] addresses))
                throw new HttpRequestException(
                    $"Refusing to connect to '{host}': it does not resolve, or it resolves to a " +
                    "private or reserved address.");

            return await ConnectToAnyAsync(addresses, port, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The callback's connection for one request: the configured proxy dialled as it is named
        /// (<see cref="IsConfiguredProxy"/>), any other endpoint through <see cref="ConnectToVettedTaskAsync"/>.
        /// </summary>
        internal static async Task<Stream> ConnectForRequestTaskAsync(
            string host, int port, HttpRequestMessage request, IWebProxy proxy, CancellationToken cancellationToken)
        {
            if (!IsConfiguredProxy(host, port, request?.RequestUri, proxy ?? DefaultProxy()))
                return await ConnectToVettedTaskAsync(host, port, cancellationToken).ConfigureAwait(false);

            IPAddress[] addresses;
            try { addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false); }
            catch (SocketException ex) { throw new HttpRequestException($"The proxy '{host}' does not resolve.", ex); }
            return await ConnectToAnyAsync(addresses, port, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Whether <paramref name="host"/>:<paramref name="port"/> is the proxy <paramref name="proxy"/>
        /// sends <paramref name="target"/> through — not the target's own server, not a bypassed
        /// request, and the endpoint the proxy answers for it. Positive identification: anything else,
        /// a proxy that cannot be asked included, is vetted.
        /// </summary>
        internal static bool IsConfiguredProxy(string host, int port, Uri target, IWebProxy proxy)
        {
            if (host == null || target == null || !target.IsAbsoluteUri || proxy == null) return false;
            if (IsEndpointOf(host, port, target)) return false;
            try
            {
                if (proxy.IsBypassed(target)) return false;
                Uri via = proxy.GetProxy(target);
                return via != null && via.IsAbsoluteUri && via != target && IsEndpointOf(host, port, via);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is PlatformNotSupportedException
                                       || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return false;
            }
        }

        private static bool IsEndpointOf(string host, int port, Uri uri)
            => uri.Port == port && string.Equals(Unbracketed(uri.IdnHost), Unbracketed(host), StringComparison.OrdinalIgnoreCase);

        private static string Unbracketed(string host)
            => host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']' ? host.Substring(1, host.Length - 2) : host;

        private static IWebProxy DefaultProxy()
        {
            try
            {
#if NET
                return HttpClient.DefaultProxy;
#else
                return WebRequest.DefaultWebProxy;
#endif
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is PlatformNotSupportedException
                                       || ex is NotSupportedException || ex is System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>
        /// Opens a TCP connection to the first of <paramref name="addresses"/> that accepts one.
        /// </summary>
        /// <remarks>
        /// Separated from the vetting so each half can be asserted on its own: that a loopback host
        /// is refused, and that a vetted address really is dialled. A test that only had the
        /// combined method could show a refusal and never show a connection.
        /// </remarks>
        internal static async Task<Stream> ConnectToAnyAsync(
            IPAddress[] addresses, int port, CancellationToken cancellationToken)
        {
            Exception last = null;

            foreach (IPAddress address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };

                try
                {
#if NET
                    await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken)
                                .ConfigureAwait(false);
#else
                    // No cancellable connect on this leg: cancellation closes the socket, which ends the attempt.
                    using (cancellationToken.Register(() => socket.Dispose()))
                        await socket.ConnectAsync(new IPEndPoint(address, port)).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
#endif
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception ex)
                {
                    socket.Dispose();
                    if (ex is OperationCanceledException) throw;
                    if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
                    last = ex;
                }
            }

            throw new HttpRequestException(
                $"No vetted address accepted a connection on port {port}.", last);
        }

        /// <summary>
        /// A <c>SocketsHttpHandler</c> made by reflection, with no automatic redirects and the vetted
        /// connection as its <c>ConnectCallback</c> — or <c>null</c> on a runtime that has no such
        /// handler or hook (.NET Framework). What the <c>netstandard2.0</c> build's
        /// <c>GuardedHttpFetch.CreateHandler</c> uses on a .NET 5 to 9 host.
        /// </summary>
        /// <param name="credentials">Server credentials, or <c>null</c>.</param>
        /// <param name="proxy">An explicit proxy, or <c>null</c> for the system default.</param>
        /// <param name="preAuthenticate">Send credentials with the first request.</param>
        internal static HttpMessageHandler TryCreateGuardedSocketsHandler(
            ICredentials credentials, IWebProxy proxy, bool preAuthenticate)
        {
            Type type = typeof(HttpClientHandler).Assembly.GetType("System.Net.Http.SocketsHttpHandler", throwOnError: false);
            if (type == null || !typeof(HttpMessageHandler).IsAssignableFrom(type)) return null;
            PropertyInfo callback = type.GetProperty("ConnectCallback");
            PropertyInfo redirects = type.GetProperty("AllowAutoRedirect");
            if (callback == null || !callback.CanWrite || redirects == null) return null;

            Delegate connect = BuildConnectCallback(callback.PropertyType, proxy);
            if (connect == null) return null;

            var handler = (HttpMessageHandler)Activator.CreateInstance(type);
            redirects.SetValue(handler, false);
            callback.SetValue(handler, connect);
            if (credentials != null) type.GetProperty("Credentials")?.SetValue(handler, credentials);
            if (proxy != null) type.GetProperty("Proxy")?.SetValue(handler, proxy);
            if (preAuthenticate) type.GetProperty("PreAuthenticate")?.SetValue(handler, true);
            return handler;
        }

        /// <summary>
        /// <c>(context, token) =&gt; new ValueTask&lt;Stream&gt;(ConnectForRequestTaskAsync(context.DnsEndPoint.Host,
        /// context.DnsEndPoint.Port, context.InitialRequestMessage, proxy, token))</c>, typed as
        /// <paramref name="callbackType"/> — the hook's own
        /// <c>Func&lt;SocketsHttpConnectionContext, CancellationToken, ValueTask&lt;Stream&gt;&gt;</c>,
        /// whose types the <c>netstandard2.0</c> build cannot name. <c>null</c> when the shape is not that.
        /// </summary>
        private static Delegate BuildConnectCallback(Type callbackType, IWebProxy proxy)
        {
            if (!callbackType.IsGenericType) return null;
            Type[] arguments = callbackType.GetGenericArguments();
            if (arguments.Length != 3 || arguments[1] != typeof(CancellationToken)) return null;
            PropertyInfo endPoint = arguments[0].GetProperty("DnsEndPoint");
            PropertyInfo request = arguments[0].GetProperty("InitialRequestMessage");
            ConstructorInfo valueTask = arguments[2].GetConstructor(new[] { typeof(Task<Stream>) });
            if (endPoint == null || endPoint.PropertyType != typeof(DnsEndPoint) || valueTask == null
                || request == null || request.PropertyType != typeof(HttpRequestMessage)) return null;

            ParameterExpression context = Expression.Parameter(arguments[0], "context");
            ParameterExpression token = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
            MemberExpression dns = Expression.Property(context, endPoint);
            MethodCallExpression connect = Expression.Call(
                typeof(GuardedHttpConnect).GetMethod(nameof(ConnectForRequestTaskAsync), BindingFlags.NonPublic | BindingFlags.Static),
                Expression.Property(dns, nameof(DnsEndPoint.Host)),
                Expression.Property(dns, nameof(DnsEndPoint.Port)),
                Expression.Property(context, request),
                Expression.Constant(proxy, typeof(IWebProxy)),
                token);
            return Expression.Lambda(callbackType, Expression.New(valueTask, connect), context, token).Compile();
        }
    }
}
