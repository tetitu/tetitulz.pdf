// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Tetitulz.Pdf.Core.Imaging.Jpx
{
    /// <summary>
    /// The codec's workers for one decode or one encode: a degree, and a loop that hands the items of a count
    /// to that many workers — the calling thread is worker 0 — through one shared counter, so the
    /// work balances itself whatever the items cost. Degree 1 runs everything on the calling
    /// thread with no task at all, so thread-affine diagnostics stay exact (the convention
    /// <c>PdfRenderOptions.MaxRenderThreads = 1</c> follows elsewhere). A worker's exception stops
    /// the others at their next item and is rethrown on the calling thread as itself — a
    /// <c>WorkBudgetExceededException</c> arrives as one, not wrapped — once every worker has
    /// stopped.
    /// </summary>
    /// <remarks>
    /// The body receives the item and the worker index (0 to the degree − 1); a worker index is
    /// held by one thread at a time within a loop, which is what lets a caller keep one workspace
    /// per worker. Every item runs exactly once, in no particular order, so a caller that must
    /// report in order collects by item and reads back afterwards.
    /// </remarks>
    internal sealed class JpxParallel
    {
        /// <summary>The most workers chosen automatically: one image need not take every core of a large machine.</summary>
        internal const int MaxAutomaticDegree = 16;

        /// <summary>Degree 1: the calling thread alone.</summary>
        internal static readonly JpxParallel Sequential = new JpxParallel(1);

        internal JpxParallel(int degree)
        {
            Degree = degree < 1 ? 1 : degree;
        }

        /// <summary>How many workers a loop may use, the calling thread included.</summary>
        internal int Degree { get; }

        /// <summary>
        /// The most workers any loop of this instance was given — 0 until a loop shares its items, so never
        /// for <see cref="Sequential"/>. What a test reads to know a pass really was handed to the workers,
        /// where the result alone is the same either way.
        /// </summary>
        internal int MostWorkers { get; private set; }

        /// <summary>
        /// The samples one worker is worth starting for when the degree is chosen automatically:
        /// a worker is a thread of its own (see <see cref="For"/>), about a millisecond to start,
        /// and tier 1 decodes some tens of nanoseconds a sample, so 2^18 samples are several
        /// milliseconds of work behind each start. Unbounded, a 40x24 fixture on twelve threads
        /// takes 13 ms where one thread takes 0.2 ms.
        /// </summary>
        internal const long SamplesPerWorker = 1L << 18;

        /// <summary>Refuses a negative <c>maxThreads</c> rather than reading it as the default.</summary>
        internal static void RequireValid(int maxThreads)
        {
            if (maxThreads < 0)
                throw new ArgumentOutOfRangeException(nameof(maxThreads), maxThreads, "maxThreads must be 0 (choose automatically) or positive.");
        }

        /// <summary>
        /// The degree a caller's <c>maxThreads</c> means: 0 chooses automatically — the processor
        /// count, at most <see cref="MaxAutomaticDegree"/>, and at most one worker per
        /// <see cref="SamplesPerWorker"/> of <paramref name="samples"/> (the decode's samples over
        /// every component, at the resolution decoded), never below 1; a positive count is taken
        /// as it is, whatever the work; a negative one is refused rather than read as the default.
        /// </summary>
        internal static int Resolve(int maxThreads, long samples)
        {
            RequireValid(maxThreads);
            if (maxThreads > 0) return maxThreads;
            long byWork = Math.Max(1, samples / SamplesPerWorker);
            return (int)Math.Min(byWork, Math.Min(Environment.ProcessorCount, MaxAutomaticDegree));
        }

        /// <summary>Runs <paramref name="body"/>(item, worker) for every item below <paramref name="count"/>.</summary>
        internal void For(int count, Action<int, int> body)
        {
            if (count <= 0) return;
            int workers = Math.Min(Degree, count);
            if (workers == 1)
            {
                for (int i = 0; i < count; i++) body(i, 0);
                return;
            }
            MostWorkers = Math.Max(MostWorkers, workers);

            // Each worker on a thread of its own, not a pool thread: a worker runs CPU-bound for a
            // whole tile while the caller blocks in WaitAll, so on a pool that other work has
            // filled the queued workers would sit behind that work — and behind the pool's slow
            // injection — while the decode waited on them. Under a whole-solution test run that
            // stalled the "really in parallel" check for its full ten seconds three runs in four.
            var loop = new Loop(count, workers, body);
            var tasks = new Task[workers - 1];
            for (int w = 1; w < workers; w++)
            {
                int worker = w;
                tasks[w - 1] = Task.Factory.StartNew(() => loop.Work(worker), CancellationToken.None,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
            loop.Work(0);
            Task.WaitAll(tasks);
            loop.Rethrow();
        }

        private sealed class Loop
        {
            private readonly int _count;
            private readonly Action<int, int> _body;
            private readonly Exception[] _errors;
            private int _next = -1;
            private volatile bool _failed;

            internal Loop(int count, int workers, Action<int, int> body)
            {
                _count = count;
                _body = body;
                _errors = new Exception[workers];
            }

            internal void Work(int worker)
            {
                while (!_failed)
                {
                    int item = Interlocked.Increment(ref _next);
                    if (item >= _count) return;
                    try
                    {
                        _body(item, worker);
                    }
                    catch (Exception e)
                    {
                        _errors[worker] = e;
                        _failed = true;
                        return;
                    }
                }
            }

            /// <summary>The lowest worker's exception, with its own type and stack, if any worker failed.</summary>
            internal void Rethrow()
            {
                foreach (Exception e in _errors)
                    if (e != null) ExceptionDispatchInfo.Capture(e).Throw();
            }
        }
    }
}
