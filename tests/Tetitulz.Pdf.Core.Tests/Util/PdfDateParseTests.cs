// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Util;

/// <summary>
/// §7.9.4 date parsing — <c>D:YYYYMMDDHHmmSSOHH'mm'</c>.
///
/// <para><b>There were two parsers and they disagreed about how much of the string exists.</b>
/// <c>PdfMetadata</c>'s read the whole thing: time of day, sign, hours and the minutes after the
/// apostrophe. <c>PdfPortfolioManager</c>'s read <b>eight characters</b> — year, month, day — and
/// returned midnight, so every embedded file in a portfolio reported a creation time of 00:00 with
/// no signal that a time had been read and discarded. Both now call
/// <see cref="PdfDate.TryParse(string)"/>.</para>
///
/// <para>The assertions name the exact instant, because a truncating parser returns a
/// <i>plausible</i> date — the right day, silently the wrong time — which is what let it live.</para>
/// </summary>
public sealed class PdfDateParseTests
{
    /// <summary>
    /// The full form, with the offset. This is the case the truncating parser turned into
    /// <c>2026-08-19T00:00Z</c>.
    /// </summary>
    [Fact]
    public void FullDate_KeepsTheTimeOfDayAndTheOffset()
    {
        DateTimeOffset d = Assert.NotNull(PdfDate.TryParse("D:20260819143005+03'00'"));

        Assert.Equal(new DateTime(2026, 8, 19, 14, 30, 5), d.DateTime);
        Assert.Equal(TimeSpan.FromHours(3), d.Offset);
        // …so the instant is 11:30 UTC, not 14:30 and not midnight.
        Assert.Equal(new DateTime(2026, 8, 19, 11, 30, 5, DateTimeKind.Utc), d.UtcDateTime);
    }

    /// <summary>A negative offset, and one with non-zero minutes — Newfoundland is the classic.</summary>
    [Fact]
    public void NegativeOffsetWithMinutes_IsSubtracted()
    {
        DateTimeOffset d = Assert.NotNull(PdfDate.TryParse("D:20260819120000-03'30'"));

        Assert.Equal(TimeSpan.FromMinutes(-210), d.Offset);
        Assert.Equal(new DateTime(2026, 8, 19, 15, 30, 0, DateTimeKind.Utc), d.UtcDateTime);
    }

    /// <summary><c>Z</c> is a relationship indicator, not a sign: the offset is zero.</summary>
    [Fact]
    public void ZuluIndicator_MeansZeroOffset()
    {
        DateTimeOffset d = Assert.NotNull(PdfDate.TryParse("D:20260819143005Z"));

        Assert.Equal(TimeSpan.Zero, d.Offset);
        Assert.Equal(new DateTime(2026, 8, 19, 14, 30, 5), d.DateTime);
    }

    /// <summary>
    /// Every component after the year is optional (§7.9.4), and the defaults are the spec's: month
    /// and day 1, everything else 0.
    /// </summary>
    [Theory]
    [InlineData("D:2026", 2026, 1, 1, 0, 0, 0)]
    [InlineData("D:202608", 2026, 8, 1, 0, 0, 0)]
    [InlineData("D:20260819", 2026, 8, 19, 0, 0, 0)]
    [InlineData("D:2026081914", 2026, 8, 19, 14, 0, 0)]
    [InlineData("D:202608191430", 2026, 8, 19, 14, 30, 0)]
    [InlineData("D:20260819143005", 2026, 8, 19, 14, 30, 5)]
    public void TruncatedForms_FillInTheSpecDefaults(
        string s, int y, int mo, int d, int h, int mi, int sec)
    {
        DateTimeOffset parsed = Assert.NotNull(PdfDate.TryParse(s));

        Assert.Equal(new DateTime(y, mo, d, h, mi, sec), parsed.DateTime);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
    }

    /// <summary>The <c>D:</c> prefix is required by the spec and tolerated as absent by readers.</summary>
    [Fact]
    public void MissingPrefix_IsStillParsed()
        => Assert.Equal(new DateTime(2026, 8, 19, 14, 30, 5),
                        Assert.NotNull(PdfDate.TryParse("20260819143005")).DateTime);

