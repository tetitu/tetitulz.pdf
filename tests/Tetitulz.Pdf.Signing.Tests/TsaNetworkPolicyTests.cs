// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Net;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// The credentials, proxy and timeout a commercial TSA behind an enterprise proxy needs, asserted
/// on the request object rather than by reaching a TSA.
/// </summary>
/// <remarks>
/// <para><b>Everything past request construction connects</b>, so "are the credentials applied" is
/// only answerable without a network if the construction is its own step. It is
/// (<c>TsaClient.BuildRequest</c>), and these assert what it produced. A test that needed the
/// internet to check this would be deleted the first time a build ran offline.</para>
///
/// <para>Each case asserts the <b>default</b> beside the configured value: a policy that was
/// applied unconditionally, or never, would satisfy half of these on its own.</para>
/// </remarks>
public class TsaNetworkPolicyTests
{
    private static readonly Uri Tsa = new Uri("https://tsa.example.test/rfc3161");

    private static HttpWebRequest Build(SigningNetworkOptions options) =>
        TsaClient.BuildRequest(Tsa, contentLength: 42,
                               contentType: "application/timestamp-query",
                               acceptType: "application/timestamp-reply",
                               network: options);

    [Fact]
    public void CredentialsAreAppliedWhenSuppliedAndAbsentOtherwise()
    {
        var creds = new NetworkCredential("tsa-user", "tsa-pass");

        Assert.Same(creds, Build(new SigningNetworkOptions { Credentials = creds }).Credentials);
        Assert.Null(Build(new SigningNetworkOptions()).Credentials);
        Assert.Null(TsaClient.BuildRequest(Tsa, 42, "a", "b", null).Credentials);
    }

    [Fact]
    public void AProxyIsAppliedWhenSupplied()
    {
        var proxy = new WebProxy("http://proxy.example.test:8080");

        Assert.Same(proxy, Build(new SigningNetworkOptions { Proxy = proxy }).Proxy);
    }

    /// <summary>
    /// Pre-authentication is opt-in, because it sends the credential before being challenged.
    /// </summary>
    [Fact]
    public void PreAuthenticateIsOffUnlessAskedFor()
    {
        Assert.False(Build(new SigningNetworkOptions { Credentials = new NetworkCredential("u", "p") })
                        .PreAuthenticate);
        Assert.True(Build(new SigningNetworkOptions { PreAuthenticate = true }).PreAuthenticate);
    }

    [Fact]
    public void ATimeoutReplacesTheBuiltInOne()
    {
        int builtIn = TsaClient.BuildRequest(Tsa, 42, "a", "b", null).Timeout;
        int configured = Build(new SigningNetworkOptions { Timeout = TimeSpan.FromSeconds(90) }).Timeout;

        Assert.Equal(90_000, configured);
        Assert.NotEqual(builtIn, configured);
    }

    /// <summary>
    /// A zero or negative timeout means "do not time out", which <c>HttpWebRequest</c> spells
    /// <c>Timeout.Infinite</c> rather than <c>0</c> — where 0 would mean "time out immediately".
    /// </summary>
    [Fact]
    public void ANonPositiveTimeoutMeansInfiniteRatherThanImmediate()
    {
        Assert.Equal(System.Threading.Timeout.Infinite,
                     Build(new SigningNetworkOptions { Timeout = TimeSpan.Zero }).Timeout);
    }

    /// <summary>An absurd timeout is clamped rather than overflowing into a negative one.</summary>
    [Fact]
    public void AnOverlongTimeoutIsClampedNotOverflowed()
    {
        int t = Build(new SigningNetworkOptions { Timeout = TimeSpan.FromDays(365) }).Timeout;

        Assert.Equal(int.MaxValue, t);
        Assert.True(t > 0, "an overflowed clamp would be negative, which HttpWebRequest rejects");
    }

    /// <summary>
    /// <b>The two security properties are not part of the policy.</b> Redirects stay refused
    /// whatever the caller asks for: a caller-configured TSA URL is trusted enough to name a
    /// private host, and that trust does not transfer to whatever a 3xx points at.
    /// </summary>
    [Fact]
    public void RedirectsStayRefusedWhateverThePolicySays()
    {
        Assert.False(Build(new SigningNetworkOptions
        {
            Credentials = new NetworkCredential("u", "p"),
            Proxy = new WebProxy("http://proxy.example.test:8080"),
            PreAuthenticate = true,
        }).AllowAutoRedirect);

        Assert.False(TsaClient.BuildRequest(Tsa, 42, "a", "b", null).AllowAutoRedirect);
    }

    /// <summary>The request is still the RFC 3161 POST it was, policy or not.</summary>
    [Fact]
    public void ThePolicyDoesNotDisturbTheRequestItself()
    {
        HttpWebRequest r = Build(new SigningNetworkOptions
        {
            Credentials = new NetworkCredential("u", "p"),
            Timeout = TimeSpan.FromSeconds(5),
        });

        Assert.Equal("POST", r.Method);
        Assert.Equal("application/timestamp-query", r.ContentType);
        Assert.Equal("application/timestamp-reply", r.Accept);
        Assert.Equal(42, r.ContentLength);
    }
}
