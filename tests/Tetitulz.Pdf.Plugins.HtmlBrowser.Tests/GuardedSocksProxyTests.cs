// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Tetitulz.Pdf.Plugins.HtmlBrowser;

namespace Tetitulz.Pdf.Plugins.HtmlBrowser.Tests;

/// <summary>
/// The proxy the browser is launched behind, spoken to as a browser speaks to it (RFC 1928): which
/// hosts it connects to, which it refuses, and that a refused host is never dialled. No browser is
/// involved; what Chromium sends through its proxy setting is Chromium's.
/// </summary>
public class GuardedSocksProxyTests
{
    private const string AllowedName = "allowed.test";

    /// <summary>A loopback server that answers each chunk it reads with the same bytes reversed.</summary>
    private sealed class ReversingServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private int _accepted;

        internal ReversingServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(); }
                    catch { return; }
                    System.Threading.Interlocked.Increment(ref _accepted);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try
                            {
                                var buffer = new byte[256];
                                int n;
                                while ((n = await client.GetStream().ReadAsync(buffer, 0, buffer.Length)) > 0)
                                {
                                    byte[] reply = buffer.Take(n).Reverse().ToArray();
                                    await client.GetStream().WriteAsync(reply, 0, reply.Length);
                                }
                            }
                            catch (IOException) { }
                        }
                    });
                }
            });
        }

        internal int Port { get; }
        internal int Accepted => System.Threading.Volatile.Read(ref _accepted);
        public void Dispose() => _listener.Stop();
    }

    /// <summary>A proxy whose guard admits <see cref="AllowedName"/> at the loopback address and nothing else.</summary>
    private static GuardedSocksProxy ProxyAllowing(List<string> asked = null) => new GuardedSocksProxy(host =>
    {
        asked?.Add(host);
        return host == AllowedName ? new[] { IPAddress.Loopback } : Array.Empty<IPAddress>();
    });

    private static async Task<TcpClient> ConnectTo(GuardedSocksProxy proxy)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, proxy.Port);
        client.ReceiveTimeout = 20_000;
        return client;
    }

    /// <summary>
    /// Reads exactly <paramref name="count"/> bytes, or as many as arrive before the stream ends.
    /// A stream that neither delivers nor ends is given twenty seconds and then failed by name, so
    /// a connection the proxy should have closed shows as a failure rather than a hung run.
    /// </summary>
    private static async Task<byte[]> Read(NetworkStream stream, int count)
    {
        var buffer = new byte[count];
        int read = 0;
        using var patience = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            while (read < count)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), patience.Token);
                if (n <= 0) break;
                read += n;
            }
        }
        catch (IOException) { }
        catch (OperationCanceledException)
        {
            Assert.Fail($"the connection stayed open and silent: {read} of {count} byte(s) arrived in twenty seconds.");
        }
        return buffer.Take(read).ToArray();
    }

    private static async Task<byte[]> Greet(NetworkStream stream, params byte[] methods)
    {
        byte[] greeting = new byte[] { 5, (byte)methods.Length }.Concat(methods).ToArray();
        await stream.WriteAsync(greeting, 0, greeting.Length);
        return await Read(stream, 2);
    }

    private static byte[] RequestForName(string name, int port, byte command = 1)
        => new byte[] { 5, command, 0, 3, (byte)name.Length }
            .Concat(Encoding.ASCII.GetBytes(name))
            .Concat(new[] { (byte)(port >> 8), (byte)port }).ToArray();

    private static byte[] RequestForAddress(IPAddress address, int port)
        => new byte[] { 5, 1, 0, (byte)(address.AddressFamily == AddressFamily.InterNetwork ? 1 : 4) }
            .Concat(address.GetAddressBytes())
            .Concat(new[] { (byte)(port >> 8), (byte)port }).ToArray();

    private static async Task<byte[]> Ask(NetworkStream stream, byte[] request)
    {
        await stream.WriteAsync(request, 0, request.Length);
        return await Read(stream, 10);
    }

    /// <summary>
    /// The control every refusal below stands on: a host the guard admits is connected to, and
    /// bytes pass both ways — so a refused host's silence is the refusal, not a proxy that
    /// connects to nothing.
    /// </summary>
    [Fact]
    public async Task AHostTheGuardAdmits_IsConnected_AndBytesPassBothWays()
    {
        using var server = new ReversingServer();
        using GuardedSocksProxy proxy = ProxyAllowing();
        using TcpClient client = await ConnectTo(proxy);
        NetworkStream stream = client.GetStream();

        Assert.Equal(new byte[] { 5, 0 }, await Greet(stream, 0));
        byte[] reply = await Ask(stream, RequestForName(AllowedName, server.Port));
        Assert.Equal(GuardedSocksProxy.ReplySucceeded, reply[1]);

        byte[] sent = Encoding.ASCII.GetBytes("through-the-proxy");
        await stream.WriteAsync(sent, 0, sent.Length);
        Assert.Equal(sent.Reverse().ToArray(), await Read(stream, sent.Length));
        Assert.Equal(1, server.Accepted);
        Assert.Equal(0, proxy.RefusedConnections);
    }

    /// <summary>
    /// The real guard, asked for the addresses a page must not reach: each gets the "not allowed
    /// by ruleset" reply, and the loopback server listening on that very port is never dialled.
    /// </summary>
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("192.168.0.10")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task AnAddressTheGuardRefuses_IsNeverDialled(string address)
    {
        using var server = new ReversingServer();
        using var proxy = new GuardedSocksProxy();
        using TcpClient client = await ConnectTo(proxy);
        NetworkStream stream = client.GetStream();

        Assert.Equal(new byte[] { 5, 0 }, await Greet(stream, 0));
        byte[] reply = await Ask(stream, RequestForAddress(IPAddress.Parse(address), server.Port));

        Assert.Equal(10, reply.Length);
        Assert.Equal(GuardedSocksProxy.ReplyNotAllowedByRuleset, reply[1]);
        Assert.Empty(await Read(stream, 1));            // the proxy closed the connection after refusing
        Assert.Equal(0, server.Accepted);
        Assert.Equal(1, proxy.RefusedConnections);
    }

    /// <summary>The name a browser sends is what is vetted: <c>localhost</c> is refused by the real guard as its address is.</summary>
    [Fact]
    public async Task ALoopbackName_IsRefusedByTheRealGuard()
    {
        using var server = new ReversingServer();
        using var proxy = new GuardedSocksProxy();
        using TcpClient client = await ConnectTo(proxy);
        NetworkStream stream = client.GetStream();

        await Greet(stream, 0);
        byte[] reply = await Ask(stream, RequestForName("localhost", server.Port));

        Assert.Equal(GuardedSocksProxy.ReplyNotAllowedByRuleset, reply[1]);
        Assert.Equal(0, server.Accepted);
    }

    /// <summary>
    /// CONNECT alone is served. BIND would open a listening port on the page's behalf and UDP
    /// ASSOCIATE a relay; both are refused by name, for a host the guard would admit.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task ACommandOtherThanConnect_IsRefused(byte command)
    {
        using var server = new ReversingServer();
        using GuardedSocksProxy proxy = ProxyAllowing();
        using TcpClient client = await ConnectTo(proxy);
        NetworkStream stream = client.GetStream();

        await Greet(stream, 0);
        byte[] reply = await Ask(stream, RequestForName(AllowedName, server.Port, command));

        Assert.Equal(GuardedSocksProxy.ReplyCommandNotSupported, reply[1]);
        Assert.Equal(0, server.Accepted);
    }

    /// <summary>A name with a byte no host name has is refused without the resolver being asked about it.</summary>
    [Theory]
    [InlineData("allowed.test evil")]
    [InlineData("allowed.test\u0000")]
    [InlineData("a/b")]
    [InlineData("user@allowed.test")]
    public async Task BytesThatAreNotAHostName_AreRefusedUnresolved(string name)
    {
        var asked = new List<string>();
        using GuardedSocksProxy proxy = ProxyAllowing(asked);
        using TcpClient client = await ConnectTo(proxy);
        NetworkStream stream = client.GetStream();

        await Greet(stream, 0);
        byte[] reply = await Ask(stream, RequestForName(name, 80));

        Assert.Equal(GuardedSocksProxy.ReplyNotAllowedByRuleset, reply[1]);
        Assert.Empty(asked);
    }

    /// <summary>The resolver's control for the test above: an ordinary name is what it is asked about.</summary>
    [Fact]
    public async Task AnOrdinaryName_IsWhatTheGuardIsAsked()
    {
        var asked = new List<string>();
        using GuardedSocksProxy proxy = ProxyAllowing(asked);
        using TcpClient client = await ConnectTo(proxy);
        NetworkStream stream = client.GetStream();

        await Greet(stream, 0);
        byte[] reply = await Ask(stream, RequestForName("other.example", 80));

        Assert.Equal(GuardedSocksProxy.ReplyNotAllowedByRuleset, reply[1]);
        Assert.Equal(new[] { "other.example" }, asked);
        Assert.Equal(1, proxy.RefusedConnections);
    }

    /// <summary>A client that offers only authenticated methods is told none is acceptable (RFC 1928 §3).</summary>
    [Fact]
    public async Task AClientOfferingNoUnauthenticatedMethod_IsToldNoneIsAcceptable()
    {
        using GuardedSocksProxy proxy = ProxyAllowing();
        using TcpClient client = await ConnectTo(proxy);

        Assert.Equal(new byte[] { 5, 0xFF }, await Greet(client.GetStream(), 2));
        Assert.Empty(await Read(client.GetStream(), 1));
    }

    /// <summary>A client speaking another protocol version is closed with nothing said to it.</summary>
    [Fact]
    public async Task AClientThatIsNotSocks5_IsClosedUnanswered()
    {
        using GuardedSocksProxy proxy = ProxyAllowing();
        using TcpClient client = await ConnectTo(proxy);
        NetworkStream stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 4, 1, 0, 80, 127, 0, 0, 1, 0 }, 0, 9);

        Assert.Empty(await Read(stream, 2));
    }

    /// <summary>
    /// Disposing ends the proxy: the tunnel that was open is closed, and the port no longer
    /// accepts — a browser cannot keep a connection past the guard that vetted it.
    /// </summary>
    [Fact]
    public async Task Disposing_ClosesTheOpenTunnel_AndStopsListening()
    {
        using var server = new ReversingServer();
        GuardedSocksProxy proxy = ProxyAllowing();
        using TcpClient client = await ConnectTo(proxy);
        NetworkStream stream = client.GetStream();
        await Greet(stream, 0);
        Assert.Equal(GuardedSocksProxy.ReplySucceeded, (await Ask(stream, RequestForName(AllowedName, server.Port)))[1]);

        proxy.Dispose();

        Assert.Empty(await Read(stream, 1));
        using var late = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => late.ConnectAsync(IPAddress.Loopback, proxy.Port));
    }

    /// <summary>
    /// A flood of idle connections is bounded: past <see cref="GuardedSocksProxy.MaxConnections"/>
    /// the next one is closed unanswered, and one that was already open is still served.
    /// </summary>
    [Fact]
    public async Task PastTheConnectionCeiling_AFloodOfIdleConnections_IsClosedUnanswered()
    {
        using var server = new ReversingServer();
        using GuardedSocksProxy proxy = ProxyAllowing();
        var idle = new List<TcpClient>();
        try
        {
            for (int i = 0; i < GuardedSocksProxy.MaxConnections; i++) idle.Add(await ConnectTo(proxy));

            // The first is answered, which shows all of them were accepted before the next is tried.
            Assert.Equal(new byte[] { 5, 0 }, await Greet(idle[0].GetStream(), 0));

            using TcpClient onePast = await ConnectTo(proxy);
            await onePast.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, 0, 3);
            Assert.Empty(await Read(onePast.GetStream(), 2));
        }
        finally
        {
            foreach (TcpClient client in idle) client.Dispose();
        }
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("xn--bcher-kva.example", true)]
    [InlineData("_dmarc.example.com", true)]
    [InlineData("exa mple.com", false)]
    [InlineData("example.com:80", false)]
    [InlineData("[::1]", false)]
    public void AHostNameIsLettersDigitsHyphenDotAndUnderscore(string name, bool isName)
        => Assert.Equal(isName ? name : null, GuardedSocksProxy.HostName(Encoding.ASCII.GetBytes(name)));
}
