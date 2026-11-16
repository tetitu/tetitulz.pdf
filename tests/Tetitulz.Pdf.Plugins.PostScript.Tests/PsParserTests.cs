// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Parser behaviour, asserted on the object graph it builds. The nesting guard has its own
/// adversarial test because a recursive-descent parser fails it by overflowing the stack, which is
/// uncatchable and takes the process with it.
/// </summary>
public sealed class PsParserTests
{
    private static List<PsObject> Parse(string source)
    {
        PostScriptDiagnostics.Clear();
        var scanner = new PsScanner(Encoding.ASCII.GetBytes(source));
        return new PsParser(scanner).ReadAll();
    }

    // ==================================================================
    //  Procedures
    // ==================================================================

    [Fact]
    public void Procedure_BecomesAnExecutableArrayOfItsContents()
    {
        var objs = Parse("{ 1 2 add }");

        Assert.Single(objs);
        Assert.Equal(PsType.Array, objs[0].Type);
        Assert.True(objs[0].IsExecutable, "a {…} literal must be executable.");

        var proc = objs[0].AsArray();
        Assert.Equal(3, proc.Length);
        Assert.Equal(1, proc[0].Num);
        Assert.Equal(2, proc[1].Num);
        Assert.Equal("add", proc[2].AsName());
        Assert.True(proc[2].IsExecutable);
    }

    [Fact]
    public void Procedure_NestsToArbitraryDepthWithinTheCap()
    {
        var objs = Parse("{ 1 { 2 { 3 } } }");

        var outer = objs[0].AsArray();
        Assert.Equal(2, outer.Length);

        var mid = outer[1].AsArray();
        Assert.Equal(2, mid.Length);

        var inner = mid[1].AsArray();
        Assert.Equal(1, inner.Length);
        Assert.Equal(3, inner[0].Num);
    }

    [Fact]
    public void Procedure_IsParsedOnce_SoTheSameArrayInstanceIsReused()
    {
        // Two references to one procedure object must be the same PsArray: PostScript composite
        // semantics require `put` through one to be visible through the other.
        var objs = Parse("{ 1 }");
        var a = objs[0].AsArray();
        var b = objs[0].AsArray();

        Assert.Same(a, b);
    }

    /// <summary>An empty procedure is legal and common — `{ } loop`, `{ } forall`.</summary>
    [Fact]
    public void EmptyProcedure_IsAZeroLengthExecutableArray()
    {
        var objs = Parse("{}");

        Assert.Single(objs);
        Assert.Equal(PsType.Array, objs[0].Type);
        Assert.Equal(0, objs[0].AsArray().Length);
    }

    // ==================================================================
    //  Simple objects
    // ==================================================================

    [Fact]
    public void LiteralName_IsNotExecutable_AndExecutableNameIs()
    {
        var objs = Parse("/foo foo");

        Assert.Equal(PsType.Name, objs[0].Type);
        Assert.False(objs[0].IsExecutable);
        Assert.True(objs[1].IsExecutable);
    }

    [Fact]
    public void IntegerAndReal_KeepDistinctTypes()
    {
        var objs = Parse("5 5.0");

        Assert.Equal(PsType.Integer, objs[0].Type);
        Assert.Equal(PsType.Real, objs[1].Type);
        Assert.Equal("integertype", objs[0].TypeName);
        Assert.Equal("realtype", objs[1].TypeName);
    }

    /// <summary>
    /// '[' and ']' are operators in PostScript, not syntax — a program may call them by name or
    /// leave them unbalanced across a procedure boundary — so the parser must hand them on as
    /// executable names rather than building an array itself.
    /// </summary>
    [Fact]
    public void ArrayBrackets_AreEmittedAsExecutableNames()
    {
        var objs = Parse("[ 1 2 ]");

        Assert.Equal(4, objs.Count);
        Assert.Equal("[", objs[0].AsName());
        Assert.True(objs[0].IsExecutable);
        Assert.Equal("]", objs[3].AsName());
    }

    [Fact]
    public void DictBrackets_AreEmittedAsExecutableNames()
    {
        var objs = Parse("<< /K 1 >>");

        Assert.Equal("<<", objs[0].AsName());
        Assert.Equal(">>", objs[3].AsName());
    }

    [Fact]
    public void String_BecomesAPsStringObject()
    {
        var objs = Parse("(hi)");

        Assert.Single(objs);
        Assert.Equal(PsType.String, objs[0].Type);
        Assert.Equal("hi", objs[0].AsString().AsLatin1());
    }

    // ==================================================================
    //  Malformed input
    // ==================================================================

    /// <summary>
    /// An unclosed '{' must not discard what was already parsed — a truncated file should still
    /// yield the drawing up to the truncation.
    /// </summary>
    [Fact]
    public void MalformedUnclosedProcedure_IsClosedImplicitlyAndReported()
    {
        var objs = Parse("{ 1 2 add");

        Assert.Single(objs);
        var proc = objs[0].AsArray();
        Assert.Equal(3, proc.Length);
        Assert.Contains("unclosed", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void MalformedUnbalancedClose_IsIgnoredAndReported()
    {
        var objs = Parse("1 } 2");

        // The stray '}' becomes a null placeholder rather than eating a real object.
        Assert.Equal(3, objs.Count);
        Assert.Equal(1, objs[0].Num);
        Assert.Equal(PsType.Null, objs[1].Type);
        Assert.Equal(2, objs[2].Num);
    }

    /// <summary>
    /// The parser is iterative precisely so this cannot overflow the CLR stack. The cap then bounds
    /// the builder list, so a hostile file allocates a bounded amount before being refused.
    /// </summary>
    [Fact]
    public void DeeplyNestedProcedureLiteral_IsRefusedWithoutStackOverflow()
    {
        string source = new string('{', 100_000);

        PostScriptDiagnostics.Clear();
        var scanner = new PsScanner(Encoding.ASCII.GetBytes(source));
        var parser  = new PsParser(scanner);

        var ex = Record.Exception(() => parser.ReadAll());

        Assert.Null(ex);
        Assert.Contains("nesting exceeds", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void AdversarialAlternatingBraces_TerminateWithoutHanging()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 50_000; i++) sb.Append("{}");

        PostScriptDiagnostics.Clear();
        var scanner = new PsScanner(Encoding.ASCII.GetBytes(sb.ToString()));
        var parser  = new PsParser(scanner);

        var objs = parser.ReadAll();

        Assert.Equal(50_000, objs.Count);
    }

    /// <summary>
    /// Past the nesting cap the parser skips to the brace that closes the outermost open procedure
    /// — every open brace, the one that tripped the cap included — so the next top-level object is
    /// the one after the procedure. It stopped one brace short, and that brace reached the
    /// interpreter as an unbalanced <c>}</c>: a spurious report and a stray null operand.
    /// </summary>
    [Fact]
    public void NestingPastTheCap_SkipsTheWholeProcedure()
    {
        try
        {
            PsLimits.MaxParseNestingDepth = 2;
            var objs = Parse("{ { { 1 } } } 5");

            Assert.Equal(2, objs.Count);
            Assert.Equal(PsType.Array, objs[0].Type);
            Assert.Equal(PsType.Integer, objs[1].Type);
            Assert.Equal(5, objs[1].Num);
            Assert.DoesNotContain("unbalanced", PostScriptDiagnostics.LastError ?? "");
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }
}
