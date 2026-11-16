// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Net;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Net;

/// <summary>
/// <see cref="GuardedHttpTransport"/>: the library's own HTTP/1.1 transport for .NET Framework, where
/// <c>HttpClientHandler</c> has no remote-address hook — the vetted connection, TLS by the host's name,
/// and a response read under ceilings a hostile server cannot move.
/// </summary>
/// <remarks>
/// <para><b>The exchange is scripted in memory.</b> The transport's connection seam hands it a stream
/// that answers with the scripted bytes a few at a time, so a line splits across reads, and records what
/// was written. No socket carries the hostile framings: an HTTP-aware filter on the loopback interface
/// (an antivirus web shield, on the machine these were written on) rewrote a malformed chunked response
/// in flight, and a test would then have asserted about the filter.</para>
/// <para><b>Sockets where the socket is the point</b>: the vetted connection refusing a loopback host
/// whose listener the seam's control reaches, and TLS against a loopback server, where the server sees
/// the name the client authenticated with.</para>
/// <para><b>No clocks.</b> A stalled stream signals when a read waits on it; a cancellation is asked for
/// after that. That includes <c>HttpClient.Timeout</c>'s default of 100 s, which every scripted client
/// turns off: on 2026-10-21 the five-bytes-a-read chunk-line test waited 1 min 51 s for its thread
/// under a whole-solution coverage run and failed on the client's timeout, not the transport's
/// refusal. A hang still fails, at the scripted stall's own minute.</para>
/// </remarks>
public sealed class GuardedHttpTransportTests
{
    // ---------------------------------------------------------------- the scripted connection

    /// <summary>
    /// One connection's stream: the scripted response, a few bytes a read, then the end — or a stall,
    /// of a read or of every write, until the stream is closed. A stall that is never closed gives up
    /// after a minute with a <see cref="TimeoutException"/>, so a transport that loses a cancellation
    /// fails its test instead of hanging the run.
    /// </summary>
    private sealed class ScriptedStream : Stream
    {
        private readonly byte[] _response;
        private readonly int _stallAt;
        private readonly int _bytesPerRead;
        private readonly bool _stallWrites;
        private readonly TaskCompletionSource<bool> _closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        internal readonly MemoryStream Written = new MemoryStream();
        internal readonly ManualResetEventSlim Stalled = new ManualResetEventSlim(false);

        /// <summary>Whether a stall was ended by the stream's close — the cancellation's evidence — rather than by giving up.</summary>
        internal bool ClosedWhileStalled { get; private set; }

        internal ScriptedStream(byte[] response, int stallAt, int bytesPerRead, bool stallWrites)
        {
            _response = response; _stallAt = stallAt; _bytesPerRead = bytesPerRead; _stallWrites = stallWrites;
        }

