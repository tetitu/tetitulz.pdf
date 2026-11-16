// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Reflection;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The diagnostics contract: thread-local storage, cleared per entry point, and honest about
/// whether a conversion ran clean.
/// </summary>
public sealed class PostScriptDiagnosticsTests
{
    /// <summary>
    /// CLAUDE.md requires every <c>Last*</c> slot to be <c>[ThreadStatic]</c> — a value read after
    /// one call must never belong to another thread's call. Asserted by reflection so it cannot
    /// regress silently when someone adds a field.
    /// </summary>
    [Theory]
    [InlineData("_lastError")]
    [InlineData("_unimplemented")]
    [InlineData("_reportCount")]
    public void DiagnosticSlot_IsBackedByAThreadStaticField(string fieldName)
    {
        var field = typeof(PostScriptDiagnostics).GetField(
            fieldName, BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(field);
        Assert.True(field!.GetCustomAttribute<ThreadStaticAttribute>() != null,
            $"{fieldName} must be [ThreadStatic]; see CLAUDE.md → Diagnostics Pattern.");
    }

    /// <summary>
    /// Per-thread storage is not per-document scope. A slot a caller reads *after* an operation
    /// must be reset *at the start* of that operation, on every path — the bug CLAUDE.md records
    /// for <c>LastPermsValidation</c>.
    /// </summary>
    [Fact]
    public void LastError_IsClearedAtTheStartOfEachConversion()
    {
        // A conversion that reports something.
        PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes("(unterminated"));
        Assert.NotNull(PostScriptDiagnostics.LastError);

        // A second conversion must not inherit it. (Both currently report "no engine", so assert
        // on the *specific* earlier message being gone rather than on null.)
        var result = PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes("1 2 add"));

        Assert.DoesNotContain("unterminated", PostScriptDiagnostics.LastError ?? "");
        Assert.DoesNotContain("unterminated", result.LastError ?? "");
    }

    [Fact]
    public void ReportCount_IsClearedAtTheStartOfEachConversion()
    {
        PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes("(a (b (c"));
        int noisy = PostScriptDiagnostics.ReportCount;
        Assert.True(noisy > 0);

        PostScriptConversion.ConvertFragment(Encoding.ASCII.GetBytes("1 2 add"));

        Assert.True(PostScriptDiagnostics.ReportCount < noisy,
            "the second conversion inherited the first one's report count.");
    }

    [Fact]
    public void UnimplementedOperators_AreDeduplicatedAndCapped()
    {
        PostScriptDiagnostics.Clear();

        for (int i = 0; i < 5; i++) PostScriptDiagnostics.ReportUnimplemented("setscreen");
        for (int i = 0; i < PsLimits.MaxRecordedUnimplemented * 2; i++)
            PostScriptDiagnostics.ReportUnimplemented("op" + i);

        var recorded = PostScriptDiagnostics.UnimplementedOperators;

        Assert.Equal(PsLimits.MaxRecordedUnimplemented, recorded.Count);
        Assert.Single(recorded.Where(n => n == "setscreen"));
    }

    /// <summary>
    /// A subscriber sees the same messages the thread-static slots record, one call per report —
    /// asserted on the message text, because "the list is non-empty" would pass with the event
    /// wired to anything at all.
    ///
    /// <para><b>The handler filters by a unique operation name, and must.</b> The slots are
    /// <c>[ThreadStatic]</c> but <see cref="PostScriptDiagnostics.OnEvent"/> is process-wide — that
    /// asymmetry is deliberate, since a host wants one telemetry feed rather than one per thread.
    /// xunit runs test classes in parallel, so an unfiltered handler also collects the corpus
    /// tests' reports and the count assertion fails intermittently.</para>
    /// </summary>
    [Fact]
    public void OnEvent_ReceivesEveryReport_WithTheComposedMessage()
    {
        const string Op = "UnitTest.OnEventReceivesEveryReport";

        PostScriptDiagnostics.Clear();
        var seen = new List<string>();
        void Handler(string m)
        {
            if (m.Contains(Op, StringComparison.Ordinal))
                lock (seen) seen.Add(m);
        }

        PostScriptDiagnostics.OnEvent += Handler;
        try
        {
            PostScriptDiagnostics.Report(Op, "first detail");
            PostScriptDiagnostics.Report(Op, "second detail",
                new InvalidOperationException("boom"));
        }
        finally
        {
            PostScriptDiagnostics.OnEvent -= Handler;
        }

        Assert.Equal(2, seen.Count);
        Assert.Equal($"[{Op}] first detail", seen[0]);
        Assert.Equal($"[{Op}] second detail — InvalidOperationException: boom", seen[1]);

        // The subscriber and the slot are two views of one report, not two reports. LastError is
        // thread-static, so a parallel class cannot have overwritten it here.
        Assert.Equal(seen[1], PostScriptDiagnostics.LastError);
        Assert.Equal(2, PostScriptDiagnostics.ReportCount);
    }

    /// <summary>
    /// A throwing subscriber must not escape: Report is called from the recovery paths whose whole
    /// job is to keep a broken program from losing the drawing, so letting a host's logging
    /// callback throw from there would convert every tolerated failure into a lost page.
    ///
    /// <para>The program deliberately raises an error — a clean conversion reports nothing at all,
    /// so it would not exercise the subscriber.</para>
    /// </summary>
    [Fact]
    public void OnEvent_ThrowingSubscriber_IsContained()
    {
        PostScriptDiagnostics.Clear();
        void Exploding(string _) => throw new InvalidOperationException("subscriber exploded");

        PostScriptDiagnostics.OnEvent += Exploding;
        try
        {
            var result = PostScriptConversion.ConvertFragment(
                Encoding.ASCII.GetBytes("nosuchoperator"));

            // The conversion completed and the failure is still recorded for the caller, even
            // though every notification threw.
            Assert.NotNull(result.LastError);
            Assert.Contains("Undefined", result.LastError);
        }
        finally
        {
            PostScriptDiagnostics.OnEvent -= Exploding;
        }
    }

    /// <summary>
    /// Thread isolation, the property [ThreadStatic] exists for: one thread's report must be
    /// invisible to another.
    /// </summary>
    [Fact]
    public void DiagnosticSlots_AreIsolatedBetweenThreads()
    {
        PostScriptDiagnostics.Clear();
        PostScriptDiagnostics.Report("Test", "main-thread message");

        string? observed = "not-run";
        var t = new Thread(() => observed = PostScriptDiagnostics.LastError);
        t.Start();
        t.Join();

        Assert.Null(observed);
        Assert.Contains("main-thread message", PostScriptDiagnostics.LastError);
    }
}
