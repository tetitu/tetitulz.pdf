// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// Execution semantics, asserted on the operand stack the program leaves behind. Every case here
/// would pass differently — not merely fail — if the operator were stubbed, which is the point:
/// <c>5 2 idiv</c> returning 2 and <c>5.0 2 idiv</c> raising typecheck cannot both come from a
/// float-only evaluator.
/// </summary>
public sealed class PsInterpreterTests
{
    /// <summary>Runs a program and returns the operand stack, bottom-first.</summary>
    private static IReadOnlyList<PsObject> Run(string source,
                                               PostScriptConversionOptions? options = null)
    {
        PostScriptDiagnostics.Clear();
        var interpreter = new PsInterpreter(options);
        var parser = new PsParser(new PsScanner(Encoding.ASCII.GetBytes(source)));
        interpreter.Run(parser.ReadAll());
        return interpreter.Operands.Items;
    }

    private static PsObject Single(string source)
    {
        var stack = Run(source);
        Assert.Single(stack);
        return stack[0];
    }

    private static double Num(string source) => Single(source).Num;

    // ==================================================================
    //  Arithmetic — integer/real distinction
    // ==================================================================

    [Fact]
    public void Add_OfTwoIntegers_StaysAnInteger()
    {
        var r = Single("2 3 add");
        Assert.Equal(PsType.Integer, r.Type);
        Assert.Equal(5, r.Num);
    }

    [Fact]
    public void Add_WithAReal_ProducesAReal()
    {
        var r = Single("2 3.5 add");
        Assert.Equal(PsType.Real, r.Type);
        Assert.Equal(5.5, r.Num);
    }

    /// <summary>
    /// <c>div</c> always yields a real, even when the operands divide evenly — the distinction from
    /// <c>idiv</c> that a float-only evaluator cannot express.
    /// </summary>
    [Fact]
    public void Div_OfEvenlyDivisibleIntegers_StillProducesAReal()
    {
        var r = Single("6 3 div");
        Assert.Equal(PsType.Real, r.Type);
        Assert.Equal(2.0, r.Num);
    }

    [Fact]
    public void IDiv_TruncatesTowardZero_AndYieldsAnInteger()
    {
        Assert.Equal(2, Num("5 2 idiv"));
        Assert.Equal(-2, Num("-5 2 idiv"));
        Assert.Equal(PsType.Integer, Single("5 2 idiv").Type);
    }

    /// <summary>
    /// <c>5.0 2 idiv</c> is a typecheck, not 2. Programs distinguish int from real by trying this.
    /// </summary>
    [Fact]
    public void IDiv_WithARealOperand_RaisesTypecheck()
    {
        var stack = Run("5.0 2 idiv");

        Assert.Empty(stack);
        Assert.Contains("TypeCheck", PostScriptDiagnostics.LastError);
    }

    /// <summary>
    /// PLRM: the remainder takes the sign of the <i>dividend</i>, so <c>-5 3 mod</c> is -2 rather
    /// than the +1 a floor-based modulo would give.
    /// </summary>
    [Fact]
    public void Mod_KeepsTheSignOfTheDividend()
    {
        Assert.Equal(2, Num("5 3 mod"));
        Assert.Equal(-2, Num("-5 3 mod"));
        Assert.Equal(2, Num("5 -3 mod"));
    }

    /// <summary><c>atan</c> answers in degrees in [0, 360), not radians.</summary>
    [Theory]
    [InlineData("0 1 atan", 0)]
    [InlineData("1 0 atan", 90)]
    [InlineData("-1 0 atan", 270)]
    [InlineData("1 1 atan", 45)]
    [InlineData("-1 -1 atan", 225)]
    public void Atan_ReturnsDegreesInZeroTo360(string source, double expected)
        => Assert.Equal(expected, Num(source), 6);

