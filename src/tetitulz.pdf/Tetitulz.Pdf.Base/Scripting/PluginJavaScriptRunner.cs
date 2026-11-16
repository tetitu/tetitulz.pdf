// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Core.Plugins;

namespace Tetitulz.Pdf.Base.Scripting
{
    /// <summary>
    /// The single site that dispatches a document's JavaScript to a registered
    /// <see cref="IJavaScriptPlugin"/>. Every caller in Base goes through here, so the engine
    /// cannot be live on one code path and silently absent on another — the defect class this
    /// repository has hit repeatedly with renderer features.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Base carries no <c>Jint</c> package reference, which would put a full ECMAScript interpreter
    /// in the package graph of every consumer of Base. The engine lives in
    /// <c>Tetitulz.Pdf.Plugins.JavaScript</c>; see <see cref="IJavaScriptPlugin"/> for the
    /// reasoning.
    /// </para>
    /// <para>
    /// <b>With no plugin registered nothing executes</b>, and callers say so rather than appearing
    /// to have run the script: <c>PdfActionRunner</c> adds a warning and leaves
    /// <c>PdfActionResult.JavaScriptExecuted</c> false. Registering the plugin — directly, or via
    /// <c>PdfPluginHost.Instance.RegisterFromLoadedAssemblies()</c> when the package is
    /// referenced — restores the previous behaviour exactly.
    /// </para>
    /// </remarks>
    public static class PluginJavaScriptRunner
    {
        [ThreadStatic]
        private static string _lastError;

        /// <summary>
        /// Why the most recent <see cref="TryExecute"/> on <b>this thread</b> did not run a script,
        /// or null if the last call ran one. Distinguishes "no engine is registered" from
        /// "an engine ran and the script itself failed" — the latter leaves this null and puts the
        /// exception on <see cref="PdfJsExecutionContext.Error"/>.
        /// </summary>
        /// <remarks>
        /// <c>[ThreadStatic]</c> per the Diagnostics Pattern in CLAUDE.md; read it on the thread
        /// that made the call.
        /// </remarks>
        public static string LastError
        {
            get => _lastError;
            private set => _lastError = value;
        }

        /// <summary>True when at least one <see cref="IJavaScriptPlugin"/> is registered.</summary>
        public static bool IsAvailable =>
            PdfPluginHost.Instance.GetPlugins<IJavaScriptPlugin>().Count > 0;

        // ------------------------------------------------------------------
        //  The document-wide budget
        // ------------------------------------------------------------------

        [ThreadStatic]
        private static DocumentBudget _budget;

        /// <summary>
        /// How much execution time one document's scripts share by default.
        /// </summary>
        /// <remarks>
        /// <b>The engine's own limits are per <i>call</i>, and a document has as many calls as it
        /// likes.</b> Each script gets 5 s and 32 MB inside the plugin, so a form carrying two
        /// hundred field scripts was entitled to a thousand seconds of CPU with nothing to say
        /// otherwise. That is not a defect in the sandbox — which holds up: no CLR access, no
        /// reflection, a fresh engine per call — it is the absence of an aggregate.
        /// </remarks>
        public static readonly TimeSpan DefaultDocumentBudget = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Opens a budget that every <see cref="TryExecute"/> on this thread charges against until
        /// the returned scope is disposed.
        /// </summary>
        /// <remarks>
        /// <para><b>Scoped rather than global, because "a document" is not something this class can
        /// see.</b> It is handed one script at a time; only the caller running a document's scripts
        /// knows where that document begins and ends, so the caller marks it. The
        /// <c>ExecuteDocumentScripts</c> overloads do it with
        /// <see cref="DefaultDocumentBudget"/>.</para>
        ///
        /// <para><b>Nesting restores rather than replaces.</b> An inner scope is what a caller uses
        /// to tighten the budget for one part of a document, and disposing it must not hand the
        /// outer one back an unspent budget.</para>
        ///
        /// <para><c>[ThreadStatic]</c>, like every other slot here: charge and read on the thread
        /// that runs the scripts.</para>
        /// </remarks>
        /// <param name="total">
        /// Total execution time the scripts inside the scope share. Zero or negative refuses every
        /// script, which is the way to switch scripting off for one operation without unregistering
        /// the plugin.
        /// </param>
        /// <returns>A scope; dispose it to restore whatever budget was in force before.</returns>
        public static IDisposable BeginDocumentBudget(TimeSpan total)
        {
            var previous = _budget;
            _budget = new DocumentBudget(total);
            return new BudgetScope(previous);
        }

