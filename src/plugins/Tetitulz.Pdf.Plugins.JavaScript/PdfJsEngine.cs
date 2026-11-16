// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core;
using Tetitulz.Pdf.Core.Scripting;
using Jint;
using Jint.Native;
using Jint.Runtime;

namespace Tetitulz.Pdf.Plugins.JavaScript
{
    /// <summary>
    /// Managed JavaScript engine for PDF form scripting: Jint 4 under a time, memory and recursion
    /// limit, running the form object model of Adobe's <i>JavaScript for Acrobat API Reference</i> —
    /// <c>app</c>, the document, fields, <c>event</c>, <c>util</c>, <c>color</c>, <c>console</c>,
    /// <c>global</c>, the constant objects and the <c>AF</c> functions — as Acrobat answers it
    /// (<c>AcrobatObjectModel.js</c>; GAPS.md §25).
    /// </summary>
    /// <remarks>
    /// <para><b>The global object is the document</b>, as in Acrobat: a script runs at the top level,
    /// the document's members are globals, and <c>this</c> in a plain function is the document.
    /// Contexts sharing a <see cref="PdfJsDocumentScope"/> run in one environment, so a
    /// document-level function is visible to the scripts after it; a context without one gets an
    /// environment of its own.</para>
    /// <para><b>What reaches outside the process is recorded, never done</b> — a URL, mail, a
    /// submission, a file, a timer, a menu item — on <see cref="LastUnsupportedRequest"/>.</para>
    /// </remarks>
    public sealed class PdfJsEngine
    {
        private static readonly TimeSpan DefaultTimeout     = TimeSpan.FromSeconds(5);
        private static readonly long     DefaultMemoryLimit = 32 * 1024 * 1024;
        private const           int      DefaultRecursionLimit = 64;

        /// <summary>
        /// What one document's scripts may allocate together, across the executions that share a
        /// <see cref="PdfJsDocumentScope"/>; past it, every further script of the document is
        /// refused, failing closed.
        /// </summary>
        /// <remarks>
        /// The engine's own limit is per execution and measures what one execution allocates, so an
        /// environment kept across executions could hold their sum. Allocation bounds what is kept:
        /// nothing is retained that was not allocated. Settable for a test; a process sets it once.
        /// </remarks>
        internal static long DocumentMemoryLimit { get; set; } = DefaultDocumentMemoryLimit;

        /// <summary>The default of <see cref="DocumentMemoryLimit"/>: eight executions' worth.</summary>
        internal const long DefaultDocumentMemoryLimit = 8L * 32 * 1024 * 1024;

        // ================================================================
        //  What a script asked for and did not get
        // ================================================================

        // What the last script asked for that this engine accepted and did not apply. Cleared at
        // the top of Execute — [ThreadStatic] per CLAUDE.md, and per-OPERATION reset, so a request
        // recorded by one document's script cannot be read as this one's.
        [ThreadStatic] private static string _lastUnsupportedRequest;

        /// <summary>
        /// What the last script on this thread asked for that this engine does not carry into the
        /// document — a field property it cannot apply (<c>readonly</c>, <c>hidden</c>, a colour), a
        /// call it does not make (<c>app.launchURL</c>, <c>this.submitForm</c>, a timer) — or
        /// <c>null</c> when it asked for none.
        /// </summary>
        /// <remarks>
        /// Without this the omission would be invisible: a script that sets a computed total
        /// read-only and then writes its value produces a successful-looking result — the value
        /// reaches <c>ModifiedFieldValues</c> — while the protection the form depends on is
        /// dropped. Reporting beats both silence and throwing: throwing would abort a calculate
        /// script that is otherwise doing useful work.
        /// </remarks>
        public static string LastUnsupportedRequest =>
            _unsupportedRecorded > MaxRecordedUnsupportedRequests
                ? _lastUnsupportedRequest + "; (" +
                  (_unsupportedRecorded - MaxRecordedUnsupportedRequests).ToString(CultureInfo.InvariantCulture) +
                  " further request(s) not recorded)"
                : _lastUnsupportedRequest;

        internal static void RecordUnsupported(string request)
        {
            // Capped, because the SCRIPT decides how many times this runs: the first
            // MaxRecordedUnsupportedRequests are kept verbatim and the rest only counted — the note
            // saying so is composed when the report is read, so a request past the cap costs a
            // count, not a copy of the report.
            _unsupportedRecorded++;
            if (_unsupportedRecorded > MaxRecordedUnsupportedRequests) return;
            // Each request is cut to a length too: it carries the field name the script passed,
            // and a name of megabytes, recorded 32 times into a report that is copied at each
            // addition, made a report of gigabytes the script's memory limit never saw.
            if (request != null && request.Length > MaxRecordedRequestChars)
                request = request.Substring(0, MaxRecordedRequestChars) + "…";
            _lastUnsupportedRequest = _lastUnsupportedRequest == null
                ? request
                : _lastUnsupportedRequest + "; " + request;
        }

