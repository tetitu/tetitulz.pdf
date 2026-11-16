// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Linq;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>Tests that write <see cref="PsLimits"/>, which is process-global.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PsLimitsCollection
{
    public const string Name = "PsLimits (process-global settings)";
}

/// <summary>
/// The interpreter's work budget and alias-chain limit, and the scanner's edges, as the overnight
/// sweep found them unguarded by any test. The budget figures were measured, not derived: the
/// program below needs exactly 19 operations.
/// </summary>
public class InterpreterBudgetTests
{
    private const string Measured = "1 2 add 3 mul { 4 5 add } exec pop pop";

    private static PsInterpreter Run(string source, long maxOps = 0)
    {
        PostScriptDiagnostics.Clear();
        var options = maxOps > 0 ? new PostScriptConversionOptions { MaxTotalOps = maxOps } : null;
        var interpreter = new PsInterpreter(options);
        interpreter.Run(new PsParser(new PsScanner(Encoding.ASCII.GetBytes(source))).ReadAll());
        return interpreter;
    }

    /// <summary>
    /// <c>MaxTotalOps</c> is the number of operations allowed, exactly: a program needing 19 runs
    /// under 19 and is stopped under 18. One operation is one unit.
    /// </summary>
    [Fact]
    public void TheBudgetAdmitsExactlyMaxTotalOps()
    {
        Assert.False(Run(Measured, 19).BudgetExhausted);
        Assert.True(Run(Measured, 18).BudgetExhausted);
    }

    /// <summary>
    /// A procedure's length is charged when it is entered, not as its elements run: a 1,000-element
    /// body that stops after its first element still costs 1,000. A 500-operation budget is
    /// exhausted; 5,000 is not.
    /// </summary>
    [Fact]
    public void AProcedureIsChargedItsWholeLengthOnEntry()
    {
        string body = string.Join(" ", Enumerable.Repeat("1", 999));
        string program = "{ { stop " + body + " } exec } stopped";

        Assert.True(Run(program, 500).BudgetExhausted);
        Assert.False(Run(program, 5000).BudgetExhausted);
    }

    /// <summary>
    /// <b>Adversarial: a <c>resourceforall</c> template that backtracks on every key.</b> <c>*aaa…ab</c>,
    /// a thousand <c>a</c>, against ten keys of two thousand: each backtrack to the star re-matches the
    /// thousand, a million comparisons a key, ten million here — and only the key count was charged, so
    /// a program defining long keys and enumerating them was a cost no budget saw. Each match is charged
    /// its comparisons: a 100 000-operation budget is exhausted, and a hundred million is not.
    /// </summary>
    [Fact]
    public void AdversarialResourceForAllTemplate_IsChargedItsComparisons()
    {
        var program = new StringBuilder();
        for (int k = 0; k < 10; k++)
            program.Append('/').Append(new string('a', 2000 + k)).Append(" 1 /Probe defineresource pop\n");
        program.Append("(*").Append(new string('a', 1000)).Append("b) { pop } 3000 string /Probe resourceforall\n");

        Assert.True(Run(program.ToString(), 100_000).BudgetExhausted);
        Assert.False(Run(program.ToString(), 100_000_000).BudgetExhausted);
    }

    /// <summary><c>quit</c> inside a procedure ends the program there: nothing after it runs.</summary>
    [Fact]
    public void QuitInsideAProcedureStopsEverything()
    {
        var stack = Run("1 { quit 2 } exec 3").Operands.Items;
        Assert.Equal(new double[] { 1 }, stack.Select(o => o.Num));
    }

    /// <summary>An executable name bound to another name is followed to what that name holds.</summary>
    [Fact]
    public void AnAliasIsFollowedToItsProcedure()
    {
        var stack = Run("/b { 42 } def /a /b cvx def a").Operands.Items;
        Assert.Equal(new double[] { 42 }, stack.Select(o => o.Num));
    }
}

[Collection(PsLimitsCollection.Name)]
public class InterpreterAndScannerLimitTests
{
    private static PsInterpreter Run(string source)
    {
        PostScriptDiagnostics.Clear();
        var interpreter = new PsInterpreter(null);
        interpreter.Run(new PsParser(new PsScanner(Encoding.ASCII.GetBytes(source))).ReadAll());
        return interpreter;
    }

    /// <summary>
    /// Alias following is bounded by <c>MaxExecStackDepth</c> lookups: at 3, a chain of two aliases
    /// (three lookups) reaches its procedure and a chain of three (four lookups) is refused.
    /// </summary>
    [Fact]
    public void AliasChainsAreBoundedByMaxExecStackDepth()
    {
        try
        {
            PsLimits.MaxExecStackDepth = 3;
            var two = Run("/c { 42 } def /b /c cvx def /a /b cvx def a").Operands.Items;
            Assert.Equal(new double[] { 42 }, two.Select(o => o.Num));

            var three = Run("/d { 42 } def /c /d cvx def /b /c cvx def /a /b cvx def a").Operands.Items;
            Assert.Empty(three);
        }
        finally { PsLimits.ResetToDefaults(); }
    }

    /// <summary>
    /// An inline data block is looked for within <c>MaxBinarySkipBytes</c> of its start: a
    /// terminator 20 bytes in is found under a 64-byte cap and not under a 16-byte one.
    /// </summary>
    [Fact]
    public void AnInlineBlockIsSoughtOnlyWithinTheCap()
    {
        byte[] data = Encoding.ASCII.GetBytes("0123456789ABCDEF0123>");
        byte[] terminator = { (byte)'>' };
        try
        {
            PsLimits.MaxBinarySkipBytes = 64;
            byte[] block = new PsScanner(data).TakeInlineBlock(terminator, out bool found);
            Assert.True(found);
            Assert.Equal(20, block.Length);

            PsLimits.MaxBinarySkipBytes = 16;
            new PsScanner(data).TakeInlineBlock(terminator, out bool foundCapped);
            Assert.False(foundCapped);
        }
        finally { PsLimits.ResetToDefaults(); }
    }
}
