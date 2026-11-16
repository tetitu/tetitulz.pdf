// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The array and string operators at their edges: a length of zero, a count that exactly matches
/// the stack, an index exactly at the end, a match at either end of a string, and an empty string
/// searched for in an empty string. Each is the side of a boundary the operator accepts, beside the
/// side it refuses.
/// </summary>
public sealed class ArrayStringBoundaryTests
{
    // ------------------------------------------------------------------ zero lengths and exact counts

    /// <summary>A length of zero is a legal, empty object (PLRM <c>array</c>, <c>string</c>, <c>packedarray</c>); −1 is a rangecheck.</summary>
    [Theory]
    [InlineData("0 array length")]
    [InlineData("0 string length")]
    [InlineData("0 packedarray length")]
    public void ZeroLength_IsAnEmptyObject(string program)
    {
        Assert.Equal(0, Single(program).Num);
    }

    [Theory]
    [InlineData("{ -1 array } stopped")]
    [InlineData("{ -1 string } stopped")]
    [InlineData("{ -1 packedarray } stopped")]
    public void NegativeLength_IsRefused(string program)
    {
        Assert.True(Single(program).AsBool());
    }

    /// <summary>
    /// <c>packedarray</c> and <c>astore</c> may take exactly as many operands as the stack holds —
    /// all of them; one more is a stackunderflow.
    /// </summary>
    [Theory]
    [InlineData("1 2 3 3 packedarray length", 3)]
    [InlineData("1 2 3 3 array astore length", 3)]
    public void ExactlyTheWholeStack_IsTaken(string program, int expectedLength)
    {
        Assert.Equal(expectedLength, Single(program).Num);
    }

    [Theory]
    [InlineData("{ 1 2 3 packedarray } stopped")]
    [InlineData("{ 1 2 3 array astore } stopped")]
    public void OneMoreThanTheStack_IsRefused(string program)
    {
        IReadOnlyList<PsObject> stack = Run(program);
        Assert.True(stack[stack.Count - 1].AsBool());
    }

    // ------------------------------------------------------------------ indices

    /// <summary>
    /// The last element is index length − 1, and index length is past the end — a rangecheck for
    /// <c>get</c> and <c>put</c>, on arrays and strings alike.
    /// </summary>
    [Theory]
    [InlineData("{ [1 2 3] 3 get } stopped")]
    [InlineData("{ (abc) 3 get } stopped")]
    [InlineData("{ [1 2 3] 3 9 put } stopped")]
    [InlineData("{ (abc) 3 65 put } stopped")]
    public void IndexAtTheLength_IsRefused(string program)
    {
        IReadOnlyList<PsObject> stack = Run(program);
        Assert.True(stack[stack.Count - 1].AsBool());
    }

    [Theory]
    [InlineData("[1 2 3] 2 get", 3)]
    [InlineData("(abc) 2 get", 99)]
    public void TheLastIndex_IsGot(string program, int expected)
    {
        Assert.Equal(expected, Single(program).Num);
    }

    [Theory]
    [InlineData("[1 2 3] dup 2 9 put 2 get", 9)]
    [InlineData("(abc) dup 2 65 put 2 get", 65)]
    public void TheLastIndex_IsPut(string program, int expected)
    {
        Assert.Equal(expected, Single(program).Num);
    }

    // ------------------------------------------------------------------ search and anchorsearch

    /// <summary>
    /// <c>search</c> finds a match at the very start and at the very end of the string; the stack
    /// it leaves, bottom first, is post, match, pre, true.
    /// </summary>
    [Theory]
    [InlineData("(abc) (a) search", "bc", "a", "")]
    [InlineData("(abc) (c) search", "", "c", "ab")]
    public void Search_FindsAMatchAtEitherEnd(string program, string post, string match, string pre)
    {
        IReadOnlyList<PsObject> stack = Run(program);

        Assert.Equal(4, stack.Count);
        Assert.Equal(post, stack[0].AsString().AsLatin1());
        Assert.Equal(match, stack[1].AsString().AsLatin1());
        Assert.Equal(pre, stack[2].AsString().AsLatin1());
        Assert.True(stack[3].AsBool());
    }

    /// <summary>The control: a string that is not there leaves the target and false.</summary>
    [Fact]
    public void Search_ForAbsentText_LeavesTheTargetAndFalse()
    {
        IReadOnlyList<PsObject> stack = Run("(abc) (x) search");

        Assert.Equal(2, stack.Count);
        Assert.Equal("abc", stack[0].AsString().AsLatin1());
        Assert.False(stack[1].AsBool());
    }