    /// <summary><c>base exponent exp</c> — the operand order is easy to invert.</summary>
    [Fact]
    public void Exp_TakesBaseThenExponent()
    {
        Assert.Equal(8.0, Num("2 3 exp"), 10);
        Assert.Equal(9.0, Num("3 2 exp"), 10);
    }

    [Fact]
    public void Sin_And_Cos_TakeDegrees()
    {
        Assert.Equal(1.0, Num("90 sin"), 10);
        Assert.Equal(1.0, Num("0 cos"), 10);
    }

    [Fact]
    public void Round_TakesTheGreaterIntegerAtTheMidpoint()
    {
        // PLRM round: "-6.5 round" is -6.0.
        Assert.Equal(3.0, Num("2.5 round"), 10);
        Assert.Equal(-2.0, Num("-2.5 round"), 10);
    }

    [Fact]
    public void Ceiling_OnAnInteger_LeavesItAnInteger()
    {
        var r = Single("5 ceiling");
        Assert.Equal(PsType.Integer, r.Type);
        Assert.Equal(5, r.Num);
    }

    // ==================================================================
    //  Booleans and bitwise
    // ==================================================================

    /// <summary>
    /// <c>and</c>/<c>or</c>/<c>xor</c>/<c>not</c> are bitwise on integers and logical on booleans.
    /// A boolean-only implementation (which is all Type 4 needs) gives the wrong answer here.
    /// </summary>
    [Theory]
    [InlineData("5 3 and", 1)]
    [InlineData("5 3 or", 7)]
    [InlineData("5 3 xor", 6)]
    [InlineData("5 not", -6)]
    public void BooleanOperators_AreBitwiseOnIntegers(string source, double expected)
        => Assert.Equal(expected, Num(source));

    [Theory]
    [InlineData("true false and", false)]
    [InlineData("true false or", true)]
    [InlineData("true true xor", false)]
    [InlineData("true not", false)]
    public void BooleanOperators_AreLogicalOnBooleans(string source, bool expected)
        => Assert.Equal(expected, Single(source).AsBool());

    [Fact]
    public void BitShift_ShiftsLeftForPositive_AndRightForNegative()
    {
        Assert.Equal(8, Num("1 3 bitshift"));
        Assert.Equal(1, Num("8 -3 bitshift"));
    }

    /// <summary><c>eq</c> compares strings by characters, not by identity.</summary>
    [Fact]
    public void Eq_ComparesStringsByContent()
    {
        Assert.True(Single("(abc) (abc) eq").AsBool());
        Assert.False(Single("(abc) (abd) eq").AsBool());
    }

    /// <summary>And numbers across the integer/real divide.</summary>
    [Fact]
    public void Eq_ComparesIntegerAndRealByValue()
        => Assert.True(Single("5 5.0 eq").AsBool());

    // ==================================================================
    //  Stack operators
    // ==================================================================

    [Fact]
    public void Roll_RotatesTheTopNOperands()
    {
        // 1 2 3 with 3 1 roll → 3 1 2
        var stack = Run("1 2 3 3 1 roll");
        Assert.Equal(new double[] { 3, 1, 2 }, stack.Select(o => o.Num).ToArray());
    }

    [Fact]
    public void Roll_WithANegativeShift_RotatesTheOtherWay()
    {
        var stack = Run("1 2 3 3 -1 roll");
        Assert.Equal(new double[] { 2, 3, 1 }, stack.Select(o => o.Num).ToArray());
    }

    [Fact]
    public void Index_CopiesTheNthOperandFromTheTop()
    {
        // 0 index duplicates the top; 2 index reaches two below it.
        Assert.Equal(new double[] { 10, 20, 30, 30 },
            Run("10 20 30 0 index").Select(o => o.Num).ToArray());
        Assert.Equal(new double[] { 10, 20, 30, 10 },
            Run("10 20 30 2 index").Select(o => o.Num).ToArray());
    }

