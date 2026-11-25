// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Collections.Generic;
using System.Threading;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;

/// <summary>
/// Collects the diagnostics one conversion reports, and <b>only</b> that conversion's.
///
/// <para><c>PostScriptDiagnostics.LastError</c> is only ever the last message, so a test asking
/// whether something was reported at all has to subscribe to <c>OnEvent</c>. But that event is
/// process-wide while every other slot on the sink is <c>[ThreadStatic]</c> — so a naive subscriber
/// receives reports raised by whatever else is converting at the same moment. xUnit runs test
/// classes in parallel, so a test asserting "the unrepresentable-text warning did NOT fire" would
/// pass alone and fail in a full run, because a concurrent test in another class legitimately
/// reports exactly that.</para>
///
/// <para>So it keeps only what this thread raised. <c>Report</c> invokes handlers synchronously on
/// the reporting thread, so the check is exact rather than a heuristic.</para>
/// </summary>
internal sealed class DiagnosticCollector : IDisposable
{
    private readonly List<string> _messages = new();
    private readonly int _threadId = Thread.CurrentThread.ManagedThreadId;
    private readonly Action<string> _handler;

    private DiagnosticCollector()
    {
        _handler = Collect;
        PostScriptDiagnostics.OnEvent += _handler;
    }

    /// <summary>Starts collecting; dispose to stop.</summary>
    public static DiagnosticCollector Start() => new();

    private void Collect(string message)
    {
        if (Thread.CurrentThread.ManagedThreadId != _threadId) return;
        lock (_messages) _messages.Add(message);
    }

    /// <summary>Everything reported on this thread since <see cref="Start"/>.</summary>
    public IReadOnlyList<string> Messages
    {
        get { lock (_messages) return _messages.ToArray(); }
    }

    /// <summary>True when any collected message contains <paramref name="fragment"/>.</summary>
    public bool Any(string fragment)
    {
        foreach (string m in Messages)
            if (m.Contains(fragment, StringComparison.Ordinal)) return true;
        return false;
    }

    public void Dispose() => PostScriptDiagnostics.OnEvent -= _handler;
}
