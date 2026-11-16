// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND MIT) also carries the source of AngleSharp.Js under
// Vendor/, which keeps its own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp;
using AngleSharp.Css;
using AngleSharp.Dom;
using Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs;
using Tetitulz.Pdf.Plugins.HtmlScript.AngleSharpJs.Dom;
using AngleSharp.Scripting;
using Jint;
using Tetitulz.Pdf.Core.Scripting;
using Tetitulz.Pdf.HtmlToPdf;

namespace Tetitulz.Pdf.Plugins.HtmlScript
{
    /// <summary>
    /// Runs a page's own JavaScript for the managed HTML engine, over AngleSharp's DOM with the
    /// Jint interpreter (AngleSharp.Js), and hands back the page as its scripts left it.
    /// </summary>
    /// <remarks>
    /// <para><b>Register it by name to turn page scripts on</b> —
    /// <c>PdfPluginHost.Instance.Register(new JintHtmlScriptPlugin())</c>; every discovery door passes
    /// it over (<see cref="Core.Plugins.PdfPluginExplicitRegistrationAttribute"/>), since it runs code
    /// the page brings. The managed engine then
    /// runs a page's scripts whenever <see cref="HtmlToPdfOptions.JavaScriptEnabled"/> is set (the
    /// default) and the page has one. Without it the engine lays out the page as written and says
    /// the scripts did not run.</para>
    ///
    /// <para><b>Page scripts are untrusted code.</b> Each runs under <see cref="HtmlScriptLimits"/> —
    /// statements, memory, time and call depth per script, and a deadline for the whole page — which
    /// the vendored AngleSharp.Js can apply only because of the one hook this repository added to it
    /// (VENDORED.md). A script's <c>&lt;script src&gt;</c> is fetched by the converter's own rules
    /// before the page opens (<see cref="ScriptInliner"/>), and the engine runs with resource loading
    /// off, so nothing a script does reaches the network or the file system.</para>
    ///
    /// <para><b>AngleSharp.Css is not optional.</b> It supplies <c>element.style</c>, the CSS object
    /// model; without it jQuery 1.x throws inside its own feature detection
    /// (<c>a.style.cssText = …</c> on a null <c>style</c>) and 3.x silently drops every
    /// <c>.css()</c>.</para>
    ///
    /// <para><b>What it cannot do, by construction:</b> there is no layout while the scripts run,
    /// so <c>offsetWidth</c>, <c>clientHeight</c>, <c>scrollTop</c> and the rest answer 0 — reported
    /// once per page, naming them (<see cref="LayoutQueryLog"/>) — and <c>getBoundingClientRect</c>
    /// is not there at all, so a script calling it throws, reported like any other; there is no
    /// <c>&lt;canvas&gt;</c> drawing. A page that needs either needs the browser engine.</para>
    /// </remarks>
    [Core.Plugins.PdfPluginExplicitRegistration]
    public sealed class JintHtmlScriptPlugin : IHtmlScriptPlugin
    {
        private readonly HtmlScriptLimits _limits;

        /// <summary>A plugin with <see cref="HtmlScriptLimits.Default"/>.</summary>
        public JintHtmlScriptPlugin() : this(HtmlScriptLimits.Default) { }

        /// <summary>A plugin with the given limits, copied — a later edit of the caller's object changes nothing.</summary>
        public JintHtmlScriptPlugin(HtmlScriptLimits limits)
        {
            if (limits == null) throw new ArgumentNullException(nameof(limits));
            _limits = limits.Clone();
        }

        /// <inheritdoc/>
        public string Name => "Tetitulz.Pdf.Plugins.HtmlScript";

        /// <inheritdoc/>
        public string Description =>
            "Runs a page's own JavaScript (AngleSharp.Js over Jint) for the managed HTML engine.";

        /// <inheritdoc/>
        public Version Version => new Version(1, 0, 0);

        /// <inheritdoc/>
        public void Initialize() { }

        /// <inheritdoc/>
        public void Shutdown() { }

