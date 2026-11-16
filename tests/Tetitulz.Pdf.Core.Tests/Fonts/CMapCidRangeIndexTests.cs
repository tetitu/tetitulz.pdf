// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <c>CMapCodec.ToCid</c> resolves a character code to a CID through a per-width sorted index
/// instead of scanning every declared range.
///
/// <para><b>Why the index exists.</b> The scan it replaces ran over every <c>cidrange</c> the CMap
/// declared, for <i>every character code</i>. <c>UniCNS-UCS2-H</c> declares <b>16 418</b> of them,
/// so a page of CJK text cost tens of millions of comparisons on both the render and the extraction
/// path.</para>
///
/// <para><b>What these tests are really pinning.</b> The index is a pure optimisation, so a test
/// that merely checks a CID is correct passes with the index deleted — it has to, since both paths
/// must agree. The claims that can actually break are the two below: that the search is right when
/// declaration order differs from sorted order, and that the <b>overlap fallback</b> survives.
/// The second is the one with teeth, because dropping it is a silent change of answer rather than a
/// crash.</para>
/// </summary>
public class CMapCidRangeIndexTests
{
    /// <summary>Wraps <paramref name="cidRangeLines"/> in the smallest CMap the parser accepts.</summary>
    private static CMapCodec Parse(params string[] cidRangeLines)
    {
        var sb = new StringBuilder();
        sb.Append("/CIDInit /ProcSet findresource begin\nbegincmap\n");
        sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        sb.Append(cidRangeLines.Length).Append(" begincidrange\n");
        foreach (string l in cidRangeLines) sb.Append(l).Append('\n');
        sb.Append("endcidrange\nendcmap\n");
        return CMapCodec.Parse(Encoding.ASCII.GetBytes(sb.ToString()));
    }

    /// <summary>
    /// Ranges declared in <b>descending</b> order, so the declaration order the scan walked and the
    /// sorted order the index searches are reverses of each other. A binary search over an
    /// unsorted array, or one that compared the wrong bound, gets these wrong; the linear scan
    /// could not.
    /// </summary>
    [Fact]
    public void RangesDeclaredOutOfOrderStillResolveToTheDeclaredCids()
    {
        var codec = Parse(
            "<0300> <03ff> 3000",
            "<0200> <02ff> 2000",
            "<0100> <01ff> 1000");

        Assert.Equal(1000, codec.ToCid(0x0100, 2));   // first code of the last-declared range
        Assert.Equal(1005, codec.ToCid(0x0105, 2));
        Assert.Equal(2005, codec.ToCid(0x0205, 2));
        Assert.Equal(3005, codec.ToCid(0x0305, 2));
        Assert.Equal(3255, codec.ToCid(0x03ff, 2));   // last code of the first-declared range
    }

    /// <summary>
    /// A code inside no declared range falls back to the code itself — the documented behaviour for
    /// subset fonts that omit their <c>cidrange</c> table. Asserted at both ends and between two
    /// ranges, because "below everything", "above everything" and "in the gap" are three different
    /// exits from a binary search and only one of them is the loop's natural end.
    /// </summary>
    [Theory]
    [InlineData(0x0050)]   // below the first range
    [InlineData(0x0280)]   // in the gap between two ranges
    [InlineData(0x0500)]   // above the last range
    public void ACodeInNoRangeFallsBackToTheCodeItself(int code)
    {
        var codec = Parse(
            "<0100> <01ff> 1000",
            "<0300> <03ff> 3000");

        Assert.Equal(code, codec.ToCid(code, 2));
    }

    /// <summary>
    /// <b>The load-bearing one.</b> When two ranges of the same width overlap, the CMap's
    /// <i>declaration order</i> decides — the first one wins — and the index is abandoned in favour
    /// of the linear scan that defines that.
    ///
    /// <para>The fixture declares the <b>narrow</b> range first and the wide one second, which is
    /// the only shape where the two strategies disagree: code <c>0x0055</c> is inside both, the
    /// scan answers from the narrow range (<c>900 + 5 = 905</c>) and a sorted search would answer
    /// from the wide one (<c>100 + 0x55 = 185</c>), because the wide range has the smaller
    /// <c>lo</c>. Declaring them the other way round produces the same answer either way and would
    /// have made this test vacuous.</para>
    ///
    /// <para>Mutation-checked: deleting the overlap guard in <c>BuildCidRangeIndex</c> makes this
    /// return <c>185</c>.</para>
    /// </summary>
    [Fact]
    public void OverlappingRangesResolveInDeclarationOrderNotSortedOrder()
    {
        var codec = Parse(
            "<0050> <0060> 900",     // narrow, declared first
            "<0000> <00ff> 100");    // wide, declared second, smaller lo

        Assert.Equal(905, codec.ToCid(0x0055, 2));

        // And a code only the wide range covers still resolves through it.
        Assert.Equal(100 + 0x00f0, codec.ToCid(0x00f0, 2));
    }

