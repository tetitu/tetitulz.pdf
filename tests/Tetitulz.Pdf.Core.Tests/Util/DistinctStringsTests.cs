// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Linq;
using Tetitulz.Pdf.Core.Util;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Util;

/// <summary>
/// The notes and warnings an operation collects about a file's objects: each once, in the order
/// first added, a set beside the list deciding — a search of the list made n notes cost n² / 2
/// comparisons — and joined for a <c>Last*</c> slot under a cap, a file having chosen n.
/// </summary>
public class DistinctStringsTests
{
    [Fact]
    public void EachEntryIsKeptOnce_InTheOrderFirstAdded_TheSetDeciding()
    {
        var entries = new DistinctStrings();
        Assert.True(entries.Add("b"));
        Assert.True(entries.Add("a"));
        Assert.False(entries.Add("b"));
        Assert.False(entries.Add(null));

        Assert.Equal(new[] { "b", "a" }, entries.Items);
        Assert.Equal(2, entries.Seen);
    }

    /// <summary><b>Adversarial: a hundred thousand distinct notes.</b> Kept once each, joined as the first twenty and a count.</summary>
    [Fact]
    public void AdversarialManyNotes_AreKeptOnce_AndJoinedUnderTheCap()
    {
        var entries = new DistinctStrings();
        for (int i = 0; i < 100_000; i++) entries.Add("object " + i);
        for (int i = 0; i < 100_000; i++) entries.Add("object " + i);

        Assert.Equal(100_000, entries.Count);
        Assert.Equal(100_000, entries.Seen);
        string joined = entries.Joined("; ");
        Assert.StartsWith("object 0; object 1; ", joined, System.StringComparison.Ordinal);
        Assert.EndsWith("object 19; and 99980 more", joined, System.StringComparison.Ordinal);
        Assert.Equal(DistinctStrings.MaxJoined, joined.Split("; ").Count(s => s.StartsWith("object", System.StringComparison.Ordinal)));
    }

    /// <summary>The control: at the cap, every entry is named and nothing is counted.</summary>
    [Fact]
    public void AtTheCap_EveryEntryIsNamed()
    {
        var entries = new DistinctStrings();
        for (int i = 0; i < DistinctStrings.MaxJoined; i++) entries.Add("n" + i);
        Assert.Equal(string.Join(", ", entries.Items), entries.Joined(", "));
        Assert.DoesNotContain("more", entries.Joined(", "), System.StringComparison.Ordinal);
    }
}
