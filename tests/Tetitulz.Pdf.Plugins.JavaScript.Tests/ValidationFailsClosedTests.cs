// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Scripting;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// <see cref="PdfJsExecutionContext.ValidationPassed"/> is a gate, and a gate must fail closed.
///
/// <para><b>The bug.</b> The property defaults to <c>true</c> and was assigned only on the engine's
/// success path. Every <c>catch</c> set <c>ctx.Error</c> and returned without touching it, so a
/// script that set <c>event.rc = false</c> and then died — a timeout, a runaway recursion, a
/// reference to an undefined function — left <c>ValidationPassed</c> <c>true</c>. A host doing the
/// documented <c>if (ctx.ValidationPassed) commit(value);</c> committed the value the script had
/// just rejected, and the caller could not tell that case from a clean approval without also
/// inspecting <c>ctx.Error</c>.</para>
///
/// <para>Each test below pairs the failing script with the equivalent that <i>completes</i>, so the
/// assertion is that the two agree — not merely that something was false. "Everything reports
/// false" would be a different bug and these would catch it.</para>
/// </summary>
public class ValidationFailsClosedTests
{
    /// <summary>
    /// The archetype: a rejection followed by an infinite loop. The engine's 5-second
    /// <c>TimeoutInterval</c> fires, so the line that copies <c>event.rc</c> back never runs.
    /// </summary>
    [Fact]
    public void RejectThenHangForever_DoesNotReportValidationPassed()
    {
        var ctx = new PdfJsExecutionContext();

        PdfJsEngine.Execute("event.rc = false; while (true) { }", ctx);

        Assert.NotNull(ctx.Error);
        Assert.False(ctx.ValidationPassed);
    }

    /// <summary>
    /// The same rejection, thrown out rather than hung: <c>noSuchFunction()</c> raises a
    /// <c>JavaScriptException</c> after <c>event.rc</c> has already been set.
    /// </summary>
    [Fact]
    public void RejectThenThrow_DoesNotReportValidationPassed()
    {
        var ctx = new PdfJsExecutionContext();

        PdfJsEngine.Execute("event.rc = false; noSuchFunction();", ctx);

        Assert.NotNull(ctx.Error);
        Assert.False(ctx.ValidationPassed);
    }

    /// <summary>
    /// A script that fails <b>without</b> having decided anything has still not approved the value.
    /// This is the case that reached a host as a silent "passed" most often, because nothing in the
    /// script looks like a rejection at all.
    /// </summary>
    [Fact]
    public void FailWithoutDeciding_DoesNotReportValidationPassed()
    {
        var ctx = new PdfJsExecutionContext();

        PdfJsEngine.Execute("noSuchFunction();", ctx);

        Assert.NotNull(ctx.Error);
        Assert.False(ctx.ValidationPassed);
    }

    /// <summary>
    /// Runaway recursion trips <c>LimitRecursion</c>, which is a CLR exception rather than a
    /// JavaScript one — a separate <c>catch</c> arm, and therefore a separate chance to leak the
    /// default.
    /// </summary>
    [Fact]
    public void UnboundedRecursion_DoesNotReportValidationPassed()
    {
        var ctx = new PdfJsExecutionContext();

        PdfJsEngine.Execute("event.rc = false; function f(){ return f(); } f();", ctx);

        Assert.NotNull(ctx.Error);
        Assert.False(ctx.ValidationPassed);
    }

    /// <summary>
    /// A failed run must say <i>why</i> validation could not be completed. Without a message the
    /// host has a rejection it cannot explain to the user.
    /// </summary>
    [Fact]
    public void AFailedRun_ExplainsWhyValidationCouldNotComplete()
    {
        var ctx = new PdfJsExecutionContext();

        PdfJsEngine.Execute("noSuchFunction();", ctx);

        Assert.False(string.IsNullOrWhiteSpace(ctx.ValidationMessage));
        Assert.Contains("Validation could not be completed", ctx.ValidationMessage!,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A script that rejected the value with its own reason keeps that reason: it is more useful to
    /// a user than "execution failed", and overwriting it would be a regression of its own.
    /// </summary>
    [Fact]
    public void AScriptsOwnRejectionMessage_SurvivesALaterFailure()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.EventValue = "500";

        // AFRange_Validate sets ctx.ValidationMessage itself, then the script dies.
        PdfJsEngine.Execute("AFRange_Validate(true, 0, true, 100); noSuchFunction();", ctx);

        Assert.False(ctx.ValidationPassed);
        Assert.NotNull(ctx.ValidationMessage);
        Assert.Contains("less than or equal to 100", ctx.ValidationMessage!, StringComparison.Ordinal);
    }

    // ────────────────────────────────────────────────────────────────────
    //  Controls — the gate must still open for scripts that complete
    // ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The same rejection <b>without</b> the failure: this is the case that always worked, and it
    /// must keep working. Together with the tests above it establishes that the engine reports what
    /// the script decided, not a constant.
    /// </summary>
    [Fact]
    public void RejectAndComplete_ReportsFailureToo()
    {
        var ctx = new PdfJsExecutionContext();

        PdfJsEngine.Execute("event.rc = false;", ctx);

        Assert.Null(ctx.Error);
        Assert.False(ctx.ValidationPassed);
    }

    /// <summary>
    /// A script that runs to completion and rejects nothing must still report success — otherwise
    /// the fix above would have replaced fail-open with fail-always, which no test asserting only
    /// <c>False</c> could detect.
    /// </summary>
    [Fact]
    public void AcceptAndComplete_ReportsSuccess()
    {
        var ctx = new PdfJsExecutionContext();

        PdfJsEngine.Execute("event.rc = true; var x = 1 + 1;", ctx);

        Assert.Null(ctx.Error);
        Assert.True(ctx.ValidationPassed);
        Assert.Null(ctx.ValidationMessage);
    }
}
