// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Threading;
using Tetitulz.Pdf.Core.Imaging.Jpx;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Core.Tests.Imaging.Jpx;

/// <summary>
/// <see cref="JpxParallel"/>: every item once, worker 0 the calling thread, degree 1 with no
/// thread at all, a worker's exception rethrown as itself, and the degree a thread count means.
/// </summary>
public sealed class JpxParallelTests
{
    /// <summary>
    /// A thousand items over four workers: each runs exactly once, on a worker index below the
    /// degree; and the work really was shared — three items that each wait for the other two to
    /// have started can only finish if three workers run at once.
    /// </summary>
    [Fact]
    public void For_RunsEveryItemOnce_OnWorkersBelowTheDegree_AndReallyInParallel()
    {
        var parallel = new JpxParallel(4);
        var runs = new int[1000];
        var workers = new int[1000];
        parallel.For(runs.Length, (item, worker) =>
        {
            Interlocked.Increment(ref runs[item]);
            workers[item] = worker;
            Thread.SpinWait(200);
        });
        Assert.All(runs, r => Assert.Equal(1, r));
        Assert.All(workers, w => Assert.InRange(w, 0, 3));

        // Each worker has a thread of its own, so the three start whatever the thread pool is
        // doing; the wait is a hang guard for a loop that never starts one, not a measurement —
        // on pool threads it timed out under a whole-solution run's load, with the third worker
        // queued behind other tests' work.
        using var started = new CountdownEvent(3);
        var threads = new int[3];
        new JpxParallel(3).For(3, (item, worker) =>
        {
            threads[item] = Thread.CurrentThread.ManagedThreadId;
            started.Signal();
            Assert.True(started.Wait(60_000), "three workers did not run at once");
        });
        Assert.Equal(3, new HashSet<int>(threads).Count);
    }

    /// <summary>Degree 1: the calling thread, in item order, whatever the count — no task is started.</summary>
    [Fact]
    public void For_WithDegreeOne_RunsOnTheCallingThreadInOrder()
    {
        int caller = Thread.CurrentThread.ManagedThreadId;
        var order = new List<int>();
        var threads = new HashSet<int>();
        JpxParallel.Sequential.For(5, (item, worker) =>
        {
            order.Add(item);
            threads.Add(Thread.CurrentThread.ManagedThreadId);
            Assert.Equal(0, worker);
        });
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, order);
        Assert.Equal(new[] { caller }, threads);
        Assert.Equal(1, JpxParallel.Sequential.Degree);
        Assert.Equal(1, new JpxParallel(0).Degree);
    }

    /// <summary>Worker 0 is the calling thread, and a count below the degree uses only as many workers as items.</summary>
    [Fact]
    public void For_WorkerZeroIsTheCallingThread_AndACountBelowTheDegreeUsesFewerWorkers()
    {
        int caller = Thread.CurrentThread.ManagedThreadId;
        var byWorker = new int[8];
        var workerOf = new int[200];
        var onCaller = new bool[200];
        // The other workers wait for the caller's first item: on threads of their own they start
        // at once and would otherwise drain two hundred cheap items before the caller claimed one.
        // What the contract promises is that worker 0 is the calling thread, item for item.
        using var callerRan = new ManualResetEventSlim();
        new JpxParallel(8).For(200, (item, worker) =>
        {
            if (worker == 0) callerRan.Set();
            else callerRan.Wait();
            Interlocked.Increment(ref byWorker[worker]);
            workerOf[item] = worker;
            onCaller[item] = Thread.CurrentThread.ManagedThreadId == caller;
            Thread.SpinWait(500);
        });
        Assert.True(byWorker[0] > 0, "the calling thread ran nothing");
        Assert.Equal(200, byWorker.Sum());
        for (int item = 0; item < 200; item++)
            Assert.Equal(workerOf[item] == 0, onCaller[item]);

        var seen = new HashSet<int>();
        new JpxParallel(8).For(2, (item, worker) => { lock (seen) seen.Add(worker); });
        Assert.Subset(new HashSet<int> { 0, 1 }, seen);
    }

    /// <summary>
    /// A worker's exception arrives on the calling thread as itself — a work-budget exhaustion is a
    /// <see cref="WorkBudgetExceededException"/>, not an aggregate — and the other workers stop at
    /// their next item rather than run the ten thousand to the end.
    /// </summary>
    [Fact]
    public void For_RethrowsAWorkersExceptionAsItself_AndStopsTheOthers()
    {
        int executed = 0;
        var ex = Assert.Throws<WorkBudgetExceededException>(() => new JpxParallel(4).For(10_000, (item, worker) =>
        {
            Interlocked.Increment(ref executed);
            if (item == 5) throw new WorkBudgetExceededException("the fifth item");
            Thread.SpinWait(2000);
        }));
        Assert.Equal("the fifth item", ex.Message);
        Assert.InRange(executed, 1, 2000);
    }

    /// <summary>
    /// 0 means the processor count capped at <see cref="JpxParallel.MaxAutomaticDegree"/> and at
    /// one worker per <see cref="JpxParallel.SamplesPerWorker"/> of work, never below one — a
    /// worker is a thread started for the decode, and a 40x24 image is not worth eleven of them;
    /// a positive count is itself whatever the work; a negative one is refused.
    /// </summary>
    [Fact]
    public void Resolve_ReadsZeroAsTheProcessorCountCapped_ByTheWork_AndRefusesANegativeCount()
    {
        int cores = Math.Min(Environment.ProcessorCount, JpxParallel.MaxAutomaticDegree);
        Assert.Equal(cores, JpxParallel.Resolve(0, long.MaxValue / 4));
        Assert.Equal(1, JpxParallel.Resolve(0, 40 * 24 * 3));
        Assert.Equal(1, JpxParallel.Resolve(0, JpxParallel.SamplesPerWorker - 1));
        Assert.Equal(1, JpxParallel.Resolve(0, JpxParallel.SamplesPerWorker));
        Assert.Equal(Math.Min(2, cores), JpxParallel.Resolve(0, 2 * JpxParallel.SamplesPerWorker));
        Assert.Equal(Math.Min(7, cores), JpxParallel.Resolve(0, 7 * JpxParallel.SamplesPerWorker + 5));
        Assert.Equal(1, JpxParallel.Resolve(0, 0));
        Assert.Equal(5, JpxParallel.Resolve(5, 1));
        Assert.Equal(64, JpxParallel.Resolve(64, long.MaxValue / 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxParallel.Resolve(-1, long.MaxValue / 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => JpxParallel.RequireValid(-1));
    }
}
