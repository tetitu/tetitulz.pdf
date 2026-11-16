// Copyright (c) 2026 Tamir Mordo
// Licensed under the Tetitulz.Pdf Attribution License, version 1.0 - not the Apache License 2.0 the
// rest of Tetitulz.Pdf is under. See LICENSE-ATTRIBUTION.txt at the repository root.
// SPDX-License-Identifier: LicenseRef-Tetitulz-Attribution-1.0

using System.Collections.Generic;
using System.IO;
using System.Text;
using Tetitulz.Pdf.Plugins.PostScript;
using Tetitulz.Pdf.Plugins.PostScript.Interpreter;
using Tetitulz.Pdf.Plugins.PostScript.Tests.TestInfrastructure;
using Xunit;

namespace Tetitulz.Pdf.Plugins.PostScript.Tests;

/// <summary>
/// The graphics-state setters at the values they correct: a negative line width, a miter limit
/// below 1, a cap or join style outside 0..2, a negative dash phase; the dash a <c>setdash</c> is
/// read back as; <c>concatmatrix</c>'s operand order; and the operators' error edges. Each is read
/// back through the matching <c>current…</c> operator, so a setter that stored the raw value
/// answers differently.
/// </summary>
public sealed class GraphicsStateBoundaryTests
{
    // ------------------------------------------------------------------ values the setters correct

    [Theory]
    [InlineData("-5 setlinewidth currentlinewidth", 5)]
    [InlineData("5 setlinewidth currentlinewidth", 5)]
    [InlineData("0.5 setmiterlimit currentmiterlimit", 1)]
    [InlineData("2.5 setmiterlimit currentmiterlimit", 2.5)]
    public void LineWidthAndMiterLimit_AreReadBackCorrected(string program, double expected)
    {
        Assert.Equal(expected, Single(program).Num);
    }

    /// <summary>
    /// A style outside 0..2 is clamped to the nearest end; 0, 1 and 2 are kept. (A value of exactly
    /// 0 reads back as 0 either way, so it is a control, not a boundary.)
    /// </summary>
    [Theory]
    [InlineData("-1 setlinecap currentlinecap", 0)]
    [InlineData("0 setlinecap currentlinecap", 0)]
    [InlineData("1 setlinecap currentlinecap", 1)]
    [InlineData("2 setlinecap currentlinecap", 2)]
    [InlineData("3 setlinecap currentlinecap", 2)]
    [InlineData("-1 setlinejoin currentlinejoin", 0)]
    [InlineData("1 setlinejoin currentlinejoin", 1)]
    [InlineData("3 setlinejoin currentlinejoin", 2)]
    public void CapAndJoinStyles_AreClamped(string program, int expected)
    {
        Assert.Equal(expected, (int)Single(program).Num);
    }

    // ------------------------------------------------------------------ dash

    /// <summary>
    /// <c>currentdash</c> leaves the array and then the phase — the order <c>setdash</c> took them —
    /// each exactly as set; a negative phase is read back as 0.
    /// </summary>
    [Fact]
    public void CurrentDash_ReturnsTheArrayThenThePhase()
    {
        IReadOnlyList<PsObject> stack = Run("[3 5] 2 setdash currentdash");

        Assert.Equal(2, stack.Count);
        PsArray dashes = stack[0].AsArray();
        Assert.Equal(2, dashes.Length);
        Assert.Equal(3, dashes[0].Num);
        Assert.Equal(5, dashes[1].Num);
        Assert.Equal(2, stack[1].Num);
    }

    [Fact]
    public void ANegativeDashPhase_IsReadBackAsZero()
    {
        IReadOnlyList<PsObject> stack = Run("[3] -2 setdash currentdash");

        Assert.Equal(0, stack[1].Num);
    }

    // ------------------------------------------------------------------ matrices