        /// <summary>A stalled peer, as a .NET Framework socket waits on it: the token is not heard, only the close.</summary>
        private async Task StallAsync()
        {
            Stalled.Set();
            if (await Task.WhenAny(_closed.Task, Task.Delay(TimeSpan.FromMinutes(1))) != _closed.Task)
                throw new TimeoutException("The stalled stream was never closed.");
            ClosedWhileStalled = true;
            throw new ObjectDisposedException(nameof(ScriptedStream));
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            int limit = _stallAt >= 0 ? Math.Min(_stallAt, _response.Length) : _response.Length;
            if (_position >= limit)
            {
                if (_stallAt < 0) return 0;
                await StallAsync();
            }
            int n = Math.Min(count, Math.Min(limit - _position, _bytesPerRead));
            Buffer.BlockCopy(_response, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) { lock (Written) Written.Write(buffer, offset, count); }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_stallWrites) await StallAsync();
            Write(buffer, offset, count);
        }
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            _closed.TrySetResult(true);
            base.Dispose(disposing);
        }
    }

    /// <summary>The connections a transport opens, each answering with the next scripted response; what each was sent.</summary>
    private sealed class Script
    {
        private readonly Func<int, string> _response;
        internal readonly ConcurrentQueue<ScriptedStream> Opened = new ConcurrentQueue<ScriptedStream>();
        internal readonly ConcurrentQueue<string> Hosts = new ConcurrentQueue<string>();
        internal int StallAt { get; init; } = -1;
        internal int BytesPerRead { get; init; } = 5;
        internal bool StallWrites { get; init; }

        internal Script(Func<int, string> response) => _response = response;
        internal Script(string response) : this(_ => response) { }

        internal int Connections => Opened.Count;
        internal ScriptedStream Last => Opened.Last();
        internal string Sent(int connection = -1) => Encoding.ASCII.GetString((connection < 0 ? Last : Opened.ElementAt(connection)).Written.ToArray());

        internal GuardedHttpTransport Transport(Func<Uri, bool> usesProxy = null, HttpMessageHandler proxied = null)
            => new GuardedHttpTransport((host, port, ct) =>
            {
                Hosts.Enqueue(host + ":" + port);
                var stream = new ScriptedStream(Encoding.ASCII.GetBytes(_response(Opened.Count)), StallAt, BytesPerRead, StallWrites);
                Opened.Enqueue(stream);
                return Task.FromResult<Stream>(stream);
            }, null, usesProxy ?? (_ => false), proxied);
    }

    private const string Url = "http://8.8.8.8/";

    private static async Task<(HttpResponseMessage Response, byte[] Body)> GetAsync(Script script, string url = Url)
    {
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead);
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        return (response, body);
    }

    private static async Task<string> BodyOf(string response) => Encoding.ASCII.GetString((await GetAsync(new Script(response))).Body);

    // ---------------------------------------------------------------- the connection

    /// <summary>A loopback server answering every connection with <c>200 ok</c>, counting them.</summary>
    private sealed class LoopbackServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private int _connections;

        internal LoopbackServer()
        {
            _listener.Start();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        TcpClient client = await _listener.AcceptTcpClientAsync();
                        Interlocked.Increment(ref _connections);
                        _ = Task.Run(async () =>
                        {
                            using (client)
                            {
                                try
                                {
                                    NetworkStream s = client.GetStream();
                                    await ReadHeadAsync(s);
                                    byte[] r = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok");
                                    await s.WriteAsync(r, 0, r.Length);
                                    client.Client.Shutdown(SocketShutdown.Send);
                                    var drain = new byte[256];
                                    while (await s.ReadAsync(drain, 0, drain.Length) > 0) { }
                                }
                                catch { /* the client may have gone */ }
                            }
                        });
                    }
                }
                catch { /* Stop() ends the loop */ }
            });
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        internal int Connections => Volatile.Read(ref _connections);
        public void Dispose() => _listener.Stop();
    }

    private static async Task ReadHeadAsync(Stream s)
    {
        var one = new byte[1];
        int matched = 0;
        while (matched < 4 && await s.ReadAsync(one, 0, 1) == 1)
            matched = (one[0] == (matched % 2 == 0 ? '\r' : '\n')) ? matched + 1 : (one[0] == '\r' ? 1 : 0);
    }

    /// <summary>
    /// The .NET Framework leg authenticates TLS under the host's <c>ServicePointManager.SecurityProtocol</c>,
    /// as <c>HttpClientHandler</c> there does: each protocol its own value, a combination their union,
    /// <c>SystemDefault</c> the operating system's choice (<see cref="SslProtocols.None"/>).
    /// </summary>
    [Fact]
    public void TheHostsSecurityProtocol_IsTheTlsProtocolsAsked()
    {
#pragma warning disable SYSLIB0039 // TLS 1.0 and 1.1 are named here only to pin the mapping
        Assert.Equal(SslProtocols.Tls12, GuardedHttpTransport.ProtocolsOf(SecurityProtocolType.Tls12));
        Assert.Equal(SslProtocols.Tls13, GuardedHttpTransport.ProtocolsOf(SecurityProtocolType.Tls13));
        Assert.Equal(SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12,
            GuardedHttpTransport.ProtocolsOf(SecurityProtocolType.Tls | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12));
#pragma warning restore SYSLIB0039
        Assert.Equal(SslProtocols.None, GuardedHttpTransport.ProtocolsOf(SecurityProtocolType.SystemDefault));
    }

    /// <summary>
    /// The transport <see cref="GuardedHttpFetch.CreateHandler"/> builds connects through the vetted
    /// connection: a loopback host, by literal and by name, is refused, and the listener — which the
    /// control below reaches through the same transport type — sees no connection.
    /// </summary>
    [Fact]
    public async Task TheDefaultTransportRefusesALoopbackHost_AndDialsNothing()
    {
        using var server = new LoopbackServer();
        using (var client = new HttpClient(new GuardedHttpTransport()))
        {
            var literal = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://127.0.0.1:{server.Port}/"));
            var byName = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync($"http://localhost:{server.Port}/"));
            Assert.Contains("'127.0.0.1'", literal.Message, StringComparison.Ordinal);
            Assert.Contains("private or reserved", byName.Message, StringComparison.Ordinal);
        }
        Assert.Equal(0, server.Connections);

        // The control: the same listener, dialled past the vetting.
        var dialled = new GuardedHttpTransport((h, p, ct) => GuardedHttpConnect.ConnectToAnyAsync(new[] { IPAddress.Loopback }, server.Port, ct), null, _ => false, null);
        using (var client = new HttpClient(dialled))
            Assert.Equal("ok", await client.GetStringAsync($"http://127.0.0.1:{server.Port}/"));
        Assert.Equal(1, server.Connections);
    }

    /// <summary>The connection is asked for the URL's own host and port — what the vetting resolves.</summary>
    [Fact]
    public async Task TheConnectionIsAskedForTheUrlsHostAndPort()
    {
        var script = new Script("HTTP/1.1 204 No Content\r\n\r\n");
        await GetAsync(script, "http://public.example:8443/x");
        Assert.Equal("public.example:8443", Assert.Single(script.Hosts));
        // The TLS port by default; the handshake then fails on the script, after the connection was asked for.
        await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(script, "https://other.example/"));
        Assert.Equal("other.example:443", script.Hosts.Last());
    }

    // ---------------------------------------------------------------- the request

    /// <summary>
    /// A GET goes out as one HTTP/1.1 request: the path and query, the host and port, the transport's
    /// own <c>Connection: close</c> and identity encoding over the caller's, the caller's other
    /// headers — and no length.
    /// </summary>
    [Fact]
    public async Task AGetIsWrittenAsOneHttp11Request()
    {
        var script = new Script("HTTP/1.1 204 No Content\r\n\r\n");
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new HttpRequestMessage(HttpMethod.Get, "http://public.example:8080/a/b?q=1&r=%20");
        request.Headers.Accept.ParseAdd("application/pkix-crl");
        request.Headers.TryAddWithoutValidation("Connection", "keep-alive");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip");
        await client.SendAsync(request);

        string[] lines = script.Sent().Split("\r\n");
        Assert.Equal("GET /a/b?q=1&r=%20 HTTP/1.1", lines[0]);
        Assert.Contains("Host: public.example:8080", lines);
        Assert.Contains("Connection: close", lines);
        Assert.Contains("Accept-Encoding: identity", lines);
        Assert.Contains("Accept: application/pkix-crl", lines);
        Assert.DoesNotContain("Connection: keep-alive", lines);
        Assert.DoesNotContain("Accept-Encoding: gzip", lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Content-Length", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, lines.Count(l => l.StartsWith("Host:", StringComparison.Ordinal)));
        Assert.EndsWith("\r\n\r\n", script.Sent(), StringComparison.Ordinal);
    }

    /// <summary>A default port is left out of the host; an IPv6 literal is bracketed.</summary>
    [Fact]
    public async Task TheHostHeader_NamesTheHostAsTheUrlDoes()
    {
        var script = new Script("HTTP/1.1 204 No Content\r\n\r\n");
        await GetAsync(script, "http://public.example/");
        Assert.Contains("\r\nHost: public.example\r\n", script.Sent(), StringComparison.Ordinal);
        await GetAsync(script, "http://[2001:db8::1]:8080/");
        Assert.Contains("\r\nHost: [2001:db8::1]:8080\r\n", script.Sent(), StringComparison.Ordinal);
        Assert.Equal("2001:db8::1:8080", script.Hosts.Last());
    }

    /// <summary>A POST carries its body, its type and the transport's length for it — an OCSP request's shape.</summary>
    [Fact]
    public async Task APostCarriesItsBodyAndItsLength()
    {
        var script = new Script("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n");
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes("REQUESTBODY"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/ocsp-request");
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://8.8.8.8/ocsp") { Content = content });

        string sent = script.Sent();
        Assert.StartsWith("POST /ocsp HTTP/1.1\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("\r\nContent-Type: application/ocsp-request\r\n", sent, StringComparison.Ordinal);
        Assert.Contains("\r\nContent-Length: 11\r\n", sent, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\nREQUESTBODY", sent, StringComparison.Ordinal);
    }

    /// <summary>An empty POST states a length of 0; a HEAD, like a GET, states none.</summary>
    [Fact]
    public async Task AnEmptyPostStatesALength_AHeadDoesNot()
    {
        var script = new Script("HTTP/1.1 204 No Content\r\n\r\n");
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, Url));
        Assert.Contains("\r\nContent-Length: 0\r\n", script.Sent(), StringComparison.Ordinal);
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url));
        Assert.StartsWith("HEAD / HTTP/1.1\r\n", script.Sent(), StringComparison.Ordinal);
        Assert.DoesNotContain("Content-Length", script.Sent(), StringComparison.Ordinal);
    }

    /// <summary>A header value holding a line break would write a header of its own: refused before anything is dialled.</summary>
    [Fact]
    public async Task AHeaderValueHoldingALineBreak_IsRefusedBeforeConnecting()
    {
        var script = new Script("HTTP/1.1 204 No Content\r\n\r\n");
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        var request = new HttpRequestMessage(HttpMethod.Get, Url);
        Assert.True(request.Headers.TryAddWithoutValidation("X-Note", "a\r\nInjected: 1"));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        Assert.Equal(0, script.Connections);

        // The control: the same header without the break is written.
        var plain = new HttpRequestMessage(HttpMethod.Get, Url);
        plain.Headers.TryAddWithoutValidation("X-Note", "a b");
        await client.SendAsync(plain);
        Assert.Contains("\r\nX-Note: a b\r\n", script.Sent(), StringComparison.Ordinal);
    }

    /// <summary>A method other than GET, POST or HEAD is not sent, nor a URL that is not http(s).</summary>
    [Fact]
    public async Task AnotherMethodOrScheme_IsNotSent()
    {
        var script = new Script("HTTP/1.1 204 No Content\r\n\r\n");
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Put, Url)));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "ftp://8.8.8.8/f")));
        Assert.Equal(0, script.Connections);
    }

    // ---------------------------------------------------------------- the response's framing

    /// <summary>A body of a declared length: read to that length, the length and type on the content's headers — where the caller's ceiling looks first.</summary>
    [Fact]
    public async Task AContentLengthBody_IsReadToItsLength()
    {
        var (response, body) = await GetAsync(new Script("HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: 5\r\nX-Server: s\r\n\r\nhelloEXTRA"));
        Assert.Equal("hello", Encoding.ASCII.GetString(body));
        Assert.Equal(5, response.Content.Headers.ContentLength);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("s", response.Headers.GetValues("X-Server").Single());
        Assert.Equal("OK", response.ReasonPhrase);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>The caller's ceiling refuses a declared length over it, having read nothing of the body.</summary>
    [Fact]
    public async Task ADeclaredLengthOverTheCeiling_IsRefusedByTheCallersRead()
    {
        const string head = "HTTP/1.1 200 OK\r\nContent-Length: 1000000\r\n\r\n";
        var script = new Script(head + "body") { StallAt = head.Length };
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url), HttpCompletionOption.ResponseHeadersRead);
        Assert.Null(await GuardedHttpFetch.ReadCappedAsync(response.Content, 1000, CancellationToken.None));
        Assert.False(script.Last.Stalled.IsSet, "the body was read");
    }

    /// <summary>A chunked body: chunks of either case of hex digit, an extension, a trailer — decoded whole, a line split across reads.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task AChunkedBody_IsDecoded(int bytesPerRead)
    {
        var script = new Script(
            "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" +
            "5\r\nhello\r\n" + "1;name=value\r\n,\r\n" + "A\r\n0123456789\r\n" + "b\r\nabcdefghijk\r\n" +
            "0\r\nX-Trailer: done\r\n\r\n") { BytesPerRead = bytesPerRead };
        Assert.Equal("hello,0123456789abcdefghijk", Encoding.ASCII.GetString((await GetAsync(script)).Body));
    }

    /// <summary>With neither length nor coding, the body runs to the close; an empty line before the status line is passed over.</summary>
    [Fact]
    public async Task ABodyWithNoFraming_RunsToTheClose()
        => Assert.Equal("until the end", await BodyOf("\r\nHTTP/1.0 200 OK\r\n\r\nuntil the end"));

    /// <summary>A HEAD, a 204 and a 304 have no body whatever their headers say.</summary>
    [Fact]
    public async Task HeadAnd204And304_HaveNoBody()
    {
        var script = new Script(n => n switch
        {
            0 => "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello",
            1 => "HTTP/1.1 204 No Content\r\nContent-Length: 5\r\n\r\nhello",
            _ => "HTTP/1.1 304 Not Modified\r\nContent-Length: 5\r\n\r\nhello",
        });
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Url));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        foreach (var expected in new[] { HttpStatusCode.NoContent, HttpStatusCode.NotModified })
        {
            var r = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url));
            Assert.Equal(expected, r.StatusCode);
            Assert.Empty(await r.Content.ReadAsByteArrayAsync());
        }
        // The control: the same declared length on a 200 to a GET is read.
        Assert.Equal("hello", await BodyOf("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello"));
    }

    /// <summary>A connection closing short of the declared length, or inside a chunk, is an error when the body is read — not a short body.</summary>
    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nshort")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n9\r\nshort")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n0\r\nX-Trailer: cut")]
    public async Task ABodyCutShort_IsAnError(string response)
    {
        using var client = new HttpClient(new Script(response).Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        HttpResponseMessage r = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url), HttpCompletionOption.ResponseHeadersRead);
        Stream body = await r.Content.ReadAsStreamAsync();
        await Assert.ThrowsAsync<IOException>(async () => { var b = new byte[64]; while (await body.ReadAsync(b, 0, b.Length) > 0) { } });
    }

    /// <summary>A chunk not followed by its CRLF is refused.</summary>
    [Fact]
    public async Task AChunkWithoutItsLineEnd_IsRefused()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BodyOf("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nokXX\r\n0\r\n\r\n"));
        Assert.Contains("does not end with CRLF", Flatten(ex), StringComparison.Ordinal);
    }

    /// <summary>
    /// A redirect comes back as itself, its request attached, so <see cref="GuardedHttpFetch"/>
    /// resolves a relative location and re-checks every target: a private one is refused, a relative
    /// one followed.
    /// </summary>
    [Fact]
    public async Task ARedirect_IsFollowedByTheFetch_AndAPrivateOneRefused()
    {
        var toPrivate = new Script("HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1/secret\r\nContent-Length: 0\r\n\r\n");
        using (var client = new HttpClient(toPrivate.Transport()) { Timeout = Timeout.InfiniteTimeSpan })
        {
            Assert.Null(GuardedHttpFetch.Get(client, new Uri("http://8.8.8.8/start"), 1000, 5, TimeSpan.FromMinutes(1), out string failure));
            Assert.Contains("Refused redirect", failure, StringComparison.Ordinal);
        }
        Assert.Equal(1, toPrivate.Connections);

        var relative = new Script(n => n == 0
            ? "HTTP/1.1 301 Moved\r\nLocation: /next\r\nContent-Length: 0\r\n\r\n"
            : "HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\ndone");
        using (var client = new HttpClient(relative.Transport()) { Timeout = Timeout.InfiniteTimeSpan })
            Assert.Equal("done", Encoding.ASCII.GetString(GuardedHttpFetch.Get(client, new Uri("http://8.8.8.8/start"), 1000, 5, TimeSpan.FromMinutes(1), out _)!));
        Assert.StartsWith("GET /next HTTP/1.1\r\n", relative.Sent(1), StringComparison.Ordinal);
        Assert.Equal(new[] { "8.8.8.8:80", "8.8.8.8:80" }, relative.Hosts.ToArray());
    }

    // ---------------------------------------------------------------- the ceilings

    /// <summary>Interim responses are passed over up to the ceiling — the control, at it — and one more is refused.</summary>
    [Fact]
    public async Task InterimResponses_ArePassedOverUpToTheCeiling()
    {
        string Interims(int n) => string.Concat(Enumerable.Repeat("HTTP/1.1 100 Continue\r\nX-I: 1\r\n\r\n", n));
        Assert.Equal("ok", await BodyOf(Interims(GuardedHttpTransport.MaxInformationalResponses) + "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => BodyOf(Interims(GuardedHttpTransport.MaxInformationalResponses + 1) + "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"));
        Assert.Contains("interim responses", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A header section past the ceiling is refused; one just under it is read (the control).</summary>
    [Fact]
    public async Task AHeaderSectionPastTheCeiling_IsRefused()
    {
        string Padded(int bytes) => "HTTP/1.1 200 OK\r\nX-Pad: " + new string('a', bytes) + "\r\nContent-Length: 2\r\n\r\nok";
        Assert.Equal("ok", await BodyOf(Padded(GuardedHttpTransport.MaxHeaderBytes - 200)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => BodyOf(Padded(GuardedHttpTransport.MaxHeaderBytes + 10)));
        Assert.Contains("longer than", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A header section of short lines that never ends stops at the ceiling: of 400 000 bytes of header
    /// lines, the transport reads about the ceiling's worth and refuses — the evidence is how far into
    /// the script it read, not a clock.
    /// </summary>
    [Fact]
    public async Task HostileHeaders_AnEndlessHeaderSection_StopsAtTheCeiling()
    {
        var sb = new StringBuilder("HTTP/1.1 200 OK\r\n");
        for (int i = 0; i < 40000; i++) sb.Append("X-H").Append(i % 10).Append(": v\r\n");
        var script = new Script(sb.ToString()) { BytesPerRead = 4096 };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(script));
        Assert.Contains("longer than", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, script.Connections);
    }

    /// <summary>
    /// A line that never ends stops at the ceiling: 400 000 bytes with no line feed are refused once the
    /// line passes the header section's ceiling, not read to the close.
    /// </summary>
    [Fact]
    public async Task HostileHeaders_ALineThatNeverEnds_StopsAtTheCeiling()
    {
        var script = new Script("HTTP/1.1 200 OK\r\nX-Endless: " + new string('a', 400000)) { BytesPerRead = 4096 };
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => GetAsync(script));
        Assert.Contains("longer than", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A trailer section past the ceiling is refused too: a chunked body's end is a header section of its own.</summary>
    [Fact]
    public async Task HostileTrailers_PastTheCeiling_AreRefused()
    {
        var sb = new StringBuilder("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nok\r\n0\r\n");
        for (int i = 0; i < 40000; i++) sb.Append("X-T: v\r\n");
        sb.Append("\r\n");
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BodyOf(sb.ToString()));
        Assert.Contains("longer than", Flatten(ex), StringComparison.Ordinal);
    }

    /// <summary>
    /// A chunk-size line past its ceiling is refused; a long extension under it is accepted (the
    /// control) — whether the line arrives a few bytes at a time, passing the ceiling before its end,
    /// or whole in one read, its end with it.
    /// </summary>
    [Theory]
    [InlineData(5)]
    [InlineData(4096)]
    public async Task AChunkSizeLinePastTheCeiling_IsRefused(int bytesPerRead)
    {
        string With(int extension) => "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n2;e=" + new string('x', extension) + "\r\nok\r\n0\r\n\r\n";
        Assert.Equal("ok", Encoding.ASCII.GetString((await GetAsync(new Script(With(GuardedHttpTransport.MaxChunkLineBytes - 10)) { BytesPerRead = bytesPerRead })).Body));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => GetAsync(new Script(With(GuardedHttpTransport.MaxChunkLineBytes + 10)) { BytesPerRead = bytesPerRead }));
        Assert.Contains("longer than", Flatten(ex), StringComparison.Ordinal);
    }

    /// <summary>A chunk size past any body, or not hexadecimal, or absent, is refused; the largest that fits a long is read as a size (the control).</summary>
    [Theory]
    [InlineData("8000000000000000", "larger than any body")]
    [InlineData("FFFFFFFFFFFFFFFFF", "larger than any body")]
    [InlineData("12G", "not hexadecimal")]
    [InlineData("-1", "not hexadecimal")]
    [InlineData("0x2", "not hexadecimal")]
    [InlineData("", "holds no size")]
    [InlineData(";ext", "holds no size")]
    public async Task AChunkSizeThatIsNotOne_IsRefused(string size, string why)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BodyOf("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n" + size + "\r\nok\r\n0\r\n\r\n"));
        Assert.Contains(why, Flatten(ex), StringComparison.Ordinal);
    }

    /// <summary>The largest chunk size a long holds is a size — the body is then cut short, an I/O error, not a refusal of the size.</summary>
    [Fact]
    public async Task TheLargestChunkSize_IsASize()
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => BodyOf("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n7FFFFFFFFFFFFFFF\r\nok"));
        Assert.DoesNotContain("larger than any body", Flatten(ex), StringComparison.Ordinal);
        Assert.Contains("inside a chunk", Flatten(ex), StringComparison.Ordinal);
    }

    /// <summary>
    /// The framings RFC 9112 refuses: both a coding and a length, a coding other than chunked, two
    /// different lengths, a length that is not digits, a folded header line, a status line that is not
    /// HTTP/1.x, a status out of range, a switch of protocols, a header with no name or a space in it.
    /// </summary>
    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nContent-Length: 2\r\n\r\n2\r\nok\r\n0\r\n\r\n", "both Transfer-Encoding and Content-Length")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip, chunked\r\n\r\n2\r\nok\r\n0\r\n\r\n", "not chunked alone")]
    [InlineData("HTTP/1.1 200 OK\r\nTransfer-Encoding: gzip\r\nTransfer-Encoding: chunked\r\n\r\n2\r\nok\r\n0\r\n\r\n", "not chunked alone")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Length: 3\r\n\r\nokk", "two different lengths")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 2x\r\n\r\nok", "not a length")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: +2\r\n\r\nok", "not a length")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 1234567890123456789\r\n\r\nok", "not a length")]
    [InlineData("HTTP/1.1 200 OK\r\nX-A: one\r\n two\r\nContent-Length: 2\r\n\r\nok", "folds a header line")]
    [InlineData("HTTP/2 200 OK\r\nContent-Length: 2\r\n\r\nok", "not HTTP/1.x")]
    [InlineData("HTTP/1.1 999 Nope\r\nContent-Length: 2\r\n\r\nok", "out of range")]
    [InlineData("HTTP/1.1 099 Low\r\nContent-Length: 2\r\n\r\nok", "out of range")]
    [InlineData("HTTP/1.1 2000 OK\r\nContent-Length: 2\r\n\r\nok", "out of range")]
    [InlineData("HTTP/1.1 2x0 OK\r\nContent-Length: 2\r\n\r\nok", "not three digits")]
    [InlineData("HTTP/1.1 101 Switching Protocols\r\nUpgrade: h2c\r\n\r\n", "switched protocols")]
    [InlineData("HTTP/1.1 200 OK\r\n: no name\r\nContent-Length: 2\r\n\r\nok", "malformed header")]
    [InlineData("HTTP/1.1 200 OK\r\nBad Name: v\r\nContent-Length: 2\r\n\r\nok", "malformed header")]
    [InlineData("HTTP/1.1 200 OK\r\nNoColon\r\nContent-Length: 2\r\n\r\nok", "malformed header")]
    public async Task AFramingRfc9112Refuses_IsRefused(string response, string why)
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => BodyOf(response));
        Assert.Contains(why, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The controls for the refusals above: the same length twice is one length; a 200 with no reason phrase, a header's spaces round its value, a bare LF line end.</summary>
    [Fact]
    public async Task TheFramingsItAccepts_AreRead()
    {
        Assert.Equal("ok", await BodyOf("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nContent-Length: 2\r\n\r\nok"));
        Assert.Equal("ok", await BodyOf("HTTP/1.1 200\r\nContent-Length:   2  \r\n\r\nok"));
        Assert.Equal("ok", await BodyOf("HTTP/1.1 200 OK\nContent-Length: 2\n\nok"));
        Assert.Equal("ok", await BodyOf("HTTP/1.1 200 OK\r\nTransfer-Encoding: Chunked\r\n\r\n2\r\nok\r\n0\r\n\r\n"));
    }

    /// <summary>A connection that closes before any response, or inside the status line, is an error, not an empty answer.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("HTTP/1.1 200 O")]
    [InlineData("HTTP/1.1 200 OK\r\nContent-Length: 2\r\n")]
    public async Task AConnectionClosedBeforeAResponse_IsAnError(string response)
        => await Assert.ThrowsAsync<HttpRequestException>(() => BodyOf(response));

    // ---------------------------------------------------------------- cancellation

    /// <summary>
    /// A server that never answers holds the request only until the caller cancels: the cancellation,
    /// asked for once the transport is waiting on the read, ends the send by closing the connection —
    /// which is how a .NET Framework socket read, deaf to its token, is ended.
    /// </summary>
    [Fact]
    public async Task HostileServer_ThatNeverAnswers_IsEndedByTheCancellation()
    {
        var script = new Script("HTTP/1.1 200 OK\r\n\r\n") { StallAt = 0 };
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource();
        Task send = client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.True(SpinWait.SpinUntil(() => script.Connections == 1 && script.Last.Stalled.IsSet, TimeSpan.FromSeconds(30)), "the read never waited");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.True(script.Last.ClosedWhileStalled, "the stall was not ended by the cancellation's close");
    }

    /// <summary>A peer that takes none of the request — a write that never completes — is ended by the cancellation too.</summary>
    [Fact]
    public async Task HostileServer_ThatTakesNoRequest_IsEndedByTheCancellation()
    {
        var script = new Script("HTTP/1.1 200 OK\r\n\r\n") { StallWrites = true };
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource();
        Task send = client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.True(SpinWait.SpinUntil(() => script.Connections == 1 && script.Last.Stalled.IsSet, TimeSpan.FromSeconds(30)), "the write never waited");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.True(script.Last.ClosedWhileStalled, "the stall was not ended by the cancellation's close");
    }

    /// <summary>A peer that never answers the TLS handshake is ended by the cancellation as well.</summary>
    [Fact]
    public async Task HostileServer_ThatStallsTheHandshake_IsEndedByTheCancellation()
    {
        var script = new Script("") { StallAt = 0 };
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = new CancellationTokenSource();
        Task send = client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://8.8.8.8/"), HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.True(SpinWait.SpinUntil(() => script.Connections == 1 && script.Last.Stalled.IsSet, TimeSpan.FromSeconds(30)), "the handshake never waited");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.True(script.Last.ClosedWhileStalled, "the stall was not ended by the cancellation's close");
        Assert.True(script.Last.Written.Length > 0, "no client hello was written");
    }

    /// <summary>A body trickled and then stalled is ended by the cancellation of the caller's read, as its budget asks.</summary>
    [Fact]
    public async Task HostileServer_ThatStallsInTheBody_IsEndedByTheCancellation()
    {
        const string head = "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n";
        var script = new Script(head + new string('a', 100)) { StallAt = head.Length + 10 };
        using var client = new HttpClient(script.Transport()) { Timeout = Timeout.InfiniteTimeSpan };
        HttpResponseMessage response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url), HttpCompletionOption.ResponseHeadersRead);
        using var cts = new CancellationTokenSource();
        Task<byte[]> read = GuardedHttpFetch.ReadCappedAsync(response.Content, 1000, cts.Token);
        Assert.True(script.Last.Stalled.Wait(TimeSpan.FromSeconds(30)), "the read never waited");
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.True(script.Last.ClosedWhileStalled, "the stall was not ended by the cancellation's close");
    }

    // ---------------------------------------------------------------- TLS

    /// <summary>A loopback TLS server with a self-signed certificate for <c>tls.example</c>, recording the name the client asked for.</summary>
    private sealed class TlsServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        internal string ServerNameIndication;

        internal TlsServer()
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=tls.example", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("tls.example");
            request.CertificateExtensions.Add(san.Build());
            using X509Certificate2 ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task ServeAsync()
        {
            try
            {
                using TcpClient client = await _listener.AcceptTcpClientAsync();
                using var tls = new SslStream(client.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificateSelectionCallback = (_, name) => { ServerNameIndication = name; return _certificate; },
                });
                await ReadHeadAsync(tls);
                byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 6\r\n\r\nsecure");
                await tls.WriteAsync(response, 0, response.Length);
                await tls.FlushAsync();
                tls.Close();
            }
            catch { /* a refused handshake ends here */ }
        }

        public void Dispose()
        {
            _listener.Stop();
            _certificate.Dispose();
        }
    }

    /// <summary>
    /// TLS is authenticated with the URL's host name — the server receives it as its SNI, and the
    /// certificate check is handed the server's certificate for it — whatever address the vetted
    /// connection dialled; the response is read through the TLS stream.
    /// </summary>
    [Fact]
    public async Task TlsIsAuthenticatedWithTheUrlsHostName()
    {
        using var server = new TlsServer();
        string certificateName = null;
        var transport = new GuardedHttpTransport(
            (host, port, ct) => GuardedHttpConnect.ConnectToAnyAsync(new[] { IPAddress.Loopback }, server.Port, ct),
            (_, certificate, _, _) => { certificateName = new X509Certificate2(certificate!).GetNameInfo(X509NameType.DnsName, false); return true; },
            _ => false, null);
        using var client = new HttpClient(transport);

        Assert.Equal("secure", await client.GetStringAsync($"https://tls.example:{server.Port}/"));
        Assert.Equal("tls.example", server.ServerNameIndication);
        Assert.Equal("tls.example", certificateName);
    }

    /// <summary>The platform's rule refuses the self-signed certificate the check above accepted: the handshake fails, as a request failure.</summary>
    [Fact]
    public async Task ASelfSignedCertificate_IsRefusedByThePlatformsRule()
    {
        using var server = new TlsServer();
        var transport = new GuardedHttpTransport(
            (host, port, ct) => GuardedHttpConnect.ConnectToAnyAsync(new[] { IPAddress.Loopback }, server.Port, ct),
            null, _ => false, null);
        using var client = new HttpClient(transport);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStringAsync($"https://tls.example:{server.Port}/"));
        Assert.IsAssignableFrom<AuthenticationException>(ex.InnerException);
    }

    // ---------------------------------------------------------------- the proxy

    /// <summary>A URL a proxy applies to goes to the proxied handler, and the transport dials nothing — the proxy resolves the host; any other is the transport's own.</summary>
    [Fact]
    public async Task AProxiedUrl_GoesToTheProxiedHandler()
    {
        var script = new Script("HTTP/1.1 204 No Content\r\n\r\n");
        var proxied = new CountingHandler();
        using var client = new HttpClient(script.Transport(u => u.AbsolutePath == "/via-proxy", proxied)) { Timeout = Timeout.InfiniteTimeSpan };

        Assert.Equal(HttpStatusCode.Accepted, (await client.GetAsync("http://8.8.8.8/via-proxy")).StatusCode);
        Assert.Equal(1, proxied.Calls);
        Assert.Equal(0, script.Connections);
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync("http://8.8.8.8/direct")).StatusCode);
        Assert.Equal(1, script.Connections);
        Assert.Equal(1, proxied.Calls);
    }

    /// <summary>The system-proxy probe answers no proxy for a URL a proxy is bypassed for — the loopback address, on any configuration.</summary>
    [Fact]
    public void TheSystemProxyIsBypassedForLoopback()
        => Assert.False(GuardedHttpTransport.SystemProxyApplies(new Uri("http://127.0.0.1/")));

    private sealed class CountingHandler : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
        }
    }

    private static string Flatten(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception e = ex; e != null; e = e.InnerException) sb.Append(e.Message).Append(' ');
        return sb.ToString();
    }
}
