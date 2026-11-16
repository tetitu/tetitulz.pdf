// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.TestHelpers;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="TrueTypeSubsetter"/> against a font program whose own tables are lying.
/// </summary>
/// <remarks>
/// <para><b>The declining return is the contract, and it is load-bearing.</b> Every caller reads
/// <c>(null, null)</c> as "embed the whole program", which is correct and lossless — so a hostile
/// font must produce a <i>refusal with a reason</i>, never a throw and never a subset built from
/// nonsense. These assert <see cref="TrueTypeSubsetter.LastDeclineReason"/> rather than a clock:
/// the guard's own evidence is deterministic, and a timing test that finishes fast has quite
/// possibly not run.</para>
///
/// <para><b>The allocation is the attack, not the read.</b> A crafted <c>glyf</c> length made
/// <c>new byte[len]</c> reserve up to 4 GiB before the <c>Array.Copy</c> that would have failed;
/// the blanket <c>catch</c> one frame up only ever saw the aftermath. Checking the directory
/// against the file length moves the refusal in front of the allocation.</para>
/// </remarks>
public class SubsetterBoundedReadTests
{
    private static readonly ushort[] Gids = { 1, 2 };

    /// <summary>The table directory of a synthetic font, as (tag, offsetPos, lengthPos).</summary>
    private static IEnumerable<(string tag, int offsetPos, int lengthPos)> Directory(byte[] font)
    {
        int n = (font[4] << 8) | font[5];
        for (int i = 0; i < n; i++)
        {
            int rec = 12 + i * 16;
            yield return (Encoding.ASCII.GetString(font, rec, 4), rec + 8, rec + 12);
        }
    }

    private static (int off, int len) TableSpan(byte[] font, string tag)
    {
        var (_, offsetPos, lengthPos) = Directory(font).First(e => e.tag == tag);
        return (ReadU32(font, offsetPos), ReadU32(font, lengthPos));
    }

    private static int ReadU32(byte[] b, int p) =>
        (b[p] << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3];

    private static void WriteU32(byte[] b, int p, uint v)
    {
        b[p] = (byte)(v >> 24); b[p + 1] = (byte)(v >> 16);
        b[p + 2] = (byte)(v >> 8); b[p + 3] = (byte)v;
    }

    // ================================================================ the control

    /// <summary>
    /// An honest font still subsets — without this, every refusal below passes for a subsetter that
    /// refuses everything.
    /// </summary>
    [Fact]
    public void AnHonestFontStillSubsets()
    {
        var (subset, map) = TrueTypeSubsetter.Subset(MinimalTrueType.Build("ABC"), Gids);

        Assert.NotNull(subset);
        Assert.NotNull(map);
        Assert.Null(TrueTypeSubsetter.LastDeclineReason);
        Assert.True(subset.Length > 0 && subset.Length < MinimalTrueType.Build("ABC").Length + 1024,
            "a subset of a three-glyph font should be about the size of the font, not wildly more");
    }

    // ================================================================ the directory

    /// <summary>
    /// <b>A <c>glyf</c> length far past the end of the file is refused before anything is
    /// allocated.</b>
    /// </summary>
    /// <remarks>
    /// 0x7FFF0000 is ~2 GiB. Before the directory check this reached
    /// <c>_glyf = new byte[gLen]</c>, which reserves that much and only then fails the copy —
    /// exactly one crafted font per 2 GiB of the host's memory, from a code path a document
    /// reaches by asking for its fonts to be embedded.
    /// </remarks>
    [Fact]
    public void AnOversizedGlyfLengthIsRefusedRatherThanAllocated()
    {
        byte[] font = MinimalTrueType.Build("ABC");
        var (_, lengthPos) = Directory(font).First(e => e.tag == "glyf") is var e
            ? (e.offsetPos, e.lengthPos) : default;
        WriteU32(font, lengthPos, 0x7FFF0000u);

        var (subset, map) = TrueTypeSubsetter.Subset(font, Gids);

        Assert.Null(subset);
        Assert.Null(map);
        Assert.Contains("outside the file", TrueTypeSubsetter.LastDeclineReason);
        Assert.Contains("glyf", TrueTypeSubsetter.LastDeclineReason);
    }

    /// <summary>An offset past the end of the file is refused, and the tag is named.</summary>
    [Theory]
    [InlineData("loca")]
    [InlineData("head")]
    [InlineData("hmtx")]
    public void AnOffsetPastTheEndOfTheFileIsRefused(string tag)
    {
        byte[] font = MinimalTrueType.Build("ABC");
        var (_, offsetPos, _) = Directory(font).First(e => e.tag == tag);
        WriteU32(font, offsetPos, (uint)font.Length + 16);

        var (subset, _) = TrueTypeSubsetter.Subset(font, Gids);

        Assert.Null(subset);
        Assert.Contains("outside the file", TrueTypeSubsetter.LastDeclineReason);
        Assert.Contains(tag, TrueTypeSubsetter.LastDeclineReason);
    }