    /// <summary>
    /// <c>concatmatrix</c> leaves A × B (PLRM): a translation by 10 followed by a scale of 2 moves a
    /// point by 20, and the same two the other way round by 10.
    /// </summary>
    [Theory]
    [InlineData("[1 0 0 1 10 0] [2 0 0 2 0 0] matrix concatmatrix 4 get", 20)]
    [InlineData("[2 0 0 2 0 0] [1 0 0 1 10 0] matrix concatmatrix 4 get", 10)]
    public void ConcatMatrix_IsTheFirstFollowedByTheSecond(string program, double expectedTranslation)
    {
        Assert.Equal(expectedTranslation, Single(program).Num);
    }

    // ------------------------------------------------------------------ arcs and rectangles

    /// <summary>A zero radius is a legal (degenerate) arc; a negative one is a rangecheck.</summary>
    [Theory]
    [InlineData("{ newpath 0 0 0 0 90 arc } stopped", false)]
    [InlineData("{ newpath 0 0 -1 0 90 arc } stopped", true)]
    [InlineData("{ newpath 0 0 0 90 0 arcn } stopped", false)]
    [InlineData("{ newpath 0 0 -1 90 0 arcn } stopped", true)]
    public void ArcRadius_ZeroIsAccepted_NegativeIsRefused(string program, bool refused)
    {
        IReadOnlyList<PsObject> stack = Run(program);

        Assert.Equal(refused, stack[stack.Count - 1].AsBool());
    }

    /// <summary>
    /// A <c>numstring</c> operand to a <c>rect*</c> operator is consumed (and reported, since the
    /// binary form is not read): the stack under it is as it was, and the string is gone.
    /// </summary>
    [Fact]
    public void ANumstringRectangle_IsConsumedAndReported()
    {
        using var log = DiagnosticCollector.Start();

        IReadOnlyList<PsObject> stack = Run("7 (abc) rectfill");

        Assert.Single(stack);
        Assert.Equal(7, stack[0].Num);
        Assert.True(log.Any("numstring"), "reported: " + string.Join(" | ", log.Messages));
    }

    // ------------------------------------------------------------------ reversepath after strokepath

    /// <summary>
    /// <c>reversepath</c> after <c>strokepath</c> is ignored, and says so — the outline was never
    /// built, so it has no direction. Before <c>strokepath</c> it reverses the path and says nothing.
    /// </summary>
    [Theory]
    [InlineData("strokepath reversepath", true)]
    [InlineData("reversepath", false)]
    public void ReversePath_AfterStrokePath_IsIgnoredAndReported(string tail, bool reported)
    {
        using var log = DiagnosticCollector.Start();
        using var source = new MemoryStream(Encoding.Latin1.GetBytes(
            "%!PS-Adobe-3.0\nnewpath 10 10 moveto 90 10 lineto 90 90 lineto " + tail + " stroke\n"));
        using var output = new MemoryStream();

        PostScriptConversion.ConvertDocument(source, output);

        Assert.Equal(reported, log.Any("'reversepath' after 'strokepath' was ignored"));
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

/// <summary>The dash array limit, set through <see cref="PsLimits"/>.</summary>
[Collection(PsLimitsCollection.Name)]
public sealed class DashArrayLimitTests
{
    /// <summary>
    /// <c>MaxDashArrayEntries</c> is the most entries a dash array may have, exactly: at a limit of
    /// 3, three are accepted and four are a limitcheck.
    /// </summary>
    [Theory]
    [InlineData("[1 1 1] 0 setdash currentdash pop length", false)]
    [InlineData("{ [1 1 1 1] 0 setdash } stopped", true)]
    public void TheLimitIsTheMostEntriesAllowed(string program, bool refused)
    {
        try
        {
            PsLimits.MaxDashArrayEntries = 3;
            PostScriptDiagnostics.Clear();
            var interpreter = new PsInterpreter(null);
            interpreter.Run(new PsParser(new PsScanner(Encoding.ASCII.GetBytes(program))).ReadAll());
            PsObject top = interpreter.Operands.Items[interpreter.Operands.Items.Count - 1];

            if (refused) Assert.True(top.AsBool());
            else Assert.Equal(3, top.Num);
        }
        finally
        {
            PsLimits.ResetToDefaults();
        }
    }
}