    [Fact]
    public void Copy_DuplicatesTheTopNOperands()
    {
        var stack = Run("1 2 3 2 copy");
        Assert.Equal(new double[] { 1, 2, 3, 2, 3 }, stack.Select(o => o.Num).ToArray());
    }

    /// <summary><c>counttomark</c> counts without consuming, so the operands stay put.</summary>
    [Fact]
    public void CountToMark_CountsOnlyAboveTheMark()
    {
        var stack = Run("1 mark 2 3 4 counttomark");

        Assert.Equal(6, stack.Count);
        Assert.Equal(3, stack[^1].Num);
    }

    [Fact]
    public void ClearToMark_DiscardsTheMarkAndEverythingAboveIt()
    {
        var stack = Run("1 mark 2 3 4 cleartomark");

        Assert.Single(stack);
        Assert.Equal(1, stack[0].Num);
    }

    // ==================================================================
    //  Dictionaries — the prologue mechanism
    // ==================================================================

    /// <summary>
    /// The single most important thing the dictionary stack does: a prologue defines procedures and
    /// later code calls them by name.
    /// </summary>
    [Fact]
    public void Def_ThenNameLookup_CallsTheProcedure()
        => Assert.Equal(30, Num("/double { 2 mul } def 15 double"));

    /// <summary>
    /// The inner definition wins while its dictionary is on the stack, and the outer one comes back
    /// after <c>end</c> — proving lookup is top-down and that <c>end</c> really pops.
    /// </summary>
    [Fact]
    public void Def_InAPushedDictionary_ShadowsAnOuterDefinition()
    {
        Assert.Equal(2, Num("/x 1 def 5 dict begin /x 2 def x end"));
        Assert.Equal(1, Num("/x 1 def 5 dict begin /x 2 def end x"));
    }

    [Fact]
    public void Known_ReportsWhetherAKeyIsPresent()
    {
        Assert.True(Single("systemdict /add known").AsBool());
        Assert.False(Single("systemdict /nosuchoperator known").AsBool());
    }

    /// <summary>
    /// <c>where</c> is the standard feature probe: <c>dict true</c> when the name is defined,
    /// a bare <c>false</c> when it is not. Answering it wrongly makes a prologue take a branch this
    /// interpreter cannot execute.
    /// </summary>
    [Fact]
    public void Where_PushesTheDefiningDictionaryAndTrue()
    {
        var found = Run("/add where");
        Assert.Equal(2, found.Count);
        Assert.Equal(PsType.Dictionary, found[0].Type);
        Assert.True(found[1].AsBool());

        var missing = Run("/nosuchname where");
        Assert.Single(missing);
        Assert.False(missing[0].AsBool());
    }

    /// <summary>The probe idiom itself, as a prologue writes it.</summary>
    [Fact]
    public void Where_DrivesTheStandardFeatureProbeIdiom()
    {
        Assert.True(Single("/add where { pop true } { false } ifelse").AsBool());
        Assert.False(Single("/nosuchname where { pop true } { false } ifelse").AsBool());
    }

    [Fact]
    public void Load_PushesTheValueWithoutExecutingIt()
    {
        // `/p { 1 2 add } def /p load` leaves the PROCEDURE on the stack, not 3.
        var stack = Run("/p { 1 2 add } def /p load");
        Assert.Single(stack);
        Assert.Equal(PsType.Array, stack[0].Type);
        Assert.True(stack[0].IsExecutable);
    }

    [Fact]
    public void DictLiteral_CollectsKeyValuePairs()
    {
        Assert.Equal(7, Num("<< /a 7 /b 8 >> /a get"));
    }

    // ==================================================================
    //  Arrays and strings
    // ==================================================================

    [Fact]
    public void ArrayLiteral_CollectsToTheMark()
    {
        Assert.Equal(3, Num("[ 1 2 3 ] length"));
        Assert.Equal(2, Num("[ 1 2 3 ] 1 get"));
    }