        /// <inheritdoc/>
        /// <remarks>
        /// <para><b>Run off the caller's thread and waited on.</b> AngleSharp's loading is
        /// asynchronous and the managed engine is not; blocking on it directly would deadlock a caller
        /// that holds a single-threaded synchronisation context (a WinForms UI thread, classic
        /// ASP.NET), because the continuations would queue behind the very wait blocking that
        /// thread.</para>
        /// <para><b>Nothing of the page outlives the call.</b> The page deadline is cancelled on every
        /// way out and the browsing context disposed, so a timer the page left behind — a
        /// <c>setInterval</c> that never clears — stops at its next statement instead of running on
        /// in the host.</para>
        /// </remarks>
        public string RunScripts(HtmlScriptRequest request)
        {
            var timers = new TimerSettleLog();
            _timersOfThisThreadsPage = timers;
            if (request == null) throw new ArgumentNullException(nameof(request));

            string html = ScriptInliner.Inline(
                request.Html, request.LoadScript, request.Report, _limits.MaxDocumentDepth);

            using var deadline = new PageDeadline(_limits.PageTimeout);
            try
            {
                string result = Task.Run(() => RunAsync(html, request.Report, _limits, deadline, timers))
                                    .GetAwaiter().GetResult();
                if (deadline.Passed)
                    request.Report(
                        "The page's scripts ran out of time (HtmlScriptLimits.PageTimeout = " +
                        _limits.PageTimeout + "); the page is laid out as they had left it, and " +
                        "nothing they would have done after that point was done.");
                return result;
            }
            finally
            {
                // Defence in depth, and recorded as an equivalent mutation
                // (sweep-html-script-limits.ps1, L7). So are the two disposals in RunAsync: with
                // this line AND both `using`s removed, a page's busy setInterval still burns no CPU
                // once the call returns (measured per OS thread) — what stops it is inside
                // AngleSharp, not here, and ATimerThePageLeavesRunningDoesNotOutliveTheCall is what
                // would notice an upgrade that changed that. It stays so a timer body that is somehow
                // still running is cancelled at its next check rather than left to its own time limit.
                deadline.Cancel();
            }
        }

        [ThreadStatic] private static TimerSettleLog? _timersOfThisThreadsPage;

        /// <summary>
        /// The timer log of the page this thread ran most recently, for tests: the hook's liveness (how
        /// many <c>setTimeout</c> calls reached it) and whether any timer was left waited on. A test
        /// seam, not a diagnostic: replaced at the start of every <see cref="RunScripts"/>, so it never
        /// holds an earlier page's log; <c>[ThreadStatic]</c>.
        /// </summary>
        internal static TimerSettleLog? TimersOfThisThreadsPage => _timersOfThisThreadsPage;

