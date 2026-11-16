// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Signing.Cms;
using Tetitulz.Pdf.Signing.Models;
using Xunit;

namespace Tetitulz.Pdf.Signing.Tests;

/// <summary>
/// <c>Cms.RevocationFreshness.WhyNotCurrent</c>, the one currency rule the verifier and the LTV
/// embedder both ask (GAPS.md §1, closed 2026-10-20). The two callers' own tests reach it through
/// their limits; these pin the rule's four arms, its boundaries and its handling of extreme limits.
/// </summary>
public class RevocationFreshnessRuleTests
{
    private static readonly DateTime At = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan Cap = TimeSpan.FromDays(365);

    private static string Why(DateTime thisUpdate, DateTime? nextUpdate, DateTime? producedAt = null,
                              TimeSpan? skew = null, TimeSpan? cap = null)
        => RevocationFreshness.WhyNotCurrent(thisUpdate, nextUpdate, producedAt, At, skew ?? Skew, cap ?? Cap);

    [Fact]
    public void CurrentDataHasNoReason()
        => Assert.Null(Why(At.AddHours(-1), At.AddDays(1), At.AddHours(-1)));

    [Fact]
    public void AThisUpdateInTheFutureIsRefused_AndTheSkewForgivesAFewMinutes()
    {
        Assert.Contains("future", Why(At.AddMinutes(6), At.AddDays(1)), StringComparison.Ordinal);
        Assert.Null(Why(At.AddMinutes(4), At.AddDays(1)));
    }

    [Fact]
    public void AnElapsedNextUpdateIsStale_AndTheSkewForgivesAFewMinutes()
    {
        string why = Why(At.AddDays(-2), At.AddMinutes(-6));
        Assert.Contains("stale", why, StringComparison.Ordinal);
        Assert.Contains("expired", why, StringComparison.Ordinal);
        Assert.Null(Why(At.AddDays(-2), At.AddMinutes(-4)));
    }

    /// <summary>The cap applies only where <c>nextUpdate</c> is absent: a live one keeps old data current.</summary>
    [Fact]
    public void TheAgeCapAppliesOnlyWithoutNextUpdate()
    {
        Assert.Contains("no nextUpdate", Why(At.AddDays(-366), null), StringComparison.Ordinal);
        Assert.Null(Why(At.AddDays(-364), null));
        Assert.Null(Why(At.AddDays(-400), At.AddDays(1)));
    }

    /// <summary>A cap of zero is "off", as <c>MaxRevocationDataAge</c> documents, not "everything is stale".</summary>
    [Fact]
    public void ACapOfZeroIsTheCapSwitchedOff()
    {
        Assert.Null(Why(At.AddDays(-4000), null, cap: TimeSpan.Zero));
        Assert.NotNull(Why(At.AddDays(-4000), null, cap: Cap));
    }

    /// <summary>
    /// Extreme limits a caller may set on <see cref="SignatureVerificationOptions"/>: the comparisons
    /// are differences, so <see cref="TimeSpan.MaxValue"/> caps nothing instead of throwing from
    /// <c>DateTime.Subtract</c>, which the verifier's own copy of the rule did.
    /// </summary>
    [Fact]
    public void AMaximalCapOrSkewCapsNothing_HostileOptionValues()
    {
        Assert.Null(Why(DateTime.MinValue, null, cap: TimeSpan.MaxValue));
        Assert.Null(Why(At.AddYears(100), DateTime.MinValue, DateTime.MaxValue, skew: TimeSpan.MaxValue));
        // Potency: the same data under ordinary limits is refused.
        Assert.NotNull(Why(DateTime.MinValue, null));
    }

    [Fact]
    public void AnOcspResponseProducedInTheFutureIsRefused_AndACrlHasNoProducedAt()
    {
        Assert.Contains("produced in the future", Why(At.AddHours(-1), At.AddDays(1), At.AddHours(1)), StringComparison.Ordinal);
        Assert.Null(Why(At.AddHours(-1), At.AddDays(1), At.AddMinutes(4)));
        Assert.Null(Why(At.AddHours(-1), At.AddDays(1), producedAt: null));
    }

    /// <summary>
    /// The verifier's defaults are the rule's: an embedder reading
    /// <see cref="RevocationFreshness.DefaultMaxAgeWithoutNextUpdate"/> and a verifier with fresh
    /// options apply one figure, and a change to one moves both.
    /// </summary>
    [Fact]
    public void TheVerifiersDefaultsAreTheRulesDefaults()
    {
        var options = new SignatureVerificationOptions();
        Assert.Equal(TimeSpan.FromDays(365), options.MaxRevocationDataAge);
        Assert.Equal(TimeSpan.FromMinutes(5), options.ClockSkew);
        Assert.Equal(RevocationFreshness.DefaultMaxAgeWithoutNextUpdate, options.MaxRevocationDataAge);
        Assert.Equal(RevocationFreshness.DefaultClockSkew, options.ClockSkew);
    }
}
