// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Threading;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Plugins;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Scripting;

/// <summary>
/// The budget a document's scripts share, on top of the per-call limits the engine already has.
/// </summary>
/// <remarks>
/// <para><b>The sandbox was never the problem, and these do not test it.</b> Each script already
/// gets 5 s and 32 MB inside <c>Plugins.JavaScript</c>, with no CLR access, no reflection and a
/// fresh engine per call. What was missing is an aggregate: a document decides how many scripts it
/// carries, so N field scripts were entitled to N × 5 s and nothing said otherwise.</para>
///
/// <para><b>Nothing here reads a clock to decide whether the guard worked.</b> A budget of zero
/// refuses the first script deterministically, and a generous one lets every script through — so
/// the assertions are about the refusal and its reason, which is the guard's own evidence. A
/// wall-clock ceiling would measure the machine's backlog under a full-solution run.</para>
/// </remarks>
/// <remarks>
/// <b>In <c>PluginHostCollection</c> because <c>PdfPluginHost.Instance</c> is a process-wide
/// singleton.</b> These register a stub engine, and so do several other classes; run in parallel
/// they would see each other's plugins and the failures would look like flakes in the budget. The
/// collection is the established answer here, and joining it is not optional for a test that
/// registers.
/// </remarks>
[Collection(TestInfrastructure.PluginHostCollection.Name)]
public class DocumentScriptBudgetTests
{
    /// <summary>
    /// A plugin that records what it was asked to run and reports success without an engine.
    /// </summary>
    /// <remarks>
    /// The real engine is in a plugin package this project does not reference, and using it would
    /// make these tests about Jint. What is under test is the dispatch point's accounting, so the
    /// stub answers instantly and counts.
    /// </remarks>
    private sealed class CountingJsPlugin : IJavaScriptPlugin
    {
        internal readonly List<string> Ran = new();
        internal TimeSpan Delay;

        public string Name => "counting-js-stub";
        public string Description => "counts what it was asked to run";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public bool TryExecute(string script, PdfJsExecutionContext context)
        {
            Ran.Add(script);
            if (Delay > TimeSpan.Zero) Thread.Sleep(Delay);
            return true;
        }
    }

    /// <summary>Registers the stub for the duration of one test and removes it afterwards.</summary>
    private sealed class RegisteredStub : IDisposable
    {
        internal readonly CountingJsPlugin Plugin = new();

        internal RegisteredStub() => PdfPluginHost.Instance.Register(Plugin);

        public void Dispose() => PdfPluginHost.Instance.Unregister(Plugin.Name);
    }

    // ================================================================

    /// <summary>With no scope open, nothing changes — the budget is opt-in per operation.</summary>
    /// <remarks>
    /// The control. Every refusal below has to be the budget rather than the stub, the host, or a
    /// registration that did not take.
    /// </remarks>
    [Fact]
    public void WithNoBudgetOpenEveryScriptStillRuns()
    {
        using var stub = new RegisteredStub();
        var ctx = new PdfJsExecutionContext();

        Assert.Null(PluginJavaScriptRunner.RemainingDocumentBudget);

        for (int i = 0; i < 5; i++)
            Assert.True(PluginJavaScriptRunner.TryExecute($"script{i}", ctx));

        Assert.Equal(5, stub.Plugin.Ran.Count);
    }

    /// <summary>A generous budget also lets every script through.</summary>
    [Fact]
    public void AGenerousBudgetLetsEveryScriptThrough()
    {
        using var stub = new RegisteredStub();
        var ctx = new PdfJsExecutionContext();

        using (PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.FromMinutes(5)))
        {
            for (int i = 0; i < 20; i++)
                Assert.True(PluginJavaScriptRunner.TryExecute($"script{i}", ctx));
        }

