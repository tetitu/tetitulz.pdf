// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Text.RegularExpressions;
using Jint;
using Tetitulz.Pdf.Base.Scripting;
using Tetitulz.Pdf.Core.Scripting;
using Tetitulz.Pdf.Plugins.JavaScript;
using Xunit;

namespace Tetitulz.Pdf.Plugins.JavaScript.Tests;

/// <summary>
/// The string built-ins a script can make larger than its operands are bounded before they build
/// (<see cref="ScriptBuiltinBounds"/>): Jint asks its memory limit when a statement starts, so one
/// call of <c>repeat</c> built 190 MiB under a 32 MiB limit, and a <c>join</c> of two hundred
/// references to one megabyte string 1.4 GB — each succeeding.
/// </summary>
public class ScriptBuiltinBoundsTests
{
    /// <summary>
    /// Every bounded built-in answers as the native one did — values, errors, how many times it
    /// converts an operand, an array met again while joined, every substitution pattern — held by
    /// running one battery in an engine without the prelude and one with it.
    /// </summary>
    [Fact]
    public void TheBoundedBuiltins_AnswerAsTheNativeOnesDid()
    {
        const string battery = @"
var out = [];
function t(f) { try { var v = f(); out.push(typeof v === 'string' ? 'S:' + v : 'V:' + JSON.stringify(v)); } catch (e) { out.push('E:' + e.name); } }
t(function () { return 'ab'.repeat(3) + '|' + 'ab'.repeat(0) + '|' + 'ab'.repeat(2.7) + '|' + 'ab'.repeat('2'); });
t(function () { return 'a'.repeat(-1); });
t(function () { return 'a'.repeat(Infinity); });
t(function () { return String.prototype.repeat.call(null, 2); });
t(function () { return String.prototype.repeat.call(12, 2); });
t(function () { return 'x'.padStart(5) + '|' + 'x'.padStart(5, 'ab') + '|' + 'x'.padEnd(5, '') + '|' + 'abc'.padEnd(2) + '|' + 'x'.padStart(NaN) + '|' + 'x'.padEnd(4, 7); });
t(function () { return 'a'.concat(1, null, undefined, { toString: function () { return 'o'; } }, [2, 3]); });
t(function () { return [1, null, undefined, 'x', { toString: function () { return 'T'; } }].join(); });
t(function () { return [1, 2].join('-') + '|' + [].join() + '|' + [, 'a', , ].join('|') + '|' + [1, 2].join(undefined) + '|' + [1, 2].join(null); });
t(function () { var c = [1]; c.push(c); c.push(2); return c.join('-'); });
t(function () { var a = [1], b = [a]; a.push(b); return String(a) + '|' + String(b); });
t(function () { return [[1, 2], [3]].join(';') + '|' + String([1, [2, 3]]); });
t(function () { return Array.prototype.join.call({ length: 3, 0: 'a', 2: 'c' }, '+') + '|' + Array.prototype.join.call('abc', '-'); });
t(function () { return Array.prototype.join.call(null); });
t(function () { var n = 0, o = { toString: function () { n++; return 'o'; } }; [o, o].join(); return n; });
t(function () { var g = 0, a = { get length() { g++; return 2; }, 0: 'a', 1: 'b' }; Array.prototype.join.call(a); return g; });
t(function () { var n = 0, sep = { toString: function () { n++; return '+'; } }; return [1, 2, 3].join(sep) + n; });
t(function () { return [1, 'b', null].toLocaleString() + '|' + [].toLocaleString(); });
t(function () { var c = [1]; c.push(c); return c.toLocaleString(); });
t(function () { return 'aXbXc'.replace('X', '-') + '|' + 'aXbXc'.replace(/X/g, '-') + '|' + 'aXbXc'.replaceAll('X', '[$&]'); });
t(function () { return 'aXb'.replace(/(a)(X)/, '$2$1') + '|' + 'aXb'.replace('X', '$$') + '|' + 'aXb'.replace('X', '$&$&') + '|' + 'aXb'.replace('X', '$`') + '|' + 'aXb'.replace('X', ""$'""); });
t(function () { return 'aXb'.replace(/(X)/, '$0') + '|' + 'aXb'.replace(/(X)/, '$10') + '|' + 'aXb'.replace(/(X)/, '$01') + '|' + 'aXb'.replace(/(X)/, '$2') + '|' + 'aXb'.replace(/X/, '$1') + '|' + 'ab'.replace('b', '$'); });
t(function () { return 'abcdefghijkl'.replace(/(a)(b)(c)(d)(e)(f)(g)(h)(i)(j)(k)(l)/, '$12$11$10$1$9') + '|' + 'abc'.replace(/(b)/, '$1$'); });
t(function () { return 'aXb'.replace(/(?<n>X)/, '[$<n>]') + '|' + 'aXb'.replace(/(?<n>X)/, '[$<m>]') + '|' + 'aXb'.replace(/(?<n>X)/, '[$<n') + '|' + 'aXb'.replace(/(X)/, '[$<n>]'); });
t(function () { return 'aXbX'.replace(/X/g, function (m, p, s) { return m + p + s.length; }) + '|' + 'aXbX'.replace(/X/g, function () { return 7; }) + '|' + 'aXbX'.replaceAll(/X/g, function (m) { return m.toLowerCase(); }); });
t(function () { return 'aXb'.replace(/(?<n>X)/, function () { return typeof arguments[arguments.length - 1]; }); });
t(function () { return 'abc'.replace(/(?:)/g, '-') + '|' + 'ab'.replace('', '$&_') + '|' + 'ä😀'.replace(/😀/u, '$&!'); });
t(function () { return 'aXb'.replaceAll(/X/, '-'); });
t(function () { return 'xyz'.replace({ toString: function () { return 'y'; } }, 'Y'); });
t(function () { return JSON.stringify({ a: [1, 'x', null, true], b: { c: 'd' }, u: undefined, f: function () { } }); });
t(function () { return JSON.stringify({ a: [1, { b: 2 }] }, null, 2) + JSON.stringify([1, [2]], null, '\t') + JSON.stringify({ a: 1 }, null, 20); });
t(function () { return JSON.stringify({ a: 1, b: 'x', c: [3] }, function (k, v) { return typeof v === 'number' ? v * 2 : v; }); });
t(function () { return JSON.stringify({ a: 1, b: 2, c: { a: 3, z: 4 } }, ['c', 'a']); });
t(function () { return JSON.stringify(new Date(0)) + JSON.stringify('x') + JSON.stringify(undefined) + JSON.stringify(null); });
t(function () { return JSON.stringify({ k: 1 }, function (k, v) { return k === '' ? v : this === undefined; }); });
t(function () { var o = {}; o.o = o; return JSON.stringify(o); });
t(function () { return [String.prototype.repeat.name, String.prototype.repeat.length, String.prototype.padStart.length, String.prototype.concat.length, String.prototype.replace.length, Array.prototype.join.length, JSON.stringify.length, Object.getOwnPropertyDescriptor(String.prototype, 'repeat').enumerable, Object.getOwnPropertyDescriptor(Array.prototype, 'join').writable]; });
JSON.stringify(out);";

        // A recursion limit, so a lost cycle rule fails the comparison instead of the process.
        string native = new Engine(o => o.LimitRecursion(64)).Evaluate(battery).AsString();
        var bounded = new Engine(o => o.LimitRecursion(64));
        bounded.Execute(ScriptBuiltinBounds.Prelude(1_000_000));
        Assert.Equal(native, bounded.Evaluate(battery).AsString());
        Assert.Contains("\"S:ababab||abab|abab\"", native, StringComparison.Ordinal);   // potency: the battery ran
    }

    /// <summary>
    /// Through the form engine each multiplier is refused past the script's own memory limit, as
    /// characters, and failing closed; its control, one size under, is built. Each script ends on
    /// the statement that builds, so without the prelude no limit is asked after it. Where the
    /// prelude counts before anything is built the refusal must be its own (<paramref name="counted"/>):
    /// Jint charges some built-ins as they go, which would refuse them anyway and hide a lost count.
    /// Where its callbacks run statements, the memory limit between them may refuse first.
    /// </summary>
    [Theory]
    [InlineData("'x'.repeat(100000000)", "'x'.repeat(1000)", true)]
    [InlineData("'x'.padStart(200000000)", "'x'.padStart(1000)", true)]
    [InlineData("'x'.padEnd(200000000, 'ab')", "'x'.padEnd(1000, 'ab')", true)]
    [InlineData("big.concat(big, big, big, big, big, big, big, big, big, big, big, big, big, big, big, big, big)", "big.concat(big)", true)]
    [InlineData("new Array(200).fill(big).join('')", "new Array(2).fill(big).join('')", true)]
    [InlineData("String(new Array(200).fill(big))", "String(new Array(2).fill(big))", true)]
    [InlineData("new Array(200).fill(big).toLocaleString()", "new Array(2).fill(big).toLocaleString()", true)]
    [InlineData("new Array(100000000).join('x')", "new Array(1000).join('x')", true)]
    [InlineData("new Array(100000000).join('')", "new Array(1000).fill('y').join('')", true)]   // holes: counted by length
    [InlineData("JSON.stringify(new Array(200).fill(big))", "JSON.stringify(new Array(2).fill(big))", false)]   // counted in a callback
    [InlineData("big.replace(/x+/, '$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&$&')", "big.replace(/x+/, '$&')", false)]
    [InlineData("'aaaaaaaaaaaaaaaaaaaa'.replace(/a/g, big)", "'a'.replace(/a/g, big)", false)]
    [InlineData("'aaaaaaaaaaaaaaaaaaaa'.replace(/a/g, function () { return big; })", "'a'.replace(/a/g, function () { return big; })", false)]
    [InlineData("'aaaaaaaaaaaaaaaaaaaa'.replaceAll('a', big)", "'a'.replaceAll('a', big)", false)]
    public void AdversarialStringBuiltins_AreRefusedPastTheLimit_AndBuiltUnderIt(string hostile, string control, bool counted)
    {
        const string prologue = "var big = 'x'.repeat(1000000); event.value = '' + ";

        var refused = new PdfJsExecutionContext();
        PdfJsEngine.Execute(prologue + hostile + ".length;", refused);
        Assert.NotNull(refused.Error);
        Assert.False(refused.ValidationPassed);
        bool byCount = refused.ValidationMessage.Contains("characters; a script may build");
        Assert.True(counted ? byCount : byCount || refused.ValidationMessage.Contains("memory limit"), refused.ValidationMessage);

        var built = new PdfJsExecutionContext();
        PdfJsEngine.Execute(prologue + control + ".length;", built);
        Assert.Null(built.Error);
        Assert.True(int.Parse(built.EventValue) > 0);
    }

    /// <summary>
    /// A regular expression runs inside one call, out of the statement timeout's reach: it now stops
    /// at the script's own five seconds, where Jint's ten held it.
    /// </summary>
    [Fact]
    public void AdversarialBacktrackingPattern_StopsAtTheScriptsTimeout()
    {
        var ctx = new PdfJsExecutionContext();
        PdfJsEngine.Execute("event.value = '' + /^(a+)+$/.test('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab');", ctx);

        var timeout = Assert.IsType<RegexMatchTimeoutException>(ctx.Error);
        Assert.Equal(TimeSpan.FromSeconds(5), timeout.MatchTimeout);
        Assert.False(ctx.ValidationPassed);
    }

    [Fact]
    public void ThePrelude_RefusesANonPositiveLimit_AndCountsTwoBytesACharacter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScriptBuiltinBounds.Prelude(0));
        Assert.Equal(16L * 1024 * 1024, ScriptBuiltinBounds.CharactersFor(32L * 1024 * 1024));
        Assert.Contains("(4096);", ScriptBuiltinBounds.Prelude(4096), StringComparison.Ordinal);
    }
}