    /// <summary>
    /// <b>The index is actually in use</b> for an ordinary CMap, and stands down for an overlapping
    /// one.
    ///
    /// <para>This asserts liveness rather than behaviour, and it is the only test here that can.
    /// Both paths return identical CIDs by construction, so every other test in this file passes
    /// with the index abandoned — which is exactly what happened when the <c>Sort</c> was mutated
    /// away: the overlap check then fired on every CMap, the optimisation switched itself off
    /// document-wide, and the suite stayed green. A silently disabled optimisation is the
    /// "non-functional behind passing tests" failure this repo has shipped three times, and it is
    /// invisible to any assertion about the answer.</para>
    /// </summary>
    [Fact]
    public void TheIndexIsUsedForOrdinaryAndOverlappingCMapsAlike()
    {
        var ordinary = Parse(
            "<0300> <03ff> 3000",
            "<0200> <02ff> 2000",
            "<0100> <01ff> 1000");
        Assert.True(ordinary.UsesCidRangeIndex,
            "an ordinary non-overlapping CMap must resolve through the sorted index");

        // Wave 17 R2.6: the index used to be stood down here, and every code of an overlapping
        // CMap then scanned the whole range list. Each sorted range carries its declaration order
        // now, so the overlapping CMap resolves through the index too — in declaration order (the
        // test above).
        var overlapping = Parse(
            "<0050> <0060> 900",
            "<0000> <00ff> 100");
        Assert.True(overlapping.UsesCidRangeIndex,
            "an overlapping CMap must resolve through the index too, in declaration order");
    }

    /// <summary>
    /// Nested ranges three deep, declared widest first and then narrowest first: the earliest
    /// declared range containing the code wins each time, and a code only the wide range covers still
    /// resolves through it — the sorted walk must look past the nearest range by <c>lo</c>.
    /// </summary>
    [Fact]
    public void NestedOverlappingRanges_ResolveToTheEarliestDeclaredContainingRange()
    {
        var widestFirst = Parse(
            "<0000> <00ff> 100",
            "<0010> <0020> 500",
            "<0015> <0018> 900");
        Assert.Equal(100 + 0x16, widestFirst.ToCid(0x0016, 2));
        Assert.Equal(100 + 0x30, widestFirst.ToCid(0x0030, 2));

        var narrowestFirst = Parse(
            "<0015> <0018> 900",
            "<0010> <0020> 500",
            "<0000> <00ff> 100");
        Assert.Equal(900 + 1, narrowestFirst.ToCid(0x0016, 2));       // the narrow range, declared first
        Assert.Equal(500 + 0x0a, narrowestFirst.ToCid(0x001a, 2));    // outside the narrow one: the middle
        Assert.Equal(100 + 0x30, narrowestFirst.ToCid(0x0030, 2));    // outside both: the wide one
        Assert.Equal(0x0200, narrowestFirst.ToCid(0x0200, 2));        // outside all: the code itself
    }

    /// <summary>
    /// A <c>cidchar</c> entry outranks any range containing the same code, on both paths. It is
    /// checked before the range lookup, so an index that answered first would shadow it.
    /// </summary>
    [Fact]
    public void ASingleCidCharStillOutranksARangeCoveringTheSameCode()
    {
        var text = new StringBuilder()
            .Append("/CIDInit /ProcSet findresource begin\nbegincmap\n")
            .Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n")
            .Append("1 begincidrange\n<0100> <01ff> 1000\nendcidrange\n")
            .Append("1 begincidchar\n<0105> 77\nendcidchar\n")
            .Append("endcmap\n")
            .ToString();

        var codec = CMapCodec.Parse(Encoding.ASCII.GetBytes(text));

        Assert.Equal(77, codec.ToCid(0x0105, 2));     // the cidchar
        Assert.Equal(1004, codec.ToCid(0x0104, 2));   // its neighbour, from the range
    }

    /// <summary>
    /// Ranges of different code widths do not see each other. A one-byte range covering
    /// <c>0x41</c> must not answer a two-byte code <c>0x0041</c>, which is a separate code in a
    /// CMap that declares both widths — the index is keyed by width for exactly this reason, and a
    /// single flat array would conflate them.
    /// </summary>
    [Fact]
    public void RangesOfDifferentWidthsDoNotAnswerEachOthersCodes()
    {
        string text = "/CIDInit /ProcSet findresource begin\nbegincmap\n"
            + "2 begincodespacerange\n<00> <80>\n<8140> <FFFF>\nendcodespacerange\n"
            + "2 begincidrange\n<41> <5a> 700\n<8140> <817e> 8000\nendcidrange\n"
            + "endcmap\n";

        var codec = CMapCodec.Parse(Encoding.ASCII.GetBytes(text));

        Assert.Equal(700, codec.ToCid(0x41, 1));       // one-byte code, one-byte range
        Assert.Equal(8000, codec.ToCid(0x8140, 2));    // two-byte code, two-byte range

        // The same numeric value at the other width matches nothing and falls back to itself.
        Assert.Equal(0x0041, codec.ToCid(0x0041, 2));
    }

    /// <summary>
    /// Scale, which is the whole point: a CMap with five thousand non-overlapping ranges resolves
    /// every one of them correctly. This is the shape of a real Adobe CJK CMap (<c>UniCNS-UCS2-H</c>
    /// declares 16 418 ranges), and it is the case the linear scan made quadratic in page length.
    /// </summary>
    [Fact]
    public void AFiveThousandRangeCMapResolvesEveryRangeCorrectly()
    {
        const int count = 5000;
        var lines = new List<string>(count);

        // Declared descending, so sorted order is the reverse of declaration order throughout.
        // Each range is 4 codes wide with a 4-code gap, so neighbours cannot overlap.
        for (int i = count - 1; i >= 0; i--)
        {
            int lo = 0x1000 + i * 8;
            lines.Add($"<{lo:x4}> <{lo + 3:x4}> {100000 + i * 4}");
        }

        var codec = Parse(lines.ToArray());

        for (int i = 0; i < count; i++)
        {
            int lo = 0x1000 + i * 8;
            Assert.Equal(100000 + i * 4, codec.ToCid(lo, 2));
            Assert.Equal(100000 + i * 4 + 3, codec.ToCid(lo + 3, 2));
            Assert.Equal(lo + 4, codec.ToCid(lo + 4, 2));   // the gap: no range, so identity
        }
    }
}
