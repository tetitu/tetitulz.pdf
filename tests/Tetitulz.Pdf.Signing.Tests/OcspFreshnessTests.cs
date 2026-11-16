// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Signing.Timestamping;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// An OCSP response may only be embedded in the <c>/DSS</c> as <b>current</b> revocation evidence
/// (RFC 6960 §3.1.2, §4.2.2.1).
/// </summary>
/// <remarks>
/// <para><b>A response with no <c>nextUpdate</c> was accepted at any age.</b> §4.2.2.1 makes
/// <c>nextUpdate</c> optional and reads its absence as "newer revocation information is available
/// all the time" — a statement about the responder, not a licence to treat one old response as
/// current forever. The two checks that existed each looked right alone and left a hole between
/// them: the stale arm was guarded on <c>NextUpdate.HasValue</c>, and the other only caught a
/// <c>thisUpdate</c> in the FUTURE. So a good, issuer-signed response whose <c>thisUpdate</c> was
/// two years old went into the DSS with <c>LastOcspFreshnessIssue</c> left null.</para>
///
/// <para><b>The consequence is the one LTV exists to prevent</b>: a revocation occurring in the
/// interval is invisible, and the document asserts the certificate was good.</para>
///
/// <para><b>The rule is tested rather than the fetch.</b> It lived inline inside the network
/// fetch, so nothing could reach it without a live responder — which is exactly why the hole
/// survived. <c>now</c> is a parameter so the rule is checkable without a clock.</para>
/// </remarks>
public class OcspFreshnessTests
{
    private static readonly DateTime Now = new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The defect: no <c>nextUpdate</c>, and a <c>thisUpdate</c> long past.</summary>
    [Theory]
    [InlineData(730)]   // two years, the register's example
    [InlineData(30)]
    [InlineData(8)]     // just past the bound
    public void AResponseWithNoNextUpdateAndAnOldThisUpdateIsRefused(int daysOld)
    {
        bool current = DssBuilder.IsOcspResponseCurrent(
            Now.AddDays(-daysOld), null, Now, out string issue);

        Assert.False(current);
        Assert.Contains("no nextUpdate", issue);
    }

    /// <summary>
    /// The floor: a recent response with no <c>nextUpdate</c> is still perfectly good. Refusing
    /// every absent <c>nextUpdate</c> would break responders that legitimately omit it.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void ARecentResponseWithNoNextUpdateIsAccepted(int daysOld)
    {
        Assert.True(DssBuilder.IsOcspResponseCurrent(
            Now.AddDays(-daysOld), null, Now, out string issue));
        Assert.Null(issue);
    }

    /// <summary>An elapsed <c>nextUpdate</c> is stale — the check that already worked.</summary>
    [Fact]
    public void AnElapsedNextUpdateIsRefused()
    {
        Assert.False(DssBuilder.IsOcspResponseCurrent(
            Now.AddDays(-10), Now.AddDays(-3), Now, out string issue));
        Assert.Contains("stale", issue);
    }

    /// <summary>
    /// A live <c>nextUpdate</c> keeps a response current <b>however old its thisUpdate is</b> —
    /// the responder has spoken for the interval, which is what nextUpdate means.
    /// </summary>
    /// <remarks>
    /// The control that stops the new bound being applied to the wrong case. Without it, "refuse
    /// anything older than seven days" satisfies the defect cases above and starts rejecting
    /// long-validity responses that are explicitly still in force.
    /// </remarks>
    [Fact]
    public void AnOldThisUpdateWithALiveNextUpdateIsAccepted()
    {
        Assert.True(DssBuilder.IsOcspResponseCurrent(
            Now.AddDays(-400), Now.AddDays(+1), Now, out string issue));
        Assert.Null(issue);
    }

    /// <summary>A <c>thisUpdate</c> in the future is skew or forgery.</summary>
    [Fact]
    public void AFutureThisUpdateIsRefused()
    {
        Assert.False(DssBuilder.IsOcspResponseCurrent(
            Now.AddHours(1), Now.AddDays(1), Now, out string issue));
        Assert.Contains("future", issue);
    }

    /// <summary>Both comparisons allow a few minutes of clock skew.</summary>
    /// <remarks>
    /// Without this, a correct response from a responder whose clock is seconds ahead is refused —
    /// a guard that fires on good input is one a caller learns to disable.
    /// </remarks>
    [Fact]
    public void SmallClockSkewIsTolerated()
    {
        Assert.True(DssBuilder.IsOcspResponseCurrent(
            Now.AddMinutes(2), Now.AddDays(1), Now, out _));
        Assert.True(DssBuilder.IsOcspResponseCurrent(
            Now.AddDays(-1), Now.AddMinutes(-2), Now, out _));
    }
}
