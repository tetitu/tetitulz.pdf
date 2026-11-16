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
/// PLRM: <c>sqrt</c> of a negative number, <c>ln</c> and <c>log</c> of zero or a negative number
/// are <c>rangecheck</c> errors — errors a program catches with <c>stopped</c> and reads from
/// <c>$error</c>. They were clamped instead (sqrt to 0, the logarithms to the smallest positive
/// double), so <c>{ -1 sqrt } stopped</c> answered false where every interpreter answers true, and
/// <c>0 ln</c> pushed −744.44 into whatever coordinate came next.
/// </summary>
public sealed class ArithmeticDomainTests
{
    [Theory]
    [InlineData("{ -1 sqrt } stopped")]
    [InlineData("{ -0.5 sqrt } stopped")]
    [InlineData("{ 0 ln } stopped")]
    [InlineData("{ -1 ln } stopped")]
    [InlineData("{ 0 log } stopped")]
    [InlineData("{ -2.5 log } stopped")]
    public void OutsideTheDomain_IsARangeCheck(string program)
    {
        IReadOnlyList<PsObject> stack = Run(program + " $error /errorname get");

        Assert.Equal(2, stack.Count);
        Assert.True(stack[0].AsBool(), "stopped should answer true: the operator raised an error");
        Assert.Equal("rangecheck", stack[1].AsName());
    }

    /// <summary>The boundary is inside the domain for <c>sqrt</c> (zero) and outside for the logarithms.</summary>
    [Theory]
    [InlineData("4 sqrt", 2.0)]
    [InlineData("0 sqrt", 0.0)]
    [InlineData("1 ln", 0.0)]
    [InlineData("100 log", 2.0)]
    [InlineData("0.001 log", -3.0)]
    public void InsideTheDomain_Computes(string program, double expected)
    {
        IReadOnlyList<PsObject> stack = Run(program);

        Assert.Single(stack);
        Assert.Equal(expected, stack[0].Num, 9);
    }

    [Fact]
    public void InsideTheDomain_StoppedAnswersFalse_AndTheResultStays()
    {
        IReadOnlyList<PsObject> stack = Run("{ 9 sqrt } stopped");

        Assert.Equal(2, stack.Count);
        Assert.Equal(3.0, stack[0].Num, 9);
        Assert.False(stack[1].AsBool());
    }

    private static IReadOnlyList<PsObject> Run(string source)
    {
        PostScriptDiagnostics.Clear();
        var interpreter = new PsInterpreter(null);
        interpreter.Run(new PsParser(new PsScanner(Encoding.ASCII.GetBytes(source))).ReadAll());
        return interpreter.Operands.Items;
    }
}
