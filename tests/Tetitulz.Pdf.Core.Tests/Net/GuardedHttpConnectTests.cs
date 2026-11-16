// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Net;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Net;

/// <summary>
/// Connecting to an address that was checked, rather than to a name that was checked and then
/// resolved again.
/// </summary>
/// <remarks>
/// <para><b>No network.</b> Every case runs against a listener this test starts on the loopback
/// interface, or against an IP literal. The rebinding race itself would need a DNS server under the
/// test's control, so the two halves are asserted separately — that a host resolving to a private
/// address is refused, and that a vetted address really is dialled — and their composition is one
/// method.</para>
///
/// <para><b>The listener is what makes a refusal falsifiable.</b> "Connecting to localhost throws"
/// passes with nothing listening at all, which is the same result for the wrong reason. Here the
/// port is genuinely open and answering — proved by the control, which gets a response out of it —
/// so a throw can only be the guard.</para>
///
/// <para><b>Nothing here reads a clock, and nothing uses a port the machine might reassign.</b> The
/// listener counts its accepts, and the closed port is held bound-but-not-listening for the test's
/// life; both are deliberate, and the reasons are written on the two fixtures.</para>
/// </remarks>
public class GuardedHttpConnectTests
{
    /// <summary>
    /// A loopback HTTP listener that answers <c>204</c> and counts the connections it accepted.
    /// </summary>
    /// <remarks>
    /// <b>It answers rather than hanging, and it counts rather than being timed.</b> A listener that
    /// accepted and went silent made the control assert "the request eventually failed", which is
    /// also true when nothing connected at all; and reading a task's <c>IsCompleted</c> the instant
    /// a client returns races the accept loop. Answering makes the control a status-code assertion,
    /// and counting makes "was it reached" a question with an answer rather than a timing guess.
    /// </remarks>
    private sealed class LoopbackListener : IDisposable
    {
        private readonly TcpListener _listener;
        private int _connections;
        private readonly ManualResetEventSlim _connected = new ManualResetEventSlim(false);

        internal LoopbackListener()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        /// <summary>How many connections have been accepted so far.</summary>
        internal int Connections => Volatile.Read(ref _connections);

        /// <summary>
        /// Waits for a connection, for the assertion that expects one: on the accept loop's signal,
        /// up to a generous 30 seconds. It polled for two, and a whole-solution run once starved
        /// the accept loop past that (green alone) — a wall clock, where the event is the evidence.
        /// </summary>
        internal bool WaitForConnection() => _connected.Wait(TimeSpan.FromSeconds(30));

        /// <remarks>
        /// <b>The close has to be orderly.</b> Writing the response and disposing the client sends
        /// an RST, and the peer reports "an established connection was aborted" instead of reading
        /// the 204 already in flight — which failed the control every run, for a reason that had
        /// nothing to do with what it was testing. Drain the request, write, shut the send side
        /// down, drain to EOF, then close.
        /// </remarks>
        private async Task ServeAsync()
        {
            byte[] response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            var buffer = new byte[4096];
            try
            {
                while (true)
                {
                    using TcpClient client = await _listener.AcceptTcpClientAsync();
                    Interlocked.Increment(ref _connections);
                    _connected.Set();
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        await stream.ReadAsync(buffer, 0, buffer.Length);
                        await stream.WriteAsync(response, 0, response.Length);
                        await stream.FlushAsync();
                        client.Client.Shutdown(SocketShutdown.Send);
                        while (await stream.ReadAsync(buffer, 0, buffer.Length) > 0) { }
                    }
                    catch { /* the peer may have gone; the count is what this is for */ }
                }
            }
            catch { /* Stop() ends the loop */ }
        }