        private static async Task<string> RunAsync(
            string html, Action<string> report, HtmlScriptLimits limits, PageDeadline pageDeadline,
            TimerSettleLog timers)
        {
            CancellationToken deadline = pageDeadline.Token;
            var layoutQueries = new LayoutQueryLog();
            using IBrowsingContext context = BrowsingContext.New(Configure(report, limits, pageDeadline, layoutQueries, timers));

            using IDocument document = await context
                .OpenAsync(response => response.Content(html))
                .ConfigureAwait(false);
            // Equivalent today, and recorded as such (sweep-html-script-plugin.ps1, P3): with every
            // script inline (ScriptInliner), OpenAsync already runs the parse, DOMContentLoaded and
            // load before it returns. Kept because it is AngleSharp's own statement of "ready".
            await document.WaitForReadyAsync().ConfigureAwait(false);

            // Timers. A zero-delay setTimeout has already run by now (measured: ten runs out of ten
            // without this window). One with a short delay — 30 ms, the "after the page settles"
            // idiom — has not: WhenStable waits only for tasks already queued, and the timer is not
            // queued until its delay passes. A bounded settle window, then stable again, runs those;
            // anything due later than HtmlScriptLimits.TimerSettleTime does not run.
            await document.WhenStable().ConfigureAwait(false);
            if (limits.TimerSettleTime > TimeSpan.Zero && !deadline.IsCancellationRequested)
            {
                long windowEnd = TimerSettleLog.After(limits.TimerSettleTime);
                try { await Task.Delay(limits.TimerSettleTime, deadline).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                await document.WhenStable().ConfigureAwait(false);

                // A timer due inside the window runs, however late the thread pool queues it: the
                // window has ended, but a timer it promised has not, so wait for that timer (the page
                // deadline still bounds the wait). Only timers set before the window ended can be due
                // by its end, so a page re-arming a zero-delay timer for ever does not hold it open.
                while (timers.AnyDueBy(windowEnd) && !deadline.IsCancellationRequested)
                {
                    timers.Waits++;
                    try { await Task.Delay(1, deadline).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                    await document.WhenStable().ConfigureAwait(false);
                }
            }

            if (layoutQueries.Describe() is string layout) report(layout);

            // The scripts may have built a tree deeper than the page they were given — one
            // innerHTML of '<span>'.repeat(100000) — and the serialiser recurses, where a stack
            // overflow ends the process. Measured again here, with the explicit stack.
            int depth = ScriptInliner.Depth(document);
            if (depth > limits.MaxDocumentDepth)
            {
                report("The page's scripts nested elements " + depth + " deep, past HtmlScriptLimits.MaxDocumentDepth (" +
                       limits.MaxDocumentDepth + "), so the page is laid out as written, without what they did: " +
                       "serialising a tree that deep could overflow the stack.");
                return html;
            }
            return document.ToHtml();
        }

        /// <summary>
        /// AngleSharp with the CSS object model and the vendored JavaScript engine under
        /// <paramref name="limits"/> — registered here rather than through <c>WithJs</c>, so the
        /// scripting service sits behind <see cref="ReportingScriptingService"/>.
        /// </summary>
        /// <remarks>
        /// <para>The three registrations are <c>JsConfigurationExtensions.WithJs</c>'s own, in its
        /// order; the only difference is the wrapper around the service that evaluates each script.
        /// The event-attribute observer and the navigation handler keep the unwrapped service,
        /// because they call into it for handlers, not for <c>&lt;script&gt;</c> evaluation.</para>
        /// <para><b>No loader is registered</b>, on purpose: with every script already inline there
        /// is nothing the page needs to fetch, and nothing a script can fetch through.</para>
        /// <para><paramref name="layoutQueries"/> is registered as a service, which is how the vendored
        /// layout getters, given only an element, find the page's log.</para>
        /// </remarks>
        private static IConfiguration Configure(
            Action<string> report, HtmlScriptLimits limits, PageDeadline pageDeadline, LayoutQueryLog layoutQueries,
            TimerSettleLog timers)
        {
            CancellationToken deadline = pageDeadline.Token;
            // MaxCallStackDepth is NOT JsScriptingOptions.MaxCallStackDepth. That one feeds Jint's
            // stack GUARD (MaxExecutionStackCount), which moves a deep call chain onto a fresh native
            // stack so it cannot crash the process — it bounds nothing. The cap on JavaScript
            // recursion is LimitRecursion (RecursionPastTheConfiguredCallDepthFails holds it); the
            // guard keeps its vendored default.
            var service = new JsScriptingService(new JsScriptingOptions
            {
                ConfigureEngine = options => options
                    .MaxStatements(limits.MaxStatementsPerScript)
                    .LimitMemory(limits.MaxMemoryBytesPerScript)
                    .TimeoutInterval(limits.ScriptTimeout)
                    .LimitRecursion(limits.MaxCallStackDepth)
                    // A regular expression runs inside one call, out of the statement timeout's
                    // reach: Jint's own regex timeout (10 s) outlasted the script's.
                    .RegexTimeoutInterval(limits.ScriptTimeout)
                    .CancellationToken(deadline),
                // Jint asks the memory limit when a statement starts, so one call of a string
                // built-in builds whatever it is asked to first — 'x'.repeat(1e9) is two gigabytes.
                // The prelude bounds those built-ins to the script's memory limit, as characters.
                PrepareEngine = engine => engine.Execute(
                    ScriptBuiltinBounds.Prelude(ScriptBuiltinBounds.CharactersFor(limits.MaxMemoryBytesPerScript))),
                // The engine's option above reaches what it evaluates as text; a script is prepared
                // and cached, and its literals keep the timeout it was prepared with.
                RegexTimeout = limits.ScriptTimeout,
            });
            return Configuration.Default
                .WithCss()
                .WithOnly(new EventAttributeObserver(service))
                .With(new JsNavigationHandler(service))
                .With(new ReportingScriptingService(service, report, limits, pageDeadline))
                .With(layoutQueries)
                .With(timers);
        }
    }
}
