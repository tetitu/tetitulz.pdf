// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Net;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Net;

/// <summary>
/// <see cref="GuardedHttpFetch"/>: fetching a URL that untrusted input chose.
///
/// <para><b>No test here touches the network.</b> A scripted <see cref="HttpMessageHandler"/>
/// stands in for it, and every host is an IP literal — the guard classifies a literal without a DNS
/// lookup, so <c>93.184.216.34</c> passes it and <c>169.254.169.254</c> does not, on any machine.</para>
/// </summary>
public class GuardedHttpFetchTests
{
    private const string Public = "http://93.184.216.34";
    private const long Cap = 64 * 1024;

    /// <summary>A handler that answers from a script and records every request it was asked.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _answer;
        public List<Uri> Requests { get; } = new();

        public ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer)
            : this((r, _) => Task.FromResult(answer(r))) { }

        public ScriptedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
            => _answer = answer;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            HttpResponseMessage response = await _answer(request, cancellationToken);
            response.RequestMessage ??= request;
            return response;
        }
    }

    /// <summary>A body of a given size that counts what was read out of it.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly long _length;
        private readonly bool _seekable;
        public long BytesRead { get; private set; }

        public CountingStream(long length, bool seekable) { _length = length; _seekable = seekable; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = (int)Math.Min(count, _length - BytesRead);
            if (n <= 0) return 0;
            Array.Clear(buffer, offset, n);
            BytesRead += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => _seekable;
        public override bool CanWrite => false;
        public override long Length => _seekable ? _length : throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage Redirect(string to, HttpStatusCode code = HttpStatusCode.Found)
    {
        var r = new HttpResponseMessage(code);
        r.Headers.Location = new Uri(to, UriKind.RelativeOrAbsolute);
        return r;
    }

    private static byte[] Get(ScriptedHandler handler, string url, out string failure,
                              long maxBytes = Cap, int maxRedirects = GuardedHttpFetch.DefaultMaxRedirects,
                              TimeSpan? timeout = null)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        return GuardedHttpFetch.Get(client, new Uri(url), maxBytes, maxRedirects,
                                    timeout ?? TimeSpan.FromSeconds(30), out failure);
    }

    // ------------------------------------------------------------------ the guard, before sending

    [Theory]
    [InlineData("http://127.0.0.1/a.png")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]   // cloud instance metadata
    [InlineData("http://10.0.0.7/a.png")]
    [InlineData("http://[::1]/a.png")]
    public void APrivateOrReservedHost_IsRefusedWithoutASingleRequest(string url)
    {
        var handler = new ScriptedHandler(_ => Ok(new byte[] { 1 }));

        byte[] body = Get(handler, url, out string failure);

        Assert.Null(body);
        Assert.Empty(handler.Requests);
        Assert.Contains("public host", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("file:///c:/windows/win.ini")]
    [InlineData("ftp://93.184.216.34/a.png")]
    public void ANonHttpScheme_IsRefusedWithoutASingleRequest(string url)
    {
        var handler = new ScriptedHandler(_ => Ok(new byte[] { 1 }));

        Assert.Null(Get(handler, url, out string failure));
        Assert.Empty(handler.Requests);
        Assert.NotNull(failure);
    }

    /// <summary>The acceptance control beside the refusals: a public literal is fetched.</summary>
    [Fact]
    public void APublicHost_IsFetched()
    {
        var handler = new ScriptedHandler(_ => Ok(new byte[] { 7, 8, 9 }));

        byte[] body = Get(handler, Public + "/a.png", out string failure);

        Assert.Equal(new byte[] { 7, 8, 9 }, body);
        Assert.Null(failure);
        Assert.Single(handler.Requests);
    }

    // ------------------------------------------------------------------ redirects

    [Fact]
    public void ARedirectToTheMetadataService_IsRefused_AndNeverRequested()
    {
        var handler = new ScriptedHandler(_ => Redirect("http://169.254.169.254/latest/?token=SECRET"));

        Assert.Null(Get(handler, Public + "/a.png", out string failure));

        Assert.Single(handler.Requests);   // the original, and nothing after it
        Assert.Contains("Refused redirect", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", failure, StringComparison.Ordinal);
    }

    [Fact]
    public void ARelativeRedirect_ResolvesAgainstTheRequest_AndIsFollowed()
    {
        var handler = new ScriptedHandler(r =>
            r.RequestUri!.AbsolutePath == "/a.png" ? Redirect("/assets/b.png") : Ok(new byte[] { 42 }));

        byte[] body = Get(handler, Public + "/a.png", out _);

        Assert.Equal(new byte[] { 42 }, body);
        Assert.Equal(new Uri(Public + "/assets/b.png"), handler.Requests[1]);
    }

    [Fact]
    public void ARedirectLoop_StopsAtTheLimit()
    {
        var handler = new ScriptedHandler(_ => Redirect(Public + "/again"));

        Assert.Null(Get(handler, Public + "/a.png", out string failure, maxRedirects: 2));

        Assert.Equal(3, handler.Requests.Count);   // the original and two hops
        Assert.Contains("Exceeded 2 redirects", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relative <c>Location</c> with no request to resolve it against has no target: it is a
    /// refusal, not a <c>new Uri(relative, relative)</c> throwing out of the redirect walk.
    /// </summary>
    [Fact]
    public void ARelativeRedirectWithNoRequestUri_IsARefusalNotAnException()
    {
        var orphan = Redirect("/elsewhere");

        HttpResponseMessage result = GuardedHttpFetch.FollowRedirects(
            orphan, _ => throw new InvalidOperationException("must not be requested"),
            GuardedHttpFetch.DefaultMaxRedirects, out string refusal);

        Assert.Null(result);
        Assert.Contains("Relative redirect", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void AnErrorStatus_IsReportedByCode()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Null(Get(handler, Public + "/a.png", out string failure));
        Assert.Contains("404", failure, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the size ceiling

    /// <summary>
    /// <b>The ceiling bounds what is read, not only what is kept.</b> The body has no declared
    /// length, so only the streaming check can stop it — and it must stop within one chunk of the
    /// ceiling. Sent with the default <c>ResponseContentRead</c>, <see cref="HttpClient"/> would
    /// have read the whole megabyte into its own buffer before the check ever ran, which is what
    /// the revocation fetches did.
    /// </summary>
    [Fact]
    public void AnUndeclaredOversizedBody_StopsBeingReadNearTheCeiling()
    {
        const long Total = 4 * 1024 * 1024;
        var body = new CountingStream(Total, seekable: false);
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });

        Assert.Null(Get(handler, Public + "/big", out string failure));

        Assert.Contains("larger than", failure, StringComparison.Ordinal);
        Assert.True(body.BytesRead <= Cap + 128 * 1024,
            $"read {body.BytesRead} bytes of a {Total}-byte body against a {Cap}-byte ceiling");
    }

    [Fact]
    public void ADeclaredOversizedBody_IsRefusedWithoutReadingIt()
    {
        var body = new CountingStream(Cap + 1, seekable: true);
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body),
        });

        Assert.Null(Get(handler, Public + "/big", out string failure));

        Assert.Contains("larger than", failure, StringComparison.Ordinal);
        Assert.Equal(0, body.BytesRead);
    }

    /// <summary>Exactly at the ceiling is accepted — the check is "more than", not "at least".</summary>
    [Fact]
    public void ABodyExactlyAtTheCeiling_IsAccepted()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new CountingStream(Cap, seekable: false)),
        });

        byte[] body = Get(handler, Public + "/exact", out string failure);

        Assert.NotNull(body);
        Assert.Equal(Cap, body.Length);
        Assert.Null(failure);
    }

    // ------------------------------------------------------------------ the time budget

    [Fact]
    public void AServerThatNeverAnswers_IsAbandonedAtTheBudget()
    {
        var handler = new ScriptedHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.Null(Get(handler, Public + "/slow", out string failure, timeout: TimeSpan.FromMilliseconds(200)));
        Assert.Contains("did not finish within", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>The body is inside the budget too.</b> A headers-only send returns as soon as the
    /// headers arrive, so the body read is the one step the send's cancellation no longer covers.
    /// A server that answers at once and then trickles nothing must still be abandoned — which
    /// needs the budget's token on the read.
    /// </summary>
    /// <remarks>
    /// <b>The evidence is the token, not the clock.</b> This used to wait on a thread-pool task for
    /// 30 seconds, and it failed a whole-solution run while passing alone in half a second.
    /// <c>Get</c> is synchronous over asynchronous by design, so under a starved pool its
    /// continuation queues behind every other test's. Now the stalling stream refuses at once a read
    /// handed no cancellable token. With the budget's token removed, the fetch fails fast with the
    /// wrong reason instead of hanging. The generous join is left only for a token that can be
    /// cancelled but never is, on a dedicated thread (CLAUDE.md's second remedy).
    /// </remarks>
    [Fact]
    public void ABodyThatNeverArrives_IsAbandonedAtTheBudget()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StallingStream()),
        });
        string failure = null;
        byte[] body = Array.Empty<byte>();

        var fetch = new Thread(() => body = Get(handler, Public + "/trickle", out failure,
                                                timeout: TimeSpan.FromMilliseconds(200)))
        { IsBackground = true };
        fetch.Start();

        Assert.True(fetch.Join(TimeSpan.FromMinutes(2)), "the body read ignored the fetch's budget");
        Assert.Null(body);
        Assert.Contains("did not finish within", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A stream whose reads wait until cancelled — and refuse outright a token that never can be,
    /// since that read could only hang.
    /// </summary>
    private sealed class StallingStream : Stream
    {
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            RequireCancellable(ct);
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            RequireCancellable(ct);
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }

        private static void RequireCancellable(CancellationToken ct)
        {
            if (!ct.CanBeCanceled)
                throw new IOException("the body read was handed no cancellation token");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ the handler

    /// <summary>
    /// The two properties every client reaching an untrusted URL relies on: redirects are never
    /// taken automatically, and the connection dials the address the guard vetted.
    /// </summary>
    /// <remarks>The callback is a closure over the handler's proxy, so the guard is shown by what it
    /// does — a loopback literal refused by name, before any socket — not by the method it names.</remarks>
    [Fact]
    public async Task TheHandler_TakesNoRedirect_AndConnectsThroughTheGuard()
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(GuardedHttpFetch.CreateHandler());

        Assert.False(handler.AllowAutoRedirect);
        Assert.NotNull(handler.ConnectCallback);
        using var client = new HttpClient(handler, disposeHandler: false);
        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://127.0.0.1:9/"));
        string messages = refused.Message + " " + refused.InnerException?.Message;
        Assert.Contains("Refusing to connect to '127.0.0.1'", messages);
    }

    [Fact]
    public void TheHandler_CarriesTheCallersCredentialsProxyAndPreAuthentication()
    {
        var credentials = new NetworkCredential("user", "pass");
        var proxy = new WebProxy("http://93.184.216.34:3128");

        using var handler = Assert.IsType<SocketsHttpHandler>(
            GuardedHttpFetch.CreateHandler(credentials, proxy, preAuthenticate: true));

        Assert.Same(credentials, handler.Credentials);
        Assert.Same(proxy, handler.Proxy);
        Assert.True(handler.PreAuthenticate);
        Assert.False(handler.AllowAutoRedirect);
    }
}
