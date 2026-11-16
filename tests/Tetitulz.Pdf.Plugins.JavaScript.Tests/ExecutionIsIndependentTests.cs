// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using Tetitulz.Pdf.Base.Scripting;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// Contexts that carry no <see cref="PdfJsDocumentScope"/> run independently: each
/// <see cref="PdfJsEngine.Execute"/> gets an engine of its own, so nothing a script defines survives
/// into the next one. The control for <see cref="DocumentScopeTests"/>, where the same two scripts
/// share a scope and the second sees what the first defined.
/// </summary>
public class ExecutionIsIndependentTests
{
    /// <summary>
    /// The exact scenario from the finding, both halves. The first script defines a helper and
    /// succeeds; the second cannot see it and fails honestly.
    /// </summary>
    [Fact]
    public void AFunctionDefinedByOneScript_IsNotVisibleToTheNext()
    {
        var documentLevel = new PdfJsExecutionContext();
        PdfJsEngine.Execute("function taxRate(){ return 0.17; }", documentLevel);
        Assert.Null(documentLevel.Error);

        var fieldLevel = new PdfJsExecutionContext { EventValue = "100" };
        PdfJsEngine.Execute("event.value = '' + (100 * taxRate());", fieldLevel);

        Assert.NotNull(fieldLevel.Error);
        // Fails closed as well, per the engine's own gate: a script that did not complete has
        // approved nothing.
        Assert.False(fieldLevel.ValidationPassed);
    }

    /// <summary>A global assigned by one script is equally gone.</summary>
    [Fact]
    public void AGlobalAssignedByOneScript_IsNotVisibleToTheNext()
    {
        var first = new PdfJsExecutionContext();
        PdfJsEngine.Execute("subtotal = 250;", first);
        Assert.Null(first.Error);

        var second = new PdfJsExecutionContext();
        PdfJsEngine.Execute("event.value = '' + subtotal;", second);

        Assert.NotNull(second.Error);
    }

    /// <summary>
    /// The control: within <b>one</b> execution a helper is perfectly visible, so the failures
    /// above are about the boundary between calls rather than about function declarations being
    /// broken.
    /// </summary>
    [Fact]
    public void AFunctionDefinedAndUsedInOneScript_Works()
    {
        var ctx = new PdfJsExecutionContext();

        PdfJsEngine.Execute("function taxRate(){ return 0.17; } event.value = '' + (100 * taxRate());", ctx);

        Assert.Null(ctx.Error);
        Assert.Equal("17", ctx.EventValue);
    }

    /// <summary>
    /// The context does carry values across calls — that is the sanctioned channel, and it is what
    /// distinguishes "no shared engine" from "nothing is shared at all".
    /// </summary>
    [Fact]
    public void TheExecutionContext_StillCarriesValuesBetweenCalls()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "5" };

        PdfJsEngine.Execute("event.value = '' + (event.value * 2);", ctx);
        Assert.Equal("10", ctx.EventValue);

        PdfJsEngine.Execute("event.value = '' + (event.value * 3);", ctx);
        Assert.Equal("30", ctx.EventValue);
    }
}