    /// <summary>
    /// <c>getinterval</c> must alias its parent, not copy: PLRM §3.3.2 says they share storage, and
    /// programs rely on writing through one and reading through the other.
    /// </summary>
    [Fact]
    public void GetInterval_SharesStorageWithItsParent()
    {
        // a = [1 2 3 4]; sub = a 1 2 getinterval; sub 0 99 put; a 1 get → 99
        Assert.Equal(99, Num("/a [ 1 2 3 4 ] def a 1 2 getinterval 0 99 put a 1 get"));
    }

    [Fact]
    public void PutInterval_WritesThroughToTheParent()
        => Assert.Equal(8, Num("/a [ 1 2 3 4 ] def a 1 [ 8 9 ] putinterval a 1 get"));

    [Fact]
    public void String_IsIndexableByByte()
        => Assert.Equal(66, Num("(ABC) 1 get"));

    [Fact]
    public void Search_SplitsAroundTheFirstMatch()
    {
        // (abcdef) (cd) search → post=(ef) match=(cd) pre=(ab) true
        var stack = Run("(abcdef) (cd) search");
        Assert.Equal(4, stack.Count);
        Assert.True(stack[3].AsBool());
        Assert.Equal("ab", stack[2].AsString().AsLatin1());
        Assert.Equal("cd", stack[1].AsString().AsLatin1());
        Assert.Equal("ef", stack[0].AsString().AsLatin1());
    }

    [Fact]
    public void Cvs_RendersIntoTheCallersBuffer()
        => Assert.Equal("42", Single("42 10 string cvs").AsString().AsLatin1());

    /// <summary>
    /// A real must render with a decimal point regardless of the machine's culture — a decimal
    /// comma would make the emitted content stream unparseable.
    /// </summary>
    [Fact]
    public void Cvs_RendersARealWithAnInvariantDecimalPoint()
        => Assert.Contains(".", Single("1.5 10 string cvs").AsString().AsLatin1());

    // ==================================================================
    //  Types
    // ==================================================================

    [Theory]
    [InlineData("5 type", "integertype")]
    [InlineData("5.0 type", "realtype")]
    [InlineData("true type", "booleantype")]
    [InlineData("/x type", "nametype")]
    [InlineData("(s) type", "stringtype")]
    [InlineData("[ ] type", "arraytype")]
    [InlineData("{ } type", "arraytype")]
    [InlineData("2 dict type", "dicttype")]
    [InlineData("null type", "nulltype")]
    public void Type_NamesEachObjectKind(string source, string expected)
        => Assert.Equal(expected, Single(source).AsName());

    [Fact]
    public void XCheck_DistinguishesProceduresFromLiteralArrays()
    {
        Assert.True(Single("{ 1 } xcheck").AsBool());
        Assert.False(Single("[ 1 ] xcheck").AsBool());
    }

    [Fact]
    public void Cvi_TruncatesTowardZero()
    {
        Assert.Equal(1, Num("1.9 cvi"));
        Assert.Equal(-1, Num("-1.9 cvi"));
    }

    // ==================================================================
    //  Control flow
    // ==================================================================

    [Fact]
    public void IfElse_TakesTheCorrectBranch()
    {
        Assert.Equal(1, Num("true { 1 } { 2 } ifelse"));
        Assert.Equal(2, Num("false { 1 } { 2 } ifelse"));
    }

    [Fact]
    public void For_AccumulatesAcrossTheRange()
        => Assert.Equal(15, Num("0 1 1 5 { add } for"));

    [Fact]
    public void For_WithANegativeIncrement_DescendsToTheLimit()
        => Assert.Equal(15, Num("0 5 -1 1 { add } for"));

    [Fact]
    public void For_WithAnUnreachableLimit_RunsZeroTimes()
        => Assert.Equal(0, Num("0 1 1 0 { add } for"));

