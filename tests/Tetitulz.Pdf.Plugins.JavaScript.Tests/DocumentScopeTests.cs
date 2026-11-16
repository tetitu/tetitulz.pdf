// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Scripting;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// One document's scripts in one environment (<see cref="PdfJsDocumentScope"/>), and the sandbox
/// that environment keeps: a ceiling on what the scripts allocate together, a bridge no script
/// reaches, and nothing done outside the process.
/// </summary>
public class DocumentScopeTests
{
    private static PdfJsExecutionContext InScope(PdfJsDocumentScope scope) =>
        new PdfJsExecutionContext { DocumentScope = scope };

    /// <summary>
    /// A document-level function is visible to the script after it when both carry the scope — the
    /// viewer's model, which <see cref="ExecutionIsIndependentTests"/> shows absent without one.
    /// </summary>
    [Fact]
    public void AFunctionADocumentScriptDefines_IsVisibleToALaterScriptInTheScope()
    {
        var scope = new PdfJsDocumentScope();
        var documentLevel = InScope(scope);
        PdfJsEngine.Execute("function taxRate(){ return 0.17; } var subtotal = 250;", documentLevel);
        Assert.Null(documentLevel.Error);

        var field = InScope(scope);
        field.EventValue = "0";
        PdfJsEngine.Execute("event.value = '' + (subtotal * taxRate());", field);

        Assert.Null(field.Error);
        Assert.Equal("42.5", field.EventValue);
    }

    /// <summary>The global object is the document: <c>this</c> in a plain function, and its members as globals.</summary>
    [Fact]
    public void TheGlobalObjectIsTheDocument()
    {
        var ctx = new PdfJsExecutionContext { NumPages = 3 };
        ctx.FieldValues["a"] = "5";
        PdfJsEngine.Execute(
            "function f(){ return this.getField('a').value; } " +
            "event.value = [f(), numPages, typeof getField, (function(){ return this; })() === this].join('|');", ctx);

        Assert.Null(ctx.Error);
        Assert.Equal("5|3|function|true", ctx.EventValue);
    }

    /// <summary>
    /// What a document's scripts allocate together is bounded: each execution here allocates about
    /// twenty megabytes and keeps none of it, inside the per-execution limit, and the document is
    /// refused once the sum passes <c>DefaultDocumentMemoryLimit</c> (256 MiB) — failing closed,
    /// with the reason. A fresh scope runs the same script (the control).
    /// </summary>
    [Fact]
    public void AdversarialAllocationAcrossADocumentsScripts_IsRefusedPastTheDocumentCeiling()
    {
        const string allocate = "var s = 'x'.repeat(10000000); s = null; event.value = 'ran';";
        var scope = new PdfJsDocumentScope();
        int ran = 0;
        PdfJsExecutionContext? refused = null;
        for (int i = 0; i < 40 && refused == null; i++)
        {
            var ctx = InScope(scope);
            PdfJsEngine.Execute(allocate, ctx);
            if (ctx.Error == null) ran++;
            else refused = ctx;
        }

        Assert.NotNull(refused);
        Assert.False(refused!.ValidationPassed);
        Assert.Contains("document's scripts exceeded their memory limit", refused.ValidationMessage);
        // 256 MiB at about 20 MB an execution: a dozen run before the ceiling, not one, not all forty.
        Assert.InRange(ran, 8, 16);

        var fresh = InScope(new PdfJsDocumentScope());
        PdfJsEngine.Execute(allocate, fresh);
        Assert.Null(fresh.Error);
        Assert.Equal("ran", fresh.EventValue);
    }

    /// <summary>
    /// A field object costs an object, not its members: the field objects share one prototype, so a
    /// script calling <c>getField</c> twenty thousand times stays inside the per-execution memory limit
    /// (each object carrying its own ninety closures spent the limit in a few hundred calls).
    /// </summary>
    [Fact]
    public void AdversarialGetFieldLoops_StayInsideTheMemoryLimit()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["a"] = "1";
        PdfJsEngine.Execute("var n = 0; for (var i = 0; i < 20000; i++) n += this.getField('a').value; event.value = '' + n;", ctx);

