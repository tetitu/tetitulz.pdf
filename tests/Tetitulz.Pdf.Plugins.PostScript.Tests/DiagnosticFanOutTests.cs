// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Plugins.PostScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A throwing <see cref="PostScriptDiagnostics.OnEvent"/> subscriber costs <b>only its own</b>
/// notification — it cannot blind the others.
/// </summary>
/// <remarks>
/// <para><b>Why this exists.</b> Invoking the multicast delegate in a single call inside one
/// <c>try</c> would let the first subscriber to throw abort the rest, every later listener silently
/// missing the event. The subscription is process-wide, so an unrelated concurrent handler that
/// throws would blind everyone.</para>
/// <para><b>The collector is filtered to the calling thread</b>, as
/// <c>RenderDiagnosticsFanOutTests</c>, <c>BuilderDiagnosticsFanOutTests</c> and
/// <c>Core.Util.DiagnosticFanOutTests</c> are. <c>OnEvent</c> is process-wide, so a whole-solution
/// run has other PostScript tests reporting into this list from other threads; and unsubscribing
/// does not stop a fan-out already in flight, so an unfiltered assertion could enumerate the list
/// while another thread is still adding to it — a failure that never shows alone or in a
/// single-assembly run.</para>
/// <para><b>Ordering matters to the test.</b> The throwing handler is subscribed <i>first</i>, so a
/// single-call invocation would abort before the good one ever ran. Subscribing it second would
/// pass under a single-call invocation too and prove nothing.</para>
/// </remarks>
public class DiagnosticFanOutTests
{
    [Fact]
    public void AThrowingSubscriber_DoesNotStopALaterOneFromBeingNotified()
    {
        var received = new List<string>();

        // FILTERED TO THE CALLING THREAD, which is what this file was missing and its three
        // siblings already had. `OnEvent` is process-wide, so under a whole-solution run any other
        // PostScript test reporting on another thread reaches this collector too — and an
        // unsubscribe does not stop an invocation already in flight, so the assertion below could
        // enumerate the list while another thread was still adding to it. That is the shape
        // CLAUDE.md names, and the remedy it names is this one rather than more locking:
        // "filter the handler to the calling thread; do not serialise the collection, which hides
        // the coupling instead of removing it".
        int thisThread = Environment.CurrentManagedThreadId;

        // Subscribed FIRST: under the old single-call invocation this aborted everything after it.
        void Throwing(string _)
        {
            if (Environment.CurrentManagedThreadId != thisThread) return;
            throw new InvalidOperationException("bad host handler");
        }

        // No lock: with the filter, only the calling thread ever touches the list.
        void Good(string m)
        {
            if (Environment.CurrentManagedThreadId != thisThread) return;
            received.Add(m);
        }

        PostScriptDiagnostics.OnEvent += Throwing;
        PostScriptDiagnostics.OnEvent += Good;
        try
        {
            PostScriptDiagnostics.Clear();
            RaiseOne();
        }
        finally
        {
            PostScriptDiagnostics.OnEvent -= Good;
            PostScriptDiagnostics.OnEvent -= Throwing;
        }

        // The MESSAGE, not merely that something arrived. LastError is set by Report itself,
        // independently of the event, so requiring the subscriber to have seen exactly that string
        // proves it was notified of this diagnostic rather than of anything at all.
        string reported = PostScriptDiagnostics.LastError;
        Assert.False(string.IsNullOrEmpty(reported));
        Assert.Contains(reported, received);
    }

    /// <summary>
    /// The containment half still holds, asserted by its <i>purpose</i>: a throwing subscriber must
    /// not cost the drawing. <c>Report</c> runs inside the recovery paths whose whole job is to keep
    /// a broken PostScript program from losing the artwork already emitted, so the claim worth
    /// testing is that the artwork survives — not merely that nothing escaped.
    /// </summary>
    [Fact]
    public void AThrowingSubscriber_DoesNotCostTheArtworkAlreadyEmitted()
    {
        void Throwing(string _) => throw new InvalidOperationException("bad host handler");

        PostScriptDiagnostics.OnEvent += Throwing;
        try
        {
            PostScriptDiagnostics.Clear();

            // Draws a stroked line, then divides by zero: the failure is tolerated and reported
            // (through the throwing handler), and the line must still be in the output.
            var result = PostScriptConversion.ConvertFragment(
                System.Text.Encoding.Latin1.GetBytes(
                    "newpath 10 10 moveto 100 100 lineto stroke 1 0 div"), null);

            string content = System.Text.Encoding.Latin1.GetString(result.Content ?? []);
            Assert.Contains("100 100 l", content);
            Assert.Contains("\nS\n", content);
        }
        finally { PostScriptDiagnostics.OnEvent -= Throwing; }
    }

    /// <summary>
    /// Raises exactly one diagnostic through the public conversion surface, so the test drives the
    /// real reporting path rather than an internal helper it might not share.
    /// </summary>
    private static void RaiseOne()
    {
        // `1 0 div` is a division by zero: a tolerated failure that reports and keeps going.
        // Driven through the same public entry point the rest of this suite uses, so the test
        // exercises the real reporting path rather than an internal helper it might not share.
        PostScriptConversion.ConvertFragment(
            System.Text.Encoding.Latin1.GetBytes("newpath 0 0 moveto 1 0 div"), null);
    }
}