        Assert.Equal(20, stub.Plugin.Ran.Count);
    }

    /// <summary>
    /// <b>An exhausted budget refuses before the plugin is reached</b>, so an exhausted document
    /// costs nothing rather than one more script's worth of engine.
    /// </summary>
    [Fact]
    public void AnExhaustedBudgetRefusesBeforeThePluginIsReached()
    {
        using var stub = new RegisteredStub();
        var ctx = new PdfJsExecutionContext();

        using (PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.Zero))
        {
            Assert.False(PluginJavaScriptRunner.TryExecute("hostile", ctx));
        }

        Assert.Empty(stub.Plugin.Ran);
        Assert.Contains("budget", PluginJavaScriptRunner.LastError);
        Assert.Contains("per document", PluginJavaScriptRunner.LastError);
    }

    /// <summary>
    /// The refusal is distinguishable from "no engine is registered" — different causes, different
    /// fixes.
    /// </summary>
    /// <remarks>
    /// Both return false, so a caller that only checks the boolean cannot tell a hostile document
    /// from a missing package reference.
    /// </remarks>
    [Fact]
    public void ABudgetRefusalIsNotTheSameMessageAsAMissingEngine()
    {
        var ctx = new PdfJsExecutionContext();

        using (PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.Zero))
        {
            using var stub = new RegisteredStub();
            Assert.False(PluginJavaScriptRunner.TryExecute("x", ctx));
            Assert.DoesNotContain("IJavaScriptPlugin is registered", PluginJavaScriptRunner.LastError);
        }

        // No plugin registered, no budget: the other message.
        Assert.False(PluginJavaScriptRunner.TryExecute("x", ctx));
        Assert.Contains("IJavaScriptPlugin is registered", PluginJavaScriptRunner.LastError);
    }

    /// <summary>
    /// <b>Time spent is charged, so a document with slow scripts runs out.</b>
    /// </summary>
    /// <remarks>
    /// The one case that must involve real elapsed time, so it is written to be robust about it:
    /// the budget is 50 ms and each script sleeps 40 ms, so the second or third is refused whatever
    /// the machine is doing. The assertion is "not all of them ran and the reason is the budget",
    /// never "it took less than N milliseconds".
    /// </remarks>
    [Fact]
    public void TimeSpentIsChargedSoASlowDocumentRunsOut()
    {
        using var stub = new RegisteredStub();
        stub.Plugin.Delay = TimeSpan.FromMilliseconds(40);
        var ctx = new PdfJsExecutionContext();

        int refused = 0;

        using (PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.FromMilliseconds(50)))
        {
            for (int i = 0; i < 10; i++)
                if (!PluginJavaScriptRunner.TryExecute($"slow{i}", ctx)) refused++;
        }

        Assert.True(refused > 0, "a 50 ms budget survived ten 40 ms scripts");
        Assert.True(stub.Plugin.Ran.Count < 10, "every script ran despite the budget");
        Assert.Contains("budget", PluginJavaScriptRunner.LastError);
    }

    /// <summary>
    /// <b>An attempt that fails is charged too</b> — otherwise a script buys unlimited time by
    /// throwing.
    /// </summary>
    private sealed class ThrowingJsPlugin : IJavaScriptPlugin
    {
        internal int Attempts;
        public string Name => "throwing-js-stub";
        public string Description => "spends time, then throws";
        public Version Version => new(1, 0, 0);
        public void Initialize() { }
        public void Shutdown() { }

        public bool TryExecute(string script, PdfJsExecutionContext context)
        {
            Attempts++;
            Thread.Sleep(40);
            throw new InvalidOperationException("the engine fell over");
        }
    }

    [Fact]
    public void AFailingAttemptIsChargedAsWell()
    {
        var plugin = new ThrowingJsPlugin();
        PdfPluginHost.Instance.Register(plugin);
        try
        {
            var ctx = new PdfJsExecutionContext();

            using (PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.FromMilliseconds(50)))
            {
                for (int i = 0; i < 10; i++) PluginJavaScriptRunner.TryExecute($"boom{i}", ctx);
            }

            Assert.True(plugin.Attempts < 10,
                "a plugin that throws after 40 ms was allowed ten attempts on a 50 ms budget");
        }
        finally
        {
            PdfPluginHost.Instance.Unregister(plugin.Name);
        }
    }

    // ================================================================ scoping

    /// <summary>
    /// <b>Disposing an inner scope restores the outer budget rather than clearing it.</b>
    /// </summary>
    /// <remarks>
    /// An inner scope is how a caller tightens the budget for part of a document. Restoring to
    /// "no budget" instead of to the outer one would turn a nested tightening into a removal — the
    /// exact opposite of what it was written for.
    /// </remarks>
    [Fact]
    public void AnInnerScopeRestoresTheOuterBudget()
    {
        using var stub = new RegisteredStub();
        var ctx = new PdfJsExecutionContext();

        using (PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.FromMinutes(5)))
        {
            using (PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.Zero))
            {
                Assert.False(PluginJavaScriptRunner.TryExecute("inner", ctx));
            }

            Assert.NotNull(PluginJavaScriptRunner.RemainingDocumentBudget);
            Assert.True(PluginJavaScriptRunner.TryExecute("outer", ctx));
        }

        Assert.Null(PluginJavaScriptRunner.RemainingDocumentBudget);
        Assert.Equal(new[] { "outer" }, stub.Plugin.Ran);
    }

    /// <summary>Disposing twice in order is harmless.</summary>
    /// <remarks>
    /// Weak on its own, and it was the only version of this test at first: a second in-order
    /// dispose re-assigns the same captured predecessor, so it is idempotent whether or not the
    /// guard exists, and the mutation deleting the guard survived. Kept as the ordinary case, with
    /// the case that actually needs the guard below it.
    /// </remarks>
    [Fact]
    public void DisposingAScopeTwiceInOrderIsHarmless()
    {
        using var stub = new RegisteredStub();

        var outer = PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.FromMinutes(5));
        var inner = PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.Zero);

        inner.Dispose();
        inner.Dispose();

        Assert.NotNull(PluginJavaScriptRunner.RemainingDocumentBudget);
        Assert.True(PluginJavaScriptRunner.TryExecute("still budgeted", new PdfJsExecutionContext()));

        outer.Dispose();
        Assert.Null(PluginJavaScriptRunner.RemainingDocumentBudget);
    }

    /// <summary>
    /// <b>A scope disposed again after its parent does not resurrect the parent's budget.</b>
    /// </summary>
    /// <remarks>
    /// This is the case the idempotence guard is actually for, and the one a mutation exposed as
    /// untested. Each scope captures the budget in force when it opened, so a stale scope disposing
    /// late would put that budget back — reinstating a document-level budget after the document
    /// finished, on a thread now doing something else. In-order disposal cannot show it, because
    /// re-assigning the same predecessor twice is a no-op.
    /// </remarks>
    [Fact]
    public void AScopeDisposedAfterItsParentDoesNotResurrectTheParentsBudget()
    {
        using var stub = new RegisteredStub();

        var outer = PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.FromMinutes(5));
        var inner = PluginJavaScriptRunner.BeginDocumentBudget(TimeSpan.Zero);

        inner.Dispose();
        outer.Dispose();
        Assert.Null(PluginJavaScriptRunner.RemainingDocumentBudget);

        inner.Dispose();   // out of order, and it must change nothing

        Assert.Null(PluginJavaScriptRunner.RemainingDocumentBudget);
    }

    /// <summary>The default is a real number, and it is the one the facade uses.</summary>
    [Fact]
    public void TheDefaultBudgetIsStatedRatherThanImplied()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), PluginJavaScriptRunner.DefaultDocumentBudget);
        Assert.True(PluginJavaScriptRunner.DefaultDocumentBudget > TimeSpan.Zero);
    }
}
