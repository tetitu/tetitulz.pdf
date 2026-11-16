// Copyright (c) 2026 Tamir Mordo
// Licensed under the Apache License, Version 2.0. See LICENSE and NOTICE at the repository root.
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Generic;
using Tetitulz.Pdf.Base.Rendering;
using Tetitulz.Pdf.Core.Objects;
using Xunit;

namespace Tetitulz.Pdf.Base.Tests.Rendering;

/// <summary>
/// §8.11.2.3's <c>/VE</c> visibility expression — the recursive <c>/And</c> / <c>/Or</c> /
/// <c>/Not</c> over optional-content groups that an OCMD may carry, and which <b>takes priority</b>
/// over its <c>/OCGs</c> list.
/// </summary>
/// <remarks>
/// <para><b>Found unguarded by a mutation-liveness sweep (2026-08-29).</b> Replacing the whole
/// expression result with "visible" left all 3 040 Base tests green — so every <c>/And</c>,
/// <c>/Or</c>, <c>/Not</c> and the depth guard were unverified, in a decision point <i>both</i>
/// renderers share through <c>IsOcEntryHidden</c>.</para>
///
/// <para><b>The consequence runs the wrong way.</b> This is the mechanism a document uses to hide a
/// draft watermark, an internal-only layer, or a redaction underlay; an evaluator that answers
/// "visible" when the file said otherwise <i>discloses</i> content, and does so identically in the
/// raster and SVG paths, so comparing the two renderers cannot surface it either.</para>
///
/// <para>Asked through <c>IsOcEntryHidden</c> rather than through a render: that is the decision
/// point both renderers call, it returns a boolean, and a pixel test would confirm the same fact
/// through two more layers while making the nesting and depth cases impractical to build. See
/// <c>Visible</c> for why "rather than through a render" is not the same as "by calling the
/// evaluator directly" — the difference cost this file a rewrite.</para>
/// </remarks>
public class VisibilityExpressionTests
{
    private const int HiddenOcg = 7;
    private const int VisibleOcg = 8;

    private static readonly ISet<int> Hidden = new HashSet<int> { HiddenOcg };

    /// <summary>An expression array: <c>[/Op operand …]</c>.</summary>
    private static PdfArray Expr(string op, params PdfObject[] operands)
    {
        var arr = new PdfArray();
        arr.Add(new PdfName(op));
        foreach (PdfObject o in operands) arr.Add(o);
        return arr;
    }

    private static PdfObject Ocg(int objectNumber) => new PdfReference(objectNumber, 0);

    /// <summary>
    /// Asks the question <b>the way the renderers ask it</b>: through <c>IsOcEntryHidden</c>, with
    /// the expression on an OCMD's <c>/VE</c>.
    /// </summary>
    /// <remarks>
    /// <b>Not by calling the evaluator directly, and that distinction was earned.</b> The first
    /// draft of this file did call it directly — every test passed, and the mutation that motivated
    /// the file (<c>IsOcmdHidden</c> discarding the expression result) <i>still passed</i>, because
    /// the tests never reached the mutated line. Covering a function is not covering the decision
    /// that uses it.
    /// </remarks>
    private static bool Visible(PdfArray expression)
    {
        var ocmd = new PdfDictionary();
        ocmd.Set(PdfName.Type, new PdfName("OCMD"));
        ocmd.Set(PdfName.VE, expression);

        return !ContentStreamInterpreter.IsOcEntryHidden(ocmd, Hidden, null);
    }

    // ---- the three operators -----------------------------------------

    [Theory]
    [InlineData(VisibleOcg, VisibleOcg, true)]    // both on  -> visible
    [InlineData(VisibleOcg, HiddenOcg, false)]    // one off  -> hidden
    [InlineData(HiddenOcg, HiddenOcg, false)]
    public void AndIsTrueOnlyWhenEveryOperandIsVisible(int a, int b, bool expected)
        => Assert.Equal(expected, Visible(Expr("And", Ocg(a), Ocg(b))));

    [Theory]
    [InlineData(VisibleOcg, VisibleOcg, true)]
    [InlineData(VisibleOcg, HiddenOcg, true)]     // one on   -> visible
    [InlineData(HiddenOcg, HiddenOcg, false)]     // both off -> hidden
    public void OrIsTrueWhenAnyOperandIsVisible(int a, int b, bool expected)
        => Assert.Equal(expected, Visible(Expr("Or", Ocg(a), Ocg(b))));

    [Theory]
    [InlineData(HiddenOcg, true)]                 // NOT hidden -> visible
    [InlineData(VisibleOcg, false)]               // NOT visible -> hidden
    public void NotInvertsItsOperand(int ocg, bool expected)
        => Assert.Equal(expected, Visible(Expr("Not", Ocg(ocg))));

    // ---- nesting, which is the whole point of an expression -----------

    /// <summary>
    /// <c>/Or</c> of a hidden group and a nested <c>/Not</c> of the same group. A flat evaluator
    /// that ignored the nested array would answer "hidden"; the nested term makes it visible.
    /// </summary>
    [Fact]
    public void ANestedSubExpressionIsEvaluatedRatherThanTreatedAsAnOperand()
    {
        PdfArray inner = Expr("Not", Ocg(HiddenOcg));                 // true
        Assert.True(Visible(Expr("Or", Ocg(HiddenOcg), inner)));      // false OR true

        PdfArray innerFalse = Expr("Not", Ocg(VisibleOcg));           // false
        Assert.False(Visible(Expr("And", Ocg(VisibleOcg), innerFalse)));
    }

    // ---- degenerate and hostile shapes --------------------------------

    /// <summary>
    /// An expression the evaluator cannot make sense of resolves to <b>visible</b>. That is the
    /// deliberate direction: content the file never asked to hide must not vanish because its
    /// expression was malformed.
    /// </summary>
    [Theory]
    [InlineData("Xor")]      // not one of the three operators
    public void AnUnknownOperatorLeavesContentVisible(string op)
        => Assert.True(Visible(Expr(op, Ocg(HiddenOcg))));

    [Fact]
    public void AnEmptyOrOperatorlessExpressionLeavesContentVisible()
    {
        Assert.True(Visible(new PdfArray()));

        var noName = new PdfArray();
        noName.Add(new PdfNumber(1));
        Assert.True(Visible(noName));

        Assert.True(Visible(Expr("Not")));   // /Not with no operand
    }

    /// <summary>
    /// <b>The depth guard fails OPEN, and the parity matters.</b> A <c>/VE</c> nested past
    /// <c>PdfLimits.MaxVisibilityExpressionDepth</c> is abandoned and the content left visible —
    /// deliberately, because an adversarial document must not be able to make a reader hide content
    /// by nesting deeply. The unwound frames may carry any number of <c>/Not</c>s, so the answer has
    /// to be forced at the top rather than inherited from whatever parity the stack happened to
    /// reach; that is exactly what the sentinel-exception design is for.
    /// </summary>
    [Fact]
    public void AnExpressionNestedPastTheDepthCapIsAbandonedAsVisible()
    {
        // Deeper than the cap, and wrapped in an odd number of /Not levels so that a guard which
        // simply returned from the innermost frame would propagate the WRONG answer outward.
        PdfArray expr = Expr("Not", Ocg(VisibleOcg));   // would be false on its own
        for (int i = 0; i < 120; i++) expr = Expr("Not", expr);

        Assert.True(Visible(expr));
    }
}
