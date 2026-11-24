// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>
    /// Composes the one-line text a diagnostic sink stores in its <c>Last*</c> slot and hands to
    /// its subscribers: <c>[operation] detail — ExceptionType: Message</c>.
    ///
    /// <para>One composition for every sink — the em-dash separator, the <c>(no detail)</c>
    /// fallback — with the one thing that differs between them, the default operation name, as
    /// the first argument.</para>
    /// </summary>
    /// <remarks>
    /// <b>It sits beside <see cref="DiagnosticFanOut"/> because it is the other half of the same
    /// method.</b> Each sink's <c>Report</c> already read
    /// <c>Compose(...) → _lastError → DiagnosticFanOut.Invoke(...)</c>, with the fan-out shared and
    /// the composition copied; this closes that.
    ///
    /// <para><b>Core, because the four callers span three rings</b> — a feature
    /// (<c>Accessibility</c>, <c>DocxToPdf</c>), <c>Base</c>, and a plugin
    /// (<c>Plugins.PostScript</c>, whose only project reference is Core). Pointing three sinks at a
    /// fourth peer's static would be both a ring violation and the type-initialization edge that
    /// crashed the Base test host once. No static fields here, so this type has no initializer;
    /// each sink keeps its own <c>[ThreadStatic]</c> slot and its own <c>OnEvent</c>, which is what
    /// makes this a downward share rather than a merge of the sinks themselves.</para>
    ///
    /// <para><b>One near-miss is deliberately excluded.</b>
    /// <c>Plugins.Conformance.PdfXValidator.Report</c> emits the identical shape inline but has
    /// NEITHER fallback, so it separates on degenerate input: a null operation gives <c>[]</c>
    /// rather than <c>[PDF/X]</c>, and a null detail gives a trailing space rather than
    /// <c>(no detail)</c>. Folding it in would be a behaviour change wearing a consolidation's
    /// clothes.</para>
    /// </remarks>
    public static class DiagnosticMessage
    {
        /// <summary>Stands in for an empty <c>detail</c>, so a message is never a bare tag.</summary>
        private const string NoDetail = "(no detail)";

        /// <summary>
        /// The composed message.
        /// </summary>
        /// <param name="defaultOperation">
        /// The sink's own name, used when <paramref name="operation"/> is null or empty — e.g.
        /// <c>"Accessibility"</c>, <c>"DocxToPdf"</c>, <c>"DocumentEdit"</c>, <c>"PostScript"</c>.
        /// </param>
        /// <param name="operation">The operation that failed, or null for the sink default.</param>
        /// <param name="detail">What happened, or null for <c>(no detail)</c>.</param>
        /// <param name="ex">The tolerated exception, or null when there was none.</param>
        public static string Compose(string defaultOperation, string operation, string detail, Exception ex)
        {
            string op = string.IsNullOrEmpty(operation) ? defaultOperation : operation;
            string what = string.IsNullOrEmpty(detail) ? NoDetail : detail;

            // string.Concat over a small fixed set — cheaper than a StringBuilder or an
            // interpolated-string handler for the 2- and 4-part cases below.
            if (ex == null)
                return string.Concat("[", op, "] ", what);

            return string.Concat("[", op, "] ", what, " — ",
                string.Concat(ex.GetType().Name, ": ", ex.Message));
        }
    }
}