        internal const int MaxRecordedUnsupportedRequests = 32;

        /// <summary>The characters of one recorded request kept; the rest are cut.</summary>
        internal const int MaxRecordedRequestChars = 200;

        [ThreadStatic] private static int _unsupportedRecorded;

        // ================================================================
        //  Execution
        // ================================================================

        /// <summary>
        /// Executes a PDF JavaScript string against the given context.
        /// Field values are read from and written back to <paramref name="ctx"/>.
        /// </summary>
        public static void Execute(string script, PdfJsExecutionContext ctx)
        {
            // Reset first, on every path: an empty script reports nothing, not the previous script's request.
            _lastUnsupportedRequest = null;
            _unsupportedRecorded = 0;

            if (string.IsNullOrEmpty(script) || ctx == null) return;

            Session session;
            try { session = SessionFor(ctx); }
            catch (Exception ex) { FailClosed(ctx, ex, "the script environment could not be built"); return; }

            lock (session.Gate)
            {
                if (session.Allocated > DocumentMemoryLimit)
                {
                    FailClosed(ctx, new PdfException(
                        "The document's scripts have allocated " + session.Allocated.ToString(CultureInfo.InvariantCulture) +
                        " bytes together, past their limit of " + DocumentMemoryLimit.ToString(CultureInfo.InvariantCulture) + "."),
                        "the document's scripts exceeded their memory limit");
                    return;
                }

                long before = AllocatedBytes();
                try
                {
                    Run(session, script, ctx);
                }
                catch (TimeoutException ex)            { FailClosed(ctx, ex, "the script exceeded its time limit"); }
                catch (MemoryLimitExceededException ex){ FailClosed(ctx, ex, "the script exceeded its memory limit"); }
                catch (JavaScriptException ex)         { FailClosed(ctx, ex, "the script raised an error"); }
                catch (Exception ex)                   { FailClosed(ctx, ex, "the script could not be executed"); }
                finally
                {
                    long after = AllocatedBytes();
                    session.Allocated += after >= before && before >= 0 ? after - before : DefaultMemoryLimit;
                }
            }
        }

        private static void Run(Session session, string script, PdfJsExecutionContext ctx)
        {
            JsHost host = session.Host;
            host.Begin(ctx);
            session.Engine.Invoke(session.Begin, EventJson(ctx));
            session.Engine.Execute(script);
            session.Engine.Invoke(session.End);

            if (host.ResultValue != null) ctx.EventValue = host.ResultValue;
            ctx.EventChange      = host.ResultChange;    // a Keystroke script may rewrite what was typed
            ctx.ValidationPassed = host.ResultRc && ctx.ValidationPassed;
            if (!ctx.ValidationPassed && ctx.ValidationMessage == null)
                ctx.ValidationMessage = "Validation failed.";
            if (!string.IsNullOrEmpty(ctx.EventFieldName) && host.ResultValue != null)
                ctx.ModifiedFieldValues[ctx.EventFieldName] = host.ResultValue;
            // A write to a field through getField wins over the event's value for the same field.
            foreach (var kv in host.PendingValues)
                ctx.ModifiedFieldValues[kv.Key] = kv.Value;
        }

        /// <summary>The event the object model builds for one execution.</summary>
        private static string EventJson(PdfJsExecutionContext ctx)
        {
            string value = ctx.EventValue ?? string.Empty;
            int length = value.Length;
            bool given = ctx.EventSelStart >= 0 && ctx.EventSelStart <= length
                         && ctx.EventSelEnd >= ctx.EventSelStart && ctx.EventSelEnd <= length;
            var sb = new StringBuilder(64 + value.Length);
            sb.Append("{\"fieldName\":"); JsHost.AppendJsonString(sb, ctx.EventFieldName ?? string.Empty);
            sb.Append(",\"name\":"); JsHost.AppendJsonString(sb, ctx.EventName ?? string.Empty);
            sb.Append(",\"type\":"); JsHost.AppendJsonString(sb, ctx.EventType ?? string.Empty);
            sb.Append(",\"value\":"); JsHost.AppendJsonString(sb, value);
            sb.Append(",\"change\":"); JsHost.AppendJsonString(sb, ctx.EventChange ?? string.Empty);
            sb.Append(",\"selStart\":").Append((given ? ctx.EventSelStart : length).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"selEnd\":").Append((given ? ctx.EventSelEnd : length).ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"willCommit\":").Append(ctx.EventWillCommit ? "true" : "false");
            return sb.Append('}').ToString();
        }