    /// <summary>
    /// A producer that closes the minutes with nothing, or writes only the hours. Both appear in
    /// real files; neither may cost the offset that <i>is</i> written.
    /// </summary>
    [Theory]
    [InlineData("D:20260819143005+03'00", 180)]
    [InlineData("D:20260819143005+03", 180)]
    [InlineData("D:20260819143005+03'", 180)]
    public void PartialOffsetSpellings_KeepTheHours(string s, int expectedMinutes)
        => Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
                        Assert.NotNull(PdfDate.TryParse(s)).Offset);

    /// <summary>
    /// Whether the date states its zone: a <c>+</c>, <c>-</c> or <c>Z</c> does and nothing does not —
    /// and the offset of a date that states none is the parser's zero, as before.
    /// </summary>
    [Theory]
    [InlineData("D:20260819143005+03'00'", true, 180)]
    [InlineData("D:20260819143005-03'30'", true, -210)]
    [InlineData("D:20260819143005Z", true, 0)]
    [InlineData("D:20260819143005", false, 0)]
    [InlineData("D:2026", false, 0)]
    public void ZoneStated_SaysWhetherTheDateNamesItsZone(string s, bool stated, int offsetMinutes)
    {
        DateTimeOffset d = Assert.NotNull(PdfDate.TryParse(s, out bool zoneStated));

        Assert.Equal(stated, zoneStated);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), d.Offset);
    }

    /// <summary>
    /// A zone written after the day, the hour or the minutes — short of the seconds §7.9.4 asks for
    /// before it — is still the zone: read at fixed places, <c>+0</c> became the seconds and the
    /// offset was lost. The zone's minutes are read without the apostrophe too (<c>+0330</c>).
    /// </summary>
    [Theory]
    [InlineData("D:202608191430+03'00'", 14, 30, 0, 180)]
    [InlineData("D:2026081914+03'00'", 14, 0, 0, 180)]
    [InlineData("D:20260819-05'00'", 0, 0, 0, -300)]
    [InlineData("D:20260819143005+0330", 14, 30, 5, 210)]
    public void AZoneAfterFewerComponents_IsStillTheZone(string s, int h, int mi, int sec, int offsetMinutes)
    {
        DateTimeOffset d = Assert.NotNull(PdfDate.TryParse(s, out bool zoneStated));

        Assert.True(zoneStated);
        Assert.Equal(new DateTime(2026, 8, 19, h, mi, sec), d.DateTime);
        Assert.Equal(TimeSpan.FromMinutes(offsetMinutes), d.Offset);
    }

    /// <summary>
    /// What a fixed-place reader passed over is passed over still: text after the seconds that is
    /// not a zone, and a last character standing alone.
    /// </summary>
    [Theory]
    [InlineData("D:20260819143005 ", 2026, 8, 19, 14, 30, 5)]
    [InlineData("D:2026081", 2026, 8, 1, 0, 0, 0)]
    [InlineData("D:202608191", 2026, 8, 19, 0, 0, 0)]
    public void TrailingTextAndALoneCharacter_ArePassedOver(string s, int y, int mo, int d, int h, int mi, int sec)
    {
        DateTimeOffset parsed = Assert.NotNull(PdfDate.TryParse(s, out bool zoneStated));

        Assert.False(zoneStated);
        Assert.Equal(new DateTime(y, mo, d, h, mi, sec), parsed.DateTime);
    }

    // ── malformed input returns null rather than throwing ─────────────────

    /// <summary>
    /// Malformed dates are someone else's file, so they must not take the document read with them.
    /// A month of 13 and a day of 32 both reach <c>DateTimeOffset</c>'s own validation.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("D:")]
    [InlineData("D:202")]              // shorter than a year
    [InlineData("D:20261319")]         // month 13
    [InlineData("D:20260232")]         // day 32
    [InlineData("D:2026081999")]       // hour 99
    [InlineData("D:20260819143005+99'00'")]  // offset beyond ±14 h
    [InlineData("D:abcd")]
    [InlineData("D:2026-03-04")]       // a sign before the day is read is punctuation, not a zone
    [InlineData("D:202603-04")]
    [InlineData("D:20260819143005+3'00'")]   // the zone's hours not two digits
    [InlineData("D:2026XX19")]
    public void MalformedDates_ReturnNullWithoutThrowing(string? s)
        => Assert.Null(PdfDate.TryParse(s));

    /// <summary>
    /// The formatting half round-trips through the parsing half. They were written separately and
    /// consolidated separately, so this is the assertion that they describe one format.
    /// </summary>
    [Fact]
    public void FormatUtc_RoundTripsThroughTryParse()
    {
        var when = new DateTime(2026, 8, 19, 14, 30, 5, DateTimeKind.Utc);

        DateTimeOffset back = Assert.NotNull(PdfDate.TryParse(PdfDate.FormatUtc(when)));

        Assert.Equal(TimeSpan.Zero, back.Offset);
        Assert.Equal(when, back.UtcDateTime);
    }
}