        Assert.True(ctx.Error == null, ctx.Error?.Message);
        Assert.Equal("20000", ctx.EventValue);
    }

    /// <summary>The bridge and the engine's two event functions are gone before a document script runs.</summary>
    [Fact]
    public void NoScriptReachesTheHostBridge_OrTheCLR()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute(
            "event.value = [typeof __tetitulzHost, typeof __tetitulzBegin, typeof __tetitulzEnd, " +
            "typeof app.GetType, typeof util.printf.constructor('return this')().__tetitulzHost].join('|');", ctx);

        Assert.Null(ctx.Error);
        Assert.Equal("undefined|undefined|undefined|undefined|undefined", ctx.EventValue);
    }

    /// <summary>
    /// What would reach outside the process is recorded and not done — a URL, mail, a submission, a
    /// file, a timer — and a privileged call grants nothing. The control: a script that asks for none
    /// of it leaves the report empty.
    /// </summary>
    [Fact]
    public void OutwardCalls_AreRecordedAndNotRun()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute(
            "app.launchURL('http://example.invalid/'); this.submitForm('http://example.invalid/'); this.mailDoc(true);" +
            "this.importDataObject('x'); var t = app.setTimeOut('event.value = 1', 10); app.execMenuItem('SaveAs');" +
            "var f = app.trustedFunction(function(){ return 7; });" +
            "event.value = typeof t + '|' + f() + '|' + app.execDialog({});", ctx);

        Assert.Null(ctx.Error);
        Assert.Equal("object|7|cancel", ctx.EventValue);
        string report = PdfJsEngine.LastUnsupportedRequest!;
        foreach (string call in new[] { "app.launchURL(http://example.invalid/) not run", "this.submitForm", "this.mailDoc",
                                        "this.importDataObject", "app.setTimeOut", "app.execMenuItem(SaveAs) not run", "app.execDialog" })
            Assert.Contains(call, report, StringComparison.Ordinal);

        PdfJsEngine.Execute("event.value = 'quiet';", new PdfJsExecutionContext());
        Assert.Null(PdfJsEngine.LastUnsupportedRequest);
    }

    /// <summary><c>app.beginPriv</c> throws when read, as in Acrobat; the script sees the refusal.</summary>
    [Fact]
    public void BeginPriv_IsRefused()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("try { app.beginPriv(); event.value = 'granted'; } catch (e) { event.value = e.name; }", ctx);
        Assert.Equal("NotAllowedError", ctx.EventValue);
    }

    /// <summary>
    /// The console and the alerts a script writes are capped, each line cut to a length, and what is
    /// not kept is counted in the last slot; under the cap every line is kept whole (the control).
    /// </summary>
    [Fact]
    public void AdversarialConsoleAndAlertFloods_AreCappedAndCounted()
    {
        var flood = new PdfJsExecutionContext();
        PdfJsEngine.Execute(
            "var big = 'y'.repeat(100000); for (var i = 0; i < 5000; i++) { console.println(big); app.alert('a' + i); }", flood);

        Assert.Null(flood.Error);
        Assert.Equal(1024, flood.ConsoleMessages.Count);
        Assert.True(flood.ConsoleMessages[0].Length <= 4097, $"{flood.ConsoleMessages[0].Length} characters kept");
        Assert.Equal("(3977 further console line(s) not kept)", flood.ConsoleMessages[1023]);
        Assert.Equal(256, flood.AlertMessages.Count);
        Assert.Equal("(4745 further alert(s) not kept)", flood.AlertMessages[255]);

        var few = new PdfJsExecutionContext();
        PdfJsEngine.Execute("console.println('one'); console.println(2); app.alert({ cMsg: 'three' });", few);
        Assert.Equal(new[] { "one", "2" }, few.ConsoleMessages);
        Assert.Equal(new[] { "three" }, few.AlertMessages);
    }

    /// <summary>
    /// A choice field's local item list — kept across a document's scripts — is capped: appending
    /// past it, or setting a longer list, is stopped with the reason; under it the items read back.
    /// </summary>
    // The cap is the claim, not the engine's five-second timeout: the list is filled to ten short of
    // the cap by one setItems call and grown past it by insertItemAt, so the work is a few calls,
    // never ten thousand racing the clock on a loaded host.
    [Theory]
    [InlineData("var a = []; for (var i = 0; i < 9990; i++) a.push('x'); f.setItems(a); for (var i = 0; i < 20; i++) f.insertItemAt('x' + i, 'x' + i, 9990 + i);")]
    [InlineData("var a = []; for (var i = 0; i < 10001; i++) a.push('x'); f.setItems(a);")]
    public void AdversarialChoiceItemGrowth_IsCapped(string grow)
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["list"] = "";
        PdfJsEngine.Execute("var f = this.getField('list'); " + grow, ctx);
        Assert.NotNull(ctx.Error);
        Assert.Contains("at most 10000 items", ctx.Error!.Message, StringComparison.Ordinal);

        var under = new PdfJsExecutionContext();
        under.FieldValues["list"] = "";
        PdfJsEngine.Execute("var f = this.getField('list'); f.setItems([['One', '1'], 'Two']); event.value = f.getItemAt(0) + ',' + f.getItemAt(1, false);", under);
        Assert.Null(under.Error);
        Assert.Equal("1,Two", under.EventValue);
    }
}
