// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// <see cref="RenderDiagnostics.Report"/> delivers to every subscriber, and a subscriber that
/// throws costs only its own notification.
/// </summary>
/// <remarks>
/// <para>This sink was a bare <c>OnEvent?.Invoke(kind, detail, ex)</c> — wrong in <b>two</b> ways at
/// once, and the only one of the seven sinks in the tree with both. It invoked the whole multicast
/// list in a single call, so the first subscriber to throw skipped every later one; and it did not
/// catch, so that exception escaped <c>Report</c> altogether.</para>
///
/// <para><b>Both halves are worse here than anywhere else, because of who subscribes.</b>
/// <c>RenderDiagnostics.OnEvent</c> is process-wide and is the most widely subscribed sink in this
/// repo — CLAUDE.md records three separate intermittent test failures caused by handlers on it,
/// one of them a collector that was correct in every respect and simply never received an event
/// another subscriber's throw had aborted. And nearly every call site is inside a <c>catch</c>
/// whose whole purpose is to keep a broken document from costing the rest of the render, so an
/// escaping exception turns a tolerated anomaly into a failed page — decided by a host's logging
/// callback rather than by the document.</para>
///
/// <para><b>Why this went unfound for two rounds.</b> The other sinks each wrapped their invoke in
/// <c>try</c>/<c>catch</c>, which is the shape a reader greps for when auditing this rule. The one
/// with no <c>catch</c> at all does not match that search, so the sink with the worst version of
/// the defect was the last one found. Searching for the <i>remedy</i> finds the near-misses;
/// searching for the <i>shape of the delivery</i> finds the rest.</para>
/// </remarks>
public sealed class RenderDiagnosticsFanOutTests
{
    /// <summary>
    /// The test the pre-fix sink would have failed: the throwing subscriber is registered first, so
    /// a single multicast invoke never reaches the collector.
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotBlindTheOnesAfterIt()
    {
        const string Sentinel = "render-fan-out-sentinel-a";
        int thisThread = Environment.CurrentManagedThreadId;
        var received = new List<RenderDiagnosticKind>();

        void Throwing(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != thisThread || detail != Sentinel) return;
            throw new InvalidOperationException("a host logging callback that throws");
        }

        void Collector(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != thisThread || detail != Sentinel) return;
            lock (received) received.Add(kind);
        }

        // Filtered to this thread AND to this test's own sentinel detail, because the event is
        // process-wide: an unfiltered handler here would make the assertion a claim about whatever
        // else the test host happened to be rendering.
        RenderDiagnostics.OnEvent += Throwing;
        RenderDiagnostics.OnEvent += Collector;
        try
        {
            RenderDiagnostics.Report(RenderDiagnosticKind.MeshBatchSplit, Sentinel);
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Collector;
            RenderDiagnostics.OnEvent -= Throwing;
        }

        Assert.Equal(new[] { RenderDiagnosticKind.MeshBatchSplit }, received);
    }

    /// <summary>
    /// All three arguments reach the surviving subscriber unchanged. A fan-out that delivered the
    /// event but dropped the detail or the exception would satisfy a subscriber count on its own,
    /// and the detail is the only thing that tells two anomalies of one kind apart.
    /// </summary>
    [Fact]
    public void TheSurvivingSubscriberReceivesKindDetailAndException()
    {
        const string Sentinel = "render-fan-out-sentinel-b";
        int thisThread = Environment.CurrentManagedThreadId;
        var boom = new InvalidOperationException("the reported cause");
        var received = new List<string>();

        void Throwing(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != thisThread || detail != Sentinel) return;
            throw new InvalidOperationException("a host logging callback that throws");
        }

        void Collector(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != thisThread || detail != Sentinel) return;
            lock (received) received.Add($"{kind}|{detail}|{ex?.Message}");
        }

        RenderDiagnostics.OnEvent += Throwing;
        RenderDiagnostics.OnEvent += Collector;
        try
        {
            RenderDiagnostics.Report(RenderDiagnosticKind.MeshBatchSplit, Sentinel, boom);
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Collector;
            RenderDiagnostics.OnEvent -= Throwing;
        }

        Assert.Equal(
            new[] { $"{RenderDiagnosticKind.MeshBatchSplit}|{Sentinel}|the reported cause" },
            received);
    }

    /// <summary>
    /// A throwing subscriber does not escape <c>Report</c> into the recovery path that called it.
    ///
    /// <para>Asserted by the reporter <b>continuing</b> — by a second <c>Report</c> that only runs
    /// if the first returned — rather than by the absence of a throw, which a body that called
    /// nothing would satisfy equally well.</para>
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotEscapeToTheReporter()
    {
        const string Sentinel = "render-fan-out-sentinel-c";
        int thisThread = Environment.CurrentManagedThreadId;
        var received = new List<int>();

        void Throwing(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != thisThread || detail != Sentinel) return;
            throw new InvalidOperationException("a host logging callback that throws");
        }

        void Collector(RenderDiagnosticKind kind, string detail, Exception ex)
        {
            if (Environment.CurrentManagedThreadId != thisThread || detail != Sentinel) return;
            lock (received) received.Add(received.Count);
        }

        RenderDiagnostics.OnEvent += Throwing;
        RenderDiagnostics.OnEvent += Collector;
        try
        {
            RenderDiagnostics.Report(RenderDiagnosticKind.MeshBatchSplit, Sentinel);
            RenderDiagnostics.Report(RenderDiagnosticKind.MeshBatchSplit, Sentinel);
        }
        finally
        {
            RenderDiagnostics.OnEvent -= Collector;
            RenderDiagnostics.OnEvent -= Throwing;
        }

        // Two, not one: the second Report is unreachable if the first let the exception out.
        Assert.Equal(new[] { 0, 1 }, received);
    }
}
