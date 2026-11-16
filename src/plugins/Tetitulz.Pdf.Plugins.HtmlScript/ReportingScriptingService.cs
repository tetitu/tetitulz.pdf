// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND MIT) also carries the source of AngleSharp.Js under
// Vendor/, which keeps its own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Io;
using AngleSharp.Scripting;
using Jint.Runtime;

namespace Tetitulz.Pdf.Plugins.HtmlScript
{
    /// <summary>
    /// Runs each <c>&lt;script&gt;</c> through AngleSharp.Js's service and reports the one that
    /// throws or hits a limit, so the failure reaches the caller instead of vanishing.
    /// </summary>
    /// <remarks>
    /// <para><b>A throwing script is the page's own error</b>, handled the way a browser handles it:
    /// the scripts after it still run and the page is still laid out. AngleSharp.Js behaves that way
    /// but raises neither <c>window.onerror</c> nor an <c>error</c> event, so without this wrapper
    /// the failure would be silent, and a page missing half of what its scripts do would report
    /// nothing.</para>
    /// <para><b>A limit says which limit.</b> "A script threw" would send a caller looking for a bug
    /// in the page when the answer is a setting on <see cref="HtmlScriptLimits"/>.</para>
    /// <para><b>Scope:</b> this sees each script as it is evaluated. A handler that throws later —
    /// inside a <c>DOMContentLoaded</c> listener or a timer — is invoked by the event loop, not
    /// here; the page deadline is reported by the plugin, which is the one place that knows it
    /// passed.</para>
    /// </remarks>
    internal sealed class ReportingScriptingService : IScriptingService
    {
        private readonly IScriptingService _inner;
        private readonly Action<string> _report;
        private readonly HtmlScriptLimits _limits;
        private readonly PageDeadline _deadline;

        /// <summary>What the page's scripts have allocated together so far, and whether the page cap was reported.</summary>
        private long _pageAllocated;
        private bool _pageMemoryReported;

        /// <summary>
        /// The bytes this thread has allocated so far, or -1 where the runtime cannot say.
        /// <c>GC.GetAllocatedBytesForCurrentThread</c> is not in netstandard2.0's surface; a .NET Core host
        /// has it, and without it every script is charged the whole per-script limit, as the form
        /// engine charges its scripts.
        /// </summary>
        private static readonly Func<long> AllocatedBytes = FindAllocatedBytes();

        private static Func<long> FindAllocatedBytes()
        {
            var method = typeof(GC).GetMethod("GetAllocatedBytesForCurrentThread", Type.EmptyTypes);
            if (method == null) return () => -1;
            return (Func<long>)method.CreateDelegate(typeof(Func<long>));
        }

        internal ReportingScriptingService(
            IScriptingService inner, Action<string> report, HtmlScriptLimits limits, PageDeadline deadline)
        {
            _inner = inner;
            _report = report;
            _limits = limits;
            _deadline = deadline;
        }

        public bool SupportsType(string mimeType) => _inner.SupportsType(mimeType);

        public async Task EvaluateScriptAsync(IResponse response, ScriptOptions options, CancellationToken cancel)
        {
            // Past the page deadline a script is not started at all. Jint's own cancellation check
            // is amortised — made every so many statements — so a short script started after the
            // deadline ran to its end before any check: the one-line script after four hundred
            // stopped ones still ran (ThePageDeadlineStopsManyIndividuallyCheapScripts caught it).
            // The deadline is asked by the clock: the token alone is cancelled by a timer callback,
            // which a busy thread pool delivers late.
            if (_deadline.Passed) return;

            // Past the page's memory cap a script is not started either: Jint's limit is per script,
            // and the page's one engine keeps what each script left in its globals.
            if (_pageAllocated > _limits.MaxMemoryBytesPerPage)
            {
                if (!_pageMemoryReported)
                {
                    _pageMemoryReported = true;
                    _report("The page's scripts have allocated " + _pageAllocated +
                            " bytes together, past HtmlScriptLimits.MaxMemoryBytesPerPage (" + _limits.MaxMemoryBytesPerPage +
                            " bytes); the scripts after it did not run.");
                }
                return;
            }

            // Measured on the thread the script runs on: an inline script's content is read
            // synchronously, so the evaluation stays on this thread; a reading that cannot be trusted
            // (another thread, no counter) charges the whole per-script limit.
            long before = AllocatedBytes();
            try
            {
                await _inner.EvaluateScriptAsync(response, options, cancel).ConfigureAwait(false);
            }
            catch (ExecutionCanceledException)
            {
                // The page deadline passed: every later script lands here. The plugin reports the
                // deadline once for the page, so this adds nothing per script.
            }
            catch (Exception ex) when (!(ex is OperationCanceledException))
            {
                _report(Describe(ex));
            }
            finally
            {
                long after = AllocatedBytes();
                _pageAllocated += before >= 0 && after >= before ? after - before : _limits.MaxMemoryBytesPerScript;
            }
        }

        private string Describe(Exception ex) => ex switch
        {
            StatementsCountOverflowException =>
                "A page script was stopped at its statement limit (HtmlScriptLimits.MaxStatementsPerScript = " +
                _limits.MaxStatementsPerScript + "); the scripts after it still ran.",
            MemoryLimitExceededException =>
                "A page script was stopped at its memory limit (HtmlScriptLimits.MaxMemoryBytesPerScript = " +
                _limits.MaxMemoryBytesPerScript + " bytes); the scripts after it still ran.",
            TimeoutException =>
                "A page script was stopped at its time limit (HtmlScriptLimits.ScriptTimeout = " +
                _limits.ScriptTimeout + "); the scripts after it still ran.",
            _ =>
                "A page script threw and was skipped; the scripts after it still ran (" +
                ex.GetType().Name + ": " + ex.Message + ").",
        };
    }
}
