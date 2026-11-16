// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND MIT) also carries the source of AngleSharp.Js under
// Vendor/, which keeps its own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using AngleSharp.Dom;
using Tetitulz.Pdf.Core.Util;

namespace Tetitulz.Pdf.Plugins.HtmlScript
{
    /// <summary>
    /// The page's <c>setTimeout</c> timers that have not run, each with when it is due — so the settle
    /// window (<see cref="HtmlScriptLimits.TimerSettleTime"/>) can wait for every timer due inside it to
    /// have run, rather than sleeping for its length and hoping each had been queued by then.
    /// </summary>
    /// <remarks>
    /// <para><b>Why the window cannot just sleep.</b> AngleSharp runs a timer as a <c>Task.Delay</c>
    /// whose continuation queues the callback on the page's event loop, and keeps no list of them. A
    /// sleep of the window's length followed by "wait for what is queued" would race the thread
    /// pool: on a loaded machine the continuation of a 30 ms timer can still be waiting for a thread
    /// when the window's own continuation queues the final stable point, and the page would be laid
    /// out without it — its output depending on how busy the host is
    /// (<c>AShortDelayTimerInsideTheSettleWindowTakesEffect</c>).</para>
    /// <para><b>Told by the vendored binding.</b> Every call from a page into the DOM goes through
    /// <c>EngineExtensions.Call</c>; for <see cref="IWindowTimers.SetTimeout"/> and
    /// <see cref="IWindowTimers.ClearTimeout"/> it asks <see cref="Intercept"/>, which records the
    /// timer and wraps its callback so the record is removed when it runs — however it ends. Nothing
    /// a page's script can see changes: the binding hands AngleSharp the same arguments' meaning and
    /// the page the same handle. Registered on the page's browsing context, as
    /// <see cref="LayoutQueryLog"/> is, and locked: callbacks run on the event loop's thread.</para>
    /// <para><c>setInterval</c> is not tracked: an interval is never "done", and its first firing
    /// inside the window keeps the race it always had.</para>
    /// </remarks>
    internal sealed class TimerSettleLog
    {
        private readonly object _gate = new object();
        private readonly Dictionary<object, long> _dueAt = new Dictionary<object, long>();
        private readonly Dictionary<int, object> _byHandle = new Dictionary<int, object>();

        /// <summary>How many times the settle window waited on a timer still due inside it.</summary>
        internal int Waits { get; set; }

        /// <summary>How many <c>setTimeout</c> calls the binding reported: the hook's liveness.</summary>
        internal int Seen { get; private set; }

        /// <summary>How many of them are still recorded as not run.</summary>
        internal int Pending
        {
            get { lock (_gate) return _dueAt.Count; }
        }

        /// <summary>A monotonic timestamp <paramref name="delay"/> from now.</summary>
        internal static long After(TimeSpan delay)
            => Stopwatch.GetTimestamp() + SaturatingCast.ToLong(Math.Max(0, delay.TotalMilliseconds) * Stopwatch.Frequency / 1000.0);

        /// <summary>Records a timer due <paramref name="delayMs"/> from now; the token its callback reports with.</summary>
        internal object Scheduled(int delayMs)
        {
            var token = new object();
            long due = After(TimeSpan.FromMilliseconds(Math.Max(0, delayMs)));
            lock (_gate) { _dueAt[token] = due; Seen++; }
            return token;
        }

        /// <summary>The page's handle for the timer behind <paramref name="token"/>, which <c>clearTimeout</c> names.</summary>
        internal void Handle(object token, int handle)
        {
            lock (_gate)
                if (_dueAt.ContainsKey(token)) _byHandle[handle] = token;
        }

        /// <summary>The timer behind <paramref name="token"/> has run.</summary>
        internal void Ran(object token)
        {
            lock (_gate) _dueAt.Remove(token);
        }

        /// <summary>The page cleared the timer it holds as <paramref name="handle"/>.</summary>
        internal void Cleared(int handle)
        {
            lock (_gate)
            {
                if (!_byHandle.TryGetValue(handle, out object? token)) return;
                _byHandle.Remove(handle);
                _dueAt.Remove(token);
            }
        }

        /// <summary>Whether a timer due at or before <paramref name="timestamp"/> has not run.</summary>
        internal bool AnyDueBy(long timestamp)
        {
            lock (_gate)
            {
                foreach (long due in _dueAt.Values)
                    if (due <= timestamp) return true;
                return false;
            }
        }

        /// <summary>
        /// Called by the vendored binding before it invokes a DOM method for a page: for
        /// <c>setTimeout</c>, records the timer, wraps its callback in <paramref name="parameters"/>
        /// and returns what to do with the handle the call returns; for <c>clearTimeout</c>, records
        /// the clearing. Anything else, or a page with no log, is left alone (null).
        /// </summary>
        internal static Action<object?>? Intercept(MethodInfo method, object? target, object?[] parameters)
        {
            if (method.DeclaringType != typeof(IWindowTimers)) return null;
            TimerSettleLog? log = (target as IWindow)?.Document?.Context?.GetService<TimerSettleLog>();
            if (log == null) return null;

            if (method.Name == nameof(IWindowTimers.ClearTimeout))
            {
                if (parameters.Length == 1 && parameters[0] is int cleared) log.Cleared(cleared);
                return null;
            }
            if (method.Name != nameof(IWindowTimers.SetTimeout) || parameters.Length != 2
                || !(parameters[0] is Action<IWindow> callback))
                return null;

            object token = log.Scheduled(parameters[1] is int delay ? delay : 0);
            parameters[0] = (Action<IWindow>)(window =>
            {
                try { callback(window); }
                finally { log.Ran(token); }
            });
            return handle => { if (handle is int id) log.Handle(token, id); };
        }
    }
}
