// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;
using Tetitulz.Pdf.Base.Operations;

namespace Tetitulz.Pdf.Accessibility.Internal
{
    /// <summary>
    /// Diagnostics sink for the Accessibility feature.
    ///
    /// <para>
    /// Failure-tolerant code paths in this assembly (structure-tree walking, incremental
    /// writing, reading-order analysis, auto-tagging, artifact marking) must never swallow
    /// an exception silently: a caught-and-dropped failure is indistinguishable from
    /// "the document was clean", which is exactly the bug class this type exists to close.
    /// Every <c>catch</c> that intentionally continues calls <see cref="Report"/>.
    /// </para>
    ///
    /// <para>
    /// Follows the repository-wide diagnostics convention (see CLAUDE.md → Diagnostics
    /// Pattern): a <see cref="ThreadStaticAttribute"/> <c>_lastError</c> slot exposed through
    /// a read-only property, so a caller that got a <c>null</c> / empty / "no issues" result
    /// back can inspect why. The slot is thread-static — the process-wide statics used by
    /// some older <c>Last*</c> diagnostics race across concurrent operations and are hostile
    /// to server hosts.
    /// </para>
    /// </summary>
    internal static class AccessibilityDiagnostics
    {
        // Thread-static: a value read after one call must never belong to another
        // thread's call.  See CLAUDE.md — new diagnostics slots are required to be
        // [ThreadStatic]; PdfTextExtractor._lastFormXObjectError is the reference pattern.
        [ThreadStatic]
        private static string _lastError;

        /// <summary>
        /// Subscribes to Base's document-edit sink, so machinery that <b>moved down into Base</b>
        /// (plan 04 D2 — <c>PdfObjectIndex</c>, the incremental revision writer, the font-embedding
        /// remediator) is still observed here.
        /// </summary>
        /// <remarks>
        /// <para>Without this, moving that code would have silently emptied
        /// <see cref="PdfAccessibility.LastDiagnostic"/> and
        /// <see cref="PdfAccessibility.DiagnosticReported"/> of a whole class of event — a
        /// behaviour regression invisible to any test that only checks results, and precisely the
        /// "a check that quietly stopped running looks like a check that found nothing" failure this
        /// codebase keeps finding.</para>
        ///
        /// <para><b>The ordering requirement is real.</b> The subscription is live only once this
        /// type has been touched. Every public entry point in <see cref="PdfAccessibility"/> calls
        /// <see cref="Clear"/> before doing any work, which is what guarantees that — the same
        /// reason <c>DocxDiagnostics.Clear()</c> runs at the top of <c>RenderToPdf</c>. Do not
        /// remove those calls on the grounds that the slot "starts null anyway".</para>
        ///
        /// <para>It forwards rather than replaces: Base's sink keeps its own
        /// <c>LastError</c> for callers of Base operations who never touch this assembly.</para>
        /// </remarks>
        static AccessibilityDiagnostics()
        {
            DocumentEditDiagnostics.OnEvent += message =>
            {
                _lastError = message;

                // Same fan-out as Report: forwarding a Base diagnostic must not let one throwing
                // subscriber skip the others either.
                DiagnosticFanOut.Invoke(OnEvent, message);
            };
        }

        /// <summary>
        /// The most recent problem reported on the <b>current thread</b>, or <c>null</c> if
        /// nothing has been reported since the last <see cref="Clear"/> (or since this thread
        /// started). Inspect this after an operation returns an empty / unchanged result to
        /// distinguish "nothing to do" from "something went wrong and was tolerated".
        /// </summary>
        internal static string LastError => _lastError;

        /// <summary>
        /// Raised for every <see cref="Report"/> call, on the reporting thread, so a host can
        /// forward accessibility anomalies to logging or telemetry. Never raised for a
        /// successful operation.
        ///
        /// <para>
        /// A throwing subscriber is contained (see <see cref="Report"/>) rather than allowed to
        /// escape, but it still costs the remaining subscribers their notification, so handlers
        /// should not throw.
        /// </para>
        ///
        /// <para>
        /// Hosts outside this assembly subscribe through the public
        /// <see cref="PdfAccessibility.DiagnosticReported"/> forwarder.
        /// </para>
        /// </summary>
        internal static event Action<string> OnEvent;

        /// <summary>
        /// Records a tolerated failure or anomaly.
        /// </summary>
        /// <param name="operation">
        /// Short, stable identifier of the failing step — conventionally
        /// <c>"Type.Method"</c> (e.g. <c>"PdfObjectIndex.Build"</c>). Used as the message prefix.
        /// </param>
        /// <param name="detail">What went wrong, and what the code did instead.</param>
        /// <param name="ex">The exception that was caught, if any.</param>
        internal static void Report(string operation, string detail, Exception ex = null)
        {
            string message = Compose(operation, detail, ex);
            _lastError = message;

            // Capture once: the field could be reassigned by another thread between the
            // null check and the invoke if we read it twice.
            // ONE implementation of subscriber fan-out, in Core: invoking the multicast list
            // in a single call means the first subscriber to throw skips every later one, and a
            // try/catch around it contains the exception without fixing the delivery.
            DiagnosticFanOut.Invoke(OnEvent, message);
        }

        /// <summary>
        /// Clears the current thread's <see cref="LastError"/>. Call at the start of a public
        /// operation so a stale message from an earlier, unrelated call is never mistaken for
        /// a diagnosis of this one.
        /// </summary>
        internal static void Clear()
        {
            _lastError = null;
        }

        // One implementation, in Core.Util.DiagnosticMessage — four sinks carried this,
        // identical apart from the default operation name below.
        private static string Compose(string operation, string detail, Exception ex)
            => Tetitulz.Pdf.Core.Util.DiagnosticMessage.Compose("Accessibility", operation, detail, ex);
    }
}
