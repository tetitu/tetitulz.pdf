// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND MIT) also carries the source of AngleSharp.Js under
// Vendor/, which keeps its own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

#nullable enable
using System;
using System.Diagnostics;
using System.Threading;

namespace Tetitulz.Pdf.Plugins.HtmlScript
{
    /// <summary>
    /// The deadline for everything one page's scripts do: a cancellation token for what can be
    /// cancelled, and a clock for the question "has it passed".
    /// </summary>
    /// <remarks>
    /// A <see cref="CancellationTokenSource"/> built with a delay cancels from a timer callback,
    /// which the thread pool runs when it has a thread to spare. On a host whose pool is busy the
    /// callback is late by as long as the pool is, and a token read alone would let scripts start
    /// long after the deadline. <see cref="Passed"/> reads the clock on the caller's own thread and
    /// cancels the token itself, so the deadline holds whenever the timer fires.
    /// </remarks>
    internal sealed class PageDeadline : IDisposable
    {
        private readonly CancellationTokenSource _source;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly TimeSpan _timeout;

        internal PageDeadline(TimeSpan timeout)
        {
            _timeout = timeout;
            _source = new CancellationTokenSource(timeout);
        }

        /// <summary>Cancelled once the deadline has passed or <see cref="Cancel"/> was called.</summary>
        internal CancellationToken Token => _source.Token;

        /// <summary>
        /// Whether the deadline has passed, by the clock; the token is cancelled on the way out when
        /// it has. A negative timeout is the infinite one and never passes by the clock.
        /// </summary>
        internal bool Passed
        {
            get
            {
                if (_source.IsCancellationRequested) return true;
                if (_timeout < TimeSpan.Zero || _clock.Elapsed < _timeout) return false;
                Cancel();
                return true;
            }
        }

        /// <summary>Cancels the token; after <see cref="Dispose"/> it does nothing.</summary>
        internal void Cancel()
        {
            try { _source.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => _source.Dispose();
    }
}
