// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The scanner's name-intern cache is <b>live</b>: a program repeating a handful of operators
/// serves nearly all of its name tokens from the cache instead of allocating a string apiece.
/// </summary>
/// <remarks>
/// <para><b>Why this cannot be tested through the output.</b> A cache hit and a cache miss produce
/// equal strings, so every other test in this repository passes identically whether the cache
/// works or is disabled outright. CLAUDE.md names this blind spot directly — "an optimisation
/// whose fast and slow paths agree cannot be pinned by asserting the answer" — and records
/// <c>CMapCodec</c> losing its CID index to exactly this, silently, for every document in the
/// library.</para>
///
/// <para><b>What it protects against, concretely.</b> A wrong mask, a hash that never repeats for
/// equal input, a table reallocated per token, or a <c>MatchesBytes</c> that always returns false —
/// every one of these keeps the emitted PDF byte-identical and restores ~14 000 allocations on a
/// 2 000-iteration program (measured: 1 285 KB → 293 KB, plan 16 §43).</para>
///
/// <para><b>The misses are asserted too, not just the hits.</b> A cache that "hit" on everything
/// including genuinely new names would be returning the wrong string — so the distinct-name test
/// below pins the floor from the other side.</para>
/// </remarks>
public class NameInternLivenessTests
{
    private static PsScanner ScanAll(string program)
    {
        var scanner = new PsScanner(Encoding.ASCII.GetBytes(program));
        scanner.ReadAll();
        return scanner;
    }

    /// <summary>
    /// The case the cache exists for: the same seven operators, two thousand times over.
    /// </summary>
    [Fact]
    public void RepeatedOperators_AreServedFromTheCache()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 2000; i++)
            sb.Append(i % 97).Append(' ').Append(i % 31).Append(" add dup 2 mul exch sub dup pop pop\n");

        PsScanner scanner = ScanAll(sb.ToString());

        int total = scanner.NameInternHits + scanner.NameInternMisses;
        Assert.True(total > 10000,
            $"only {total} name tokens scanned; the fixture is not exercising the cache");

        // Seven distinct names across ~14 000 tokens: essentially everything should hit.
        Assert.True(scanner.NameInternHits > total * 0.99,
            $"only {scanner.NameInternHits} of {total} name tokens were interned "
            + $"({scanner.NameInternMisses} misses). The cache is not working, and nothing else in "
            + "this repository would notice — the output is identical either way.");
    }

    /// <summary>
    /// The floor from the other side: genuinely distinct names must MISS, or the cache is handing
    /// back a string that does not match the bytes it was asked about.
    /// </summary>
    [Fact]
    public void DistinctNames_Miss()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++) sb.Append("/name").Append(i).Append(' ');

        PsScanner scanner = ScanAll(sb.ToString());

        Assert.True(scanner.NameInternMisses >= 500,
            $"500 distinct names produced only {scanner.NameInternMisses} misses — the cache is "
            + "returning a shared string for names that differ, which would corrupt every one of "
            + "them.");
    }

    /// <summary>
    /// The correctness property underneath the whole cache: an interned name still decodes to
    /// exactly its own bytes. A collision must cost an allocation, never a wrong answer.
    /// </summary>
    [Fact]
    public void InternedNamesStillDecodeToTheirOwnBytes()
    {
        // Names sharing a length and a prefix are the shape most likely to collide in a
        // direct-mapped table, so ask for them repeatedly and interleaved.
        var sb = new StringBuilder();
        for (int round = 0; round < 50; round++)
            sb.Append("/alpha /alphb /alphc /alpha /alphb /alphc ");

        var scanner = new PsScanner(Encoding.ASCII.GetBytes(sb.ToString()));
        var tokens = scanner.ReadAll();

        int alpha = 0, alphb = 0, alphc = 0, other = 0;
        foreach (PsToken t in tokens)
        {
            if (t.Kind != PsTokenKind.LiteralName) continue;
            switch (t.Text)
            {
                case "alpha": alpha++; break;
                case "alphb": alphb++; break;
                case "alphc": alphc++; break;
                default: other++; break;
            }
        }

        Assert.Equal(0, other);
        Assert.Equal(100, alpha);
        Assert.Equal(100, alphb);
        Assert.Equal(100, alphc);
    }
}
