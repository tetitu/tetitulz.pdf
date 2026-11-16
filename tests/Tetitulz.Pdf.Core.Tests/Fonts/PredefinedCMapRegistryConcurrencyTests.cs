// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Tetitulz.Pdf.Core.Fonts;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Fonts;

/// <summary>
/// <see cref="PredefinedCMapRegistry"/> is process-wide public API: a host registers CMaps at
/// startup (the CMap plugin registers 167 in a row), and the parsed-CMap cache behind
/// <c>LookupPredefined</c> is filled lazily during **text extraction**, which this library runs
/// concurrently (<c>PdfPageRenderer</c> uses <c>Parallel.For</c>).
///
/// <para>
/// Both caches used to be plain <see cref="Dictionary{TKey,TValue}"/> statics. Concurrent writes
/// to one of those can lose entries, tear a bucket chain, or spin forever inside a resize — which
/// showed up as an intermittently failing CMap plugin test whenever registration raced a lookup.
/// These tests hammer both entry points from many threads at once.
/// </para>
/// </summary>
public sealed class PredefinedCMapRegistryConcurrencyTests
{
    /// <summary>A CMap mapping CID 0x42 to a caller-chosen BMP code point.</summary>
    private static byte[] CMapMapping42To(int codePoint)
        => Encoding.ASCII.GetBytes(
            "begincmap\n" +
            "1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n" +
            "1 begincidchar\n<" + codePoint.ToString("X4", CultureInfo.InvariantCulture) + "> 66\nendcidchar\n" +
            "endcmap\n");

    [Fact]
    public void Register_FromManyThreadsAtOnce_LosesNoEntry()
    {
        // Unique prefix so this test cannot collide with any other registration in the process.
        string prefix = "ConcurrencyProbe-" + Guid.NewGuid().ToString("N") + "-";
        const int threads = 8;
        const int perThread = 250;

        var start = new Barrier(threads);
        Parallel.For(0, threads, t =>
        {
            start.SignalAndWait();
            for (int i = 0; i < perThread; i++)
                PredefinedCMapRegistry.Register(
                    prefix + t.ToString(CultureInfo.InvariantCulture) + "-" + i.ToString(CultureInfo.InvariantCulture),
                    CMapMapping42To(0x4E2D));
        });

        // Every single one must still be there. Under a plain Dictionary a concurrent resize
        // silently drops entries (when it does not throw outright).
        var missing = new List<string>();
        for (int t = 0; t < threads; t++)
            for (int i = 0; i < perThread; i++)
            {
                string name = prefix + t.ToString(CultureInfo.InvariantCulture) + "-" + i.ToString(CultureInfo.InvariantCulture);
                if (!PredefinedCMapRegistry.IsRegistered(name)) missing.Add(name);
            }

        Assert.Empty(missing);
    }

    [Fact]
    public void RegisterAndLookup_Concurrently_DoNotCorruptTheRegistry()
    {
        // One set of threads registers; another set looks up (which fills the lazy parsed-CMap
        // cache). This is the real-world shape: a host registering while pages are extracted.
        string prefix = "ConcurrencyProbe-" + Guid.NewGuid().ToString("N") + "-";
        const int names = 64;

        for (int i = 0; i < names; i++)
            PredefinedCMapRegistry.Register(prefix + i.ToString(CultureInfo.InvariantCulture), CMapMapping42To(0x4E2D));

        Exception failure = null;
        var start = new Barrier(8);
        Parallel.For(0, 8, worker =>
        {
            try
            {
                start.SignalAndWait();
                for (int i = 0; i < names; i++)
                {
                    string name = prefix + i.ToString(CultureInfo.InvariantCulture);
                    if ((worker & 1) == 0)
                        PredefinedCMapRegistry.Register(name, CMapMapping42To(0x4E2D));
                    else
                        Assert.True(PredefinedCMapRegistry.IsRegistered(name), name);
                }
            }
            catch (Exception ex)
            {
                Interlocked.CompareExchange(ref failure, ex, null);
            }
        });

        Assert.Null(failure);
        for (int i = 0; i < names; i++)
            Assert.True(PredefinedCMapRegistry.IsRegistered(prefix + i.ToString(CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Register_OverwritesTheExistingEntry()
    {
        // Semantics the concurrency change must not alter: last registration wins, and the
        // cached parse of the previous bytes is invalidated.
        string name = "ConcurrencyProbe-" + Guid.NewGuid().ToString("N");

        Assert.False(PredefinedCMapRegistry.IsRegistered(name));
        PredefinedCMapRegistry.Register(name, CMapMapping42To(0x4E2D));
        Assert.True(PredefinedCMapRegistry.IsRegistered(name));

        PredefinedCMapRegistry.Register(name, CMapMapping42To(0x4E00));
        Assert.True(PredefinedCMapRegistry.IsRegistered(name));
    }

    [Fact]
    public void Register_NullNameOrBytes_IsIgnoredNotThrown()
    {
        PredefinedCMapRegistry.Register(null, CMapMapping42To(0x4E2D));
        PredefinedCMapRegistry.Register("ConcurrencyProbe-NullBytes", null);
        Assert.False(PredefinedCMapRegistry.IsRegistered("ConcurrencyProbe-NullBytes"));
        Assert.False(PredefinedCMapRegistry.IsRegistered(null));
    }
}