    /// <summary>The empty string occurs at the start of every string — the empty one included.</summary>
    [Fact]
    public void Search_ForTheEmptyStringInTheEmptyString_Matches()
    {
        IReadOnlyList<PsObject> stack = Run("() () search");

        Assert.Equal(4, stack.Count);
        Assert.True(stack[3].AsBool());
    }

    /// <summary>
    /// <c>anchorsearch</c> matches a prefix — a proper one, and one as long as the string itself —
    /// leaving post, match, true; a string that does not start with it leaves the target and false.
    /// </summary>
    [Theory]
    [InlineData("(abc) (ab) anchorsearch", "c", "ab")]
    [InlineData("(abc) (abc) anchorsearch", "", "abc")]
    public void AnchorSearch_MatchesAPrefix(string program, string post, string match)
    {
        IReadOnlyList<PsObject> stack = Run(program);

        Assert.Equal(3, stack.Count);
        Assert.Equal(post, stack[0].AsString().AsLatin1());
        Assert.Equal(match, stack[1].AsString().AsLatin1());
        Assert.True(stack[2].AsBool());
    }

    [Theory]
    [InlineData("(abc) (bc) anchorsearch")]
    [InlineData("(abc) (abcd) anchorsearch")]
    public void AnchorSearch_ForANonPrefix_LeavesTheTargetAndFalse(string program)
    {
        IReadOnlyList<PsObject> stack = Run(program);

        Assert.Equal(2, stack.Count);
        Assert.Equal("abc", stack[0].AsString().AsLatin1());
        Assert.False(stack[1].AsBool());
    }

    // ------------------------------------------------------------------ harness

    private static IReadOnlyList<PsObject> Run(string source)
    {
        PostScriptDiagnostics.Clear();
        var interpreter = new PsInterpreter(null);
        interpreter.Run(new PsParser(new PsScanner(Encoding.ASCII.GetBytes(source))).ReadAll());
        return interpreter.Operands.Items;
    }

    private static PsObject Single(string source)
    {
        IReadOnlyList<PsObject> stack = Run(source);
        Assert.Single(stack);
        return stack[0];
    }
}

/// <summary>The single-array length limit, set through <see cref="PsLimits"/>.</summary>
[Collection(PsLimitsCollection.Name)]
public sealed class ArrayLengthLimitTests
{
    /// <summary>
    /// <c>MaxSingleArrayLength</c> is the longest array allowed, exactly: at a limit of 5, a
    /// five-element array is made and a six-element one is refused.
    /// </summary>
    [Theory]
    [InlineData("5 array length", false)]
    [InlineData("{ 6 array } stopped", true)]
    public void TheLimitIsTheLongestArrayAllowed(string program, bool refused)
    {
        try
        {
            PsLimits.MaxSingleArrayLength = 5;
            PostScriptDiagnostics.Clear();
            var interpreter = new PsInterpreter(null);
            interpreter.Run(new PsParser(new PsScanner(Encoding.ASCII.GetBytes(program))).ReadAll());
            PsObject top = interpreter.Operands.Items[interpreter.Operands.Items.Count - 1];

            if (refused) Assert.True(top.AsBool());
            else Assert.Equal(5, top.Num);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }

    /// <summary>
    /// <c>put</c> into a string takes an integer in 0..255 (PLRM: <c>rangecheck</c> otherwise). It
    /// masked the value to a byte, so <c>256</c> stored 0 and a program probing the bound with
    /// <c>stopped</c> was told the store succeeded.
    /// </summary>
    [Theory]
    [InlineData("{ (abc) 0 255 put } stopped", false)]
    [InlineData("{ (abc) 0 256 put } stopped", true)]
    [InlineData("{ (abc) 0 -1 put } stopped", true)]
    public void StringPut_RefusesAValueOutsideAByte(string program, bool refused)
    {
        PostScriptDiagnostics.Clear();
        var interpreter = new PsInterpreter(null);
        interpreter.Run(new PsParser(new PsScanner(Encoding.ASCII.GetBytes(program))).ReadAll());
        PsObject top = interpreter.Operands.Items[interpreter.Operands.Items.Count - 1];

        Assert.Equal(refused, top.AsBool());
    }
}
