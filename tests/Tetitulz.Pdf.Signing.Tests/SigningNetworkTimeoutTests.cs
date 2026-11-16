// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Threading;
using Tetitulz.Pdf.Signing.Models;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// One reading of <see cref="SigningNetworkOptions.Timeout"/> for both transports — the TSA's
/// <c>HttpWebRequest</c> and the revocation fetches' <c>HttpClient</c> — and the budget every
/// OCSP and CRL fetch runs under.
/// </summary>
/// <remarks>
/// <para>The option had three readings. <c>TsaClient</c> read a non-positive value as "do not time
/// out"; <c>DssBuilder.ClientFor</c> read it as the built-in 15 s; and the CRL and OCSP fetches
/// capped whatever the caller set at 15 s through a fixed cancellation, so a 60 s timeout against
/// a slow responder still failed at 15 s while the same value reached the TSA intact. An overlong
/// value was clamped for the TSA and thrown out by <c>HttpClient</c>. A network policy that means
/// something different per transport is not a policy.</para>
///
/// <para>No network is touched: the tests assert the client, the request and the cancellation
/// source, which is where the policy either landed or did not.</para>
/// </remarks>
public class SigningNetworkTimeoutTests
{
    private static readonly Uri Tsa = new("https://tsa.example.test/tsr");

    private static int TsaTimeout(SigningNetworkOptions? network)
        => TsaClient.BuildRequest(Tsa, 42, "a", "b", network).Timeout;

    /// <summary>
    /// The built-ins differ on purpose — revocation data is best-effort LTV material, a TSA round
    /// trip is the signature — and a null or empty policy keeps each transport's own.
    /// </summary>
    [Fact]
    public void NullKeepsEachTransportsOwnBuiltInTimeout()
    {
        TimeSpan revocation = DssBuilder.ClientFor(null).Timeout;
        int tsa = TsaTimeout(null);

        Assert.Equal(revocation, DssBuilder.ClientFor(new SigningNetworkOptions()).Timeout);
        Assert.Equal(tsa, TsaTimeout(new SigningNetworkOptions()));
        Assert.True(tsa > revocation.TotalMilliseconds,
            "the TSA budget is documented as the longer of the two");
    }

    [Fact]
    public void ANonPositiveTimeoutDisablesTheTimeoutOnBothTransports()
    {
        var zero = new SigningNetworkOptions { Timeout = TimeSpan.Zero };
        var negative = new SigningNetworkOptions { Timeout = TimeSpan.FromSeconds(-5) };

        Assert.Equal(Timeout.InfiniteTimeSpan, DssBuilder.ClientFor(zero).Timeout);
        Assert.Equal(Timeout.InfiniteTimeSpan, DssBuilder.ClientFor(negative).Timeout);
        Assert.Equal(Timeout.Infinite, TsaTimeout(zero));
        Assert.Equal(Timeout.Infinite, TsaTimeout(negative));
    }

    /// <summary>
    /// <c>HttpClient.Timeout</c> throws above <c>int.MaxValue</c> milliseconds and
    /// <c>HttpWebRequest.Timeout</c> is an <c>int</c>; a caller's absurd value is clamped on both
    /// rather than refused on one.
    /// </summary>
    [Fact]
    public void AnOverlongTimeoutIsClampedOnBothTransportsRatherThanRefused()
    {
        var overlong = new SigningNetworkOptions { Timeout = TimeSpan.FromDays(365) };

        Assert.Equal(TimeSpan.FromMilliseconds(int.MaxValue), DssBuilder.ClientFor(overlong).Timeout);
        Assert.Equal(int.MaxValue, TsaTimeout(overlong));
    }

    /// <summary>
    /// The per-URL budget of an OCSP or CRL fetch is the configured timeout — not the built-in
    /// figure the fixed cancellation used to apply over the top of it.
    /// </summary>
    [Fact]
    public void TheRevocationBudgetIsTheConfiguredTimeoutNotTheBuiltInCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(60),
            DssBuilder.RevocationBudget(new SigningNetworkOptions { Timeout = TimeSpan.FromSeconds(60) }));
        Assert.Equal(DssBuilder.ClientFor(null).Timeout, DssBuilder.RevocationBudget(null));
        Assert.Equal(Timeout.InfiniteTimeSpan,
            DssBuilder.RevocationBudget(new SigningNetworkOptions { Timeout = TimeSpan.Zero }));
    }

    /// <summary>
    /// The budget is what cancels a fetch: one source for a whole redirect chain, cancelled at the
    /// configured timeout — and not before it, and never when the caller disabled it.
    /// </summary>
    /// <remarks>
    /// The budget's length is <see cref="TheRevocationBudgetIsTheConfiguredTimeoutNotTheBuiltInCap"/>'s
    /// evidence; here the brief budget need only fire. Its timer runs on the thread pool, which a
    /// whole-solution run on a loaded host can starve for seconds, so the wait allows two minutes —
    /// it returns as soon as the token is cancelled. The two negative windows cannot fail by load: a
    /// busy host delays a cancellation, never advances it.
    /// </remarks>
    [Fact]
    public void AFetchIsCancelledAtTheConfiguredBudgetAndNotBefore()
    {
        var brief = new SigningNetworkOptions { Timeout = TimeSpan.FromMilliseconds(100) };
        using (var budget = DssBuilder.RevocationBudgetSource(brief, CancellationToken.None))
        {
            Assert.True(budget.Token.WaitHandle.WaitOne(TimeSpan.FromMinutes(2)),
                "a 100 ms budget did not cancel the fetch within two minutes");
        }

        var generous = new SigningNetworkOptions { Timeout = TimeSpan.FromHours(1) };
        using (var budget = DssBuilder.RevocationBudgetSource(generous, CancellationToken.None))
        {
            Assert.False(budget.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)),
                "an hour-long budget cancelled the fetch within 200 ms");
        }

        var none = new SigningNetworkOptions { Timeout = TimeSpan.Zero };
        using (var budget = DssBuilder.RevocationBudgetSource(none, CancellationToken.None))
        {
            Assert.False(budget.Token.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200)),
                "a disabled timeout cancelled the fetch");
        }
    }

    /// <summary>The caller's own token is linked in, so it still cuts a fetch short.</summary>
    [Fact]
    public void TheCallersOwnCancellationStillCutsAFetchShort()
    {
        using var caller = new CancellationTokenSource();
        using var budget = DssBuilder.RevocationBudgetSource(null, caller.Token);

        Assert.False(budget.Token.IsCancellationRequested);
        caller.Cancel();
        Assert.True(budget.Token.IsCancellationRequested);
    }
}
