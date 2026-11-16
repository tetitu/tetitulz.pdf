// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Builder.Export;
using Xunit;

namespace Tetitulz.Pdf.Builder.Tests;

/// <summary>
/// <see cref="BuilderDiagnostics.Report"/> delivers to every subscriber, and a subscriber that
/// throws costs only its own notification.
/// </summary>
/// <remarks>
/// <para>This sink had the <c>try</c>/<c>catch</c> and still had the defect, which is the half that
/// survives review: <c>try { OnEvent?.Invoke(d); } catch { }</c> contains a throwing handler's
/// exception but does <b>not</b> fix the delivery, because one multicast invoke skips every
/// subscriber after the one that threw. The exception is gone and so are the notifications, and
/// nothing in the code says so.</para>
///
/// <para>An export raises several distinct diagnostics — a missing font, then a missing glyph,
/// then an undecodable image — which is the reason this type is a sink rather than a
/// <c>LastError</c> slot. A host that aggregates telemetry naturally attaches more than one
/// handler, and that is exactly the shape the defect needs.</para>
/// </remarks>
public sealed class BuilderDiagnosticsFanOutTests
{
    /// <summary>
    /// The throwing subscriber is registered first, so a single multicast invoke never reaches the
    /// collector — and the collector must receive the diagnostic itself, not merely a count.
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotBlindTheOnesAfterIt()
    {
        const string Sentinel = "builder-fan-out-sentinel-a";
        int thisThread = Environment.CurrentManagedThreadId;
        var received = new List<string>();

        void Throwing(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId != thisThread || d.Detail != Sentinel) return;
            throw new InvalidOperationException("a host telemetry handler that throws");
        }

        void Collector(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId != thisThread || d.Detail != Sentinel) return;
            lock (received) received.Add($"{d.Kind}|{d.Detail}|{d.Exception?.Message}");
        }

        // The event is process-wide, so the handlers filter on this thread and on this test's own
        // sentinel: an unfiltered one makes the assertion a claim about the whole test host.
        BuilderDiagnostics.OnEvent += Throwing;
        BuilderDiagnostics.OnEvent += Collector;
        try
        {
            BuilderDiagnostics.Report(
                BuilderDiagnosticKind.UnsupportedByExportFormat,
                Sentinel,
                new InvalidOperationException("the reported cause"));
        }
        finally
        {
            BuilderDiagnostics.OnEvent -= Collector;
            BuilderDiagnostics.OnEvent -= Throwing;
        }

        Assert.Equal(
            new[] { $"{BuilderDiagnosticKind.UnsupportedByExportFormat}|{Sentinel}|the reported cause" },
            received);
    }

    /// <summary>
    /// A throwing subscriber does not escape <c>Report</c> into the export that called it, and the
    /// <c>[ThreadStatic]</c> slot is still written — the two things a caller relies on when it
    /// tolerates a failure and carries on.
    ///
    /// <para>Asserted by the reporter <b>continuing</b>, through a second <c>Report</c> that runs
    /// only if the first returned.</para>
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotEscapeToTheReporter()
    {
        const string First = "builder-fan-out-sentinel-b1";
        const string Second = "builder-fan-out-sentinel-b2";
        int thisThread = Environment.CurrentManagedThreadId;

        void Throwing(BuilderDiagnostic d)
        {
            if (Environment.CurrentManagedThreadId != thisThread) return;
            if (d.Detail == First || d.Detail == Second)
                throw new InvalidOperationException("a host telemetry handler that throws");
        }

        BuilderDiagnostics.OnEvent += Throwing;
        try
        {
            BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat, First);
            BuilderDiagnostics.Report(BuilderDiagnosticKind.UnsupportedByExportFormat, Second);
        }
        finally
        {
            BuilderDiagnostics.OnEvent -= Throwing;
        }

        // The second Report is unreachable if the first let the exception out, so this asserts
        // both that Report returned and that it still recorded what it was told.
        Assert.Equal(Second, BuilderDiagnostics.LastError?.Detail);
    }
}
