// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Base.Operations
{
    /// <summary>
    /// Diagnostics sink for document-editing machinery in <c>Tetitulz.Pdf.Base</c> that tolerates a
    /// failure and carries on — object indexing, incremental revision writing, retroactive font
    /// embedding.
    /// </summary>
    /// <remarks>
    /// <para><b>Why a second sink alongside <c>RenderDiagnostics</c>.</b> That one is a *render*
    /// sink: its <c>RenderDiagnosticKind</c> enumerates ways a page can be drawn wrongly, and a host
    /// subscribes to it to learn about pixels. Editing failures are a different question with a
    /// different audience, and folding them in would force every render-telemetry consumer to filter
    /// out events about font programs. CLAUDE.md's diagnostics convention explicitly allows a
    /// feature-scoped sink for exactly this case.</para>
    ///
    /// <para><b>Why it exists at all.</b> This machinery moved down from
    /// <c>Tetitulz.Pdf.Accessibility</c> so that the Conformance plugin could reach it without the
    /// banned plugin → feature reference (plan 04 D2). It reported through that assembly's
    /// <c>AccessibilityDiagnostics</c>, which cannot follow it here — Base may not reference a
    /// feature. Accessibility forwards this sink into its own, so
    /// <c>PdfAccessibility.LastDiagnostic</c> and <c>DiagnosticReported</c> still observe every
    /// event they observed before the move.</para>
    ///
    /// <para>Follows the repository-wide convention: a <see cref="ThreadStaticAttribute"/> slot
    /// behind a read-only property, because a process-wide static races across concurrent
    /// operations and is hostile to server hosts. Read it on the thread that ran the operation.</para>
    /// </remarks>
    public static class DocumentEditDiagnostics
    {
        [ThreadStatic]
        private static string _lastError;

        /// <summary>
        /// The most recent problem reported on the <b>current thread</b>, or <c>null</c> if nothing
        /// has been reported since the last <see cref="Clear"/>. Inspect this after an operation
        /// returns an empty or unchanged result, to tell "nothing to do" from "something failed and
        /// was tolerated".
        /// </summary>
        public static string LastError => _lastError;

        /// <summary>
        /// Raised for every <see cref="Report"/> call, on the reporting thread. Never raised for a
        /// successful operation. Each subscriber is invoked on its own — see <see cref="Report"/> —
        /// so a throwing one is contained and costs only its own notification.
        /// </summary>
        public static event Action<string> OnEvent;

        /// <summary>Records a tolerated failure or anomaly.</summary>
        /// <param name="operation">
        /// Short, stable identifier of the failing step, conventionally <c>"Type.Method"</c>
        /// (e.g. <c>"PdfObjectIndex.Build"</c>). Used as the message prefix.
        /// </param>
        /// <param name="detail">What went wrong, and what the code did instead.</param>
        /// <param name="ex">The exception that was caught, if any.</param>
        public static void Report(string operation, string detail, Exception ex = null)
        {
            string message = Compose(operation, detail, ex);
            _lastError = message;

            // Captured once: the field could be reassigned by another thread between a null check
            // and an invoke if it were read twice.
            // ONE implementation of subscriber fan-out, in Core: invoking the multicast list
            // in a single call means the first subscriber to throw skips every later one, and a
            // try/catch around it contains the exception without fixing the delivery.
            DiagnosticFanOut.Invoke(OnEvent, message);
        }

        /// <summary>
        /// Clears the current thread's <see cref="LastError"/>. Call at the start of a public
        /// operation so a stale message from an earlier, unrelated call is never mistaken for a
        /// diagnosis of this one — per-thread storage is not per-document scope.
        /// </summary>
        public static void Clear()
        {
            _lastError = null;
        }

        // One implementation, in Core.Util.DiagnosticMessage — four sinks carried this,
        // identical apart from the default operation name below.
        private static string Compose(string operation, string detail, Exception ex)
            => Tetitulz.Pdf.Core.Util.DiagnosticMessage.Compose("DocumentEdit", operation, detail, ex);
    }
}
