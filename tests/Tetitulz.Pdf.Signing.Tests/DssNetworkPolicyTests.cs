// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Net;
using System.Net.Http;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The client <c>DssBuilder</c> fetches OCSP and CRLs through, once a caller supplies credentials
/// or a proxy.
/// </summary>
/// <remarks>
/// <para><b>The property that matters most here is not the credential; it is
/// <c>AllowAutoRedirect</c>.</b> Redirects are followed by hand so every hop can be re-checked
/// against the private-address guard. A policy-built handler that followed them itself would take
/// the first hop unguarded — turning a caller's proxy setting into an SSRF bypass, which is the one
/// way this convenience could do real harm.</para>
///
/// <para>No network is touched: these assert the client and its handler, which is where the policy
/// either landed or did not.</para>
/// </remarks>
public class DssNetworkPolicyTests
{
    [Fact]
    public void NoPolicyReturnsTheSharedClient()
    {
        Assert.Same(DssBuilder.ClientFor(null), DssBuilder.ClientFor(null));
    }

    /// <summary>
    /// One client per options instance — not one per request, which is the socket-exhaustion
    /// pattern the shared instance exists to avoid.
    /// </summary>
    [Fact]
    public void OnePolicyGetsOneClientAndTwoPoliciesGetTwo()
    {
        var a = new SigningNetworkOptions { Credentials = new NetworkCredential("u", "p") };
        var b = new SigningNetworkOptions { Credentials = new NetworkCredential("u", "p") };

        Assert.Same(DssBuilder.ClientFor(a), DssBuilder.ClientFor(a));
        Assert.NotSame(DssBuilder.ClientFor(a), DssBuilder.ClientFor(b));
        Assert.NotSame(DssBuilder.ClientFor(a), DssBuilder.ClientFor(null));
    }

    /// <summary>
    /// <b>Redirects stay hand-followed whatever the policy asks for.</b> The guard walks each hop;
    /// a handler that redirected itself would take the first one unvetted.
    /// </summary>
    [Fact]
    public void APolicyClientNeverFollowsRedirectsItself()
    {
        var options = new SigningNetworkOptions
        {
            Credentials = new NetworkCredential("u", "p"),
            Proxy = new WebProxy("http://proxy.example.test:8080"),
            PreAuthenticate = true,
            Timeout = TimeSpan.FromSeconds(3),
        };

        Assert.False(HandlerOf(DssBuilder.ClientFor(options)).AllowAutoRedirect);
        Assert.False(HandlerOf(DssBuilder.ClientFor(null)).AllowAutoRedirect);
    }

    /// <summary>
    /// <b>A plain credential does not ride a revocation fetch.</b> An OCSP, CRL or AIA URL comes out
    /// of an untrusted certificate extension, so a <see cref="NetworkCredential"/> — which answers any
    /// host's challenge, Basic in the clear — would be sent to whatever host the attacker named. It is
    /// dropped from the revocation handler; the proxy still reaches it, which is both the potency
    /// control (the policy was applied, not ignored) and the reason the credential's absence is a
    /// deliberate filter rather than an empty policy. The TSA keeps the caller's credential, which
    /// <c>TsaNetworkPolicyTests.CredentialsAreAppliedWhenSuppliedAndAbsentOtherwise</c> is the
    /// acceptance control for.
    /// </summary>
    [Fact]
    public void APlainCredentialIsNotSentToRevocationHostsButTheProxyIs()
    {
        var creds = new NetworkCredential("ocsp-user", "ocsp-pass");
        var proxy = new WebProxy("http://proxy.example.test:8080");

        SocketsHttpHandler h = HandlerOf(DssBuilder.ClientFor(
            new SigningNetworkOptions { Credentials = creds, Proxy = proxy }));

        Assert.Null(h.Credentials);
        Assert.Same(proxy, h.Proxy);
        Assert.True(h.UseProxy, "a supplied proxy that is not enabled is not used at all");
    }

