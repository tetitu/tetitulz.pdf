// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Tetitulz.Pdf.Accessibility;
using Xunit;

namespace Tetitulz.Pdf.Accessibility.Tests;

/// <summary>
/// The accessibility diagnostics slot — <see cref="PdfAccessibility.LastDiagnostic"/> and the
/// <see cref="PdfAccessibility.DiagnosticReported"/> forwarder — as a <em>behaviour</em> rather
/// than as a member that exists.
///
/// <para>
/// CLAUDE.md requires new diagnostics to be <c>[ThreadStatic]</c> precisely because a
/// process-global slot races on a server host: a value read after one call may belong to another
/// thread's call. A test that merely reflects over the property, or that reads it after a single
/// sequential call, passes just as happily on a plain <c>static</c> field — so it proves nothing
/// about the property that actually matters.
/// </para>
///
/// <para>
/// Each test here therefore drives a <em>real</em> tolerated failure through the public API and
/// asserts on the content that comes back. The concurrency test is barriered: both threads finish
/// writing before either one reads, so a global slot cannot pass by the two calls happening not
/// to overlap.
/// </para>
/// </summary>
public class DiagnosticIsolationTests
{
    // =====================================================================
    //  Fixtures — self-contained, in-memory, ASCII
    // =====================================================================

    private static byte[] BuildPdf(IReadOnlyList<string> bodies)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.7\n");

        var offsets = new int[bodies.Count];
        for (int i = 0; i < bodies.Count; i++)
        {
            offsets[i] = sb.Length;
            sb.Append((i + 1).ToString(CultureInfo.InvariantCulture))
              .Append(" 0 obj\n").Append(bodies[i]).Append("\nendobj\n");
        }