        /// <summary>
        /// Execution time left in the innermost open budget, or <c>null</c> when none is open.
        /// </summary>
        public static TimeSpan? RemainingDocumentBudget => _budget?.Remaining;

        private sealed class DocumentBudget
        {
            internal DocumentBudget(TimeSpan total) => Remaining = total;

            internal TimeSpan Remaining { get; private set; }

            internal bool IsExhausted => Remaining <= TimeSpan.Zero;

            internal void Charge(TimeSpan spent) =>
                Remaining = Remaining > spent ? Remaining - spent : TimeSpan.Zero;
        }

        private sealed class BudgetScope : IDisposable
        {
            private readonly DocumentBudget _previous;
            private bool _disposed;

            internal BudgetScope(DocumentBudget previous) => _previous = previous;

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _budget = _previous;
            }
        }

        /// <summary>
        /// Runs <paramref name="script"/> against <paramref name="context"/> using the first
        /// registered <see cref="IJavaScriptPlugin"/>.
        /// </summary>
        /// <returns>
        /// <c>true</c> if an engine handled the script. <c>false</c> when no plugin is registered,
        /// when every registered plugin declined, or when a plugin threw — with the reason on
        /// <see cref="LastError"/>. A script that ran and itself threw returns <c>true</c>; that
        /// failure is on <see cref="PdfJsExecutionContext.Error"/>.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        public static bool TryExecute(string script, PdfJsExecutionContext context)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));

            // Reset at the start of every call, on every path: a caller reads this *after* an
            // operation, so a stale value from a previous document would answer the wrong
            // question. (CLAUDE.md records a decryption bug caused by exactly that omission.)
            LastError = null;

            // Charged before dispatch, so an exhausted budget costs nothing at all rather than one
            // more 5-second script. A refusal here is reported like every other: the caller can
            // tell "the budget ran out" from "no engine is registered" and from "the script threw".
            DocumentBudget budget = _budget;
            if (budget != null && budget.IsExhausted)
            {
                LastError =
                    "The document's shared JavaScript budget is exhausted, so this script was not " +
                    "executed. The engine's own limits are per call; this one is per document.";
                return false;
            }

            var plugins = PdfPluginHost.Instance.GetPlugins<IJavaScriptPlugin>();
            if (plugins.Count == 0)
            {
                LastError =
                    "No IJavaScriptPlugin is registered, so the script was not executed. " +
                    "Reference Tetitulz.Pdf.Plugins.JavaScript and register it (directly, or via " +
                    "PdfPluginHost.Instance.RegisterFromLoadedAssemblies()).";
                return false;
            }

            // Every attempt is charged, including one that throws or declines: a plugin that spends
            // four seconds and then fails has still spent four seconds of the document's budget,
            // and charging only successes would let a script buy unlimited time by failing.
            var spent = System.Diagnostics.Stopwatch.StartNew();
            try
            {
            foreach (var plugin in plugins)
            {
                try
                {
                    if (plugin.TryExecute(script, context))
                    {
                        // Cleared before returning true. An earlier plugin that THREW left its
                        // message in the slot, so a later plugin running the script successfully
                        // returned true with LastError still set - contradicting this property's
                        // own contract ("null if the last call ran one") and making a caller that
                        // follows it log a failure for a script that executed and mutated the
                        // form. A per-plugin fault is still visible while it matters: it is what
                        // the slot holds if nothing ends up running the script.
                        LastError = null;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    // A misbehaving plugin must not abort the caller's operation; record why and
                    // offer the script to the next one.
                    LastError = $"{plugin.Name} threw {ex.GetType().Name}: {ex.Message}";
                }
            }

            LastError ??= "Every registered IJavaScriptPlugin declined the script.";
            return false;
            }
            finally
            {
                budget?.Charge(spent.Elapsed);
            }
        }
    }
}