        /// <summary>
        /// Records a script failure on <paramref name="ctx"/> and — the point of this method —
        /// leaves <see cref="PdfJsExecutionContext.ValidationPassed"/> <c>false</c>.
        /// </summary>
        /// <remarks>
        /// <para><b>This is a fail-closed gate.</b> <c>ValidationPassed</c> defaults to
        /// <c>true</c>; assigned only on the success path, it would stay <c>true</c> for a script
        /// that set <c>event.rc = false</c> and then timed out (or threw), and a host doing the
        /// documented <c>if (ctx.ValidationPassed) commit(value);</c> would commit the value the
        /// script rejected. A script that did not complete has approved nothing, whatever it
        /// assigned before it stopped.</para>
        /// <para><see cref="PdfJsExecutionContext.ValidationMessage"/> is only filled in when the
        /// script did not already set one (through <c>AFRange_Validate</c>, say): the script's own
        /// reason is more useful to a user than "execution failed".</para>
        /// </remarks>
        private static void FailClosed(PdfJsExecutionContext ctx, Exception ex, string what)
        {
            ctx.Error            = ex;
            ctx.ValidationPassed = false;
            if (ctx.ValidationMessage == null)
                ctx.ValidationMessage = "Validation could not be completed: " + what + " (" +
                                        ex.GetType().Name + ": " + ex.Message + ").";
        }

        // ================================================================
        //  The environment
        // ================================================================

        /// <summary>One engine with the object model loaded, and what its document's scripts have spent.</summary>
        private sealed class Session
        {
            internal readonly object Gate = new object();
            internal Engine Engine;
            internal JsHost Host;
            internal JsValue Begin;
            internal JsValue End;
            internal long Allocated;
        }

        private static Session SessionFor(PdfJsExecutionContext ctx)
        {
            PdfJsDocumentScope scope = ctx.DocumentScope;
            if (scope == null) return NewSession();
            lock (scope)
            {
                if (scope.EngineState is Session existing) return existing;
                Session created = NewSession();
                scope.EngineState = created;
                return created;
            }
        }

        private static Session NewSession()
        {
            var engine = new Engine(opts =>
            {
                opts.LimitMemory(DefaultMemoryLimit);
                opts.TimeoutInterval(DefaultTimeout);
                opts.CatchClrExceptions();
                // Bound recursion so runaway JS (e.g. `function f(){f();}f();`)
                // raises a catchable RecursionDepthOverflowException instead of
                // a native StackOverflowException that would crash the host
                // before the memory/timeout limits can fire.
                opts.LimitRecursion(DefaultRecursionLimit);
                // A regular expression runs inside one call, so the statement-level timeout
                // cannot stop it: Jint's own regex timeout (10 s) outlasted the script's.
                opts.RegexTimeoutInterval(DefaultTimeout);
            });

            // One call of a string built-in allocates whatever it builds before the memory limit
            // is asked (Core.Scripting.ScriptBuiltinBounds); the prelude bounds them first, the
            // object model's own calls included.
            engine.Execute(BoundsPrelude.Value);

            var host = new JsHost();
            engine.SetValue("__tetitulzHost", host);
            engine.Execute(ObjectModel.Value);
            var session = new Session
            {
                Engine = engine,
                Host = host,
                Begin = engine.GetValue("__tetitulzBegin"),
                End = engine.GetValue("__tetitulzEnd"),
            };
            // The engine keeps the two functions; no document script can call them.
            engine.Execute("delete this.__tetitulzBegin; delete this.__tetitulzEnd;");
            return session;
        }

        private static readonly Lazy<string> BoundsPrelude = new Lazy<string>(
            () => ScriptBuiltinBounds.Prelude(ScriptBuiltinBounds.CharactersFor(DefaultMemoryLimit)));

        private static readonly Lazy<string> ObjectModel = new Lazy<string>(() =>
        {
            const string name = "Tetitulz.Pdf.Plugins.JavaScript.AcrobatObjectModel.js";
            using Stream stream = typeof(PdfJsEngine).GetTypeInfo().Assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException("The form object model resource " + name + " is missing.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });

        // GC.GetAllocatedBytesForCurrentThread is not in netstandard2.0's surface; a .NET Core host
        // has it, and without it every execution is charged the whole per-execution limit.
        private static readonly Func<long> AllocatedBytes = FindAllocatedBytes();

        private static Func<long> FindAllocatedBytes()
        {
            MethodInfo method = typeof(GC).GetTypeInfo().GetDeclaredMethod("GetAllocatedBytesForCurrentThread");
            if (method == null) return () => -1;
            return (Func<long>)method.CreateDelegate(typeof(Func<long>));
        }
    }
}