    /// <summary>
    /// A length that fits only when read as unsigned overflow is refused too.
    /// </summary>
    /// <remarks>
    /// <c>off + len</c> wraps past <c>uint.MaxValue</c>, so a naive <c>off + len &gt; length</c>
    /// check would pass it. The check is written as <c>len &gt; length - off</c> for that reason.
    /// </remarks>
    [Fact]
    public void ALengthThatOverflowsWhenAddedToTheOffsetIsRefused()
    {
        byte[] font = MinimalTrueType.Build("ABC");
        var (_, offsetPos, lengthPos) = Directory(font).First(e => e.tag == "glyf");

        WriteU32(font, offsetPos, 16);
        WriteU32(font, lengthPos, 0xFFFFFFF0u);   // 16 + this wraps to zero

        var (subset, _) = TrueTypeSubsetter.Subset(font, Gids);

        Assert.Null(subset);
        Assert.Contains("outside the file", TrueTypeSubsetter.LastDeclineReason);
    }

    // ================================================================ loca's own values

    /// <summary>
    /// <b>A <c>loca</c> entry pointing past <c>glyf</c> is refused, even though the directory is
    /// honest.</b>
    /// </summary>
    /// <remarks>
    /// This is the case the directory check cannot reach: every table fits the file, and the lie is
    /// inside one of them. It matters because <c>Components</c> reaches the value as
    /// <c>new R(_glyf, (int)s)</c> — a large offset truncates through the cast and reads a
    /// perfectly valid but completely different glyph, so the failure is silent rather than loud.
    /// </remarks>
    [Fact]
    public void AMalformedLocaPointingPastGlyfIsRefused()
    {
        byte[] font = MinimalTrueType.Build("ABC");
        var (locaOff, _) = TableSpan(font, "loca");

        // Short format: entries are offset/2, so 0xFFFF means byte offset 131070.
        font[locaOff + 2] = 0xFF;
        font[locaOff + 3] = 0xFF;

        var (subset, _) = TrueTypeSubsetter.Subset(font, Gids);

        Assert.Null(subset);
        Assert.Contains("past the", TrueTypeSubsetter.LastDeclineReason);
        Assert.Contains("glyf", TrueTypeSubsetter.LastDeclineReason);
    }

    /// <summary>
    /// <b>A <c>loca</c> that goes backwards is refused</b> — that glyph has a negative length.
    /// </summary>
    /// <remarks>
    /// §5.3.4 makes <c>loca</c> monotonically non-decreasing. A decreasing pair is not merely odd:
    /// <c>e &lt;= s</c> reads as "this glyph is empty", so the glyph silently vanishes from the
    /// subset instead of the font being refused.
    /// </remarks>
    [Fact]
    public void AMalformedLocaThatDecreasesIsRefused()
    {
        byte[] font = MinimalTrueType.Build("ABC");
        var (locaOff, locaLen) = TableSpan(font, "loca");
        var (_, glyfLen) = TableSpan(font, "glyf");

        Assert.True(locaLen >= 8, "the fixture needs at least four short-format entries");
        Assert.True(glyfLen >= 16, "and a glyf table with room for both values inside it");

        // BOTH values must stay inside glyf, or the past-the-end check fires first and this test
        // asserts the wrong refusal — which is what the first version of it did.
        font[locaOff + 2] = 0x00; font[locaOff + 3] = 0x04;   // entry 1 = byte offset 8
        font[locaOff + 4] = 0x00; font[locaOff + 5] = 0x01;   // entry 2 = byte offset 2

        var (subset, _) = TrueTypeSubsetter.Subset(font, Gids);

        Assert.Null(subset);
        Assert.Contains("decreases", TrueTypeSubsetter.LastDeclineReason);
    }

    // ================================================================ the contract around it

    /// <summary>
    /// <b>Every refusal is a decline, not a throw</b> — the return-null contract callers depend on.
    /// </summary>
    /// <remarks>
    /// A throw escaping here would reach the blanket <c>catch</c> and still return null, so the
    /// visible result would be the same. The difference is the reason: "the TrueType subsetter
    /// threw (Index was outside the bounds of the array)" tells a caller nothing they can act on,
    /// and this asserts none of these refusals take that path.
    /// </remarks>
    [Fact]
    public void EveryRefusalNamesItsCauseRatherThanReportingAThrow()
    {
        var crafted = new List<byte[]>();

        byte[] a = MinimalTrueType.Build("ABC");
        WriteU32(a, Directory(a).First(e => e.tag == "glyf").lengthPos, 0x7FFF0000u);
        crafted.Add(a);

        byte[] b = MinimalTrueType.Build("ABC");
        var (locaOff, _) = TableSpan(b, "loca");
        b[locaOff + 2] = 0xFF; b[locaOff + 3] = 0xFF;
        crafted.Add(b);

        foreach (byte[] font in crafted)
        {
            Assert.Null(TrueTypeSubsetter.Subset(font, Gids).subset);
            Assert.DoesNotContain("threw", TrueTypeSubsetter.LastDeclineReason);
        }
    }

    /// <summary>The decline reason does not survive into the next call.</summary>
    [Fact]
    public void TheDeclineReasonIsClearedBySuccess()
    {
        byte[] bad = MinimalTrueType.Build("ABC");
        WriteU32(bad, Directory(bad).First(e => e.tag == "glyf").lengthPos, 0x7FFF0000u);

        Assert.Null(TrueTypeSubsetter.Subset(bad, Gids).subset);
        Assert.NotNull(TrueTypeSubsetter.LastDeclineReason);

        Assert.NotNull(TrueTypeSubsetter.Subset(MinimalTrueType.Build("ABC"), Gids).subset);
        Assert.Null(TrueTypeSubsetter.LastDeclineReason);
    }
}
