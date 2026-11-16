// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Scripting;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// Tests for the managed PDF JavaScript engine (GAP 23.1).
/// All tests run in-memory — no PDF files required.
/// </summary>
public class PdfJavaScriptEngineTests
{
    // ----------------------------------------------------------------
    //  Basic execution
    // ----------------------------------------------------------------

    [Fact]
    public void Execute_NullScript_DoesNotThrow()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute(null, ctx);
        Assert.Null(ctx.Error);
    }

    [Fact]
    public void Execute_EmptyScript_DoesNotThrow()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("", ctx);
        Assert.Null(ctx.Error);
    }

    [Fact]
    /// <summary>
    /// A null context makes <c>Execute</c> a no-op. Surviving the call is only half of that
    /// contract — the other half is that the engine is <b>still usable afterwards</b>, which is
    /// what a partially-initialised or poisoned engine would fail. The predecessor asserted
    /// neither: its body contained no assertion at all.
    /// </summary>
    public void Execute_NullContext_IsANoOpAndLeavesTheEngineUsable()
    {
        PdfJsEngine.Execute("var x = 1;", null);

        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("var y = 2 + 3;", ctx);

        Assert.Null(ctx.Error);
    }

    [Fact]
    public void Execute_SimpleArithmetic_DoesNotError()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("var x = 2 + 3;", ctx);
        Assert.Null(ctx.Error);
    }

    [Fact]
    public void Execute_SyntaxError_CapturesError()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("this is not valid javascript }{", ctx);
        Assert.NotNull(ctx.Error);
    }

    // ----------------------------------------------------------------
    //  app.alert
    // ----------------------------------------------------------------

    [Fact]
    public void Execute_AppAlert_CapturesMessage()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("app.alert('Hello World');", ctx);
        Assert.Contains("Hello World", ctx.AlertMessages);
        Assert.Null(ctx.Error);
    }

    [Fact]
    public void Execute_AppAlert_MultipleAlerts()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("app.alert('One'); app.alert('Two');", ctx);
        Assert.Equal(2, ctx.AlertMessages.Count);
    }

    [Fact]
    public void Execute_AppAlert_ReturnsOne()
    {
        var ctx = new PdfJsExecutionContext();
        // alert returns 1 (OK button) — script should work without error
        PdfJsEngine.Execute("var r = app.alert('test'); if (r !== 1) throw 'bad';", ctx);
        Assert.Null(ctx.Error);
    }

    // ----------------------------------------------------------------
    //  event object
    // ----------------------------------------------------------------

    [Fact]
    public void Execute_EventValue_ReadAndWrite()
    {
        var ctx = new PdfJsExecutionContext
        {
            EventFieldName = "myField",
            EventValue     = "100"
        };
        PdfJsEngine.Execute("event.value = String(Number(event.value) * 2);", ctx);
        Assert.Equal("200", ctx.EventValue);
        Assert.Null(ctx.Error);
    }

    [Fact]
    public void Execute_EventRc_SetFalse_FailsValidation()
    {
        var ctx = new PdfJsExecutionContext
        {
            EventFieldName = "qty",
            EventValue     = "abc"
        };
        PdfJsEngine.Execute("if (isNaN(Number(event.value))) event.rc = false;", ctx);
        Assert.False(ctx.ValidationPassed);
        Assert.Null(ctx.Error);
    }

    [Fact]
    public void Execute_EventRc_DefaultTrue()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "42" };
        PdfJsEngine.Execute("var x = event.value;", ctx);
        Assert.True(ctx.ValidationPassed);
    }

    [Fact]
    public void Execute_EventWillCommit_IsReadable()
    {
        var ctx = new PdfJsExecutionContext { EventWillCommit = true };
        PdfJsEngine.Execute("if (!event.willCommit) throw 'wrong';", ctx);
        Assert.Null(ctx.Error);
    }

    // ----------------------------------------------------------------
    //  this / doc — field access
    // ----------------------------------------------------------------

    [Fact]
    public void Execute_DocGetField_ReturnsFieldValue()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["price"] = "9.99";
        PdfJsEngine.Execute("var f = doc.getField('price'); event.value = f.value;", ctx);
        Assert.Equal("9.99", ctx.EventValue);
        Assert.Null(ctx.Error);
    }

    /// <summary>
    /// The API enumerates fields with <c>numFields</c> and <c>getNthFieldName</c>, in alphabetical
    /// order as Acrobat numbers them, whatever order the host listed them in.
    /// </summary>
    [Fact]
    public void Execute_NumFieldsAndGetNthFieldName_ListTheFieldsAlphabetically()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["b"] = "2";
        ctx.FieldValues["a"] = "1";
        PdfJsEngine.Execute(
            "event.value = this.numFields + ':' + this.getNthFieldName(0) + ',' + getNthFieldName(1) + ',' + this.getNthFieldName(2);", ctx);
        Assert.Equal("2:a,b,undefined", ctx.EventValue);
        Assert.Null(ctx.Error);
    }

    [Fact]
    public void Execute_DocNumPages_IsAccessible()
    {
        var ctx = new PdfJsExecutionContext { NumPages = 7 };
        PdfJsEngine.Execute("event.value = String(doc.numPages);", ctx);
        Assert.Equal("7", ctx.EventValue);
        Assert.Null(ctx.Error);
    }

    [Fact]
    public void Execute_FieldValueWrite_CapturedInModifiedFieldValues()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["total"] = "0";
        PdfJsEngine.Execute("doc.getField('total').value = '42';", ctx);
        Assert.True(ctx.ModifiedFieldValues.ContainsKey("total"));
        Assert.Equal("42", ctx.ModifiedFieldValues["total"]);
        Assert.Null(ctx.Error);
    }

    // ----------------------------------------------------------------
    //  Event field name → ModifiedFieldValues
    // ----------------------------------------------------------------

    [Fact]
    public void Execute_EventValueChange_CapturedInModifiedFieldValues()
    {
        var ctx = new PdfJsExecutionContext
        {
            EventFieldName = "qty",
            EventValue     = "5"
        };
        PdfJsEngine.Execute("event.value = '10';", ctx);
        Assert.Equal("10", ctx.ModifiedFieldValues["qty"]);
    }

    // ----------------------------------------------------------------
    //  AFNumber_Format
    // ----------------------------------------------------------------

    [Fact]
    public void AFNumber_Format_FormatsDecimalCorrectly()
    {
        var ctx = new PdfJsExecutionContext
        {
            EventFieldName = "price",
            EventValue     = "1234.5"
        };
        // 2 decimal places, comma separator, no negative style, no currency
        PdfJsEngine.Execute("AFNumber_Format(2, 0, 0, 0, '', true);", ctx);
        Assert.Contains("1,234.50", ctx.EventValue);
        Assert.Null(ctx.Error);
    }

    [Fact]
    public void AFNumber_Format_NoGroupingSeparator()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "1234.5" };
        PdfJsEngine.Execute("AFNumber_Format(2, 1, 0, 0, '', true);", ctx);
        Assert.Equal("1234.50", ctx.EventValue);
    }

    /// <summary>
    /// Acrobat's negStyle: 0 a minus, 1 no sign (the field turns red), 2 parentheses, 3 parentheses
    /// in red — Fixtures/Acrobat's D cases.
    /// </summary>
    [Theory]
    [InlineData(0, "-500")]
    [InlineData(1, "500")]
    [InlineData(2, "(500)")]
    [InlineData(3, "(500)")]
    public void AFNumber_Format_NegativeStyles(int negStyle, string expected)
    {
        var ctx = new PdfJsExecutionContext { EventValue = "-500" };
        PdfJsEngine.Execute($"AFNumber_Format(0, 1, {negStyle}, 0, '', true);", ctx);
        Assert.Equal(expected, ctx.EventValue);
    }

    [Fact]
    public void AFNumber_Format_WithCurrencyPrepend()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "42" };
        PdfJsEngine.Execute("AFNumber_Format(2, 1, 0, 0, '$', true);", ctx);
        Assert.StartsWith("$", ctx.EventValue);
    }

    // ----------------------------------------------------------------
    //  AFRange_Validate
    // ----------------------------------------------------------------

    [Fact]
    public void AFRange_Validate_ValueInRange_Passes()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "50" };
        PdfJsEngine.Execute("AFRange_Validate(true, 0, true, 100);", ctx);
        Assert.True(ctx.ValidationPassed);
        Assert.Null(ctx.Error);
    }

    [Fact]
    public void AFRange_Validate_ValueBelowMin_Fails()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "-5" };
        PdfJsEngine.Execute("AFRange_Validate(true, 0, true, 100);", ctx);
        Assert.False(ctx.ValidationPassed);
        Assert.NotNull(ctx.ValidationMessage);
    }

    [Fact]
    public void AFRange_Validate_ValueAboveMax_Fails()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "200" };
        PdfJsEngine.Execute("AFRange_Validate(true, 0, true, 100);", ctx);
        Assert.False(ctx.ValidationPassed);
    }

    [Fact]
    /// <summary>
    /// <c>AFRange_Validate</c> must alert only when the value is out of range, and the alert must
    /// name the range. Asserting the in-range case adds <b>no</b> message is what makes this
    /// falsifiable — a stub that alerted unconditionally would satisfy "not empty".
    /// </summary>
    public void AFRange_Validate_AlertsOnlyWhenTheValueIsOutOfRange()
    {
        var outOfRange = new PdfJsExecutionContext { EventValue = "999" };
        PdfJsEngine.Execute("AFRange_Validate(true, 0, true, 10);", outOfRange);

        string alert = Assert.Single(outOfRange.AlertMessages);
        Assert.Contains("0", alert);
        Assert.Contains("10", alert);

        var inRange = new PdfJsExecutionContext { EventValue = "5" };
        PdfJsEngine.Execute("AFRange_Validate(true, 0, true, 10);", inRange);

        Assert.Empty(inRange.AlertMessages);
    }

    // ----------------------------------------------------------------
    //  AFSimple_Calculate
    // ----------------------------------------------------------------

    [Fact]
    public void AFSimple_Calculate_Sum()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["a"] = "10";
        ctx.FieldValues["b"] = "20";
        ctx.FieldValues["c"] = "30";
        PdfJsEngine.Execute("AFSimple_Calculate('SUM', ['a','b','c']);", ctx);
        Assert.Equal("60", ctx.EventValue);
    }

    [Fact]
    public void AFSimple_Calculate_Avg()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["x"] = "10";
        ctx.FieldValues["y"] = "20";
        PdfJsEngine.Execute("AFSimple_Calculate('AVG', ['x','y']);", ctx);
        Assert.Equal("15", ctx.EventValue);
    }

    [Fact]
    public void AFSimple_Calculate_Product()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["p"] = "3";
        ctx.FieldValues["q"] = "4";
        PdfJsEngine.Execute("AFSimple_Calculate('PRD', ['p','q']);", ctx);
        Assert.Equal("12", ctx.EventValue);
    }

    [Fact]
    public void AFSimple_Calculate_Min()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["v1"] = "7";
        ctx.FieldValues["v2"] = "3";
        ctx.FieldValues["v3"] = "9";
        PdfJsEngine.Execute("AFSimple_Calculate('MIN', ['v1','v2','v3']);", ctx);
        Assert.Equal("3", ctx.EventValue);
    }

    [Fact]
    public void AFSimple_Calculate_Max()
    {
        var ctx = new PdfJsExecutionContext();
        ctx.FieldValues["v1"] = "7";
        ctx.FieldValues["v2"] = "3";
        ctx.FieldValues["v3"] = "9";
        PdfJsEngine.Execute("AFSimple_Calculate('MAX', ['v1','v2','v3']);", ctx);
        Assert.Equal("9", ctx.EventValue);
    }

    // ----------------------------------------------------------------
    //  AFNumber_Keystroke
    // ----------------------------------------------------------------

    [Fact]
    public void AFNumber_Keystroke_ValidNumber_Passes()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "12", EventChange = "3" };
        PdfJsEngine.Execute("AFNumber_Keystroke(2, 0, 0, 0, '', true);", ctx);
        Assert.True(ctx.ValidationPassed);
    }

    [Fact]
    public void AFNumber_Keystroke_NonNumericChange_FailsRc()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "12", EventChange = "x" };
        PdfJsEngine.Execute("AFNumber_Keystroke(2, 0, 0, 0, '', true);", ctx);
        // rc should be false
        Assert.False(ctx.ValidationPassed);
    }

    // ----------------------------------------------------------------
    //  util functions
    // ----------------------------------------------------------------

    [Fact]
    public void Util_Printd_FormatsDate()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute(
            "event.value = util.printd('mm/dd/yyyy', '2024-03-15');", ctx);
        Assert.Equal("03/15/2024", ctx.EventValue);
        Assert.Null(ctx.Error);
    }

    // ----------------------------------------------------------------
    //  Multi-statement / conditional script
    // ----------------------------------------------------------------

    [Fact]
    public void Execute_ConditionalScript_WorksCorrectly()
    {
        var ctx = new PdfJsExecutionContext { EventValue = "15" };
        PdfJsEngine.Execute(
            @"var v = Number(event.value);
              if (v > 10) {
                  event.value = 'high';
              } else {
                  event.value = 'low';
              }",
            ctx);
        Assert.Equal("high", ctx.EventValue);
    }

    [Fact]
    public void Execute_ForLoop_Accumulates()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute(
            @"var sum = 0;
              for (var i = 1; i <= 5; i++) sum += i;
              event.value = String(sum);",
            ctx);
        Assert.Equal("15", ctx.EventValue);
    }

    // ----------------------------------------------------------------
    //  Timeout / memory guard
    // ----------------------------------------------------------------

    [Fact]
    public void Execute_InfiniteLoop_CapturesTimeoutError()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("while (true) {}", ctx);
        Assert.NotNull(ctx.Error);
    }

    // ----------------------------------------------------------------
    //  app.response
    // ----------------------------------------------------------------

    [Fact]
    public void Execute_AppResponse_ReturnsDefaultValue()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("var r = app.response('Enter value', 'Title', 'default'); event.value = r;", ctx);
        Assert.Equal("default", ctx.EventValue);
        Assert.Null(ctx.Error);
    }

    // ----------------------------------------------------------------
    //  PdfJsExecutionContext defaults
    // ----------------------------------------------------------------

    [Fact]
    public void Context_DefaultValidationPassed_IsTrue()
    {
        var ctx = new PdfJsExecutionContext();
        Assert.True(ctx.ValidationPassed);
    }

    [Fact]
    public void Context_AlertMessages_StartsEmpty()
    {
        var ctx = new PdfJsExecutionContext();
        Assert.Empty(ctx.AlertMessages);
    }

    [Fact]
    public void Context_ModifiedFieldValues_StartsEmpty()
    {
        var ctx = new PdfJsExecutionContext();
        Assert.Empty(ctx.ModifiedFieldValues);
    }
}