    /// <summary>
    /// The acceptance control: a <see cref="CredentialCache"/> <i>does</i> reach the revocation
    /// handler, because it binds each credential to a specific host, scheme and authentication type,
    /// so it cannot answer a challenge from the host an attacker's certificate named. This is the one
    /// credential shape a caller can give that both authenticates a private revocation responder and
    /// stays safe.
    /// </summary>
    [Fact]
    public void AHostScopedCredentialCacheReachesTheHandler()
    {
        var cache = new CredentialCache
        {
            { new Uri("http://ocsp.internal.test"), "Basic", new NetworkCredential("ocsp-user", "ocsp-pass") },
        };

        SocketsHttpHandler h = HandlerOf(DssBuilder.ClientFor(
            new SigningNetworkOptions { Credentials = cache }));

        Assert.Same(cache, h.Credentials);
    }

    /// <summary><c>CredentialCache.DefaultNetworkCredentials</c> is not a <see cref="CredentialCache"/>, so it is dropped too — it would send the process identity to the attacker's host.</summary>
    [Fact]
    public void DefaultNetworkCredentialsAreNotSentToRevocationHosts()
    {
        SocketsHttpHandler h = HandlerOf(DssBuilder.ClientFor(
            new SigningNetworkOptions { Credentials = CredentialCache.DefaultNetworkCredentials }));

        Assert.Null(h.Credentials);
    }

    /// <summary>The control: an empty policy leaves the handler's defaults alone.</summary>
    [Fact]
    public void AnEmptyPolicyAddsNothing()
    {
        SocketsHttpHandler h = HandlerOf(DssBuilder.ClientFor(new SigningNetworkOptions()));

        Assert.Null(h.Credentials);
        Assert.False(h.PreAuthenticate);
    }

    /// <summary>
    /// What a non-positive or overlong value means is one reading for both transports, in
    /// <see cref="SigningNetworkTimeoutTests"/>.
    /// </summary>
    [Fact]
    public void ATimeoutReplacesTheBuiltInOne()
    {
        TimeSpan builtIn = DssBuilder.ClientFor(null).Timeout;
        TimeSpan configured =
            DssBuilder.ClientFor(new SigningNetworkOptions { Timeout = TimeSpan.FromSeconds(11) }).Timeout;

        Assert.Equal(TimeSpan.FromSeconds(11), configured);
        Assert.NotEqual(builtIn, configured);
    }

    /// <summary>
    /// The handler behind a client. <b>It is a <c>SocketsHttpHandler</c> on this leg and an
    /// <c>HttpClientHandler</c> on <c>netstandard2.0</c></b>, because only the former has the
    /// <c>ConnectCallback</c> that pins the connection to the address the guard checked. These
    /// tests run on <c>net10.0</c>, so they assert the one they can see.
    /// </summary>
    /// <summary>
    /// <b>Every client resolves and connects through the guard</b>, so the address that was
    /// classified is the address the socket goes to.
    /// </summary>
    /// <remarks>
    /// Without the callback the handler resolves the hostname itself, a second time, after
    /// <c>PrivateAddressGuard</c> has already resolved it and thrown the answer away — and an OCSP
    /// or CRL host comes from an untrusted certificate extension, so that second answer is the
    /// attacker's to choose. Asserted on both the policy client and the shared one, because a
    /// callback wired into only the policy path would leave the overwhelmingly common route
    /// unpinned.
    /// </remarks>
    [Fact]
    public void EveryClientConnectsThroughTheAddressGuard()
    {
        Assert.NotNull(HandlerOf(DssBuilder.ClientFor(null)).ConnectCallback);
        Assert.NotNull(HandlerOf(DssBuilder.ClientFor(
            new SigningNetworkOptions { Proxy = new WebProxy("http://proxy.example.test:8080") }))
            .ConnectCallback);
    }

    private static SocketsHttpHandler HandlerOf(HttpClient client)
    {
        var field = typeof(HttpMessageInvoker).GetField(
            "_handler", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.True(field != null, "HttpMessageInvoker no longer stores its handler in _handler");

        var handler = field!.GetValue(client) as SocketsHttpHandler;
        Assert.True(handler != null, "the client was not built on a SocketsHttpHandler");
        return handler!;
    }
}