        public void Dispose()
        {
            _listener.Stop();
            _connected.Dispose();
        }
    }

    /// <summary>
    /// A loopback port guaranteed to refuse a connection, held for the test's life.
    /// </summary>
    /// <remarks>
    /// <b>Bound but never listening</b>, which refuses connections while keeping the port reserved.
    /// The obvious version — bind, read the port, release it — hands back a port anything else on
    /// the machine may take in between, and then the test expecting a refusal gets a connection
    /// instead. That is a whole-machine race dressed as a unit test.
    /// </remarks>
    private sealed class ClosedLoopbackPort : IDisposable
    {
        private readonly Socket _socket;

        internal ClosedLoopbackPort()
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        }

        internal int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

        public void Dispose() => _socket.Dispose();
    }

    // ================================================================ the vetting half

    /// <summary>
    /// <b>The addresses come back, which is the whole difference from <c>IsSafeHost</c>.</b> A
    /// caller that only gets a <c>bool</c> has to name the host again to connect, and the name is
    /// resolved a second time.
    /// </summary>
    [Fact]
    public void APublicLiteralComesBackAsTheAddressToConnectTo()
    {
        Assert.True(PrivateAddressGuard.TryResolveSafeAddresses("8.8.8.8", out IPAddress[] addresses));

        Assert.Equal(IPAddress.Parse("8.8.8.8"), Assert.Single(addresses));
    }

    /// <summary>A host resolving privately is refused, and hands back nothing rather than null.</summary>
    /// <remarks>
    /// <c>localhost</c> is the one name every host resolves privately without a network, which is
    /// what makes this assertable offline.
    /// </remarks>
    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("[::1]")]
    [InlineData("")]
    [InlineData(null)]
    public void APrivateOrUnresolvableHostYieldsNoAddresses(string host)
    {
        Assert.False(PrivateAddressGuard.TryResolveSafeAddresses(host, out IPAddress[] addresses));

        Assert.NotNull(addresses);
        Assert.Empty(addresses);
    }

    /// <summary>
    /// <b>A host that cannot be resolved at all fails closed.</b>
    /// </summary>
    /// <remarks>
    /// <para>Nothing above reaches this: every case there is either an IP literal or a name that
    /// resolves. The mutation flipping the resolver's <c>catch</c> from <c>false</c> to <c>true</c>
    /// therefore survived, and it is the worst direction to be wrong in — a resolution failure
    /// would have read as "safe", with an empty address list behind it.</para>
    ///
    /// <para><b>The name is over-long rather than merely non-existent, and that is deliberate.</b>
    /// <c>Dns.GetHostAddresses</c> rejects a hostname past 255 characters locally, before any query
    /// — so this is offline and deterministic. A name like <c>nowhere.invalid</c> depends on the
    /// resolver: plenty of ISPs answer NXDOMAIN with a public wildcard address, which would make
    /// this test pass or fail by network.</para>
    /// </remarks>
    [Fact]
    public void AnUnresolvableHostFailsClosed()
    {
        string tooLong = new string('a', 300);

        Assert.False(PrivateAddressGuard.TryResolveSafeAddresses(tooLong, out IPAddress[] addresses));
        Assert.Empty(addresses);
        Assert.False(PrivateAddressGuard.IsSafeHost(tooLong));
    }

    /// <summary>The two entry points cannot disagree: one is written in terms of the other.</summary>
    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("localhost", false)]
    public void IsSafeHostAgreesWithTheResolvingForm(string host, bool expected)
    {
        Assert.Equal(expected, PrivateAddressGuard.IsSafeHost(host));
        Assert.Equal(expected, PrivateAddressGuard.TryResolveSafeAddresses(host, out _));
    }

    // ================================================================ the connecting half

    /// <summary>
    /// A vetted address really is dialled — the control that makes the refusals below mean something.
    /// </summary>
    [Fact]
    public async Task AVettedAddressIsConnectedTo()
    {
        using var listener = new LoopbackListener();

        using Stream stream = await GuardedHttpConnect.ConnectToAnyAsync(
            new[] { IPAddress.Loopback }, listener.Port, CancellationToken.None);

        Assert.True(stream.CanWrite);
        Assert.True(listener.WaitForConnection(), "the listener never saw the connection");
    }

    /// <summary><b>The guarded path refuses the very port the control just reached.</b></summary>
    /// <remarks>
    /// Same listener, same port, one method further up: this one resolves and classifies first.
    /// Without the guard it connects, which is exactly what an attacker-chosen OCSP host is after.
    /// </remarks>
    [Fact]
    public async Task AHostResolvingToLoopbackIsRefusedAndNothingIsDialled()
    {
        using var listener = new LoopbackListener();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await GuardedHttpConnect.ConnectToVettedAddressAsync(
                "localhost", listener.Port, CancellationToken.None));

        Assert.Contains("private or reserved", ex.Message);
        Assert.Equal(0, listener.Connections);
    }

    /// <summary>The same refusal for a literal, where no DNS is involved at all.</summary>
    [Fact]
    public async Task ALoopbackLiteralIsRefusedToo()
    {
        using var listener = new LoopbackListener();

        await Assert.ThrowsAsync<HttpRequestException>(
            async () => await GuardedHttpConnect.ConnectToVettedAddressAsync(
                "127.0.0.1", listener.Port, CancellationToken.None));

        Assert.Equal(0, listener.Connections);
    }

    /// <summary>
    /// A refused address and a refused connection are different failures, and the messages say so.
    /// </summary>
    [Fact]
    public async Task ARefusedConnectionIsReportedSeparatelyFromARefusedAddress()
    {
        // ConnectToAnyAsync is past the vetting, so this is the OTHER failure: an address that got
        // through and then would not accept a connection.
        using var closed = new ClosedLoopbackPort();

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await GuardedHttpConnect.ConnectToAnyAsync(
                new[] { IPAddress.Loopback }, closed.Port, CancellationToken.None));

        Assert.Contains("No vetted address accepted a connection", ex.Message);
        Assert.DoesNotContain("private or reserved", ex.Message);
    }

    // ================================================================ wired into a handler

    /// <summary><b>The control: without the callback, the handler reaches the listener.</b></summary>
    /// <remarks>
    /// <para>This is what makes the guarded case below evidence. A test that only showed a request
    /// failing would pass for a dozen reasons that have nothing to do with the guard — nothing
    /// listening, the wrong port, no loopback at all. Here the same URL, listener and handler type
    /// get a <c>204</c> out of it, and the only difference is the one line under test.</para>
    ///
    /// <para><b>The URL names <c>127.0.0.1</c> rather than <c>localhost</c>, and that is not
    /// cosmetic.</b> <c>localhost</c> resolves to <c>::1</c> as well, and this listener binds IPv4 —
    /// so which address the handler reached for was the resolver's choice, and this test failed once
    /// in a full run because of it. A control has to be the deterministic half of the pair. The
    /// name is still exercised, in the guarded case below, where the answer is a refusal that no
    /// resolution order can change.</para>
    /// </remarks>
    [Fact]
    public async Task WithoutTheCallbackTheHandlerReachesLoopback()
    {
        using var listener = new LoopbackListener();
        using var handler = new SocketsHttpHandler();
        using var client = new HttpClient(handler);

        HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{listener.Port}/");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, listener.Connections);
    }

    /// <summary><b>With the callback, the very same request never opens a socket.</b></summary>
    /// <remarks>
    /// <para>One line apart from the control above: same URL, same listener, same handler type. And
    /// the second case is the one the control cannot be — a hostname, refused before any resolution
    /// order can matter.</para>
    ///
    /// <para><b>Each refusal has to name the host the handler asked for.</b> Asserting only that
    /// something was refused let a mutation through: a callback that ignored its argument and vetted
    /// a fixed unresolvable name refused every request too, identically, and every assertion here
    /// still passed. The host in the message is the difference between the guard doing its job and
    /// the guard refusing everything — and it is what a caller needs to attribute the refusal.</para>
    /// </remarks>
    [Fact]
    public async Task WithTheCallbackTheHandlerRefusesLoopbackBeforeConnecting()
    {
        using var listener = new LoopbackListener();
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = GuardedHttpConnect.ConnectAsync,
        };
        using var client = new HttpClient(handler);

        var literal = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync($"http://127.0.0.1:{listener.Port}/"));
        var byName = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync($"http://localhost:{listener.Port}/"));

        Assert.Contains("private or reserved", Flatten(literal));
        Assert.Contains("private or reserved", Flatten(byName));

        Assert.Contains("'127.0.0.1'", Flatten(literal));
        Assert.Contains("'localhost'", Flatten(byName));

        Assert.Equal(0, listener.Connections);
    }

    /// <summary>
    /// <b>The handler the <c>netstandard2.0</c> build makes on a runtime with the hook</b> — found by
    /// reflection, its callback compiled from an expression — refuses as the typed one does, each refusal
    /// naming the host the handler asked for, and opens no socket. The control is the plain handler
    /// above, which reaches the same listener.
    /// </summary>
    [Fact]
    public async Task TheReflectedHandlerRefusesLoopbackAsTheTypedOneDoes()
    {
        using var listener = new LoopbackListener();
        HttpMessageHandler created = GuardedHttpConnect.TryCreateGuardedSocketsHandler(null, null, false)!;
        using var handler = Assert.IsType<SocketsHttpHandler>(created);
        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
        using var client = new HttpClient(handler, disposeHandler: false);

        var literal = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync($"http://127.0.0.1:{listener.Port}/"));
        var byName = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync($"http://localhost:{listener.Port}/"));

        Assert.Contains("'127.0.0.1'", Flatten(literal));
        Assert.Contains("'localhost'", Flatten(byName));
        Assert.Equal(0, listener.Connections);
    }

    /// <summary>The reflected handler carries the caller's credentials, proxy and pre-authentication, as the typed one does.</summary>
    [Fact]
    public void TheReflectedHandlerCarriesTheCallersOptions()
    {
        var credentials = new CredentialCache();
        var proxy = new WebProxy("http://proxy.example:8080");
        using var handler = Assert.IsType<SocketsHttpHandler>(GuardedHttpConnect.TryCreateGuardedSocketsHandler(credentials, proxy, true));
        Assert.Same(credentials, handler.Credentials);
        Assert.Same(proxy, handler.Proxy);
        Assert.True(handler.PreAuthenticate);

        using var plain = Assert.IsType<SocketsHttpHandler>(GuardedHttpConnect.TryCreateGuardedSocketsHandler(null, null, false));
        Assert.Null(plain.Credentials);
        Assert.False(plain.PreAuthenticate);
    }

    /// <summary>
    /// <b>A proxy the caller configured is dialled, wherever it is</b> — a corporate proxy is on a
    /// private address and a local agent on loopback — while a request that bypasses it is still
    /// vetted. <c>SocketsHttpHandler</c> opens its connection to the proxy through the same callback,
    /// and a callback vetting every endpoint refused every fetch behind such a proxy.
    /// </summary>
    /// <remarks>The proxy is the loopback listener, the target a public literal it is asked for; the
    /// bypassed request names a second listener directly, and the guard refuses it before a socket.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AConfiguredProxyOnLoopbackIsDialledAndABypassedRequestIsStillVetted(bool reflected)
    {
        using var proxyListener = new LoopbackListener();
        using var direct = new LoopbackListener();
        var proxy = new WebProxy($"http://127.0.0.1:{proxyListener.Port}", BypassOnLocal: false,
                                 BypassList: new[] { @"127\.0\.0\.2" });
        HttpMessageHandler handler = reflected
            ? GuardedHttpConnect.TryCreateGuardedSocketsHandler(null, proxy, false)!
            : GuardedHttpFetch.CreateHandler(proxy: proxy);
        using var client = new HttpClient(handler);

        HttpResponseMessage response = await client.GetAsync("http://8.8.8.8/ocsp");
        var bypassed = await Assert.ThrowsAsync<HttpRequestException>(
            () => client.GetAsync($"http://127.0.0.2:{direct.Port}/"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, proxyListener.Connections);
        Assert.Contains("'127.0.0.2'", Flatten(bypassed));
        Assert.Contains("private or reserved", Flatten(bypassed));
        Assert.Equal(0, direct.Connections);
    }

    /// <summary>
    /// The proxy is identified positively: an endpoint is dialled unvetted only when it is what the
    /// proxy answers for the request — not merely because it is not the request's own server. The
    /// control is the proxy's own endpoint.
    /// </summary>
    [Fact]
    public void OnlyTheEndpointTheProxyAnswers_IsTakenForTheProxy()
    {
        var target = new Uri("http://8.8.8.8/ocsp");
        var proxy = new WebProxy("http://127.0.0.1:3128");

        Assert.True(GuardedHttpConnect.IsConfiguredProxy("127.0.0.1", 3128, target, proxy));
        Assert.False(GuardedHttpConnect.IsConfiguredProxy("10.0.0.9", 3128, target, proxy));     // another host
        Assert.False(GuardedHttpConnect.IsConfiguredProxy("127.0.0.1", 8080, target, proxy));    // another port
        Assert.False(GuardedHttpConnect.IsConfiguredProxy("8.8.8.8", 80, target, proxy));        // the target itself
        Assert.False(GuardedHttpConnect.IsConfiguredProxy("127.0.0.1", 3128, target, null));     // no proxy
    }

    private static string Flatten(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception e = ex; e != null; e = e.InnerException) sb.Append(e.Message).Append(' ');
        return sb.ToString();
    }
}