    [Fact]
    public void Repeat_RunsExactlyNTimes()
        => Assert.Equal(5, Num("0 5 { 1 add } repeat"));

    [Fact]
    public void Repeat_WithZeroCount_IsANoOp()
        => Assert.Equal(0, Num("0 0 { 1 add } repeat"));

    [Fact]
    public void Loop_RunsUntilExit()
        => Assert.Equal(10, Num("0 { 1 add dup 10 ge { exit } if } loop"));

    [Fact]
    public void ForAll_OverAnArray_VisitsEveryElement()
        => Assert.Equal(6, Num("0 [ 1 2 3 ] { add } forall"));

    [Fact]
    public void ForAll_OverAString_VisitsEveryByte()
        => Assert.Equal(65 + 66, Num("0 (AB) { add } forall"));

    [Fact]
    public void ForAll_OverADictionary_VisitsKeyAndValue()
        => Assert.Equal(3, Num("0 << /a 1 /b 2 >> { exch pop add } forall"));

    /// <summary>
    /// An <b>empty</b> procedure still iterates. <c>forall</c> pushes each element and then runs the
    /// body; a body with no operators in it runs zero operators each time round, which is not the
    /// same as the loop running once. The distinction is only visible with an empty body, which is
    /// why it is easy to get backwards — the check for "nothing to execute" belongs after the
    /// iteration is exhausted, not after the first element is pushed.
    /// </summary>
    [Fact]
    public void ForAll_WithAnEmptyProcedure_StillPushesEveryElement()
        => Assert.Equal(new double[] { 1, 2, 3 },
                        Run("[ 1 2 3 ] { } forall").Select(o => o.Num).ToArray());

    [Fact]
    public void ForAllOverAString_WithAnEmptyProcedure_StillPushesEveryByte()
        => Assert.Equal(new double[] { 65, 66 },
                        Run("(AB) { } forall").Select(o => o.Num).ToArray());

    [Fact]
    public void For_WithAnEmptyProcedure_StillPushesEveryControlValue()
        => Assert.Equal(new double[] { 1, 2, 3 },
                        Run("1 1 3 { } for").Select(o => o.Num).ToArray());

    [Fact]
    public void Exec_RunsAProcedureFromTheStack()
        => Assert.Equal(3, Num("{ 1 2 add } exec"));

    // ==================================================================
    //  stopped / stop / errors
    // ==================================================================

    /// <summary>
    /// An <c>undefined</c> name must be a real, catchable error. Swallowing it is how a feature
    /// probe silently takes the wrong branch.
    /// </summary>
    [Fact]
    public void UndefinedName_InsideStopped_PushesTrue()
    {
        var stack = Run("{ nosuchthing } stopped");
        Assert.Single(stack);
        Assert.True(stack[0].AsBool());
    }

    [Fact]
    public void Stopped_OnACleanProcedure_PushesFalse()
    {
        var stack = Run("{ 1 2 add pop } stopped");
        Assert.Single(stack);
        Assert.False(stack[0].AsBool());
    }

    /// <summary>
    /// <c>stackunderflow</c> must be catchable rather than silently yielding 0 —
    /// <c>PdfFunctionEvaluator.Pop</c> returns 0, which is right for Type 4 and wrong here.
    /// </summary>
    [Fact]
    public void StackUnderflow_IsCatchableByStopped()
    {
        var stack = Run("{ pop } stopped");
        Assert.Single(stack);
        Assert.True(stack[0].AsBool());
    }

    /// <summary>
    /// <c>stop</c> abandons the rest of the procedure and pushes true. It does <b>not</b> roll the
    /// operand stack back: PostScript has no transaction, so whatever the procedure pushed before
    /// stopping is still there — 1 and 2 here, and not 3.
    /// </summary>
    [Fact]
    public void Stop_UnwindsToTheEnclosingStopped_LeavingEarlierOperands()
    {
        var stack = Run("{ 1 2 stop 3 } stopped");

        Assert.Equal(3, stack.Count);
        Assert.Equal(1, stack[0].Num);
        Assert.Equal(2, stack[1].Num);
        Assert.True(stack[2].AsBool());
    }

