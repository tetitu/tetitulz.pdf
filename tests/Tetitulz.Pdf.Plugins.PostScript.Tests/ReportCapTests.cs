// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System;
using System.Text;
using System.Threading;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// A conversion composes and delivers at most <see cref="PsLimits.MaxReportsPerConversion"/>
/// reports, then one saying the rest were suppressed; <see cref="PostScriptDiagnostics.ReportCount"/>
/// goes on counting. Almost every report site is reachable from a loop, so
/// <c>{ erasepage } repeat</c> was one message and one <c>OnEvent</c> call a step.
/// </summary>
[Collection(PsLimitsCollection.Name)]
public sealed class ReportCapTests
{
    private static (int Delivered, int Counted, string Last) Run(int repeats)
    {
        int thread = Environment.CurrentManagedThreadId, delivered = 0;
        void OnEvent(string _) { if (Environment.CurrentManagedThreadId == thread) Interlocked.Increment(ref delivered); }
        PostScriptDiagnostics.OnEvent += OnEvent;
        try
        {
            PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes($"{repeats} {{ erasepage }} repeat"));
            return (delivered, PostScriptDiagnostics.ReportCount, PostScriptDiagnostics.LastError);
        }
        finally { PostScriptDiagnostics.OnEvent -= OnEvent; }
    }

    [Fact]
    public void AdversarialReportLoop_DeliversTheCapAndOneNotice()
    {
        try
        {
            PsLimits.MaxReportsPerConversion = 50;
            var (delivered, counted, last) = Run(5000);

            Assert.Equal(51, delivered);
            Assert.Equal(5000, counted);
            Assert.Contains("more than 50 reports", last, StringComparison.Ordinal);
        }
        finally { PsLimits.ResetToDefaults(); }
    }

    /// <summary>The control: below the cap every report is delivered, and the last is its own.</summary>
    [Fact]
    public void ReportsBelowTheCap_AreEachDelivered()
    {
        try
        {
            PsLimits.MaxReportsPerConversion = 50;
            var (delivered, counted, last) = Run(50);

            Assert.Equal(50, delivered);
            Assert.Equal(50, counted);
            Assert.Contains("'erasepage' was ignored", last, StringComparison.Ordinal);
        }
        finally { PsLimits.ResetToDefaults(); }
    }
}
