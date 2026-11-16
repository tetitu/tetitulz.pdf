// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.IO;
using System.Linq;
using System.Threading;
using Tetitulz.Pdf.Base.Operations;
using Tetitulz.Pdf.Base.Operations.Extraction;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Diagnostics;

/// <summary>
/// The server-host scenario CLAUDE.md's "Diagnostics Pattern" section warns about, for the
/// <c>Tetitulz.Pdf.Base</c> <c>Last*</c> slots: two requests fail differently at the same moment
/// and each one must read back <b>its own</b> reason.
///
/// <para>Before the <c>[ThreadStatic]</c> conversion these were plain process-wide statics, so
/// whichever thread wrote last won and the other silently read a diagnostic belonging to someone
/// else's document. The <see cref="Barrier"/> rendezvous below removes the timing luck: both
/// threads are guaranteed to have written before either reads, so with process-global storage
/// both would necessarily observe the same value and these assertions could not pass.</para>
/// </summary>
public class ThreadStaticDiagnosticIsolationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs <paramref name="body"/> on two dedicated threads. Each thread performs its failing
    /// operation, waits for the other to finish writing, then reads the diagnostic back.
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
                { IsBackground = true, Name = "base-diag-isolation-" + index };
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
    //  PdfSpatialQuery.LastError — two real queries over a real layout,
    //  each failing for its own reason, at the same time.
    // ==================================================================

    [Fact]
    public void PdfSpatialQuery_LastError_IsIsolatedPerThread()
    {
        // Same in-memory layout the PdfSpatialQuery suite already uses: on baseline y=700 the
        // word "ID" is leftmost and "99" is rightmost, so one query runs off the left edge and
        // the other off the right edge — two different failures, one shared diagnostic slot.
        const int Alef = 0x05D0, Bet = 0x05D1;
        byte[] pdf = new InvoicePdfBuilder()
            .AsciiRun("ID", x: 50, y: 700, step: 4)
            .AsciiRun("99", x: 90, y: 700, step: 4)
            .HebRunRtl(InvoicePdfBuilder.Hb(Alef, Bet), xRight: 250, y: 650)
            .AsciiRun("55", x: 200, y: 650)
            .Build();

        PdfPageTextLayout page;
        using (var ms = new MemoryStream(pdf))
            page = PdfStructuredTextExtractor.Extract(ms, new WordAssemblyOptions { ApplyBidi = true })[0];

        PdfTextWord Word(string text) => page.Lines.SelectMany(l => l.Words).First(w => w.Text == text);
        var leftmost = Word("ID");
        var rightmost = Word("99");

        string[] seen = RunConcurrently(
            i =>
            {
                if (i == 0) PdfSpatialQuery.WordToTheRightOf(page, rightmost);
                else        PdfSpatialQuery.WordToTheLeftOf(page, leftmost);
            },
            () => PdfSpatialQuery.LastError);

        // Each thread reads back the reason ITS query failed, not its peer's.
        Assert.Equal("no word to the right", seen[0]);
        Assert.Equal("no word to the left", seen[1]);
    }

    // ==================================================================
    //  A thread that never ran the operation sees the default, never a
    //  neighbouring thread's diagnostic.
    // ==================================================================

    [Fact]
    public void ThreadThatNeverQueried_SeesNullNotAnotherThreadsDiagnostic()
    {
        string observedByBystander = "sentinel";

        using (var written = new ManualResetEventSlim(false))
        {
            var writer = new Thread(() =>
            {
                PdfSpatialQuery.WordsInRect(null, 0f, 0f, 1f, 1f); // sets "page is null"
                written.Set();
            })
            { IsBackground = true };

            var bystander = new Thread(() =>
            {
                written.Wait(Timeout);
                observedByBystander = PdfSpatialQuery.LastError;
            })
            { IsBackground = true };

            writer.Start();
            bystander.Start();

            Assert.True(writer.Join(Timeout), "Writer thread did not finish in time.");
            Assert.True(bystander.Join(Timeout), "Bystander thread did not finish in time.");
        }

        Assert.Null(observedByBystander);
    }
}
