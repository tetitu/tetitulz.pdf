// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// Mixed licences: this package (Apache-2.0 AND MIT) also carries the source of AngleSharp.Js under
// Vendor/, which keeps its own licence. See NOTICE.md at the repository root.
// SPDX-License-Identifier: Apache-2.0

#nullable enable
using System;

namespace Tetitulz.Pdf.Plugins.HtmlScript
{
    /// <summary>
    /// What a page's scripts may consume. A page's scripts are untrusted code, so every one of these
    /// is a guard, and a page that reaches one is laid out as its scripts had left it at that point,
    /// with the limit reported.
    /// </summary>
    /// <remarks>
    /// <para><b>Per script, and per page.</b> Jint resets its statement, memory and time counters for
    /// every script it runs, so those three bound one script — a page of a thousand cheap scripts
    /// would pass all three. Two bound the page: <see cref="PageTimeout"/>, a deadline for everything
    /// its scripts do, handlers and timers included, after which nothing of the page's runs again; and
    /// <see cref="MaxMemoryBytesPerPage"/>, what its <c>&lt;script&gt;</c>s may allocate together,
    /// since a time bound does not bound what a hundred quick scripts each keep.</para>
    ///
    /// <para>Values are taken as given; a non-positive one is not a way to turn a limit off.</para>
    /// </remarks>
    public sealed class HtmlScriptLimits
    {
        /// <summary>The defaults: generous for real pages (jQuery's own start-up included), fatal to runaway ones.</summary>
        public static HtmlScriptLimits Default => new HtmlScriptLimits();

        /// <summary>Statements one script (or one handler call) may execute. Default 10 000 000.</summary>
        public int MaxStatementsPerScript { get; set; } = 10_000_000;

        /// <summary>Bytes one script may allocate. Default 64 MiB.</summary>
        public long MaxMemoryBytesPerScript { get; set; } = 64L * 1024 * 1024;

        /// <summary>
        /// Bytes the page's <c>&lt;script&gt;</c>s may allocate together. Default 256 MiB.
        /// </summary>
        /// <remarks>
        /// <para>Jint's memory limit is reset for every script, and the page's one engine keeps what
        /// each script put in its globals: a hundred scripts each holding a string under the per-script
        /// limit held gigabytes, in seconds, under every other limit. Each script's allocation is
        /// measured on the thread that ran it and added to the page's total; once the total is past
        /// this, the scripts after it are not started, and the page is laid out as its scripts had
        /// left it, with the limit reported. A handler or a timer the event loop invokes runs
        /// outside the scripts' evaluation and is bounded by <see cref="PageTimeout"/> alone.</para>
        /// </remarks>
        public long MaxMemoryBytesPerPage { get; set; } = 256L * 1024 * 1024;

        /// <summary>How long one script may run. Default 5 seconds.</summary>
        public TimeSpan ScriptTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How long the page's scripts may run in total, from the first script to the last timer.
        /// Default 15 seconds.
        /// </summary>
        public TimeSpan PageTimeout { get; set; } = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Nested calls before a script fails with "Maximum call stack size exceeded" rather than
        /// exhausting the process's stack. Default 2 000.
        /// </summary>
        public int MaxCallStackDepth { get; set; } = 2_000;

        /// <summary>
        /// The deepest element nesting a page may have for its scripts to run. Default 1 024.
        /// </summary>
        /// <remarks>
        /// <b>A process guard, not a script limit.</b> AngleSharp walks the tree recursively — to
        /// serialise it, to answer <c>querySelectorAll</c> — and a stack overflow cannot be caught:
        /// a page nested a hundred thousand deep would take the host process down, not just the
        /// conversion. Such a page is measured with an explicit stack before anything recursive
        /// touches it, and its scripts are not run (reported); the managed engine then lays it out
        /// as written, under its own, lower, depth cap.
        /// </remarks>
        public int MaxDocumentDepth { get; set; } = 1_024;

        /// <summary>
        /// How long after the page has loaded its timers are given to fire before the page is laid
        /// out. Default 100 ms: enough for the zero-delay <c>setTimeout</c> a ready handler uses to
        /// "run after the page settles", not enough for an animation or a poll to hold the conversion
        /// up. A <c>setTimeout</c> due inside it runs, however late a busy machine gets to it — the
        /// window waits for it, under <see cref="PageTimeout"/>; a timer due later than this does not
        /// run.
        /// </summary>
        public TimeSpan TimerSettleTime { get; set; } = TimeSpan.FromMilliseconds(100);

        internal HtmlScriptLimits Clone() => (HtmlScriptLimits)MemberwiseClone();
    }
}
