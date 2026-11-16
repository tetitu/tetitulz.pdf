// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Plugins.PostScript
{
    /// <summary>
    /// Diagnostics sink for the PostScript plugin.
    ///
    /// <para><b>Why this feature gets its own sink</b> (CLAUDE.md → Diagnostics Pattern, case 3):
    /// one conversion routinely tolerates <i>many</i> distinct failures — an undefined name, three
    /// unimplemented operators, a refused image, a truncated stream — and a single <c>Last*</c> slot
    /// keeps only the last one. A converter that returns plausible-looking output after fifty
    /// recoveries must be distinguishable from one that ran clean, which is what
    /// <see cref="ReportCount"/> and <see cref="UnimplementedOperators"/> are for.</para>
    ///
    /// <para><b>Public, unlike the accessibility sink it is modelled on.</b> That one is forwarded
    /// through <c>PdfAccessibility</c>; this plugin has no facade in Base to forward through — Base
    /// must never reference a plugin — so the sink itself is the host's way in.</para>
    ///
    /// <para><b>Every slot is <see cref="ThreadStaticAttribute"/></b>, as the repo requires: a value
    /// read after one call must never belong to another thread's call.</para>
    /// </summary>
    public static class PostScriptDiagnostics
    {
        [ThreadStatic] private static string _lastError;
        [ThreadStatic] private static List<string> _unimplemented;
        [ThreadStatic] private static int _reportCount;

        /// <summary>
        /// The most recent tolerated failure on the <b>current thread</b>, or <c>null</c> when
        /// nothing has been reported since the last <see cref="Clear"/>. Inspect it after a
        /// conversion returns empty or partial output to tell "there was nothing to draw" from
        /// "something failed and was tolerated". Past <see cref="PsLimits.MaxReportsPerConversion"/>
        /// reports it is the one saying the rest were suppressed.
        /// </summary>
        public static string LastError => _lastError;

        /// <summary>
        /// PostScript operators encountered but not implemented, deduplicated, in first-seen order,
        /// capped at <see cref="PsLimits.MaxRecordedUnimplemented"/>. A non-empty list means the
        /// drawing is missing something the program asked for.
        ///
        /// <para><b>Two kinds of entry, and the difference matters when you read one.</b> Most come
        /// from <c>PsSystemDict</c>'s arity table — an operator this interpreter recognises,
        /// consumes the operands of, and does not perform. The rest come from an <b>undefined
        /// name</b> that cost a whole top-level object, which is the more serious of the two: the
        /// operands were <i>not</i> consumed, so everything after it in that object ran against a
        /// desynchronised stack. Such an entry is normally a name this interpreter lacks, but it can
        /// also be a name the program itself never defined; <see cref="LastError"/> carries the
        /// wording that tells them apart. Names probed with <c>where</c> / <c>known</c>, and names
        /// raised inside <c>{ … } stopped</c>, never appear here — a probe answering "no" is the
        /// mechanism working, not a gap.</para>
        /// </summary>
        public static IReadOnlyList<string> UnimplementedOperators =>
            (IReadOnlyList<string>)_unimplemented ?? Array.Empty<string>();

        /// <summary>
        /// How many failures were tolerated on this thread since the last <see cref="Clear"/>.
        /// Zero means the conversion ran clean.
        /// </summary>
        public static int ReportCount => _reportCount;

        /// <summary>
        /// Raised on the reporting thread for every <see cref="Report"/>, so a host can forward
        /// conversion anomalies to logging or telemetry. A throwing subscriber is contained and
        /// <b>costs only its own notification</b>: subscribers are invoked one at a time, so one bad
        /// handler cannot blind the others.
        /// </summary>
        ///
        /// <remarks>
        /// <b>The subscription is process-wide, unlike every other slot on this type.</b>
        /// <see cref="LastError"/>, <see cref="ReportCount"/> and
        /// <see cref="UnimplementedOperators"/> are <c>[ThreadStatic]</c>, so each conversion reads
        /// only its own; a handler attached here receives reports from <i>every</i> thread, and the
        /// message carries no conversion identity. A server converting concurrently therefore sees
        /// one interleaved stream it cannot attribute — subscribe for aggregate telemetry, and read
        /// the thread-static properties when the question is "what happened to <i>this</i> call?".
        /// Filter on <c>Thread.CurrentThread.ManagedThreadId</c> inside the handler if a subscriber
        /// needs per-conversion isolation; the event is raised synchronously on the reporting
        /// thread, so that works. This is a real cost of one shared sink, not an oversight — a
        /// per-conversion event would mean threading a context object through every operator.
        /// </remarks>
        public static event Action<string> OnEvent;

        /// <summary>
        /// Clears this thread's slots.
        ///
        /// <para><b>Must be called at the start of every public entry point, on every path.</b>
        /// Per-thread storage is not per-document scope, and the two are easy to confuse: CLAUDE.md
        /// records a decryption bug where a <c>[ThreadStatic]</c> slot was still wrong because it
        /// was written only on one code path, so a later document read the previous document's
        /// verdict.</para>
        /// </summary>
        public static void Clear()
        {
            _lastError    = null;
            _unimplemented = null;
            _reportCount  = 0;
            _oversizedNumericTokenReported = false;
        }

        /// <summary>Records a tolerated failure or anomaly.</summary>
        /// <param name="operation">
        /// Short, stable identifier of the failing step — conventionally <c>"Type.Method"</c>
        /// (e.g. <c>"PsScanner.ScanLiteralString"</c>). Used as the message prefix.
        /// </param>
        /// <param name="detail">What went wrong, and what the code did instead.</param>
        /// <param name="ex">The exception that was caught, if any.</param>
        /// <remarks>
        /// Capped at <see cref="PsLimits.MaxReportsPerConversion"/> a conversion: past it a report is
        /// counted and nothing more, after one that says so, which then stays in <see cref="LastError"/>.
        /// </remarks>
        internal static void Report(string operation, string detail, Exception ex = null)
        {
            if (_reportCount > PsLimits.MaxReportsPerConversion)
            {
                _reportCount++;
                return;
            }
            if (_reportCount == PsLimits.MaxReportsPerConversion)
            {
                operation = "PostScriptDiagnostics";
                detail = $"more than {PsLimits.MaxReportsPerConversion} reports; the rest of this conversion's " +
                         "are counted in ReportCount and not delivered";
                ex = null;
            }
            string message = Compose(operation, detail, ex);
            _lastError = message;
            _reportCount++;

            // Each subscriber is invoked SEPARATELY, and that is load-bearing rather than a nicety:
            // one throwing handler must not cost the others their notification. Core.Util.DiagnosticFanOut
            // owns the rule for every sink in the tree.
            DiagnosticFanOut.Invoke(OnEvent, message);
        }

        /// <summary>
        /// Records an operator the interpreter recognises but does not implement. Deduplicated, so
        /// an operator inside a loop is recorded once, and capped so a hostile file cannot make the
        /// diagnostics the attack.
        /// </summary>
        internal static void ReportUnimplemented(string psOperatorName)
        {
            if (string.IsNullOrEmpty(psOperatorName)) return;

            _unimplemented ??= new List<string>();
            if (_unimplemented.Count >= PsLimits.MaxRecordedUnimplemented) return;
            if (_unimplemented.Contains(psOperatorName)) return;

            _unimplemented.Add(psOperatorName);
            Report("PsInterpreter", $"operator '{psOperatorName}' is not implemented; " +
                                    "its operands were consumed and execution continued");
        }

        /// <summary>
        /// Records an executable name that resolved to nothing and took a whole top-level object
        /// with it. Shares <see cref="UnimplementedOperators"/> with
        /// <see cref="ReportUnimplemented"/>, and therefore its deduplication and its cap.
        ///
        /// <para>Called only from <c>PsInterpreter.Run</c>'s catch, so a name that an enclosing
        /// <c>stopped</c> handled — the shape every feature probe takes — is not recorded.</para>
        /// </summary>
        internal static void ReportUndefinedName(string name)
        {
            if (string.IsNullOrEmpty(name)) return;

            // Deduplicated including the Report, exactly as ReportUnimplemented is: an undefined
            // name can only fire once per top-level object, but a file of a million such objects
            // would otherwise compose a million messages and fire OnEvent a million times, making
            // the diagnostics themselves the attack.
            _unimplemented ??= new List<string>();
            if (_unimplemented.Count >= PsLimits.MaxRecordedUnimplemented) return;
            if (_unimplemented.Contains(name)) return;

            _unimplemented.Add(name);

            Report("PsInterpreter",
                $"'{name}' is not defined in this interpreter; unlike a recognised-but-unimplemented " +
                "operator its operands were NOT consumed, so the rest of the top-level object was " +
                "abandoned rather than run against a desynchronised stack");
        }

        /// <summary>
        /// Records that a token was too long for the number parser to examine, <b>once</b>.
        ///
        /// <para>One-shot on purpose. This fires during <i>scanning</i>, once per offending token,
        /// and <see cref="Report"/> is not deduplicated, and its cap is a thousand, not one —
        /// <see cref="ReportUnimplemented"/> is deduplicated. A file of a million over-long tokens would
        /// otherwise produce a million composed messages and a million <see cref="OnEvent"/>
        /// invocations, making the diagnostics themselves the attack.</para>
        /// </summary>
        internal static void ReportOversizedNumericToken(int length)
        {
            if (_oversizedNumericTokenReported) return;
            _oversizedNumericTokenReported = true;

            Report("PsNumberParser",
                $"a {length}-character token exceeds the {PsLimits.MaxNumericTokenChars}-character " +
                "limit the number parser examines, so it was treated as a name; if it was meant as " +
                "a number, whatever uses it will raise 'undefined'. Reported once per conversion.");
        }

        [ThreadStatic] private static bool _oversizedNumericTokenReported;

        // One implementation, in Core.Util.DiagnosticMessage — four sinks carried this,
        // identical apart from the default operation name below.
        private static string Compose(string operation, string detail, Exception ex)
            => Tetitulz.Pdf.Core.Util.DiagnosticMessage.Compose("PostScript", operation, detail, ex);
    }
}
