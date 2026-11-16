// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Core.Fonts;
using Tetitulz.Pdf.Core.IO.Filters;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Core.Tests.Diagnostics;

/// <summary>
/// The server-host scenario CLAUDE.md's "Diagnostics Pattern" section warns about: two requests
/// fail differently at the same moment, and each one must read back <b>its own</b> reason.
///
/// <para>Before the <c>[ThreadStatic]</c> conversion these <c>Last*</c> slots were plain
/// process-wide statics, so whichever thread wrote last won and the other thread silently read a
/// diagnostic belonging to someone else's document — the worst kind of wrong answer, because it
/// looks like a real answer. These tests would have failed (non-deterministically, and
/// deterministically with the barrier below) against the old storage.</para>
///
/// <para>Each test writes on both threads, rendezvouses at a <see cref="Barrier"/> so both writes
/// are guaranteed to have happened, and only then reads. That removes the timing luck: with a
/// process-global static both threads necessarily observe the same value after the barrier, so
/// the assertion cannot pass by accident.</para>
///
/// <para>Joins the <c>FilterPipelineStatics</c> collection because
/// <see cref="FilterPipeline.StrictUnknownFilters"/> is a genuine process-wide configuration
/// toggle (not a diagnostic) that another test class flips.</para>
/// </summary>
[Collection("FilterPipelineStatics")]
public class ThreadStaticDiagnosticIsolationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="body"/> on two dedicated threads. Each thread performs its failing
    /// operation, waits for the other to finish writing, then reads the diagnostic back.
    /// Returns what each thread saw.
    /// </summary>
    private static string[] RunConcurrently(Action<int> body, Func<string> read)
    {
        var seen = new string[2];
        var failures = new Exception[2];
        var threads = new Thread[2];

        using (var barrier = new Barrier(2))
        {
            for (int i = 0; i < 2; i++)
            {
                int index = i;
                threads[index] = new Thread(() =>
                {
                    // Never let an exception skip the barrier — that would deadlock the peer.
                    try { body(index); }
                    catch (Exception ex) { failures[index] = ex; }

                    barrier.SignalAndWait(Timeout);
                    seen[index] = read();
                })
                { IsBackground = true, Name = "diag-isolation-" + index };
                threads[index].Start();
            }

            foreach (var t in threads)
                Assert.True(t.Join(Timeout), $"Thread '{t.Name}' did not finish within {Timeout}.");
        }

        Assert.Null(failures[0]);
        Assert.Null(failures[1]);
        return seen;
    }

    // ==================================================================
    //  FilterPipeline.LastUnknownFilterName
    // ==================================================================

    [Fact]
    public void FilterPipeline_LastUnknownFilterName_IsIsolatedPerThread()
    {
        FilterPipeline.StrictUnknownFilters = false;
        byte[] payload = Encoding.ASCII.GetBytes("not really compressed");
        var filterNames = new[] { "AlphaMysteryDecode", "BetaMysteryDecode" };

        string[] seen = RunConcurrently(
            i => FilterPipeline.Decode(payload, new PdfName(filterNames[i]), null),
            () => FilterPipeline.LastUnknownFilterName);

        // Each thread reads back the filter name IT could not decode, not its peer's.
        Assert.Equal("AlphaMysteryDecode", seen[0]);
        Assert.Equal("BetaMysteryDecode", seen[1]);
    }

    // ==================================================================
    //  CMapParser.LastUsecmapName
    // ==================================================================

    [Fact]
    public void CMapParser_LastUsecmapName_IsIsolatedPerThread()
    {
        // Both targets take the "Identity" branch, so neither parse recurses into a
        // registered CMap — the value each thread reads is purely its own.
        var cmaps = new[]
        {
            Encoding.ASCII.GetBytes("/Identity-H usecmap\nbeginbfchar\n<0041> <0061>\nendbfchar\n"),
            Encoding.ASCII.GetBytes("/Identity-V usecmap\nbeginbfchar\n<0042> <0062>\nendbfchar\n"),
        };

        string[] seen = RunConcurrently(
            i => CMapParser.Parse(cmaps[i]),
            () => CMapParser.LastUsecmapName);

        Assert.Equal("Identity-H", seen[0]);
        Assert.Equal("Identity-V", seen[1]);
    }

    // ==================================================================
    //  A thread that never ran the operation sees the default, never a
    //  neighbouring thread's value.
    // ==================================================================

    [Fact]
    public void ThreadThatNeverDecoded_SeesNullNotAnotherThreadsDiagnostic()
    {
        FilterPipeline.StrictUnknownFilters = false;
        byte[] payload = Encoding.ASCII.GetBytes("not really compressed");

        string observedByBystander = null;
        using (var written = new ManualResetEventSlim(false))
        {
            var writer = new Thread(() =>
            {
                FilterPipeline.Decode(payload, new PdfName("GammaMysteryDecode"), null);
                written.Set();
            })
            { IsBackground = true };

            var bystander = new Thread(() =>
            {
                written.Wait(Timeout);
                observedByBystander = FilterPipeline.LastUnknownFilterName;
            })
            { IsBackground = true };

            writer.Start();
            bystander.Start();

            Assert.True(writer.Join(Timeout), "Writer thread did not finish in time.");
            Assert.True(bystander.Join(Timeout), "Bystander thread did not finish in time.");
        }

        // The bystander never decoded anything, so it must not inherit the writer's diagnostic.
        Assert.Null(observedByBystander);
    }
}