    /// <summary>
    /// An error that nothing catches abandons only the current top-level object; the next one still
    /// runs. That is what keeps one bad prologue line from costing the whole illustration.
    /// </summary>
    [Fact]
    public void UncaughtError_AbandonsOnlyTheCurrentTopLevelObject()
    {
        var stack = Run("{ nosuchthing } exec 42");

        Assert.Single(stack);
        Assert.Equal(42, stack[0].Num);
        Assert.Contains("Undefined", PostScriptDiagnostics.LastError);
    }

    // ==================================================================
    //  Unimplemented operators
    // ==================================================================

    /// <summary>
    /// A known-but-unimplemented operator consumes exactly its declared operands and execution
    /// continues. Leaving them behind would desynchronise everything after it.
    /// </summary>
    [Fact]
    public void UnimplementedOperator_ConsumesItsOperands_AndExecutionContinues()
    {
        // setscreen takes three operands. 99 must survive and be the only thing left.
        var stack = Run("99 60 45 { pop 1 } setscreen");

        Assert.Single(stack);
        Assert.Equal(99, stack[0].Num);
        Assert.Contains("setscreen", PostScriptDiagnostics.UnimplementedOperators);
    }

    [Fact]
    public void UnimplementedOperator_IsRecordedOnlyOnce_EvenInALoop()
    {
        Run("5 { 60 45 { pop 1 } setscreen } repeat");

        Assert.Single(PostScriptDiagnostics.UnimplementedOperators
                          .Where(n => n == "setscreen"));
    }

    /// <summary>
    /// <c>languagelevel</c> is probed by prologues to pick a code path, so the honest answer matters
    /// more than a placeholder null.
    /// </summary>
    [Fact]
    public void LanguageLevel_ReportsTwo()
        => Assert.Equal(2, Num("languagelevel"));

    /// <summary>
    /// <c>usertime</c> must not read the clock: a conversion has to be byte-reproducible or no test
    /// can assert on its output.
    /// </summary>
    [Fact]
    public void UserTime_IsDeterministic()
        => Assert.Equal(Num("usertime"), Num("usertime"));

    /// <summary>
    /// Seeding with the same value must give the same sequence — the conversion has to be
    /// byte-reproducible, so <c>rand</c> may not be wired to a clock or to <see cref="Random"/>'s
    /// default seed.
    /// </summary>
    [Fact]
    public void Rand_IsDeterministicForAGivenSeed()
    {
        double first  = Num("1 srand rand");
        double second = Num("1 srand rand");
        Assert.Equal(first, second);

        // …and a different seed gives a different value, or "deterministic" would be satisfied by
        // returning a constant.
        Assert.NotEqual(first, Num("2 srand rand"));
    }

    [Fact]
    public void Bind_LeavesTheProcedureOnTheStack()
    {
        var stack = Run("{ 1 2 add } bind");
        Assert.Single(stack);
        Assert.Equal(PsType.Array, stack[0].Type);
    }

    // ==================================================================
    //  Adversarial input — every guard in PsLimits
    // ==================================================================

