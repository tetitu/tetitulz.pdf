// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using Tetitulz.Pdf.Base.Scripting;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// One call of a built-in that allocates from a number — <c>new ArrayBuffer(n)</c>, a typed array of
/// length <c>n</c>, <c>resize</c> — or joins past its operands outside the array's join (a typed
/// array's, <c>String.raw</c>, a regular expression's <c>[Symbol.replace]</c>) is refused with a
/// <c>RangeError</c> past the script's limit: Jint asks the memory limit only when a statement
/// starts, so <c>new ArrayBuffer(1e9)</c> allocated a gigabyte. Small ones still work, and the
/// constructors keep their identity (the controls).
/// </summary>
public sealed class AllocatingBuiltinBoundsTests
{
    private static string Run(string body)
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("try { " + body + " } catch (e) { event.value = (e instanceof RangeError) ? 'range' : 'other:' + e; }", ctx);
        return ctx.EventValue;
    }

    [Theory]
    [InlineData("var b = new ArrayBuffer(1e9); event.value = 'allocated';")]
    [InlineData("var b = new Float64Array(2e8); event.value = 'allocated';")]
    [InlineData("var b = new ArrayBuffer(8, { maxByteLength: 1e9 }); event.value = 'allocated';")]
    [InlineData("event.value = new Uint8Array(100000).join('x'.repeat(1000)).length;")]
    [InlineData("var raw = []; var big = 'y'.repeat(100000); for (var i = 0; i < 2000; i++) raw.push(big); event.value = String.raw({ raw: raw }).length;")]
    [InlineData("var big = 'z'.repeat(100000); event.value = /a/g[Symbol.replace]('a'.repeat(1000), function () { return big; }).length;")]
    public void AdversarialAllocatingBuiltin_IsARangeError(string script)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal("range", Run(script));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256L * 1024 * 1024);
    }

    /// <summary>
    /// <c>JSON.stringify</c> with an allow-list (an array replacer) was not counted: the list filters an
    /// object's keys and never an array's elements, so two hundred references to one megabyte were
    /// two hundred megabytes in one call. Now counted, the call is stopped — by the RangeError, or by
    /// the memory limit the counting pass gives a statement to check — and never builds its result.
    /// </summary>
    [Fact]
    public void AdversarialStringifyUnderAnAllowList_IsStoppedBeforeItBuilds()
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        string value = Run("var big = 'q'.repeat(1000000); event.value = JSON.stringify(new Array(200).fill(big), []).length;");
        Assert.NotEqual("200000201", value);   // 200 strings of 1 000 000, quoted, comma-separated, bracketed
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 256L * 1024 * 1024);
    }

    [Theory]
    [InlineData("var a = new Uint8Array(16); event.value = (a instanceof Uint8Array) + ',' + (a.constructor === Uint8Array) + ',' + a.length;", "true,true,16")]
    [InlineData("var b = new ArrayBuffer(32); event.value = (b instanceof ArrayBuffer) + ',' + b.byteLength;", "true,32")]
    [InlineData("event.value = new Int16Array([1, 2, 3]).join('-');", "1-2-3")]
    [InlineData("event.value = String.raw({ raw: ['a', 'b', 'c'] }, 1, 2);", "a1b2c")]
    [InlineData("event.value = 'aXa'.replace(/a/g, '[$&]') + /b/g[Symbol.replace]('bb', 'c');", "[a]X[a]cc")]
    [InlineData("event.value = JSON.stringify({ b: 1, a: [1, { a: 2, c: 3 }], c: 4 }, ['a', 'b']);", "{\"a\":[1,{\"a\":2}],\"b\":1}")]
    public void SmallCalls_StillWork(string script, string expected)
        => Assert.Equal(expected, Run(script));
}
