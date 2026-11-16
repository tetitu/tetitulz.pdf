// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;

namespace Tetitulz.Pdf.Core.Util
{
    /// <summary>
    /// Delivers one diagnostic message to every subscriber of a multicast handler, so that a
    /// subscriber which throws costs <b>only its own notification</b>.
    ///
    /// <para><b>This exists because the obvious code is wrong.</b> <c>handler(message)</c> invokes
    /// the whole multicast list in a single call, so the first subscriber to throw aborts the walk and
    /// every later subscriber never hears the event at all. Wrapping that call in
    /// <c>try</c>/<c>catch</c> contains the exception but does <b>not</b> fix the delivery: the later
    /// subscribers are still skipped.</para>
    ///
    /// <para><b>Both halves matter most for <c>RenderDiagnostics.Report</c>.</b> A bare
    /// <c>OnEvent?.Invoke(kind, detail, ex)</c> would let a throwing host handler both blind every
    /// later subscriber <i>and</i> escape into the recovery path that reports the anomaly — turning a
    /// tolerated failure into a failed render. It is also the most widely subscribed sink in the
    /// tree.</para>
    ///
    /// <para>The cost is not theoretical. These subscriptions are process-wide, so an unrelated
    /// concurrent host or test handler that throws would silently blind every other listener — a
    /// collector correct in every respect, thread-filtered, locked, per-instance and cleanly
    /// disposed, would simply never receive a diagnostic it asked for, and pass when run
    /// alone.</para>
    ///
    /// <para><b>Swallowing is deliberate.</b> Every caller reports from inside a recovery path whose
    /// whole purpose is to keep a broken document from costing the rest of the work. Letting a
    /// host's logging callback throw out of there would convert every tolerated failure into an
    /// escaping exception — strictly worse than losing one telemetry event.</para>
    /// </summary>
    public static class DiagnosticFanOut
    {
        /// <summary>
        /// Invokes each subscriber of <paramref name="handler"/> separately. Null is a no-op, so a
        /// caller need not null-check first.
        /// </summary>
        /// <param name="handler">
        /// A snapshot of the event field. Callers must capture it into a local before calling:
        /// the field can be reassigned by another thread between a null check and the invoke.
        /// </param>
        /// <param name="message">The composed diagnostic message.</param>
        public static void Invoke(Action<string> handler, string message)
        {
            if (handler == null) return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try { ((Action<string>)subscriber)(message); }
                catch { /* see the type remarks: one bad handler costs only its own notification */ }
            }
        }

        /// <summary>
        /// The same rule for a sink whose event carries a payload type of its own rather than a
        /// composed string — <c>BuilderDiagnostics</c> raises an <c>Action&lt;BuilderDiagnostic&gt;</c>.
        /// </summary>
        /// <remarks>
        /// The <see cref="Invoke(Action{string}, string)"/> overload above is kept rather than
        /// deleted in favour of this one: it is in the published Core API surface, so replacing it
        /// would be a binary break for a consumer that has already compiled against it. Overload
        /// resolution prefers the non-generic form, so the existing call sites are unchanged.
        /// </remarks>
        /// <param name="handler">
        /// A snapshot of the event field. Callers must capture it into a local — or pass the field
        /// directly as this argument, which reads it exactly once — rather than null-checking it
        /// and then invoking it, which reads it twice and races with a concurrent unsubscribe.
        /// </param>
        /// <param name="message">The diagnostic to deliver.</param>
        public static void Invoke<T>(Action<T> handler, T message)
        {
            if (handler == null) return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try { ((Action<T>)subscriber)(message); }
                catch { /* see the type remarks: one bad handler costs only its own notification */ }
            }
        }

        /// <summary>
        /// The same rule for a sink whose event carries several arguments — <c>RenderDiagnostics</c>
        /// raises an <c>Action&lt;RenderDiagnosticKind, string, Exception&gt;</c>, and it is both the
        /// most widely subscribed sink in the tree and the one that had neither half of this rule:
        /// it invoked the whole multicast list in one call <b>and</b> did not catch, so a throwing
        /// host handler escaped <c>Report</c> into the recovery path that called it.
        /// </summary>
        /// <param name="handler">A snapshot of the event field; see the single-argument overload.</param>
        /// <param name="arg1">First event argument.</param>
        /// <param name="arg2">Second event argument.</param>
        /// <param name="arg3">Third event argument.</param>
        public static void Invoke<T1, T2, T3>(Action<T1, T2, T3> handler, T1 arg1, T2 arg2, T3 arg3)
        {
            if (handler == null) return;

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try { ((Action<T1, T2, T3>)subscriber)(arg1, arg2, arg3); }
                catch { /* see the type remarks: one bad handler costs only its own notification */ }
            }
        }
    }
}