    /// <summary>
    /// <c>{ } loop</c> has no body to tick through, so the loop frame must charge the budget
    /// itself or it spins for ever.
    /// </summary>
    [Fact]
    public void InfiniteLoop_IsBoundedByTheOperationBudget()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 50_000 };

        var stack = Run("{ } loop", options);

        Assert.Empty(stack);
        Assert.Contains("budget", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void InfiniteLoopWithABody_IsBoundedByTheOperationBudget()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 50_000 };

        Run("{ 1 pop } loop", options);

        Assert.Contains("budget", PostScriptDiagnostics.LastError);
    }

    /// <summary>
    /// The step loop is iterative precisely so this raises a catchable <c>execstackoverflow</c>
    /// instead of an uncatchable CLR <c>StackOverflowException</c>, which cannot be caught and
    /// takes the process down.
    /// </summary>
    [Fact]
    public void SelfRecursiveProcedure_IsBoundedByExecStackDepth_WithoutStackOverflow()
    {
        PostScriptDiagnostics.Clear();
        var ex = Record.Exception(() => Run("/f { f } def f"));

        Assert.Null(ex);
        Assert.Contains("ExecStackOverflow", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void MutuallyRecursiveProcedures_AreBoundedWithoutStackOverflow()
    {
        PostScriptDiagnostics.Clear();
        var ex = Record.Exception(() => Run("/a { b } def /b { a } def a"));

        Assert.Null(ex);
        Assert.Contains("ExecStackOverflow", PostScriptDiagnostics.LastError);
    }

    /// <summary>
    /// The pair matters: the cap has to refuse the absurd while still allowing the merely large,
    /// or it is not at the right value.
    /// </summary>
    [Fact]
    public void HugeStringAllocation_IsRefused()
    {
        var stack = Run("1000000000 string");

        Assert.Empty(stack);
        Assert.Contains("LimitCheck", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void LegalMillionByteString_Succeeds()
    {
        var stack = Run("1000000 string");

        Assert.Single(stack);
        Assert.Equal(1_000_000, stack[0].AsString().Length);
    }

    [Fact]
    public void HugeArrayAllocation_IsRefused()
    {
        Run("1000000000 array");
        Assert.Contains("LimitCheck", PostScriptDiagnostics.LastError);
    }

    /// <summary>
    /// Per-allocation caps do not compose: every string here is legal and the total is not. Only an
    /// aggregate budget catches it.
    /// </summary>
    [Fact]
    public void RepeatedAllocationInALoop_IsBoundedByTheAggregateAllocationBudget()
    {
        var options = new PostScriptConversionOptions
        {
            MaxTotalOps = 5_000_000,
            MaxCompositeAllocationBytes = 4L * 1024 * 1024
        };

        PostScriptDiagnostics.Clear();
        var ex = Record.Exception(() => Run("{ 1000000 string pop } loop", options));

        Assert.Null(ex);
        Assert.NotNull(PostScriptDiagnostics.LastError);
    }

    /// <summary>
    /// The nested-loop product is the shape <c>PdfFunctionEvaluator</c>'s own budget comment calls
    /// out: each loop is individually bounded and their product is not.
    /// </summary>
    [Fact]
    public void NestedLoopsProduct_IsBoundedByOneBudget()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 100_000 };

        PostScriptDiagnostics.Clear();
        var ex = Record.Exception(() =>
            Run("1000000 { 1000000 { 1 pop } repeat } repeat", options));

        Assert.Null(ex);
        Assert.Contains("budget", PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void OperandStackBomb_IsBoundedWithoutExhaustingMemory()
    {
        var options = new PostScriptConversionOptions { MaxTotalOps = 5_000_000 };

        PostScriptDiagnostics.Clear();
        var ex = Record.Exception(() => Run("{ 1 } loop", options));

        Assert.Null(ex);
        Assert.NotNull(PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void DictStackBomb_IsRefusedAtTheDepthCap()
    {
        PostScriptDiagnostics.Clear();
        Run("{ 2 dict begin } loop", new PostScriptConversionOptions { MaxTotalOps = 500_000 });

        Assert.NotNull(PostScriptDiagnostics.LastError);
    }

    [Fact]
    public void GarbageInput_DoesNotThrowAndReports()
    {
        PostScriptDiagnostics.Clear();
        var ex = Record.Exception(() => Run("] >> } ) pop pop add mul"));

        Assert.Null(ex);
        Assert.NotNull(PostScriptDiagnostics.LastError);
    }
}
