// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Net;

namespace Tetitulz.Pdf.Plugins.HtmlBrowser
{
    /// <summary>
    /// A SOCKS5 proxy on the loopback interface that the browser is launched behind, so that every
    /// connection a page causes — a request, a beacon, a WebSocket, a worker's fetch, a preconnect —
    /// is made here, to an address this process has vetted, or not at all.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a proxy, when the context already routes requests.</b> A request route sees what
    /// the page's renderer asks for. It does not see a <c>navigator.sendBeacon</c>, which the
    /// browser process sends, nor a WebSocket a worker opens, and it answers before the browser
    /// resolves the name again to connect. The browser's network stack has one door that all of
    /// those pass, its proxy setting, and SOCKS5 hands the proxy the host <i>name</i>
    /// (RFC 1928 §5, address type 3): the name is resolved once, here, by
    /// <see cref="PrivateAddressGuard.TryResolveSafeAddresses"/>, and the connection is dialled to
    /// one of the addresses that came back. The address checked is the address connected to.</para>
    /// <para><b>What it is not.</b> It carries TCP. A page's WebRTC candidate is UDP and does not
    /// pass through it. It asks for no credentials — the browsers offer none to a SOCKS proxy — so
    /// for the length of a conversion any local process may connect through it, to the public
    /// addresses it could reach anyway and to nothing else.</para>
    /// <para>One instance serves one browser, and is disposed after it: disposing closes the
    /// listener and every connection still open.</para>
    /// </remarks>
    internal sealed class GuardedSocksProxy : IDisposable
    {
        /// <summary>Connections served at once; one past it is closed unanswered.</summary>
        internal const int MaxConnections = 256;

        /// <summary>How long a client has to say where it wants to go.</summary>
        internal static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

        /// <summary>How long one vetted address is given to accept the connection.</summary>
        internal static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);

        private const byte Version = 5;
        private const byte NoAuthentication = 0;
        private const byte NoAcceptableMethod = 0xFF;
        private const byte CommandConnect = 1;
        private const byte AddressIPv4 = 1;
        private const byte AddressDomainName = 3;
        private const byte AddressIPv6 = 4;

        /// <summary>RFC 1928 §6 reply codes this proxy gives.</summary>
        internal const byte ReplySucceeded = 0;
        internal const byte ReplyNotAllowedByRuleset = 2;
        internal const byte ReplyHostUnreachable = 4;
        internal const byte ReplyCommandNotSupported = 7;
        internal const byte ReplyAddressTypeNotSupported = 8;

        private readonly TcpListener _listener;
        private readonly Func<string, IPAddress[]> _vettedAddresses;
        private readonly object _gate = new object();
        private readonly HashSet<TcpClient> _open = new HashSet<TcpClient>();
        private int _refused;
        private bool _disposed;

        /// <summary>A proxy that connects only where <see cref="PrivateAddressGuard"/> allows.</summary>
        internal GuardedSocksProxy() : this(SafeAddresses)
        {
        }

        /// <summary>
        /// The seam a test uses to name the hosts that may be reached, since the guard itself
        /// refuses the loopback address every test server listens on.
        /// </summary>
        /// <param name="vettedAddresses">
        /// The addresses a host may be reached at, or none to refuse it. Called once a connection.
        /// </param>
        internal GuardedSocksProxy(Func<string, IPAddress[]> vettedAddresses)
        {
            _vettedAddresses = vettedAddresses ?? throw new ArgumentNullException(nameof(vettedAddresses));
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        /// <summary>The loopback port the proxy listens on.</summary>
        internal int Port { get; }

        /// <summary>The proxy as a browser's proxy setting names it.</summary>
        internal string Server => "socks5://127.0.0.1:" + Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>How many connections were refused because the guard does not allow their host.</summary>
        internal int RefusedConnections => Volatile.Read(ref _refused);

        private static IPAddress[] SafeAddresses(string host)
            => PrivateAddressGuard.TryResolveSafeAddresses(host, out IPAddress[] addresses) ? addresses : Array.Empty<IPAddress>();

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }   // the listener was stopped
                catch (SocketException) { return; }

