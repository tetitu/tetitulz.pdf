// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.DocxToPdf.Rendering
{
    /// <summary>
    /// Diagnostics sink for the DOCX → PDF renderer.
    ///
    /// <para>
    /// A converter tolerates a great many partial failures — a drawing it cannot lay out, a
    /// preset geometry it approximates, a colour it cannot resolve, a glyph no available font
    /// covers. Every one of those looks, in the output, exactly like content the source document
    /// never contained. This type is what makes the two distinguishable.
    /// </para>
    ///
    /// <para>
    /// A per-feature sink rather than a set of <c>Last*</c> properties, because a single
    /// <c>RenderDrawing</c> call routinely tolerates several distinct failures at once (an
    /// unknown preset <i>and</i> an ignored effect <i>and</i> an unsupported fill), and a
    /// <c>Last*</c> slot keeps only the last. See CLAUDE.md → Diagnostics Pattern, item 3;
    /// <c>Tetitulz.Pdf.Accessibility.Internal.AccessibilityDiagnostics</c> is the reference
    /// implementation this mirrors.
    /// </para>
    ///
    /// <para>
    /// The slot is <see cref="ThreadStaticAttribute"/>: a value read after one conversion must
    /// never belong to another thread's conversion. New diagnostics slots in this repository are
    /// required to be thread-static.
    /// </para>
    /// </summary>
    internal static class DocxDiagnostics
    {
        [ThreadStatic]
        private static string _lastError;

        /// <summary>
        /// The most recent anomaly reported on the <b>current thread</b>, or <c>null</c> if
        /// nothing has been reported since the last <see cref="Clear"/>. Inspect this after a
        /// conversion to tell "the document had no such content" from "the content was there and
        /// something about it could not be rendered".
        /// </summary>
        internal static string LastError => _lastError;

        /// <summary>
        /// Raised for every <see cref="Report"/> call, on the reporting thread, so a host can
        /// forward conversion anomalies to logging or telemetry. Never raised for content that
        /// rendered as the source described it.
        ///
        /// <para>
        /// Each subscriber is invoked on its own (see <see cref="Report"/>), so a throwing one is
        /// contained and costs only its own notification. Hosts outside this assembly subscribe
        /// through the public <see cref="DocxToPdfConverter.DiagnosticReported"/> forwarder.
        /// </para>
        /// </summary>
        internal static event Action<string> OnEvent;

        /// <summary>
        /// Records a tolerated failure, approximation or omission.
        /// </summary>
        /// <param name="operation">
        /// Short, stable identifier of the step — conventionally <c>"Type.Method"</c>
        /// (e.g. <c>"DrawingRenderer.Fallback"</c>). Used as the message prefix.
        /// </param>
        /// <param name="detail">What could not be rendered, and what the renderer did instead.</param>
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
        /// Clears the current thread's <see cref="LastError"/>. Called at the start of a
        /// conversion, on every path: per-thread storage is not per-document scope, so without
        /// this a caller reading the slot after converting document B could be handed document
        /// A's message.
        /// </summary>
        internal static void Clear()
        {
            _lastError = null;
        }

        // One implementation, in Core.Util.DiagnosticMessage — four sinks carried this,
        // identical apart from the default operation name below.
        private static string Compose(string operation, string detail, Exception ex)
            => Tetitulz.Pdf.Core.Util.DiagnosticMessage.Compose("DocxToPdf", operation, detail, ex);
    }
}
