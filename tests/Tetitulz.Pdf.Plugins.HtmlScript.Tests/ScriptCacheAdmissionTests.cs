// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Linq;
using Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs.Cache;
using Xunit;

namespace Tetitulz.Pdf.Plugins.HtmlScript.Tests;

/// <summary>The process-wide script cache, which these tests clear and fill: run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ScriptCacheCollection
{
    public const string Name = "The process-wide script cache";
}

/// <summary>
/// What the vendored script cache keeps (VENDORED.md, item 10): a source short enough and prepared a second time,
/// with the entry served least giving way when every slot is taken — so the first page a process converts cannot
/// fill the cache with scripts of its own for good.
/// </summary>
[Collection(ScriptCacheCollection.Name)]
public class ScriptCacheAdmissionTests
{
    private static string Source(string tag) => $"var v_{tag}_{Guid.NewGuid():N} = 1;";

    /// <summary>A source prepared once is not kept; prepared again, it is.</summary>
    [Fact]
    public void ASourceIsKeptOnItsSecondPreparation()
    {
        ScriptCache.Clear();
        string source = Source("twice");

        ScriptCache.GetOrCreate(source);
        Assert.False(ScriptCache.Holds(source));
        ScriptCache.GetOrCreate(source);
        Assert.True(ScriptCache.Holds(source));
    }

    /// <summary>A source past the length cap is prepared and never kept; one at the cap is (the control).</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void ASourcePastTheLengthCap_IsNeverKept(int past, bool kept)
    {
        ScriptCache.Clear();
        string head = Source("long") + "//";
        string source = head + new string('x', ScriptCache.MaxCachedSourceLength - head.Length + past);

        ScriptCache.GetOrCreate(source);
        ScriptCache.GetOrCreate(source);
        Assert.Equal(kept, ScriptCache.Holds(source));
    }

    /// <summary>
    /// Thirty-two sources fill the cache, and every one but the eighth is served again. A new source prepared twice
    /// takes a slot, and the one that gives way is the eighth: the entry served least.
    /// </summary>
    [Fact]
    public void AFullCache_MakesRoomByDroppingTheEntryServedLeast()
    {
        ScriptCache.Clear();
        string[] sources = Enumerable.Range(0, 32).Select(i => Source("fill" + i)).ToArray();
        foreach (string s in sources) { ScriptCache.GetOrCreate(s); ScriptCache.GetOrCreate(s); }
        Assert.Equal(32, ScriptCache.Count);
        for (int i = 0; i < sources.Length; i++)
            if (i != 7) ScriptCache.GetOrCreate(sources[i]);     // served from the cache: one hit each

        string newcomer = Source("new");
        ScriptCache.GetOrCreate(newcomer);
        ScriptCache.GetOrCreate(newcomer);

        Assert.True(ScriptCache.Holds(newcomer));
        Assert.False(ScriptCache.Holds(sources[7]));
        Assert.Equal(32, ScriptCache.Count);
        Assert.Equal(31, sources.Count(s => ScriptCache.Holds(s)));   // exactly one of the thirty-two gave way
    }
}