        int xref = sb.Length;
        int size = bodies.Count + 1;
        sb.Append("xref\n0 ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append("\n0000000000 65535 f \n");
        for (int i = 0; i < offsets.Length; i++)
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:D10} 00000 n \n", offsets[i]);
        sb.Append("trailer\n<< /Size ").Append(size.ToString(CultureInfo.InvariantCulture))
          .Append(" /Root 1 0 R >>\n");
        sb.AppendFormat(CultureInfo.InvariantCulture, "startxref\n{0}\n%%EOF\n", xref);

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    /// <summary>A well-formed, parseable, untagged one-page document.</summary>
    private static byte[] UntaggedPdf() => BuildPdf(new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>",
    });

    private static byte[] SetLanguage(byte[] pdf, string tag)
    {
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfAccessibility.SetDocumentLanguage(input, output, tag);
        return output.ToArray();
    }

    private static byte[] FixHeadings(byte[] pdf)
    {
        using var input  = new MemoryStream(pdf);
        using var output = new MemoryStream();
        PdfAccessibility.FixHeadingHierarchy(input, output);
        return output.ToArray();
    }

    // Two distinct, deterministic, tolerated failures. Neither aborts its operation, so neither
    // is observable through the return value alone — which is exactly why the slot exists.
    private const string LanguageOperation = "PdfStructureTreeEditor.SetDocumentLanguage";
    private const string HeadingOperation  = "PdfStructureTreeEditor.FixHeadingHierarchy";

    // =====================================================================
    //  The slot is cleared, and written only when something really happened
    // =====================================================================

    [Fact]
    public void LastDiagnostic_AfterACleanEdit_IsNull()
    {
        // Prime the slot with a real failure so a *stale* value would be visible below.
        SetLanguage(UntaggedPdf(), "zz!!primed");
        Assert.False(string.IsNullOrEmpty(PdfAccessibility.LastDiagnostic),
            "the priming call was supposed to report a malformed language tag.");

        SetLanguage(UntaggedPdf(), "en-US");

        Assert.True(PdfAccessibility.LastDiagnostic == null,
            "a clean edit must clear the slot on entry and report nothing, but it held: " +
            PdfAccessibility.LastDiagnostic);
    }

    [Fact]
    public void LastDiagnostic_MalformedLanguageTag_NamesTheOperationAndTheOffendingValue()
    {
        byte[] input  = UntaggedPdf();
        byte[] output = SetLanguage(input, "en!!US");

        string message = PdfAccessibility.LastDiagnostic;
        Assert.False(string.IsNullOrEmpty(message),
            "writing a malformed BCP 47 tag is tolerated, so it must be reported.");
        Assert.Contains(LanguageOperation, message);
        Assert.Contains("en!!US", message);
        Assert.Contains("not a well-formed BCP 47 tag", message);

        // The operation is *tolerated*, not aborted: the document is still written.
        Assert.True(output.Length >= input.Length);
    }

    [Fact]
    public void LastDiagnostic_UntaggedDocument_ExplainsWhyTheEditDidNothing()
    {
        FixHeadings(UntaggedPdf());

        string message = PdfAccessibility.LastDiagnostic;
        Assert.False(string.IsNullOrEmpty(message),
            "\"0 headings changed\" on an untagged document must be distinguishable from " +
            "\"0 headings needed changing\".");
        Assert.Contains(HeadingOperation, message);
        Assert.Contains("has no /StructTreeRoot", message);
    }

    // =====================================================================
    //  Per-thread isolation
    // =====================================================================

    /// <summary>
    /// Two threads trigger two <em>different</em> tolerated failures at the same time and each
    /// must read back its own message. The barrier makes both writes precede either read, so the
    /// test cannot pass merely because the calls did not overlap; and this thread, which ran
    /// cleanly, must still read <c>null</c> afterwards.
    /// </summary>
    [Fact]
    public void LastDiagnostic_IsThreadStatic_ConcurrentFailuresNeverCross()
    {
        const string LanguageTag = "aa!!alpha";

        // Put this thread into the "nothing to report" state.
        SetLanguage(UntaggedPdf(), "en-US");
        Assert.True(PdfAccessibility.LastDiagnostic == null,
            "the clean run must have left this thread's slot empty.");

        using var barrier = new Barrier(2);
        var seen     = new string[2];
        var failures = new Exception[2];

        void Worker(int slot, Action operation)
        {
            try
            {
                operation();

                // Both writes land before either read; otherwise a process-global slot would
                // pass this test whenever the two operations happened to be sequential.
                if (!barrier.SignalAndWait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("the partner thread never reached the barrier.");

                seen[slot] = PdfAccessibility.LastDiagnostic;
            }
            catch (Exception ex)
            {
                // Release the partner rather than leaving it to time out on a torn run. A failure
                // to do so is folded into the report instead of being swallowed.
                try
                {
                    failures[slot] = ex;
                    barrier.RemoveParticipant();
                }
                catch (Exception releaseFailure)
                {
                    failures[slot] = new AggregateException(ex, releaseFailure);
                }
            }
        }

        var languageThread = new Thread(() => Worker(0, () => SetLanguage(UntaggedPdf(), LanguageTag)))
        {
            IsBackground = true,
            Name = "a11y-diag-language",
        };
        var headingThread = new Thread(() => Worker(1, () => FixHeadings(UntaggedPdf())))
        {
            IsBackground = true,
            Name = "a11y-diag-heading",
        };

        languageThread.Start();
        headingThread.Start();

        Assert.True(languageThread.Join(TimeSpan.FromSeconds(60)), "the language worker did not finish.");
        Assert.True(headingThread.Join(TimeSpan.FromSeconds(60)),  "the heading worker did not finish.");

        Assert.True(failures[0] == null, "the language worker threw: " + failures[0]);
        Assert.True(failures[1] == null, "the heading worker threw: "  + failures[1]);

        Assert.False(string.IsNullOrEmpty(seen[0]), "the language worker read back no diagnostic.");
        Assert.False(string.IsNullOrEmpty(seen[1]), "the heading worker read back no diagnostic.");

        // Each thread sees its own failure …
        Assert.Contains(LanguageOperation, seen[0]);
        Assert.Contains(LanguageTag,       seen[0]);
        Assert.Contains(HeadingOperation,  seen[1]);
        Assert.Contains("has no /StructTreeRoot", seen[1]);

        // … and nothing of the other's.
        Assert.DoesNotContain(HeadingOperation,  seen[0]);
        Assert.DoesNotContain(LanguageOperation, seen[1]);
        Assert.DoesNotContain(LanguageTag,       seen[1]);

        // … and neither leaks into the thread that ran cleanly.
        Assert.True(PdfAccessibility.LastDiagnostic == null,
            "a worker thread's diagnostic leaked into this thread's slot: " +
            PdfAccessibility.LastDiagnostic);
    }

    // =====================================================================
    //  The event forwarder
    // =====================================================================

    /// <summary>
    /// <see cref="PdfAccessibility.DiagnosticReported"/> is documented to fire <b>on the reporting
    /// thread</b> with the same message the slot receives. That pairing is the only way a host can
    /// observe diagnostics a single <see cref="PdfAccessibility.LastDiagnostic"/> read would miss,
    /// so it is worth pinning down.
    /// </summary>
    [Fact]
    public void DiagnosticReported_FiresOnTheReportingThreadWithTheSlotsMessage()
    {
        var captured = new List<string>();
        int workerThreadId = 0;
        var gate = new object();

        void Handler(string message)
        {
            // Other tests may be running concurrently on other threads; only this test's worker
            // is of interest, and filtering by thread id is itself part of the claim being made.
            if (Environment.CurrentManagedThreadId != Volatile.Read(ref workerThreadId)) return;
            lock (gate) captured.Add(message);
        }

        string slotInsideWorker = null;
        Exception workerFailure = null;

        PdfAccessibility.DiagnosticReported += Handler;
        try
        {
            var worker = new Thread(() =>
            {
                try
                {
                    Volatile.Write(ref workerThreadId, Environment.CurrentManagedThreadId);
                    SetLanguage(UntaggedPdf(), "cc!!charlie");
                    slotInsideWorker = PdfAccessibility.LastDiagnostic;
                }
                catch (Exception ex) { workerFailure = ex; }
            })
            {
                IsBackground = true,
                Name = "a11y-diag-event",
            };

            worker.Start();
            Assert.True(worker.Join(TimeSpan.FromSeconds(60)), "the event worker did not finish.");
            Assert.True(workerFailure == null, "the event worker threw: " + workerFailure);
        }
        finally
        {
            PdfAccessibility.DiagnosticReported -= Handler;
        }

        Assert.False(string.IsNullOrEmpty(slotInsideWorker),
            "the worker's own slot must hold the malformed-tag diagnostic.");

        List<string> onWorkerThread;
        lock (gate) onWorkerThread = new List<string>(captured);

        Assert.NotEmpty(onWorkerThread);
        Assert.True(onWorkerThread.Contains(slotInsideWorker),
            "the event must carry the same message the slot received. Slot: " + slotInsideWorker +
            " | events: " + string.Join(" || ", onWorkerThread));
        Assert.Contains("cc!!charlie", slotInsideWorker);

        // The main thread never reported anything, so its own slot is untouched by the worker.
        Assert.True(PdfAccessibility.LastDiagnostic == null ||
                    PdfAccessibility.LastDiagnostic.IndexOf("cc!!charlie", StringComparison.Ordinal) < 0,
            "the worker's diagnostic leaked onto the test thread: " + PdfAccessibility.LastDiagnostic);
    }
}