                if (!Track(client))
                {
                    client.Dispose();
                    continue;
                }
                _ = ServeAsync(client);
            }
        }

        private bool Track(TcpClient client)
        {
            lock (_gate)
            {
                if (_disposed || _open.Count >= MaxConnections) return false;
                _open.Add(client);
                return true;
            }
        }

        private void Untrack(TcpClient client)
        {
            lock (_gate) _open.Remove(client);
            client.Dispose();
        }

        private async Task ServeAsync(TcpClient client)
        {
            TcpClient upstream = null;
            try
            {
                client.NoDelay = true;
                NetworkStream stream = client.GetStream();

                (string host, int port, byte refusal) = await ReadRequestAsync(client, stream).ConfigureAwait(false);
                if (host == null)
                {
                    if (refusal != 0) await ReplyAsync(stream, refusal).ConfigureAwait(false);
                    return;
                }

                IPAddress[] addresses = _vettedAddresses(host) ?? Array.Empty<IPAddress>();
                if (addresses.Length == 0)
                {
                    Interlocked.Increment(ref _refused);
                    await ReplyAsync(stream, ReplyNotAllowedByRuleset).ConfigureAwait(false);
                    return;
                }

                upstream = await ConnectAsync(addresses, port).ConfigureAwait(false);
                if (upstream == null || !Track(upstream))
                {
                    upstream?.Dispose();
                    upstream = null;
                    await ReplyAsync(stream, ReplyHostUnreachable).ConfigureAwait(false);
                    return;
                }

                await ReplyAsync(stream, ReplySucceeded).ConfigureAwait(false);

                // Bytes both ways until either side ends; then both are closed, which ends the other
                // copy. A browser does not half-close a connection it still reads from.
                NetworkStream far = upstream.GetStream();
                Task toServer = stream.CopyToAsync(far);
                Task toClient = far.CopyToAsync(stream);
                await Task.WhenAny(toServer, toClient).ConfigureAwait(false);
                // The copy still running ends with the close below; its fault is this method's doing.
                _ = toServer.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                _ = toClient.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
            }
            catch (IOException) { }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally
            {
                if (upstream != null) Untrack(upstream);
                Untrack(client);
            }
        }

        /// <summary>
        /// Reads the greeting and the request (RFC 1928 §3, §4). Answers the host and port to
        /// connect to, or a null host with the reply to give — zero for a client that is not
        /// speaking SOCKS5, which is closed unanswered.
        /// </summary>
        private static async Task<(string Host, int Port, byte Refusal)> ReadRequestAsync(TcpClient client, NetworkStream stream)
        {
            using (var timeout = new CancellationTokenSource(HandshakeTimeout))
            using (timeout.Token.Register(() => client.Close()))
            {
                var head = new byte[2];
                if (!await FillAsync(stream, head, 2).ConfigureAwait(false) || head[0] != Version) return (null, 0, 0);

                var methods = new byte[head[1]];
                if (!await FillAsync(stream, methods, methods.Length).ConfigureAwait(false)) return (null, 0, 0);
                bool offersNone = Array.IndexOf(methods, NoAuthentication) >= 0;
                await stream.WriteAsync(new[] { Version, offersNone ? NoAuthentication : NoAcceptableMethod }, 0, 2).ConfigureAwait(false);
                if (!offersNone) return (null, 0, 0);

                var request = new byte[4];
                if (!await FillAsync(stream, request, 4).ConfigureAwait(false) || request[0] != Version) return (null, 0, 0);

                string host;
                switch (request[3])
                {
                    case AddressIPv4:
                    case AddressIPv6:
                        var raw = new byte[request[3] == AddressIPv4 ? 4 : 16];
                        if (!await FillAsync(stream, raw, raw.Length).ConfigureAwait(false)) return (null, 0, 0);
                        host = new IPAddress(raw).ToString();
                        break;
                    case AddressDomainName:
                        var length = new byte[1];
                        if (!await FillAsync(stream, length, 1).ConfigureAwait(false) || length[0] == 0) return (null, 0, 0);
                        var name = new byte[length[0]];
                        if (!await FillAsync(stream, name, name.Length).ConfigureAwait(false)) return (null, 0, 0);
                        host = HostName(name);
                        if (host == null) return (null, 0, ReplyNotAllowedByRuleset);
                        break;
                    default:
                        return (null, 0, ReplyAddressTypeNotSupported);
                }

                var portBytes = new byte[2];
                if (!await FillAsync(stream, portBytes, 2).ConfigureAwait(false)) return (null, 0, 0);
                int port = (portBytes[0] << 8) | portBytes[1];

                // CONNECT alone: BIND would open a listening port for the page, UDP ASSOCIATE a
                // relay to anywhere.
                if (request[1] != CommandConnect) return (null, 0, ReplyCommandNotSupported);
                if (port == 0) return (null, 0, ReplyNotAllowedByRuleset);
                return (host, port, 0);
            }
        }

        /// <summary>
        /// A domain name as text, or <c>null</c> for bytes that are not one: a browser sends a
        /// host in its ASCII form, so a byte outside letters, digits, hyphen, dot and underscore
        /// is not a name a resolver should be handed.
        /// </summary>
        internal static string HostName(byte[] name)
        {
            foreach (byte b in name)
            {
                bool ok = (b >= 'a' && b <= 'z') || (b >= 'A' && b <= 'Z') || (b >= '0' && b <= '9')
                          || b == '-' || b == '.' || b == '_';
                if (!ok) return null;
            }
            return Encoding.ASCII.GetString(name);
        }

        private static async Task<TcpClient> ConnectAsync(IPAddress[] addresses, int port)
        {
            foreach (IPAddress address in addresses)
            {
                var upstream = new TcpClient(address.AddressFamily) { NoDelay = true };
                try
                {
                    Task connect = upstream.ConnectAsync(address, port);
                    if (await Task.WhenAny(connect, Task.Delay(ConnectTimeout)).ConfigureAwait(false) == connect)
                    {
                        await connect.ConfigureAwait(false);
                        return upstream;
                    }
                    _ = connect.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                upstream.Dispose();
            }
            return null;
        }

        private static Task ReplyAsync(NetworkStream stream, byte reply)
            => stream.WriteAsync(new byte[] { Version, reply, 0, AddressIPv4, 0, 0, 0, 0, 0, 0 }, 0, 10);

        private static async Task<bool> FillAsync(NetworkStream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer, read, count - read).ConfigureAwait(false);
                if (n <= 0) return false;
                read += n;
            }
            return true;
        }

        /// <summary>Stops listening and closes every connection still open.</summary>
        public void Dispose()
        {
            TcpClient[] open;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                open = new TcpClient[_open.Count];
                _open.CopyTo(open);
                _open.Clear();
            }
            try { _listener.Stop(); } catch (SocketException) { }
            foreach (TcpClient client in open) client.Dispose();
        }
    }
}
