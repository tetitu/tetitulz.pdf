// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Plugins.JavaScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// A script that does not run to completion leaves
/// <see cref="PdfJsExecutionContext.ValidationPassed"/> <b>false</b>.
///
/// <para><b>Why this exists.</b> <c>ValidationPassed</c> was assigned from the event proxy's
/// <c>rc</c> only on the success path; every catch set <c>Error</c> and returned without touching
/// it, so the property kept its <c>= true</c> default after an aborted execution. That is the
/// unsafe direction for a gate with this name: a field's <c>/AA /V</c> script of
/// <c>event.rc = false; &lt;something that fails&gt;</c> set <c>rc</c> on the proxy and then threw,
/// so a host doing <c>if (ctx.ValidationPassed) commit(value)</c> committed the value the script
/// had just rejected. Written without the failure the same script correctly yields false, so the
/// two cases were indistinguishable to a caller that did not also inspect <c>Error</c>.</para>
/// </summary>
public class ValidationFailClosedTests
{
    [Fact]
    public void Execute_ScriptThatRejectsThenThrows_LeavesValidationFailed()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("event.rc = false; noSuchFunction();", ctx);

        Assert.NotNull(ctx.Error);
        Assert.False(ctx.ValidationPassed);
    }

    [Fact]
    public void Execute_ScriptThatRejectsThenHangs_LeavesValidationFailed()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("event.rc = false; while (true) {}", ctx);

        Assert.NotNull(ctx.Error);
        Assert.False(ctx.ValidationPassed);
    }

    /// <summary>
    /// A script that never touched <c>event.rc</c> and then failed is still not a pass: the engine
    /// has no evidence either way, and "no evidence" must not read as "approved".
    /// </summary>
    [Fact]
    public void Execute_ScriptThatThrowsWithoutSettingRc_LeavesValidationFailed()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("throw new Error('boom');", ctx);

        Assert.False(ctx.ValidationPassed);
        Assert.NotNull(ctx.ValidationMessage);
    }

    /// <summary>
    /// The control: a script that completes and accepts the value still passes, so the three
    /// assertions above cannot pass by the engine having started to fail everything.
    /// </summary>
    [Fact]
    public void Execute_ScriptThatCompletesAndAccepts_LeavesValidationPassed()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("event.value = '42';", ctx);

        Assert.Null(ctx.Error);
        Assert.True(ctx.ValidationPassed);
        Assert.Equal("42", ctx.EventValue);
    }

    /// <summary>
    /// A script that rejects the value with its own message and then fails keeps <i>its</i> message:
    /// "the amount must be positive" is more useful than "the script failed".
    /// </summary>
    [Fact]
    public void Execute_ScriptWithItsOwnValidationMessage_KeepsItAfterAFailure()
    {
        var ctx = new PdfJsExecutionContext { ValidationMessage = "Amount must be positive." };
        PdfJsEngine.Execute("event.rc = false; noSuchFunction();", ctx);

        Assert.False(ctx.ValidationPassed);
        Assert.Equal("Amount must be positive.", ctx.ValidationMessage);
    }
}
